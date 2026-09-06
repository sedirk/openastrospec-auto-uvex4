using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3LocalSearchPlannerTests
{
    [Fact]
    public void SquareSpiralIsDeterministicBoundedAndOneStepAtATime()
    {
        var limits = new G3LocalSearchLimits(
            G3LocalSearchPattern.SquareSpiral,
            StepArcseconds: 10,
            MaximumRadiusArcseconds: 30,
            MaximumCumulativeMotionArcseconds: 240,
            MaximumAttempts: 12,
            MaximumElapsedTime: TimeSpan.FromMinutes(5));

        var points = G3LocalSearchPlanner.Build(limits);

        Assert.Equal(12, points.Count);
        Assert.Collection(
            points.Take(4),
            point => AssertOffset(point, 10, 0),
            point => AssertOffset(point, 10, 10),
            point => AssertOffset(point, 0, 10),
            point => AssertOffset(point, -10, 10));
        Assert.All(points, point =>
        {
            Assert.InRange(point.RadiusArcseconds, double.Epsilon, limits.MaximumRadiusArcseconds);
            Assert.Equal(limits.StepArcseconds, point.MoveFromPreviousArcseconds, 9);
        });
    }

    [Fact]
    public void CircularBoundaryUsesContinuousCardinalBridgesInsteadOfStoppingAtFirstOutsidePoint()
    {
        var limits = new G3LocalSearchLimits(
            G3LocalSearchPattern.SquareSpiral,
            StepArcseconds: 10,
            MaximumRadiusArcseconds: 20,
            MaximumCumulativeMotionArcseconds: 40,
            MaximumAttempts: 10,
            MaximumElapsedTime: TimeSpan.FromMinutes(1));

        var points = G3LocalSearchPlanner.Build(limits);

        Assert.Equal(10, points.Count);
        Assert.Collection(
            points.Take(8),
            point => AssertOffset(point, 10, 0),
            point => AssertOffset(point, 10, 10),
            point => AssertOffset(point, 0, 10),
            point => AssertOffset(point, -10, 10),
            point => AssertOffset(point, -10, 0),
            point => AssertOffset(point, -10, -10),
            point => AssertOffset(point, 0, -10),
            point => AssertOffset(point, 10, -10));
        AssertOffset(points[8], 10, 0);
        AssertOffset(points[9], 20, 0);
        Assert.All(points, point =>
        {
            Assert.InRange(point.RadiusArcseconds, 0, limits.MaximumRadiusArcseconds);
            Assert.Equal(limits.StepArcseconds, point.MoveFromPreviousArcseconds, 9);
        });
    }

    [Fact]
    public void AttemptLimitTruncatesOtherwiseLargerPlan()
    {
        var limits = new G3LocalSearchLimits(
            G3LocalSearchPattern.SquareSpiral,
            StepArcseconds: 5,
            MaximumRadiusArcseconds: 100,
            MaximumCumulativeMotionArcseconds: 100,
            MaximumAttempts: 3,
            MaximumElapsedTime: TimeSpan.FromMinutes(1));

        var points = G3LocalSearchPlanner.Build(limits);

        Assert.Equal(3, points.Count);
        Assert.Equal([1, 2, 3], points.Select(point => point.Attempt));
    }

    [Theory]
    [InlineData(0, 10, 0)]
    [InlineData(1, 10, 1)]
    [InlineData(10, 10, 1)]
    [InlineData(10.1, 10, 2)]
    [InlineData(30, 10, 3)]
    public void ReturnMoveCountNeverRequiresAnOversizedStep(
        double radiusArcseconds,
        double stepArcseconds,
        int expected)
    {
        Assert.Equal(
            expected,
            G3LocalSearchPlanner.RequiredReturnMoves(radiusArcseconds, stepArcseconds));
    }

    [Fact]
    public void InvalidOrUnreturnableLimitsAreRejected()
    {
        var limits = new G3LocalSearchLimits(
            G3LocalSearchPattern.SquareSpiral,
            StepArcseconds: 20,
            MaximumRadiusArcseconds: 10,
            MaximumCumulativeMotionArcseconds: 20,
            MaximumAttempts: 0,
            MaximumElapsedTime: TimeSpan.Zero);

        var issues = limits.Validate();

        Assert.Contains(issues, issue => issue.Contains("radius must reserve", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Contains("safe return", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Contains("attempt", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Contains("elapsed", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => G3LocalSearchPlanner.Build(limits));
    }

    [Fact]
    public void FirstFullSegmentRoundTripAndTwoActionsMustFitTheDeclaredLimits()
    {
        const double stepArcseconds = 300;
        const double fullSegmentArcseconds =
            stepArcseconds + 2 * G3LocalSearchLimits.StableEndpointAllowanceArcseconds;
        const double minimumRoundTripArcseconds = 2 * fullSegmentArcseconds;
        var valid = new G3LocalSearchLimits(
            G3LocalSearchPattern.SquareSpiral,
            StepArcseconds: stepArcseconds,
            MaximumRadiusArcseconds: fullSegmentArcseconds,
            MaximumCumulativeMotionArcseconds: minimumRoundTripArcseconds,
            MaximumAttempts: 2,
            MaximumElapsedTime: TimeSpan.FromMinutes(1));

        Assert.Empty(valid.Validate());
        Assert.Equal(310, fullSegmentArcseconds);
        Assert.Equal(620, minimumRoundTripArcseconds);

        var radiusIssues = valid with { MaximumRadiusArcseconds = fullSegmentArcseconds - 0.01 };
        Assert.Contains(
            radiusIssues.Validate(),
            issue => issue.Contains("radius must reserve one fully charged segment", StringComparison.Ordinal));

        var cumulativeIssues = valid with { MaximumCumulativeMotionArcseconds = minimumRoundTripArcseconds - 0.01 };
        Assert.Contains(
            cumulativeIssues.Validate(),
            issue => issue.Contains("cumulative-motion limit", StringComparison.Ordinal));

        var attemptIssues = valid with { MaximumAttempts = 1 };
        Assert.Contains(
            attemptIssues.Validate(),
            issue => issue.Contains("one outward action and one safe return action", StringComparison.Ordinal));
    }

    private static void AssertOffset(
        G3LocalSearchWaypoint point,
        double expectedRaArcseconds,
        double expectedDeclinationArcseconds)
    {
        Assert.Equal(expectedRaArcseconds, point.RaTangentOffsetArcseconds, 9);
        Assert.Equal(expectedDeclinationArcseconds, point.DeclinationOffsetArcseconds, 9);
    }
}
