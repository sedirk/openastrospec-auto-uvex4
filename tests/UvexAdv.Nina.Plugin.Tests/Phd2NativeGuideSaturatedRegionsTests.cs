using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2NativeGuideSaturatedRegionsTests
{
    [Theory]
    [InlineData(1000, "AnnularGhost")]
    [InlineData(60000, "Indeterminate")]
    public void NativeRingRimIsExcludedEvenOutsideTargetRecognitionRadius(int centerValue, string topology)
    {
        const int width = 480, height = 360;
        var pixels = Enumerable.Repeat((ushort)1000, width * height).ToArray();
        for (var y = 150; y <= 250; y++)
        for (var x = 270; x <= 370; x++)
        {
            var radius = Math.Sqrt(Math.Pow(x - 320, 2) + Math.Pow(y - 200, 2));
            if (radius <= 28) pixels[y * width + x] = (ushort)centerValue;
            if (radius is >= 16 and <= 28) pixels[y * width + x] = 65520;
        }
        var frame = new MonochromeFrame(width, height, pixels, 65520);
        var exclusions = Phd2NativeGuideSaturatedRegions.Measure(frame, new GuideStarSelectionPolicy());
        var ring = Assert.Single(exclusions);
        Assert.Equal(topology, ring.Topology);
        Assert.True(Phd2NativeGuideSaturatedRegions.Contains(ring.Region, new(342, 200)));
        // No rank or substitute: a separately selected native star remains usable.
        Assert.False(Phd2NativeGuideSaturatedRegions.Contains(ring.Region, new(390, 200)));
        Assert.Equal((ushort)65520, frame[342, 200]);
    }

    [Fact]
    public void UnsaturatedBroadStarIsNotTurnedIntoAHardMorphologyVeto()
    {
        var pixels = Enumerable.Repeat((ushort)1000, 240 * 180).ToArray();
        for (var y = 60; y < 110; y++)
        for (var x = 80; x < 130; x++) pixels[y * 240 + x] = 50000;
        Assert.Empty(Phd2NativeGuideSaturatedRegions.Measure(new(240, 180, pixels, 65520), new()));
    }

    [Fact]
    public void NativeRetryRegionsExcludeWholeMeasuredStructureNotOnlyOneRejectedRimPoint()
    {
        var slit = new SlitGeometry("slit", new(817, 426), 2, 950, 6, 1, "G3", 1, 1);
        var structure = new UvexAdv.Phd2.Phd2Rectangle(689, 178, 177, 177);
        var regions = Phd2NativeGuideSearchRegions.Build(1920, 1080, new(806, 434), 120,
            slit, 20, 10, [new(747, 235)], [], [structure]);
        Assert.NotEmpty(regions);
        Assert.All(regions, r => Assert.False(r.X < structure.X + structure.Width &&
            structure.X < r.X + r.Width && r.Y < structure.Y + structure.Height && structure.Y < r.Y + r.Height));
    }
}
