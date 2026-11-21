using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class AdaptiveGuidingRecoveryTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-10T12:00:00Z");
    private static GuideRecoveryAction Clear(AdaptiveGuidingRecovery session, int seconds) =>
        session.Observe(Start.AddSeconds(seconds), Start.AddSeconds(seconds), true, 4, 10, 0);

    [Fact]
    public void PrimarySettleAllowanceSurvivesReentryAndMeridianContinuation()
    {
        var session = new NativeMeridianSession(new(2, 4, 120), _ => true);
        var recovery = session.GuidingRecovery;
        Assert.True(recovery.TryConsumePrimaryStarSettle());
        recovery.BeginCheck(Start);
        Assert.False(recovery.TryConsumePrimaryStarSettle());
        session.RecordSegment("first", 1, 1, 0, 120);
        session.RequestAtFrameBoundary(120);
        session.ConfirmFlip();
        Assert.Same(recovery, session.GuidingRecovery);
        Assert.False(session.GuidingRecovery.TryConsumePrimaryStarSettle());
        Assert.True(new AdaptiveGuidingRecovery().TryConsumePrimaryStarSettle());
    }

    [Fact]
    public void CloudsWaitWithoutSelectionOrCalibrationThenRequireTwoFreshClearFrames()
    {
        var s = new AdaptiveGuidingRecovery(); s.BeginCheck(Start);
        for (var t = 0; t < 100; t += 15)
            Assert.Equal(GuideRecoveryAction.WaitForStars, s.Observe(Start.AddSeconds(t), Start.AddSeconds(t), true, 0, 0, 0));
        Assert.Equal(TimeSpan.FromSeconds(15), s.NextStarCheckDelay);
        Assert.Equal(0, s.Reselections); Assert.False(s.CalibrationConsumed);
        Assert.Equal(GuideRecoveryAction.WaitForStars, Clear(s, 101));
        Assert.Equal(TimeSpan.FromSeconds(1), s.NextStarCheckDelay);
        Assert.Equal(GuideRecoveryAction.WaitForStars, Clear(s, 101)); // rereading one frame is not confirmation
        Assert.Equal(TimeSpan.FromSeconds(15), s.NextStarCheckDelay);
        Assert.Equal(GuideRecoveryAction.WaitForStars, Clear(s, 104));
        Assert.Equal(GuideRecoveryAction.Reselect, Clear(s, 107));
        Assert.Equal(1, s.Reselections); Assert.False(s.CalibrationConsumed);
    }

    [Fact]
    public void LongExposureRecoveryRegistersAFreshStoppedFieldBeforeGuideReselection()
    {
        const int width = 1200;
        const int height = 900;
        var reference = Pattern(0, 0);
        var current = Pattern(2.4, -1.3);
        var target = new StarCandidate(new(950, 450), 8000, 1_000_000, 80, 5, .1, 0, 450);
        var field = G3FieldState.Failed(
                GateResult.Pass("field", "fixture"),
                "old.fit")
            with
            {
                Frame = new MonochromeFrame(width, height, new ushort[width * height], 65520),
                Candidates = reference,
                SlitDetection = new SlitLocusDetection(
                    GateResult.Pass("slit", "fixture"),
                    new SlitGeometry("fixture", new(600, 460), 3, 400, 10, 1, "g3", 1, 1),
                    20, 0, 0),
                TargetIdentification = new TargetIdentification(
                    GateResult.Pass("target", "fixture"),
                    target,
                    target.Centroid,
                    0,
                    2),
            };

        var seed = RealObservationStageRunner.TryRegisterSameFieldRecoverySeed(
            field, current, width, height, 30, default);

        Assert.NotNull(seed);
        Assert.True(seed!.Registration.Accepted);
        Assert.True(seed.Registration.MatchedStars >= 5);
        Assert.InRange(seed.Target.X, 952.2, 952.6);
        Assert.InRange(seed.Target.Y, 448.5, 448.9);
        Assert.Equal(field.SlitDetection.Geometry, seed.Slit);
        Assert.NotEqual(seed.Registration.Transform!.Apply(seed.Slit.AcquisitionPoint), seed.Slit.AcquisitionPoint);
    }

    [Fact]
    public void UnrelatedRecoveryFieldCannotBecomeASameFieldSeed()
    {
        const int width = 1200;
        const int height = 900;
        var field = G3FieldState.Failed(
                GateResult.Pass("field", "fixture"),
                "old.fit")
            with
            {
                Frame = new MonochromeFrame(width, height, new ushort[width * height], 65520),
                Candidates = Pattern(0, 0),
                SlitDetection = new SlitLocusDetection(
                    GateResult.Pass("slit", "fixture"),
                    new SlitGeometry("fixture", new(600, 460), 3, 400, 10, 1, "g3", 1, 1),
                    20, 0, 0),
                TargetIdentification = new TargetIdentification(
                    GateResult.Pass("target", "fixture"),
                    new StarCandidate(new(950, 450), 8000, 1_000_000, 80, 5, .1, 0, 450),
                    new(950, 450), 0, 2),
            };
        var unrelated = Enumerable.Range(0, 10)
            .Select(i => new StarCandidate(
                new(1170 - i * 61, 860 - i * 47), 8000, 1_000_000, 80,
                3, .1, 0, Math.Min(1170 - i * 61, 860 - i * 47)))
            .ToArray();

        Assert.Null(RealObservationStageRunner.TryRegisterSameFieldRecoverySeed(
            field, unrelated, width, height, 30, default));
    }

    [Theory]
    [InlineData(16, 95.76296531263989, 0.0000308641975308642)] // Recorded HD 26380, 02:18:15.
    [InlineData(11, 122.7048466898377, 0.0000617283950617284)] // Recorded HD 26380, 02:18:33.
    public void RecordedThinCloudRecoveryFramesAuthorizeOnlyBoundedLocalReacquisition(
        int stars, double medianSnr, double saturatedPixelFraction)
    {
        Assert.True(AdaptiveGuidingRecovery.HasUsableStars(
            stars, medianSnr, saturatedPixelFraction));
        var s = new AdaptiveGuidingRecovery();
        s.BeginCheck(Start);
        Assert.Equal(GuideRecoveryAction.WaitForStars,
            s.Observe(Start, Start, true, stars, medianSnr, saturatedPixelFraction));
        Assert.Equal(GuideRecoveryAction.Reselect,
            s.Observe(Start.AddSeconds(15), Start.AddSeconds(15), true,
                stars, medianSnr, saturatedPixelFraction));
        Assert.Equal(1, s.Reselections);
        Assert.False(s.CalibrationConsumed);
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
        Assert.Contains("Phd2AdaptiveStarCapture.CaptureAsync", source);
        Assert.DoesNotContain("phd2.CaptureFullFrameAsync", source);
        Assert.Contains("GuideRecovery.Observe", source);
        Assert.Contains("MarkResumeRecoveryRequired()", source);
        Assert.DoesNotContain("SetLockPosition", source);
        Assert.DoesNotContain("CloseDome", source);
        Assert.DoesNotContain("pendingPhd2LockShift = null", source);
        var localStart = source.IndexOf(
            "private async Task<bool> TryReacquireSameFieldGuideWithoutMotionAsync(",
            StringComparison.Ordinal);
        var localEnd = source.IndexOf(
            "private StageResult AdaptiveRecoveryExhausted(",
            localStart,
            StringComparison.Ordinal);
        Assert.True(localStart >= 0 && localEnd > localStart);
        var local = source[localStart..localEnd];
        Assert.Contains("AcquireFreshPhd2PlacementGuideAsync", local);
        Assert.Contains("CapturePhd2GuidingMeasurementsAsync", local);
        Assert.Contains("RebaseAfterSameFieldGuideEpoch", local);
        Assert.Contains("coordinatorMountMotionIssued = false", local);
        Assert.Contains("fullG3WcsRebuildIssued = false", local);
        Assert.Contains("Phd2NoGuideStarException", local);
        Assert.Contains("Phd2GuideSelectionUnavailableException", local);
        Assert.Contains("Phd2GuidingFrameLostException", local);
        Assert.Contains("Phd2CommandTimeoutException", local);
        Assert.DoesNotContain("AcquireG3SlitFieldAsync", local);
        Assert.DoesNotContain("PlaceTargetOnSlitWithPhd2Async", local);
        Assert.DoesNotContain("RequestNativeMeridianBoundary", local);
        Assert.True(source.IndexOf(
                "TryReacquireSameFieldGuideWithoutMotionAsync(",
                StringComparison.Ordinal) <
            source.IndexOf(
                "var budget = await CheckScienceRebuildBudgetAsync(",
                StringComparison.Ordinal));
        var runner = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        Assert.DoesNotContain("PHD2_REBUILD_IDLE_OR_PREVIEW", runner);
        Assert.Contains("StopPreviewAndConfirmAsync(before.ConnectionEpoch", runner);
        Assert.Contains("automaticRebuildStopProof = proof", runner);
        Assert.Contains("before.Profile != after.Profile", runner);
        Assert.Contains("!proof.IsCurrent(phd2.Snapshot)", runner);
    }

    private static StarCandidate[] Pattern(double dx, double dy)
    {
        var points = new[]
        {
            (100d, 100d), (300d, 120d), (520d, 90d), (140d, 340d), (360d, 360d),
            (600d, 320d), (180d, 650d), (480d, 680d), (800d, 160d), (820d, 720d),
        };
        return points.Select(p =>
        {
            var x = p.Item1 + dx;
            var y = p.Item2 + dy;
            return new StarCandidate(
                new(x, y), 5000, 250_000, 40, 3, .1, 0,
                Math.Min(Math.Min(x, 1200 - x), Math.Min(y, 900 - y)));
        }).ToArray();
    }
}
