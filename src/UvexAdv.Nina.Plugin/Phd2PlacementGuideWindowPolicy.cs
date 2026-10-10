using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed record Phd2SlitApertureResidual(
    double MidpointPixels, double AlongSlitPixels, double CrossSlitPixels,
    double HalfLengthPixels, double UncertaintyPixels);

internal sealed record Phd2CorrectionWindowDecision(
    bool CanContinue, string Code, double? LatestGuideResidualPixels = null,
    double? MaximumEndpointSpreadPixels = null);

// Structured measurement rejection, not an exception-message allow-list.
// Only the initial pre-motion caller may turn this into a checked quality skip.
internal sealed class Phd2PlacementGuideWindowRejectedException(
    int completedWindows, IReadOnlyList<double> residuals, double tolerance,
    long connectionEpoch, long guideEpoch, Phd2Point expectedLock) : InvalidOperationException(
        "PHD2_GUIDE_WINDOW_NOT_STABLE: The bounded same-lock fresh-frame windows did not all meet the commissioned guide residual; no extra lock movement or budget reset was allowed.")
{
    internal const string StoppedCode = "PHD2_PRE_MOTION_GUIDE_WINDOW_REJECTED";
    internal int CompletedWindows { get; } = completedWindows;
    internal IReadOnlyList<double> Residuals { get; } = residuals.ToArray();
    internal double Tolerance { get; } = tolerance;
    internal long ConnectionEpoch { get; } = connectionEpoch;
    internal long GuideEpoch { get; } = guideEpoch;
    internal Phd2Point ExpectedLock { get; } = expectedLock;

    internal bool CanClassifyBeforeMotion(Phd2StateSnapshot current, bool hasExactLockHistory) =>
        !hasExactLockHistory && CompletedWindows > 0 && Residuals.Count >= 3 &&
        double.IsFinite(Tolerance) && Tolerance > 0 &&
        Residuals.All(value => double.IsFinite(value) && value >= 0) &&
        !Phd2PlacementGuideWindowPolicy.AllWithinTolerance(Residuals, Tolerance) &&
        current.IsConnected && !current.AutomationPaused && !current.Phd2Paused &&
        current.AppState == Phd2AppState.Guiding && !current.PendingSettleOperationId.HasValue &&
        current.ConnectionEpoch == ConnectionEpoch && current.GuideEpoch == GuideEpoch &&
        current.GuideOutput is { Failed: false, ConsecutiveMissingOutputs: 0 };
}

internal static class Phd2PlacementGuideWindowPolicy
{
    internal static bool IsValidWarningCeiling(double preferred, double ceiling) =>
        double.IsFinite(preferred) && preferred >= 0 && double.IsFinite(ceiling) &&
        (ceiling == 0 || (preferred > 0 && ceiling >= preferred && ceiling <= Math.Min(3, preferred * 1.5)));

    // Each index is an entirely new, complete window, never a re-interpretation
    // of a rejected frame set. The caller retains its original time budget.
    internal static double ToleranceForFreshWindow(double preferred, double ceiling, int window)
    {
        if (!IsValidWarningCeiling(preferred, ceiling) || window < 1)
            throw new ArgumentOutOfRangeException(nameof(ceiling));
        return ceiling == 0 ? preferred : preferred + (ceiling - preferred) * Math.Min(2, window - 1) / 2;
    }

    internal static bool CanAcceptWarningWindow(IReadOnlyList<Phd2SlitFieldMeasurement> samples,
        IReadOnlyList<double> residuals, double tolerance, bool sameEpochAndLock, bool liveOutputConfirmed) =>
        sameEpochAndLock && liveOutputConfirmed && samples.Count >= 3 && samples.Count == residuals.Count &&
        samples.All(item => item.GuidePositionMeasuredInFrame && item.TargetIdentityConfirmed &&
            HasMeasuredPositionAuthority(item.TargetPositionAuthority)) && AllWithinTolerance(residuals, tolerance);

    internal static bool CanRollCorrectionWindow(bool supervised, Phd2SlitGuideMode guideMode,
        bool planAllowed, bool planComplete, bool waitingForLock, int availableFrames, int requiredFrames) =>
        supervised && guideMode == Phd2SlitGuideMode.OffSlitGuideStar && planAllowed && !planComplete &&
        waitingForLock && availableFrames >= Math.Max(3, requiredFrames);

    // FIFO only: never search for a favourable subset or average away a bad
    // frame. Callers must keep the same guide epoch AND exact lock; after any
    // motion the production path acquires an entirely new post-lock window.
    internal static IReadOnlyList<T> AppendConsecutiveSample<T>(IReadOnlyList<T> previous, T latest,
        int requiredFrames, Func<T, Phd2SlitFieldMeasurement> measurement)
    {
        var count = Math.Max(3, requiredFrames);
        if (previous.Count < count)
            throw new InvalidOperationException("PHD2_ROLLING_WINDOW_INCOMPLETE: A full preceding window is required.");
        var next = measurement(latest);
        var last = measurement(previous[^1]);
        if (next.CapturedUtc <= last.CapturedUtc ||
            previous.Any(item => string.Equals(measurement(item).FrameSha256, next.FrameSha256, StringComparison.OrdinalIgnoreCase)) ||
            previous.Any(item => measurement(item).TopologyFingerprintSha256 != next.TopologyFingerprintSha256))
            throw new InvalidOperationException("PHD2_ROLLING_WINDOW_EVIDENCE_CHANGED: Repeated, out-of-order or different-topology evidence cannot extend this window.");
        return previous.Skip(previous.Count - (count - 1)).Append(latest).ToArray();
    }

    // Motion continuation is not final slit/guide-quality acceptance. For an
    // explicitly supervised off-slit guide, common image motion cancels in
    // guide + slit - target. Use EVERY frame to check that absolute endpoint,
    // and require the latest guide measurement to have reached the existing
    // lock before dispatching another already-authorized bounded stage.
    internal static Phd2CorrectionWindowDecision EvaluateCorrectionWindow(
        IReadOnlyList<Phd2SlitFieldMeasurement> samples, Phd2Point currentLock,
        Phd2SlitGuideMode guideMode, bool supervised, bool planAllowed, bool planComplete,
        double guideTolerance, double endpointTolerance, int requiredFrames)
    {
        if (!supervised || guideMode != Phd2SlitGuideMode.OffSlitGuideStar || !planAllowed || planComplete)
            return new(false, "CORRECTION_WINDOW_NOT_APPLICABLE");
        if (samples.Count < Math.Max(3, requiredFrames) || !Finite(currentLock) ||
            !double.IsFinite(guideTolerance) || guideTolerance <= 0 ||
            !double.IsFinite(endpointTolerance) || endpointTolerance <= 0)
            return new(false, "CORRECTION_WINDOW_INVALID");

        var topology = samples[0].TopologyFingerprintSha256;
        var frames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var endpoints = new List<Phd2Point>();
        DateTimeOffset? previousTime = null;
        foreach (var sample in samples)
        {
            if (!sample.GuidePositionMeasuredInFrame || !sample.TargetIdentityConfirmed ||
                !HasMeasuredPositionAuthority(sample.TargetPositionAuthority) ||
                !Finite(sample.GuideStar) || !Finite(sample.TargetCentroid) || !Finite(sample.RecognizedSlitAcquisitionPoint) ||
                string.IsNullOrWhiteSpace(topology) || sample.TopologyFingerprintSha256 != topology ||
                string.IsNullOrWhiteSpace(sample.FrameSha256) || !frames.Add(sample.FrameSha256) ||
                (previousTime.HasValue && sample.CapturedUtc <= previousTime.Value))
                return new(false, "CORRECTION_WINDOW_EVIDENCE_INVALID");
            previousTime = sample.CapturedUtc;
            var endpoint = new Phd2Point(
                sample.GuideStar.X + sample.RecognizedSlitAcquisitionPoint.X - sample.TargetCentroid.X,
                sample.GuideStar.Y + sample.RecognizedSlitAcquisitionPoint.Y - sample.TargetCentroid.Y);
            if (!Finite(endpoint)) return new(false, "CORRECTION_WINDOW_EVIDENCE_INVALID");
            endpoints.Add(endpoint);
        }

        var guideResidual = Distance(samples[^1].GuideStar, currentLock);
        var spread = 0d;
        for (var i = 0; i < endpoints.Count; i++)
            for (var j = 0; j < i; j++)
                spread = Math.Max(spread, Distance(endpoints[i], endpoints[j]));
        if (!double.IsFinite(guideResidual) || !double.IsFinite(spread))
            return new(false, "CORRECTION_WINDOW_EVIDENCE_INVALID");
        if (guideResidual > guideTolerance)
            return new(false, "CORRECTION_WINDOW_LOCK_NOT_REACHED", guideResidual, spread);
        if (spread > endpointTolerance)
            return new(false, "CORRECTION_WINDOW_ENDPOINT_UNSTABLE", guideResidual, spread);
        return new(true, "CORRECTION_WINDOW_COHERENT", guideResidual, spread);
    }

    private static bool Finite(Phd2Point point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
    private static double Distance(Phd2Point a, Phd2Point b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    internal static bool MustWaitForExistingLock(
        IReadOnlyList<double> guideResiduals, double guideTolerance,
        bool sameFrameMeasuredGeometry, bool planAllowed, string planCode) =>
        sameFrameMeasuredGeometry && CanWaitWithoutNewMotion(planAllowed, planCode) &&
        !AllWithinTolerance(guideResiduals, guideTolerance);

    internal static string FailureCode(bool deadlineExpired, int completedWindows) =>
        deadlineExpired && completedWindows == 0 ? "PHD2_FRESH_GUIDE_WINDOW_DEADLINE" : "PHD2_GUIDE_WINDOW_NOT_STABLE";

    internal static bool HasMeasuredPositionAuthority(Phd2TargetPositionAuthority authority) => authority is
        Phd2TargetPositionAuthority.DetectedTargetCentroid or Phd2TargetPositionAuthority.CatalogWcsIdentityWithSaturatedTopologyCentroid or
        Phd2TargetPositionAuthority.CatalogWcsRegisteredField;

    internal static Phd2SlitApertureResidual? ProjectOnMeasuredSlit(PixelPoint target, SlitGeometry slit)
    {
        if (!double.IsFinite(target.X) || !double.IsFinite(target.Y) ||
            !double.IsFinite(slit.AcquisitionPoint.X) || !double.IsFinite(slit.AcquisitionPoint.Y) ||
            !double.IsFinite(slit.AngleDegrees) || !double.IsFinite(slit.LengthPixels) || slit.LengthPixels <= 0 ||
            !double.IsFinite(slit.WidthPixels) || slit.WidthPixels <= 0 ||
            !double.IsFinite(slit.UncertaintyPixels) || slit.UncertaintyPixels < 0) return null;
        var angle = slit.AngleDegrees * Math.PI / 180;
        var dx = target.X - slit.AcquisitionPoint.X;
        var dy = target.Y - slit.AcquisitionPoint.Y;
        return new(Math.Sqrt(dx * dx + dy * dy), Math.Cos(angle) * dx + Math.Sin(angle) * dy,
            -Math.Sin(angle) * dx + Math.Cos(angle) * dy, slit.LengthPixels / 2, slit.UncertaintyPixels);
    }

    internal static bool CanProbeAlongSlitWithPrecisionWarning(
        bool explicitlyAuthorized, bool measuredGeometryConfirmed,
        IReadOnlyList<Phd2SlitApertureResidual?> samples, double targetTolerance,
        double nearTargetAllowance, double acquisitionEnvelope)
    {
        if (!explicitlyAuthorized || !measuredGeometryConfirmed || samples.Count < 3 ||
            !double.IsFinite(targetTolerance) || targetTolerance <= 0 ||
            !double.IsFinite(nearTargetAllowance) || nearTargetAllowance < 0 ||
            !double.IsFinite(acquisitionEnvelope) || acquisitionEnvelope <= 0) return false;
        // This is only the explicitly supervised PROBE route, never exact midpoint
        // completion. Every new target frame must be near the measured finite slit,
        // including its uncertainty, not merely near an infinitely extended line.
        // Retain the original radial acquisition envelope and every actual residual.
        return samples.All(sample => sample is not null &&
            double.IsFinite(sample.MidpointPixels) && sample.MidpointPixels >= 0 &&
            sample.MidpointPixels <= acquisitionEnvelope &&
            double.IsFinite(sample.AlongSlitPixels) && double.IsFinite(sample.CrossSlitPixels) &&
            double.IsFinite(sample.HalfLengthPixels) && sample.HalfLengthPixels > 0 &&
            double.IsFinite(sample.UncertaintyPixels) && sample.UncertaintyPixels >= 0 &&
            Math.Abs(sample.AlongSlitPixels) + sample.UncertaintyPixels < sample.HalfLengthPixels &&
            Math.Abs(sample.CrossSlitPixels) + sample.UncertaintyPixels <= targetTolerance + nearTargetAllowance);
    }

    internal static bool CanProbeWithPrecisionWarning(
        bool explicitlyAuthorized, bool measuredGeometryConfirmed,
        IReadOnlyList<double> residuals, double targetTolerance,
        double nearTargetAllowance, double acquisitionEnvelope)
    {
        if (!explicitlyAuthorized || !measuredGeometryConfirmed || residuals.Count < 3 ||
            !double.IsFinite(targetTolerance) || targetTolerance <= 0 ||
            !double.IsFinite(nearTargetAllowance) || nearTargetAllowance < 0 ||
            !AllWithinTolerance(residuals, acquisitionEnvelope)) return false;
        var ordered = residuals.OrderBy(value => value).ToArray();
        var median = (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2;
        // Probing is not exact placement. Typical position must already be near
        // the slit; individual wind samples remain visible and are not dropped.
        return median <= targetTolerance + nearTargetAllowance;
    }

    internal static bool CanWaitWithoutNewMotion(bool planAllowed, string planCode) => planAllowed ||
        planCode is "FRESH_G3_RESIDUAL_REQUIRED" or "SLIT_LOCK_RETURN_ATTEMPT_RESERVE" or
            "SLIT_LOCK_RETURN_CUMULATIVE_RESERVE" or "SLIT_LOCK_RETURN_TIME_RESERVE";

    internal static bool AllWithinTolerance(IReadOnlyList<double> residuals, double tolerance) =>
        residuals.Count > 0 && double.IsFinite(tolerance) && tolerance > 0 &&
        residuals.All(value => double.IsFinite(value) && value >= 0 && value <= tolerance);
}
