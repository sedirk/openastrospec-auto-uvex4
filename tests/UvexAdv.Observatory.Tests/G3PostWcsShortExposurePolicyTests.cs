using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3PostWcsShortExposurePolicyTests
{
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
