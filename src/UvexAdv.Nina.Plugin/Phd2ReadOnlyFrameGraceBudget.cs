using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

/// <summary>One extra read-only wait per pre-exposure optical window, not a capture restart.</summary>
internal sealed class Phd2ReadOnlyFrameGraceBudget
{
    internal static readonly TimeSpan ExtraWait = TimeSpan.FromSeconds(20);
    private bool consumed;

    internal bool TryConsume(Phd2CommandTimeoutException failure,
        Phd2StateSnapshot before, Phd2StateSnapshot current)
    {
        if (consumed || failure.Operation != "fresh guiding-frame evidence" || !SameGuiding(before, current))
            return false;
        consumed = true;
        return true;
    }

    internal static bool SameGuiding(Phd2StateSnapshot before, Phd2StateSnapshot current) =>
        Valid(before) && Valid(current) && before.ConnectionEpoch == current.ConnectionEpoch &&
        before.GuideEpoch == current.GuideEpoch && before.LockPosition == current.LockPosition;

    private static bool Valid(Phd2StateSnapshot snapshot) =>
        snapshot.IsConnected && snapshot.AppState == Phd2AppState.Guiding &&
        !snapshot.AutomationPaused && !snapshot.Phd2Paused && !snapshot.PendingSettleOperationId.HasValue &&
        snapshot.LockPosition is { } position && double.IsFinite(position.X) && double.IsFinite(position.Y);
}
