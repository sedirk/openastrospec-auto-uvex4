using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed record G3MeasuredHandoffDecision(GateResult Gate, G3AcquisitionMotionState? Charged = null);

// Accounting only. Caller proves immutable frame, original owner/pier/epoch,
// settled ledger and current mount position before invoking this policy.
internal static class G3MeasuredHandoffPolicy
{
    internal static G3MeasuredHandoffDecision Charge(G3AcquisitionMotionState state,
        double displacement, double radius, DateTimeOffset now)
    {
        if (state.Validate().Count != 0 || state.Phase != G3AcquisitionMotionPhase.SettledBudgetLedger ||
            now < state.UpdatedUtc || !double.IsFinite(displacement) || displacement < 0 ||
            !double.IsFinite(radius) || radius < 0)
            return new(GateResult.Unknown("G3_HANDOFF_ACCOUNTING_INVALID", "Fresh handoff has an invalid ledger, clock or geometry; no adoption or motion."));
        var progress = state.MaximumSingleCorrectionArcseconds - 2 * state.ArrivalToleranceArcseconds;
        var returns = radius <= state.ArrivalToleranceArcseconds ? 0 :
            Math.Ceiling((radius + 2 * state.ArrivalToleranceArcseconds) / progress);
        var chargedDistance = state.CumulativeMotionArcseconds + displacement;
        var chargedActions = (long)state.CorrectionAttempts + 1;
        var requiredDistance = chargedDistance + returns * state.MaximumSingleCorrectionArcseconds;
        var requiredActions = chargedActions + returns;
        var requiredSeconds = (now - state.MotionClockStartedUtc).TotalSeconds +
            (1 + returns) * state.WorstCaseActionSeconds + G3AcquisitionReturnTimePolicy.CleanupReserveSeconds;
        var metrics = new Dictionary<string, double> {
            ["handoffArcseconds"] = displacement, ["radiusArcseconds"] = radius,
            ["requiredDistanceArcseconds"] = requiredDistance, ["requiredActions"] = requiredActions,
            ["requiredSeconds"] = requiredSeconds, ["reservedReturnActions"] = returns,
            ["maximumDistanceArcseconds"] = state.MaximumCumulativeMotionArcseconds,
            ["maximumActions"] = state.MaximumCorrectionAttempts, ["maximumSeconds"] = state.MaximumElapsedSeconds };
        if (displacement > state.MaximumSingleCorrectionArcseconds || radius > state.MaximumRadiusArcseconds + state.ArrivalToleranceArcseconds ||
            requiredDistance > state.MaximumCumulativeMotionArcseconds || requiredActions > state.MaximumCorrectionAttempts ||
            requiredSeconds > state.MaximumElapsedSeconds)
            return new(GateResult.TargetQuality("G3_FRESH_HANDOFF_BUDGET_EXHAUSTED",
                "Fresh stopped handoff cannot fit its original movement/action/time and full return reserve. No new command or budget reset; checked target termination is required.", metrics));
        return new(GateResult.Pass("G3_FRESH_HANDOFF_DEBITED", "Current measured position adopted with conservative debit, not a new movement allowance.", metrics),
            state with { CumulativeMotionArcseconds = chargedDistance, CorrectionAttempts = checked((int)chargedActions), UpdatedUtc = now });
    }
}
