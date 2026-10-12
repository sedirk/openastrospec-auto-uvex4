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
        Assert.Contains("guideSnrPreparationConsumed = true", helper);
        Assert.True(helper.IndexOf("await StopPhdAndWaitAsync", StringComparison.Ordinal) <
            helper.IndexOf("var brighter = await CaptureAndSelectPhd2GuideAtExposureAsync", StringComparison.Ordinal));
        Assert.Contains("staleFrameReused = false", helper);
        Assert.Contains("catch (Phd2GuidingFrameLostException lost)", helper);
        Assert.Contains("lost.SelectedStarSnrBaseline is { } sampleOwner", helper);
        Assert.Contains("if (restored) throw new PhysicalActionGateException", helper);
        Assert.DoesNotContain("GuideRecovery.", helper);
        Assert.DoesNotContain("catch (Exception", helper);
        Assert.DoesNotContain("SetExactLockPosition", helper);
        Assert.DoesNotContain("GuideAndSettle", helper);
    }
}
