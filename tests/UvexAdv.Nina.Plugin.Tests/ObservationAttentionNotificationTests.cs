using System.Globalization;
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

        var culture = CultureInfo.GetCultureInfo("zh-CN");
        var first = tracker.Evaluate(snapshot, gate, culture);
        var duplicate = tracker.Evaluate(snapshot, gate, culture);

        Assert.NotNull(first.Notification);
        Assert.Equal(ObservationAttentionSeverity.Warning, first.Notification!.Severity);
        Assert.Contains("G3 解算", first.Notification.Body, StringComparison.Ordinal);
        Assert.Contains("G3_MAIN_FOCUS_UNVERIFIED", first.Notification.Body, StringComparison.Ordinal);
        Assert.Contains("G3/WCS 证据不足", first.Notification.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("没有可靠的星核证据", first.Notification.Body, StringComparison.Ordinal);
        Assert.Contains("打开“诊断与证据”", first.Notification.Body, StringComparison.Ordinal);
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

        var result = tracker.Evaluate(snapshot, null, CultureInfo.GetCultureInfo("zh-CN"));

        Assert.NotNull(result.Notification);
        Assert.Equal(ObservationAttentionSeverity.Error, result.Notification!.Severity);
        Assert.Contains("RUN_FAULTED", result.Notification.Body, StringComparison.Ordinal);
        Assert.Contains("当前质量门未通过", result.Notification.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("camera transport failed", result.Notification.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void EnglishNotificationUsesEnglishPresentationAndKeepsCodeStable()
    {
        var tracker = new ObservationAttentionNotificationTracker();
        var snapshot = Snapshot(
            ObservationRunState.PausedNeedsAttention,
            ObservationStage.StartGuiding,
            "raw adapter detail",
            "PHD2_NATIVE_GUIDE_GEOMETRY_REJECTED");
        var gate = GateResult.Fail(
            "PHD2_NATIVE_GUIDE_GEOMETRY_REJECTED",
            "PHD2 selected a star outside the detector-edge safety envelope.");

        var result = tracker.Evaluate(snapshot, gate, CultureInfo.GetCultureInfo("en-US"));

        Assert.NotNull(result.Notification);
        Assert.Equal("OpenAstroSpec automation needs attention", result.Notification!.Title);
        Assert.Contains("Stage: Select guide star", result.Notification.Body, StringComparison.Ordinal);
        Assert.Contains("Code: PHD2_NATIVE_GUIDE_GEOMETRY_REJECTED", result.Notification.Body, StringComparison.Ordinal);
        Assert.False(ObservationUiPresentation.ContainsCjk(result.Notification.Body));
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
