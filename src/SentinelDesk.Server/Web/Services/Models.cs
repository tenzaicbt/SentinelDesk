using SentinelDesk.Server.Services;
using SentinelDesk.Shared;

namespace SentinelDesk.Server.Web.Services;

// Shapes of the JSON the console reads from the server API (/api/...). Types the server already declares
// (LivePresence, AgentPolicy, HardwareInfo, report rows, AlertRule, AuditEntry, AgentRelease) are reused directly.

public sealed record Me(Guid Id, string Username, string DisplayName, string? Email, string Role, bool MfaEnabled, bool MustChangePassword,
    Guid? OrgUnitScope, string? ScopeName, DateTime? LastLoginAt);

public sealed record LoginResult(bool MfaRequired, string Token, bool MfaSetupRecommended = false);
public sealed record MfaSetup(string Secret, string Uri);

public sealed record OrgPolicyRef(Guid PolicyId, string? Name);
public sealed record OrgUnitRow(Guid Id, Guid? ParentId, string Name, string Type, double? Lat, double? Lng, int Devices, int Online, List<OrgPolicyRef>? Policies);

public sealed record DeviceRow(Guid Id, string Hostname, string? Domain, string? User, string? Ip, string? PublicIp, string Os, string OsBuild,
    string AgentVersion, string Status, bool Online, DateTime? LastSeen, Guid? OrgUnitId, string? Branch, string? Manufacturer, string? Model,
    string? Serial, string? Tags, bool IsSimulated, long TodayBytes, double? Cpu, double? Ram, string? CurrentApp, int Alerts);

public sealed record SearchHit(Guid Id, string Hostname, string? LoggedInUser, string? LastIp, string Status);

// ── dashboard
public sealed record Kpis(int Total, int Online, int Pending, int OpenAlerts, int CriticalAlerts, long TodayBytesIn, long TodayBytesOut, long YesterdayBytes, double ActiveHoursToday);
public sealed record UsageDay(string Day, long BytesIn, long BytesOut, double ActiveHours);
public sealed record TopApp(string App, long Seconds, int Devices);
public sealed record TopSite(string Domain, string? Category, long Hits);
public sealed record CategoryHits(string Category, long Hits);
public sealed record BranchPoint(Guid Id, string Name, double Lat, double Lng, int Total, int Online, long TodayBytes);
public sealed record DevicePoint(Guid Id, string Hostname, string? User, string? Branch, double Lat, double Lng, double AccuracyM, DateTime? At, string? Source, bool Online);
public sealed record NameCount(string? Name, int Count);
public sealed record RecentAlert(long Id, string Title, string Severity, DateTime TriggeredAt, Guid DeviceId, string? Hostname);
public sealed record TopConsumer(Guid DeviceId, string? Hostname, long Bytes);
public sealed record DashboardData(Kpis Kpis, List<UsageDay> Usage, List<TopApp> TopApps, List<TopSite>? TopSites, List<CategoryHits>? Categories,
    List<BranchPoint> Branches, List<DevicePoint>? DeviceLocations, List<NameCount> Os, List<NameCount> Versions, List<RecentAlert> RecentAlerts, List<TopConsumer> TopConsumers);

public sealed record LiveRow(LivePresence Presence, string? Branch);

// ── device detail
public sealed record Restriction(Guid Id, string Name, string Kind);
public sealed record DevicePolicyInfo(Guid Id, string Name, int EffectiveVersion, bool Applied, List<Restriction>? Restrictions);
public sealed record TodayUsage(long BytesIn, long BytesOut, long ActiveSec, long IdleSec);
public sealed record LocationInfo(bool Enabled, double? Lat, double? Lng, double? AccuracyM, DateTime? At, string? Source, string? Error);
public sealed record DeviceDetail(Guid Id, string Hostname, string? Domain, string MachineId, string? Serial, string? Manufacturer, string? Model,
    string Os, string OsBuild, string AgentVersion, string Status, DateTime EnrolledAt, DateTime? LastSeenAt, string? LastIp, string? PublicIp,
    string? LoggedInUser, Guid? OrgUnitId, List<string> OrgPath, string? Tags, string? CertThumbprint, DateTime? CertExpiresAt, bool IsSimulated,
    DevicePolicyInfo Policy, bool Online, LivePresence? Presence, TodayUsage Today, LocationInfo? Location, HardwareInfo? Hardware,
    DateTime? HardwareCollectedAt, SecurityPosture? Security, DateTime? SecurityCollectedAt, bool LiveChannel);

public sealed record LocationPoint(DateTime Time, double Lat, double Lng, double AccuracyM, string? Source);

public sealed record AppTitle(string Title, long Sec);
public sealed record AppUsage(string App, long ActiveSec, List<AppTitle>? Titles);
public sealed record HourActivity(int Hour, long ActiveSec, long IdleSec);
public sealed record AppsData(List<AppUsage> Apps, List<HourActivity> Timeline, long TotalActiveSec, long TotalIdleSec, List<string?> Users,
    List<ProcessInfo>? Processes, DateTime? ProcessesCollectedAt);

public sealed record DayBytes(string Day, long BytesIn, long BytesOut);
public sealed record HourBytes(int Hour, long BytesIn, long BytesOut);
public sealed record AdapterBytes(string Adapter, string? Type, long BytesIn, long BytesOut);
public sealed record ProcessBytes(string Process, long BytesIn, long BytesOut);
public sealed record BandwidthData(List<DayBytes> Daily, List<HourBytes> Hourly, List<AdapterBytes> Adapters, List<ProcessBytes> Processes, int RangeDays);

public sealed record WebDomain(string Domain, string Category, long Hits, DateTime LastVisit, long BytesIn, long BytesOut, List<string> Hosts, List<string?> Users, List<string?> Processes);
public sealed record WebCategory(string Category, long Hits, long Bytes);
public sealed record WebData(List<WebDomain> Domains, List<WebCategory> Categories, long TotalBytesIn, long TotalBytesOut);
public sealed record LiveSite(string Domain, string Category, long InBps, long OutBps);
public sealed record LiveWeb(DateTime? At, bool Online, List<LiveSite> Sites);

public sealed record CommandQueued(Guid Id, string Status);
public sealed record UninstallCode(string Code, DateTime ExpiresUtc);
public sealed record UnlockCode(string Code, int Hours, DateTime ValidUntil);
public sealed record QueuedResult(int Queued);
public sealed record DeletedResult(int Deleted);
public sealed record RemovedResult(int Removed);
public sealed record IdResult(Guid Id);
public sealed record TokenResult(string Token);

// ── hub messages
public sealed record LiveSampleMsg(Guid DeviceId, long Seq, long AgentTimeMs, long ServerTimeMs, int IntervalMs, double Cpu, double Ram,
    long NetInBps, long NetOutBps, string? CurrentApp, string? CurrentTitle, bool Idle, bool Locked);
public sealed record AlertMsg(long Id, string Title, string Severity, Guid DeviceId, string? Hostname, DateTime TriggeredAt);
public sealed record CommandStatusMsg(Guid Id, string Type, string Status, string? Result);

// ── alerts
public sealed record AlertRow(long Id, string Title, string? Details, string Severity, string Status, DateTime TriggeredAt, string? HandledBy,
    DateTime? HandledAt, Guid DeviceId, string Hostname, string? Rule);

// ── policies
public sealed record PolicyAssignmentRow(Guid Id, string TargetType, Guid TargetId, DateTime? UntilUtc, string? Name);
public sealed record PolicyExceptionRow(Guid Id, string TargetType, Guid TargetId, string? Value, DateTime? UntilUtc, string? Reason, string? CreatedBy, bool Global, string? Name);
public sealed record PolicyRow(Guid Id, string Name, string? Description, int Version, bool IsDefault, string Kind, DateTime UpdatedAt, string? UpdatedBy,
    AgentPolicy Settings, List<PolicyAssignmentRow> Assignments, List<PolicyExceptionRow>? Exceptions);
public sealed record AccessRequestRow(Guid Id, Guid DeviceId, string? Hostname, string? User, string Kind, string Value, string? Reason,
    string Status, DateTime CreatedAt, string? DecidedBy, DateTime? DecidedAt, DateTime? GrantUntilUtc);
public sealed record EmergencyRow(Guid Id, Guid? OrgUnitId, string Scope, DateTime? UntilUtc, string? Reason, string? CreatedBy, DateTime CreatedAt);
public sealed record RecentDrive(string Serial, string? Model, string? LastHost, DateTime LastSeen);

// ── agents
public sealed record TokenRow(Guid Id, string Name, string Prefix, Guid? OrgUnitId, string? OrgUnit, DateTime ExpiresAt, int UsesLeft, int UsedCount,
    bool AutoApprove, bool Revoked, DateTime CreatedAt, string? CreatedBy, bool Active, bool Revealable, int Devices, int ActiveDevices, int OnlineDevices);
public sealed record TokenDevice(Guid Id, string Hostname, string Status, bool Online, DateTime? LastSeen, string? User, string AgentVersion,
    DateTime EnrolledAt, string? OrgUnit, string Os);
public sealed record VersionCount(string Version, int Count);
public sealed record ReleasesData(List<Data.AgentRelease> Releases, List<VersionCount> Versions);
public sealed record ServerInfo(string CaThumbprint, DateTime CaExpires, string SigningKeyFingerprint, string EncryptionKeyFingerprint,
    string? PublicUrl, bool RequireMtls, bool DemoMode);
public sealed record InstallerInfo(bool Available, long? SizeBytes, DateTime? BuiltAt, List<string> Suggestions);

// ── users
public sealed record UserRow(Guid Id, string Username, string DisplayName, string? Email, string Role, Guid? OrgUnitScope, string? Scope,
    bool MfaEnabled, bool Disabled, DateTime? LastLoginAt, bool Locked);

// ── reports
public sealed record UsageSeries(string Day, long BytesIn, long BytesOut);
public sealed record BranchUsage(string Branch, double TotalMb, int Devices);
public sealed record UsageReport(List<Endpoints.ReportEndpoints.UsageRow> Rows, List<UsageSeries> Series, List<BranchUsage> ByBranch);
public sealed record AppsReport(List<Endpoints.ReportEndpoints.AppRow> Rows);
public sealed record WebReport(List<Endpoints.ReportEndpoints.WebRow> Rows, List<CategoryHits> Categories);
public sealed record InventoryReport(List<Endpoints.ReportEndpoints.InventoryRow> Rows);
public sealed record SoftwareReport(List<Endpoints.ReportEndpoints.SoftwareRow> Rows);
