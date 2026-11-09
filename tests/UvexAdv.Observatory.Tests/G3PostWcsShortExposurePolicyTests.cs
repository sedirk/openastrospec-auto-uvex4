using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3PostWcsShortExposurePolicyTests
{
    private static G3ShortPositionConfirmationDecision Unconfirmed(string code, bool retry = true) =>
        new(GateResult.Unknown(code, "Recorded short-frame confirmation outcome"), null, false, retry, false, .5);

    [Fact]
    public void Hd21455DisagreeingMeasuredPairUsesLastAdjustmentWhileTwoFreshFramesRemain()
    {
        var p = G3PostWcsShortExposurePolicy.Start(10);
        p = p.AfterConfirmation("G3_SEP_POSITION_MEASURED", Unconfirmed("G3_SHORT_REPEAT_REQUIRED"), 1);
        Assert.Equal(10, p.ExposureMilliseconds);
        p = p.AfterConfirmation("G3_SEP_UNMEASURED", Unconfirmed("G3_SEP_UNMEASURED"), 2);
        Assert.Equal(30, p.ExposureMilliseconds);
        p = p.AfterConfirmation("G3_SEP_POSITION_MEASURED", Unconfirmed("G3_SHORT_REPEAT_REQUIRED"), 3);
        Assert.Equal(30, p.ExposureMilliseconds);
        p = p.AfterConfirmation("G3_SEP_POSITION_MEASURED", Unconfirmed("G3_SHORT_REPEAT_DISAGREES"), 4);
        Assert.Equal(30, p.ExposureMilliseconds);
        Assert.Equal(1, p.Adjustments);
        Assert.Equal(p, p.AfterConfirmation("G3_SEP_POSITION_MEASURED", Unconfirmed("G3_SEP_PARENT_BLEND_UNRESOLVED"), 5));
        Assert.Equal(p, p.AfterConfirmation("G3_SEP_AMBIGUOUS", Unconfirmed("G3_SEP_AMBIGUOUS", false), 6));
        Assert.Equal(6, G3PostWcsShortExposurePolicy.MaximumFrames);
    }

    [Theory]
    [InlineData("G3_SHORT_FRAME_REUSED")]
    [InlineData("G3_SHORT_CONFIRMATION_INVALID")]
    [InlineData("G3_SHORT_POSITION_OUTSIDE")]
    public void ConfirmationFailureCannotBecomeNewCaptureAuthority(string code)
    {
        var p = G3PostWcsShortExposurePolicy.Start(10);
        Assert.Equal(p, p.AfterConfirmation("G3_SEP_POSITION_MEASURED", Unconfirmed(code, false), 2));
        Assert.Equal(p, p.AfterConfirmation("G3_SEP_UNMEASURED", Unconfirmed(code, false), 2));
        var saturated = p.AfterMeasurement(G3ShortExposurePolicy.SaturatedCode, 1);
        Assert.Equal(saturated, saturated.AfterConfirmation("G3_SEP_POSITION_MEASURED", Unconfirmed("G3_SHORT_REPEAT_DISAGREES"), 2));
    }

    [Fact]
    public void WeakTenMillisecondFrameGetsBoundedSignalSearchNotLongSolveLadder()
    {
        var policy = G3PostWcsShortExposurePolicy.Start(10);
        policy = policy.AfterMeasurement("G3_SEP_UNMEASURED", 1);
        Assert.Equal(30, policy.ExposureMilliseconds);
        policy = policy.AfterMeasurement("G3_SEP_AMBIGUOUS", 2);
        Assert.Equal(90, policy.ExposureMilliseconds);
        Assert.Equal(policy, policy.AfterMeasurement("G3_SEP_UNMEASURED", 3));
        Assert.Equal(6, G3PostWcsShortExposurePolicy.MaximumFrames);
    }

    [Fact]
    public void WeakMeasuredPositionUsesRemainingFramesAtTheCommissionedStrongerCeiling()
    {
        var policy = G3PostWcsShortExposurePolicy.Start(10, 2000);
        policy = policy.AfterMeasurement("G3_SEP_UNMEASURED", 1);
        policy = policy.AfterMeasurement("G3_SEP_UNMEASURED", 2);
        policy = policy.AfterMeasurement("G3_SEP_UNMEASURED", 3);
        Assert.Equal(535, policy.ExposureMilliseconds);
        policy = policy.AfterConfirmation("G3_SEP_POSITION_MEASURED",
            Unconfirmed("G3_SHORT_REPEAT_REQUIRED"), 4, 15.6);
        Assert.Equal(2000, policy.ExposureMilliseconds);
        Assert.Equal(4, policy.Adjustments);
        Assert.Equal(policy, policy.AfterMeasurement("G3_SEP_UNMEASURED", 5));
    }

    [Fact]
    public void ValidCentroidDisagreementRepeatsExposureInsteadOfEscalating()
    {
        var policy = G3PostWcsShortExposurePolicy.Start(10, 2000);
        var next = policy.AfterConfirmation("G3_SEP_POSITION_MEASURED",
            Unconfirmed("G3_SHORT_REPEAT_DISAGREES"), 2, 95.3);
        Assert.Equal(policy, next);
    }

    [Fact]
    public void CommissionedOffSlitExposureBecomesABoundedLowSignalCeiling()
    {
        var policy = G3PostWcsShortExposurePolicy.Start(10, 2000);
        Assert.True(policy.ExtendedSignalRange);
        Assert.Equal(2000, policy.CeilingMilliseconds);
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var next = policy.AfterMeasurement("G3_SEP_UNMEASURED", attempt);
            Assert.InRange(next.ExposureMilliseconds, policy.ExposureMilliseconds + 1, 2000);
            policy = next;
        }
        Assert.Equal(2000, policy.ExposureMilliseconds);
        Assert.Equal(6, G3PostWcsShortExposurePolicy.MaximumFrames);
        Assert.Equal(policy.AfterMeasurement("G3_SEP_UNMEASURED", 5),
            policy.AfterMeasurement("G3_SEP_UNMEASURED", 6));
    }

    [Theory]
    [InlineData(1, 5000)]
    [InlineData(10, 2000)]
    [InlineData(100, 5000)]
    public void ExtendedSearchReservesSameSettingPairEvenAfterEarlierMeasuredFrame(int start, int ceiling)
    {
        var policy = G3PostWcsShortExposurePolicy.Start(start, ceiling);
        Assert.Equal(policy, policy.AfterMeasurement("G3_SEP_POSITION_MEASURED", 1));
        for (var attempt = 2; attempt <= 4; attempt++)
            policy = policy.AfterMeasurement("G3_SEP_UNMEASURED", attempt);
        Assert.Equal(ceiling, policy.ExposureMilliseconds);
        Assert.Equal(policy, policy.AfterMeasurement("G3_SEP_UNMEASURED", 5));
        Assert.Equal(policy, policy.AfterMeasurement("G3_SEP_UNMEASURED", 6));
    }

    [Fact]
    public void OrdinaryShortSearchStillUsesTheOriginalTwoAdjustmentEnvelope()
    {
        var policy = G3PostWcsShortExposurePolicy.Start(10);
        Assert.False(policy.ExtendedSignalRange);
        policy = policy.AfterMeasurement("G3_SEP_UNMEASURED", 1);
        policy = policy.AfterMeasurement("G3_SEP_UNMEASURED", 2);
        Assert.Equal(90, policy.ExposureMilliseconds);
        Assert.Equal(policy, policy.AfterMeasurement("G3_SEP_UNMEASURED", 3));
    }

    [Fact]
    public void SaturationReducesExposureAndPreventsOscillation()
    {
        var policy = G3PostWcsShortExposurePolicy.Start(10).AfterMeasurement(G3ShortExposurePolicy.SaturatedCode, 1);
        Assert.Equal(5, policy.ExposureMilliseconds);
        Assert.Equal(policy, policy.AfterMeasurement("G3_SEP_UNMEASURED", 2));
        Assert.Equal(2, policy.AfterMeasurement(G3ShortExposurePolicy.SaturatedCode, 2).ExposureMilliseconds);
    }

    [Theory]
    [InlineData("G3_SEP_POSITION_MEASURED")]
    [InlineData("G3_SEP_INVALID_COVERAGE")]
    [InlineData("G3_SHORT_FRAME_REUSED")]
    [InlineData("G3_SHORT_REPEAT_DISAGREES")]
    public void NonPhotometricFailuresAndGoodFramesDoNotChangeExposure(string code)
    {
        var policy = G3PostWcsShortExposurePolicy.Start(10);
        Assert.Equal(policy, policy.AfterMeasurement(code, 1));
    }

    [Fact]
    public void LimitsReserveTwoNewFramesAndHonorLongerCommissionedStartingPoint()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => G3PostWcsShortExposurePolicy.Start(0));
        var p = G3PostWcsShortExposurePolicy.Start(50);
        Assert.Equal(100, p.AfterMeasurement("G3_SEP_UNMEASURED", 1).ExposureMilliseconds);
        Assert.Equal(p, p.AfterMeasurement("G3_SEP_UNMEASURED", 5));
        p = G3PostWcsShortExposurePolicy.Start(200);
        Assert.Equal(p, p.AfterMeasurement("G3_SEP_UNMEASURED", 1));
    }
}
