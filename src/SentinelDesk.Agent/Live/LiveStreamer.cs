using System.Diagnostics;
using System.Net.NetworkInformation;
using SentinelDesk.Agent.Collectors;
using SentinelDesk.Agent.Native;
using SentinelDesk.Agent.Pipeline;
using SentinelDesk.Shared;

namespace SentinelDesk.Agent.Live;

/// <summary>
/// On-demand high-rate telemetry: while a console user watches this device the server sends "startStream"
/// (renewed every ~15 s) and we push a <see cref="LiveSample"/> every interval over the existing SignalR
/// channel - fire-and-forget, so end-to-end latency is network latency only. Stops on "stopStream", when the
/// lease lapses or when the channel drops. Uses its own CPU/NIC baselines so the 5 s collector is unaffected.
/// </summary>
public sealed class LiveStreamer(AgentState state, ILogger<LiveStreamer> log)
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Func<LiveSample, Task>? _send;
    private int _intervalMs = 500;
    private long _leaseUntil; // Environment.TickCount64

    private ulong _prevIdle, _prevKernel, _prevUser;
    private readonly Dictionary<string, (long In, long Out)> _prevNic = new();

    public void Start(int intervalMs, int leaseSec, Func<LiveSample, Task> send)
    {
        lock (_gate)
        {
            _intervalMs = Math.Clamp(intervalMs, 250, 5000);
            _leaseUntil = Environment.TickCount64 + Math.Clamp(leaseSec, 10, 300) * 1000L;
            _send = send;
            if (_cts is not null) return; // already running: this was a lease renewal / interval change
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _ = Task.Run(() => RunAsync(ct), ct);
        }
        log.LogInformation("Live stream started ({Interval} ms)", _intervalMs);
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_cts is null) return;
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }
        log.LogInformation("Live stream stopped");
    }

    private async Task RunAsync(CancellationToken ct)
    {
        SampleCpu();
        SampleNics();
        var clock = Stopwatch.StartNew();
        long seq = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int interval;
                Func<LiveSample, Task>? send;
                lock (_gate) { interval = _intervalMs; send = _send; }
                await Task.Delay(interval, ct);

                if (Environment.TickCount64 > Interlocked.Read(ref _leaseUntil)) { Stop(); return; }

                var secs = Math.Max(0.05, clock.Elapsed.TotalSeconds);
                clock.Restart();
                var (dIn, dOut) = SampleNics();
                var sample = new LiveSample
                {
                    Seq = ++seq, AgentTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), IntervalMs = interval,
                    Cpu = SampleCpu(), Ram = SampleRam(),
                    NetInBps = (long)(dIn / secs), NetOutBps = (long)(dOut / secs),
                    CurrentApp = state.CurrentApp, CurrentTitle = state.CurrentTitle, UserIdle = state.UserIdle, Locked = state.Locked,
                };
                if (send is null) continue;
                try { await send(sample); }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // channel dropped: the server restarts the stream when the agent reconnects
                    log.LogDebug("Live sample send failed: {Message}", ex.Message);
                    Stop();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
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

    /// <summary>Bytes since the previous call across physical adapters (VPN excluded to avoid double counting).</summary>
    private (long In, long Out) SampleNics()
    {
        long totalIn = 0, totalOut = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var type = MetricsCollector.Classify(nic);
            if (type is null || type == "VPN" || nic.OperationalStatus != OperationalStatus.Up) continue;
            IPInterfaceStatistics stats;
            try { stats = nic.GetIPStatistics(); } catch { continue; }
            var cur = (stats.BytesReceived, stats.BytesSent);
            if (_prevNic.TryGetValue(nic.Id, out var prev))
            {
                totalIn += cur.BytesReceived >= prev.In ? cur.BytesReceived - prev.In : 0;
                totalOut += cur.BytesSent >= prev.Out ? cur.BytesSent - prev.Out : 0;
            }
            _prevNic[nic.Id] = cur;
        }
        return (totalIn, totalOut);
    }
}
