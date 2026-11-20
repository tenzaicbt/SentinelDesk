using SentinelDesk.Agent.Live;

namespace SentinelDesk.Agent.Core;

/// <summary>
/// Zero-touch enrollment: when SENTINELDESK_SERVER and SENTINELDESK_TOKEN are set (e.g. local development,
/// or an imaging/Intune environment) the agent enrolls itself, retrying until the server is reachable.
/// </summary>
public sealed class AutoEnrollWorker(AgentConfig config, ServerClient server, IServiceProvider services, ILogger<AutoEnrollWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var url = Environment.GetEnvironmentVariable("SENTINELDESK_SERVER");
        var token = Environment.GetEnvironmentVariable("SENTINELDESK_TOKEN");
        if (config.IsEnrolled || string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token)) return;

        var delay = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested && !config.IsEnrolled)
        {
            try
            {
                log.LogInformation("Enrolling with {Server}…", url);
                await Enrollment.EnrollAsync(url, token, null, trustOnFirstUse: true, log, ct, target: config);
                server.Reset();
                log.LogInformation("Enrolled as device {Device} (status: {Status})", config.DeviceId, config.Status);
                services.GetRequiredService<HeartbeatWorker>().Kick();
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("Enrollment failed ({Message}) — is the server running at {Server}? Retrying in {Delay:0}s", ex.Message, url, delay.TotalSeconds);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            }
        }
    }
}
