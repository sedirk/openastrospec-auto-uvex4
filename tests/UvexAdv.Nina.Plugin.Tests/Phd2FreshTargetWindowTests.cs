using System.Globalization;
using UvexAdv.Observatory;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2FreshTargetWindowTests
{
    [Fact]
    public void OneAmbiguousThirdFrameRestartsRatherThanCherryPicksTheWindow()
    {
        var window = new Phd2FreshTargetWindow(3);
        window.Observe(true); window.Observe(true); window.Observe(false);
        Assert.Equal(0, window.ConsecutiveFrames);
        Assert.False(window.Complete);
        Assert.False(window.Exhausted);
        window.Observe(true); window.Observe(true);
        Assert.False(window.Complete);
        window.Observe(true);
        Assert.True(window.Complete);
        Assert.Equal(6, window.Captures);
        Assert.Equal(1, window.RejectedFrames);
    }

    [Fact]
    public void TwoWindowRestartsHaveOneFiniteCaptureEnvelope()
    {
        var window = new Phd2FreshTargetWindow(3);
        foreach (var measured in new[] { true, true, false, true, true, false, true, true, true })
            window.Observe(measured);
        Assert.True(window.Complete);
        Assert.Equal(9, window.Captures);
        Assert.Equal(window.MaximumCaptures, window.Captures);
        Assert.Throws<InvalidOperationException>(() => window.Observe(true));
    }

    [Fact]
    public void PersistentAmbiguityDoesNotAuthorizeAnyPositionOrInfiniteRetry()
    {
        var window = new Phd2FreshTargetWindow(3);
        window.Observe(false); window.Observe(false); window.Observe(false);
        Assert.True(window.Exhausted);
        Assert.False(window.Complete);
        Assert.Equal(0, window.ConsecutiveFrames);
        Assert.Throws<InvalidOperationException>(() => window.Observe(true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Phd2FreshTargetWindow(0));
    }

    [Theory]
    [InlineData(true, "G3_POST_WCS_TARGET_NOT_MEASURED", true)]
    [InlineData(false, "PHD2_FRESH_TARGET_STOP_UNCONFIRMED", false)]
    public void TypedOpticalFailureSurvivesOuterPlacementBoundaryOnlyAfterCheckedStop(
        bool stopped, string code, bool skippable)
    {
        var failure = new Phd2FreshTargetNotMeasuredException(
            GateResult.Unknown(Phd2FreshTargetWindow.FailureCode, "ambiguous fresh core"), "evidence.json");
        var result = failure.AfterStop(stopped);
        Assert.Equal(code, result.Gate.Code);
        Assert.Equal("evidence.json", result.EvidencePath);
        Assert.False(result.CanAdvance);
        Assert.Equal(skippable, NativeSequencePlan.IsSkippableQualityFailure(result.Gate.Code));
        Assert.False(NativeSequencePlan.IsSkippableQualityFailure("PHD2_SLIT_PLACEMENT_FAILED_SAFE"));
    }

    private static Phd2StateSnapshot Guiding => Phd2StateSnapshot.Disconnected with {
        IsConnected = true, AppState = Phd2AppState.Guiding, ConnectionEpoch = 1, GuideEpoch = 11,
        LockPosition = new(777, 920), SelectedStar = new(777, 920),
        GuideOutput = new(4, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false,
            new(4, .853, -1.89, 38.28, 5.37, 2, 1, "On-Step (ASCOM)", 1, 1, 43, 62))
    };

    [Fact]
    public void ReadOnlyRetryRequiresSameLiveOwnerSelectionLockAndCalibration()
    {
        var original = Guiding;
        Assert.True(Phd2FreshTargetWindow.SameGuide(original, original, original.LockPosition!, .1));
        foreach (var changed in new[] {
            original with { IsConnected = false }, original with { GuideEpoch = 12 },
            original with { ConnectionEpoch = 2 }, original with { AutomationPaused = true },
            original with { Phd2Paused = true }, original with { AppState = Phd2AppState.LostLock },
            original with { PendingSettleOperationId = 9 }, original with { LockPosition = new(778,920) },
            original with { SelectedStar = new(777,919) }, original with { CalibrationChangeSequence = 999 },
            original with { GuideOutput = null },
            original with { GuideOutput = original.GuideOutput! with { Failed = true } },
            original with { GuideOutput = original.GuideOutput! with { ConsecutiveMissingOutputs = 1 } },
            original with { Profile = new(999,"different") } })
            Assert.False(Phd2FreshTargetWindow.SameGuide(original, changed, original.LockPosition!, .1));
    }

    [Fact]
    public void DiagnosticExplainsFreshMeasurementNotFailedGotoOrCloudConclusion()
    {
        var presentation = ObservationUiPresentation.Present(ObservationStage.PlaceTargetOnSlit,
            GateResult.Unknown(Phd2FreshTargetWindow.FailureCode, "measurement not unique"),
            CultureInfo.GetCultureInfo("zh-CN"));
        Assert.Contains("补取新帧", presentation.Summary);
        Assert.Contains("可跳过本目标", presentation.Summary);
    }
}
