using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class AdaptiveGuidingRecoveryTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-10T12:00:00Z");
    private static GuideRecoveryAction Clear(AdaptiveGuidingRecovery session, int seconds) =>
        session.Observe(Start.AddSeconds(seconds), Start.AddSeconds(seconds), true, 4, 10, 0);

    [Fact]
    public void CloudsWaitWithoutSelectionOrCalibrationThenRequireTwoFreshClearFrames()
    {
        var s = new AdaptiveGuidingRecovery(); s.BeginCheck(Start);
        for (var t = 0; t < 100; t += 15)
            Assert.Equal(GuideRecoveryAction.WaitForStars, s.Observe(Start.AddSeconds(t), Start.AddSeconds(t), true, 0, 0, 0));
        Assert.Equal(0, s.Reselections); Assert.False(s.CalibrationConsumed);
        Assert.Equal(GuideRecoveryAction.WaitForStars, Clear(s, 101));
        Assert.Equal(GuideRecoveryAction.Reselect, Clear(s, 116));
        Assert.Equal(1, s.Reselections); Assert.False(s.CalibrationConsumed);
    }

    [Fact]
    public void FailedReselectionEscalatesOnceAndLaterFailureCannotLoopCalibration()
    {
        var s = new AdaptiveGuidingRecovery(); s.BeginCheck(Start);
        Clear(s, 0); Assert.Equal(GuideRecoveryAction.Reselect, Clear(s, 15));
        Assert.Equal(GuideRecoveryAction.Exhausted, Clear(s, 16)); // Same check cannot dispatch twice.
        s.BeginCheck(Start.AddSeconds(60));
        Clear(s, 60); Assert.Equal(GuideRecoveryAction.ForceCalibration, Clear(s, 75));
        Assert.True(s.CalibrationConsumed); // Reserved before any equipment await.
        s.BeginCheck(Start.AddSeconds(120));
        Clear(s, 120); Assert.Equal(GuideRecoveryAction.Exhausted, Clear(s, 135));
        Assert.Equal(1, s.Reselections);
    }

    [Theory]
    [InlineData(2, 20, 0)]
    [InlineData(10, 4.9, 0)]
    [InlineData(10, double.NaN, 0)]
    [InlineData(10, 20, double.NaN)]
    [InlineData(10, 20, 0.01)]
    [InlineData(10, 20, -1)]
    public void WeakOrSaturatedFieldsBreakContinuity(int stars, double snr, double clipping)
    {
        var s = new AdaptiveGuidingRecovery(); s.BeginCheck(Start);
        Clear(s, 0);
        Assert.Equal(GuideRecoveryAction.WaitForStars, s.Observe(Start.AddSeconds(15), Start.AddSeconds(15), true, stars, snr, clipping));
        Assert.Equal(GuideRecoveryAction.WaitForStars, Clear(s, 30));
        Assert.Equal(GuideRecoveryAction.Reselect, Clear(s, 45));
    }

    [Theory]
    [InlineData(0, true)] // Replayed frame.
    [InlineData(-31, true)] // Stale.
    [InlineData(16, true)] // Future clock.
    [InlineData(15, false)]
    public void InvalidFramesNeverAuthorizeAnAction(int frameSeconds, bool valid)
    {
        var s = new AdaptiveGuidingRecovery(); s.BeginCheck(Start); Clear(s, 0);
        Assert.Equal(GuideRecoveryAction.WaitForStars, s.Observe(Start.AddSeconds(15), Start.AddSeconds(frameSeconds), valid, 4, 10, 0));
        Assert.Equal(GuideRecoveryAction.WaitForStars, Clear(s, 30));
    }

    [Fact]
    public void TotalDeadlineAndClockRollbackCannotBeResetByReentryOrMeridianSegment()
    {
        var session = new NativeMeridianSession(new(2, 4, 120), _ => true);
        var s = session.GuidingRecovery; s.BeginCheck(Start); Clear(s, 0); Clear(s, 15);
        session.RecordSegment("first", 1, 1, 0, 120); session.RequestAtFrameBoundary(120); session.ConfirmFlip();
        Assert.Same(s, session.GuidingRecovery);
        s.BeginCheck(Start.AddSeconds(901));
        Assert.Equal(Start, s.StartedUtc);
        Assert.Equal(GuideRecoveryAction.Exhausted, Clear(s, 902));
        Assert.Equal(GuideRecoveryAction.Exhausted, Clear(s, -1));
    }

    [Fact]
    public void ExplicitOrObservedNativeCalibrationPreventsAnotherAutomaticCalibration()
    {
        var s = new AdaptiveGuidingRecovery(); s.RecordExplicitCalibration(); s.BeginCheck(Start);
        Clear(s, 0); Assert.Equal(GuideRecoveryAction.Reselect, Clear(s, 15));
        s.BeginCheck(Start.AddSeconds(30)); Clear(s, 30);
        Assert.Equal(GuideRecoveryAction.Exhausted, Clear(s, 45));
    }

    [Theory]
    [InlineData("GUIDING_LOST", true)]
    [InlineData("PHD2_INITIAL_GUIDE_STAR_LOSS", true)]
    [InlineData("PHD2_STRUCTURED_GUIDE_SESSION_LOSS", true)]
    [InlineData("GUIDING_NOT_STABLE", true)]
    [InlineData("PHD2_GUIDE_OUTPUT_FAILED", false)]
    [InlineData("PHD2_LOCK_RETURN_EPOCH_CHANGED", false)]
    [InlineData("STAGE_EXCEPTION", false)]
    [InlineData("UNSAFE", false)]
    [InlineData("G3_MOTION_RETURN_TIME_LIMIT", false)]
    [InlineData("PHD2_OWNER_CALIBRATION_UNAVAILABLE", false)]
    [InlineData("PHD2_NATIVE_FRAME_TIMEOUT", false)]
    public void OnlyReviewedGuideFailuresEnterRecovery(string code, bool expected)
    {
        Assert.Equal(expected, AdaptiveGuidingRecovery.Handles(ObservationStage.PlaceTargetOnSlit, code));
        Assert.False(AdaptiveGuidingRecovery.Handles(ObservationStage.FinalizeObservation, code));
    }

    [Fact]
    public void NativeSkipListDistinguishesConfirmedExhaustionFromUnknownStopAndReturnDebt()
    {
        Assert.True(NativeSequencePlan.IsSkippableQualityFailure(AdaptiveGuidingRecovery.ExhaustedCode));
        Assert.True(NativeSequencePlan.IsSkippableQualityFailure(AdaptiveGuidingRecovery.StarsTimeoutCode));
        Assert.False(NativeSequencePlan.IsSkippableQualityFailure("PHD2_ADAPTIVE_OWNER_CHANGED"));
        Assert.False(NativeSequencePlan.IsSkippableQualityFailure("PHD2_ADAPTIVE_STOP_RECEIPT_REQUIRED"));
        Assert.False(NativeSequencePlan.IsSkippableQualityFailure("G3_MOTION_RETURN_TIME_LIMIT"));
        Assert.Empty(new NativeSequencePlan(2, 4, 120).Validate());
        Assert.NotEmpty(new NativeSequencePlan(2, 4, 120, GuideStarWaitSeconds: 29).Validate());
        Assert.NotEmpty(new NativeSequencePlan(2, 4, 120, GuideStarWaitSeconds: 601).Validate());
    }

    [Fact]
    public void ProductionUsesOwnedNativeFramesBeforeResumingExistingAcquisitionChain()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.AdaptiveGuidingRecovery.cs"));
        Assert.Contains("EnsurePhdStoppedForAutomaticRebuildAsync", source);
        Assert.Contains("automaticRebuildStopProof?.IsCurrent", source);
        Assert.Contains("HasNoOutstandingGuideRecoveryMotion", source);
        Assert.Contains("SequenceStopGate()", source);
        Assert.Contains("RequireImmediatePhysicalActionGatesAsync", source);
        Assert.Contains("ValidateG3FieldMountBindingForMotionAsync", source);
        Assert.Contains("phd2.CaptureFullFrameAsync", source);
        Assert.Contains("GuideRecovery.Observe", source);
        Assert.Contains("MarkResumeRecoveryRequired()", source);
        Assert.DoesNotContain("SetLockPosition", source);
        Assert.DoesNotContain("GuideAndSettleAsync", source);
        Assert.DoesNotContain("CloseDome", source);
        Assert.DoesNotContain("pendingPhd2LockShift = null", source);
    }
}
