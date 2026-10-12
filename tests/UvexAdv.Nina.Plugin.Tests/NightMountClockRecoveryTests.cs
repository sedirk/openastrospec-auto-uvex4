using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class NightMountClockRecoveryTests
{
    [Fact]
    public void ProductionRepairSharesStartupOwnerAndRetainsPhysicalGates()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var start = source.IndexOf("private async Task<GateResult> EnsureMountClockSynchronizedAsync(", StringComparison.Ordinal);
        var end = source.IndexOf("private C11MainFocusOwnerSnapshot ReadC11MainFocusOwner(", start, StringComparison.Ordinal);
        var body = source[start..end];
        Assert.Contains("allowInvalidClockForIdleRecovery: true", body);
        Assert.Contains("SequenceStopGate()", body);
        Assert.Contains("ValidateOpticalCoverOpen()", body);
        Assert.Contains("activeQhyJobs.Count != 0", body);
        Assert.Contains("phd2.Snapshot.AppState != Phd2AppState.Stopped", body);
        Assert.Contains("ValidateNinaProfileOwnerSelections", body);
        Assert.Contains("TelescopeSettings.TimeSync", body);
        Assert.Contains("Disconnect().WaitAsync(TimeSpan.FromSeconds(20)", body);
        Assert.Contains("Connect().WaitAsync(TimeSpan.FromSeconds(30)", body);
        Assert.Contains("postConfirmation.Observe(afterClock)", body);
        Assert.Contains("after.SideOfPier != beforePier", body);
        Assert.Contains("after.TrackingEnabled != beforeTracking", body);
        Assert.DoesNotContain("SetTrackingEnabled", body);
        Assert.DoesNotContain("CommandString", body);
        Assert.DoesNotContain("new Telescope", body);
        Assert.Equal(1, source.Split("allowInvalidClockForIdleRecovery: true").Length - 1);
    }

    [Theory]
    [InlineData("MOUNT_CLOCK_RECHECK_REQUIRED")]
    [InlineData("MOUNT_CLOCK_OFFSET_EXCEEDED")]
    [InlineData("MOUNT_CLOCK_UNAVAILABLE")]
    public async Task TerminalClockFailureRecoversOnlyOnceAndNeverBecomesQualityWhitelist(string code)
    {
        var policy = new NightMountClockRecovery();
        var failure = GateResult.Unknown(code, "recorded two-day clock error");
        Assert.False(NativeSequencePlan.IsSkippableQualityFailure(failure));
        var calls = new List<string>();
        Task Save(CancellationToken _) { Assert.True(policy.Attempted); calls.Add("intent"); return Task.CompletedTask; }
        Task<GateResult> Repair(CancellationToken _) { calls.Add("repair"); return Task.FromResult(GateResult.Pass("MOUNT_CLOCK_VALID", "")); }
        var result = await policy.TryRecoverAsync(failure, true, Save, Repair, default);
        Assert.Equal(GateDisposition.Passed, result!.Disposition);
        Assert.Equal(new[] { "intent", "repair" }, calls);
        Assert.Null(await policy.TryRecoverAsync(failure, true, Save, Repair, default));
        Assert.Equal(2, calls.Count);
        Assert.NotEqual(GateDisposition.Passed, failure.Disposition);
    }

    [Theory]
    [InlineData("NIGHT_UNSAFE", true)]
    [InlineData("PHD2_STOP_UNCONFIRMED", true)]
    [InlineData("MOUNT_CLOCK_POLICY_INVALID", true)]
    [InlineData("MOUNT_CLOCK_RECHECK_REQUIRED", false)]
    public async Task UnjoinedCancelledUnsafeOrUnknownBoundaryCannotInvokeRecovery(string code, bool eligible)
    {
        var policy = new NightMountClockRecovery();
        var result = await policy.TryRecoverAsync(GateResult.Unknown(code, ""), eligible,
            _ => throw new Xunit.Sdk.XunitException("must not journal an action"),
            _ => throw new Xunit.Sdk.XunitException("must not reconnect"), default);
        Assert.Null(result); Assert.False(policy.Attempted);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("throw")]
    [InlineData("invalid-clock")]
    public async Task FailedRecoveryCannotRetryOrGrantContinuation(string phase)
    {
        var policy = new NightMountClockRecovery(); var repairCalls = 0;
        var failure = GateResult.Unknown("MOUNT_CLOCK_RECHECK_REQUIRED", "");
        var result = await policy.TryRecoverAsync(failure, true,
            _ => phase == "save" ? Task.FromException(new IOException("journal failed")) : Task.CompletedTask,
            _ => { repairCalls++; return phase == "throw" ? Task.FromException<GateResult>(new TimeoutException()) :
                Task.FromResult(GateResult.Fail("MOUNT_CLOCK_RECOVERY_POSTCHECK_FAILED", "still wrong")); }, default);
        Assert.NotEqual(GateDisposition.Passed, result!.Disposition);
        Assert.True(policy.Attempted);
        Assert.Equal(phase == "save" ? 0 : 1, repairCalls);
        Assert.Null(await policy.TryRecoverAsync(failure, true, _ => throw new Exception(), _ => throw new Exception(), default));
    }

    [Fact]
    public async Task CancellationAfterIntentNeverConnectsAndKeepsAttemptCharged()
    {
        using var cts = new CancellationTokenSource();
        var policy = new NightMountClockRecovery();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.TryRecoverAsync(
            GateResult.Unknown("MOUNT_CLOCK_RECHECK_REQUIRED", ""), true,
            _ => { cts.Cancel(); return Task.CompletedTask; },
            _ => throw new Xunit.Sdk.XunitException("must not reconnect"), cts.Token));
        Assert.True(policy.Attempted);
    }
}
