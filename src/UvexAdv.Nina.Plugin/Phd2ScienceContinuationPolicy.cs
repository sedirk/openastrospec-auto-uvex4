using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal static class Phd2ScienceContinuationPolicy
{
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
        now >= ledger.StartedUtc && (now - ledger.StartedUtc).TotalSeconds < ledger.MaximumElapsedSeconds;

    private static double Distance(Phd2Point point, double x, double y) =>
        Math.Sqrt((point.X - x) * (point.X - x) + (point.Y - y) * (point.Y - y));
}
