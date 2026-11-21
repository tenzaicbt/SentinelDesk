using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SentinelDesk.Setup
{
    /// <summary>Single-page installer: welcome → (settings if not preconfigured) → progress → done.</summary>
    public sealed class SetupForm : Form
    {
        private readonly SetupConfig _config;
        private readonly Panel _header = new Panel { Dock = DockStyle.Top, Height = 96 };
        private readonly Label _title = new Label { AutoSize = true, ForeColor = Color.White, BackColor = Color.Transparent, Font = new Font("Segoe UI Semibold", 15f) };
        private readonly Label _subtitle = new Label { AutoSize = true, ForeColor = Color.FromArgb(212, 212, 216), BackColor = Color.Transparent, Font = new Font("Segoe UI", 9.5f) };
        private readonly Panel _body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(28, 20, 28, 12) };
        private readonly TextBox _server = new TextBox();
        private readonly TextBox _token = new TextBox();
        private readonly TextBox _key = new TextBox { UseSystemPasswordChar = true };
        private readonly CheckBox _tofu = new CheckBox { Text = "Trust the server certificate on first connection (private / self-signed certificate)", AutoSize = true };
        private readonly TextBox _log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 8.5f), Visible = false, BorderStyle = BorderStyle.FixedSingle };
        private readonly ProgressBar _progress = new ProgressBar { Style = ProgressBarStyle.Marquee, Visible = false, Height = 6 };
        private readonly Button _install = new Button { Text = "Install", Width = 120, Height = 34 };
        private readonly Button _cancel = new Button { Text = "Cancel", Width = 100, Height = 34 };
        public int ExitCode { get; private set; } = 1602; // user cancelled
        private bool _finished;

        public SetupForm(SetupConfig config)
        {
            _config = config;
            Text = "SentinelDesk Agent Setup";
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(600, 470);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _header.Paint += PaintHeader;
            _title.Location = new Point(92, 22);
            _subtitle.Location = new Point(94, 56);
            _header.Controls.Add(_title);
            _header.Controls.Add(_subtitle);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = Color.FromArgb(250, 250, 250) };
            _install.Location = new Point(ClientSize.Width - 28 - 120, 13);
            _cancel.Location = new Point(_install.Left - 10 - 100, 13);
            StyleButton(_install, primary: true);
            StyleButton(_cancel, primary: false);
            _install.Click += async (_, __) => { if (_finished) Close(); else await InstallAsync(); };
            _cancel.Click += (_, __) => Close();
            footer.Controls.Add(_install);
            footer.Controls.Add(_cancel);

            Controls.Add(_body);
            Controls.Add(footer);
            Controls.Add(_header);
            AcceptButton = _install;

            BuildWelcome();
        }

        private void BuildWelcome()
        {
            var installed = Installer.IsInstalled();
            _title.Text = installed ? "SentinelDesk is already installed" : "Install SentinelDesk Agent";
            _subtitle.Text = _config.IsPreconfigured
                ? $"Pre-configured for {(_config.BranchName ?? _config.OrgName ?? "your organization")}"
                : "Company device monitoring · managed by IT";

            var y = 0;
            void Add(Control c, int h = 0) { c.Location = new Point(0, y); c.Width = _body.ClientSize.Width - _body.Padding.Horizontal; _body.Controls.Add(c); y += (h > 0 ? h : c.Height) + 6; }
            Label Text(string s, bool bold = false, Color? color = null) => new Label
            {
                Text = s, AutoSize = false, Height = 20, ForeColor = color ?? Ink,
                Font = bold ? new Font("Segoe UI Semibold", 9.5f) : Font
            };

            if (installed)
            {
                var l = Text("This computer is already protected and reporting to IT. No action is needed.\n\nTo reinstall, uninstall first with the Uninstall Key or a one-time code from the console.");
                l.Height = 100;
                Add(l);
                _install.Text = "Close";
                _cancel.Visible = false;
                ExitCode = 0;
                _finished = true;
                return;
            }
            if (!Installer.HasPackage())
            {
                var l = Text("This setup file was built without the agent package.\nRun build.ps1, or download the installer from the web console (Agent management).", color: Color.FromArgb(200, 40, 40));
                l.Height = 60;
                Add(l);
                _install.Enabled = false;
                return;
            }

            var intro = Text("This installs the SentinelDesk monitoring agent on this company computer. It runs as a protected Windows service " +
                             "with a tray icon, and securely reports device health, inventory, application and network usage to IT.");
            intro.Height = 58;
            Add(intro);

            if (_config.IsPreconfigured)
            {
                y += 6;
                Add(Text("Management server", true));
                Add(Text(_config.ServerUrl, color: Color.FromArgb(0xC2, 0x41, 0x0C)));
                if (!string.IsNullOrEmpty(_config.BranchName)) { Add(Text("Branch", true)); Add(Text(_config.BranchName)); }
                y += 6;
                var note = Text("Administrator rights are required. After installation the agent can only be removed with the organization's Uninstall Key.",
                    color: Color.FromArgb(113, 113, 122));
                note.Height = 40;
                Add(note);
            }
            else
            {
                Add(Text("Management server address", true));
                _server.Text = _config.ServerUrl ?? "https://";
                Add(_server);
                Add(Text("Enrollment token (from the console → Agent management)", true));
                _token.Text = _config.EnrollToken ?? "";
                Add(_token);
                Add(Text("Organization uninstall key", true));
                _key.Text = _config.UninstallKey ?? "";
                Add(_key);
                _tofu.Checked = _config.TrustOnFirstUse;
                Add(_tofu, 24);
            }

            _progress.Location = new Point(0, y + 4);
            _progress.Width = _body.ClientSize.Width - _body.Padding.Horizontal;
            _body.Controls.Add(_progress);
            _log.Location = new Point(0, y + 16);
            _log.Size = new Size(_progress.Width, Math.Max(80, _body.ClientSize.Height - _body.Padding.Vertical - y - 20));
            _log.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _body.Controls.Add(_log);
        }

        private async Task InstallAsync()
        {
            if (!_config.IsPreconfigured)
            {
                _config.ServerUrl = _server.Text.Trim();
                _config.EnrollToken = _token.Text.Trim();
                _config.UninstallKey = _key.Text;
                _config.TrustOnFirstUse = _tofu.Checked;
                if (!Uri.TryCreate(_config.ServerUrl, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http"))
                { MessageBox.Show(this, "Enter a valid server address, e.g. https://monitor.company.lk", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                if (_config.EnrollToken.Length < 6) { MessageBox.Show(this, "Enter the enrollment token.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                if (_config.UninstallKey.Length < 8) { MessageBox.Show(this, "The uninstall key must be at least 8 characters.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            }

            foreach (Control c in _body.Controls) if (c is TextBox t && t != _log) t.Enabled = false;
            _tofu.Enabled = false;
            _install.Enabled = false;
            _cancel.Enabled = false;
            _progress.Visible = true;
            _log.Visible = true;
            _title.Text = "Installing…";

            var code = await Task.Run(() => Installer.Run(_config, line => BeginInvoke((Action)(() => _log.AppendText(line + Environment.NewLine)))));
            ExitCode = code;
            _progress.Visible = false;
            _title.Text = code == 0 ? "SentinelDesk is installed" : "Installation failed";
            _subtitle.Text = code == 0 ? "This computer is now protected and reporting to IT." : $"Details: {Installer.LogFile}";
            _header.Invalidate();
            _install.Text = "Finish";
            _install.Enabled = true;
            _finished = true;
        }

        private static readonly Color Ink = Color.FromArgb(0x17, 0x17, 0x17);
        private static readonly Color InkDeep = Color.FromArgb(0x0A, 0x0A, 0x0A);
        private static readonly Color Orange = Color.FromArgb(0xFF, 0x6A, 0x00);
        private Icon _logo;

        private void PaintHeader(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using (var b = new LinearGradientBrush(_header.ClientRectangle, InkDeep, Ink, 0f))
                g.FillRectangle(b, _header.ClientRectangle);
            using (var accent = new SolidBrush(Orange))
                g.FillRectangle(accent, 0, _header.Height - 3, _header.Width, 3);
            // brand globe (embedded multi-size setup.ico)
            if (_logo == null)
            {
                using (var s = typeof(SetupForm).Assembly.GetManifestResourceStream("setup.ico"))
                    if (s != null) _logo = new Icon(s, 48, 48);
            }
            if (_logo != null) g.DrawIcon(_logo, new Rectangle(24, 23, 48, 48));
        }

        private static void StyleButton(Button b, bool primary)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = Color.FromArgb(212, 212, 216);
            b.BackColor = primary ? Orange : Color.White;
            b.ForeColor = primary ? Color.White : Ink;
            b.Font = new Font("Segoe UI Semibold", 9.5f);
            b.Cursor = Cursors.Hand;
        }
    }
}
