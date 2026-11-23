using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SentinelDesk.Server.Data;
using SentinelDesk.Server.Hubs;
using SentinelDesk.Server.Security;
using SentinelDesk.Shared;

namespace SentinelDesk.Server.Services;

/// <summary>Evaluates alert rules every minute.</summary>
public sealed class AlertEngine(IServiceScopeFactory scopes, PresenceStore presence, ILogger<AlertEngine> log) : BackgroundService
{
    private DateTime _lastRun = DateTime.UtcNow.AddMinutes(-5);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), ct);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        do
        {
            try { await RunAsync(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogError(ex, "Alert evaluation failed"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var hub = scope.ServiceProvider.GetRequiredService<IHubContext<ConsoleHub>>();
        var notifier = scope.ServiceProvider.GetRequiredService<Notifier>();
        var since = _lastRun;
        var now = DateTime.UtcNow;
        _lastRun = now;

        var rules = await db.AlertRules.AsNoTracking().Where(r => r.Enabled).ToListAsync(ct);
        var devices = await db.Devices.AsNoTracking().Where(d => d.Status == DeviceStatus.Active).ToDictionaryAsync(d => d.Id, ct);
        var today = IngestService.Today();
        var created = new List<(Alert Alert, AlertRule Rule)>();

        async Task Raise(AlertRule rule, Guid deviceId, string dedup, string title, string? details)
        {
            var key = $"{rule.Id:N}:{deviceId:N}:{dedup}";
            if (await db.Alerts.AnyAsync(a => a.DedupKey == key, ct)) return;
            var alert = new Alert
            {
                RuleId = rule.Id, DeviceId = deviceId, Severity = rule.Severity, Title = title, Details = details, DedupKey = key
            };
            db.Alerts.Add(alert);
            created.Add((alert, rule));
        }

        foreach (var rule in rules)
        {
            switch (rule.Type)
            {
                case "dataUsageDaily":
                {
                    var limit = (long)(rule.Threshold * 1024 * 1024);
                    var hits = await db.Daily.AsNoTracking().Where(d => d.Day == today && d.BytesIn + d.BytesOut > limit).ToListAsync(ct);
                    foreach (var h in hits.Where(h => devices.ContainsKey(h.DeviceId)))
                        await Raise(rule, h.DeviceId, today.ToString("yyyyMMdd"), $"Daily data usage over {Fmt(limit)}",
                            $"Today: {Fmt(h.BytesIn + h.BytesOut)} (↓ {Fmt(h.BytesIn)} / ↑ {Fmt(h.BytesOut)})");
                    break;
                }
                case "agentOffline":
                {
                    var cutoff = now.AddMinutes(-Math.Max(1, rule.Threshold));
                    foreach (var d in devices.Values.Where(d => d.LastSeenAt < cutoff && !presence.IsOnline(d.Id, TimeSpan.FromMinutes(rule.Threshold))))
                        await Raise(rule, d.Id, $"off{d.LastSeenAt!.Value.Ticks}", $"Agent offline > {rule.Threshold:0} min",
                            $"Last seen {d.LastSeenAt:yyyy-MM-dd HH:mm} UTC from {d.LastIp}");
                    // auto-resolve offline alerts when device returns
                    var open = await db.Alerts.Where(a => a.RuleId == rule.Id && a.Status != "resolved").ToListAsync(ct);
                    foreach (var a in open.Where(a => presence.IsOnline(a.DeviceId)))
                    { a.Status = "resolved"; a.HandledBy = "system"; a.HandledAt = now; }
                    break;
                }
                case "agentSilent":
                {
                    // An active device that has not reported for hours, without being uninstalled from the console, may have
                    // had the agent removed or blocked (e.g. firewall, re-imaged PC). Threshold in hours.
                    var hours = Math.Max(1, rule.Threshold);
                    var cutoff = now.AddHours(-hours);
                    foreach (var d in devices.Values.Where(d => d.LastSeenAt < cutoff && !presence.IsOnline(d.Id)))
                        await Raise(rule, d.Id, $"silent{d.LastSeenAt!.Value.Ticks}", $"Agent silent for over {hours:0} h - possible removal",
                            $"No contact since {d.LastSeenAt:yyyy-MM-dd HH:mm} UTC (last IP {d.LastIp}). If the PC was retired, mark it retired in Devices.");
                    var open = await db.Alerts.Where(a => a.RuleId == rule.Id && a.Status != "resolved").ToListAsync(ct);
                    foreach (var a in open.Where(a => presence.IsOnline(a.DeviceId)))
                    { a.Status = "resolved"; a.HandledBy = "system"; a.HandledAt = now; }
                    break;
                }
                case "cpuHigh":
                {
                    var from = now.AddMinutes(-10);
                    var hot = await db.Perf.AsNoTracking().Where(p => p.Time >= from)
                        .GroupBy(p => p.DeviceId).Select(g => new { g.Key, Avg = g.Average(x => x.Cpu), N = g.Count() })
                        .Where(x => x.N >= 3 && x.Avg > rule.Threshold).ToListAsync(ct);
                    foreach (var h in hot.Where(h => devices.ContainsKey(h.Key)))
                        await Raise(rule, h.Key, now.ToString("yyyyMMddHH"), $"Sustained CPU above {rule.Threshold:0}%", $"10-min average {h.Avg:0.0}%");
                    break;
                }
                case "diskLow":
                {
                    foreach (var p in presence.All.Where(p => p.Disk > rule.Threshold && devices.ContainsKey(p.DeviceId)))
                        await Raise(rule, p.DeviceId, today.ToString("yyyyMMdd"), $"Disk usage above {rule.Threshold:0}%", $"System drive {p.Disk:0}% used");
                    break;
                }
                case "software":
                {
                    var names = Split(rule.Parameter);
                    if (names.Length == 0) break;
                    var sw = await db.Software.AsNoTracking().Where(s => s.RemovedAt == null && s.FirstSeen >= since.AddMinutes(-1)).ToListAsync(ct);
                    foreach (var s in sw)
                        if (names.FirstOrDefault(n => s.Name.Contains(n, StringComparison.OrdinalIgnoreCase)) is { } match && devices.ContainsKey(s.DeviceId))
                            await Raise(rule, s.DeviceId, $"sw:{s.Name}", $"Unapproved software: {s.Name}", $"{s.Name} {s.Version} ({s.Publisher}) matched '{match}'");
                    break;
                }
                case "blocked":
                {
                    var evs = await db.Events.AsNoTracking().Where(e => e.Time >= since.AddSeconds(-90) && e.Type.StartsWith("block.") && e.Type != "block.policyApplied").ToListAsync(ct);
                    foreach (var e in evs.Where(e => devices.ContainsKey(e.DeviceId)))
                        await Raise(rule, e.DeviceId, $"ev{e.Id}", e.Type == "block.app" ? "Blocked application" : "Blocked website", e.Message);
                    break;
                }
                case "tamper":
                case "usb":
                {
                    var prefix = rule.Type == "tamper" ? "tamper." : "usb.";
                    var evs = await db.Events.AsNoTracking().Where(e => e.Time >= since.AddSeconds(-90) && e.Type.StartsWith(prefix)).ToListAsync(ct);
                    foreach (var e in evs.Where(e => devices.ContainsKey(e.DeviceId)))
                        await Raise(rule, e.DeviceId, $"ev{e.Id}", rule.Type == "tamper" ? $"Tamper attempt: {e.Type[7..]}" : "USB storage connected", e.Message);
                    break;
                }
                case "category":
                {
                    var cats = Split(rule.Parameter);
                    if (cats.Length == 0) break;
                    var hits = await db.Web.AsNoTracking().Where(w => w.Time >= since.AddSeconds(-90) && cats.Contains(w.Category))
                        .Select(w => new { w.DeviceId, w.Category, w.Domain, w.User }).ToListAsync(ct);
                    foreach (var h in hits.Where(h => devices.ContainsKey(h.DeviceId)))
                        await Raise(rule, h.DeviceId, $"{h.Category}:{today:yyyyMMdd}", $"{h.Category} site visited", $"{h.Domain} by {h.User ?? "unknown user"}");
                    break;
                }
            }
        }

        await db.SaveChangesAsync(ct);
        foreach (var (alert, rule) in created)
        {
            var host = devices.TryGetValue(alert.DeviceId, out var d) ? d.Hostname : alert.DeviceId.ToString();
            await hub.Clients.Group("console").SendAsync("alert", new { alert.Id, alert.Title, alert.Severity, alert.DeviceId, Hostname = host, alert.TriggeredAt }, ct);
            await notifier.SendAsync(alert, rule, host);
        }
        if (created.Count > 0) log.LogInformation("Raised {Count} alerts", created.Count);
    }

    private static string[] Split(string? s) => (s ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string Fmt(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{bytes / 1024.0:0} KB"
    };
}

/// <summary>Data retention (PDPA data minimisation): raw telemetry vs. daily aggregates.</summary>
public sealed class RetentionService(IServiceScopeFactory scopes, IConfiguration config, ILogger<RetentionService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), ct);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                var raw = DateTime.UtcNow.AddDays(-config.GetValue("Retention:RawDays", 90));
                var agg = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-config.GetValue("Retention:AggregateDays", 730)));
                var n = await db.Perf.Where(x => x.Time < raw).ExecuteDeleteAsync(ct)
                        + await db.NetAdapter.Where(x => x.Time < raw).ExecuteDeleteAsync(ct)
                        + await db.NetProcess.Where(x => x.Time < raw).ExecuteDeleteAsync(ct)
                        + await db.AppUsage.Where(x => x.Time < raw).ExecuteDeleteAsync(ct)
                        + await db.Web.Where(x => x.Time < raw).ExecuteDeleteAsync(ct)
                        + await db.Events.Where(x => x.Time < raw).ExecuteDeleteAsync(ct)
                        + await db.Daily.Where(x => x.Day < agg).ExecuteDeleteAsync(ct)
                        + await db.Commands.Where(x => x.CreatedAt < raw).ExecuteDeleteAsync(ct);
                var loc = DateTime.UtcNow.AddDays(-config.GetValue("Retention:LocationDays", 30));
                n += await db.Locations.Where(x => x.Time < loc).ExecuteDeleteAsync(ct);
                await db.Commands.Where(c => c.Status == "pending" && c.ExpiresAt < DateTime.UtcNow)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, "expired"), ct);
                if (n > 0) log.LogInformation("Retention removed {Count} rows", n);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogError(ex, "Retention job failed"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}

public static class Seeder
{
    /// <summary>EnsureCreated never alters an existing database - add columns introduced after the first release here.</summary>
    private static async Task<HashSet<string>> UpgradeSchemaAsync(AppDb db, ILogger log)
    {
        (string Table, string Column, string Ddl)[] columns =
        [
            ("Web", "BytesIn", "INTEGER NOT NULL DEFAULT 0"),
            ("Web", "BytesOut", "INTEGER NOT NULL DEFAULT 0"),
            ("EnrollmentTokens", "TokenProtected", "TEXT NULL"),
            ("Devices", "EnrollmentTokenId", "TEXT NULL"),
            ("Devices", "LocLat", "REAL NULL"),
            ("Devices", "LocLng", "REAL NULL"),
            ("Devices", "LocAccuracyM", "REAL NULL"),
            ("Devices", "LocAt", "TEXT NULL"),
            ("Devices", "LocSource", "TEXT NULL"),
            ("Devices", "LocError", "TEXT NULL"),
            ("Policies", "Kind", "TEXT NOT NULL DEFAULT 'monitoring'"),
        ];
        var added = new HashSet<string>();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        // tables introduced after the first release
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "Locations" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Locations" PRIMARY KEY AUTOINCREMENT,
                "DeviceId" TEXT NOT NULL, "Time" TEXT NOT NULL, "Lat" REAL NOT NULL, "Lng" REAL NOT NULL,
                "AccuracyM" REAL NOT NULL, "Source" TEXT NULL);
            CREATE INDEX IF NOT EXISTS "IX_Locations_DeviceId_Time" ON "Locations" ("DeviceId", "Time");
            """);
        foreach (var (table, column, ddl) in columns)
        {
            await using var check = conn.CreateCommand();
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
            if (Convert.ToInt64(await check.ExecuteScalarAsync()) > 0) continue;
            await db.Database.ExecuteSqlRawAsync($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {ddl}");
            log.LogInformation("Database upgraded: added {Table}.{Column}", table, column);
            added.Add($"{table}.{column}");
        }
        return added;
    }

    /// <summary>
    /// Blocking used to be part of monitoring policies. Moves it into block policies (websites / apps / usb) with the same
    /// assignments; blocking on the Default policy goes to the whole organization. Safe to run on every start.
    /// </summary>
    private static async Task MigrateLegacyBlockingAsync(AppDb db, ILogger log)
    {
        var root = await db.OrgUnits.AsNoTracking().Where(o => o.ParentId == null).Select(o => (Guid?)o.Id).FirstOrDefaultAsync();
        var sources = await db.Policies.Where(p => p.Kind == PolicyKinds.Monitoring).ToListAsync();
        var moved = 0;
        foreach (var src in sources)
        {
            var model = Json.Deserialize<AgentPolicy>(src.Json);
            if (model is null) continue;
            var b = model.Block;
            var parts = new List<(string Kind, string Label, AgentPolicy Settings)>();
            if (b.Urls.Count > 0 || b.Categories.Count > 0)
                parts.Add((PolicyKinds.Websites, "websites", new AgentPolicy { Block = new BlockSettings { Urls = b.Urls, Categories = b.Categories } }));
            if (b.Apps.Count > 0)
                parts.Add((PolicyKinds.Apps, "applications", new AgentPolicy { Block = new BlockSettings { Apps = b.Apps, NotifyUser = b.NotifyUser, Message = b.Message } }));
            if (model.Collectors.UsbBlock)
                parts.Add((PolicyKinds.Usb, "USB drives", new AgentPolicy { Collectors = new CollectorSettings { UsbBlock = true } }));
            if (parts.Count == 0) continue;

            var assignments = await db.PolicyAssignments.AsNoTracking().Where(a => a.PolicyId == src.Id).ToListAsync();
            if (src.IsDefault && root is { } r) assignments = [new PolicyAssignment { TargetType = "orgUnit", TargetId = r }];
            foreach (var (kind, label, settings) in parts)
            {
                var name = src.IsDefault ? $"Block {label} (all computers)" : parts.Count == 1 ? src.Name : $"{src.Name} - {label}";
                settings.Name = name;
                var np = new Policy { Name = name, Description = src.Description, Kind = kind, Json = Json.Serialize(settings), UpdatedBy = "system" };
                db.Policies.Add(np);
                foreach (var a in assignments) db.PolicyAssignments.Add(new PolicyAssignment { PolicyId = np.Id, TargetType = a.TargetType, TargetId = a.TargetId });
                moved++;
            }
            // the monitoring policy keeps everything except the blocking
            model.Block = new BlockSettings();
            model.Collectors.UsbBlock = false;
            src.Json = Json.Serialize(model);
            src.Version++;
            if (!src.IsDefault) await db.PolicyAssignments.Where(a => a.PolicyId == src.Id).ExecuteDeleteAsync();
        }
        if (moved == 0) return;
        await db.SaveChangesAsync();
        log.LogInformation("Moved blocking from monitoring policies into {Count} block policies", moved);
    }

    /// <summary>
    /// One-off when Devices.EnrollmentTokenId is introduced: recover each device's token from its latest enrollment
    /// event ("Enrolled with token 'name'"), picking the newest token with that name created before the event.
    /// </summary>
    private static async Task BackfillDeviceTokensAsync(AppDb db, ILogger log)
    {
        var tokens = await db.EnrollmentTokens.AsNoTracking().Select(t => new { t.Id, t.Name, t.CreatedAt }).ToListAsync();
        var events = await db.Events.AsNoTracking().Where(e => e.Type == "agent.enrolled" || e.Type == "agent.reenrolled")
            .Select(e => new { e.DeviceId, e.Time, e.Message }).ToListAsync();
        var linked = 0;
        foreach (var ev in events.GroupBy(e => e.DeviceId).Select(g => g.MaxBy(e => e.Time)!))
        {
            var m = System.Text.RegularExpressions.Regex.Match(ev.Message ?? "", "token '(.*)'");
            if (!m.Success) continue;
            var tok = tokens.Where(t => t.Name == m.Groups[1].Value && t.CreatedAt <= ev.Time.AddMinutes(1)).MaxBy(t => t.CreatedAt);
            if (tok is null) continue;
            linked += await db.Devices.Where(d => d.Id == ev.DeviceId && d.EnrollmentTokenId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.EnrollmentTokenId, tok.Id));
        }
        log.LogInformation("Linked {Count} existing device(s) to their enrollment token", linked);
    }

    public static async Task SeedAsync(AppDb db, ServerKeys keys, IConfiguration config, ILogger log)
    {
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        var added = await UpgradeSchemaAsync(db, log);
        if (added.Contains("Devices.EnrollmentTokenId")) await BackfillDeviceTokensAsync(db, log);

        if (!await db.OrgUnits.AnyAsync())
        {
            var root = new OrgUnit { Name = config["Organization:Name"] ?? "Organization", Type = "organization" };
            db.OrgUnits.Add(root);
            db.OrgUnits.Add(new OrgUnit { ParentId = root.Id, Name = "Head Office", Type = "branch", Lat = 6.9271, Lng = 79.8612 });
        }

        if (!await db.AdminUsers.AnyAsync())
        {
            var pwd = config["Bootstrap:AdminPassword"] ?? "ChangeMe@2026";
            var admin = new AdminUser { Username = "admin", DisplayName = "System Administrator", Role = Roles.SuperAdmin, MustChangePassword = true };
            admin.PasswordHash = Passwords.Hash(admin, pwd);
            db.AdminUsers.Add(admin);
            log.LogWarning("Created bootstrap admin user 'admin' — sign in and change the password immediately");
        }

        if (!await db.Policies.AnyAsync())
        {
            db.Policies.Add(new Policy
            {
                Name = "Default", Description = "Baseline monitoring for all company devices", IsDefault = true,
                Json = Json.Serialize(PolicyService.DefaultPolicy()), UpdatedBy = "system"
            });
            var usb = PolicyService.DefaultPolicy();
            usb.Name = "Block USB drives";
            usb.Collectors.UsbBlock = true;
            db.Policies.Add(new Policy { Name = "Block USB drives", Description = "Finance & cashier counters", Kind = PolicyKinds.Usb, Json = Json.Serialize(usb), UpdatedBy = "system" });
        }

        if (!await db.AlertRules.AnyAsync())
        {
            db.AlertRules.AddRange(
                new AlertRule { Name = "Daily data usage > 2 GB", Type = "dataUsageDaily", Threshold = 2048, Severity = "warning" },
                new AlertRule { Name = "Agent offline > 30 min", Type = "agentOffline", Threshold = 30, Severity = "warning" },
                new AlertRule { Name = "Sustained CPU > 90%", Type = "cpuHigh", Threshold = 90, Severity = "info" },
                new AlertRule { Name = "System disk > 90% full", Type = "diskLow", Threshold = 90, Severity = "warning" },
                new AlertRule { Name = "Tamper attempt", Type = "tamper", Severity = "critical", NotifyEmail = true, NotifyWebhook = true },
                new AlertRule { Name = "Unapproved software", Type = "software", Parameter = "uTorrent, BitTorrent, TeamViewer, AnyDesk, CCleaner", Severity = "warning" },
                new AlertRule { Name = "Gaming / streaming during work", Type = "category", Parameter = "Gaming", Severity = "info" },
                new AlertRule { Name = "USB storage connected", Type = "usb", Severity = "info", Enabled = false });
        }
        // added after the first release: make sure existing installations get it too
        if (!await db.AlertRules.AnyAsync(r => r.Type == "agentSilent"))
        {
            db.AlertRules.Add(new AlertRule { Name = "Agent silent > 24 h (possible removal)", Type = "agentSilent", Threshold = 24, Severity = "critical", NotifyEmail = true, NotifyWebhook = true });
            await db.SaveChangesAsync();
        }
        await MigrateLegacyBlockingAsync(db, log);
        if (!await db.AlertRules.AnyAsync(r => r.Type == "blocked"))
        {
            db.AlertRules.Add(new AlertRule { Name = "Blocked application or website", Type = "blocked", Severity = "info" });
            await db.SaveChangesAsync();
        }
        // Local development: a well-known enrollment token so `dotnet run` of the agent enrolls itself
        if (config["Dev:EnrollToken"] is { Length: > 0 } devToken)
        {
            var hash = Endpoints.AgentEndpoints.HashToken(devToken);
            var existing = await db.EnrollmentTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
            if (existing is not null) existing.TokenProtected ??= keys.ProtectToken(devToken); // already plain in config
            else
                db.EnrollmentTokens.Add(new EnrollmentToken
                {
                    Name = "Local development", TokenHash = hash, TokenProtected = keys.ProtectToken(devToken), Prefix = devToken[..Math.Min(9, devToken.Length)],
                    OrgUnitId = (db.OrgUnits.Local.FirstOrDefault(o => o.Name == "Head Office") ?? await db.OrgUnits.FirstOrDefaultAsync(o => o.Name == "Head Office" || o.Name == "Colombo HQ"))?.Id,
                    ExpiresAt = DateTime.UtcNow.AddYears(5), UsesLeft = 1000, AutoApprove = true, CreatedBy = "system"
                });
        }
        await db.SaveChangesAsync();
    }
}
