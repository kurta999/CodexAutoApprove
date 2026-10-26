namespace CodexAutoApprove;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        bool background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        if (background)
        {
            try
            {
                if (!AppSettings.Load(Path.Combine(AppContext.BaseDirectory, "settings.ini")).StartWithWindows)
                {
                    WindowsStartup.Synchronize(false);
                    return;
                }
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, "Codex Auto Approve — invalid startup settings");
                return;
            }
        }
        using var singleton = new Mutex(true, @"Local\CodexAutoApprove", out bool first);
        if (!first)
        {
            MessageBox.Show("Codex Auto Approve is already running. Open its tray icon to change settings.",
                "Codex Auto Approve", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "Codex Auto Approve");
        Application.Run(new MainForm(background));
        singleton.ReleaseMutex();
    }
}
