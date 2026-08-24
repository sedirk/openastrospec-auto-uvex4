using UvexAdv.Spectroscopy;

namespace UvexAdv.Spectroscopy.Tests;

public sealed class SpectralTraceQualityAnalyzerTests
{
    [Fact]
    public void FindsClippedWavelengthColumnsEvenWhenWholeFrameFractionIsSmall()
    {
        const int width = 1_000;
        const int height = 1_000;
        var pixels = Enumerable.Repeat(256d, width * height).ToArray();
        for (var x = 300; x < 720; x++) pixels[(500 * width) + x] = 65_520;
        for (var x = 0; x < width; x++)
        {
            if (pixels[(500 * width) + x] < 65_000) pixels[(500 * width) + x] = 20_000;
        }

        var quality = SpectralTraceQualityAnalyzer.Analyze(
            new SpectralImage(width, height, pixels, 65_535),
            new ImageRoi(0, 0, width, height),
            DispersionAxis.Horizontal,
            apertureStart: 450,
            apertureLength: 100);

        Assert.InRange(quality.FullFrameSaturatedFraction, 0.0004, 0.0005);
        Assert.InRange(quality.ClippedDispersionColumnFraction, 0.419, 0.421);
        Assert.Equal(420, quality.LongestClippedDispersionColumnRun);
        Assert.Equal(500, quality.TraceSpatialCenterPixel);
        Assert.True(quality.TraceSaturatedFraction > 0.04);
    }

    [Fact]
    public void SupportsVerticalDispersionAndReportsTraceCenter()
    {
        const int width = 80;
        const int height = 200;
        var pixels = Enumerable.Repeat(300d, width * height).ToArray();
        for (var y = 0; y < height; y++) pixels[(y * width) + 37] = 12_000;

        var quality = SpectralTraceQualityAnalyzer.Analyze(
            new SpectralImage(width, height, pixels, 65_535),
            new ImageRoi(0, 0, width, height),
            DispersionAxis.Vertical,
            apertureStart: 10,
            apertureLength: 60);

        Assert.Equal(37, quality.TraceSpatialCenterPixel);
        Assert.Equal(0, quality.ClippedDispersionColumnFraction);
        Assert.True(quality.LineSnrPerResolutionElement > 100);
    }
}
