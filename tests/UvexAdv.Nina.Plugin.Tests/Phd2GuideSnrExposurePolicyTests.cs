using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2GuideSnrExposurePolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PreviewCandidateLossAllowsLocalReselectionWithoutAnyPreviousExposureTier(int epochDelta)
    {
        Phd2Point star = new(629.19, 577.24);
        var baseline = Phd2StateSnapshot.Disconnected with { IsConnected = true, AppState = Phd2AppState.Looping,
            ConnectionEpoch = 1, GuideEpoch = 100, EventSequence = 100, SelectedStar = star, LockPosition = star };
        var lost = baseline with { GuideEpoch = 100 + epochDelta, EventSequence = 105, LockPosition = null };
        Assert.True(Phd2GuideSnrExposurePolicy.CanRecoverCandidateLoss(baseline, lost, lost, star, true, false));
        foreach (var changed in new[] { lost with { AppState = Phd2AppState.Guiding },
            lost with { AppState = Phd2AppState.Calibrating }, lost with { CalibrationChangeSequence = 1 },
            lost with { ConnectionEpoch = 2 }, lost with { Phd2Paused = true }, lost with { GuideEpoch = 104 } })
            Assert.False(Phd2GuideSnrExposurePolicy.CanRecoverCandidateLoss(baseline, lost, changed, star, true, false));
        Assert.False(Phd2GuideSnrExposurePolicy.CanRecoverCandidateLoss(baseline, lost, lost, star, false, false));
        Assert.False(Phd2GuideSnrExposurePolicy.CanRecoverCandidateLoss(baseline, lost, lost, star, true, true));
    }

    [Theory]
    [InlineData(Phd2AppState.Selected, 1)]
    [InlineData(Phd2AppState.Selected, 2)]
    [InlineData(Phd2AppState.Looping, 1)]
    public void LongerExposureCandidateLossCanRestoreOnceBeforeGuideOnly(Phd2AppState state, int delta)
    {
        Phd2Point star = new(599.43, 78.2);
        var baseline = Phd2StateSnapshot.Disconnected with { IsConnected = true, AppState = state,
            ConnectionEpoch = 1, GuideEpoch = 87, EventSequence = 100, SelectedStar = star };
        var lost = baseline with { AppState = Phd2AppState.LostLock, GuideEpoch = 87 + delta, EventSequence = 105 };
        Assert.True(Phd2GuideSnrExposurePolicy.CanRestoreAfterCandidateLoss(baseline, lost, lost, star, true, true, false));
        foreach (var (previous, idle, guiding) in new[] { (false, true, false), (true, false, false), (true, true, true) })
            Assert.False(Phd2GuideSnrExposurePolicy.CanRestoreAfterCandidateLoss(baseline, lost, lost, star, previous, idle, guiding));
        foreach (var bad in new[] { lost with { ConnectionEpoch = 2 }, lost with { SelectedStar = new(700, 90) },
            lost with { IsConnected = false }, lost with { AutomationPaused = true }, lost with { Phd2Paused = true },
            lost with { AppState = Phd2AppState.Guiding }, lost with { AppState = Phd2AppState.Calibrating },
            lost with { PendingSettleOperationId = 4 }, lost with { CalibrationChangeSequence = 3 },
            lost with { LastConfigurationChange = new(106, DateTimeOffset.UtcNow, null, null, false) },
            lost with { GuideEpoch = 91 } })
            Assert.False(Phd2GuideSnrExposurePolicy.CanRestoreAfterCandidateLoss(baseline, lost, bad, star, true, true, false));
    }

    private static Phd2SelectedStarSnrObservation Observation(int exposure = 2000, double? snr = 20, int? error = 0) =>
        new(1, 2, new(1125, 503), exposure,
            Enumerable.Range(2, 3).Select(x => new Phd2SelectedStarSnrSample(x, snr, error)).ToArray(), false, DateTimeOffset.UtcNow);

    [Theory]
    [InlineData(2000, 20, 0, 4000)]
    [InlineData(4000, 20, 1, 6000)]
    [InlineData(2000, 25, 0, 3000)]
    [InlineData(2000, 30, 0, null)]
    [InlineData(2000, 50, 0, null)]
    [InlineData(6000, 10, 1, null)]
    [InlineData(2000, 10, 2, null)]
    public void WeakStarGetsNativeBoundedExposureButGoodStarAndLimitsDoNot(int exposure, double snr, int increases, int? next) =>
        Assert.Equal(next, Phd2GuideSnrExposurePolicy.NextExposure(Observation(exposure, snr),
            new[] { 2000, 3000, 4000, 6000, 10000 }, increases, true, true));

    [Theory]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(true, true, 1)]
    [InlineData(true, true, 3)]
    public void DirectTargetOutstandingMotionOrClippingDoesNotRequestLongerExposure(bool motionFree, bool offSlit, int error) =>
        Assert.Null(Phd2GuideSnrExposurePolicy.NextExposure(Observation(error: error), [2000, 4000], 0, motionFree, offSlit));

    [Fact]
    public void MissingTimeoutAndUnsupportedTiersAreAdvisoryNotAnInventedLowSnr()
    {
        foreach (var observation in new[] { Observation(snr: null), Observation(snr: double.NaN),
            Observation() with { TimedOut = true }, Observation() with { Samples = [] } })
        {
            Assert.Null(Phd2GuideSnrExposurePolicy.MedianSnr(observation));
            Assert.Null(Phd2GuideSnrExposurePolicy.NextExposure(observation, [2000, 4000], 0, true, true));
        }
        Assert.Null(Phd2GuideSnrExposurePolicy.NextExposure(Observation(), [1000, 2000, 10000], 0, true, true));
    }

    [Fact]
    public void ProductionUsesSameChosenExposureForCaptureAndResidualsAndNeverOptimizesOldReturnDebt()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "UVEX-ADV.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var file = File.ReadAllText(Path.Combine(root.FullName, "src/UvexAdv.Nina.Plugin/RealObservationStageRunner.Phd2SlitPlacement.cs"));
        Assert.Contains("var expectedExposureMilliseconds = GuideExposureFor(preset, guideMode)", file);
        Assert.Contains("selectedExposureMilliseconds ?? GuideExposureFor(preset, resolvedMode)", file);
        Assert.Equal(2, file.Split("await PrepareSnrOptimizedGuideSelectionAsync(").Length - 1);
        var helper = File.ReadAllText(Path.Combine(root.FullName, "src/UvexAdv.Nina.Plugin/RealObservationStageRunner.GuideSnr.cs"));
        Assert.Contains("pendingPhd2LockShift is null", helper);
        Assert.Contains("Volatile.Read(ref phd2GuidingEverStarted) != 0", helper);
        Assert.Contains("guideSnrPreparation.TryBegin", helper);
        Assert.Contains("HasNoOutstandingGuideRecoveryMotion", helper);
        Assert.Contains("guideSnrPreparation.TryChargeIncrease", helper);
        Assert.True(helper.IndexOf("await StopPhdAndWaitAsync", StringComparison.Ordinal) <
            helper.IndexOf("var brighter = await CaptureAndSelectPhd2GuideAtExposureAsync", StringComparison.Ordinal));
        Assert.Contains("staleFrameReused = false", helper);
        Assert.Contains("catch (Phd2GuidingFrameLostException lost)", helper);
        Assert.Contains("lost.SelectedStarSnrBaseline is { } sampleOwner", helper);
        Assert.Contains("if (restored) throw new PhysicalActionGateException", helper);
        // A lost-candidate proposal may now lengthen exposure too; failure of
        // that optional longer image must reach the existing fresh restoration.
        Assert.DoesNotContain("if (localReselection) throw", helper);
        Assert.Contains("localReselection && next == choice.ExposureMilliseconds", helper);
        Assert.True(helper.LastIndexOf("if (restored)",StringComparison.Ordinal) <
            helper.LastIndexOf("if (localReselection) continue",StringComparison.Ordinal));
        Assert.DoesNotContain("GuideRecovery.", helper);
        Assert.DoesNotContain("catch (Exception", helper);
        Assert.DoesNotContain("SetExactLockPosition", helper);
        Assert.DoesNotContain("GuideAndSettle", helper);
    }

    private static Phd2StateSnapshot Preview() => Phd2StateSnapshot.Disconnected with
    { IsConnected = true, AppState = Phd2AppState.Looping, ConnectionEpoch = 1, GuideEpoch = 100 };

    [Fact]
    public void RecordedFront1LowSnrPreviewLossTriesLongerNativeTierBeforeDirectFallback()
    {
        // HD 220539: frame 5, SNR 0, native error 2 at 2000ms. Previously
        // this proposal was 2000ms again, then the target fell back to an
        // ambiguous 10ms direct-target frame after local selection exhausted.
        var lost = new Phd2GuideStep(5, null, null, 0, 0, 6.36, 2);
        Assert.Equal(4000, Phd2GuideSnrExposurePolicy.NextExposureAfterCandidateLoss(lost,
            2000, [1000, 2000, 3000, 4000, 6000, 10000], 0));
        Assert.Equal(6000, Phd2GuideSnrExposurePolicy.NextExposureAfterCandidateLoss(lost, 4000, [4000, 6000], 1));
        Assert.Null(Phd2GuideSnrExposurePolicy.NextExposureAfterCandidateLoss(lost, 4000, [4000, 6000], 2));
        Assert.Null(Phd2GuideSnrExposurePolicy.NextExposureAfterCandidateLoss(lost, 6000, [6000, 10000], 0));
        foreach(var other in new Phd2GuideStep?[] { null, lost with { ErrorCode = 1 }, lost with { ErrorCode = 3 } })
            Assert.Null(Phd2GuideSnrExposurePolicy.NextExposureAfterCandidateLoss(other, 2000, [2000, 4000], 0));
    }

    [Fact]
    public void CheckedRecoveryActuallyReentersPreparationWithoutReplenishingExposureIncreases()
    {
        var b = new Phd2GuideSnrPreparationBudget();
        var s = Preview();
        Assert.True(b.TryBegin(s, true, true, false, out var recovery));
        Assert.False(recovery);
        Assert.True(b.TryChargeIncrease(2000, 4000));
        Assert.False(b.TryBegin(s, true, true, true, out _));
        for(var attempt=1; attempt<=2; attempt++)
        {
            var stopped = s with { AppState=Phd2AppState.Stopped, GuideEpoch=100+attempt };
            var receipt = new Phd2DependencyRebuildStopProof(1, stopped.GuideEpoch, $"checked-stop-{attempt}.json");
            Assert.True(b.AdmitRecovery(receipt, stopped));
            Assert.False(b.AdmitRecovery(receipt, stopped));
            Assert.True(b.TryBegin(s with { GuideEpoch=110+attempt }, true, true, true, out recovery));
            Assert.True(recovery);
            Assert.False(b.TryBegin(s, true, true, true, out _));
        }
        Assert.False(b.AdmitRecovery(new(1, 100, "third.json"), s with { AppState=Phd2AppState.Stopped }));
        Assert.True(b.TryChargeIncrease(4000, 6000));
        Assert.Equal(2, b.Increases);
        Assert.False(b.TryChargeIncrease(2000, 4000));
    }

    [Fact]
    public void PreparationNeverAdoptsAnUnconfirmedStopActiveGuideOrDifferentConnection()
    {
        var s = Preview();
        foreach(var invalid in new[] { s, s with { AppState=Phd2AppState.Guiding },
            s with { AppState=Phd2AppState.Stopped, ConnectionEpoch=2 },
            s with { AppState=Phd2AppState.Stopped, PendingSettleOperationId=1 } })
            Assert.False(new Phd2GuideSnrPreparationBudget().AdmitRecovery(new(1,100,"stop.json"), invalid));
        foreach(var (motionFree, offSlit, state) in new[] {
            (false,true,s), (true,false,s), (true,true,s with { ConnectionEpoch=2 }),
            (true,true,s with { AppState=Phd2AppState.Guiding }), (true,true,s with { Phd2Paused=true }) })
        {
            var b=new Phd2GuideSnrPreparationBudget();
            Assert.True(b.AdmitRecovery(new(1,100,"stop.json"),s with { AppState=Phd2AppState.Stopped }));
            Assert.False(b.TryBegin(state,motionFree,offSlit,true,out _));
            Assert.False(b.TryBegin(s,true,true,true,out _));
        }
    }

    [Fact]
    public void RecoveryPreviewLossCanBeHandledOnlyInsideExplicitPreparationAndNeverAsLiveGuiding()
    {
        var star=new Phd2Point(900,180);
        var baseline=Preview() with { SelectedStar=star, EventSequence=10 };
        var lost=baseline with { GuideEpoch=101, EventSequence=11, LockPosition=null };
        Assert.False(Phd2GuideSnrExposurePolicy.CanRecoverCandidateLoss(baseline,lost,lost,star,true,true));
        Assert.True(Phd2GuideSnrExposurePolicy.CanRecoverCandidateLoss(baseline,lost,lost,star,true,true,true));
        Assert.False(Phd2GuideSnrExposurePolicy.CanRecoverCandidateLoss(baseline,lost,
            lost with { AppState=Phd2AppState.Guiding },star,true,true,true));
        Assert.False(Phd2GuideSnrExposurePolicy.CanRecoverCandidateLoss(baseline,lost,lost,star,false,true,true));
    }
}
