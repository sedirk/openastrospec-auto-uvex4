using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class HorizonCalculatorTests
{
    [Fact]
    public void Mid0BudgetDoesNotGrowFrom35To55MinutesDuringSearch()
    {
        var start = DateTimeOffset.Parse("2026-10-25T16:10:46.4405321Z");
        var now = DateTimeOffset.Parse("2026-10-25T16:18:11.1020671Z");
        var plan = new ObservationPlan("mid0-replay", "setup",
            new EquatorialTarget("HD 218393", "HD 218393", 346.77588398267, 50.19235793967),
            new ObservatorySite(33.37583333333333, 120.41666666666667, 0), start, TimeSpan.FromMinutes(35),
            new HorizonPolicy(42, 0, 0), new MotionLimits(), "atr", "g3", "qhy");
        Assert.Equal(HorizonEvaluation.WindowExhaustedCode, HorizonCalculator.Evaluate(plan with
            { PlannedStartUtc = now, PlannedDuration = TimeSpan.FromMinutes(55) }).ToRuntimeGateResult().Code);
        var context = new ObservationContext(plan);
        context.LimitVisibilityTo(start.AddMinutes(35));
        context.RemainingWorstCaseDuration = TimeSpan.FromMinutes(55);
        Assert.Equal(start.AddMinutes(35) - now, context.ProtectedDurationAt(now));
        Assert.Equal("HORIZON_CLEAR", HorizonCalculator.Evaluate(plan with
            { PlannedStartUtc = now, PlannedDuration = context.ProtectedDurationAt(now) }).ToRuntimeGateResult().Code);
        context.LimitVisibilityTo(start.AddHours(1)); // cannot refill on retry/segment
        Assert.Equal(start.AddMinutes(35) - now, context.ProtectedDurationAt(now));
        Assert.Equal(TimeSpan.FromMinutes(3), SetShortScience());
        TimeSpan SetShortScience() { context.RemainingWorstCaseDuration = TimeSpan.FromMinutes(3); return context.ProtectedDurationAt(now); }
        Assert.Equal("HORIZON_BLOCKED", HorizonCalculator.Evaluate(plan with
            { PlannedStartUtc = DateTimeOffset.Parse("2026-10-25T17:10:00Z"), PlannedDuration = TimeSpan.FromSeconds(1) }).ToRuntimeGateResult().Code);
    }

    [Theory]
    [InlineData(35, "HORIZON_CLEAR")]
    [InlineData(55, HorizonEvaluation.WindowExhaustedCode)]
    public void V460RecordedPlacementWindowIsForecastRejectionNotCurrentWallCrossing(int minutes, string expected)
    {
        var plan = new ObservationPlan("replay", "setup",
            new EquatorialTarget("V460 Cyg", "V* V460 Cyg", 325.50451287212, 35.51019997682),
            new ObservatorySite(33.37583333333333, 120.41666666666667, 0),
            DateTimeOffset.Parse("2026-10-09T23:35:37.2343235+08:00"), TimeSpan.FromMinutes(minutes),
            new HorizonPolicy(42, 0, 0), new MotionLimits(), "atr", "g3", "qhy");
        var evaluation = HorizonCalculator.Evaluate(plan);
        var gate = evaluation.ToRuntimeGateResult();
        Assert.Equal(expected, gate.Code);
        Assert.Equal(evaluation.Passed, new StageResult(gate).CanAdvance);
        if (!evaluation.Passed)
        {
            Assert.Equal("HORIZON_BLOCKED", evaluation.ToGateResult().Code);
            Assert.InRange(gate.Metrics!["currentAltitudeDegrees"], 51.4, 51.6);
            Assert.Equal(55, gate.Metrics["protectedDurationMinutes"]);
            Assert.Equal(48, gate.Metrics["minutesToFirstBlockedSample"]);
            Assert.InRange(gate.Metrics["firstBlockedAltitudeDegrees"], 41.9, 42);
        }
    }

    [Theory]
    [InlineData("below")] [InlineData("missing")] [InlineData("nonfinite")]
    [InlineData("current-failure")] [InlineData("one-sample")]
    public void RuntimeClassificationNeverMakesCurrentOrUnprovenHorizonSkippable(string scenario)
    {
        var now = DateTimeOffset.UtcNow;
        var first = new AltitudeSample(now, scenario == "nonfinite" ? double.NaN : 55, 90, 42, scenario != "below");
        var failed = new AltitudeSample(now.AddMinutes(30), 41, 95, 42, false);
        var samples = scenario == "missing" ? Array.Empty<AltitudeSample>() : scenario == "one-sample"
            ? new[] { failed } : new[] { first, failed };
        var evaluation = new HorizonEvaluation(false, 41, -1, scenario == "current-failure" ? first : failed, samples);
        Assert.Equal("HORIZON_BLOCKED", evaluation.ToRuntimeGateResult().Code);
    }

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
