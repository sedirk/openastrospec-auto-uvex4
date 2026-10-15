using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2DependencyRebuildStopPolicyTests
{
    [Theory]
    [InlineData("skip")]
    [InlineData("cleanup-failure")]
    [InlineData("late-unsafe")]
    [InlineData("late-cancel")]
    [InlineData("late-deadline")]
    public async Task RecordedInitialLossTerminalExecutesNextNativeTargetOnlyAfterStoppedBoundary(string mode)
    {
        var result = new Phd2SettleResult(false, "timed-out waiting for guider to settle", 71, 45,
            DateTimeOffset.Parse("2026-10-13T17:34:09Z"));
        var snapshot = CompletedInitialGuide() with { AppState = Phd2AppState.LostLock,
            LastSettle = result, LockPosition = new(368.021, 29.797) };
        Assert.True(Phd2DependencyRebuildStopPolicy.CanClassifyInitialLostStarFailure(snapshot, 1, result, false));
        var gate = Phd2DependencyRebuildStopPolicy.AfterConfirmedInitialGuideStop(result, lostStars: true);
        Assert.False(NativeSequencePlan.IsSkippableQualityFailure(
            UvexAdv.Observatory.GateResult.Unknown(gate.Code, "Unconfirmed owner/stop is not optical quality")));
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(mode, gate.Code, gate,
            UvexAdv.Observatory.ObservationStage.PlaceTargetOnSlit);
    }

    [Theory]
    [InlineData(Phd2AppState.Guiding, 0)]
    [InlineData(Phd2AppState.Guiding, 3)]
    [InlineData(Phd2AppState.LostLock, 3)]
    public void CompletedCalibrationWithFailedSettleRequiresFreshFieldNotGenericFaultOrArrival(
        Phd2AppState state, int dropped)
    {
        // 2026-10-11 02:54:13: calibration completed, 41 settle frames,
        // three dropped. This combination used to bypass every typed branch.
        var result = new Phd2SettleResult(false, "timed-out waiting for guider to settle", 41, dropped,
            DateTimeOffset.Parse("2026-10-10T18:54:13Z")) { NativeCalibrationOccurred = true };
        var snapshot = CompletedInitialGuide() with { AppState = state,
            LastSettle = result with { NativeCalibrationOccurred = false } };
        Assert.True(Phd2DependencyRebuildStopPolicy.CanReacquireAfterCalibrationSettleFailure(snapshot, 1, result, false));
        Assert.False(snapshot.HasCurrentSuccessfulSettle);
        Assert.False(Phd2DependencyRebuildStopPolicy.CanClassifyInitialSettleQualityFailure(snapshot, 1, result, false));
        Assert.Null(Phd2DependencyRebuildStopPolicy.RecordCompletedInitialGuide(snapshot, 1, result, false));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanReacquireAfterCalibrationSettleFailure(snapshot, 1, result, true));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanReacquireAfterCalibrationSettleFailure(snapshot, 2, result, false));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanReacquireAfterCalibrationSettleFailure(snapshot, 1,
            result with { NativeCalibrationOccurred = false }, false));
        foreach (var bad in new[] { snapshot with { PendingSettleOperationId = 2 },
            snapshot with { LastSettleCommandAccepted = false }, snapshot with { LastSettleOperationId = null },
            snapshot with { LastSettle = null }, snapshot with { LastSettleGuideEpoch = 120 },
            snapshot with { AppState = Phd2AppState.Calibrating }, snapshot with { AutomationPaused = true },
            snapshot with { Phd2Paused = true }, snapshot with { IsConnected = false },
            snapshot with { GuideOutput = new(1, 3, result.CompletedUtc, result.CompletedUtc, true,
                new(1, 1, 1, 20, 3, 2, 0, "OnStep", 3, 3, 0, 0)) } })
            Assert.False(Phd2DependencyRebuildStopPolicy.CanReacquireAfterCalibrationSettleFailure(bad, 1, result, false));
    }

    [Fact]
    public void CalibrationSettleFailureReachesExistingCheckedStopAndFreshFieldWithoutMaskingItsResult()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.Phd2SlitPlacement.cs"));
        var decision = source.IndexOf("var calibrationNeedsFreshField =", StringComparison.Ordinal);
        var reject = source.IndexOf("if (!settle.Succeeded && !windSampledSettle && !calibrationNeedsFreshField)", decision, StringComparison.Ordinal);
        var branch = source.IndexOf("if (settle.NativeCalibrationOccurred)", reject, StringComparison.Ordinal);
        var stop = source.IndexOf("await StopPhdAndWaitAsync", branch, StringComparison.Ordinal);
        var idle = source.IndexOf("await WaitForPostCalibrationMountIdleAsync", stop, StringComparison.Ordinal);
        var acquire = source.IndexOf("var reacquired = await AcquireG3SlitFieldAsync", idle, StringComparison.Ordinal);
        var recurse = source.IndexOf("postCalibrationReacquisitionDepth + 1", acquire, StringComparison.Ordinal);
        Assert.True(decision >= 0 && reject > decision && branch > reject && stop > branch && idle > stop && acquire > idle && recurse > acquire);
        Assert.Contains("return reacquired;", source[acquire..recurse]);
        Assert.DoesNotContain("POST_CALIBRATION_G3_REACQUISITION_BLOCKED", source[acquire..recurse]);
        Assert.Contains("nativeSettle = settle", source[branch..stop]);
        Assert.DoesNotContain("Succeeded = true", source[decision..recurse]);
    }

    [Fact]
    public void DroppedInitialFramesPermitOnlyCorrelatedStopAndStarDetectionNotQualityPass()
    {
        var s = CompletedInitialGuide();
        var lost = s.LastSettle! with { Succeeded = false, DroppedFrames = 3 };
        s = s with { LastSettle = lost };
        Assert.True(Phd2DependencyRebuildStopPolicy.CanClassifyInitialLostStarFailure(s, 1, lost, false));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanClassifyInitialSettleQualityFailure(s, 1, lost, false));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanClassifyInitialLostStarFailure(s, 1, lost, true));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanClassifyInitialLostStarFailure(s, 2, lost, false));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanClassifyInitialLostStarFailure(s, 1, lost with { NativeCalibrationOccurred = true }, false));
        foreach (var bad in new[] { s with { IsConnected = false }, s with { Phd2Paused = true },
            s with { LastSettleOperationId = null }, s with { LastSettleCommandAccepted = false },
            s with { GuideEpoch = s.GuideEpoch + 1 }, s with { AppState = Phd2AppState.Calibrating } })
            Assert.False(Phd2DependencyRebuildStopPolicy.CanClassifyInitialLostStarFailure(bad, 1, lost, false));
    }
    [Fact]
    public void RecordedHd18552InitialGuideLossCanBeCheckedStoppedBeforePlacementSessionExists()
    {
        // Immutable 2026-10-09T18:53:58Z guide operation: conn 1, epoch 121,
        // native lock differs legitimately from the earlier selected point.
        // At 18:54:08Z the native guide residual grew 4.62 -> 67.22 px and
        // both axes reported 2500 ms pulses. This receipt does NOT fix that
        // instability or promote its frames; it authorizes stopping our owner.
        var completed = CompletedInitialGuide();
        var receipt = Phd2DependencyRebuildStopPolicy.RecordCompletedInitialGuide(
            completed, 1, completed.LastSettle!, hasExactLockHistory: false);
        Assert.NotNull(receipt);
        Assert.Equal(1, receipt.OperationId);
        Assert.Equal(new Phd2Point(1128.076, 517.658), receipt.Lock);

        var lost = SingleLoss(completed);
        Assert.False(lost.HasCurrentSuccessfulSettle);
        Assert.True(Phd2DependencyRebuildStopPolicy.CanStopInitialGuideAfterSingleLoss(
            lost, true, receipt, new(1128.08, 517.66), hasExactLockHistory: false));
        Assert.False(lost.HasCurrentSuccessfulSettle);
        Assert.Null(lost.LastSettle);
    }

    [Fact]
    public void InitialStopReceiptCannotBeCreatedFromSelectionReadbackTimeoutOrCalibration()
    {
        var completed = CompletedInitialGuide();
        var result = completed.LastSettle!;
        Assert.Null(Phd2DependencyRebuildStopPolicy.RecordCompletedInitialGuide(completed, 1, result, true));
        Assert.Null(Phd2DependencyRebuildStopPolicy.RecordCompletedInitialGuide(completed, 2, result, false));
        Assert.Null(Phd2DependencyRebuildStopPolicy.RecordCompletedInitialGuide(
            completed, 1, result with { CompletedUtc = result.CompletedUtc.AddSeconds(1) }, false));
        foreach (var other in new[] { result with { Succeeded = false }, result with { NativeCalibrationOccurred = true } })
            Assert.Null(Phd2DependencyRebuildStopPolicy.RecordCompletedInitialGuide(
                completed with { LastSettle = other }, 1, other, false));
        foreach (var other in new[] { completed with { LastSettleOperationId = null },
            completed with { LastSettleCommandAccepted = false }, completed with { LastSettleConnectionEpoch = 2 },
            completed with { LastSettleGuideEpoch = 120 }, completed with { PendingSettleOperationId = 2 },
            completed with { AppState = Phd2AppState.Selected }, completed with { AppState = Phd2AppState.LostLock },
            completed with { IsConnected = false }, completed with { AutomationPaused = true },
            completed with { Phd2Paused = true }, completed with { LockPosition = null } })
            Assert.Null(Phd2DependencyRebuildStopPolicy.RecordCompletedInitialGuide(other, 1, result, false));
    }

    [Fact]
    public void InitialStopReceiptRefusesMissingReceiptMotionHistoryNewConnectionAndChangedLock()
    {
        var completed = CompletedInitialGuide();
        var receipt = Phd2DependencyRebuildStopPolicy.RecordCompletedInitialGuide(completed, 1, completed.LastSettle!, false);
        var lost = SingleLoss(completed);
        var actual = receipt!.Lock;
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopInitialGuideAfterSingleLoss(lost, false, receipt, actual, false));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopInitialGuideAfterSingleLoss(lost, true, null, actual, false));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopInitialGuideAfterSingleLoss(lost, true, receipt, actual, true));
        foreach (var point in new Phd2Point?[] { null, new(actual.X + 1, actual.Y), new(double.NaN, actual.Y),
            new(actual.X, double.PositiveInfinity) })
            Assert.False(Phd2DependencyRebuildStopPolicy.CanStopInitialGuideAfterSingleLoss(lost, true, receipt, point, false));
        foreach (var other in new[] { lost with { ConnectionEpoch = 2 }, lost with { IsConnected = false },
            lost with { AutomationPaused = true }, lost with { Phd2Paused = true },
            lost with { PendingSettleOperationId = 2 }, lost with { CalibrationChangeSequence = 7 },
            lost with { LastConfigurationChange = new(162, DateTimeOffset.UtcNow, null, null, false) },
            lost with { LastSettleOperationId = 2 }, lost with { LastSettle = completed.LastSettle },
            lost with { EventSequence = completed.EventSequence } })
            Assert.False(Phd2DependencyRebuildStopPolicy.CanStopInitialGuideAfterSingleLoss(other, true, receipt, actual, false));
    }

    [Theory]
    [InlineData(Phd2AppState.Guiding, 0)]
    [InlineData(Phd2AppState.Guiding, 1)]
    [InlineData(Phd2AppState.LostLock, 0)]
    [InlineData(Phd2AppState.LostLock, 2)]
    [InlineData(Phd2AppState.LostLock, 3)]
    [InlineData(Phd2AppState.Paused, 1)]
    [InlineData(Phd2AppState.Stopped, 1)]
    [InlineData(Phd2AppState.Selected, 1)]
    [InlineData(Phd2AppState.Looping, 1)]
    [InlineData(Phd2AppState.Calibrating, 1)]
    public void InitialReceiptDoesNotBridgeOtherLifecycleChangesOrRecoveredGuiding(Phd2AppState state, int epochChanges)
    {
        var completed = CompletedInitialGuide();
        var receipt = Phd2DependencyRebuildStopPolicy.RecordCompletedInitialGuide(completed, 1, completed.LastSettle!, false);
        var other = SingleLoss(completed) with { AppState = state, GuideEpoch = completed.GuideEpoch + epochChanges };
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopInitialGuideAfterSingleLoss(other, true, receipt, receipt!.Lock, false));
    }

    [Fact]
    public void ProductionInitialReceiptIsRecordedOnlyAfterCompletedGuideAndConsumedThroughExistingCheckedStop()
    {
        var placement = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.Phd2SlitPlacement.cs"));
        var start = placement.IndexOf("private async Task<StageResult> PlaceTargetOnSlitWithPhd2Async(", StringComparison.Ordinal);
        var clear = placement.IndexOf("phd2CompletedInitialGuideStopReceipt = null;", start, StringComparison.Ordinal);
        var completed = placement.IndexOf("CanRecordCompletedGuideOwner(", start, StringComparison.Ordinal);
        var record = placement.IndexOf("Phd2DependencyRebuildStopPolicy.RecordCompletedInitialGuide(", completed, StringComparison.Ordinal);
        var window = placement.IndexOf("firstMeasurements = await CapturePhd2PlacementGuideWindowAsync(", record, StringComparison.Ordinal);
        Assert.True(start >= 0 && clear > start && completed > clear && record > completed && window > record);
        Assert.Contains("pendingPhd2LockShift is not null || currentLedger is not null", placement[record..window]);

        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var helperStart = source.IndexOf("private async Task<GateResult> EnsurePhdStoppedForAutomaticRebuildAsync(", StringComparison.Ordinal);
        var helperEnd = source.IndexOf("public override async Task<GateResult> RevalidateAsync(", helperStart, StringComparison.Ordinal);
        var helper = source[helperStart..helperEnd];
        Assert.Contains("owned is null && stage == ObservationStage.PlaceTargetOnSlit", helper);
        Assert.Contains("sourceCode == \"PHD2_STRUCTURED_GUIDE_SESSION_LOSS\"", helper);
        Assert.DoesNotContain("phd2PreMotionOutputOwner", helper);
        var intent = helper.IndexOf("phd2-dependency-rebuild-stop-intent", StringComparison.Ordinal);
        var recheck = helper.IndexOf("CanStopInitialGuideAfterSingleLoss(", intent, StringComparison.Ordinal);
        var stop = helper.IndexOf("StopCaptureAndConfirmAsync", StringComparison.Ordinal);
        var confirmed = helper.IndexOf("ValidateConfirmedPhdStop(stopped,", StringComparison.Ordinal);
        var consume = helper.IndexOf("phd2CompletedInitialGuideStopReceipt = null;", StringComparison.Ordinal);
        Assert.True(intent >= 0 && recheck > intent && stop > recheck && confirmed > stop && consume > confirmed);
        Assert.Contains("stopAuthorityOnly = true, placementProven = false", helper);
        Assert.Contains("durableMotionBudgetsReset = false", helper);
        Assert.DoesNotContain("pendingPhd2LockShift = null", helper);
        Assert.DoesNotContain("GuideAndSettleAsync", helper);
        Assert.DoesNotContain("SetLockPosition", helper);
    }

    private static Phd2StateSnapshot CompletedInitialGuide()
    {
        var result = new Phd2SettleResult(true, null, 4, 0, DateTimeOffset.Parse("2026-10-09T18:53:58.2125361Z"));
        return Owned(Phd2AppState.Guiding) with { ConnectionEpoch = 1, GuideEpoch = 121, EventSequence = 158,
            LastSettle = result, LastSettleOperationId = 1, LastSettleCommandAccepted = true,
            LastSettleConnectionEpoch = 1, LastSettleGuideEpoch = 121, LockPosition = new(1128.076, 517.658) };
    }

    private static Phd2StateSnapshot SingleLoss(Phd2StateSnapshot completed) => completed with
    {
        AppState = Phd2AppState.LostLock, GuideEpoch = completed.GuideEpoch + 1,
        EventSequence = completed.EventSequence + 3, LastSettle = null, LastSettleOperationId = null,
        LastSettleCommandAccepted = false, LastSettleConnectionEpoch = null, LastSettleGuideEpoch = null,
    };

    [Fact]
    public void InitialSettleQualityRequiresNoMotionHistoryNoLossAndCurrentOutput()
    {
        var now = DateTimeOffset.UtcNow;
        var result = new Phd2SettleResult(false, "timeout", 45, 0, now);
        var output = new Phd2GuideOutputStatus(45, 0, now, now, false,
            new(45, 2, 2, 20, 4, 3, 0, "On-Step", 2, 2, 20, 40));
        var s = Owned(Phd2AppState.Guiding) with { LockPosition = new(770, 932), LastSettle = result,
            LastSettleOperationId = 8, LastSettleCommandAccepted = true,
            LastSettleConnectionEpoch = 7, LastSettleGuideEpoch = 98, GuideOutput = output };
        Assert.True(Phd2DependencyRebuildStopPolicy.CanClassifyInitialSettleQualityFailure(s, 7, result, false));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanClassifyInitialSettleQualityFailure(s, 7, result, true));
        foreach (var other in new[] { s with { GuideOutput = null }, s with { GuideOutput = output with { Failed = true } },
            s with { GuideOutput = output with { ConsecutiveMissingOutputs = 1 } }, s with { AppState = Phd2AppState.LostLock },
            s with { GuideEpoch = 99 }, s with { IsConnected = false } })
            Assert.False(Phd2DependencyRebuildStopPolicy.CanClassifyInitialSettleQualityFailure(other, 7, result, false));
        foreach (var other in new[] { result with { Succeeded = true }, result with { NativeCalibrationOccurred = true },
            result with { DroppedFrames = 1 }, result with { TotalFrames = 2 } })
            Assert.False(Phd2DependencyRebuildStopPolicy.CanClassifyInitialSettleQualityFailure(s with { LastSettle = other }, 7, other, false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeStartRecentringIsStopOwnedAfterCorrelatedTerminalEvenWhenSettleFails(bool success)
    {
        var result = new Phd2SettleResult(success, success ? null : "timed-out waiting for guider to settle", 53, 0, DateTimeOffset.UtcNow);
        var nativeLock = new Phd2Point(770.13, 931.92);
        var snapshot = Owned(Phd2AppState.Guiding) with { LockPosition = nativeLock, LastSettle = result,
            LastSettleOperationId = 8, LastSettleCommandAccepted = true,
            LastSettleConnectionEpoch = 7, LastSettleGuideEpoch = 98 };
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(
            snapshot, true, 7, new(768.5, 929.8), nativeLock));
        Assert.True(Phd2DependencyRebuildStopPolicy.CanRecordCompletedGuideOwner(snapshot, 7, result));
        Assert.True(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(snapshot, true, 7, snapshot.LockPosition, nativeLock));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(snapshot, true, 7, snapshot.LockPosition, new(772, 932)));
    }

    [Fact]
    public void CompletedGuideOwnershipRequiresCurrentAcceptedTerminalAndFiniteLock()
    {
        var result = new Phd2SettleResult(false, "timeout", 53, 0, DateTimeOffset.UtcNow);
        var s = Owned(Phd2AppState.Guiding) with { LockPosition = new(770.13, 931.92), LastSettle = result,
            LastSettleOperationId = 8, LastSettleCommandAccepted = true,
            LastSettleConnectionEpoch = 7, LastSettleGuideEpoch = 98 };
        Assert.False(Phd2DependencyRebuildStopPolicy.CanRecordCompletedGuideOwner(s, 6, result));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanRecordCompletedGuideOwner(s, 7, result with { CompletedUtc = result.CompletedUtc.AddSeconds(-1) }));
        foreach (var other in new[] { s with { IsConnected = false }, s with { AutomationPaused = true },
            s with { Phd2Paused = true }, s with { LastSettleCommandAccepted = false },
            s with { LastSettleOperationId = null }, s with { LastSettleConnectionEpoch = 6 },
            s with { LastSettleGuideEpoch = 97 }, s with { PendingSettleOperationId = 9 },
            s with { LastSettle = null }, s with { LockPosition = null },
            s with { LockPosition = new(double.NaN, 932) }, s with { AppState = Phd2AppState.Stopped } })
            Assert.False(Phd2DependencyRebuildStopPolicy.CanRecordCompletedGuideOwner(other, 7, result));
    }

    [Fact]
    public void ProductionCapturesNativeTerminalLockBeforeSettleFailureIsClassified()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.Phd2SlitPlacement.cs"));
        var completed = source.IndexOf("var guideProof = phd2.Snapshot;", StringComparison.Ordinal);
        var capture = source.IndexOf("CanRecordCompletedGuideOwner(", completed, StringComparison.Ordinal);
        var failure = source.IndexOf("if (!settle.Succeeded && !windSampledSettle && !calibrationNeedsFreshField)", completed, StringComparison.Ordinal);
        Assert.True(completed >= 0 && capture > completed && failure > capture);
    }

    [Theory]
    [InlineData(Phd2AppState.Selected)]
    [InlineData(Phd2AppState.Looping)]
    public void PreGuideSelectionCanAuthorizeCheckedStopAfterInitialSettleFailure(Phd2AppState state)
    {
        Phd2Point selected = new(578.37, 985.97);
        Assert.True(Phd2DependencyRebuildStopPolicy.CanRecordPreGuideOwner(Owned(state), 7, selected, selected));
        Assert.True(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(
            Owned(Phd2AppState.Guiding), true, 7, selected, selected));
        Assert.True(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(
            Owned(Phd2AppState.LostLock), true, 7, selected, selected));
    }

    [Fact]
    public void PreGuideOwnershipRefusesExternalGuidingPauseReconnectAndChangedSelection()
    {
        var s = Owned(Phd2AppState.Selected);
        Phd2Point p = new(578.37, 985.97);
        Assert.False(Phd2DependencyRebuildStopPolicy.CanRecordPreGuideOwner(s, 6, p, p));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanRecordPreGuideOwner(s, 7, p, null));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanRecordPreGuideOwner(s, 7, p, new(p.X + 1, p.Y)));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanRecordPreGuideOwner(s, 7, p, new(double.NaN, p.Y)));
        foreach (var other in new[] { s with { IsConnected = false }, s with { Phd2Paused = true },
            s with { AutomationPaused = true }, s with { PendingSettleOperationId = 3 },
            s with { AppState = Phd2AppState.Guiding }, s with { AppState = Phd2AppState.Calibrating },
            s with { AppState = Phd2AppState.LostLock } })
            Assert.False(Phd2DependencyRebuildStopPolicy.CanRecordPreGuideOwner(other, 7, p, p));
    }

    [Fact]
    public void ProductionCapturesSelectedOwnershipBeforeAwaitingFirstSettle()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.Phd2SlitPlacement.cs"));
        var capture = source.IndexOf("phd2PreMotionOutputOwner = (selectionConnectionEpoch, preGuideLock!);", StringComparison.Ordinal);
        var check = source.IndexOf("CanRecordPreGuideOwner(", StringComparison.Ordinal);
        var start = source.IndexOf("var settle = await phd2.GuideAndSettleAsync(", check, StringComparison.Ordinal);
        Assert.True(check >= 0 && capture > check && start > capture);
    }

    private static Phd2StateSnapshot Owned(Phd2AppState state) => Phd2StateSnapshot.Disconnected with
    {
        IsConnected = true, AppState = state, ConnectionEpoch = 7, GuideEpoch = 98,
    };

    [Theory]
    [InlineData(Phd2AppState.Guiding)]
    [InlineData(Phd2AppState.LostLock)]
    public void ChangedLossEpochDoesNotPreventCheckedStopOfOurSameConnectionAndLock(Phd2AppState state)
    {
        Assert.True(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(
            Owned(state), true, 7, new(760.5, 578.47), new(760.5, 578.47)));
    }

    [Theory]
    [InlineData(Phd2AppState.Calibrating)]
    [InlineData(Phd2AppState.Paused)]
    [InlineData(Phd2AppState.Unknown)]
    [InlineData(Phd2AppState.Looping)]
    [InlineData(Phd2AppState.Selected)]
    [InlineData(Phd2AppState.Stopped)]
    public void StopAuthorityDoesNotExpandToUnownedNonGuidingStates(Phd2AppState state)
    {
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(
            Owned(state), true, 7, new(760.5, 578.47), new(760.5, 578.47)));
    }

    [Fact]
    public void PauseReconnectMissingOwnershipAndChangedLockAllRefuseTakeover()
    {
        var s = Owned(Phd2AppState.LostLock);
        Phd2Point point = new(760.5, 578.47);
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(s, false, 7, point, point));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(s, true, null, point, point));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(s, true, 6, point, point));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(s with { IsConnected = false }, true, 7, point, point));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(s with { Phd2Paused = true }, true, 7, point, point));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(s with { AutomationPaused = true }, true, 7, point, point));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(s with { PendingSettleOperationId = 3 }, true, 7, point, point));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(s, true, 7, null, point));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(s, true, 7, point, null));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(s, true, 7, point, new(point.X + 1, point.Y)));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(s, true, 7, point, new(double.NaN, point.Y)));
    }

    [Fact]
    public void ProductionStopsBeforeInvalidatingEvidenceAndBeforeAcquisitionCanRestart()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var start = source.IndexOf("case ObservationAutomaticRecoveryAction.RebuildStageDependencies:", StringComparison.Ordinal);
        var end = source.IndexOf("case ObservationAutomaticRecoveryAction.RetrySameStage:", start, StringComparison.Ordinal);
        var branch = source[start..end];
        Assert.True(branch.IndexOf("EnsurePhdStoppedForAutomaticRebuildAsync", StringComparison.Ordinal) <
            branch.IndexOf("MarkResumeRecoveryRequired()", StringComparison.Ordinal));
        Assert.Contains("rebuildStop.Disposition != GateDisposition.Passed", branch, StringComparison.Ordinal);
        var helperStart = source.IndexOf("private async Task<GateResult> EnsurePhdStoppedForAutomaticRebuildAsync(", StringComparison.Ordinal);
        var helperEnd = source.IndexOf("public override async Task<GateResult> RevalidateAsync(", helperStart, StringComparison.Ordinal);
        var helper = source[helperStart..helperEnd];
        Assert.True(helper.IndexOf("ValidateIdentityAsync", StringComparison.Ordinal) < helper.IndexOf("StopCaptureAndConfirmAsync", StringComparison.Ordinal));
        Assert.True(helper.IndexOf("CanStopOwnedSession", StringComparison.Ordinal) < helper.IndexOf("StopCaptureAndConfirmAsync", StringComparison.Ordinal));
        Assert.Contains("ValidateConfirmedPhdStop(stopped,", helper, StringComparison.Ordinal);
        Assert.Contains("phd2-dependency-rebuild-stop-intent", helper, StringComparison.Ordinal);
        Assert.Contains("phd2-dependency-rebuild-stop-confirmed", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("CaptureFullFrameAsync", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("CaptureSingleFrame", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("SetLockPosition", helper, StringComparison.Ordinal);
    }
}
