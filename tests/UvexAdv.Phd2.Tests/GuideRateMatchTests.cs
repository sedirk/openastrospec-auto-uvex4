using UvexAdv.Phd2;

namespace UvexAdv.Phd2.Tests;

public sealed class GuideRateMatchTests
{
    [Fact]
    public void NoOldCalibrationRequiresCalibrationButDoesNotPretendCurrentMountRateIsUnknown()
    {
        var failures = new List<string>();
        var unknown = new List<string>();
        Phd2GuideRateMatch.Validate(null, 15, 15, failures, unknown);
        Assert.Empty(failures);
        Assert.Equal(2, unknown.Count);
        Assert.All(unknown, reason => Assert.StartsWith("PHD2_CALIBRATION_GUIDE_RATE_UNCONFIRMED:", reason));
        Assert.DoesNotContain(unknown, reason => reason.Contains("PHD2_GUIDE_RATE_UNCONFIRMED", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(7.5, 7.5, 0, 0)]
    [InlineData(7.7, 7.7, 0, 0)]
    [InlineData(15, 15, 2, 0)]
    [InlineData(7.5, 15, 1, 0)]
    [InlineData(0, 7.5, 0, 1)]
    [InlineData(double.NaN, double.PositiveInfinity, 0, 2)]
    public void PhysicalGuideSpeedIsNotPixelRateOrCameraScale(double ra, double dec, int failed, int unknown)
    {
        var failures = new List<string>();
        var missing = new List<string>();
        Phd2GuideRateMatch.Validate(new(DateTimeOffset.UtcNow, 7.5, 7.5), ra, dec, failures, missing);
        Assert.Equal(failed, failures.Count);
        Assert.Equal(unknown, missing.Count);
    }
}
