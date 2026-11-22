using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace SentinelDesk.Shared;

/// <summary>
/// Self-protection enforced by the Agent and Guard services (both LocalSystem). The service DACL set at install time stops
/// administrators from stopping or deleting the services through the Service Control Manager, but the service's registry key
/// would still let them set Start=Disabled or point ImagePath elsewhere and reboot, and Safe Mode would skip the services.
/// This locks those registry keys to SYSTEM (owner SYSTEM, administrators read-only), registers the services for Safe Mode,
/// and repairs anything that was changed. Every repair is returned so the caller can raise a tamper event.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ServiceProtection
{
    public const string AgentService = "SentinelDeskAgent";
    public const string GuardService = "SentinelDeskGuard";
    private const string ServicesRoot = @"SYSTEM\CurrentControlSet\Services";
    private const string SafeBootRoot = @"SYSTEM\CurrentControlSet\Control\SafeBoot";
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private static readonly string[] SafeBootModes = ["Minimal", "Network"];

    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier AuthUsers = new(WellKnownSidType.AuthenticatedUserSid, null);

    private const RegistryRights WriteRights = RegistryRights.SetValue | RegistryRights.CreateSubKey | RegistryRights.Delete |
        RegistryRights.ChangePermissions | RegistryRights.TakeOwnership | RegistryRights.CreateLink;

    /// <summary>
    /// Checks and repairs both services, their Safe Mode registration and the tray auto-start entry.
    /// <paramref name="reportAcl"/> is false on the first pass after (re)start, so locking keys for the first time (fresh
    /// install / upgrade) is not reported as tampering; value changes are always reported.
    /// </summary>
    public static List<string> Enforce(string installDir, bool reportAcl)
    {
        var findings = new List<string>();
        // not installed as services (development run from the build output): nothing to protect
        using (var installed = Registry.LocalMachine.OpenSubKey($@"{ServicesRoot}\{AgentService}")) if (installed is null) return findings;
        // Agent and Guard both enforce: serialise so a change is repaired (and reported) once, not twice.
        using var mutex = new Mutex(false, @"Global\SentinelDesk.Protection");
        var owned = false;
        try { owned = mutex.WaitOne(TimeSpan.FromSeconds(5)); } catch (AbandonedMutexException) { owned = true; }
        try
        {
            void Try(string what, Action a)
            {
                try { a(); }
                catch (Exception ex) { findings.Add($"Could not verify {what} (permissions may have been taken over): {ex.Message}"); }
            }
            Try(AgentService, () => EnforceService(AgentService, Path.Combine(installDir, "SentinelDesk.Agent.exe"), reportAcl, findings));
            Try(GuardService, () => EnforceService(GuardService, Path.Combine(installDir, "SentinelDesk.Guard.exe"), reportAcl, findings));
            Try("tray auto-start", () => EnforceTrayAutostart(Path.Combine(installDir, "SentinelDesk.Tray.exe"), findings));
        }
        finally { if (owned) mutex.ReleaseMutex(); }
        return findings;
    }

    private static void EnforceService(string name, string exePath, bool reportAcl, List<string> findings)
    {
        using (var key = Registry.LocalMachine.OpenSubKey($@"{ServicesRoot}\{name}", RegistryKeyPermissionCheck.ReadWriteSubTree,
                   RegistryRights.ReadKey | RegistryRights.WriteKey | RegistryRights.ChangePermissions | RegistryRights.TakeOwnership))
        {
            if (key is null) return; // service missing: reported by the watchdog check
            if (Lock(key) && reportAcl) findings.Add($"Permissions on the {name} service registry key were changed - locked again");

            var start = key.GetValue("Start") as int?;
            if (start != 2)
            {
                key.SetValue("Start", 2, RegistryValueKind.DWord);
                findings.Add($"{name} start type was set to {StartName(start)} - restored to Automatic");
            }
            var image = (key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string)?.Trim();
            if (!string.Equals(image?.Trim('"'), exePath, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue("ImagePath", $"\"{exePath}\"", RegistryValueKind.ExpandString);
                findings.Add($"{name} program path was changed to '{image}' - restored");
            }
            var account = key.GetValue("ObjectName") as string;
            if (!string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue("ObjectName", "LocalSystem", RegistryValueKind.String);
                findings.Add($"{name} service account was changed to '{account}' - restored to LocalSystem");
            }
            if (key.GetValue("DeleteFlag") is int)
            {
                // "sc delete" was accepted somehow (service marked for deletion at next boot): undo it
                key.DeleteValue("DeleteFlag", false);
                findings.Add($"{name} service was marked for deletion - deletion cancelled");
            }
        }

        foreach (var mode in SafeBootModes)
        {
            using var parent = Registry.LocalMachine.OpenSubKey($@"{SafeBootRoot}\{mode}", writable: true);
            if (parent is null) continue;
            using var existing = parent.OpenSubKey(name, RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.ReadKey | RegistryRights.WriteKey | RegistryRights.ChangePermissions | RegistryRights.TakeOwnership);
            if (existing is null)
            {
                using var created = parent.CreateSubKey(name, writable: true);
                created.SetValue("", "Service", RegistryValueKind.String);
                Lock(created);
                if (reportAcl) findings.Add($"Safe Mode registration of {name} ({mode}) was removed - restored");
                continue;
            }
            if (existing.GetValue("") as string != "Service") existing.SetValue("", "Service", RegistryValueKind.String);
            if (Lock(existing) && reportAcl) findings.Add($"Permissions on the Safe Mode registration of {name} were changed - locked again");
        }
    }

    private static void EnforceTrayAutostart(string trayExe, List<string> findings)
    {
        if (!File.Exists(trayExe)) return;
        using var run = Registry.LocalMachine.OpenSubKey(RunKey, writable: true);
        if (run is null) return;
        var expected = $"\"{trayExe}\"";
        var current = run.GetValue("SentinelDesk") as string;
        if (string.Equals(current, expected, StringComparison.OrdinalIgnoreCase)) return;
        run.SetValue("SentinelDesk", expected, RegistryValueKind.String);
        findings.Add(current is null ? "Tray auto-start entry was removed - restored" : $"Tray auto-start entry was changed to '{current}' - restored");
    }

    /// <summary>Removes Safe Mode registration (authorized uninstall only).</summary>
    public static void RemoveSafeBoot()
    {
        foreach (var mode in SafeBootModes)
            foreach (var name in new[] { AgentService, GuardService })
                try { Registry.LocalMachine.DeleteSubKeyTree($@"{SafeBootRoot}\{mode}\{name}", false); } catch { }
    }

    /// <summary>Owner SYSTEM; SYSTEM full control; Administrators and Authenticated Users read. Returns true if it had to change anything.</summary>
    private static bool Lock(RegistryKey key)
    {
        var current = key.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (IsLocked(current)) return false;
        var sec = new RegistrySecurity();
        sec.SetOwner(System);
        sec.SetAccessRuleProtection(true, false);
        sec.AddAccessRule(new RegistryAccessRule(System, RegistryRights.FullControl, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new RegistryAccessRule(Admins, RegistryRights.ReadKey, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new RegistryAccessRule(AuthUsers, RegistryRights.ReadKey, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        key.SetAccessControl(sec);
        return true;
    }

    private static bool IsLocked(RegistrySecurity sec)
    {
        if (sec.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner != System) return false;
        foreach (RegistryAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (r.AccessControlType == AccessControlType.Allow && r.IdentityReference is SecurityIdentifier sid && sid != System && (r.RegistryRights & WriteRights) != 0)
                return false;
        return true;
    }

    private static string StartName(int? start) => start switch
    {
        0 => "Boot", 1 => "System", 2 => "Automatic", 3 => "Manual", 4 => "Disabled", null => "(missing)", _ => start.ToString()!,
    };

    /// <summary>Data folder: SYSTEM only (it holds the device secret, queue key and uninstall-key hash). Strips explicit
    /// ACEs left on files by older versions (e.g. Administrators: Modify on guard-events.jsonl).</summary>
    public static void LockDataDir(string dataDir)
    {
        var di = new DirectoryInfo(dataDir);
        if (!di.Exists) return;
        var sec = new DirectorySecurity();
        sec.SetOwner(System);
        sec.SetAccessRuleProtection(true, false);
        sec.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        di.SetAccessControl(sec);
        foreach (var f in di.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            try
            {
                if (f is FileInfo fi)
                {
                    var fs = fi.GetAccessControl();
                    if (fs.GetAccessRules(true, false, typeof(SecurityIdentifier)).Count == 0 && !fs.AreAccessRulesProtected) continue;
                    var clean = new FileSecurity(); clean.SetAccessRuleProtection(false, false); fi.SetAccessControl(clean);
                }
                else if (f is DirectoryInfo d)
                {
                    var ds = d.GetAccessControl();
                    if (ds.GetAccessRules(true, false, typeof(SecurityIdentifier)).Count == 0 && !ds.AreAccessRulesProtected) continue;
                    var clean = new DirectorySecurity(); clean.SetAccessRuleProtection(false, false); d.SetAccessControl(clean);
                }
            }
            catch { /* best effort per entry */ }
        }
    }
}
