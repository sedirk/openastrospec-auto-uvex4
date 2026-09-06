using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2CalibrationRefreshPolicyTests
{
    [Theory]
    [InlineData(11.1, true)]
    [InlineData(10.0, false)]
    [InlineData(5.0, false)]
    public void AgedCalibrationUsesTheVersionedQualifiedGeometryBand(double error, bool expected)
    {
        Assert.Equal(expected, Phd2CalibrationRefreshPolicy.ShouldRefresh(TimeSpan.FromDays(8), error,
            TimeSpan.FromDays(7), 10, false, false, false));
    }

    [Fact]
    public void RefreshRequiresBothKnownAgeAndPoorGeometryAndNoOutstandingMotion()
    {
        Assert.True(Phd2CalibrationRefreshPolicy.ShouldRefresh(TimeSpan.FromDays(8), 37.5,
            TimeSpan.FromDays(7), 15, false, false, false));
        Assert.False(Phd2CalibrationRefreshPolicy.ShouldRefresh(TimeSpan.FromDays(1), 37.5,
            TimeSpan.FromDays(7), 15, false, false, false));
        Assert.False(Phd2CalibrationRefreshPolicy.ShouldRefresh(TimeSpan.FromDays(8), 5,
            TimeSpan.FromDays(7), 15, false, false, false));
        Assert.False(Phd2CalibrationRefreshPolicy.ShouldRefresh(null, 37.5,
            TimeSpan.FromDays(7), 15, false, false, false));
        Assert.False(Phd2CalibrationRefreshPolicy.ShouldRefresh(TimeSpan.FromDays(8), 37.5,
            TimeSpan.FromDays(7), 15, true, false, false));
        Assert.False(Phd2CalibrationRefreshPolicy.ShouldRefresh(TimeSpan.FromDays(8), 37.5,
            TimeSpan.FromDays(7), 15, false, true, false));
        Assert.False(Phd2CalibrationRefreshPolicy.ShouldRefresh(TimeSpan.FromDays(8), 37.5,
            TimeSpan.FromDays(7), 15, false, false, true));
    }
}
