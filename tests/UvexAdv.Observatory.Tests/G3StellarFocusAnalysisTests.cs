using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3StellarFocusAnalysisTests
{
    [Fact]
    public void AnalyzerAcceptsBroadMildlyComaticMultiStarField()
    {
        const int width = 320, height = 220;
        var pixels = Enumerable.Repeat((ushort)900, width * height).ToArray();
        var stars = new[]
        {
            (45d, 45d), (105d, 58d), (170d, 44d), (255d, 60d),
            (72d, 142d), (150d, 157d), (245d, 145d), (285d, 178d)
        };
        foreach (var (x, y) in stars)
        {
            AddEllipticalGaussian(pixels, width, height, x, y, 3.5, 2.4, 15000);
            AddEllipticalGaussian(pixels, width, height, x + 3.2, y + 0.8, 4.2, 2.8, 3200);
        }

        var result = G3StellarFocusAnalyzer.Analyze(new MonochromeFrame(width, height, pixels, 65520));

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.InRange(result.StarCount, 6, 8);
        Assert.InRange(result.MedianFwhmPixels, 5.0, 12.0);
        Assert.InRange(result.MedianEllipticity, 0.15, 0.75);
        Assert.InRange(result.Confidence, 0.42, 1);
        Assert.All(result.Gate.Metrics!, pair => Assert.True(double.IsFinite(pair.Value)));
    }

    [Fact]
    public void AnalyzerReturnsUnknownWhenVeryBroadComaticFieldCannotSupportFocus()
    {
        const int width = 240, height = 160;
        var pixels = Enumerable.Repeat((ushort)1100, width * height).ToArray();
        AddEllipticalGaussian(pixels, width, height, 70, 75, 8.5, 1.6, 18000);
        AddEllipticalGaussian(pixels, width, height, 165, 82, 9.0, 1.7, 17000);

        var result = G3StellarFocusAnalyzer.Analyze(new MonochromeFrame(width, height, pixels, 65520));

        Assert.Equal(GateDisposition.Indeterminate, result.Gate.Disposition);
        Assert.Contains(result.Gate.Code, new[]
        {
            "G3_FOCUS_STARS_INSUFFICIENT",
            "G3_FOCUS_STARS_TOO_ELONGATED",
            "G3_FOCUS_CONFIDENCE_LOW"
        });
        Assert.False(double.IsNaN(result.MedianFwhmPixels));
    }

    [Fact]
    public void AnalyzerRejectsTwelvePixelWideFieldSeenInCommissioningReplay()
    {
        const int width = 320, height = 220;
        var pixels = Enumerable.Repeat((ushort)900, width * height).ToArray();
        var stars = new[]
        {
            (45d, 45d), (105d, 58d), (170d, 44d), (255d, 60d),
            (72d, 142d), (150d, 157d), (245d, 145d), (285d, 178d)
        };
        foreach (var (x, y) in stars)
        {
            // sigma=5.25 px corresponds to a nominal Gaussian FWHM of about
            // 12.36 px, matching the 12.34-12.90 px measured in the real G3
            // Deneb commissioning frames that visibly show annular/crescent
            // defocus.
            AddEllipticalGaussian(pixels, width, height, x, y, 5.25, 5.25, 15000);
        }

        var result = G3StellarFocusAnalyzer.Analyze(
            new MonochromeFrame(width, height, pixels, 65520));

        Assert.Equal(GateDisposition.Indeterminate, result.Gate.Disposition);
        Assert.Equal("G3_FOCUS_STARS_TOO_BROAD", result.Gate.Code);
        Assert.True(result.MedianFwhmPixels > 10);
    }

    [Fact]
    public void AnalyzerDoesNotPromoteSinglePixelReadNoiseMaximaToBroadStars()
    {
        const int width = 320, height = 220;
        var random = new Random(20260817);
        var pixels = new ushort[width * height];
        for (var index = 0; index < pixels.Length; index++)
        {
            // A clipped, low-offset distribution resembles the short G3 commissioning frames.
            var noise = 17 + NextGaussian(random) * 13;
            pixels[index] = (ushort)Math.Clamp(Math.Round(noise), 0, 4095);
        }

        // Hot/read-noise peaks are deliberately numerous enough for the permissive local-maximum
        // detector to find them.  None has the coherent 3x3 core required of a focus star.
        for (var index = 0; index < 250; index++)
        {
            var x = random.Next(15, width - 15);
            var y = random.Next(15, height - 15);
            pixels[y * width + x] = (ushort)random.Next(90, 180);
        }

        var result = G3StellarFocusAnalyzer.Analyze(
            new MonochromeFrame(width, height, pixels, 4095));

        Assert.Equal(GateDisposition.Indeterminate, result.Gate.Disposition);
        Assert.Equal("G3_FOCUS_STARS_NOT_DETECTED", result.Gate.Code);
        Assert.Equal(0, result.StarCount);
        Assert.Equal(0, result.DetectedStarCount);
        Assert.Contains("local maxima", result.Gate.Message);
    }

    [Fact]
    public void RobustFitFindsInteriorMinimumDespiteOneOutlier()
    {
        var samples = new[] { 4550, 4600, 4650, 4700, 4750, 4800, 4850, 4900, 4950 }
            .Select(position => Sample(position, 4.4 + 0.000035 * Math.Pow(position - 4750, 2) + (position == 4600 ? 4.5 : 0)))
            .ToArray();

        var result = G3StellarFocusPlanner.Fit(samples, 4550);

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.True(result.IsActionable);
        Assert.InRange(result.RecommendedPositionSteps!.Value, 4735, 4765);
        Assert.InRange(result.PredictedMinimumFwhmPixels, 4.0, 5.0);
        Assert.True(result.CurvaturePerStepSquared > 0);
        Assert.InRange(result.RobustOutlierCount, 1, 2);
        Assert.All(result.Gate.Metrics!, pair => Assert.True(double.IsFinite(pair.Value)));
    }

    [Fact]
    public void FlatScanDoesNotAuthorizeFocusPosition()
    {
        var samples = new[] { 4600, 4650, 4700, 4750, 4800, 4850, 4900 }
            .Select((position, index) => Sample(position, 5.0 + (index % 2 == 0 ? 0.01 : -0.01)))
            .ToArray();

        var result = G3StellarFocusPlanner.Fit(samples, 4600);

        Assert.NotEqual(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Null(result.RecommendedPositionSteps);
        Assert.Equal(4600, result.FallbackPositionSteps);
        Assert.Contains(result.Gate.Code, new[] { "G3_FOCUS_CURVATURE_INVALID", "G3_FOCUS_CURVATURE_TOO_FLAT", "G3_FOCUS_MINIMUM_AT_BOUNDARY" });
    }

    [Fact]
    public void BoundaryMinimumDoesNotAuthorizeFocusPosition()
    {
        var samples = new[] { 4600, 4650, 4700, 4750, 4800, 4850, 4900 }
            .Select(position => Sample(position, 4.2 + 0.00004 * Math.Pow(position - 4580, 2)))
            .ToArray();

        var result = G3StellarFocusPlanner.Fit(samples, 4600);

        Assert.Equal(GateDisposition.Indeterminate, result.Gate.Disposition);
        Assert.Equal("G3_FOCUS_MINIMUM_AT_BOUNDARY", result.Gate.Code);
        Assert.Null(result.RecommendedPositionSteps);
        Assert.Equal(4600, result.FallbackPositionSteps);
    }

    [Fact]
    public void VerificationWorseningBeyondThresholdRequiresInitialPositionRollback()
    {
        var samples = new[] { 4550, 4600, 4650, 4700, 4750, 4800, 4850, 4900, 4950 }
            .Select(position => Sample(position, 4.5 + 0.000035 * Math.Pow(position - 4750, 2)))
            .ToArray();
        var plan = G3StellarFocusPlanner.Fit(samples, 4550);
        Assert.True(plan.IsActionable);
        var verification = Measurement(plan.InitialFwhmPixels * 1.025);

        var result = G3StellarFocusPlanner.Verify(plan, verification);

        Assert.Equal(GateDisposition.Failed, result.Gate.Disposition);
        Assert.Equal("G3_FOCUS_VERIFICATION_WORSE", result.Gate.Code);
        Assert.True(result.MustReturnToFallback);
        Assert.Equal(4550, result.SelectedPositionSteps);
        Assert.Equal(4550, result.FallbackPositionSteps);
        Assert.InRange(result.ChangeFromInitialFraction, 0.0249, 0.0251);
    }

    [Fact]
    public void NonFiniteScanValueIsRejectedWithoutNonFiniteOutput()
    {
        var samples = new[]
        {
            Sample(4600, 7), Sample(4650, 5.8), Sample(4700, double.NaN),
            Sample(4750, 4.5), Sample(4800, 5.1)
        };

        var result = G3StellarFocusPlanner.Fit(samples, 4600);

        Assert.Equal("G3_FOCUS_SCAN_INSUFFICIENT", result.Gate.Code);
        Assert.False(result.IsActionable);
        Assert.All(result.Gate.Metrics!, pair => Assert.True(double.IsFinite(pair.Value)));
    }

    private static G3StellarFocusScanSample Sample(int position, double fwhm) =>
        new(position, Measurement(fwhm));

    private static G3StellarFocusMeasurement Measurement(double fwhm) => new(
        GateResult.Pass("SYNTHETIC", "Synthetic passing focus metric."),
        fwhm,
        0.25,
        8,
        8,
        0,
        30,
        0.05,
        0.9,
        Array.Empty<StarCandidate>());

    private static void AddEllipticalGaussian(
        ushort[] pixels,
        int width,
        int height,
        double centerX,
        double centerY,
        double sigmaX,
        double sigmaY,
        double amplitude)
    {
        var radiusX = (int)Math.Ceiling(sigmaX * 5);
        var radiusY = (int)Math.Ceiling(sigmaY * 5);
        for (var y = Math.Max(0, (int)Math.Floor(centerY) - radiusY); y <= Math.Min(height - 1, (int)Math.Ceiling(centerY) + radiusY); y++)
        for (var x = Math.Max(0, (int)Math.Floor(centerX) - radiusX); x <= Math.Min(width - 1, (int)Math.Ceiling(centerX) + radiusX); x++)
        {
            var dx = (x - centerX) / sigmaX;
            var dy = (y - centerY) / sigmaY;
            var value = pixels[y * width + x] + amplitude * Math.Exp(-(dx * dx + dy * dy) / 2);
            pixels[y * width + x] = (ushort)Math.Min(65520, Math.Round(value));
        }
    }

    private static double NextGaussian(Random random)
    {
        var u1 = Math.Max(double.Epsilon, random.NextDouble());
        var u2 = random.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}
