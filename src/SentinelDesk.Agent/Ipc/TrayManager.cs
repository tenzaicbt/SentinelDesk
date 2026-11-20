using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using SentinelDesk.Agent.Core;
using SentinelDesk.Agent.Native;
using SentinelDesk.Agent.Pipeline;
using SentinelDesk.Shared;

namespace SentinelDesk.Agent.Ipc;

/// <summary>
/// Named-pipe server for the per-user tray app. The pipe ACL lets users talk to the agent, but the protocol has
/// no control verbs (no stop/disable) and, when running as a service, only the signed tray binary from the install
/// folder is accepted as a client. Also (re)launches the tray in every interactive session.
/// </summary>
public sealed class TrayManager(AgentState state, TelemetryAggregator agg, PolicyStore policy, AgentConfig config, ILogger<TrayManager> log) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly ConcurrentDictionary<int, DateTime> _launched = new();

    private sealed class Client(NamedPipeServerStream pipe, int sessionId)
    {
        public NamedPipeServerStream Pipe { get; } = pipe;
        public int SessionId { get; set; } = sessionId;
        public SemaphoreSlim WriteLock { get; } = new(1);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        policy.Changed += p => { _ = BroadcastAsync(ConfigMessage()); };
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
        try
        {
            if (NativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid))
            {
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
            var client = new Client(pipe, sessionId);
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
        DeviceId = config.DeviceId == Guid.Empty ? null : config.DeviceId.ToString(), AgentVersion = AgentInfo.Version, Server = config.ServerUrl
    };

    private PipeMessage StatusMessage() => new()
    {
        Type = "status", State = state.ConnectionState, StatusText = state.StatusDetail, Server = config.ServerUrl,
        LastUploadUtc = state.LastUploadUtc, QueueDepth = state.QueueDepth, AgentVersion = AgentInfo.Version
    };

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
