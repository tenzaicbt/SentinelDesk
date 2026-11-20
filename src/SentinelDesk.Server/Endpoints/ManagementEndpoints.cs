using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SentinelDesk.Server.Data;
using SentinelDesk.Server.Security;
using SentinelDesk.Server.Services;
using SentinelDesk.Shared;

namespace SentinelDesk.Server.Endpoints;

public static class ManagementEndpoints
{
    public sealed record OrgUnitRequest(string Name, string Type, Guid? ParentId, double? Lat, double? Lng);
    public sealed record PolicyRequest(string Name, string? Description, AgentPolicy Settings);
    public sealed record AssignRequest(string TargetType, Guid TargetId);
    public sealed record RuleRequest(string Name, string Type, double Threshold, string? Parameter, string Severity, bool Enabled, bool NotifyEmail, bool NotifyWebhook);
    public sealed record TokenRequest(string Name, Guid? OrgUnitId, int Days, int Uses, bool AutoApprove);
    public sealed record InstallerRequest(string? Name, Guid? OrgUnitId, int Days, int Uses, bool AutoApprove, string ServerUrl, string UninstallKey, bool TrustOnFirstUse, string? ServerCertSha256);
    public sealed record UserRequest(string Username, string DisplayName, string? Email, string Role, Guid? OrgUnitScope, string? Password, bool Disabled);
    public sealed record RolloutRequest(int Percent);
    public sealed record IdsRequest(long[] Ids);

    public static void MapManagementApi(this WebApplication app)
    {
        MapOrg(app);
        MapPolicies(app);
        MapAlerts(app);
        MapAgents(app);
        MapAdmin(app);
    }

    // ───────────── Organization ─────────────
    private static void MapOrg(WebApplication app)
    {
        app.MapGet("/api/org", async (AppDb db, PresenceStore presence, AccessScope scope) =>
        {
            var units = await db.OrgUnits.AsNoTracking().ToListAsync();
            var allowed = await scope.UnitsAsync();
            var devices = await db.Devices.AsNoTracking().Where(d => d.Status != DeviceStatus.Retired).Select(d => new { d.Id, d.OrgUnitId }).ToListAsync();
            var assignments = await db.PolicyAssignments.AsNoTracking().Where(a => a.TargetType == "orgUnit").ToListAsync();
            var policies = await db.Policies.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name);
            return Results.Ok(units.Where(u => allowed is null || allowed.Contains(u.Id)).Select(u => new
            {
                u.Id, u.ParentId, u.Name, u.Type, u.Lat, u.Lng,
                Devices = devices.Count(d => d.OrgUnitId == u.Id),
                Online = devices.Count(d => d.OrgUnitId == u.Id && presence.IsOnline(d.Id)),
                Policy = assignments.FirstOrDefault(a => a.TargetId == u.Id) is { } a ? new { a.PolicyId, Name = policies.GetValueOrDefault(a.PolicyId) } : null
            }).OrderBy(u => u.Name));
        }).RequireAuthorization("Console");

        var w = app.MapGroup("/api/org").RequireAuthorization("Super");
        w.MapPost("", async (OrgUnitRequest r, AppDb db, AuditService audit) =>
        {
            if (string.IsNullOrWhiteSpace(r.Name)) return Results.BadRequest();
            var u = new OrgUnit { Name = r.Name.Trim(), Type = r.Type, ParentId = r.ParentId, Lat = r.Lat, Lng = r.Lng };
            db.OrgUnits.Add(u);
            await db.SaveChangesAsync();
            await audit.LogAsync("org.create", u.Name);
            return Results.Ok(u);
        });
        w.MapPut("/{id:guid}", async (Guid id, OrgUnitRequest r, AppDb db, AuditService audit) =>
        {
            var u = await db.OrgUnits.FindAsync(id);
            if (u is null) return Results.NotFound();
            if (r.ParentId == id) return Results.Problem("A unit cannot be its own parent", statusCode: 400);
            u.Name = r.Name.Trim(); u.Type = r.Type; u.Lat = r.Lat; u.Lng = r.Lng;
            if (u.ParentId is not null) u.ParentId = r.ParentId;
            await db.SaveChangesAsync();
            await audit.LogAsync("org.update", u.Name);
            return Results.Ok(u);
        });
        w.MapDelete("/{id:guid}", async (Guid id, AppDb db, AuditService audit) =>
        {
            var u = await db.OrgUnits.FindAsync(id);
            if (u is null) return Results.NotFound();
            if (u.ParentId is null) return Results.Problem("The root organization cannot be deleted", statusCode: 400);
            if (await db.OrgUnits.AnyAsync(o => o.ParentId == id) || await db.Devices.AnyAsync(d => d.OrgUnitId == id))
                return Results.Problem("Move child units and devices first", statusCode: 400);
            db.OrgUnits.Remove(u);
            await db.PolicyAssignments.Where(a => a.TargetId == id).ExecuteDeleteAsync();
            await db.SaveChangesAsync();
            await audit.LogAsync("org.delete", u.Name);
            return Results.Ok();
        });
    }

    // ───────────── Policies ─────────────
    private static void MapPolicies(WebApplication app)
    {
        app.MapGet("/api/policies", async (AppDb db) =>
        {
            var policies = await db.Policies.AsNoTracking().OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name).ToListAsync();
            var assignments = await db.PolicyAssignments.AsNoTracking().ToListAsync();
            var units = await db.OrgUnits.AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.Name);
            var devices = await db.Devices.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Hostname);
            return Results.Ok(policies.Select(p => new
            {
                p.Id, p.Name, p.Description, p.Version, p.IsDefault, p.UpdatedAt, p.UpdatedBy,
                Settings = Json.Deserialize<AgentPolicy>(p.Json),
                Assignments = assignments.Where(a => a.PolicyId == p.Id).Select(a => new
                {
                    a.Id, a.TargetType, a.TargetId,
                    Name = a.TargetType == "device" ? devices.GetValueOrDefault(a.TargetId) : units.GetValueOrDefault(a.TargetId)
                })
            }));
        }).RequireAuthorization("Console");

        var w = app.MapGroup("/api/policies").RequireAuthorization("Super");

        w.MapPost("", async (PolicyRequest r, AppDb db, AccessScope scope, AuditService audit) =>
        {
            r.Settings.Name = r.Name;
            var p = new Policy { Name = r.Name, Description = r.Description, Json = Json.Serialize(r.Settings), UpdatedBy = scope.Username };
            db.Policies.Add(p);
            await db.SaveChangesAsync();
            await audit.LogAsync("policy.create", p.Name);
            return Results.Ok(new { p.Id });
        });

        w.MapPut("/{id:guid}", async (Guid id, PolicyRequest r, AppDb db, AccessScope scope, AuditService audit, CommandService commands, PresenceStore presence) =>
        {
            var p = await db.Policies.FindAsync(id);
            if (p is null) return Results.NotFound();
            r.Settings.Name = r.Name;
            p.Name = r.Name; p.Description = r.Description; p.Json = Json.Serialize(r.Settings);
            p.Version++; p.UpdatedAt = DateTime.UtcNow; p.UpdatedBy = scope.Username;
            await db.SaveChangesAsync();
            await audit.LogAsync("policy.update", p.Name, $"v{p.Version}");
            // push to all live agents — each re-resolves and only downloads if its effective version changed
            await commands.BroadcastPolicyChangedAsync(presence.All.Select(x => x.DeviceId));
            return Results.Ok(new { p.Version });
        });

        w.MapDelete("/{id:guid}", async (Guid id, AppDb db, AuditService audit) =>
        {
            var p = await db.Policies.FindAsync(id);
            if (p is null) return Results.NotFound();
            if (p.IsDefault) return Results.Problem("The default policy cannot be deleted", statusCode: 400);
            await db.PolicyAssignments.Where(a => a.PolicyId == id).ExecuteDeleteAsync();
            db.Policies.Remove(p);
            await db.SaveChangesAsync();
            await audit.LogAsync("policy.delete", p.Name);
            return Results.Ok();
        });

        w.MapPost("/{id:guid}/assign", async (Guid id, AssignRequest r, AppDb db, AuditService audit, CommandService commands, PresenceStore presence) =>
        {
            if (r.TargetType is not ("orgUnit" or "device")) return Results.BadRequest();
            await db.PolicyAssignments.Where(a => a.TargetType == r.TargetType && a.TargetId == r.TargetId).ExecuteDeleteAsync();
            db.PolicyAssignments.Add(new PolicyAssignment { PolicyId = id, TargetType = r.TargetType, TargetId = r.TargetId });
            await db.SaveChangesAsync();
            await audit.LogAsync("policy.assign", id.ToString(), $"{r.TargetType}:{r.TargetId}");
            await commands.BroadcastPolicyChangedAsync(presence.All.Select(x => x.DeviceId));
            return Results.Ok();
        });

        w.MapDelete("/assignments/{assignmentId:guid}", async (Guid assignmentId, AppDb db, AuditService audit, CommandService commands, PresenceStore presence) =>
        {
            await db.PolicyAssignments.Where(a => a.Id == assignmentId).ExecuteDeleteAsync();
            await audit.LogAsync("policy.unassign", assignmentId.ToString());
            await commands.BroadcastPolicyChangedAsync(presence.All.Select(x => x.DeviceId));
            return Results.Ok();
        });
    }

    // ───────────── Alerts ─────────────
    private static void MapAlerts(WebApplication app)
    {
        app.MapGet("/api/alerts", async (string? status, string? severity, Guid? deviceId, AppDb db, AccessScope scope) =>
        {
            var devices = await (await scope.DevicesAsync()).Select(d => new { d.Id, d.Hostname, d.OrgUnitId }).ToDictionaryAsync(d => d.Id);
            var q = db.Alerts.AsNoTracking();
            if (!string.IsNullOrEmpty(status) && status != "all") q = q.Where(a => a.Status == status);
            if (!string.IsNullOrEmpty(severity)) q = q.Where(a => a.Severity == severity);
            if (deviceId is { } did) q = q.Where(a => a.DeviceId == did);
            var list = await q.OrderByDescending(a => a.TriggeredAt).Take(500).ToListAsync();
            var rules = await db.AlertRules.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name);
            return Results.Ok(list.Where(a => devices.ContainsKey(a.DeviceId)).Select(a => new
            {
                a.Id, a.Title, a.Details, a.Severity, a.Status, a.TriggeredAt, a.HandledBy, a.HandledAt, a.DeviceId,
                Hostname = devices[a.DeviceId].Hostname, Rule = rules.GetValueOrDefault(a.RuleId)
            }));
        }).RequireAuthorization("Console");

        foreach (var (action, status) in new[] { ("acknowledge", "acknowledged"), ("resolve", "resolved") })
            app.MapPost($"/api/alerts/{action}", async (IdsRequest r, AppDb db, AccessScope scope, AuditService audit) =>
            {
                var alerts = await db.Alerts.Where(a => r.Ids.Contains(a.Id)).ToListAsync();
                foreach (var a in alerts)
                {
                    if (!await scope.CanAccessDeviceAsync(a.DeviceId)) continue;
                    a.Status = status; a.HandledBy = scope.Username; a.HandledAt = DateTime.UtcNow;
                }
                await db.SaveChangesAsync();
                await audit.LogAsync($"alert.{action}", $"{alerts.Count} alerts");
                return Results.Ok();
            }).RequireAuthorization("Write");

        app.MapGet("/api/alert-rules", async (AppDb db) => Results.Ok(await db.AlertRules.AsNoTracking().OrderBy(r => r.Name).ToListAsync()))
            .RequireAuthorization("Console");

        var w = app.MapGroup("/api/alert-rules").RequireAuthorization("Super");
        w.MapPost("", async (RuleRequest r, AppDb db, AuditService audit) =>
        {
            var rule = new AlertRule();
            Apply(rule, r);
            db.AlertRules.Add(rule);
            await db.SaveChangesAsync();
            await audit.LogAsync("alertRule.create", rule.Name);
            return Results.Ok(rule);
        });
        w.MapPut("/{id:guid}", async (Guid id, RuleRequest r, AppDb db, AuditService audit) =>
        {
            var rule = await db.AlertRules.FindAsync(id);
            if (rule is null) return Results.NotFound();
            Apply(rule, r);
            await db.SaveChangesAsync();
            await audit.LogAsync("alertRule.update", rule.Name);
            return Results.Ok(rule);
        });
        w.MapDelete("/{id:guid}", async (Guid id, AppDb db, AuditService audit) =>
        {
            await db.AlertRules.Where(x => x.Id == id).ExecuteDeleteAsync();
            await audit.LogAsync("alertRule.delete", id.ToString());
            return Results.Ok();
        });

        static void Apply(AlertRule rule, RuleRequest r)
        {
            rule.Name = r.Name; rule.Type = r.Type; rule.Threshold = r.Threshold; rule.Parameter = r.Parameter;
            rule.Severity = r.Severity; rule.Enabled = r.Enabled; rule.NotifyEmail = r.NotifyEmail; rule.NotifyWebhook = r.NotifyWebhook;
        }
    }

    // ───────────── Agent management ─────────────
    private static void MapAgents(WebApplication app)
    {
        var g = app.MapGroup("/api/agents").RequireAuthorization("Super");

        g.MapGet("/tokens", async (AppDb db) =>
        {
            var units = await db.OrgUnits.AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.Name);
            var tokens = await db.EnrollmentTokens.AsNoTracking().OrderByDescending(t => t.CreatedAt).ToListAsync();
            return Results.Ok(tokens.Select(t => new
            {
                t.Id, t.Name, t.Prefix, t.OrgUnitId, OrgUnit = t.OrgUnitId is { } u ? units.GetValueOrDefault(u) : null,
                t.ExpiresAt, t.UsesLeft, t.UsedCount, t.AutoApprove, t.Revoked, t.CreatedAt, t.CreatedBy,
                Active = !t.Revoked && t.ExpiresAt > DateTime.UtcNow && t.UsesLeft > 0
            }));
        });

        g.MapPost("/tokens", async (TokenRequest r, AppDb db, AccessScope scope, AuditService audit) =>
        {
            var raw = "SD-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
            var t = new EnrollmentToken
            {
                Name = r.Name, OrgUnitId = r.OrgUnitId, ExpiresAt = DateTime.UtcNow.AddDays(Math.Clamp(r.Days, 1, 365)),
                UsesLeft = Math.Clamp(r.Uses, 1, 100_000), AutoApprove = r.AutoApprove, TokenHash = AgentEndpoints.HashToken(raw),
                Prefix = raw[..9], CreatedBy = scope.Username
            };
            db.EnrollmentTokens.Add(t);
            await db.SaveChangesAsync();
            await audit.LogAsync("enrollToken.create", t.Name);
            return Results.Ok(new { t.Id, token = raw });
        });

        g.MapPost("/tokens/{id:guid}/revoke", async (Guid id, AppDb db, AuditService audit) =>
        {
            await db.EnrollmentTokens.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Revoked, true));
            await audit.LogAsync("enrollToken.revoke", id.ToString());
            return Results.Ok();
        });

        g.MapGet("/releases", async (AppDb db) =>
        {
            var releases = await db.Releases.AsNoTracking().OrderByDescending(r => r.UploadedAt).ToListAsync();
            var versions = await db.Devices.AsNoTracking().Where(d => d.Status == DeviceStatus.Active).GroupBy(d => d.AgentVersion)
                .Select(x => new { Version = x.Key, Count = x.Count() }).ToListAsync();
            return Results.Ok(new { releases, versions = versions.OrderByDescending(v => v.Version) });
        });

        g.MapPost("/releases", async (HttpRequest req, AppDb db, ServerKeys keys, IConfiguration config, AccessScope scope, AuditService audit) =>
        {
            if (!req.HasFormContentType) return Results.BadRequest();
            var form = await req.ReadFormAsync();
            var file = form.Files.GetFile("package");
            var version = form["version"].ToString().Trim();
            if (file is null || !Version.TryParse(version, out _)) return Results.Problem("A .zip package and a version (e.g. 1.0.1) are required", statusCode: 400);
            if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return Results.Problem("Package must be a .zip of the agent publish folder", statusCode: 400);
            var dir = Path.Combine(config["Storage:DataDir"] ?? "data", "releases");
            Directory.CreateDirectory(dir);
            var name = $"SentinelDesk-Agent-{version}.zip";
            var path = Path.Combine(dir, name);
            await using (var fs = File.Create(path)) await file.CopyToAsync(fs);
            await using var read = File.OpenRead(path);
            var sha = Convert.ToHexString(await SHA256.HashDataAsync(read));
            var rel = new AgentRelease
            {
                Version = version, FileName = name, Sha256 = sha, SizeBytes = file.Length, Notes = form["notes"],
                Signature = Signing.Sign(keys.SigningKey, $"{version}|{sha}"), UploadedBy = scope.Username
            };
            db.Releases.Add(rel);
            await db.SaveChangesAsync();
            await audit.LogAsync("release.upload", version, sha);
            return Results.Ok(rel);
        }).DisableAntiforgery();

        g.MapPost("/releases/{id:guid}/rollout", async (Guid id, RolloutRequest r, AppDb db, AuditService audit) =>
        {
            var rel = await db.Releases.FindAsync(id);
            if (rel is null) return Results.NotFound();
            rel.RolloutPercent = Math.Clamp(r.Percent, 0, 100);
            await db.SaveChangesAsync();
            await audit.LogAsync("release.rollout", rel.Version, $"{rel.RolloutPercent}%");
            return Results.Ok();
        });

        g.MapDelete("/releases/{id:guid}", async (Guid id, AppDb db, IConfiguration config, AuditService audit) =>
        {
            var rel = await db.Releases.FindAsync(id);
            if (rel is null) return Results.NotFound();
            var path = Path.Combine(config["Storage:DataDir"] ?? "data", "releases", rel.FileName);
            if (File.Exists(path)) File.Delete(path);
            db.Releases.Remove(rel);
            await db.SaveChangesAsync();
            await audit.LogAsync("release.delete", rel.Version);
            return Results.Ok();
        });

        // ── One-file installer for offices: SentinelDesk-Setup.exe with server URL, a fresh enrollment token and the
        //    uninstall-key hash embedded (appended trailer). Office staff just double-click it (UAC prompt).
        g.MapGet("/installer", (IWebHostEnvironment env, IConfiguration config) =>
        {
            var path = FindInstaller(env, config);
            var fi = path is null ? null : new FileInfo(path);
            // addresses offices could use to reach this server (localhost won't work from other PCs)
            var host = System.Net.Dns.GetHostName();
            var ips = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."))
                .Select(a => a.Address.ToString()).Distinct();
            var suggestions = new[] { config["Server:PublicUrl"] }.Concat(ips.Prepend(host).Select(h => $"https://{h}:5443"))
                .Where(u => !string.IsNullOrEmpty(u) && !u.Contains("monitor.company.lk")).Distinct();
            return Results.Ok(new { available = fi is not null, sizeBytes = fi?.Length, builtAt = fi?.LastWriteTimeUtc, suggestions });
        });

        g.MapPost("/installer", async (InstallerRequest r, AppDb db, IWebHostEnvironment env, IConfiguration config, AccessScope scope, AuditService audit) =>
        {
            var path = FindInstaller(env, config);
            if (path is null) return Results.Problem("SentinelDesk-Setup.exe has not been built yet - run build.ps1 on the server", statusCode: 404);
            if (!Uri.TryCreate(r.ServerUrl?.Trim(), UriKind.Absolute, out var server) || server.Scheme is not ("https" or "http"))
                return Results.Problem("Enter the server address the offices will connect to, e.g. https://monitor.company.lk:5443", statusCode: 400);
            if (server.IsLoopback) return Results.Problem("'localhost' only works on this computer - use the server's network name or IP address", statusCode: 400);
            if (string.IsNullOrWhiteSpace(r.UninstallKey) || r.UninstallKey.Length < 8) return Results.Problem("Uninstall key must be at least 8 characters", statusCode: 400);

            var unit = r.OrgUnitId is { } ou ? await db.OrgUnits.FindAsync(ou) : null;
            var raw = "SD-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
            var token = new EnrollmentToken
            {
                Name = string.IsNullOrWhiteSpace(r.Name) ? $"Installer - {unit?.Name ?? "unassigned"} - {DateTime.UtcNow:yyyy-MM-dd}" : r.Name.Trim(),
                OrgUnitId = unit?.Id, ExpiresAt = DateTime.UtcNow.AddDays(Math.Clamp(r.Days, 1, 365)), UsesLeft = Math.Clamp(r.Uses, 1, 100_000),
                AutoApprove = r.AutoApprove, TokenHash = AgentEndpoints.HashToken(raw), Prefix = raw[..9], CreatedBy = scope.Username
            };
            db.EnrollmentTokens.Add(token);
            await db.SaveChangesAsync();

            var org = (await db.OrgUnits.AsNoTracking().FirstOrDefaultAsync(o => o.ParentId == null))?.Name;
            var settings = new Dictionary<string, string?>
            {
                ["server"] = server.GetLeftPart(UriPartial.Authority), ["token"] = raw, ["uninstallKeyHash"] = KeyHash.Hash(r.UninstallKey),
                ["tofu"] = r.TrustOnFirstUse ? "1" : "0", ["pin"] = r.ServerCertSha256?.Replace(":", "").Trim(), ["org"] = org, ["branch"] = unit?.Name,
            };
            var body = Encoding.UTF8.GetBytes(string.Join('\n', settings.Where(kv => !string.IsNullOrEmpty(kv.Value)).Select(kv => $"{kv.Key}={kv.Value}")));
            var exe = await File.ReadAllBytesAsync(path);
            var file = new byte[exe.Length + body.Length + 12];
            exe.CopyTo(file, 0);
            body.CopyTo(file, exe.Length);
            BitConverter.GetBytes(body.Length).CopyTo(file, exe.Length + body.Length);
            Encoding.ASCII.GetBytes("SDSETUP1").CopyTo(file, exe.Length + body.Length + 4);

            await audit.LogAsync("installer.download", token.Name, $"server={settings["server"]} uses={token.UsesLeft} days={r.Days}");
            var safe = new string((unit?.Name ?? "Organization").Where(char.IsLetterOrDigit).ToArray());
            return Results.File(file, "application/vnd.microsoft.portable-executable", $"SentinelDesk-Setup-{safe}.exe");
        });

        g.MapGet("/server-info", (ServerKeys keys, IConfiguration config) => Results.Ok(new
        {
            CaThumbprint = keys.CaCert.Thumbprint,
            CaExpires = keys.CaCert.NotAfter,
            SigningKeyFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(keys.SigningPublicPem)))[..32],
            EncryptionKeyFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(keys.EncryptionPublicPem)))[..32],
            PublicUrl = config["Server:PublicUrl"],
            RequireMtls = config.GetValue("Security:RequireMtls", false),
            DemoMode = config.GetValue("Demo:Enabled", false),
        }));
    }

    private static string? FindInstaller(IWebHostEnvironment env, IConfiguration config)
    {
        var dataDir = config["Storage:DataDir"] ?? "data";
        string?[] candidates =
        [
            config["Installer:Path"],
            Path.Combine(AppContext.BaseDirectory, "installer", "SentinelDesk-Setup.exe"),
            Path.Combine(dataDir, "installer", "SentinelDesk-Setup.exe"),
            Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "..", "publish", "SentinelDesk-Setup.exe")), // dev: repo/publish
        ];
        return candidates.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
    }

    // ───────────── Users & audit ─────────────
    private static void MapAdmin(WebApplication app)
    {
        var g = app.MapGroup("/api/users").RequireAuthorization("Super");
        g.MapGet("", async (AppDb db) =>
        {
            var units = await db.OrgUnits.AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.Name);
            var users = await db.AdminUsers.AsNoTracking().OrderBy(u => u.Username).ToListAsync();
            return Results.Ok(users.Select(u => new
            {
                u.Id, u.Username, u.DisplayName, u.Email, u.Role, u.OrgUnitScope, Scope = u.OrgUnitScope is { } s ? units.GetValueOrDefault(s) : null,
                u.MfaEnabled, u.Disabled, u.LastLoginAt, Locked = u.LockedUntil > DateTime.UtcNow
            }));
        });
        g.MapPost("", async (UserRequest r, AppDb db, AuditService audit) =>
        {
            if (!Roles.All.Contains(r.Role)) return Results.Problem("Unknown role", statusCode: 400);
            var username = r.Username.Trim().ToLowerInvariant();
            if (await db.AdminUsers.AnyAsync(u => u.Username == username)) return Results.Problem("Username already exists", statusCode: 400);
            var pwdError = r.Password is null ? "Password required" : Passwords.Validate(r.Password!);
            if (pwdError is not null) return Results.Problem(pwdError, statusCode: 400);
            var u = new AdminUser
            {
                Username = username, DisplayName = r.DisplayName, Email = r.Email, Role = r.Role, OrgUnitScope = r.OrgUnitScope,
                Disabled = r.Disabled, MustChangePassword = true
            };
            u.PasswordHash = Passwords.Hash(u, r.Password!);
            db.AdminUsers.Add(u);
            await db.SaveChangesAsync();
            await audit.LogAsync("user.create", u.Username, u.Role);
            return Results.Ok(new { u.Id });
        });
        g.MapPut("/{id:guid}", async (Guid id, UserRequest r, AppDb db, AccessScope scope, AuditService audit) =>
        {
            var u = await db.AdminUsers.FindAsync(id);
            if (u is null) return Results.NotFound();
            if (!Roles.All.Contains(r.Role)) return Results.Problem("Unknown role", statusCode: 400);
            if (u.Username == scope.Username && (r.Disabled || r.Role != Roles.SuperAdmin))
                return Results.Problem("You cannot demote or disable your own account", statusCode: 400);
            u.DisplayName = r.DisplayName; u.Email = r.Email; u.Role = r.Role; u.OrgUnitScope = r.OrgUnitScope; u.Disabled = r.Disabled;
            if (!string.IsNullOrEmpty(r.Password))
            {
                if (Passwords.Validate(r.Password) is { } err) return Results.Problem(err, statusCode: 400);
                u.PasswordHash = Passwords.Hash(u, r.Password!);
                u.MustChangePassword = true;
                u.LockedUntil = null;
            }
            await db.SaveChangesAsync();
            await audit.LogAsync("user.update", u.Username, $"role={u.Role} disabled={u.Disabled}");
            return Results.Ok();
        });
        g.MapPost("/{id:guid}/reset-mfa", async (Guid id, AppDb db, AuditService audit) =>
        {
            var u = await db.AdminUsers.FindAsync(id);
            if (u is null) return Results.NotFound();
            u.MfaEnabled = false; u.TotpSecret = null;
            await db.SaveChangesAsync();
            await audit.LogAsync("user.resetMfa", u.Username);
            return Results.Ok();
        });

        app.MapGet("/api/audit", async (string? actor, string? action, int? limit, AppDb db) =>
        {
            var q = db.Audit.AsNoTracking();
            if (!string.IsNullOrEmpty(actor)) q = q.Where(a => a.Actor.Contains(actor));
            if (!string.IsNullOrEmpty(action)) q = q.Where(a => a.Action.StartsWith(action));
            return Results.Ok(await q.OrderByDescending(a => a.Id).Take(Math.Clamp(limit ?? 300, 1, 2000)).ToListAsync());
        }).RequireAuthorization("Audit");
    }
}
