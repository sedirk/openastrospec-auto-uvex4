using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

// Cross-layer contracts: use production refusals and TWO native target
// containers, not just a boolean skip classifier. No physical owner is opened.
public sealed class FailureChainAuditTests
{
    public static IEnumerable<object[]> BudgetCases()
    {
        foreach (var cause in new[] { "time", "attempt", "cumulative", "segment-attempt", "segment-cumulative" })
        foreach (var boundary in new[] { "skip", "cleanup-failure", "end-policy" })
            yield return [cause, boundary];
    }

    [Theory]
    [MemberData(nameof(BudgetCases))]
    public async Task FineMotionRefusalNeverDispatchesAndCanExecuteNextNativeTarget(string cause, string boundary)
    {
        var start = DateTimeOffset.Parse("2026-10-08T12:00:00Z");
        var limits = new MotionLimits(.5, 1.5, 5, TimeSpan.FromMinutes(12));
        var gate = cause.StartsWith("segment-", StringComparison.Ordinal)
            ? SlitPlacementRecoveryPlanner.ValidateOutboundAndReturnReserve(limits, cause == "segment-cumulative" ? 1.4 : 0, cause == "segment-attempt" ? 4 : 0, .1)
            : SlitPlacementRecoveryPlanner.ValidateCorrectionBudget(limits, cause == "cumulative" ? 1.5 : 0,
                cause == "attempt" ? 5 : 0, .1, start, start.AddMinutes(cause == "time" ? 13 : 1));
        Assert.Equal(GateFailureScope.TargetQuality, gate.FailureScope);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(boundary, gate.Code, gate, ObservationStage.PlaceTargetOnSlit);
    }

    [Theory]
    [InlineData("nan")] [InlineData("overdrawn")] [InlineData("single")]
    [InlineData("clock")] [InlineData("limits")]
    public async Task FineMotionInvalidStateIsNotNormalExhaustion(string cause)
    {
        var now = DateTimeOffset.UtcNow;
        var limits = new MotionLimits(.5, cause == "limits" ? double.NaN : 1.5, 5);
        var gate = SlitPlacementRecoveryPlanner.ValidateCorrectionBudget(limits, cause == "nan" ? double.NaN : 0,
            cause == "overdrawn" ? 6 : 5, cause == "single" ? 1 : .1,
            cause == "clock" ? now.AddHours(1) : now.AddMinutes(-20), now);
        Assert.Equal(GateFailureScope.Run, gate.FailureScope);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("unknown-gate", gate.Code, gate);
    }

    [Theory]
    [InlineData("SLIT_LOCK_ACQUISITION_CUMULATIVE_RESERVE")]
    [InlineData("SLIT_LOCK_ACQUISITION_ATTEMPT_RESERVE")]
    [InlineData("SLIT_LOCK_ACQUISITION_TIME_RESERVE")]
    [InlineData("SLIT_LOCK_RETURN_CUMULATIVE_RESERVE")]
    [InlineData("SLIT_LOCK_RETURN_ATTEMPT_RESERVE")]
    [InlineData("SLIT_LOCK_RETURN_TIME_RESERVE")]
    [InlineData("SLIT_RESIDUAL_SEARCH_WINDOW")]
    [InlineData("GUIDE_LOCK_RESIDUAL_HIGH")]
    public async Task EveryReviewedPlannerQualityRefusalSurvivesTheNativeBoundary(string code)
    {
        var gate = Phd2HandoffRecoveryPolicy.FailureGate(code, "Production planner protocol refusal.");
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("skip", code, gate, ObservationStage.PlaceTargetOnSlit);
    }

    [Theory]
    [InlineData("SLIT_LOCK_ALTITUDE_SAFETY")] [InlineData("SLIT_LOCK_PIER_CHANGED")]
    [InlineData("SLIT_LOCK_LEDGER_INVALID")] [InlineData("SLIT_LOCK_LEDGER_CLOCK_INVALID")]
    [InlineData("PHD2_LOCK_SHIFT_UNQUALIFIED")] [InlineData("G3_TOPOLOGY_FINGERPRINT_CHANGED")]
    [InlineData("TARGET_IDENTITY_UNCONFIRMED")] [InlineData("UNKNOWN_NEW_CODE")]
    public async Task PlannerSafetyAndUnknownRefusalsKeepTheirOriginalIdentity(string code)
    {
        var gate = Phd2HandoffRecoveryPolicy.FailureGate(code, "Original safety/identity refusal.");
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("unknown-gate", code, gate, ObservationStage.PlaceTargetOnSlit);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task SearchReturnCannotLaunderDeviceFailureIntoQuality(bool returned)
    {
        var source = GateResult.Unknown("PHD2_IDENTITY_STALE", "Owner identity failed.");
        var report = GateResult.Unknown(returned ? "G3_BOUNDED_SEARCH_EXHAUSTED_RETURNED" : "G3_SEARCH_RETURN_BLOCKED", "Return report.");
        var gate = G3WcsRecoveryPolicy.SearchTerminalGate(report, source, returned,
            GateResult.TargetQuality("G3_MOTION_RETURN_TIME_LIMIT", "Budget spent."));
        Assert.Equal(source.Code, gate.Code);
        Assert.Equal(GateFailureScope.Run, gate.FailureScope);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("unknown-gate", gate.Code, gate);
    }

    [Theory]
    [InlineData("skip")] [InlineData("cleanup-failure")] [InlineData("end-policy")]
    public async Task SearchBudgetReturnPreservesReasonWithoutClaimingOriginArrival(string boundary)
    {
        var report = GateResult.Unknown("G3_SEARCH_RETURN_BLOCKED", "No origin arrival proven.");
        var stop = G3WcsRecoveryPolicy.SearchStopGate("G3_SEARCH_TIME_EXHAUSTED", "Time used.", null);
        var gate = G3WcsRecoveryPolicy.SearchTerminalGate(report, stop, false,
            GateResult.TargetQuality("G3_MOTION_RETURN_TIME_LIMIT", "No command fits."));
        Assert.Equal("G3_MOTION_RETURN_TIME_LIMIT", gate.Code);
        Assert.Contains("No origin arrival proven", gate.Message);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(boundary, gate.Code, gate);
    }

    [Fact]
    public async Task ChangedPierDuringReturnDoesNotOverrideOriginalDeviceFailure()
    {
        var source = GateResult.Unknown("PHD2_IDENTITY_STALE", "Owner identity failed.");
        var gate = G3WcsRecoveryPolicy.UnreturnedCenteringGate(
            GateResult.Fail("G3_SEARCH_PIER_SIDE_CHANGED", "Known side changed."),
            "No origin arrival proven.", new Dictionary<string, double>(), source);
        Assert.Equal(source.Code, gate.Code);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("unknown-gate", gate.Code, gate);
    }

    [Theory]
    [InlineData("stale-gate")] [InlineData("late-cancel")] [InlineData("late-unsafe")] [InlineData("late-end")]
    [InlineData("late-deadline")] [InlineData("late-dawn")]
    public async Task TerminalStageAndLateNightSignalsControlActualNextTarget(string boundary) =>
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(boundary, "TARGET_TEST_EXHAUSTED",
            GateResult.TargetQuality("TARGET_TEST_EXHAUSTED", "No hardware; ordinary target exhaustion."));
}
