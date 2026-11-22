using Windows.Devices.Geolocation;
using SentinelDesk.Shared;

namespace SentinelDesk.Tray;

/// <summary>
/// Reads the device position from Windows Location (Wi-Fi / GPS / cellular / IP based) and hands it to the agent.
/// Runs in the user's session because Windows applies the "let desktop apps access your location" setting per user.
/// Only active while the policy enables location; the agent ignores positions when it is disabled.
/// </summary>
public sealed class LocationTracker(AgentPipe pipe) : IDisposable
{
    private System.Threading.Timer? _timer;
    private int _intervalMin;
    private int _busy;

    public void Configure(bool enabled, int intervalMin)
    {
        intervalMin = Math.Clamp(intervalMin, 1, 120);
        if (!enabled) { _timer?.Dispose(); _timer = null; _intervalMin = 0; return; }
        if (_timer is not null && _intervalMin == intervalMin) return;
        _intervalMin = intervalMin;
        _timer?.Dispose();
        _timer = new System.Threading.Timer(_ => _ = ReadAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(intervalMin));
    }

    private async Task ReadAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed)
            {
                await pipe.SendAsync(new PipeMessage
                {
                    Type = "location",
                    LocationError = access == GeolocationAccessStatus.Denied
                        ? "Windows location is turned off (Settings > Privacy & security > Location, incl. 'Let desktop apps access your location')"
                        : "Windows location is unavailable on this PC"
                });
                return;
            }
            var geo = new Geolocator { DesiredAccuracy = PositionAccuracy.High };
            var pos = await geo.GetGeopositionAsync(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30));
            var c = pos.Coordinate;
            await pipe.SendAsync(new PipeMessage
            {
                Type = "location",
                Location = new DeviceLocation(c.Point.Position.Latitude, c.Point.Position.Longitude, c.Accuracy,
                    c.Timestamp.UtcDateTime, c.PositionSource.ToString())
            });
        }
        catch (Exception ex)
        {
            try { await pipe.SendAsync(new PipeMessage { Type = "location", LocationError = $"Location could not be read: {ex.Message}" }); } catch { }
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    public void Dispose() => _timer?.Dispose();
}
