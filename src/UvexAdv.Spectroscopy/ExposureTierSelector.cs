namespace UvexAdv.Spectroscopy;

public sealed record SpectralProbeMetrics(
    double ExposureSeconds,
    double BiasLevelAdu,
    double HighPercentileAdu,
    double FullScaleAdu,
    double SaturatedFraction,
    double ContinuumSnrPerResolutionElement,
    double LineSnrPerResolutionElement,
    double TargetToSkyContrast,
    bool GuidingStable,
    string SourceFrameId,
    double TraceSaturatedFraction = 0,
    double ClippedDispersionColumnFraction = 0,
    int LongestClippedDispersionColumnRun = 0,
    int TraceSpatialCenterPixel = -1,
    int TraceSpatialHalfWidthPixels = 0);

public sealed record ExposureTierOptions(
    IReadOnlyList<double> ExposureLadderSeconds,
    double TargetFullScaleFraction = 0.65,
    double MaximumFullScaleFraction = 0.82,
    double MaximumSaturatedFraction = 0.001,
    double MaximumTraceSaturatedFraction = 0.001,
    double MaximumClippedDispersionColumnFraction = 0.002,
    int MaximumConsecutiveClippedDispersionColumns = 3,
    double MinimumTargetToSkyContrast = 1.15,
    double MinimumContinuumSnr = 5,
    double MinimumLineSnr = 8)
{
    public static ExposureTierOptions Default { get; } = new(
        [0.1, 0.3, 1, 3, 10, 30, 60, 120, 300, 600]);
}

public sealed record ExposureTierDecision(
    bool Accepted,
    double SelectedExposureSeconds,
    double PredictedFullScaleFraction,
    string Code,
    string Reason,
    IReadOnlyDictionary<string, double> Metrics);

public static class ExposureTierSelector
{
    public static ExposureTierDecision Select(SpectralProbeMetrics probe, ExposureTierOptions? options = null)
    {
        options ??= ExposureTierOptions.Default;
        var ladder = options.ExposureLadderSeconds
            .Where(value => double.IsFinite(value) && value > 0)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
        if (ladder.Length == 0) throw new ArgumentException("Exposure ladder contains no positive finite values.", nameof(options));
        if (!double.IsFinite(probe.ExposureSeconds) || probe.ExposureSeconds <= 0) return Reject("PROBE_EXPOSURE_INVALID", "Probe exposure is not positive.", ladder[0], double.NaN, probe);
        if (!double.IsFinite(probe.BiasLevelAdu) || !double.IsFinite(probe.HighPercentileAdu) ||
            !double.IsFinite(probe.FullScaleAdu) || probe.FullScaleAdu <= probe.BiasLevelAdu)
        {
            return Reject("PROBE_RANGE_INVALID", "Probe full-scale, percentile, and bias values must be finite and ordered.", ladder[0], double.NaN, probe);
        }
        if (!double.IsFinite(probe.SaturatedFraction) || probe.SaturatedFraction is < 0 or > 1)
        {
            return Reject("PROBE_SATURATION_INVALID", "Probe saturated fraction must be finite and within [0, 1].", ladder[0], double.NaN, probe);
        }
        if (!double.IsFinite(probe.TraceSaturatedFraction) || probe.TraceSaturatedFraction is < 0 or > 1 ||
            !double.IsFinite(probe.ClippedDispersionColumnFraction) || probe.ClippedDispersionColumnFraction is < 0 or > 1 ||
            probe.LongestClippedDispersionColumnRun < 0)
        {
            return Reject("PROBE_TRACE_SATURATION_INVALID", "Trace saturation metrics must be finite and within their valid ranges.", ladder[0], double.NaN, probe);
        }
        if (!double.IsFinite(probe.ContinuumSnrPerResolutionElement) || probe.ContinuumSnrPerResolutionElement < 0 ||
            !double.IsFinite(probe.LineSnrPerResolutionElement) || probe.LineSnrPerResolutionElement < 0)
        {
            return Reject("PROBE_SNR_INVALID", "Probe continuum and line SNR values must be finite and non-negative.", ladder[0], double.NaN, probe);
        }
        if (!double.IsFinite(probe.TargetToSkyContrast) || probe.TargetToSkyContrast < 0)
        {
            return Reject("PROBE_CONTRAST_INVALID", "Probe target/sky contrast must be finite and non-negative.", ladder[0], double.NaN, probe);
        }
        if (!probe.GuidingStable) return Reject("GUIDING_UNSTABLE", "Probe was acquired while guiding was unstable.", probe.ExposureSeconds, double.NaN, probe);
        if (probe.TargetToSkyContrast < options.MinimumTargetToSkyContrast) return Reject("TARGET_SKY_CONTRAST_LOW", $"Target/sky contrast {probe.TargetToSkyContrast:F2} is below {options.MinimumTargetToSkyContrast:F2}.", probe.ExposureSeconds, double.NaN, probe);

        var usableRange = probe.FullScaleAdu - probe.BiasLevelAdu;
        var signal = Math.Max(0, probe.HighPercentileAdu - probe.BiasLevelAdu);
        var observedFraction = signal / usableRange;
        var traceClipped = probe.TraceSaturatedFraction > options.MaximumTraceSaturatedFraction ||
            probe.ClippedDispersionColumnFraction > options.MaximumClippedDispersionColumnFraction ||
            probe.LongestClippedDispersionColumnRun > options.MaximumConsecutiveClippedDispersionColumns;
        if (probe.SaturatedFraction > options.MaximumSaturatedFraction || traceClipped || observedFraction >= options.MaximumFullScaleFraction)
        {
            var ratio = observedFraction > 0 ? options.TargetFullScaleFraction / observedFraction : 0.25;
            var desired = probe.ExposureSeconds * Math.Clamp(ratio, 0.02, 0.95);
            var safeBackoffTiers = ladder
                .Where(value => value < probe.ExposureSeconds)
                .Select(exposure => (Exposure: exposure, Predicted: observedFraction * exposure / probe.ExposureSeconds))
                .Where(item => double.IsFinite(item.Predicted) && item.Predicted <= options.MaximumFullScaleFraction)
                .ToArray();
            if (safeBackoffTiers.Length == 0)
            {
                var shortestPrediction = observedFraction * ladder[0] / probe.ExposureSeconds;
                return Reject(
                    "NO_SAFE_SATURATION_BACKOFF",
                    "The probe clipped and no shorter configured tier is predicted to remain below the safety ceiling.",
                    ladder[0],
                    shortestPrediction,
                    probe);
            }

            var preferred = safeBackoffTiers.Where(item => item.Exposure <= desired).ToArray();
            var selectedBackoff = preferred.Length > 0
                ? preferred.MaxBy(static item => item.Exposure)
                : safeBackoffTiers.MinBy(static item => item.Exposure);
            return Accept(
                "SATURATION_BACKOFF",
                traceClipped
                    ? $"Spectral trace clipped {probe.ClippedDispersionColumnFraction:P2} of wavelength columns (longest run {probe.LongestClippedDispersionColumnRun}); selected lower tier {selectedBackoff.Exposure:G4} s for a fresh probe."
                    : $"Probe was near clipping; selected bounded lower tier {selectedBackoff.Exposure:G4} s for a fresh probe.",
                selectedBackoff.Exposure,
                selectedBackoff.Predicted,
                probe);
        }

        if (observedFraction <= 0)
        {
            return Reject("NO_POSITIVE_SPECTRAL_SIGNAL", "Bias-subtracted spectral percentile is not positive.", ladder[0], 0, probe);
        }

        var targetExposure = probe.ExposureSeconds * options.TargetFullScaleFraction / observedFraction;
        var safeTiers = ladder
            .Select(exposure => (Exposure: exposure, Predicted: observedFraction * exposure / probe.ExposureSeconds))
            .Where(item => item.Predicted <= options.MaximumFullScaleFraction)
            .ToArray();
        if (safeTiers.Length == 0)
        {
            return Reject("NO_SAFE_EXPOSURE_TIER", "Even the shortest exposure tier is predicted to clip.", ladder[0], observedFraction * ladder[0] / probe.ExposureSeconds, probe);
        }

        var selected = safeTiers
            .OrderBy(item => Math.Abs(Math.Log(item.Exposure / targetExposure)))
            .ThenByDescending(item => item.Exposure)
            .First();
        var snrLimited = probe.ContinuumSnrPerResolutionElement < options.MinimumContinuumSnr
            && probe.LineSnrPerResolutionElement < options.MinimumLineSnr;
        var reason = snrLimited
            ? $"Probe SNR is low; advanced to the closest safe tier {selected.Exposure:G4} s."
            : $"Selected the closest safe tier to the {options.TargetFullScaleFraction:P0} spectral-ROI target.";
        return Accept(snrLimited ? "LOW_SNR_SAFE_TIER" : "EXPOSURE_TIER_SELECTED", reason, selected.Exposure, selected.Predicted, probe);
    }

    private static ExposureTierDecision Accept(string code, string reason, double selected, double predicted, SpectralProbeMetrics probe) =>
        new(true, selected, predicted, code, reason, BuildMetrics(probe, predicted));

    private static ExposureTierDecision Reject(string code, string reason, double selected, double predicted, SpectralProbeMetrics probe) =>
        new(false, selected, predicted, code, reason, BuildMetrics(probe, predicted));

    private static IReadOnlyDictionary<string, double> BuildMetrics(SpectralProbeMetrics probe, double predicted) =>
        new Dictionary<string, double>
        {
            ["probeExposureSeconds"] = probe.ExposureSeconds,
            ["biasLevelAdu"] = probe.BiasLevelAdu,
            ["highPercentileAdu"] = probe.HighPercentileAdu,
            ["fullScaleAdu"] = probe.FullScaleAdu,
            ["saturatedFraction"] = probe.SaturatedFraction,
            ["traceSaturatedFraction"] = probe.TraceSaturatedFraction,
            ["clippedDispersionColumnFraction"] = probe.ClippedDispersionColumnFraction,
            ["longestClippedDispersionColumnRun"] = probe.LongestClippedDispersionColumnRun,
            ["traceSpatialCenterPixel"] = probe.TraceSpatialCenterPixel,
            ["traceSpatialHalfWidthPixels"] = probe.TraceSpatialHalfWidthPixels,
            ["continuumSnrPerResolutionElement"] = probe.ContinuumSnrPerResolutionElement,
            ["lineSnrPerResolutionElement"] = probe.LineSnrPerResolutionElement,
            ["targetToSkyContrast"] = probe.TargetToSkyContrast,
            ["predictedFullScaleFraction"] = predicted
        };
}
