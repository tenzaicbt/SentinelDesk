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

    public async Task<(Policy Policy, int EffectiveVersion)> ResolveAsync(Device device)
    {
        var assignments = await db.PolicyAssignments.AsNoTracking().ToListAsync();
        Guid? policyId = assignments.FirstOrDefault(a => a.TargetType == "device" && a.TargetId == device.Id)?.PolicyId;
        if (policyId is null && device.OrgUnitId is { } unit)
        {
            var units = await db.OrgUnits.AsNoTracking().ToDictionaryAsync(o => o.Id);
            Guid? cursor = unit;
            var guard = 0;
            while (cursor is { } c && policyId is null && guard++ < 20)
            {
                policyId = assignments.FirstOrDefault(a => a.TargetType == "orgUnit" && a.TargetId == c)?.PolicyId;
                cursor = units.TryGetValue(c, out var ou) ? ou.ParentId : null;
            }
        }
        var policy = (policyId is { } pid ? await db.Policies.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid) : null)
                     ?? await db.Policies.AsNoTracking().FirstAsync(p => p.IsDefault);
        return (policy, EffectiveVersion(policy));
    }

    public static int EffectiveVersion(Policy p) =>
        (int)(Crc32.HashToUInt32(Encoding.UTF8.GetBytes($"{p.Id:N}|{p.Version}")) & 0x7FFFFFFF);

    public async Task<SignedPolicy> SignedForAsync(Device device)
    {
        var (policy, version) = await ResolveAsync(device);
        var model = Json.Deserialize<AgentPolicy>(policy.Json) ?? DefaultPolicy();
        model.PolicyId = policy.Id.ToString();
        model.Name = policy.Name;
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
