using System.Diagnostics;

namespace CodexAutoApprove;

internal sealed class MainForm : Form
{
    private readonly NotifyIcon tray;
    private readonly MonitorService monitor = new(AppContext.BaseDirectory);
    private readonly CancellationTokenSource cancellation = new();
    private readonly Label status = new() { AutoSize = true, MaximumSize = new(610, 0), Text = "Loading reference images…" };
    private readonly Label layouts = new() { AutoSize = true, Text = "All supplied layouts are monitored together." };
    private readonly Label lastClick = new() { AutoSize = true, Text = "No approval clicked yet." };
    private readonly NumericUpDown interval = new() { Minimum = .2m, Maximum = 3600, DecimalPlaces = 1, Increment = .5m, Value = 2, Width = 95 };
    private readonly CheckBox startup = new() { AutoSize = true, Text = "Start with Windows" };
    private readonly CheckBox minimize = new() { AutoSize = true, Checked = true, Text = "Minimize / close to system tray" };
    private readonly Button pause = new() { Text = "Pause monitoring", AutoSize = true };
    private readonly ListBox activityLog = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true,
        IntegralHeight = false, Font = new Font("Consolas", 9), Name = "activityLog" };
    private readonly ToolStripMenuItem startupMenu = new("Start with Windows") { CheckOnClick = true };
    private readonly ToolStripMenuItem pauseMenu = new("Pause monitoring");
    private readonly ToolStripMenuItem minimizeMenu = new("Minimize to tray") { CheckOnClick = true, Checked = true };
    private Task? worker;
    private bool exiting;
    private bool saving;
    private bool resourcesDisposed;
    private bool shutdownInProgress;
    private readonly bool background;
    private bool minimizeToTray = true;

    public MainForm(bool background, bool preview = false)
    {
        this.background = background;
        if (preview) status.Text = "Preview — monitoring is not running";
        Text = "Codex Auto Approve";
        using (var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("CodexAutoApprove.assets.app.ico")!) Icon = new Icon(iconStream);
        AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new(780, 690);
        MinimumSize = new(700, 620); StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        var menu = new MenuStrip();
        var settingsMenu = new ToolStripMenuItem("Settings");
        settingsMenu.DropDownItems.Add(startupMenu);
        settingsMenu.DropDownItems.Add(minimizeMenu);
        settingsMenu.DropDownItems.Add("Open settings.ini", null, (_, _) => OpenFile("settings.ini"));
        settingsMenu.DropDownItems.Add("Open images folder", null, (_, _) => OpenFile("images"));
        menu.Items.Add(settingsMenu); menu.Items.Add("Exit", null, (_, _) => Exit());
        MainMenuStrip = menu;
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new(20, 16, 20, 10), AutoScroll = true };
        body.Controls.Add(new Label { AutoSize = true, Text = "Automatic approval", Font = new Font(Font, FontStyle.Bold), Margin = new(3, 0, 3, 12) });
        body.Controls.Add(status); body.Controls.Add(layouts);
        body.Controls.Add(new Label { AutoSize = true, MaximumSize = new(605, 0), Text = "Detects the reference PNGs on every monitor and clicks Allow once.\nPause monitoring or exit from the system tray at any time.", Margin = new(3, 12, 3, 14) });
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new(0, 0, 0, 8) };
        row.Controls.Add(new Label { AutoSize = true, Text = "Check interval (seconds):", Padding = new(0, 5, 4, 0) });
        row.Controls.Add(interval); body.Controls.Add(row); body.Controls.Add(startup); body.Controls.Add(minimize);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new(0, 10, 0, 12) };
        var save = new Button { Text = "Save settings", AutoSize = true };
        buttons.Controls.Add(save); buttons.Controls.Add(pause); body.Controls.Add(buttons); body.Controls.Add(lastClick);
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        content.ColumnStyles.Add(new(SizeType.Percent, 100));
        content.RowStyles.Add(new(SizeType.Absolute, 390));
        content.RowStyles.Add(new(SizeType.Percent, 100));
        var logGroup = new GroupBox { Dock = DockStyle.Fill, Text = "Activity log — latest 100 entries", Padding = new(8), Margin = new(20, 0, 20, 16) };
        logGroup.Controls.Add(activityLog);
        content.Controls.Add(body, 0, 0); content.Controls.Add(logGroup, 0, 1);
        Controls.Add(content); Controls.Add(menu);
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open", null, (_, _) => ShowWindow()); trayMenu.Items.Add(pauseMenu);
        trayMenu.Items.Add("Settings", null, (_, _) => ShowWindow()); trayMenu.Items.Add("Exit", null, (_, _) => Exit());
        tray = new NotifyIcon { Icon = Icon, Text = "Codex Auto Approve — monitoring", Visible = !preview, ContextMenuStrip = trayMenu };
        tray.DoubleClick += (_, _) => ShowWindow();
        pause.Click += (_, _) => TogglePause(); pauseMenu.Click += (_, _) => TogglePause();
        save.Click += (_, _) => SaveSettings(startup.Checked);
        startupMenu.Click += (_, _) => { startup.Checked = startupMenu.Checked; SaveSettings(startup.Checked); };
        minimizeMenu.Click += (_, _) => { minimize.Checked = minimizeMenu.Checked; SaveSettings(startup.Checked); };
        monitor.Status += text => OnUi(() => status.Text = text);
        monitor.LogEntry += entry => OnUi(() => AppendLog(entry));
        monitor.ConfigurationLoaded += (config, count) => OnUi(() =>
        {
            interval.Value = (decimal)config.IntervalSeconds;
            startup.Checked = startupMenu.Checked = config.StartWithWindows;
            minimize.Checked = minimizeMenu.Checked = minimizeToTray = config.MinimizeToTray;
            layouts.Text = $"{count} reference layouts active — all are checked on each scan.";
        });
        monitor.Clicked += match => OnUi(() => lastClick.Text = $"Last approval: {DateTime.Now:T} · {match.FileName} · ({match.ClickPoint.X}, {match.ClickPoint.Y})");
        Shown += (_, _) =>
        {
            if (preview) return;
            worker = Task.Run(() => monitor.RunAsync(cancellation.Token));
            if (this.background) Hide();
        };
        FormClosing += HandleFormClosing;
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized && minimizeToTray) Hide(); };
        if (preview) AppendLog("Preview — per-image match percentages appear here after every scan.");
    }

    private void AppendLog(string entry)
    {
        activityLog.BeginUpdate();
        try
        {
            activityLog.Items.Add(entry);
            while (activityLog.Items.Count > 100) activityLog.Items.RemoveAt(0);
            activityLog.TopIndex = activityLog.Items.Count - 1;
        }
        finally { activityLog.EndUpdate(); }
    }

    private void OnUi(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch (InvalidOperationException) { }
    }

    private void SaveSettings(bool enabled)
    {
        if (saving) return;
        try
        {
            saving = true;
            string path = Path.Combine(AppContext.BaseDirectory, "settings.ini");
            // Validate before saving; registry is reconciled again on configuration reload.
            AppSettings.Load(path);
            AppSettings.SaveGeneral(path, (double)interval.Value, enabled, minimize.Checked);
            WindowsStartup.Synchronize(enabled);
            startup.Checked = startupMenu.Checked = enabled;
            minimizeToTray = minimizeMenu.Checked = minimize.Checked;
            status.Text = "Settings saved. Monitoring will use them on the next scan.";
        }
        catch (Exception e) { MessageBox.Show(this, e.Message, "Could not save settings", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { saving = false; }
    }

    private void TogglePause()
    {
        monitor.Paused = !monitor.Paused;
        pause.Text = pauseMenu.Text = monitor.Paused ? "Resume monitoring" : "Pause monitoring";
        tray.Text = "Codex Auto Approve — " + (monitor.Paused ? "paused" : "monitoring");
        status.Text = monitor.Paused ? "Paused — no automatic clicks" : "Monitoring resumes on the next scan…";
    }

    private void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    private void OpenFile(string name)
    {
        try { Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, name)) { UseShellExecute = true }); }
        catch (Exception e) { MessageBox.Show(this, e.Message, "Could not open " + name); }
    }

    private void Exit() { exiting = true; Close(); }

    private async void HandleFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (shutdownInProgress) { e.Cancel = true; return; }
        if (e.CloseReason == CloseReason.UserClosing && !exiting && minimizeToTray) { e.Cancel = true; Hide(); return; }
        monitor.Paused = true; cancellation.Cancel(); tray.Visible = false;
        if (worker is not null && !worker.IsCompleted && e.CloseReason != CloseReason.WindowsShutDown)
        {
            e.Cancel = true; shutdownInProgress = true;
            try { await worker; } catch (OperationCanceledException) { }
            worker = null; shutdownInProgress = false; BeginInvoke(Close); return;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed)
        {
            resourcesDisposed = true; tray.Dispose(); cancellation.Cancel(); cancellation.Dispose();
        }
        base.Dispose(disposing);
    }
}
