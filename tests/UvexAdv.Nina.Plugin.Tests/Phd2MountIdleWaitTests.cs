using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2MountIdleWaitTests
{
    private static GateResult Idle => GateResult.Pass("G3_SEARCH_MOUNT_STATE_VALID", "idle");
    private static GateResult Pulse => GateResult.Unknown("G3_SEARCH_PULSE_GUIDING_ACTIVE", "pulse active");

    private static async Task<(GateResult Gate, double Seconds)> Replay(Func<double, GateResult> read)
    {
        var now = TimeSpan.Zero;
        var gate = await Phd2MountIdleWait.WaitAsync(_ => Task.FromResult(read(now.TotalSeconds)),
            CancellationToken.None, () => now, (duration, _) => { now += duration; return Task.CompletedTask; });
        return (gate, now.TotalSeconds);
    }

    [Fact]
    public async Task StoppedCaptureDoesNotImmediatelyGrantMountHandoff()
    {
        var result = await Replay(_ => Idle);
        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal(3, result.Seconds);
    }

    [Fact]
    public async Task InFlightPulseAndDelayedMountPollAreWaitedOutBeforeFreshAcquisition()
    {
        var result = await Replay(seconds => seconds < 2.5 ? Pulse : Idle);
        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal(5.5, result.Seconds);
        Assert.Equal(5, result.Gate.Metrics!["pulseActiveSamples"]);
    }

    [Fact]
    public async Task RecurringPulseRestartsQuietWindowButNotDeadline()
    {
        var result = await Replay(seconds => seconds == 2.5 ? Pulse : Idle);
        Assert.Equal(6, result.Seconds);
        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        var neverQuiet = await Replay(seconds => seconds % 2 == 0 ? Pulse : Idle);
        Assert.Equal(Phd2MountIdleWait.TimeoutCode, neverQuiet.Gate.Code);
        Assert.Equal(15, neverQuiet.Seconds);
    }

    [Fact]
    public async Task StuckPulseFailsAtOriginalBoundWithoutClearingMotionFlag()
    {
        var result = await Replay(_ => Pulse);
        Assert.Equal(Phd2MountIdleWait.TimeoutCode, result.Gate.Code);
        Assert.Equal(15, result.Seconds);
        Assert.Equal(30, result.Gate.Metrics!["pulseActiveSamples"]);
    }

    [Theory]
    [InlineData("G3_SEARCH_MOUNT_DISCONNECTED")]
    [InlineData("G3_SEARCH_MOUNT_SLEWING")]
    [InlineData("G3_SEARCH_MOUNT_PARKED")]
    [InlineData("G3_SEARCH_TRACKING_DISABLED")]
    [InlineData("G3_SEARCH_PIER_SIDE_CHANGED")]
    [InlineData("G3_SEARCH_PIER_SIDE_UNKNOWN")]
    [InlineData("PHD2_REBUILD_STOP_UNCONFIRMED")]
    public async Task OtherFailuresDoNotBecomePulseRetries(string code)
    {
        var result = await Replay(seconds => seconds < 1 ? Pulse : GateResult.Unknown(code, "blocked"));
        Assert.Equal(code, result.Gate.Code);
        Assert.Equal(1, result.Seconds);
    }

    [Fact]
    public async Task CancellationDuringReadDoesNotReturnSuccessOrTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Phd2MountIdleWait.WaitAsync(async token =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Idle;
        }, cancellation.Token));
    }

    [Fact]
    public async Task ReadCompletingBeyondDeadlineCannotAuthorizeHandoff()
    {
        var now = TimeSpan.Zero;
        var result = await Phd2MountIdleWait.WaitAsync(_ =>
        {
            now = TimeSpan.FromSeconds(16);
            return Task.FromResult(Idle);
        }, CancellationToken.None, () => now);
        Assert.Equal(Phd2MountIdleWait.TimeoutCode, result.Code);
    }

    [Fact]
    public void ProductionWaitsAfterStopBeforeAcquisitionWithoutRestartOrMotionCommands()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.Phd2SlitPlacement.cs"));
        var stop = source.IndexOf("var calibrationStopPierSide", StringComparison.Ordinal);
        var wait = source.IndexOf("WaitForPostCalibrationMountIdleAsync", stop, StringComparison.Ordinal);
        var acquire = source.IndexOf("AcquireG3SlitFieldAsync", wait, StringComparison.Ordinal);
        Assert.True(stop >= 0 && wait > stop && acquire > wait);
        Assert.Contains("if (!mountIdle.CanAdvance) return mountIdle", source[wait..acquire]);
        Assert.Contains("reacquired.Gate.Metrics", source[acquire..]);
        var handoff = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.Handoff.cs"));
        var start = handoff.IndexOf("private async Task<StageResult> WaitForPostCalibrationMountIdleAsync", StringComparison.Ordinal);
        var end = handoff.IndexOf("private double Phd2HandoffResidualPixels", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var body = handoff[start..end];
        Assert.Contains("GetAppStateAsync", body);
        Assert.Contains("stopProof.IsCurrent", body);
        Assert.Contains("ValidateG3SearchMountState(expectedPierSide)", body);
        Assert.DoesNotContain("Slew", body);
        Assert.DoesNotContain("StopCapture", body);
        Assert.DoesNotContain("SetLockPosition", body);
        Assert.DoesNotContain("CaptureFullFrame", body);
    }
}
