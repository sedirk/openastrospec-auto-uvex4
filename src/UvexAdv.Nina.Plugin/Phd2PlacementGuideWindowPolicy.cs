using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed record Phd2SlitApertureResidual(
    double MidpointPixels, double AlongSlitPixels, double CrossSlitPixels,
    double HalfLengthPixels, double UncertaintyPixels);

internal static class Phd2PlacementGuideWindowPolicy
{
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
