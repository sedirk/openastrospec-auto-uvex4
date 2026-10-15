using UvexAdv.Phd2;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal static class Phd2ScienceContinuationPolicy
{
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
