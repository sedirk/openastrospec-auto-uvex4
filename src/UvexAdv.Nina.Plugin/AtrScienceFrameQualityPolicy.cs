using UvexAdv.Observatory;
using UvexAdv.Spectroscopy;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Classifies an already captured, immutable science frame. GuidingStable is
/// the runner's epoch/continuity decision, including its approved supervised
/// warning mode; this policy does not impose a new slit-residual threshold.
/// A rejected frame must still be saved, but cannot increment accepted counts.
/// </summary>
internal static class AtrScienceFrameQualityPolicy
{
    /// <summary>
    /// Check before reprobe or dependency recovery as well as before science
    /// capture. An exhausted unfinished block has no work that a new exposure
    /// or guide rebuild can authorize. A completed block remains successful,
    /// including when its final accepted warning frame used the last attempt.
    /// </summary>
    public static GateResult? RemainingAttemptGate(
        int acceptedFrames, int attemptedFrames, AtrRunConfiguration configuration) =>
        acceptedFrames < configuration.ScienceFrameCount &&
        attemptedFrames >= configuration.MaximumScienceAttempts
            ? GateResult.Fail("ATR_ATTEMPT_LIMIT",
                $"Accepted {acceptedFrames}/{configuration.ScienceFrameCount} frames after {attemptedFrames} bounded attempts; no further reprobe or dependency recovery is authorized for this exhausted science block.")
            : null;

    public static GateResult Evaluate(SpectralProbeMetrics metrics, AtrRunConfiguration configuration)
    {
        if (!metrics.GuidingStable)
        {
            return GateResult.Unknown(
                "GUIDING_UNSTABLE",
                "ATR science-frame guiding continuity was not preserved. The immutable frame is retained but not accepted; fresh guide/slit evidence is required before another exposure.");
        }
        if (metrics.SaturatedFraction > configuration.MaximumSaturatedFraction) return GateResult.Fail("ATR_SATURATION_LIMIT", $"ATR science frame full-ROI saturated fraction {metrics.SaturatedFraction:P3} exceeds {configuration.MaximumSaturatedFraction:P3}.");
        if (metrics.TraceSaturatedFraction > configuration.MaximumTraceSaturatedFraction) return GateResult.Fail("ATR_TRACE_SATURATION_LIMIT", $"ATR spectral-trace saturated fraction {metrics.TraceSaturatedFraction:P3} exceeds {configuration.MaximumTraceSaturatedFraction:P3}.");
        if (metrics.ClippedDispersionColumnFraction > configuration.MaximumClippedDispersionColumnFraction) return GateResult.Fail("ATR_CLIPPED_COLUMNS_LIMIT", $"ATR spectrum clips {metrics.ClippedDispersionColumnFraction:P2} of wavelength columns, above {configuration.MaximumClippedDispersionColumnFraction:P2}.");
        if (metrics.LongestClippedDispersionColumnRun > configuration.MaximumConsecutiveClippedDispersionColumns) return GateResult.Fail("ATR_CONSECUTIVE_CLIP_LIMIT", $"ATR spectrum has {metrics.LongestClippedDispersionColumnRun} consecutive clipped wavelength columns; limit is {configuration.MaximumConsecutiveClippedDispersionColumns}.");
        if (metrics.TargetToSkyContrast < configuration.MinimumTargetToSkyContrast)
        {
            return GateResult.Warn(
                "ATR_TARGET_CONTRAST_LOW",
                $"ATR target/sky proxy {metrics.TargetToSkyContrast:F2} is below {configuration.MinimumTargetToSkyContrast:F2}; the immutable frame remains scientifically usable for stacking and is retained.");
        }
        if (metrics.LineSnrPerResolutionElement < configuration.MinimumLineSnr &&
            metrics.ContinuumSnrPerResolutionElement < configuration.MinimumContinuumSnr)
        {
            return GateResult.Warn(
                "ATR_SNR_LOW",
                "ATR science frame has low single-frame continuum and line SNR proxies; it is retained so faint-target SNR can accumulate by stacking.");
        }
        return GateResult.Pass("ATR_FRAME_QUALITY_VALID", "ATR science frame passed trace-local clipping, contrast and SNR gates.");
    }
}
