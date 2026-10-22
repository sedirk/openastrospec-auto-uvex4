using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2FreshTargetFluxPolicyTests
{
    [Fact]
    public void FirstMeasuredCentroidIsNotRejectedAsDiscontinuousFromAnUnmeasuredPrediction()
    {
        var predicted = new PixelPoint(819.263, 422.308);
        var firstMeasured = new PixelPoint(897.624, 498.039);
        var forecast = TargetIdentification.FromCatalogWcs(predicted, 1920,1080,"Neighbour WCS prediction");
        Assert.False(Phd2FreshTargetFluxPolicy.HasMeasuredHandoffPosition(forecast));
        Assert.False(Phd2TargetContinuityPolicy.RejectMeasuredDiscontinuity(
            Phd2FreshTargetFluxPolicy.HasMeasuredHandoffPosition(forecast), firstMeasured, predicted, 6));
        // This removes only an invalid continuity comparison, NOT the 100px
        // search gate. The recorded 109px mismatch is still not selectable.
        Assert.False(Phd2TargetContinuityPolicy.IsContinuous(firstMeasured, predicted, 100));
        Assert.True(Phd2TargetContinuityPolicy.RejectMeasuredDiscontinuity(true, firstMeasured, predicted, 6));
        Assert.False(Phd2TargetContinuityPolicy.RejectMeasuredDiscontinuity(true, new(900,499), firstMeasured, 6));
        Assert.True(Phd2TargetContinuityPolicy.RejectMeasuredDiscontinuity(true, new(920,499), firstMeasured, 6));
    }

    [Fact]
    public void AllThreeInitialPathsCarryReferenceAuthorityAndAcceptedFrameActivatesContinuity()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Sources","RealObservationStageRunner.Phd2SlitPlacement.cs"));
        Assert.Equal(3, source.Split("referenceTargetMeasured: Phd2FreshTargetFluxPolicy.HasMeasuredHandoffPosition(").Length - 1);
        Assert.Contains("referenceGuide: referenceGuide, referenceTargetMeasured: referenceTargetMeasured)", source);
        Assert.Contains("referenceTargetMeasured, targetLocal, expectedTargetLocal, continuityAllowance)", source);
        Assert.Contains("referenceTargetMeasured = true;", source);
    }

    [Fact]
    public void ConfirmedNeighbourIdentitySurvivesExposureFramesWithoutInventingTargetFieldSolve()
    {
        var id = new TargetIdentification(GateResult.Pass("TARGET_IDENTIFIED", "short measured"),
            new StarCandidate(new(850, 460), 65520, 41185424, 50, 15, .1, 1, 300), new(850, 460), 0, 2);
        var field = G3FieldState.Failed(GateResult.Pass("G3_FIELD_ANALYZED_MOTION_PREDICTED", "verified neighbour"))
            with { TargetIdentification = id, NeighbourCatalogIdentityEvidencePath = "verified-motion-and-two-shorts.json" };
        var exposureOnly = field with { FramePath = "new-exposure.fit", TargetIdentification = id with { PredictionResidualPixels = 2 } };
        Assert.Null(exposureOnly.Solve);
        Assert.Equal("verified-motion-and-two-shorts.json", Phd2FreshTargetFluxPolicy.CatalogIdentityEvidence(exposureOnly));
        Assert.Null(Phd2FreshTargetFluxPolicy.CatalogIdentityEvidence(field with { NeighbourCatalogIdentityEvidencePath = null }));
        Assert.Null(Phd2FreshTargetFluxPolicy.CatalogIdentityEvidence(field with { Gate = GateResult.Unknown("UNCONFIRMED", "") }));
        Assert.Null(Phd2FreshTargetFluxPolicy.CatalogIdentityEvidence(field with {
            TargetIdentification = id with { Gate = GateResult.Unknown("TARGET_AMBIGUOUS", "") } }));
        Assert.Null(Phd2FreshTargetFluxPolicy.CatalogIdentityEvidence(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MeasuredHandoffKeepsActualFluxAuthorityAndProvenance(bool shortPosition)
    {
        var prediction = TargetIdentification.FromCatalogWcs(new(816.445, 422.2), 1920, 1080, "neighbour prediction");
        var measured = new TargetIdentification(GateResult.Pass("TARGET_IDENTIFIED", "HD 223554 fresh centroid"),
            new StarCandidate(new(812.552, 420.257), 3070, 1396800, 58.4, 10.83, .1, 0, 100),
            new(816.445, 422.2), 4.35, 2.6227)
        { BoundShortPositionEvidencePath = shortPosition ? "independent-short.json" : null,
            CatalogPositionSpreadPixels = shortPosition ? 1.14 : 0 };
        var retained = Phd2FreshTargetFluxPolicy.PreserveMeasuredHandoff(prediction, measured, false);
        Assert.Same(measured.Target, retained.Target);
        Assert.Equal(measured.Authority, retained.Authority);
        Assert.Equal(measured.BoundShortPositionEvidencePath, retained.BoundShortPositionEvidencePath);
        Assert.Equal(measured.CatalogPositionSpreadPixels, retained.CatalogPositionSpreadPixels);
        Assert.True(Phd2FreshTargetFluxPolicy.HasMeasuredHandoffPosition(retained));
        Assert.Same(prediction, Phd2FreshTargetFluxPolicy.PreserveMeasuredHandoff(prediction, measured, true));
        Assert.Same(prediction, Phd2FreshTargetFluxPolicy.PreserveMeasuredHandoff(prediction,
            measured with { Gate = GateResult.Unknown("TARGET_AMBIGUOUS", "not measured") }, false));
        Assert.Same(prediction, Phd2FreshTargetFluxPolicy.PreserveMeasuredHandoff(prediction, null, false));
    }

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
