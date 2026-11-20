using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using SentinelDesk.Server.Data;
using SentinelDesk.Shared;

namespace SentinelDesk.Server.Security;

/// <summary>
/// Authenticates agents: HMAC token (device id, timestamp, nonce) + optional/required mTLS client certificate
/// issued by our device CA and bound to the device record (thumbprint pinning).
/// </summary>
public sealed class DeviceAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ServerKeys keys,
    DeviceCache cache,
    IConfiguration config) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private static readonly ConcurrentDictionary<string, long> SeenNonces = new();

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? token = null;
        var header = Request.Headers.Authorization.ToString();
        if (header.StartsWith(DeviceAuth.Scheme + " ", StringComparison.Ordinal)) token = header[(DeviceAuth.Scheme.Length + 1)..];
        else if (header.StartsWith("Bearer ", StringComparison.Ordinal)) token = header[7..]; // SignalR access token provider
        else if (Request.Query.TryGetValue("access_token", out var qs)) token = qs.ToString();
        if (string.IsNullOrEmpty(token)) return AuthenticateResult.NoResult();

        var parts = token.Split('.');
        if (parts.Length != 4 || !Guid.TryParseExact(parts[0], "N", out var deviceId) || !long.TryParse(parts[1], out var ts))
            return AuthenticateResult.Fail("malformed device token");

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - ts) > 300) return AuthenticateResult.Fail("device token expired (check clock)");

        var expected = DeviceAuth.Compute(deviceId, ts, parts[2], keys.DeviceSecret(deviceId));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[3])))
            return AuthenticateResult.Fail("bad device signature");

        // replay protection (nonces kept for the validity window)
        var nonceKey = parts[0] + parts[2];
        if (!SeenNonces.TryAdd(nonceKey, ts)) return AuthenticateResult.Fail("replayed token");
        if (SeenNonces.Count > 200_000)
            foreach (var kv in SeenNonces.Where(k => now - k.Value > 600).ToList()) SeenNonces.TryRemove(kv.Key, out _);

        var device = await cache.GetAsync(deviceId);
        if (device is null) return AuthenticateResult.Fail("unknown device");
        if (device.Status is DeviceStatus.Disabled or DeviceStatus.Retired) return AuthenticateResult.Fail("device disabled");

        // mTLS binding
        var cert = await Context.Connection.GetClientCertificateAsync();
        var requireMtls = config.GetValue("Security:RequireMtls", false);
        if (cert is not null)
        {
            if (!keys.ValidateDeviceCertificate(cert) ||
                !string.Equals(cert.Thumbprint, device.CertThumbprint, StringComparison.OrdinalIgnoreCase))
                return AuthenticateResult.Fail("client certificate not bound to device");
        }
        else if (requireMtls) return AuthenticateResult.Fail("client certificate required");

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, deviceId.ToString()),
            new Claim("device_status", device.Status.ToString()),
            new Claim("mtls", cert is not null ? "1" : "0")
        ], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}

/// <summary>Small in-memory cache for device auth lookups.</summary>
public sealed class DeviceCache(IServiceScopeFactory scopes)
{
    private readonly ConcurrentDictionary<Guid, (Device Device, DateTime At)> _cache = new();

    public async Task<Device?> GetAsync(Guid id)
    {
        if (_cache.TryGetValue(id, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromSeconds(60)) return hit.Device;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var d = await db.Devices.FindAsync(id);
        if (d is not null) _cache[id] = (d, DateTime.UtcNow);
        return d;
    }

    public void Invalidate(Guid id) => _cache.TryRemove(id, out _);
}
