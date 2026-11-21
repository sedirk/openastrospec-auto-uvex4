using UvexAdv.Phd2;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal enum PreGuidePreviewReadback { SameSelection, CandidateLost, ContextChanged }

internal static class Phd2HandoffRecoveryPolicy
{
    internal static bool CentreInsideOneStageOnRebuild(string reason) =>
        reason is "PHD2_PRE_SHIFT_TARGET_DRIFT" or "PHD2_WCS_TAKEOVER_POSITION_CHANGED";

    internal static int RemainingPreviewSelections(int used, bool alreadyReselected, bool hasRejectedCandidates,
        bool guidingStarted, bool recovery) =>
        used is >= 1 and < 4 && !alreadyReselected && !hasRejectedCandidates && !guidingStarted && !recovery ? 4 - used : 0;

    internal static bool SameLostPreviewOwner(Phd2StateSnapshot before, Phd2StateSnapshot now) =>
        before.ConnectionEpoch == now.ConnectionEpoch && before.GuideEpoch == now.GuideEpoch &&
        now.IsConnected && !now.AutomationPaused && !now.Phd2Paused && now.GuideOutput?.Failed != true &&
        now.PendingSettleOperationId is null && now.AppState is Phd2AppState.Looping &&
        before.Profile == now.Profile && before.Equipment == now.Equipment && before.SelectedStar == now.SelectedStar &&
        before.LockPosition == now.LockPosition && before.CalibrationChangeSequence == now.CalibrationChangeSequence &&
        before.LastConfigurationChange?.EventSequence == now.LastConfigurationChange?.EventSequence;

    // The native candidate/SNR preparation does not measure the target. A
    // fresh selection image can be re-used, but a changed optical position
    // must be noticed BEFORE the long initial settle, not only afterwards.
    // This is a request for one existing-loop image, not a new freshness or
    // science-admission threshold; use the commissioned measurement lifetime.
    internal static bool NeedsPreGuideRefresh(DateTimeOffset completedUtc, DateTimeOffset now,
        double maximumMeasurementAgeSeconds) =>
        completedUtc == default || completedUtc > now ||
        !double.IsFinite(maximumMeasurementAgeSeconds) || maximumMeasurementAgeSeconds <= 0 ||
        now - completedUtc > TimeSpan.FromSeconds(maximumMeasurementAgeSeconds);

    internal static bool FreshTargetExceedsHandoff(TargetIdentification target, SlitGeometry slit,
        double remainingHandoffRadius) => Phd2FreshTargetFluxPolicy.HasMeasuredHandoffPosition(target) &&
        target.Target is { } measured && double.IsFinite(remainingHandoffRadius) && remainingHandoffRadius >= 0 &&
        Math.Sqrt(Math.Pow(measured.Centroid.X - slit.AcquisitionPoint.X, 2) +
            Math.Pow(measured.Centroid.Y - slit.AcquisitionPoint.Y, 2)) + target.CatalogPositionSpreadPixels > remainingHandoffRadius;

    // A measured target that fitted one fine stage when handed over, but now
    // needs a large multi-stage jump, is not the original fine-placement case.
    // Prefer the already budgeted, once-only stopped coarse repair before the
    // first lock command; never reinterpret a native StarLost after dispatch.
    // A large but repeatably controllable residual alone is not a reason to
    // discard a usable field: retain the ordinary path if there is still one
    // commissioned growth allowance left before exhausting the live envelope.
    internal static bool GrewBeyondOneStage(double coarse, double fresh, double stage, double growth, double remainingRadius) =>
        double.IsFinite(coarse) && coarse >= 0 && double.IsFinite(stage) && stage > 0 &&
        double.IsFinite(growth) && growth > 0 && double.IsFinite(fresh) &&
        double.IsFinite(remainingRadius) && remainingRadius > 0 && remainingRadius - fresh < growth &&
        coarse <= stage && fresh > stage + growth && fresh - coarse > growth;

    internal static PreGuidePreviewReadback ClassifyPreviewReadback(Phd2StateSnapshot before,
        Phd2StateSnapshot after, Phd2Point selected, long? freshFrameSequence, bool motionFree)
    {
        const PreGuidePreviewReadback invalid = PreGuidePreviewReadback.ContextChanged;
        if (!before.IsConnected || !after.IsConnected || before.AutomationPaused || after.AutomationPaused ||
            before.Phd2Paused || after.Phd2Paused || before.ConnectionEpoch != after.ConnectionEpoch ||
            before.Profile != after.Profile || before.Equipment != after.Equipment ||
            before.SelectedStar != selected || after.SelectedStar != selected || before.LockPosition != selected ||
            !double.IsFinite(selected.X) || !double.IsFinite(selected.Y) ||
            before.CalibrationChangeSequence != after.CalibrationChangeSequence ||
            before.LastConfigurationChange?.EventSequence != after.LastConfigurationChange?.EventSequence ||
            before.PendingSettleOperationId.HasValue || after.PendingSettleOperationId.HasValue ||
            before.GuideOutput?.Failed == true || after.GuideOutput?.Failed == true ||
            before.AppState is not (Phd2AppState.Looping or Phd2AppState.Selected) ||
            after.EventSequence <= before.EventSequence || (freshFrameSequence is { } frame &&
                (frame <= before.EventSequence || frame > after.EventSequence))) return invalid;

        // SaveNextLoopingFrame does one get_app_state and waits for the next
        // LoopingExposures. A perfectly good selected star can cause BOTH
        // Looping -> Selected -> Looping. This is not a loss witness.
        var delta = after.GuideEpoch - before.GuideEpoch;
        var changes = after.RecentGuideEpochChanges.Where(change => change.Epoch > before.GuideEpoch).ToArray();
        if (delta is < 0 or > 4 || changes.Length != delta) return invalid;
        var state = before.AppState;
        var sequence = before.EventSequence;
        var sawLockLoss = false;
        var sawStarLoss = false;
        var epoch = before.GuideEpoch;
        foreach (var change in changes)
        {
            var rpc = change.Cause == "GetAppStateAsync";
            if (change.PreviousEpoch != epoch || change.Epoch != epoch + 1) return invalid;
            epoch = change.Epoch;
            if (change.PreviousState != state || change.EventSequence < sequence ||
                (!rpc && change.EventSequence == sequence) || change.EventSequence > after.EventSequence) return invalid;
            switch (change.Cause)
            {
                case "GetAppStateAsync" when state == Phd2AppState.Looping && change.State == Phd2AppState.Selected: break;
                case "event:LoopingExposures" when (state == Phd2AppState.Selected || sawLockLoss && state == Phd2AppState.LostLock)
                    && change.State == Phd2AppState.Looping: break;
                case "event:StarLost":
                case "event:LockPositionLost":
                    if (change.State != (state == Phd2AppState.Looping ? Phd2AppState.Looping : Phd2AppState.LostLock)) return invalid;
                    sawLockLoss = true;
                    sawStarLoss |= change.Cause == "event:StarLost";
                    break;
                default: return invalid; // Pause, stop, reselect, rebind, unknown or missing provenance.
            }
            state = change.State;
            sequence = change.EventSequence;
        }
        if (state != after.AppState || (after.LockPosition != before.LockPosition &&
            !(sawLockLoss && after.LockPosition is null)) || (!sawStarLoss && before.LastGuideStep != after.LastGuideStep))
            return invalid;
        if (after.LastPreviewStarMissSequence < before.LastPreviewStarMissSequence ||
            after.LastPreviewStarMissSequence > after.EventSequence) return invalid;
        var freshMiss = after.LastPreviewStarMissSequence > before.EventSequence;
        if (sawLockLoss || freshMiss)
            return motionFree && freshMiss ? PreGuidePreviewReadback.CandidateLost : invalid;
        return freshFrameSequence.HasValue && after.AppState == Phd2AppState.Looping && delta <= 2
            ? PreGuidePreviewReadback.SameSelection : invalid;
    }

    // The PHD2 planner has a protocol result, not an Observatory GateResult.
    // Translate only reviewed, non-dispatched target/budget refusals here.
    // Geometry, owner, safety, topology and malformed ledgers stay run failures.
    internal static GateResult FailureGate(string code, string message) => code is
        "SLIT_LOCK_ACQUISITION_CUMULATIVE_RESERVE" or "SLIT_LOCK_ACQUISITION_ATTEMPT_RESERVE" or
        "SLIT_LOCK_ACQUISITION_TIME_RESERVE" or "SLIT_LOCK_RETURN_CUMULATIVE_RESERVE" or
        "SLIT_LOCK_RETURN_ATTEMPT_RESERVE" or "SLIT_LOCK_RETURN_TIME_RESERVE" or
        "SLIT_RESIDUAL_SEARCH_WINDOW" or "GUIDE_LOCK_RESIDUAL_HIGH" or "G3_TARGET_FLUX_OUTSIDE_ENVELOPE"
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
