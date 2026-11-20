using System.Drawing.Drawing2D;
using Microsoft.Win32;
using SentinelDesk.Shared;

namespace SentinelDesk.Tray;

/// <summary>Tray icon + menu. Deliberately has no Exit / Stop / Pause entries.</summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly AgentPipe _pipe = new();
    private readonly ActivityTracker _tracker = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly SynchronizationContext _ui;
    private readonly string _user = $"{Environment.UserDomainName}\\{Environment.UserName}";
    private int _tick;
    private string _state = "starting";
    private StatusForm? _status;

    public TraySettings Settings { get; private set; } = new();
    public PipeMessage? LastStatus { get; private set; }
    public string? DeviceId { get; private set; }
    public string? AgentVersion { get; private set; }

    public TrayContext()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        var menu = new ContextMenuStrip { ShowImageMargin = false, Font = new Font("Segoe UI", 9.5f), Padding = new Padding(4) };
        menu.Items.Add("Status", null, (_, _) => ShowStatus());
        menu.Items.Add("Monitoring notice", null, (_, _) => ShowNotice());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Contact IT", null, (_, _) => MessageBox.Show(Settings.ContactIt, "Contact IT", MessageBoxButtons.OK, MessageBoxIcon.Information));
        menu.Items.Add("About SentinelDesk", null, (_, _) => MessageBox.Show(
            $"SentinelDesk Endpoint Agent {AgentVersion ?? "1.0.0"}\nDevice ID: {DeviceId ?? "not enrolled"}\n\nThis agent is managed by your IT department and cannot be closed by users.",
            "About", MessageBoxButtons.OK, MessageBoxIcon.Information));

        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "SentinelDesk — starting…", Icon = Icons.For("starting") };
        _icon.DoubleClick += (_, _) => ShowStatus();

        _pipe.Message += m => _ui.Post(_ => OnMessage(m), null);
        _pipe.ConnectionChanged += c => _ui.Post(_ => { if (!c) SetState("agent-down", "Agent service not reachable"); }, null);
        _pipe.Start();

        SystemEvents.SessionSwitch += OnSessionSwitch;
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
    }

    private void OnTick()
    {
        var usage = _tracker.Tick(_user);
        if (usage is { Count: > 0 }) _ = _pipe.SendAsync(new PipeMessage { Type = "usage", Usage = usage, User = _user });
        if (++_tick % 5 == 0)
            _ = _pipe.SendAsync(new PipeMessage { Type = "current", CurrentApp = _tracker.CurrentApp, CurrentTitle = _tracker.CurrentTitle, Idle = _tracker.Idle, User = _user });
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        var ev = e.Reason switch { SessionSwitchReason.SessionLock => "lock", SessionSwitchReason.SessionUnlock => "unlock", _ => null };
        if (ev is null) return;
        _tracker.Locked = ev == "lock";
        _ = _pipe.SendAsync(new PipeMessage { Type = "session", SessionEvent = ev, User = _user });
    }

    private void OnMessage(PipeMessage m)
    {
        switch (m.Type)
        {
            case "config":
                if (m.Tray is not null) Settings = m.Tray;
                _tracker.CaptureTitles = m.CaptureWindowTitles ?? "masked";
                _tracker.IdleThresholdSec = m.IdleThresholdSec > 0 ? m.IdleThresholdSec : 300;
                _tracker.ExcludeApps = new HashSet<string>(m.ExcludeApps ?? [], StringComparer.OrdinalIgnoreCase);
                DeviceId = m.DeviceId;
                AgentVersion = m.AgentVersion;
                break;
            case "status":
                LastStatus = m;
                AgentVersion = m.AgentVersion ?? AgentVersion;
                SetState(m.State ?? "offline", m.StatusText);
                _status?.Refresh(this);
                break;
            case "notify":
                _icon.ShowBalloonTip(15000, m.Title ?? "IT Department", m.Body ?? "", ToolTipIcon.Info);
                break;
        }
    }

    private void SetState(string state, string? detail)
    {
        if (_state == state && _icon.Text.Length > 0) return;
        _state = state;
        _icon.Icon = Icons.For(state);
        var text = state switch
        {
            "connected" => "SentinelDesk — protected & connected",
            "offline" => "SentinelDesk — offline (data buffered)",
            "pending" => "SentinelDesk — awaiting approval",
            "agent-down" => "SentinelDesk — agent not running",
            _ => "SentinelDesk — attention required"
        };
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public string State => _state;

    private void ShowStatus()
    {
        if (_status is { IsDisposed: false }) { _status.Activate(); return; }
        _status = new StatusForm();
        _status.Refresh(this);
        _status.Show();
    }

    private void ShowNotice() => new NoticeForm(Settings).Show();

    protected override void ExitThreadCore()
    {
        // only reached on logoff/shutdown
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _icon.Visible = false;
        _icon.Dispose();
        _pipe.Dispose();
        base.ExitThreadCore();
    }
}

public static class Icons
{
    private static readonly Dictionary<string, Icon> Cache = new();

    public static Color ColorFor(string state) => state switch
    {
        "connected" => Color.FromArgb(16, 185, 129),
        "offline" or "pending" or "starting" => Color.FromArgb(245, 158, 11),
        _ => Color.FromArgb(239, 68, 68)
    };

    /// <summary>The SentinelDesk flame (embedded multi-size .ico) - window / taskbar icon.</summary>
    public static Icon App => _app ??= LoadApp(32);
    private static Icon? _app;

    private static Icon LoadApp(int size)
    {
        using var s = typeof(Icons).Assembly.GetManifestResourceStream("SentinelDesk.ico");
        return s is null ? (Icon)SystemIcons.Application.Clone() : new Icon(s, size, size);
    }

    /// <summary>Flame logo with a status dot (green = connected, amber = offline/pending, red = problem).</summary>
    public static Icon For(string state)
    {
        if (Cache.TryGetValue(state, out var ic)) return ic;
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using (var flame = LoadApp(32)) g.DrawIcon(flame, new Rectangle(0, 0, 32, 32));
            using var dot = new SolidBrush(ColorFor(state));
            g.FillEllipse(Brushes.White, 19, 19, 13, 13);
            g.FillEllipse(dot, 21, 21, 9, 9);
        }
        return Cache[state] = Icon.FromHandle(bmp.GetHicon());
    }
}
