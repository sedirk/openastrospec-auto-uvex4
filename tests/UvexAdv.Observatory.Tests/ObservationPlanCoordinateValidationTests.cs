using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class ObservationPlanCoordinateValidationTests
{
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.000_001)]
    [InlineData(360)]
    public void NonFiniteOrOutOfRangeRightAscensionIsRejected(double rightAscensionDegrees)
    {
        var issues = CreatePlan(rightAscensionDegrees, 45).Validate();

        Assert.Contains(issues, issue => issue.Contains("Right ascension must be finite", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-90.000_001)]
    [InlineData(90.000_001)]
    public void NonFiniteOrOutOfRangeDeclinationIsRejected(double declinationDegrees)
    {
        var issues = CreatePlan(310.35798, declinationDegrees).Validate();

        Assert.Contains(issues, issue => issue.Contains("Declination must be finite", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, -90)]
    [InlineData(0, 0)]
    [InlineData(359.999_999, 90)]
    public void FiniteBoundaryCoordinatesRemainValid(double rightAscensionDegrees, double declinationDegrees)
    {
        var issues = CreatePlan(rightAscensionDegrees, declinationDegrees).Validate();

        Assert.DoesNotContain(issues, issue => issue.Contains("Right ascension", StringComparison.Ordinal));
        Assert.DoesNotContain(issues, issue => issue.Contains("Declination", StringComparison.Ordinal));
    }

    private static ObservationPlan CreatePlan(double rightAscensionDegrees, double declinationDegrees) => new(
        "coordinate-validation",
        "night-setup",
        new EquatorialTarget("Test target", "TEST", rightAscensionDegrees, declinationDegrees),
        new ObservatorySite(33.37583333333333, 120.41666666666667, 0),
        DateTimeOffset.Parse("2026-08-18T14:00:00Z"),
        TimeSpan.FromMinutes(10),
        new HorizonPolicy(),
        new MotionLimits(),
        "ATR585M",
        "G3M2210M",
        "QHYminiCam8M",
        RequireSafetyMonitor: false);
}
