using SentinelDesk.Agent.Collectors;
using SentinelDesk.Agent.Core;
using SentinelDesk.Agent.Ipc;
using SentinelDesk.Agent.Live;
using SentinelDesk.Agent.Pipeline;
using SentinelDesk.Shared;

// ─────────────────────────────── CLI ───────────────────────────────
//  SentinelDesk.Agent.exe                      → run (Windows service host)
//  SentinelDesk.Agent.exe console              → run interactively (diagnostics)
//  SentinelDesk.Agent.exe enroll --server URL --token TOKEN [--pin SHA256] [--tofu]
//  SentinelDesk.Agent.exe set-uninstall-key KEY   (admin; stores PBKDF2 hash)
//  SentinelDesk.Agent.exe verify-uninstall-key KEY → exit code 0 = valid
//  SentinelDesk.Agent.exe status
var cmd = args.FirstOrDefault()?.ToLowerInvariant();
string? Arg(string name) { var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

switch (cmd)
{
    case "enroll":
    {
        var server = Arg("--server");
        var token = Arg("--token");
        if (server is null || token is null) { Console.Error.WriteLine("usage: enroll --server https://host:5443 --token SD-XXXX [--pin SHA256] [--tofu]"); return 2; }
        using var lf = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
        try
        {
            var c = await Enrollment.EnrollAsync(server, token, Arg("--pin"), args.Contains("--tofu"), lf.CreateLogger("enroll"), CancellationToken.None);
            Console.WriteLine($"Enrolled as device {c.DeviceId} (status: {c.Status})");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
    case "set-uninstall-key":
    {
        if (!AgentPaths.IsAdmin()) { Console.Error.WriteLine("Administrator rights required"); return 5; }
        var key = args.ElementAtOrDefault(1);
        if (string.IsNullOrWhiteSpace(key) || key.Length < 8) { Console.Error.WriteLine("Uninstall key must be at least 8 characters"); return 2; }
        AgentPaths.EnsureDataDir();
        var c = AgentConfig.Load();
        c.UninstallKeyHash = key.StartsWith("pbkdf2-sha256$") ? key : KeyHash.Hash(key);
        c.Save();
        Console.WriteLine("Uninstall key stored");
        return 0;
    }
    case "verify-uninstall-key":
    {
        var key = args.ElementAtOrDefault(1) ?? "";
        var c = AgentConfig.Load();
        var ok = KeyHash.Verify(key, c.UninstallKeyHash)
                 || (c.OneTimeUninstallExpiresUtc > DateTime.UtcNow && KeyHash.Verify(key.ToUpperInvariant(), c.OneTimeUninstallHash));
        if (!ok)
        {
            // record the failed attempt for the tamper alert pipeline (relayed by the running agent)
            try
            {
                File.AppendAllText(AgentPaths.GuardEvents, Json.Serialize(new AgentEvent(DateTime.UtcNow, "tamper.uninstallAttempt", "critical",
                    $"Uninstall attempted with an invalid key by {Environment.UserDomainName}\\{Environment.UserName}")) + "\n");
            }
            catch { }
        }
        Console.WriteLine(ok ? "valid" : "invalid");
        return ok ? 0 : 1;
    }
    case "status":
    {
        var c = AgentConfig.Load();
        Console.WriteLine($"Agent version : {AgentInfo.Version}");
        Console.WriteLine($"Data dir      : {AgentPaths.DataDir}");
        Console.WriteLine($"Server        : {c.ServerUrl}");
        Console.WriteLine($"Device id     : {(c.IsEnrolled ? c.DeviceId : "(not enrolled)")}");
        Console.WriteLine($"Status        : {c.Status}");
        Console.WriteLine($"Key provider  : {c.KeyProvider}");
        Console.WriteLine($"Uninstall key : {(c.UninstallKeyHash is null ? "NOT SET" : "set")}");
        return 0;
    }
}

// ─────────────────────────────── Host ───────────────────────────────
AgentPaths.EnsureDataDir();
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = AgentPaths.AgentServiceName);
builder.Logging.AddEventLog(o => { o.SourceName = "SentinelDesk Agent"; o.LogName = "Application"; });
builder.Logging.AddFilter<Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider>(l => l >= LogLevel.Warning);
builder.Services.Configure<HostOptions>(o =>
{
    o.ShutdownTimeout = TimeSpan.FromSeconds(15);
    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
});

var config = AgentConfig.Load();

builder.Services.AddSingleton(config);
builder.Services.AddSingleton<ServerClient>();
builder.Services.AddSingleton<PolicyStore>();
builder.Services.AddSingleton<AgentState>();
builder.Services.AddSingleton<TelemetryAggregator>();
builder.Services.AddSingleton<LocalQueue>();
builder.Services.AddSingleton<CommandExecutor>();
builder.Services.AddSingleton<UpdateManager>();
builder.Services.AddSingleton<LiveStreamer>();

void Hosted<T>() where T : class, IHostedService
{
    builder.Services.AddSingleton<T>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<T>());
}
Hosted<AutoEnrollWorker>();
Hosted<TamperMonitor>();
Hosted<MetricsCollector>();
Hosted<EtwCollector>();
Hosted<InventoryCollector>();
Hosted<SessionCollector>();
Hosted<UsbCollector>();
Hosted<TrayManager>();
Hosted<Uploader>();
Hosted<HeartbeatWorker>();
Hosted<CommandChannel>();

var host = builder.Build();
var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SentinelDesk");
if (!config.IsEnrolled && Environment.GetEnvironmentVariable("SENTINELDESK_SERVER") is null)
    log.LogWarning("Agent is not enrolled - run 'SentinelDesk.Agent.exe enroll --server <url> --token <token>'. Data will be buffered locally.");
log.LogInformation("SentinelDesk Agent {Version} starting (data: {Dir})", AgentInfo.Version, AgentPaths.DataDir);
await host.RunAsync();
return 0;
