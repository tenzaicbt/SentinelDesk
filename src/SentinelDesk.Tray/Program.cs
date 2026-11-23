using System.IO.Pipes;
using System.Text;
using SentinelDesk.Shared;

namespace SentinelDesk.Tray;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Windows runs "SentinelDesk.Tray.exe --blocked <program> [args]" instead of a program blocked by policy
        // (Image File Execution Options). Tell the agent, explain it to the user, and exit - never start a second tray.
        if (args.Length > 0 && args[0].Equals("--blocked", StringComparison.OrdinalIgnoreCase))
        {
            ShowBlocked(args.Length > 1 ? args[1] : null);
            return;
        }

        // "Reconnect to server": started elevated from the tray menu
        if (args.Length > 0 && args[0].Equals("--reconnect", StringComparison.OrdinalIgnoreCase))
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new ReconnectForm());
            return;
        }

        // one tray per interactive session
        using var mutex = new Mutex(true, @"Local\SentinelDesk.Tray", out var first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }

    private static void ShowBlocked(string? program)
    {
        var exe = string.IsNullOrWhiteSpace(program) ? "This application" : Path.GetFileName(program.Trim('"'));
        // the pipe stays open while the message is shown: the agent writes its greeting before it reads our line
        NamedPipeClientStream? pipe = null;
        try
        {
            pipe = new NamedPipeClientStream(".", PipeNames.Agent, PipeDirection.InOut);
            pipe.Connect(1500);
            var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
            var bytes = Encoding.UTF8.GetBytes(Json.Serialize(new PipeMessage { Type = "blocked", CurrentApp = exe, User = user }) + "\n");
            pipe.Write(bytes);
            pipe.Flush();
        }
        catch { /* agent not reachable: still block and explain */ }

        try
        {
            ApplicationConfiguration.Initialize();
            MessageBox.Show($"{exe} is blocked by your organisation's policy.\n\nContact your support team if you need it for your work.",
                "SentinelDesk — Application blocked", MessageBoxButtons.OK, MessageBoxIcon.Stop);
        }
        finally { pipe?.Dispose(); }
    }
}
