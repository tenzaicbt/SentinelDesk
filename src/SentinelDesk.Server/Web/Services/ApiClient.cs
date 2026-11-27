using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using SentinelDesk.Shared;

namespace SentinelDesk.Server.Web.Services;

public sealed class ApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// The address the console uses to call this server's own API: the first address Kestrel listens on, with wildcard
/// hosts mapped to loopback. The console runs in the server process, so every console action still goes through the
/// API endpoints and their authorization, validation and audit logging.
/// </summary>
public sealed class LoopbackAddress(IServer server, IConfiguration config)
{
    private Uri? _uri;

    public Uri Uri => _uri ??= Resolve();

    private Uri Resolve()
    {
        if (config["Console:ApiBaseUrl"] is { Length: > 0 } configured) return new Uri(configured);
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        var pick = addresses.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                   ?? addresses.FirstOrDefault() ?? "http://localhost:5080";
        var b = new UriBuilder(pick.Replace("://+", "://localhost").Replace("://*", "://localhost"));
        if (b.Host is "0.0.0.0" or "[::]" or "::" or "localhost") b.Host = "127.0.0.1";
        b.Path = "/";
        return b.Uri;
    }

    /// <summary>The server presents a certificate for its public name, not for the loopback IP: accept it only
    /// when calling ourselves on an IP address this machine listens on.</summary>
    public HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, errors) => errors == System.Net.Security.SslPolicyErrors.None || IPAddress.TryParse(Uri.Host.Trim('[', ']'), out _) },
    };
}

/// <summary>Typed calls to the server API with the signed-in admin's token. Scoped: one per browser tab.</summary>
public sealed class ApiClient(IHttpClientFactory factory, LoopbackAddress loopback)
{
    public static readonly JsonSerializerOptions JsonOptions = Json.Options;

    public string? Token { get; set; }
    /// <summary>The browser's address, forwarded so rate limits and the audit log see the real client.</summary>
    public string? ClientIp { get; set; }
    public event Action? Unauthorized;

    private HttpClient Client()
    {
        var c = factory.CreateClient("console-api");
        c.BaseAddress = loopback.Uri;
        return c;
    }

    private HttpRequestMessage Request(HttpMethod method, string url, HttpContent? content = null, string? token = null)
    {
        var req = new HttpRequestMessage(method, url) { Content = content };
        var t = token ?? Token;
        if (!string.IsNullOrEmpty(t)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t);
        if (!string.IsNullOrEmpty(ClientIp)) req.Headers.TryAddWithoutValidation("X-Forwarded-For", ClientIp);
        return req;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req)
    {
        HttpResponseMessage res;
        try { res = await Client().SendAsync(req); }
        catch (HttpRequestException) { throw new ApiException(0, "Unable to reach the server"); }
        var path = req.RequestUri?.OriginalString ?? "";
        if (res.StatusCode == HttpStatusCode.Unauthorized && !path.StartsWith("/api/auth/login") && !path.StartsWith("/api/auth/mfa"))
            Unauthorized?.Invoke();
        if (!res.IsSuccessStatusCode)
        {
            var msg = res.ReasonPhrase ?? res.StatusCode.ToString();
            try
            {
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is { Length: > 0 } ds) msg = ds;
                    else if (doc.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } ts) msg = ts;
                }
            }
            catch (JsonException) { /* not JSON */ }
            if (res.StatusCode == HttpStatusCode.Forbidden && msg == "Forbidden") msg = "You do not have permission to do this.";
            var status = (int)res.StatusCode;
            res.Dispose();
            throw new ApiException(status, msg);
        }
        return res;
    }

    private static JsonContent Body(object? body) => JsonContent.Create(body ?? new { }, options: JsonOptions);

    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        using (res)
        {
            if (res.Content.Headers.ContentLength == 0 || res.Content.Headers.ContentType?.MediaType?.Contains("json") != true) return default!;
            return (await res.Content.ReadFromJsonAsync<T>(JsonOptions))!;
        }
    }

    public async Task<T> GetAsync<T>(string url) => await Read<T>(await SendAsync(Request(HttpMethod.Get, url)));
    public async Task<T> PostAsync<T>(string url, object? body = null, string? token = null) => await Read<T>(await SendAsync(Request(HttpMethod.Post, url, Body(body), token)));
    public async Task PostAsync(string url, object? body = null) => (await SendAsync(Request(HttpMethod.Post, url, Body(body)))).Dispose();
    public async Task PutAsync(string url, object? body) => (await SendAsync(Request(HttpMethod.Put, url, Body(body)))).Dispose();
    public async Task DeleteAsync(string url) => (await SendAsync(Request(HttpMethod.Delete, url))).Dispose();
    public async Task<T> DeleteAsync<T>(string url) => await Read<T>(await SendAsync(Request(HttpMethod.Delete, url)));
    public async Task<T> PostFormAsync<T>(string url, MultipartFormDataContent form) => await Read<T>(await SendAsync(Request(HttpMethod.Post, url, form)));

    /// <summary>A file the API returns (CSV export, configured installer): bytes and the file name from Content-Disposition.</summary>
    public async Task<(byte[] Bytes, string FileName, string ContentType)> DownloadAsync(string url, object? postBody = null, string fallbackName = "export.csv")
    {
        using var res = await SendAsync(postBody is null ? Request(HttpMethod.Get, url) : Request(HttpMethod.Post, url, Body(postBody)));
        var bytes = await res.Content.ReadAsByteArrayAsync();
        var cd = res.Content.Headers.ContentDisposition;
        var name = (cd?.FileNameStar ?? cd?.FileName)?.Trim('"') ?? fallbackName;
        return (bytes, name, res.Content.Headers.ContentType?.ToString() ?? "application/octet-stream");
    }

    public static string Qs(params (string Key, object? Value)[] values)
    {
        var parts = values.Where(v => v.Value is not null && v.Value.ToString() != "")
            .Select(v => $"{Uri.EscapeDataString(v.Key)}={Uri.EscapeDataString(Convert.ToString(v.Value, System.Globalization.CultureInfo.InvariantCulture)!)}");
        var s = string.Join("&", parts);
        return s.Length > 0 ? "?" + s : "";
    }
}
