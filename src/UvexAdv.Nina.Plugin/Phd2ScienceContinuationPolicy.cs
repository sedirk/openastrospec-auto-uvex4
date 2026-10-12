using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal static class Phd2ScienceContinuationPolicy
{
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
        var credit = Math.Min(requestedSeconds,
            Math.Min(monotonicElapsedSeconds, (completedUtc - openedUtc).TotalSeconds));
        if (credit <= 0) return null;
        return before with
        {
            ExcludedAtrExposureSeconds = before.ExcludedAtrExposureSeconds + credit,
            UpdatedUtc = completedUtc,
        };
    }

    private static double Distance(Phd2Point point, double x, double y) =>
        Math.Sqrt((point.X - x) * (point.X - x) + (point.Y - y) * (point.Y - y));
}
