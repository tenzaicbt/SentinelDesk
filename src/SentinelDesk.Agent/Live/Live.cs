using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.ServiceProcess;
using Microsoft.AspNetCore.SignalR.Client;
using SentinelDesk.Agent.Collectors;
using SentinelDesk.Agent.Core;
using SentinelDesk.Agent.Ipc;
using SentinelDesk.Agent.Pipeline;
using SentinelDesk.Shared;

namespace SentinelDesk.Agent.Live;

/// <summary>Periodic heartbeat; also the fallback path for commands, policy changes and updates.</summary>
public sealed class HeartbeatWorker(ServerClient server, AgentConfig config, AgentState state, PolicyStore policy,
    CommandExecutor executor, UpdateManager updates, ILogger<HeartbeatWorker> log) : BackgroundService
{
    private readonly SemaphoreSlim _kick = new(0);
    public void Kick() => _kick.Release();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            if (config.IsEnrolled)
            {
                try
                {
                    var (todayIn, todayOut) = state.Today();
                    var res = await server.HeartbeatAsync(new HeartbeatRequest
                    {
                        AgentVersion = AgentInfo.Version, LoggedInUser = state.CurrentUser, LocalIp = LocalIp(),
                        UptimeSec = (long)(Native.NativeMethods.GetTickCount64() / 1000), Cpu = state.Cpu, Ram = state.Ram, Disk = state.Disk,
                        NetInBps = state.NetInBps, NetOutBps = state.NetOutBps, TodayBytesIn = todayIn, TodayBytesOut = todayOut,
                        CurrentApp = state.CurrentApp, CurrentTitle = state.CurrentTitle, UserIdle = state.UserIdle, Locked = state.Locked,
                        QueueDepth = state.QueueDepth, PolicyVersion = policy.Current.Version, TrayRunning = state.TrayRunning,
                        ConnectionType = state.ConnectionType,
                        TopDomains = policy.Current.Collectors.WebDomains ? state.TakeTopSites() : null
                    }, ct);
                    failures = 0;
                    state.LastHeartbeatUtc = DateTime.UtcNow;
                    state.ConnectionState = res.Status == "active" ? "connected" : "pending";
                    state.StatusDetail = res.Status == "active" ? null : $"Device is {res.Status} — awaiting administrator approval";
                    if (config.Status != res.Status) { config.Status = res.Status; config.Save(); }

                    if (res.Status == "active" && res.PolicyVersion != policy.Current.Version) await RefreshPolicyAsync(ct);
                    foreach (var cmd in res.Commands) await executor.ExecuteAsync(cmd, viaHub: false, ct);
                    if (res.Update is not null) await updates.StageAsync(res.Update, ct);

                    // detect a large wall-clock skew against the server (clock tampering breaks auth & timelines)
                    if (Math.Abs((res.ServerTimeUtc - DateTime.UtcNow).TotalMinutes) > 3)
                        log.LogWarning("Local clock differs from server by {Min:0} min", (res.ServerTimeUtc - DateTime.UtcNow).TotalMinutes);
                }
                catch (ServerUnavailableException ex)
                {
                    failures++;
                    state.ConnectionState = ex.Status is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden ? "error" : "offline";
                    state.StatusDetail = ex.Message;
                    if (failures == 1 || failures % 20 == 0) log.LogWarning("Heartbeat failed: {Message}", ex.Message);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogError(ex, "Heartbeat error"); }
            }
            else state.ConnectionState = "error";

            var interval = Math.Clamp(policy.Current.Collectors.HeartbeatSec, 10, 600);
            if (failures > 0) interval = Math.Min(600, interval * Math.Min(failures, 10)) + Random.Shared.Next(0, 10);
            await _kick.WaitAsync(TimeSpan.FromSeconds(interval), ct).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    public async Task RefreshPolicyAsync(CancellationToken ct)
    {
        try
        {
            var signed = await server.GetPolicyAsync(ct);
            policy.Apply(signed);
        }
        catch (Exception ex) { log.LogWarning("Policy refresh failed: {Message}", ex.Message); }
    }

    private static string? LocalIp()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("10.255.255.255", 1); // no packets sent; selects the primary route
            return (s.LocalEndPoint as System.Net.IPEndPoint)?.Address.ToString();
        }
        catch { return null; }
    }
}

/// <summary>Persistent SignalR channel for instant server → agent commands.</summary>
public sealed class CommandChannel(ServerClient server, AgentConfig config, CommandExecutor executor,
    HeartbeatWorker heartbeat, LiveStreamer streamer, ILogger<CommandChannel> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        while (!config.IsEnrolled && !ct.IsCancellationRequested) await Task.Delay(10_000, ct);

        var conn = new HubConnectionBuilder()
            .WithUrl(config.ServerUrl.TrimEnd('/') + "/hubs/agent", o =>
            {
                o.AccessTokenProvider = () => Task.FromResult<string?>(DeviceAuth.CreateToken(config.DeviceId, config.GetSecret()!));
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
                o.WebSocketConfiguration = ws =>
                {
                    if (server.ClientCertificate is { } c) ws.ClientCertificates.Add(c);
                    ws.RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                        server.ValidateServerCertificate(cert as System.Security.Cryptography.X509Certificates.X509Certificate2, errors);
                };
            })
            .WithAutomaticReconnect(new ForeverRetry())
            .Build();

        conn.On<ServerCommand>("command", async cmd =>
        {
            var result = await executor.ExecuteAsync(cmd, viaHub: true, ct);
            if (result is not null) try { await conn.InvokeAsync("Ack", result, ct); } catch { }
        });
        conn.On("policyChanged", () => heartbeat.Kick());
        // a console user is watching this device: push sub-second samples until told to stop (or the lease lapses)
        conn.On<int, int>("startStream", (intervalMs, leaseSec) =>
            streamer.Start(intervalMs, leaseSec, s => conn.SendAsync("Stream", s, ct)));
        conn.On("stopStream", streamer.Stop);
        conn.Reconnecting += _ => { streamer.Stop(); return Task.CompletedTask; };
        conn.Closed += _ => { streamer.Stop(); return Task.CompletedTask; };
        conn.Reconnected += _ => { log.LogInformation("Live channel reconnected"); heartbeat.Kick(); return Task.CompletedTask; };

        var delay = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await conn.StartAsync(ct);
                log.LogInformation("Live command channel connected");
                break;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogDebug("Live channel connect failed: {Message}", ex.Message);
                await Task.Delay(delay + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 3000)), ct);
                delay = TimeSpan.FromSeconds(Math.Min(300, delay.TotalSeconds * 2));
            }
        }
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        streamer.Stop();
        await conn.DisposeAsync();
    }

    private sealed class ForeverRetry : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext ctx) =>
            TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(ctx.PreviousRetryCount, 8)))) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 5000));
    }
}

/// <summary>Verifies (ECDSA signature, device binding, expiry, replay) and executes server commands.</summary>
public sealed class CommandExecutor(AgentConfig config, ServerClient server, IServiceProvider services, ILogger<CommandExecutor> log)
{
    private readonly ConcurrentDictionary<Guid, DateTime> _done = new();

    public async Task<CommandResult?> ExecuteAsync(ServerCommand cmd, bool viaHub, CancellationToken ct)
    {
        if (!_done.TryAdd(cmd.CommandId, DateTime.UtcNow)) return null;
        CommandResult result;
        using (var key = config.ServerSigningKey())
        {
            if (!Signing.Verify(key, cmd.SigningString(), cmd.Signature) || cmd.DeviceId != config.DeviceId)
            {
                log.LogWarning("Rejected command {Id}: bad signature or wrong device", cmd.CommandId);
                services.GetRequiredService<TelemetryAggregator>().AddEvent(new AgentEvent(DateTime.UtcNow, "tamper.badCommand", "critical", $"Rejected unsigned/forged command '{cmd.Type}'"));
                return null;
            }
        }
        if (cmd.ExpiresUtc < DateTime.UtcNow) result = new CommandResult(cmd.CommandId, false, "expired");
        else
        {
            try { result = new CommandResult(cmd.CommandId, true, await RunAsync(cmd, ct)); }
            catch (Exception ex) { result = new CommandResult(cmd.CommandId, false, ex.Message); }
        }
        log.LogInformation("Command {Type} → {Ok}", cmd.Type, result.Success ? "ok" : result.Message);
        if (!viaHub) try { await server.PostCommandResultAsync(result, ct); } catch { }
        return result;
    }

    private async Task<string> RunAsync(ServerCommand cmd, CancellationToken ct)
    {
        switch (cmd.Type)
        {
            case CommandTypes.PolicyUpdated:
                await services.GetRequiredService<HeartbeatWorker>().RefreshPolicyAsync(ct);
                return "policy refreshed";
            case CommandTypes.RefreshInventory:
                services.GetRequiredService<InventoryCollector>().RefreshNow();
                services.GetRequiredService<Uploader>().UploadNow();
                return "inventory collection started";
            case CommandTypes.CollectNow:
                services.GetRequiredService<Uploader>().UploadNow();
                services.GetRequiredService<HeartbeatWorker>().Kick();
                return "flushed";
            case CommandTypes.ShowMessage:
                var m = Json.Deserialize<Dictionary<string, string>>(cmd.Payload ?? "{}") ?? [];
                await services.GetRequiredService<TrayManager>().NotifyAsync(m.GetValueOrDefault("title") ?? "SentinelDesk", m.GetValueOrDefault("body") ?? "");
                return "message delivered to tray";
            case CommandTypes.SetUninstallCode:
                var u = Json.Deserialize<Dictionary<string, object>>(cmd.Payload ?? "{}") ?? [];
                config.OneTimeUninstallHash = u.GetValueOrDefault("hash")?.ToString();
                config.OneTimeUninstallExpiresUtc = DateTime.TryParse(u.GetValueOrDefault("expiresUtc")?.ToString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var exp) ? exp : DateTime.UtcNow.AddHours(24);
                config.Save();
                return "one-time uninstall code set";
            case CommandTypes.Uninstall:
                File.WriteAllText(AgentPaths.UninstallMarker, $"{cmd.CommandId}|{DateTime.UtcNow:O}");
                var script = Path.Combine(AgentPaths.InstallDir, "uninstall-agent.ps1");
                if (!File.Exists(script)) return "uninstall script missing";
                Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -RemoteAuthorized")
                { UseShellExecute = false, CreateNoWindow = true });
                return "uninstall started";
            case CommandTypes.UpdateAgent:
                services.GetRequiredService<HeartbeatWorker>().Kick();
                return "update check triggered";
            default:
                throw new InvalidOperationException($"Unknown command {cmd.Type}");
        }
    }
}

/// <summary>Downloads, verifies (SHA-256 + server ECDSA signature) and stages agent updates for the Guard service to install.</summary>
public sealed class UpdateManager(ServerClient server, AgentConfig config, TelemetryAggregator agg, ILogger<UpdateManager> log)
{
    private string? _staged;

    public async Task StageAsync(AgentUpdateInfo info, CancellationToken ct)
    {
        if (_staged == info.Version) return;
        var pendingFile = Path.Combine(AgentPaths.UpdatesDir, "pending.json");
        if (File.Exists(pendingFile)) return;
        using (var key = config.ServerSigningKey())
            if (!Signing.Verify(key, $"{info.Version}|{info.Sha256}", info.Signature))
            {
                log.LogWarning("Update {Version} rejected: invalid signature", info.Version);
                return;
            }
        Directory.CreateDirectory(AgentPaths.UpdatesDir);
        var zip = Path.Combine(AgentPaths.UpdatesDir, $"agent-{info.Version}.zip");
        await server.DownloadAsync(info.Url, zip, ct);
        await using (var fs = File.OpenRead(zip))
        {
            var sha = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!string.Equals(sha, info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                log.LogWarning("Update {Version} rejected: hash mismatch", info.Version);
                fs.Close();
                File.Delete(zip);
                return;
            }
        }
        File.WriteAllText(pendingFile, Json.Serialize(new { info.Version, Zip = zip, info.Sha256, info.Signature }));
        _staged = info.Version;
        agg.AddEvent(new AgentEvent(DateTime.UtcNow, "agent.updateStaged", "info", $"Agent update {info.Version} downloaded and verified"));
        log.LogInformation("Update {Version} staged for Guard", info.Version);
    }
}

/// <summary>Detects service stops, guard tampering, clock manipulation and relays Guard watchdog events.</summary>
public sealed class TamperMonitor(TelemetryAggregator agg, ILogger<TamperMonitor> log) : BackgroundService
{
    private long _guardOffset;
    private bool _firstProtectPass = true;
    private HashSet<string> _lastFindings = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        CheckPreviousStop();
        var sw = Stopwatch.StartNew();
        var wall = DateTime.UtcNow;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        _guardOffset = File.Exists(AgentPaths.GuardEvents) ? new FileInfo(AgentPaths.GuardEvents).Length : 0;
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var drift = (DateTime.UtcNow - wall) - sw.Elapsed;
                if (Math.Abs(drift.TotalSeconds) > 120)
                    agg.AddEvent(new AgentEvent(DateTime.UtcNow, "tamper.clockChanged", "warning", $"System clock changed by {drift.TotalMinutes:+0.0;-0.0} minutes"));
                sw.Restart();
                wall = DateTime.UtcNow;
                EnsureGuard();
                Protect();
                RelayGuardEvents();
                File.WriteAllText(Path.Combine(AgentPaths.DataDir, "alive.json"), Json.Serialize(new { at = DateTime.UtcNow, clean = false }));
            }
            catch (Exception ex) { log.LogDebug(ex, "Tamper monitor tick failed"); }
        }
    }

    public override Task StopAsync(CancellationToken ct)
    {
        try { File.WriteAllText(Path.Combine(AgentPaths.DataDir, "alive.json"), Json.Serialize(new { at = DateTime.UtcNow, clean = true })); } catch { }
        return base.StopAsync(ct);
    }

    private void CheckPreviousStop()
    {
        try
        {
            var path = Path.Combine(AgentPaths.DataDir, "alive.json");
            if (!File.Exists(path)) return;
            var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement;
            var at = doc.GetProperty("at").GetDateTime().ToUniversalTime();
            var clean = doc.GetProperty("clean").GetBoolean();
            var boot = DateTime.UtcNow - TimeSpan.FromMilliseconds(Native.NativeMethods.GetTickCount64());
            // stopped while Windows kept running (i.e. not a shutdown) and not an authorized uninstall/update
            if (at > boot && !File.Exists(AgentPaths.UninstallMarker) && !File.Exists(Path.Combine(AgentPaths.UpdatesDir, "updating.flag")))
                agg.AddEvent(new AgentEvent(DateTime.UtcNow, clean ? "tamper.serviceStopped" : "tamper.agentKilled", "critical",
                    $"Agent was {(clean ? "stopped" : "terminated")} at {at:yyyy-MM-dd HH:mm:ss} UTC while Windows was running (down {(DateTime.UtcNow - at).TotalMinutes:0} min)"));
            else agg.AddEvent(new AgentEvent(DateTime.UtcNow, "agent.started", "info", $"Agent {AgentInfo.Version} started"));
        }
        catch { }
    }

    private void EnsureGuard()
    {
        if (!AgentPaths.IsSystem()) return;
        try
        {
            using var sc = new ServiceController(AgentPaths.GuardServiceName);
            if (sc.Status is ServiceControllerStatus.Stopped && !File.Exists(AgentPaths.UninstallMarker))
            {
                agg.AddEvent(new AgentEvent(DateTime.UtcNow, "tamper.guardStopped", "critical", "Guard watchdog service was stopped — restarting"));
                sc.Start();
            }
        }
        catch (InvalidOperationException) { /* guard not installed (dev) */ }
    }

    /// <summary>Same checks as the Guard (service registry keys, Safe Mode, tray auto-start), so each watches the other.</summary>
    private void Protect()
    {
        if (!OperatingSystem.IsWindows() || !AgentPaths.IsSystem() || File.Exists(AgentPaths.UninstallMarker)
            || File.Exists(Path.Combine(AgentPaths.UpdatesDir, "updating.flag"))) return;
        List<string> findings;
        try { findings = ServiceProtection.Enforce(AgentPaths.InstallDir, reportAcl: !_firstProtectPass); }
        catch (Exception ex) { findings = [$"Service protection could not be verified: {ex.Message}"]; }
        _firstProtectPass = false;
        foreach (var f in findings.Where(f => !_lastFindings.Contains(f)))
            agg.AddEvent(new AgentEvent(DateTime.UtcNow, "tamper.serviceConfig", "critical", f));
        _lastFindings = [.. findings];
    }

    private void RelayGuardEvents()
    {
        if (!File.Exists(AgentPaths.GuardEvents)) return;
        using var fs = new FileStream(AgentPaths.GuardEvents, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length < _guardOffset) _guardOffset = 0;
        fs.Seek(_guardOffset, SeekOrigin.Begin);
        using var reader = new StreamReader(fs);
        while (reader.ReadLine() is { } line)
        {
            var ev = Json.Deserialize<AgentEvent>(line);
            if (ev is not null) agg.AddEvent(ev);
        }
        _guardOffset = fs.Length;
    }
}
