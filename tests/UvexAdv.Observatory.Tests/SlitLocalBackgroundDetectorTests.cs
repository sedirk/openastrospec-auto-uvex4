using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class SlitLocalBackgroundDetectorTests
{
    private static readonly SlitGeometry Seed = new("fresh-led", new PixelPoint(64, 40), 0, 100, 2.25, 0.5, "G3", 1, 1);

    [Fact]
    public void AlongSlitIlluminationGradientDoesNotBecomeDetectorNoise()
    {
        var image = Frame(slit: true);
        var ordinary = SlitLocusDetector.DetectDarkSlit(image, Seed, 2, 1, 3);
        var result = SlitLocalBackgroundDetector.Detect(image, Seed, 12, 2, 3);
        Assert.Equal(GateDisposition.Indeterminate, ordinary.Gate.Disposition);
        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.True(result.ContrastSigma >= 3);
        Assert.InRange(Math.Abs(result.PerpendicularOffsetPixels), 0, 2);
        Assert.InRange(Math.Abs(result.AngleOffsetDegrees), 0, 1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void GradientOrOneBrightFlankCannotInventADarkSlit(bool slit, bool brightFlank)
    {
        var result = SlitLocalBackgroundDetector.Detect(Frame(slit, brightFlank), Seed, 12, 2, 3);
        Assert.Equal(GateDisposition.Indeterminate, result.Gate.Disposition);
    }

    [Fact]
    public void SaturatedOrBlankFrameCannotPass()
    {
        foreach (var value in new ushort[] { 0, 4095 })
        {
            var image = new MonochromeFrame(128, 80, Enumerable.Repeat(value, 128 * 80).ToArray(), 4095);
            Assert.Equal(GateDisposition.Indeterminate, SlitLocalBackgroundDetector.Detect(image, Seed, 12, 2, 3).Gate.Disposition);
        }
    }

    private static MonochromeFrame Frame(bool slit, bool brightFlank = false)
    {
        var pixels = new ushort[128 * 80];
        for (var y = 0; y < 80; y++)
        for (var x = 0; x < 128; x++)
        {
            var value = 100 + 4 * x + ((x * 7 + y * 3) % 5);
            if (slit && Math.Abs(y - 40) <= 1) value -= 10;
            if (brightFlank && y == 48) value += 100;
            pixels[y * 128 + x] = (ushort)value;
        }
        return new MonochromeFrame(128, 80, pixels, 4095);
    }
}
