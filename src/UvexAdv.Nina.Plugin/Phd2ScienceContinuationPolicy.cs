using UvexAdv.Phd2;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal static class Phd2ScienceContinuationPolicy
{
    // HD 223554: the client invalidates settle twice for one native
    // StarLost -> Guiding transition. The epoch gap or equal lock coordinates
    // alone cannot distinguish this from an operator stop/reselect.
    internal static bool IsNativeLossRecovery(Phd2StateSnapshot state,
        long connection, long guide, long calibrationSequence, Phd2Point selected,
        Phd2Point endpoint, double tolerance)
    {
        if (!IsSameFieldGuideActiveAfterLoss(state, connection, selected, endpoint, tolerance) ||
            state.CalibrationChangeSequence != calibrationSequence ||
            state.GuideOutput is not { ConsecutiveMissingOutputs: 0 } output ||
            state.RecentGuideEpochChanges.Count < 2) return false;
        var loss = state.RecentGuideEpochChanges[^2];
        var found = state.RecentGuideEpochChanges[^1];
        return loss.PreviousEpoch == guide && loss.Epoch == guide + 1 &&
            loss is { Cause: "event:StarLost", PreviousState: Phd2AppState.Guiding, State: Phd2AppState.LostLock } &&
            found.PreviousEpoch == loss.Epoch && found.Epoch == loss.Epoch + 1 &&
            found.Epoch == state.GuideEpoch &&
            found is { Cause: "event:GuideStep" or "event:AppState", PreviousState: Phd2AppState.LostLock, State: Phd2AppState.Guiding } &&
            found.EventSequence > loss.EventSequence && found.ObservedUtc >= loss.ObservedUtc &&
            found.ObservedUtc - loss.ObservedUtc <= TimeSpan.FromSeconds(60) &&
            output.UpdatedUtc >= loss.ObservedUtc && state.LastGuideStep?.Frame == output.LastFrame;
    }

    internal static Phd2LockShiftPendingState? BindVerifiedLossWindow(Phd2LockShiftPendingState ledger,
        Phd2PostLockGuidingObservation proof, Phd2StateSnapshot current, Phd2Point selected, Phd2Point originalLock,
        long calibrationSequence, Phd2GuidingFrameResult last, DateTimeOffset now)
    {
        var tolerance = proof.LockTolerancePixels;
        if (ledger.Validate().Count != 0 || ledger.Phase != Phd2LockShiftPendingPhase.SettledBudgetLedger ||
            now < ledger.UpdatedUtc || proof.ResidualsCompletedUtc != last.CompletedUtc || now < last.CompletedUtc ||
            !proof.HasAcceptedWindow(current) ||
            !IsNativeLossRecovery(current, ledger.ConnectionEpoch, ledger.GuideEpoch, calibrationSequence,
                selected, originalLock, tolerance) ||
            !(Distance(proof.LockPosition, ledger.CurrentLockX, ledger.CurrentLockY) <= tolerance) ||
            !(Distance(proof.LockPosition, ledger.RequestedLockX, ledger.RequestedLockY) <= tolerance))
            return null;
        // No coordinate translation: unlike a reselection the lock never moved.
        return ledger with { GuideEpoch = proof.GuideEpoch, LastFramePath = last.Path,
            LastAcceptedFrameSha256 = last.Sha256, UpdatedUtc = now,
            LastReason = "Same native lock after StarLost; fresh immutable optical window verified." };
    }

    // Refresh the read-only approval, not the physical calibration. A long probe
    // can outlive the client TTL without changing any of the actual owners.
    internal static async Task<GateResult> RevalidateSameLockAsync(Phd2CalibrationValidation original,
        Func<CancellationToken, Task> physicalGates, Func<CancellationToken, Task<Phd2CalibrationValidation>> refresh,
        Func<CancellationToken, Task<bool>> sameOwnerAndLock, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await physicalGates(token).ConfigureAwait(false);
        var current = await refresh(token).ConfigureAwait(false);
        if (!current.IsValid || !current.Calibration.Calibrated || current.AllowNativeCalibration ||
            current.ConnectionEpoch != original.ConnectionEpoch || current.Profile != original.Profile ||
            current.CalibrationChangeSequence != original.CalibrationChangeSequence)
            return GateResult.Unknown("PHD2_SAME_LOCK_CALIBRATION_CHANGED",
                "Same-lock recovery did not read back the original calibrated owner. No settle or calibration was dispatched.");
        await physicalGates(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return await sameOwnerAndLock(token).ConfigureAwait(false)
            ? GateResult.Pass("PHD2_SAME_LOCK_READBACK_CURRENT", "Fresh read-only calibration and the original owner/lock passed.")
            : GateResult.Unknown("PHD2_SAME_LOCK_OWNER_CHANGED", "Owner/selection/lock changed during readback. No settle was dispatched.");
    }
    // A StarLost notification can be older than an already recovered live
    // guide. This permits only the original same-epoch fresh optical check;
    // it does NOT characterize reselection/calibration/reacquisition as inert.
    internal static bool MayVerifySettledGuideInPlace(string sourceCode) =>
        sourceCode is "GUIDING_LOST" or "GUIDING_UNSTABLE" or "GUIDING_NOT_STABLE";

    // A long science/probe exposure can outlive PHD2's last native settle
    // attestation while the same selected star and exact runtime lock remain
    // actively guiding. Permit the existing same-lock native settle route in
    // that narrow case; the source label is not authoritative (a completed
    // frame can surface it as GUIDING_LOST), so the current PHD2 state below
    // must still prove the same selected star, lock, epoch and live output.
    internal static bool CanResettleAfterExposureAtSettledLock(
        string sourceCode,
        Phd2LockShiftPendingState ledger,
        Phd2StateSnapshot state,
        long sessionConnection,
        long sessionGuide,
        Phd2Point selectedGuide,
        Phd2Point settledLock,
        Phd2Point? actualLock,
        double lockTolerance)
    {
        if (sourceCode is not ("GUIDING_LOST" or "GUIDING_UNSTABLE" or "GUIDING_NOT_STABLE") ||
            !double.IsFinite(lockTolerance) || lockTolerance <= 0 ||
            ledger.Validate().Count != 0 ||
            ledger.Phase != Phd2LockShiftPendingPhase.SettledBudgetLedger)
            return false;

        return state.IsConnected && !state.AutomationPaused && !state.Phd2Paused &&
            state.AppState == Phd2AppState.Guiding &&
            state.ConnectionEpoch == sessionConnection && state.GuideEpoch == sessionGuide &&
            state.PendingSettleOperationId is null &&
            state.GuideOutput is { Failed: false } &&
            state.SelectedStar is { } selected &&
            Distance(selected, selectedGuide.X, selectedGuide.Y) <= lockTolerance &&
            state.LockPosition is { } nativeLock &&
            Distance(nativeLock, settledLock.X, settledLock.Y) <= lockTolerance &&
            actualLock is { } actual &&
            Distance(actual, settledLock.X, settledLock.Y) <= lockTolerance &&
            Distance(actual, ledger.CurrentLockX, ledger.CurrentLockY) <= lockTolerance &&
            Distance(actual, ledger.RequestedLockX, ledger.RequestedLockY) <= lockTolerance;
    }

    // Thin cloud commonly drops a selected guide star without changing the
    // telescope field. PHD2 can reacquire that same star and begin a new guide
    // epoch by itself. This predicate recognizes only an already recovered,
    // live same-owner field; it does not authorize selection, calibration,
    // mount motion or exposure.
    internal static bool IsSameFieldGuideActiveAfterLoss(
        Phd2StateSnapshot state,
        long sessionConnection,
        Phd2Point selectedGuide,
        Phd2Point settledLock,
        double lockTolerance)
    {
        if (!double.IsFinite(lockTolerance) || lockTolerance <= 0) return false;
        return state.IsConnected && !state.AutomationPaused && !state.Phd2Paused &&
            state.AppState == Phd2AppState.Guiding &&
            state.ConnectionEpoch == sessionConnection &&
            state.PendingSettleOperationId is null &&
            state.GuideOutput is { Failed: false } &&
            state.SelectedStar is { } selected &&
            Distance(selected, selectedGuide.X, selectedGuide.Y) <= lockTolerance &&
            state.LockPosition is { } nativeLock &&
            Distance(nativeLock, settledLock.X, settledLock.Y) <= lockTolerance;
    }

    internal static GateResult? RebuildBudgetGate(Phd2LockShiftPendingState ledger,
        DateTimeOffset now, GateResult sourceGate, GateResult bindingGate)
    {
        if (bindingGate.Disposition != GateDisposition.Passed) return bindingGate;
        var errors = ledger.Validate();
        if (errors.Count != 0)
            return GateResult.Unknown("PHD2_LOCK_LEDGER_RECONCILIATION_REQUIRED", string.Join("; ", errors));
        if (ledger.Phase != Phd2LockShiftPendingPhase.SettledBudgetLedger)
            return GateResult.Unknown("PHD2_COARSE_HANDOFF_RETURN_REQUIRED", "Unsettled lock motion still owns its return; no rebuild authorized.");
        if (now < ledger.UpdatedUtc || now < ledger.MotionClockStartedUtc)
            return GateResult.Unknown("PHD2_LOCK_LEDGER_CLOCK_INVALID", "The lock ledger is ahead of the current clock; this is not normal budget exhaustion.");
        if (HasReplacementBudget(ledger, now)) return null;
        return GateResult.TargetQuality("PHD2_SCIENCE_RECOVERY_BUDGET_UNAVAILABLE",
            $"原始原因 {sourceGate.Code}: {sourceGate.Message} 原入缝预算不足以重建；未发出新运动，原预算及已保存科学帧保留，停止确认后按目标策略处理。",
            new Dictionary<string, double> { ["attemptsUsed"] = ledger.AttemptsUsed,
                ["cumulativeCommandedPixels"] = ledger.CumulativeCommandedPixels,
                ["lineageWallSeconds"] = (now - ledger.StartedUtc).TotalSeconds,
                ["excludedAtrExposureSeconds"] = ledger.ExcludedAtrExposureSeconds,
                ["activeMotionElapsedSeconds"] = (now - ledger.MotionClockStartedUtc).TotalSeconds,
                ["maximumElapsedSeconds"] = ledger.MaximumElapsedSeconds });
    }

    // Resume the original motion lineage, not a new placement budget. The
    // caller must separately attest the exact owner/session/binding/lock.
    internal static Phd2LockShiftLedger? ResumeMotionLedger(Phd2LockShiftPendingState ledger,
        DateTimeOffset now) => !HasReplacementBudget(ledger, now) ? null : ledger.ToPlannerLedger();

    // A completed motion ledger is not an exposure timer. This only permits
    // fresh observations at the existing lock, never another motion command.
    internal static bool CanObserveAtSettledLock(Phd2LockShiftPendingState ledger,
        Phd2StateSnapshot state, long sessionConnection, long sessionGuide,
        bool acceptedGuidingEvidence, Phd2Point? actualLock, double lockTolerance) =>
        ledger.Phase == Phd2LockShiftPendingPhase.SettledBudgetLedger &&
        state.IsConnected && !state.AutomationPaused && !state.Phd2Paused &&
        state.AppState == Phd2AppState.Guiding && state.GuideOutput?.Failed != true &&
        state.ConnectionEpoch == sessionConnection && state.GuideEpoch == sessionGuide &&
        ledger.ConnectionEpoch == sessionConnection && ledger.GuideEpoch == sessionGuide &&
        state.PendingSettleOperationId is null && acceptedGuidingEvidence &&
        double.IsFinite(lockTolerance) && lockTolerance > 0 && actualLock is not null &&
        Distance(actualLock, ledger.CurrentLockX, ledger.CurrentLockY) <= lockTolerance &&
        Distance(actualLock, ledger.RequestedLockX, ledger.RequestedLockY) <= lockTolerance;

    internal static bool HasReplacementBudget(Phd2LockShiftPendingState ledger, DateTimeOffset now) =>
        ledger.Phase == Phd2LockShiftPendingPhase.SettledBudgetLedger &&
        ledger.AttemptsUsed < ledger.MaximumAttempts &&
        ledger.CumulativeCommandedPixels < ledger.MaximumCumulativePixels - 1e-9 &&
        ledger.Validate().Count == 0 && now >= ledger.MotionClockStartedUtc &&
        (now - ledger.MotionClockStartedUtc).TotalSeconds < ledger.MaximumElapsedSeconds;

    // Only a completed, explicitly timed ATR capture at an already settled
    // endpoint may be excluded. No credit for pauses, crashes, failed captures,
    // readout overhead, unverified motion, or changes to the durable ledger.
    internal static Phd2LockShiftPendingState? CreditExposure(
        Phd2LockShiftPendingState before, Phd2LockShiftPendingState after,
        DateTimeOffset openedUtc, DateTimeOffset completedUtc, double requestedSeconds,
        double monotonicElapsedSeconds)
    {
        if (before != after || before.Validate().Count != 0 ||
            before.Phase != Phd2LockShiftPendingPhase.SettledBudgetLedger ||
            openedUtc < before.UpdatedUtc || completedUtc < openedUtc ||
            !double.IsFinite(requestedSeconds) || requestedSeconds <= 0 ||
            !double.IsFinite(monotonicElapsedSeconds) || monotonicElapsedSeconds <= 0)
            return null;
        var credit = CompletedExposureCredit(openedUtc, completedUtc, requestedSeconds, monotonicElapsedSeconds);
        if (credit <= 0) return null;
        return before with
        {
            ExcludedAtrExposureSeconds = before.ExcludedAtrExposureSeconds + credit,
            UpdatedUtc = completedUtc,
        };
    }

    internal static double CompletedExposureCredit(DateTimeOffset openedUtc, DateTimeOffset completedUtc,
        double requestedSeconds, double monotonicElapsedSeconds) => completedUtc < openedUtc ||
        !double.IsFinite(requestedSeconds) || requestedSeconds <= 0 ||
        !double.IsFinite(monotonicElapsedSeconds) || monotonicElapsedSeconds <= 0 ? 0 :
        Math.Min(requestedSeconds, Math.Min(monotonicElapsedSeconds, (completedUtc - openedUtc).TotalSeconds));

    private static double Distance(Phd2Point point, double x, double y) =>
        Math.Sqrt((point.X - x) * (point.X - x) + (point.Y - y) * (point.Y - y));
}
