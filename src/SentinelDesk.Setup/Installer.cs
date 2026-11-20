using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;

namespace SentinelDesk.Setup
{
    /// <summary>Extracts the embedded agent bundle and runs the hardened install script (install-agent.ps1).</summary>
    public static class Installer
    {
        public static string LogFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", "SentinelDesk-Setup.log");

        public static bool IsInstalled()
        {
            try
            {
                using (var sc = new ServiceController("SentinelDeskAgent")) { var _ = sc.Status; return true; }
            }
            catch (InvalidOperationException) { return false; }
        }

        public static bool HasPackage() => typeof(Installer).Assembly.GetManifestResourceInfo("agent.zip") != null;

        /// <summary>Returns 0 on success. Progress lines are reported through <paramref name="log"/>.</summary>
        public static int Run(SetupConfig c, Action<string> log)
        {
            void Log(string s)
            {
                log?.Invoke(s);
                try { File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {s}{Environment.NewLine}"); } catch { }
            }

            if (!HasPackage()) { Log("ERROR: this setup file does not contain the agent package (build it with build.ps1)."); return 10; }
            if (IsInstalled()) { Log("SentinelDesk is already installed on this computer."); return 0; }
            if (string.IsNullOrWhiteSpace(c.ServerUrl) || string.IsNullOrWhiteSpace(c.EnrollToken) || string.IsNullOrWhiteSpace(c.UninstallKey))
            { Log("ERROR: server address, enrollment token and uninstall key are required."); return 2; }

            var work = Path.Combine(Path.GetTempPath(), "SentinelDesk-Setup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Log("Extracting agent package...");
                Directory.CreateDirectory(work);
                var zipPath = Path.Combine(work, "agent.zip");
                using (var res = typeof(Installer).Assembly.GetManifestResourceStream("agent.zip"))
                using (var fs = File.Create(zipPath)) res.CopyTo(fs);
                var bundle = Path.Combine(work, "agent");
                ZipFile.ExtractToDirectory(zipPath, bundle);
                File.Delete(zipPath);

                var script = Path.Combine(bundle, "install-agent.ps1");
                if (!File.Exists(script)) { Log("ERROR: install-agent.ps1 missing from package."); return 11; }

                var args = new StringBuilder($"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -SourceDir \"{bundle}\"");
                args.Append($" -ServerUrl \"{Escape(c.ServerUrl)}\" -EnrollToken \"{Escape(c.EnrollToken)}\" -UninstallKey \"{Escape(c.UninstallKey)}\"");
                if (!string.IsNullOrWhiteSpace(c.ServerCertSha256)) args.Append($" -ServerCertSha256 \"{Escape(c.ServerCertSha256)}\"");
                if (c.TrustOnFirstUse) args.Append(" -TrustOnFirstUse");

                Log($"Installing and enrolling with {c.ServerUrl} ...");
                var psi = new ProcessStartInfo("powershell.exe", args.ToString())
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = bundle
                };
                using (var p = Process.Start(psi))
                {
                    p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log(e.Data.TrimEnd()); };
                    p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log("! " + e.Data.TrimEnd()); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    p.WaitForExit();
                    Log(p.ExitCode == 0 ? "Installation completed." : $"Installation failed (code {p.ExitCode}). See {LogFile}");
                    return p.ExitCode == 0 && IsInstalled() ? 0 : Math.Max(1, p.ExitCode);
                }
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.Message);
                return 1;
            }
            finally
            {
                Task.Run(() => { try { Directory.Delete(work, true); } catch { } });
            }
        }

        private static string Escape(string s) => s.Replace("\"", "");
    }
}
