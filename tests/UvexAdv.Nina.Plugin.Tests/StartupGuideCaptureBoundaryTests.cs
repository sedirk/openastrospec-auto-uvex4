using UvexAdv.Observatory;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class StartupGuideCaptureBoundaryTests
{
    private static Phd2SingleFrameResult Frame() => new("synthetic.fit", false, true, DateTimeOffset.UtcNow, 1000);
    private static Phd2StopCaptureResult Stopped() => new(Phd2AppState.Stopped, Phd2AppState.Stopped, true, true, DateTimeOffset.UtcNow);

    [Fact]
    public async Task CompletedNativeFrameIsReturnedOnlyAfterOriginalOwnerStopCompletes()
    {
        var guard = new StartupGuideCaptureBoundary(TimeSpan.FromMilliseconds(100));
        var calls = new List<string>();
        var captureToken = CancellationToken.None;
        var result = await guard.RunAsync(t => { captureToken = t; calls.Add("capture"); return Task.FromResult(Frame()); },
            t => { Assert.True(captureToken.IsCancellationRequested); Assert.False(t.IsCancellationRequested); calls.Add("stop"); return Task.FromResult(Stopped()); },
            t => { t.ThrowIfCancellationRequested(); calls.Add("safety"); return Task.CompletedTask; }, default);
        Assert.Equal("synthetic.fit", result.Path);
        Assert.Equal(new[] { "safety", "capture", "stop" }, calls);
        Assert.Null(guard.UnjoinedOperation);
        Assert.Null(guard.StopIssue);
    }

    [Theory]
    [InlineData("capture-fault")]
    [InlineData("capture-sync-fault")]
    [InlineData("unsafe")]
    [InlineData("cancel")]
    public async Task FailedOrCancelledFrameStillStopsAndJoinsItsActualOwner(string failure)
    {
        using var cancel = new CancellationTokenSource();
        var native = new TaskCompletionSource<Phd2SingleFrameResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var guard = new StartupGuideCaptureBoundary(TimeSpan.FromMilliseconds(100));
        var checks = 0;
        var stops = 0;
        var error = await Record.ExceptionAsync(() => guard.RunAsync(t =>
        {
            if (failure == "capture-sync-fault") throw new InvalidOperationException("capture dispatch failed");
            if (failure == "capture-fault") return Task.FromException<Phd2SingleFrameResult>(new InvalidOperationException("capture failed"));
            if (failure == "cancel") cancel.Cancel();
            return native.Task;
        }, t =>
        {
            stops++;
            native.TrySetCanceled();
            Assert.False(t.IsCancellationRequested);
            return Task.FromResult(Stopped());
        }, t =>
        {
            t.ThrowIfCancellationRequested();
            if (++checks > 1 && failure == "unsafe")
                throw new PhysicalActionGateException(GateResult.Unknown("SAFETY_UNSAFE", "Unsafe during native frame."));
            return Task.CompletedTask;
        }, cancel.Token).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.NotNull(error);
        Assert.IsNotType<TimeoutException>(error);
        Assert.Equal(1, stops);
        Assert.Null(guard.StopIssue);
        Assert.Null(guard.UnjoinedOperation);
        if (failure == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
        if (failure == "unsafe") Assert.Equal("SAFETY_UNSAFE", Assert.IsType<PhysicalActionGateException>(error).Gate.Code);
    }

    [Theory]
    [InlineData(false, Phd2AppState.Stopped)]
    [InlineData(true, Phd2AppState.Looping)]
    [InlineData(true, Phd2AppState.Guiding)]
    public async Task ConfirmedIdleFlagDoesNotReplaceExactStoppedState(bool idle, Phd2AppState state)
    {
        var guard = new StartupGuideCaptureBoundary(TimeSpan.FromMilliseconds(20));
        var error = await Assert.ThrowsAsync<PhysicalActionGateException>(() => guard.RunAsync(_ => Task.FromResult(Frame()),
            _ => Task.FromResult(Stopped() with { ConfirmedIdle = idle, FinalState = state }), _ => Task.CompletedTask, default));
        Assert.Equal("GUIDE_STARTUP_STOP_UNCONFIRMED", error.Gate.Code);
        Assert.NotNull(guard.StopIssue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopTransportFailureRemainsAHardBoundaryEvenWhenTheFrameCompleted(bool synchronous)
    {
        var guard = new StartupGuideCaptureBoundary(TimeSpan.FromMilliseconds(20));
        var error = await Assert.ThrowsAsync<PhysicalActionGateException>(() => guard.RunAsync(_ => Task.FromResult(Frame()),
            _ => synchronous ? throw new IOException("stop transport failed") : Task.FromException<Phd2StopCaptureResult>(new IOException("stop transport failed")),
            _ => Task.CompletedTask, default));
        Assert.Equal("GUIDE_STARTUP_STOP_UNCONFIRMED", error.Gate.Code);
        Assert.NotNull(guard.StopIssue);
        Assert.Null(guard.UnjoinedOperation);
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("stop")]
    [InlineData("both")]
    public async Task NonCooperativeActualTaskIsLatchedAfterBoundedStopAndCannotBeMistakenForJoined(string pending)
    {
        using var cancel = new CancellationTokenSource();
        var native = new TaskCompletionSource<Phd2SingleFrameResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource<Phd2StopCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var guard = new StartupGuideCaptureBoundary(TimeSpan.FromMilliseconds(20));
        var calls = new List<string>();
        if (pending == "stop") native.SetResult(Frame());
        var error = await Assert.ThrowsAsync<PhysicalActionGateException>(() => guard.RunAsync(_ =>
        {
            calls.Add("capture");
            if (pending != "stop") cancel.Cancel();
            return native.Task;
        }, _ =>
        {
            calls.Add("stop-dispatched");
            return pending == "capture" ? Task.FromResult(Stopped()) : stopped.Task;
        }, t => { t.ThrowIfCancellationRequested(); return Task.CompletedTask; }, cancel.Token).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("GUIDE_STARTUP_STOP_UNCONFIRMED", error.Gate.Code);
        Assert.Equal(new[] { "capture", "stop-dispatched" }, calls);
        Assert.NotNull(guard.UnjoinedOperation);
        Assert.False(guard.UnjoinedOperation!.IsCompleted);
        Assert.NotNull(guard.StopIssue);
        // Finish the real fake ports only after proving the latch. Completion
        // does not erase the historical failure or mint an idle receipt.
        native.TrySetResult(Frame()); stopped.TrySetResult(Stopped());
        await guard.UnjoinedOperation.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.NotNull(guard.StopIssue);
    }

    [Fact]
    public async Task FailedPreflightStartsNeitherCaptureNorStop()
    {
        var guard = new StartupGuideCaptureBoundary(TimeSpan.FromMilliseconds(20));
        var calls = 0;
        await Assert.ThrowsAsync<PhysicalActionGateException>(() => guard.RunAsync(
            _ => { calls++; return Task.FromResult(Frame()); },
            _ => { calls++; return Task.FromResult(Stopped()); },
            _ => throw new PhysicalActionGateException(GateResult.Unknown("SAFETY_UNSAFE", "Before dispatch.")), default));
        Assert.Equal(0, calls);
    }
}
