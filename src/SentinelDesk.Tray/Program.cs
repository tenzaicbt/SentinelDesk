namespace SentinelDesk.Tray;

static class Program
{
    [STAThread]
    static void Main()
    {
        // one tray per interactive session
        using var mutex = new Mutex(true, @"Local\SentinelDesk.Tray", out var first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}
