using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3CatalogTargetPositionPolicyTests
{
    [Fact]
    public void SameFrameTrn29OffsetIsIncludedBeforeCoarseHandoffNotDeferredToGuideTakeover()
    {
        var prediction = new PixelPoint(803.5828, 432.1583);
        var slit = new PixelPoint(817.5425, 428.8558);
        var pixels = Enumerable.Repeat((ushort)1000, 1920 * 1080).ToArray();
        for (var y = 472; y <= 486; y++)
        for (var x = 764; x <= 778; x++)
            if ((x - 771) * (x - 771) + (y - 479) * (y - 479) < 49) pixels[y * 1920 + x] = 40000;
        var frame = new MonochromeFrame(1920, 1080, pixels, 65520);
        var identified = G3CatalogTargetPositionPolicy.Identify(frame,
            [new StarCandidate(new(771, 479), 40000, 100000, 80, 4, 0, 0, 400)], prediction, false, 100);
        Assert.True(identified.CatalogPositionRefinedFromSameFrame);
        Assert.Equal(TargetIdentificationAuthority.CatalogWcsProjection, identified.Authority);
        Assert.InRange(identified.PredictionResidualPixels, 55, 60);
        Assert.InRange(Distance(identified.Target!.Centroid, slit), 65, 72);
        var destination = G3CatalogTargetPositionPolicy.ProjectionDestination(identified, slit, 100);
        // Applying the fresh WCS displacement must put the measured star at
        // the slit, not the catalogue projection. No fixed optical offset.
        var movedStar = new PixelPoint(identified.Target.Centroid.X + destination.X - prediction.X,
            identified.Target.Centroid.Y + destination.Y - prediction.Y);
        Assert.InRange(Distance(movedStar, slit), 0, 1e-9);
    }

    [Fact]
    public void InvisibleTargetNeverSnapsToAnUnrelatedVisibleStar()
    {
        var point = new PixelPoint(80, 60);
        var frame = new MonochromeFrame(200, 150, new ushort[200 * 150], 65520);
        var star = new StarCandidate(new(120, 75), 30000, 100000, 80, 4, 0, 0, 60);
        var target = G3CatalogTargetPositionPolicy.Identify(frame, [star], point, true, 100);
        Assert.False(target.CatalogPositionRefinedFromSameFrame);
        Assert.Equal(point, target.Target!.Centroid);
        Assert.Equal(0, target.Target.FluxAdu);
        Assert.Equal(point, G3CatalogTargetPositionPolicy.ProjectionDestination(target, point, 100));
    }

    [Theory]
    [InlineData(18, true)]
    [InlineData(58, true)]
    [InlineData(101, false)]
    public void RefinementNeverExpandsTheCommissionedRecognitionWindow(double distance, bool accepted)
    {
        var frame = new MonochromeFrame(400, 200, new ushort[400 * 200], 65520);
        var point = new PixelPoint(100, 100);
        var candidate = new StarCandidate(new(100 + distance, 100), 5000, 10000, 30, 3, 0, 0, 50);
        var result = G3CatalogTargetPositionPolicy.Identify(frame, [candidate], point, false, 100);
        Assert.Equal(accepted, result.CatalogPositionRefinedFromSameFrame);
    }

    [Fact]
    public void AmbiguousCandidatesRemainCatalogOnlyAndCannotProvideACorrectionOffset()
    {
        var frame = new MonochromeFrame(200, 200, new ushort[40000], 65520);
        var a = new StarCandidate(new(105, 100), 5000, 10000, 30, 3, 0, 0, 50);
        var result = G3CatalogTargetPositionPolicy.Identify(frame, [a, a with { Centroid = new(94, 100) }], new(100, 100), false, 100);
        Assert.False(result.CatalogPositionRefinedFromSameFrame);
        Assert.Equal(new PixelPoint(90, 90), G3CatalogTargetPositionPolicy.ProjectionDestination(result, new(90, 90), 100));
        Assert.Throws<InvalidOperationException>(() => G3CatalogTargetPositionPolicy.ProjectionDestination(
            result with { CatalogPositionRefinedFromSameFrame = true, Target = a with { Centroid = new(250, 100) } }, new(90, 90), 100));
    }

    private static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(Math.Pow(a.X-b.X, 2) + Math.Pow(a.Y-b.Y, 2));

    [Fact]
    public void ClippedRecognitionRegionCannotRefineCatalogPositionFromAHaloFragment()
    {
        var pixels = new ushort[400 * 400];
        pixels[190 * 400 + 190] = 65520;
        var frame = new MonochromeFrame(400, 400, pixels, 65520);
        var projection = new PixelPoint(200, 200);
        var fragment = new StarCandidate(new(220, 220), 65520, 10000, 30, 3, 0, .5, 50);
        var identified = G3CatalogTargetPositionPolicy.Identify(frame, [fragment], projection, false, 100);
        Assert.True(G3CatalogTargetPositionPolicy.NeedsShortPositionCheck(frame, projection, 100));
        Assert.False(identified.HasCatalogPositionRefinement);
        Assert.Equal(projection, identified.Target!.Centroid);
        Assert.Contains("short-exposure", identified.Gate.Message);
    }

    [Fact]
    public void SeparatelyAttestedShortPositionUsesMeasuredOffsetButDoesNotClaimSameFrame()
    {
        var frame = new MonochromeFrame(400, 400, new ushort[160000], 65520);
        var projection = new PixelPoint(200, 200);
        var candidate = new StarCandidate(new(150, 210), 40000, 100000, 80, 4, 0, 0, 100);
        var measured = SlitTargetIdentifier.Identify(frame, [candidate], projection, 100);
        Assert.True(G3CatalogTargetPositionPolicy.CanUseShortMeasurement(frame, measured));
        var refined = measured with { Authority = TargetIdentificationAuthority.CatalogWcsProjection,
            BoundShortPositionEvidencePath = "bound-short-confirmation.json" };
        Assert.False(refined.CatalogPositionRefinedFromSameFrame);
        Assert.True(refined.HasCatalogPositionRefinement);
        Assert.Equal(new PixelPoint(250,190), G3CatalogTargetPositionPolicy.ProjectionDestination(refined, projection, 100));
        Assert.False(G3CatalogTargetPositionPolicy.CanUseShortMeasurement(frame, refined));
        Assert.False(G3CatalogTargetPositionPolicy.CanUseShortMeasurement(frame,
            measured with { Target = candidate with { SaturatedFraction = .01 } }));
        Assert.False(G3CatalogTargetPositionPolicy.CanUseShortMeasurement(frame,
            measured with { Gate = GateResult.Unknown("TARGET_AMBIGUOUS", "ambiguous") }));
        Assert.Throws<InvalidOperationException>(() => G3CatalogTargetPositionPolicy.ProjectionDestination(
            refined with { Target = candidate with { Centroid = new(10,10) } }, projection, 100));
    }

    [Fact]
    public void OversizedRegionRemainsAnExclusionEvenWhenItsCentreIsOutsideRecognitionWindow()
    {
        var pixels = Enumerable.Repeat((ushort)1000,512*512).ToArray();
        for (var y=80;y<304;y++) for(var x=80;x<304;x++) pixels[y*512+x]=65520;
        for (var y=190;y<193;y++) for(var x=308;x<311;x++) pixels[y*512+x]=65520;
        var frame = new MonochromeFrame(512,512,pixels,65520);
        var prediction = new PixelPoint(320,192);
        var topology = SaturatedTargetGhostTopologyAnalyzer.Analyze(frame,prediction,100);
        Assert.NotEqual(GateDisposition.Passed,topology.Gate.Disposition);
        Assert.Null(topology.Target);
        Assert.Contains(topology.Candidates,c=>c.Gate.Code=="SATURATED_SOURCE_OVERSIZED" && c.SaturatedPixels==50176);
        Assert.Contains(topology.Candidates,c=>c.Gate.Code=="SATURATED_SOURCE_HALO_FRAGMENT" && c.SaturatedPixels==9);
        var fragment = new StarCandidate(new(309,191),65520,10000,50,3,0,.5,100);
        var identified = SlitTargetIdentifier.Identify(frame,[fragment],prediction,100);
        Assert.NotEqual(GateDisposition.Passed,identified.Gate.Disposition);
    }
}
