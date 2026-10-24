namespace UvexAdv.Observatory;

/// <summary>Neighbour/mount predictions are search priors, not a new optical observation.</summary>
public sealed record G3MotionPredictionTimingAssessment(bool SearchHintUsable,
    bool CurrentPositionUsable, double UncertaintyPixels, GateResult Gate)
{
    public bool RequiresIndependentTargetPair => true;
    public bool MotionAuthority => false;
}

public static class G3MotionPredictionTimingPolicy
{
    public static G3MotionPredictionTimingAssessment Evaluate(G3SparseExposureTiming? source,
        DateTimeOffset now, double spatialUncertaintyPixels, double recognitionRadiusPixels,
        G3SparseDriftBound? drift = null)
    {
        G3MotionPredictionTimingAssessment Miss(string code, string message) => new(false, false,
            spatialUncertaintyPixels, GateResult.Warn(code, message));
        if (source is null || source.CaptureRequestedUtc == default || now == default ||
            source.CaptureCompletedUtc < source.CaptureRequestedUtc ||
            source.SolveCompletedUtc < source.CaptureCompletedUtc || source.SolveCompletedUtc > now ||
            !double.IsFinite(source.ExposureSeconds) || source.ExposureSeconds <= 0 ||
            source.ExposureSeconds > (source.CaptureCompletedUtc - source.CaptureRequestedUtc).TotalSeconds ||
            !double.IsFinite(spatialUncertaintyPixels) || spatialUncertaintyPixels < 0 ||
            !double.IsFinite(recognitionRadiusPixels) || recognitionRadiusPixels <= 0)
            return Miss("G3_NEIGHBOUR_TIMING_UNAVAILABLE",
                "Neighbour prediction lacks an original valid exposure/solve interval; use the normal bounded optical route.");
        var age = (now - source.EarliestMidpointUtc).TotalSeconds;
        if (age > G3SparseFrameTimingLimits.Default.MaximumConnectedSpan.TotalSeconds)
            return Miss("G3_NEIGHBOUR_PREDICTION_STALE",
                "The old optical anchor is outside the unchanged connected-span window; mount readback does not refresh it.");
        if (drift is not null && (!double.IsFinite(drift.UpperPixelsPerSecond) ||
            drift.UpperPixelsPerSecond < 0 || string.IsNullOrWhiteSpace(drift.MeasurementEvidenceId)))
            return Miss("G3_NEIGHBOUR_DRIFT_INVALID", "Invalid measured image drift cannot refresh a neighbour prediction.");
        var uncertainty = spatialUncertaintyPixels + (drift?.UpperPixelsPerSecond ?? 0) *
            (age + source.ExposureSeconds / 2);
        if (!double.IsFinite(uncertainty) || uncertainty > recognitionRadiusPixels)
            return Miss("G3_NEIGHBOUR_SEARCH_ENVELOPE_EXHAUSTED",
                "Exposure smear and elapsed optical drift exceed the existing recognition envelope; no budget was enlarged.");
        // No measured drift is UNKNOWN, not an observed zero. A bounded old
        // spatial prior may suggest where to inspect fresh independent pixels,
        // never authorize an unmeasured current position or slit arrival.
        var current = drift is not null &&
            age <= G3SparseFrameTimingLimits.Default.MaximumCurrentMidpointAge.TotalSeconds;
        return new(true, current, uncertainty, GateResult.Warn(
            current ? "G3_NEIGHBOUR_TIMED_PREDICTION" : "G3_NEIGHBOUR_SEARCH_HINT_ONLY",
            current ? "Exposure/solve delay and measured drift are included; independent target confirmation remains required."
                : "Time-bounded search hint only; unknown drift is not zero and unmeasured slit arrival is not authorized.",
            new Dictionary<string, double>
            {
                ["sourceMidpointAgeUpperSeconds"] = age,
                ["sourceExposureSeconds"] = source.ExposureSeconds,
                ["sourceSolveLatencySeconds"] = (source.SolveCompletedUtc - source.CaptureCompletedUtc).TotalSeconds,
                ["spatialUncertaintyPixels"] = spatialUncertaintyPixels,
                ["currentUncertaintyPixels"] = uncertainty,
                ["driftWasMeasured"] = drift is null ? 0 : 1,
                ["currentPositionUsable"] = current ? 1 : 0,
            }));
    }
}
