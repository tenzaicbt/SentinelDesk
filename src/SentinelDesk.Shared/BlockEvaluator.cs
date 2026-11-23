using System.Security.Cryptography;
using System.Text;

namespace SentinelDesk.Shared;

/// <summary>What a device must enforce right now, after schedules, users, expiry, exceptions and modes are applied.</summary>
public sealed class ActiveBlocks
{
    public List<UrlRule> BlockUrls { get; init; } = [];
    public List<UrlRule> WarnUrls { get; init; } = [];
    public List<UrlRule> LogUrls { get; init; } = [];
    public List<string> BlockApps { get; init; } = [];
    public List<string> WarnApps { get; init; } = [];
    public List<string> LogApps { get; init; } = [];
    /// <summary>Programs allowed despite a (wildcard) rule, e.g. after an approved access request.</summary>
    public List<string> AllowApps { get; init; } = [];
    /// <summary>none | readOnly | block (strictest active USB policy).</summary>
    public string UsbMode { get; init; } = "none";
    public List<string> UsbAllowSerials { get; init; } = [];
    public bool NotifyUser { get; init; } = true;
    public string Message { get; init; } = new BlockSettings().Message;

    /// <summary>A stable fingerprint, so the agent only re-applies when the result really changed.</summary>
    public string Signature => string.Join("|",
        string.Join(",", BlockUrls), string.Join(",", WarnUrls), string.Join(",", LogUrls),
        string.Join(",", BlockApps), string.Join(",", WarnApps), string.Join(",", LogApps), string.Join(",", AllowApps),
        UsbMode, string.Join(",", UsbAllowSerials), NotifyUser, Message);
}

/// <summary>
/// Turns the block policies delivered to a device into what is enforced at a given moment. Pure logic, shared by the
/// agent (enforcement, also offline) and tests.
/// </summary>
public static class BlockEvaluator
{
    private static readonly string[] DayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    /// <param name="nowUtc">Server-corrected current time.</param>
    /// <param name="localNow">The same moment in the schedule time zone.</param>
    /// <param name="userNames">The signed-in user ("DOMAIN\user") and their group names; empty when nobody is signed in.</param>
    /// <param name="unlockedUntilUtc">Set while an offline unlock code is in effect (nothing is blocked).</param>
    public static ActiveBlocks Evaluate(BlockSettings b, bool legacyUsbBlock, DateTime nowUtc, DateTime localNow,
        IReadOnlyCollection<string> userNames, DateTime? unlockedUntilUtc = null)
    {
        if (unlockedUntilUtc is { } u && u > nowUtc) return new ActiveBlocks();

        var exceptions = b.Exceptions.Where(e => e.ExpiresUtc is null || e.ExpiresUtc > nowUtc).ToList();
        if (exceptions.Any(e => e.PolicyId is null && e.Value is null && e.Kind == "all")) return new ActiveBlocks();

        var sets = b.Sets.Count > 0 ? b.Sets : Legacy(b);
        var urls = new Dictionary<string, List<UrlRule>> { [BlockModes.Block] = [], [BlockModes.Warn] = [], [BlockModes.Log] = [] };
        var apps = new Dictionary<string, List<string>> { [BlockModes.Block] = [], [BlockModes.Warn] = [], [BlockModes.Log] = [] };
        var usbMode = legacyUsbBlock ? "block" : "none";
        var usbAllow = new List<string>();
        var allowApps = new List<string>();
        bool? notify = null; string? message = null;

        foreach (var s in sets)
        {
            if (s.ExpiresUtc is { } exp && exp <= nowUtc) continue;
            if (!InSchedule(s.Schedule, localNow)) continue;
            if (!ForUser(s.Users, userNames)) continue;
            if (exceptions.Any(e => (e.PolicyId is null || e.PolicyId == s.PolicyId) && e.Value is null && (e.Kind == "all" || e.Kind == s.Kind))) continue;
            var mode = BlockModes.All.Contains(s.Mode) ? s.Mode : BlockModes.Block;
            switch (s.Kind)
            {
                case "websites":
                    var rules = BlockRules.UrlRules(new BlockSettings { Urls = s.Urls, Categories = s.Categories });
                    var allowed = exceptions.Where(e => (e.PolicyId is null || e.PolicyId == s.PolicyId) && e.Kind is "websites" or "all" && e.Value is not null)
                        .Select(e => BlockRules.NormalizeUrl(e.Value!, out _)).OfType<UrlRule>().ToList();
                    // an allowed site also allows its subdomains and helper domains, so an approved request really works
                    var allowedHosts = allowed.Where(a => a.HostOnly).SelectMany(a => BlockRules.RelatedTo(a.Host).Prepend(a.Host)).ToHashSet();
                    urls[mode].AddRange(rules.Where(r => !allowed.Contains(r) && !allowedHosts.Any(h => r.Host == h || r.Host.EndsWith("." + h))));
                    break;
                case "apps":
                    var allowedApps = exceptions.Where(e => (e.PolicyId is null || e.PolicyId == s.PolicyId) && e.Kind is "apps" or "all" && e.Value is not null)
                        .Select(e => BlockRules.NormalizeApp(e.Value!, out _)).OfType<string>().ToList();
                    // an exact rule for an allowed program is dropped; a wildcard rule stays and the agent skips the allowed name
                    apps[mode].AddRange(BlockRules.AppRules(new BlockSettings { Apps = s.Apps }).Where(a => !allowedApps.Contains(a)));
                    allowApps.AddRange(allowedApps);
                    if (mode == BlockModes.Block && notify is null) { notify = s.NotifyUser; message = s.Message; }
                    break;
                case "usb":
                    var m = s.UsbMode == "readOnly" ? "readOnly" : "block";
                    if (mode == BlockModes.Log) break;   // log-only USB = record insertions only (already collected)
                    if (m == "block" || usbMode == "none") usbMode = m;
                    usbAllow.AddRange(s.UsbAllowSerials);
                    break;
            }
        }
        // the same site/app in several policies: the strictest mode wins
        var bu = Distinct(urls[BlockModes.Block]);
        var wu = Distinct(urls[BlockModes.Warn]).Where(r => !bu.Contains(r)).ToList();
        var lu = Distinct(urls[BlockModes.Log]).Where(r => !bu.Contains(r) && !wu.Contains(r)).ToList();
        var ba = apps[BlockModes.Block].Distinct().Order(StringComparer.Ordinal).ToList();
        var wa = apps[BlockModes.Warn].Distinct().Where(a => !ba.Contains(a)).Order(StringComparer.Ordinal).ToList();
        var la = apps[BlockModes.Log].Distinct().Where(a => !ba.Contains(a) && !wa.Contains(a)).Order(StringComparer.Ordinal).ToList();
        if (exceptions.Any(e => e.Kind is "usb" && e.Value is null)) usbMode = "none";
        return new ActiveBlocks
        {
            BlockUrls = bu, WarnUrls = wu, LogUrls = lu, BlockApps = ba, WarnApps = wa, LogApps = la,
            AllowApps = allowApps.Distinct().Order(StringComparer.Ordinal).ToList(),
            UsbMode = usbMode,
            UsbAllowSerials = usbAllow.Select(x => x.Trim().ToUpperInvariant()).Where(x => x.Length > 0).Distinct().Order(StringComparer.Ordinal).ToList(),
            NotifyUser = notify ?? true, Message = string.IsNullOrWhiteSpace(message) ? new BlockSettings().Message : message,
        };
    }

    private static List<UrlRule> Distinct(List<UrlRule> r) =>
        r.Distinct().OrderBy(x => x.Host, StringComparer.Ordinal).ThenBy(x => x.Path, StringComparer.Ordinal).ToList();

    /// <summary>Policies from a server older than 1.2.1 (lists merged into Block directly).</summary>
    private static List<BlockRuleSet> Legacy(BlockSettings b) => b.Urls.Count + b.Categories.Count + b.Apps.Count == 0 ? [] :
        [new BlockRuleSet { PolicyId = "legacy", Kind = "websites", Urls = b.Urls, Categories = b.Categories },
         new BlockRuleSet { PolicyId = "legacy", Kind = "apps", Apps = b.Apps, NotifyUser = b.NotifyUser, Message = b.Message }];

    public static bool InSchedule(BlockSchedule s, DateTime localNow)
    {
        if (!s.Enabled) return true;
        if (!s.Days.Any(d => d.StartsWith(DayNames[(int)localNow.DayOfWeek], StringComparison.OrdinalIgnoreCase))) return false;
        if (!TimeOnly.TryParse(s.Start, out var start) || !TimeOnly.TryParse(s.End, out var end)) return true;
        var t = TimeOnly.FromDateTime(localNow);
        return start <= end ? t >= start && t < end : t >= start || t < end;   // overnight ranges, e.g. 22:00–06:00
    }

    public static bool ForUser(List<string> users, IReadOnlyCollection<string> names)
    {
        if (users.Count == 0) return true;
        foreach (var u in users)
        {
            var want = u.Trim();
            if (want.Length == 0) continue;
            foreach (var n in names)
            {
                if (string.Equals(n, want, StringComparison.OrdinalIgnoreCase)) return true;
                // "user" or "Group" without a domain matches "DOMAIN\user" / "DOMAIN\Group"
                if (!want.Contains('\\') && n.EndsWith("\\" + want, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }
}

/// <summary>
/// Offline unlock codes: the console derives a code from the device secret; the agent checks it without a connection.
/// Code = first 50 bits of HMAC-SHA256(secret, "unlock|device|hourWindow|hours"), as 10 Crockford base-32 characters.
/// </summary>
public static class UnlockCodes
{
    public static readonly int[] Durations = [1, 4, 8];
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static long Window(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds() / 3600;

    public static string Create(Guid deviceId, string secretBase64, long window, int hours)
    {
        using var h = new HMACSHA256(Convert.FromBase64String(secretBase64));
        var mac = h.ComputeHash(Encoding.UTF8.GetBytes($"unlock|{deviceId:N}|{window}|{hours}"));
        ulong v = 0;
        for (var i = 0; i < 8; i++) v = (v << 8) | mac[i];
        var sb = new StringBuilder();
        for (var i = 0; i < 10; i++) { sb.Append(Alphabet[(int)(v >> 59)]); v <<= 5; }
        return $"{sb.ToString(0, 5)}-{sb.ToString(5, 5)}";
    }

    public static string Normalize(string code) =>
        new string(code.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray()).Replace('O', '0').Replace('I', '1').Replace('L', '1');

    /// <summary>Returns the unlock duration in hours when the code is valid for this or the previous hour window.</summary>
    public static int? Check(Guid deviceId, string secretBase64, string code, DateTime nowUtc)
    {
        var want = Normalize(code);
        if (want.Length != 10) return null;
        var w = Window(nowUtc);
        foreach (var window in new[] { w, w - 1 })
            foreach (var hours in Durations)
                if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Normalize(Create(deviceId, secretBase64, window, hours))), Encoding.ASCII.GetBytes(want)))
                    return hours;
        return null;
    }
}
