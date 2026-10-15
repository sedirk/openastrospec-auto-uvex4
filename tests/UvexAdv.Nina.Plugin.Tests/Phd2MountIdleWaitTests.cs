using UvexAdv.Observatory;
using Xunit;
using System.Reflection;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2MountIdleWaitTests
{
    [Theory]
    [InlineData("delayed-poll", true)]
    [InlineData("pulse-stuck", false)]
    [InlineData("disconnected", false)]
    [InlineData("identity", false)]
    [InlineData("slewing", false)]
    [InlineData("guide-resumed", false)]
    [InlineData("epoch", false)]
    public async Task CompletedTargetWaitsForTheSameStoppedOwnerBeforeNextNativeTarget(string mode, bool expectedNext)
    {
        var waitCalls = 0;
        await new NativeSequencePlanTests().AssertCompletedTargetPulseDrainBoundaryAsync(async token =>
        {
            waitCalls++;
            var now = TimeSpan.Zero;
            var snapshot = Phd2StateSnapshot.Disconnected with { IsConnected = true, AppState = Phd2AppState.Stopped,
                ConnectionEpoch = 1, GuideEpoch = 2 };
            var client = DispatchProxy.Create<IPhd2Client, ReadOnlyProxy>();
            ((ReadOnlyProxy)(object)client).Read = name => name switch
            {
                "get_Snapshot" => snapshot,
                "GetAppStateAsync" => ReadGuide(),
                _ => throw new InvalidOperationException($"Unexpected owner action: {name}"),
            };
            Task<Phd2AppState> ReadGuide()
            {
                if (mode == "guide-resumed" && now.TotalSeconds >= 1) snapshot = snapshot with { AppState = Phd2AppState.Guiding };
                if (mode == "epoch" && now.TotalSeconds >= 1) snapshot = snapshot with { GuideEpoch = 3 };
                return Task.FromResult(snapshot.AppState);
            }
            var mount = DispatchProxy.Create<ITelescopeMediator, ReadOnlyProxy>();
            ((ReadOnlyProxy)(object)mount).Read = name => name == "GetInfo"
                ? new TelescopeInfo { Connected = mode != "disconnected", DeviceId = mode == "identity" ? "other" : "mount",
                    Slewing = mode == "slewing", IsPulseGuiding = mode == "pulse-stuck" || now.TotalSeconds < 2.5 }
                : throw new InvalidOperationException($"Unexpected mount action: {name}");
            var result = await Phd2MountIdleWait.WaitAfterTargetStopAsync(client, mount, "mount", token,
                () => now, (duration, ct) => { ct.ThrowIfCancellationRequested(); now += duration; return Task.CompletedTask; });
            Assert.Equal(expectedNext, result.Disposition == GateDisposition.Passed);
            if (expectedNext) Assert.Equal(5.5, now.TotalSeconds);
            if (mode == "pulse-stuck") Assert.Equal(15, now.TotalSeconds);
            if (result.Disposition != GateDisposition.Passed) throw new InvalidOperationException(result.Code);
        }, expectedNext);
        Assert.True(waitCalls > 0);
    }

    public class ReadOnlyProxy : DispatchProxy
    {
        public Func<string, object?> Read = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Read(method!.Name);
    }

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
        // Keep the whole result (including metrics, evidence and metadata),
        // not an unknown wrapper that hides a skippable quality gate.
        Assert.Contains("return reacquired;", source[acquire..]);
        Assert.DoesNotContain("POST_CALIBRATION_G3_REACQUISITION_BLOCKED", source[acquire..]);
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
