using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SentinelDesk.Server.Data;

namespace SentinelDesk.Server.Security;

public sealed class JwtService(ServerKeys keys, IConfiguration config)
{
    public const string Issuer = "sentineldesk";

    public SymmetricSecurityKey Key => new(keys.JwtKey());

    public string Issue(AdminUser u, bool mfaPending = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, u.Id.ToString()),
            new(ClaimTypes.Name, u.Username),
            new("display_name", u.DisplayName),
            new(ClaimTypes.Role, mfaPending ? "MfaPending" : u.Role),
        };
        if (u.OrgUnitScope is { } scope) claims.Add(new Claim("org_scope", scope.ToString()));
        var minutes = mfaPending ? 5 : config.GetValue("Security:SessionMinutes", 480);
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(minutes),
            signingCredentials: new SigningCredentials(Key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public static class Passwords
{
    private static readonly PasswordHasher<AdminUser> Hasher = new();
    public static string Hash(AdminUser u, string password) => Hasher.HashPassword(u, password);
    public static bool Verify(AdminUser u, string password) =>
        Hasher.VerifyHashedPassword(u, u.PasswordHash, password) != PasswordVerificationResult.Failed;

    public static string? Validate(string password) =>
        password.Length < 10 ? "Password must be at least 10 characters"
        : !password.Any(char.IsUpper) || !password.Any(char.IsLower) || !password.Any(char.IsDigit)
            ? "Password needs upper, lower case letters and a digit" : null;
}

/// <summary>RFC 6238 TOTP (30 s, 6 digits, SHA-1) — compatible with Microsoft/Google Authenticator.</summary>
public static class Totp
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string NewSecret() => Base32(RandomNumberGenerator.GetBytes(20));

    public static string Uri(string secret, string user) =>
        $"otpauth://totp/SentinelDesk:{System.Uri.EscapeDataString(user)}?secret={secret}&issuer=SentinelDesk&digits=6&period=30";

    public static bool Verify(string secret, string code)
    {
        code = code.Replace(" ", "");
        if (code.Length != 6) return false;
        var key = FromBase32(secret);
        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        for (var i = -1; i <= 1; i++)
            if (Compute(key, step + i) == code) return true;
        return false;
    }

    private static string Compute(byte[] key, long counter)
    {
        var msg = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(msg);
        using var h = new HMACSHA1(key);
        var hash = h.ComputeHash(msg);
        var o = hash[^1] & 0xF;
        var bin = ((hash[o] & 0x7F) << 24) | (hash[o + 1] << 16) | (hash[o + 2] << 8) | hash[o + 3];
        return (bin % 1_000_000).ToString("D6");
    }

    private static string Base32(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    private static byte[] FromBase32(string s)
    {
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in s.TrimEnd('=').ToUpperInvariant())
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(c); bits += 5;
            if (bits >= 8) { output.Add((byte)(buffer >> (bits - 8))); bits -= 8; }
        }
        return output.ToArray();
    }
}

/// <summary>Resolves which org units (and therefore devices) the current admin may see.</summary>
public sealed class AccessScope(AppDb db, IHttpContextAccessor http)
{
    private HashSet<Guid>? _units;
    private bool _resolved;

    public ClaimsPrincipal User => http.HttpContext!.User;
    public string Username => User.Identity?.Name ?? "system";
    public string Role => User.FindFirstValue(ClaimTypes.Role) ?? "";
    public bool CanSeeWeb => Role != Roles.HrViewer;
    public bool CanSeeTitles => Role is Roles.SuperAdmin or Roles.BranchAdmin;
    public bool CanWrite => Role is Roles.SuperAdmin or Roles.BranchAdmin;
    public bool IsSuper => Role == Roles.SuperAdmin;

    /// <summary>null = unrestricted.</summary>
    public async Task<HashSet<Guid>?> UnitsAsync()
    {
        if (_resolved) return _units;
        _resolved = true;
        var scope = User.FindFirstValue("org_scope");
        if (IsSuper || scope is null || !Guid.TryParse(scope, out var root)) return _units = null;
        var all = await db.OrgUnits.AsNoTracking().Select(o => new { o.Id, o.ParentId }).ToListAsync();
        var set = new HashSet<Guid> { root };
        bool added;
        do
        {
            added = false;
            foreach (var o in all)
                if (o.ParentId is { } p && set.Contains(p) && set.Add(o.Id)) added = true;
        } while (added);
        return _units = set;
    }

    public async Task<IQueryable<Device>> DevicesAsync()
    {
        var units = await UnitsAsync();
        var q = db.Devices.AsNoTracking();
        return units is null ? q : q.Where(d => d.OrgUnitId != null && units.Contains(d.OrgUnitId.Value));
    }

    public async Task<bool> CanAccessDeviceAsync(Guid deviceId)
    {
        var units = await UnitsAsync();
        if (units is null) return true;
        var d = await db.Devices.AsNoTracking().Where(x => x.Id == deviceId).Select(x => x.OrgUnitId).FirstOrDefaultAsync();
        return d is { } u && units.Contains(u);
    }
}
