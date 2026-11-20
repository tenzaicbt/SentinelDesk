using Microsoft.AspNetCore.SignalR;
using SentinelDesk.Server.Data;
using SentinelDesk.Server.Hubs;
using SentinelDesk.Shared;

namespace SentinelDesk.Server.Services;

public sealed class HeartbeatService(PresenceStore presence, IHubContext<ConsoleHub> hub, DeviceCacheInvalidator invalidate)
{
    public async Task ApplyAsync(AppDb db, Device device, HeartbeatRequest hb, string? ip, bool realtime)
    {
        var now = DateTime.UtcNow;
        var p = new LivePresence(device.Id, device.Hostname, device.OrgUnitId, now, hb.LoggedInUser, hb.LocalIp ?? ip,
            Math.Round(hb.Cpu, 1), Math.Round(hb.Ram, 1), Math.Round(hb.Disk, 1), hb.NetInBps, hb.NetOutBps,
            hb.TodayBytesIn, hb.TodayBytesOut, hb.CurrentApp, hb.CurrentTitle, hb.UserIdle, hb.Locked,
            hb.UptimeSec, hb.AgentVersion, hb.TrayRunning, hb.QueueDepth, hb.ConnectionType, realtime);
        var wasOnline = presence.IsOnline(device.Id);
        presence.Set(p);
        presence.SetLiveSites(device.Id, hb.TopDomains?.Where(s => !string.IsNullOrWhiteSpace(s.Domain)).Take(20)
            .Select(s => new LiveDomain(s.Domain.Length > 200 ? s.Domain[..200] : s.Domain.ToLowerInvariant(), Math.Max(0, s.InBps), Math.Max(0, s.OutBps))).ToList());

        var tracked = db.Devices.Local.FirstOrDefault(d => d.Id == device.Id) ?? db.Devices.Attach(device).Entity;
        var userChanged = tracked.LoggedInUser != hb.LoggedInUser;
        if (tracked.LastSeenAt is null || now - tracked.LastSeenAt > TimeSpan.FromSeconds(55) || userChanged || !wasOnline
            || tracked.AgentVersion != hb.AgentVersion)
        {
            tracked.LastSeenAt = now;
            tracked.LastIp = hb.LocalIp ?? tracked.LastIp;
            tracked.PublicIp = ip;
            tracked.LoggedInUser = hb.LoggedInUser;
            tracked.AgentVersion = hb.AgentVersion;
            tracked.PolicyVersionApplied = hb.PolicyVersion;
            await db.SaveChangesAsync();
            invalidate.Invalidate(device.Id);
        }

        // Branch-scoped fan-out: unrestricted admins listen on live:all, branch admins on live:{unit}
        var groups = new List<string> { "live:all", $"device:{device.Id}" };
        if (device.OrgUnitId is { } unit) groups.Add($"live:{unit}");
        await hub.Clients.Groups(groups).SendAsync("presence", p);
        if (!wasOnline) await hub.Clients.Group("console").SendAsync("deviceOnline", new { device.Id, device.Hostname });
    }
}

/// <summary>Indirection so services don't depend on the security layer directly.</summary>
public sealed class DeviceCacheInvalidator(Security.DeviceCache cache)
{
    public void Invalidate(Guid id) => cache.Invalidate(id);
}
