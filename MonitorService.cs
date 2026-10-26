namespace CodexAutoApprove;

internal sealed class MonitorService
{
    private readonly string folder;
    private readonly List<ButtonTemplate> templates = new();
    private string signature = "";
    private DateTime lastClick = DateTime.MinValue;
    private string? lastError;
    private AppSettings? settings;
    private volatile bool paused;
    public bool Paused { get => paused; set => paused = value; }
    public event Action<string>? Status;
    public event Action<AppSettings, int>? ConfigurationLoaded;
    public event Action<ButtonMatch>? Clicked;
    public event Action<string>? LogEntry;

    public MonitorService(string folder) { this.folder = folder; }

    private void Reload()
    {
        string settingsPath = Path.Combine(folder, "settings.ini");
        if (!File.Exists(settingsPath)) AppSettings.SaveGeneral(settingsPath, 2, false);
        string imageFolder = Path.Combine(folder, "images");
        string[] files = Directory.Exists(imageFolder) ? Directory.GetFiles(imageFolder).Where(p =>
            Path.GetExtension(p).Equals(".png", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p).ToArray() : [];
        string current = File.GetLastWriteTimeUtc(settingsPath).Ticks + ":" + new FileInfo(settingsPath).Length + "|" +
            string.Join("|", files.Select(p => p + ":" + File.GetLastWriteTimeUtc(p).Ticks + ":" + new FileInfo(p).Length));
        if (current == signature) return;
        var next = AppSettings.Load(settingsPath);
        WindowsStartup.Synchronize(next.StartWithWindows);
        var loaded = new List<ButtonTemplate>();
        try
        {
            foreach (string file in files)
                loaded.Add(new(file, next.ClickPoints.TryGetValue(Path.GetFileName(file), out var point) ? point : null));
            if (loaded.Count == 0) throw new InvalidOperationException("No reference PNGs found. Add button screenshots to the images folder.");
        }
        catch { foreach (var template in loaded) template.Dispose(); throw; }
        foreach (var template in templates) template.Dispose();
        templates.Clear(); templates.AddRange(loaded); settings = next; signature = current;
        ConfigurationLoaded?.Invoke(next, templates.Count);
    }

    public async Task RunAsync(CancellationToken token)
    {
        DateTime nextScan = DateTime.MinValue;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var previousSettings = settings;
                    Reload();
                    if (!ReferenceEquals(previousSettings, settings)) nextScan = DateTime.MinValue;
                    if (!paused && settings is not null)
                    {
                        Status?.Invoke($"Monitoring {templates.Count} layouts on {Screen.AllScreens.Length} screen(s)…");
                        if (DateTime.UtcNow >= nextScan)
                        {
                            ScanAndClick(settings, token);
                            nextScan = DateTime.UtcNow.AddSeconds(settings.IntervalSeconds);
                        }
                    }
                    else Status?.Invoke("Paused — no automatic clicks");
                    lastError = null;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception e)
                {
                    // A bad or partially edited configuration blocks clicking until a valid reload.
                    Status?.Invoke("Monitoring blocked: " + e.Message);
                    if (lastError != e.Message) { lastError = e.Message; Log("ERROR: " + e.Message); }
                }
                // Poll configuration even when a long scan interval is configured.
                await Task.Delay(TimeSpan.FromMilliseconds(200), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { foreach (var template in templates) template.Dispose(); templates.Clear(); }
    }

    private void ScanAndClick(AppSettings config, CancellationToken token)
    {
        var frames = new List<ScreenFrame>();
        foreach (Screen screen in Screen.AllScreens)
        {
            token.ThrowIfCancellationRequested();
            if (paused) return;
            using var capture = Desktop.Capture(screen.Bounds);
            var pixels = PixelImage.FromBitmap(capture);
            double[] scales = config.Scales.Concat(new[] { screen.Bounds.Height / 1080.0, screen.Bounds.Width / 1920.0 })
                .Where(s => s >= .25 && s <= 4).Select(s => Math.Round(s, 3)).Distinct().ToArray();
            frames.Add(new(pixels, screen.Bounds, scales));
        }
        var results = new List<ImageScanResult>();
        bool cooldown = DateTime.UtcNow - lastClick < TimeSpan.FromSeconds(config.CooldownSeconds);
        // Finish and log EVERY image before selecting a click, even during cooldown.
        foreach (var result in ScanAnalyzer.Analyze(frames, templates, config.MinimumScore, token))
        {
            results.Add(result);
            Log(result.Describe(config.MinimumScore, cooldown));
        }
        if (paused || DateTime.UtcNow - lastClick < TimeSpan.FromSeconds(config.CooldownSeconds)) return;
        foreach (var result in results.Where(r => r.Match is not null).OrderByDescending(r => r.Match!.Score))
        {
            token.ThrowIfCancellationRequested();
            if (paused) return;
            var match = result.Match!;
            var template = templates.First(t => t.FileName == result.FileName);
            // Verify against a NEW capture, near the original location, so a vanished prompt is never clicked.
            Rectangle verificationArea = Rectangle.Inflate(match.Bounds, 5, 5);
            verificationArea.Intersect(frames.First(f => f.Bounds.Contains(match.Bounds)).Bounds);
            using var fresh = Desktop.Capture(verificationArea);
            var verified = TemplateMatcher.Find(PixelImage.FromBitmap(fresh), verificationArea.Location, template,
                new[] { match.Scale }, config.MinimumScore, token);
            if (verified is null || !verified.Bounds.Contains(verified.ClickPoint))
            {
                Log($"{match.FileName}: fresh verification failed; no click.");
                continue;
            }
            token.ThrowIfCancellationRequested();
            if (paused) return;
            Desktop.Click(verified.ClickPoint);
            lastClick = DateTime.UtcNow;
            Log($"Clicked {verified.FileName} at ({verified.ClickPoint.X}, {verified.ClickPoint.Y}), scale {verified.Scale:0.###}, score {verified.Score:P1}");
            Clicked?.Invoke(verified);
            return; // One click per scan, shared cooldown across all layouts.
        }
    }

    private void Log(string message)
    {
        string entry = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}";
        LogEntry?.Invoke(entry);
        try
        {
            string path = Path.Combine(folder, "activity.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Move(path, path + ".previous", true);
            File.AppendAllText(path, entry + Environment.NewLine);
        }
        catch { /* Logging must not stop monitoring. */ }
    }
}
