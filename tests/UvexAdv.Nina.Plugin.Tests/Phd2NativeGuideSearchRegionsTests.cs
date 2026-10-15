using UvexAdv.Observatory;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2NativeGuideSearchRegionsTests
{
    [Fact]
    public void RecordedHd220539SeedWouldLeaveDetectorDuringSlitPlacement()
    {
        var guide = new PixelPoint(1325.29, 1058.41);
        var target = new PixelPoint(788.531, 410.521);
        var slit = new SlitGeometry("slit", new(817, 429.571), -2, 410, 3.5, .5, "G3", 1, 1);
        Assert.True(1079 - guide.Y > 20); // Old seed-only check admitted it.
        Assert.Equal(1.54, Phd2NativeGuideSearchRegions.PlannedEdgeDistance(1920, 1080,
            guide, target, slit.AcquisitionPoint), 3);
        var regions = Phd2NativeGuideSearchRegions.Build(1920, 1080, target, 120, slit, 20, 10, [guide], []);
        Assert.NotEmpty(regions); // Reject this candidate, not the target/night.
        foreach (var r in regions)
        foreach (var p in new[] { new PixelPoint(r.X, r.Y), new PixelPoint(r.X + r.Width - 1, r.Y + r.Height - 1) })
            Assert.True(Phd2NativeGuideSearchRegions.PlannedEdgeDistance(1920, 1080, p, target, slit.AcquisitionPoint) >= 20);
    }

    [Theory]
    [InlineData(25, 500, -30, 0)]
    [InlineData(1895, 500, 30, 0)]
    [InlineData(1000, 25, 0, -30)]
    [InlineData(1000, 1055, 0, 30)]
    public void DirectionalGuardRejectsOutwardTravelButAllowsMovingAway(double x, double y, double dx, double dy)
    {
        var target = new PixelPoint(817, 429);
        Assert.True(Phd2NativeGuideSearchRegions.PlannedEdgeDistance(1920, 1080, new(x, y),
            target, new(target.X + dx, target.Y + dy)) < 20);
        Assert.True(Phd2NativeGuideSearchRegions.PlannedEdgeDistance(1920, 1080, new(x, y),
            target, new(target.X - dx, target.Y - dy)) >= 20);
    }

    [Fact]
    public void LedExtentExcludesAsymmetricTailBeyondCommissionedCentralSegment()
    {
        var slit = new SlitGeometry("slit", new(817, 426), -2, 410, 3.5, .5, "G3", 1, 1)
        { IlluminationExtent = new(-364, 540, 36, false, false) };
        var regions = Phd2NativeGuideSearchRegions.Build(1920, 1080, new(817, 426), 60, slit, 20, 10, [], []);
        Assert.NotEmpty(regions);
        foreach (var r in regions)
        for (var y = r.Y; y < r.Y + r.Height; y += 8)
        for (var x = r.X; x < r.X + r.Width; x += 8)
            Assert.True(GuideStarSelector.DistanceToGuideExclusion(new(x, y), slit) > 11.75);
    }

    [Fact]
    public void RegionsExcludeHaloFiniteSlitEdgesAndRejectedPointsWithoutRankingStars()
    {
        var target = new PixelPoint(837, 410);
        var rejected = new PixelPoint(761, 363);
        var slit = new SlitGeometry("slit", new(817, 426), 2, 950, 6, 1, "G3", 1, 1);
        var regions = Phd2NativeGuideSearchRegions.Build(1920, 1080, target, 120, slit, 20, 10, [rejected], []);
        Assert.NotEmpty(regions);
        foreach (var r in regions)
        {
            Assert.InRange(r.X, 21, 1899);
            Assert.InRange(r.Y, 21, 1059);
            Assert.True(r.X + r.Width <= 1899 && r.Y + r.Height <= 1059);
            for (var y = r.Y; y < r.Y + r.Height; y += 10)
            for (var x = r.X; x < r.X + r.Width; x += 10)
            {
                Assert.True(Math.Sqrt(Math.Pow(x - target.X, 2) + Math.Pow(y - target.Y, 2)) > 120);
                Assert.True(GuideStarSelector.DistanceToSlit(new(x, y), slit) > 13);
                Assert.True(Math.Abs(x - rejected.X) > 20 || Math.Abs(y - rejected.Y) > 20);
            }
        }
        var next = Phd2NativeGuideSearchRegions.Build(1920, 1080, target, 120, slit, 20, 10, [rejected], [regions[0]]);
        Assert.NotEmpty(next);
        Assert.All(next, r => Assert.False(Overlaps(r, regions[0])));
    }

    [Fact]
    public void InvalidTargetCannotDefineASearchRegion()
    {
        var slit = new SlitGeometry("slit", new(817, 426), 2, 950, 6, 1, "G3", 1, 1);
        Assert.Empty(Phd2NativeGuideSearchRegions.Build(1920, 1080, new(double.NaN, 10), 120, slit, 20, 10, [], []));
        Assert.Empty(Phd2NativeGuideSearchRegions.Build(1920, 1080, new(9999, 10), 120, slit, 20, 10, [], []));
    }

    private static bool Overlaps(Phd2Rectangle a, Phd2Rectangle b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
}
