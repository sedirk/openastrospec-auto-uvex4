using System.Globalization;
using UvexAdv.Observatory;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2StageFailurePolicyTests
{
    [Theory]
    [InlineData("PHD2_GUIDE_OUTPUT_UNAVAILABLE")]
    [InlineData("PHD2_GUIDE_OUTPUT_RETURN_PENDING")]
    [InlineData("PHD2_GUIDE_OUTPUT_STOP_UNCONFIRMED")]
    [InlineData("PHD2_GUIDE_OUTPUT_RECOVERY_EXHAUSTED")]
    [InlineData("PHD2_GUIDE_OUTPUT_RECONNECT_FAILED")]
    public void OutputFailureRemainsSpecificAndCannotGrantGenericRebuild(string code)
    {
        var gate = GateResult.Unknown(code, "native guide output unavailable");
        Assert.False(ObservationAutomaticRecoveryPolicy.For(ObservationStage.PlaceTargetOnSlit, gate).IsRecoverable);
        var chinese = ObservationUiPresentation.Present(ObservationStage.PlaceTargetOnSlit, gate, new CultureInfo("zh-CN"));
        var english = ObservationUiPresentation.Present(ObservationStage.PlaceTargetOnSlit, gate, new CultureInfo("en-US"));
        Assert.DoesNotContain("当前质量门", chinese.Summary);
        Assert.Contains("输出", chinese.Summary);
        Assert.Contains("脉冲", chinese.Recommendation);
        Assert.Contains("不是精度", chinese.AutomaticRecovery);
        Assert.False(ObservationUiPresentation.ContainsCjk(english.Summary));
        Assert.False(ObservationUiPresentation.ContainsCjk(english.Recommendation));
    }

    [Fact]
    public void SharedOutputRecoveryRetainsReturnDebtAndDoesNotLoopCoarseBudget()
    {
        var placement = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.Phd2SlitPlacement.cs"));
        var recovery = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.GuideOutput.cs"));
        var check = placement.IndexOf("if (phd2.Snapshot.GuideOutput?.Failed == true)", placement.IndexOf("catch (Exception ex)", placement.IndexOf("var lockOrigin =", StringComparison.Ordinal), StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(check >= 0);
        Assert.Contains("RecoverPhd2GuideOutputBeforeMotionAsync", placement[check..]);
        Assert.True(check < placement.IndexOf("IsStructuredPhd2GuideSessionLoss(ex)", check, StringComparison.Ordinal));
        Assert.Contains("Math.Max(3, policy.RequiredFreshResidualsPerLockShiftStage)", placement);
        Assert.Contains("Phase = Phd2LockShiftPendingPhase.ReturnRequired", recovery);
        Assert.Contains("if (halted.Gate.Code != Phd2GuideOutputStatus.FailureCode) return halted", recovery);
        Assert.Contains("phd2OutputReconnectAttempts >= 1", recovery);
        Assert.Contains("ReconnectEquipmentAfterOutputFailureAsync", recovery);
        Assert.Contains("allowChargedCurrentPositionHandoff: true", recovery);
        Assert.Contains("actualPulseResponseValidated = false", recovery);
        Assert.DoesNotContain("SetExactLockPositionAsync", recovery);
        Assert.DoesNotContain("Slew", recovery);
        Assert.DoesNotContain("pendingPhd2LockShift = null", recovery);
    }

    [Theory]
    [InlineData("fresh guiding-frame evidence", Phd2StageFailurePolicy.FrameTimeout)]
    [InlineData("get_app_state", Phd2StageFailurePolicy.StatusTimeout)]
    public void TypedTimeoutHasSpecificPresentationButNeverGrantsRestart(string operation, string code)
    {
        var error = new Phd2CommandTimeoutException(operation, TimeSpan.FromSeconds(20));
        Assert.Equal(code, Phd2StageFailurePolicy.CodeFor(error));
        var gate = GateResult.Unknown(code, error.Message);
        Assert.False(ObservationAutomaticRecoveryPolicy.For(ObservationStage.SelectAtrExposure, gate).IsRecoverable);
        var chinese = ObservationUiPresentation.Present(ObservationStage.SelectAtrExposure, gate, new CultureInfo("zh-CN"));
        var english = ObservationUiPresentation.Present(ObservationStage.SelectAtrExposure, gate, new CultureInfo("en-US"));
        Assert.DoesNotContain("未分类", chinese.Summary);
        Assert.Contains("PHD2", chinese.Summary);
        Assert.Contains("USB/供电", chinese.Recommendation);
        Assert.Contains("不能复用旧帧", chinese.AutomaticRecovery);
        Assert.Contains("PHD2", english.Recommendation);
        Assert.False(ObservationUiPresentation.ContainsCjk(english.Recommendation));
        Assert.Equal(ObservationPreviewChannel.G3SlitField,
            ObservationOperatorGuidance.For(ObservationStage.SelectAtrExposure, gate, new CultureInfo("zh-CN")).PreviewChannel);
    }

    [Fact]
    public void AlertTextOrUncertainMutationTimeoutCannotAcquireARecoveryClassification()
    {
        Assert.Null(Phd2StageFailurePolicy.CodeFor(new InvalidOperationException("fresh guiding-frame evidence: camera disconnected")));
        foreach (var operation in new[] { "guide", "save_image", "set_lock_position", "capture_single_frame", "stop_capture" })
            Assert.Null(Phd2StageFailurePolicy.CodeFor(new Phd2CommandTimeoutException(operation, TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public void RealRunnerRetainsCleanupAndOriginalTypedFailure()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var start = source.IndexOf("var failureCode = Phd2StageFailurePolicy.CodeFor(ex)", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("private async Task<(bool Retry, StageResult Result)>", start, StringComparison.Ordinal);
        var handler = source[start..end];
        Assert.Contains("CleanupAfterFailureAsync", handler);
        Assert.Contains("InvalidateStageState(stage)", handler);
        Assert.Contains("ownerAtFailure.LastAlert", handler);
        Assert.Contains("Attention(stage, failureCode", handler);
        Assert.DoesNotContain("PrepareAutomaticStageRecoveryAsync", handler);
    }
}
