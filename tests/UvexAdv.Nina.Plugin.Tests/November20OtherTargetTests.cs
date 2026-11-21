using UvexAdv.Observatory;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class November20OtherTargetTests
{
    [Theory]
    [InlineData(2, 4.20, true, true, true)]
    [InlineData(1, 4.20, true, true, false)]
    [InlineData(2, 5.41, true, true, false)]
    [InlineData(2, 4.20, false, true, false)]
    [InlineData(2, 4.20, true, false, false)]
    public void SplitMayBeLoggedButNeverBecomeIndependentUnsplitProof(int children, double shift,
        bool sameSetting, bool ordinary, bool retained)
    {
        static G3ShortPositionMeasurement Star(double x, int n)
        {
            var gate=GateResult.Pass("G3_SEP_POSITION_MEASURED","test",new Dictionary<string,double> {
                ["sepWholeParentPosition"]=1, ["sepSelectedParentChildCount"]=n, ["sepParentContinuityPixels"]=Math.Abs(x-100) });
            return new(gate,new(gate,new(new(x,100),4600,60000,44,0,.2,0,200),new(100,100),Math.Abs(x-100),3),true,.5);
        }
        var a=Star(100,1);
        var b=Star(100+shift,children);
        var decision=G3ShortPositionMeasurementPolicy.EvaluateConfirmation(a,b,new string('A',64),new string('B',64),3,100,4);
        Assert.Equal(retained,G3TransientSplitWitness.MayKeepUnsplitAnchor(a,b,decision,sameSetting,ordinary));
        var time=DateTimeOffset.UtcNow;
        Assert.False(G3TransientSplitWitness.MayPairNext(b,time,time.AddSeconds(9)));
        if(children>1) Assert.False(G3TransientSplitWitness.MayPairNext(b,time,time.AddSeconds(1)));
    }

    [Theory]
    [InlineData("PHD2_WCS_TAKEOVER_POSITION_CHANGED", true)]
    [InlineData("PHD2_PRE_SHIFT_TARGET_DRIFT", true)]
    [InlineData("PHD2_COARSE_HANDOFF_RETURN_REQUIRED", false)]
    [InlineData("NIGHT_UNSAFE", false)]
    public void OnlyMeasuredTakeoverDriftAimsForOneStageOnAlreadyBudgetedRebuild(string code, bool expected) =>
        Assert.Equal(expected, Phd2HandoffRecoveryPolicy.CentreInsideOneStageOnRebuild(code));
    [Theory]
    [InlineData(false, false, 12726.4, true)]
    [InlineData(false, false, 11000, true)]
    [InlineData(false, true, 12726.4, false)]
    [InlineData(true, false, 12726.4, false)]
    [InlineData(false, false, 300, false)]
    public void OriginalFieldSolveIsOneShotAndOnlyForRemoteInferences(bool tried, bool formal, double distance, bool expected) =>
        Assert.Equal(expected, G3SparseRecoveryWorkflow.NeedsNativeFarFieldCheck(tried, formal, distance, .383, 1920, 1080));

    [Fact]
    public void TranslatedShortWindowRequiresIndependentFieldAndCurrentExposureTiming()
    {
        var now = DateTimeOffset.UtcNow;
        var reference = new G3SparseExposureTiming(now.AddSeconds(-22), now.AddSeconds(-16), 5, now.AddSeconds(-16));
        var current = new G3SparseExposureTiming(now.AddSeconds(-4), now.AddSeconds(-1), 2, now);
        PixelPoint target = new(810.8,416.6);
        G3RegisteredStarPair Pair(double x,double y) => new(new(x,y),new(x+1,y-13),0);
        var fit = new G3OverlapRegistrationResult(true,"fit","test",new(1,0,1,-13),
            [Pair(350,200),Pair(1250,210),Pair(240,890)],[Pair(1390,720),Pair(138,550)],.5,.9,1);
        PixelPoint? Hint(G3OverlapRegistrationResult transform, G3SparseExposureTiming at) =>
            G3TargetTrackPolicy.RegisteredShortSearchHint(transform,target,10.24,100,1920,1080,reference,at,now);
        Assert.Equal(new PixelPoint(811.8,403.6), Hint(fit,current));
        Assert.Null(Hint(fit with { HoldoutPairs=[] },current));
        Assert.Null(Hint(fit with { Accepted=false },current));
        Assert.Null(Hint(fit with { Transform=new(1,0,0,-94) },current));
        Assert.Null(Hint(fit,current with { CaptureRequestedUtc=reference.CaptureRequestedUtc }));
    }

    [Fact]
    public void LostTakeoverUsesRemainingPreviewAllowanceAndSameOwnerOnly()
    {
        Assert.Equal(3, Phd2HandoffRecoveryPolicy.RemainingPreviewSelections(1, false, false, false, false));
        Assert.Equal(0, Phd2HandoffRecoveryPolicy.RemainingPreviewSelections(4, false, false, false, false));
        Assert.Equal(0, Phd2HandoffRecoveryPolicy.RemainingPreviewSelections(2, true, true, false, false));
        Assert.Equal(0, Phd2HandoffRecoveryPolicy.RemainingPreviewSelections(1, false, false, true, false));
        var lost = Phd2StateSnapshot.Disconnected with { IsConnected = true, AppState = Phd2AppState.Looping,
            ConnectionEpoch = 1, GuideEpoch = 14, EventSequence = 155 };
        Assert.True(Phd2HandoffRecoveryPolicy.SameLostPreviewOwner(lost, lost));
        Assert.False(Phd2HandoffRecoveryPolicy.SameLostPreviewOwner(lost, lost with { ConnectionEpoch = 2 }));
        Assert.False(Phd2HandoffRecoveryPolicy.SameLostPreviewOwner(lost, lost with { GuideEpoch = 15 }));
        Assert.False(Phd2HandoffRecoveryPolicy.SameLostPreviewOwner(lost, lost with { AppState = Phd2AppState.Guiding }));
    }

    [Theory]
    [InlineData("G3_SEARCH_NOT_STARTED_RETURNED", ObservationStage.AcquireG3SlitField)]
    [InlineData("G3_WCS_TARGET_OUTSIDE_RETURN_BUDGET", ObservationStage.AcquireG3SlitField)]
    [InlineData("PHD2_OFF_SLIT_NATIVE_SELECTION_EXHAUSTED", ObservationStage.PlaceTargetOnSlit)]
    [InlineData("PHD2_PRE_GUIDE_SELECTION_UNAVAILABLE", ObservationStage.PlaceTargetOnSlit)]
    [InlineData("G3_POST_WCS_SHORT_UNCONFIRMED", ObservationStage.AcquireG3SlitField)]
    [InlineData("G3_MOTION_SINGLE_LIMIT", ObservationStage.AcquireG3SlitField)]
    [InlineData("G3_BOUNDED_SEARCH_EXHAUSTED_RETURNED", ObservationStage.AcquireG3SlitField)]
    [InlineData("G3_CATALOG_SHORT_POSITION_UNCONFIRMED", ObservationStage.AcquireG3SlitField)]
    public async Task RemainingQualityFailuresExecuteNextTargetOnlyAfterStop(string code, ObservationStage stage)
    {
        var gate = GateResult.TargetQuality(code, "Replay; no on-sky success implied.");
        var night = new NativeSequencePlanTests();
        await night.AssertNativeFailureBoundaryAsync("skip", code, gate, stage);
        await night.AssertNativeFailureBoundaryAsync("cleanup-failure", code, gate, stage);
    }
}
