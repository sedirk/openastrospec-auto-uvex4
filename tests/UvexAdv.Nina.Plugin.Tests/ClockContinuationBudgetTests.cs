using UvexAdv.Observatory;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class ClockContinuationBudgetTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-12T00:00:00Z");
    private static ClockContinuationBudget Budget => new(new(3, 120, Now.AddMinutes(-2), 12, 600, 1200),
        new(2, 30, Now.AddSeconds(-80), 8, 100, 600), 60, 120, Now.AddMinutes(-3), 1800, 3, .04, 3, 8, 1, 1);

    [Fact]
    public void FreshOpticalEndpointsKeepOnlyOriginalNumericDebitsAndLimits()
    {
        var fresh = new G3AcquisitionMotionState(G3AcquisitionMotionState.CurrentSchemaVersion,
            G3AcquisitionMotionState.CurrentTangentProjectionId, "new-run", new('1', 32), new('A', 64), new('B', 64), new('C', 64),
            G3AcquisitionMotionKind.WcsCentering, G3AcquisitionMotionPhase.SettledBudgetLedger, "pierEast", "J2000",
            10, 20, 10, 20, 10, 20, 0, 0, 0, 100, 500, 1000, 20, 2, 10, 2000, 0, 0, Now, Now, Now, "new-frame.json");
        var carried = Budget.Charge(fresh);
        Assert.Equal(fresh.OriginRaDegrees, carried.OriginRaDegrees);
        Assert.Equal(fresh.PriorReportedRaDegrees, carried.PriorReportedRaDegrees);
        Assert.Equal(fresh.BudgetLineageId, carried.BudgetLineageId);
        Assert.Equal(fresh.DeclaredEvidencePath, carried.DeclaredEvidencePath);
        Assert.Equal(3, carried.CorrectionAttempts); Assert.Equal(120, carried.CumulativeMotionArcseconds);
        Assert.Equal(Now.AddMinutes(-2), carried.StartedUtc);
        Assert.Equal(12, carried.MaximumCorrectionAttempts); Assert.Equal(600, carried.MaximumCumulativeMotionArcseconds);
        Assert.Equal(1200, carried.MaximumElapsedSeconds); Assert.Equal(60, carried.MaximumSingleCorrectionArcseconds);
        Assert.Equal(120, carried.MaximumRadiusArcseconds);
        var after = Budget.Charge(carried with { CorrectionAttempts = 5, CumulativeMotionArcseconds = 200 });
        Assert.Equal(5, after.CorrectionAttempts); Assert.Equal(200, after.CumulativeMotionArcseconds);
        var lockLedger = new Phd2LockShiftLedger("new-epoch-lineage", new(200, 100), new(200, 100), 0, 0, Now, null);
        var charged = Budget.Charge(lockLedger);
        Assert.Equal(lockLedger.OriginLockPosition, charged.OriginLockPosition);
        Assert.Equal(lockLedger.CurrentLockPosition, charged.CurrentLockPosition);
        Assert.Null(charged.LastAcceptedFrameSha256); Assert.Equal(lockLedger.LineageId, charged.LineageId);
        Assert.Equal(2, charged.AttemptsUsed); Assert.Equal(30, charged.CumulativeCommandedPixels);
        Assert.Equal(Now.AddSeconds(-80), charged.StartedUtc);
    }

    [Theory]
    [InlineData("g3-time")] [InlineData("phd-time")] [InlineData("fine-time")]
    [InlineData("attempts")] [InlineData("distance")] [InlineData("probe")]
    public void ClockRepairDoesNotReplenishExhaustedBudget(string part)
    {
        Assert.True(Budget.HasRemainingBudget(Now));
        var bad = part switch
        {
            "g3-time" => Budget with { G3 = Budget.G3! with { StartedUtc = Now.AddHours(-1) } },
            "phd-time" => Budget with { Phd2 = Budget.Phd2! with { StartedUtc = Now.AddHours(-1) } },
            "fine-time" => Budget with { FineStartedUtc = Now.AddHours(-1) },
            "attempts" => Budget with { Phd2 = Budget.Phd2! with { Attempts = 8 } },
            "distance" => Budget with { G3 = Budget.G3! with { Distance = 600 } },
            _ => Budget with { ProbeAttempts = 8 },
        };
        Assert.False(bad.HasRemainingBudget(Now));
    }

    [Fact]
    public void TargetDeadlineIsSkippableButCannotOverrideNightSafetyDeadline()
    {
        var plan = new NativeSequencePlan(1, 2, 0, DeadlineUtc: Now.AddMinutes(5), TargetDeadlineUtc: Now);
        Assert.True(NativeSequencePlan.IsSkippableQualityFailure(plan.StopGate(Now, _ => -30)));
        Assert.Equal("NIGHT_DEADLINE_REACHED", plan.StopGate(Now.AddMinutes(5), _ => -30)!.Code);
        Assert.Equal("NIGHT_DAWN_REACHED", (plan with { StopAtDawn = true }).StopGate(Now, _ => 0)!.Code);
    }

    [Fact]
    public async Task ContinuedProbeUsesRemainingVisitsWithoutReusingPriorScienceCredit()
    {
        var f = new AtrWorkflowFixture(1, [30, 60]);
        f.Probe.InheritedAttempts = 2;
        f.Port.NextMetrics = m => m with { TargetToSkyContrast = .5 };
        var result = await f.Science.SelectExposureAsync(f.Context, default);
        Assert.Equal("ATR_PROBE_ATTEMPT_LIMIT", result.Gate.Code);
        Assert.Single(f.Port.Captures); Assert.Equal(0, f.Science.AcceptedFrames);
        Assert.Equal(1, f.Probe.AttemptedFrames);
    }
}
