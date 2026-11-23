using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using SentinelDesk.Agent.Core;
using SentinelDesk.Agent.Enforcement;
using SentinelDesk.Agent.Live;
using SentinelDesk.Agent.Native;
using SentinelDesk.Agent.Pipeline;
using SentinelDesk.Shared;

namespace SentinelDesk.Agent.Ipc;

/// <summary>
/// Named-pipe server for the per-user tray app. The pipe ACL lets users talk to the agent, but the protocol has
/// no control verbs (no stop/disable) and, when running as a service, only the signed tray binary from the install
/// folder is accepted as a client. Also (re)launches the tray in every interactive session.
/// </summary>
public sealed class TrayManager(AgentState state, TelemetryAggregator agg, PolicyStore policy, AgentConfig config, BlockEnforcer blocker,
    ServerClient server, IServiceProvider services, ILogger<TrayManager> log) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly ConcurrentDictionary<int, DateTime> _launched = new();

    private sealed class Client(NamedPipeServerStream pipe, int sessionId, int pid)
    {
        public NamedPipeServerStream Pipe { get; } = pipe;
        public int SessionId { get; set; } = sessionId;
        public int Pid { get; } = pid;
        public SemaphoreSlim WriteLock { get; } = new(1);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        policy.Changed += p => { _ = BroadcastAsync(ConfigMessage()); };
        blocker.Notify += (title, body) => { _ = NotifyAsync(title, body); };
        _ = Task.Run(() => LauncherLoop(ct), ct);
        _ = Task.Run(() => StatusLoop(ct), ct);

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(ct);
                var accepted = pipe;
                pipe = null;
                _ = Task.Run(() => HandleClientAsync(accepted, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Pipe server error");
                pipe?.Dispose();
                await Task.Delay(2000, ct);
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        try { sec.SetOwner(WindowsIdentity.GetCurrent().User!); } catch { }
        return NamedPipeServerStreamAcl.Create(PipeNames.Agent, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 64 * 1024, 64 * 1024, sec);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var sessionId = -1;
        var clientPid = -1;
        try
        {
            if (NativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid))
            {
                clientPid = (int)pid;
                using var proc = Process.GetProcessById((int)pid);
                sessionId = proc.SessionId;
                if (AgentPaths.IsSystem())
                {
                    string? path = null;
                    try { path = proc.MainModule?.FileName; } catch { }
                    if (!string.Equals(path, AgentPaths.TrayExe, StringComparison.OrdinalIgnoreCase))
                    {
                        log.LogWarning("Rejected pipe client {Path} (pid {Pid})", path, pid);
                        agg.AddEvent(new AgentEvent(DateTime.UtcNow, "tamper.pipeSpoof", "warning", $"Unexpected process connected to agent pipe: {path ?? "unknown"}"));
                        return;
                    }
                }
            }
            var client = new Client(pipe, sessionId, clientPid);
            _clients[id] = client;
            await SendAsync(client, ConfigMessage());
            await SendAsync(client, StatusMessage());

            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                if (line.Length > 256 * 1024) continue;
                PipeMessage? msg;
                try { msg = Json.Deserialize<PipeMessage>(line); } catch { continue; }
                if (msg is not null) Handle(client, msg);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        catch (Exception ex) { log.LogDebug(ex, "Pipe client error"); }
        finally
        {
            _clients.TryRemove(id, out _);
            pipe.Dispose();
        }
    }

    private void Handle(Client client, PipeMessage msg)
    {
        if (client.SessionId >= 0) state.TraySessions[client.SessionId] = DateTime.UtcNow;
        var c = policy.Current.Collectors;
        var allowed = policy.ActivityCollectionAllowed(DateTime.UtcNow);
        switch (msg.Type)
        {
            case "current":
                state.UserIdle = msg.Idle;
                if (msg.CurrentApp is { } app && (policy.AppExcluded(app) || !allowed))
                {
                    state.CurrentApp = null; state.CurrentTitle = null;
                }
                else
                {
                    state.CurrentApp = msg.CurrentApp;
                    state.CurrentTitle = msg.CurrentApp is null ? null : policy.FilterTitle(msg.CurrentApp, msg.CurrentTitle);
                }
                break;
            case "blocked" when msg.CurrentApp is { Length: > 0 } blockedApp:
                // sent by the tray's IFEO notice when Windows refused to start a blocked program
                if (blocker.IsAppBlocked(blockedApp)) blocker.ReportBlockedApp(blockedApp, msg.User ?? state.CurrentUser);
                break;
            case "accessRequest":
                _ = Task.Run(() => AccessRequestAsync(client, msg));
                break;
            case "unlock" when msg.Token is { Length: > 0 } code:
                _ = Task.Run(async () =>
                {
                    var (ok, text) = await blocker.TryUnlockAsync(code, msg.User ?? state.CurrentUser);
                    await SendAsync(client, new PipeMessage { Type = "unlockResult", State = ok ? "ok" : "error", StatusText = text });
                });
                break;
            case "reenroll":
                _ = Task.Run(() => ReenrollAsync(client, msg));
                break;
            case "usage" when c.AppUsage && allowed && msg.Usage is not null:
                foreach (var u in msg.Usage.Take(500))
                {
                    if (policy.AppExcluded(u.App)) continue;
                    agg.AddAppUsage(u with
                    {
                        Title = policy.FilterTitle(u.App, u.Title),
                        ActiveSec = Math.Clamp(u.ActiveSec, 0, 3600), IdleSec = Math.Clamp(u.IdleSec, 0, 3600),
                        Time = u.Time.ToUniversalTime()
                    });
                }
                break;
            case "location" when c.Location:
                if (msg.Location is { } loc && Math.Abs(loc.Lat) <= 90 && Math.Abs(loc.Lng) <= 180 && !(loc.Lat == 0 && loc.Lng == 0))
                {
                    state.Location = loc with { AccuracyM = Math.Clamp(loc.AccuracyM, 0, 1_000_000), AtUtc = loc.AtUtc > DateTime.UtcNow ? DateTime.UtcNow : loc.AtUtc.ToUniversalTime() };
                    state.LocationError = null;
                }
                else state.LocationError = msg.LocationError is { Length: > 0 } err ? (err.Length > 200 ? err[..200] : err) : "No position available";
                break;
            case "session" when c.Sessions && msg.SessionEvent is "lock" or "unlock":
                state.Locked = msg.SessionEvent == "lock";
                agg.AddEvent(new AgentEvent(DateTime.UtcNow, $"session.{msg.SessionEvent}", "info",
                    $"Workstation {(msg.SessionEvent == "lock" ? "locked" : "unlocked")} by {msg.User}"));
                break;
        }
    }

    private PipeMessage ConfigMessage() => new()
    {
        Type = "config", Tray = policy.Current.Tray, CaptureWindowTitles = policy.Current.Collectors.CaptureWindowTitles,
        IdleThresholdSec = policy.Current.Collectors.IdleThresholdSec, ExcludeApps = policy.Current.Privacy.ExcludeApps,
        DeviceId = config.DeviceId == Guid.Empty ? null : config.DeviceId.ToString(), AgentVersion = AgentInfo.Version, Server = config.ServerUrl,
        LocationEnabled = policy.Current.Collectors.Location, LocationIntervalMin = Math.Clamp(policy.Current.Collectors.LocationIntervalMin, 1, 120)
    };

    private PipeMessage StatusMessage() => new()
    {
        Type = "status", State = state.ConnectionState, StatusText = state.StatusDetail, Server = config.ServerUrl,
        LastUploadUtc = state.LastUploadUtc, QueueDepth = state.QueueDepth, AgentVersion = AgentInfo.Version
    };

    /// <summary>Forwards an access request the user typed in the tray to the console.</summary>
    private async Task AccessRequestAsync(Client client, PipeMessage msg)
    {
        string text; var ok = false;
        var kind = msg.Title is "websites" or "apps" or "usb" ? msg.Title : "websites";
        var value = msg.CurrentApp?.Trim() ?? "";
        if (value.Length is 0 or > 300) text = "Enter the website or program you need.";
        else
        {
            try
            {
                var ack = await server.PostAccessRequestAsync(new AccessRequestDto(kind, value, msg.Body, msg.User ?? state.CurrentUser), CancellationToken.None);
                ok = true;
                text = ack.Duplicate ? "You already asked for this. Your administrator will answer soon." : "Request sent. You will get a message when your administrator answers.";
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Access request failed");
                text = "The request could not be sent. Check that the computer is connected and try again.";
            }
        }
        await SendAsync(client, new PipeMessage { Type = "accessRequestResult", State = ok ? "ok" : "error", StatusText = text });
    }

    private readonly SemaphoreSlim _reenrollLock = new(1);
    private readonly ConcurrentQueue<DateTime> _reenrollAttempts = new();

    /// <summary>
    /// Re-enrolls this device with a new enrollment token, entered by an administrator in the tray ("Reconnect to server").
    /// Only accepted from the signed tray binary running elevated as an administrator. A failure keeps the current connection.
    /// </summary>
    private async Task ReenrollAsync(Client client, PipeMessage msg)
    {
        async Task Reply(bool ok, string text)
        {
            log.LogInformation("Reconnect request: {Result}", text);
            await SendAsync(client, new PipeMessage { Type = "reenrollResult", State = ok ? "ok" : "error", StatusText = text, Server = config.ServerUrl });
        }

        if (client.Pid <= 0 || !NativeMethods.IsElevatedAdministrator(client.Pid))
        {
            agg.AddEvent(new AgentEvent(DateTime.UtcNow, "tamper.reenrollDenied", "warning", $"Reconnect attempted without administrator rights by {msg.User ?? state.CurrentUser ?? "unknown user"}"));
            await Reply(false, "Administrator rights are needed. Choose \"Reconnect to server\" again and approve the Windows prompt.");
            return;
        }
        // at most 5 attempts in 10 minutes
        var now = DateTime.UtcNow;
        while (_reenrollAttempts.TryPeek(out var t) && now - t > TimeSpan.FromMinutes(10)) _reenrollAttempts.TryDequeue(out _);
        if (_reenrollAttempts.Count >= 5) { await Reply(false, "Too many attempts. Wait 10 minutes and try again."); return; }
        _reenrollAttempts.Enqueue(now);

        var token = msg.Token?.Trim() ?? "";
        if (token.Length is < 6 or > 200) { await Reply(false, "Paste the enrollment token from the console (it starts with SD-)."); return; }
        var url = string.IsNullOrWhiteSpace(msg.Server) ? config.ServerUrl : msg.Server.Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        { await Reply(false, "The server address is not valid, e.g. https://monitor.company.lk:5443"); return; }
        var sameServer = string.Equals(url, config.ServerUrl, StringComparison.OrdinalIgnoreCase);

        if (!await _reenrollLock.WaitAsync(0)) { await Reply(false, "A reconnect is already in progress."); return; }
        try
        {
            // a new server address needs its certificate trusted again; the same server keeps its pinned certificate
            if (!sameServer) config.PinnedServerCertSha256 = null;
            await Enrollment.EnrollAsync(url, token, null, trustOnFirstUse: !sameServer && msg.TrustServer, log, CancellationToken.None, target: config);
            server.Reset();
            services.GetRequiredService<HeartbeatWorker>().Kick();
            agg.AddEvent(new AgentEvent(DateTime.UtcNow, "agent.reconnected", "info",
                $"Device reconnected with a new enrollment token by {msg.User ?? "an administrator"}{(sameServer ? "" : $" (server changed to {url})")}"));
            await Reply(true, config.Status == "active" ? "Reconnected. This computer is sending data again."
                : "Reconnected. An administrator must approve this computer in the console (Devices).");
        }
        catch (Exception ex)
        {
            var reason = ex.Message.Contains("401") || ex.Message.Contains("403") || ex.Message.Contains("400") ? "The token was not accepted. It may be expired, used up or mistyped. Create a new one in the console."
                : ex is HttpRequestException or TaskCanceledException ? $"Could not reach {url}. Check the address and the network."
                : ex.Message;
            await Reply(false, reason);
        }
        finally { _reenrollLock.Release(); }
    }

    public Task NotifyAsync(string title, string body) => BroadcastAsync(new PipeMessage { Type = "notify", Title = title, Body = body });

    private async Task BroadcastAsync(PipeMessage msg)
    {
        foreach (var c in _clients.Values) await SendAsync(c, msg);
    }

    private static async Task SendAsync(Client c, PipeMessage msg)
    {
        await c.WriteLock.WaitAsync();
        try
        {
            var bytes = Encoding.UTF8.GetBytes(Json.Serialize(msg) + "\n");
            await c.Pipe.WriteAsync(bytes);
            await c.Pipe.FlushAsync();
        }
        catch { }
        finally { c.WriteLock.Release(); }
    }

    private async Task StatusLoop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(ct)) await BroadcastAsync(StatusMessage());
    }

    /// <summary>Ensures the tray runs in every active session; a tray that disappears while the session lives is a tamper signal.</summary>
    private async Task LauncherLoop(CancellationToken ct)
    {
        if (!AgentPaths.IsSystem() || !File.Exists(AgentPaths.TrayExe)) return;
        await Task.Delay(TimeSpan.FromSeconds(15), ct);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                var trays = Process.GetProcessesByName("SentinelDesk.Tray").Select(p => { var s = p.SessionId; p.Dispose(); return s; }).ToHashSet();
                var active = NativeMethods.EnumerateSessions().Where(x => x.State == NativeMethods.WTS_CONNECTSTATE_CLASS.Active).ToList();
                // forget sessions that ended (logoff is not tampering)
                foreach (var gone in _launched.Keys.Where(k => active.All(a => a.SessionId != k)).ToList()) _launched.TryRemove(gone, out _);
                foreach (var s in active)
                {
                    if (trays.Contains(s.SessionId)) { _launched[s.SessionId] = DateTime.UtcNow; continue; }
                    // the tray was running in this still-active session and is now gone → it was killed
                    if (_launched.TryRemove(s.SessionId, out _))
                        agg.AddEvent(new AgentEvent(DateTime.UtcNow, "tamper.trayKilled", "warning",
                            $"Tray app was closed in {s.Domain}\\{s.User}'s session — restarted"));
                    if (!NativeMethods.StartInSession(s.SessionId, AgentPaths.TrayExe, out var err))
                        log.LogDebug("Tray launch in session {Session} failed: {Error}", s.SessionId, err);
                }
            }
            catch (Exception ex) { log.LogDebug(ex, "Tray launcher error"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
