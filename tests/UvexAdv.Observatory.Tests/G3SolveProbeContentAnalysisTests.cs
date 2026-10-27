using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3SolveProbeContentAnalysisTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void BrightGradientNoiseAndIsolatedHotPixelsDoNotAuthorizeMotion(int seed)
    {
        const int width = 512, height = 384;
        var random = new Random(seed);
        var pixels = new ushort[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var gaussian = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
            pixels[y * width + x] = (ushort)Math.Clamp(12000 + x * 30 + y * 10 + gaussian * 1800, 0, 65520);
        }
        for (var i = 0; i < 8; i++) pixels[random.Next(pixels.Length)] = 65520;
        var result = G3SolveProbeContentAnalyzer.Analyze(new MonochromeFrame(width, height, pixels, 65520));
        Assert.False(result.HasCoherentSource);
        Assert.Equal("G3_CLOUD_OR_TRANSPARENCY_INVALID", result.Gate.Code);
    }

    [Fact]
    public void FlatCloudLikeFrameDoesNotAuthorizeSearchMotion()
    {
        var pixels = Enumerable.Repeat((ushort)800, 96 * 96).ToArray();

        var result = G3SolveProbeContentAnalyzer.Analyze(new MonochromeFrame(96, 96, pixels, 4095));

        Assert.False(result.HasCoherentSource);
        Assert.Equal(GateDisposition.Indeterminate, result.Gate.Disposition);
        Assert.Equal("G3_CLOUD_OR_TRANSPARENCY_INVALID", result.Gate.Code);
    }

    [Fact]
    public void SaturatedButSpatiallyCoherentSourceRemainsStructured()
    {
        const int width = 96;
        const int height = 96;
        var pixels = Enumerable.Repeat((ushort)500, width * height).ToArray();
        for (var y = 39; y <= 57; y++)
        for (var x = 39; x <= 57; x++)
        {
            var radiusSquared = (x - 48) * (x - 48) + (y - 48) * (y - 48);
            if (radiusSquared <= 36) pixels[y * width + x] = 4095;
            else if (radiusSquared <= 81) pixels[y * width + x] = 2600;
        }

        var result = G3SolveProbeContentAnalyzer.Analyze(new MonochromeFrame(width, height, pixels, 4095));

        Assert.True(result.HasCoherentSource);
        Assert.True(
            result.SaturatedPixelFraction <
            G3SolveProbeContentAnalyzer.MaximumSearchAuthorizingSaturatedPixelFraction);
        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal("G3_SOLVE_PROBE_STRUCTURED_FIELD", result.Gate.Code);
        Assert.NotEqual(G3SolveProbeContentAnalyzer.OverexposedGateCode, result.Gate.Code);
    }

    [Fact]
    public void FullyClippedFrameGetsDedicatedOverexposureGate()
    {
        const int width = 96;
        const int height = 96;
        const ushort saturation = 65520;
        var pixels = Enumerable.Repeat(saturation, width * height).ToArray();

        var result = G3SolveProbeContentAnalyzer.Analyze(
            new MonochromeFrame(width, height, pixels, saturation));

        Assert.Equal(1d, result.SaturatedPixelFraction, precision: 12);
        Assert.Equal(GateDisposition.Indeterminate, result.Gate.Disposition);
        Assert.Equal(G3SolveProbeContentAnalyzer.OverexposedGateCode, result.Gate.Code);
        Assert.NotNull(result.Gate.Metrics);
        Assert.Contains("sampledSaturatedPixelFraction", result.Gate.Metrics!.Keys);
    }
}
