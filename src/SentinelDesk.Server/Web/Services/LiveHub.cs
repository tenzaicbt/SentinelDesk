using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using SentinelDesk.Server.Services;

namespace SentinelDesk.Server.Web.Services;

/// <summary>
/// One connection to the server's console hub (/hubs/console) per browser tab, with the admin's token. Pages
/// subscribe to its events and join groups (WatchLive / WatchDevice) while they are shown.
/// </summary>
public sealed class LiveHub(LoopbackAddress loopback, ApiClient api, ILogger<LiveHub> log) : IAsyncDisposable
{
    private HubConnection? _conn;
    private readonly List<(string Join, string Leave, object? Arg)> _groups = [];
    private readonly SemaphoreSlim _lock = new(1, 1);
    private CancellationTokenSource? _cts;

    public bool Connected => _conn?.State == HubConnectionState.Connected;
    public event Action? StateChanged;
    public event Action<LivePresence>? Presence;
    public event Action<LiveSampleMsg>? LiveSample;
    public event Action<AlertMsg>? Alert;
    public event Action<Guid>? InventoryUpdated;
    public event Action<CommandStatusMsg>? CommandStatus;

    public async Task StartAsync()
    {
        if (_conn is not null) return;
        var url = new Uri(loopback.Uri, "/hubs/console");
        _conn = new HubConnectionBuilder()
            .WithUrl(url, o =>
            {
                o.AccessTokenProvider = () => Task.FromResult(api.Token);
                o.Transports = HttpTransportType.WebSockets | HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => loopback.CreateHandler();
                o.WebSocketConfiguration = ws => ws.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                if (!string.IsNullOrEmpty(api.ClientIp)) o.Headers["X-Forwarded-For"] = api.ClientIp;
            })
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30)])
            .AddJsonProtocol(o =>
            {
                o.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
                o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            })
            .Build();

        _conn.On<LivePresence>("presence", p => Presence?.Invoke(p));
        _conn.On<LiveSampleMsg>("liveSample", s => LiveSample?.Invoke(s));
        _conn.On<AlertMsg>("alert", a => Alert?.Invoke(a));
        _conn.On<Guid>("inventoryUpdated", id => InventoryUpdated?.Invoke(id));
        _conn.On<CommandStatusMsg>("commandStatus", c => CommandStatus?.Invoke(c));
        _conn.Reconnecting += _ => { StateChanged?.Invoke(); return Task.CompletedTask; };
        _conn.Reconnected += async _ => { await RejoinAsync(); StateChanged?.Invoke(); };
        _conn.Closed += async _ => { StateChanged?.Invoke(); await ConnectLoopAsync(); };

        _cts = new CancellationTokenSource();
        _ = ConnectLoopAsync();
    }

    private async Task ConnectLoopAsync()
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        while (_conn is not null && !ct.IsCancellationRequested && _conn.State == HubConnectionState.Disconnected)
        {
            try
            {
                await _conn.StartAsync(ct);
                await RejoinAsync();
                StateChanged?.Invoke();
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogDebug(ex, "Console hub connection failed; retrying");
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { return; }
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RejoinAsync()
    {
        (string, string, object?)[] groups;
        lock (_groups) groups = [.. _groups];
        foreach (var (join, _, arg) in groups.Distinct()) await InvokeQuiet(join, arg);
    }

    private async Task InvokeQuiet(string method, object? arg)
    {
        if (_conn?.State != HubConnectionState.Connected) return;
        try
        {
            if (arg is null) await _conn.InvokeAsync(method);
            else await _conn.InvokeAsync(method, arg);
        }
        catch (Exception ex) { log.LogDebug(ex, "Hub call {Method} failed", method); }
    }

    /// <summary>Joins a server group until the returned handle is disposed (re-joined after reconnects).</summary>
    public async Task<IAsyncDisposable> JoinAsync(string join, string leave, object? arg = null)
    {
        var entry = (join, leave, arg);
        bool first;
        lock (_groups) { first = !_groups.Contains(entry); _groups.Add(entry); }
        if (first) await InvokeQuiet(join, arg);
        return new Membership(this, entry);
    }

    private async Task LeaveAsync((string Join, string Leave, object? Arg) entry)
    {
        bool last;
        lock (_groups) { _groups.Remove(entry); last = !_groups.Contains(entry); }
        if (last) await InvokeQuiet(entry.Leave, entry.Arg);
    }

    private sealed class Membership(LiveHub hub, (string, string, object?) entry) : IAsyncDisposable
    {
        private bool _done;
        public async ValueTask DisposeAsync()
        {
            if (_done) return;
            _done = true;
            await hub.LeaveAsync(entry);
        }
    }

    public async Task StopAsync()
    {
        await _lock.WaitAsync();
        try
        {
            _cts?.Cancel();
            if (_conn is not null) { var c = _conn; _conn = null; await c.DisposeAsync(); }
            lock (_groups) _groups.Clear();
        }
        finally { _lock.Release(); }
        StateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
