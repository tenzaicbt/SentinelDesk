using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using SentinelDesk.Agent.Core;
using SentinelDesk.Agent.Pipeline;
using SentinelDesk.Shared;

namespace SentinelDesk.Agent.Collectors;

/// <summary>
/// Kernel ETW (per-process TCP/UDP bytes, process start) and DNS-Client ETW (domains resolved) — no kernel driver needed.
/// Requires administrator / LocalSystem; silently disabled otherwise.
/// </summary>
public sealed class EtwCollector(TelemetryAggregator agg, PolicyStore policy, AgentState state, ILogger<EtwCollector> log) : BackgroundService
{
    private const string KernelSessionName = "SentinelDesk-Kernel";
    private const string DnsSessionName = "SentinelDesk-Dns";
    private static readonly Guid DnsClientProvider = new("1C95126E-7EEA-49A9-A3FE-A378B03DDB4D");

    private readonly ConcurrentDictionary<int, string> _pidNames = new();
    private readonly ConcurrentDictionary<(string, string), DateTime> _recentDomains = new();
    // IP -> host name it was resolved for (from DNS answers), so traffic can be attributed to sites
    private readonly ConcurrentDictionary<IPAddress, string> _ipHost = new();
    private volatile HashSet<IPAddress> _localIps = [];
    private DateTime _localIpsAt = DateTime.MinValue;
    private DateTime _activityCheckedAt = DateTime.MinValue;
    private volatile bool _activityAllowed;
    private TraceEventSession? _kernel, _dns;

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        if (!(TraceEventSession.IsElevated() ?? false))
        {
            log.LogWarning("Not elevated: per-process bandwidth and DNS collection disabled (run as service/admin)");
            return Task.CompletedTask;
        }
        ct.Register(StopSessions);
        StartThread("etw-kernel", RunKernel, ct);
        StartThread("etw-dns", RunDns, ct);
        return Task.CompletedTask;
    }

    private void StartThread(string name, Action<CancellationToken> body, CancellationToken ct)
    {
        new Thread(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { body(ct); }
                catch (Exception ex) { log.LogWarning(ex, "{Name} session failed; restarting in 30s", name); }
                if (ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30))) break;
            }
        }) { IsBackground = true, Name = name }.Start();
    }

    private void RunKernel(CancellationToken ct)
    {
        using var session = new TraceEventSession(KernelSessionName) { StopOnDispose = true };
        _kernel = session;
        session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.Process);
        var k = session.Source.Kernel;

        k.ProcessStart += e =>
        {
            _pidNames[e.ProcessID] = e.ImageFileName;
            if (policy.Current.Collectors.Processes && policy.ActivityCollectionAllowed(DateTime.UtcNow) && !IsNoise(e.ImageFileName))
                agg.AddEvent(new AgentEvent(DateTime.UtcNow, "process.start", "info", $"{e.ImageFileName} started",
                    new() { ["pid"] = e.ProcessID.ToString(), ["cmd"] = Trunc(e.CommandLine, 300) ?? "", ["parent"] = e.ParentID.ToString() }));
        };
        k.ProcessStop += e => _pidNames.TryRemove(e.ProcessID, out _);

        k.TcpIpRecv += e => Bytes(e.ProcessID, e.ProcessName, e.size, 0, e.daddr, e.saddr);
        k.TcpIpSend += e => Bytes(e.ProcessID, e.ProcessName, 0, e.size, e.daddr, e.saddr);
        k.TcpIpRecvIPV6 += e => Bytes(e.ProcessID, e.ProcessName, e.size, 0, e.daddr, e.saddr);
        k.TcpIpSendIPV6 += e => Bytes(e.ProcessID, e.ProcessName, 0, e.size, e.daddr, e.saddr);
        k.UdpIpRecv += e => Bytes(e.ProcessID, e.ProcessName, e.size, 0, e.daddr, e.saddr);
        k.UdpIpSend += e => Bytes(e.ProcessID, e.ProcessName, 0, e.size, e.daddr, e.saddr);
        k.UdpIpRecvIPV6 += e => Bytes(e.ProcessID, e.ProcessName, e.size, 0, e.daddr, e.saddr);
        k.UdpIpSendIPV6 += e => Bytes(e.ProcessID, e.ProcessName, 0, e.size, e.daddr, e.saddr);

        log.LogInformation("Kernel ETW session started (network + process)");
        session.Source.Process();
    }

    private void RunDns(CancellationToken ct)
    {
        using var session = new TraceEventSession(DnsSessionName) { StopOnDispose = true };
        _dns = session;
        session.EnableProvider(DnsClientProvider, TraceEventLevel.Informational);
        session.Source.Dynamic.All += e =>
        {
            // 3006 = query issued, 3008 = query completed
            if ((int)e.ID != 3008) return;
            if (!policy.Current.Collectors.WebDomains || !policy.ActivityCollectionAllowed(DateTime.UtcNow)) return;
            var name = (e.PayloadByName("QueryName") as string)?.Trim().TrimEnd('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(name) || !name.Contains('.') || name.EndsWith(".local") || name.EndsWith(".arpa") || name.Length > 200) return;
            if (policy.DomainExcluded(name)) return;
            RememberAnswers(name, e.PayloadByName("QueryResults") as string);
            var process = ResolveName(e.ProcessID, null);
            var key = (name, process ?? "");
            var now = DateTime.UtcNow;
            if (_recentDomains.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(60)) return; // dedup per minute
            _recentDomains[key] = now;
            if (_recentDomains.Count > 20_000) _recentDomains.Clear();
            agg.AddDomain(name, process, state.CurrentUser);
        };
        log.LogInformation("DNS-Client ETW session started");
        session.Source.Process();
    }

    private void Bytes(int pid, string? name, int recv, int send, IPAddress? daddr, IPAddress? saddr)
    {
        if (recv + send <= 0) return;
        var c = policy.Current.Collectors;
        string? proc = null;
        if (c.BandwidthPerProcess)
        {
            proc = ResolveName(pid, name) ?? $"pid:{pid}";
            agg.AddProcessBytes(proc, recv, send);
        }
        if (!c.WebDomains || _ipHost.IsEmpty || !ActivityAllowed()) return;
        var remote = RemoteAddress(daddr, saddr);
        if (remote is null || !_ipHost.TryGetValue(remote, out var host)) return;
        proc ??= ResolveName(pid, name);
        agg.AddDomainBytes(host, proc, state.CurrentUser, recv, send);
        state.AddSiteBytes(host, recv, send);
    }

    /// <summary>Records the A/AAAA answers of a completed query ("1.2.3.4;::ffff:1.2.3.4;type: 5 cdn.example.net;...").</summary>
    private void RememberAnswers(string host, string? results)
    {
        if (string.IsNullOrEmpty(results)) return;
        foreach (var part in results.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IPAddress.TryParse(part, out var ip)) continue; // skips "type: 5 <cname>" entries
            ip = Normalize(ip);
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) continue;
            _ipHost[ip] = host;
        }
        if (_ipHost.Count > 50_000) _ipHost.Clear();
    }

    /// <summary>Kernel TCP/UDP events carry both endpoints; the remote one is whichever is not this machine.</summary>
    private IPAddress? RemoteAddress(IPAddress? daddr, IPAddress? saddr)
    {
        if (DateTime.UtcNow - _localIpsAt > TimeSpan.FromSeconds(60))
        {
            _localIpsAt = DateTime.UtcNow;
            try
            {
                _localIps = NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Select(a => Normalize(a.Address)).ToHashSet();
            }
            catch { }
        }
        var d = daddr is null ? null : Normalize(daddr);
        var s = saddr is null ? null : Normalize(saddr);
        var remote = d is not null && !_localIps.Contains(d) ? d : s;
        return remote is null || IPAddress.IsLoopback(remote) ? null : remote;
    }

    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    /// <summary>Working-hours gate, re-evaluated at most once a second (it is checked for every packet).</summary>
    private bool ActivityAllowed()
    {
        var now = DateTime.UtcNow;
        if (now - _activityCheckedAt > TimeSpan.FromSeconds(1))
        {
            _activityAllowed = policy.ActivityCollectionAllowed(now);
            _activityCheckedAt = now;
        }
        return _activityAllowed;
    }

    private string? ResolveName(int pid, string? etwName)
    {
        if (pid <= 4) return "System";
        if (_pidNames.TryGetValue(pid, out var n)) return n;
        if (!string.IsNullOrEmpty(etwName)) return _pidNames[pid] = etwName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? etwName : etwName + ".exe";
        try
        {
            using var p = Process.GetProcessById(pid);
            return _pidNames[pid] = p.ProcessName + ".exe";
        }
        catch { return null; }
    }

    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "conhost.exe", "svchost.exe", "RuntimeBroker.exe", "backgroundTaskHost.exe", "dllhost.exe", "WmiPrvSE.exe", "SearchProtocolHost.exe",
        "SearchFilterHost.exe", "taskhostw.exe", "smartscreen.exe", "audiodg.exe", "consent.exe", "CompPkgSrv.exe", "sppsvc.exe", "MpCmdRun.exe",
        "SentinelDesk.Agent.exe", "SentinelDesk.Tray.exe", "SentinelDesk.Guard.exe", "rundll32.exe", "WUDFHost.exe", "msedgewebview2.exe"
    };

    private static bool IsNoise(string image) => Noise.Contains(image);

    private static string? Trunc(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    private void StopSessions()
    {
        try { _kernel?.Dispose(); } catch { }
        try { _dns?.Dispose(); } catch { }
    }
}
