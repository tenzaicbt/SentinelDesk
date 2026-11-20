using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;
using SentinelDesk.Agent.Core;
using SentinelDesk.Agent.Pipeline;
using SentinelDesk.Shared;

namespace SentinelDesk.Agent.Collectors;

/// <summary>Hardware / software / security posture (daily + on demand) and running-process snapshots (5 min).</summary>
public sealed class InventoryCollector(TelemetryAggregator agg, PolicyStore policy, ILogger<InventoryCollector> log) : BackgroundService
{
    private readonly SemaphoreSlim _now = new(0);
    private DateTime _lastInventory = DateTime.MinValue;
    private DateTime _lastProcesses = DateTime.MinValue;
    private string? _lastSoftwareHash;

    public void RefreshNow()
    {
        _lastInventory = DateTime.MinValue;
        _lastProcesses = DateTime.MinValue;
        _now.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var c = policy.Current.Collectors;
                if (c.Inventory && DateTime.UtcNow - _lastInventory > TimeSpan.FromHours(24))
                {
                    _lastInventory = DateTime.UtcNow;
                    await Task.Run(() =>
                    {
                        agg.SetHardware(Hardware());
                        var sw = Software();
                        _lastSoftwareHash = Hash(sw);
                        agg.SetSoftware(sw);
                        if (c.Security) agg.SetSecurity(Security());
                    }, ct);
                    log.LogInformation("Inventory collected");
                }
                else if (c.Inventory && DateTime.UtcNow.Minute % 30 == 0)
                {
                    // cheap change detection: re-send the software list only when it changed
                    var sw = Software();
                    var h = Hash(sw);
                    if (h != _lastSoftwareHash) { _lastSoftwareHash = h; agg.SetSoftware(sw); }
                }
                if (c.Processes && DateTime.UtcNow - _lastProcesses > TimeSpan.FromMinutes(5))
                {
                    _lastProcesses = DateTime.UtcNow;
                    agg.SetProcesses(Processes());
                }
            }
            catch (Exception ex) { log.LogWarning(ex, "Inventory collection failed"); }
            await _now.WaitAsync(TimeSpan.FromSeconds(60), ct).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    private static string Hash(List<SoftwareItem> sw) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("|", sw.Select(s => s.Name + s.Version)))));

    private static IEnumerable<ManagementBaseObject> Query(string wql, string scope = @"root\cimv2")
    {
        using var s = new ManagementObjectSearcher(scope, wql);
        foreach (var o in s.Get()) yield return o;
    }

    public static HardwareInfo Hardware()
    {
        var h = new HardwareInfo();
        Try(() =>
        {
            foreach (var o in Query("SELECT Manufacturer, Model, TotalPhysicalMemory, Domain FROM Win32_ComputerSystem"))
            {
                h.Manufacturer = o["Manufacturer"]?.ToString()?.Trim();
                h.Model = o["Model"]?.ToString()?.Trim();
                h.RamMb = Convert.ToInt64(o["TotalPhysicalMemory"] ?? 0) / 1048576;
                h.Domain = o["Domain"]?.ToString();
            }
        });
        Try(() =>
        {
            foreach (var o in Query("SELECT SerialNumber, SMBIOSBIOSVersion FROM Win32_BIOS"))
            { h.Serial = o["SerialNumber"]?.ToString()?.Trim(); h.BiosVersion = o["SMBIOSBIOSVersion"]?.ToString(); }
        });
        Try(() =>
        {
            foreach (var o in Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor"))
            {
                h.Cpu = o["Name"]?.ToString()?.Trim();
                h.CpuCores += Convert.ToInt32(o["NumberOfCores"] ?? 0);
                h.CpuThreads += Convert.ToInt32(o["NumberOfLogicalProcessors"] ?? 0);
            }
        });
        Try(() =>
        {
            foreach (var o in Query("SELECT Caption FROM Win32_OperatingSystem")) h.Os = o["Caption"]?.ToString()?.Replace("Microsoft ", "").Trim();
            h.OsBuild = Enrollment.OsBuild();
        });
        Try(() =>
        {
            foreach (var o in Query("SELECT Name FROM Win32_VideoController")) { h.Gpu = o["Name"]?.ToString(); break; }
        });
        Try(() =>
        {
            var media = new Dictionary<string, string>();
            try
            {
                foreach (var o in Query("SELECT FriendlyName, MediaType FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage"))
                    media[o["FriendlyName"]?.ToString() ?? ""] = Convert.ToInt32(o["MediaType"] ?? 0) switch { 3 => "HDD", 4 => "SSD", _ => "Unknown" };
            }
            catch { }
            var model = Query("SELECT Model FROM Win32_DiskDrive WHERE MediaType LIKE 'Fixed%'").Select(o => o["Model"]?.ToString()).FirstOrDefault();
            foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
                h.Disks.Add(new DiskInfo(d.Name.TrimEnd('\\'), model, d.TotalSize / 1_000_000_000, d.AvailableFreeSpace / 1_000_000_000,
                    model is not null && media.TryGetValue(model, out var t) ? t : media.Values.FirstOrDefault()));
        });
        Try(() =>
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var type = MetricsCollector.Classify(nic);
                if (type is null) continue;
                var ip = nic.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
                var mac = string.Join(":", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
                h.Nics.Add(new NicInfo(nic.Name, mac.Length > 0 ? mac : null, type, ip, nic.Speed > 0 ? nic.Speed / 1_000_000 : 0));
            }
        });
        Try(() =>
        {
            foreach (var o in Query("SELECT IsEnabled_InitialValue FROM Win32_Tpm", @"root\CIMV2\Security\MicrosoftTpm")) h.TpmPresent = true;
        });
        return h;
    }

    public static List<SoftwareItem> Software()
    {
        var items = new Dictionary<string, SoftwareItem>(StringComparer.OrdinalIgnoreCase);
        void Read(RegistryKey? root, string path)
        {
            using var key = root?.OpenSubKey(path);
            if (key is null) return;
            foreach (var sub in key.GetSubKeyNames())
            {
                try
                {
                    using var k = key.OpenSubKey(sub);
                    var name = k?.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (k!.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    if (k.GetValue("ParentKeyName") is not null || k.GetValue("ReleaseType") is "Update" or "Hotfix" or "Security Update") continue;
                    var version = k.GetValue("DisplayVersion") as string;
                    items[$"{name}|{version}"] = new SoftwareItem(name.Trim(), version?.Trim(), (k.GetValue("Publisher") as string)?.Trim(), k.GetValue("InstallDate") as string);
                }
                catch { }
            }
        }
        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        Read(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64), uninstall);
        Read(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32), uninstall);
        try
        {
            foreach (var sid in Registry.Users.GetSubKeyNames().Where(s => s.StartsWith("S-1-5-21") && !s.EndsWith("_Classes")))
                Read(Registry.Users, $@"{sid}\{uninstall}");
        }
        catch { }
        return items.Values.OrderBy(i => i.Name).ToList();
    }

    public static SecurityPosture Security()
    {
        var s = new SecurityPosture();
        Try(() =>
        {
            foreach (var o in Query("SELECT displayName, productState FROM AntiVirusProduct", @"root\SecurityCenter2"))
            {
                var state = Convert.ToInt32(o["productState"] ?? 0);
                var enabled = ((state >> 12) & 0xF) == 1;
                // prefer an enabled third-party product over a passive Defender entry
                if (s.Antivirus is not null && s.AntivirusEnabled == true && !enabled) continue;
                s.Antivirus = o["displayName"]?.ToString();
                s.AntivirusEnabled = enabled;
                s.AntivirusUpToDate = ((state >> 4) & 0xF) == 0;
            }
        });
        Try(() =>
        {
            const string fw = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";
            var profiles = new[] { "DomainProfile", "StandardProfile", "PublicProfile" };
            s.FirewallEnabled = profiles.All(p => Registry.LocalMachine.OpenSubKey($@"{fw}\{p}")?.GetValue("EnableFirewall") is int v && v == 1);
        });
        Try(() =>
        {
            var sys = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\');
            foreach (var o in Query($"SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter='{sys}'", @"root\CIMV2\Security\MicrosoftVolumeEncryption"))
                s.BitLocker = Convert.ToInt32(o["ProtectionStatus"] ?? 2) switch { 0 => "Off", 1 => "On", _ => "Unknown" };
        });
        Try(() =>
        {
            DateTime? latest = null;
            foreach (var o in Query("SELECT InstalledOn FROM Win32_QuickFixEngineering"))
                if (DateTime.TryParse(o["InstalledOn"]?.ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var d) && (latest is null || d > latest))
                    latest = d;
            s.LastUpdateInstalled = latest?.ToUniversalTime();
        });
        Try(() =>
        {
            s.RebootPending = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") is not null
                              || Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") is not null;
        });
        Try(() => s.UsbStorageBlocked = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\USBSTOR")?.GetValue("Start") is int v && v == 4);
        return s;
    }

    public static List<ProcessInfo> Processes()
    {
        var sessions = Native.NativeMethods.EnumerateSessions().ToDictionary(s => s.SessionId, s => s.User);
        var list = new List<ProcessInfo>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id <= 4) continue;
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { }
                list.Add(new ProcessInfo(p.Id, p.ProcessName + ".exe", sessions.GetValueOrDefault(p.SessionId) ?? (p.SessionId == 0 ? "SYSTEM" : null),
                    p.WorkingSet64 / 1048576, path));
            }
            catch { }
            finally { p.Dispose(); }
        }
        return list.OrderByDescending(p => p.MemoryMb).Take(300).ToList();
    }

    private static void Try(Action a)
    {
        try { a(); } catch { /* individual WMI classes may be unavailable (e.g. Server SKU, no admin) */ }
    }
}
