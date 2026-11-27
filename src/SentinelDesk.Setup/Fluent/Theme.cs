using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SentinelDesk.Setup.Fluent
{
    /// <summary>
    /// Windows 11 Fluent colours and type (light / dark from the Windows app theme), with the SentinelDesk orange
    /// as the accent colour.
    /// </summary>
    public static class Theme
    {
        public static readonly bool Dark = IsDarkMode();

        // surfaces
        public static readonly Color Window = Dark ? Rgb(0x202020) : Rgb(0xF3F3F3);       // Mica-like base
        public static readonly Color Rail = Dark ? Rgb(0x1C1C1C) : Rgb(0xEBEBEB);
        public static readonly Color Card = Dark ? Rgb(0x2B2B2B) : Rgb(0xFFFFFF);
        public static readonly Color CardBorder = Dark ? Rgb(0x3A3A3A) : Rgb(0xE5E5E5);
        public static readonly Color Divider = Dark ? Rgb(0x353535) : Rgb(0xE5E5E5);

        // text
        public static readonly Color Text = Dark ? Rgb(0xFFFFFF) : Rgb(0x1A1A1A);
        public static readonly Color TextSecondary = Dark ? Rgb(0xC5C5C5) : Rgb(0x5F5F5F);
        public static readonly Color TextDisabled = Dark ? Rgb(0x787878) : Rgb(0xA0A0A0);

        // controls
        public static readonly Color Control = Dark ? Rgb(0x2D2D2D) : Rgb(0xFBFBFB);
        public static readonly Color ControlHover = Dark ? Rgb(0x323232) : Rgb(0xF6F6F6);
        public static readonly Color ControlPressed = Dark ? Rgb(0x272727) : Rgb(0xF0F0F0);
        public static readonly Color ControlBorder = Dark ? Rgb(0x3C3C3C) : Rgb(0xE1E1E1);
        public static readonly Color ControlStrong = Dark ? Rgb(0x9A9A9A) : Rgb(0x868686);
        public static readonly Color Input = Dark ? Rgb(0x2D2D2D) : Rgb(0xFFFFFF);

        // accent: SentinelDesk orange
        public static readonly Color Accent = Dark ? Rgb(0xFF7A1A) : Rgb(0xFF6A00);
        public static readonly Color AccentHover = Dark ? Rgb(0xFF8B38) : Rgb(0xF06400);
        public static readonly Color AccentPressed = Dark ? Rgb(0xE86A10) : Rgb(0xD85A00);
        public static readonly Color AccentText = Rgb(0xFFFFFF);
        public static readonly Color AccentSoft = Dark ? Rgb(0x3A2718) : Rgb(0xFFF0E6);

        public static readonly Color Success = Dark ? Rgb(0x6CCB5F) : Rgb(0x0F7B0F);
        public static readonly Color Error = Dark ? Rgb(0xFF99A4) : Rgb(0xC42B1C);
        public static readonly Color Caution = Dark ? Rgb(0xFCE100) : Rgb(0x9D5D00);

        private static readonly string TextFamily = Has("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
        private static readonly string DisplayFamily = Has("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI";
        public static readonly string IconFamily = Has("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

        public static readonly Font Body = new Font(TextFamily, 10f);
        public static readonly Font BodyStrong = new Font(TextFamily, 10f, FontStyle.Bold);
        public static readonly Font Caption = new Font(TextFamily, 9f);
        public static readonly Font Subtitle = new Font(DisplayFamily, 14f, FontStyle.Bold);
        public static readonly Font Title = new Font(DisplayFamily, 20f, FontStyle.Bold);
        public static readonly Font Mono = new Font("Cascadia Mono", 8.75f) is { } f && f.Name == "Cascadia Mono" ? f : new Font("Consolas", 8.75f);

        public static Font Icon(float size) => new Font(IconFamily, size);

        public static Color Rgb(int rgb) => Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);

        private static bool Has(string family) => FontFamily.Families.Any(f => f.Name == family);

        public static bool IsDarkMode()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var d = radius * 2;
            var p = new GraphicsPath();
            if (radius <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>Window chrome: dark title bar, Mica-tinted caption and rounded corners on Windows 11.</summary>
    public static class WindowChrome
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

        public static void Apply(IntPtr hwnd)
        {
            try
            {
                var dark = Theme.Dark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
                if (!IsWindows11) return;
                var round = 2;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
                var mica = 2;   // main-window Mica behind the title bar
                DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref mica, 4);
                var c = Theme.Window;
                var caption = c.R | (c.G << 8) | (c.B << 16);   // COLORREF: the title bar blends into the window
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, 4);
            }
            catch { /* older Windows: default chrome */ }
        }
    }
}
