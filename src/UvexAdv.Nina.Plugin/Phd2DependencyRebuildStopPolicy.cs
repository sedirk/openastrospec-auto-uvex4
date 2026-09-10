using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal static class Phd2DependencyRebuildStopPolicy
{
    // This compares the unchanged commanded lock, not guide accuracy. A lost
    // centroid invalidates GuideEpoch, so that epoch cannot attest continued
    // science; it also must not prevent a checked stop of our own lost session.
    internal static bool CanStopOwnedSession(
        Phd2StateSnapshot snapshot,
        bool guidingStartedByRun,
        long? ownedConnectionEpoch,
        Phd2Point? expectedLock,
        Phd2Point? actualLock) =>
        snapshot.IsConnected && !snapshot.AutomationPaused && !snapshot.Phd2Paused &&
        snapshot.AppState is Phd2AppState.Guiding or Phd2AppState.LostLock &&
        !snapshot.PendingSettleOperationId.HasValue &&
        guidingStartedByRun && ownedConnectionEpoch == snapshot.ConnectionEpoch &&
        expectedLock is not null && actualLock is not null &&
        double.IsFinite(expectedLock.X) && double.IsFinite(expectedLock.Y) &&
        double.IsFinite(actualLock.X) && double.IsFinite(actualLock.Y) &&
        Math.Abs(expectedLock.X - actualLock.X) <= 0.25 &&
        Math.Abs(expectedLock.Y - actualLock.Y) <= 0.25;
}
