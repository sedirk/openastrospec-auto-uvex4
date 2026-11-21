using System.Reflection;
using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class NinaRoofReadbackTests
{
    private const string Id = "RRCI.Dome";
    private static GateResult ValidBinding() => GateResult.Pass("LOCKED", "original roof");
    private static DomeInfo Roof(ShutterState state, bool moving = false, bool connected = true, string id = Id) =>
        new() { DeviceId = id, Connected = connected, ShutterStatus = state, Slewing = moving, CanSetShutter = true };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutOrCancellationRetainsNativeOperationAcrossRunnersAndBlocksNewMotion(bool cancel)
    {
        var mediator = DispatchProxy.Create<IDomeMediator, NoHardware>();
        var first = NinaRoofConnectionBoundary.For(mediator);
        var next = NinaRoofConnectionBoundary.For(mediator);
        Assert.Same(first, next);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var connects = 0;
        Task<bool> Connect()
        {
            Interlocked.Increment(ref connects);
            entered.TrySetResult();
            return native.Task;
        }
        var run = first.ConnectAsync(Connect, ValidBinding,
            cancel ? TimeSpan.FromSeconds(4) : TimeSpan.FromMilliseconds(450), cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(4)));
            }
            else
            {
                Assert.Equal("ROOF_ADAPTER_CONNECT_TIMEOUT", (await run.WaitAsync(TimeSpan.FromSeconds(4))).Code);
            }
            Assert.Equal("ROOF_ADAPTER_CONNECT_PENDING", next.PendingGate?.Code);
            var duplicate = await next.ConnectAsync(Connect, ValidBinding, TimeSpan.FromSeconds(1), default);
            Assert.Equal("ROOF_ADAPTER_CONNECT_PENDING", duplicate.Code);
            var close = await new NinaRoofCloseBoundary().CloseOnceAsync(() => Roof(ShutterState.ShutterOpen),
                () => next.PendingGate, Id, _ => throw new InvalidOperationException("must not command"),
                _ => throw new InvalidOperationException("must not confirm"), default);
            Assert.Equal("ROOF_ADAPTER_CONNECT_PENDING", close.Code);
            Assert.Equal(1, Volatile.Read(ref connects));
        }
        finally { native.TrySetResult(true); }
        using var settling = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (next.PendingGate is not null) await Task.Delay(10, settling.Token);
        // A late ACK clears the outstanding call only. It is not a new position.
        Assert.Equal("ROOF_STATE_UNKNOWN",
            NinaRoofReadback.CheckObserving(Roof(ShutterState.ShutterNone, connected: false), Id, allowClosed: false).Code);
    }

    [Fact]
    public async Task ProfileChangeBeforeNativeDispatchMakesZeroCalls()
    {
        var b = new NinaRoofConnectionBoundary();
        var result = await b.ConnectAsync(() => throw new InvalidOperationException("must not connect"),
            () => GateResult.Fail("NINA_ENVIRONMENT_DEVICE_SELECTION_DRIFT", "new roof"), TimeSpan.FromSeconds(3), default);
        Assert.Equal("NINA_ENVIRONMENT_DEVICE_SELECTION_DRIFT", result.Code);
        Assert.Null(b.PendingGate);
    }

    [Theory]
    [InlineData(ShutterState.ShutterOpen, false, true, "RRCI.Dome", false, "ROOF_ENDPOINT_CONFIRMED")]
    [InlineData(ShutterState.ShutterOpen, true, true, "RRCI.Dome", false, "ROOF_POSITION_UNCONFIRMED")]
    [InlineData(ShutterState.ShutterOpen, false, false, "RRCI.Dome", false, "ROOF_STATE_UNKNOWN")]
    [InlineData(ShutterState.ShutterOpen, false, true, "other", false, "ROOF_ADAPTER_IDENTITY_MISMATCH")]
    [InlineData(ShutterState.ShutterClosed, false, true, "RRCI.Dome", false, "ROOF_NOT_OPEN")]
    [InlineData(ShutterState.ShutterClosed, true, true, "RRCI.Dome", true, "ROOF_POSITION_UNCONFIRMED")]
    [InlineData(ShutterState.ShutterClosed, false, true, "RRCI.Dome", true, "ROOF_ENDPOINT_CONFIRMED")]
    [InlineData(ShutterState.ShutterNone, false, true, "RRCI.Dome", false, "ROOF_POSITION_UNCONFIRMED")]
    [InlineData(ShutterState.ShutterError, false, true, "RRCI.Dome", false, "ROOF_POSITION_UNCONFIRMED")]
    public void AnInvalidSampleIsNotReportedAsAClosedRoof(ShutterState state, bool moving, bool connected,
        string id, bool allowClosed, string code)
    {
        var result = NinaRoofReadback.CheckObserving(Roof(state, moving, connected, id), Id, allowClosed);
        Assert.Equal(code, result.Code);
        Assert.Equal(code == "ROOF_ENDPOINT_CONFIRMED", result.Disposition == GateDisposition.Passed);
    }

    [Theory]
    [InlineData(ShutterState.ShutterOpen)]
    [InlineData(ShutterState.ShutterClosed)]
    public async Task EndpointRequiresStoppedReadbackEvenIfStateAlreadyMatches(ShutterState endpoint)
    {
        var reads = 0;
        var stopped = await NinaRoofReadback.WaitForEndpointAsync(() => Roof(endpoint, moving: ++reads < 3), () => null,
            Id, endpoint, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(10), default);
        Assert.Equal(GateDisposition.Passed, stopped.Disposition);
        Assert.Equal(3, reads);
        var moving = await NinaRoofReadback.WaitForEndpointAsync(() => Roof(endpoint, moving: true), () => null,
            Id, endpoint, TimeSpan.FromMilliseconds(35), TimeSpan.FromMilliseconds(10), default);
        Assert.Equal("ROOF_TRANSITION_TIMEOUT", moving.Code);
    }

    [Theory]
    [InlineData("closing", 0, true)]
    [InlineData("closed", 0, true)]
    [InlineData("closing-reversed", 0, false)]
    [InlineData("close-ack-without-terminal", 1, false)]
    [InlineData("close-rejected", 1, false)]
    [InlineData("open", 1, true)]
    [InlineData("opening", 0, false)]
    [InlineData("disconnected", 0, false)]
    [InlineData("other-device", 0, false)]
    [InlineData("connected-no-status", 0, false)]
    [InlineData("position5-as-error", 0, false)]
    [InlineData("closed-still-moving", 0, false)]
    public async Task CloseCallsStayOneShotAndNeverUseMissingOrMovingReadback(string mode, int expectedCalls, bool safe)
    {
        var roof = mode switch
        {
            "closing" or "closing-reversed" => Roof(ShutterState.ShutterClosing),
            "closed" => Roof(ShutterState.ShutterClosed),
            "opening" => Roof(ShutterState.ShutterOpening),
            "disconnected" => Roof(ShutterState.ShutterOpen, connected: false),
            "other-device" => Roof(ShutterState.ShutterOpen, id: "other"),
            "connected-no-status" => Roof(ShutterState.ShutterNone),
            "position5-as-error" => Roof(ShutterState.ShutterError),
            "closed-still-moving" => Roof(ShutterState.ShutterClosed, moving: true),
            _ => Roof(ShutterState.ShutterOpen),
        };
        var calls = 0;
        var boundary = new NinaRoofCloseBoundary();
        var a = await boundary.CloseOnceAsync(() => roof, () => null, Id, _ =>
        {
            calls++;
            if (mode == "open") roof = Roof(ShutterState.ShutterClosed);
            return Task.FromResult(mode != "close-rejected");
        }, ct =>
        {
            if (mode == "closing") roof = Roof(ShutterState.ShutterClosed);
            if (mode == "closing-reversed") roof = Roof(ShutterState.ShutterOpen);
            return NinaRoofReadback.WaitForEndpointAsync(() => roof, () => null, Id, ShutterState.ShutterClosed,
                TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(5), ct);
        }, default);
        Assert.Equal(expectedCalls, calls);
        Assert.Equal(safe, a.Disposition == GateDisposition.Passed);
        if (mode is "close-ack-without-terminal" or "close-rejected" or "closing-reversed")
        {
            var b = await boundary.CloseOnceAsync(() => roof, () => null, Id,
                _ => throw new InvalidOperationException("repeat close not allowed"),
                _ => throw new InvalidOperationException("cannot confirm"), default);
            Assert.Equal("ROOF_CLOSE_NOT_AUTHORIZED", b.Code);
        }
    }

    [Fact]
    public async Task AlreadyClosingButChangedBackBeforeReadDoesNotAuthorizeAParkPathCommand()
    {
        var b = new NinaRoofCloseBoundary();
        async Task<GateResult> Close(bool observeOnly) => await b.CloseOnceAsync(() => Roof(ShutterState.ShutterOpen),
            () => null, Id, _ => throw new InvalidOperationException("repeat close not allowed"),
            _ => throw new InvalidOperationException("cannot confirm"), default, observeOnly);
        Assert.Equal("ROOF_CLOSE_NOT_AUTHORIZED", (await Close(observeOnly: true)).Code);
        Assert.Equal("ROOF_CLOSE_NOT_AUTHORIZED", (await Close(observeOnly: false)).Code);
    }

    public class NoHardware : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException("test does not access a device");
    }
}

public sealed partial class NativeSequencePlanTests
{
    [Fact]
    public async Task RecordedNovember16RoofConnectHangStopsNativeStartBeforeAnotherTargetOrEndActions()
    {
        // Nov 16: native Disconnect at 18:00:51; no roof connect ACK while
        // ValidateNightSetup was waiting, then native 18:53 failure. Inject
        // the same missing completion, not a synthetic success/status flag.
        var b = new NinaRoofConnectionBoundary();
        var native = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var gate = await b.ConnectAsync(() => native.Task, () => GateResult.Pass("BOUND", "locked"),
                TimeSpan.FromMilliseconds(200), default);
            Assert.Equal("ROOF_ADAPTER_CONNECT_TIMEOUT", gate.Code);
            await AssertNativeFailureBoundaryAsync("roof-transport-failure", gate.Code, gate,
                failureStage: ObservationStage.ValidateNightSetup, nativeEndArea: true);
        }
        finally { native.TrySetResult(false); }
    }
}
