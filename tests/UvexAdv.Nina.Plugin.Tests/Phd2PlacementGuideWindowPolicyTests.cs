using Xunit;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2PlacementGuideWindowPolicyTests
{
    private static SlitGeometry Slit(double angle = 0, double length = 410) =>
        new("run-led", new PixelPoint(817.595607783773, 430.3780275505807), angle, length, 3, 0.5, "guide", 1, 1);

    [Fact]
    public void ActualAlongSlitResidualCanAuthorizeOnlySupervisedProbe()
    {
        var sample = Phd2PlacementGuideWindowPolicy.ProjectOnMeasuredSlit(
            new PixelPoint(826.193186072632, 430.98165481093224), Slit(-2));
        Assert.NotNull(sample);
        Assert.InRange(sample.MidpointPixels, 8.61, 8.63);
        Assert.InRange(sample.CrossSlitPixels, 0.90, 0.91);
        Phd2SlitApertureResidual?[] samples = [sample, sample, sample];
        Assert.True(Phd2PlacementGuideWindowPolicy.CanProbeAlongSlitWithPrecisionWarning(true, true, samples, 2, 2, 75));
        Assert.False(Phd2PlacementGuideWindowPolicy.CanProbeAlongSlitWithPrecisionWarning(false, true, samples, 2, 2, 75));
        Assert.False(Phd2PlacementGuideWindowPolicy.CanProbeAlongSlitWithPrecisionWarning(true, false, samples, 2, 2, 75));
        Assert.False(Phd2PlacementGuideWindowPolicy.AllWithinTolerance([sample.MidpointPixels], 2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(-32)]
    public void SlitProjectionFollowsMeasuredAngle(double angle)
    {
        var slit = Slit(angle);
        var a = angle * Math.PI / 180;
        var sample = Phd2PlacementGuideWindowPolicy.ProjectOnMeasuredSlit(new PixelPoint(
            slit.AcquisitionPoint.X + 10 * Math.Cos(a) - Math.Sin(a),
            slit.AcquisitionPoint.Y + 10 * Math.Sin(a) + Math.Cos(a)), slit)!;
        Assert.Equal(10, sample.AlongSlitPixels, 8);
        Assert.Equal(1, sample.CrossSlitPixels, 8);
    }

    [Theory]
    [InlineData(10, 6, 410, 0.5)] // across the slit, not an along-slit displacement
    [InlineData(10, 1, 20, 0.5)] // outside the finite measured slit
    [InlineData(76, 1, 410, 0.5)] // original radial acquisition envelope
    [InlineData(10, 1, 410, 4)] // geometry uncertainty cannot be ignored
    public void RejectsUnsafeOrUncertainApertureProjection(double along, double across, double length, double uncertainty)
    {
        var slit = Slit(length: length) with { UncertaintyPixels = uncertainty };
        var sample = Phd2PlacementGuideWindowPolicy.ProjectOnMeasuredSlit(
            new(slit.AcquisitionPoint.X + along, slit.AcquisitionPoint.Y + across), slit);
        Assert.False(Phd2PlacementGuideWindowPolicy.CanProbeAlongSlitWithPrecisionWarning(true, true, [sample, sample, sample], 2, 2, 75));
    }

    [Fact]
    public void EveryFreshSampleAndFiniteGeometryAreRequired()
    {
        var slit = Slit();
        var good = Phd2PlacementGuideWindowPolicy.ProjectOnMeasuredSlit(
            new(slit.AcquisitionPoint.X + 10, slit.AcquisitionPoint.Y + 1), slit)!;
        Assert.Null(Phd2PlacementGuideWindowPolicy.ProjectOnMeasuredSlit(new(double.NaN, 3), slit));
        Assert.Null(Phd2PlacementGuideWindowPolicy.ProjectOnMeasuredSlit(new(3, 4), slit with { AngleDegrees = double.NaN }));
        Assert.False(Phd2PlacementGuideWindowPolicy.CanProbeAlongSlitWithPrecisionWarning(true, true, [good, good], 2, 2, 75));
        Assert.False(Phd2PlacementGuideWindowPolicy.CanProbeAlongSlitWithPrecisionWarning(true, true, [good, good, null], 2, 2, 75));
        Assert.False(Phd2PlacementGuideWindowPolicy.CanProbeAlongSlitWithPrecisionWarning(true, true,
            [good, good, good with { CrossSlitPixels = 5 }], 2, 2, 75));
    }

    [Fact]
    public void ProbeConsentAllowsNearSlitWindSamplesWithoutClaimingPrecision()
    {
        double[] residuals = [4.6, 3.0, 1.1];
        Assert.False(Phd2PlacementGuideWindowPolicy.AllWithinTolerance(residuals, 2));
        Assert.True(Phd2PlacementGuideWindowPolicy.CanProbeWithPrecisionWarning(true, true, residuals, 2, 2, 75));
        Assert.False(Phd2PlacementGuideWindowPolicy.CanProbeWithPrecisionWarning(false, true, residuals, 2, 2, 75));
        Assert.False(Phd2PlacementGuideWindowPolicy.CanProbeWithPrecisionWarning(true, false, residuals, 2, 2, 75));
        Assert.False(Phd2PlacementGuideWindowPolicy.CanProbeWithPrecisionWarning(true, true, [20, 21, 22], 2, 2, 75));
        Assert.False(Phd2PlacementGuideWindowPolicy.CanProbeWithPrecisionWarning(true, true, [1, 1.5, 80], 2, 2, 75));
        Assert.False(Phd2PlacementGuideWindowPolicy.CanProbeWithPrecisionWarning(true, true, [1, 1.5], 2, 2, 75));
        Assert.False(Phd2PlacementGuideWindowPolicy.CanProbeWithPrecisionWarning(true, true, [1, double.NaN, 2], 2, 2, 75));
    }

    [Theory]
    [InlineData("FRESH_G3_RESIDUAL_REQUIRED", true)]
    [InlineData("SLIT_LOCK_RETURN_ATTEMPT_RESERVE", true)]
    [InlineData("SLIT_LOCK_RETURN_TIME_RESERVE", true)]
    [InlineData("G3_FRAME_REUSED", false)]
    [InlineData("G3_TARGET_IDENTITY_INVALID", false)]
    [InlineData("PHD2_LOCK_LEDGER_BINDING_CHANGED", false)]
    [InlineData("SLIT_LOCK_SAFETY_BLOCKED", false)]
    public void ReadOnlyWindWaitDoesNotReclassifySafetyOrIdentityFailures(string code, bool allowed)
    {
        Assert.Equal(allowed, Phd2PlacementGuideWindowPolicy.CanWaitWithoutNewMotion(false, code));
    }

    [Fact]
    public void RequiresEveryNewFrameRatherThanCherryPickingGoodLastFrame()
    {
        Assert.False(Phd2PlacementGuideWindowPolicy.AllWithinTolerance([0.80, 2.40, 2.41], 2));
        Assert.False(Phd2PlacementGuideWindowPolicy.AllWithinTolerance([2.41, 0.80, 0.70], 2));
        Assert.True(Phd2PlacementGuideWindowPolicy.AllWithinTolerance([0.80, 1.99, 0.70], 2));
        Assert.False(Phd2PlacementGuideWindowPolicy.AllWithinTolerance([], 2));
        Assert.False(Phd2PlacementGuideWindowPolicy.AllWithinTolerance([double.NaN], 2));
    }
}
