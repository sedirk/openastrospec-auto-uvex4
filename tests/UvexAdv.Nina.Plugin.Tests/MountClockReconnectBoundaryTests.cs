using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class MountClockReconnectBoundaryTests
{
    [Fact]
    public async Task SuccessfulSequentialNativeOperationsKeepTheirExactTaskAndReturnValue()
    {
        var boundary = new MountClockReconnectBoundary(TimeSpan.FromMilliseconds(20));
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = boundary.RunAsync("disconnect", () => disconnected.Task, TimeSpan.FromSeconds(2), default);
        Assert.Same(disconnected.Task, boundary.PendingOperation);
        Assert.True(boundary.HasUnjoinedOperation);
        Assert.NotNull(boundary.BoundaryGate);
        disconnected.SetResult();
        await waiting;
        Assert.Null(boundary.BoundaryGate);
        Assert.Null(boundary.Issue);
        var connected = Task.FromResult(true);
        Assert.True(await boundary.RunAsync("connect", () => connected, TimeSpan.FromSeconds(1), default));
        Assert.Same(connected, boundary.PendingOperation);
        Assert.Null(boundary.BoundaryGate);
    }

    [Fact]
    public async Task RejectedButJoinedConnectIsNotClaimedSuccessfulOrMislabelledUnjoined()
    {
        var boundary = new MountClockReconnectBoundary();
        Assert.False(await boundary.RunAsync("connect", () => Task.FromResult(false), TimeSpan.FromSeconds(1), default));
        Assert.False(boundary.HasUnjoinedOperation);
        Assert.Null(boundary.BoundaryGate);
        // The caller retains the existing reconnect-rejected failure gate.
    }

    [Theory]
    [InlineData("disconnect", false)]
    [InlineData("connect", false)]
    [InlineData("disconnect", true)]
    [InlineData("connect", true)]
    public async Task TimeoutOrCancellationCannotForgetActualUnjoinedOwnerAndLateCompletionCannotClearLatch(string name, bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var actual = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var boundary = new MountClockReconnectBoundary(TimeSpan.FromMilliseconds(20));
        var wrapper = boundary.RunAsync(name, () => actual.Task,
            cancel ? TimeSpan.FromSeconds(2) : TimeSpan.FromMilliseconds(20), cancellation.Token);
        if (cancel) cancellation.Cancel();
        var error = await Record.ExceptionAsync(() => wrapper.WaitAsync(TimeSpan.FromSeconds(1)));
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<TimeoutException>(error);
        Assert.True(wrapper.IsCompleted);
        Assert.False(actual.Task.IsCompleted);
        Assert.Same(actual.Task, boundary.PendingOperation);
        Assert.True(boundary.HasUnjoinedOperation);
        Assert.Equal("MOUNT_CLOCK_OWNER_OPERATION_UNCONFIRMED", boundary.BoundaryGate!.Code);
        Assert.NotNull(boundary.Issue);
        var dispatches = 0;
        await Assert.ThrowsAsync<PhysicalActionGateException>(() => boundary.RunAsync("connect", () =>
        {
            dispatches++;
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(1), default));
        Assert.Equal(0, dispatches);
        actual.SetResult(true);
        await actual.Task;
        Assert.False(boundary.HasUnjoinedOperation);
        Assert.NotNull(boundary.BoundaryGate); // Never replaced by an instantaneous idle readback.
        await Assert.ThrowsAsync<PhysicalActionGateException>(() => boundary.RunAsync("another-connect", () => Task.CompletedTask,
            TimeSpan.FromSeconds(1), default));
    }

    [Fact]
    public async Task CancelledWaitCanJoinTheOriginalTaskWithoutIssuingAnyCleanupCommand()
    {
        using var cancellation = new CancellationTokenSource();
        var actual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var boundary = new MountClockReconnectBoundary(TimeSpan.FromSeconds(1));
        var commands = 0;
        var wrapper = boundary.RunAsync("disconnect", () =>
        {
            commands++;
            dispatched.SetResult();
            return actual.Task;
        }, TimeSpan.FromSeconds(2), cancellation.Token);
        await dispatched.Task;
        cancellation.Cancel();
        actual.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wrapper);
        Assert.Equal(1, commands);
        Assert.Same(actual.Task, boundary.PendingOperation);
        Assert.False(boundary.HasUnjoinedOperation);
        Assert.Null(boundary.Issue);
        Assert.Null(boundary.BoundaryGate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynchronousOrAsynchronousOwnerFailureStillPropagatesWithoutFabricatingAnUnjoinedTask(bool synchronous)
    {
        var boundary = new MountClockReconnectBoundary(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAsync<IOException>(() => boundary.RunAsync("connect", () => synchronous
            ? throw new IOException("native owner failure") : Task.FromException(new IOException("native owner failure")),
            TimeSpan.FromSeconds(1), default));
        Assert.False(boundary.HasUnjoinedOperation);
        Assert.Null(boundary.Issue);
        Assert.Null(boundary.BoundaryGate);
    }

    [Fact]
    public async Task CancellationBeforeDispatchNeverInvokesOwner()
    {
        var boundary = new MountClockReconnectBoundary();
        var commands = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => boundary.RunAsync("connect", () =>
        {
            commands++;
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(1), new CancellationToken(true)));
        Assert.Equal(0, commands);
        Assert.Null(boundary.PendingOperation);
        Assert.Null(boundary.BoundaryGate);
    }

    [Fact]
    public async Task ConcurrentLifecycleRequestCannotDispatchBeforeFirstNativeTaskJoins()
    {
        var boundary = new MountClockReconnectBoundary();
        var actual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = boundary.RunAsync("disconnect", () => actual.Task, TimeSpan.FromSeconds(1), default);
        var secondCommands = 0;
        await Assert.ThrowsAsync<PhysicalActionGateException>(() => boundary.RunAsync("connect", () =>
        {
            secondCommands++;
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(1), default));
        Assert.Equal(0, secondCommands);
        actual.SetResult();
        await waiting;
        Assert.Null(boundary.BoundaryGate);
    }

    [Fact]
    public void RealRunnerUsesActualOwnerBoundaryBeforeDisposalMechanicalCleanupAndFreshIdleEligibility()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        Assert.Contains("mountClockReconnectBoundary.RunAsync(\"disconnect\", () => telescopeMediator.Disconnect()", source);
        Assert.Contains("mountClockReconnectBoundary.RunAsync(\"connect\", () => telescopeMediator.Connect()", source);
        Assert.DoesNotContain("telescopeMediator.Disconnect().WaitAsync", source);
        Assert.DoesNotContain("telescopeMediator.Connect().WaitAsync", source);
        var clock = source[source.IndexOf("private async Task<GateResult> EnsureMountClockSynchronizedAsync", StringComparison.Ordinal)..];
        Assert.True(clock.IndexOf("mountClockReconnectBoundary.BoundaryGate", StringComparison.Ordinal) <
            clock.IndexOf("var initial = ValidateMountClock()", StringComparison.Ordinal));
        var action = source[source.IndexOf("private GateResult ValidateCurrentActionPrerequisites", StringComparison.Ordinal)..];
        Assert.True(action.IndexOf("mountClockReconnectBoundary.BoundaryGate", StringComparison.Ordinal) <
            action.IndexOf("var authorization =", StringComparison.Ordinal));
        var dispose = source[source.IndexOf("public async ValueTask DisposeAsync()", StringComparison.Ordinal)..];
        Assert.True(dispose.IndexOf("mountClockReconnectBoundary.HasUnjoinedOperation", StringComparison.Ordinal) <
            dispose.IndexOf("disposed = true", StringComparison.Ordinal));
        var cleanup = source[source.IndexOf("private async Task<IReadOnlyList<string>> CleanupAfterFailureCoreAsync", StringComparison.Ordinal)..];
        var ownerGate = cleanup.IndexOf("mountClockReconnectBoundary.BoundaryGate", StringComparison.Ordinal);
        var suppress = cleanup.IndexOf("allowMechanicalActions = false", StringComparison.Ordinal);
        var mechanical = cleanup.IndexOf("if (allowMechanicalActions &&", StringComparison.Ordinal);
        Assert.True(ownerGate >= 0 && suppress > ownerGate && mechanical > suppress);
        var native = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "UvexAdv.Nina.Plugin", "RealObservationStageRunner.NativeSequence.cs"));
        Assert.Contains("mountClockReconnectBoundary.BoundaryGate is null", native);
        var closeout = native[native.IndexOf("internal async Task<IReadOnlyList<string>> CloseSequenceNightAsync", StringComparison.Ordinal)..];
        Assert.True(closeout.IndexOf("mountClockReconnectBoundary.BoundaryGate", StringComparison.Ordinal) <
            closeout.IndexOf("CloseOpticalCoverAsync", StringComparison.Ordinal));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
