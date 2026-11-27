using System;
using System.Windows.Forms;

namespace SentinelDesk.Setup
{
    internal static class Program
    {
        /// <summary>
        ///  FluentSetup - installs, upgrades and removes the SentinelDesk agent.
        ///
        ///  SentinelDesk-Setup.exe                    guided install, or upgrade when an older agent is installed (UAC prompt)
        ///  SentinelDesk-Setup.exe /quiet  (or /S)    silent, with the settings embedded by the web console (GPO / Intune / SCCM)
        ///      overrides: /server URL  /token TOKEN  /key UNINSTALLKEY  /pin SHA256  /tofu   (also /server=URL ...)
        ///  Uninstall.exe  (or Setup.exe /uninstall)  remove; silent: Uninstall.exe /S /key=KEY
        ///
        ///  Exit codes: 0 done (or nothing to do) · 2 settings missing · 12 setup file not signed for upgrades ·
        ///  20 upgrade rolled back · 21 package rejected · 1602 cancelled · other = failed (log: C:\Windows\Temp\SentinelDesk-Setup.log)
        /// </summary>
        [STAThread]
        private static int Main(string[] args)
        {
            var config = SetupConfig.Load(args);

            var relaunched = Installer.RelaunchFromTempIfNeeded(config, args);
            if (relaunched is int code) return code;

            var mode = Installer.DetectMode(config);
            if (config.Quiet)
            {
                switch (mode)
                {
                    case SetupMode.Install: return Installer.Install(config, null);
                    case SetupMode.Upgrade: return Installer.Upgrade(config, null);
                    case SetupMode.Uninstall: return Installer.Uninstall(config, null);
                    default: return 0;   // already installed / nothing to remove
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var form = new WizardForm(config, mode);
            Application.Run(form);
            return form.ExitCode;
        }
    }
}
