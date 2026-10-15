using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class AtrSavedTemperatureRecoveryTests
{
    private static GateResult Ready => GateResult.Pass("ATR_SCIENCE_TEMPERATURE_READY", "Fresh same-owner ready.");
    private static GateResult Zero => AtrCoolingReadinessPolicy.Evaluate(
        new(true, "atr", true, true, 0, -10, 61), "atr", -10);
    private static GateResult SavedFailure(GateResult? owner = null) => AtrCoolingReadinessPolicy.EvaluateSavedFrame(
        new Dictionary<string, string> { ["CCD-TEMP"] = "0", ["SET-TEMP"] = "-10" }, -10, owner ?? Zero);

    [Fact]
    public async Task Wr137ZeroProbeRecoversOnceAndCreditsOnlyTheFreshReplacement()
    {
        var f = new AtrWorkflowFixture(2);
        f.Port.SavedTemperature = i => i == 0 ? SavedFailure() : Ready;
        Assert.True((await f.Science.SelectExposureAsync(f.Context, default)).CanAdvance);
        Assert.Equal(2, f.Probe.AttemptedFrames);
        Assert.Equal(2, f.Probe.RetainedFrames);
        Assert.Equal(1, f.Probe.AcceptedFrames);
        Assert.Equal(2, f.Port.Events.Count(e => e == "temperature"));
        Assert.Equal(f.Port.Saved[1].Path, Assert.Single(f.Port.ScienceReferences));
        Assert.Equal(GateFailureScope.TargetQuality, f.Port.Saved[0].TemperatureGate.FailureScope);
        Assert.True((await f.Science.RunAsync(f.Context, default)).CanAdvance);
        Assert.Equal(2, f.Science.AcceptedFrames);
        Assert.Equal(60, f.Science.AcceptedExposureSeconds);
        Assert.Equal(3, f.Port.Saved.Count); // failed probe remains distinct
    }

    [Fact]
    public async Task ScienceRecoveryKeepsAcceptedFramesAndAttemptsAndSharesTheSingleAllowance()
    {
        var f = new AtrWorkflowFixture(3, attempts: 5);
        f.Port.SavedTemperature = i => i is 1 or 3 ? SavedFailure() : Ready;
        await f.Science.SelectExposureAsync(f.Context, default);
        var result = await f.Science.RunAsync(f.Context, default);
        Assert.Equal(AtrCoolingReadinessPolicy.SavedFrameInvalidCode, result.Gate.Code);
        Assert.Equal(2, f.Science.AcceptedFrames); // initial + freshly revalidated probe
        Assert.Equal(4, f.Science.AttemptedFrames); // also includes both rejected science attempts
        Assert.Equal(60, f.Science.AcceptedExposureSeconds);
        Assert.Equal(2, f.Probe.AttemptedFrames);
        Assert.Equal(4, f.Port.Saved.Count);
        Assert.DoesNotContain(f.Port.Saved[1].Path, f.Port.ScienceReferences);
        Assert.DoesNotContain(f.Port.Saved[3].Path, f.Port.ScienceReferences);
    }

    [Theory]
    [InlineData("skip")] [InlineData("cleanup-failure")] [InlineData("end-policy")]
    [InlineData("late-cancel")] [InlineData("late-unsafe")]
    [InlineData("late-end")] [InlineData("late-deadline")] [InlineData("late-dawn")]
    public async Task PersistentSavedTemperatureFailureActuallyRunsNextTargetOnlyAtCheckedBoundary(string boundary)
    {
        var f = new AtrWorkflowFixture(2);
        f.Port.SavedTemperature = _ => SavedFailure();
        var result = await f.Science.SelectExposureAsync(f.Context, default);
        Assert.False(result.CanAdvance);
        Assert.Equal(2, f.Port.Saved.Count); // no infinite retries
        Assert.Equal(0, f.Science.AcceptedFrames);
        Assert.Equal(2, f.Port.Events.Count(e => e == "temperature"));
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(boundary, result.Gate.Code,
            result.Gate, ObservationStage.SelectAtrExposure);
    }

    [Theory]
    [InlineData("ATR_COOLING_RECOVERY_EXHAUSTED")]
    [InlineData("ATR_SCIENCE_TEMPERATURE_TIMEOUT")]
    public async Task CoolingExhaustionPreservesTheOriginalReasonAndSkipsAfterStop(string code)
    {
        var f = new AtrWorkflowFixture();
        f.Port.SavedTemperature = _ => SavedFailure();
        f.Port.CoolingReadiness = i => i == 1 ? Ready :
            AtrCoolingReadinessPolicy.CoolingWaitExhausted(code, "Existing cooling allowance spent.", Zero);
        var result = await f.Science.SelectExposureAsync(f.Context, default);
        Assert.Equal(code, result.Gate.Code);
        Assert.Single(f.Port.Saved);
        Assert.Equal(0, f.Science.AcceptedFrames);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("skip", code, result.Gate, ObservationStage.SelectAtrExposure);
    }

    [Theory]
    [InlineData("ATR_COOLING_IDENTITY_CHANGED")]
    [InlineData("ATR_COOLING_DISCONNECTED")]
    [InlineData("ATR_COOLING_FRESH_READ_FAILED")]
    [InlineData("ATR_COOLING_RECOVERY_STOP_UNCONFIRMED")]
    [InlineData("ATR_COOLING_RECOVERY_CAMERA_BUSY")]
    [InlineData("UNKNOWN_DRIVER_FAILURE")]
    public async Task SavedFrameAndCoolingExhaustionCannotHideAnOwnerOrStopFailure(string code)
    {
        var fault = GateResult.Unknown(code, "No valid owner or stopped proof.");
        Assert.Same(fault, AtrCoolingReadinessPolicy.CoolingWaitExhausted("ATR_SCIENCE_TEMPERATURE_TIMEOUT", "timeout", fault));
        var f = new AtrWorkflowFixture();
        f.Port.SavedTemperature = _ => SavedFailure(fault);
        var result = await f.Science.SelectExposureAsync(f.Context, default);
        Assert.Equal(code, result.Gate.Code);
        Assert.Equal(GateFailureScope.Run, result.Gate.FailureScope);
        Assert.Single(f.Port.Saved);
        Assert.Equal(1, f.Port.Events.Count(e => e == "temperature"));
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("unknown-gate", code, result.Gate, ObservationStage.SelectAtrExposure);
    }

    [Theory]
    [InlineData("stop")] [InlineData("immediate")]
    public async Task RecoveryCannotAuthorizeAnExposureAcrossAChangedBoundary(string boundary)
    {
        var f = new AtrWorkflowFixture();
        f.Port.SavedTemperature = _ => SavedFailure();
        f.Port.CoolingReadiness = i => { if (i == 2) f.Port.Deny = boundary; return Ready; };
        if (boundary == "immediate")
            await Assert.ThrowsAsync<OperationCanceledException>(() => f.Science.SelectExposureAsync(f.Context, default));
        else Assert.False((await f.Science.SelectExposureAsync(f.Context, default)).CanAdvance);
        Assert.Single(f.Port.Captures);
        Assert.Equal(0, f.Science.AcceptedFrames);
    }

    [Fact]
    public async Task ScienceAttemptExhaustionDoesNotEvenEnterCoolingRecovery()
    {
        var f = new AtrWorkflowFixture(3, attempts: 2);
        await f.Science.SelectExposureAsync(f.Context, default);
        f.Port.SavedTemperature = _ => SavedFailure();
        Assert.False((await f.Science.RunAsync(f.Context, default)).CanAdvance);
        Assert.Equal(2, f.Science.AttemptedFrames);
        Assert.Equal(1, f.Science.AcceptedFrames);
        Assert.Equal(1, f.Port.Events.Count(e => e == "temperature"));
    }

    [Fact]
    public void TemperatureRetrySharesTheExistingSingleProbeRepeatSlot()
    {
        var b = new AtrProbeVisitBudget([30, 60, 120]);
        Assert.True(b.TryBeginProbe(30, false));
        Assert.True(b.TryBeginProbe(30, false, true));
        Assert.Equal(1, b.TemperatureReprobes);
        Assert.False(b.CanRepeatAfterTemperatureFailure);
        Assert.True(b.TryBeginProbe(60, false));
        Assert.False(b.TryBeginProbe(30, true));
        Assert.True(b.TryBeginProbe(120, false));
        Assert.Equal(b.MaximumAttempts, b.AttemptCount);
        Assert.False(b.TryBeginProbe(120, false, true));
    }

    [Theory]
    [InlineData(2, "ATR_PROBE_ATTEMPT_LIMIT")]
    [InlineData(3, "ATR_PROBE_TIER_CYCLE")]
    public async Task ClippingAfterTemperatureRepeatSkipsWithoutAnotherRepeatBudget(int tierCount, string code)
    {
        var f = new AtrWorkflowFixture(1, new double[] { 30, 60, 120 }.Take(tierCount).ToArray());
        var peaks = new Queue<double>([20000, 20000, 65000]);
        // Keep this a genuinely signal-limited ladder. A measured SNR-ready
        // first tier now intentionally ends optional exposure optimization.
        f.Port.NextMetrics = m => m with { HighPercentileAdu = peaks.Dequeue(),
            ContinuumSnrPerResolutionElement = 1, LineSnrPerResolutionElement = 2 };
        f.Port.SavedTemperature = i => i == 0 ? SavedFailure() : Ready;
        var result = await f.Science.SelectExposureAsync(f.Context, default);
        Assert.Equal(code, result.Gate.Code);
        Assert.Equal(new double[] { 30, 30, 60 }, f.Port.Captures.Select(c => c.Metrics.ExposureSeconds));
        Assert.Equal(3, f.Probe.RetainedFrames);
        Assert.Equal(0, f.Science.AcceptedFrames);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("skip", code, result.Gate, ObservationStage.SelectAtrExposure);
    }
}
