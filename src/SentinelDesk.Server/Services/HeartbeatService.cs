using Microsoft.EntityFrameworkCore;
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
        await ApplyLocationAsync(db, tracked, hb, now);

        // Branch-scoped fan-out: unrestricted admins listen on live:all, branch admins on live:{unit}
        var groups = new List<string> { "live:all", $"device:{device.Id}" };
        if (device.OrgUnitId is { } unit) groups.Add($"live:{unit}");
        await hub.Clients.Groups(groups).SendAsync("presence", p);
        if (!wasOnline) await hub.Clients.Group("console").SendAsync("deviceOnline", new { device.Id, device.Hostname });
    }

    /// <summary>Latest position on the device row; history row when it moved &gt;150 m or every 30 min while stationary.</summary>
    private static async Task ApplyLocationAsync(AppDb db, Device d, HeartbeatRequest hb, DateTime now)
    {
        if (hb.Location is not { } loc)
        {
            var err = hb.LocationError is { Length: > 200 } e ? e[..200] : hb.LocationError;
            if (d.LocError != err) { d.LocError = err; await db.SaveChangesAsync(); }
            return;
        }
        if (Math.Abs(loc.Lat) > 90 || Math.Abs(loc.Lng) > 180) return;
        var at = loc.AtUtc > now ? now : loc.AtUtc;
        if (d.LocAt == at && d.LocLat == loc.Lat && d.LocLng == loc.Lng) return; // same fix as the previous heartbeat
        var source = loc.Source is { Length: > 32 } s ? s[..32] : loc.Source;

        var last = await db.Locations.AsNoTracking().Where(x => x.DeviceId == d.Id).OrderByDescending(x => x.Time).FirstOrDefaultAsync();
        if (last is null || DistanceM(last.Lat, last.Lng, loc.Lat, loc.Lng) > Math.Max(150, loc.AccuracyM) || at - last.Time > TimeSpan.FromMinutes(30))
            db.Locations.Add(new LocationPoint { DeviceId = d.Id, Time = at, Lat = loc.Lat, Lng = loc.Lng, AccuracyM = loc.AccuracyM, Source = source });

        d.LocLat = loc.Lat; d.LocLng = loc.Lng; d.LocAccuracyM = Math.Round(loc.AccuracyM); d.LocAt = at; d.LocSource = source; d.LocError = null;
        await db.SaveChangesAsync();
    }

    public static double DistanceM(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6_371_000;
        double Rad(double v) => v * Math.PI / 180;
        var dLat = Rad(lat2 - lat1); var dLng = Rad(lng2 - lng1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}

/// <summary>Indirection so services don't depend on the security layer directly.</summary>
public sealed class DeviceCacheInvalidator(Security.DeviceCache cache)
{
    public void Invalidate(Guid id) => cache.Invalidate(id);
}
