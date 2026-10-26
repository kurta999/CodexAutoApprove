using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CodexAutoApprove;

public sealed class PixelImage
{
    public int Width { get; }
    public int Height { get; }
    public int[] Pixels { get; }

    public PixelImage(int width, int height, int[] pixels)
    {
        Width = width; Height = height; Pixels = pixels;
    }

    public static PixelImage FromBitmap(Bitmap bitmap)
    {
        using var converted = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(converted)) g.DrawImageUnscaled(bitmap, 0, 0);
        var data = converted.LockBits(new Rectangle(0, 0, converted.Width, converted.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new int[converted.Width * converted.Height];
            for (int y = 0; y < converted.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * converted.Width, converted.Width);
            return new(converted.Width, converted.Height, pixels);
        }
        finally { converted.UnlockBits(data); }
    }
}

public sealed record ButtonMatch(string FileName, Rectangle Bounds, Point ClickPoint, double Score, double Scale);

public sealed class ButtonTemplate : IDisposable
{
    public string FileName { get; }
    public Bitmap Original { get; }
    public Point ClickPoint { get; }
    private readonly Dictionary<Size, PreparedTemplate> cache = new();

    public ButtonTemplate(string path, Point? clickPoint)
    {
        FileName = Path.GetFileName(path);
        using var source = new Bitmap(path);
        Original = new Bitmap(source);
        ClickPoint = clickPoint ?? DetectLightButton(PixelImage.FromBitmap(Original));
        if (ClickPoint.X < 0 || ClickPoint.Y < 0 || ClickPoint.X >= Original.Width || ClickPoint.Y >= Original.Height)
            throw new FormatException($"Click coordinates for {FileName} are outside its {Original.Width}×{Original.Height} image.");
    }

    public PreparedTemplate AtScale(double scale)
    {
        var size = new Size(Math.Max(4, (int)Math.Round(Original.Width * scale)), Math.Max(4, (int)Math.Round(Original.Height * scale)));
        if (cache.TryGetValue(size, out var prepared)) return prepared;
        using var resized = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            if (size == Original.Size) g.DrawImageUnscaled(Original, 0, 0);
            else g.DrawImage(Original, new Rectangle(Point.Empty, size));
        }
        prepared = new(PixelImage.FromBitmap(resized));
        cache[size] = prepared;
        return prepared;
    }

    private static Point DetectLightButton(PixelImage image)
    {
        bool Light(int pixel) => ((pixel >> 16) & 255) >= 225 && ((pixel >> 8) & 255) >= 225 && (pixel & 255) >= 225;
        var seen = new bool[image.Pixels.Length];
        int largest = 0;
        Rectangle button = Rectangle.Empty;
        for (int i = 0; i < seen.Length; i++)
        {
            if (seen[i] || !Light(image.Pixels[i])) continue;
            var queue = new Queue<int>(); queue.Enqueue(i); seen[i] = true;
            int count = 0, minX = image.Width, maxX = 0, minY = image.Height, maxY = 0;
            while (queue.TryDequeue(out int p))
            {
                int x = p % image.Width, y = p / image.Width;
                count++; minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                void Visit(int q) { if (!seen[q] && Light(image.Pixels[q])) { seen[q] = true; queue.Enqueue(q); } }
                if (x > 0) Visit(p - 1); if (x + 1 < image.Width) Visit(p + 1);
                if (y > 0) Visit(p - image.Width); if (y + 1 < image.Height) Visit(p + image.Width);
            }
            if (count > largest && maxX - minX > 25 && maxY - minY > 8)
            {
                largest = count; button = Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
            }
        }
        if (button.IsEmpty)
            throw new FormatException($"Cannot infer the approval button. Add a [Template:filename.png] section with ClickX and ClickY in settings.ini.");
        return new(button.Left + (int)(button.Width * .45), button.Top + button.Height / 2);
    }

    public void Dispose() { Original.Dispose(); cache.Clear(); }
}

public sealed class PreparedTemplate
{
    public PixelImage Image { get; }
    public Point[] Features { get; }
    public Point[] LabelFeatures { get; }
    public Point[] Samples { get; }

    public PreparedTemplate(PixelImage image)
    {
        Image = image;
        var edges = new List<(Point Point, int Contrast)>();
        for (int y = 1; y < image.Height - 1; y++)
            for (int x = 1; x < image.Width - 1; x++)
            {
                int p = y * image.Width + x;
                int contrast = Math.Max(Difference(image.Pixels[p], image.Pixels[p + 1]), Difference(image.Pixels[p], image.Pixels[p + image.Width]));
                if (contrast > 80) edges.Add((new(x, y), contrast));
            }
        var selected = new List<Point>();
        foreach (var edge in edges.OrderByDescending(e => e.Contrast))
        {
            if (selected.All(p => Math.Abs(p.X - edge.Point.X) + Math.Abs(p.Y - edge.Point.Y) > 4)) selected.Add(edge.Point);
            if (selected.Count == 24) break;
        }
        if (selected.Count < 6) throw new FormatException("A reference PNG has too little detail for reliable button matching.");
        // Explicitly verify dark lettering inside the light approval button. A whole-image
        // score alone can hide a wrong/missing label because most pixels are background.
        var lettering = new List<(Point Point, int Contrast)>();
        for (int y = 2; y < image.Height - 2; y++)
            for (int x = 2; x < image.Width - 2; x++)
            {
                int pixel = image.Pixels[y * image.Width + x];
                int brightness = (((pixel >> 16) & 255) + ((pixel >> 8) & 255) + (pixel & 255)) / 3;
                if (brightness > 190) continue;
                int lightNeighbors = 0;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int neighbor = image.Pixels[(y + dy) * image.Width + x + dx];
                        if (((neighbor >> 16) & 255) > 220 && ((neighbor >> 8) & 255) > 220 && (neighbor & 255) > 220) lightNeighbors++;
                    }
                if (lightNeighbors >= 13) lettering.Add((new(x, y), 255 - brightness));
            }
        var label = new List<Point>();
        foreach (var letter in lettering.OrderByDescending(p => p.Contrast))
        {
            if (label.All(p => Math.Abs(p.X - letter.Point.X) + Math.Abs(p.Y - letter.Point.Y) > 2)) label.Add(letter.Point);
            if (label.Count >= 32) break;
        }
        LabelFeatures = label.ToArray();
        Features = label.Concat(selected).Distinct().ToArray();
        var samples = new List<Point>(selected);
        for (int y = 1; y < image.Height; y += Math.Max(1, image.Height / 9))
            for (int x = 1; x < image.Width; x += Math.Max(1, image.Width / 18)) samples.Add(new(x, y));
        Samples = samples.ToArray();
    }

    internal static int Difference(int a, int b) => Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)) +
        Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) + Math.Abs((a & 255) - (b & 255));
}

public static class TemplateMatcher
{
    // A coarse diagnostic search gives a useful similarity even when the click matcher
    // rejects every location. Scores are measured over ALL pixels of a real candidate;
    // diagnostic candidates never authorize a click.
    public static double? MeasureCandidate(PixelImage screen, ButtonTemplate template,
        IEnumerable<double> scales, CancellationToken cancellationToken = default)
    {
        double? bestScore = null;
        foreach (double scale in scales.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prepared = template.AtScale(scale);
            var reference = prepared.Image;
            int maxX = screen.Width - reference.Width, maxY = screen.Height - reference.Height;
            if (maxX < 0 || maxY < 0) continue;
            var samples = prepared.Samples.Where((_, i) => i % 4 == 0).Concat(prepared.LabelFeatures.Take(8)).Distinct().ToArray();
            int[] offsets = samples.Select(p => p.Y * screen.Width + p.X).ToArray();
            int[] values = samples.Select(p => reference.Pixels[p.Y * reference.Width + p.X]).ToArray();
            int bestError = int.MaxValue;
            Point candidate = Point.Empty;
            void Consider(int x, int y)
            {
                int start = y * screen.Width + x, error = 0;
                for (int s = 0; s < offsets.Length; s++)
                {
                    error += PreparedTemplate.Difference(screen.Pixels[start + offsets[s]], values[s]);
                    if (error >= bestError) return;
                }
                bestError = error; candidate = new(x, y);
            }
            const int step = 8;
            for (int y = 0; ; y = Math.Min(maxY, y + step))
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int x = 0; ; x = Math.Min(maxX, x + step))
                {
                    Consider(x, y);
                    if (x == maxX) break;
                }
                if (y == maxY) break;
            }
            Point coarse = candidate;
            for (int y = Math.Max(0, coarse.Y - step + 1); y <= Math.Min(maxY, coarse.Y + step - 1); y++)
                for (int x = Math.Max(0, coarse.X - step + 1); x <= Math.Min(maxX, coarse.X + step - 1); x++) Consider(x, y);
            long fullError = 0;
            for (int y = 0; y < reference.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int start = (candidate.Y + y) * screen.Width + candidate.X;
                for (int x = 0; x < reference.Width; x++)
                    fullError += PreparedTemplate.Difference(screen.Pixels[start + x], reference.Pixels[y * reference.Width + x]);
            }
            double score = 1 - fullError / (765.0 * reference.Width * reference.Height);
            if (!bestScore.HasValue || score > bestScore) bestScore = score;
        }
        return bestScore;
    }

    public static ButtonMatch? Find(PixelImage screen, Point screenOrigin, ButtonTemplate template,
        IEnumerable<double> scales, double minimumScore, CancellationToken cancellationToken = default)
    {
        ButtonMatch? best = null;
        foreach (double scale in scales.Distinct().OrderBy(s => Math.Abs(s - 1)))
        {
            var prepared = template.AtScale(scale);
            var reference = prepared.Image;
            int maxX = screen.Width - reference.Width, maxY = screen.Height - reference.Height;
            if (maxX < 0 || maxY < 0) continue;
            int[] referenceFeatures = prepared.Features.Select(p => reference.Pixels[p.Y * reference.Width + p.X]).ToArray();
            int[] featureOffsets = prepared.Features.Select(p => p.Y * screen.Width + p.X).ToArray();
            double featureBudget = featureOffsets.Length * Math.Max(65, (1 - minimumScore) * 765);
            int[] labelValues = prepared.LabelFeatures.Select(p => reference.Pixels[p.Y * reference.Width + p.X]).ToArray();
            int[] labelOffsets = prepared.LabelFeatures.Select(p => p.Y * screen.Width + p.X).ToArray();
            int[] referenceSamples = prepared.Samples.Select(p => reference.Pixels[p.Y * reference.Width + p.X]).ToArray();
            int[] sampleOffsets = prepared.Samples.Select(p => p.Y * screen.Width + p.X).ToArray();
            for (int y = 0; y <= maxY; y++)
            {
                if (y % 32 == 0) cancellationToken.ThrowIfCancellationRequested();
                for (int x = 0; x <= maxX; x++)
                {
                    int start = y * screen.Width + x;
                    if (PreparedTemplate.Difference(screen.Pixels[start + featureOffsets[0]], referenceFeatures[0]) > 150 ||
                        PreparedTemplate.Difference(screen.Pixels[start + featureOffsets[1]], referenceFeatures[1]) > 150) continue;
                    int featureError = 0;
                    bool rejected = false;
                    for (int f = 0; f < featureOffsets.Length; f++)
                    {
                        featureError += PreparedTemplate.Difference(screen.Pixels[start + featureOffsets[f]], referenceFeatures[f]);
                        if (featureError > featureBudget) { rejected = true; break; }
                    }
                    if (rejected) continue;
                    int labelError = 0;
                    for (int l = 0; l < labelOffsets.Length; l++)
                    {
                        labelError += PreparedTemplate.Difference(screen.Pixels[start + labelOffsets[l]], labelValues[l]);
                        if (labelError > labelOffsets.Length * 90) break;
                    }
                    if (labelError > labelOffsets.Length * 90) continue;
                    double cutoff = Math.Max(minimumScore, best?.Score ?? 0);
                    double sampleBudget = (1 - cutoff) * 765 * sampleOffsets.Length;
                    int sampleError = 0;
                    for (int s = 0; s < sampleOffsets.Length; s++)
                    {
                        sampleError += PreparedTemplate.Difference(screen.Pixels[start + sampleOffsets[s]], referenceSamples[s]);
                        if (sampleError > sampleBudget) break;
                    }
                    if (sampleError > sampleBudget) continue;
                    // Verify all pixels, including the label, before accepting a match.
                    long fullError = 0;
                    double budget = (1 - cutoff) * 765 * reference.Width * reference.Height;
                    for (int ry = 0; ry < reference.Height && fullError <= budget; ry++)
                        for (int rx = 0; rx < reference.Width; rx++)
                            fullError += PreparedTemplate.Difference(screen.Pixels[start + ry * screen.Width + rx], reference.Pixels[ry * reference.Width + rx]);
                    double score = 1 - fullError / (765.0 * reference.Width * reference.Height);
                    if (score < cutoff) continue;
                    var location = new Point(screenOrigin.X + x, screenOrigin.Y + y);
                    var click = new Point(location.X + (int)Math.Round(template.ClickPoint.X * reference.Width / (double)template.Original.Width),
                        location.Y + (int)Math.Round(template.ClickPoint.Y * reference.Height / (double)template.Original.Height));
                    best = new(template.FileName, new(location, new(reference.Width, reference.Height)), click, score, scale);
                    if (score > .99999) return best;
                }
            }
        }
        return best;
    }
}
