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
        Assert.Contains("光谱仪导星相机解算", first.Notification.Body, StringComparison.Ordinal);
        Assert.Contains("G3_MAIN_FOCUS_UNVERIFIED", first.Notification.Body, StringComparison.Ordinal);
        Assert.Contains("光谱仪导星相机/WCS 证据不足", first.Notification.Body, StringComparison.Ordinal);
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
    [InlineData(ObservationRunState.Idle)]
    [InlineData(ObservationRunState.RunningAuto)]
    [InlineData(ObservationRunState.Finalizing)]
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

    [Fact]
    public void CompletedReportsTargetAndAcceptedScienceFramesNotAnOldWarning()
    {
        var tracker = new ObservationAttentionNotificationTracker();
        var notification = tracker.Evaluate(Completed(), GateResult.Unknown("OLD_WARNING", "old failure"),
            CultureInfo.GetCultureInfo("zh-CN"), new("run-1", "WR 152", 3, "real")).Notification;

        Assert.NotNull(notification);
        Assert.Equal(ObservationAttentionSeverity.Success, notification.Severity);
        Assert.Equal("OpenAstroSpec 目标观测完成", notification.Title);
        Assert.Contains("WR 152", notification.Body);
        Assert.Contains("已接受科学帧：3 张", notification.Body);
        Assert.Contains("运行：run-1", notification.Body);
        Assert.Contains("设备收口状态请查看运行报告", notification.Body);
        Assert.DoesNotContain("OLD_WARNING", notification.Body);
    }

    [Fact]
    public void CompletionIsOncePerRunDespiteRefreshCultureOrInterveningIdle()
    {
        var tracker = new ObservationAttentionNotificationTracker();
        Assert.NotNull(tracker.Evaluate(Completed(), null).Notification);
        var duplicate = tracker.Evaluate(Completed() with { UpdatedUtc = DateTimeOffset.UtcNow.AddMinutes(1) }, null);
        Assert.Null(duplicate.Notification);
        Assert.False(duplicate.ClearActiveIndicator);
        Assert.True(tracker.Evaluate(ObservationSnapshot.Idle, null).ClearActiveIndicator);
        Assert.Null(tracker.Evaluate(Completed(), null, CultureInfo.GetCultureInfo("en-US")).Notification);
        Assert.NotNull(tracker.Evaluate(Completed() with { ObservationRunId = "run-2" }, null).Notification);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void AnonymousCompletionCannotNotify(string? runId)
    {
        Assert.Null(new ObservationAttentionNotificationTracker().Evaluate(
            Completed() with { ObservationRunId = runId }, null).Notification);
    }

    [Fact]
    public void StaleContextCannotSupplyAnotherTargetsNameCountOrSimulationMode()
    {
        var notification = new ObservationAttentionNotificationTracker().Evaluate(Completed(), null,
            CultureInfo.GetCultureInfo("en-US"), new("other-run", "Wrong target", 999, "simulator")).Notification!;
        Assert.Equal("OpenAstroSpec target observation completed", notification.Title);
        Assert.Contains("Target name unavailable", notification.Body);
        Assert.Contains("Science frame count unavailable", notification.Body);
        Assert.DoesNotContain("Wrong target", notification.Body);
        Assert.DoesNotContain("999", notification.Body);
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public void SimulationCompletionIsExplicitlyNotRealAcquisition(string culture)
    {
        var notification = new ObservationAttentionNotificationTracker().Evaluate(Completed(), null,
            CultureInfo.GetCultureInfo(culture), new("run-1", "Test target", 3, "simulator")).Notification!;
        Assert.Equal(ObservationAttentionSeverity.Success, notification.Severity);
        if (culture == "zh-CN")
        {
            Assert.Contains("模拟", notification.Title);
            Assert.Contains("未采集真实科学帧", notification.Body);
        }
        else
        {
            Assert.Contains("simulated", notification.Title);
            Assert.Contains("no real science frames acquired", notification.Body);
            Assert.False(ObservationUiPresentation.ContainsCjk(notification.Body));
        }
    }

    [Fact]
    public void RecoveredBlockerCanCompleteAndNextRunCanStillWarn()
    {
        var tracker = new ObservationAttentionNotificationTracker();
        var blocked = Snapshot(ObservationRunState.PausedNeedsAttention, ObservationStage.StartGuiding,
            "PHD2 lost lock", "PHD_SETTLE_FAILED");
        Assert.Equal(ObservationAttentionSeverity.Warning, tracker.Evaluate(blocked, null).Notification!.Severity);
        Assert.Equal(ObservationAttentionSeverity.Success, tracker.Evaluate(Completed(), null).Notification!.Severity);
        Assert.Null(tracker.Evaluate(Completed(), null).Notification);
        Assert.Equal(ObservationAttentionSeverity.Warning, tracker.Evaluate(
            blocked with { ObservationRunId = "run-2" }, null).Notification!.Severity);
    }

    private static ObservationSnapshot Completed() => ObservationSnapshot.Idle with
    {
        ObservationRunId = "run-1", State = ObservationRunState.Completed, CompletedStageCount = 11,
    };

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
