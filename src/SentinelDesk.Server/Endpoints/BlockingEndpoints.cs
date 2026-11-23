using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SentinelDesk.Server.Data;
using SentinelDesk.Server.Security;
using SentinelDesk.Server.Services;
using SentinelDesk.Shared;

namespace SentinelDesk.Server.Endpoints;

/// <summary>
/// Advanced blocking: exceptions and temporary unblocks, access requests from the tray, offline unlock codes and the
/// emergency "Unblock all". Every change is audited and pushed to online agents straight away.
/// </summary>
public static class BlockingEndpoints
{
    public sealed record ExceptionRequest(string TargetType, Guid TargetId, DateTime? UntilUtc, string? Value, string? Reason);
    public sealed record DecideRequest(DateTime? UntilUtc);
    public sealed record UnlockCodeRequest(int Hours);
    public sealed record EmergencyRequest(Guid? OrgUnitId, DateTime? UntilUtc, string? Reason, string Password, string? Code);

    public static void MapBlockingApi(this WebApplication app)
    {
        // ───────────── exceptions (a computer or branch where a block policy does not apply) ─────────────
        var super = app.MapGroup("/api").RequireAuthorization("Super");

        super.MapPost("/policies/{id:guid}/exceptions", async (Guid id, ExceptionRequest r, AppDb db, AccessScope scope, AuditService audit, CommandService commands, PresenceStore presence) =>
        {
            var p = await db.Policies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (p is null || !PolicyKinds.IsRestriction(p.Kind)) return Results.NotFound();
            if (r.TargetType is not ("orgUnit" or "device")) return Results.BadRequest();
            if (r.UntilUtc is { } u && u <= DateTime.UtcNow) return Results.Problem("The end time is in the past", statusCode: 400);
            var value = string.IsNullOrWhiteSpace(r.Value) ? null : r.Value.Trim();
            if (value is not null && (p.Kind == PolicyKinds.Websites ? BlockRules.NormalizeUrl(value, out var e1) is null : BlockRules.NormalizeApp(value, out e1) is null))
                return Results.Problem(e1 ?? "Not a valid value", statusCode: 400);
            var row = new BlockExceptionRow
            {
                PolicyId = id, Kind = p.Kind, Value = value, TargetType = r.TargetType, TargetId = r.TargetId,
                UntilUtc = r.UntilUtc, Reason = r.Reason?.Trim(), CreatedBy = scope.Username,
            };
            db.BlockExceptions.Add(row);
            await db.SaveChangesAsync();
            await audit.LogAsync("policy.exception.add", p.Name, $"{r.TargetType}:{r.TargetId} {value ?? "(whole policy)"} until {r.UntilUtc?.ToString("u") ?? "removed"}");
            await commands.BroadcastPolicyChangedAsync(presence.All.Select(x => x.DeviceId));
            return Results.Ok(new { row.Id });
        });

        super.MapDelete("/policies/exceptions/{id:guid}", async (Guid id, AppDb db, AuditService audit, CommandService commands, PresenceStore presence) =>
        {
            var row = await db.BlockExceptions.FindAsync(id);
            if (row is null) return Results.NotFound();
            db.BlockExceptions.Remove(row);
            await db.SaveChangesAsync();
            await audit.LogAsync("policy.exception.remove", row.PolicyId?.ToString() ?? "any", $"{row.TargetType}:{row.TargetId} {row.Value}");
            await commands.BroadcastPolicyChangedAsync(presence.All.Select(x => x.DeviceId));
            return Results.Ok();
        });

        // ───────────── access requests ─────────────
        app.MapGet("/api/access-requests", async (string? status, AppDb db, AccessScope scope) =>
        {
            var units = await scope.UnitsAsync();
            var devices = await db.Devices.AsNoTracking().Select(d => new { d.Id, d.Hostname, d.OrgUnitId }).ToDictionaryAsync(d => d.Id);
            var q = db.AccessRequests.AsNoTracking();
            q = status is { Length: > 0 } ? q.Where(r => r.Status == status) : q.Where(r => r.Status == "pending" || r.CreatedAt > DateTime.UtcNow.AddDays(-7));
            var rows = await q.OrderByDescending(r => r.CreatedAt).Take(200).ToListAsync();
            return Results.Ok(rows
                .Where(r => units is null || (devices.TryGetValue(r.DeviceId, out var d) && d.OrgUnitId is { } u && units.Contains(u)))
                .Select(r => new
                {
                    r.Id, r.DeviceId, Hostname = devices.GetValueOrDefault(r.DeviceId)?.Hostname, r.User, r.Kind, r.Value, r.Reason,
                    r.Status, r.CreatedAt, r.DecidedBy, r.DecidedAt, r.GrantUntilUtc,
                }));
        }).RequireAuthorization("Console");

        app.MapPost("/api/access-requests/{id:guid}/approve", async (Guid id, DecideRequest r, AppDb db, AccessScope scope, AuditService audit, CommandService commands) =>
        {
            var req = await db.AccessRequests.FindAsync(id);
            if (req is null) return Results.NotFound();
            if (!await scope.CanAccessDeviceAsync(req.DeviceId)) return Results.Forbid();
            if (req.Status != "pending") return Results.Problem("This request was already answered", statusCode: 400);
            if (r.UntilUtc is { } u && u <= DateTime.UtcNow) return Results.Problem("The end time is in the past", statusCode: 400);
            req.Status = "approved"; req.DecidedBy = scope.Username; req.DecidedAt = DateTime.UtcNow; req.GrantUntilUtc = r.UntilUtc;
            db.BlockExceptions.Add(new BlockExceptionRow
            {
                Kind = req.Kind, Value = req.Value, TargetType = "device", TargetId = req.DeviceId, UntilUtc = r.UntilUtc,
                Reason = $"Access request approved for {req.User ?? "the user"}", AccessRequestId = req.Id, CreatedBy = scope.Username,
            });
            await db.SaveChangesAsync();
            await audit.LogAsync("accessRequest.approve", req.Value, $"{req.DeviceId} until {r.UntilUtc?.ToString("u") ?? "removed"}");
            await commands.BroadcastPolicyChangedAsync([req.DeviceId]);
            await commands.QueueAsync(req.DeviceId, CommandTypes.ShowMessage, Json.Serialize(new
            {
                title = "Access request approved",
                body = r.UntilUtc is { } until ? $"You can use {req.Value} until {until.ToLocalTime():HH:mm, d MMM}." : $"You can use {req.Value} again.",
            }), scope.Username);
            return Results.Ok();
        }).RequireAuthorization("Write");

        app.MapPost("/api/access-requests/{id:guid}/reject", async (Guid id, AppDb db, AccessScope scope, AuditService audit, CommandService commands) =>
        {
            var req = await db.AccessRequests.FindAsync(id);
            if (req is null) return Results.NotFound();
            if (!await scope.CanAccessDeviceAsync(req.DeviceId)) return Results.Forbid();
            if (req.Status != "pending") return Results.Problem("This request was already answered", statusCode: 400);
            req.Status = "rejected"; req.DecidedBy = scope.Username; req.DecidedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await audit.LogAsync("accessRequest.reject", req.Value, req.DeviceId.ToString());
            await commands.QueueAsync(req.DeviceId, CommandTypes.ShowMessage,
                Json.Serialize(new { title = "Access request declined", body = $"Your request for {req.Value} was declined." }), scope.Username);
            return Results.Ok();
        }).RequireAuthorization("Write");

        // the agent forwards a request typed by the user in the tray
        app.MapPost("/agent/v1/access-requests", async (AccessRequestDto r, ClaimsPrincipal user, AppDb db) =>
        {
            var deviceId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var kind = r.Kind is "websites" or "apps" or "usb" ? r.Kind : "websites";
            var value = (r.Value ?? "").Trim();
            if (value.Length is 0 or > 300) return Results.Problem("Enter the website or program", statusCode: 400);
            var since = DateTime.UtcNow.AddHours(-1);
            if (await db.AccessRequests.CountAsync(x => x.DeviceId == deviceId && x.CreatedAt > since) >= 10)
                return Results.Problem("Too many requests from this computer. Try again later.", statusCode: 429);
            if (await db.AccessRequests.AnyAsync(x => x.DeviceId == deviceId && x.Status == "pending" && x.Kind == kind && x.Value == value))
                return Results.Ok(new { duplicate = true });
            db.AccessRequests.Add(new AccessRequest
            {
                DeviceId = deviceId, User = r.User?.Trim() is { Length: > 0 and <= 120 } u ? u : null, Kind = kind, Value = value,
                Reason = r.Reason?.Trim() is { Length: > 0 } reason ? reason[..Math.Min(500, reason.Length)] : null,
            });
            db.Events.Add(new DeviceEvent { DeviceId = deviceId, Time = DateTime.UtcNow, Type = "block.accessRequested", Severity = "info",
                Message = $"{r.User ?? "A user"} asked for access to {value}" });
            await db.SaveChangesAsync();
            return Results.Ok(new { duplicate = false });
        }).RequireAuthorization("Device").RequireRateLimiting("agent");

        // ───────────── offline unlock code ─────────────
        app.MapPost("/api/devices/{id:guid}/unlock-code", async (Guid id, UnlockCodeRequest r, AppDb db, AccessScope scope, ServerKeys keys, AuditService audit) =>
        {
            if (!await scope.CanAccessDeviceAsync(id)) return Results.Forbid();
            if (!UnlockCodes.Durations.Contains(r.Hours)) return Results.Problem("Choose 1, 4 or 8 hours", statusCode: 400);
            var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
            if (device is null) return Results.NotFound();
            var now = DateTime.UtcNow;
            var code = UnlockCodes.Create(id, keys.DeviceSecret(id), UnlockCodes.Window(now), r.Hours);
            // valid in this hour window and the next one (the agent also accepts the previous window)
            var validUntil = DateTimeOffset.FromUnixTimeSeconds((UnlockCodes.Window(now) + 2) * 3600).UtcDateTime;
            await audit.LogAsync("device.unlockCode", device.Hostname, $"{r.Hours} h");
            return Results.Ok(new { code, hours = r.Hours, validUntil });
        }).RequireAuthorization("Write");

        // ───────────── emergency "Unblock all" ─────────────
        super.MapGet("/emergency", async (AppDb db) =>
        {
            var now = DateTime.UtcNow;
            var units = await db.OrgUnits.AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.Name);
            var rows = await db.EmergencyUnblocks.AsNoTracking().Where(e => e.EndedAt == null).ToListAsync();
            return Results.Ok(rows.Where(e => e.UntilUtc is null || e.UntilUtc > now).Select(e => new
            {
                e.Id, e.OrgUnitId, Scope = e.OrgUnitId is { } u ? units.GetValueOrDefault(u) : "All computers", e.UntilUtc, e.Reason, e.CreatedBy, e.CreatedAt,
            }));
        });

        super.MapPost("/emergency", async (EmergencyRequest r, AppDb db, AccessScope scope, AuditService audit, CommandService commands, PresenceStore presence) =>
        {
            // step-up: the administrator confirms with their password (and MFA code when MFA is on)
            var admin = await db.AdminUsers.FirstOrDefaultAsync(a => a.Username == scope.Username);
            if (admin is null || !Passwords.Verify(admin, r.Password ?? "")) return Results.Problem("The password is not correct", statusCode: 400);
            if (admin.MfaEnabled && !Totp.Verify(admin.TotpSecret, r.Code ?? "")) return Results.Problem("The MFA code is not correct", statusCode: 400);
            if (r.UntilUtc is { } u && u <= DateTime.UtcNow) return Results.Problem("The end time is in the past", statusCode: 400);
            var row = new EmergencyUnblock { OrgUnitId = r.OrgUnitId, UntilUtc = r.UntilUtc, Reason = r.Reason?.Trim(), CreatedBy = scope.Username };
            db.EmergencyUnblocks.Add(row);
            await db.SaveChangesAsync();
            await audit.LogAsync("emergency.unblockAll", r.OrgUnitId?.ToString() ?? "organization", $"until {r.UntilUtc?.ToString("u") ?? "turned off"} · {r.Reason}");
            await commands.BroadcastPolicyChangedAsync(presence.All.Select(x => x.DeviceId));
            return Results.Ok(new { row.Id });
        });

        super.MapDelete("/emergency/{id:guid}", async (Guid id, AppDb db, AccessScope scope, AuditService audit, CommandService commands, PresenceStore presence) =>
        {
            var row = await db.EmergencyUnblocks.FindAsync(id);
            if (row is null) return Results.NotFound();
            row.EndedAt = DateTime.UtcNow; row.EndedBy = scope.Username;
            await db.SaveChangesAsync();
            await audit.LogAsync("emergency.end", row.OrgUnitId?.ToString() ?? "organization");
            await commands.BroadcastPolicyChangedAsync(presence.All.Select(x => x.DeviceId));
            return Results.Ok();
        });

        // ───────────── USB drives seen recently (for the allow list) ─────────────
        app.MapGet("/api/usb/recent-drives", async (AppDb db) =>
        {
            var since = DateTime.UtcNow.AddDays(-30);
            var evs = await db.Events.AsNoTracking().Where(e => e.Type == "usb.inserted" && e.Time > since && e.DataJson != null)
                .OrderByDescending(e => e.Time).Take(2000).Select(e => new { e.DeviceId, e.Time, e.DataJson }).ToListAsync();
            var hosts = await db.Devices.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Hostname);
            var drives = new Dictionary<string, (string Model, string Host, DateTime Time)>();
            foreach (var e in evs)
            {
                Dictionary<string, string>? data = null;
                try { data = Json.Deserialize<Dictionary<string, string>>(e.DataJson!); } catch { }
                var serial = data?.GetValueOrDefault("serial")?.Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(serial) || drives.ContainsKey(serial)) continue;
                drives[serial] = (data?.GetValueOrDefault("model") ?? "", hosts.GetValueOrDefault(e.DeviceId) ?? "", e.Time);
            }
            return Results.Ok(drives.Take(100).Select(kv => new { serial = kv.Key, model = kv.Value.Model, lastHost = kv.Value.Host, lastSeen = kv.Value.Time }));
        }).RequireAuthorization("Console");
    }
}
