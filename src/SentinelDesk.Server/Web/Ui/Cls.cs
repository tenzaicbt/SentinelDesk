namespace SentinelDesk.Server.Web.Ui;

/// <summary>Class-name helpers and the shared class strings of the console's controls.</summary>
public static class Cls
{
    /// <summary>Joins the non-empty class names (like clsx).</summary>
    public static string Join(params string?[] parts) => string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    public static string? If(bool condition, string cls) => condition ? cls : null;

    public const string Input =
        "h-8 w-full min-w-0 rounded-md border border-line-strong bg-transparent px-2.5 py-1 text-sm text-fg shadow-xs placeholder:text-muted dark:bg-white/4.5 " +
        "outline-none transition-[color,box-shadow] focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 disabled:opacity-50";

    public static string InputCls(string? extra = null) => Join(Input, extra);

    public const string TextArea = "w-full rounded-lg border border-line-strong bg-panel px-3 py-2 text-sm focus:border-brand focus:outline-none focus:ring-3 focus:ring-brand-soft";

    public const string Table =
        "w-full caption-bottom text-sm [&_thead_tr]:border-b [&_thead_tr]:border-line [&_th]:h-8 [&_th]:whitespace-nowrap [&_th]:px-2.5 [&_th]:text-left [&_th]:align-middle [&_th]:text-xs [&_th]:font-medium [&_th]:text-muted [&_td]:px-2.5 [&_td]:py-1.5 [&_td]:align-middle [&_tbody_tr]:border-b [&_tbody_tr]:border-line [&_tbody_tr:last-child]:border-0 [&_tbody_tr]:transition-colors [&_tbody_tr:hover]:bg-panel-2/50";

    public static string Tone(string tone) => tone switch
    {
        "brand" => "bg-brand-soft text-brand-ink ring-brand/25",
        "good" => "bg-good/10 text-good-ink ring-good/30",
        "warning" => "bg-warning/15 text-amber-700 dark:text-warning ring-warning/40",
        "serious" => "bg-serious/15 text-orange-700 dark:text-serious ring-serious/40",
        "critical" => "bg-critical/10 text-critical ring-critical/30",
        _ => "bg-transparent text-fg ring-line-strong",
    };

    /// <summary>Org units a device or token can be placed in (everything but the root), by name.</summary>
    public static IEnumerable<Services.OrgUnitRow> Units(IEnumerable<Services.OrgUnitRow>? org) =>
        (org ?? []).Where(o => o.Type != "organization").OrderBy(o => o.Name, StringComparer.CurrentCulture);

    public static bool IsLaptop(string? tags, string? model) =>
        (tags?.Contains("laptop") ?? false) || System.Text.RegularExpressions.Regex.IsMatch(model ?? "", "book|thinkpad|latitude|notebook", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static string Css(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record Opt(string Value, string Label, bool Disabled = false);
public sealed record TabItem(string Id, string Label, string? Icon = null, int? Count = null);
