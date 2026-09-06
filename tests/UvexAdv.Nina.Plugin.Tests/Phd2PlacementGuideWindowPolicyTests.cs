using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2PlacementGuideWindowPolicyTests
{
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
