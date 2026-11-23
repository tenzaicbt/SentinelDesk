using System.Management;
using Microsoft.Win32;
using SentinelDesk.Agent.Core;
using SentinelDesk.Agent.Native;
using SentinelDesk.Agent.Pipeline;
using SentinelDesk.Shared;

namespace SentinelDesk.Agent.Collectors;

/// <summary>Interactive logon/logoff detection via WTS session enumeration (lock/unlock come from the tray).</summary>
public sealed class SessionCollector(TelemetryAggregator agg, AgentState state, PolicyStore policy, ILogger<SessionCollector> log) : BackgroundService
{
    private Dictionary<int, string> _known = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var first = true;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try
            {
                var sessions = NativeMethods.EnumerateSessions();
                var active = sessions.Where(s => s.State == NativeMethods.WTS_CONNECTSTATE_CLASS.Active).ToList();
                var console = NativeMethods.WTSGetActiveConsoleSessionId();
                var primary = active.FirstOrDefault(s => s.SessionId == console);
                if (primary.User is null && active.Count > 0) primary = active[0];
                state.CurrentUser = primary.User is null ? null : string.IsNullOrEmpty(primary.Domain) ? primary.User : $"{primary.Domain}\\{primary.User}";

                var now = sessions.ToDictionary(s => s.SessionId, s => string.IsNullOrEmpty(s.Domain) ? s.User! : $"{s.Domain}\\{s.User}");
                if (!first && policy.Current.Collectors.Sessions)
                {
                    foreach (var (id, user) in now.Where(kv => !_known.ContainsKey(kv.Key)))
                        agg.AddEvent(new AgentEvent(DateTime.UtcNow, "session.logon", "info", $"{user} signed in (session {id})", new() { ["session"] = id.ToString() }));
                    foreach (var (id, user) in _known.Where(kv => !now.ContainsKey(kv.Key)))
                        agg.AddEvent(new AgentEvent(DateTime.UtcNow, "session.logoff", "info", $"{user} signed out (session {id})", new() { ["session"] = id.ToString() }));
                }
                _known = now;
                first = false;
            }
            catch (Exception ex) { log.LogDebug(ex, "Session poll failed"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}

/// <summary>USB mass-storage logging and optional blocking (USBSTOR service start type).</summary>
public sealed class UsbCollector(TelemetryAggregator agg, PolicyStore policy, AgentState state, ILogger<UsbCollector> log) : BackgroundService
{
    private ManagementEventWatcher? _watcher;

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            _watcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_VolumeChangeEvent WHERE EventType = 2 OR EventType = 3"));
            _watcher.EventArrived += (_, e) =>
            {
                try { OnVolume(e.NewEvent); } catch (Exception ex) { log.LogDebug(ex, "USB event handling failed"); }
            };
            _watcher.Start();
            ct.Register(() => { try { _watcher.Stop(); _watcher.Dispose(); } catch { } });
        }
        catch (Exception ex) { log.LogWarning(ex, "USB watcher unavailable"); }
        return Task.CompletedTask;
    }

    private void OnVolume(ManagementBaseObject ev)
    {
        if (!policy.Current.Collectors.UsbLog) return;
        var drive = ev["DriveName"]?.ToString();
        var type = Convert.ToInt32(ev["EventType"]);
        if (string.IsNullOrEmpty(drive)) return;
        if (type == 2)
        {
            var di = new DriveInfo(drive);
            if (di.DriveType != DriveType.Removable) return;
            string? model = null, serial = null;
            try
            {
                using var s = new ManagementObjectSearcher("SELECT Model, SerialNumber FROM Win32_DiskDrive WHERE InterfaceType='USB'");
                foreach (var o in s.Get()) { model = o["Model"]?.ToString(); serial = o["SerialNumber"]?.ToString()?.Trim(); }
            }
            catch { }
            var size = di.IsReady ? $"{di.TotalSize / 1_000_000_000.0:0.#} GB" : "";
            agg.AddEvent(new AgentEvent(DateTime.UtcNow, "usb.inserted", "info", $"USB storage connected: {model ?? di.VolumeLabel} {size} ({drive}) by {state.CurrentUser}",
                new() { ["drive"] = drive, ["model"] = model ?? "", ["serial"] = serial ?? "", ["label"] = di.IsReady ? di.VolumeLabel : "" }));
        }
        else
        {
            agg.AddEvent(new AgentEvent(DateTime.UtcNow, "usb.removed", "info", $"Removable volume {drive} removed"));
        }
    }
}
