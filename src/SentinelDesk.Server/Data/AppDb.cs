using Microsoft.EntityFrameworkCore;

namespace SentinelDesk.Server.Data;

public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<OrgUnit> OrgUnits => Set<OrgUnit>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<HardwareSnapshot> Hardware => Set<HardwareSnapshot>();
    public DbSet<SecuritySnapshot> Security => Set<SecuritySnapshot>();
    public DbSet<ProcessSnapshot> Processes => Set<ProcessSnapshot>();
    public DbSet<InstalledSoftware> Software => Set<InstalledSoftware>();
    public DbSet<Policy> Policies => Set<Policy>();
    public DbSet<PolicyAssignment> PolicyAssignments => Set<PolicyAssignment>();
    public DbSet<BlockExceptionRow> BlockExceptions => Set<BlockExceptionRow>();
    public DbSet<AccessRequest> AccessRequests => Set<AccessRequest>();
    public DbSet<EmergencyUnblock> EmergencyUnblocks => Set<EmergencyUnblock>();
    public DbSet<AlertRule> AlertRules => Set<AlertRule>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();
    public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();
    public DbSet<DeviceCommand> Commands => Set<DeviceCommand>();
    public DbSet<AgentRelease> Releases => Set<AgentRelease>();
    public DbSet<PerfPoint> Perf => Set<PerfPoint>();
    public DbSet<LocationPoint> Locations => Set<LocationPoint>();
    public DbSet<NetAdapterPoint> NetAdapter => Set<NetAdapterPoint>();
    public DbSet<NetProcessPoint> NetProcess => Set<NetProcessPoint>();
    public DbSet<AppUsagePoint> AppUsage => Set<AppUsagePoint>();
    public DbSet<WebPoint> Web => Set<WebPoint>();
    public DbSet<DeviceEvent> Events => Set<DeviceEvent>();
    public DbSet<DailyUsage> Daily => Set<DailyUsage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Device>().HasIndex(x => x.MachineId);
        b.Entity<Device>().HasIndex(x => x.OrgUnitId);
        b.Entity<Device>().Property(x => x.Status).HasConversion<string>().HasMaxLength(12);
        b.Entity<AdminUser>().HasIndex(x => x.Username).IsUnique();
        b.Entity<InstalledSoftware>().HasIndex(x => new { x.DeviceId, x.Name });
        b.Entity<Alert>().HasIndex(x => new { x.Status, x.TriggeredAt });
        b.Entity<Alert>().HasIndex(x => x.DedupKey);
        b.Entity<AuditEntry>().HasIndex(x => x.At);
        b.Entity<DeviceCommand>().HasIndex(x => new { x.DeviceId, x.Status });
        b.Entity<PerfPoint>().HasIndex(x => new { x.DeviceId, x.Time });
        b.Entity<LocationPoint>().HasIndex(x => new { x.DeviceId, x.Time });
        b.Entity<NetAdapterPoint>().HasIndex(x => new { x.DeviceId, x.Time });
        b.Entity<NetAdapterPoint>().HasIndex(x => x.Time);
        b.Entity<NetProcessPoint>().HasIndex(x => new { x.DeviceId, x.Time });
        b.Entity<AppUsagePoint>().HasIndex(x => new { x.DeviceId, x.Time });
        b.Entity<AppUsagePoint>().HasIndex(x => x.Time);
        b.Entity<WebPoint>().HasIndex(x => new { x.DeviceId, x.Time });
        b.Entity<WebPoint>().HasIndex(x => x.Time);
        b.Entity<DeviceEvent>().HasIndex(x => new { x.DeviceId, x.Time });
        b.Entity<DeviceEvent>().HasIndex(x => x.Time);
        b.Entity<DailyUsage>().HasKey(x => new { x.DeviceId, x.Day });
        b.Entity<DailyUsage>().HasIndex(x => x.Day);
        b.Entity<BlockExceptionRow>().ToTable("BlockExceptions").HasIndex(x => new { x.TargetType, x.TargetId });
        b.Entity<AccessRequest>().HasIndex(x => new { x.Status, x.CreatedAt });

        // All timestamps are stored as UTC; make sure they come back flagged as UTC (serialised with 'Z').
        var utc = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
            v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var utcNullable = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime?, DateTime?>(
            v => v == null ? v : v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime(),
            v => v == null ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));
        foreach (var entity in b.Model.GetEntityTypes())
            foreach (var prop in entity.GetProperties())
            {
                if (prop.ClrType == typeof(DateTime)) prop.SetValueConverter(utc);
                else if (prop.ClrType == typeof(DateTime?)) prop.SetValueConverter(utcNullable);
            }
    }
}
