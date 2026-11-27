using SentinelDesk.Server.Security;
using SentinelDesk.Server.Services;

namespace SentinelDesk.Server.Endpoints;

/// <summary>Settings administrators change in the console.</summary>
public static class SettingsEndpoints
{
    public sealed record LiveRateRequest(int IntervalMs);

    public static void MapSettingsApi(this WebApplication app)
    {
        // Live view refresh rate: how often a watched device sends a live sample (bandwidth vs. smoothness).
        app.MapGet("/api/settings/live", (LiveStreamRegistry streams, ServerSettings settings) =>
        {
            var s = settings.Current;
            return Results.Ok(new
            {
                streams.IntervalMs, streams.DefaultIntervalMs, Choices = LiveStreamRegistry.Choices,
                ChangedAt = s.LiveStreamChangedAt, ChangedBy = s.LiveStreamChangedBy, WatchedDevices = streams.Watched().Count,
            });
        }).RequireAuthorization("Console");

        app.MapPut("/api/settings/live", async (LiveRateRequest r, LiveStreamRegistry streams, LiveStreamControl control, ServerSettings settings,
            AccessScope scope, AuditService audit, ILoggerFactory logs) =>
        {
            if (!LiveStreamRegistry.Choices.Contains(r.IntervalMs))
                return Results.Problem("Choose Off, 0.5, 1, 2 or 5 seconds", statusCode: 400);
            var before = streams.IntervalMs;
            settings.Update(s => { s.LiveStreamIntervalMs = r.IntervalMs; s.LiveStreamChangedAt = DateTime.UtcNow; s.LiveStreamChangedBy = scope.Username; });
            await audit.LogAsync("settings.liveRefreshRate", r.IntervalMs == 0 ? "off" : $"{r.IntervalMs} ms", $"was {(before == 0 ? "off" : $"{before} ms")}");

            // apply now to devices being watched: a new interval is picked up by the running stream, Off stops it
            var log = logs.CreateLogger("SentinelDesk.Settings");
            foreach (var d in streams.Watched())
                try { await (r.IntervalMs == 0 ? control.StopAsync(d) : control.StartAsync(d)); }
                catch (Exception ex) { log.LogDebug("Applying the live refresh rate to {Device} failed: {Message}", d, ex.Message); }
            return Results.Ok(new { streams.IntervalMs });
        }).RequireAuthorization("Super");
    }
}
