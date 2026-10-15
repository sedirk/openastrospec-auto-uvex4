using UvexAdv.Phd2;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

// A locally returned initial guide operation, retained only for a checked stop.
// It is separate from the pre-guide selection/lock readback tuple: neither a
// selected point nor LostLock can manufacture a completed-operation receipt.
internal sealed record Phd2CompletedInitialGuideStopReceipt(
    long ConnectionEpoch,
    long GuideEpoch,
    long OperationId,
    Phd2Point Lock,
    long EventSequence,
    long CalibrationChangeSequence,
    long? ConfigurationChangeSequence);

internal static class Phd2DependencyRebuildStopPolicy
{
    internal const string InitialSettleNotReachedCode = "PHD2_INITIAL_SETTLE_NOT_REACHED";

    internal static GateResult AfterConfirmedInitialGuideStop(Phd2SettleResult settle, bool lostStars) =>
        GateResult.TargetQuality(lostStars ? "PHD2_INITIAL_GUIDE_STAR_LOSS" : InitialSettleNotReachedCode,
            "初始导星未达到稳定条件，已确认停止；尚未发出入缝微调，也未开始光谱曝光。按原预算尝试恢复，仍失败时由整夜序列独立核对空闲与运动账本后跳过。",
            new Dictionary<string, double> { ["totalGuideFrames"] = settle.TotalFrames,
                ["droppedGuideFrames"] = settle.DroppedFrames, ["stopConfirmed"] = 1 });

    internal static Phd2CompletedInitialGuideStopReceipt? RecordCompletedInitialGuide(
        Phd2StateSnapshot snapshot, long selectionConnectionEpoch, Phd2SettleResult returned,
        bool hasExactLockHistory) =>
        !hasExactLockHistory && returned.Succeeded && !returned.NativeCalibrationOccurred &&
        snapshot.AppState == Phd2AppState.Guiding &&
        CanRecordCompletedGuideOwner(snapshot, selectionConnectionEpoch, returned)
            ? new(snapshot.ConnectionEpoch, snapshot.GuideEpoch, snapshot.LastSettleOperationId!.Value,
                snapshot.LockPosition!, snapshot.EventSequence, snapshot.CalibrationChangeSequence,
                snapshot.LastConfigurationChange?.EventSequence)
            : null;

    internal static bool CanStopInitialGuideAfterSingleLoss(
        Phd2StateSnapshot snapshot, bool guidingStartedByRun,
        Phd2CompletedInitialGuideStopReceipt? receipt, Phd2Point? actualLock,
        bool hasExactLockHistory) =>
        receipt is not null && !hasExactLockHistory &&
        snapshot.AppState == Phd2AppState.LostLock &&
        // One native loss invalidates the successful guide epoch. Do not
        // bridge another stop/start, reselect, dither, pause/resume or loss.
        // Repeated/ambiguous lifecycle changes retain the ordinary hard stop.
        receipt.GuideEpoch < long.MaxValue && snapshot.GuideEpoch == receipt.GuideEpoch + 1 &&
        snapshot.EventSequence > receipt.EventSequence &&
        snapshot.CalibrationChangeSequence == receipt.CalibrationChangeSequence &&
        snapshot.LastConfigurationChange?.EventSequence == receipt.ConfigurationChangeSequence &&
        snapshot.LastSettleOperationId is null && snapshot.LastSettle is null &&
        snapshot.GuideOutput?.Failed != true &&
        CanStopOwnedSession(snapshot, guidingStartedByRun, receipt.ConnectionEpoch, receipt.Lock, actualLock);

    // Only classify a correlated terminal BEFORE exact-lock history, with
    // current native telemetry and no loss/output/calibration failure.
    // This rejects the target, never promotes unstable guiding to science.
    internal static bool CanClassifyInitialSettleQualityFailure(Phd2StateSnapshot snapshot,
        long selectionConnectionEpoch, Phd2SettleResult result, bool hasExactLockHistory) =>
        !hasExactLockHistory && !result.Succeeded && !result.NativeCalibrationOccurred &&
        result.TotalFrames >= 3 && result.DroppedFrames == 0 &&
        snapshot.AppState == Phd2AppState.Guiding &&
        snapshot.GuideOutput is { Failed: false, ConsecutiveMissingOutputs: 0 } &&
        CanRecordCompletedGuideOwner(snapshot, selectionConnectionEpoch, result);

    // Lost frames justify a checked stop + fresh star detection, never a
    // successful settle or a claim that clouds caused the loss.
    internal static bool CanClassifyInitialLostStarFailure(Phd2StateSnapshot snapshot,
        long selectionConnectionEpoch, Phd2SettleResult result, bool hasExactLockHistory) =>
        !hasExactLockHistory && !result.Succeeded && !result.NativeCalibrationOccurred &&
        result.DroppedFrames > 0 && snapshot.GuideOutput?.Failed != true &&
        CanRecordCompletedGuideOwner(snapshot, selectionConnectionEpoch, result);

    // NativeCalibrationOccurred is set by Phd2Client only for this operation's
    // CalibrationComplete event. Its later SettleDone can fail independently.
    // That does not undo calibration or make pre-calibration geometry usable:
    // allow the existing checked-stop / fresh-field path, NOT slit or science
    // authority. Drops during the initial return are retained as evidence.
    internal static bool CanReacquireAfterCalibrationSettleFailure(Phd2StateSnapshot snapshot,
        long selectionConnectionEpoch, Phd2SettleResult result, bool hasExactLockHistory) =>
        !hasExactLockHistory && !result.Succeeded && result.NativeCalibrationOccurred &&
        snapshot.GuideOutput?.Failed != true &&
        CanRecordCompletedGuideOwner(snapshot, selectionConnectionEpoch, result);

    // The native guide RPC may snap the selected lock to its first new-frame
    // centroid before StartGuiding (possibly after SettleBegin). Accept that lock only from the returned,
    // locally correlated terminal operation, even when settling failed.
    // This is stop authority, not a successful-settle or placement attestation.
    internal static bool CanRecordCompletedGuideOwner(Phd2StateSnapshot snapshot,
        long selectionConnectionEpoch, Phd2SettleResult returned) =>
        snapshot.IsConnected && !snapshot.AutomationPaused && !snapshot.Phd2Paused &&
        snapshot.AppState is Phd2AppState.Guiding or Phd2AppState.LostLock &&
        snapshot.ConnectionEpoch == selectionConnectionEpoch &&
        !snapshot.PendingSettleOperationId.HasValue &&
        snapshot.LastSettleOperationId.HasValue && snapshot.LastSettleCommandAccepted &&
        snapshot.LastSettleConnectionEpoch == snapshot.ConnectionEpoch &&
        snapshot.LastSettleGuideEpoch == snapshot.GuideEpoch &&
        snapshot.LastSettle == returned with { NativeCalibrationOccurred = false } &&
        snapshot.LockPosition is { } point && double.IsFinite(point.X) && double.IsFinite(point.Y);

    internal static bool CanRecordPreGuideOwner(Phd2StateSnapshot snapshot,
        long selectionConnectionEpoch, Phd2Point selected, Phd2Point? actual) =>
        snapshot.IsConnected && !snapshot.AutomationPaused && !snapshot.Phd2Paused &&
        snapshot.AppState is Phd2AppState.Selected or Phd2AppState.Looping &&
        !snapshot.PendingSettleOperationId.HasValue &&
        snapshot.ConnectionEpoch == selectionConnectionEpoch &&
        actual is not null && double.IsFinite(selected.X) && double.IsFinite(selected.Y) &&
        double.IsFinite(actual.X) && double.IsFinite(actual.Y) &&
        Math.Abs(selected.X - actual.X) <= 0.25 && Math.Abs(selected.Y - actual.Y) <= 0.25;

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
