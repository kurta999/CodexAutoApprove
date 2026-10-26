using System.Diagnostics;
using System.Drawing.Drawing2D;
using CodexAutoApprove;

string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
int checks = 0;
string scratch = Path.Combine(root, "tests", ".scratch");
Directory.CreateDirectory(scratch);
void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    checks++; Console.WriteLine("PASS: " + message);
}
var config = AppSettings.Load(Path.Combine(root, "settings.ini"));
var templates = Directory.GetFiles(Path.Combine(root, "images"), "*.png").Select(p =>
    new ButtonTemplate(p, config.ClickPoints.TryGetValue(Path.GetFileName(p), out var click) ? click : null)).ToArray();
Check(new[] { "allow_once.png", "allow_once_2.png", "allow_once_3.png" }.All(name => templates.Any(t => t.FileName == name)),
    "All three supplied layouts are loaded together");
Check(config.MinimumScore == .90, "Default configuration uses a 90% match threshold");
var stopwatch = Stopwatch.StartNew();
foreach (var template in templates)
{
    foreach (double scale in new[] { .75, 1, 1.25, 1.5, 2.0 })
    {
        using var screen = new Bitmap(1000, 650);
        var reference = template.AtScale(scale).Image;
        var point = new Point(101, 157);
        using (var g = Graphics.FromImage(screen))
        {
            g.Clear(Color.FromArgb(41, 51, 61));
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(template.Original, new Rectangle(point, new(reference.Width, reference.Height)));
        }
        var origin = new Point(-1920, -180);
        var match = TemplateMatcher.Find(PixelImage.FromBitmap(screen), origin, template, new[] { 1.0, scale }, config.MinimumScore);
        Check(match is not null, $"{template.FileName}: detect at {scale}×");
        Check(match!.Bounds.Location == new Point(origin.X + point.X, origin.Y + point.Y), $"{template.FileName}: correct position at {scale}× on a negative-origin monitor");
        var expected = new Point(origin.X + point.X + (int)Math.Round(template.ClickPoint.X * reference.Width / (double)template.Original.Width),
            origin.Y + point.Y + (int)Math.Round(template.ClickPoint.Y * reference.Height / (double)template.Original.Height));
        Check(match.ClickPoint == expected, $"{template.FileName}: approval offset scales correctly at {scale}×");
        Check(match.Bounds.Contains(match.ClickPoint), "Click stays within matched reference");
    }
    using var negative = new Bitmap(500, 200);
    using (var g = Graphics.FromImage(negative)) { g.Clear(Color.FromArgb(48, 48, 48)); g.FillRectangle(Brushes.White, 20, 50, 320, 30); }
    Check(TemplateMatcher.Find(PixelImage.FromBitmap(negative), Point.Empty, template, new[] { .75, 1, 1.25 }, config.MinimumScore) is null,
        $"{template.FileName}: reject a generic light rectangle without the approval label");
    using (var wrongLabel = new Bitmap(template.Original))
    {
        using (var g = Graphics.FromImage(wrongLabel))
            g.FillRectangle(Brushes.White, template.FileName switch
            {
                "allow_once.png" => new Rectangle(121, 14, 66, 18),
                "allow_once_2.png" => new Rectangle(132, 46, 75, 20),
                "allow_once_3.png" => new Rectangle(93, 8, 69, 19),
                _ => new Rectangle(template.ClickPoint.X - 35, template.ClickPoint.Y - 9, 70, 18)
            });
        Check(TemplateMatcher.Find(PixelImage.FromBitmap(wrongLabel), Point.Empty, template, new[] { 1.0 }, config.MinimumScore) is null,
            $"{template.FileName}: reject the same layout when the approval label is removed");
    }
    var originalPixels = PixelImage.FromBitmap(template.Original);
    var brighterPixels = originalPixels.Pixels.Select(pixel => unchecked((int)0xff000000) |
        (Math.Min(255, ((pixel >> 16) & 255) + 25) << 16) |
        (Math.Min(255, ((pixel >> 8) & 255) + 25) << 8) | Math.Min(255, (pixel & 255) + 25)).ToArray();
    var brighter = new PixelImage(originalPixels.Width, originalPixels.Height, brighterPixels);
    var tolerantMatch = TemplateMatcher.Find(brighter, Point.Empty, template, new[] { 1.0 }, config.MinimumScore);
    Check(tolerantMatch is not null && tolerantMatch.Score >= .90 && tolerantMatch.Score < .96,
        $"{template.FileName}: 90% threshold accepts a brightness difference rejected at 96%");
    Check(TemplateMatcher.Find(brighter, Point.Empty, template, new[] { 1.0 }, .96) is null,
        $"{template.FileName}: previous 96% threshold rejects that brightness difference");
    var diagnostic = ScanAnalyzer.Analyze(new[] { new ScreenFrame(brighter, new Rectangle(0, 0, brighter.Width, brighter.Height), new[] { 1.0 }) },
        new[] { template }, .96).Single();
    Check(diagnostic.Match is null && diagnostic.Score is >= .90 and < .96,
        $"{template.FileName}: reports an actual percentage even below the click threshold");
    Check(diagnostic.Describe(.96, false).Contains("%") && diagnostic.Describe(.96, false).Contains("no verified match"),
        $"{template.FileName}: diagnostic log distinguishes similarity from click eligibility");
    string temporaryImage = Path.Combine(scratch, Guid.NewGuid() + ".png");
    try
    {
        template.Original.Save(temporaryImage);
        using var automatic = new ButtonTemplate(temporaryImage, null);
        Check(automatic.ClickPoint.X > 0 && automatic.ClickPoint.Y > 0, $"{template.FileName}: infer a safe light-button center for new PNGs");
    }
    finally { File.Delete(temporaryImage); }
}
using (var together = new Bitmap(Math.Max(900, templates.Max(t => t.Original.Width) + 40), templates.Sum(t => t.Original.Height + 40) + 20))
{
    using (var g = Graphics.FromImage(together))
    {
        g.Clear(Color.DimGray);
        int y = 20;
        foreach (var template in templates)
        {
            g.DrawImageUnscaled(template.Original, 20, y);
            y += template.Original.Height + 40;
        }
    }
    var pixels = PixelImage.FromBitmap(together);
    foreach (var template in templates)
        Check(TemplateMatcher.Find(pixels, Point.Empty, template, new[] { 1.0 }, config.MinimumScore) is not null, "All layouts can coexist: " + template.FileName);
    var results = ScanAnalyzer.Analyze(new[] { new ScreenFrame(pixels, new Rectangle(Point.Empty, together.Size), new[] { 1.0 }) }, templates, config.MinimumScore).ToArray();
    Check(results.Length == templates.Length && results.All(r => r.Match is not null && r.Score > .99999),
        "Each scan reports every image even when the first image already matches");
    Check(results.All(r => r.Describe(config.MinimumScore, true).Contains("click cooldown")),
        "Every image still has a percentage while clicking is in cooldown");
}
using (var fullHd = new Bitmap(1920, 1080))
{
    using (var g = Graphics.FromImage(fullHd))
    {
        g.Clear(Color.FromArgb(48, 48, 48));
        g.FillRectangle(Brushes.White, 500, 600, 400, 50);
        g.DrawString("An unrelated desktop dialog", SystemFonts.DefaultFont, Brushes.Black, 510, 612);
    }
    var pixels = PixelImage.FromBitmap(fullHd);
    var scanTime = Stopwatch.StartNew();
    foreach (var template in templates)
        Check(TemplateMatcher.Find(pixels, Point.Empty, template, config.Scales, config.MinimumScore) is null,
            "Full 1080p desktop rejects unrelated content across every configured scale: " + template.FileName);
    var results = ScanAnalyzer.Analyze(new[] { new ScreenFrame(pixels, new Rectangle(Point.Empty, fullHd.Size), config.Scales) }, templates, config.MinimumScore).ToArray();
    Check(results.Length == templates.Length && results.All(r => r.Match is null && r.Score is >= 0 and <= 1),
        "Unmatched 1080p scan reports a measured diagnostic score for every image");
    Console.WriteLine($"1080p scan of all layouts / all scales: {scanTime.Elapsed.TotalSeconds:0.00}s");
}
var tooSmall = ScanAnalyzer.Analyze(new[] { new ScreenFrame(new PixelImage(1, 1, new[] { 0 }), new Rectangle(0, 0, 1, 1), new[] { 1.0 }) },
    templates, config.MinimumScore).ToArray();
Check(tooSmall.All(r => r.Score is null && r.Describe(config.MinimumScore, false).Contains("N/A")),
    "References that cannot fit are reported as N/A rather than an invented percentage");
string temporaryIni = Path.Combine(scratch, Guid.NewGuid() + ".ini");
try
{
    File.Copy(Path.Combine(root, "settings.ini"), temporaryIni);
    AppSettings.SaveGeneral(temporaryIni, 1.5, true);
    var saved = AppSettings.Load(temporaryIni);
    Check(saved.IntervalSeconds == 1.5 && saved.StartWithWindows, "INI persists interval and enabled startup");
    Check(saved.ClickPoints.Count == config.ClickPoints.Count && saved.MinimumScore == config.MinimumScore, "Saving general settings preserves all layout offsets and matching settings");
    AppSettings.SaveGeneral(temporaryIni, 3, false);
    Check(!AppSettings.Load(temporaryIni).StartWithWindows, "INI can disable automatic startup");
    AppSettings.SaveGeneral(temporaryIni, 3, false, false);
    Check(!AppSettings.Load(temporaryIni).MinimizeToTray, "INI can disable minimize to tray");
    AppSettings.SaveGeneral(temporaryIni, 3, false, true);
    Check(AppSettings.Load(temporaryIni).MinimizeToTray, "INI can enable minimize to tray");
    File.WriteAllText(temporaryIni, "[General]\nCheckIntervalSeconds=2\n");
    var defaults = AppSettings.Load(temporaryIni);
    Check(defaults.MinimumScore == .90 && defaults.ClickPoints.ContainsKey("allow_once_3.png"),
        "Missing matching settings fall back to 90% and include the third layout offset");
    File.WriteAllText(temporaryIni, "[General]\nCheckIntervalSeconds=NaN\n");
    bool rejected = false;
    try { AppSettings.Load(temporaryIni); } catch (FormatException) { rejected = true; }
    Check(rejected, "Invalid settings are rejected before monitoring");
}
finally { File.Delete(temporaryIni); foreach (var template in templates) template.Dispose(); }
Exception? renderError = null;
var rendering = new Thread(() =>
{
    try
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        var type = typeof(AppSettings).Assembly.GetType("CodexAutoApprove.MainForm")!;
        using var form = (Form)Activator.CreateInstance(type, new object[] { false, true })!;
        form.Show(); form.PerformLayout(); form.Update();
        var log = (ListBox)form.Controls.Find("activityLog", true).Single();
        var append = type.GetMethod("AppendLog", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        log.Items.Clear();
        for (int i = 0; i < 103; i++) append.Invoke(form, new object[] { $"entry {i}" });
        Check(log.Items.Count == 100, "Log panel retains a maximum of 100 entries");
        Check((string)log.Items[0] == "entry 3" && (string)log.Items[99] == "entry 102",
            "Log panel discards the oldest entries and preserves chronological order");
        log.Items.Clear();
        append.Invoke(form, new object[] { "Preview — per-image match percentages appear here after every scan." });
        using var preview = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(preview, new Rectangle(Point.Empty, preview.Size));
        preview.Save(Path.Combine(scratch, "ui-preview.png"));
    }
    catch (Exception e) { renderError = e; }
});
rendering.SetApartmentState(ApartmentState.STA); rendering.Start(); rendering.Join();
Check(renderError is null, "Settings window renders without starting monitoring: " + renderError?.Message);
Console.WriteLine($"{checks} checks passed in {stopwatch.Elapsed.TotalSeconds:0.00}s. No screen captures, registry changes, or real clicks were performed.");
