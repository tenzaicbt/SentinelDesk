using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SentinelDesk.Server.Data;
using SentinelDesk.Server.Hubs;
using SentinelDesk.Shared;

namespace SentinelDesk.Server.Services;

public sealed class IngestService(AppDb db, IHubContext<ConsoleHub> hub, ILogger<IngestService> log)
{
    private static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : DateTime.SpecifyKind(t.ToUniversalTime(), DateTimeKind.Utc);

    public async Task IngestAsync(Guid deviceId, TelemetryPayload p, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var maxFuture = now.AddMinutes(10);

        foreach (var s in p.Perf.Where(s => s.Time < maxFuture))
            db.Perf.Add(new PerfPoint { DeviceId = deviceId, Time = Utc(s.Time), Cpu = (float)s.Cpu, Ram = (float)s.Ram, Disk = (float)s.Disk });

        var dailyDelta = new Dictionary<DateOnly, (long In, long Out, int Active, int Idle)>();
        foreach (var s in p.Adapters.Where(s => s.Time < maxFuture && (s.BytesIn > 0 || s.BytesOut > 0)))
        {
            db.NetAdapter.Add(new NetAdapterPoint
            {
                DeviceId = deviceId, Time = Utc(s.Time), Adapter = Trunc(s.Adapter, 120), Type = Trunc(s.Type, 20),
                BytesIn = Math.Max(0, s.BytesIn), BytesOut = Math.Max(0, s.BytesOut)
            });
            var day = DateOnly.FromDateTime(LocalDay(s.Time));
            dailyDelta.TryGetValue(day, out var d);
            dailyDelta[day] = (d.In + Math.Max(0, s.BytesIn), d.Out + Math.Max(0, s.BytesOut), d.Active, d.Idle);
        }

        foreach (var s in p.ProcessNet.Where(s => s.Time < maxFuture && (s.BytesIn > 0 || s.BytesOut > 0)))
            db.NetProcess.Add(new NetProcessPoint
            {
                DeviceId = deviceId, Time = Utc(s.Time), Process = Trunc(s.Process, 120), BytesIn = s.BytesIn, BytesOut = s.BytesOut
            });

        foreach (var s in p.AppUsage.Where(s => s.Time < maxFuture))
        {
            db.AppUsage.Add(new AppUsagePoint
            {
                DeviceId = deviceId, Time = Utc(s.Time), User = Trunc(s.User, 80), App = Trunc(s.App, 120),
                Title = Trunc(s.Title, 300), ActiveSec = Math.Clamp(s.ActiveSec, 0, 3600), IdleSec = Math.Clamp(s.IdleSec, 0, 3600)
            });
            var day = DateOnly.FromDateTime(LocalDay(s.Time));
            dailyDelta.TryGetValue(day, out var d);
            dailyDelta[day] = (d.In, d.Out, d.Active + s.ActiveSec, d.Idle + s.IdleSec);
        }

        foreach (var s in p.Web.Where(s => s.Time < maxFuture && !string.IsNullOrWhiteSpace(s.Domain) && s.Count + s.BytesIn + s.BytesOut > 0))
            db.Web.Add(new WebPoint
            {
                DeviceId = deviceId, Time = Utc(s.Time), User = Trunc(s.User, 80), Domain = Trunc(s.Domain.ToLowerInvariant(), 200),
                Process = Trunc(s.Process, 120), Category = Domains.Categorize(s.Domain), Count = Math.Max(0, s.Count),
                BytesIn = Math.Max(0, s.BytesIn), BytesOut = Math.Max(0, s.BytesOut)
            });

        var newEvents = new List<DeviceEvent>();
        foreach (var e in p.Events)
        {
            var ev = new DeviceEvent
            {
                DeviceId = deviceId, Time = Utc(e.Time), Type = Trunc(e.Type, 40)!, Severity = Trunc(e.Severity, 10)!,
                Message = Trunc(e.Message, 500)!, DataJson = e.Data is null ? null : Json.Serialize(e.Data)
            };
            db.Events.Add(ev);
            newEvents.Add(ev);
        }

        var device = await db.Devices.FirstAsync(d => d.Id == deviceId, ct);

        if (p.Hardware is { } hw)
        {
            var snap = await db.Hardware.FindAsync([deviceId], ct);
            if (snap is null) db.Hardware.Add(snap = new HardwareSnapshot { DeviceId = deviceId });
            snap.CollectedAt = now;
            snap.Json = Json.Serialize(hw);
            device.Manufacturer = Trunc(hw.Manufacturer, 64);
            device.Model = Trunc(hw.Model, 64);
            device.Serial = Trunc(hw.Serial, 64) ?? device.Serial;
            if (!string.IsNullOrEmpty(hw.Os)) device.Os = Trunc(hw.Os, 80)!;
            if (!string.IsNullOrEmpty(hw.OsBuild)) device.OsBuild = Trunc(hw.OsBuild, 40)!;
        }

        if (p.Security is { } sec)
        {
            var snap = await db.Security.FindAsync([deviceId], ct);
            if (snap is null) db.Security.Add(snap = new SecuritySnapshot { DeviceId = deviceId });
            snap.CollectedAt = now;
            snap.Json = Json.Serialize(sec);
        }

        if (p.Processes is { } procs)
        {
            var snap = await db.Processes.FindAsync([deviceId], ct);
            if (snap is null) db.Processes.Add(snap = new ProcessSnapshot { DeviceId = deviceId });
            snap.CollectedAt = now;
            snap.Json = Json.Serialize(procs.Take(400));
        }

        if (p.Software is { } sw) await MergeSoftwareAsync(deviceId, sw, now, ct);

        foreach (var (day, d) in dailyDelta)
        {
            var row = await db.Daily.FindAsync([deviceId, day], ct);
            if (row is null) db.Daily.Add(row = new DailyUsage { DeviceId = deviceId, Day = day });
            row.BytesIn += d.In; row.BytesOut += d.Out; row.ActiveSec += d.Active; row.IdleSec += d.Idle;
        }

        await db.SaveChangesAsync(ct);

        if (newEvents.Count > 0)
            await hub.Clients.Group($"device:{deviceId}").SendAsync("events", newEvents.Select(e => new { e.Time, e.Type, e.Severity, e.Message }), ct);
        if (p.Hardware is not null || p.Software is not null || p.Security is not null)
            await hub.Clients.Group($"device:{deviceId}").SendAsync("inventoryUpdated", deviceId, ct);
    }

    private async Task MergeSoftwareAsync(Guid deviceId, List<SoftwareItem> items, DateTime now, CancellationToken ct)
    {
        var current = await db.Software.Where(s => s.DeviceId == deviceId && s.RemovedAt == null).ToListAsync(ct);
        var isFirst = current.Count == 0 && !await db.Software.AnyAsync(s => s.DeviceId == deviceId, ct);
        var incoming = items.Where(i => !string.IsNullOrWhiteSpace(i.Name))
            .GroupBy(i => (i.Name.Trim(), i.Version?.Trim()))
            .Select(g => g.First()).ToList();
        var key = (string n, string? v) => $"{n}\u0001{v}";
        var currentMap = current.GroupBy(c => key(c.Name, c.Version)).ToDictionary(g => g.Key, g => g.First());
        var incomingKeys = new HashSet<string>();

        foreach (var i in incoming)
        {
            var k = key(Trunc(i.Name.Trim(), 200)!, Trunc(i.Version?.Trim(), 60));
            incomingKeys.Add(k);
            if (currentMap.ContainsKey(k)) continue;
            db.Software.Add(new InstalledSoftware
            {
                DeviceId = deviceId, Name = Trunc(i.Name.Trim(), 200)!, Version = Trunc(i.Version?.Trim(), 60),
                Publisher = Trunc(i.Publisher, 120), InstallDate = Trunc(i.InstallDate, 20), FirstSeen = now
            });
            if (!isFirst)
                db.Events.Add(new DeviceEvent
                {
                    DeviceId = deviceId, Time = now, Type = "software.installed", Severity = "info",
                    Message = $"Installed: {i.Name} {i.Version}".Trim()
                });
        }
        foreach (var (k, c) in currentMap)
        {
            if (incomingKeys.Contains(k)) continue;
            c.RemovedAt = now;
            db.Events.Add(new DeviceEvent
            {
                DeviceId = deviceId, Time = now, Type = "software.removed", Severity = "info", Message = $"Removed: {c.Name} {c.Version}".Trim()
            });
        }
        log.LogDebug("Software merged for {Device}: {Count} items", deviceId, incoming.Count);
    }

    private static readonly TimeZoneInfo Tz = FindTz();
    private static TimeZoneInfo FindTz()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Colombo"); }
        catch { try { return TimeZoneInfo.FindSystemTimeZoneById("Sri Lanka Standard Time"); } catch { return TimeZoneInfo.Local; } }
    }

    /// <summary>Reporting day boundary uses the organisation's timezone (Asia/Colombo).</summary>
    public static DateTime LocalDay(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(Utc(utc), Tz).Date;
    public static DateOnly Today() => DateOnly.FromDateTime(LocalDay(DateTime.UtcNow));
    public static DateTime DayStartUtc(DateOnly day) => TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), Tz);
    public static TimeZoneInfo OrgTz => Tz;

    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(s))]
    private static string? Trunc(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
