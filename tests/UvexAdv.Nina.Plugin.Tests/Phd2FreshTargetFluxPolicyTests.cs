using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2FreshTargetFluxPolicyTests
{
    [Fact]
    public void CatalogRefinementRequiresFreshUniqueStellarPositionInsideOriginalAcquisitionWindow()
    {
        var point = new PixelPoint(803, 425);
        var star = new StarCandidate(point, 3000, 50_000, 30, 4, 0.1, 0, 200);
        var id = new TargetIdentification(GateResult.Pass("TARGET_IDENTIFIED", "fresh"),
            star, point, 4, 3, TargetIdentificationAuthority.StellarCentroid);
        Assert.True(Phd2FreshTargetFluxPolicy.CanRefineCatalogPositionWithStellarCentroid(id, 20));
        Assert.False(Phd2FreshTargetFluxPolicy.CanRefineCatalogPositionWithStellarCentroid(
            id with { Gate = GateResult.Unknown("TARGET_AMBIGUOUS", "ambiguous") }, 20));
        Assert.False(Phd2FreshTargetFluxPolicy.CanRefineCatalogPositionWithStellarCentroid(
            id with { PredictionResidualPixels = 21 }, 20));
        Assert.False(Phd2FreshTargetFluxPolicy.CanRefineCatalogPositionWithStellarCentroid(
            id with { Authority = TargetIdentificationAuthority.CatalogWcsProjection }, 20));
        Assert.False(Phd2FreshTargetFluxPolicy.CanRefineCatalogPositionWithStellarCentroid(
            id with { Target = star with { FluxAdu = double.NaN } }, 20));
    }

    [Theory]
    [InlineData(true, "TARGET_IDENTIFIED_SATURATED_TOPOLOGY", TargetIdentificationAuthority.BrightWingCentroid, true)]
    [InlineData(false, "TARGET_IDENTIFIED_SATURATED_TOPOLOGY", TargetIdentificationAuthority.BrightWingCentroid, false)]
    [InlineData(true, "TARGET_IDENTIFIED", TargetIdentificationAuthority.StellarCentroid, false)]
    [InlineData(true, "TARGET_IDENTIFIED", TargetIdentificationAuthority.BrightWingCentroid, false)]
    public void OnlyFreshTypedSaturatedTopologyWithCatalogWcsChainUsesNonPhotometricFlux(
        bool formalWcs, string code, TargetIdentificationAuthority authority, bool expected)
    {
        var point = new PixelPoint(803.6, 425.3);
        var source = new StarCandidate(point, 4095, 289_073_664, 50, 15, 0.1, 1, 300);
        var identification = new TargetIdentification(
            GateResult.Pass(code, "fresh topology"), source, point, 0, 2, authority);
        Assert.Equal(expected, Phd2FreshTargetFluxPolicy.UsesSaturatedTopologyFluxNotApplicable(identification, formalWcs));
        Assert.False(Phd2FreshTargetFluxPolicy.UsesSaturatedTopologyFluxNotApplicable(
            identification with { Gate = GateResult.Unknown(code, "uncertain") }, formalWcs));
    }
}
