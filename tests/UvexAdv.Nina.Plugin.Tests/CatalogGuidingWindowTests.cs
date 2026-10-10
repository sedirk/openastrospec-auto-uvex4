using UvexAdv.Phd2;
using Xunit;
namespace UvexAdv.Nina.Plugin.Tests;

public sealed class CatalogGuidingWindowTests
{
    [Theory]
    [InlineData(1,2)]
    [InlineData(2,2.5)]
    [InlineData(3,3)]
    [InlineData(4,3)]
    public void WarningTiersAreBoundedAndLegacyPackagesStayStrict(int window, double expected)
    {
        Assert.Equal(expected, Phd2PlacementGuideWindowPolicy.ToleranceForFreshWindow(2,3,window));
        Assert.Equal(2, Phd2PlacementGuideWindowPolicy.ToleranceForFreshWindow(2,0,window));
    }

    [Theory]
    [InlineData(2,3.01)]
    [InlineData(1,2)]
    [InlineData(2,1.9)]
    [InlineData(2,-1)]
    [InlineData(2,double.NaN)]
    [InlineData(double.PositiveInfinity,3)]
    public void WarningPolicyRejectsUnboundedOrMalformedCeilings(double preferred,double ceiling) =>
        Assert.False(Phd2PlacementGuideWindowPolicy.IsValidWarningCeiling(preferred,ceiling));

    [Fact]
    public void RecordedWindowCanWarnAtThirdTierButNotBeCalledStrictOrGrantSlitCompletion()
    {
        Phd2SlitFieldMeasurement Sample() => new(new string('a',64), DateTimeOffset.UtcNow,
            new string('b',64), new(103,100), new(200,200), new(215,200), 25, true, 2000,
            true,"identity","flux",100,"residual", Phd2TargetPositionAuthority.DetectedTargetCentroid,
            GuidePositionMeasuredInFrame:true);
        var samples = new[] { Sample(), Sample(), Sample() };
        double[] residuals = [2.65704,2.18835,2.85773];
        foreach(var tolerance in new[]{2d,2.5d})
            Assert.False(Phd2PlacementGuideWindowPolicy.CanAcceptWarningWindow(samples,residuals,tolerance,true,true));
        Assert.True(Phd2PlacementGuideWindowPolicy.CanAcceptWarningWindow(samples,residuals,3,true,true));
        Assert.False(Phd2PlacementGuideWindowPolicy.AllWithinTolerance(residuals,2));
        Assert.False(Phd2PlacementGuideWindowPolicy.CanAcceptWarningWindow(samples,residuals,3,false,true));
        Assert.False(Phd2PlacementGuideWindowPolicy.CanAcceptWarningWindow(samples,residuals,3,true,false));
        Assert.False(Phd2PlacementGuideWindowPolicy.CanAcceptWarningWindow(samples,[2,2,3.01],3,true,true));
        Assert.False(Phd2PlacementGuideWindowPolicy.CanAcceptWarningWindow(samples,[2,double.NaN,2],3,true,true));
        foreach(var invalid in new[]{Sample() with { TargetIdentityConfirmed=false },
            Sample() with { GuidePositionMeasuredInFrame=false },
            Sample() with { TargetPositionAuthority=Phd2TargetPositionAuthority.CatalogWcsProjection }})
            Assert.False(Phd2PlacementGuideWindowPolicy.CanAcceptWarningWindow([samples[0],samples[1],invalid],residuals,3,true,true));
    }

    [Fact]
    public void PostLockNativeSettleStillMustSucceedWithinOriginalStage()
    {
        var criteria=Phd2SlitSettleTiming.ForBoundedWarningPlacement(new(2,10,120),3,30);
        Assert.Equal(new Phd2SettleCriteria(3,5,18),criteria);
        var source=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Sources","RealObservationStageRunner.Phd2SlitPlacement.cs"));
        Assert.Contains("if (!stageSettle.Succeeded || !stageProof.HasCurrentSuccessfulSettle",source);
        Assert.Contains("PHD2_TARGET_AT_SLIT_MIDPOINT_GUIDE_PRECISION_WARNING",source);
        Assert.Contains("maximumWarningTolerance = preset.EffectiveGuideLockResidualPixels",source);
    }

    [Fact]
    public void PreMotionCompletedWindowRejectionIsNotSuccessfulPlacementOrPostMotionSkip()
    {
        var now = DateTimeOffset.UtcNow;
        var output = new Phd2GuideOutputStatus(45, 0, now, now, false,
            new(45, 2, 2, 20, 4, 3, 0, "On-Step", 2, 2, 20, 40));
        var s = Phd2StateSnapshot.Disconnected with { IsConnected = true, AppState = Phd2AppState.Guiding,
            ConnectionEpoch = 7, GuideEpoch = 98, GuideOutput = output };
        // Recorded 01:59 window: native initial settle passed; optical residuals did not.
        var failure = new Phd2PlacementGuideWindowRejectedException(4, [2.65704, 2.18835, 2.85773], 2, 7, 98, new(770, 932));
        Assert.True(failure.CanClassifyBeforeMotion(s, false));
        Assert.False(failure.CanClassifyBeforeMotion(s, true));
        foreach (var changed in new[] { s with { GuideEpoch = 99 }, s with { ConnectionEpoch = 8 },
            s with { IsConnected = false }, s with { AppState = Phd2AppState.LostLock },
            s with { AutomationPaused = true }, s with { Phd2Paused = true },
            s with { PendingSettleOperationId = 3 }, s with { GuideOutput = null },
            s with { GuideOutput = output with { Failed = true } },
            s with { GuideOutput = output with { ConsecutiveMissingOutputs = 1 } } })
            Assert.False(failure.CanClassifyBeforeMotion(changed, false));
        foreach (var rejected in new[] {
            new Phd2PlacementGuideWindowRejectedException(0, [3,3,3], 2, 7, 98, new(770,932)),
            new Phd2PlacementGuideWindowRejectedException(4, [3,3], 2, 7, 98, new(770,932)),
            new Phd2PlacementGuideWindowRejectedException(4, [3,double.NaN,3], 2, 7, 98, new(770,932)),
            new Phd2PlacementGuideWindowRejectedException(4, [1,1,1], 2, 7, 98, new(770,932)) })
            Assert.False(rejected.CanClassifyBeforeMotion(s, false));
    }

    [Fact]
    public void ProductionOnlyCatchesTypedRejectionAtInitialWindowAndChecksStopBeforeQualityCode()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Sources","RealObservationStageRunner.Phd2SlitPlacement.cs"));
        Assert.Single(source.Split("catch (Phd2PlacementGuideWindowRejectedException").Skip(1));
        var caught = source.IndexOf("catch (Phd2PlacementGuideWindowRejectedException", StringComparison.Ordinal);
        Assert.True(caught > source.IndexOf("firstMeasurements = await CapturePhd2PlacementGuideWindowAsync",StringComparison.Ordinal));
        Assert.True(caught < source.IndexOf("var first = firstMeasurements[^1]",StringComparison.Ordinal));
        var stop = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Sources","RealObservationStageRunner.GuideOutput.cs"));
        var branch = stop[stop.IndexOf("StopPreMotionGuideWindowQualityFailureAsync",StringComparison.Ordinal)..stop.IndexOf("StopInitialGuideQualityFailureAsync",StringComparison.Ordinal)];
        Assert.True(branch.IndexOf("ValidateConfirmedPhdStop",StringComparison.Ordinal) < branch.IndexOf("GateResult.Unknown",StringComparison.Ordinal));
        Assert.Contains("CanStopOwnedSession",branch);
        Assert.Contains("ValidateIdentityAsync",branch);
        Assert.Contains("pendingPhd2LockShift is not null",branch);
    }

    [Theory]
    [InlineData(true,0,"PHD2_FRESH_GUIDE_WINDOW_DEADLINE")]
    [InlineData(true,3,"PHD2_GUIDE_WINDOW_NOT_STABLE")]
    [InlineData(false,4,"PHD2_GUIDE_WINDOW_NOT_STABLE")]
    public void ReportsCompletedRejectionsBeforeDeadline(bool expired,int complete,string code) =>
        Assert.Equal(code,Phd2PlacementGuideWindowPolicy.FailureCode(expired,complete));

    [Fact]
    public void OnlyMeasuredPositionsCanUseTheOptedInWarning()
    {
        Assert.False(Phd2PlacementGuideWindowPolicy.HasMeasuredPositionAuthority(Phd2TargetPositionAuthority.CatalogWcsProjection));
        Assert.False(Phd2PlacementGuideWindowPolicy.HasMeasuredPositionAuthority((Phd2TargetPositionAuthority)99));
        Assert.True(Phd2PlacementGuideWindowPolicy.HasMeasuredPositionAuthority(Phd2TargetPositionAuthority.CatalogWcsRegisteredField));
        // The real first recorded window stays a quality warning, not "within 2 pixels".
        Assert.False(Phd2PlacementGuideWindowPolicy.AllWithinTolerance([1.72665,3.28141,1.15137],2));
    }
}
