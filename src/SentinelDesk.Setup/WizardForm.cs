using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using SentinelDesk.Setup.Fluent;

namespace SentinelDesk.Setup
{
    /// <summary>
    /// FluentSetup: Windows 11 Fluent-style wizard for installing, upgrading and removing the SentinelDesk agent.
    /// Steps are shown in the left rail; each page is built in code into the content area.
    /// </summary>
    public sealed class WizardForm : Form, IProgressSink
    {
        private enum Page { Welcome, License, Connection, Key, Progress, Finish, Info }

        private readonly SetupConfig _config;
        private readonly SetupMode _mode;
        private readonly List<Page> _pages = new List<Page>();
        private int _index;
        private bool _busy;
        private int _result = -1;

        private readonly Panel _rail = new Panel { Dock = DockStyle.Left, Width = 236, Padding = new Padding(18, 22, 14, 18) };
        private readonly StepRail _steps = new StepRail();
        private readonly Panel _main = new Panel { Dock = DockStyle.Fill, Padding = new Padding(36, 30, 36, 0) };
        private readonly FluentLabel _title = new FluentLabel("", Theme.Title) { Dock = DockStyle.Top, Height = 44 };
        private readonly FluentLabel _subtitle = new FluentLabel("", Theme.Body, Theme.TextSecondary) { Dock = DockStyle.Top, Height = 44 };
        private readonly Panel _content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 10, 0, 0) };
        private readonly Panel _footer = new Panel { Dock = DockStyle.Bottom, Height = 72 };
        private readonly FluentButton _back = new FluentButton { Text = "Back", Width = 104, Height = 34 };
        private readonly FluentButton _next = new FluentButton { Text = "Next", Width = 120, Height = 34, Accent = true };
        private readonly FluentButton _cancel = new FluentButton { Text = "Cancel", Width = 104, Height = 34 };

        // page state
        private readonly FluentTextBox _server = new FluentTextBox { Placeholder = "https://monitor.company.lk:5443" };
        private readonly FluentTextBox _token = new FluentTextBox { Placeholder = "SD-XXXXXXXXXXXXXXXXXXXXXXXX" };
        private readonly FluentTextBox _key = new FluentTextBox { Password = true, Placeholder = "At least 8 characters" };
        private readonly FluentCheckBox _tofu = new FluentCheckBox { Text = "Trust the server certificate on first connection (private or self-signed certificate)" };
        private readonly FluentCheckBox _accept = new FluentCheckBox { Text = "I accept the license terms" };
        private readonly FluentTextBox _unKey = new FluentTextBox { Password = true, Placeholder = "Uninstall Key or one-time code" };
        private FluentLabel _error;
        private FluentLabel _statusLine;
        private FluentProgress _progress;
        private TextBox _log;

        public int ExitCode => _result < 0 ? 1602 : _result;   // 1602 = cancelled by the user

        public WizardForm(SetupConfig config, SetupMode mode)
        {
            _config = config;
            _mode = mode;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            Text = mode == SetupMode.Uninstall || mode == SetupMode.NotInstalled ? "Remove SentinelDesk Agent" : "SentinelDesk Agent Setup";
            Font = Theme.Body;
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
            ClientSize = new Size(820, 560);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _server.Text = config.ServerUrl;
            _token.Text = config.EnrollToken;
            _key.Text = config.UninstallKey;
            _tofu.Checked = config.TrustOnFirstUse;
            _unKey.Text = config.Uninstall ? config.UninstallKey : null;
            _accept.CheckedChanged += (_, __) => UpdateButtons();

            BuildRail();
            BuildFooter();
            _main.Controls.Add(_content);
            _main.Controls.Add(_subtitle);
            _main.Controls.Add(_title);
            Controls.Add(_main);
            Controls.Add(_footer);
            Controls.Add(_rail);

            switch (mode)
            {
                case SetupMode.Install:
                    _pages.AddRange(new[] { Page.Welcome, Page.License });
                    if (!config.IsPreconfigured) _pages.Add(Page.Connection);
                    _pages.AddRange(new[] { Page.Progress, Page.Finish });
                    break;
                case SetupMode.Upgrade when config.CanUpgrade: _pages.AddRange(new[] { Page.Welcome, Page.Progress, Page.Finish }); break;
                case SetupMode.Uninstall: _pages.AddRange(new[] { Page.Welcome, Page.Key, Page.Progress, Page.Finish }); break;
                default: _pages.Add(Page.Info); break;
            }
            _steps.SetSteps(_pages.ConvertAll(StepName));
            Show(0);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            WindowChrome.Apply(Handle);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy) { e.Cancel = true; return; }   // never interrupt an install / upgrade half-way
            base.OnFormClosing(e);
        }

        private string StepName(Page p)
        {
            switch (p)
            {
                case Page.Welcome: return "Welcome";
                case Page.License: return "License";
                case Page.Connection: return "Connection";
                case Page.Key: return "Uninstall key";
                case Page.Progress: return _mode == SetupMode.Upgrade ? "Upgrade" : _mode == SetupMode.Uninstall ? "Remove" : "Install";
                case Page.Finish: return "Finish";
                default: return "Status";
            }
        }

        // ───────────────────────────── chrome ─────────────────────────────

        private void BuildRail()
        {
            _rail.BackColor = Theme.Rail;
            var brand = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Theme.Rail };
            brand.Paint += (_, e) =>
            {
                var g = e.Graphics;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                var size = Dpi.S(brand, 34);
                try { using (var ico = new Icon(Icon ?? SystemIcons.Application, size, size)) g.DrawIcon(ico, new Rectangle(0, Dpi.S(brand, 2), size, size)); } catch { }
                var x = size + Dpi.S(brand, 10);
                using (var f = new Font(Theme.Subtitle.FontFamily, 12.5f, FontStyle.Bold))
                {
                    TextRenderer.DrawText(g, "Sentinel", f, new Point(x, 0), Theme.Text, TextFormatFlags.NoPadding);
                    var w = TextRenderer.MeasureText(g, "Sentinel", f, Size.Empty, TextFormatFlags.NoPadding).Width;
                    TextRenderer.DrawText(g, "Desk", f, new Point(x + w, 0), Theme.Accent, TextFormatFlags.NoPadding);
                }
                TextRenderer.DrawText(g, "Agent Setup", Theme.Caption, new Point(x, Dpi.S(brand, 22)), Theme.TextSecondary, TextFormatFlags.NoPadding);
            };
            _steps.Dock = DockStyle.Fill;
            _steps.BackColor = Theme.Rail;
            _steps.Padding = new Padding(0, 10, 0, 0);
            var version = new FluentLabel($"Version {Installer.SetupVersion}", Theme.Caption, Theme.TextSecondary) { Dock = DockStyle.Bottom, Height = 40 };
            var credit = new FluentLabel("© 2026 Yohan Koshala", Theme.Caption, Theme.TextSecondary) { Dock = DockStyle.Bottom, Height = 20 };
            _rail.Controls.Add(_steps);
            _rail.Controls.Add(version);
            _rail.Controls.Add(credit);
            _rail.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 14, BackColor = Theme.Rail });
            _rail.Controls.Add(brand);
        }

        private void BuildFooter()
        {
            _footer.BackColor = Theme.Window;
            _footer.Paint += (_, e) => { using (var p = new Pen(Theme.Divider)) e.Graphics.DrawLine(p, 0, 0, _footer.Width, 0); };
            _footer.Resize += (_, __) => LayoutFooter();
            _footer.Controls.AddRange(new Control[] { _back, _next, _cancel });
            _back.Click += (_, __) => { if (!_busy && _index > 0) Show(_index - 1); };
            _next.Click += (_, __) => Next();
            _cancel.Click += (_, __) => Close();
            AcceptButton = _next;
        }

        private void LayoutFooter()
        {
            var right = _footer.Width - Dpi.S(this, 36);
            var y = (_footer.Height - _next.Height) / 2;
            _cancel.Location = new Point(right - _cancel.Width, y);
            _next.Location = new Point(_cancel.Left - Dpi.S(this, 8) - _next.Width, y);
            _back.Location = new Point(_next.Left - Dpi.S(this, 8) - _back.Width, y);
        }

        private void UpdateButtons()
        {
            var page = _pages[_index];
            _back.Visible = _index > 0 && page != Page.Progress && page != Page.Finish && page != Page.Info;
            _cancel.Visible = page != Page.Finish && page != Page.Info;
            _cancel.Enabled = !_busy;
            _next.Enabled = !_busy && (page != Page.License || _accept.Checked);
            var nextPage = _index + 1 < _pages.Count ? _pages[_index + 1] : (Page?)null;
            _next.Text = page == Page.Finish || page == Page.Info ? "Close"
                : page == Page.Progress ? "Next"
                : nextPage == Page.Progress ? (_mode == SetupMode.Upgrade ? "Upgrade" : _mode == SetupMode.Uninstall ? "Remove" : "Install")
                : "Next";
            _next.Visible = page != Page.Progress;
            LayoutFooter();
        }

        // ───────────────────────────── navigation ─────────────────────────────

        private void Show(int index)
        {
            _index = index;
            _steps.Current = index;
            _content.SuspendLayout();
            // keep the fields (they hold what was typed) and dispose everything else of the previous page
            foreach (var keep in new Control[] { _server, _token, _key, _tofu, _accept, _unKey }) keep.Parent = null;
            var old = new List<Control>();
            foreach (Control c in _content.Controls) old.Add(c);
            _content.Controls.Clear();
            foreach (var c in old) c.Dispose();
            _error = null;
            switch (_pages[index])
            {
                case Page.Welcome: BuildWelcome(); break;
                case Page.License: BuildLicense(); break;
                case Page.Connection: BuildConnection(); break;
                case Page.Key: BuildKey(); break;
                case Page.Progress: BuildProgress(); break;
                case Page.Finish: BuildFinish(); break;
                default: BuildInfo(); break;
            }
            _content.ResumeLayout(true);
            UpdateButtons();
            if (_pages[index] == Page.Progress) BeginOperation();
        }

        private void Next()
        {
            var page = _pages[_index];
            if (page == Page.Finish || page == Page.Info) { Close(); return; }
            if (page == Page.Connection && !ValidateConnection()) return;
            if (page == Page.Key && !ValidateKey()) return;
            if (_index + 1 < _pages.Count) Show(_index + 1);
        }

        private bool ValidateConnection()
        {
            var server = _server.Text.Trim();
            if (!(server.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || server.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) || server.Length < 10)
                return Fail("Enter the server address, for example https://monitor.company.lk:5443", _server);
            if (_token.Text.Trim().Length < 6) return Fail("Enter the enrollment token from the console (Agent management).", _token);
            if (_key.Text.Length < 8) return Fail("The uninstall key must be at least 8 characters.", _key);
            _config.ServerUrl = server;
            _config.EnrollToken = _token.Text.Trim();
            _config.UninstallKey = _key.Text;
            _config.TrustOnFirstUse = _tofu.Checked;
            return true;
        }

        private bool ValidateKey()
        {
            if (string.IsNullOrEmpty(_unKey.Text)) return Fail("Enter the Uninstall Key, or a one-time code created for this computer in the console.", _unKey);
            _config.UninstallKey = _unKey.Text;
            return true;
        }

        private bool Fail(string message, Control focus)
        {
            if (_error != null) { _error.Text = message; _error.Visible = true; }
            focus?.Focus();
            return false;
        }

        // ───────────────────────────── pages ─────────────────────────────

        private void SetHeader(string title, string subtitle)
        {
            _title.Text = title;
            _subtitle.Text = subtitle;
        }

        private T Add<T>(T c, int height) where T : Control
        {
            c.Dock = DockStyle.Top;
            c.Height = height;
            _content.Controls.Add(c);
            c.BringToFront();
            return c;
        }

        private void Gap(int h) => Add(new Panel { BackColor = Color.Transparent }, h);

        private Card AddCard(int height)
        {
            var card = Add(new Card(), height);
            return card;
        }

        /// <summary>Usable width inside a card (cards span the content area, with 20 px padding left and right).</summary>
        private int W => _content.ClientSize.Width - 40;

        private void Row(Control parent, int y, string key, string value)
        {
            parent.Controls.Add(new FluentLabel(key, Theme.Body, Theme.TextSecondary) { Location = new Point(20, y), Size = new Size(170, 24) });
            parent.Controls.Add(new FluentLabel(value, Theme.BodyStrong) { Location = new Point(190, y), Size = new Size(W - 170, 24), AutoEllipsis = true });
        }

        private void BuildWelcome()
        {
            switch (_mode)
            {
                case SetupMode.Upgrade:
                    SetHeader("Upgrade SentinelDesk Agent", "A newer version of the agent is ready to install on this computer.");
                    break;
                case SetupMode.Uninstall:
                    SetHeader("Remove SentinelDesk Agent", "Removing the agent requires your organisation's Uninstall Key.");
                    break;
                default:
                    SetHeader("Install SentinelDesk Agent", "Connect this company computer to your SentinelDesk management server.");
                    break;
            }
            var rows = new List<KeyValuePair<string, string>>();
            if (_mode == SetupMode.Upgrade)
            {
                rows.Add(new KeyValuePair<string, string>("Installed version", Installer.InstalledVersion ?? "unknown"));
                rows.Add(new KeyValuePair<string, string>("New version", Installer.SetupVersion));
            }
            else if (_mode == SetupMode.Uninstall)
            {
                rows.Add(new KeyValuePair<string, string>("Installed version", Installer.InstalledVersion ?? "unknown"));
                rows.Add(new KeyValuePair<string, string>("Install folder", Installer.InstallDir));
            }
            else
            {
                rows.Add(new KeyValuePair<string, string>("Version", Installer.SetupVersion));
                if (_config.IsPreconfigured)
                {
                    rows.Add(new KeyValuePair<string, string>("Management server", _config.ServerUrl));
                    if (!string.IsNullOrEmpty(_config.BranchName)) rows.Add(new KeyValuePair<string, string>("Branch", _config.BranchName));
                    else if (!string.IsNullOrEmpty(_config.OrgName)) rows.Add(new KeyValuePair<string, string>("Organization", _config.OrgName));
                }
                else rows.Add(new KeyValuePair<string, string>("Management server", "You enter it on the Connection page"));
            }
            var card = AddCard(32 + rows.Count * 30);
            for (var i = 0; i < rows.Count; i++) Row(card, 16 + i * 30, rows[i].Key, rows[i].Value);
            Gap(16);

            string text;
            if (_mode == SetupMode.Upgrade)
                text = "The SentinelDesk Guard service checks that the new version is signed by your management server, keeps a copy of the current version, installs the new one and goes back to the current version automatically if the agent does not start. The computer keeps its connection and its data.";
            else if (_mode == SetupMode.Uninstall)
                text = "SentinelDesk removes the agent, the watchdog and the tray app, and lifts every block it applied on this computer. Wrong keys are recorded and reported to the console; after 5 wrong keys, removing is locked for 30 minutes.";
            else
                text = "SentinelDesk runs as a protected Windows service with a tray icon. It securely reports device health, inventory, application and network usage to your management server. Administrator rights are required, and afterwards the agent can only be removed with your organisation's Uninstall Key.";
            Add(new FluentLabel(text, Theme.Body, Theme.TextSecondary), 96);
        }

        private void BuildLicense()
        {
            SetHeader("License agreement", "Please read the license terms before installing.");
            var card = AddCard(300);
            card.Padding = new Padding(16, 12, 6, 12);
            var text = new RichTextBox
            {
                Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Card, ForeColor = Theme.Text,
                Font = Theme.Caption, ScrollBars = RichTextBoxScrollBars.Vertical, DetectUrls = false, TabStop = false,
                Text = LicenseText(),
            };
            card.Controls.Add(text);
            Gap(12);
            Add(_accept, 30);
            _accept.BackColor = Theme.Window;
        }

        private static string LicenseText()
        {
            try
            {
                using (var s = typeof(WizardForm).Assembly.GetManifestResourceStream("LICENSE.txt"))
                using (var r = new StreamReader(s)) return r.ReadToEnd();
            }
            catch { return "SentinelDesk is proprietary software created and developed by Yohan Koshala. All rights reserved."; }
        }

        private void BuildConnection()
        {
            SetHeader("Connection settings", "Enter the details from the web console (Agent management → Enrollment).");
            var card = AddCard(318);
            var y = 14;
            void Field(string label, FluentTextBox box, string hint = null)
            {
                card.Controls.Add(new FluentLabel(label, Theme.BodyStrong) { Location = new Point(20, y), Size = new Size(W, 22) });
                y += 24;
                box.Location = new Point(20, y);
                box.Size = new Size(W, 34);
                box.BackColor = Theme.Card;
                card.Controls.Add(box);
                y += 40;
                if (hint != null) { card.Controls.Add(new FluentLabel(hint, Theme.Caption, Theme.TextSecondary) { Location = new Point(20, y - 4), Size = new Size(W, 20) }); y += 18; }
            }
            Field("Management server address", _server);
            Field("Enrollment token", _token);
            Field("Organisation uninstall key", _key, "Needed later, with administrator rights, to remove the agent.");
            _tofu.Location = new Point(20, y + 4);
            _tofu.Size = new Size(W, 44);
            _tofu.BackColor = Theme.Card;
            card.Controls.Add(_tofu);
            Gap(8);
            _error = Add(new FluentLabel("", Theme.Body, Theme.Error) { Visible = false }, 40);
            _server.Focus();
        }

        private void BuildKey()
        {
            SetHeader("Uninstall key", "Enter the key to confirm that you may remove SentinelDesk from this computer.");
            var card = AddCard(150);
            card.Controls.Add(new FluentLabel("Uninstall Key or one-time code", Theme.BodyStrong) { Location = new Point(20, 16), Size = new Size(W, 22) });
            _unKey.Location = new Point(20, 42);
            _unKey.Size = new Size(W, 34);
            _unKey.BackColor = Theme.Card;
            card.Controls.Add(_unKey);
            card.Controls.Add(new FluentLabel("A one-time code for this computer can be created in the console: the computer's page → ⋯ → Generate one-time uninstall code.",
                Theme.Caption, Theme.TextSecondary) { Location = new Point(20, 84), Size = new Size(W, 40) });
            Gap(8);
            _error = Add(new FluentLabel("", Theme.Body, Theme.Error) { Visible = false }, 40);
            _unKey.Focus();
        }

        private void BuildProgress()
        {
            var verb = _mode == SetupMode.Upgrade ? "Upgrading" : _mode == SetupMode.Uninstall ? "Removing" : "Installing";
            SetHeader($"{verb} SentinelDesk", _mode == SetupMode.Install && _config.ServerUrl != null ? $"Connecting to {_config.ServerUrl}" : "This takes about a minute. Keep this window open.");
            var card = AddCard(108);
            _statusLine = new FluentLabel("Preparing…", Theme.BodyStrong) { Location = new Point(20, 18), Size = new Size(W, 24), AutoEllipsis = true };
            _progress = new FluentProgress { Location = new Point(20, 54), Size = new Size(W, 4), Indeterminate = true };
            card.Controls.Add(_statusLine);
            card.Controls.Add(_progress);
            card.Controls.Add(new FluentLabel("Do not turn off or restart the computer.", Theme.Caption, Theme.TextSecondary) { Location = new Point(20, 70), Size = new Size(W, 20) });
            Gap(12);
            var toggle = Add(new LinkLabel
            {
                Text = "Show details", LinkColor = Theme.Accent, ActiveLinkColor = Theme.AccentPressed, LinkBehavior = LinkBehavior.HoverUnderline,
                Font = Theme.Body, BackColor = Theme.Window,
            }, 26);
            Gap(6);
            var logCard = AddCard(170);
            logCard.Visible = false;
            logCard.Padding = new Padding(12, 10, 4, 10);
            _log = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
                BackColor = Theme.Card, ForeColor = Theme.TextSecondary, Font = Theme.Mono, WordWrap = true,
            };
            logCard.Controls.Add(_log);
            toggle.LinkClicked += (_, __) => { logCard.Visible = !logCard.Visible; toggle.Text = logCard.Visible ? "Hide details" : "Show details"; };
        }

        private void BuildFinish()
        {
            var ok = _result == 0;
            string title, sub, body;
            switch (_mode)
            {
                case SetupMode.Upgrade:
                    title = ok ? "Upgrade complete" : "The upgrade did not complete";
                    sub = ok ? $"SentinelDesk {Installer.SetupVersion} is installed and running." : $"SentinelDesk {Installer.InstalledVersion ?? ""} is still installed and running.";
                    body = ok ? "The computer kept its connection, settings and data."
                        : _result == 20 ? "The new version did not start, so the Guard service put the previous version back automatically. The details are in the console's event log for this computer."
                        : _result == 21 ? "The Guard service refused the package because its signature did not match your management server. Nothing was changed."
                        : _result == 12 ? "This setup file is not signed by your management server. Use the office installer from the console, or roll the update out from Releases."
                        : "Nothing was changed. See the log for details.";
                    break;
                case SetupMode.Uninstall:
                    title = ok ? "SentinelDesk was removed" : "SentinelDesk was not removed";
                    sub = ok ? "The agent, the watchdog and the tray app are gone, and every block was lifted." : "Check the Uninstall Key. Wrong keys are recorded and reported.";
                    body = ok ? "You can close this window." : "If you do not have the key, ask your IT team for a one-time code for this computer.";
                    break;
                default:
                    title = ok ? "SentinelDesk is installed" : "SentinelDesk could not be installed";
                    sub = ok ? "This computer is protected and connected to the management server." : "Anything that was already set up has been removed again.";
                    body = ok ? "The tray icon appears within about 30 seconds."
                        : "Check that this computer can reach the server (firewall, port 5443), that the token is valid and not used up, and the certificate trust option.";
                    break;
            }
            SetHeader(title, sub);
            var card = AddCard(120);
            var badge = new Label
            {
                Text = ok ? "" : "", Font = Theme.Icon(16f), ForeColor = Theme.AccentText, BackColor = Theme.Card,
                TextAlign = ContentAlignment.MiddleCenter, Location = new Point(20, 22), Size = new Size(44, 44),
            };
            badge.Paint += (_, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.Clear(Theme.Card);
                using (var b = new SolidBrush(ok ? Theme.Success : Theme.Error)) e.Graphics.FillEllipse(b, 0, 0, badge.Width - 1, badge.Height - 1);
                TextRenderer.DrawText(e.Graphics, badge.Text, badge.Font, badge.ClientRectangle, Theme.Dark ? Theme.Rgb(0x1A1A1A) : Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            card.Controls.Add(badge);
            card.Controls.Add(new FluentLabel(body, Theme.Body, Theme.Text) { Location = new Point(80, 22), Size = new Size(W - 60, 80) });
            if (!ok)
            {
                Gap(12);
                var open = Add(new LinkLabel { Text = "Open the setup log", LinkColor = Theme.Accent, LinkBehavior = LinkBehavior.HoverUnderline, Font = Theme.Body, BackColor = Theme.Window }, 26);
                open.LinkClicked += (_, __) => { try { Process.Start("notepad.exe", Installer.LogFile); } catch { } };
            }
        }

        private void BuildInfo()
        {
            if (_mode == SetupMode.NotInstalled)
            {
                SetHeader("SentinelDesk is not installed", "There is nothing to remove on this computer.");
                _result = 0;
                return;
            }
            if (_mode == SetupMode.Upgrade)
            {
                SetHeader("This setup file cannot upgrade", $"SentinelDesk {Installer.InstalledVersion ?? ""} is installed on this computer.");
                var c2 = AddCard(92);
                Row(c2, 16, "Installed version", Installer.InstalledVersion ?? "unknown");
                Row(c2, 46, "This setup", Installer.SetupVersion);
                Gap(14);
                Add(new FluentLabel("Only setup files signed by your management server can upgrade an installed agent, so a copied or modified file cannot replace it. "
                    + "Download the office installer for this branch from the web console (Agent management → Enrollment), or roll the new version out from Agent management → Releases.",
                    Theme.Body, Theme.Caution), 80);
                _result = 12;
                return;
            }
            SetHeader("SentinelDesk is already installed", "This computer is already protected and connected. No action is needed.");
            var card = AddCard(92);
            Row(card, 16, "Installed version", Installer.InstalledVersion ?? "unknown");
            Row(card, 46, "This setup", Installer.SetupVersion);
            Gap(14);
            Add(new FluentLabel("To install a newer version, use a newer setup file, or roll the update out from the web console (Agent management → Releases).",
                Theme.Body, Theme.TextSecondary), 60);
            _result = 0;
        }

        // ───────────────────────────── operation ─────────────────────────────

        private void BeginOperation()
        {
            _busy = true;
            ControlBox = false;
            UpdateButtons();
            var t = new Thread(() =>
            {
                int code;
                try
                {
                    code = _mode == SetupMode.Upgrade ? Installer.Upgrade(_config, this)
                        : _mode == SetupMode.Uninstall ? Installer.Uninstall(_config, this)
                        : Installer.Install(_config, this);
                }
                catch (Exception ex) { Log("ERROR: " + ex.Message); code = 1; }
                BeginInvoke((Action)(() =>
                {
                    _busy = false;
                    ControlBox = true;
                    _result = code;
                    Show(_pages.IndexOf(Page.Finish));
                }));
            }) { IsBackground = true };
            t.Start();
        }

        public void Status(string text, int? percent = null)
        {
            if (IsDisposed) return;
            BeginInvoke((Action)(() =>
            {
                if (_statusLine == null || _statusLine.IsDisposed) return;
                if (!string.IsNullOrEmpty(text)) _statusLine.Text = text;
                if (percent is int p) { _progress.Indeterminate = false; _progress.Value = p; }
            }));
        }

        public void Log(string line)
        {
            if (IsDisposed) return;
            BeginInvoke((Action)(() =>
            {
                if (_log == null || _log.IsDisposed) return;
                _log.AppendText(line + Environment.NewLine);
            }));
        }
    }
}
