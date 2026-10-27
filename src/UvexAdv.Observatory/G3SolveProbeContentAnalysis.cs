namespace UvexAdv.Observatory;

/// <summary>
/// Read-only content assessment for a solve-only G3 exposure.  It does not try
/// to infer a target or cloud morphology.  Its sole safety purpose is to
/// distinguish a frame containing at least one spatially coherent astronomical
/// source from a frame that cannot justify moving the mount after a failed WCS.
/// </summary>
public sealed record G3SolveProbeContentAssessment(
    GateResult Gate,
    G3StellarFocusMeasurement StellarMeasurement,
    double BackgroundMedianAdu,
    double BackgroundNoiseSigmaAdu,
    double RobustDynamicRangeSigma,
    double SaturatedPixelFraction)
{
    // Local background witnesses authorize only the existing failed-solve
    // recovery branch. They are NOT a target centroid, focus metric or WCS.
    public int LocalBackgroundSourceCount { get; init; }
    public int CoherentSourceCount => Math.Max(StellarMeasurement.DetectedStarCount, LocalBackgroundSourceCount);
    public bool HasCoherentSource => CoherentSourceCount > 0;
    public double UpperBackgroundAdu { get; init; }
}

public static class G3SolveProbeContentAnalyzer
{
    // A compact saturated target can still establish real spatial structure,
    // but widespread clipping cannot distinguish stars, background or optical
    // gradients and must never authorize a neighbouring-field mount search.
    // Ten percent is deliberately far above an ordinary saturated stellar core
    // while still rejecting detector-wide sky/background clipping.
    public const double MaximumSearchAuthorizingSaturatedPixelFraction = 0.10d;
    public const string OverexposedGateCode = "G3_SOLVE_PROBE_OVEREXPOSED";

    public static G3SolveProbeContentAssessment Analyze(MonochromeFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var samples = new List<double>(Math.Max(256, frame.Width * frame.Height / 64));
        var saturated = 0;
        var sampled = 0;
        for (var y = 0; y < frame.Height; y += 8)
        for (var x = 0; x < frame.Width; x += 8)
        {
            var value = frame[x, y];
            samples.Add(value);
            sampled++;
            if (value >= frame.SaturationLevel) saturated++;
        }

        samples.Sort();
        var background = Percentile(samples, 0.5);
        var deviations = samples
            .Select(value => Math.Abs(value - background))
            .OrderBy(value => value)
            .ToArray();
        var sigma = Math.Max(1, Percentile(deviations, 0.5) * 1.4826);
        var high = Percentile(samples, 0.995);
        var dynamicRangeSigma = Math.Max(0, (high - background) / sigma);
        var saturatedFraction = sampled == 0 ? 0 : saturated / (double)sampled;
        var stellar = G3StellarFocusAnalyzer.Analyze(frame);
        var localCount = stellar.DetectedStarCount == 0 &&
            saturatedFraction < MaximumSearchAuthorizingSaturatedPixelFraction
            ? CountLocalSources(frame) : 0;
        var coherentCount = Math.Max(stellar.DetectedStarCount, localCount);
        var metrics = new Dictionary<string, double>
        {
            ["coherentSourceCount"] = coherentCount,
            ["globalBackgroundSourceCount"] = stellar.DetectedStarCount,
            ["localBackgroundSourceCount"] = localCount,
            ["usableSourceCount"] = stellar.StarCount,
            ["medianSignalToNoise"] = FiniteOrZero(stellar.MedianSignalToNoise),
            ["backgroundMedianAdu"] = background,
            ["backgroundNoiseSigmaAdu"] = sigma,
            ["robustDynamicRangeSigma"] = dynamicRangeSigma,
            ["sampledSaturatedPixelFraction"] = saturatedFraction,
        };

        // Saturated/broad sources still establish real spatial structure when
        // clipping remains local. Widespread clipping takes precedence over a
        // morphology detection because it destroys the field evidence needed
        // to justify a search move.
        var gate = saturatedFraction >= MaximumSearchAuthorizingSaturatedPixelFraction
            ? GateResult.Unknown(
                OverexposedGateCode,
                $"The solve-only G3 frame is overexposed: {saturatedFraction:P1} of sampled pixels reached the detector plateau, at or above the {MaximumSearchAuthorizingSaturatedPixelFraction:P0} no-motion limit. The frame cannot authorize a longer exposure or neighbouring-field mount search.",
                metrics)
            : coherentCount > 0
            ? GateResult.Pass(
                "G3_SOLVE_PROBE_STRUCTURED_FIELD",
                $"The solve-only G3 frame contains spatially coherent sources (global {stellar.DetectedStarCount}, local-background {localCount}); a failed WCS may use only the configured bounded recovery path. This is not target identification or a focus measurement.",
                metrics)
            : GateResult.Unknown(
                "G3_CLOUD_OR_TRANSPARENCY_INVALID",
                "The solve-only G3 frame contains no spatially coherent source. An empty field cannot be distinguished safely from cloud or lost transparency, so this exposure does not authorize mount search motion.",
                metrics);

        return new G3SolveProbeContentAssessment(
            gate,
            stellar,
            background,
            sigma,
            dynamicRangeSigma,
            saturatedFraction) { UpperBackgroundAdu = Percentile(samples, 0.90), LocalBackgroundSourceCount = localCount };
    }

    private static int CountLocalSources(MonochromeFrame frame)
    {
        // Same raw-pixel/core/shape rules, but estimate the background within
        // overlapping 128px windows: moonlight/vignetting must not masquerade
        // as read noise over the entire detector. Overlap exceeds both edge
        // margins, so a source on a tile seam is not lost. Never change raw
        // pixels or feed these cropped measurements into target/focus control.
        const int size = 128, stride = 96;
        if (frame.Width <= size && frame.Height <= size) return 0;
        var centers = new List<PixelPoint>();
        var structuredWithoutUsableStars = false;
        for (var top = 0; top < frame.Height - 28; top += stride)
        for (var left = 0; left < frame.Width - 28; left += stride)
        {
            var width = Math.Min(size, frame.Width - left);
            var height = Math.Min(size, frame.Height - top);
            var pixels = new ushort[width * height];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++) pixels[y * width + x] = frame[left + x, top + y];
            // A median over nine pixels is not a single-pixel peak. Requiring
            // 4 times single-pixel noise erased real moonlit sparse stars.
            // Keep the 3.5-sigma peak and all existing spatial/finite tests;
            // this local 3x3-core witness does not relax focus or identity gates.
            var local = G3StellarFocusAnalyzer.Analyze(new MonochromeFrame(width, height, pixels, frame.SaturationLevel),
                new G3StellarFocusAnalysisOptions(MinimumCoreMedianProminenceSigma: 2.5));
            structuredWithoutUsableStars |= local.DetectedStarCount > 0 && local.Stars.Count == 0;
            foreach (var star in local.Stars)
            {
                var center = new PixelPoint(left + star.Centroid.X, top + star.Centroid.Y);
                if (centers.All(p => Math.Pow(p.X - center.X, 2) + Math.Pow(p.Y - center.Y, 2) >= 49)) centers.Add(center);
            }
        }
        return Math.Max(centers.Count, structuredWithoutUsableStars ? 1 : 0);
    }

    private static double Percentile(IReadOnlyList<double> sorted, double fraction)
    {
        if (sorted.Count == 0) return 0;
        var index = Math.Clamp((int)Math.Round((sorted.Count - 1) * fraction), 0, sorted.Count - 1);
        return sorted[index];
    }

    private static double FiniteOrZero(double value) => double.IsFinite(value) ? value : 0;
}
