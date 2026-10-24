using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class SlitTargetPixelSupportTests
{
    [Fact]
    public void OrdinaryApertureCandidateBelowItsOwnBackgroundCannotAnchorAnOutsideBrightSource()
    {
        var (pixels, prediction, noise) = Scene();
        var frame = new MonochromeFrame(300, 220, pixels, 65520);
        var result = SlitTargetIdentifier.Identify(frame, [noise], prediction, 40);
        Assert.Equal("TARGET_NOT_FOUND", result.Gate.Code);
        Assert.Null(result.Target);
        Assert.Equal(1, result.Gate.Metrics!["unsupportedOrdinaryCandidateCount"]);
        Assert.True(result.Gate.Metrics["unsupportedCoreMedianAdu"] <= result.Gate.Metrics["unsupportedBackgroundAdu"]);
        Assert.True(result.Gate.Metrics["unsupportedCoreMedianAdu"] <= result.Gate.Metrics["unsupportedBoundaryUpperQuartileAdu"]);
        // Finding the outside primary for morphology never expands this window.
        Assert.Equal(prediction, result.PredictedPoint);
    }

    [Fact]
    public void TrueFaintComponentInsideTheWindowSurvivesWithTheBrightPrimaryOutside()
    {
        var (pixels, prediction, _) = Scene();
        AddGaussian(pixels, 300, 220, prediction.X, prediction.Y, 2, 1200);
        var real = new StarCandidate(prediction, 2200, 15000, 12, 4.7, .1, 0, 90);
        var result = SlitTargetIdentifier.Identify(new(300, 220, pixels, 65520), [real], prediction, 40);
        Assert.Equal("TARGET_IDENTIFIED", result.Gate.Code);
        Assert.Same(real, result.Target);
        Assert.Equal(TargetIdentificationAuthority.StellarCentroid, result.Authority);
    }

    [Fact]
    public void WeakPositiveCoreIsNotRequiredToPassTheIndependentCompanionTwoSigmaPreference()
    {
        var pixels = Enumerable.Repeat((ushort)1000, 100 * 100).ToArray();
        for (var y = 49; y <= 51; y++)
        for (var x = 49; x <= 51; x++) pixels[y * 100 + x] = 1001;
        var weak = new StarCandidate(new(50, 50), 1001, 200, 8, 4, .1, 0, 49);
        var result = SlitTargetIdentifier.Identify(new(100, 100, pixels), [weak], new(50, 50), 20);
        Assert.Equal("TARGET_IDENTIFIED", result.Gate.Code);
        Assert.Same(weak, result.Target);
    }

    [Fact]
    public void TwoSupportedCloseComponentsKeepTheOriginalAmbiguityRule()
    {
        var pixels = Enumerable.Repeat((ushort)1000, 100 * 100).ToArray();
        AddGaussian(pixels, 100, 100, 44, 50, 1.5, 1500);
        AddGaussian(pixels, 100, 100, 56, 50, 1.5, 3000);
        var first = new StarCandidate(new(44, 50), 2500, 15000, 12, 3.5, .1, 0, 40);
        var second = new StarCandidate(new(56, 50), 4000, 30000, 20, 3.5, .1, 0, 40);
        var result = SlitTargetIdentifier.Identify(new(100, 100, pixels), [first, second], new(50, 50), 20);
        Assert.Equal("TARGET_AMBIGUOUS", result.Gate.Code);
        Assert.NotEqual(GateDisposition.Passed, result.Gate.Disposition);
    }

    [Fact]
    public void AnIsolatedDetectedStarKeepsItsMeasuredCentroid()
    {
        var pixels = Enumerable.Repeat((ushort)1000, 100 * 100).ToArray();
        AddGaussian(pixels, 100, 100, 52, 48, 2.2, 3000);
        var frame = new MonochromeFrame(100, 100, pixels);
        var result = SlitTargetIdentifier.Identify(frame, StarFieldDetector.Detect(frame), new(50, 50), 20);
        Assert.Equal("TARGET_IDENTIFIED", result.Gate.Code);
        Assert.InRange(result.Target!.Centroid.X, 51.8, 52.2);
        Assert.InRange(result.Target.Centroid.Y, 47.8, 48.2);
    }

    private static (ushort[] Pixels, PixelPoint Prediction, StarCandidate Noise) Scene()
    {
        const int width = 300, height = 220;
        var pixels = Enumerable.Repeat((ushort)1000, width * height).ToArray();
        AddGaussian(pixels, width, height, 220, 150, 10, 100000);
        var prediction = new PixelPoint(100, 100);
        for (var y = 99; y <= 101; y++)
        for (var x = 99; x <= 101; x++) pixels[y * width + x] = 800;
        pixels[100 * width + 100] = 5000; // Aperture/hot-pixel maximum, not a supported core.
        var noise = new StarCandidate(prediction, 5000, 30000, 20, 5.8, .1, 0, 90);
        return (pixels, prediction, noise);
    }

    private static void AddGaussian(ushort[] pixels, int width, int height,
        double cx, double cy, double sigma, double amplitude)
    {
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = 1000 + amplitude * Math.Exp(-((x - cx) * (x - cx) + (y - cy) * (y - cy)) / (2 * sigma * sigma));
            pixels[y * width + x] = Math.Max(pixels[y * width + x], (ushort)Math.Clamp(value, 0, 65535));
        }
    }
}
