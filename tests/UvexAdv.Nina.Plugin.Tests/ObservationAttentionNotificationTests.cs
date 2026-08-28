using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class ObservationAttentionNotificationTests
{
    [Fact]
    public void NeedsAttentionProducesOneWarningWithStageCodeAndReason()
    {
        var tracker = new ObservationAttentionNotificationTracker();
        var snapshot = Snapshot(
            ObservationRunState.PausedNeedsAttention,
            ObservationStage.AcquireG3SlitField,
            "等待人工处理",
            "G3_FOCUS_STARS_NOT_DETECTED");
        var gate = GateResult.Unknown("G3_MAIN_FOCUS_UNVERIFIED", "没有可靠的星核证据");

        var first = tracker.Evaluate(snapshot, gate);
        var duplicate = tracker.Evaluate(snapshot, gate);

        Assert.NotNull(first.Notification);
        Assert.Equal(ObservationAttentionSeverity.Warning, first.Notification!.Severity);
        Assert.Contains("G3 fresh", first.Notification.Body, StringComparison.Ordinal);
        Assert.Contains("G3_MAIN_FOCUS_UNVERIFIED", first.Notification.Body, StringComparison.Ordinal);
        Assert.Contains("没有可靠的星核证据", first.Notification.Body, StringComparison.Ordinal);
        Assert.Null(duplicate.Notification);
        Assert.False(duplicate.ClearActiveIndicator);
    }

    [Fact]
    public void FaultProducesErrorAndIgnoresCleanupEventForItsIdentity()
    {
        var tracker = new ObservationAttentionNotificationTracker();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new ObservationSnapshot(
            "run-1",
            ObservationRunState.Faulted,
            ObservationStage.RunScienceBlock,
            null,
            "camera transport failed",
            null,
            9,
            11,
            now,
            new[]
            {
                new ObservationEvent(now.AddSeconds(-1), ObservationRunState.Faulted, ObservationStage.RunScienceBlock, "RUN_FAULTED", "camera transport failed"),
                new ObservationEvent(now, ObservationRunState.Faulted, ObservationStage.RunScienceBlock, "FAULT_CLEANUP_COMPLETED", "cleanup complete"),
            });

        var result = tracker.Evaluate(snapshot, null);

        Assert.NotNull(result.Notification);
        Assert.Equal(ObservationAttentionSeverity.Error, result.Notification!.Severity);
        Assert.Contains("RUN_FAULTED", result.Notification.Body, StringComparison.Ordinal);
        Assert.Contains("camera transport failed", result.Notification.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ObservationRunState.Paused)]
    [InlineData(ObservationRunState.ManualTakeover)]
    [InlineData(ObservationRunState.Cancelled)]
    [InlineData(ObservationRunState.Completed)]
    public void OperatorAndNormalStatesDoNotNotify(ObservationRunState state)
    {
        var tracker = new ObservationAttentionNotificationTracker();

        var result = tracker.Evaluate(
            Snapshot(state, ObservationStage.StartGuiding, "operator action", "RUN_PAUSED"),
            null);

        Assert.Null(result.Notification);
        Assert.False(result.ClearActiveIndicator);
    }

    [Fact]
    public void RecoveryRearmsTheSameBlocker()
    {
        var tracker = new ObservationAttentionNotificationTracker();
        var blocked = Snapshot(
            ObservationRunState.PausedNeedsAttention,
            ObservationStage.StartGuiding,
            "PHD2 lost lock",
            "PHD_SETTLE_FAILED");

        Assert.NotNull(tracker.Evaluate(blocked, null).Notification);
        var recovered = tracker.Evaluate(
            Snapshot(ObservationRunState.Validating, ObservationStage.StartGuiding, "revalidating", "RESUME_REVALIDATING"),
            null);
        Assert.True(recovered.ClearActiveIndicator);
        Assert.NotNull(tracker.Evaluate(blocked, null).Notification);
    }

    private static ObservationSnapshot Snapshot(
        ObservationRunState state,
        ObservationStage stage,
        string message,
        string code)
    {
        var now = DateTimeOffset.UtcNow;
        return new ObservationSnapshot(
            "run-1",
            state,
            stage,
            stage,
            message,
            state == ObservationRunState.PausedNeedsAttention ? message : null,
            3,
            11,
            now,
            new[] { new ObservationEvent(now, state, stage, code, message) });
    }
}
