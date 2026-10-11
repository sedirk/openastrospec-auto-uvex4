using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

// Retry only a missing/ambiguous optical measurement, never a device operation.
// A rejection breaks the consecutive window: do not cherry-pick good frames on
// opposite sides of it or turn the previous position into a new measurement.
internal sealed class Phd2FreshTargetWindow(int requiredFrames)
{
    internal const int MaximumRejectedFramesToRetry = 2;
    internal const string FailureCode = "G3_POST_WCS_TARGET_NOT_MEASURED";
    internal int MaximumCaptures { get; } = requiredFrames > 0
        ? checked(requiredFrames * (MaximumRejectedFramesToRetry + 1))
        : throw new ArgumentOutOfRangeException(nameof(requiredFrames));
    internal int Captures { get; private set; }
    internal int RejectedFrames { get; private set; }
    internal int ConsecutiveFrames { get; private set; }
    internal bool Complete => ConsecutiveFrames == requiredFrames;
    internal bool Exhausted => !Complete &&
        (RejectedFrames > MaximumRejectedFramesToRetry || Captures >= MaximumCaptures);

    internal void Observe(bool measured)
    {
        if (Complete || Exhausted) throw new InvalidOperationException("The fresh target window has ended.");
        Captures++;
        if (measured) ConsecutiveFrames++;
        else { ConsecutiveFrames = 0; RejectedFrames++; }
    }

    internal static bool SameGuide(Phd2StateSnapshot before, Phd2StateSnapshot after,
        Phd2Point expectedLock, double tolerance) =>
        double.IsFinite(tolerance) && tolerance >= 0 &&
        before.IsConnected && after.IsConnected &&
        before.AppState == Phd2AppState.Guiding && after.AppState == Phd2AppState.Guiding &&
        !before.AutomationPaused && !after.AutomationPaused && !before.Phd2Paused && !after.Phd2Paused &&
        !before.PendingSettleOperationId.HasValue && !after.PendingSettleOperationId.HasValue &&
        before.ConnectionEpoch == after.ConnectionEpoch && before.GuideEpoch == after.GuideEpoch &&
        before.Profile == after.Profile && before.Equipment == after.Equipment &&
        before.SelectedStar == after.SelectedStar &&
        before.CalibrationChangeSequence == after.CalibrationChangeSequence &&
        before.LockPosition is { } prior && after.LockPosition is { } current &&
        Distance(prior, expectedLock) <= tolerance && Distance(current, expectedLock) <= tolerance &&
        after.GuideOutput is { Failed: false, ConsecutiveMissingOutputs: 0 };

    private static double Distance(Phd2Point a, Phd2Point b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}

internal sealed class Phd2FreshTargetNotMeasuredException(GateResult gate, string evidencePath)
    : InvalidOperationException($"{Phd2FreshTargetWindow.FailureCode}: {gate.Message}")
{
    internal GateResult Gate { get; } = gate;
    internal string EvidencePath { get; } = evidencePath;

    internal StageResult AfterStop(bool stopConfirmed) => stopConfirmed
        ? new(Gate, EvidencePath)
        : new(GateResult.Unknown("PHD2_FRESH_TARGET_STOP_UNCONFIRMED",
            $"Target measurement failed and the checked guide stop is unconfirmed. {Gate.Message}"), EvidencePath);
}
