using System.Drawing.Drawing2D;
using SentinelDesk.Shared;

namespace SentinelDesk.Tray;

/// <summary>Compact, modern status card (custom-painted, follows the Windows light/dark app theme).</summary>
public sealed class StatusForm : Form
{
    private readonly bool _dark = IsDarkMode();
    private string _state = "starting";
    private readonly List<(string Label, string Value)> _rows = [];

    public StatusForm()
    {
        Text = "SentinelDesk Status";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(380, 320);
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
        DoubleBuffered = true;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = _dark ? Brand.InkDeep : Color.White;
        Icon = Icons.App;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    public void Refresh(TrayContext ctx)
    {
        _state = ctx.State;
        var s = ctx.LastStatus;
        _rows.Clear();
        _rows.Add(("Signed-in user", $"{Environment.UserDomainName}\\{Environment.UserName}"));
        _rows.Add(("Computer", Environment.MachineName));
        _rows.Add(("Management server", s?.Server ?? "—"));
        _rows.Add(("Last upload", s?.LastUploadUtc is { } t ? Ago(t) : "—"));
        _rows.Add(("Buffered batches", (s?.QueueDepth ?? 0).ToString()));
        _rows.Add(("Agent version", ctx.AgentVersion ?? "—"));
        if (!string.IsNullOrEmpty(s?.StatusText) && _state != "connected") _rows.Add(("Detail", s.StatusText!));
        Invalidate();
    }

    private static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        return d.TotalSeconds < 60 ? "just now" : d.TotalMinutes < 60 ? $"{d.TotalMinutes:0} min ago" : utc.ToLocalTime().ToString("g");
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var fg = _dark ? Color.FromArgb(250, 250, 250) : Brand.Ink;
        var muted = _dark ? Color.FromArgb(161, 161, 170) : Brand.Slate;
        var line = _dark ? Color.FromArgb(44, 44, 48) : Color.FromArgb(231, 231, 233);

        // header band: ink with a brand-orange accent line
        using (var band = new LinearGradientBrush(new Rectangle(0, 0, ClientSize.Width, 84), Brand.InkDeep, Brand.Ink, 0f))
            g.FillRectangle(band, 0, 0, ClientSize.Width, 84);
        using (var accent = new SolidBrush(Brand.Orange))
            g.FillRectangle(accent, 0, 82, ClientSize.Width, 2);
        g.DrawIcon(Icons.For(_state), new Rectangle(18, 22, 40, 40));
        using var title = new Font("Segoe UI", 13f, FontStyle.Bold);
        TextRenderer.DrawText(g, "Sentinel", title, new Point(66, 18), Color.White, TextFormatFlags.NoPadding);
        var titleWidth = TextRenderer.MeasureText(g, "Sentinel", title, Size.Empty, TextFormatFlags.NoPadding).Width;
        TextRenderer.DrawText(g, "Desk", title, new Point(66 + titleWidth, 18), Brand.Orange, TextFormatFlags.NoPadding);
        var (label, color) = _state switch
        {
            "connected" => ("Protected · Connected", Icons.ColorFor("connected")),
            "offline" => ("Offline · data is buffered securely", Icons.ColorFor("offline")),
            "pending" => ("Awaiting administrator approval", Icons.ColorFor("pending")),
            "agent-down" => ("Agent service not reachable", Icons.ColorFor("error")),
            _ => ("Attention required", Icons.ColorFor("error"))
        };
        var pill = new Rectangle(66, 48, TextRenderer.MeasureText(label, Font).Width + 22, 22);
        using (var path = Rounded(pill, 11)) using (var b = new SolidBrush(Color.FromArgb(40, 255, 255, 255))) g.FillPath(b, path);
        using (var dot = new SolidBrush(color)) g.FillEllipse(dot, pill.X + 8, pill.Y + 7, 8, 8);
        TextRenderer.DrawText(g, label, Font, new Point(pill.X + 18, pill.Y + 3), Color.White);

        var y = 100;
        foreach (var (k, v) in _rows)
        {
            TextRenderer.DrawText(g, k, Font, new Point(20, y), muted);
            TextRenderer.DrawText(g, v, Font, new Rectangle(160, y, ClientSize.Width - 176, 20), fg, TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
            y += 26;
            using var pen = new Pen(line);
            g.DrawLine(pen, 20, y - 5, ClientSize.Width - 20, y - 5);
        }
        using var small = new Font("Segoe UI", 8.5f);
        TextRenderer.DrawText(g, "Managed by your organisation · see “Monitoring notice” for what is collected", small,
            new Rectangle(20, ClientSize.Height - 44, ClientSize.Width - 40, 20), muted, TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, Brand.Credit, small, new Rectangle(20, ClientSize.Height - 24, ClientSize.Width - 40, 20), muted, TextFormatFlags.EndEllipsis);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, radius * 2, radius * 2, 180, 90);
        p.AddArc(r.Right - radius * 2, r.Y, radius * 2, radius * 2, 270, 90);
        p.AddArc(r.Right - radius * 2, r.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
        p.AddArc(r.X, r.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static bool IsDarkMode()
    {
        try
        {
            return Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")
                ?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }
}

/// <summary>Transparency notice (PDPA): what is and isn't collected.</summary>
public sealed class NoticeForm : Form
{
    public NoticeForm(TraySettings settings, bool locationEnabled = false)
    {
        var dark = StatusForm.IsDarkMode();
        Text = "Monitoring notice";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 360);
        Font = new Font("Segoe UI", 10f);
        BackColor = dark ? Brand.InkDeep : Color.White;
        ForeColor = dark ? Color.FromArgb(250, 250, 250) : Brand.Ink;
        Icon = Icons.App;

        var heading = new Label { Text = "This device is monitored", Font = new Font("Segoe UI Semibold", 14f), AutoSize = true, Location = new Point(24, 20) };
        var body = new Label
        {
            Text = settings.MonitoringNotice +
                   (locationEnabled ? "\n\nLocation: this device's location (from Windows Location) is also collected." : "") +
                   "\n\nData is encrypted in transit and at rest, retained for a limited period and accessible only to authorised staff. " +
                   "Questions: " + settings.ContactIt,
            Location = new Point(24, 64), Size = new Size(472, 230)
        };
        var ok = new Button { Text = "I understand", Size = new Size(130, 34), Location = new Point(366, 306), FlatStyle = FlatStyle.Flat, BackColor = Brand.Orange, ForeColor = Color.White };
        ok.FlatAppearance.BorderSize = 0;
        ok.Click += (_, _) => Close();
        var credit = new LinkLabel
        {
            Text = Brand.Credit, AutoSize = true, Location = new Point(24, 316), Font = new Font("Segoe UI", 8.5f),
            LinkColor = Brand.Orange, ActiveLinkColor = Brand.OrangeDeep, ForeColor = dark ? Color.FromArgb(161, 161, 170) : Brand.Slate,
            LinkBehavior = LinkBehavior.HoverUnderline,
        };
        credit.LinkArea = new LinkArea(Brand.Credit.IndexOf("www.", StringComparison.Ordinal), "www.lordefx.com".Length);
        credit.LinkClicked += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.lordefx.com") { UseShellExecute = true }); } catch { } };
        Controls.AddRange([heading, body, ok, credit]);
        AcceptButton = ok;
    }
}

/// <summary>SentinelDesk brand palette (doc/logo).</summary>
internal static class Brand
{
    public static readonly Color Ink = Color.FromArgb(0x17, 0x17, 0x17);
    public static readonly Color InkDeep = Color.FromArgb(0x0A, 0x0A, 0x0A);
    public static readonly Color Orange = Color.FromArgb(0xFF, 0x6A, 0x00);
    public static readonly Color OrangeDeep = Color.FromArgb(0xE0, 0x5D, 0x00);
    public static readonly Color Slate = Color.FromArgb(0x71, 0x71, 0x7A);
    public const string Credit = "© 2026 Yohan Koshala · www.lordefx.com";
}
