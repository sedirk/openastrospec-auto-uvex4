using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class ObservationRunCoordinatorTests
{
    [Fact]
    public async Task PassingStagesAdvanceWithoutConfirmation()
    {
        using var coordinator = new ObservationRunCoordinator();
        var runner = new FakeRunner();

        await coordinator.StartAsync(CreatePlan(), runner);

        Assert.Equal(ObservationRunState.Completed, coordinator.Snapshot.State);
        Assert.Equal(ObservationRunCoordinator.Stages, runner.Executed);
        Assert.Equal(0, runner.PausedCount);
    }

    [Fact]
    public async Task FirstSnapshotIsNotPublishedWhileStartLockIsHeld()
    {
        using var coordinator = new ObservationRunCoordinator();
        var runner = new FakeRunner();
        var firstSnapshotObserved = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        coordinator.SnapshotChanged += (_, _) =>
        {
            var independentReader = Task.Run(() => coordinator.Snapshot);
            firstSnapshotObserved.TrySetResult(
                independentReader.Wait(TimeSpan.FromSeconds(1)));
        };

        await coordinator.StartAsync(CreatePlan(), runner);

        Assert.True(
            await firstSnapshotObserved.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            "SnapshotChanged was published while StartAsync still held the coordinator lock.");
    }

    [Fact]
    public async Task RunnerWithoutDurableCompletionAcknowledgerIsRejectedBeforeRunStarts()
    {
        using var coordinator = new ObservationRunCoordinator();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            coordinator.StartAsync(CreatePlan(), new NonAcknowledgingRunner()));

        Assert.Contains("durable completion", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ObservationRunState.Idle, coordinator.Snapshot.State);
    }

    [Fact]
    public async Task FailedGatePausesAndResumeRerunsSameStage()
    {
        using var coordinator = new ObservationRunCoordinator();
        var runner = new FakeRunner { FailOnceAt = ObservationStage.CoarseCenter };
        var run = coordinator.StartAsync(CreatePlan(), runner);

        await WaitForStateAsync(coordinator, ObservationRunState.PausedNeedsAttention);
        Assert.Equal(ObservationStage.CoarseCenter, coordinator.Snapshot.CurrentStage);
        Assert.Contains("test failure", coordinator.Snapshot.PauseReason, StringComparison.OrdinalIgnoreCase);

        Assert.True(coordinator.Resume());
        await run;

        Assert.Equal(ObservationRunState.Completed, coordinator.Snapshot.State);
        Assert.Equal(2, runner.Executed.Count(stage => stage == ObservationStage.CoarseCenter));
        Assert.Equal(1, runner.RevalidationCount);
    }

    [Fact]
    public async Task ManualPauseStopsBeforeNextAtomicStage()
    {
        using var coordinator = new ObservationRunCoordinator();
        var runner = new FakeRunner { BlockAt = ObservationStage.AcquireQhyWideField };
        var run = coordinator.StartAsync(CreatePlan(), runner);

        await runner.BlockEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.RequestPause("operator pause");
        runner.ReleaseBlock.TrySetResult();
        await WaitForStateAsync(coordinator, ObservationRunState.Paused);

        Assert.DoesNotContain(ObservationStage.CoarseCenter, runner.Executed);
        Assert.True(coordinator.Resume());
        await run;
        Assert.Equal(ObservationRunState.Completed, coordinator.Snapshot.State);
    }

    [Fact]
    public async Task UnconfirmedPauseSafetyCleanupEntersNeedsAttentionInsteadOfFaulting()
    {
        using var coordinator = new ObservationRunCoordinator();
        var runner = new FakeRunner
        {
            BlockAt = ObservationStage.AcquireQhyWideField,
            PauseFailure = new InvalidOperationException("synthetic slit LED OFF readback failure"),
        };
        var run = coordinator.StartAsync(CreatePlan(), runner);

        await runner.BlockEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.RequestPause("operator pause");
        runner.ReleaseBlock.TrySetResult();
        await WaitForStateAsync(coordinator, ObservationRunState.PausedNeedsAttention);

        Assert.Equal(0, runner.FaultedCount);
        Assert.Contains("OFF readback failure", coordinator.Snapshot.PauseReason, StringComparison.Ordinal);
        Assert.Contains(
            coordinator.Snapshot.RecentEvents,
            item => item.Code == "PAUSE_SAFETY_CLEANUP_FAILED");

        coordinator.Cancel();
        await run;
        Assert.Equal(ObservationRunState.Cancelled, coordinator.Snapshot.State);
    }

    [Fact]
    public async Task CheckpointPausesInsideMultiActionStageBeforeNextAction()
    {
        using var coordinator = new ObservationRunCoordinator();
        var runner = new CheckpointRunner();
        var run = coordinator.StartAsync(CreatePlan(), runner);
        await runner.FirstActionFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));

        coordinator.RequestPause("pause between frames");
        runner.AllowCheckpoint.TrySetResult();
        await WaitForStateAsync(coordinator, ObservationRunState.Paused);
        Assert.Equal(1, runner.ActionCount);

        Assert.True(coordinator.Resume());
        await run;
        Assert.Equal(2, runner.ActionCount);
        Assert.Equal(ObservationRunState.Completed, coordinator.Snapshot.State);
    }

    [Fact]
    public async Task CancelFromPauseCompletesAsCancelled()
    {
        using var coordinator = new ObservationRunCoordinator();
        var runner = new FakeRunner { FailOnceAt = ObservationStage.AcquireG3SlitField };
        var run = coordinator.StartAsync(CreatePlan(), runner);
        await WaitForStateAsync(coordinator, ObservationRunState.PausedNeedsAttention);

        coordinator.Cancel();
        await run;

        Assert.Equal(ObservationRunState.Cancelled, coordinator.Snapshot.State);
        Assert.Equal(1, runner.CancelledCount);
    }

    [Fact]
    public async Task UnexpectedFaultInvokesCleanupAndPreservesOriginalFailure()
    {
        using var coordinator = new ObservationRunCoordinator();
        var runner = new FakeRunner { ThrowAt = ObservationStage.StartQhyPhotometry };

        await coordinator.StartAsync(CreatePlan(), runner);

        Assert.Equal(ObservationRunState.Faulted, coordinator.Snapshot.State);
        Assert.Equal(1, runner.FaultedCount);
        Assert.Contains("synthetic stage exception", coordinator.Snapshot.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(coordinator.Snapshot.RecentEvents, item => item.Code == "FAULT_CLEANUP_COMPLETED");
    }

    [Fact]
    public async Task NonCancellationExceptionAfterTokenCancellationUsesCancellationPathOnly()
    {
        using var coordinator = new ObservationRunCoordinator();
        using var cancellation = new CancellationTokenSource();
        var runner = new FakeRunner
        {
            ThrowAt = ObservationStage.StartQhyPhotometry,
            BeforeThrow = cancellation.Cancel,
        };

        await coordinator.StartAsync(CreatePlan(), runner, cancellation.Token);

        Assert.Equal(ObservationRunState.Cancelled, coordinator.Snapshot.State);
        Assert.Equal(1, runner.CancelledCount);
        Assert.Equal(0, runner.FaultedCount);
        Assert.DoesNotContain(coordinator.Snapshot.RecentEvents, item => item.Code == "RUN_FAULTED");
    }

    [Fact]
    public async Task CancellationDuringFaultRecoveryStopsBeforeNextRecoverySegment()
    {
        using var coordinator = new ObservationRunCoordinator();
        var runner = new FakeRunner
        {
            ThrowAt = ObservationStage.StartQhyPhotometry,
            BlockFaultRecoveryBetweenSegments = true,
        };

        var run = coordinator.StartAsync(CreatePlan(), runner);
        await runner.FaultRecoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, runner.FaultRecoverySegmentCount);

        coordinator.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ObservationRunState.Cancelled, coordinator.Snapshot.State);
        Assert.Equal(1, runner.FaultedCount);
        Assert.Equal(1, runner.CancelledCount);
        Assert.Equal(1, runner.FaultRecoverySegmentCount);
    }

    [Fact]
    public async Task ConcurrentCheckpointsJoinOnePauseEpochAndBothResume()
    {
        using var coordinator = new ObservationRunCoordinator();
        var runner = new ConcurrentCheckpointRunner();
        var run = coordinator.StartAsync(CreatePlan(), runner);
        await runner.StageReady.Task.WaitAsync(TimeSpan.FromSeconds(2));

        coordinator.RequestPause("concurrent checkpoint test");
        runner.AllowCheckpoints.TrySetResult();
        await WaitForStateAsync(coordinator, ObservationRunState.Paused);

        Assert.Equal(1, runner.PausedCount);
        Assert.True(coordinator.Resume());
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, runner.CompletedCheckpoints);
        Assert.Equal(ObservationRunState.Completed, coordinator.Snapshot.State);
    }

    [Fact]
    public async Task CancellationNotificationsRemainInStateOrder()
    {
        using var coordinator = new ObservationRunCoordinator();
        var runner = new FakeRunner { FailOnceAt = ObservationStage.AcquireG3SlitField };
        var states = new List<ObservationRunState>();
        coordinator.SnapshotChanged += (_, changed) => states.Add(changed.State);
        var run = coordinator.StartAsync(CreatePlan(), runner);
        await WaitForStateAsync(coordinator, ObservationRunState.PausedNeedsAttention);

        coordinator.Cancel();
        await run;

        var cancelling = states.LastIndexOf(ObservationRunState.Cancelling);
        var cancelled = states.LastIndexOf(ObservationRunState.Cancelled);
        Assert.True(cancelling >= 0);
        Assert.True(cancelled > cancelling);
        Assert.Equal(ObservationRunState.Cancelled, states[^1]);
    }

    [Fact]
    public async Task TakeoverRequestedWhilePausedReleasesDevicesWithoutResume()
    {
        using var coordinator = new ObservationRunCoordinator();
        var runner = new FakeRunner { FailOnceAt = ObservationStage.AcquireG3SlitField };
        var run = coordinator.StartAsync(CreatePlan(), runner);
        await WaitForStateAsync(coordinator, ObservationRunState.PausedNeedsAttention);

        coordinator.RequestTakeover("operator needs direct control");
        await WaitForStateAsync(coordinator, ObservationRunState.ManualTakeover);

        Assert.Equal(1, runner.TakeoverCount);
        coordinator.Cancel();
        await run;
    }

    [Fact]
    public async Task CompletedIsInvisibleUntilDurableCommitterAcknowledgesExactSnapshot()
    {
        using var coordinator = new ObservationRunCoordinator();
        var runner = new BlockingCompletionRunner();
        var states = new List<ObservationRunState>();
        coordinator.SnapshotChanged += (_, changed) => states.Add(changed.State);

        var run = coordinator.StartAsync(CreatePlan(), runner);
        await runner.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(ObservationRunState.Finalizing, coordinator.Snapshot.State);
        Assert.DoesNotContain(ObservationRunState.Completed, states);

        runner.AllowCommit.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(ObservationRunState.Completed, coordinator.Snapshot.State);
        Assert.Same(runner.CommittedSnapshot, coordinator.Snapshot);
        Assert.Equal(ObservationRunState.Finalizing, states[^2]);
        Assert.Equal(ObservationRunState.Completed, states[^1]);
    }

    [Fact]
    public async Task FinalManifestFailureFaultsWithoutEverPublishingCompleted()
    {
        using var coordinator = new ObservationRunCoordinator();
        var runner = new FailingCompletionRunner();
        var states = new List<ObservationRunState>();
        coordinator.SnapshotChanged += (_, changed) => states.Add(changed.State);

        await coordinator.StartAsync(CreatePlan(), runner);

        Assert.Equal(ObservationRunState.Faulted, coordinator.Snapshot.State);
        Assert.Contains("synthetic manifest fsync failure", coordinator.Snapshot.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ObservationRunState.Finalizing, states);
        Assert.DoesNotContain(ObservationRunState.Completed, states);
    }

    [Fact]
    public async Task CancellationBeforeFinalCommitPointWinsAndNeverCreatesCompletedSnapshot()
    {
        using var coordinator = new ObservationRunCoordinator();
        var runner = new BlockingCompletionRunner();
        var states = new List<ObservationRunState>();
        coordinator.SnapshotChanged += (_, changed) => states.Add(changed.State);

        var run = coordinator.StartAsync(CreatePlan(), runner);
        await runner.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        coordinator.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(ObservationRunState.Cancelled, coordinator.Snapshot.State);
        Assert.Null(runner.CommittedSnapshot);
        Assert.Contains(ObservationRunState.Cancelling, states);
        Assert.DoesNotContain(ObservationRunState.Completed, states);
    }

    [Fact]
    public async Task CancellationAfterDurableCommitPointCannotOverwriteCompletedOutcome()
    {
        using var coordinator = new ObservationRunCoordinator();
        var runner = new CommitPointBlockingRunner();
        var states = new List<ObservationRunState>();
        coordinator.SnapshotChanged += (_, changed) => states.Add(changed.State);

        var run = coordinator.StartAsync(CreatePlan(), runner);
        await runner.CommitPointReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
        coordinator.Cancel();

        Assert.Equal(ObservationRunState.Finalizing, coordinator.Snapshot.State);
        Assert.DoesNotContain(ObservationRunState.Cancelling, states);

        runner.AllowAcknowledgement.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(ObservationRunState.Completed, coordinator.Snapshot.State);
        Assert.Equal(ObservationRunState.Completed, states[^1]);
        Assert.DoesNotContain(ObservationRunState.Cancelled, states);
    }

    private static ObservationPlan CreatePlan()
    {
        var start = DateTimeOffset.UtcNow;
        var target = TargetAtZenith(start, 33.37583333333333, 120.41666666666667);
        return new ObservationPlan(
            "test-run",
            "test-setup",
            target,
            new ObservatorySite(33.37583333333333, 120.41666666666667, 0),
            start,
            TimeSpan.FromMinutes(1),
            new HorizonPolicy(40, 5, 2, TimeSpan.FromSeconds(10)),
            new MotionLimits(),
            "ATR585M-test",
            "c11+ccdt67+slit+2210",
            "QHYminiCam8M-test");
    }

    private static EquatorialTarget TargetAtZenith(DateTimeOffset utc, double latitude, double longitude)
    {
        var jd = utc.ToUnixTimeMilliseconds() / 86_400_000d + 2_440_587.5;
        var t = (jd - 2_451_545d) / 36_525d;
        var gmst = 280.46061837 + 360.98564736629 * (jd - 2_451_545d) + 0.000387933 * t * t - t * t * t / 38_710_000d;
        var ra = ((gmst + longitude) % 360 + 360) % 360;
        return new EquatorialTarget("Zenith test", "TEST", ra, latitude);
    }

    private static async Task WaitForStateAsync(ObservationRunCoordinator coordinator, ObservationRunState state)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (coordinator.Snapshot.State != state && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.Equal(state, coordinator.Snapshot.State);
    }

    private abstract class AcknowledgingTestRunner : ObservationStageRunnerBase, IObservationRunCompletionCommitter
    {
        public virtual Task<ObservationSnapshot> CommitCompletionAsync(
            Func<ObservationSnapshot> createCompletedSnapshot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(createCompletedSnapshot());
        }
    }

    private sealed class NonAcknowledgingRunner : ObservationStageRunnerBase
    {
        public override Task<StageResult> ExecuteStageAsync(
            ObservationStage stage,
            ObservationContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(new StageResult(GateResult.Pass("PASS", "passed")));
    }

    private sealed class FakeRunner : AcknowledgingTestRunner
    {
        private bool failed;
        public List<ObservationStage> Executed { get; } = new();
        public ObservationStage? FailOnceAt { get; init; }
        public ObservationStage? BlockAt { get; init; }
        public ObservationStage? ThrowAt { get; init; }
        public Action? BeforeThrow { get; init; }
        public bool BlockFaultRecoveryBetweenSegments { get; init; }
        public Exception? PauseFailure { get; init; }
        public TaskCompletionSource BlockEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBlock { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FaultRecoveryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PausedCount { get; private set; }
        public int RevalidationCount { get; private set; }
        public int CancelledCount { get; private set; }
        public int FaultedCount { get; private set; }
        public int FaultRecoverySegmentCount { get; private set; }
        public int TakeoverCount { get; private set; }

        public override async Task<StageResult> ExecuteStageAsync(ObservationStage stage, ObservationContext context, CancellationToken cancellationToken)
        {
            Executed.Add(stage);
            if (stage == ThrowAt)
            {
                BeforeThrow?.Invoke();
                throw new InvalidOperationException("synthetic stage exception");
            }
            if (stage == BlockAt)
            {
                BlockEntered.TrySetResult();
                await ReleaseBlock.Task.WaitAsync(cancellationToken);
            }
            if (!failed && stage == FailOnceAt)
            {
                failed = true;
                return new StageResult(GateResult.Fail("TEST_FAILURE", "test failure"));
            }
            return new StageResult(GateResult.Pass("PASS", "passed"));
        }

        public override Task OnPausedAsync(ObservationContext context, CancellationToken cancellationToken)
        {
            PausedCount++;
            return PauseFailure is null
                ? Task.CompletedTask
                : Task.FromException(PauseFailure);
        }

        public override Task<GateResult> RevalidateAsync(ObservationContext context, CancellationToken cancellationToken)
        {
            RevalidationCount++;
            return Task.FromResult(GateResult.Pass("PASS", "revalidated"));
        }

        public override Task OnCancelledAsync(ObservationContext context, CancellationToken cancellationToken)
        {
            CancelledCount++;
            return Task.CompletedTask;
        }

        public override async Task OnFaultedAsync(ObservationContext context, Exception cause, CancellationToken cancellationToken)
        {
            FaultedCount++;
            if (!BlockFaultRecoveryBetweenSegments) return;

            cancellationToken.ThrowIfCancellationRequested();
            FaultRecoverySegmentCount++;
            FaultRecoveryEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            FaultRecoverySegmentCount++;
        }

        public override Task OnTakeoverAsync(ObservationContext context, CancellationToken cancellationToken)
        {
            TakeoverCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class CheckpointRunner : AcknowledgingTestRunner
    {
        public TaskCompletionSource FirstActionFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowCheckpoint { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ActionCount { get; private set; }

        public override async Task<StageResult> ExecuteStageAsync(ObservationStage stage, ObservationContext context, CancellationToken cancellationToken)
        {
            if (stage == ObservationStage.RunScienceBlock)
            {
                ActionCount++;
                FirstActionFinished.TrySetResult();
                await AllowCheckpoint.Task.WaitAsync(cancellationToken);
                await context.CheckpointAsync(cancellationToken);
                ActionCount++;
            }
            return new StageResult(GateResult.Pass("PASS", "passed"));
        }
    }

    private sealed class ConcurrentCheckpointRunner : AcknowledgingTestRunner
    {
        private int completedCheckpoints;
        public TaskCompletionSource StageReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowCheckpoints { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PausedCount { get; private set; }
        public int CompletedCheckpoints => Volatile.Read(ref completedCheckpoints);

        public override async Task<StageResult> ExecuteStageAsync(
            ObservationStage stage,
            ObservationContext context,
            CancellationToken cancellationToken)
        {
            if (stage == ObservationStage.RunScienceBlock)
            {
                StageReady.TrySetResult();
                await AllowCheckpoints.Task.WaitAsync(cancellationToken);
                var first = CompleteCheckpointAsync(context, cancellationToken);
                var second = CompleteCheckpointAsync(context, cancellationToken);
                await Task.WhenAll(first, second);
            }
            return new StageResult(GateResult.Pass("PASS", "passed"));
        }

        public override Task OnPausedAsync(ObservationContext context, CancellationToken cancellationToken)
        {
            PausedCount++;
            return Task.CompletedTask;
        }

        private async Task CompleteCheckpointAsync(ObservationContext context, CancellationToken cancellationToken)
        {
            await context.CheckpointAsync(cancellationToken);
            Interlocked.Increment(ref completedCheckpoints);
        }
    }

    private sealed class BlockingCompletionRunner : ObservationStageRunnerBase, IObservationRunCompletionCommitter
    {
        public TaskCompletionSource CommitEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowCommit { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ObservationSnapshot? CommittedSnapshot { get; private set; }

        public override Task<StageResult> ExecuteStageAsync(
            ObservationStage stage,
            ObservationContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(new StageResult(GateResult.Pass("PASS", "passed")));

        public async Task<ObservationSnapshot> CommitCompletionAsync(
            Func<ObservationSnapshot> createCompletedSnapshot,
            CancellationToken cancellationToken)
        {
            CommitEntered.TrySetResult();
            await AllowCommit.Task.WaitAsync(cancellationToken);
            CommittedSnapshot = createCompletedSnapshot();
            return CommittedSnapshot;
        }
    }

    private sealed class FailingCompletionRunner : ObservationStageRunnerBase, IObservationRunCompletionCommitter
    {
        public override Task<StageResult> ExecuteStageAsync(
            ObservationStage stage,
            ObservationContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(new StageResult(GateResult.Pass("PASS", "passed")));

        public Task<ObservationSnapshot> CommitCompletionAsync(
            Func<ObservationSnapshot> createCompletedSnapshot,
            CancellationToken cancellationToken) =>
            Task.FromException<ObservationSnapshot>(
                new IOException("synthetic manifest fsync failure"));
    }

    private sealed class CommitPointBlockingRunner : ObservationStageRunnerBase, IObservationRunCompletionCommitter
    {
        public TaskCompletionSource CommitPointReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowAcknowledgement { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<StageResult> ExecuteStageAsync(
            ObservationStage stage,
            ObservationContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(new StageResult(GateResult.Pass("PASS", "passed")));

        public async Task<ObservationSnapshot> CommitCompletionAsync(
            Func<ObservationSnapshot> createCompletedSnapshot,
            CancellationToken cancellationToken)
        {
            var committed = createCompletedSnapshot();
            CommitPointReached.TrySetResult();
            await AllowAcknowledgement.Task;
            return committed;
        }
    }
}
