using System.Globalization;
using UvexAdv.Observatory;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2StageFailurePolicyTests
{
    [Theory]
    [InlineData("Outbound exact-lock stage failed or became ambiguous:")]
    [InlineData("Post-lock operation-bound settle failed:")]
    [InlineData("Fresh post-stage G3 residual failed:")]
    public void InnerReturnHandlersCannotSwallowTypedGuideLoss(string returnReason)
    {
        var source = WorkflowSource.Convergence;
        var reasonIndex = source.IndexOf(returnReason, StringComparison.Ordinal);
        Assert.True(reasonIndex >= 0);
        var catchIndex = source.LastIndexOf("catch (Exception ex)", reasonIndex, StringComparison.Ordinal);
        Assert.True(catchIndex >= 0);
        var handler = source[catchIndex..reasonIndex];
        Assert.StartsWith("catch (Exception ex) when (Phd2StageFailurePolicy.FindLostFrame(ex) is null)", handler);
        Assert.Contains("ReturnPhd2LockToOriginAsync", handler);
    }

    [Fact]
    public void NativeFrameFourteenLossRetainsCaptureEvenIfOwnerLaterRecovers()
    {
        var lostStep = new Phd2GuideStep(14, null, null, 0, null, null, 2);
        var snapshot = Phd2StateSnapshot.Disconnected with
        {
            IsConnected = true, AppState = Phd2AppState.LostLock,
            ConnectionEpoch = 1, GuideEpoch = 104, LastGuideStep = lostStep,
        };
        var error = new Phd2GuidingFrameLostException(snapshot);
        var laterSnapshot = snapshot with { AppState = Phd2AppState.Guiding, GuideEpoch = 105, LastGuideStep = null };
        var found = Phd2StageFailurePolicy.FindLostFrame(new InvalidOperationException("frame wrapper", error));
        Assert.NotNull(found);
        Assert.Same(error, found);
        Assert.Equal(14, found.LostFrame!.Frame);
        Assert.Equal(0, found.LostFrame.Snr);
        Assert.Equal(2, found.LostFrame.ErrorCode);
        Assert.Equal(Phd2AppState.LostLock, found.Snapshot.AppState);
        Assert.NotEqual(found.Snapshot.GuideEpoch, laterSnapshot.GuideEpoch);
        // The same message without the native typed event is not a loss authority.
        Assert.Null(Phd2StageFailurePolicy.FindLostFrame(new InvalidOperationException(error.Message)));
    }

    [Fact]
    public void PlacementLossRetainsTypedEventWithoutGrantingAnotherRecovery()
    {
        var loss = new Phd2GuidingFrameLostException(Phd2StateSnapshot.Disconnected with
            { IsConnected = true, AppState = Phd2AppState.LostLock });
        Assert.Same(loss, Phd2StageFailurePolicy.FindLostFrame(new InvalidOperationException("wrapper", loss)));
        Assert.Null(Phd2StageFailurePolicy.FindLostFrame(new InvalidOperationException("StarLost wind timeout")));
        var gate = GateResult.Unknown(Phd2StageFailurePolicy.PlacementStarLost, loss.Message);
        Assert.False(ObservationAutomaticRecoveryPolicy.For(ObservationStage.PlaceTargetOnSlit, gate).IsRecoverable);
        var presentation = ObservationUiPresentation.Present(ObservationStage.PlaceTargetOnSlit, gate, new CultureInfo("zh-CN"));
        Assert.Contains("丢星", presentation.Summary);
        Assert.Contains("不只是", presentation.Summary);
        Assert.Contains("不重置预算", presentation.AutomaticRecovery);
        var guidance = ObservationOperatorGuidance.For(ObservationStage.PlaceTargetOnSlit, gate, new CultureInfo("zh-CN"));
        Assert.Equal(ObservationPreviewChannel.G3SlitField, guidance.PreviewChannel);
    }

    [Fact]
    public void PlacementTerminalLossRecordsStopWithoutClearingMotionDebt()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.Phd2SlitPlacement.cs"));
        var start = source.IndexOf("var lostFrameFailure =", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("private async Task<Exception?>", start, StringComparison.Ordinal);
        var handler = source[start..end];
        Assert.Contains("phd2-placement-star-lost", handler);
        Assert.Contains("stopConfirmed = true", handler);
        Assert.Contains("windCauseConfirmed = false", handler);
        Assert.Contains("Phd2StageFailurePolicy.PlacementStarLost", handler);
        Assert.Contains("Phase = Phd2LockShiftPendingPhase.ReturnRequired", handler);
        Assert.Contains("Phd2LockShiftPendingStore.WriteAtomicAsync", handler);
        Assert.Contains("finally", handler);
        Assert.Contains("lostGuideFrame", handler);
        Assert.DoesNotContain("AttemptsUsed =", handler);
        Assert.DoesNotContain("CumulativeCommandedPixels =", handler);
        Assert.DoesNotContain("GuideEpoch =", handler);
        Assert.DoesNotContain("pendingPhd2LockShift = null", handler);
        Assert.DoesNotContain("SetExactLockPositionAsync", handler);
    }

    [Fact]
    public void TypedLostStarUsesExistingBoundedRecoveryNotTimeoutClassification()
    {
        var error = new Phd2GuidingFrameLostException(Phd2StateSnapshot.Disconnected with
            { IsConnected = true, AppState = Phd2AppState.LostLock });
        Assert.Equal("GUIDING_LOST", Phd2StageFailurePolicy.CodeFor(error));
        var plan = ObservationAutomaticRecoveryPolicy.For(ObservationStage.SelectAtrExposure,
            GateResult.Unknown("GUIDING_LOST", error.Message));
        Assert.True(plan.IsRecoverable);
        Assert.Equal(1, plan.MaximumAttempts);
    }
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
    [InlineData("native single-frame capture", Phd2StageFailurePolicy.NativeFrameTimeout)]
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
