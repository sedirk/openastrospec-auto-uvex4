using UvexAdv.Phd2;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal static class Phd2HandoffRecoveryPolicy
{
    // The PHD2 planner has a protocol result, not an Observatory GateResult.
    // Translate only reviewed, non-dispatched target/budget refusals here.
    // Geometry, owner, safety, topology and malformed ledgers stay run failures.
    internal static GateResult FailureGate(string code, string message) => code is
        "SLIT_LOCK_ACQUISITION_CUMULATIVE_RESERVE" or "SLIT_LOCK_ACQUISITION_ATTEMPT_RESERVE" or
        "SLIT_LOCK_ACQUISITION_TIME_RESERVE" or "SLIT_LOCK_RETURN_CUMULATIVE_RESERVE" or
        "SLIT_LOCK_RETURN_ATTEMPT_RESERVE" or "SLIT_LOCK_RETURN_TIME_RESERVE" or
        "SLIT_RESIDUAL_SEARCH_WINDOW" or "GUIDE_LOCK_RESIDUAL_HIGH"
            ? GateResult.TargetQuality(code, message)
            : GateResult.Unknown(code, message);

    public static bool ShouldReacquire(Phd2LockShiftAcquisitionBudget budget,
        double residualPixels, double existingNearSlitWindowPixels) =>
        !budget.IsAllowed && double.IsFinite(residualPixels) &&
        double.IsFinite(existingNearSlitWindowPixels) && existingNearSlitWindowPixels > 0 &&
        residualPixels > existingNearSlitWindowPixels &&
        budget.Code is "SLIT_LOCK_ACQUISITION_CUMULATIVE_RESERVE" or "SLIT_LOCK_ACQUISITION_ATTEMPT_RESERVE" or
            "SLIT_LOCK_ACQUISITION_TIME_RESERVE" or "SLIT_LOCK_RETURN_CUMULATIVE_RESERVE" or
            "SLIT_LOCK_RETURN_ATTEMPT_RESERVE" or "SLIT_LOCK_RETURN_TIME_RESERVE" or "SLIT_RESIDUAL_SEARCH_WINDOW";
}
