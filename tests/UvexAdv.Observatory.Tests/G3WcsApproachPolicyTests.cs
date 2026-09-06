using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3WcsApproachPolicyTests
{
    [Theory]
    [InlineData(700, 12000)]
    [InlineData(700, -12000)]
    [InlineData(12000, 500)]
    [InlineData(-12000, 500)]
    public void LongTransferApproachesAnExteriorNeighbourThenFinishesWithoutAnotherWaypoint(double x, double y)
    {
        var slit = new PixelPoint(817, 426);
        var neighbour = G3WcsApproachPolicy.ChooseTargetPixel(new(x, y), slit, 1920, 1080);
        Assert.True(neighbour.X < 0 || neighbour.X >= 1920 || neighbour.Y < 0 || neighbour.Y >= 1080);
        Assert.Equal(slit, G3WcsApproachPolicy.ChooseTargetPixel(neighbour, slit, 1920, 1080));
    }

    [Fact]
    public void NearbyStarKeepsTheOriginalSlitDestination()
    {
        var slit = new PixelPoint(817, 426);
        Assert.Equal(slit, G3WcsApproachPolicy.ChooseTargetPixel(new(1000, 430), slit, 1920, 1080));
        Assert.Throws<ArgumentException>(() => G3WcsApproachPolicy.ChooseTargetPixel(new(double.NaN, 430), slit, 1920, 1080));
    }
}
