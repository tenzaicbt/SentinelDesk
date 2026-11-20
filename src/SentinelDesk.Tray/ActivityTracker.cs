using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SentinelDesk.Shared;

namespace SentinelDesk.Tray;

/// <summary>
/// Foreground-application time tracking in the user's session (1 s sampling, 1-minute rollups).
/// Only the active app name and (policy permitting) window title are read — never keystrokes or screen content.
/// </summary>
public sealed class ActivityTracker
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    private readonly Dictionary<(string App, string? Title), int> _active = new();
    private int _idle;
    private DateTime _minute = Minute(DateTime.UtcNow);
    private readonly Dictionary<int, string> _pidCache = new();

    public string CaptureTitles { get; set; } = "masked";
    public int IdleThresholdSec { get; set; } = 300;
    public HashSet<string> ExcludeApps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Locked { get; set; }

    public string? CurrentApp { get; private set; }
    public string? CurrentTitle { get; private set; }
    public bool Idle { get; private set; }

    private static DateTime Minute(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    /// <summary>Called once per second. Returns a finished minute of usage when the minute rolls over.</summary>
    public List<AppUsageSample>? Tick(string user)
    {
        List<AppUsageSample>? flushed = null;
        var now = DateTime.UtcNow;
        var minute = Minute(now);
        if (minute != _minute)
        {
            flushed = _active.Select(kv => new AppUsageSample(_minute, user, kv.Key.App, kv.Key.Title, kv.Value, 0)).ToList();
            if (_idle > 0) flushed.Add(new AppUsageSample(_minute, user, "(idle)", null, 0, _idle));
            _active.Clear();
            _idle = 0;
            _minute = minute;
        }

        Idle = Locked || IdleSeconds() >= IdleThresholdSec;
        if (Idle) { _idle++; CurrentApp = null; CurrentTitle = null; return flushed; }

        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) { _idle++; return flushed; }
        GetWindowThreadProcessId(hwnd, out var pid);
        var app = ProcessName((int)pid);
        if (app is null || ExcludeApps.Contains(app)) { CurrentApp = null; CurrentTitle = null; return flushed; }

        string? title = null;
        if (CaptureTitles != "none")
        {
            var sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);
            title = sb.Length > 0 ? sb.ToString() : null;
        }
        CurrentApp = app;
        CurrentTitle = title;
        var key = (app, title);
        _active[key] = _active.GetValueOrDefault(key) + 1;
        return flushed;
    }

    private string? ProcessName(int pid)
    {
        if (_pidCache.TryGetValue(pid, out var n)) return n;
        try
        {
            using var p = Process.GetProcessById(pid);
            n = p.ProcessName + ".exe";
            if (n.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)) return n; // UWP host — don't cache
            if (_pidCache.Count > 500) _pidCache.Clear();
            return _pidCache[pid] = n;
        }
        catch { return null; }
    }

    private static int IdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return (int)((uint)Environment.TickCount - info.dwTime) / 1000;
    }
}
