using UvexAdv.Observatory;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2NativeGuideSearchRegionsTests
{
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
