namespace UvexAdv.Observatory;

/// <summary>
/// Same-frame dark-aperture check for an independently authorized slit seed.
/// Pair each slit sample with its own two flanks so illumination gradients
/// along the slit do not masquerade as random detector noise. This is not a
/// blind slit finder: search is capped at one slit width and one degree.
/// </summary>
public static class SlitLocalBackgroundDetector
{
    public static SlitLocusDetection Detect(
        MonochromeFrame frame, SlitGeometry authorizedSeed,
        double maximumPerpendicularSearchPixels, double maximumAngleSearchDegrees,
        double minimumContrastSigma)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(authorizedSeed);
        if (!double.IsFinite(minimumContrastSigma) || minimumContrastSigma <= 0 ||
            !double.IsFinite(maximumPerpendicularSearchPixels) || maximumPerpendicularSearchPixels < 0 ||
            !double.IsFinite(maximumAngleSearchDegrees) || maximumAngleSearchDegrees < 0 ||
            !double.IsFinite(authorizedSeed.AcquisitionPoint.X) || !double.IsFinite(authorizedSeed.AcquisitionPoint.Y) ||
            !double.IsFinite(authorizedSeed.AngleDegrees) ||
            !double.IsFinite(authorizedSeed.WidthPixels) || authorizedSeed.WidthPixels <= 0 ||
            !double.IsFinite(authorizedSeed.LengthPixels) || authorizedSeed.LengthPixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(minimumContrastSigma));

        var radius = Math.Floor(Math.Min(maximumPerpendicularSearchPixels, authorizedSeed.WidthPixels));
        var angleRadius = Math.Floor(Math.Min(maximumAngleSearchDegrees, 1));
        var bestScore = 0d;
        var bestOffset = 0d;
        var bestAngle = 0d;
        var bestSupport = 0d;
        var bestCount = 0;
        for (var da = -angleRadius; da <= angleRadius; da++)
        for (var offset = -radius; offset <= radius; offset++)
        {
            var (score, support, count) = Score(frame, authorizedSeed, offset, da);
            if (!double.IsFinite(score) || score <= bestScore) continue;
            bestScore = score;
            bestOffset = offset;
            bestAngle = da;
            bestSupport = support;
            bestCount = count;
        }
        var angle = (authorizedSeed.AngleDegrees + bestAngle) * Math.PI / 180;
        var geometry = authorizedSeed with
        {
            AcquisitionPoint = new PixelPoint(
                authorizedSeed.AcquisitionPoint.X - Math.Sin(angle) * bestOffset,
                authorizedSeed.AcquisitionPoint.Y + Math.Cos(angle) * bestOffset),
            AngleDegrees = authorizedSeed.AngleDegrees + bestAngle,
            UncertaintyPixels = Math.Max(authorizedSeed.UncertaintyPixels, 1),
        };
        var metrics = new Dictionary<string, double>
        {
            ["slitContrastSigma"] = bestScore,
            ["perpendicularOffsetPixels"] = bestOffset,
            ["angleOffsetDegrees"] = bestAngle,
            ["localBackgroundNormalization"] = 1,
            ["pairedSampleCount"] = bestCount,
            ["positiveDarkContrastFraction"] = bestSupport,
            ["minimumContrastSigma"] = minimumContrastSigma,
            ["maximumSeedOffsetPixels"] = radius,
        };
        var gate = bestScore >= minimumContrastSigma
            ? GateResult.Pass("SLIT_LOCUS_LOCAL_BACKGROUND_DETECTED",
                $"The current frame's paired-flank dark aperture passed at {bestScore:F2}σ with {bestSupport:P0} positive samples; search stayed within one authorized slit width and one degree.", metrics)
            : GateResult.Unknown("SLIT_LOCUS_LOCAL_BACKGROUND_LOW_CONFIDENCE",
                "Paired local backgrounds did not establish the authorized dark aperture at the unchanged contrast threshold.", metrics);
        return new SlitLocusDetection(gate, geometry, bestScore, bestOffset, bestAngle);
    }

    private static (double Score, double Support, int Count) Score(
        MonochromeFrame frame, SlitGeometry seed, double offset, double da)
    {
        var angle = (seed.AngleDegrees + da) * Math.PI / 180;
        var vx = Math.Cos(angle); var vy = Math.Sin(angle);
        var nx = -vy; var ny = vx;
        var cx = seed.AcquisitionPoint.X + nx * offset;
        var cy = seed.AcquisitionPoint.Y + ny * offset;
        var halfLength = Math.Min(seed.LengthPixels / 2,
            Math.Sqrt((double)frame.Width * frame.Width + (double)frame.Height * frame.Height));
        var halfWidth = Math.Max(1, seed.WidthPixels / 2);
        var guard = Math.Max(8, seed.WidthPixels * 3);
        var contrasts = new List<double>();
        var leftContrasts = new List<double>();
        var rightContrasts = new List<double>();
        var proposedCount = 0;
        for (var along = -halfLength; along <= halfLength; along += 3)
        {
            proposedCount++;
            var x = cx + vx * along; var y = cy + vy * along;
            if (!Read(x - nx * guard, y - ny * guard, out var left) ||
                !Read(x + nx * guard, y + ny * guard, out var right)) continue;
            var on = new List<double>();
            var complete = true;
            for (var across = -halfWidth; across <= halfWidth; across++)
            {
                if (!Read(x + nx * across, y + ny * across, out var value)) { complete = false; break; }
                on.Add(value);
            }
            if (!complete || on.Count < 2) continue;
            var center = Median(on);
            contrasts.Add((left + right) / 2 - center);
            leftContrasts.Add(left - center);
            rightContrasts.Add(right - center);
        }
        if (contrasts.Count < 20 || contrasts.Count < proposedCount * 2d / 3)
            return (0, 0, contrasts.Count);
        var support = contrasts.Count(value => value > 0) / (double)contrasts.Count;
        // A single bright flank is not a dark slit. Both sides must show a
        // positive median depression, and at least 2/3 of paired samples agree.
        if (support < 2d / 3 || Median(leftContrasts) <= 0 || Median(rightContrasts) <= 0)
            return (0, support, contrasts.Count);
        var median = Median(contrasts);
        var mad = Median(contrasts.Select(value => Math.Abs(value - median)));
        // Four samples per effective independent element, matching the
        // conservative oversampling convention of the ordinary detector.
        var sigma = Math.Max(0.25, 1.4826 * mad / Math.Sqrt(contrasts.Count / 4d));
        return (median / sigma, support, contrasts.Count);

        bool Read(double x, double y, out double value)
        {
            var ix = (int)Math.Round(x); var iy = (int)Math.Round(y);
            value = 0;
            if (ix < 0 || ix >= frame.Width || iy < 0 || iy >= frame.Height) return false;
            value = frame[ix, iy];
            return value < frame.SaturationLevel;
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        var center = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[center - 1] + sorted[center]) / 2 : sorted[center];
    }
}
