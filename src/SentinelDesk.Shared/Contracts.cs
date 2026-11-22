using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentinelDesk.Shared;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
    public static T? Deserialize<T>(ReadOnlySpan<byte> json) => JsonSerializer.Deserialize<T>(json, Options);
}

// ───────────────────────── Enrollment ─────────────────────────

public sealed record EnrollRequest(
    string EnrollToken,
    string MachineId,
    string Hostname,
    string? Domain,
    string? Serial,
    string Os,
    string OsBuild,
    string AgentVersion,
    string CsrPem);

public sealed record EnrollResponse(
    Guid DeviceId,
    string DeviceSecret,
    string ClientCertPem,
    string CaCertPem,
    string ServerEncryptionKeyPem,
    string ServerSigningKeyPem,
    string Status);

// ───────────────────────── Live stream ─────────────────────────

/// <summary>
/// High-rate sample pushed over the agent's SignalR channel while a console user is watching the device
/// (server → agent "startStream"/"stopStream"). Relayed straight to viewers; never stored.
/// </summary>
public sealed class LiveSample
{
    public long Seq { get; set; }
    /// <summary>Agent wall clock, Unix ms.</summary>
    public long AgentTimeMs { get; set; }
    public int IntervalMs { get; set; }
    public double Cpu { get; set; }
    public double Ram { get; set; }
    public long NetInBps { get; set; }
    public long NetOutBps { get; set; }
    public string? CurrentApp { get; set; }
    public string? CurrentTitle { get; set; }
    public bool UserIdle { get; set; }
    public bool Locked { get; set; }
}

// ───────────────────────── Heartbeat ─────────────────────────

public sealed class HeartbeatRequest
{
    public string AgentVersion { get; set; } = "";
    public string? LoggedInUser { get; set; }
    public string? LocalIp { get; set; }
    public long UptimeSec { get; set; }
    public double Cpu { get; set; }
    public double Ram { get; set; }
    public double Disk { get; set; }
    public long NetInBps { get; set; }
    public long NetOutBps { get; set; }
    public long TodayBytesIn { get; set; }
    public long TodayBytesOut { get; set; }
    public string? CurrentApp { get; set; }
    public string? CurrentTitle { get; set; }
    public bool UserIdle { get; set; }
    public bool Locked { get; set; }
    public int QueueDepth { get; set; }
    public int PolicyVersion { get; set; }
    public bool TrayRunning { get; set; }
    public string? ConnectionType { get; set; }
    /// <summary>Busiest sites right now (only when web-domain collection is allowed by policy).</summary>
    public List<LiveDomain>? TopDomains { get; set; }
    /// <summary>Latest Windows location fix (only when location collection is enabled by policy).</summary>
    public DeviceLocation? Location { get; set; }
    /// <summary>Why no location is available (e.g. Windows location turned off), when enabled by policy.</summary>
    public string? LocationError { get; set; }
}

/// <summary>A position from Windows Location (Wi-Fi / GPS / cell / IP based). Accuracy is the radius in metres.</summary>
public sealed record DeviceLocation(double Lat, double Lng, double AccuracyM, DateTime AtUtc, string? Source);

public sealed class HeartbeatResponse
{
    public string Status { get; set; } = "active";
    public int PolicyVersion { get; set; }
    public DateTime ServerTimeUtc { get; set; }
    public List<ServerCommand> Commands { get; set; } = [];
    public AgentUpdateInfo? Update { get; set; }
}

public sealed class AgentUpdateInfo
{
    public string Version { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Signature { get; set; } = "";
}

// ───────────────────────── Telemetry ─────────────────────────

/// <summary>Envelope sent over the wire. Payload = AES-256-GCM(gzip(JSON(TelemetryPayload))).</summary>
public sealed class TelemetryBatch
{
    public long BatchSeq { get; set; }
    public string WrappedKey { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Ciphertext { get; set; } = "";
}

public sealed class UploadAck
{
    public long BatchSeq { get; set; }
    public bool Accepted { get; set; }
    public bool Duplicate { get; set; }
}

public sealed class TelemetryPayload
{
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<PerfSample> Perf { get; set; } = [];
    public List<AdapterSample> Adapters { get; set; } = [];
    public List<ProcessNetSample> ProcessNet { get; set; } = [];
    public List<AppUsageSample> AppUsage { get; set; } = [];
    public List<WebSample> Web { get; set; } = [];
    public List<AgentEvent> Events { get; set; } = [];
    public HardwareInfo? Hardware { get; set; }
    public List<SoftwareItem>? Software { get; set; }
    public SecurityPosture? Security { get; set; }
    public List<ProcessInfo>? Processes { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Perf.Count == 0 && Adapters.Count == 0 && ProcessNet.Count == 0 && AppUsage.Count == 0
                           && Web.Count == 0 && Events.Count == 0 && Hardware is null && Software is null
                           && Security is null && Processes is null;
}

public sealed record PerfSample(DateTime Time, double Cpu, double Ram, double Disk);
public sealed record AdapterSample(DateTime Time, string Adapter, string Type, long BytesIn, long BytesOut);
public sealed record ProcessNetSample(DateTime Time, string Process, long BytesIn, long BytesOut);
public sealed record AppUsageSample(DateTime Time, string? User, string App, string? Title, int ActiveSec, int IdleSec);
/// <summary>Per minute: DNS lookups of a host, plus bytes exchanged with the IPs that host resolved to (0 from older agents).</summary>
public sealed record WebSample(DateTime Time, string? User, string Domain, string? Process, int Count, long BytesIn = 0, long BytesOut = 0);
/// <summary>Live traffic to a site (registrable domain) since the previous heartbeat.</summary>
public sealed record LiveDomain(string Domain, long InBps, long OutBps);
public sealed record AgentEvent(DateTime Time, string Type, string Severity, string Message, Dictionary<string, string>? Data = null);
public sealed record ProcessInfo(int Pid, string Name, string? User, long MemoryMb, string? Path);

public sealed class HardwareInfo
{
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? Serial { get; set; }
    public string? BiosVersion { get; set; }
    public string? Cpu { get; set; }
    public int CpuCores { get; set; }
    public int CpuThreads { get; set; }
    public long RamMb { get; set; }
    public string? Os { get; set; }
    public string? OsBuild { get; set; }
    public string? Domain { get; set; }
    public List<DiskInfo> Disks { get; set; } = [];
    public List<NicInfo> Nics { get; set; } = [];
    public string? Gpu { get; set; }
    public bool TpmPresent { get; set; }
}

public sealed record DiskInfo(string Name, string? Model, long SizeGb, long FreeGb, string? Type);
public sealed record NicInfo(string Name, string? Mac, string? Type, string? Ip, long SpeedMbps);
public sealed record SoftwareItem(string Name, string? Version, string? Publisher, string? InstallDate);

public sealed class SecurityPosture
{
    public string? Antivirus { get; set; }
    public bool? AntivirusEnabled { get; set; }
    public bool? AntivirusUpToDate { get; set; }
    public bool? FirewallEnabled { get; set; }
    public string? BitLocker { get; set; }
    public DateTime? LastUpdateInstalled { get; set; }
    public bool? RebootPending { get; set; }
    public bool? UsbStorageBlocked { get; set; }
}

// ───────────────────────── Commands ─────────────────────────

public static class CommandTypes
{
    public const string PolicyUpdated = "policyUpdated";
    public const string RefreshInventory = "refreshInventory";
    public const string CollectNow = "collectNow";
    public const string ShowMessage = "showMessage";
    public const string SetUninstallCode = "setUninstallCode";
    public const string Uninstall = "uninstall";
    public const string UpdateAgent = "updateAgent";
}

public sealed class ServerCommand
{
    public Guid CommandId { get; set; }
    public Guid DeviceId { get; set; }
    public string Type { get; set; } = "";
    public string? Payload { get; set; }
    public DateTime IssuedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    /// <summary>ECDSA P-256 signature (base64) over <see cref="SigningString"/>.</summary>
    public string Signature { get; set; } = "";

    public string SigningString() =>
        $"{CommandId:N}|{DeviceId:N}|{Type}|{Payload}|{IssuedUtc:O}|{ExpiresUtc:O}";
}

public sealed record CommandResult(Guid CommandId, bool Success, string? Message);

// ───────────────────────── Policy ─────────────────────────

public sealed class SignedPolicy
{
    public int Version { get; set; }
    public string PolicyJson { get; set; } = "";
    public string Signature { get; set; } = "";
}

public sealed class AgentPolicy
{
    public string PolicyId { get; set; } = "default";
    public string Name { get; set; } = "Default";
    public int Version { get; set; }
    public CollectorSettings Collectors { get; set; } = new();
    public WorkingHours WorkingHours { get; set; } = new();
    public UploadSettings Upload { get; set; } = new();
    public PrivacySettings Privacy { get; set; } = new();
    public TraySettings Tray { get; set; } = new();
}

public sealed class CollectorSettings
{
    public int HeartbeatSec { get; set; } = 30;
    public int PerformanceSec { get; set; } = 15;
    public bool AppUsage { get; set; } = true;
    /// <summary>full | masked | none</summary>
    public string CaptureWindowTitles { get; set; } = "masked";
    public int IdleThresholdSec { get; set; } = 300;
    public bool BandwidthPerProcess { get; set; } = true;
    public bool BandwidthPerAdapter { get; set; } = true;
    public bool WebDomains { get; set; } = true;
    public bool UsbLog { get; set; } = true;
    public bool UsbBlock { get; set; }
    public bool Inventory { get; set; } = true;
    public bool Security { get; set; } = true;
    public bool Processes { get; set; } = true;
    public bool Sessions { get; set; } = true;
    /// <summary>Device location from Windows Location. Off by default: enable per policy and tell staff (PDPA).</summary>
    public bool Location { get; set; }
    public int LocationIntervalMin { get; set; } = 5;
}

public sealed class WorkingHours
{
    public string TimeZone { get; set; } = "Sri Lanka Standard Time";
    public string Days { get; set; } = "Mon-Fri";
    public string Start { get; set; } = "08:30";
    public string End { get; set; } = "17:00";
    public bool CollectOutsideHours { get; set; } = true;
}

public sealed class UploadSettings
{
    public int IntervalSec { get; set; } = 60;
    public int OfflineMaxMb { get; set; } = 200;
    public int OfflineMaxDays { get; set; } = 7;
}

public sealed class PrivacySettings
{
    public List<string> ExcludeApps { get; set; } = ["KeePass.exe", "1Password.exe"];
    public List<string> ExcludeDomains { get; set; } = ["*.bank.lk", "*.combank.net"];
}

public sealed class TraySettings
{
    public string ContactIt { get; set; } = "Service desk · ext. 1234";
    public string MonitoringNotice { get; set; } =
        "This company-owned computer is monitored in line with the Acceptable Use Policy. " +
        "Collected: device health, installed software, application usage time, network data usage and " +
        "website domains. NOT collected: keystrokes, screenshots, passwords, file contents or personal messages.";
}

// ───────────────────────── Tray IPC (named pipe, newline-delimited JSON) ─────────────────────────

public static class PipeNames
{
    public const string Agent = "SentinelDesk.Agent.v1";
}

public sealed class PipeMessage
{
    public string Type { get; set; } = "";
    // tray → agent
    public string? User { get; set; }
    public int SessionId { get; set; }
    public List<AppUsageSample>? Usage { get; set; }
    public string? CurrentApp { get; set; }
    public string? CurrentTitle { get; set; }
    public bool Idle { get; set; }
    public string? SessionEvent { get; set; }
    public DeviceLocation? Location { get; set; }
    public string? LocationError { get; set; }
    // agent → tray
    public string? State { get; set; }
    public string? StatusText { get; set; }
    public string? Server { get; set; }
    public DateTime? LastUploadUtc { get; set; }
    public int QueueDepth { get; set; }
    public string? Title { get; set; }
    public string? Body { get; set; }
    public TraySettings? Tray { get; set; }
    public string? CaptureWindowTitles { get; set; }
    public int IdleThresholdSec { get; set; }
    public List<string>? ExcludeApps { get; set; }
    public string? DeviceId { get; set; }
    public string? AgentVersion { get; set; }
    public bool LocationEnabled { get; set; }
    public int LocationIntervalMin { get; set; }
}
