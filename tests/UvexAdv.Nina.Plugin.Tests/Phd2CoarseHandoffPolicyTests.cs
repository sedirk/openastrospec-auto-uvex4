using UvexAdv.Nina.Plugin;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2CoarseHandoffPolicyTests
{
    private static Phd2LockShiftLimits Limits() => new(
        25, 100, 8, TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
        1, 1, 2, 100, 20, 20, 2, 4, 3, 1, 1e9);

    [Fact]
    public void HundredPixelRoundTripBudgetCannotAcceptSeventyPixelOneWayHandoff()
    {
        var limits = Limits();
        var maximum = Phd2CoarseHandoffPolicy.MaximumInitialResidual(limits, 0.5, 2);
        Assert.InRange(maximum, 43.99, 44.01);
        Assert.True(71.42 > maximum);
        Assert.Equal(100, limits.MaximumAcquisitionResidualPixels);
        Assert.Equal(100, limits.MaximumCumulativePixels);
        // Current commissioned coarse acquisition radius stays 20, not 100.
        Assert.Equal(20, Math.Min(20, maximum));
    }

    [Fact]
    public void ActionAndTimeReservationsAlsoLimitTheHandoff()
    {
        var twoActions = Phd2CoarseHandoffPolicy.MaximumInitialResidual(Limits() with { MaximumAttempts = 2 }, 0.5, 2);
        var oneMinute = Phd2CoarseHandoffPolicy.MaximumInitialResidual(Limits() with { MaximumElapsed = TimeSpan.FromSeconds(60) }, 0.5, 2);
        Assert.InRange(twoActions, 9.49, 9.51);
        Assert.InRange(oneMinute, 9.49, 9.51);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.1)]
    public void InvalidScaleCannotCreateCoarseHandoffAuthority(double scale)
    {
        Assert.Throws<ArgumentException>(() => Phd2CoarseHandoffPolicy.MaximumInitialResidual(Limits(), scale, 2));
    }
}
