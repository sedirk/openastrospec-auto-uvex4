using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>Recovery classification only; never grants motion or replenishes a ledger.</summary>
public static class G3WcsRecoveryPolicy
{
    public const string ExhaustedReturnedCode = "G3_WCS_CENTERING_BUDGET_EXHAUSTED_RETURNED";

    public static bool CompletedSolvedNeighbourApproach(bool isNeighbourApproach,
        double commandScale, bool hasFreshFormalSolve, double priorRemainingArcseconds,
        double freshRemainingArcseconds, double arrivalToleranceArcseconds) =>
        isNeighbourApproach && commandScale == 1d && hasFreshFormalSolve &&
        HasMeasuredApproachProgress(priorRemainingArcseconds, freshRemainingArcseconds, arrivalToleranceArcseconds);

    public static bool NeedsCoarseCentering(GateResult fieldGate, bool hasFreshFormalSolve,
        double adoptedResidualPixels, double coarseHandoffPixels) =>
        fieldGate.Disposition == GateDisposition.Passed && hasFreshFormalSolve &&
        double.IsFinite(adoptedResidualPixels) && adoptedResidualPixels >= 0 &&
        double.IsFinite(coarseHandoffPixels) && coarseHandoffPixels > 0 &&
        adoptedResidualPixels > coarseHandoffPixels;

    public static bool HasMeasuredApproachProgress(
        double priorRemainingArcseconds, double freshRemainingArcseconds, double arrivalToleranceArcseconds) =>
        double.IsFinite(priorRemainingArcseconds) && priorRemainingArcseconds > 0 &&
        double.IsFinite(freshRemainingArcseconds) && freshRemainingArcseconds >= 0 &&
        double.IsFinite(arrivalToleranceArcseconds) && arrivalToleranceArcseconds >= 0 &&
        freshRemainingArcseconds < priorRemainingArcseconds - arrivalToleranceArcseconds;

    public static bool RecheckExistingCoarseHandoffBeforeImprovement(
        GateResult freshFieldGate, bool hasFreshFormalSolve,
        double catalogResidualPixels, double coarseHandoffPixels) =>
        freshFieldGate.Disposition == GateDisposition.Passed && hasFreshFormalSolve &&
        double.IsFinite(catalogResidualPixels) && catalogResidualPixels >= 0 &&
        double.IsFinite(coarseHandoffPixels) && coarseHandoffPixels > 0 &&
        catalogResidualPixels <= coarseHandoffPixels;

    public static bool PreferSolvedOriginToBlindSearch(
        int completedWcsMoves, GateResult? failedFieldGate,
        GateResult? freshOriginGate, bool hasFreshFormalOriginSolve) =>
        // At least one completed, charged move bounds recursion by the existing
        // action ledger. Zero-action inverse/geometry failures must not recurse.
        completedWcsMoves > 0 && hasFreshFormalOriginSolve &&
        failedFieldGate?.Code.StartsWith("G3_PLATE_SOLVE_LADDER_EXHAUSTED", StringComparison.Ordinal) == true &&
        (freshOriginGate?.Disposition == GateDisposition.Passed ||
         freshOriginGate is { Disposition: GateDisposition.Indeterminate, Code: "G3_SOLVED_TARGET_OUTSIDE" });

    public static bool IsReserveFailure(GateResult? gate) =>
        gate is { Disposition: not GateDisposition.Passed } && gate.Code is
            "G3_MOTION_RETURN_CUMULATIVE_RESERVE_LIMIT" or
            "G3_MOTION_RETURN_ATTEMPT_RESERVE_LIMIT" or
            "G3_MOTION_RETURN_TIME_RESERVE_LIMIT";

    public static bool HasNoGlobalSearchRoundTripBudget(
        G3AcquisitionMotionState returned,
        G3LocalSearchLimits search,
        MotionLimits commissioned,
        DateTimeOffset nowUtc)
    {
        // Failure to reserve a large WCS step does NOT imply that a smaller
        // search step is impossible. Reject early only when even an optimistic
        // first-step round trip cannot fit the unchanged global ceilings.
        // Otherwise the normal fresh-field and exact search-reserve gates run.
        var minimumRoundTrip = 2 * Math.Max(0,
            search.StepArcseconds - returned.CurrentRadiusArcseconds - returned.ArrivalToleranceArcseconds);
        var remainingMotion = commissioned.MaximumCumulativeCorrectionDegrees * 3600d -
            returned.CumulativeMotionArcseconds;
        var remainingActions = commissioned.MaximumCorrectionAttempts - returned.CorrectionAttempts;
        var remainingSeconds = commissioned.EffectiveMaximumAcquisitionTime.TotalSeconds -
            Math.Max(0, (nowUtc - returned.StartedUtc).TotalSeconds);
        return remainingMotion + 1e-9 < minimumRoundTrip || remainingActions < 2 ||
            remainingSeconds + 1e-9 < 2 * returned.WorstCaseActionSeconds;
    }
}
