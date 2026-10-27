namespace UvexAdv.Observatory;

/// <summary>
/// PC-clock native-capture bracket, not invented hardware shutter timestamps.
/// The shutter duration is the requested/read-back exposure duration. Its
/// midpoint lies between request+duration/2 and completion-duration/2; RPC,
/// download and save delay remain explicit timing uncertainty.
/// </summary>
public sealed record G3SparseExposureTiming(DateTimeOffset CaptureRequestedUtc,
    DateTimeOffset CaptureCompletedUtc, double ExposureSeconds, DateTimeOffset SolveCompletedUtc)
{
    public DateTimeOffset EarliestMidpointUtc => CaptureRequestedUtc.AddSeconds(ExposureSeconds / 2);
    public DateTimeOffset LatestMidpointUtc => CaptureCompletedUtc.AddSeconds(-ExposureSeconds / 2);
    public DateTimeOffset MidpointUtc => EarliestMidpointUtc + (LatestMidpointUtc - EarliestMidpointUtc) / 2;
}

public enum G3SparseTimingPurpose
{
    RegisteredCurrentFrame,
    ExtrapolatedPosition,
}

/// <summary>An empirical image/sky measurement, not mount tracking=true or a default zero.</summary>
public sealed record G3SparseDriftBound(double UpperPixelsPerSecond, string MeasurementEvidenceId);

public sealed record G3SparseFrameTimingLimits(TimeSpan MaximumCurrentMidpointAge,
    TimeSpan MaximumAdjacentMidpointGap, TimeSpan MaximumConnectedSpan)
{
    public static G3SparseFrameTimingLimits Default { get; } = new(
        TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120));

    // Quality-route limits can be tightened within this version. The caller's
    // original acquisition/return/night deadlines remain separate and earlier.
    public bool IsValid => MaximumCurrentMidpointAge > TimeSpan.Zero && MaximumCurrentMidpointAge <= Default.MaximumCurrentMidpointAge &&
        MaximumAdjacentMidpointGap > TimeSpan.Zero && MaximumAdjacentMidpointGap <= Default.MaximumAdjacentMidpointGap &&
        MaximumConnectedSpan > TimeSpan.Zero && MaximumConnectedSpan <= Default.MaximumConnectedSpan;
}

public sealed record G3SparseTimingAssessment(bool Usable, GateResult Gate,
    double UncertaintyPixels, double CurrentMidpointAgeUpperSeconds,
    double MaximumAdjacentGapUpperSeconds, double ConnectedSpanUpperSeconds,
    double? ExposureDriftPixels, bool DriftWasMeasured, bool TemporalExtrapolationAuthorized)
{
    public bool RequiresFreshShortTargetPair => true;
    public bool MotionAuthority => false;
}

/// <summary>
/// Admission and post-solve revalidation for sparse optical fallbacks. A stale,
/// invalid or unmeasured extrapolation rejects only this fallback (warning),
/// never grants extra acquisition/return time, and never stops an entire night.
/// Actual pixel registration may describe its measured current frame without a
/// drift estimate, but cannot use that as authorization for temporal projection.
/// </summary>
public static class G3SparseFrameTimingPolicy
{
    public static double RegisteredDriftUpperBound(double measuredDisplacementPixels,
        double maximumHoldoutResidualPixels, double minimumMidpointSeparationSeconds)
    {
        if (!double.IsFinite(measuredDisplacementPixels) || measuredDisplacementPixels < 0 ||
            !double.IsFinite(maximumHoldoutResidualPixels) || maximumHoldoutResidualPixels < 0 ||
            !double.IsFinite(minimumMidpointSeparationSeconds) || minimumMidpointSeparationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(minimumMidpointSeparationSeconds));
        // One pixel floor on EACH endpoint, plus independent holdout errors.
        // No encoder error term: the two actual images measured that motion.
        return (measuredDisplacementPixels + 2 * Math.Max(1, maximumHoldoutResidualPixels)) /
            minimumMidpointSeparationSeconds;
    }

    public static G3SparseTimingAssessment Evaluate(G3SparseExposureTiming current, DateTimeOffset now,
        G3SparseTimingPurpose purpose, double registrationUncertaintyPixels,
        double maximumPositionUncertaintyPixels,
        IReadOnlyList<G3SparseExposureTiming>? connectedFrames = null,
        G3SparseDriftBound? measuredDrift = null, G3SparseFrameTimingLimits? limits = null)
    {
        limits ??= G3SparseFrameTimingLimits.Default;
        G3SparseTimingAssessment Reject(string suffix, string reason, double age = 0, double gap = 0, double span = 0) =>
            new(false, GateResult.Warn("G3_SPARSE_TIMING_" + suffix, reason), 0, age, gap, span, null, false, false);
        if (!limits.IsValid || !Enum.IsDefined(purpose) || now == default || !Valid(current, now) ||
            !double.IsFinite(registrationUncertaintyPixels) || registrationUncertaintyPixels < 0 ||
            !double.IsFinite(maximumPositionUncertaintyPixels) || maximumPositionUncertaintyPixels <= 0)
            return Reject("INVALID", "Invalid/reversed/future capture or solve timing; this fallback is not used.");
        if (connectedFrames is not null && (connectedFrames.Count > 16 || connectedFrames.Any(f => !Valid(f, now))))
            return Reject("CONNECTED_INVALID", "Every bounded graph source needs its original capture bracket and completed solve timestamp.");
        var frames = (connectedFrames ?? []).Append(current)
            .Distinct().OrderBy(f => f.CaptureRequestedUtc).ToArray();
        if (frames.Length > 16 || frames.Any(f => !Valid(f, now)))
            return Reject("CONNECTED_INVALID", "Every bounded graph source needs its original capture bracket and completed solve timestamp.");
        var age = (now - current.EarliestMidpointUtc).TotalSeconds;
        var gap = frames.Zip(frames.Skip(1), (a, b) =>
            Math.Max(0, (b.LatestMidpointUtc - a.EarliestMidpointUtc).TotalSeconds)).DefaultIfEmpty(0).Max();
        var span = (frames.Max(f => f.LatestMidpointUtc) - frames.Min(f => f.EarliestMidpointUtc)).TotalSeconds;
        if (age > limits.MaximumCurrentMidpointAge.TotalSeconds)
            return Reject("CURRENT_STALE", "Capture duration, download and solve latency leave the current midpoint too old; use a new bounded field, not mount readback as fresh optical evidence.", age, gap, span);
        if (gap > limits.MaximumAdjacentMidpointGap.TotalSeconds)
            return Reject("GAP_EXCEEDED", "The image midpoint gap is too large for this realtime registration chain.", age, gap, span);
        if (span > limits.MaximumConnectedSpan.TotalSeconds)
            return Reject("SPAN_EXCEEDED", "The connected image set spans too much time; it cannot be retained as a realtime mosaic.", age, gap, span);
        if (measuredDrift is not null && (!double.IsFinite(measuredDrift.UpperPixelsPerSecond) ||
            measuredDrift.UpperPixelsPerSecond < 0 || string.IsNullOrWhiteSpace(measuredDrift.MeasurementEvidenceId)))
            return Reject("DRIFT_INVALID", "A drift bound needs finite nonnegative measured rate and its image/sky evidence reference.", age, gap, span);
        if (purpose == G3SparseTimingPurpose.ExtrapolatedPosition && measuredDrift is null)
            return Reject("DRIFT_UNMEASURED", "Unknown image drift is not zero; a timestamp or unchanged mount readback cannot authorize temporal position extrapolation.", age, gap, span);
        var exposureDrift = measuredDrift?.UpperPixelsPerSecond * current.ExposureSeconds;
        // Age uses the EARLIEST possible midpoint, hence includes bracket delay.
        // Half the full shutter drift additionally covers the integrated image's
        // positional smear; no constant-velocity correction is sent to a mount.
        var uncertainty = registrationUncertaintyPixels + (measuredDrift?.UpperPixelsPerSecond ?? 0) *
            (age + current.ExposureSeconds / 2);
        if (!double.IsFinite(uncertainty) || uncertainty > maximumPositionUncertaintyPixels)
            return Reject("UNCERTAINTY_EXCEEDED", "Measured drift during exposure and since the capture midpoint exceeds the unchanged recognition envelope.", age, gap, span);
        var metrics = new Dictionary<string, double>
        {
            ["currentMidpointAgeUpperSeconds"] = age,
            ["captureMidpointIntervalSeconds"] = (current.LatestMidpointUtc - current.EarliestMidpointUtc).TotalSeconds,
            ["exposureSeconds"] = current.ExposureSeconds,
            ["solveAfterCaptureSeconds"] = (current.SolveCompletedUtc - current.CaptureCompletedUtc).TotalSeconds,
            ["adjacentMidpointGapUpperSeconds"] = gap,
            ["connectedSpanUpperSeconds"] = span,
            ["positionUncertaintyPixels"] = uncertainty,
            ["maximumPositionUncertaintyPixels"] = maximumPositionUncertaintyPixels,
            ["driftWasMeasured"] = measuredDrift is null ? 0 : 1,
        };
        if (measuredDrift is not null)
        {
            metrics["measuredDriftUpperPixelsPerSecond"] = measuredDrift.UpperPixelsPerSecond;
            metrics["exposureDriftPixels"] = exposureDrift!.Value;
        }
        return new(true, GateResult.Warn("G3_SPARSE_TIMING_CURRENT",
            measuredDrift is null
                ? "Current registered pixels only; drift remains unknown, no temporal extrapolation, and a new independent short target pair is mandatory."
                : "Exposure/capture/solve elapsed time and measured drift are included in the coarse prediction uncertainty; a new independent short target pair remains mandatory.", metrics),
            uncertainty, age, gap, span, exposureDrift, measuredDrift is not null,
            purpose == G3SparseTimingPurpose.ExtrapolatedPosition && measuredDrift is not null);
    }

    private static bool Valid(G3SparseExposureTiming? timing, DateTimeOffset now) => timing is not null &&
        timing.CaptureRequestedUtc != default && timing.CaptureCompletedUtc >= timing.CaptureRequestedUtc &&
        timing.SolveCompletedUtc >= timing.CaptureCompletedUtc && timing.SolveCompletedUtc <= now &&
        double.IsFinite(timing.ExposureSeconds) && timing.ExposureSeconds > 0 &&
        timing.ExposureSeconds <= (timing.CaptureCompletedUtc - timing.CaptureRequestedUtc).TotalSeconds;
}
