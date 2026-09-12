using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal static class Phd2HandoffRecoveryPolicy
{
    public static bool ShouldReacquire(Phd2LockShiftAcquisitionBudget budget,
        double residualPixels, double existingNearSlitWindowPixels) =>
        !budget.IsAllowed && double.IsFinite(residualPixels) &&
        double.IsFinite(existingNearSlitWindowPixels) && existingNearSlitWindowPixels > 0 &&
        residualPixels > existingNearSlitWindowPixels &&
        budget.Code is "SLIT_LOCK_ACQUISITION_CUMULATIVE_RESERVE" or "SLIT_LOCK_ACQUISITION_ATTEMPT_RESERVE" or
            "SLIT_LOCK_ACQUISITION_TIME_RESERVE" or "SLIT_LOCK_RETURN_CUMULATIVE_RESERVE" or
            "SLIT_LOCK_RETURN_ATTEMPT_RESERVE" or "SLIT_LOCK_RETURN_TIME_RESERVE" or "SLIT_RESIDUAL_SEARCH_WINDOW";
}
