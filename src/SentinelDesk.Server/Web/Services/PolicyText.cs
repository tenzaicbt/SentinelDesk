using SentinelDesk.Shared;

namespace SentinelDesk.Server.Web.Services;

public sealed record KindInfo(string Label, string Short, string Text, string Placeholder, string Icon);

/// <summary>Block policies: the three kinds of restriction the console manages, and how they are described.</summary>
public static class PolicyText
{
    public static readonly string[] KindOrder = ["websites", "apps", "usb"];

    public static readonly Dictionary<string, KindInfo> Kinds = new()
    {
        ["websites"] = new("Block websites", "Websites", "Stop websites from opening, by address or by type (social media, games…).", "e.g. Block social media", "Globe"),
        ["apps"] = new("Block applications", "Applications", "Stop programs from running, such as games or file-sharing apps.", "e.g. Block games", "AppWindow"),
        ["usb"] = new("Block USB drives", "USB drives", "Stop pen drives and external hard disks from being used.", "e.g. No USB at cashier counters", "Usb"),
    };

    public static KindInfo Kind(string kind) => Kinds.TryGetValue(kind, out var k) ? k : Kinds["websites"];

    public static readonly string[] Days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    public static BlockSchedule WorkingHours() => new() { Enabled = true, Days = ["Mon", "Tue", "Wed", "Thu", "Fri"], Start = "08:30", End = "17:00" };

    /// <summary>Must match Domains.BlockableCategories on the server.</summary>
    public static readonly (string Name, string Examples)[] Categories =
    [
        ("Social", "Facebook, Instagram, TikTok, X"),
        ("Messaging", "WhatsApp, Telegram, Discord"),
        ("Streaming", "YouTube, Netflix, Spotify"),
        ("Gaming", "Steam, Epic Games, Roblox"),
        ("Shopping", "Daraz, Amazon, ikman"),
        ("News", "BBC, Daily Mirror, Ada Derana"),
        ("AI Tools", "ChatGPT, Claude, Gemini"),
        ("Cloud Storage", "Dropbox, WeTransfer, MEGA"),
    ];

    /// <summary>What a policy blocks, in a few words.</summary>
    public static string Summary(PolicyRow p)
    {
        var b = p.Settings.Block ?? new BlockSettings();
        var extra = new[]
        {
            b.Mode == "warn" ? "warn only" : b.Mode == "log" ? "log only" : "",
            b.Schedule?.Enabled == true ? $"{b.Schedule.Start}–{b.Schedule.End}" : "",
            b.Users?.Count > 0 ? $"{b.Users.Count} user/group{(b.Users.Count == 1 ? "" : "s")}" : "",
        }.Where(x => x != "").ToList();
        var main = MainSummary(p.Kind, b);
        return extra.Count > 0 ? $"{main} · {string.Join(" · ", extra)}" : main;
    }

    private static string MainSummary(string kind, BlockSettings b)
    {
        switch (kind)
        {
            case "websites":
                var parts = new[] { b.Urls.Count > 0 ? Fmt.Plural(b.Urls.Count, "website", "websites") : "", string.Join(", ", b.Categories) }.Where(x => x != "").ToList();
                return parts.Count > 0 ? $"Blocks {string.Join(" and ", parts)}" : "No websites added yet";
            case "apps":
                return b.Apps.Count > 0 ? $"Blocks {Fmt.Plural(b.Apps.Count, "application", "applications")}" : "No applications added yet";
            default:
                return b.UsbMode == "readOnly" ? "USB drives read-only"
                    : $"Blocks USB drives{(b.UsbAllowSerials?.Count > 0 ? $" ({b.UsbAllowSerials.Count} allowed)" : "")}";
        }
    }

    public static readonly Dictionary<string, string> RequestKindLabel = new() { ["websites"] = "Website", ["apps"] = "Application", ["usb"] = "USB drive" };
}

/// <summary>"For how long" choices for temporary blocks, exceptions and emergency unblocks.</summary>
public static class Durations
{
    public static readonly (string Value, string Label)[] Options =
        [("always", "Always"), ("1h", "1 hour"), ("today", "Until the end of today"), ("date", "Until a date…")];

    /// <summary>End time (UTC) for a choice: null = until removed; false = no valid future date chosen yet.</summary>
    public static (bool Ok, DateTime? Until) UntilFrom(string choice, string date, Fmt fmt)
    {
        switch (choice)
        {
            case "always": return (true, null);
            case "1h": return (true, DateTime.UtcNow.AddHours(1));
            case "today": return (true, fmt.ToUtc(fmt.Now.Date.AddHours(23).AddMinutes(59)));
            default:
                if (DateTime.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var local))
                {
                    var utc = fmt.ToUtc(local);
                    if (utc > DateTime.UtcNow) return (true, utc);
                }
                return (false, null);
        }
    }
}
