using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SentinelDesk.Server.Data;
using SentinelDesk.Server.Hubs;
using SentinelDesk.Server.Security;
using SentinelDesk.Shared;

namespace SentinelDesk.Server.Services;

/// <summary>Live device state (Redis equivalent for single-node deployments).</summary>
public sealed class PresenceStore
{
    private readonly ConcurrentDictionary<Guid, LivePresence> _live = new();
    private readonly ConcurrentDictionary<Guid, string> _agentConnections = new();

    public IReadOnlyCollection<LivePresence> All => _live.Values.ToList();
    public LivePresence? Get(Guid id) => _live.TryGetValue(id, out var p) ? p : null;
    public void Set(LivePresence p) => _live[p.DeviceId] = p;
    public void Remove(Guid id) => _live.TryRemove(id, out _);

    public bool IsOnline(Guid id, TimeSpan? window = null) =>
        _live.TryGetValue(id, out var p) && DateTime.UtcNow - p.LastSeen < (window ?? TimeSpan.FromSeconds(150));

    public void AgentConnected(Guid id, string connectionId) => _agentConnections[id] = connectionId;
    public void AgentDisconnected(Guid id, string connectionId)
    {
        if (_agentConnections.TryGetValue(id, out var c) && c == connectionId) _agentConnections.TryRemove(id, out _);
    }
    public string? AgentConnection(Guid id) => _agentConnections.TryGetValue(id, out var c) ? c : null;

    // Live per-site traffic. Kept out of the presence broadcast (sent to every console viewer) because it is
    // browsing data: served only by GET /api/devices/{id}/web/live, which checks the web-viewing permission.
    private readonly ConcurrentDictionary<Guid, (DateTime At, List<LiveDomain> Sites)> _liveSites = new();
    public void SetLiveSites(Guid id, List<LiveDomain>? sites) { if (sites is not null) _liveSites[id] = (DateTime.UtcNow, sites); }
    public (DateTime At, List<LiveDomain> Sites)? LiveSites(Guid id) => _liveSites.TryGetValue(id, out var s) ? s : null;
}

public sealed record LivePresence(
    Guid DeviceId, string Hostname, Guid? OrgUnitId, DateTime LastSeen, string? User, string? Ip,
    double Cpu, double Ram, double Disk, long NetInBps, long NetOutBps, long TodayIn, long TodayOut,
    string? CurrentApp, string? CurrentTitle, bool Idle, bool Locked, long UptimeSec, string AgentVersion,
    bool TrayRunning, int QueueDepth, string? ConnectionType, bool Realtime);

public sealed class PolicyService(AppDb db, ServerKeys keys)
{
    public static AgentPolicy DefaultPolicy() => new() { PolicyId = "default", Name = "Default" };

    /// <summary>The device's monitoring policy, the restriction policies stacked on top, and the version of the combination.</summary>
    public sealed record Resolved(Policy Policy, IReadOnlyList<Policy> Restrictions, int EffectiveVersion);

    public async Task<Resolved> ResolveAsync(Device device)
    {
        var assignments = await db.PolicyAssignments.AsNoTracking().ToListAsync();
        var policies = await db.Policies.AsNoTracking().ToDictionaryAsync(p => p.Id);

        // targets from most to least specific: the device, then its org unit and every parent up to the organization
        var chain = new List<(string Type, Guid Id)> { ("device", device.Id) };
        if (device.OrgUnitId is { } unit)
        {
            var units = await db.OrgUnits.AsNoTracking().ToDictionaryAsync(o => o.Id);
            Guid? cursor = unit;
            for (var guard = 0; cursor is { } c && guard < 20; guard++)
            {
                chain.Add(("orgUnit", c));
                cursor = units.TryGetValue(c, out var ou) ? ou.ParentId : null;
            }
        }
        var applying = chain.SelectMany(t => assignments.Where(a => a.TargetType == t.Type && a.TargetId == t.Id))
            .Select(a => policies.GetValueOrDefault(a.PolicyId)).OfType<Policy>().ToList();

        // monitoring settings always come from the Default policy (the console only manages block policies)
        var main = policies.Values.FirstOrDefault(p => p.IsDefault) ?? await db.Policies.AsNoTracking().FirstAsync(p => p.IsDefault);
        var restrictions = applying.Where(p => PolicyKinds.IsRestriction(p.Kind)).DistinctBy(p => p.Id).OrderBy(p => p.Id).ToList();
        return new Resolved(main, restrictions, EffectiveVersion(main, restrictions));
    }

    public static int EffectiveVersion(Policy main, IEnumerable<Policy> restrictions)
    {
        var key = string.Join(";", restrictions.Select(r => $"{r.Id:N}|{r.Version}").Prepend($"{main.Id:N}|{main.Version}"));
        return (int)(Crc32.HashToUInt32(Encoding.UTF8.GetBytes(key)) & 0x7FFFFFFF);
    }

    /// <summary>Adds a restriction policy's rules to the device's effective policy.</summary>
    public static void Merge(AgentPolicy into, Policy restriction)
    {
        var r = Json.Deserialize<AgentPolicy>(restriction.Json)?.Block ?? new BlockSettings();
        var b = into.Block;
        switch (restriction.Kind)
        {
            case PolicyKinds.Websites:
                b.Urls = [.. b.Urls.Union(r.Urls, StringComparer.OrdinalIgnoreCase)];
                b.Categories = [.. b.Categories.Union(r.Categories, StringComparer.OrdinalIgnoreCase)];
                break;
            case PolicyKinds.Apps:
                if (b.Apps.Count == 0) { b.NotifyUser = r.NotifyUser; b.Message = r.Message; }
                b.Apps = [.. b.Apps.Union(r.Apps, StringComparer.OrdinalIgnoreCase)];
                break;
            case PolicyKinds.Usb:
                into.Collectors.UsbBlock = true;
                break;
        }
    }

    public async Task<SignedPolicy> SignedForAsync(Device device)
    {
        var (policy, restrictions, version) = await ResolveAsync(device);
        var model = Json.Deserialize<AgentPolicy>(policy.Json) ?? DefaultPolicy();
        foreach (var r in restrictions) Merge(model, r);
        model.PolicyId = policy.Id.ToString();
        model.Name = restrictions.Count == 0 ? policy.Name : $"{policy.Name} + {string.Join(", ", restrictions.Select(r => r.Name))}";
        model.Version = version;
        var json = Json.Serialize(model);
        return new SignedPolicy { Version = version, PolicyJson = json, Signature = Signing.Sign(keys.SigningKey, json) };
    }
}

public sealed class CommandService(AppDb db, ServerKeys keys, PresenceStore presence, IHubContext<AgentHub> agentHub)
{
    public ServerCommand ToSigned(DeviceCommand c)
    {
        var cmd = new ServerCommand
        {
            CommandId = c.Id, DeviceId = c.DeviceId, Type = c.Type, Payload = c.Payload,
            IssuedUtc = DateTime.SpecifyKind(c.CreatedAt, DateTimeKind.Utc),
            ExpiresUtc = DateTime.SpecifyKind(c.ExpiresAt, DateTimeKind.Utc)
        };
        cmd.Signature = Signing.Sign(keys.SigningKey, cmd.SigningString());
        return cmd;
    }

    public async Task<DeviceCommand> QueueAsync(Guid deviceId, string type, string? payload, string actor, bool persist = true)
    {
        var cmd = new DeviceCommand { DeviceId = deviceId, Type = type, Payload = payload, CreatedBy = actor };
        // truncate sub-microsecond precision so the signed timestamp round-trips through the DB unchanged
        cmd.CreatedAt = new DateTime(cmd.CreatedAt.Ticks - cmd.CreatedAt.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        cmd.ExpiresAt = cmd.CreatedAt.AddDays(3);
        db.Commands.Add(cmd);
        await db.SaveChangesAsync();
        await TryPushAsync(cmd);
        return cmd;
    }

    public async Task TryPushAsync(DeviceCommand cmd)
    {
        var conn = presence.AgentConnection(cmd.DeviceId);
        if (conn is null) return;
        await agentHub.Clients.Client(conn).SendAsync("command", ToSigned(cmd));
        cmd.Status = "sent";
        await db.SaveChangesAsync();
    }

    public async Task BroadcastPolicyChangedAsync(IEnumerable<Guid> deviceIds)
    {
        foreach (var id in deviceIds)
            if (presence.AgentConnection(id) is { } conn)
                await agentHub.Clients.Client(conn).SendAsync("policyChanged");
    }
}

public sealed class AuditService(AppDb db, IHttpContextAccessor http)
{
    public async Task LogAsync(string action, string? target = null, string? details = null)
    {
        var ctx = http.HttpContext;
        db.Audit.Add(new AuditEntry
        {
            Actor = ctx?.User.Identity?.Name ?? "system",
            Action = action,
            Target = target,
            Details = details?.Length > 1000 ? details[..1000] : details,
            Ip = ctx?.Connection.RemoteIpAddress?.ToString()
        });
        await db.SaveChangesAsync();
    }
}

public sealed class Notifier(IConfiguration config, IHttpClientFactory http, ILogger<Notifier> log)
{
    public async Task SendAsync(Alert alert, AlertRule rule, string hostname)
    {
        var subject = $"[SentinelDesk] {alert.Severity.ToUpperInvariant()}: {alert.Title} — {hostname}";
        if (rule.NotifyWebhook && config["Notifications:WebhookUrl"] is { Length: > 0 } url)
        {
            try
            {
                await http.CreateClient().PostAsJsonAsync(url, new { text = $"{subject}\n{alert.Details}" });
            }
            catch (Exception ex) { log.LogWarning(ex, "Webhook notification failed"); }
        }
        if (rule.NotifyEmail && config["Notifications:Smtp:Host"] is { Length: > 0 } host)
        {
            try
            {
                using var smtp = new System.Net.Mail.SmtpClient(host, config.GetValue("Notifications:Smtp:Port", 587))
                {
                    EnableSsl = config.GetValue("Notifications:Smtp:Ssl", true),
                    Credentials = new System.Net.NetworkCredential(config["Notifications:Smtp:User"], config["Notifications:Smtp:Password"])
                };
                await smtp.SendMailAsync(config["Notifications:Smtp:From"]!, config["Notifications:Smtp:To"]!, subject, alert.Details ?? alert.Title);
            }
            catch (Exception ex) { log.LogWarning(ex, "Email notification failed"); }
        }
    }
}
