namespace UvexAdv.Nina.Plugin;

internal static class Phd2PlacementGuideWindowPolicy
{
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
