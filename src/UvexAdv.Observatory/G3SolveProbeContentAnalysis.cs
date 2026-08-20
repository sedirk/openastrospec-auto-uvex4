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
    public bool HasCoherentSource => StellarMeasurement.DetectedStarCount > 0;
}

public static class G3SolveProbeContentAnalyzer
{
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
        var metrics = new Dictionary<string, double>
        {
            ["coherentSourceCount"] = stellar.DetectedStarCount,
            ["usableSourceCount"] = stellar.StarCount,
            ["medianSignalToNoise"] = FiniteOrZero(stellar.MedianSignalToNoise),
            ["backgroundMedianAdu"] = background,
            ["backgroundNoiseSigmaAdu"] = sigma,
            ["robustDynamicRangeSigma"] = dynamicRangeSigma,
            ["sampledSaturatedPixelFraction"] = saturatedFraction,
        };

        // Saturated/broad sources still establish real spatial structure.  They
        // may enter the deterministic bright-target branch, but never focus.
        var gate = stellar.DetectedStarCount > 0
            ? GateResult.Pass(
                "G3_SOLVE_PROBE_STRUCTURED_FIELD",
                $"The solve-only G3 frame contains {stellar.DetectedStarCount} spatially coherent source(s); a failed WCS may use only the configured bounded recovery path.",
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
            saturatedFraction);
    }

    private static double Percentile(IReadOnlyList<double> sorted, double fraction)
    {
        if (sorted.Count == 0) return 0;
        var index = Math.Clamp((int)Math.Round((sorted.Count - 1) * fraction), 0, sorted.Count - 1);
        return sorted[index];
    }

    private static double FiniteOrZero(double value) => double.IsFinite(value) ? value : 0;
}
