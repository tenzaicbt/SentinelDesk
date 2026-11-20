using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SentinelDesk.Setup
{
    /// <summary>
    /// Settings carried inside the setup file. The web console (or build.ps1) appends them to the end of the exe:
    ///   [key=value lines (UTF-8)][length: int32 LE]["SDSETUP1"]
    /// Command-line switches override them: /server URL /token TOKEN /key UNINSTALLKEY /pin SHA256 /tofu /quiet
    /// </summary>
    public sealed class SetupConfig
    {
        public const string Marker = "SDSETUP1";

        public string ServerUrl;
        public string EnrollToken;
        /// <summary>PBKDF2 hash (from the console) or a plain key typed by the installer.</summary>
        public string UninstallKey;
        public string ServerCertSha256;
        public bool TrustOnFirstUse;
        public string OrgName;
        public string BranchName;
        public bool Quiet;

        public bool IsPreconfigured => !string.IsNullOrEmpty(ServerUrl) && !string.IsNullOrEmpty(EnrollToken) && !string.IsNullOrEmpty(UninstallKey);

        public static SetupConfig Load(string[] args)
        {
            var c = new SetupConfig();
            foreach (var kv in ReadTrailer(System.Reflection.Assembly.GetExecutingAssembly().Location)) c.Set(kv.Key, kv.Value);

            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i].TrimStart('/', '-').ToLowerInvariant();
                string Next() => i + 1 < args.Length ? args[++i] : "";
                switch (a)
                {
                    case "quiet": case "q": case "silent": case "s": c.Quiet = true; break;
                    case "tofu": c.TrustOnFirstUse = true; break;
                    case "server": c.ServerUrl = Next(); break;
                    case "token": c.EnrollToken = Next(); break;
                    case "key": c.UninstallKey = Next(); break;
                    case "pin": c.ServerCertSha256 = Next(); break;
                }
            }
            return c;
        }

        private void Set(string key, string value)
        {
            switch (key)
            {
                case "server": ServerUrl = value; break;
                case "token": EnrollToken = value; break;
                case "uninstallKeyHash": UninstallKey = value; break;
                case "pin": ServerCertSha256 = value; break;
                case "tofu": TrustOnFirstUse = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                case "org": OrgName = value; break;
                case "branch": BranchName = value; break;
            }
        }

        public static Dictionary<string, string> ReadTrailer(string exePath)
        {
            var result = new Dictionary<string, string>();
            try
            {
                using (var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (fs.Length < 12) return result;
                    var tail = new byte[12];
                    fs.Seek(-12, SeekOrigin.End);
                    fs.Read(tail, 0, 12);
                    if (Encoding.ASCII.GetString(tail, 4, 8) != Marker) return result;
                    var len = BitConverter.ToInt32(tail, 0);
                    if (len <= 0 || len > 64 * 1024 || len > fs.Length - 12) return result;
                    var body = new byte[len];
                    fs.Seek(-12 - len, SeekOrigin.End);
                    fs.Read(body, 0, len);
                    foreach (var line in Encoding.UTF8.GetString(body).Split('\n'))
                    {
                        var idx = line.IndexOf('=');
                        if (idx > 0) result[line.Substring(0, idx).Trim()] = line.Substring(idx + 1).Trim();
                    }
                }
            }
            catch { /* no trailer → interactive setup */ }
            return result;
        }

        /// <summary>Builds the trailer bytes (used by tests / tooling; the server has its own copy).</summary>
        public static byte[] BuildTrailer(IDictionary<string, string> values)
        {
            var body = Encoding.UTF8.GetBytes(string.Join("\n", values.Where(v => !string.IsNullOrEmpty(v.Value)).Select(v => v.Key + "=" + v.Value)));
            return body.Concat(BitConverter.GetBytes(body.Length)).Concat(Encoding.ASCII.GetBytes(Marker)).ToArray();
        }
    }
}
