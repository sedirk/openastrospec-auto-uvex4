using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class SlitIlluminationPositioningSpotRegressionTests
{
    [Fact]
    public void MeasuredSixHundredPixelSlitPassesInsideAnOversizedThousandPixelSeed()
    {
        const int width = 1200;
        const int height = 240;
        const ushort saturation = 65520;
        const double actualAngleDegrees = -1.75;
        const double actualLengthPixels = 600;
        const double actualWidthPixels = 7;
        const double alongMidpointPixels = -100;
        const double acrossOffsetPixels = 14;
        var seed = new SlitGeometry(
            "oversized-historical-seed",
            new PixelPoint(width / 2d, height / 2d),
            0,
            1000,
            5,
            12,
            "G3-test",
            1,
            1);
        var off = new ushort[width * height];
        var on = new ushort[width * height];
        var random = new Random(41903);
        var angle = actualAngleDegrees * Math.PI / 180;
        var alongX = Math.Cos(angle);
        var alongY = Math.Sin(angle);
        var acrossX = -alongY;
        var acrossY = alongX;
        var centerX = seed.AcquisitionPoint.X + alongX * alongMidpointPixels + acrossX * acrossOffsetPixels;
        var centerY = seed.AcquisitionPoint.Y + alongY * alongMidpointPixels + acrossY * acrossOffsetPixels;
        var sigma = actualWidthPixels / 2.354820045;

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var baseline = 2300 + 0.05 * x - 0.08 * y;
            var offNoise = (random.NextDouble() - 0.5) * 20;
            var onNoise = (random.NextDouble() - 0.5) * 20;
            var dx = x - centerX;
            var dy = y - centerY;
            var along = alongX * dx + alongY * dy;
            var across = acrossX * dx + acrossY * dy;
            var line = Math.Abs(along) <= actualLengthPixels / 2
                ? 1100 * Math.Exp(-0.5 * across * across / (sigma * sigma))
                : 0;
            off[y * width + x] = (ushort)Math.Clamp(Math.Round(baseline + offNoise), 0, saturation - 1);
            on[y * width + x] = (ushort)Math.Clamp(Math.Round(baseline + 18 + onNoise + line), 0, saturation - 1);
        }

        var result = SlitIlluminationPairAnalyzer.Analyze(
            new MonochromeFrame(width, height, off, saturation),
            new MonochromeFrame(width, height, on, saturation),
            seed);

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal("SLIT_LED_PAIR_GEOMETRY_MEASURED", result.Gate.Code);
        Assert.InRange(result.Geometry.LengthPixels, 570, 630);
        Assert.InRange(result.AlongSpanFraction, 0.57, 0.63);
        Assert.True(result.AlongSignalFraction > 0.85);
        Assert.InRange(result.AlongStartOffsetPixels, -420, -380);
        Assert.InRange(result.AlongEndOffsetPixels, 180, 220);
        Assert.InRange(result.Geometry.AcquisitionPoint.X, 485, 515);
    }

    [Fact]
    public void CompactPositioningSpotCannotAuthorizeAThroughSlitGeometry()
    {
        const int width = 240;
        const int height = 190;
        const ushort saturation = 65520;
        var seed = new SlitGeometry(
            "historical-seed-only",
            new PixelPoint(width / 2d, height / 2d),
            90,
            140,
            4,
            8,
            "G3-test",
            1,
            1);
        var off = new ushort[width * height];
        var on = new ushort[width * height];
        var random = new Random(88021);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var baseline = 2100 + 0.08 * x - 0.04 * y;
            var noise = (random.NextDouble() - 0.5) * 12;
            off[y * width + x] = (ushort)Math.Clamp(Math.Round(baseline + noise), 0, saturation - 1);

            // An intentionally very bright but compact locator spot near the
            // historical seed. It does not traverse the commissioned slit
            // length and therefore must never become mount-motion authority.
            var dx = x - (seed.AcquisitionPoint.X + 7);
            var dy = y - (seed.AcquisitionPoint.Y - 11);
            var spot = 45_000 * Math.Exp(-(dx * dx + dy * dy) / (2 * 3.2 * 3.2));
            on[y * width + x] = (ushort)Math.Clamp(
                Math.Round(baseline + 14 + noise + spot),
                0,
                saturation - 1);
        }

        var result = SlitIlluminationPairAnalyzer.Analyze(
            new MonochromeFrame(width, height, off, saturation),
            new MonochromeFrame(width, height, on, saturation),
            seed,
            new SlitIlluminationPairOptions(
                MaximumPerpendicularSearchPixels: 32,
                MaximumAngleSearchDegrees: 5,
                AngleStepDegrees: 0.5,
                MaximumMeasuredWidthPixels: 16,
                AlongSampleStepPixels: 1.5,
                MinimumAlongSamples: 40,
                MinimumContrastSigma: 5,
                MinimumUniquenessRatio: 1.15,
                MinimumValidFraction: 0.70,
                MaximumSaturatedFraction: 0.02,
                MaximumSidebandAsymmetry: 0.75));

        Assert.NotEqual(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal("SLIT_LED_PAIR_NOT_THROUGHGOING", result.Gate.Code);
        Assert.True(result.AlongSignalFraction < 0.50 || result.AlongSpanFraction < 0.75);
        Assert.True(result.Geometry.LengthPixels < 48);
    }
}
