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

    /// <summary>The device's monitoring policy, the block policies on it (with when each one ends), its exceptions,
    /// whether an emergency unblock applies, and the version of the combination.</summary>
    public sealed record Resolved(Policy Policy, IReadOnlyList<Policy> Restrictions, int EffectiveVersion)
    {
        public IReadOnlyDictionary<Guid, DateTime?> Until { get; init; } = new Dictionary<Guid, DateTime?>();
        public IReadOnlyList<BlockExceptionRow> Exceptions { get; init; } = [];
        public EmergencyUnblock? Emergency { get; init; }
    }

    /// <summary>The device, then its org unit and every parent up to the organization (most specific first).</summary>
    public async Task<List<(string Type, Guid Id)>> ChainAsync(Device device)
    {
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
        return chain;
    }

    public async Task<Resolved> ResolveAsync(Device device)
    {
        var now = DateTime.UtcNow;
        var assignments = (await db.PolicyAssignments.AsNoTracking().ToListAsync()).Where(a => a.UntilUtc is null || a.UntilUtc > now).ToList();
        var policies = await db.Policies.AsNoTracking().ToDictionaryAsync(p => p.Id);
        var chain = await ChainAsync(device);
        bool InChain(string type, Guid id) => chain.Any(t => t.Type == type && t.Id == id);

        // monitoring settings always come from the Default policy (the console only manages block policies)
        var main = policies.Values.FirstOrDefault(p => p.IsDefault) ?? await db.Policies.AsNoTracking().FirstAsync(p => p.IsDefault);

        var emergencies = await db.EmergencyUnblocks.AsNoTracking().Where(e => e.EndedAt == null).ToListAsync();
        var emergency = emergencies.FirstOrDefault(e => (e.UntilUtc is null || e.UntilUtc > now) && (e.OrgUnitId is null || InChain("orgUnit", e.OrgUnitId.Value)));

        var restrictions = new List<Policy>();
        var until = new Dictionary<Guid, DateTime?>();
        var exceptions = new List<BlockExceptionRow>();
        if (emergency is null)
        {
            foreach (var a in assignments.Where(a => InChain(a.TargetType, a.TargetId)))
            {
                if (!policies.TryGetValue(a.PolicyId, out var p) || !PolicyKinds.IsRestriction(p.Kind)) continue;
                if (!until.ContainsKey(p.Id)) { restrictions.Add(p); until[p.Id] = a.UntilUtc; }
                else if (until[p.Id] is { } u) until[p.Id] = a.UntilUtc is null ? null : (a.UntilUtc > u ? a.UntilUtc : u);   // the longest assignment wins
            }
            restrictions = [.. restrictions.OrderBy(p => p.Id)];
            exceptions = (await db.BlockExceptions.AsNoTracking().ToListAsync())
                .Where(e => (e.UntilUtc is null || e.UntilUtc > now) && InChain(e.TargetType, e.TargetId)).OrderBy(e => e.Id).ToList();
        }

        var key = string.Join(";", restrictions.Select(r => $"{r.Id:N}|{r.Version}|{until[r.Id]:O}")
            .Concat(exceptions.Select(e => $"x{e.Id:N}|{e.UntilUtc:O}"))
            .Prepend(emergency is null ? "" : $"e{emergency.Id:N}")
            .Prepend($"{main.Id:N}|{main.Version}"));
        return new Resolved(main, restrictions, (int)(Crc32.HashToUInt32(Encoding.UTF8.GetBytes(key)) & 0x7FFFFFFF))
        { Until = until, Exceptions = exceptions, Emergency = emergency };
    }

    /// <summary>One block policy as a rule set for the agent, which applies schedules, users and expiry itself (also offline).</summary>
    public static BlockRuleSet ToRuleSet(Policy restriction, DateTime? untilUtc)
    {
        var r = Json.Deserialize<AgentPolicy>(restriction.Json)?.Block ?? new BlockSettings();
        return new BlockRuleSet
        {
            PolicyId = restriction.Id.ToString(), Name = restriction.Name, Kind = restriction.Kind,
            Urls = restriction.Kind == PolicyKinds.Websites ? r.Urls : [],
            Categories = restriction.Kind == PolicyKinds.Websites ? r.Categories : [],
            Apps = restriction.Kind == PolicyKinds.Apps ? r.Apps : [],
            Mode = r.Mode, Schedule = r.Schedule, Users = r.Users, ExpiresUtc = untilUtc,
            NotifyUser = r.NotifyUser, Message = r.Message, UsbMode = r.UsbMode, UsbAllowSerials = r.UsbAllowSerials,
        };
    }

    public async Task<SignedPolicy> SignedForAsync(Device device)
    {
        var res = await ResolveAsync(device);
        var model = Json.Deserialize<AgentPolicy>(res.Policy.Json) ?? DefaultPolicy();
        model.Block = new BlockSettings
        {
            Sets = [.. res.Restrictions.Select(r => ToRuleSet(r, res.Until.GetValueOrDefault(r.Id)))],
            Exceptions = [.. res.Exceptions.Select(e => new BlockException
            {
                PolicyId = e.PolicyId?.ToString(), Kind = e.Kind, Value = e.Value, ExpiresUtc = e.UntilUtc, Reason = e.Reason,
            })],
        };
        model.Collectors.UsbBlock = false;   // USB is decided by the agent from the USB rule sets
        model.PolicyId = res.Policy.Id.ToString();
        model.Name = res.Emergency is not null ? $"{res.Policy.Name} (emergency unblock)"
            : res.Restrictions.Count == 0 ? res.Policy.Name : $"{res.Policy.Name} + {string.Join(", ", res.Restrictions.Select(r => r.Name))}";
        model.Version = res.EffectiveVersion;
        var json = Json.Serialize(model);
        return new SignedPolicy { Version = res.EffectiveVersion, PolicyJson = json, Signature = Signing.Sign(keys.SigningKey, json) };
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
