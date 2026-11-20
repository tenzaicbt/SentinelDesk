using System;
using System.Windows.Forms;

namespace SentinelDesk.Setup
{
    internal static class Program
    {
        /// <summary>
        ///  SentinelDesk-Setup.exe            → guided install (UAC prompt)
        ///  SentinelDesk-Setup.exe /quiet     → silent install using the settings embedded by the console (GPO / Intune / SCCM)
        ///  Exit code 0 = installed (or already installed).
        /// </summary>
        [STAThread]
        private static int Main(string[] args)
        {
            var config = SetupConfig.Load(args);
            if (config.Quiet) return Installer.Run(config, null);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var form = new SetupForm(config);
            Application.Run(form);
            return form.ExitCode;
        }
    }
}
