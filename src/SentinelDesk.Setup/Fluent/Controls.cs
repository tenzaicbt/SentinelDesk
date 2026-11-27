using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SentinelDesk.Setup.Fluent
{
    internal static class Dpi
    {
        public static int S(Control c, int px) => (int)Math.Round(px * c.DeviceDpi / 96.0);
        public static float F(Control c, float px) => px * c.DeviceDpi / 96f;
    }

    /// <summary>Fluent button: accent (filled orange) or standard (subtle fill with a hairline border), 4 px corners.</summary>
    public class FluentButton : Button
    {
        private bool _hover, _down;

        [DefaultValue(false)] public bool Accent { get; set; }

        public FluentButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            Font = Theme.Body;
            Cursor = Cursors.Default;
            MinimumSize = new Size(96, 32);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Window);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color fill, border, text;
            if (!Enabled)
            {
                fill = Accent ? (Theme.Dark ? Theme.Rgb(0x434343) : Theme.Rgb(0xBFBFBF)) : Theme.Control;
                border = Accent ? fill : Theme.ControlBorder;
                text = Accent ? (Theme.Dark ? Theme.Rgb(0xA7A7A7) : Theme.Rgb(0xFFFFFF)) : Theme.TextDisabled;
            }
            else if (Accent)
            {
                fill = _down ? Theme.AccentPressed : _hover ? Theme.AccentHover : Theme.Accent;
                border = fill;
                text = Theme.AccentText;
            }
            else
            {
                fill = _down ? Theme.ControlPressed : _hover ? Theme.ControlHover : Theme.Control;
                border = Theme.ControlBorder;
                text = _down ? Theme.TextSecondary : Theme.Text;
            }
            using (var path = Theme.Round(r, Dpi.F(this, 4)))
            using (var b = new SolidBrush(fill))
            using (var p = new Pen(border))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }
            if (Focused && ShowFocusCues)
                using (var path = Theme.Round(new RectangleF(-1.5f, -1.5f, Width + 2, Height + 2), Dpi.F(this, 6)))
                using (var p = new Pen(Theme.Text, 2f))
                    g.DrawPath(p, path);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>Fluent text box: rounded field with a thin border and an accent underline while focused.</summary>
    public class FluentTextBox : UserControl
    {
        private readonly TextBox _box = new TextBox { BorderStyle = BorderStyle.None };
        private string _placeholder;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        public FluentTextBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            _box.Font = Theme.Body;
            _box.BackColor = Theme.Input;
            _box.ForeColor = Theme.Text;
            _box.GotFocus += (_, __) => Invalidate();
            _box.LostFocus += (_, __) => Invalidate();
            _box.TextChanged += (_, __) => OnTextChanged(EventArgs.Empty);
            _box.HandleCreated += (_, __) => ApplyPlaceholder();
            Controls.Add(_box);
            Height = 34;
            BackColor = Theme.Card;
        }

        [Browsable(true), EditorBrowsable(EditorBrowsableState.Always), DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public override string Text { get => _box.Text; set => _box.Text = value; }

        [DefaultValue(false)] public bool Password { get => _box.UseSystemPasswordChar; set => _box.UseSystemPasswordChar = value; }

        [DefaultValue(null)] public string Placeholder { get => _placeholder; set { _placeholder = value; ApplyPlaceholder(); } }

        public TextBox Inner => _box;

        private void ApplyPlaceholder()
        {
            if (_box.IsHandleCreated) SendMessage(_box.Handle, 0x1501 /* EM_SETCUEBANNER */, (IntPtr)1, _placeholder ?? "");
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); _box.Focus(); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            var pad = Dpi.S(this, 11);
            _box.Location = new Point(pad, (Height - _box.Height) / 2);
            _box.Width = Width - pad * 2;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Card);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            var focused = _box.Focused;
            using (var path = Theme.Round(r, Dpi.F(this, 4)))
            using (var b = new SolidBrush(Enabled ? Theme.Input : Theme.Control))
            using (var p = new Pen(Theme.ControlBorder))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
                // Fluent bottom stroke: thin grey, or a 2 px accent line while typing
                var old = g.Clip;
                g.SetClip(path);
                using (var line = new SolidBrush(focused ? Theme.Accent : Theme.ControlStrong))
                {
                    var h = focused ? Dpi.F(this, 2) : 1f;
                    g.FillRectangle(line, 0, Height - h, Width, h);
                }
                g.Clip = old;
            }
        }
    }

    /// <summary>Fluent check box: rounded square, filled with the accent colour and a check mark when on.</summary>
    public class FluentCheckBox : CheckBox
    {
        private bool _hover;

        public FluentCheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = Theme.Body;
            ForeColor = Theme.Text;
            AutoSize = false;
            Height = 28;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Card);
            var size = Dpi.F(this, 18);
            var box = new RectangleF(0.5f, (Height - size) / 2, size, size);
            using (var path = Theme.Round(box, Dpi.F(this, 4)))
            {
                if (Checked)
                {
                    using (var b = new SolidBrush(_hover ? Theme.AccentHover : Theme.Accent)) g.FillPath(b, path);
                    using (var p = new Pen(Theme.AccentText, Dpi.F(this, 1.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        g.DrawLines(p, new[]
                        {
                            new PointF(box.X + size * 0.24f, box.Y + size * 0.52f),
                            new PointF(box.X + size * 0.42f, box.Y + size * 0.70f),
                            new PointF(box.X + size * 0.77f, box.Y + size * 0.32f),
                        });
                }
                else
                {
                    using (var b = new SolidBrush(_hover ? Theme.ControlHover : Theme.Input)) g.FillPath(b, path);
                    using (var p = new Pen(Theme.ControlStrong)) g.DrawPath(p, path);
                }
            }
            var tx = (int)(size + Dpi.F(this, 10));
            TextRenderer.DrawText(g, Text, Font, new Rectangle(tx, 0, Width - tx, Height), Enabled ? Theme.Text : Theme.TextDisabled,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            if (Focused && ShowFocusCues)
                using (var p = new Pen(Theme.Text, 1.5f)) g.DrawRectangle(p, tx - 3, 2, Width - tx + 1, Height - 5);
        }
    }

    /// <summary>Thin Fluent progress bar: determinate, or an indeterminate sliding segment.</summary>
    public class FluentProgress : Control
    {
        private readonly Timer _timer = new Timer { Interval = 16 };
        private float _phase;
        private int _value;
        private bool _indeterminate;

        public FluentProgress()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 4;
            _timer.Tick += (_, __) => { _phase = (_phase + 0.012f) % 1.6f; Invalidate(); };
        }

        public int Value { get => _value; set { _value = Math.Max(0, Math.Min(100, value)); Invalidate(); } }

        public bool Indeterminate
        {
            get => _indeterminate;
            set { _indeterminate = value; _timer.Enabled = value; Invalidate(); }
        }

        protected override void Dispose(bool disposing) { if (disposing) _timer.Dispose(); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Card);
            var h = Height;
            using (var track = new Pen(Theme.ControlStrong, 1f))
                g.DrawLine(track, 0, h / 2f, Width, h / 2f);
            float x0, x1;
            if (_indeterminate)
            {
                var start = (_phase - 0.4f) * Width;
                x0 = Math.Max(0, start);
                x1 = Math.Min(Width, start + Width * 0.4f);
            }
            else { x0 = 0; x1 = Width * _value / 100f; }
            if (x1 <= x0) return;
            using (var path = Theme.Round(new RectangleF(x0, 0, x1 - x0, h), h / 2f))
            using (var b = new SolidBrush(Theme.Accent))
                g.FillPath(b, path);
        }
    }

    /// <summary>The wizard's step list (left rail): done, current (accent pill) and upcoming steps.</summary>
    public class StepRail : Control
    {
        private List<string> _steps = new List<string>();
        private int _current;

        public StepRail()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public void SetSteps(IEnumerable<string> steps) { _steps = new List<string>(steps); Invalidate(); }
        public int Current { get => _current; set { _current = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            var rowH = Dpi.S(this, 40);
            using (var iconFont = Theme.Icon(Dpi.F(this, 9)))
                for (var i = 0; i < _steps.Count; i++)
                {
                    var y = i * rowH;
                    var row = new Rectangle(0, y, Width, rowH - Dpi.S(this, 4));
                    var current = i == _current;
                    var done = i < _current;
                    if (current)
                    {
                        using (var path = Theme.Round(row, Dpi.F(this, 4)))
                        using (var b = new SolidBrush(Theme.Dark ? Theme.Rgb(0x2D2D2D) : Theme.Rgb(0xF9F9F9)))
                            g.FillPath(b, path);
                        using (var path = Theme.Round(new RectangleF(0, y + row.Height * 0.3f, Dpi.F(this, 3), row.Height * 0.4f), Dpi.F(this, 1.5f)))
                        using (var b = new SolidBrush(Theme.Accent))
                            g.FillPath(b, path);
                    }
                    // number / check circle
                    var d = Dpi.F(this, 22);
                    var c = new RectangleF(Dpi.F(this, 14), y + (row.Height - d) / 2, d, d);
                    if (done)
                    {
                        using (var b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, c);
                        TextRenderer.DrawText(g, "", iconFont, Rectangle.Round(c), Theme.AccentText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    }
                    else
                    {
                        using (var p = new Pen(current ? Theme.Accent : Theme.ControlStrong, Dpi.F(this, 1.5f))) g.DrawEllipse(p, c);
                        TextRenderer.DrawText(g, (i + 1).ToString(), Theme.Caption, Rectangle.Round(c), current ? Theme.Accent : Theme.TextSecondary,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    }
                    var tx = (int)(c.Right + Dpi.F(this, 12));
                    TextRenderer.DrawText(g, _steps[i], current ? Theme.BodyStrong : Theme.Body, new Rectangle(tx, y, Width - tx, row.Height),
                        current || done ? Theme.Text : Theme.TextSecondary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                }
        }
    }

    /// <summary>Rounded card surface (Fluent "layer").</summary>
    public class Card : Panel
    {
        public Card()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            Padding = new Padding(20, 16, 20, 16);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Window);
            using (var path = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Dpi.F(this, 8)))
            using (var b = new SolidBrush(Theme.Card))
            using (var p = new Pen(Theme.CardBorder))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }
        }
    }

    /// <summary>Label that draws with TextRenderer in the theme colours (wraps, no background).</summary>
    public class FluentLabel : Label
    {
        public FluentLabel(string text, Font font = null, Color? color = null)
        {
            Text = text;
            Font = font ?? Theme.Body;
            ForeColor = color ?? Theme.Text;
            BackColor = Color.Transparent;
            AutoSize = false;
            UseMnemonic = false;
        }
    }
}
