using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class CancellationSignalTests
{
    [Fact]
    public void EveryCallbackIsSignalledAndAllNativeErrorsRemainAvailable()
    {
        using var source = new CancellationTokenSource();
        var notified = 0;
        using var first = source.Token.Register(() => { notified++; throw new NullReferenceException("released ToupTek owner"); });
        using var second = source.Token.Register(() => { notified++; throw new IOException("native callback failure"); });
        using var third = source.Token.Register(() => notified++);
        var failure = Assert.IsType<AggregateException>(CancellationSignal.Request(source));
        Assert.True(source.IsCancellationRequested);
        Assert.Equal(3, notified);
        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.Contains(failure.InnerExceptions, ex => ex is NullReferenceException);
        Assert.Contains(failure.InnerExceptions, ex => ex is IOException);
        Assert.Null(CancellationSignal.Request(source));
        Assert.Equal(3, notified); // No repeat invocation of failed callbacks.
    }

    [Fact]
    public void RetiredOrMissingSourceCreatesNoNewAuthorityOrError()
    {
        var source = new CancellationTokenSource();
        source.Dispose();
        Assert.Null(CancellationSignal.Request(source));
        Assert.Null(CancellationSignal.Request(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallbackFailureDoesNotSkipOwnerCleanupOrInventPhysicalStop(bool stopUnconfirmed)
    {
        using var coordinator = new ObservationRunCoordinator();
        var runner = new ThrowingNativeOwner(stopUnconfirmed);
        var callbackFailures = new List<AggregateException>();
        coordinator.CancellationCallbackFailed += (_, failure) => callbackFailures.Add(failure);
        var plan = new ObservationPlan("cancel-callback-" + Guid.NewGuid().ToString("N"), "test",
            new("recorded owner teardown", "SIM", 0, 89), new(31, 121, 20), DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(1), new(0, 0, 0), new(), "ATR", "G3", "QHY");
        var run = coordinator.StartAsync(plan, runner);
        await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(Record.Exception(coordinator.Cancel));
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, runner.CleanupCalls);
        Assert.Equal(1, runner.OtherCallbackCalls);
        Assert.Contains("released ToupTek owner", Assert.Single(callbackFailures).ToString(), StringComparison.Ordinal);
        Assert.Equal(stopUnconfirmed ? ObservationRunState.Faulted : ObservationRunState.Cancelled,
            coordinator.Snapshot.State);
        Assert.Equal(stopUnconfirmed ? "CANCEL_CLEANUP_FAILED" : "RUN_CANCELLED", coordinator.Snapshot.RecentEvents.Last().Code);
        Assert.DoesNotContain(ObservationStage.FinalizeObservation, runner.Stages);
        Assert.False(coordinator.Resume());
    }

    private sealed class ThrowingNativeOwner(bool stopUnconfirmed) : ObservationStageRunnerBase, IObservationRunCompletionCommitter
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CleanupCalls, OtherCallbackCalls;
        internal List<ObservationStage> Stages { get; } = [];
        public override async Task<StageResult> ExecuteStageAsync(ObservationStage stage, ObservationContext context, CancellationToken token)
        {
            Stages.Add(stage);
            using var other = token.Register(() => OtherCallbackCalls++);
            using var brokenSdk = token.Register(() => throw new NullReferenceException("released ToupTek owner"));
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(GateResult.Pass("UNREACHABLE", "No cancelled owner may continue."));
        }
        public override Task OnCancelledAsync(ObservationContext context, CancellationToken token)
        {
            CleanupCalls++;
            Assert.False(token.IsCancellationRequested); // Independent finite cleanup token.
            return stopUnconfirmed ? Task.FromException(new InvalidOperationException("actual owner stop not confirmed")) : Task.CompletedTask;
        }
        public Task<ObservationSnapshot> CommitCompletionAsync(Func<ObservationSnapshot> create, CancellationToken token) =>
            throw new InvalidOperationException("A cancelled run must not complete.");
    }
}
