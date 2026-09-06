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
    public void SaturatedSolidCoreHasExplicitMeasuredAuthority() => Assert.True(Accept(Measured() with
    {
        Gate = GateResult.Pass("TARGET_IDENTIFIED_SATURATED_TOPOLOGY", "Unique filled core"),
        Authority = TargetIdentificationAuthority.BrightWingCentroid,
    }));

    [Fact]
    public void CatalogProjectionAloneCannotSkipSolve() => Assert.False(Accept(
        TargetIdentification.FromCatalogWcs(Predicted, 1920, 1080, "Prediction only")));

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
