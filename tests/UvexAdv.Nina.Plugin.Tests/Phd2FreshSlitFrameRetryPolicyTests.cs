using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2FreshSlitFrameRetryPolicyTests
{
    [Fact]
    public void LowConfidenceSlitFramesReceiveExactlyTwoAdditionalFreshCaptures()
    {
        var gate = GateResult.Unknown(
            "SLIT_LOCUS_LOW_CONFIDENCE",
            "Best dark-line contrast is only 2.93 sigma.");

        Assert.Equal(5, Phd2FreshSlitFrameRetryPolicy.MaximumCaptureAttempts(3));
        Assert.True(Phd2FreshSlitFrameRetryPolicy.CanRetry(gate, captureAttempts: 2, acceptedFreshResiduals: 1, requiredFreshResiduals: 3));
        Assert.True(Phd2FreshSlitFrameRetryPolicy.CanRetry(gate, captureAttempts: 4, acceptedFreshResiduals: 2, requiredFreshResiduals: 3));
        Assert.False(Phd2FreshSlitFrameRetryPolicy.CanRetry(gate, captureAttempts: 4, acceptedFreshResiduals: 1, requiredFreshResiduals: 3));
        Assert.False(Phd2FreshSlitFrameRetryPolicy.CanRetry(gate, captureAttempts: 5, acceptedFreshResiduals: 2, requiredFreshResiduals: 3));
    }

    [Fact]
    public void OneRequiredResidualStillHasABoundedThreeCaptureEnvelope()
    {
        var gate = GateResult.Unknown("SLIT_LOCUS_LOW_CONFIDENCE", "transient");

        Assert.Equal(3, Phd2FreshSlitFrameRetryPolicy.MaximumCaptureAttempts(1));
        Assert.True(Phd2FreshSlitFrameRetryPolicy.CanRetry(gate, captureAttempts: 2, acceptedFreshResiduals: 0, requiredFreshResiduals: 1));
        Assert.False(Phd2FreshSlitFrameRetryPolicy.CanRetry(gate, captureAttempts: 3, acceptedFreshResiduals: 0, requiredFreshResiduals: 1));
    }

    [Theory]
    [InlineData("SLIT_LED_IDENTITY_GEOMETRY_UNAVAILABLE")]
    [InlineData("G3_FRAME_TOPOLOGY_MISMATCH")]
    [InlineData("TARGET_IDENTITY_UNCONFIRMED")]
    public void NonTransientEvidenceFailuresNeverUseTheSlitFrameRetryAllowance(string code)
    {
        var gate = GateResult.Unknown(code, "not a transient slit-contrast miss");

        Assert.False(Phd2FreshSlitFrameRetryPolicy.CanRetry(gate, captureAttempts: 1, acceptedFreshResiduals: 0, requiredFreshResiduals: 3));
    }
}
