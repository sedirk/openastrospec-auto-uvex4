using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3PostWcsMeasuredHandoffPolicyTests
{
    private static readonly PixelPoint Predicted = new(821, 428);
    private static readonly PixelPoint Slit = new(817, 427);

    private static TargetIdentification Measured(double x = 813, double y = 441) => new(
        GateResult.Pass("TARGET_IDENTIFIED", "Measured unique stellar centroid"),
        new StarCandidate(new PixelPoint(x, y), 1000, 10000, 50, 3, 0, 0, 100),
        Predicted, 15, 2);

    private static bool Accept(TargetIdentification target, double uncertainty = 5) =>
        G3PostWcsMeasuredHandoffPolicy.CanHandOff(target, Predicted, uncertainty, Slit, 1920, 1080, 20);

    [Fact]
    public void ActualNearSlitCentroidCanAvoidRedundantSolve() => Assert.True(Accept(Measured()));

    [Fact]
    public void SaturatedSolidCoreAloneCannotProveAnUnblendedTarget() => Assert.False(Accept(Measured() with
    {
        Gate = GateResult.Pass("TARGET_IDENTIFIED_SATURATED_TOPOLOGY", "Unique filled core"),
        Authority = TargetIdentificationAuthority.BrightWingCentroid,
    }));

    [Fact]
    public void MatchingIndependentUnsaturatedShortFrameConfirmsASaturatedCore()
    {
        var saturated = Measured() with {
            Gate = GateResult.Pass("TARGET_IDENTIFIED_SATURATED_TOPOLOGY", "Filled clipped component"),
            Authority = TargetIdentificationAuthority.BrightWingCentroid };
        Assert.True(G3PostWcsMeasuredHandoffPolicy.CanProposeHandoff(saturated, Predicted, 5, Slit, 1920,1080,20));
        Assert.True(G3PostWcsMeasuredHandoffPolicy.CanHandOff(saturated, Predicted, 5, Slit, 1920,1080,20, Measured(814,440)));
        Assert.False(G3PostWcsMeasuredHandoffPolicy.CanHandOff(saturated, Predicted, 5, Slit, 1920,1080,20, saturated));
    }

    [Fact]
    public void DenebLongExposureMergedStarGhostIsRejectedByShortExposureActualCore()
    {
        var longBlob = Measured(814.42,409.14) with {
            Gate = GateResult.Pass("TARGET_IDENTIFIED_SATURATED_TOPOLOGY", "Merged star and ghost"),
            Authority = TargetIdentificationAuthority.BrightWingCentroid };
        Assert.True(G3PostWcsMeasuredHandoffPolicy.CanProposeHandoff(longBlob, Predicted, 5, Slit,1920,1080,20));
        Assert.False(G3PostWcsMeasuredHandoffPolicy.CanHandOff(longBlob, Predicted, 5, Slit,1920,1080,20, Measured(827.28,476.91)));
        Assert.False(Accept(longBlob));
    }

    [Fact]
    public void CatalogProjectionAloneCannotSkipSolve() => Assert.False(Accept(
        TargetIdentification.FromCatalogWcs(Predicted, 1920, 1080, "Prediction only")));

    [Theory]
    [InlineData(0.030470914127423823)]
    [InlineData(double.NaN)]
    [InlineData(-0.01)]
    public void OrdinaryClippedHaloCannotMasqueradeAsNearSlitTarget(double saturatedFraction)
    {
        var target = Measured(823.4586, 417.0654);
        Assert.False(Accept(target with
        {
            Target = target.Target! with { SaturatedFraction = saturatedFraction },
        }));
    }

    [Fact]
    public void IdentifiedCoreOutsideFineWindowStillRequiresFormalSolve()
    {
        var target = Measured(812.46, 373.10);
        Assert.False(Accept(target with
        {
            Gate = GateResult.Pass("TARGET_IDENTIFIED_SATURATED_TOPOLOGY", "Measured filled core"),
            Authority = TargetIdentificationAuthority.BrightWingCentroid,
            Target = target.Target! with { SaturatedFraction = 1 },
        }));
    }

    [Fact]
    public void FailedOrAmbiguousIdentificationCannotSkipSolve() => Assert.False(Accept(Measured() with
    {
        Gate = GateResult.Unknown("TARGET_AMBIGUOUS", "Two candidates"),
    }));

    [Theory]
    [InlineData(850, 427)]
    [InlineData(817, 450)]
    [InlineData(double.NaN, 427)]
    public void ActualPositionMustRemainInBothOriginalWindows(double x, double y) =>
        Assert.False(Accept(Measured(x, y)));

    [Fact]
    public void ExcessPredictionUncertaintyStillRequiresOriginalRoute() => Assert.False(Accept(Measured(), 21));
}
