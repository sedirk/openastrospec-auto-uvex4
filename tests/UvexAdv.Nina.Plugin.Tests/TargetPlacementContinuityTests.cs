using UvexAdv.Observatory;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class TargetPlacementContinuityTests
{
    [Fact]
    public void SearchFollowsMeasuredPrimaryNotTheCommandedLockOrBrightNeighbour()
    {
        var predicted=Phd2TargetContinuityPolicy.Predict(new(790,411),new(1699,798),new(1692,790));
        Assert.Equal(new Phd2Point(783,403),predicted);
        Assert.True(Phd2TargetContinuityPolicy.IsContinuous(new(782,400),new(predicted.X,predicted.Y),6));
        Assert.False(Phd2TargetContinuityPolicy.IsContinuous(new(790,414),new(predicted.X,predicted.Y),6));
        Assert.False(Phd2TargetContinuityPolicy.IsContinuous(new(double.NaN,400),new(predicted.X,predicted.Y),6));
    }

    [Fact]
    public void NativeRecoveryAllowanceIsPerChargedStageAndCannotBeReplenishedByARepeatedCall()
    {
        var recovery=new AdaptiveGuidingRecovery();
        Assert.False(recovery.TryConsumePrimaryStarSettle("lineage",0));
        Assert.True(recovery.TryConsumePrimaryStarSettle("lineage",1));
        Assert.False(recovery.TryConsumePrimaryStarSettle("lineage",1));
        Assert.True(recovery.TryConsumePrimaryStarSettle("lineage",2));
        Assert.False(recovery.TryConsumePrimaryStarSettle("lineage",2));
    }

    [Fact]
    public async Task ChargedRecoveryOrdinalSurvivesDurableReloadAndBudgetHandoff()
    {
        var directory = Path.Combine(Path.GetTempPath(), "uvex-continuity-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "pending.json");
        try
        {
            var state = Phd2SlitPlacementCommissioningTests.CreateState("old-run", Phd2LockShiftPendingPhase.SettledBudgetLedger) with
            {
                CurrentLockX = 100, CurrentLockY = 100, RequestedLockX = 100, RequestedLockY = 100,
                AttemptsUsed = 2, PrimaryStarSettleRecoveryAttempted = true, PrimaryStarSettleRecoveryStage = 2,
            };
            await Phd2LockShiftPendingStore.WriteAtomicAsync(path, state, default);
            var loaded = await Phd2LockShiftPendingStore.LoadAsync(path, default);
            Assert.Null(loaded.Error);
            Assert.Equal(state, loaded.State);
            var handoff = Phd2LockShiftBudgetHandoff.CreateCurrentRunSettledCopy(loaded.State!, "new-run", state.RecoveryContextSha256, state.UpdatedUtc.AddSeconds(1));
            Assert.Equal(2, handoff.PrimaryStarSettleRecoveryStage);
            Assert.Equal(state.StartedUtc, handoff.StartedUtc);
            Assert.Equal(state.CumulativeCommandedPixels, handoff.CumulativeCommandedPixels);
            Assert.NotEmpty(Phd2LockShiftBudgetHandoff.ValidateCompletedHandoff(state,
                handoff with { PrimaryStarSettleRecoveryStage = 0 }, "new-run", state.RecoveryContextSha256));
            Assert.NotEmpty((state with { PrimaryStarSettleRecoveryStage = 3 }).Validate());
            Assert.NotEmpty((state with { PrimaryStarSettleRecoveryAttempted = false }).Validate());
            var legacy = state with { PrimaryStarSettleRecoveryStage = 0 };
            await Phd2LockShiftPendingStore.WriteAtomicAsync(path, legacy, default);
            Assert.DoesNotContain("primaryStarSettleRecoveryStage", await File.ReadAllTextAsync(path));
            Assert.Equal(legacy, (await Phd2LockShiftPendingStore.LoadAsync(path, default)).State);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void CoreOverlapAuthorizesProbeNotExactPlacementAndNeverStrictOrOutsideFiniteSlit()
    {
        var aperture=new Phd2SlitApertureResidual(12,10,6,205,1,1.5,8);
        var quality=new ScienceQualityPolicy(true,false);
        var result=quality.EvaluateSlitWarning(true,true,[12,12,12],[aperture,aperture,aperture],2,3,100);
        Assert.True(result.CanProbe);
        Assert.False(Phd2PlacementGuideWindowPolicy.AllWithinTolerance([12,12,12],2));
        foreach(var bad in new[]{aperture with {TargetCoreRadiusPixels=0},
            aperture with {CrossSlitPixels=30},aperture with {AlongSlitPixels=204},
            aperture with {TargetCoreRadiusPixels=double.NaN},aperture with {MidpointPixels=101}})
            Assert.False(quality.EvaluateSlitWarning(true,true,[12,12,12],[bad,bad,bad],2,3,100).CanProbe);
        Assert.False(new ScienceQualityPolicy(false,false).EvaluateSlitWarning(true,true,[12,12,12],[aperture,aperture,aperture],2,3,100).CanProbe);
        Assert.False(quality.EvaluateSlitWarning(false,true,[12,12,12],[aperture,aperture,aperture],2,3,100).CanProbe);
        Assert.False(quality.EvaluateSlitWarning(true,false,[12,12,12],[aperture,aperture,aperture],2,3,100).CanProbe);
        Assert.False(quality.EvaluateSlitWarning(true,true,[12,12],[aperture,aperture],2,3,100).CanProbe);
    }
}
