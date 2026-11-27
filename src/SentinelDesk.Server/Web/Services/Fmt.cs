using System.Globalization;

namespace SentinelDesk.Server.Web.Services;

/// <summary>
/// Display formatting in the viewer's time zone and language (read from the browser when the session starts).
/// Scoped: one per browser tab.
/// </summary>
public sealed class Fmt
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public TimeZoneInfo Zone { get; private set; } = TimeZoneInfo.Local;
    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");

    public void Configure(string? timeZone, string? language)
    {
        if (!string.IsNullOrWhiteSpace(timeZone))
            try { Zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone); } catch { /* unknown zone: keep the server's */ }
        if (!string.IsNullOrWhiteSpace(language))
            try { Culture = CultureInfo.GetCultureInfo(language); } catch { /* unknown culture: keep en-US */ }
    }

    public DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone);
    public DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);
    /// <summary>A wall-clock time in the viewer's zone (e.g. from a datetime-local input) as UTC.</summary>
    public DateTime ToUtc(DateTime local) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone);

    public static string Bytes(double? n, int digits = 1)
    {
        if (n is null || double.IsNaN(n.Value)) return "—";
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        var i = 0;
        var v = Math.Abs(n.Value);
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        var d = i == 0 ? 0 : v < 10 ? digits : v < 100 ? Math.Min(digits, 1) : 0;
        return $"{v.ToString("F" + d, Inv)} {u[i]}";
    }

    public static string Bps(double? n) => n is null ? "—" : $"{Bytes(n)}/s";

    public static string Duration(double? sec)
    {
        if (sec is null || sec == 0) return "0m";
        var h = (long)Math.Floor(sec.Value / 3600);
        var m = (long)Math.Round(sec.Value % 3600 / 60, MidpointRounding.AwayFromZero);
        if (h >= 24) return $"{h / 24}d {h % 24}h";
        return h > 0 ? $"{h}h {m}m" : $"{m}m";
    }

    public string Ago(DateTime? utc)
    {
        if (utc is null) return "never";
        var s = (DateTime.UtcNow - DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc)).TotalSeconds;
        if (s < 45) return "just now";
        if (s < 3600) return $"{Math.Round(s / 60, MidpointRounding.AwayFromZero)} min ago";
        if (s < 86400) return $"{Math.Round(s / 3600, MidpointRounding.AwayFromZero)} h ago";
        if (s < 86400 * 30) return $"{Math.Round(s / 86400, MidpointRounding.AwayFromZero)} d ago";
        return Local(utc.Value).ToString(Culture.DateTimeFormat.ShortDatePattern, Culture);
    }

    /// <summary>Medium date + short time, e.g. "Nov 27, 2026, 10:30 AM".</summary>
    public string DateTimeText(DateTime? utc) => utc is null ? "—"
        : $"{Local(utc.Value).ToString("MMM d, yyyy", Culture)}, {Local(utc.Value).ToString(Culture.DateTimeFormat.ShortTimePattern, Culture)}";

    public string Time(DateTime utc) => Local(utc).ToString(Culture.DateTimeFormat.ShortTimePattern, Culture);
    public string TimeWithSeconds(DateTime utc) => Local(utc).ToString(Culture.DateTimeFormat.LongTimePattern, Culture);

    /// <summary>"yyyy-MM-dd" → "Fri 27".</summary>
    public string ShortDay(string day) =>
        System.DateTime.TryParseExact(day, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d) ? d.ToString("ddd d", Culture) : day;

    public string LongDate(DateTime local) => local.ToString("dddd, d MMMM", Culture);

    public string Num(double? n) => n is null ? "—" : n.Value.ToString("#,##0.###", Culture);
    public static string Pct(double? n) => n is null ? "—" : $"{Math.Round(n.Value, MidpointRounding.AwayFromZero)}%";

    /// <summary>"until 14:30" today, otherwise "until 27 Nov, 14:30".</summary>
    public string Until(DateTime? utc)
    {
        if (utc is null) return "";
        var t = Local(utc.Value);
        var time = t.ToString(Culture.DateTimeFormat.ShortTimePattern, Culture);
        return t.Date == Now.Date ? $"until {time}" : $"until {t.ToString("d MMM", Culture)}, {time}";
    }

    /// <summary>UTC calendar date, like the API's date filters.</summary>
    public static string Today() => System.DateTime.UtcNow.ToString("yyyy-MM-dd", Inv);
    public static string DaysAgo(int n) => System.DateTime.UtcNow.AddDays(-n).ToString("yyyy-MM-dd", Inv);

    private static readonly Dictionary<string, string> Apps = new()
    {
        ["chrome.exe"] = "Google Chrome", ["msedge.exe"] = "Microsoft Edge", ["firefox.exe"] = "Firefox", ["OUTLOOK.EXE"] = "Outlook",
        ["EXCEL.EXE"] = "Excel", ["WINWORD.EXE"] = "Word", ["POWERPNT.EXE"] = "PowerPoint", ["ms-teams.exe"] = "Microsoft Teams",
        ["Teams.exe"] = "Microsoft Teams", ["saplogon.exe"] = "SAP GUI", ["AcroRd32.exe"] = "Adobe Acrobat", ["Acrobat.exe"] = "Adobe Acrobat",
        ["explorer.exe"] = "File Explorer", ["WhatsApp.exe"] = "WhatsApp", ["Code.exe"] = "VS Code", ["AutoCAD.exe"] = "AutoCAD",
        ["acad.exe"] = "AutoCAD", ["vlc.exe"] = "VLC", ["Zoom.exe"] = "Zoom", ["notepad.exe"] = "Notepad", ["devenv.exe"] = "Visual Studio",
        ["WindowsTerminal.exe"] = "Terminal", ["Spotify.exe"] = "Spotify",
    };

    public static string AppName(string? exe) =>
        exe is null ? "" : Apps.TryGetValue(exe, out var n) ? n : exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;

    /// <summary>"OFFICE\nimal" → "nimal".</summary>
    public static string? ShortUser(string? user) => user?.Split('\\')[^1];

    public static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}
