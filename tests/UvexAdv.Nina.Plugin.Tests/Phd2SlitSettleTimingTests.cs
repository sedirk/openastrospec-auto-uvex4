using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2SlitSettleTimingTests
{
    [Theory]
    [InlineData(120, 30, 18, 5)]
    [InlineData(12, 30, 12, 5)]
    [InlineData(120, 15, 9, 5)]
    [InlineData(2, 30, 2, 2)]
    public void LivePlacementTimingReservesOpticalFrameTimeInsideConfiguredTimeout(
        int configuredSeconds, double stage, int expectedTimeout, int expectedStable)
    {
        var result = Phd2SlitSettleTiming.ForBoundedWarningPlacement(new(1.5, 10, configuredSeconds), 3, stage);
        Assert.Equal(expectedTimeout, result.TimeoutSeconds);
        Assert.Equal(expectedStable, result.StableTimeSeconds);
        Assert.Equal(3, result.Pixels);
    }

    [Fact]
    public void RealThirtySecondStageAllowsGuideResponseAndReservesFreshFrameTime()
    {
        var result = Phd2SlitSettleTiming.ForSupervisedPlacement(new Phd2SettleCriteria(1.5, 10, 120), 30);
        Assert.Equal(18, result.TimeoutSeconds);
        Assert.Equal(3, result.StableTimeSeconds);
        Assert.Equal(1.5, result.Pixels);
    }

    [Fact]
    public void DoesNotExtendExplicitlyShorterConfiguredTimeout()
    {
        var configured = new Phd2SettleCriteria(1.5, 1, 2);
        Assert.Equal(configured, Phd2SlitSettleTiming.ForSupervisedPlacement(configured, 30));
    }
}
