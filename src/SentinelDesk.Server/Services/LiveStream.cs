using Microsoft.AspNetCore.SignalR;
using SentinelDesk.Server.Hubs;

namespace SentinelDesk.Server.Services;

/// <summary>
/// Tracks which console connections are watching which device, so an agent streams high-rate
/// <see cref="Shared.LiveSample"/>s only while somebody is actually looking at it.
/// The first watcher starts the device's stream, the last one to leave stops it.
/// </summary>
public sealed class LiveStreamRegistry(IConfiguration config)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, HashSet<string>> _byDevice = new();
    private readonly Dictionary<string, HashSet<Guid>> _byConnection = new();

    /// <summary>Sample interval the agent is asked to stream at (Live:StreamIntervalMs, 250–5000, default 500).</summary>
    public int IntervalMs { get; } = Math.Clamp(config.GetValue("Live:StreamIntervalMs", 500), 250, 5000);

    /// <summary>Agents stop streaming on their own if not renewed within this window (dead console, lost stop message).</summary>
    public const int LeaseSec = 45;

    /// <returns>true when this is the device's first watcher (stream must be started).</returns>
    public bool Watch(string connectionId, Guid deviceId)
    {
        lock (_gate)
        {
            if (!_byDevice.TryGetValue(deviceId, out var conns)) _byDevice[deviceId] = conns = [];
            var first = conns.Count == 0;
            conns.Add(connectionId);
            if (!_byConnection.TryGetValue(connectionId, out var devs)) _byConnection[connectionId] = devs = [];
            devs.Add(deviceId);
            return first;
        }
    }

    /// <returns>true when the device has no watchers left (stream must be stopped).</returns>
    public bool Unwatch(string connectionId, Guid deviceId)
    {
        lock (_gate)
        {
            if (_byConnection.TryGetValue(connectionId, out var devs)) { devs.Remove(deviceId); if (devs.Count == 0) _byConnection.Remove(connectionId); }
            if (!_byDevice.TryGetValue(deviceId, out var conns) || !conns.Remove(connectionId)) return false;
            if (conns.Count > 0) return false;
            _byDevice.Remove(deviceId);
            return true;
        }
    }

    /// <returns>Devices left without watchers after this console connection closed.</returns>
    public List<Guid> DropConnection(string connectionId)
    {
        lock (_gate)
        {
            if (!_byConnection.Remove(connectionId, out var devs)) return [];
            var emptied = new List<Guid>();
            foreach (var d in devs)
                if (_byDevice.TryGetValue(d, out var conns) && conns.Remove(connectionId) && conns.Count == 0)
                {
                    _byDevice.Remove(d);
                    emptied.Add(d);
                }
            return emptied;
        }
    }

    public bool IsWatched(Guid deviceId) { lock (_gate) return _byDevice.ContainsKey(deviceId); }
    public List<Guid> Watched() { lock (_gate) return [.. _byDevice.Keys]; }
}

/// <summary>Sends start/stop stream signals to an agent's live channel (no-op if the agent isn't connected).</summary>
public sealed class LiveStreamControl(LiveStreamRegistry registry, PresenceStore presence, IHubContext<AgentHub> agents)
{
    public Task StartAsync(Guid deviceId) =>
        presence.AgentConnection(deviceId) is { } c
            ? agents.Clients.Client(c).SendAsync("startStream", registry.IntervalMs, LiveStreamRegistry.LeaseSec)
            : Task.CompletedTask;

    public Task StopAsync(Guid deviceId) =>
        presence.AgentConnection(deviceId) is { } c ? agents.Clients.Client(c).SendAsync("stopStream") : Task.CompletedTask;
}

/// <summary>Renews the stream lease of every watched device well inside <see cref="LiveStreamRegistry.LeaseSec"/>.</summary>
public sealed class LiveStreamRenewer(LiveStreamRegistry registry, LiveStreamControl control, ILogger<LiveStreamRenewer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(ct))
            foreach (var d in registry.Watched())
                try { await control.StartAsync(d); }
                catch (Exception ex) { log.LogDebug("Stream lease renewal for {Device} failed: {Message}", d, ex.Message); }
    }
}
