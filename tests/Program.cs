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
    new ButtonTemplate(p, config.ClickPoints[Path.GetFileName(p)])).ToArray();
Check(templates.Length == 2, "Both supplied layouts are loaded together");
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
            g.FillRectangle(Brushes.White, template.FileName == "allow_once.png" ? new Rectangle(121, 14, 66, 18) : new Rectangle(132, 46, 75, 20));
        Check(TemplateMatcher.Find(PixelImage.FromBitmap(wrongLabel), Point.Empty, template, new[] { 1.0 }, config.MinimumScore) is null,
            $"{template.FileName}: reject the same layout when the approval label is removed");
    }
    string temporaryImage = Path.Combine(scratch, Guid.NewGuid() + ".png");
    try
    {
        template.Original.Save(temporaryImage);
        using var automatic = new ButtonTemplate(temporaryImage, null);
        Check(automatic.ClickPoint.X > 0 && automatic.ClickPoint.Y > 0, $"{template.FileName}: infer a safe light-button center for new PNGs");
    }
    finally { File.Delete(temporaryImage); }
}
using (var both = new Bitmap(900, 500))
{
    using (var g = Graphics.FromImage(both))
    {
        g.Clear(Color.DimGray);
        g.DrawImageUnscaled(templates[0].Original, 20, 30);
        g.DrawImageUnscaled(templates[1].Original, 400, 250);
    }
    var pixels = PixelImage.FromBitmap(both);
    foreach (var template in templates)
        Check(TemplateMatcher.Find(pixels, Point.Empty, template, new[] { 1.0 }, .96) is not null, "Both layouts can coexist: " + template.FileName);
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
        Check(TemplateMatcher.Find(pixels, Point.Empty, template, config.Scales, .96) is null,
            "Full 1080p desktop rejects unrelated content across every configured scale: " + template.FileName);
    Console.WriteLine($"1080p scan of both layouts / all scales: {scanTime.Elapsed.TotalSeconds:0.00}s");
}
string temporaryIni = Path.Combine(scratch, Guid.NewGuid() + ".ini");
try
{
    File.Copy(Path.Combine(root, "settings.ini"), temporaryIni);
    AppSettings.SaveGeneral(temporaryIni, 1.5, true);
    var saved = AppSettings.Load(temporaryIni);
    Check(saved.IntervalSeconds == 1.5 && saved.StartWithWindows, "INI persists interval and enabled startup");
    Check(saved.ClickPoints.Count == 2 && saved.MinimumScore == config.MinimumScore, "Saving general settings preserves both layout offsets and matching settings");
    AppSettings.SaveGeneral(temporaryIni, 3, false);
    Check(!AppSettings.Load(temporaryIni).StartWithWindows, "INI can disable automatic startup");
    AppSettings.SaveGeneral(temporaryIni, 3, false, false);
    Check(!AppSettings.Load(temporaryIni).MinimizeToTray, "INI can disable minimize to tray");
    AppSettings.SaveGeneral(temporaryIni, 3, false, true);
    Check(AppSettings.Load(temporaryIni).MinimizeToTray, "INI can enable minimize to tray");
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
        using var preview = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(preview, new Rectangle(Point.Empty, preview.Size));
        preview.Save(Path.Combine(scratch, "ui-preview.png"));
    }
    catch (Exception e) { renderError = e; }
});
rendering.SetApartmentState(ApartmentState.STA); rendering.Start(); rendering.Join();
Check(renderError is null, "Settings window renders without starting monitoring: " + renderError?.Message);
Console.WriteLine($"{checks} checks passed in {stopwatch.Elapsed.TotalSeconds:0.00}s. No screen captures, registry changes, or real clicks were performed.");
