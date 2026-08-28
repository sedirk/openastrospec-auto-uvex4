using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class SlitDarkApertureHdrAnalysisTests
{
    private static readonly SlitGeometry Seed = new(
        "synthetic-reflection-seed",
        new PixelPoint(100, 90),
        0,
        140,
        6,
        0.25,
        "G3M2210M",
        1,
        1);

    [Fact]
    public void SaturatedReflectiveEdgeDoesNotHidePhysicalDarkAperture()
    {
        var shortPair = Pair(8, 1_500, 180, 1);
        var longPair = Pair(8, 6_000, 720, 2);

        var result = SlitDarkApertureHdrAnalyzer.Analyze(
            shortPair.Off,
            shortPair.On,
            longPair.Off,
            longPair.On,
            Seed,
            Options(sharedPsf: true));

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.NotEqual(SlitDarkApertureResolution.Unresolved, result.Resolution);
        Assert.InRange(result.ApertureWidthPixels, 7.25, 8.75);
        Assert.InRange(result.Geometry.AcquisitionPoint.Y, 93.25, 94.75);
        Assert.True(result.LongExposureSaturatedFraction > 0);
        Assert.Contains("physical dark aperture", result.Gate.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompletelyClippedLongExposureDoesNotVetoDirectShortExposureTwoEdgeMeasurement()
    {
        var shortPair = Pair(8, 1_500, 180, 1);
        var pixels = Enumerable.Repeat((ushort)4_095, 200 * 180).ToArray();
        var longPair = (
            Off: new MonochromeFrame(200, 180, pixels, 4_095),
            On: new MonochromeFrame(200, 180, pixels, 4_095));

        var result = SlitDarkApertureHdrAnalyzer.Analyze(
            shortPair.Off,
            shortPair.On,
            longPair.Off,
            longPair.On,
            Seed,
            Options(sharedPsf: true));

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal("SLIT_DARK_APERTURE_SHORT_EXPOSURE_DIRECTLY_MEASURED", result.Gate.Code);
        Assert.Equal(SlitDarkApertureResolution.DirectTwoEdge, result.Resolution);
        Assert.InRange(result.ApertureWidthPixels, 7.25, 8.75);
        Assert.Contains("short frame independently resolved both edges", result.Gate.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompletelyClippedLongExposureStillRejectsAShortSingleReflectiveRidge()
    {
        var shortPair = Pair(8, 1_500, 0, 1);
        var pixels = Enumerable.Repeat((ushort)4_095, 200 * 180).ToArray();
        var longPair = (
            Off: new MonochromeFrame(200, 180, pixels, 4_095),
            On: new MonochromeFrame(200, 180, pixels, 4_095));

        var result = SlitDarkApertureHdrAnalyzer.Analyze(
            shortPair.Off,
            shortPair.On,
            longPair.Off,
            longPair.On,
            Seed,
            Options(sharedPsf: true));

        Assert.Equal("SLIT_DARK_APERTURE_SECOND_EDGE_NOT_FOUND", result.Gate.Code);
        Assert.NotEqual(GateDisposition.Passed, result.Gate.Disposition);
    }

    [Fact]
    public void SingleReflectiveRidgeCannotMasqueradeAsSlitWidth()
    {
        var shortPair = Pair(8, 1_500, 0, 1);
        var longPair = Pair(8, 4_000, 0, 2);

        var result = SlitDarkApertureHdrAnalyzer.Analyze(
            shortPair.Off,
            shortPair.On,
            longPair.Off,
            longPair.On,
            Seed,
            Options(sharedPsf: true));

        Assert.True(
            result.Gate.Disposition != GateDisposition.Passed,
            $"{result.Gate.Code}: width={result.ApertureWidthPixels:F3}, ratio={result.SecondaryEdgeAmplitudeRatio:F3}, dBIC={result.DeltaBic:F3}");
        Assert.Equal("SLIT_DARK_APERTURE_SECOND_EDGE_NOT_FOUND", result.Gate.Code);
        Assert.Equal(0, result.ApertureWidthPixels);
    }

    [Fact]
    public void ExplicitCommissionedNarrowTransferRegistersFreshRidgeWithoutInventingFreshWidth()
    {
        var shortPair = Pair(8, 1_500, 0, 1);
        var longPair = Pair(8, 4_000, 0, 2);
        var options = Options(sharedPsf: true) with
        {
            ExpectedReflectiveEdgeToApertureCenterPixels = 1.5,
            AllowCommissionedMidpointTransfer = true,
            CommissionedApertureWidthPixels = 3.5,
            CommissionedWidthUncertaintyPixels = 0.5,
        };

        var result = SlitDarkApertureHdrAnalyzer.Analyze(
            shortPair.Off,
            shortPair.On,
            longPair.Off,
            longPair.On,
            Seed,
            options);

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal("SLIT_DARK_APERTURE_COMMISSIONED_TRANSFER_REGISTERED", result.Gate.Code);
        Assert.Equal(SlitDarkApertureResolution.CommissionedMidpointTransfer, result.Resolution);
        Assert.Equal(3.5, result.ApertureWidthPixels, precision: 12);
        Assert.Equal(0.5, result.WidthUncertaintyPixels, precision: 12);
        Assert.Equal(1.5, result.ReflectiveEdgeToApertureCenterPixels, precision: 12);
        Assert.Equal(Seed.AcquisitionPoint.X, result.Geometry.AcquisitionPoint.X, precision: 9);
        Assert.Equal(1, result.Gate.Metrics!["freshReflectiveRegistrationPassed"]);
        Assert.Equal(1, result.Gate.Metrics!["commissionedMidpointTransferApplied"]);
        Assert.Equal(1, result.Gate.Metrics!["commissionedAlongSlitAnchorPreserved"]);
        Assert.Contains("does not claim a fresh two-edge", result.Gate.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommissionedTransferIsRejectedWhenLongFrameHasNoDynamicRange()
    {
        var shortPair = Pair(8, 1_500, 0, 1);
        var pixels = Enumerable.Repeat((ushort)4_095, 200 * 180).ToArray();
        var clipped = new MonochromeFrame(200, 180, pixels, 4_095);
        var options = Options(sharedPsf: true) with
        {
            ExpectedReflectiveEdgeToApertureCenterPixels = 1.5,
            AllowCommissionedMidpointTransfer = true,
            CommissionedApertureWidthPixels = 3.5,
            CommissionedWidthUncertaintyPixels = 0.5,
        };

        var result = SlitDarkApertureHdrAnalyzer.Analyze(
            shortPair.Off,
            shortPair.On,
            clipped,
            clipped,
            Seed,
            options);

        Assert.NotEqual(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal("SLIT_DARK_APERTURE_SECOND_EDGE_NOT_FOUND", result.Gate.Code);
    }

    [Fact]
    public void BlendedNarrowApertureNeedsExplicitSharedPsfAuthority()
    {
        var shortPair = Pair(3.5, 1_500, 150, 1);
        var longPair = Pair(3.5, 5_000, 500, 2);
        var strictBic = Options(sharedPsf: false) with { MinimumTwoEdgeDeltaBic = 1_000_000 };

        var rejected = SlitDarkApertureHdrAnalyzer.Analyze(
            shortPair.Off, shortPair.On, longPair.Off, longPair.On, Seed, strictBic);
        var commissioned = SlitDarkApertureHdrAnalyzer.Analyze(
            shortPair.Off,
            shortPair.On,
            longPair.Off,
            longPair.On,
            Seed,
            strictBic with { SharedPsfIsCommissioned = true });

        Assert.Equal("SLIT_DARK_APERTURE_MODEL_NOT_COMMISSIONED", rejected.Gate.Code);
        Assert.Equal(GateDisposition.Passed, commissioned.Gate.Disposition);
        Assert.Equal(SlitDarkApertureResolution.SharedPsfModel, commissioned.Resolution);
        Assert.InRange(commissioned.ApertureWidthPixels, 2.75, 4.25);
    }

    [Fact]
    public void CommissionedNarrowApertureDoesNotTreatLedGradientAsRandomNoise()
    {
        var shortPair = Pair(3.5, 1_500, 150, 1, ledGradientPerPixel: 2.0);
        var longPair = Pair(3.5, 5_000, 500, 2, ledGradientPerPixel: 4.0);

        var result = SlitDarkApertureHdrAnalyzer.Analyze(
            shortPair.Off,
            shortPair.On,
            longPair.Off,
            longPair.On,
            Seed,
            Options(sharedPsf: true) with { MinimumProfileSignalToNoise = 3.5 });

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal(SlitDarkApertureResolution.SharedPsfModel, result.Resolution);
        Assert.InRange(result.ApertureWidthPixels, 2.75, 4.25);
        Assert.True(result.Gate.Metrics!["fitSignalToNoise"] >= 3.5);
    }

    [Fact]
    public void CommissionedDirectionRejectsReflectionShoulderOnOppositeSide()
    {
        var shortPair = Pair(3.5, 1_500, 150, 1, secondaryDirection: -1);
        var longPair = Pair(3.5, 5_000, 500, 2, secondaryDirection: -1);

        var result = SlitDarkApertureHdrAnalyzer.Analyze(
            shortPair.Off,
            shortPair.On,
            longPair.Off,
            longPair.On,
            Seed,
            Options(sharedPsf: true) with
            {
                ExpectedReflectiveEdgeToApertureCenterPixels = 1.5,
            });

        Assert.NotEqual(GateDisposition.Passed, result.Gate.Disposition);
    }

    [Fact]
    public void SharedPsfMeasurementUsesCommissionedOffsetFromFreshReflectiveEdge()
    {
        var shortPair = Pair(3.5, 1_500, 150, 1);
        var longPair = Pair(3.5, 5_000, 500, 2);
        var options = Options(sharedPsf: true) with
        {
            MinimumTwoEdgeDeltaBic = 1_000_000,
            ExpectedReflectiveEdgeToApertureCenterPixels = 4,
        };

        var result = SlitDarkApertureHdrAnalyzer.Analyze(
            shortPair.Off,
            shortPair.On,
            longPair.Off,
            longPair.On,
            Seed,
            options);

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal(SlitDarkApertureResolution.SharedPsfModel, result.Resolution);
        Assert.Equal(4, result.ReflectiveEdgeToApertureCenterPixels, precision: 12);
        Assert.InRange(result.Geometry.AcquisitionPoint.Y, 93.99, 94.01);
        Assert.Equal(1, result.Gate.Metrics!["commissionedMidpointTransferApplied"]);
    }

    private static SlitDarkApertureHdrOptions Options(bool sharedPsf) => new(
        MaximumPerpendicularSearchPixels: 40,
        MaximumAngleSearchDegrees: 2,
        MinimumApertureWidthPixels: 1.5,
        MaximumApertureWidthPixels: 20,
        EdgePsfAlphaPixels: 0.625,
        EdgePsfBeta: 0.43,
        ProfileStepPixels: 0.25,
        MinimumSecondaryEdgeAmplitudeRatio: 0.03,
        MaximumSecondaryEdgeAmplitudeRatio: 0.8,
        MinimumTwoEdgeDeltaBic: 8,
        MinimumLongExposureValidFraction: 0.1,
        MinimumLongExposureDynamicRangeAdu: 10,
        MinimumProfileSignalToNoise: 2,
        SharedPsfIsCommissioned: sharedPsf);

    private static (MonochromeFrame Off, MonochromeFrame On) Pair(
        double separation,
        double primaryAmplitude,
        double secondaryAmplitude,
        double exposureScale,
        double ledGradientPerPixel = 0,
        int secondaryDirection = 1)
    {
        const int width = 200;
        const int height = 180;
        const ushort saturation = 4_095;
        var off = new ushort[width * height];
        var on = new ushort[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var baseline = 120 + 0.15 * y;
            off[y * width + x] = (ushort)Math.Round(baseline);
            var within = x is >= 25 and <= 175;
            var signal = within
                ? primaryAmplitude * Moffat(y - 90) + secondaryAmplitude * Moffat(y - (90 + secondaryDirection * separation))
                : 0;
            var raw = baseline + exposureScale * 3 + ledGradientPerPixel * y + signal;
            on[y * width + x] = (ushort)Math.Clamp(Math.Round(raw), 0, saturation);
        }
        return (
            new MonochromeFrame(width, height, off, saturation),
            new MonochromeFrame(width, height, on, saturation));
    }

    private static double Moffat(double x) => Math.Pow(1 + Math.Pow(x / 0.625, 2), -0.43);
}
