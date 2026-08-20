using UvexAdv.Spectroscopy;

namespace UvexAdv.Spectroscopy.Tests;

public sealed class SpectroscopyTests
{
    [Fact]
    public void ExtractsHorizontalSpectrumAndSubtractsBackground()
    {
        const int width = 80;
        const int height = 20;
        var pixels = Enumerable.Repeat(100d, width * height).ToArray();
        for (var y = 7; y < 13; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[(y * width) + x] += Gaussian(x, 40, 2) * 1000;
            }
        }

        var image = new SpectralImage(width, height, pixels, 65_535);
        var spectrum = SpectrumExtractor.Extract(image, new SpectrumExtractionOptions(
            new ImageRoi(0, 0, width, height),
            DispersionAxis.Horizontal,
            ApertureStart: 7,
            ApertureLength: 6));
        var line = SpectralLineMeasurer.Measure(spectrum, new SpectralLineWindow(40, 10), minimumSnr: 5);

        Assert.True(line.IsValid, line.FailureReason);
        Assert.InRange(line.CentroidPixel, 39.9, 40.1);
        Assert.InRange(line.FwhmPixels, 4.5, 4.9);
    }

    [Fact]
    public void FindsFocusCurveMinimum()
    {
        var samples = Enumerable.Range(-3, 7)
            .Select(offset => new FocusSample(
                1000 + (offset * 100),
                new FocusMetric(2 + (0.00002 * Math.Pow(offset * 100 - 25, 2)), 3, 1, [])))
            .ToArray();

        var fit = FocusCurveFitter.Fit(samples);

        Assert.True(fit.IsValid, fit.FailureReason);
        Assert.InRange(fit.OptimumPositionSteps, 1024.9, 1025.1);
        Assert.True(fit.RSquared > 0.99);
    }

    [Fact]
    public void MeasuresAbsorptionLineWithAutomaticPolarity()
    {
        var flux = Enumerable.Range(0, 100)
            .Select(x => 1000 - (Gaussian(x, 40, 2) * 300) + ((x % 2) * 0.5))
            .ToArray();
        var spectrum = new Spectrum1D(flux, 0, new ImageRoi(0, 0, 100, 3), DispersionAxis.Horizontal);

        var line = SpectralLineMeasurer.Measure(spectrum, new SpectralLineWindow(40, 10), minimumSnr: 5);

        Assert.True(line.IsValid, line.FailureReason);
        Assert.Equal(SpectralLinePolarity.Absorption, line.Polarity);
        Assert.InRange(line.CentroidPixel, 39.9, 40.1);
        Assert.InRange(line.FwhmPixels, 4.5, 4.9);
    }

    [Fact]
    public void DetectsAndRepairsDocumentedAtr585mSdkWrap()
    {
        const int width = 256;
        const int height = 80;
        var original = new double[width * height];
        for (var y = 0; y < height; y++)
        {
            var spatial = Math.Exp(-0.5 * Math.Pow((y - 37) / 3d, 2));
            for (var x = 0; x < width; x++)
            {
                var spectrum = 500 + (2 * x) + (Gaussian(x, 150, 12) * 300);
                original[(y * width) + x] = 200 + (20 * spatial * spectrum);
            }
        }

        var wrapped = new double[original.Length];
        for (var y = 0; y < height; y++)
        {
            Array.Copy(original, (y * width) + width - 64, wrapped, y * width, 64);
            Array.Copy(original, y * width, wrapped, (y * width) + 64, width - 64);
        }

        var result = Atr585mSdkWrapRepair.DetectAndRepairHorizontal(
            wrapped,
            width,
            height,
            new ImageRoi(0, 0, width, height),
            28,
            20);

        Assert.True(result.Applied);
        Assert.Equal(-64, result.AppliedShiftPixels);
        Assert.True(result.SeamScoreSigma > 4);
        Assert.Equal(original, wrapped);
    }

    [Fact]
    public void LeavesCleanAtr585mFrameUnchanged()
    {
        const int width = 256;
        const int height = 20;
        var pixels = Enumerable.Range(0, width * height)
            .Select(index => 500d + (index % width))
            .ToArray();
        var original = pixels.ToArray();

        var result = Atr585mSdkWrapRepair.DetectAndRepairHorizontal(
            pixels,
            width,
            height,
            new ImageRoi(0, 0, width, height),
            0,
            height);

        Assert.False(result.Applied);
        Assert.Equal(original, pixels);
    }

    [Fact]
    public void FitsWavelengthPolynomial()
    {
        var points = Enumerable.Range(0, 7)
            .Select(i => new WavelengthPoint(i * 500, 400 + (0.05 * i * 500) + (0.000001 * Math.Pow(i * 500, 2))))
            .ToArray();

        var solution = WavelengthCalibrator.Fit(points);

        Assert.InRange(solution.RmsNm, 0, 1e-8);
        Assert.InRange(solution.PixelToWavelengthNm(1250), 463.9, 464.1);
    }

    [Theory]
    [InlineData(100.1, true, 0)]
    [InlineData(95, false, 35)]
    public void CalculatesBoundedWavelengthCorrection(double centroid, bool withinTolerance, int expectedSteps)
    {
        var line = new SpectralLineMeasurement(centroid, 3, 30, 100, 500, true);
        var correction = WavelengthLock.Calculate(line, 100, 10, tolerancePixels: 0.25);

        Assert.Equal(withinTolerance, correction.WithinTolerance);
        Assert.Equal(expectedSteps, correction.CorrectionSteps);
    }

    private static double Gaussian(double x, double center, double sigma) =>
        Math.Exp(-0.5 * Math.Pow((x - center) / sigma, 2));
}
