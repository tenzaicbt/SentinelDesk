using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.JSInterop;

namespace SentinelDesk.Server.Web.Services;

public sealed record Permissions(bool Write, bool Super, bool Web, bool Titles, bool Audit, bool Live)
{
    public static Permissions For(string? role) => new(
        Write: role is "SuperAdmin" or "BranchAdmin",
        Super: role is "SuperAdmin",
        Web: role is not null && role != "HrViewer",
        Titles: role is "SuperAdmin" or "BranchAdmin",
        Audit: role is "SuperAdmin" or "Auditor",
        Live: role is not null && role != "HrViewer");
}

/// <summary>
/// The signed-in admin for one browser tab. The API token is kept in the tab's session storage (encrypted with
/// ASP.NET Data Protection), so a reload keeps you signed in and closing the tab signs you out.
/// </summary>
public sealed class Session(ApiClient api, ProtectedSessionStorage storage, IJSRuntime js, Fmt fmt, ILogger<Session> log)
{
    private const string TokenKey = "sd-token";

    public bool Ready { get; private set; }
    public Me? Me { get; private set; }
    public Permissions Can { get; private set; } = Permissions.For(null);
    public bool Dark { get; private set; }
    public event Action? Changed;

    private sealed record BrowserEnv(string? TimeZone, string? Language, bool Dark);

    public async Task InitAsync(string? clientIp)
    {
        api.ClientIp = clientIp;
        api.Unauthorized += () => _ = LogoutAsync();
        try
        {
            var env = await js.InvokeAsync<BrowserEnv>("sd.env");
            fmt.Configure(env.TimeZone, env.Language);
            Dark = env.Dark;
        }
        catch (JSException) { }
        try
        {
            var stored = await storage.GetAsync<string>(TokenKey);
            if (stored.Success && !string.IsNullOrEmpty(stored.Value)) { api.Token = stored.Value; await RefreshAsync(); }
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or InvalidOperationException)
        {
            log.LogDebug(ex, "Stored console session could not be read");
            await ClearAsync();
        }
        Ready = true;
        Changed?.Invoke();
    }

    public async Task RefreshAsync()
    {
        if (string.IsNullOrEmpty(api.Token)) { Me = null; return; }
        try { Me = await api.GetAsync<Me>("/api/auth/me"); }
        catch (ApiException) { await ClearAsync(); }
        Can = Permissions.For(Me?.Role);
        Changed?.Invoke();
    }

    public async Task LoginAsync(string token)
    {
        api.Token = token;
        try { await storage.SetAsync(TokenKey, token); } catch (Exception ex) { log.LogDebug(ex, "Could not store the console session"); }
        await RefreshAsync();
    }

    public async Task LogoutAsync()
    {
        await ClearAsync();
        Changed?.Invoke();
    }

    private async Task ClearAsync()
    {
        api.Token = null;
        Me = null;
        Can = Permissions.For(null);
        try { await storage.DeleteAsync(TokenKey); } catch { /* circuit closing */ }
    }

    public async Task ToggleThemeAsync()
    {
        Dark = !Dark;
        try { await js.InvokeVoidAsync("sd.setTheme", Dark); } catch (JSException) { }
        Changed?.Invoke();
    }

    public string? Token => api.Token;
}
