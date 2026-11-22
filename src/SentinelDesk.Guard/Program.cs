using System.IO.Compression;
using System.Security.Cryptography;
using System.ServiceProcess;
using SentinelDesk.Shared;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "SentinelDeskGuard");
builder.Logging.AddEventLog(o => { o.SourceName = "SentinelDesk Guard"; o.LogName = "Application"; });
builder.Services.AddHostedService<GuardWorker>();
await builder.Build().RunAsync();

/// <summary>
/// Watchdog: keeps the agent running, detects binary tampering and installs verified agent updates (with rollback).
/// The agent watches the guard in return, so stopping one is detected and reverted by the other.
/// </summary>
public sealed class GuardWorker(ILogger<GuardWorker> log) : BackgroundService
{
    private const string AgentService = "SentinelDeskAgent";
    private static readonly string DataDir = Environment.GetEnvironmentVariable("SENTINELDESK_DATA") is { Length: > 0 } d
        ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SentinelDesk");
    private static readonly string InstallDir = AppContext.BaseDirectory;
    private static string EventsFile => Path.Combine(DataDir, "guard-events.jsonl");
    private static string StateFile => Path.Combine(DataDir, "guard-state.json");
    private static string UpdatesDir => Path.Combine(DataDir, "updates");
    private static string UninstallMarker => Path.Combine(DataDir, "uninstall.authorized");

    private string? _lastReportedHash;
    private int _restarts;
    private bool _firstProtectPass = true;
    private HashSet<string> _lastFindings = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(DataDir);
        try { ServiceProtection.LockDataDir(DataDir); } catch (Exception ex) { log.LogWarning(ex, "Could not lock data folder"); }
        await Task.Delay(TimeSpan.FromSeconds(20), ct);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try
            {
                if (File.Exists(UninstallMarker)) continue; // authorized uninstall in progress
                var pending = Path.Combine(UpdatesDir, "pending.json");
                if (File.Exists(pending)) { await ApplyUpdateAsync(pending, ct); continue; }
                Protect();
                EnsureAgentRunning();
                CheckIntegrity();
            }
            catch (Exception ex) { log.LogError(ex, "Guard tick failed"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    /// <summary>Service registry keys, Safe Mode registration and tray auto-start: repaired and reported if changed.</summary>
    private void Protect()
    {
        List<string> findings;
        try { findings = ServiceProtection.Enforce(InstallDir, reportAcl: !_firstProtectPass); }
        catch (Exception ex) { findings = [$"Service protection could not be verified: {ex.Message}"]; }
        _firstProtectPass = false;
        // report each problem once per occurrence (a problem we cannot repair would otherwise repeat every 10 s)
        foreach (var f in findings.Where(f => !_lastFindings.Contains(f)))
            Emit("tamper.serviceConfig", "critical", f);
        _lastFindings = [.. findings];
    }

    private void EnsureAgentRunning()
    {
        using var sc = new ServiceController(AgentService);
        ServiceControllerStatus status;
        try { status = sc.Status; }
        catch (InvalidOperationException)
        {
            Emit("tamper.agentServiceMissing", "critical", "SentinelDesk agent service is missing (deleted?)");
            return;
        }
        if (status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
        {
            _restarts++;
            Emit("tamper.agentStopped", "critical", $"Agent service was found {status} — restarted by Guard (restart #{_restarts})");
            try { sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20)); sc.Start(); }
            catch (Exception ex) { log.LogError(ex, "Failed to restart agent"); }
        }
    }

    private void CheckIntegrity()
    {
        var hash = HashBinaries();
        var state = File.Exists(StateFile) ? Json.Deserialize<GuardState>(File.ReadAllText(StateFile)) : null;
        if (state is null)
        {
            File.WriteAllText(StateFile, Json.Serialize(new GuardState(hash, DateTime.UtcNow)));
            return;
        }
        if (state.BaselineHash != hash && _lastReportedHash != hash)
        {
            _lastReportedHash = hash;
            Emit("tamper.binaryModified", "critical", "Agent binaries changed outside of a signed update");
        }
    }

    private static string HashBinaries()
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var f in Directory.GetFiles(InstallDir, "SentinelDesk.*").Where(f => f.EndsWith(".dll") || f.EndsWith(".exe")).Order())
        {
            sha.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(f)));
            sha.AppendData(File.ReadAllBytes(f));
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private async Task ApplyUpdateAsync(string pendingFile, CancellationToken ct)
    {
        var p = Json.Deserialize<PendingUpdate>(await File.ReadAllTextAsync(pendingFile, ct));
        File.Delete(pendingFile);
        if (p is null || !File.Exists(p.Zip)) return;

        // re-verify independently of the agent (defence against a compromised agent process)
        var config = Json.Deserialize<AgentConfigView>(await File.ReadAllTextAsync(Path.Combine(DataDir, "agent.json"), ct));
        await using (var fs = File.OpenRead(p.Zip))
        {
            var sha = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            using var key = ECDsa.Create();
            key.ImportFromPem(config!.ServerSigningKeyPem);
            if (!string.Equals(sha, p.Sha256, StringComparison.OrdinalIgnoreCase) || !Signing.Verify(key, $"{p.Version}|{sha}", p.Signature))
            {
                Emit("tamper.updateRejected", "critical", $"Update package {p.Version} failed verification and was discarded");
                return;
            }
        }

        var flag = Path.Combine(UpdatesDir, "updating.flag");
        await File.WriteAllTextAsync(flag, p.Version, ct);
        var backup = Path.Combine(UpdatesDir, "backup");
        log.LogInformation("Installing agent update {Version}", p.Version);
        using var sc = new ServiceController(AgentService);
        try
        {
            if (sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60)); }
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            Directory.CreateDirectory(backup);
            foreach (var f in Directory.GetFiles(InstallDir)) File.Copy(f, Path.Combine(backup, Path.GetFileName(f)), true);

            using (var zip = ZipFile.OpenRead(p.Zip))
                foreach (var entry in zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)))
                {
                    var dest = Path.GetFullPath(Path.Combine(InstallDir, entry.FullName));
                    if (!dest.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase)) continue; // zip-slip guard
                    if (entry.Name.StartsWith("SentinelDesk.Guard", StringComparison.OrdinalIgnoreCase)) continue; // in use; updated by MSI
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, true);
                }

            sc.Refresh();
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
            await File.WriteAllTextAsync(StateFile, Json.Serialize(new GuardState(HashBinaries(), DateTime.UtcNow)), ct);
            Emit("agent.updated", "info", $"Agent updated to {p.Version}");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Update failed — rolling back");
            try
            {
                sc.Refresh();
                if (sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); }
                foreach (var f in Directory.GetFiles(backup))
                    if (!Path.GetFileName(f).StartsWith("SentinelDesk.Guard")) File.Copy(f, Path.Combine(InstallDir, Path.GetFileName(f)), true);
                sc.Start();
            }
            catch (Exception rex) { log.LogError(rex, "Rollback failed"); }
            Emit("agent.updateFailed", "warning", $"Update to {p.Version} failed and was rolled back: {ex.Message}");
        }
        finally
        {
            File.Delete(flag);
            try { File.Delete(p.Zip); } catch { }
        }
    }

    private void Emit(string type, string severity, string message)
    {
        log.LogWarning("{Type}: {Message}", type, message);
        try { File.AppendAllText(EventsFile, Json.Serialize(new AgentEvent(DateTime.UtcNow, type, severity, message)) + "\n"); } catch { }
    }

    private sealed record GuardState(string BaselineHash, DateTime At);
    private sealed record PendingUpdate(string Version, string Zip, string Sha256, string Signature);
    private sealed record AgentConfigView(string? ServerSigningKeyPem);
}
