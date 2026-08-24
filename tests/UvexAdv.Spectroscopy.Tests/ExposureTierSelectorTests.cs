using UvexAdv.Spectroscopy;
using Xunit;

namespace UvexAdv.Spectroscopy.Tests;

public sealed class ExposureTierSelectorTests
{
    [Fact]
    public void UsesSpectralRoiToSelectNearestSafeTier()
    {
        var probe = new SpectralProbeMetrics(10, 256, 10000, 65535, 0, 12, 20, 2, true, "probe");

        var decision = ExposureTierSelector.Select(probe);

        Assert.True(decision.Accepted);
        Assert.Equal(30, decision.SelectedExposureSeconds);
        Assert.InRange(decision.PredictedFullScaleFraction, 0.44, 0.46);
    }

    [Fact]
    public void SaturationForcesShorterTier()
    {
        var probe = new SpectralProbeMetrics(10, 256, 64000, 65535, 0.01, 20, 30, 3, true, "probe");

        var decision = ExposureTierSelector.Select(probe);

        Assert.True(decision.Accepted);
        Assert.True(decision.SelectedExposureSeconds < probe.ExposureSeconds);
        Assert.Equal("SATURATION_BACKOFF", decision.Code);
    }

    [Fact]
    public void NarrowClippedTraceCannotHideBehindLowWholeFrameFraction()
    {
        var probe = new SpectralProbeMetrics(
            10,
            256,
            65520,
            65535,
            0.0004,
            20,
            30,
            3,
            true,
            "probe",
            TraceSaturatedFraction: 0.04,
            ClippedDispersionColumnFraction: 0.42,
            LongestClippedDispersionColumnRun: 900,
            TraceSpatialCenterPixel: 760,
            TraceSpatialHalfWidthPixels: 4);

        var decision = ExposureTierSelector.Select(probe);

        Assert.True(decision.Accepted);
        Assert.Equal("SATURATION_BACKOFF", decision.Code);
        Assert.True(decision.SelectedExposureSeconds < probe.ExposureSeconds);
        Assert.Equal(0.42, decision.Metrics["clippedDispersionColumnFraction"]);
    }

    [Fact]
    public void LowContrastDoesNotInventAUsableExposure()
    {
        var probe = new SpectralProbeMetrics(30, 256, 2000, 65535, 0, 2, 3, 1.02, true, "probe");

        var decision = ExposureTierSelector.Select(probe);

        Assert.False(decision.Accepted);
        Assert.Equal("TARGET_SKY_CONTRAST_LOW", decision.Code);
    }

    [Fact]
    public void SaturatedProbeShorterThanShortestTierIsRejected()
    {
        var probe = new SpectralProbeMetrics(0.01, 256, 65000, 65535, 0.02, 20, 30, 3, true, "probe");
        var options = new ExposureTierOptions([0.1, 0.3, 1]);

        var decision = ExposureTierSelector.Select(probe, options);

        Assert.False(decision.Accepted);
        Assert.Equal("NO_SAFE_SATURATION_BACKOFF", decision.Code);
    }

    [Theory]
    [InlineData(double.NaN, 10, 2, "PROBE_SATURATION_INVALID")]
    [InlineData(0, double.PositiveInfinity, 2, "PROBE_SNR_INVALID")]
    [InlineData(0, 10, double.NaN, "PROBE_CONTRAST_INVALID")]
    public void NonFiniteQualityMetricsAreRejected(double saturatedFraction, double snr, double contrast, string code)
    {
        var probe = new SpectralProbeMetrics(10, 256, 10000, 65535, saturatedFraction, snr, 20, contrast, true, "probe");

        var decision = ExposureTierSelector.Select(probe);

        Assert.False(decision.Accepted);
        Assert.Equal(code, decision.Code);
    }
}
