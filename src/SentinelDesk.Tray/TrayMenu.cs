using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SentinelDesk.Tray;

/// <summary>
/// The tray menu in a modern style: rounded corners (Windows 11), roomy items with Fluent icons, a status header,
/// and colours that follow the Windows light/dark app theme (switches live when the theme changes).
/// </summary>
public sealed class TrayMenu : ContextMenuStrip
{
    private readonly MenuHeader _header = new();
    private readonly ModernRenderer _renderer = new();
    private readonly List<(ToolStripMenuItem Item, string Glyph)> _glyphs = [];

    public TrayMenu()
    {
        Font = new Font("Segoe UI", 9.75f);
        Padding = new Padding(6, 6, 6, 6);
        ShowImageMargin = false;   // icons are drawn by the renderer, aligned with the header logo
        ShowCheckMargin = false;
        DropShadowEnabled = true;
        Renderer = _renderer;
        Items.Add(_header);
        Items.Add(new ToolStripSeparator());
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
    }

    /// <summary>Adds an item with a Segoe Fluent / MDL2 icon glyph.</summary>
    public ToolStripMenuItem Add(string text, string glyph, Action onClick)
    {
        var s = DeviceDpi / 96f;
        // the text is drawn by the renderer at ModernRenderer.TextLeft; the right padding reserves that width
        var item = new ToolStripMenuItem(text) { Padding = new Padding(0, (int)(6 * s), (int)((ModernRenderer.TextLeft + 8) * s), (int)(6 * s)), Margin = new Padding(0, 1, 0, 1) };
        item.Click += (_, _) => onClick();
        _glyphs.Add((item, glyph));
        item.Tag = new ItemIcons(Glyphs.Render(glyph, _renderer.Palette.Icon, DeviceDpi), Glyphs.Render(glyph, _renderer.Palette.Accent, DeviceDpi));
        Items.Add(item);
        return item;
    }

    public void AddSeparator() => Items.Add(new ToolStripSeparator { Margin = new Padding(0, 3, 0, 3) });

    /// <summary>Status line in the header ("Protected and connected", ...) with its status colour.</summary>
    public void SetStatus(string text, Color dot)
    {
        _header.Status = text;
        _header.Dot = dot;
        _header.Invalidate();
    }

    private void OnPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
        {
            if (InvokeRequired) BeginInvoke(() => ApplyTheme()); else ApplyTheme();
        }
    }

    internal void ApplyTheme(MenuPalette? palette = null)
    {
        _renderer.Palette = palette ?? MenuPalette.Current();
        _header.Palette = _renderer.Palette;
        BackColor = _renderer.Palette.Back;
        ForeColor = _renderer.Palette.Text;
        foreach (var (item, glyph) in _glyphs)
        {
            var old = item.Tag as ItemIcons;
            item.Tag = new ItemIcons(Glyphs.Render(glyph, _renderer.Palette.Icon, DeviceDpi), Glyphs.Render(glyph, _renderer.Palette.Accent, DeviceDpi));
            old?.Dispose();
        }
        Invalidate();
    }

    protected override void OnOpening(System.ComponentModel.CancelEventArgs e)
    {
        base.OnOpening(e);
        // the theme may have changed while the menu was closed (UserPreferenceChanged is not always raised for it)
        if (MenuPalette.Current().Dark != _renderer.Palette.Dark) ApplyTheme();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.RoundCorners(Handle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        base.Dispose(disposing);
    }
}

/// <summary>
/// Light / dark colours of the menu, taken from the web console's design tokens: panel, line, text and muted greys,
/// and the brand orange. The hovered item looks like the console sidebar's active item (orange tint, orange icon).
/// </summary>
public sealed record MenuPalette(bool Dark, Color Back, Color Border, Color Text, Color Muted, Color Icon, Color Hover, Color Pressed, Color Separator,
    Color Accent, Color Chip)
{
    public static MenuPalette Current() => StatusForm.IsDarkMode()
        // dark: panel #171717, line white/9 %, fg #fafafa, muted #a1a1aa, brand #ff7a1a, brand-soft = brand 14 % on the panel
        ? new(true, Color.FromArgb(0x17, 0x17, 0x17), Color.FromArgb(0x2E, 0x2E, 0x2E), Color.FromArgb(0xFA, 0xFA, 0xFA), Color.FromArgb(0xA1, 0xA1, 0xAA),
              Color.FromArgb(0xD4, 0xD4, 0xD8), Color.FromArgb(0x37, 0x25, 0x17), Color.FromArgb(0x45, 0x2C, 0x18), Color.FromArgb(0x2A, 0x2A, 0x2A),
              Color.FromArgb(0xFF, 0x7A, 0x1A), Color.FromArgb(0x23, 0x23, 0x25))
        // light: panel #ffffff, line #e7e7e9, fg #171717, muted #71717a, brand #ff6a00, brand-soft = brand 10 % on white
        : new(false, Color.White, Color.FromArgb(0xE7, 0xE7, 0xE9), Color.FromArgb(0x17, 0x17, 0x17), Color.FromArgb(0x71, 0x71, 0x7A),
              Color.FromArgb(0x3F, 0x3F, 0x46), Color.FromArgb(0xFF, 0xF0, 0xE6), Color.FromArgb(0xFF, 0xE4, 0xD1), Color.FromArgb(0xE7, 0xE7, 0xE9),
              Color.FromArgb(0xFF, 0x6A, 0x00), Color.FromArgb(0xF4, 0xF4, 0xF5));
}

/// <summary>Paints the menu: flat background, rounded hover highlight, thin separators, no image-margin band.</summary>
internal sealed class ModernRenderer : ToolStripRenderer
{
    public MenuPalette Palette { get; set; } = MenuPalette.Current();

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(Palette.Back);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // Windows 11 draws the rounded border itself; elsewhere a 1 px line
        if (Native.HasRoundedCorners) return;
        using var p = new Pen(Palette.Border);
        e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { /* same colour as the menu */ }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var g = e.Graphics;
        if (e.Item.Selected && e.Item.Enabled)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height - 1);
            using var path = RoundRect(r, 6);
            using var b = new SolidBrush(e.Item.Pressed ? Palette.Pressed : Palette.Hover);
            g.FillPath(b, path);
        }
        // the item's icon (glyph bitmaps in Tag), in the same column as the header logo; orange while hovered
        if (e.Item.Tag is ItemIcons icons)
        {
            var icon = e.Item.Selected && e.Item.Enabled ? icons.Hover : icons.Normal;
            var x = (int)(10 * (e.ToolStrip?.DeviceDpi ?? 96) / 96f);
            g.DrawImage(icon, x, (e.Item.Height - icon.Height) / 2, icon.Width, icon.Height);
        }
    }

    /// <summary>Left edge of item text: after the icon column (10 px inset + 20 px icon + 16 px gap), at 96 DPI.</summary>
    public const int TextLeft = 46;

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (e.Item is not ToolStripMenuItem || e.Item.Owner is not TrayMenu)
        {
            base.OnRenderItemText(e);
            return;
        }
        var s = (e.ToolStrip?.DeviceDpi ?? 96) / 96f;
        var x = (int)(TextLeft * s);
        var r = new Rectangle(x, 0, e.Item.Width - x - (int)(12 * s), e.Item.Height);
        TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, r, e.Item.Enabled ? Palette.Text : Palette.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var p = new Pen(Palette.Separator);
        e.Graphics.DrawLine(p, 8, y, e.Item.Width - 8, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Palette.Muted;
        base.OnRenderArrow(e);
    }

    public static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>An item's icon in its normal and hovered colours.</summary>
internal sealed record ItemIcons(Bitmap Normal, Bitmap Hover) : IDisposable
{
    public void Dispose() { Normal.Dispose(); Hover.Dispose(); }
}

/// <summary>Non-clickable menu header, like the console sidebar's: logo, wordmark, "ENDPOINT METRICS", and the agent's status.</summary>
internal sealed class MenuHeader : ToolStripItem
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Status { get; set; } = "Starting…";
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Dot { get; set; } = Icons.ColorFor("starting");
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public MenuPalette Palette { get; set; } = MenuPalette.Current();

    public MenuHeader() { AutoSize = false; Enabled = true; Margin = new Padding(0, 0, 0, 2); }

    public override bool CanSelect => false;

    private float Scale => (Owner?.DeviceDpi ?? 96) / 96f;

    public override Size GetPreferredSize(Size constrainingSize) => new((int)(280 * Scale), (int)(76 * Scale));

    protected override void OnOwnerChanged(EventArgs e)
    {
        base.OnOwnerChanged(e);
        Size = GetPreferredSize(Size.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var s = Scale;
        int icon = (int)(30 * s), x = (int)(5 * s), y = (int)(7 * s);   // centred over the item icon column
        using (var logo = Icons.LoadApp(icon)) g.DrawIcon(logo, new Rectangle(x, y, icon, icon));

        // wordmark: "Sentinel" in ink, "Desk" in brand orange, extra bold - as in the console sidebar
        var tx = (int)(ModernRenderer.TextLeft * s);
        using var title = new Font("Segoe UI Black", 11f);
        using var tag = new Font("Segoe UI Semibold", 6.75f);
        using var small = new Font("Segoe UI", 8.75f);
        TextRenderer.DrawText(g, "Sentinel", title, new Point(tx, (int)(5 * s)), Palette.Text, TextFormatFlags.NoPadding);
        var w = TextRenderer.MeasureText(g, "Sentinel", title, Size.Empty, TextFormatFlags.NoPadding).Width;
        TextRenderer.DrawText(g, "Desk", title, new Point(tx + w, (int)(5 * s)), Palette.Accent, TextFormatFlags.NoPadding);
        DrawSpaced(g, "ENDPOINT METRICS", tag, Palette.Muted, tx, (int)(26 * s), 1.4f * s);

        // status row: rounded chip with a coloured dot, like the console's "Live updates connected"
        var chip = new Rectangle(x, (int)(47 * s), Width - x - (int)(8 * s), (int)(23 * s));
        using (var path = ModernRenderer.RoundRect(chip, (int)(6 * s)))
        using (var b = new SolidBrush(Palette.Chip))
            g.FillPath(b, path);
        var dot = (int)(7 * s);
        var dx = chip.X + (int)(10 * s);
        using (var b = new SolidBrush(Dot)) g.FillEllipse(b, dx, chip.Y + (chip.Height - dot) / 2, dot, dot);
        TextRenderer.DrawText(g, Status, small, new Rectangle(dx + dot + (int)(8 * s), chip.Y, chip.Right - dx - dot - (int)(16 * s), chip.Height),
            Palette.Text, TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    /// <summary>Upper-case label with letter spacing (the console's tracking-[0.2em]).</summary>
    private static void DrawSpaced(Graphics g, string text, Font font, Color color, int x, int y, float spacing)
    {
        float cx = x;
        foreach (var ch in text)
        {
            var t = ch.ToString();
            TextRenderer.DrawText(g, t, font, new Point((int)cx, y), color, TextFormatFlags.NoPadding);
            cx += TextRenderer.MeasureText(g, t, font, Size.Empty, TextFormatFlags.NoPadding).Width + spacing;
        }
    }
}

/// <summary>Icon glyphs from the Windows icon font (Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10).</summary>
internal static class Glyphs
{
    public const string Status = "";      // Diagnostic
    public const string Notice = "";      // Shield
    public const string Support = "";     // Help
    public const string About = "";       // Info
    public const string Request = "";     // Permissions
    public const string Unlock = "";      // Unlock
    public const string Reconnect = "";   // Refresh

    private static readonly string Family = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    public static Bitmap Render(string glyph, Color color, int dpi)
    {
        var px = (int)Math.Round(20 * dpi / 96.0);
        var bmp = new Bitmap(px, px);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font(Family, 15f * dpi / 96f, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(glyph, font, brush, new RectangleF(0, 0, px, px), fmt);
        return bmp;
    }
}

internal static class Native
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    /// <summary>Windows 11 (build 22000+) rounds windows that ask for it.</summary>
    public static bool HasRoundedCorners => Environment.OSVersion.Version.Build >= 22000;

    public static void RoundCorners(IntPtr hwnd)
    {
        if (!HasRoundedCorners) return;
        var v = DWMWCP_ROUND;
        try { DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref v, sizeof(int)); } catch { /* older Windows */ }
    }
}
