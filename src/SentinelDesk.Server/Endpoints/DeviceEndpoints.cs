using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SentinelDesk.Server.Data;
using SentinelDesk.Server.Security;
using SentinelDesk.Server.Services;
using SentinelDesk.Shared;

namespace SentinelDesk.Server.Endpoints;

public static class DeviceEndpoints
{
    public sealed record CommandRequest(string Type, string? Payload);
    public sealed record UpdateDeviceRequest(Guid? OrgUnitId, string? Tags);
    public sealed record BulkCommandRequest(Guid[] DeviceIds, string Type, string? Payload);

    private static readonly string[] AllowedCommands =
    [
        CommandTypes.RefreshInventory, CommandTypes.CollectNow, CommandTypes.ShowMessage, CommandTypes.Uninstall, CommandTypes.PolicyUpdated
    ];

    public static void MapDeviceApi(this WebApplication app)
    {
        var g = app.MapGroup("/api/devices").RequireAuthorization("Console");
        var write = app.MapGroup("/api/devices").RequireAuthorization("Write");

        g.MapGet("", async (string? search, Guid? orgUnitId, string? status, bool? online, AccessScope scope, AppDb db, PresenceStore presence) =>
        {
            var q = await scope.DevicesAsync();
            if (!string.IsNullOrWhiteSpace(search))
                q = q.Where(d => d.Hostname.Contains(search) || (d.LoggedInUser != null && d.LoggedInUser.Contains(search))
                                 || (d.LastIp != null && d.LastIp.Contains(search)) || (d.Serial != null && d.Serial.Contains(search)));
            if (orgUnitId is { } ou)
            {
                var all = await db.OrgUnits.AsNoTracking().Select(o => new { o.Id, o.ParentId }).ToListAsync();
                var subtree = Subtree(ou, all.Select(o => (o.Id, o.ParentId)));
                q = q.Where(d => d.OrgUnitId != null && subtree.Contains(d.OrgUnitId.Value));
            }
            if (Enum.TryParse<DeviceStatus>(status, true, out var st)) q = q.Where(d => d.Status == st);
            else q = q.Where(d => d.Status != DeviceStatus.Retired);

            var list = await q.OrderBy(d => d.Hostname).ToListAsync();
            var today = IngestService.Today();
            var daily = await db.Daily.AsNoTracking().Where(x => x.Day == today).ToDictionaryAsync(x => x.DeviceId);
            var units = await db.OrgUnits.AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.Name);
            var openAlerts = await db.Alerts.AsNoTracking().Where(a => a.Status == "open").GroupBy(a => a.DeviceId)
                .Select(x => new { x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);

            var rows = list.Select(d =>
            {
                var p = presence.Get(d.Id);
                var isOnline = presence.IsOnline(d.Id);
                daily.TryGetValue(d.Id, out var du);
                return new
                {
                    d.Id, d.Hostname, d.Domain, User = p?.User ?? d.LoggedInUser, Ip = p?.Ip ?? d.LastIp, d.PublicIp, d.Os, d.OsBuild,
                    d.AgentVersion, Status = d.Status.ToString().ToLowerInvariant(), Online = isOnline, LastSeen = p?.LastSeen ?? d.LastSeenAt,
                    d.OrgUnitId, Branch = d.OrgUnitId is { } u ? units.GetValueOrDefault(u) : null,
                    d.Manufacturer, d.Model, d.Serial, d.Tags, d.IsSimulated,
                    TodayBytes = isOnline && p is not null ? Math.Max(p.TodayIn + p.TodayOut, (du?.BytesIn ?? 0) + (du?.BytesOut ?? 0)) : (du?.BytesIn ?? 0) + (du?.BytesOut ?? 0),
                    Cpu = isOnline ? p?.Cpu : null, Ram = isOnline ? p?.Ram : null,
                    CurrentApp = isOnline ? p?.CurrentApp : null,
                    Alerts = openAlerts.GetValueOrDefault(d.Id),
                };
            });
            if (online is { } o) rows = rows.Where(r => r.Online == o);
            return Results.Ok(rows.ToList());
        });

        g.MapGet("/{id:guid}", async (Guid id, AccessScope scope, AppDb db, PresenceStore presence, PolicyService policies, AuditService audit) =>
        {
            if (!await scope.CanAccessDeviceAsync(id)) return Results.Forbid();
            var d = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (d is null) return Results.NotFound();
            var hw = await db.Hardware.AsNoTracking().FirstOrDefaultAsync(x => x.DeviceId == id);
            var sec = await db.Security.AsNoTracking().FirstOrDefaultAsync(x => x.DeviceId == id);
            var units = await db.OrgUnits.AsNoTracking().ToDictionaryAsync(o => o.Id);
            var path = new List<string>();
            var cursor = d.OrgUnitId;
            for (var i = 0; cursor is { } c && units.TryGetValue(c, out var ou) && i < 10; i++) { path.Insert(0, ou.Name); cursor = ou.ParentId; }
            var (policy, ver) = await policies.ResolveAsync(d);
            var today = IngestService.Today();
            var du = await db.Daily.AsNoTracking().FirstOrDefaultAsync(x => x.DeviceId == id && x.Day == today);
            var p = presence.Get(id);
            await audit.LogAsync("device.view", d.Hostname);
            return Results.Ok(new
            {
                d.Id, d.Hostname, d.Domain, d.MachineId, d.Serial, d.Manufacturer, d.Model, d.Os, d.OsBuild, d.AgentVersion,
                Status = d.Status.ToString().ToLowerInvariant(), d.EnrolledAt, d.LastSeenAt, d.LastIp, d.PublicIp, d.LoggedInUser,
                d.OrgUnitId, OrgPath = path, d.Tags, d.CertThumbprint, d.CertExpiresAt, d.IsSimulated,
                Policy = new { policy.Id, policy.Name, EffectiveVersion = ver, Applied = d.PolicyVersionApplied == ver || d.IsSimulated },
                Online = presence.IsOnline(id),
                Presence = p is null ? null : DashboardEndpoints.Sanitize(p, scope),
                Today = new { BytesIn = Math.Max(du?.BytesIn ?? 0, p?.TodayIn ?? 0), BytesOut = Math.Max(du?.BytesOut ?? 0, p?.TodayOut ?? 0), ActiveSec = du?.ActiveSec ?? 0, IdleSec = du?.IdleSec ?? 0 },
                Hardware = hw is null ? null : Json.Deserialize<HardwareInfo>(hw.Json),
                HardwareCollectedAt = hw?.CollectedAt,
                Security = sec is null ? null : Json.Deserialize<SecurityPosture>(sec.Json),
                SecurityCollectedAt = sec?.CollectedAt,
                LiveChannel = presence.AgentConnection(id) is not null || (p?.Realtime ?? false),
            });
        });

        g.MapGet("/{id:guid}/perf", async (Guid id, int? hours, AccessScope scope, AppDb db) =>
        {
            if (!await scope.CanAccessDeviceAsync(id)) return Results.Forbid();
            var h = Math.Clamp(hours ?? 24, 1, 24 * 14);
            var from = DateTime.UtcNow.AddHours(-h);
            var points = await db.Perf.AsNoTracking().Where(x => x.DeviceId == id && x.Time >= from).OrderBy(x => x.Time)
                .Select(x => new { x.Time, x.Cpu, x.Ram, x.Disk }).ToListAsync();
            var bucket = TimeSpan.FromMinutes(h <= 6 ? 1 : h <= 24 ? 5 : 30);
            var series = points.GroupBy(x => new DateTime(x.Time.Ticks - x.Time.Ticks % bucket.Ticks, DateTimeKind.Utc))
                .Select(gr => new { Time = gr.Key, Cpu = Math.Round(gr.Average(x => x.Cpu), 1), Ram = Math.Round(gr.Average(x => x.Ram), 1), Disk = Math.Round(gr.Average(x => x.Disk), 1) });
            return Results.Ok(series);
        });

        g.MapGet("/{id:guid}/bandwidth", async (Guid id, int? days, AccessScope scope, AppDb db) =>
        {
            if (!await scope.CanAccessDeviceAsync(id)) return Results.Forbid();
            var n = Math.Clamp(days ?? 30, 1, 365);
            var today = IngestService.Today();
            var from = today.AddDays(-n + 1);
            var daily = await db.Daily.AsNoTracking().Where(x => x.DeviceId == id && x.Day >= from).ToDictionaryAsync(x => x.Day);
            var dayStart = IngestService.DayStartUtc(today);
            var rangeStart = IngestService.DayStartUtc(today.AddDays(-Math.Min(n, 7) + 1));
            var hourlyRaw = (await db.NetAdapter.AsNoTracking().Where(x => x.DeviceId == id && x.Time >= dayStart)
                .Select(x => new { x.Time, x.BytesIn, x.BytesOut }).ToListAsync())
                .Select(x => new { Hour = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(x.Time, DateTimeKind.Utc), IngestService.OrgTz).Hour, x.BytesIn, x.BytesOut })
                .ToList();
            var hourly = Enumerable.Range(0, 24).Select(hh => new
            {
                Hour = hh,
                BytesIn = hourlyRaw.Where(x => x.Hour == hh).Sum(x => x.BytesIn),
                BytesOut = hourlyRaw.Where(x => x.Hour == hh).Sum(x => x.BytesOut),
            });
            var adapters = await db.NetAdapter.AsNoTracking().Where(x => x.DeviceId == id && x.Time >= rangeStart)
                .GroupBy(x => new { x.Adapter, x.Type }).Select(gr => new { gr.Key.Adapter, gr.Key.Type, BytesIn = gr.Sum(x => x.BytesIn), BytesOut = gr.Sum(x => x.BytesOut) })
                .ToListAsync();
            var processes = await db.NetProcess.AsNoTracking().Where(x => x.DeviceId == id && x.Time >= rangeStart)
                .GroupBy(x => x.Process).Select(gr => new { Process = gr.Key, BytesIn = gr.Sum(x => x.BytesIn), BytesOut = gr.Sum(x => x.BytesOut) })
                .ToListAsync();
            return Results.Ok(new
            {
                Daily = Enumerable.Range(0, n).Select(i => from.AddDays(i)).Select(dd => new
                {
                    Day = dd.ToString("yyyy-MM-dd"), BytesIn = daily.GetValueOrDefault(dd)?.BytesIn ?? 0, BytesOut = daily.GetValueOrDefault(dd)?.BytesOut ?? 0
                }),
                Hourly = hourly,
                Adapters = adapters.OrderByDescending(a => a.BytesIn + a.BytesOut),
                Processes = processes.OrderByDescending(p => p.BytesIn + p.BytesOut).Take(25),
                RangeDays = Math.Min(n, 7),
            });
        });

        g.MapGet("/{id:guid}/apps", async (Guid id, int? days, AccessScope scope, AppDb db) =>
        {
            if (!await scope.CanAccessDeviceAsync(id)) return Results.Forbid();
            var n = Math.Clamp(days ?? 1, 1, 90);
            var today = IngestService.Today();
            var from = IngestService.DayStartUtc(today.AddDays(-n + 1));
            var rows = await db.AppUsage.AsNoTracking().Where(x => x.DeviceId == id && x.Time >= from)
                .Select(x => new { x.Time, x.App, x.Title, x.ActiveSec, x.IdleSec, x.User }).ToListAsync();
            var apps = rows.Where(r => r.App != "(idle)").GroupBy(r => r.App).Select(gr => new
            {
                App = gr.Key, ActiveSec = gr.Sum(x => x.ActiveSec),
                Titles = scope.CanSeeTitles
                    ? gr.Where(x => !string.IsNullOrEmpty(x.Title)).GroupBy(x => x.Title!).Select(t => new { Title = t.Key, Sec = t.Sum(x => x.ActiveSec) })
                        .OrderByDescending(t => t.Sec).Take(8).ToList()
                    : null
            }).OrderByDescending(x => x.ActiveSec).ToList();
            var timeline = Enumerable.Range(0, 24).Select(h => new
            {
                Hour = h,
                ActiveSec = rows.Where(r => TimeZoneInfo.ConvertTimeFromUtc(r.Time, IngestService.OrgTz).Hour == h).Sum(r => r.ActiveSec) / n,
                IdleSec = rows.Where(r => TimeZoneInfo.ConvertTimeFromUtc(r.Time, IngestService.OrgTz).Hour == h).Sum(r => r.IdleSec) / n,
            });
            var procs = await db.Processes.AsNoTracking().FirstOrDefaultAsync(x => x.DeviceId == id);
            return Results.Ok(new
            {
                Apps = apps,
                Timeline = timeline,
                TotalActiveSec = rows.Sum(r => r.ActiveSec),
                TotalIdleSec = rows.Sum(r => r.IdleSec),
                Users = rows.Where(r => r.User != null).Select(r => r.User).Distinct(),
                Processes = procs is null ? null : Json.Deserialize<List<ProcessInfo>>(procs.Json),
                ProcessesCollectedAt = procs?.CollectedAt,
            });
        });

        g.MapGet("/{id:guid}/web", async (Guid id, int? days, AccessScope scope, AppDb db) =>
        {
            if (!scope.CanSeeWeb) return Results.Forbid();
            if (!await scope.CanAccessDeviceAsync(id)) return Results.Forbid();
            var n = Math.Clamp(days ?? 7, 1, 90);
            var from = IngestService.DayStartUtc(IngestService.Today().AddDays(-n + 1));
            var rows = await db.Web.AsNoTracking().Where(x => x.DeviceId == id && x.Time >= from)
                .Select(x => new { x.Domain, x.Category, x.Count, x.Time, x.User, x.Process, x.BytesIn, x.BytesOut }).ToListAsync();
            var domains = rows.GroupBy(r => Domains.Registrable(r.Domain)).Select(gr => new
            {
                Domain = gr.Key, Category = gr.First().Category, Hits = gr.Sum(x => x.Count), LastVisit = gr.Max(x => x.Time),
                BytesIn = gr.Sum(x => x.BytesIn), BytesOut = gr.Sum(x => x.BytesOut),
                Hosts = gr.Select(x => x.Domain).Distinct().Take(6), Users = gr.Select(x => x.User).Where(u => u != null).Distinct(),
                Processes = gr.Select(x => x.Process).Where(p => p != null).Distinct().Take(4)
            }).OrderByDescending(x => x.BytesIn + x.BytesOut).ThenByDescending(x => x.Hits).ToList();
            var categories = rows.GroupBy(r => r.Category).Select(gr => new
            {
                Category = gr.Key, Hits = gr.Sum(x => x.Count), Bytes = gr.Sum(x => x.BytesIn + x.BytesOut)
            }).OrderByDescending(x => x.Hits);
            return Results.Ok(new
            {
                Domains = domains, Categories = categories,
                TotalBytesIn = rows.Sum(x => x.BytesIn), TotalBytesOut = rows.Sum(x => x.BytesOut)
            });
        });

        // live per-site throughput from the latest heartbeat (browsing data -> same permission as /web)
        g.MapGet("/{id:guid}/web/live", async (Guid id, AccessScope scope, PresenceStore presence) =>
        {
            if (!scope.CanSeeWeb) return Results.Forbid();
            if (!await scope.CanAccessDeviceAsync(id)) return Results.Forbid();
            var live = presence.LiveSites(id);
            var fresh = live is { } l && DateTime.UtcNow - l.At < TimeSpan.FromMinutes(3) && presence.IsOnline(id);
            var sites = fresh ? live!.Value.Sites : [];
            return Results.Ok(new
            {
                At = live?.At, Online = presence.IsOnline(id),
                Sites = sites.Select(s => new { s.Domain, Category = Domains.Categorize(s.Domain), s.InBps, s.OutBps })
            });
        });

        g.MapGet("/{id:guid}/software", async (Guid id, AccessScope scope, AppDb db) =>
        {
            if (!await scope.CanAccessDeviceAsync(id)) return Results.Forbid();
            var items = await db.Software.AsNoTracking().Where(x => x.DeviceId == id).OrderBy(x => x.Name).ToListAsync();
            return Results.Ok(items.Select(s => new { s.Id, s.Name, s.Version, s.Publisher, s.InstallDate, s.FirstSeen, s.RemovedAt }));
        });

        g.MapGet("/{id:guid}/events", async (Guid id, string? type, int? limit, AccessScope scope, AppDb db) =>
        {
            if (!await scope.CanAccessDeviceAsync(id)) return Results.Forbid();
            var q = db.Events.AsNoTracking().Where(x => x.DeviceId == id);
            if (!string.IsNullOrEmpty(type))
            {
                var prefixes = type.Split(',');
                q = q.Where(x => prefixes.Any(p => x.Type.StartsWith(p)));
            }
            var list = await q.OrderByDescending(x => x.Time).Take(Math.Clamp(limit ?? 200, 1, 1000)).ToListAsync();
            return Results.Ok(list.Select(e => new { e.Id, e.Time, e.Type, e.Severity, e.Message, Data = e.DataJson }));
        });

        g.MapGet("/{id:guid}/commands", async (Guid id, AccessScope scope, AppDb db) =>
        {
            if (!await scope.CanAccessDeviceAsync(id)) return Results.Forbid();
            return Results.Ok(await db.Commands.AsNoTracking().Where(c => c.DeviceId == id).OrderByDescending(c => c.CreatedAt).Take(30)
                .Select(c => new { c.Id, c.Type, c.Status, c.CreatedAt, c.CreatedBy, c.CompletedAt, c.Result }).ToListAsync());
        });

        write.MapPost("/{id:guid}/commands", async (Guid id, CommandRequest req, AccessScope scope, CommandService commands, AuditService audit) =>
        {
            if (!await scope.CanAccessDeviceAsync(id)) return Results.Forbid();
            if (!AllowedCommands.Contains(req.Type)) return Results.Problem("Unsupported command", statusCode: 400);
            if (req.Type == CommandTypes.Uninstall && !scope.IsSuper) return Results.Problem("Only Super Admins can uninstall agents", statusCode: 403);
            var cmd = await commands.QueueAsync(id, req.Type, req.Payload, scope.Username);
            await audit.LogAsync($"device.command.{req.Type}", id.ToString(), req.Payload);
            return Results.Ok(new { cmd.Id, cmd.Status });
        });

        write.MapPost("/bulk-command", async (BulkCommandRequest req, AccessScope scope, CommandService commands, AuditService audit) =>
        {
            if (!AllowedCommands.Contains(req.Type) || req.Type == CommandTypes.Uninstall) return Results.Problem("Unsupported bulk command", statusCode: 400);
            var n = 0;
            foreach (var id in req.DeviceIds.Distinct().Take(5000))
                if (await scope.CanAccessDeviceAsync(id)) { await commands.QueueAsync(id, req.Type, req.Payload, scope.Username); n++; }
            await audit.LogAsync($"device.bulkCommand.{req.Type}", $"{n} devices", req.Payload);
            return Results.Ok(new { queued = n });
        });

        write.MapPost("/{id:guid}/uninstall-code", async (Guid id, AccessScope scope, AppDb db, CommandService commands, AuditService audit) =>
        {
            if (!await scope.CanAccessDeviceAsync(id)) return Results.Forbid();
            var device = await db.Devices.FirstAsync(d => d.Id == id);
            var code = string.Join("-", Enumerable.Range(0, 3).Select(_ => RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZ23456789", 4)));
            var hash = KeyHash.Hash(code);
            device.UninstallCodeHash = hash;
            await db.SaveChangesAsync();
            var expires = DateTime.UtcNow.AddHours(24);
            await commands.QueueAsync(id, CommandTypes.SetUninstallCode, Json.Serialize(new { hash, expiresUtc = expires }), scope.Username);
            await audit.LogAsync("device.uninstallCode", device.Hostname);
            return Results.Ok(new { code, expiresUtc = expires });
        });

        write.MapPut("/{id:guid}", async (Guid id, UpdateDeviceRequest req, AccessScope scope, AppDb db, AuditService audit, DeviceCache cache, CommandService commands) =>
        {
            if (!await scope.CanAccessDeviceAsync(id)) return Results.Forbid();
            var units = await scope.UnitsAsync();
            if (req.OrgUnitId is { } target && units is not null && !units.Contains(target)) return Results.Forbid();
            var d = await db.Devices.FirstAsync(x => x.Id == id);
            var moved = d.OrgUnitId != req.OrgUnitId;
            d.OrgUnitId = req.OrgUnitId;
            d.Tags = req.Tags;
            await db.SaveChangesAsync();
            cache.Invalidate(id);
            if (moved) await commands.BroadcastPolicyChangedAsync([id]);
            await audit.LogAsync("device.update", d.Hostname, $"orgUnit={req.OrgUnitId} tags={req.Tags}");
            return Results.Ok();
        });

        foreach (var (action, status) in new[] { ("approve", DeviceStatus.Active), ("enable", DeviceStatus.Active), ("disable", DeviceStatus.Disabled), ("retire", DeviceStatus.Retired) })
        {
            write.MapPost($"/{{id:guid}}/{action}", async (Guid id, AccessScope scope, AppDb db, AuditService audit, DeviceCache cache, PresenceStore presence) =>
            {
                if (!await scope.CanAccessDeviceAsync(id)) return Results.Forbid();
                var d = await db.Devices.FirstAsync(x => x.Id == id);
                d.Status = status;
                await db.SaveChangesAsync();
                cache.Invalidate(id);
                if (status != DeviceStatus.Active) presence.Remove(id);
                await audit.LogAsync($"device.{action}", d.Hostname);
                return Results.Ok();
            });
        }

        app.MapDelete("/api/devices/simulated", async (AppDb db, AuditService audit, PresenceStore presence) =>
        {
            var ids = await db.Devices.Where(d => d.IsSimulated).Select(d => d.Id).ToListAsync();
            foreach (var id in ids) presence.Remove(id);
            await DemoSimulator.PurgeAsync(db);
            await audit.LogAsync("demo.purge", $"{ids.Count} devices");
            return Results.Ok(new { removed = ids.Count });
        }).RequireAuthorization("Super");
    }

    public static HashSet<Guid> Subtree(Guid root, IEnumerable<(Guid Id, Guid? ParentId)> all)
    {
        var list = all.ToList();
        var set = new HashSet<Guid> { root };
        bool added;
        do
        {
            added = false;
            foreach (var (id, parent) in list)
                if (parent is { } p && set.Contains(p) && set.Add(id)) added = true;
        } while (added);
        return set;
    }
}
