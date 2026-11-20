using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SentinelDesk.Server.Data;
using SentinelDesk.Server.Security;
using SentinelDesk.Server.Services;
using SentinelDesk.Shared;

namespace SentinelDesk.Server.Endpoints;

public static class ReportEndpoints
{
    public static void MapReportApi(this WebApplication app)
    {
        var g = app.MapGroup("/api/reports").RequireAuthorization("Console");

        g.MapGet("/usage", async (DateOnly? from, DateOnly? to, Guid? orgUnitId, string? format, AppDb db, AccessScope scope, AuditService audit) =>
        {
            var (f, t) = Range(from, to);
            var devices = await ScopedDevices(db, scope, orgUnitId);
            var ids = devices.Keys.ToList();
            var daily = await db.Daily.AsNoTracking().Where(x => x.Day >= f && x.Day <= t).ToListAsync();
            daily = daily.Where(x => devices.ContainsKey(x.DeviceId)).ToList();
            var rows = daily.GroupBy(x => x.DeviceId).Select(gr => new UsageRow(
                devices[gr.Key].Hostname, devices[gr.Key].Branch, devices[gr.Key].User,
                Mb(gr.Sum(x => x.BytesIn)), Mb(gr.Sum(x => x.BytesOut)), Mb(gr.Sum(x => x.BytesIn + x.BytesOut)),
                Math.Round(gr.Sum(x => x.ActiveSec) / 3600.0, 1), Math.Round(gr.Sum(x => x.IdleSec) / 3600.0, 1), gr.Key))
                .OrderByDescending(r => r.TotalMb).ToList();
            await audit.LogAsync("report.usage", $"{f}..{t}", format);
            if (format == "csv") return Csv(rows, $"data-usage_{f:yyyyMMdd}-{t:yyyyMMdd}.csv");
            var series = Enumerable.Range(0, t.DayNumber - f.DayNumber + 1).Select(i => f.AddDays(i)).Select(d => new
            {
                Day = d.ToString("yyyy-MM-dd"),
                BytesIn = daily.Where(x => x.Day == d).Sum(x => x.BytesIn),
                BytesOut = daily.Where(x => x.Day == d).Sum(x => x.BytesOut),
            });
            var byBranch = rows.GroupBy(r => r.Branch ?? "Unassigned").Select(gr => new { Branch = gr.Key, TotalMb = Math.Round(gr.Sum(r => r.TotalMb), 1), Devices = gr.Count() })
                .OrderByDescending(x => x.TotalMb);
            return Results.Ok(new { rows, series, byBranch });
        });

        g.MapGet("/apps", async (DateOnly? from, DateOnly? to, Guid? orgUnitId, string? format, AppDb db, AccessScope scope, AuditService audit) =>
        {
            var (f, t) = Range(from, to);
            var devices = await ScopedDevices(db, scope, orgUnitId);
            var start = IngestService.DayStartUtc(f);
            var end = IngestService.DayStartUtc(t.AddDays(1));
            var raw = await db.AppUsage.AsNoTracking().Where(x => x.Time >= start && x.Time < end && x.App != "(idle)")
                .GroupBy(x => new { x.App, x.DeviceId }).Select(gr => new { gr.Key.App, gr.Key.DeviceId, Sec = gr.Sum(x => x.ActiveSec) }).ToListAsync();
            var rows = raw.Where(r => devices.ContainsKey(r.DeviceId)).GroupBy(r => r.App)
                .Select(gr => new AppRow(gr.Key, Math.Round(gr.Sum(x => x.Sec) / 3600.0, 1), gr.Count(), Math.Round(gr.Sum(x => x.Sec) / 3600.0 / Math.Max(1, gr.Count()), 2)))
                .OrderByDescending(r => r.Hours).ToList();
            await audit.LogAsync("report.apps", $"{f}..{t}", format);
            return format == "csv" ? Csv(rows, $"app-usage_{f:yyyyMMdd}-{t:yyyyMMdd}.csv") : Results.Ok(new { rows });
        });

        g.MapGet("/web", async (DateOnly? from, DateOnly? to, Guid? orgUnitId, string? format, AppDb db, AccessScope scope, AuditService audit) =>
        {
            if (!scope.CanSeeWeb) return Results.Forbid();
            var (f, t) = Range(from, to);
            var devices = await ScopedDevices(db, scope, orgUnitId);
            var start = IngestService.DayStartUtc(f);
            var end = IngestService.DayStartUtc(t.AddDays(1));
            var raw = await db.Web.AsNoTracking().Where(x => x.Time >= start && x.Time < end && x.Category != "System")
                .GroupBy(x => new { x.Domain, x.Category, x.DeviceId }).Select(gr => new { gr.Key.Domain, gr.Key.Category, gr.Key.DeviceId, Hits = gr.Sum(x => x.Count) }).ToListAsync();
            var filtered = raw.Where(r => devices.ContainsKey(r.DeviceId)).ToList();
            var rows = filtered.GroupBy(r => Domains.Registrable(r.Domain))
                .Select(gr => new WebRow(gr.Key, gr.First().Category, gr.Sum(x => x.Hits), gr.Select(x => x.DeviceId).Distinct().Count()))
                .OrderByDescending(r => r.Hits).Take(500).ToList();
            var categories = filtered.GroupBy(r => r.Category).Select(gr => new { Category = gr.Key, Hits = gr.Sum(x => x.Hits) }).OrderByDescending(x => x.Hits);
            await audit.LogAsync("report.web", $"{f}..{t}", format);
            return format == "csv" ? Csv(rows, $"web-usage_{f:yyyyMMdd}-{t:yyyyMMdd}.csv") : Results.Ok(new { rows, categories });
        });

        g.MapGet("/inventory", async (Guid? orgUnitId, string? format, AppDb db, AccessScope scope, AuditService audit) =>
        {
            var devices = await ScopedDevices(db, scope, orgUnitId);
            var hw = await db.Hardware.AsNoTracking().ToDictionaryAsync(h => h.DeviceId, h => Json.Deserialize<HardwareInfo>(h.Json));
            var sec = await db.Security.AsNoTracking().ToDictionaryAsync(h => h.DeviceId, h => Json.Deserialize<SecurityPosture>(h.Json));
            var rows = devices.Values.OrderBy(d => d.Hostname).Select(d =>
            {
                var h = hw.GetValueOrDefault(d.Id);
                var s = sec.GetValueOrDefault(d.Id);
                return new InventoryRow(d.Hostname, d.Branch, d.User, h?.Manufacturer, h?.Model, h?.Serial, h?.Os ?? d.Os, h?.OsBuild, h?.Cpu,
                    h is null ? null : (int)Math.Round(h.RamMb / 1024.0), h?.Disks.Sum(x => x.SizeGb), d.AgentVersion,
                    s?.Antivirus, s?.AntivirusEnabled, s?.FirewallEnabled, s?.BitLocker, s?.LastUpdateInstalled?.ToString("yyyy-MM-dd"), d.LastSeen);
            }).ToList();
            await audit.LogAsync("report.inventory", null, format);
            return format == "csv" ? Csv(rows, "hardware-inventory.csv") : Results.Ok(new { rows });
        });

        g.MapGet("/software", async (Guid? orgUnitId, string? format, AppDb db, AccessScope scope, AuditService audit) =>
        {
            var devices = await ScopedDevices(db, scope, orgUnitId);
            var raw = await db.Software.AsNoTracking().Where(s => s.RemovedAt == null)
                .Select(s => new { s.Name, s.Version, s.Publisher, s.DeviceId }).ToListAsync();
            var rows = raw.Where(r => devices.ContainsKey(r.DeviceId)).GroupBy(r => r.Name)
                .Select(gr => new SoftwareRow(gr.Key, gr.First().Publisher, gr.Select(x => x.DeviceId).Distinct().Count(),
                    string.Join(", ", gr.Select(x => x.Version).Distinct().Take(5))))
                .OrderByDescending(r => r.Installs).ToList();
            await audit.LogAsync("report.software", null, format);
            return format == "csv" ? Csv(rows, "software-inventory.csv") : Results.Ok(new { rows });
        });
    }

    public sealed record UsageRow(string Hostname, string? Branch, string? User, double InMb, double OutMb, double TotalMb, double ActiveHours, double IdleHours, Guid DeviceId);
    public sealed record AppRow(string App, double Hours, int Devices, double AvgHoursPerDevice);
    public sealed record WebRow(string Domain, string Category, int Hits, int Devices);
    public sealed record InventoryRow(string Hostname, string? Branch, string? User, string? Manufacturer, string? Model, string? Serial, string? Os,
        string? OsBuild, string? Cpu, int? RamGb, long? DiskGb, string AgentVersion, string? Antivirus, bool? AvEnabled, bool? Firewall,
        string? BitLocker, string? LastPatch, DateTime? LastSeen);
    public sealed record SoftwareRow(string Name, string? Publisher, int Installs, string Versions);
    private sealed record DeviceRef(Guid Id, string Hostname, string? Branch, string? User, string Os, string AgentVersion, DateTime? LastSeen);

    private static (DateOnly From, DateOnly To) Range(DateOnly? from, DateOnly? to)
    {
        var t = to ?? IngestService.Today();
        var f = from ?? t.AddDays(-6);
        if (f > t) (f, t) = (t, f);
        if (t.DayNumber - f.DayNumber > 366) f = t.AddDays(-366);
        return (f, t);
    }

    private static async Task<Dictionary<Guid, DeviceRef>> ScopedDevices(AppDb db, AccessScope scope, Guid? orgUnitId)
    {
        var q = (await scope.DevicesAsync()).Where(d => d.Status != DeviceStatus.Retired);
        if (orgUnitId is { } ou)
        {
            var all = await db.OrgUnits.AsNoTracking().Select(o => new { o.Id, o.ParentId }).ToListAsync();
            var subtree = DeviceEndpoints.Subtree(ou, all.Select(o => (o.Id, o.ParentId)));
            q = q.Where(d => d.OrgUnitId != null && subtree.Contains(d.OrgUnitId.Value));
        }
        var units = await db.OrgUnits.AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.Name);
        return (await q.ToListAsync()).ToDictionary(d => d.Id, d => new DeviceRef(d.Id, d.Hostname,
            d.OrgUnitId is { } u ? units.GetValueOrDefault(u) : null, d.LoggedInUser, d.Os, d.AgentVersion, d.LastSeenAt));
    }

    private static double Mb(long bytes) => Math.Round(bytes / 1048576.0, 1);

    private static IResult Csv<T>(IEnumerable<T> rows, string fileName)
    {
        var props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.Name != "DeviceId").ToArray();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", props.Select(p => p.Name)));
        foreach (var r in rows)
            sb.AppendLine(string.Join(",", props.Select(p => Escape(p.GetValue(r)))));
        return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", fileName);

        static string Escape(object? v)
        {
            var s = v switch
            {
                null => "",
                DateTime dt => dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => v.ToString() ?? ""
            };
            // neutralise spreadsheet formula injection
            if (s.Length > 0 && "=+-@".Contains(s[0]) && v is string) s = "'" + s;
            return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        }
    }
}
