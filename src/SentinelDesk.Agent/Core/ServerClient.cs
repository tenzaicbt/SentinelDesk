using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SentinelDesk.Shared;

namespace SentinelDesk.Agent.Core;

public sealed class ServerUnavailableException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>HTTPS client: mTLS device certificate, server certificate pinning fallback, HMAC device token per request.</summary>
public sealed class ServerClient : IDisposable
{
    private readonly AgentConfig _config;
    private readonly ILogger _log;
    private HttpClient? _http;
    private X509Certificate2? _clientCert;

    public ServerClient(AgentConfig config, ILogger<ServerClient> log)
    {
        _config = config;
        _log = log;
    }

    public X509Certificate2? ClientCertificate => _clientCert ??= DeviceKeys.LoadClientCertificate(_config, _log);

    public bool ValidateServerCertificate(X509Certificate2? cert, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (cert is null || string.IsNullOrEmpty(_config.PinnedServerCertSha256)) return false;
        var hash = Convert.ToHexString(SHA256.HashData(cert.RawData));
        return string.Equals(hash, _config.PinnedServerCertSha256, StringComparison.OrdinalIgnoreCase);
    }

    public SocketsHttpHandler CreateHandler(bool authenticate = true)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, cert, _, errors) => ValidateServerCertificate(cert as X509Certificate2, errors),
            }
        };
        if (authenticate && ClientCertificate is { } c)
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { c };
        return handler;
    }

    private HttpClient Http => _http ??= new HttpClient(new DeviceAuthHandler(_config) { InnerHandler = CreateHandler() })
    {
        BaseAddress = new Uri(_config.ServerUrl.TrimEnd('/') + "/"),
        Timeout = TimeSpan.FromSeconds(60),
        DefaultRequestHeaders = { { "User-Agent", $"SentinelDesk-Agent/{AgentInfo.Version}" } }
    };

    public void Reset()
    {
        _http?.Dispose();
        _http = null;
        _clientCert = null;
    }

    public Task<HeartbeatResponse> HeartbeatAsync(HeartbeatRequest hb, CancellationToken ct) => PostAsync<HeartbeatRequest, HeartbeatResponse>("agent/v1/heartbeat", hb, ct);
    public Task<UploadAck> UploadAsync(TelemetryBatch batch, CancellationToken ct) => PostAsync<TelemetryBatch, UploadAck>("agent/v1/telemetry", batch, ct);

    public Task<AccessRequestAck> PostAccessRequestAsync(AccessRequestDto request, CancellationToken ct) =>
        PostAsync<AccessRequestDto, AccessRequestAck>("agent/v1/access-requests", request, ct);

    public sealed record AccessRequestAck(bool Duplicate);

    public async Task<SignedPolicy> GetPolicyAsync(CancellationToken ct)
    {
        using var res = await Send(() => Http.GetAsync("agent/v1/policy", ct));
        return (await res.Content.ReadFromJsonAsync<SignedPolicy>(Json.Options, ct))!;
    }

    public async Task PostCommandResultAsync(CommandResult result, CancellationToken ct)
    {
        using var res = await Send(() => Http.PostAsJsonAsync($"agent/v1/commands/{result.CommandId}/result", result, Json.Options, ct));
    }

    public async Task DownloadAsync(string relativeUrl, string destination, CancellationToken ct)
    {
        using var res = await Send(() => Http.GetAsync(relativeUrl.TrimStart('/'), HttpCompletionOption.ResponseHeadersRead, ct));
        await using var fs = File.Create(destination);
        await res.Content.CopyToAsync(fs, ct);
    }

    private async Task<TRes> PostAsync<TReq, TRes>(string url, TReq body, CancellationToken ct)
    {
        using var res = await Send(() => Http.PostAsJsonAsync(url, body, Json.Options, ct));
        return (await res.Content.ReadFromJsonAsync<TRes>(Json.Options, ct))!;
    }

    private static async Task<HttpResponseMessage> Send(Func<Task<HttpResponseMessage>> call)
    {
        HttpResponseMessage res;
        try { res = await call(); }
        catch (HttpRequestException ex) { throw new ServerUnavailableException(ex.Message); }
        catch (TaskCanceledException ex) when (!ex.CancellationToken.IsCancellationRequested) { throw new ServerUnavailableException("timeout"); }
        if (res.IsSuccessStatusCode) return res;
        var text = await res.Content.ReadAsStringAsync();
        res.Dispose();
        throw new ServerUnavailableException($"{(int)res.StatusCode} {res.ReasonPhrase}: {text[..Math.Min(text.Length, 300)]}", res.StatusCode);
    }

    public void Dispose() => _http?.Dispose();

    private sealed class DeviceAuthHandler(AgentConfig config) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (config.IsEnrolled)
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(DeviceAuth.Scheme,
                    DeviceAuth.CreateToken(config.DeviceId, config.GetSecret()!));
            return base.SendAsync(request, ct);
        }
    }
}

public static class AgentInfo
{
    public static string Version { get; } =
        typeof(AgentInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";
}
