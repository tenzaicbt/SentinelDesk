using SentinelDesk.Shared;

namespace SentinelDesk.Server.Services;

/// <summary>Settings an administrator changes in the console. Stored in &lt;data&gt;/server-settings.json.</summary>
public sealed class ServerSettingsData
{
    /// <summary>Live view sample interval in ms (0 = live stream off, heartbeats only). Null = Live:StreamIntervalMs (default 500).</summary>
    public int? LiveStreamIntervalMs { get; set; }
    public DateTime? LiveStreamChangedAt { get; set; }
    public string? LiveStreamChangedBy { get; set; }
}

public sealed class ServerSettings
{
    private readonly string _path;
    private readonly object _gate = new();
    private ServerSettingsData _data;

    public ServerSettings(IConfiguration config)
    {
        _path = Path.Combine(config["Storage:DataDir"] ?? "data", "server-settings.json");
        try { _data = File.Exists(_path) ? Json.Deserialize<ServerSettingsData>(File.ReadAllText(_path)) ?? new() : new(); }
        catch (Exception) { _data = new(); }
    }

    public ServerSettingsData Current { get { lock (_gate) return Json.Deserialize<ServerSettingsData>(Json.Serialize(_data))!; } }

    public void Update(Action<ServerSettingsData> change)
    {
        lock (_gate)
        {
            change(_data);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, Json.Serialize(_data));
            File.Move(tmp, _path, overwrite: true);
        }
    }
}
