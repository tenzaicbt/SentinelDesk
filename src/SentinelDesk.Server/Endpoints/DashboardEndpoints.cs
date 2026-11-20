using Microsoft.EntityFrameworkCore;
using SentinelDesk.Server.Data;
using SentinelDesk.Server.Security;
using SentinelDesk.Server.Services;
using SentinelDesk.Shared;

namespace SentinelDesk.Server.Endpoints;

public static class DashboardEndpoints
{
    public static void MapDashboardApi(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization("Console");

        g.MapGet("/dashboard", async (AppDb db, AccessScope scope, PresenceStore presence) =>
        {
            var devices = await (await scope.DevicesAsync()).Where(d => d.Status != DeviceStatus.Retired)
                .Select(d => new { d.Id, d.OrgUnitId, d.Os, d.Status, d.AgentVersion, d.LastSeenAt }).ToListAsync();
            var ids = devices.Select(d => d.Id).ToHashSet();
            var restricted = await scope.UnitsAsync() is not null;
            var today = IngestService.Today();
            var dayStart = IngestService.DayStartUtc(today);
            var weekAgo = today.AddDays(-13);

            var online = devices.Count(d => presence.IsOnline(d.Id));
            var daily = await db.Daily.AsNoTracking().Where(x => x.Day >= weekAgo).ToListAsync();
            if (restricted) daily = daily.Where(x => ids.Contains(x.DeviceId)).ToList();
            var usage = Enumerable.Range(0, 14).Select(i => today.AddDays(-13 + i)).Select(day => new
            {
                Day = day.ToString("yyyy-MM-dd"),
                BytesIn = daily.Where(x => x.Day == day).Sum(x => x.BytesIn),
                BytesOut = daily.Where(x => x.Day == day).Sum(x => x.BytesOut),
                ActiveHours = Math.Round(daily.Where(x => x.Day == day).Sum(x => (double)x.ActiveSec) / 3600, 1),
            }).ToList();

            var appsQ = db.AppUsage.AsNoTracking().Where(a => a.Time >= dayStart && a.App != "(idle)");
            var webQ = db.Web.AsNoTracking().Where(w => w.Time >= dayStart && w.Category != "System");
            if (restricted) { appsQ = appsQ.Where(a => ids.Contains(a.DeviceId)); webQ = webQ.Where(w => ids.Contains(w.DeviceId)); }

            var topApps = await appsQ.GroupBy(a => a.App)
                .Select(x => new { App = x.Key, Seconds = x.Sum(a => a.ActiveSec), Devices = x.Select(a => a.DeviceId).Distinct().Count() })
                .OrderByDescending(x => x.Seconds).Take(8).ToListAsync();

            object? topSites = null, categories = null;
            if (scope.CanSeeWeb)
            {
                var web = await webQ.GroupBy(w => new { w.Domain, w.Category })
                    .Select(x => new { x.Key.Domain, x.Key.Category, Hits = x.Sum(w => w.Count) }).ToListAsync();
                topSites = web.GroupBy(w => Domains.Registrable(w.Domain))
                    .Select(x => new { Domain = x.Key, Category = x.First().Category, Hits = x.Sum(w => w.Hits) })
                    .OrderByDescending(x => x.Hits).Take(8).ToList();
                categories = web.GroupBy(w => w.Category).Select(x => new { Category = x.Key, Hits = x.Sum(w => w.Hits) })
                    .OrderByDescending(x => x.Hits).ToList();
            }

            var units = await db.OrgUnits.AsNoTracking().ToListAsync();
            var branches = units.Where(u => u.Lat is not null).Select(u => new
            {
                u.Id, u.Name, u.Lat, u.Lng,
                Total = devices.Count(d => d.OrgUnitId == u.Id),
                Online = devices.Count(d => d.OrgUnitId == u.Id && presence.IsOnline(d.Id)),
                TodayBytes = daily.Where(x => x.Day == today && devices.Any(d => d.Id == x.DeviceId && d.OrgUnitId == u.Id)).Sum(x => x.BytesIn + x.BytesOut)
            }).Where(b => b.Total > 0).OrderByDescending(b => b.Total).ToList();

            var alertsQ = db.Alerts.AsNoTracking().Where(a => a.Status == "open");
            if (restricted) alertsQ = alertsQ.Where(a => ids.Contains(a.DeviceId));
            var openAlerts = await alertsQ.GroupBy(a => a.Severity).Select(x => new { Severity = x.Key, Count = x.Count() }).ToListAsync();
            var hostnames = await (await scope.DevicesAsync()).Select(d => new { d.Id, d.Hostname }).ToDictionaryAsync(d => d.Id, d => d.Hostname);
            var recentAlerts = (await alertsQ.OrderByDescending(a => a.TriggeredAt).Take(6).ToListAsync())
                .Select(a => new { a.Id, a.Title, a.Severity, a.TriggeredAt, a.DeviceId, Hostname = hostnames.GetValueOrDefault(a.DeviceId) });

            var todayUsage = daily.Where(x => x.Day == today).ToList();
            var yesterday = daily.Where(x => x.Day == today.AddDays(-1)).ToList();
            var topConsumers = todayUsage.OrderByDescending(x => x.BytesIn + x.BytesOut).Take(6)
                .Select(x => new { x.DeviceId, Hostname = hostnames.GetValueOrDefault(x.DeviceId), Bytes = x.BytesIn + x.BytesOut });

            return Results.Ok(new
            {
                Kpis = new
                {
                    Total = devices.Count,
                    Online = online,
                    Pending = devices.Count(d => d.Status == DeviceStatus.Pending),
                    OpenAlerts = openAlerts.Sum(a => a.Count),
                    CriticalAlerts = openAlerts.Where(a => a.Severity == "critical").Sum(a => a.Count),
                    TodayBytesIn = todayUsage.Sum(x => x.BytesIn),
                    TodayBytesOut = todayUsage.Sum(x => x.BytesOut),
                    YesterdayBytes = yesterday.Sum(x => x.BytesIn + x.BytesOut),
                    ActiveHoursToday = Math.Round(todayUsage.Sum(x => (double)x.ActiveSec) / 3600, 1),
                },
                Usage = usage,
                TopApps = topApps,
                TopSites = topSites,
                Categories = categories,
                Branches = branches,
                Os = devices.GroupBy(d => d.Os).Select(x => new { Name = string.IsNullOrEmpty(x.Key) ? "Unknown" : x.Key, Count = x.Count() }),
                Versions = devices.GroupBy(d => d.AgentVersion).Select(x => new { Name = x.Key, Count = x.Count() }).OrderByDescending(x => x.Name),
                RecentAlerts = recentAlerts,
                TopConsumers = topConsumers,
            });
        });

        g.MapGet("/live", async (AccessScope scope, PresenceStore presence, AppDb db) =>
        {
            var units = await scope.UnitsAsync();
            var names = await db.OrgUnits.AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.Name);
            return Results.Ok(presence.All
                .Where(p => DateTime.UtcNow - p.LastSeen < TimeSpan.FromSeconds(150))
                .Where(p => units is null || (p.OrgUnitId is { } u && units.Contains(u)))
                .Select(p => Sanitize(p, scope) with { })
                .Select(p => new { presence = p, branch = p.OrgUnitId is { } u ? names.GetValueOrDefault(u) : null })
                .OrderBy(x => x.presence.Hostname));
        });

        g.MapGet("/search", async (string q, AccessScope scope) =>
        {
            q = q.Trim();
            if (q.Length < 2) return Results.Ok(Array.Empty<object>());
            var res = await (await scope.DevicesAsync())
                .Where(d => d.Hostname.Contains(q) || (d.LoggedInUser != null && d.LoggedInUser.Contains(q)) || (d.Serial != null && d.Serial.Contains(q)) || (d.LastIp != null && d.LastIp.Contains(q)))
                .OrderBy(d => d.Hostname).Take(10)
                .Select(d => new { d.Id, d.Hostname, d.LoggedInUser, d.LastIp, d.Status }).ToListAsync();
            return Results.Ok(res);
        });
    }

    public static LivePresence Sanitize(LivePresence p, AccessScope scope) =>
        scope.CanSeeTitles ? p : p with { CurrentTitle = null };
}
