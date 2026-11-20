namespace SentinelDesk.Shared;

/// <summary>Domain normalisation + lightweight built-in URL categorisation.</summary>
public static class Domains
{
    private static readonly HashSet<string> SecondLevel = new(StringComparer.OrdinalIgnoreCase)
    {
        "com.lk","gov.lk","ac.lk","edu.lk","org.lk","net.lk","co.uk","org.uk","ac.uk","com.au","co.in","com.sg","co.jp"
    };

    public static string Registrable(string host)
    {
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        var labels = host.Split('.');
        if (labels.Length <= 2) return host;
        var lastTwo = $"{labels[^2]}.{labels[^1]}";
        return SecondLevel.Contains(lastTwo) && labels.Length >= 3 ? $"{labels[^3]}.{lastTwo}" : lastTwo;
    }

    public static bool Matches(string domain, string pattern)
    {
        pattern = pattern.Trim().ToLowerInvariant();
        domain = domain.ToLowerInvariant();
        if (pattern.StartsWith("*.")) return domain == pattern[2..] || domain.EndsWith(pattern[1..]);
        return domain == pattern;
    }

    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // Social
        ["facebook.com"] = "Social", ["fbcdn.net"] = "Social", ["instagram.com"] = "Social", ["tiktok.com"] = "Social",
        ["twitter.com"] = "Social", ["x.com"] = "Social", ["twimg.com"] = "Social", ["linkedin.com"] = "Social",
        ["reddit.com"] = "Social", ["snapchat.com"] = "Social", ["pinterest.com"] = "Social", ["threads.net"] = "Social",
        // Messaging
        ["whatsapp.com"] = "Messaging", ["whatsapp.net"] = "Messaging", ["telegram.org"] = "Messaging", ["viber.com"] = "Messaging",
        ["messenger.com"] = "Messaging", ["discord.com"] = "Messaging", ["signal.org"] = "Messaging",
        // Streaming
        ["youtube.com"] = "Streaming", ["googlevideo.com"] = "Streaming", ["ytimg.com"] = "Streaming", ["netflix.com"] = "Streaming",
        ["nflxvideo.net"] = "Streaming", ["spotify.com"] = "Streaming", ["twitch.tv"] = "Streaming", ["primevideo.com"] = "Streaming",
        ["disneyplus.com"] = "Streaming", ["soundcloud.com"] = "Streaming",
        // Productivity / Work
        ["office.com"] = "Productivity", ["office365.com"] = "Productivity", ["sharepoint.com"] = "Productivity",
        ["microsoftonline.com"] = "Productivity", ["teams.microsoft.com"] = "Productivity", ["outlook.com"] = "Email",
        ["live.com"] = "Email", ["gmail.com"] = "Email", ["zoom.us"] = "Productivity", ["slack.com"] = "Productivity",
        ["atlassian.net"] = "Productivity", ["notion.so"] = "Productivity", ["github.com"] = "Development",
        ["stackoverflow.com"] = "Development", ["gitlab.com"] = "Development", ["npmjs.com"] = "Development", ["nuget.org"] = "Development",
        ["gov.lk"] = "Government", ["ac.lk"] = "Education", ["lk"] = "Local",
        // Cloud storage
        ["dropbox.com"] = "Cloud Storage", ["onedrive.com"] = "Cloud Storage", ["box.com"] = "Cloud Storage", ["wetransfer.com"] = "Cloud Storage",
        ["mega.nz"] = "Cloud Storage",
        // Search / AI
        ["google.com"] = "Search", ["bing.com"] = "Search", ["duckduckgo.com"] = "Search", ["chatgpt.com"] = "AI Tools",
        ["openai.com"] = "AI Tools", ["claude.ai"] = "AI Tools", ["gemini.google.com"] = "AI Tools",
        // News
        ["bbc.com"] = "News", ["cnn.com"] = "News", ["dailymirror.lk"] = "News", ["adaderana.lk"] = "News", ["newsfirst.lk"] = "News",
        ["sundaytimes.lk"] = "News", ["lankadeepa.lk"] = "News", ["hirunews.lk"] = "News",
        // Shopping
        ["amazon.com"] = "Shopping", ["ebay.com"] = "Shopping", ["daraz.lk"] = "Shopping", ["aliexpress.com"] = "Shopping", ["ikman.lk"] = "Shopping",
        // Gaming
        ["steampowered.com"] = "Gaming", ["roblox.com"] = "Gaming", ["epicgames.com"] = "Gaming", ["miniclip.com"] = "Gaming",
        // System / background
        ["windowsupdate.com"] = "System", ["microsoft.com"] = "System", ["msftconnecttest.com"] = "System", ["windows.com"] = "System",
        ["msedge.net"] = "System", ["azureedge.net"] = "System", ["akamaiedge.net"] = "System", ["gstatic.com"] = "System",
        ["googleapis.com"] = "System", ["digicert.com"] = "System", ["cloudflare.com"] = "System", ["akamaized.net"] = "System",
        ["windows.net"] = "System", ["bing.net"] = "System", ["skype.com"] = "Productivity", ["local"] = "System", ["arpa"] = "System",
    };

    public static string Categorize(string domain)
    {
        domain = domain.ToLowerInvariant();
        if (Map.TryGetValue(domain, out var c)) return c;
        var reg = Registrable(domain);
        if (Map.TryGetValue(reg, out c)) return c;
        var tld2 = domain.Split('.') is { Length: >= 2 } l ? $"{l[^2]}.{l[^1]}" : domain;
        if (Map.TryGetValue(tld2, out c)) return c;
        if (domain.EndsWith(".lk")) return "Local";
        return "Uncategorized";
    }
}
