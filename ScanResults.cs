namespace CodexAutoApprove;

public sealed record ScreenFrame(PixelImage Pixels, Rectangle Bounds, double[] Scales);

public sealed record ImageScanResult(string FileName, ButtonMatch? Match, double? Score)
{
    public string Describe(double minimumScore, bool cooldown)
    {
        string percentage = Score is double score ? $"{score * 100:0.0}%" : "N/A (reference does not fit)";
        string outcome = Match is not null ? "match accepted" : "no verified match";
        if (Match is null && Score.HasValue) outcome += " (diagnostic candidate)";
        if (cooldown) outcome += "; click cooldown";
        return $"{FileName}: {percentage} | threshold {minimumScore * 100:0.0}% | {outcome}";
    }
}

public static class ScanAnalyzer
{
    public static IEnumerable<ImageScanResult> Analyze(IReadOnlyList<ScreenFrame> frames,
        IEnumerable<ButtonTemplate> templates, double minimumScore, CancellationToken token = default)
    {
        foreach (var template in templates)
        {
            token.ThrowIfCancellationRequested();
            ButtonMatch? bestMatch = null;
            foreach (var frame in frames)
            {
                var match = TemplateMatcher.Find(frame.Pixels, frame.Bounds.Location, template, frame.Scales, minimumScore, token);
                if (match is not null && (bestMatch is null || match.Score > bestMatch.Score)) bestMatch = match;
            }
            double? score = bestMatch?.Score;
            if (bestMatch is null)
                foreach (var frame in frames)
                {
                    double? candidate = TemplateMatcher.MeasureCandidate(frame.Pixels, template, frame.Scales, token);
                    if (candidate.HasValue && (!score.HasValue || candidate > score)) score = candidate;
                }
            yield return new(template.FileName, bestMatch, score);
        }
    }
}
