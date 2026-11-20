using System.Net.NetworkInformation;
using SentinelDesk.Agent.Core;
using SentinelDesk.Agent.Native;
using SentinelDesk.Agent.Pipeline;
using SentinelDesk.Shared;

namespace SentinelDesk.Agent.Collectors;

/// <summary>CPU / RAM / disk + per-adapter bandwidth (whole-NIC counters, so daily totals are exact).</summary>
public sealed class MetricsCollector(TelemetryAggregator agg, AgentState state, PolicyStore policy, ILogger<MetricsCollector> log) : BackgroundService
{
    private ulong _prevIdle, _prevKernel, _prevUser;
    private readonly Dictionary<string, (long In, long Out)> _prevNic = new();
    private readonly List<(double Cpu, double Ram, double Disk)> _perfWindow = [];
    private readonly Dictionary<string, (string Type, long In, long Out)> _minuteNic = new();
    private DateTime _minute = TelemetryAggregator.Minute(DateTime.UtcNow);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        SampleCpu();
        SampleNics(initial: true);
        var lastPerf = DateTime.UtcNow;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var (inDelta, outDelta) = SampleNics(initial: false);
                state.NetInBps = inDelta / 5;
                state.NetOutBps = outDelta / 5;
                state.AddBytes(inDelta, outDelta);

                state.Cpu = SampleCpu();
                state.Ram = SampleRam();
                state.Disk = SampleDisk();

                if (DateTime.UtcNow - lastPerf >= TimeSpan.FromSeconds(Math.Max(5, policy.Current.Collectors.PerformanceSec)))
                {
                    _perfWindow.Add((state.Cpu, state.Ram, state.Disk));
                    lastPerf = DateTime.UtcNow;
                }

                var minute = TelemetryAggregator.Minute(DateTime.UtcNow);
                if (minute != _minute) RollMinute(minute);
            }
            catch (Exception ex) { log.LogDebug(ex, "Metrics sample failed"); }
        }
    }

    private void RollMinute(DateTime newMinute)
    {
        if (_perfWindow.Count > 0)
        {
            agg.AddPerf(new PerfSample(_minute, Math.Round(_perfWindow.Average(p => p.Cpu), 1), Math.Round(_perfWindow.Average(p => p.Ram), 1), Math.Round(_perfWindow.Average(p => p.Disk), 1)));
            _perfWindow.Clear();
        }
        if (policy.Current.Collectors.BandwidthPerAdapter)
            foreach (var (name, v) in _minuteNic.Where(kv => kv.Value.In > 0 || kv.Value.Out > 0))
                agg.AddAdapter(new AdapterSample(_minute, name, v.Type, v.In, v.Out));
        _minuteNic.Clear();
        _minute = newMinute;
    }

    private double SampleCpu()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var di = idle.Value - _prevIdle; var dk = kernel.Value - _prevKernel; var du = user.Value - _prevUser;
        _prevIdle = idle.Value; _prevKernel = kernel.Value; _prevUser = user.Value;
        var total = dk + du;
        return total == 0 ? 0 : Math.Round(Math.Clamp((total - di) * 100.0 / total, 0, 100), 1);
    }

    private static double SampleRam()
    {
        var m = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        return NativeMethods.GlobalMemoryStatusEx(ref m) ? m.dwMemoryLoad : 0;
    }

    private static double SampleDisk()
    {
        var sys = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
        return sys.IsReady && sys.TotalSize > 0 ? Math.Round((sys.TotalSize - sys.TotalFreeSpace) * 100.0 / sys.TotalSize, 1) : 0;
    }

    private (long In, long Out) SampleNics(bool initial)
    {
        long totalIn = 0, totalOut = 0;
        string? primaryType = null;
        long primaryBytes = -1;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var type = Classify(nic);
            if (type is null || nic.OperationalStatus != OperationalStatus.Up) continue;
            IPInterfaceStatistics stats;
            try { stats = nic.GetIPStatistics(); } catch { continue; }
            var key = nic.Id;
            var cur = (stats.BytesReceived, stats.BytesSent);
            if (_prevNic.TryGetValue(key, out var prev) && !initial)
            {
                var dIn = cur.BytesReceived >= prev.In ? cur.BytesReceived - prev.In : cur.BytesReceived;
                var dOut = cur.BytesSent >= prev.Out ? cur.BytesSent - prev.Out : cur.BytesSent;
                // VPN tunnels carry traffic that is also counted on the physical NIC — report but don't double count totals
                if (type != "VPN") { totalIn += dIn; totalOut += dOut; }
                _minuteNic.TryGetValue(nic.Name, out var m);
                _minuteNic[nic.Name] = (type, m.In + dIn, m.Out + dOut);
                if (type != "VPN" && dIn + dOut > primaryBytes) { primaryBytes = dIn + dOut; primaryType = type; }
            }
            _prevNic[key] = cur;
        }
        if (primaryType is not null) state.ConnectionType = primaryType;
        return (totalIn, totalOut);
    }

    private static readonly string[] VirtualHints = ["hyper-v", "vmware", "virtualbox", "loopback", "pseudo", "wan miniport", "bluetooth", "kernel debug", "teredo", "isatap", "vethernet", "npcap", "docker", "wsl"];
    private static readonly string[] VpnHints = ["vpn", "tap-windows", "wireguard", "fortinet", "anyconnect", "globalprotect", "openvpn", "forticlient", "pulse secure", "sonicwall", "wintun"];

    public static string? Classify(NetworkInterface nic)
    {
        var d = (nic.Description + " " + nic.Name).ToLowerInvariant();
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) return null;
        if (VpnHints.Any(d.Contains) || nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp && !d.Contains("mobile")) return "VPN";
        if (VirtualHints.Any(d.Contains)) return null;
        return nic.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
            NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Mobile",
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => d.Contains("mobile") || d.Contains("rndis") ? "Mobile" : "LAN",
            _ => null
        };
    }
}
