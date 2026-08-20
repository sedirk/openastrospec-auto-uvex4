using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class HorizonCalculatorTests
{
    [Fact]
    public void PolarisIsRejectedByFortyDegreeWallAtThisSite()
    {
        var plan = new ObservationPlan(
            "run", "setup",
            new EquatorialTarget("Polaris", "HIP 11767", 37.95456067, 89.26410897),
            new ObservatorySite(33.37583333333333, 120.41666666666667, 0),
            new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero),
            TimeSpan.FromHours(1),
            new HorizonPolicy(40, 0, 0),
            new MotionLimits(), "atr", "g3", "qhy");

        var result = HorizonCalculator.Evaluate(plan);

        Assert.False(result.Passed);
        Assert.InRange(result.MinimumAltitudeDegrees, 32, 35);
    }

    [Fact]
    public void AzimuthProfileInterpolatesAcrossNorthWrap()
    {
        var policy = new HorizonPolicy(AzimuthProfile:
        [
            new HorizonPoint(350, 40),
            new HorizonPoint(10, 50),
            new HorizonPoint(180, 42)
        ]);

        Assert.Equal(45, HorizonCalculator.InterpolateHorizonAltitude(policy, 0), 6);
    }
}
