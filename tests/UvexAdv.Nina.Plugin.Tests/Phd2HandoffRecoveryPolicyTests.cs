using UvexAdv.Observatory;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2HandoffRecoveryPolicyTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(5, false)]
    [InlineData(5.01, true)]
    [InlineData(28, true)]
    [InlineData(-1, true)]
    public void PreGuideRecheckUsesTheOriginalMeasurementLifetimeNotAnotherSolveLadder(double age, bool refresh)
    {
        var now = DateTimeOffset.Parse("2026-11-14T17:48:20Z");
        Assert.Equal(refresh, Phd2HandoffRecoveryPolicy.NeedsPreGuideRefresh(now.AddSeconds(-age), now, 5));
        Assert.True(Phd2HandoffRecoveryPolicy.NeedsPreGuideRefresh(default, now, 5));
    }

    [Fact]
    public void FreshLoopReadbackMayOnlyChangeTheAttestedSelectedLoopingEpochs()
    {
        var before = Preview with { AppState = Phd2AppState.Selected, EventSequence = 10 };
        bool Same(Phd2StateSnapshot after) => Phd2HandoffRecoveryPolicy.ClassifyPreviewReadback(
            before, after, before.SelectedStar!, 11, true) == PreGuidePreviewReadback.SameSelection;
        Assert.False(Same(before)); // No fresh frame.
        var change = new Phd2GuideEpochChange(4, 5, "event:LoopingExposures", 11,
            DateTimeOffset.UtcNow, Phd2AppState.Selected, Phd2AppState.Looping);
        var after = before with { GuideEpoch = 5, AppState = Phd2AppState.Looping, EventSequence = 11,
            RecentGuideEpochChanges = [change] };
        Assert.True(Same(after));
        Assert.False(Same(after with { RecentGuideEpochChanges = [] }));
        Assert.False(Same(after with { GuideEpoch = 6 }));
        Assert.False(Same(after with
        { RecentGuideEpochChanges = [change with { PreviousEpoch = 3 }] }));
        Assert.False(Same(after with
        { RecentGuideEpochChanges = [change with { Cause = "event:LockPositionSet" }] }));
        Assert.False(Same(after with
        { RecentGuideEpochChanges = [change with { PreviousState = Phd2AppState.Stopped }] }));
        foreach (var altered in new[] { after with { ConnectionEpoch = 2 }, after with { AppState = Phd2AppState.Guiding },
            after with { SelectedStar = new(301, 250) }, after with { LockPosition = new(301, 250) },
            after with { CalibrationChangeSequence = 99 }, after with { AutomationPaused = true },
            after with { PendingSettleOperationId = 1 }, after with { IsConnected = false } })
            Assert.False(Same(altered));
    }

    [Fact]
    public void Wr134SelectedReadbackAndSeventhPreviewFrameDoNotMeanTheStarWasLost()
    {
        var p = new Phd2Point(838.17, 640.37);
        var before = Preview with { GuideEpoch = 14, EventSequence = 100, SelectedStar = p, LockPosition = p };
        var poll = new Phd2GuideEpochChange(14, 15, "GetAppStateAsync", 100, DateTimeOffset.UtcNow,
            Phd2AppState.Looping, Phd2AppState.Selected);
        var loop = new Phd2GuideEpochChange(15, 16, "event:LoopingExposures", 101, DateTimeOffset.UtcNow,
            Phd2AppState.Selected, Phd2AppState.Looping);
        var after = before with { GuideEpoch = 16, EventSequence = 101, RecentGuideEpochChanges = [poll, loop] };
        Assert.True(Phd2GuideSnrExposurePolicy.CanRecoverCandidateLoss(before, after, after, p, true, false));
        PreGuidePreviewReadback Check(Phd2StateSnapshot snap, long? frame = 101, bool idle = true) =>
            Phd2HandoffRecoveryPolicy.ClassifyPreviewReadback(before, snap, p, frame, idle);
        Assert.Equal(PreGuidePreviewReadback.SameSelection, Check(after));
        Assert.Equal(PreGuidePreviewReadback.SameSelection, Check(before with { EventSequence = 101 }));
        Assert.Equal(PreGuidePreviewReadback.ContextChanged, Check(after, 100));
        Assert.Equal(PreGuidePreviewReadback.ContextChanged, Check(after, 105));
        Assert.Equal(PreGuidePreviewReadback.ContextChanged, Check(after, null));
        Assert.Equal(PreGuidePreviewReadback.ContextChanged, Check(after with { RecentGuideEpochChanges = [loop] }));
        Assert.Equal(PreGuidePreviewReadback.ContextChanged, Check(after with
            { RecentGuideEpochChanges = [poll, loop with { PreviousState = Phd2AppState.Looping }] }));
        var reportedMiss = after with { LastPreviewStarMissSequence = 101 }; // native frame error >= 2
        Assert.Equal(PreGuidePreviewReadback.CandidateLost, Check(reportedMiss));
        Assert.Equal(PreGuidePreviewReadback.ContextChanged, Check(reportedMiss, idle: false));
        var newer = loop with { PreviousEpoch = 16, Epoch = 17, EventSequence = 102, PreviousState = Phd2AppState.Looping };
        var lost = after with { LockPosition = null, LastPreviewStarMissSequence = 102, EventSequence = 102,
            GuideEpoch = 17, RecentGuideEpochChanges = [poll, loop, newer with { Cause = "event:LockPositionLost" }] };
        Assert.Equal(PreGuidePreviewReadback.CandidateLost, Check(lost));
        Assert.Equal(PreGuidePreviewReadback.CandidateLost, Check(lost, null));
        foreach (var other in new[] { lost with { ConnectionEpoch = 2 }, lost with { Phd2Paused = true },
            lost with { CalibrationChangeSequence = 102 }, lost with { PendingSettleOperationId = 2 },
            lost with { RecentGuideEpochChanges = [poll, loop, newer with { Cause = "event:StarSelected" }] },
            lost with { SelectedStar = new(839, 641) } })
            Assert.Equal(PreGuidePreviewReadback.ContextChanged, Check(other));
    }

    private static Phd2StateSnapshot Preview => Phd2StateSnapshot.Disconnected with
    { IsConnected = true, AppState = Phd2AppState.Looping, ConnectionEpoch = 1, GuideEpoch = 4,
        SelectedStar = new(300, 250), LockPosition = new(300, 250) };

    [Theory]
    [InlineData(35, 44, false)]
    [InlineData(49.72, 44, true)]
    [InlineData(1, 0, true)]
    [InlineData(0, 0, false)]
    public void ARefreshedOrReplacedSelectionMustStillFitTheSameRemainingFineAndReturnEnvelope(
        double residual, double remaining, bool coarse)
    {
        var target = new TargetIdentification(GateResult.Pass("measured", "current pixels"),
            new(new(500 + residual, 400), 100, 200, 30, 3, 0, 0, 50), new(500, 400), residual, 2);
        var slit = new SlitGeometry("run-led", new(500, 400), 0, 410, 3.25, .5, "G3", 1, 1);
        Assert.Equal(coarse, Phd2HandoffRecoveryPolicy.FreshTargetExceedsHandoff(target, slit, remaining));
        Assert.False(Phd2HandoffRecoveryPolicy.FreshTargetExceedsHandoff(target with
            { Gate = GateResult.Unknown("TARGET_AMBIGUOUS", "not a measured target") }, slit, remaining));
    }

    [Theory]
    [InlineData(14.345, false)]
    [InlineData(20, false)]
    [InlineData(69.28, true)]
    [InlineData(515.328, true)]
    public void EverySolvedSearchExitMustUseTheSameMeasuredCoarseWindow(double residual, bool center) =>
        Assert.Equal(center, G3WcsRecoveryPolicy.NeedsCoarseCentering(GateResult.Pass("G3_FIELD_ANALYZED", "frame"), true, residual, 20));

    [Fact]
    public void UnsolvedImageCannotAuthorizeACoarseMove() => Assert.False(
        G3WcsRecoveryPolicy.NeedsCoarseCentering(GateResult.Pass("BRIGHT_TARGET", "frame"), false, 69, 20));

    [Theory]
    [InlineData("SLIT_LOCK_ACQUISITION_CUMULATIVE_RESERVE", 69, true)]
    [InlineData("SLIT_RESIDUAL_SEARCH_WINDOW", 509, true)]
    [InlineData("SLIT_LOCK_RETURN_TIME_RESERVE", 3, false)]
    [InlineData("G3_FRAME_REUSED", 69, false)]
    [InlineData("GUIDE_LOCK_RESIDUAL_HIGH", 69, false)]
    [InlineData("PHD2_LOCK_LEDGER_BINDING_CHANGED", 69, false)]
    public void RecoveryIsExplicitAndDoesNotDestroyNearSlitWarningPath(string code, double residual, bool expected) =>
        Assert.Equal(expected, Phd2HandoffRecoveryPolicy.ShouldReacquire(
            new(false, code, "test", 0, 0, 0, 0, 0, 0, 0), residual, 7));
}
