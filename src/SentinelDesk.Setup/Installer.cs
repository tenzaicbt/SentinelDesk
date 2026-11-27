using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace SentinelDesk.Setup
{
    public enum SetupMode { Install, Upgrade, AlreadyInstalled, Uninstall, NotInstalled }

    /// <summary>Progress from an operation: a status line, an optional percentage (null = keep), and raw log lines.</summary>
    public interface IProgressSink
    {
        void Status(string text, int? percent = null);
        void Log(string line);
    }

    /// <summary>
    /// Install, upgrade and uninstall of the SentinelDesk agent.
    /// - Install: extracts the embedded agent bundle and runs the hardened install-agent.ps1 (which rolls back a failed install).
    /// - Upgrade: hands the embedded package to the installed Guard service as a pending update. The Guard verifies the
    ///   server signature, backs up the installed files, installs, and rolls back if the agent does not start.
    /// - Uninstall: runs the installed uninstall-agent.ps1 (Uninstall Key or a one-time code from the console).
    /// </summary>
    public static class Installer
    {
        public static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SentinelDesk");
        public static string LogFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", "SentinelDesk-Setup.log");
        private static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        private const string AppKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SentinelDesk";

        public static bool IsInstalled()
        {
            try { using (var sc = new ServiceController("SentinelDeskAgent")) { var _ = sc.Status; return true; } }
            catch (InvalidOperationException) { return false; }
        }

        public static bool HasPackage() => typeof(Installer).Assembly.GetManifestResourceInfo("agent.zip") != null;

        /// <summary>Version of this setup file (and of the agent package inside it).</summary>
        public static string SetupVersion
        {
            get
            {
                var v = typeof(Installer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                        ?? typeof(Installer).Assembly.GetName().Version.ToString(3);
                return Clean(v);
            }
        }

        /// <summary>Version of the agent installed on this computer, or null.</summary>
        public static string InstalledVersion
        {
            get
            {
                try
                {
                    var dll = Path.Combine(InstallDir, "SentinelDesk.Agent.dll");
                    return File.Exists(dll) ? Clean(FileVersionInfo.GetVersionInfo(dll).ProductVersion) : null;
                }
                catch { return null; }
            }
        }

        private static string Clean(string v) => (v ?? "").Split('+')[0].Trim();

        private static Version Parse(string v) => Version.TryParse(Clean(v), out var x) ? x : new Version(0, 0);

        public static SetupMode DetectMode(SetupConfig c)
        {
            if (c.Uninstall) return IsInstalled() ? SetupMode.Uninstall : SetupMode.NotInstalled;
            if (!IsInstalled()) return SetupMode.Install;
            return Parse(SetupVersion) > Parse(InstalledVersion) ? SetupMode.Upgrade : SetupMode.AlreadyInstalled;
        }

        private static void Write(IProgressSink sink, string line)
        {
            sink?.Log(line);
            try { File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}"); } catch { }
        }

        // ───────────────────────────── install ─────────────────────────────

        private static readonly string[] InstallSteps =
        {
            "Removing leftovers", "Copying files", "Enrolling with the management server", "Storing uninstall key hash",
            "Registering event log sources", "Creating services", "Locking down", "Starting services", "Hardening", "Registering the tray",
        };

        /// <summary>Returns 0 on success.</summary>
        public static int Install(SetupConfig c, IProgressSink sink)
        {
            if (!HasPackage()) { Write(sink, "ERROR: this setup file does not contain the agent package (build it with build.ps1)."); return 10; }
            if (IsInstalled()) { Write(sink, "SentinelDesk is already installed on this computer."); return 0; }
            if (string.IsNullOrWhiteSpace(c.ServerUrl) || string.IsNullOrWhiteSpace(c.EnrollToken) || string.IsNullOrWhiteSpace(c.UninstallKey))
            { Write(sink, "ERROR: server address, enrollment token and uninstall key are required."); return 2; }

            var work = PrivateWorkFolder();
            try
            {
                sink?.Status("Extracting the agent package…", 5);
                Write(sink, "Extracting agent package...");
                var bundle = Path.Combine(work, "agent");
                ExtractPackage(Path.Combine(work, "agent.zip"));
                ZipFile.ExtractToDirectory(Path.Combine(work, "agent.zip"), bundle);

                var script = Path.Combine(bundle, "install-agent.ps1");
                if (!File.Exists(script)) { Write(sink, "ERROR: install-agent.ps1 missing from package."); return 11; }

                var args = new StringBuilder($"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" -SourceDir \"{bundle}\"");
                args.Append($" -ServerUrl \"{Escape(c.ServerUrl)}\" -EnrollToken \"{Escape(c.EnrollToken)}\" -UninstallKey \"{Escape(c.UninstallKey)}\"");
                if (!string.IsNullOrWhiteSpace(c.ServerCertSha256)) args.Append($" -ServerCertSha256 \"{Escape(c.ServerCertSha256)}\"");
                if (c.TrustOnFirstUse) args.Append(" -TrustOnFirstUse");

                sink?.Status($"Connecting to {c.ServerUrl}…", 12);
                Write(sink, $"Installing and enrolling with {c.ServerUrl} ...");
                var code = RunPowerShell(args.ToString(), bundle, null, line =>
                {
                    Write(sink, line);
                    var step = line.Trim().StartsWith(">") ? line.Trim().TrimStart('>').Trim() : null;
                    if (step == null) return;
                    var i = Array.FindIndex(InstallSteps, s => step.StartsWith(s, StringComparison.OrdinalIgnoreCase));
                    sink?.Status(step.TrimEnd('.') + "…", i >= 0 ? 15 + i * 8 : (int?)null);
                });
                if (code != 0 || !IsInstalled())
                {
                    Write(sink, $"Installation failed (code {code}). Any partial install was rolled back. See {LogFile}");
                    return Math.Max(1, code);
                }
                sink?.Status("Registering in Apps & features…", 96);
                RegisterApp(SetupVersion);
                Write(sink, "Installation completed.");
                sink?.Status("Installed", 100);
                return 0;
            }
            catch (Exception ex) { Write(sink, "ERROR: " + ex.Message); return 1; }
            finally { Cleanup(work); }
        }

        // ───────────────────────────── upgrade ─────────────────────────────

        /// <summary>Returns 0 when the Guard installed the new version; anything else means the old version is still in place.</summary>
        public static int Upgrade(SetupConfig c, IProgressSink sink)
        {
            if (!HasPackage()) { Write(sink, "ERROR: this setup file does not contain the agent package."); return 10; }
            if (!c.CanUpgrade)
            {
                Write(sink, "ERROR: this setup file is not signed by your management server, so it cannot upgrade an installed agent. " +
                            "Download the office installer from the console (Agent management), or roll the new version out from Releases.");
                return 12;
            }
            var work = PrivateWorkFolder();
            var task = "SentinelDesk-Setup-" + Path.GetFileName(work).Substring(9);
            try
            {
                sink?.Status("Checking the update package…", 8);
                var zip = Path.Combine(work, "agent.zip");
                ExtractPackage(zip);
                string sha;
                using (var fs = File.OpenRead(zip)) using (var h = SHA256.Create()) sha = BitConverter.ToString(h.ComputeHash(fs)).Replace("-", "");
                if (!sha.Equals(c.PackageSha256, StringComparison.OrdinalIgnoreCase) || Clean(c.PackageVersion) != SetupVersion)
                {
                    Write(sink, "ERROR: the agent package does not match the signature in this setup file.");
                    return 13;
                }
                Write(sink, $"Package {SetupVersion} ({sha.Substring(0, 16)}…) matches the server signature.");

                File.WriteAllText(Path.Combine(work, "request.json"),
                    "{\"version\":\"" + SetupVersion + "\",\"sha256\":\"" + sha + "\",\"signature\":\"" + c.PackageSignature.Replace("\"", "") + "\"}", new UTF8Encoding(false));
                var script = Path.Combine(work, "upgrade.ps1");
                File.WriteAllText(script, UpgradeScript, new UTF8Encoding(true));

                sink?.Status("Handing the update to the SentinelDesk Guard service…", 20);
                Write(sink, "Starting the SYSTEM upgrade task...");
                var tr = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Work \"{work}\"";
                Run("schtasks.exe", $"/Create /TN {task} /RU SYSTEM /SC ONCE /ST 23:59 /RL HIGHEST /F /TR \"{tr.Replace("\"", "\\\"")}\"");
                Run("schtasks.exe", $"/Run /TN {task}");

                var result = Path.Combine(work, "result.txt");
                var status = Path.Combine(work, "status.txt");
                var start = DateTime.UtcNow;
                string last = null;
                while (!File.Exists(result) && DateTime.UtcNow - start < TimeSpan.FromMinutes(6))
                {
                    Thread.Sleep(1000);
                    var s = File.Exists(status) ? SafeRead(status) : null;
                    if (s != null && s != last)
                    {
                        last = s;
                        if (s == "handed") { Write(sink, "Update handed to the Guard service; it verifies the signature and backs up the current version."); sink?.Status("Verifying and backing up the installed version…", 40); }
                        else if (s == "installing") { Write(sink, "Guard is installing the new version..."); sink?.Status("Installing the new version…", 65); }
                    }
                    else if (last == "handed")
                    {
                        var p = 40 + (int)Math.Min(20, (DateTime.UtcNow - start).TotalSeconds / 3);
                        sink?.Status(null, p);
                    }
                }
                var verdict = File.Exists(result) ? SafeRead(result) : "timeout|The upgrade did not finish in time.";
                var kind = verdict.Split('|')[0];
                var message = verdict.Contains("|") ? verdict.Substring(verdict.IndexOf('|') + 1) : verdict;
                Write(sink, $"Guard: {kind} - {message}");
                if (kind != "ok") return kind == "rolledback" ? 20 : kind == "rejected" ? 21 : 22;

                sink?.Status("Updating the Apps & features entry…", 95);
                RegisterApp(SetupVersion);
                Write(sink, $"Upgraded to {SetupVersion}.");
                sink?.Status("Upgraded", 100);
                return 0;
            }
            catch (Exception ex) { Write(sink, "ERROR: " + ex.Message); return 1; }
            finally
            {
                Run("schtasks.exe", $"/Delete /TN {task} /F");
                Cleanup(work);
            }
        }

        /// <summary>
        /// Runs as SYSTEM: puts the package where the Guard picks up updates and reports the Guard's verdict
        /// (agent.updated / agent.updateFailed = rolled back / tamper.updateRejected) from its event log.
        /// </summary>
        private const string UpgradeScript = @"param([string] $Work)
$ErrorActionPreference = 'Stop'
$result = Join-Path $Work 'result.txt'
$status = Join-Path $Work 'status.txt'
function Done($kind, $msg) { Set-Content -Path $result -Value ""$kind|$msg"" -NoNewline; exit 0 }
try {
    $req = Get-Content -Raw (Join-Path $Work 'request.json') | ConvertFrom-Json
    $data = Join-Path $env:ProgramData 'SentinelDesk'
    $upd = Join-Path $data 'updates'
    New-Item -ItemType Directory -Force $upd | Out-Null
    $events = Join-Path $data 'guard-events.jsonl'
    $offset = if (Test-Path $events) { (Get-Item $events).Length } else { 0 }
    if (Test-Path (Join-Path $upd 'pending.json')) { Done 'error' 'Another agent update is already waiting to be installed. Try again in a minute.' }
    $zip = Join-Path $upd (""setup-"" + $req.version + "".zip"")
    Copy-Item (Join-Path $Work 'agent.zip') $zip -Force
    $pending = @{ version = $req.version; zip = $zip; sha256 = $req.sha256; signature = $req.signature } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText((Join-Path $upd 'pending.json'), $pending)
    Set-Content -Path $status -Value 'handed' -NoNewline
    $deadline = (Get-Date).AddMinutes(5)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        if ((Test-Path (Join-Path $upd 'updating.flag'))) { Set-Content -Path $status -Value 'installing' -NoNewline }
        if (-not (Test-Path $events)) { continue }
        $len = (Get-Item $events).Length
        if ($len -lt $offset) { $offset = 0 }
        if ($len -eq $offset) { continue }
        $fs = [IO.File]::Open($events, 'Open', 'Read', 'ReadWrite')
        try { [void]$fs.Seek($offset, 'Begin'); $text = (New-Object IO.StreamReader($fs)).ReadToEnd() } finally { $fs.Close() }
        foreach ($line in ($text -split ""`n"")) {
            if (-not $line.Trim()) { continue }
            try { $e = $line | ConvertFrom-Json } catch { continue }
            switch ($e.type) {
                'agent.updated'         { Done 'ok' $e.message }
                'agent.updateFailed'    { Done 'rolledback' $e.message }
                'tamper.updateRejected' { Done 'rejected' $e.message }
            }
        }
    }
    Done 'timeout' 'The Guard service did not install the update within 5 minutes. Is the SentinelDesk Guard service running?'
} catch { Done 'error' $_.Exception.Message }
";

        // ───────────────────────────── uninstall ─────────────────────────────

        /// <summary>
        /// Uninstall.exe lives in the install folder, which the uninstall deletes: when started from there it copies itself
        /// to %TEMP% and runs that copy (like any Windows uninstaller). Returns the copy's exit code in silent mode.
        /// </summary>
        public static int? RelaunchFromTempIfNeeded(SetupConfig c, string[] args)
        {
            if (!c.Uninstall || c.FromTemp) return null;
            var exe = Assembly.GetExecutingAssembly().Location;
            if (!exe.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase)) return null;
            var temp = Path.Combine(Path.GetTempPath(), "SentinelDesk-Uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
            File.Copy(exe, temp, true);
            var a = string.Join(" ", args.Select(x => x.Contains(" ") ? "\"" + x + "\"" : x)) + " /uninstall /fromtemp";
            var p = Process.Start(new ProcessStartInfo(temp, a) { UseShellExecute = false });
            if (!c.Quiet) return 0;           // interactive: let the copy take over (frees this file for deletion)
            p.WaitForExit();
            return p.ExitCode;
        }

        public static int Uninstall(SetupConfig c, IProgressSink sink)
        {
            var script = Path.Combine(InstallDir, "uninstall-agent.ps1");
            if (!IsInstalled()) { Write(sink, "SentinelDesk is not installed on this computer."); RemoveAppEntry(); return 0; }
            if (!File.Exists(script)) { Write(sink, $"ERROR: {script} was not found."); return 3; }
            if (string.IsNullOrEmpty(c.UninstallKey)) { Write(sink, "ERROR: the Uninstall Key or a one-time code is required: Uninstall.exe /S /key=<key>"); return 2; }

            sink?.Status("Checking the Uninstall Key…", 20);
            Write(sink, "Verifying the Uninstall Key and removing SentinelDesk...");
            // the key goes to the script in its environment, never on a command line other processes can read
            var code = RunPowerShell($"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"", Path.GetTempPath(), c.UninstallKey, line =>
            {
                Write(sink, line);
                if (line.Contains("Removing SentinelDesk")) sink?.Status("Removing SentinelDesk…", 60);
            });
            if (code != 0) { Write(sink, $"SentinelDesk was not removed (code {code})."); return code; }
            RemoveAppEntry();
            ScheduleLeftoverCleanup();
            sink?.Status("Removed", 100);
            Write(sink, "SentinelDesk was removed.");
            return 0;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existing, string newName, int flags);

        /// <summary>Files Windows still had open (e.g. a waiting Uninstall.exe) are deleted at the next restart.</summary>
        private static void ScheduleLeftoverCleanup()
        {
            try
            {
                if (!Directory.Exists(InstallDir)) return;
                foreach (var f in Directory.GetFiles(InstallDir, "*", SearchOption.AllDirectories)) MoveFileEx(f, null, 4 /* MOVEFILE_DELAY_UNTIL_REBOOT */);
                MoveFileEx(InstallDir, null, 4);
            }
            catch { }
        }

        // ───────────────────────────── helpers ─────────────────────────────

        /// <summary>Apps &amp; features entry (64-bit registry view). The uninstall button runs Uninstall.exe in the install folder.</summary>
        public static void RegisterApp(string version)
        {
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var k = hklm.CreateSubKey(AppKey))
            {
                k.SetValue("DisplayName", "SentinelDesk Agent");
                k.SetValue("DisplayVersion", version);
                k.SetValue("Publisher", "Yohan Koshala (LordeFX)");
                k.SetValue("URLInfoAbout", "https://www.lordefx.com");
                k.SetValue("HelpLink", "https://github.com/tenzaicbt");
                k.SetValue("Comments", "Created and developed by Yohan Koshala");
                k.SetValue("DisplayIcon", Path.Combine(InstallDir, "SentinelDesk.Tray.exe"));
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", "\"" + Path.Combine(InstallDir, "Uninstall.exe") + "\"");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                try
                {
                    var kb = Directory.GetFiles(InstallDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024;
                    k.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, kb), RegistryValueKind.DWord);
                }
                catch { }
            }
        }

        private static void RemoveAppEntry()
        {
            try { using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)) hklm.DeleteSubKeyTree(AppKey, false); } catch { }
        }

        /// <summary>A work folder only SYSTEM and Administrators can open (a standard user cannot plant or read files in it).</summary>
        private static string PrivateWorkFolder()
        {
            var work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", "sd-setup-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(work);
            Run("icacls.exe", $"\"{work}\" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F /Q");
            return work;
        }

        private static void ExtractPackage(string zipPath)
        {
            using (var res = typeof(Installer).Assembly.GetManifestResourceStream("agent.zip"))
            using (var fs = File.Create(zipPath)) res.CopyTo(fs);
        }

        private static void Cleanup(string work)
        {
            ThreadPool.QueueUserWorkItem(_ => { for (var i = 0; i < 5; i++) try { Directory.Delete(work, true); return; } catch { Thread.Sleep(1000); } });
        }

        private static string SafeRead(string path)
        {
            try { return File.ReadAllText(path).Trim(); } catch { return null; }
        }

        private static int RunPowerShell(string args, string workDir, string uninstallKey, Action<string> line)
        {
            var psi = new ProcessStartInfo(PowerShell, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = workDir,
            };
            if (uninstallKey != null) psi.EnvironmentVariables["SD_UNINSTALL_KEY"] = uninstallKey;
            using (var p = Process.Start(psi))
            {
                p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) line(e.Data.TrimEnd()); };
                p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) line("! " + e.Data.TrimEnd()); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        private static void Run(string exe, string args)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit();
                }
            }
            catch { }
        }

        private static string Escape(string s) => (s ?? "").Replace("\"", "");
    }
}
