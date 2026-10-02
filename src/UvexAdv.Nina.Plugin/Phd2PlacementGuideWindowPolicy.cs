using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed record Phd2SlitApertureResidual(
    double MidpointPixels, double AlongSlitPixels, double CrossSlitPixels,
    double HalfLengthPixels, double UncertaintyPixels);

internal sealed record Phd2CorrectionWindowDecision(
    bool CanContinue, string Code, double? LatestGuideResidualPixels = null,
    double? MaximumEndpointSpreadPixels = null);

internal static class Phd2PlacementGuideWindowPolicy
{
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
