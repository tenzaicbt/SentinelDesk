using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using SentinelDesk.Agent.Core;
using SentinelDesk.Shared;

namespace SentinelDesk.Agent.Pipeline;

/// <summary>Live state shared between collectors, the heartbeat and the tray.</summary>
public sealed class AgentState
{
    public volatile string ConnectionState = "starting"; // connected | offline | error | pending
    public volatile string? StatusDetail;
    public DateTime? LastUploadUtc;
    public DateTime? LastHeartbeatUtc;
    public int QueueDepth;
    public double Cpu, Ram, Disk;
    public long NetInBps, NetOutBps;
    public string? ConnectionType;
    public string? CurrentApp, CurrentTitle, CurrentUser;
    /// <summary>Server time minus local time, from the last heartbeat. Timed block rules use server time.</summary>
    public TimeSpan ServerClockOffset;
    public bool UserIdle, Locked;
    public volatile DeviceLocation? Location;
    public volatile string? LocationError;
    public readonly ConcurrentDictionary<int, DateTime> TraySessions = new();
    public readonly DateTime StartedUtc = DateTime.UtcNow;

    private readonly Lock _todayLock = new();
    private DateOnly _day = DateOnly.FromDateTime(DateTime.Now);
    private long _todayIn, _todayOut;

    public AgentState() => LoadToday();

    public void AddBytes(long bytesIn, long bytesOut)
    {
        lock (_todayLock)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (today != _day) { _day = today; _todayIn = _todayOut = 0; }
            _todayIn += bytesIn; _todayOut += bytesOut;
        }
    }

    public (long In, long Out) Today()
    {
        lock (_todayLock) return DateOnly.FromDateTime(DateTime.Now) == _day ? (_todayIn, _todayOut) : (0, 0);
    }

    public void SaveToday()
    {
        try
        {
            lock (_todayLock) File.WriteAllText(AgentPaths.StateFile, Json.Serialize(new TodayState(_day, _todayIn, _todayOut)));
        }
        catch { }
    }

    private void LoadToday()
    {
        try
        {
            if (!File.Exists(AgentPaths.StateFile)) return;
            var s = Json.Deserialize<TodayState>(File.ReadAllText(AgentPaths.StateFile));
            if (s is not null && s.Day == _day) { _todayIn = s.In; _todayOut = s.Out; }
        }
        catch { }
    }

    private sealed record TodayState(DateOnly Day, long In, long Out);

    // live per-site traffic, reset on every heartbeat
    private readonly Lock _siteLock = new();
    private Dictionary<string, (long In, long Out)> _sites = new();
    private DateTime _sitesSince = DateTime.UtcNow;

    public void AddSiteBytes(string host, long bytesIn, long bytesOut)
    {
        var site = Domains.Registrable(host);
        lock (_siteLock)
        {
            _sites.TryGetValue(site, out var v);
            _sites[site] = (v.In + bytesIn, v.Out + bytesOut);
        }
    }

    /// <summary>Busiest sites since the previous call, as bytes/second.</summary>
    public List<LiveDomain> TakeTopSites(int max = 10)
    {
        Dictionary<string, (long In, long Out)> sites;
        double secs;
        lock (_siteLock)
        {
            sites = _sites;
            secs = Math.Max(1, (DateTime.UtcNow - _sitesSince).TotalSeconds);
            _sites = new();
            _sitesSince = DateTime.UtcNow;
        }
        return sites.OrderByDescending(kv => kv.Value.In + kv.Value.Out).Take(max)
            .Select(kv => new LiveDomain(kv.Key, (long)(kv.Value.In / secs), (long)(kv.Value.Out / secs))).ToList();
    }

    public bool TrayRunning => TraySessions.Values.Any(t => DateTime.UtcNow - t < TimeSpan.FromSeconds(30));
}

/// <summary>Collects samples into 1-minute buckets and produces upload payloads.</summary>
public sealed class TelemetryAggregator
{
    private readonly Lock _lock = new();
    private TelemetryPayload _pending = new();
    private readonly Dictionary<(DateTime, string), (long In, long Out)> _procNet = new();
    private readonly Dictionary<(DateTime, string, string?), (string? Process, int Count, long In, long Out)> _web = new();
    private readonly Dictionary<(DateTime, string, string?, string?), (int Active, int Idle)> _apps = new();

    public static DateTime Minute(DateTime utc) => new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    public void AddPerf(PerfSample s) { lock (_lock) _pending.Perf.Add(s); }
    public void AddAdapter(AdapterSample s) { lock (_lock) _pending.Adapters.Add(s); }
    public void AddEvent(AgentEvent e) { lock (_lock) _pending.Events.Add(e); }
    public void SetHardware(HardwareInfo h) { lock (_lock) _pending.Hardware = h; }
    public void SetSoftware(List<SoftwareItem> s) { lock (_lock) _pending.Software = s; }
    public void SetSecurity(SecurityPosture s) { lock (_lock) _pending.Security = s; }
    public void SetProcesses(List<ProcessInfo> p) { lock (_lock) _pending.Processes = p; }

    public void AddProcessBytes(string process, long bytesIn, long bytesOut)
    {
        var key = (Minute(DateTime.UtcNow), process);
        lock (_lock)
        {
            _procNet.TryGetValue(key, out var v);
            _procNet[key] = (v.In + bytesIn, v.Out + bytesOut);
        }
    }

    public void AddDomain(string domain, string? process, string? user)
    {
        var key = (Minute(DateTime.UtcNow), domain, user);
        lock (_lock)
        {
            _web.TryGetValue(key, out var v);
            _web[key] = (process ?? v.Process, v.Count + 1, v.In, v.Out);
        }
    }

    /// <summary>Traffic to an IP that <paramref name="domain"/> resolved to.</summary>
    public void AddDomainBytes(string domain, string? process, string? user, long bytesIn, long bytesOut)
    {
        var key = (Minute(DateTime.UtcNow), domain, user);
        lock (_lock)
        {
            _web.TryGetValue(key, out var v);
            _web[key] = (v.Process ?? process, v.Count, v.In + bytesIn, v.Out + bytesOut);
        }
    }

    public void AddAppUsage(AppUsageSample s)
    {
        var key = (Minute(s.Time), s.App, s.Title, s.User);
        lock (_lock)
        {
            _apps.TryGetValue(key, out var v);
            _apps[key] = (Math.Min(3600, v.Active + s.ActiveSec), Math.Min(3600, v.Idle + s.IdleSec));
        }
    }

    public TelemetryPayload Flush()
    {
        lock (_lock)
        {
            var p = _pending;
            _pending = new TelemetryPayload();
            p.ProcessNet.AddRange(_procNet.Select(kv => new ProcessNetSample(kv.Key.Item1, kv.Key.Item2, kv.Value.In, kv.Value.Out)));
            p.Web.AddRange(_web.Select(kv => new WebSample(kv.Key.Item1, kv.Key.Item3, kv.Key.Item2, kv.Value.Process, kv.Value.Count, kv.Value.In, kv.Value.Out)));
            p.AppUsage.AddRange(_apps.Select(kv => new AppUsageSample(kv.Key.Item1, kv.Key.Item4, kv.Key.Item2, kv.Key.Item3, kv.Value.Active, kv.Value.Idle)));
            _procNet.Clear(); _web.Clear(); _apps.Clear();
            p.CreatedUtc = DateTime.UtcNow;
            return p;
        }
    }
}

/// <summary>Encrypted offline buffer (SQLite; each row AES-256-GCM sealed with a DPAPI-protected key).</summary>
public sealed class LocalQueue : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly Lock _lock = new();

    public LocalQueue()
    {
        AgentPaths.EnsureDataDir();
        var isNew = !File.Exists(AgentPaths.QueueDb);
        _db = new SqliteConnection($"Data Source={AgentPaths.QueueDb};Pooling=False");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS queue (id INTEGER PRIMARY KEY AUTOINCREMENT, created INTEGER NOT NULL, size INTEGER NOT NULL, data BLOB NOT NULL);");
        if (isNew)
        {
            // seed the sequence with a time-based value so batch_seq stays monotonic even if the queue is recreated
            Exec("INSERT INTO queue(created,size,data) VALUES (0,0,x'00'); DELETE FROM queue;");
            Exec($"UPDATE sqlite_sequence SET seq = {DateTimeOffset.UtcNow.ToUnixTimeSeconds() * 1000} WHERE name='queue';");
        }
    }

    public void Enqueue(byte[] json)
    {
        var sealedData = LocalCrypto.Seal(PayloadCrypto.Gzip(json), LocalKey.Get());
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO queue(created,size,data) VALUES ($c,$s,$d)";
            cmd.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$s", sealedData.Length);
            cmd.Parameters.AddWithValue("$d", sealedData);
            cmd.ExecuteNonQuery();
        }
    }

    public (long Id, byte[] Json)? Peek()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id, data FROM queue ORDER BY id LIMIT 1";
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            var id = r.GetInt64(0);
            var data = (byte[])r[1];
            try { return (id, PayloadCrypto.Gunzip(LocalCrypto.Open(data, LocalKey.Get()))); }
            catch
            {
                r.Close();
                Delete(id); // corrupted / foreign key — drop it
                return null;
            }
        }
    }

    public void Delete(long id)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM queue WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public int Count()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM queue";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>Enforces the offline cap (size + age); oldest data is dropped first.</summary>
    public int Trim(int maxMb, int maxDays)
    {
        lock (_lock)
        {
            var removed = 0;
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM queue WHERE created < $cut";
                cmd.Parameters.AddWithValue("$cut", DateTimeOffset.UtcNow.AddDays(-maxDays).ToUnixTimeSeconds());
                removed += cmd.ExecuteNonQuery();
            }
            while (true)
            {
                using var size = _db.CreateCommand();
                size.CommandText = "SELECT COALESCE(SUM(size),0) FROM queue";
                if (Convert.ToInt64(size.ExecuteScalar()) <= (long)maxMb * 1024 * 1024) break;
                using var del = _db.CreateCommand();
                del.CommandText = "DELETE FROM queue WHERE id IN (SELECT id FROM queue ORDER BY id LIMIT 50)";
                var n = del.ExecuteNonQuery();
                removed += n;
                if (n == 0) break;
            }
            return removed;
        }
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}

/// <summary>Flushes aggregated telemetry into the queue and drains it to the server with backoff.</summary>
public sealed class Uploader(TelemetryAggregator aggregator, LocalQueue queue, ServerClient server, AgentConfig config,
    PolicyStore policy, AgentState state, ILogger<Uploader> log) : BackgroundService
{
    private readonly SemaphoreSlim _kick = new(0);
    private DateTime _nextFlush = DateTime.UtcNow.AddSeconds(20);
    private TimeSpan _backoff = TimeSpan.FromSeconds(5);
    private DateTime _retryAt = DateTime.MinValue;

    public void UploadNow()
    {
        _nextFlush = DateTime.UtcNow;
        _retryAt = DateTime.MinValue;
        _kick.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow >= _nextFlush)
                {
                    FlushToQueue();
                    _nextFlush = DateTime.UtcNow.AddSeconds(Math.Clamp(policy.Current.Upload.IntervalSec, 15, 3600));
                }
                if (config.IsEnrolled && DateTime.UtcNow >= _retryAt) await DrainAsync(ct);
                state.QueueDepth = queue.Count();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogError(ex, "Uploader loop error"); }
            await _kick.WaitAsync(TimeSpan.FromSeconds(5), ct).ContinueWith(_ => { }, CancellationToken.None);
        }
        FlushToQueue(); // persist anything pending on shutdown
    }

    private void FlushToQueue()
    {
        var payload = aggregator.Flush();
        if (payload.IsEmpty) return;
        queue.Enqueue(System.Text.Encoding.UTF8.GetBytes(Json.Serialize(payload)));
        var trimmed = queue.Trim(policy.Current.Upload.OfflineMaxMb, policy.Current.Upload.OfflineMaxDays);
        if (trimmed > 0) log.LogWarning("Offline buffer cap reached — dropped {Count} oldest batches", trimmed);
        state.SaveToday();
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        using var rsa = config.ServerEncryptionKey();
        for (var i = 0; i < 200 && !ct.IsCancellationRequested; i++)
        {
            var next = queue.Peek();
            if (next is null) break;
            var (id, json) = next.Value;
            try
            {
                var batch = PayloadCrypto.Encrypt(id, json, rsa);
                var ack = await server.UploadAsync(batch, ct);
                if (!ack.Accepted)
                {
                    state.ConnectionState = "pending";
                    _retryAt = DateTime.UtcNow.AddMinutes(2);
                    return;
                }
                queue.Delete(id);
                state.LastUploadUtc = DateTime.UtcNow;
                if (state.ConnectionState != "connected") state.ConnectionState = "connected";
                _backoff = TimeSpan.FromSeconds(5);
            }
            catch (ServerUnavailableException ex)
            {
                state.ConnectionState = "offline";
                state.StatusDetail = ex.Message;
                // exponential backoff with jitter so thousands of agents don't stampede after an outage
                _retryAt = DateTime.UtcNow + _backoff + TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)_backoff.TotalMilliseconds / 2 + 1));
                _backoff = TimeSpan.FromSeconds(Math.Min(600, _backoff.TotalSeconds * 2));
                log.LogWarning("Upload failed ({Message}); retry in {Delay:0}s", ex.Message, (_retryAt - DateTime.UtcNow).TotalSeconds);
                return;
            }
        }
    }
}
