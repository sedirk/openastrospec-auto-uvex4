using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3SolveProbeContentAnalysisTests
{
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
        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal("G3_SOLVE_PROBE_STRUCTURED_FIELD", result.Gate.Code);
    }
}
