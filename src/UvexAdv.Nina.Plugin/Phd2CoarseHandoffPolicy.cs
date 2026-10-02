using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

/// <summary>Coarse-routing advice, not permission to send a lock or mount command.</summary>
internal static class Phd2CoarseHandoffPolicy
{
    internal static double LimitToRemainingLedger(double initialRadius, Phd2LockShiftLimits limits,
        double minimumStageScale, double residualGrowthAllowance,
        int attemptsUsed, double cumulativePixels, TimeSpan elapsed)
    {
        if (attemptsUsed < 0 || !double.IsFinite(cumulativePixels) || cumulativePixels < 0 || elapsed < TimeSpan.Zero)
            return 0;
        var remaining = limits with
        {
            MaximumAttempts = limits.MaximumAttempts - attemptsUsed,
            MaximumCumulativePixels = limits.MaximumCumulativePixels - cumulativePixels,
            MaximumElapsed = limits.MaximumElapsed - elapsed,
        };
        // With no motion left, only already-on-slit geometry can be handed
        // over for read-only verification. This does not fund another move.
        if (remaining.MaximumAttempts <= 0 || remaining.MaximumCumulativePixels <= 0 || remaining.MaximumElapsed <= TimeSpan.Zero)
            return Math.Min(initialRadius, limits.TargetOnSlitTolerancePixels);
        return Math.Min(initialRadius, MaximumInitialResidual(remaining, minimumStageScale, residualGrowthAllowance));
    }

    public static double SelectHandoffRadius(bool measuredTargetConfirmed, double catalogRadius,
        double measuredSearchRadius, Phd2LockShiftLimits limits, double minimumStageScale,
        double residualGrowthAllowance)
    {
        if (!double.IsFinite(catalogRadius) || catalogRadius <= 0 ||
            !double.IsFinite(measuredSearchRadius) || measuredSearchRadius <= 0)
            throw new ArgumentException("Finite positive target-recognition radii are required.");
        // Catalog-only positioning keeps its commissioned narrow routing radius.
        // An independently measured target may use the existing round-trip envelope.
        // Neither route grants a lock command: the live planner still checks the
        // qualified calibration, fresh measurement and inherited remaining ledger.
        var recognitionRadius = measuredTargetConfirmed
            ? Math.Min(measuredSearchRadius, limits.MaximumAcquisitionResidualPixels)
            : catalogRadius;
        return Math.Min(recognitionRadius,
            MaximumInitialResidual(limits, minimumStageScale, residualGrowthAllowance));
    }

    public static double MaximumInitialResidual(
        Phd2LockShiftLimits limits, double minimumStageScale, double residualGrowthAllowance)
    {
        if (!double.IsFinite(minimumStageScale) || minimumStageScale is <= 0 or > 1 ||
            !double.IsFinite(residualGrowthAllowance) || residualGrowthAllowance < 0 ||
            !double.IsFinite(limits.MaximumCumulativePixels) || limits.MaximumCumulativePixels <= 0 ||
            !double.IsFinite(limits.MaximumStagePixels) || limits.MaximumStagePixels <= 0 ||
            limits.LockVerificationTolerancePixels < 0 ||
            limits.LockVerificationTolerancePixels >= limits.MaximumStagePixels ||
            limits.MaximumStageDuration <= TimeSpan.Zero || limits.MaximumElapsed <= TimeSpan.Zero ||
            limits.MaximumAttempts < 1)
            throw new ArgumentException("A validated lock-shift envelope is required for coarse handoff.");

        var outwardStep = limits.MaximumStagePixels * minimumStageScale;
        var returnProgress = limits.MaximumStagePixels - limits.LockVerificationTolerancePixels;
        bool Fits(double distance)
        {
            var outwardCount = Math.Ceiling(distance / outwardStep);
            // Same exact-lock return upper bound as Phd2SlitLockShiftPlanner.
            var returnCount = Math.Ceiling((distance + limits.LockVerificationTolerancePixels) / returnProgress);
            var totalMotion = 2 * distance + limits.LockVerificationTolerancePixels +
                2 * limits.LockVerificationTolerancePixels * returnCount;
            return totalMotion <= limits.MaximumCumulativePixels &&
                outwardCount + returnCount <= limits.MaximumAttempts &&
                (outwardCount + returnCount) * limits.MaximumStageDuration.TotalSeconds <= limits.MaximumElapsed.TotalSeconds;
        }

        var low = 0d;
        var high = limits.MaximumCumulativePixels / 2;
        for (var i = 0; i < 48; i++)
        {
            var middle = (low + high) / 2;
            if (Fits(middle)) low = middle;
            else high = middle;
        }
        // Leave the already-commissioned measurement/lock uncertainty for the
        // fresh guiding handoff. Never relabel this as strict slit acceptance.
        return Math.Max(limits.TargetOnSlitTolerancePixels,
            low - residualGrowthAllowance - limits.LockPreconditionTolerancePixels);
    }
}
