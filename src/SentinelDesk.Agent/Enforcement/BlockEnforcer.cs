using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using SentinelDesk.Agent.Core;
using SentinelDesk.Agent.Pipeline;
using SentinelDesk.Shared;

namespace SentinelDesk.Agent.Enforcement;

/// <summary>
/// Enforces the policy's blocked applications and websites.
///  - Applications: Image File Execution Options "Debugger" redirect (Windows refuses to start the exe and runs the tray's
///    "blocked" notice instead) plus a process watcher that ends blocked processes, including wildcard rules.
///  - Websites: the browsers' own enterprise URL block lists (Chrome, Edge, Brave, Firefox; supports paths and shows the
///    browser's "blocked by your organisation" page) plus a managed section of the hosts file for every other app.
/// Everything is re-applied every minute; changes made on the device are reverted and reported as tamper events.
/// </summary>
public sealed partial class BlockEnforcer(PolicyStore policy, TelemetryAggregator agg, AgentState state, ILogger<BlockEnforcer> log) : BackgroundService
{
    private const string IfeoPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string Marker = "SentinelDeskBlock";
    private const string HostsBegin = "# BEGIN SentinelDesk block list - managed by your organisation, do not edit";
    private const string HostsEnd = "# END SentinelDesk block list";

    /// <summary>Chromium-based browsers read the same URLBlocklist policy format.</summary>
    private static readonly string[] ChromiumKeys =
    [
        @"SOFTWARE\Policies\Google\Chrome\URLBlocklist",
        @"SOFTWARE\Policies\Microsoft\Edge\URLBlocklist",
        @"SOFTWARE\Policies\BraveSoftware\Brave\URLBlocklist",
    ];
    private const string FirefoxKey = @"SOFTWARE\Policies\Mozilla\Firefox\WebsiteFilter\Block";

    private static string StateFile => Path.Combine(AgentPaths.DataDir, "block-state.json");
    private static string HostsFile => Path.Combine(Environment.SystemDirectory, @"drivers\etc\hosts");

    private volatile IReadOnlyList<string> _apps = [];
    private volatile IReadOnlyList<UrlRule> _urls = [];
    private readonly ConcurrentDictionary<string, DateTime> _reported = new();
    private readonly SemaphoreSlim _applyLock = new(1);
    private int _appliedVersion = -1;

    /// <summary>Raised to show the user a tray notification (title, body).</summary>
    public event Action<string, string>? Notify;

    public IReadOnlyList<UrlRule> UrlRules => _urls;

    private sealed class BlockState
    {
        public List<string> Ifeo { get; set; } = [];
        public Dictionary<string, List<string>> Browser { get; set; } = [];
        /// <summary>Browser secure-DNS settings this agent changed: "registry path|value name" → the value before (null = none).</summary>
        public Dictionary<string, string?> DnsPrevious { get; set; } = [];
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        policy.Changed += _ => { Task.Run(() => ApplyAsync(fromPolicyChange: true)); };
        await ApplyAsync(fromPolicyChange: true);
        var tick = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { break; }
            try { SweepProcesses(); } catch (Exception ex) { log.LogDebug(ex, "Process sweep failed"); }
            if (++tick % 20 == 0) await ApplyAsync(fromPolicyChange: false);   // every minute: verify and repair
        }
    }

    // ───────────── apply ─────────────

    private async Task ApplyAsync(bool fromPolicyChange)
    {
        await _applyLock.WaitAsync();
        try
        {
            var p = policy.Current;
            var apps = BlockRules.AppRules(p.Block);
            var urls = BlockRules.UrlRules(p.Block);
            _apps = apps;
            _urls = urls;
            var st = LoadState();

            var changed = new List<string>();
            if (ApplyIfeo(apps.Where(a => !a.Contains('*')).ToList(), st)) changed.Add("application block list");
            if (ApplyBrowserPolicies(urls, st)) changed.Add("browser block list");
            // a browser's own secure DNS (DoH) skips the hosts file: turn it off while any website is blocked
            if (ApplySecureDns(off: urls.Count > 0, st)) changed.Add("browser secure DNS");
            // open browsers only re-read their policies when told to: send the same signal as "gpupdate"
            if (changed.Contains("browser block list") || changed.Contains("browser secure DNS"))
            {
                try { if (!RefreshPolicyEx(true, 1)) log.LogDebug("Policy refresh signal failed ({Error})", Marshal.GetLastWin32Error()); }
                catch (Exception ex) { log.LogDebug(ex, "Policy refresh signal failed"); }
            }
            if (ApplyHosts(urls.Where(u => u.HostOnly).Select(u => u.Host).ToList())) changed.Add("hosts file");
            SaveState(st);
            SweepProcesses();

            // the same policy version was already applied, so a difference now means someone changed it on the device
            var tampered = !fromPolicyChange && _appliedVersion == p.Version && changed.Count > 0;
            if (tampered)
            {
                log.LogWarning("Block settings were changed on the device and restored: {What}", string.Join(", ", changed));
                agg.AddEvent(new AgentEvent(DateTime.UtcNow, "tamper.blockList", "critical",
                    $"Blocking settings were changed on the device and restored ({string.Join(", ", changed)})"));
            }
            else if (changed.Count > 0 || _appliedVersion != p.Version)
            {
                log.LogInformation("Block policy applied: {Apps} applications, {Urls} website rules", apps.Count, urls.Count);
                if (_appliedVersion != -1 && changed.Count > 0)
                    agg.AddEvent(new AgentEvent(DateTime.UtcNow, "block.policyApplied", "info",
                        $"Blocking updated: {apps.Count} applications, {urls.Count} website rules"));
            }
            _appliedVersion = p.Version;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            log.LogDebug("No permission to apply block settings (not running as SYSTEM)");
        }
        catch (Exception ex) { log.LogWarning(ex, "Failed to apply block settings"); }
        finally { _applyLock.Release(); }
    }

    // ───────────── applications ─────────────

    private static string DebuggerCommand => $"\"{AgentPaths.TrayExe}\" --blocked";

    /// <summary>Writes the IFEO redirect for exact program names (both registry views). Returns true when something changed.</summary>
    private static bool ApplyIfeo(List<string> exact, BlockState st)
    {
        var changed = false;
        var desired = exact.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var root = hklm.CreateSubKey(IfeoPath, writable: true);
            // remove ours that are no longer blocked
            foreach (var name in root.GetSubKeyNames())
            {
                if (desired.Contains(name)) continue;
                using var k = root.OpenSubKey(name, writable: true);
                if (k?.GetValue(Marker) is null) continue;
                k.DeleteValue("Debugger", false);
                k.DeleteValue(Marker, false);
                var empty = k.ValueCount == 0 && k.SubKeyCount == 0;
                k.Dispose();
                if (empty) root.DeleteSubKey(name, false);
                changed = true;
            }
            foreach (var name in desired)
            {
                using var k = root.CreateSubKey(name, writable: true);
                if (k.GetValue("Debugger") as string != DebuggerCommand) { k.SetValue("Debugger", DebuggerCommand); changed = true; }
                if (k.GetValue(Marker) is null) { k.SetValue(Marker, 1, RegistryValueKind.DWord); changed = true; }
            }
        }
        st.Ifeo = [.. desired.Order(StringComparer.OrdinalIgnoreCase)];
        return changed;
    }

    /// <summary>Ends running processes that match a blocked application.</summary>
    private void SweepProcesses()
    {
        var apps = _apps;
        if (apps.Count == 0) return;
        foreach (var proc in Process.GetProcesses())
        {
            using (proc)
            {
                if (proc.Id <= 4) continue;
                var exe = proc.ProcessName + ".exe";
                if (Match(apps, exe) is { } rule) KillBlocked(proc, exe, rule);
            }
        }
    }

    /// <summary>Called by the kernel ETW collector for every new process, so blocked apps end within milliseconds.</summary>
    public void OnProcessStart(int pid, string? imageName)
    {
        var apps = _apps;
        if (apps.Count == 0 || string.IsNullOrEmpty(imageName)) return;
        var exe = Path.GetFileName(imageName);
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
        if (Match(apps, exe) is not { } rule) return;
        try { using var proc = Process.GetProcessById(pid); KillBlocked(proc, exe, rule); }
        catch (ArgumentException) { }   // already gone
    }

    private static string? Match(IReadOnlyList<string> apps, string exe)
    {
        foreach (var a in apps) if (BlockRules.WildcardMatch(exe, a)) return a;
        return null;
    }

    private void KillBlocked(Process proc, string exe, string rule)
    {
        try { proc.Kill(entireProcessTree: true); }
        catch (Exception ex) { log.LogDebug(ex, "Could not end blocked process {Exe}", exe); return; }
        ReportBlockedApp(exe, state.CurrentUser, rule == exe.ToLowerInvariant() ? null : rule);
    }

    /// <summary>Records a blocked launch (from the process watcher or from the tray's IFEO notice). Rate-limited per app.</summary>
    public void ReportBlockedApp(string exe, string? user, string? rule = null)
    {
        exe = Path.GetFileName(exe.Trim().Trim('"'));
        if (exe.Length == 0 || exe.Length > 200) return;
        var now = DateTime.UtcNow;
        var key = "app:" + exe.ToLowerInvariant();
        if (_reported.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(2)) return;
        _reported[key] = now;
        if (_reported.Count > 5000) _reported.Clear();
        log.LogInformation("Blocked application {Exe} for {User}", exe, user);
        agg.AddEvent(new AgentEvent(now, "block.app", "warning", $"Blocked application {exe}{(user is null ? "" : $" for {user}")}",
            new() { ["app"] = exe, ["user"] = user ?? "", ["rule"] = rule ?? exe }));
        if (policy.Current.Block.NotifyUser) Notify?.Invoke("Application blocked", $"{exe}: {policy.Current.Block.Message}");
    }

    // ───────────── websites ─────────────

    /// <summary>Called by the DNS collector: records attempts to reach a blocked site (rate-limited per domain).</summary>
    public void OnDnsLookup(string host, string? process)
    {
        var urls = _urls;
        if (urls.Count == 0 || !BlockRules.HostBlocked(urls, host)) return;
        var now = DateTime.UtcNow;
        var key = "web:" + Domains.Registrable(host);
        if (_reported.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(10)) return;
        _reported[key] = now;
        agg.AddEvent(new AgentEvent(now, "block.web", "warning", $"Blocked website {host}{(state.CurrentUser is { } u ? $" for {u}" : "")}",
            new() { ["domain"] = host, ["process"] = process ?? "", ["user"] = state.CurrentUser ?? "" }));
    }

    /// <summary>Rule formats: Chromium "example.com" (domain + subdomains) or "example.com/path"; Firefox match patterns.</summary>
    private static IEnumerable<string> ChromiumEntries(UrlRule r) => [r.Host + r.Path];

    private static IEnumerable<string> FirefoxEntries(UrlRule r) => r.HostOnly
        ? [$"*://{r.Host}/*", $"*://*.{r.Host}/*"]
        : [$"*://{r.Host}{r.Path}*", $"*://*.{r.Host}{r.Path}*"];

    private static readonly (string Path, string Name, object Off)[] SecureDnsValues =
    [
        (@"SOFTWARE\Policies\Google\Chrome", "DnsOverHttpsMode", "off"),
        (@"SOFTWARE\Policies\Microsoft\Edge", "DnsOverHttpsMode", "off"),
        (@"SOFTWARE\Policies\BraveSoftware\Brave", "DnsOverHttpsMode", "off"),
        (@"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS", "Enabled", 0),
        (@"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS", "Locked", 1),
    ];

    /// <summary>
    /// Turns the browsers' built-in secure DNS off (off = true) or restores what was there before (off = false).
    /// The previous values are remembered, so removing the blocks or uninstalling puts the browsers back as they were.
    /// </summary>
    private static bool ApplySecureDns(bool off, BlockState st)
    {
        var changed = false;
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        foreach (var (path, name, offValue) in SecureDnsValues)
        {
            var id = $"{path}|{name}";
            if (off)
            {
                using var key = hklm.CreateSubKey(path, writable: true);
                var current = key.GetValue(name);
                if (!st.DnsPrevious.ContainsKey(id)) st.DnsPrevious[id] = current?.ToString();
                if (current?.ToString() == offValue.ToString()) continue;
                if (offValue is int i) key.SetValue(name, i, RegistryValueKind.DWord); else key.SetValue(name, (string)offValue, RegistryValueKind.String);
                changed = true;
            }
            else if (st.DnsPrevious.Remove(id, out var previous))
            {
                using var key = hklm.OpenSubKey(path, writable: true);
                if (key is null) continue;
                if (previous is null) key.DeleteValue(name, false);
                else if (offValue is int) key.SetValue(name, int.TryParse(previous, out var n) ? n : 0, RegistryValueKind.DWord);
                else key.SetValue(name, previous, RegistryValueKind.String);
                key.Dispose();
                DeleteIfEmpty(hklm, path);
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// Deletes a browser policy folder the agent created (e.g. SOFTWARE\Policies\Google\Chrome) when it is empty, then its
    /// empty parents, so nothing is left behind after unblocking. Stops at the first folder that still holds anything and
    /// never touches SOFTWARE\Policies itself.
    /// </summary>
    private static void DeleteIfEmpty(RegistryKey hklm, string path)
    {
        const string Root = @"SOFTWARE\Policies";
        for (var p = path; p.Length > Root.Length && p.StartsWith(Root + @"\", StringComparison.OrdinalIgnoreCase); p = Path.GetDirectoryName(p)!)
        {
            using (var k = hklm.OpenSubKey(p))
            {
                if (k is null) continue;
                if (k.ValueCount > 0 || k.SubKeyCount > 0) return;
            }
            hklm.DeleteSubKey(p, false);
        }
    }

    private static bool ApplyBrowserPolicies(List<UrlRule> urls, BlockState st)
    {
        var changed = false;
        foreach (var key in ChromiumKeys) changed |= ApplyList(key, urls.SelectMany(ChromiumEntries).ToList(), st);
        changed |= ApplyList(FirefoxKey, urls.SelectMany(FirefoxEntries).ToList(), st);
        return changed;
    }

    /// <summary>
    /// Policy lists are numbered values ("1", "2", ...). Entries written by other tools are kept; only the entries this
    /// agent wrote before are replaced, then the list is renumbered.
    /// </summary>
    private static bool ApplyList(string path, List<string> ours, BlockState st)
    {
        var previous = st.Browser.GetValueOrDefault(path) ?? [];
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var existing = hklm.OpenSubKey(path);
        if (existing is null && ours.Count == 0) { st.Browser.Remove(path); return false; }
        var current = existing?.GetValueNames()
            .Select(n => (Name: n, Num: int.TryParse(n, out var i) ? i : int.MaxValue, Value: existing.GetValue(n) as string))
            .Where(x => x.Value is not null).OrderBy(x => x.Num).Select(x => x.Value!).ToList() ?? [];
        existing?.Dispose();

        var others = current.Where(v => !previous.Contains(v) && !ours.Contains(v)).ToList();
        var desired = others.Concat(ours).ToList();
        st.Browser[path] = ours;
        if (current.SequenceEqual(desired)) return false;

        if (desired.Count == 0)
        {
            hklm.DeleteSubKeyTree(path, false);
            DeleteIfEmpty(hklm, Path.GetDirectoryName(path)!);
            return true;
        }
        using var key = hklm.CreateSubKey(path, writable: true);
        foreach (var n in key.GetValueNames()) key.DeleteValue(n, false);
        for (var i = 0; i < desired.Count; i++) key.SetValue((i + 1).ToString(), desired[i], RegistryValueKind.String);
        return true;
    }

    /// <summary>Maintains a marked section of the hosts file that points blocked domains at 0.0.0.0.</summary>
    private bool ApplyHosts(List<string> hosts)
    {
        string text;
        try { text = File.Exists(HostsFile) ? File.ReadAllText(HostsFile) : ""; }
        catch (IOException ex) { log.LogDebug(ex, "hosts file busy"); return false; }

        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var start = lines.FindIndex(l => l.TrimEnd() == HostsBegin);
        var end = start >= 0 ? lines.FindIndex(start, l => l.TrimEnd() == HostsEnd) : -1;
        if (start < 0 && hosts.Count == 0) return false;   // nothing to block and no section of ours: leave the file alone
        if (start >= 0 && end < 0) end = lines.Count - 1;   // damaged section: replace to the end
        var outside = start >= 0 ? lines.Take(start).Concat(lines.Skip(end + 1)).ToList() : lines;
        while (outside.Count > 0 && outside[^1].Trim().Length == 0) outside.RemoveAt(outside.Count - 1);

        var section = new List<string>();
        if (hosts.Count > 0)
        {
            section.Add(HostsBegin);
            foreach (var h in hosts)
            {
                section.Add($"0.0.0.0 {h}");
                if (!h.StartsWith("www.")) section.Add($"0.0.0.0 www.{h}");
            }
            section.Add(HostsEnd);
        }
        // compare only our section, so unrelated edits elsewhere in the file are never rewritten or reported
        var current = start >= 0 ? lines.Skip(start).Take(end - start + 1).Select(l => l.TrimEnd()).ToList() : [];
        if (current.SequenceEqual(section)) return false;
        var result = string.Join("\r\n", section.Count > 0 ? outside.Append("").Concat(section) : outside) + "\r\n";

        var attrs = File.Exists(HostsFile) ? File.GetAttributes(HostsFile) : FileAttributes.Normal;
        if (attrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(HostsFile, attrs & ~FileAttributes.ReadOnly);
        var tmp = HostsFile + ".sentineldesk.tmp";
        File.WriteAllText(tmp, result, new UTF8Encoding(false));
        File.Move(tmp, HostsFile, overwrite: true);
        try { DnsFlushResolverCache(); } catch { }
        return true;
    }

    // ───────────── state ─────────────

    private BlockState LoadState()
    {
        try { if (File.Exists(StateFile)) return Json.Deserialize<BlockState>(File.ReadAllText(StateFile)) ?? new(); }
        catch (Exception ex) { log.LogDebug(ex, "Could not read block state"); }
        return new();
    }

    private void SaveState(BlockState st)
    {
        try { File.WriteAllText(StateFile, Json.Serialize(st)); }
        catch (Exception ex) { log.LogDebug(ex, "Could not save block state"); }
    }

    /// <summary>Tells Windows and running browsers (Chrome, Edge, Brave watch for it) that machine policy changed.</summary>
    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RefreshPolicyEx([MarshalAs(UnmanagedType.Bool)] bool machine, uint options);

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DnsFlushResolverCache();
}
