using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SentinelDesk.Server.Data;
using SentinelDesk.Server.Security;
using SentinelDesk.Server.Services;

namespace SentinelDesk.Server.Endpoints;

public static class AuthEndpoints
{
    public sealed record LoginRequest(string Username, string Password);
    public sealed record CodeRequest(string Code);
    public sealed record ChangePasswordRequest(string Current, string Next);

    public static void MapAuthApi(this WebApplication app)
    {
        var g = app.MapGroup("/api/auth");

        g.MapPost("/login", async (LoginRequest req, AppDb db, JwtService jwt, AuditService audit, IConfiguration config) =>
        {
            var user = await db.AdminUsers.FirstOrDefaultAsync(u => u.Username == req.Username.Trim().ToLower());
            if (user is null || user.Disabled)
            {
                await audit.LogAsync("auth.failed", req.Username);
                return Results.Problem("Invalid username or password", statusCode: 401);
            }
            if (user.LockedUntil > DateTime.UtcNow)
                return Results.Problem($"Account locked. Try again after {user.LockedUntil:HH:mm} UTC", statusCode: 423);
            if (!Passwords.Verify(user, req.Password))
            {
                user.FailedLogins++;
                if (user.FailedLogins >= 5) { user.LockedUntil = DateTime.UtcNow.AddMinutes(15); user.FailedLogins = 0; }
                await db.SaveChangesAsync();
                await audit.LogAsync("auth.failed", user.Username);
                return Results.Problem("Invalid username or password", statusCode: 401);
            }
            user.FailedLogins = 0;
            await db.SaveChangesAsync();

            if (user.MfaEnabled)
                return Results.Ok(new { mfaRequired = true, token = jwt.Issue(user, mfaPending: true) });

            user.LastLoginAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await audit.LogAsync("auth.login", user.Username);
            return Results.Ok(new
            {
                mfaRequired = false, token = jwt.Issue(user),
                mfaSetupRecommended = config.GetValue("Security:RequireMfa", false) && !user.MfaEnabled
            });
        }).RequireRateLimiting("login");

        g.MapPost("/mfa", async (CodeRequest req, ClaimsPrincipal principal, AppDb db, JwtService jwt, AuditService audit) =>
        {
            var user = await db.AdminUsers.FindAsync(Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!));
            if (user is null || user.TotpSecret is null) return Results.Unauthorized();
            if (!Totp.Verify(user.TotpSecret, req.Code))
            {
                await audit.LogAsync("auth.mfaFailed", user.Username);
                return Results.Problem("Invalid verification code", statusCode: 401);
            }
            user.LastLoginAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await audit.LogAsync("auth.login", user.Username, "mfa");
            return Results.Ok(new { mfaRequired = false, token = jwt.Issue(user) });
        }).RequireAuthorization("MfaPending").RequireRateLimiting("login");

        var me = g.MapGroup("").RequireAuthorization("Console");

        me.MapGet("/me", async (ClaimsPrincipal principal, AppDb db) =>
        {
            var user = await db.AdminUsers.AsNoTracking().FirstAsync(u => u.Id == Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!));
            var scopeName = user.OrgUnitScope is { } s ? (await db.OrgUnits.FindAsync(s))?.Name : null;
            return Results.Ok(new
            {
                user.Id, user.Username, user.DisplayName, user.Email, user.Role, user.MfaEnabled, user.MustChangePassword,
                user.OrgUnitScope, ScopeName = scopeName, user.LastLoginAt
            });
        });

        me.MapPost("/change-password", async (ChangePasswordRequest req, ClaimsPrincipal principal, AppDb db, AuditService audit) =>
        {
            var user = await db.AdminUsers.FirstAsync(u => u.Id == Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!));
            if (!Passwords.Verify(user, req.Current)) return Results.Problem("Current password is incorrect", statusCode: 400);
            if (Passwords.Validate(req.Next) is { } err) return Results.Problem(err, statusCode: 400);
            user.PasswordHash = Passwords.Hash(user, req.Next);
            user.MustChangePassword = false;
            await db.SaveChangesAsync();
            await audit.LogAsync("auth.passwordChanged", user.Username);
            return Results.Ok();
        });

        me.MapPost("/mfa/setup", async (ClaimsPrincipal principal, AppDb db) =>
        {
            var user = await db.AdminUsers.FirstAsync(u => u.Id == Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!));
            if (user.MfaEnabled) return Results.Problem("MFA is already enabled", statusCode: 400);
            user.TotpSecret = Totp.NewSecret();
            await db.SaveChangesAsync();
            return Results.Ok(new { secret = user.TotpSecret, uri = Totp.Uri(user.TotpSecret, user.Username) });
        });

        me.MapPost("/mfa/enable", async (CodeRequest req, ClaimsPrincipal principal, AppDb db, AuditService audit) =>
        {
            var user = await db.AdminUsers.FirstAsync(u => u.Id == Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!));
            if (user.TotpSecret is null || !Totp.Verify(user.TotpSecret, req.Code)) return Results.Problem("Invalid code", statusCode: 400);
            user.MfaEnabled = true;
            await db.SaveChangesAsync();
            await audit.LogAsync("auth.mfaEnabled", user.Username);
            return Results.Ok();
        });

        me.MapPost("/mfa/disable", async (CodeRequest req, ClaimsPrincipal principal, AppDb db, AuditService audit) =>
        {
            var user = await db.AdminUsers.FirstAsync(u => u.Id == Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!));
            if (user.TotpSecret is null || !Totp.Verify(user.TotpSecret, req.Code)) return Results.Problem("Invalid code", statusCode: 400);
            user.MfaEnabled = false;
            user.TotpSecret = null;
            await db.SaveChangesAsync();
            await audit.LogAsync("auth.mfaDisabled", user.Username);
            return Results.Ok();
        });
    }
}
