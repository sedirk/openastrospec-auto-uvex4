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
            if ((x - 771) * (x - 771) + (y - 479) * (y - 479) < 49) pixels[y * 1920 + x] = 65520;
        var frame = new MonochromeFrame(1920, 1080, pixels, 65520);
        var identified = G3CatalogTargetPositionPolicy.Identify(frame, [], prediction, false, 100);
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
}
