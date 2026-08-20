using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class SlitIlluminationPairAnalysisTests
{
    private const int Width = 280;
    private const int Height = 220;
    private static readonly SlitGeometry Seed = new(
        "historical-overlay-seed",
        new PixelPoint(140, 110),
        88,
        160,
        4,
        10,
        "G3M2210M-test",
        1,
        1);

    [Fact]
    public void BrightPairRecoversTranslationAngleAndWidthInsteadOfTrustingSeed()
    {
        const double expectedOffset = 13.5;
        const double expectedAngle = 91.5;
        const double expectedWidth = 5.5;
        var pair = CreatePair(
            [new SyntheticLine(expectedOffset, expectedAngle, expectedWidth, 850)],
            noiseSigma: 12,
            seed: 417);

        var result = SlitIlluminationPairAnalyzer.Analyze(
            pair.Off,
            pair.On,
            Seed);

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal(SlitIlluminationPolarity.Bright, result.Polarity);
        Assert.InRange(result.PerpendicularOffsetPixels, expectedOffset - 1.25, expectedOffset + 1.25);
        Assert.InRange(result.Geometry.AngleDegrees, expectedAngle - 0.75, expectedAngle + 0.75);
        Assert.InRange(result.MeasuredWidthPixels, expectedWidth - 1.5, expectedWidth + 1.5);
        Assert.True(result.Confidence > 0.55);
        Assert.Equal(result.MeasuredWidthPixels, result.Geometry.WidthPixels, 8);
        Assert.NotEqual(Seed.AcquisitionPoint, result.Geometry.AcquisitionPoint);
    }

    [Fact]
    public void DarkPairRemainsMeasurableWithNoiseAndIsolatedBadPixels()
    {
        const double expectedOffset = -9;
        const double expectedAngle = 85.5;
        var pair = CreatePair(
            [new SyntheticLine(expectedOffset, expectedAngle, 4, -650)],
            noiseSigma: 28,
            seed: 912,
            isolatedBadPixels: 80);

        var result = SlitIlluminationPairAnalyzer.Analyze(
            pair.Off,
            pair.On,
            Seed,
            TestOptions(maximumAngleSearchDegrees: 5));

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal(SlitIlluminationPolarity.Dark, result.Polarity);
        Assert.InRange(result.PerpendicularOffsetPixels, expectedOffset - 1.5, expectedOffset + 1.5);
        Assert.InRange(result.Geometry.AngleDegrees, expectedAngle - 1, expectedAngle + 1);
        Assert.True(result.BadPixelFraction > 0);
        Assert.True(result.ContrastSigma >= 5);
    }

    [Fact]
    public void LedWithOnlyAUniformLevelChangeIsIndeterminate()
    {
        var off = Enumerable.Repeat((ushort)1800, Width * Height).ToArray();
        var on = Enumerable.Repeat((ushort)1825, Width * Height).ToArray();

        var result = SlitIlluminationPairAnalyzer.Analyze(
            new MonochromeFrame(Width, Height, off, 65520),
            new MonochromeFrame(Width, Height, on, 65520),
            Seed,
            TestOptions());

        Assert.Equal(GateDisposition.Indeterminate, result.Gate.Disposition);
        Assert.Contains(result.Gate.Code, new[] { "SLIT_LED_PAIR_LOW_CONTRAST", "SLIT_LED_PAIR_NO_DIFFERENTIAL_SIGNAL" });
        Assert.True(result.ContrastSigma < 5);
    }

    [Fact]
    public void SaturatedIlluminatedSlitFailsClosed()
    {
        var pair = CreatePair(
            [new SyntheticLine(4, 89, 14, 70_000)],
            noiseSigma: 4,
            seed: 123,
            clipLineToSaturation: true);

        var result = SlitIlluminationPairAnalyzer.Analyze(
            pair.Off,
            pair.On,
            Seed,
            TestOptions(maximumSaturatedFraction: 0.01));

        Assert.Equal(GateDisposition.Failed, result.Gate.Disposition);
        Assert.Equal("SLIT_LED_PAIR_SATURATED", result.Gate.Code);
        Assert.Equal(SlitIlluminationPolarity.Unknown, result.Polarity);
        Assert.True(result.SaturatedFraction > 0.01);
        Assert.Equal(Seed, result.Geometry);
    }

    [Fact]
    public void TwoEquallyStrongParallelFeaturesAreGeometricallyAmbiguous()
    {
        var pair = CreatePair(
            [
                new SyntheticLine(-14, 89.5, 4.5, 700),
                new SyntheticLine(14, 89.5, 4.5, 700)
            ],
            noiseSigma: 8,
            seed: 773);

        var result = SlitIlluminationPairAnalyzer.Analyze(
            pair.Off,
            pair.On,
            Seed,
            TestOptions(maximumAngleSearchDegrees: 4, minimumUniquenessRatio: 1.20));

        Assert.Equal(GateDisposition.Indeterminate, result.Gate.Disposition);
        Assert.Equal("SLIT_LED_PAIR_GEOMETRY_AMBIGUOUS", result.Gate.Code);
        Assert.True(result.UniquenessRatio < 1.20);
    }

    [Fact]
    public void RecoveryEvidenceRegressionClustersProfileRefinedLociRatherThanRawTrialBands()
    {
        // Immutable 2026-08-18 recovery evidence exposed this exact edge case:
        // the second trial band was centered at -4 px, but its own measured
        // profile placed the physical line at +1.097 px. Comparing the raw
        // band center invented a distinct geometry just beyond the tolerance.
        const double bestRefinedCenter = 1.9556137994706686;
        const double bestAngleOffset = 0.5;
        const double secondRawTrialCenter = -4;
        const double secondRefinedCenter = 1.0974043867855303;
        const double secondAngleOffset = -1.5;
        const double seedLength = 460;
        const double clusterTolerance = 13.548268036435186;

        var rawTrialSeparation = SlitIlluminationPairAnalyzer.MaximumLineLocusSeparationPixels(
            bestRefinedCenter,
            bestAngleOffset,
            secondRawTrialCenter,
            secondAngleOffset,
            seedLength);
        var refinedLocusSeparation = SlitIlluminationPairAnalyzer.MaximumLineLocusSeparationPixels(
            bestRefinedCenter,
            bestAngleOffset,
            secondRefinedCenter,
            secondAngleOffset,
            seedLength);

        Assert.InRange(rawTrialSeparation, 13.98, 13.99);
        Assert.True(rawTrialSeparation > clusterTolerance);
        Assert.InRange(refinedLocusSeparation, 8.88, 8.90);
        Assert.True(refinedLocusSeparation <= clusterTolerance);
    }

    [Fact]
    public void DifferentFrameDimensionsAreRejectedBeforeAnalysis()
    {
        var off = new MonochromeFrame(20, 20, new ushort[400]);
        var on = new MonochromeFrame(21, 20, new ushort[420]);

        Assert.Throws<ArgumentException>(() => SlitIlluminationPairAnalyzer.Analyze(off, on, Seed));
    }

    [Fact]
    public void CoarseWidthGridRecoversWideSlitWithoutEncodingItsNominalLabel()
    {
        const double expectedWidth = 58;
        var pair = CreatePair(
            [new SyntheticLine(2, 89, expectedWidth, 900)],
            noiseSigma: 8,
            seed: 8441);
        var result = SlitIlluminationPairAnalyzer.Analyze(
            pair.Off,
            pair.On,
            Seed,
            new SlitIlluminationPairOptions(
                MaximumPerpendicularSearchPixels: 32,
                MaximumAngleSearchDegrees: 4,
                AngleStepDegrees: 0.5,
                MaximumMeasuredWidthPixels: 96,
                AlongSampleStepPixels: 1.5,
                MinimumAlongSamples: 45,
                MinimumContrastSigma: 5,
                MinimumUniquenessRatio: 1.10,
                MinimumValidFraction: 0.65,
                MaximumSaturatedFraction: 0.02,
                MaximumSidebandAsymmetry: 0.80,
                MinimumAlongSignalFraction: 0.50,
                MinimumMeasuredLengthPixels: 48,
                MinimumLengthToWidthRatio: 2,
                MaximumAlongGapPixels: 12,
                MeasuredWidthStepPixels: 4));

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.InRange(result.MeasuredWidthPixels, expectedWidth - 3, expectedWidth + 3);
        Assert.True(result.Geometry.LengthPixels / result.MeasuredWidthPixels > 2);
    }

    [Fact]
    public void WideScanIgnoresDistantSaturatedFeatureOutsideEveryPossibleSlitCore()
    {
        const ushort saturation = 65520;
        const double expectedWidth = 58;
        var pair = CreatePair(
            [new SyntheticLine(2, 89, expectedWidth, 900)],
            noiseSigma: 8,
            seed: 8442);
        var off = new ushort[Width * Height];
        var on = new ushort[Width * Height];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            off[y * Width + x] = pair.Off[x, y];
            on[y * Width + x] = pair.On[x, y];
            var dx = x - Seed.AcquisitionPoint.X;
            var dy = y - Seed.AcquisitionPoint.Y;
            var angle = Seed.AngleDegrees * Math.PI / 180;
            var along = Math.Cos(angle) * dx + Math.Sin(angle) * dy;
            var across = -Math.Sin(angle) * dx + Math.Cos(angle) * dy;
            if (Math.Abs(along) <= Seed.LengthPixels / 2 && across is >= 112 and <= 124)
                on[y * Width + x] = saturation;
        }

        var result = SlitIlluminationPairAnalyzer.Analyze(
            new MonochromeFrame(Width, Height, off, saturation),
            new MonochromeFrame(Width, Height, on, saturation),
            Seed,
            new SlitIlluminationPairOptions(
                MaximumPerpendicularSearchPixels: 32,
                MaximumAngleSearchDegrees: 4,
                AngleStepDegrees: 0.5,
                MaximumMeasuredWidthPixels: 96,
                AlongSampleStepPixels: 1.5,
                MinimumAlongSamples: 45,
                MinimumContrastSigma: 5,
                MinimumUniquenessRatio: 1.10,
                MinimumValidFraction: 0.65,
                MaximumSaturatedFraction: 0.02,
                MaximumSidebandAsymmetry: 0.80,
                MinimumAlongSignalFraction: 0.50,
                MinimumMeasuredLengthPixels: 48,
                MinimumLengthToWidthRatio: 2,
                MaximumAlongGapPixels: 12,
                MeasuredWidthStepPixels: 4));

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.InRange(result.MeasuredWidthPixels, expectedWidth - 3, expectedWidth + 3);
        Assert.True(result.SaturatedFraction < 0.02);
    }

    private static SlitIlluminationPairOptions TestOptions(
        double maximumAngleSearchDegrees = 6,
        double minimumUniquenessRatio = 1.10,
        double maximumSaturatedFraction = 0.02) =>
        new(
            MaximumPerpendicularSearchPixels: 32,
            MaximumAngleSearchDegrees: maximumAngleSearchDegrees,
            AngleStepDegrees: 0.5,
            MaximumMeasuredWidthPixels: 16,
            AlongSampleStepPixels: 1.5,
            MinimumAlongSamples: 45,
            MinimumContrastSigma: 5,
            MinimumUniquenessRatio: minimumUniquenessRatio,
            MinimumValidFraction: 0.65,
            MaximumSaturatedFraction: maximumSaturatedFraction,
            MaximumSidebandAsymmetry: 0.80);

    private static FramePair CreatePair(
        IReadOnlyList<SyntheticLine> lines,
        double noiseSigma,
        int seed,
        int isolatedBadPixels = 0,
        bool clipLineToSaturation = false)
    {
        const ushort saturation = 65520;
        var random = new Random(seed);
        var off = new ushort[Width * Height];
        var on = new ushort[Width * Height];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var baseLevel = 2200 + 0.17 * x - 0.11 * y;
            var offValue = baseLevel + NextGaussian(random) * noiseSigma;
            var onValue = baseLevel + 19 + NextGaussian(random) * noiseSigma;
            foreach (var line in lines)
            {
                var angle = line.AngleDegrees * Math.PI / 180;
                var perpendicularX = -Math.Sin(angle);
                var perpendicularY = Math.Cos(angle);
                var alongX = Math.Cos(angle);
                var alongY = Math.Sin(angle);
                var centerX = Seed.AcquisitionPoint.X + perpendicularX * line.OffsetPixels;
                var centerY = Seed.AcquisitionPoint.Y + perpendicularY * line.OffsetPixels;
                var dx = x - centerX;
                var dy = y - centerY;
                var across = perpendicularX * dx + perpendicularY * dy;
                var along = alongX * dx + alongY * dy;
                if (Math.Abs(along) > Seed.LengthPixels / 2) continue;
                var sigma = line.FwhmPixels / 2.354820045;
                onValue += line.AmplitudeAdu * Math.Exp(-0.5 * across * across / (sigma * sigma));
            }
            off[y * Width + x] = ToUshort(offValue, saturation, false);
            on[y * Width + x] = ToUshort(onValue, saturation, clipLineToSaturation);
        }

        for (var index = 0; index < isolatedBadPixels; index++)
        {
            var x = random.Next(3, Width - 3);
            var y = random.Next(3, Height - 3);
            var delta = index % 2 == 0 ? 18_000 : -1_800;
            on[y * Width + x] = ToUshort(on[y * Width + x] + delta, saturation, false);
        }

        return new FramePair(
            new MonochromeFrame(Width, Height, off, saturation),
            new MonochromeFrame(Width, Height, on, saturation));
    }

    private static ushort ToUshort(double value, ushort saturation, bool clipToSaturation)
    {
        if (clipToSaturation && value >= saturation) return saturation;
        return (ushort)Math.Clamp(Math.Round(value), 0, saturation);
    }

    private static double NextGaussian(Random random)
    {
        var u1 = Math.Max(double.Epsilon, random.NextDouble());
        var u2 = random.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    private sealed record SyntheticLine(double OffsetPixels, double AngleDegrees, double FwhmPixels, double AmplitudeAdu);
    private sealed record FramePair(MonochromeFrame Off, MonochromeFrame On);
}
