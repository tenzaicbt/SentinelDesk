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
public sealed partial class BlockEnforcer(PolicyStore policy, TelemetryAggregator agg, AgentState state, AgentConfig config, ILogger<BlockEnforcer> log) : BackgroundService
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

    private volatile ActiveBlocks _active = new();
    private string? _appliedSignature;
    private readonly ConcurrentDictionary<string, DateTime> _reported = new();
    private readonly ConcurrentQueue<DateTime> _unlockAttempts = new();
    private readonly SemaphoreSlim _applyLock = new(1);
    private int _appliedVersion = -1;
    private (string? User, IReadOnlyCollection<string> Names) _userCache = (null, []);

    /// <summary>Raised to show the user a tray notification (title, body).</summary>
    public event Action<string, string>? Notify;

    /// <summary>True while any website rule (block, warn or log) needs the DNS collector to report lookups.</summary>
    public bool WatchesDns => _active.BlockUrls.Count + _active.WarnUrls.Count + _active.LogUrls.Count > 0;

    private sealed class BlockState
    {
        public List<string> Ifeo { get; set; } = [];
        public Dictionary<string, List<string>> Browser { get; set; } = [];
        /// <summary>Browser secure-DNS settings this agent changed: "registry path|value name" → the value before (null = none).</summary>
        public Dictionary<string, string?> DnsPrevious { get; set; } = [];
        /// <summary>Offline unlock code in effect until (server time).</summary>
        public DateTime? UnlockedUntilUtc { get; set; }
        /// <summary>Hashes of unlock codes already used (one-time).</summary>
        public List<string> UsedCodes { get; set; } = [];
        /// <summary>USB storage devices this agent disabled (device instance ids), re-enabled on unblock.</summary>
        public List<string> UsbDisabled { get; set; } = [];
        /// <summary>USB storage driver start values before the agent changed them.</summary>
        public Dictionary<string, int> UsbStartPrevious { get; set; } = [];
        public bool UsbDenyWrite { get; set; }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        policy.Changed += _ => { Task.Run(() => ApplyAsync(fromPolicyChange: true)); };
        await ApplyAsync(fromPolicyChange: true);
        var tick = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { break; }
            tick++;
            try
            {
                // schedules, expiry, the signed-in user and unlock codes change what applies without a new policy
                if (Evaluate(LoadState()).Signature != _appliedSignature) await ApplyAsync(fromPolicyChange: true);
                else if (tick % 20 == 0) await ApplyAsync(fromPolicyChange: false);   // every minute: verify and repair
                SweepProcesses();
                if (tick % 2 == 0 && _active.UsbMode == "block") SweepUsb();
            }
            catch (Exception ex) { log.LogDebug(ex, "Enforcement tick failed"); }
        }
    }

    // ───────────── what applies right now ─────────────

    private DateTime ServerNowUtc => DateTime.UtcNow + state.ServerClockOffset;

    private ActiveBlocks Evaluate(BlockState st)
    {
        var p = policy.Current;
        var now = ServerNowUtc;
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(p.WorkingHours.TimeZone); } catch { tz = TimeZoneInfo.Local; }
        return BlockEvaluator.Evaluate(p.Block, p.Collectors.UsbBlock, now, TimeZoneInfo.ConvertTimeFromUtc(now, tz), CurrentUserNames(), st.UnlockedUntilUtc);
    }

    /// <summary>The signed-in user and their groups, for policies limited to users or groups.</summary>
    private IReadOnlyCollection<string> CurrentUserNames()
    {
        var user = state.CurrentUser;
        if (string.IsNullOrEmpty(user)) return [];
        if (_userCache.User == user) return _userCache.Names;
        var names = new List<string> { user };
        try { names.AddRange(Native.NativeMethods.SessionGroupNames(Native.NativeMethods.WTSGetActiveConsoleSessionId())); }
        catch (Exception ex) { log.LogDebug(ex, "Could not read the user's groups"); }
        _userCache = (user, names.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        return _userCache.Names;
    }

    // ───────────── apply ─────────────

    private async Task ApplyAsync(bool fromPolicyChange)
    {
        await _applyLock.WaitAsync();
        try
        {
            var p = policy.Current;
            var st = LoadState();
            var a = Evaluate(st);
            _active = a;
            var urls = a.BlockUrls;

            var changed = new List<string>();
            if (ApplyIfeo(a.BlockApps.Where(x => !x.Contains('*') && !a.AllowApps.Contains(x)).ToList(), st)) changed.Add("application block list");
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
            if (ApplyUsb(a, st)) changed.Add("USB storage");
            SaveState(st);
            SweepProcesses();

            // the same policy and the same active rules were already applied: a difference means someone changed the device
            var tampered = !fromPolicyChange && _appliedVersion == p.Version && _appliedSignature == a.Signature && changed.Count > 0;
            if (tampered)
            {
                log.LogWarning("Block settings were changed on the device and restored: {What}", string.Join(", ", changed));
                agg.AddEvent(new AgentEvent(DateTime.UtcNow, "tamper.blockList", "critical",
                    $"Blocking settings were changed on the device and restored ({string.Join(", ", changed)})"));
            }
            else if (changed.Count > 0 || _appliedVersion != p.Version)
            {
                var summary = $"{a.BlockApps.Count} applications and {a.BlockUrls.Count} website rules blocked" +
                              (a.WarnApps.Count + a.WarnUrls.Count > 0 ? $", {a.WarnApps.Count + a.WarnUrls.Count} warned" : "") +
                              (a.LogApps.Count + a.LogUrls.Count > 0 ? $", {a.LogApps.Count + a.LogUrls.Count} logged" : "") +
                              (a.UsbMode != "none" ? $", USB storage {(a.UsbMode == "readOnly" ? "read-only" : "blocked")}" : "");
                log.LogInformation("Blocking applied: {Summary}", summary);
                if (_appliedVersion != -1 && changed.Count > 0)
                    agg.AddEvent(new AgentEvent(DateTime.UtcNow, "block.policyApplied", "info", $"Blocking updated: {summary}"));
            }
            _appliedVersion = p.Version;
            _appliedSignature = a.Signature;
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

    /// <summary>Ends running processes that match a blocked application; reports warned / logged ones.</summary>
    private void SweepProcesses()
    {
        var a = _active;
        if (a.BlockApps.Count + a.WarnApps.Count + a.LogApps.Count == 0) return;
        foreach (var proc in Process.GetProcesses())
        {
            using (proc)
            {
                if (proc.Id <= 4) continue;
                Check(proc, proc.ProcessName + ".exe", a);
            }
        }
    }

    /// <summary>Called by the kernel ETW collector for every new process, so blocked apps end within milliseconds.</summary>
    public void OnProcessStart(int pid, string? imageName)
    {
        var a = _active;
        if (a.BlockApps.Count + a.WarnApps.Count + a.LogApps.Count == 0 || string.IsNullOrEmpty(imageName)) return;
        var exe = Path.GetFileName(imageName);
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
        try { using var proc = Process.GetProcessById(pid); Check(proc, exe, a); }
        catch (ArgumentException) { }   // already gone
    }

    private void Check(Process proc, string exe, ActiveBlocks a)
    {
        if (a.AllowApps.Any(x => string.Equals(x, exe, StringComparison.OrdinalIgnoreCase))) return;
        if (Match(a.BlockApps, exe) is { } rule)
        {
            try { proc.Kill(entireProcessTree: true); }
            catch (Exception ex) { log.LogDebug(ex, "Could not end blocked process {Exe}", exe); return; }
            ReportBlockedApp(exe, state.CurrentUser, rule == exe.ToLowerInvariant() ? null : rule);
        }
        else if (Match(a.WarnApps, exe) is not null) Report("block.warned", "app:" + exe, TimeSpan.FromMinutes(30),
            $"Warned about application {exe}", exe, notify: ("Not allowed by your organisation", $"{exe} is not approved for work use. This is recorded."));
        else if (Match(a.LogApps, exe) is not null) Report("block.logged", "app:" + exe, TimeSpan.FromMinutes(30), $"Application {exe} used (logged)", exe);
    }

    private static string? Match(IReadOnlyList<string> apps, string exe)
    {
        foreach (var a in apps) if (BlockRules.WildcardMatch(exe, a)) return a;
        return null;
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
        if (_active.NotifyUser) Notify?.Invoke("Application blocked", $"{exe}: {_active.Message}");
    }

    /// <summary>True when a program the tray reports (IFEO notice) is blocked right now.</summary>
    public bool IsAppBlocked(string exe) =>
        Match(_active.BlockApps, Path.GetFileName(exe)) is not null && !_active.AllowApps.Contains(Path.GetFileName(exe).ToLowerInvariant());

    private void Report(string type, string key, TimeSpan every, string message, string value, (string Title, string Body)? notify = null)
    {
        var now = DateTime.UtcNow;
        if (_reported.TryGetValue(type + key, out var last) && now - last < every) return;
        _reported[type + key] = now;
        agg.AddEvent(new AgentEvent(now, type, "info", $"{message}{(state.CurrentUser is { } u ? $" for {u}" : "")}",
            new() { ["value"] = value, ["user"] = state.CurrentUser ?? "" }));
        if (notify is { } n) Notify?.Invoke(n.Title, n.Body);
    }

    // ───────────── websites ─────────────

    /// <summary>Called by the DNS collector: records attempts to reach a blocked, warned or logged site (rate-limited per domain).</summary>
    public void OnDnsLookup(string host, string? process)
    {
        var a = _active;
        if (BlockRules.HostBlocked(a.BlockUrls, host))
        {
            var now = DateTime.UtcNow;
            var key = "web:" + Domains.Registrable(host);
            if (_reported.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(10)) return;
            _reported[key] = now;
            agg.AddEvent(new AgentEvent(now, "block.web", "warning", $"Blocked website {host}{(state.CurrentUser is { } u ? $" for {u}" : "")}",
                new() { ["domain"] = host, ["process"] = process ?? "", ["user"] = state.CurrentUser ?? "" }));
        }
        else if (BlockRules.HostBlocked(a.WarnUrls, host))
            Report("block.warned", "web:" + Domains.Registrable(host), TimeSpan.FromMinutes(30), $"Warned about website {host}", host,
                notify: ("Not allowed by your organisation", $"{Domains.Registrable(host)} is not approved for work use. This visit is recorded."));
        else if (BlockRules.HostBlocked(a.LogUrls, host))
            Report("block.logged", "web:" + Domains.Registrable(host), TimeSpan.FromMinutes(30), $"Website {host} visited (logged)", host);
    }

    // ───────────── offline unlock code ─────────────

    /// <summary>Checks an unlock code typed in the tray. A valid code pauses all blocking for 1, 4 or 8 hours, once.</summary>
    public async Task<(bool Ok, string Text)> TryUnlockAsync(string code, string? user)
    {
        var now = DateTime.UtcNow;
        while (_unlockAttempts.TryPeek(out var t) && now - t > TimeSpan.FromMinutes(10)) _unlockAttempts.TryDequeue(out _);
        if (_unlockAttempts.Count >= 5) return (false, "Too many attempts. Wait 10 minutes and try again.");
        _unlockAttempts.Enqueue(now);
        if (config.GetSecret() is not { } secret || config.DeviceId == Guid.Empty) return (false, "This computer is not enrolled.");
        var hours = UnlockCodes.Check(config.DeviceId, secret, code, ServerNowUtc);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(UnlockCodes.Normalize(code))));
        var st = LoadState();
        if (hours is null)
        {
            agg.AddEvent(new AgentEvent(now, "tamper.unlockCodeWrong", "warning", $"Wrong unlock code entered by {user ?? "a user"}"));
            return (false, "That code is not valid. Check it, or ask for a new one (codes are valid for about an hour).");
        }
        if (st.UsedCodes.Contains(hash)) return (false, "This code was already used. Ask for a new one.");
        st.UsedCodes = [.. st.UsedCodes.TakeLast(50), hash];
        st.UnlockedUntilUtc = ServerNowUtc.AddHours(hours.Value);
        SaveState(st);
        agg.AddEvent(new AgentEvent(now, "block.unlocked", "warning", $"Blocking paused for {hours} h with an unlock code by {user ?? "a user"}"));
        await ApplyAsync(fromPolicyChange: true);
        return (true, $"Blocking is paused on this computer until {st.UnlockedUntilUtc.Value.ToLocalTime():HH:mm}.");
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

    // ───────────── USB storage ─────────────

    private static readonly string[] UsbDrivers = ["USBSTOR", "UASPStor"];
    private const string RemovableDisksDenyWrite = @"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}";

    /// <summary>
    /// block: storage drivers off (when no drive is allowed) and plugged-in drives switched off · readOnly: Windows
    /// "deny write" for removable disks · none: everything put back as it was. Allowed drives (serial) are never touched.
    /// </summary>
    private bool ApplyUsb(ActiveBlocks a, BlockState st)
    {
        var changed = false;
        var block = a.UsbMode == "block";
        // drivers: off only when nothing is allowed (an allowed drive needs the driver; others are switched off one by one)
        var driversOff = block && a.UsbAllowSerials.Count == 0;
        foreach (var drv in UsbDrivers)
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{drv}", writable: true);
            if (key is null) continue;
            var current = key.GetValue("Start") as int? ?? 3;
            if (driversOff && current != 4)
            {
                if (!st.UsbStartPrevious.ContainsKey(drv)) st.UsbStartPrevious[drv] = current;
                key.SetValue("Start", 4, RegistryValueKind.DWord); changed = true;
            }
            else if (!driversOff && st.UsbStartPrevious.Remove(drv, out var previous))
            {
                key.SetValue("Start", previous == 4 ? 3 : previous, RegistryValueKind.DWord); changed = true;
            }
        }
        // read-only
        var denyWrite = a.UsbMode == "readOnly";
        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        {
            if (denyWrite)
            {
                using var k = hklm.CreateSubKey(RemovableDisksDenyWrite, writable: true);
                if (k.GetValue("Deny_Write") as int? != 1) { k.SetValue("Deny_Write", 1, RegistryValueKind.DWord); changed = true; }
                st.UsbDenyWrite = true;
            }
            else if (st.UsbDenyWrite)
            {
                using (var k = hklm.OpenSubKey(RemovableDisksDenyWrite, writable: true)) k?.DeleteValue("Deny_Write", false);
                DeleteIfEmpty(hklm, RemovableDisksDenyWrite);
                st.UsbDenyWrite = false; changed = true;
            }
        }
        // drives that are already plugged in
        if (block) changed |= DisableUsbDrives(a, st);
        else if (st.UsbDisabled.Count > 0)
        {
            foreach (var id in st.UsbDisabled.ToList())
                if (PnpUtil("/enable-device", id)) st.UsbDisabled.Remove(id);
            PnpUtil("/scan-devices", null);   // drives mount again without unplugging
            changed = true;
        }
        return changed;
    }

    private void SweepUsb()
    {
        var a = _active;
        if (a.UsbMode != "block") return;
        var st = LoadState();
        if (DisableUsbDrives(a, st)) SaveState(st);
    }

    /// <summary>Switches off plugged-in USB storage that is not on the allow list. Returns true when something changed.</summary>
    private bool DisableUsbDrives(ActiveBlocks a, BlockState st)
    {
        var changed = false;
        foreach (var (id, serial, model) in UsbDisks())
        {
            if (st.UsbDisabled.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;
            if (a.UsbAllowSerials.Any(s => serial.Contains(s, StringComparison.OrdinalIgnoreCase) || id.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
            if (!PnpUtil("/disable-device", id)) continue;
            st.UsbDisabled.Add(id);
            changed = true;
            agg.AddEvent(new AgentEvent(DateTime.UtcNow, "usb.blocked", "warning", $"Blocked USB drive {model} ({serial}){(state.CurrentUser is { } u ? $" for {u}" : "")}",
                new() { ["serial"] = serial, ["model"] = model, ["instanceId"] = id }));
            Notify?.Invoke("USB drive blocked", $"{model} cannot be used on this computer.");
        }
        return changed;
    }

    /// <summary>Removable USB disks (incl. USB 3 UASP drives and card readers), never internal disks.</summary>
    private static List<(string Id, string Serial, string Model)> UsbDisks()
    {
        var list = new List<(string, string, string)>();
        using var s = new System.Management.ManagementObjectSearcher("SELECT PNPDeviceID, SerialNumber, Model, InterfaceType, MediaType FROM Win32_DiskDrive");
        foreach (var o in s.Get())
        {
            var id = o["PNPDeviceID"]?.ToString() ?? "";
            var iface = o["InterfaceType"]?.ToString() ?? "";
            var media = o["MediaType"]?.ToString() ?? "";
            var usb = iface.Equals("USB", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"USBSTOR\", StringComparison.OrdinalIgnoreCase)
                      || media.StartsWith("External", StringComparison.OrdinalIgnoreCase) || media.StartsWith("Removable", StringComparison.OrdinalIgnoreCase);
            if (!usb || id.Length == 0) continue;
            var serial = (o["SerialNumber"]?.ToString() ?? "").Trim();
            if (serial.Length == 0 && id.Contains('\\')) serial = id[(id.LastIndexOf('\\') + 1)..].Split('&')[0];
            list.Add((id, serial.ToUpperInvariant(), o["Model"]?.ToString() ?? "USB drive"));
        }
        return list;
    }

    /// <summary>Runs pnputil (Windows 10 2004+) to switch a device on or off.</summary>
    private bool PnpUtil(string verb, string? instanceId)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add(verb);
            if (instanceId is not null) psi.ArgumentList.Add(instanceId);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(30_000)) { try { p.Kill(); } catch { } return false; }
            if (p.ExitCode != 0) log.LogDebug("pnputil {Verb} {Id} failed ({Code}): {Output}", verb, instanceId, p.ExitCode, output.Trim());
            return p.ExitCode == 0;
        }
        catch (Exception ex) { log.LogDebug(ex, "pnputil {Verb} failed", verb); return false; }
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
