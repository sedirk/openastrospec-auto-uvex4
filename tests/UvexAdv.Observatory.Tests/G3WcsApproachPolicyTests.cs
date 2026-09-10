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
        Assert.Equal(slit, G3WcsApproachPolicy.ChooseTargetPixel(slit, slit, 1920, 1080));
        Assert.Throws<ArgumentException>(() => G3WcsApproachPolicy.ChooseTargetPixel(new(double.NaN, 430), slit, 1920, 1080));
    }

    [Fact]
    public void TenLacProjectionWithinOldRadialShortcutStillNeedsANearerFormalSolve()
    {
        var target = new PixelPoint(-1064.587591, -2097.350757);
        var slit = new PixelPoint(817.473, 426.867);
        var neighbour = G3WcsApproachPolicy.ChooseTargetPixel(target, slit, 1920, 1080);
        static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(Math.Pow(a.X-b.X, 2)+Math.Pow(a.Y-b.Y, 2));
        Assert.True(Distance(target, slit) < 2 * 1920);
        Assert.Equal(-540, neighbour.Y, 6);
        Assert.NotEqual(slit, neighbour);
        Assert.Equal(Distance(target, slit), Distance(target, neighbour) + Distance(neighbour, slit), 6);
        Assert.Equal(slit, G3WcsApproachPolicy.ChooseTargetPixel(neighbour, slit, 1920, 1080));
    }

    [Theory]
    [InlineData(-541, 426, -540, 426)]
    [InlineData(2461, 426, 2460, 426)]
    [InlineData(817, -541, 817, -540)]
    [InlineData(817, 1621, 817, 1620)]
    public void EachExpandedDetectorEdgeUsesAForwardWaypointWithoutAZeroLengthSecondLeg(
        double x, double y, double expectedX, double expectedY)
    {
        var slit = new PixelPoint(817, 426);
        var neighbour = G3WcsApproachPolicy.ChooseTargetPixel(new(x, y), slit, 1920, 1080);
        Assert.Equal(expectedX, neighbour.X, 8);
        Assert.Equal(expectedY, neighbour.Y, 8);
        Assert.Equal(slit, G3WcsApproachPolicy.ChooseTargetPixel(neighbour, slit, 1920, 1080));
    }

    [Fact]
    public void ScheatHaloClearanceStopsEarlierOnTheSamePathWithoutAddingTravel()
    {
        var target = new PixelPoint(-2927.2691, -4122.3380);
        var slit = new PixelPoint(817.473, 426.867);
        var neighbour = G3WcsApproachPolicy.ChooseTargetPixel(target, slit, 1920, 1080);
        Assert.Equal(-540, neighbour.Y, 6);
        var dx = target.X - slit.X;
        var dy = target.Y - slit.Y;
        var oldFraction = (-120 - slit.Y) / dy;
        var oldNeighbour = new PixelPoint(slit.X + dx * oldFraction, -120);
        static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(Math.Pow(a.X-b.X, 2)+Math.Pow(a.Y-b.Y, 2));
        Assert.True(Distance(target, neighbour) < Distance(target, oldNeighbour));
        Assert.Equal(Distance(target, slit), Distance(target, neighbour) + Distance(neighbour, slit), 6);
        Assert.Equal(slit, G3WcsApproachPolicy.ChooseTargetPixel(neighbour, slit, 1920, 1080));
    }

    [Fact]
    public void ScheatFailedNeighbourChangesTheNextWaypointWithoutMovingTheHaloBoundary()
    {
        var target = new PixelPoint(-2292.8, -3261.6);
        var slit = new PixelPoint(817.473, 426.867);
        var first = G3WcsApproachPolicy.ChooseTargetPixel(target, slit, 1920, 1080);
        var retry = G3WcsApproachPolicy.ChooseTargetPixel(target, slit, 1920, 1080, 1);
        Assert.Equal(first.X + 540, retry.X, 6);
        Assert.Equal(-540, retry.Y, 6);
        Assert.NotEqual(first, retry);
        Assert.Equal(slit, G3WcsApproachPolicy.ChooseTargetPixel(retry, slit, 1920, 1080, 1));
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1080, 1920)]
    [InlineData(960, 540)]
    public void AlternateWaypointsRemainOnTheAllowedRectangleAndDistinctWithinTheEightAttemptEnvelope(int width, int height)
    {
        var slit = new PixelPoint(width * .42, height * .4);
        var clearance = G3WcsApproachPolicy.GetNeighbourClearancePixels(width, height);
        var points = Enumerable.Range(0, 8).Select(failed =>
            G3WcsApproachPolicy.ChooseTargetPixel(new(-12000, -12000), slit, width, height, failed)).ToArray();
        Assert.Equal(points.Length, points.Distinct().Count());
        foreach (var point in points)
        {
            Assert.InRange(point.X, -clearance - 1e-6, width + clearance + 1e-6);
            Assert.InRange(point.Y, -clearance - 1e-6, height + clearance + 1e-6);
            Assert.True(Math.Abs(point.X + clearance) < 1e-6 || Math.Abs(point.Y + clearance) < 1e-6 ||
                Math.Abs(point.X - width - clearance) < 1e-6 || Math.Abs(point.Y - height - clearance) < 1e-6);
        }
        Assert.Throws<ArgumentException>(() => G3WcsApproachPolicy.ChooseTargetPixel(new(-12000, -12000), slit, width, height, -1));
    }

    [Theory]
    [InlineData(1920, 1080, 540)]
    [InlineData(1080, 1920, 540)]
    [InlineData(960, 540, 270)]
    [InlineData(200, 160, 120)]
    public void NeighbourClearanceScalesWithDetectorGeometry(int width, int height, double expected)
    {
        Assert.Equal(expected, G3WcsApproachPolicy.GetNeighbourClearancePixels(width, height));
        Assert.Throws<ArgumentOutOfRangeException>(() => G3WcsApproachPolicy.GetNeighbourClearancePixels(0, height));
    }
}
