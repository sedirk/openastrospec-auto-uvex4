using System.Text.Json;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class SlitIlluminationExtentTests
{
    private static SlitGeometry Seed(double angle = -2) => new("test", new(400, 250), angle, 160, 3.5, .5, "G3", 1, 1);

    [Theory]
    [InlineData(-2)]
    [InlineData(0)]
    [InlineData(17)]
    public void DetectsAsymmetricFaintEndsBeyondOldRoiWithoutMovingAnchor(double angle)
    {
        var seed = Seed(angle);
        var shortPair = Pair(seed, 1);
        var longPair = Pair(seed, 2);
        var result = SlitIlluminationExtentAnalyzer.Analyze(shortPair.Off, shortPair.On, longPair.Off, longPair.On, seed);
        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.InRange(result.Extent!.StartOffsetPixels, -232, -168);
        Assert.InRange(result.Extent.EndOffsetPixels, 568, 632);
        Assert.True(result.Extent.LengthPixels > 4 * seed.LengthPixels);
        Assert.Equal(0, result.Gate.Metrics!["physicalEndpointsProven"]);
        Assert.Equal(0, result.Gate.Metrics["placementAuthorityExpanded"]);
        var geometry = seed with { IlluminationExtent = result.Extent };
        Assert.Equal(seed.AcquisitionPoint, geometry.AcquisitionPoint);
        Assert.Equal(seed.WidthPixels, geometry.WidthPixels);
        Assert.Equal(seed.LengthPixels, geometry.LengthPixels);
    }

    [Fact]
    public void GradientFixedStarsAndDetachedGlintDoNotSupplyALine()
    {
        var pair = Pair(Seed(), 1, noLine: true);
        var result = SlitIlluminationExtentAnalyzer.Analyze(pair.Off, pair.On, pair.Off, pair.On, Seed());
        Assert.NotEqual(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Null(result.Extent);
    }

    [Fact]
    public void LargeMissingIntervalIsNotBridgedToDetachedIllumination()
    {
        var pair = Pair(Seed(), 1, gap: true);
        var result = SlitIlluminationExtentAnalyzer.Analyze(pair.Off, pair.On, pair.Off, pair.On, Seed());
        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.InRange(result.Extent!.EndOffsetPixels, 168, 232);
    }

    [Fact]
    public void ClippedLongFrameFallsBackToValidShortSupport()
    {
        var pair = Pair(Seed(), 1);
        var clipped = new MonochromeFrame(pair.Off.Width, pair.Off.Height,
            Enumerable.Repeat(ushort.MaxValue, pair.Off.Width * pair.Off.Height).ToArray());
        var result = SlitIlluminationExtentAnalyzer.Analyze(pair.Off, pair.On, pair.Off, clipped, Seed());
        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.InRange(result.Extent!.LengthPixels, 740, 860);
        var absent = SlitIlluminationExtentAnalyzer.Analyze(pair.Off, clipped, pair.Off, clipped, Seed());
        Assert.Null(absent.Extent);
    }

    [Fact]
    public void ImageClippedSupportIsExplicitlyMarkedNotAResolvedEndpoint()
    {
        var seed = Seed() with { AcquisitionPoint = new(100, 250) };
        var pair = Pair(seed, 1);
        var result = SlitIlluminationExtentAnalyzer.Analyze(pair.Off, pair.On, pair.Off, pair.On, seed);
        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.True(result.Extent!.StartImageLimited);
        Assert.False(result.Extent.EndImageLimited);
    }

    [Fact]
    public void GuideExclusionExpandsButNearestPlacementAndLegacyJsonStayUnchanged()
    {
        var seed = Seed(0);
        var extended = seed with { IlluminationExtent = new(-200, 600, 36, false, false) };
        var tail = new PixelPoint(900, 250);
        Assert.Equal(0, GuideStarSelector.DistanceToGuideExclusion(tail, extended));
        Assert.Equal(new PixelPoint(480, 250), GuideStarSelector.ClosestPointOnSlit(tail, extended));
        Assert.Equal(420, GuideStarSelector.DistanceToSlit(tail, extended));
        var native = GuideStarSelector.ValidateNativeSelection([], extended,
            new StarCandidate(new(400, 100), 2000, 10000, 30, 3, .1, 0, 80), tail, 5);
        Assert.Equal(GateDisposition.Failed, native.Gate.Disposition);
        var legacy = JsonSerializer.Deserialize<SlitGeometry>("""
            {"CalibrationId":"legacy","AcquisitionPoint":{"X":400,"Y":250},"AngleDegrees":0,
            "LengthPixels":160,"WidthPixels":3.5,"UncertaintyPixels":0.5,"CameraIdentity":"G3","BinningX":1,"BinningY":1}
            """);
        Assert.Null(legacy!.IlluminationExtent);
        Assert.Equal(GuideStarSelector.DistanceToSlit(tail, legacy), GuideStarSelector.DistanceToGuideExclusion(tail, legacy));
        Assert.Equal(extended, JsonSerializer.Deserialize<SlitGeometry>(JsonSerializer.Serialize(extended)));
    }

    private static (MonochromeFrame Off, MonochromeFrame On) Pair(SlitGeometry seed, double scale,
        bool noLine = false, bool gap = false)
    {
        const int width = 1200, height = 520;
        var off = new ushort[width * height]; var on = new ushort[width * height];
        var random = new Random(913 + (int)scale);
        var angle = seed.AngleDegrees * Math.PI / 180;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var dx = x - seed.AcquisitionPoint.X; var dy = y - seed.AcquisitionPoint.Y;
            var along = dx * Math.Cos(angle) + dy * Math.Sin(angle);
            var normal = -dx * Math.Sin(angle) + dy * Math.Cos(angle);
            var star = 4000 * Math.Exp(-((x - 730d) * (x - 730d) + (y - 270d) * (y - 270d)) / 10);
            var background = 1000 + star + 0.2 * x + 0.3 * y;
            off[y * width + x] = (ushort)(background + random.Next(-8, 9));
            var signal = !noLine && along is >= -200 and <= 600 && !(gap && along is > 200 and < 330)
                ? (120 + 1600 * Math.Exp(-along * along / 12000)) * Math.Exp(-normal * normal / 8) : 0;
            var glint = 4000 * Math.Exp(-((along - 710) * (along - 710) + normal * normal) / 8);
            on[y * width + x] = (ushort)(background + scale * (800 + .3 * x + .6 * y + signal + glint) + random.Next(-8, 9));
        }
        return (new(width, height, off), new(width, height, on));
    }
}
