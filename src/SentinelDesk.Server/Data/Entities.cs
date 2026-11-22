using System.ComponentModel.DataAnnotations;

namespace SentinelDesk.Server.Data;

public enum DeviceStatus { Pending, Active, Disabled, Retired }

public sealed class OrgUnit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ParentId { get; set; }
    [MaxLength(120)] public string Name { get; set; } = "";
    /// <summary>organization | region | branch | department</summary>
    [MaxLength(20)] public string Type { get; set; } = "branch";
    public double? Lat { get; set; }
    public double? Lng { get; set; }
}

public sealed class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? OrgUnitId { get; set; }
    [MaxLength(64)] public string MachineId { get; set; } = "";
    [MaxLength(64)] public string Hostname { get; set; } = "";
    [MaxLength(64)] public string? Domain { get; set; }
    [MaxLength(64)] public string? Serial { get; set; }
    [MaxLength(64)] public string? Manufacturer { get; set; }
    [MaxLength(64)] public string? Model { get; set; }
    [MaxLength(80)] public string Os { get; set; } = "";
    [MaxLength(40)] public string OsBuild { get; set; } = "";
    [MaxLength(20)] public string AgentVersion { get; set; } = "";
    [MaxLength(64)] public string? CertThumbprint { get; set; }
    public DateTime? CertExpiresAt { get; set; }
    public DeviceStatus Status { get; set; } = DeviceStatus.Pending;
    /// <summary>Enrollment token used for the latest (re-)enrollment.</summary>
    public Guid? EnrollmentTokenId { get; set; }
    // latest Windows location (only when enabled by policy)
    public double? LocLat { get; set; }
    public double? LocLng { get; set; }
    public double? LocAccuracyM { get; set; }
    public DateTime? LocAt { get; set; }
    [MaxLength(32)] public string? LocSource { get; set; }
    [MaxLength(200)] public string? LocError { get; set; }
    public DateTime EnrolledAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenAt { get; set; }
    [MaxLength(45)] public string? LastIp { get; set; }
    [MaxLength(45)] public string? PublicIp { get; set; }
    [MaxLength(80)] public string? LoggedInUser { get; set; }
    public long LastBatchSeq { get; set; }
    public int PolicyVersionApplied { get; set; }
    [MaxLength(200)] public string? Tags { get; set; }
    public bool IsSimulated { get; set; }
    public string? UninstallCodeHash { get; set; }
}

public sealed class HardwareSnapshot
{
    [Key] public Guid DeviceId { get; set; }
    public DateTime CollectedAt { get; set; }
    public string Json { get; set; } = "{}";
}

public sealed class SecuritySnapshot
{
    [Key] public Guid DeviceId { get; set; }
    public DateTime CollectedAt { get; set; }
    public string Json { get; set; } = "{}";
}

public sealed class ProcessSnapshot
{
    [Key] public Guid DeviceId { get; set; }
    public DateTime CollectedAt { get; set; }
    public string Json { get; set; } = "[]";
}

public sealed class InstalledSoftware
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    [MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(60)] public string? Version { get; set; }
    [MaxLength(120)] public string? Publisher { get; set; }
    [MaxLength(20)] public string? InstallDate { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime? RemovedAt { get; set; }
}

public sealed class Policy
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(80)] public string Name { get; set; } = "";
    [MaxLength(300)] public string? Description { get; set; }
    public int Version { get; set; } = 1;
    public string Json { get; set; } = "{}";
    public bool IsDefault { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    [MaxLength(80)] public string? UpdatedBy { get; set; }
}

public sealed class PolicyAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PolicyId { get; set; }
    /// <summary>orgUnit | device</summary>
    [MaxLength(20)] public string TargetType { get; set; } = "orgUnit";
    public Guid TargetId { get; set; }
}

public sealed class AlertRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(100)] public string Name { get; set; } = "";
    /// <summary>dataUsageDaily | agentOffline | cpuHigh | diskLow | software | tamper | category | usb</summary>
    [MaxLength(30)] public string Type { get; set; } = "";
    public double Threshold { get; set; }
    [MaxLength(300)] public string? Parameter { get; set; }
    /// <summary>info | warning | critical</summary>
    [MaxLength(10)] public string Severity { get; set; } = "warning";
    public bool Enabled { get; set; } = true;
    public bool NotifyEmail { get; set; }
    public bool NotifyWebhook { get; set; }
}

public sealed class Alert
{
    public long Id { get; set; }
    public Guid RuleId { get; set; }
    public Guid DeviceId { get; set; }
    public DateTime TriggeredAt { get; set; } = DateTime.UtcNow;
    [MaxLength(10)] public string Severity { get; set; } = "warning";
    [MaxLength(200)] public string Title { get; set; } = "";
    [MaxLength(1000)] public string? Details { get; set; }
    /// <summary>open | acknowledged | resolved</summary>
    [MaxLength(15)] public string Status { get; set; } = "open";
    [MaxLength(80)] public string? HandledBy { get; set; }
    public DateTime? HandledAt { get; set; }
    [MaxLength(120)] public string DedupKey { get; set; } = "";
}

public static class Roles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string BranchAdmin = "BranchAdmin";
    public const string Auditor = "Auditor";
    public const string HrViewer = "HrViewer";
    public static readonly string[] All = [SuperAdmin, BranchAdmin, Auditor, HrViewer];
}

public sealed class AdminUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(60)] public string Username { get; set; } = "";
    [MaxLength(100)] public string DisplayName { get; set; } = "";
    [MaxLength(120)] public string? Email { get; set; }
    public string PasswordHash { get; set; } = "";
    [MaxLength(20)] public string Role { get; set; } = Roles.Auditor;
    public Guid? OrgUnitScope { get; set; }
    public string? TotpSecret { get; set; }
    public bool MfaEnabled { get; set; }
    public bool Disabled { get; set; }
    public bool MustChangePassword { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public int FailedLogins { get; set; }
    public DateTime? LockedUntil { get; set; }
}

public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    [MaxLength(60)] public string Actor { get; set; } = "";
    [MaxLength(60)] public string Action { get; set; } = "";
    [MaxLength(200)] public string? Target { get; set; }
    [MaxLength(45)] public string? Ip { get; set; }
    [MaxLength(1000)] public string? Details { get; set; }
}

public sealed class EnrollmentToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(80)] public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    /// <summary>The token itself, AES-256-GCM encrypted with a key derived from the server master key, so admins can
    /// reveal and re-use it. Null for tokens created before this was added (those exist only as a hash).</summary>
    public string? TokenProtected { get; set; }
    [MaxLength(12)] public string Prefix { get; set; } = "";
    public Guid? OrgUnitId { get; set; }
    public DateTime ExpiresAt { get; set; }
    public int UsesLeft { get; set; }
    public int UsedCount { get; set; }
    public bool AutoApprove { get; set; } = true;
    public bool Revoked { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [MaxLength(60)] public string? CreatedBy { get; set; }
}

public sealed class DeviceCommand
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    [MaxLength(30)] public string Type { get; set; } = "";
    public string? Payload { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddDays(3);
    [MaxLength(60)] public string? CreatedBy { get; set; }
    /// <summary>pending | sent | succeeded | failed | expired</summary>
    [MaxLength(12)] public string Status { get; set; } = "pending";
    public DateTime? CompletedAt { get; set; }
    [MaxLength(500)] public string? Result { get; set; }
}

public sealed class AgentRelease
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(20)] public string Version { get; set; } = "";
    [MaxLength(200)] public string FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Signature { get; set; } = "";
    public long SizeBytes { get; set; }
    public int RolloutPercent { get; set; }
    [MaxLength(300)] public string? Notes { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    [MaxLength(60)] public string? UploadedBy { get; set; }
}

// ───────────── time-series (hypertables in the PostgreSQL/Timescale deployment) ─────────────

/// <summary>Location history: a row when the device moved (&gt;150 m) or every 30 minutes while stationary.</summary>
public sealed class LocationPoint
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    public DateTime Time { get; set; }
    public double Lat { get; set; }
    public double Lng { get; set; }
    public double AccuracyM { get; set; }
    [MaxLength(32)] public string? Source { get; set; }
}

public sealed class PerfPoint
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    public DateTime Time { get; set; }
    public float Cpu { get; set; }
    public float Ram { get; set; }
    public float Disk { get; set; }
}

public sealed class NetAdapterPoint
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    public DateTime Time { get; set; }
    [MaxLength(120)] public string Adapter { get; set; } = "";
    [MaxLength(20)] public string Type { get; set; } = "";
    public long BytesIn { get; set; }
    public long BytesOut { get; set; }
}

public sealed class NetProcessPoint
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    public DateTime Time { get; set; }
    [MaxLength(120)] public string Process { get; set; } = "";
    public long BytesIn { get; set; }
    public long BytesOut { get; set; }
}

public sealed class AppUsagePoint
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    public DateTime Time { get; set; }
    [MaxLength(80)] public string? User { get; set; }
    [MaxLength(120)] public string App { get; set; } = "";
    [MaxLength(300)] public string? Title { get; set; }
    public int ActiveSec { get; set; }
    public int IdleSec { get; set; }
}

public sealed class WebPoint
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    public DateTime Time { get; set; }
    [MaxLength(80)] public string? User { get; set; }
    [MaxLength(200)] public string Domain { get; set; } = "";
    [MaxLength(120)] public string? Process { get; set; }
    [MaxLength(30)] public string Category { get; set; } = "";
    public int Count { get; set; }
    /// <summary>Bytes exchanged with the IPs this host resolved to (attributed by the agent from DNS answers).</summary>
    public long BytesIn { get; set; }
    public long BytesOut { get; set; }
}

public sealed class DeviceEvent
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    public DateTime Time { get; set; }
    [MaxLength(40)] public string Type { get; set; } = "";
    [MaxLength(10)] public string Severity { get; set; } = "info";
    [MaxLength(500)] public string Message { get; set; } = "";
    public string? DataJson { get; set; }
}

/// <summary>Daily rollup (continuous-aggregate equivalent) maintained on ingest.</summary>
public sealed class DailyUsage
{
    public Guid DeviceId { get; set; }
    public DateOnly Day { get; set; }
    public long BytesIn { get; set; }
    public long BytesOut { get; set; }
    public int ActiveSec { get; set; }
    public int IdleSec { get; set; }
}
