using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>Recovery classification only; never grants motion or replenishes a ledger.</summary>
public static class G3WcsRecoveryPolicy
{
    internal static GateResult SearchStopGate(string code, string message, GateResult? original) =>
        original ?? (code is "G3_SEARCH_ATTEMPTS_EXHAUSTED" or "G3_SEARCH_TIME_EXHAUSTED" or
            "G3_SEARCH_STABLE_ENDPOINT_RETURN_RESERVE_LIMIT"
            ? GateResult.TargetQuality(code, message) : GateResult.Unknown(code, message));

    internal static GateResult SearchTerminalGate(GateResult report, GateResult source,
        bool returned, GateResult? returnFailure)
    {
        // Returning does not repair a device/identity failure, and refusing a
        // budget-limited return does not turn an ordinary miss into a night fault.
        if (source.FailureScope != GateFailureScope.TargetQuality)
            return source with { Message = $"{source.Message} {report.Message}" };
        if (!returned)
            return returnFailure is { Disposition: not GateDisposition.Passed }
                ? returnFailure with { Message = $"{report.Message} {returnFailure.Message}" } : report;
        return report with { FailureScope = GateFailureScope.TargetQuality };
    }

    public const string ExhaustedReturnedCode = "G3_WCS_CENTERING_BUDGET_EXHAUSTED_RETURNED";
    public const string ResponseNotConvergedCode = "G3_WCS_CENTERING_RESPONSE_NOT_CONVERGED_RETURNED";
    public const string PierChangedCode = "G3_WCS_CENTERING_PIER_CHANGED";

    // A typed, known-side change invalidates this target's old coordinate frame;
    // it is not permission to return across the pier or to flip again. ADR-0027
    // still requires terminal owner-stop proof and authenticated ledger retirement
    // before the night may skip. Unknown/unsafe/ownership failures remain hard.
    internal static GateResult UnreturnedCenteringGate(GateResult? returnFailure, string message,
        IReadOnlyDictionary<string, double> metrics, GateResult? sourceFailure = null) =>
        sourceFailure is { Disposition: not GateDisposition.Passed, FailureScope: GateFailureScope.Run }
            ? sourceFailure with { Message = message, Metrics = WithReturnMetrics(sourceFailure, metrics) }
        : returnFailure is { Code: "G3_SEARCH_PIER_SIDE_CHANGED", Disposition: GateDisposition.Failed }
            ? GateResult.TargetQuality(PierChangedCode, message, metrics)
        : returnFailure is { Disposition: not GateDisposition.Passed }
            ? returnFailure with { Message = message, Metrics = WithReturnMetrics(returnFailure, metrics) }
            : GateResult.Unknown("G3_WCS_CENTERING_RETURN_BLOCKED", message, metrics);

    private static IReadOnlyDictionary<string, double> WithReturnMetrics(GateResult original, IReadOnlyDictionary<string, double> context)
    {
        var merged = new Dictionary<string, double>(context);
        if (original.Metrics is not null)
            foreach (var item in original.Metrics) merged[item.Key] = item.Value;
        return merged;
    }

    // A failed exterior staging field is not proof that the target field is
    // unusable. After returning and obtaining a fresh formal WCS, try the
    // actual destination once if it fits ONE existing step. This chooses a
    // route only: the runner still preflights and charges outbound + return.
    public static bool CanTryDirectAfterFailedNeighbour(int failedNeighbours, int directAttempts,
        bool hasFreshFormalSolve, double requiredArcseconds, double maximumSingleArcseconds,
        double arrivalToleranceArcseconds) =>
        failedNeighbours > 0 && directAttempts == 0 && hasFreshFormalSolve &&
        double.IsFinite(requiredArcseconds) && requiredArcseconds > 0 &&
        double.IsFinite(maximumSingleArcseconds) && maximumSingleArcseconds > 0 &&
        double.IsFinite(arrivalToleranceArcseconds) && arrivalToleranceArcseconds > 0 &&
        requiredArcseconds + arrivalToleranceArcseconds <= maximumSingleArcseconds;

    // One additional fresh-WCS correction may absorb a small, uncertain optical
    // response. This is NOT backlash calibration, a gain multiplier, handoff
    // authority or a new motion allowance. The normal outbound/return gates run
    // again, and the retry count is persisted across acquisition families.
    public static bool CanRetryNearTargetResponse(
        bool priorMeasured, bool freshMeasured, bool hasFreshFormalSolve,
        bool directUnsegmentedMove, double priorResidualPixels, double freshResidualPixels,
        double priorSpreadPixels, double freshSpreadPixels, double pixelScaleArcseconds,
        double arrivalToleranceArcseconds, double recognitionRadiusPixels, int retriesUsed) =>
        priorMeasured && freshMeasured && hasFreshFormalSolve && directUnsegmentedMove && retriesUsed == 0 &&
        double.IsFinite(priorResidualPixels) && priorResidualPixels > 0 &&
        double.IsFinite(freshResidualPixels) && freshResidualPixels > 0 &&
        double.IsFinite(priorSpreadPixels) && priorSpreadPixels >= 0 &&
        double.IsFinite(freshSpreadPixels) && freshSpreadPixels >= 0 &&
        double.IsFinite(pixelScaleArcseconds) && pixelScaleArcseconds > 0 &&
        double.IsFinite(arrivalToleranceArcseconds) && arrivalToleranceArcseconds > 0 &&
        double.IsFinite(recognitionRadiusPixels) && recognitionRadiusPixels > 0 &&
        priorResidualPixels + priorSpreadPixels <= recognitionRadiusPixels &&
        freshResidualPixels + freshSpreadPixels <= recognitionRadiusPixels &&
        freshResidualPixels - priorResidualPixels <= priorSpreadPixels + freshSpreadPixels +
            arrivalToleranceArcseconds / pixelScaleArcseconds;

    public static bool CompletedSolvedNeighbourApproach(bool isNeighbourApproach,
        double commandScale, bool hasFreshFormalSolve, double priorRemainingArcseconds,
        double freshRemainingArcseconds, double arrivalToleranceArcseconds,
        double maximumSingleArcseconds = double.NaN) =>
        // A capped first step need not reach the exterior staging point. If its
        // NEW formal WCS proves progress and the real target now fits one step,
        // finish toward the target instead of spending a second move on another
        // exterior field. The caller still charges the exact outbound + return.
        isNeighbourApproach && double.IsFinite(commandScale) && commandScale > 0 && commandScale <= 1 &&
        (commandScale == 1d || (double.IsFinite(maximumSingleArcseconds) && maximumSingleArcseconds > 0 &&
            freshRemainingArcseconds + arrivalToleranceArcseconds <= maximumSingleArcseconds)) && hasFreshFormalSolve &&
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
