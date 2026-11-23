using System.Text.RegularExpressions;

namespace SentinelDesk.Shared;

/// <summary>A website rule: a host (subdomains included) and an optional path prefix.</summary>
public sealed record UrlRule(string Host, string Path)
{
    public bool HostOnly => Path.Length == 0;
    public override string ToString() => Host + Path;
}

/// <summary>Parsing and validation of block rules, shared by the server (validation) and the agent (enforcement).</summary>
public static partial class BlockRules
{
    public const int MaxApps = 500;
    public const int MaxUrls = 2000;

    /// <summary>Windows and SentinelDesk processes that may never be blocked.</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "lsaiso.exe", "svchost.exe",
        "explorer.exe", "dwm.exe", "fontdrvhost.exe", "sihost.exe", "ctfmon.exe", "taskhostw.exe", "runtimebroker.exe",
        "logonui.exe", "userinit.exe", "conhost.exe", "consent.exe", "searchhost.exe", "startmenuexperiencehost.exe",
        "shellexperiencehost.exe", "textinputhost.exe", "securityhealthservice.exe", "msmpeng.exe", "wuauclt.exe",
        "trustedinstaller.exe", "tiworker.exe", "dllhost.exe", "rundll32.exe", "msiexec.exe", "spoolsv.exe", "audiodg.exe",
        "sentineldesk.agent.exe", "sentineldesk.guard.exe", "sentineldesk.tray.exe", "uninstall.exe",
    };

    /// <summary>Domains that would break Windows, sign-in or SentinelDesk itself if blocked.</summary>
    private static readonly string[] ProtectedDomains =
        ["microsoft.com", "windows.com", "windowsupdate.com", "microsoftonline.com", "live.com", "msftconnecttest.com", "digicert.com", "localhost"];

    /// <summary>Normalises an app rule to a lower-case executable name. Returns null and a reason when it is not valid.</summary>
    public static string? NormalizeApp(string raw, out string? error)
    {
        error = null;
        var s = raw.Trim().Trim('"').Replace('/', '\\');
        if (s.Contains('\\')) s = s[(s.LastIndexOf('\\') + 1)..];   // a full path was pasted: keep the file name
        s = s.ToLowerInvariant();
        if (s.Length == 0) { error = "empty"; return null; }
        if (!s.EndsWith(".exe") && !s.EndsWith('*')) s += ".exe";
        if (s.Length > 100 || !AppRegex().IsMatch(s)) { error = $"'{raw}' is not a valid program name (e.g. steam.exe)"; return null; }
        if (s.Trim('*', '.').Replace("exe", "").Length < 2) { error = $"'{raw}' would match too many programs"; return null; }
        if (Protected.Contains(s) || (s.Contains('*') && Protected.Any(p => WildcardMatch(p, s)))) { error = $"'{raw}' is a Windows or SentinelDesk program and cannot be blocked"; return null; }
        return s;
    }

    /// <summary>Normalises a website rule ("https://www.facebook.com/", "*.tiktok.com", "youtube.com/shorts").</summary>
    public static UrlRule? NormalizeUrl(string raw, out string? error)
    {
        error = null;
        var s = raw.Trim().ToLowerInvariant();
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        if (s.StartsWith("*.")) s = s[2..];
        var slash = s.IndexOfAny(['/', '?', '#']);
        var host = (slash >= 0 ? s[..slash] : s).TrimEnd('.');
        var path = slash >= 0 ? s[slash..].TrimEnd('/', '*') : "";
        if (path is "?" or "#") path = "";
        var colon = host.LastIndexOf(':');
        if (colon > 0 && host[(colon + 1)..].All(char.IsAsciiDigit)) host = host[..colon];   // drop a port
        if (host.StartsWith("www.")) host = host[4..];
        if (host.Length == 0 || host.Length > 253 || !HostRegex().IsMatch(host) || !host.Contains('.'))
        { error = $"'{raw}' is not a valid website (e.g. facebook.com or youtube.com/shorts)"; return null; }
        if (path.Length > 200) { error = $"'{raw}' is too long"; return null; }
        if (ProtectedDomains.Any(p => host == p || host.EndsWith("." + p)))
        { error = $"'{raw}' is needed by Windows or sign-in and cannot be blocked"; return null; }
        return new UrlRule(host, path);
    }

    /// <summary>
    /// Big sites load their pages, videos and scripts from other domains. Blocking the main site also blocks these, so an
    /// open tab, an installed web app or a cached page stops working too. Only applied to whole-site rules (no path).
    /// </summary>
    private static readonly Dictionary<string, string[]> Related = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tiktok.com"] = ["tiktokcdn.com", "tiktokcdn-us.com", "tiktokcdn-eu.com", "tiktokv.com", "tiktokv.us", "tiktokv.eu", "tiktokw.us", "tiktokw.eu", "ttwstatic.com", "byteoversea.com", "ibytedtos.com", "ibyteimg.com", "muscdn.com", "tiktok.org"],
        ["facebook.com"] = ["fbcdn.net", "facebook.net", "fb.com", "fb.me", "fbsbx.com", "messenger.com"],
        ["instagram.com"] = ["cdninstagram.com", "ig.me"],
        ["youtube.com"] = ["googlevideo.com", "ytimg.com", "youtu.be", "youtube-nocookie.com", "youtubei.googleapis.com"],
        ["x.com"] = ["twitter.com", "twimg.com", "t.co"],
        ["twitter.com"] = ["x.com", "twimg.com", "t.co"],
        ["netflix.com"] = ["nflxvideo.net", "nflximg.net", "nflxext.com", "nflxso.net"],
        ["whatsapp.com"] = ["whatsapp.net", "wa.me"],
        ["snapchat.com"] = ["sc-cdn.net", "snap.com"],
        ["reddit.com"] = ["redd.it", "redditmedia.com", "redditstatic.com"],
        ["spotify.com"] = ["scdn.co", "spotifycdn.com"],
        ["discord.com"] = ["discord.gg", "discordapp.com", "discordapp.net", "discord.media"],
        ["telegram.org"] = ["t.me", "telegram.me"],
        ["twitch.tv"] = ["ttvnw.net", "jtvnw.net"],
        ["linkedin.com"] = ["licdn.com"],
        ["pinterest.com"] = ["pinimg.com"],
        ["threads.net"] = ["threads.com"],
        ["primevideo.com"] = ["aiv-cdn.net", "aiv-delivery.net"],
        ["disneyplus.com"] = ["disney-plus.net", "dssott.com"],
        ["roblox.com"] = ["rbxcdn.com"],
        ["steampowered.com"] = ["steamcommunity.com", "steamstatic.com", "steamserver.net"],
        ["epicgames.com"] = ["epicgames.dev", "unrealengine.com"],
    };

    /// <summary>The related domains that are blocked together with a whole-site rule.</summary>
    public static IEnumerable<string> RelatedTo(string host) => Related.TryGetValue(host, out var r) ? r : [];

    /// <summary>All website rules of a policy, including the domains of blocked categories and related domains, de-duplicated.</summary>
    public static List<UrlRule> UrlRules(BlockSettings b)
    {
        var rules = new List<UrlRule>();
        foreach (var u in b.Urls) if (NormalizeUrl(u, out _) is { } r) rules.Add(r);
        foreach (var c in b.Categories) foreach (var d in Domains.InCategory(c)) if (NormalizeUrl(d, out _) is { } r) rules.Add(r);
        foreach (var r in rules.Where(r => r.HostOnly).ToList())
            foreach (var d in RelatedTo(r.Host)) if (NormalizeUrl(d, out _) is { } rel) rules.Add(rel);
        // a host-only rule already covers every path rule on the same host or its subdomains
        var hosts = rules.Where(r => r.HostOnly).Select(r => r.Host).ToHashSet();
        return rules.Distinct()
            .Where(r => r.HostOnly || !hosts.Any(h => r.Host == h || r.Host.EndsWith("." + h)))
            .OrderBy(r => r.Host, StringComparer.Ordinal).ThenBy(r => r.Path, StringComparer.Ordinal).ToList();
    }

    public static List<string> AppRules(BlockSettings b) =>
        b.Apps.Select(a => NormalizeApp(a, out _)).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList();

    /// <summary>Validates and normalises a policy's block settings in place. Returns the problems found (empty = valid).</summary>
    public static List<string> Sanitize(BlockSettings b)
    {
        var errors = new List<string>();
        var apps = new List<string>();
        foreach (var a in b.Apps.Where(a => !string.IsNullOrWhiteSpace(a)))
            if (NormalizeApp(a, out var e) is { } n) { if (!apps.Contains(n)) apps.Add(n); } else errors.Add(e!);
        var urls = new List<string>();
        foreach (var u in b.Urls.Where(u => !string.IsNullOrWhiteSpace(u)))
            if (NormalizeUrl(u, out var e) is { } r) { if (!urls.Contains(r.ToString())) urls.Add(r.ToString()); } else errors.Add(e!);
        var cats = new List<string>();
        foreach (var c in b.Categories)
            if (Domains.BlockableCategories.FirstOrDefault(x => x.Equals(c, StringComparison.OrdinalIgnoreCase)) is { } cat) { if (!cats.Contains(cat)) cats.Add(cat); }
            else errors.Add($"'{c}' is not a website category that can be blocked");
        if (apps.Count > MaxApps) errors.Add($"At most {MaxApps} blocked applications");
        if (urls.Count > MaxUrls) errors.Add($"At most {MaxUrls} blocked websites");
        b.Apps = apps; b.Urls = urls; b.Categories = cats;
        b.Message = string.IsNullOrWhiteSpace(b.Message) ? new BlockSettings().Message : b.Message.Trim()[..Math.Min(300, b.Message.Trim().Length)];
        return errors;
    }

    /// <summary>True when the host (e.g. from a DNS lookup) is covered by a host-only rule.</summary>
    public static bool HostBlocked(IReadOnlyCollection<UrlRule> rules, string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        foreach (var r in rules) if (r.HostOnly && (host == r.Host || host.EndsWith("." + r.Host))) return true;
        return false;
    }

    /// <summary>Case-insensitive match of a process name against a rule with optional "*" wildcards.</summary>
    public static bool WildcardMatch(string name, string pattern)
    {
        if (!pattern.Contains('*')) return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
        var rx = "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$";
        return Regex.IsMatch(name, rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    [GeneratedRegex(@"^[a-z0-9 _\-\.\(\)\+\*&]+$")]
    private static partial Regex AppRegex();

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9\-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9\-]*[a-z0-9])?)*$")]
    private static partial Regex HostRegex();
}
