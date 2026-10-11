using UvexAdv.Phd2;
using Xunit;
namespace UvexAdv.Nina.Plugin.Tests;

public sealed class CatalogGuidingWindowTests
{
    [Fact]
    public void PrecisionUsesOneThreePixelWarningWithoutEscalation()
    {
        Assert.Equal(3, Phd2PlacementGuideWindowPolicy.GuideResidualWarningPixels);
        Assert.False(Phd2PlacementGuideWindowPolicy.HasPrecisionWarning([2,2.5,3]));
        Assert.True(Phd2PlacementGuideWindowPolicy.HasPrecisionWarning([2,3,3.01]));
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
    public void RecordedSixCepWindowContinuesAboveThreeButNeverGrantsSlitCompletion()
    {
        Phd2SlitFieldMeasurement Sample() => new(new string('a',64), DateTimeOffset.UtcNow,
            new string('b',64), new(103,100), new(200,200), new(215,200), 25, true, 2000,
            true,"identity","flux",100,"residual", Phd2TargetPositionAuthority.DetectedTargetCentroid,
            GuidePositionMeasuredInFrame:true);
        var samples = new[] { Sample(), Sample(), Sample() };
        double[] residuals = [3.524213671,3.575492693,4.967944545];
        Assert.True(Phd2PlacementGuideWindowPolicy.HasUsableMeasuredWindow(samples,residuals,true,true));
        Assert.True(Phd2PlacementGuideWindowPolicy.HasPrecisionWarning(residuals));
        Assert.False(Phd2PlacementGuideWindowPolicy.AllWithinTolerance(residuals,2));
        Assert.False(Phd2PlacementGuideWindowPolicy.HasUsableMeasuredWindow(samples,residuals,false,true));
        Assert.False(Phd2PlacementGuideWindowPolicy.HasUsableMeasuredWindow(samples,residuals,true,false));
        Assert.True(Phd2PlacementGuideWindowPolicy.HasUsableMeasuredWindow(samples,[2,2,30],true,true));
        Assert.False(Phd2PlacementGuideWindowPolicy.HasUsableMeasuredWindow(samples,[2,double.NaN,2],true,true));
        Assert.False(Phd2PlacementGuideWindowPolicy.HasUsableMeasuredWindow(samples,[2,-1,2],true,true));
        Assert.False(Phd2PlacementGuideWindowPolicy.HasUsableMeasuredWindow(samples[..2],[2,2],true,true));
        foreach(var invalid in new[]{Sample() with { TargetIdentityConfirmed=false },
            Sample() with { GuidePositionMeasuredInFrame=false },
            Sample() with { TargetPositionAuthority=Phd2TargetPositionAuthority.CatalogWcsProjection }})
            Assert.False(Phd2PlacementGuideWindowPolicy.HasUsableMeasuredWindow([samples[0],samples[1],invalid],residuals,true,true));
    }

    [Fact]
    public void PostLockMeasuredContinuationPreservesOriginalDeadlineAndAdvisoryGuidePrecision()
    {
        var criteria=Phd2SlitSettleTiming.ForBoundedWarningPlacement(new(2,10,120),3,30);
        Assert.Equal(new Phd2SettleCriteria(3,5,18),criteria);
        var source=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Sources","RealObservationStageRunner.Phd2SlitPlacement.cs"));
        Assert.Contains("stageReadOnlyObservation.AcceptResiduals",WorkflowSource.Convergence);
        Assert.Contains("measuredContinuation: true",WorkflowSource.Ports);
        Assert.Contains("exact.CompletedUtc.AddSeconds(preset.MaximumStageSeconds)",WorkflowSource.Convergence);
        Assert.Contains("PHD2_TARGET_AT_SLIT_MIDPOINT_GUIDE_PRECISION_WARNING",WorkflowSource.Convergence);
        var window = source[source.IndexOf("private async Task<IReadOnlyList<Phd2GuidingResidualState>> CapturePhd2PlacementGuideWindowAsync",StringComparison.Ordinal)..source.IndexOf("private async Task VerifyWindSampledGuidingBeforeAtrAsync",StringComparison.Ordinal)];
        Assert.Contains("precisionBlocks = false",window);
        Assert.DoesNotContain("ToleranceForFreshWindow",window);
        Assert.DoesNotContain("HasSupervisedScienceOptIn",window);
        Assert.DoesNotContain("for (",window);
        Assert.DoesNotContain("throw new Phd2PlacementGuideWindowRejectedException",window);
    }

    [Fact]
    public void ObsoletePrecisionStopCannotBeReintroducedThroughAnInitialWindowCatch()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.Phd2SlitPlacement.cs"));
        var output = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.GuideOutput.cs"));
        Assert.DoesNotContain("Phd2PlacementGuideWindowRejectedException", source);
        Assert.DoesNotContain("StopPreMotionGuideWindowQualityFailureAsync", output);
        // Real initial guide failure retains its owner, output and confirmed-stop checks.
        var branch = output[output.IndexOf("StopInitialGuideQualityFailureAsync", StringComparison.Ordinal)..output.IndexOf("HaltFailedPhd2GuideOutputAsync", StringComparison.Ordinal)];
        Assert.Contains("ValidateIdentityAsync", branch);
        Assert.Contains("CanStopOwnedSession", branch);
        Assert.Contains("ThrowIfGuideOutputFailed", branch);
        Assert.True(branch.IndexOf("ValidateConfirmedPhdStop", StringComparison.Ordinal) <
            branch.IndexOf("GateResult.Unknown", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(2.65704, 2.18835, 2.85773, false)]
    [InlineData(3.524213671, 3.575492693, 4.967944545, true)]
    public void RecordedInitialPrecisionRejectionsNowRemainUsableMeasuredWindows(
        double first, double second, double third, bool warning)
    {
        Phd2SlitFieldMeasurement Sample() => new(new string('a', 64), DateTimeOffset.UtcNow,
            new string('b', 64), new(103, 100), new(200, 200), new(215, 200), 25, true, 2000,
            true, "identity", "flux", 100, "residual", Phd2TargetPositionAuthority.DetectedTargetCentroid,
            GuidePositionMeasuredInFrame: true);
        double[] residuals = [first, second, third];
        var samples = new[] { Sample(), Sample(), Sample() };
        Assert.True(Phd2PlacementGuideWindowPolicy.HasUsableMeasuredWindow(samples, residuals, true, true));
        Assert.Equal(warning, Phd2PlacementGuideWindowPolicy.HasPrecisionWarning(residuals));
        Assert.False(Phd2PlacementGuideWindowPolicy.AllWithinTolerance(residuals, 2));
        // Removing an unreachable precision stop must not remove live ownership/output guards.
        Assert.False(Phd2PlacementGuideWindowPolicy.HasUsableMeasuredWindow(samples, residuals, false, true));
        Assert.False(Phd2PlacementGuideWindowPolicy.HasUsableMeasuredWindow(samples, residuals, true, false));
    }

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
