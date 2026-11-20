using System.Text.RegularExpressions;
using SentinelDesk.Shared;

namespace SentinelDesk.Agent.Core;

/// <summary>Holds the active policy. Only policies carrying a valid server ECDSA signature are accepted.</summary>
public sealed partial class PolicyStore(AgentConfig config, ILogger<PolicyStore> log)
{
    private AgentPolicy _current = LoadCached(config, log);
    public AgentPolicy Current => _current;
    public event Action<AgentPolicy>? Changed;

    public bool Apply(SignedPolicy signed)
    {
        using var key = config.ServerSigningKey();
        if (!Signing.Verify(key, signed.PolicyJson, signed.Signature))
        {
            log.LogWarning("Rejected policy v{Version}: invalid signature", signed.Version);
            return false;
        }
        var policy = Json.Deserialize<AgentPolicy>(signed.PolicyJson);
        if (policy is null) return false;
        policy.Version = signed.Version;
        File.WriteAllText(AgentPaths.PolicyFile, Json.Serialize(signed));
        _current = policy;
        log.LogInformation("Applied policy '{Name}' (v{Version})", policy.Name, policy.Version);
        Changed?.Invoke(policy);
        return true;
    }

    private static AgentPolicy LoadCached(AgentConfig config, ILogger log)
    {
        try
        {
            if (File.Exists(AgentPaths.PolicyFile) && config.ServerSigningKeyPem is not null)
            {
                var signed = Json.Deserialize<SignedPolicy>(File.ReadAllText(AgentPaths.PolicyFile));
                using var key = config.ServerSigningKey();
                if (signed is not null && Signing.Verify(key, signed.PolicyJson, signed.Signature))
                {
                    var p = Json.Deserialize<AgentPolicy>(signed.PolicyJson)!;
                    p.Version = signed.Version;
                    return p;
                }
                log.LogWarning("Cached policy failed signature verification — possible tampering; using defaults");
            }
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not load cached policy"); }
        return new AgentPolicy { Version = 0 };
    }

    /// <summary>Working-hours gate for user-activity collectors.</summary>
    public bool ActivityCollectionAllowed(DateTime utcNow)
    {
        var wh = _current.WorkingHours;
        if (wh.CollectOutsideHours) return true;
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(wh.TimeZone); } catch { tz = TimeZoneInfo.Local; }
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
        var days = wh.Days.Equals("Mon-Sat", StringComparison.OrdinalIgnoreCase)
            ? local.DayOfWeek != DayOfWeek.Sunday
            : wh.Days.Equals("All", StringComparison.OrdinalIgnoreCase) || local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
        if (!days) return false;
        return TimeOnly.TryParse(wh.Start, out var s) && TimeOnly.TryParse(wh.End, out var e) && TimeOnly.FromDateTime(local) is var t && t >= s && t <= e;
    }

    public bool AppExcluded(string app) =>
        _current.Privacy.ExcludeApps.Any(x => string.Equals(x, app, StringComparison.OrdinalIgnoreCase));

    public bool DomainExcluded(string domain) =>
        _current.Privacy.ExcludeDomains.Any(p => Domains.Matches(domain, p));

    /// <summary>Applies the window-title privacy mode: full | masked | none.</summary>
    public string? FilterTitle(string app, string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || AppExcluded(app)) return null;
        switch (_current.Collectors.CaptureWindowTitles)
        {
            case "none": return null;
            case "full": return title.Length > 300 ? title[..300] : title;
            default:
                var masked = EmailRegex().Replace(title, "[email]");
                masked = NumberRegex().Replace(masked, "[#]");
                return masked.Length > 80 ? masked[..80] + "…" : masked;
        }
    }

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.-]+")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\d{4,}")]
    private static partial Regex NumberRegex();
}
