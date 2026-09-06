using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2SlitSettleTimingTests
{
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
