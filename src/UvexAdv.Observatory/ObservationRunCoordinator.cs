namespace UvexAdv.Observatory;

public interface IObservationStageRunner
{
    Task<StageResult> ExecuteStageAsync(ObservationStage stage, ObservationContext context, CancellationToken cancellationToken);
    Task<GateResult> RevalidateAsync(ObservationContext context, CancellationToken cancellationToken);
    Task OnPausedAsync(ObservationContext context, CancellationToken cancellationToken);
    Task OnResumingAsync(ObservationContext context, CancellationToken cancellationToken);
    Task OnTakeoverAsync(ObservationContext context, CancellationToken cancellationToken);
    Task OnCancelledAsync(ObservationContext context, CancellationToken cancellationToken);
    Task OnFaultedAsync(ObservationContext context, Exception cause, CancellationToken cancellationToken);
}

/// <summary>
/// Mandatory two-phase completion boundary for coordinator runners. The
/// callback must be invoked only after all earlier evidence, gate,
/// and counter writes have drained. Returning acknowledges that the exact
/// completed snapshot has been durably committed.
/// </summary>
public interface IObservationRunCompletionCommitter
{
    Task<ObservationSnapshot> CommitCompletionAsync(
        Func<ObservationSnapshot> createCompletedSnapshot,
        CancellationToken cancellationToken);
}

public abstract class ObservationStageRunnerBase : IObservationStageRunner
{
    public abstract Task<StageResult> ExecuteStageAsync(ObservationStage stage, ObservationContext context, CancellationToken cancellationToken);

    public virtual Task<GateResult> RevalidateAsync(ObservationContext context, CancellationToken cancellationToken) =>
        Task.FromResult(GateResult.Pass("REVALIDATED", "All stale gates were revalidated."));

    public virtual Task OnPausedAsync(ObservationContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task OnResumingAsync(ObservationContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task OnTakeoverAsync(ObservationContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task OnCancelledAsync(ObservationContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task OnFaultedAsync(ObservationContext context, Exception cause, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class ObservationRunCoordinator : IDisposable
{
    public static IReadOnlyList<ObservationStage> Stages { get; } = Enum.GetValues<ObservationStage>();

    private readonly object sync = new();
    private readonly SemaphoreSlim pauseEpochGate = new(1, 1);
    private readonly List<ObservationEvent> events = new();
    private CancellationTokenSource? runCancellation;
    private TaskCompletionSource<bool>? resumeSignal;
    private Task? activeRun;
    private ObservationSnapshot snapshot = ObservationSnapshot.Idle;
    private bool pauseRequested;
    private bool takeoverRequested;
    private string? requestedPauseReason;
    private bool completionCommitPointReached;
    private bool disposed;

    public event EventHandler<ObservationSnapshot>? SnapshotChanged;

    public ObservationSnapshot Snapshot
    {
        get { lock (sync) return snapshot; }
    }

    public Task StartAsync(ObservationPlan plan, IObservationStageRunner runner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(runner);
        if (runner is not IObservationRunCompletionCommitter)
        {
            throw new ArgumentException(
                "An observation runner must provide a durable completion committer; Completed cannot be self-acknowledged.",
                nameof(runner));
        }
        var validationIssues = plan.Validate();
        if (validationIssues.Count > 0)
        {
            throw new ArgumentException(
                $"Observation plan is invalid and cannot be repaired through Resume: {string.Join(" ", validationIssues)}",
                nameof(plan));
        }

        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (activeRun is { IsCompleted: false }) throw new InvalidOperationException("An observation is already running.");
            runCancellation?.Dispose();
            runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pauseRequested = false;
            takeoverRequested = false;
            requestedPauseReason = null;
            completionCommitPointReached = false;
            resumeSignal = null;
            events.Clear();
            activeRun = RunAsync(plan, runner, runCancellation.Token);
            return activeRun;
        }
    }

    public void RequestPause(string reason = "Operator requested pause.")
    {
        EventHandler<ObservationSnapshot>? handler = null;
        ObservationSnapshot? published = null;
        lock (sync)
        {
            if (!CanControl(snapshot.State)) return;
            pauseRequested = true;
            requestedPauseReason = string.IsNullOrWhiteSpace(reason) ? "Operator requested pause." : reason;
            if (snapshot.State == ObservationRunState.RunningAuto)
            {
                PublishLocked(ObservationRunState.PauseRequested, snapshot.CurrentStage, snapshot.NextStage, "Pause requested; waiting for the current bounded action to finish.", requestedPauseReason, "PAUSE_REQUESTED");
                handler = SnapshotChanged;
                published = snapshot;
            }
        }
        if (published is not null) handler?.Invoke(this, published);
    }

    public void RequestTakeover(string reason = "Operator requested manual takeover.")
    {
        EventHandler<ObservationSnapshot>? handler = null;
        ObservationSnapshot? published = null;
        TaskCompletionSource<bool>? wakePauseEpoch = null;
        lock (sync)
        {
            if (!CanControl(snapshot.State)) return;
            takeoverRequested = true;
            pauseRequested = true;
            requestedPauseReason = string.IsNullOrWhiteSpace(reason) ? "Operator requested manual takeover." : reason;
            if (snapshot.State == ObservationRunState.RunningAuto)
            {
                PublishLocked(ObservationRunState.PauseRequested, snapshot.CurrentStage, snapshot.NextStage, "Manual takeover requested; waiting for the current bounded action to finish.", requestedPauseReason, "TAKEOVER_REQUESTED");
                handler = SnapshotChanged;
                published = snapshot;
            }
            else if (snapshot.State is ObservationRunState.Paused or ObservationRunState.PausedNeedsAttention)
            {
                PublishLocked(
                    ObservationRunState.PauseRequested,
                    snapshot.CurrentStage,
                    snapshot.NextStage,
                    "Manual takeover requested; releasing coordinated device ownership.",
                    requestedPauseReason,
                    "TAKEOVER_TRANSITION_REQUESTED");
                handler = SnapshotChanged;
                published = snapshot;
                wakePauseEpoch = resumeSignal;
            }
        }
        if (published is not null) handler?.Invoke(this, published);
        wakePauseEpoch?.TrySetResult(true);
    }

    public bool Resume()
    {
        TaskCompletionSource<bool>? signal;
        lock (sync)
        {
            if (snapshot.State is not (ObservationRunState.Paused or ObservationRunState.PausedNeedsAttention or ObservationRunState.ManualTakeover)) return false;
            pauseRequested = false;
            takeoverRequested = false;
            requestedPauseReason = null;
            signal = resumeSignal;
        }
        return signal?.TrySetResult(true) == true;
    }

    public void Cancel()
    {
        EventHandler<ObservationSnapshot>? handler;
        ObservationSnapshot? published;
        CancellationTokenSource? cancellation;
        lock (sync)
        {
            // Once the durable completion commit point has been crossed, the
            // outcome is already irrevocably Completed. A cancellation that won
            // the lock before that point still cancels the final write and wins.
            if (completionCommitPointReached || !CanCancel(snapshot.State)) return;
            PublishLocked(ObservationRunState.Cancelling, snapshot.CurrentStage, snapshot.NextStage, "Cancellation requested.", snapshot.PauseReason, "CANCEL_REQUESTED");
            resumeSignal?.TrySetCanceled();
            handler = SnapshotChanged;
            published = snapshot;
            cancellation = runCancellation;
        }
        handler?.Invoke(this, published);
        cancellation?.Cancel();
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            runCancellation?.Cancel();
            resumeSignal?.TrySetCanceled();
            runCancellation?.Dispose();
        }
    }

    private async Task RunAsync(ObservationPlan plan, IObservationStageRunner runner, CancellationToken cancellationToken)
    {
        var context = new ObservationContext(plan);
        var stageIndex = 0;
        try
        {
            SetState(plan.ObservationRunId, ObservationRunState.Validating, ObservationStage.ValidateNightSetup, Stages.ElementAtOrDefault(1), "Validating observation plan.", null, "PLAN_VALIDATING", 0);
            var horizonGate = HorizonCalculator.Evaluate(plan).ToGateResult();
            if (horizonGate.Disposition != GateDisposition.Passed)
            {
                await PauseForAttentionAsync(plan, runner, context, ObservationStage.ValidateNightSetup, horizonGate.Message, horizonGate.Code, cancellationToken).ConfigureAwait(false);
            }

            while (stageIndex < Stages.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stage = Stages[stageIndex];
                await RunCheckpointAsync(plan, runner, context, stage, stageIndex, cancellationToken).ConfigureAwait(false);
                context.SetCheckpoint(token => RunCheckpointAsync(plan, runner, context, stage, stageIndex, token));
                SetState(plan.ObservationRunId, ObservationRunState.RunningAuto, stage, Stages.ElementAtOrDefault(stageIndex + 1), $"Running {stage}.", null, "STAGE_STARTED", stageIndex);

                var result = await runner.ExecuteStageAsync(stage, context, cancellationToken).ConfigureAwait(false);
                AddEvent(ObservationRunState.RunningAuto, stage, result.Gate.Code, result.Gate.Message, result.EvidencePath);

                if (!result.CanAdvance)
                {
                    await PauseForAttentionAsync(plan, runner, context, stage, result.Gate.Message, result.Gate.Code, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                stageIndex++;
                await RunCheckpointAsync(plan, runner, context, stageIndex < Stages.Count ? Stages[stageIndex] : null, stageIndex, cancellationToken).ConfigureAwait(false);
            }

            SetState(
                plan.ObservationRunId,
                ObservationRunState.Finalizing,
                null,
                null,
                "Finalizing the durable observation manifest.",
                null,
                "RUN_FINALIZING",
                Stages.Count);

            var committer = (IObservationRunCompletionCommitter)runner;
            ObservationSnapshot? preparedCompletion = null;
            var committedCompletion = await committer.CommitCompletionAsync(
                () => preparedCompletion = CreateCompletionSnapshotAtCommitPoint(
                    plan.ObservationRunId,
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
            if (preparedCompletion is null || !ReferenceEquals(preparedCompletion, committedCompletion))
            {
                throw new InvalidOperationException(
                    "The completion committer did not acknowledge the exact snapshot supplied at the durable commit point.");
            }
            PublishCommittedCompletion(committedCompletion, plan.ObservationRunId);
        }
        catch (OperationCanceledException)
        {
            await CompleteCancellationAsync(plan, runner, context, stageIndex).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Cancellation is authoritative even when the bounded action that
            // observed it surfaced a non-OCE (for example, an adapter wrapping
            // a cancelled transport wait). Never reinterpret that race as a
            // fault and authorize fault-return motion with a fresh token.
            if (cancellationToken.IsCancellationRequested)
            {
                await CompleteCancellationAsync(plan, runner, context, stageIndex).ConfigureAwait(false);
                return;
            }

            Exception? cleanupFailure = null;
            try
            {
                // Preserve the run token. A cancellation that arrives during a
                // bounded fault return must stop that return before its next
                // segment; using None here would create new motion authority.
                await runner.OnFaultedAsync(context, ex, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await CompleteCancellationAsync(plan, runner, context, stageIndex).ConfigureAwait(false);
                return;
            }
            catch (Exception cleanupException) { cleanupFailure = cleanupException; }

            if (cancellationToken.IsCancellationRequested)
            {
                await CompleteCancellationAsync(plan, runner, context, stageIndex).ConfigureAwait(false);
                return;
            }

            SetState(plan.ObservationRunId, ObservationRunState.Faulted, Stages.ElementAtOrDefault(stageIndex), null, ex.Message, null, "RUN_FAULTED", stageIndex);
            if (cleanupFailure is null)
            {
                AddEvent(ObservationRunState.Faulted, Stages.ElementAtOrDefault(stageIndex), "FAULT_CLEANUP_COMPLETED", "Fault cleanup completed; no new automated actions will be issued.");
            }
            else
            {
                AddEvent(
                    ObservationRunState.Faulted,
                    Stages.ElementAtOrDefault(stageIndex),
                    "FAULT_CLEANUP_FAILED",
                    $"Original fault: {ex.Message} Cleanup fault: {cleanupFailure.Message}");
            }
        }
    }

    private async Task CompleteCancellationAsync(
        ObservationPlan plan,
        IObservationStageRunner runner,
        ObservationContext context,
        int stageIndex)
    {
        try { await runner.OnCancelledAsync(context, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { AddEvent(ObservationRunState.Cancelling, null, "CANCEL_CLEANUP_FAILED", ex.Message); }
        SetState(plan.ObservationRunId, ObservationRunState.Cancelled, null, null, "Observation cancelled.", null, "RUN_CANCELLED", stageIndex);
    }

    private async Task PauseForAttentionAsync(
        ObservationPlan plan,
        IObservationStageRunner runner,
        ObservationContext context,
        ObservationStage stage,
        string reason,
        string code,
        CancellationToken cancellationToken)
    {
        lock (sync)
        {
            pauseRequested = true;
            requestedPauseReason = reason;
        }
        await EnterPauseAsync(plan, runner, context, stage, ObservationRunState.PausedNeedsAttention, reason, code, cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitForManualPauseIfNeededAsync(
        ObservationPlan plan,
        IObservationStageRunner runner,
        ObservationContext context,
        ObservationStage? stage,
        int completedStages,
        CancellationToken cancellationToken)
    {
        bool shouldPause;
        bool takeover;
        string reason;
        lock (sync)
        {
            shouldPause = pauseRequested;
            takeover = takeoverRequested;
            reason = requestedPauseReason ?? (takeover ? "Operator requested manual takeover." : "Operator requested pause.");
        }
        if (!shouldPause) return;

        await EnterPauseAsync(
            plan,
            runner,
            context,
            stage,
            takeover ? ObservationRunState.ManualTakeover : ObservationRunState.Paused,
            reason,
            takeover ? "MANUAL_TAKEOVER" : "RUN_PAUSED",
            cancellationToken,
            completedStages).ConfigureAwait(false);
    }

    private async Task RunCheckpointAsync(
        ObservationPlan plan,
        IObservationStageRunner runner,
        ObservationContext context,
        ObservationStage? stage,
        int completedStages,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await WaitForManualPauseIfNeededAsync(plan, runner, context, stage, completedStages, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // Re-evaluate from "now" before every physical action. Using the full
            // planned science duration at setup checkpoints is conservative and also
            // accounts for elapsed acquisition/settling overhead near the 40° wall.
            var protectedDuration = context.RemainingWorstCaseDuration ?? plan.PlannedDuration;
            var horizon = HorizonCalculator.Evaluate(plan with
            {
                PlannedStartUtc = DateTimeOffset.UtcNow,
                PlannedDuration = protectedDuration,
            }).ToGateResult();
            if (horizon.Disposition == GateDisposition.Passed) return;

            await PauseForAttentionAsync(
                plan,
                runner,
                context,
                stage ?? ObservationStage.ValidateNightSetup,
                horizon.Message,
                horizon.Code,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnterPauseAsync(
        ObservationPlan plan,
        IObservationStageRunner runner,
        ObservationContext context,
        ObservationStage? stage,
        ObservationRunState pausedState,
        string reason,
        string code,
        CancellationToken cancellationToken,
        int? completedStages = null)
    {
        await pauseEpochGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another concurrent checkpoint may have completed this pause epoch while
            // we waited for the gate. In that case it is already safe to continue.
            lock (sync)
            {
                if (!pauseRequested) return;
            }

            var nextPausedState = pausedState;
            var nextReason = reason;
            var nextCode = code;
            while (true)
            {
                bool takeover;
                lock (sync)
                {
                    takeover = takeoverRequested || nextPausedState == ObservationRunState.ManualTakeover;
                }

                try
                {
                    if (takeover)
                    {
                        await runner.OnTakeoverAsync(context, cancellationToken).ConfigureAwait(false);
                        nextPausedState = ObservationRunState.ManualTakeover;
                        nextCode = "MANUAL_TAKEOVER";
                    }
                    else
                    {
                        await runner.OnPausedAsync(context, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A pause/takeover hook is itself a safety action. If it
                    // cannot prove devices safe (for example, slit LED OFF
                    // readback is unavailable), preserve the run for inspection
                    // in PausedNeedsAttention instead of misclassifying the
                    // recoverable safety condition as an unrelated run fault.
                    nextPausedState = ObservationRunState.PausedNeedsAttention;
                    nextCode = takeover
                        ? "TAKEOVER_SAFETY_CLEANUP_FAILED"
                        : "PAUSE_SAFETY_CLEANUP_FAILED";
                    nextReason =
                        $"{(takeover ? "Takeover" : "Pause")} safety cleanup was not confirmed: {ex.Message}";
                    lock (sync)
                    {
                        pauseRequested = true;
                        takeoverRequested = false;
                        requestedPauseReason = nextReason;
                    }
                }

                TaskCompletionSource<bool> signal;
                EventHandler<ObservationSnapshot>? handler;
                ObservationSnapshot published;
                lock (sync)
                {
                    signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    resumeSignal = signal;
                    PublishLocked(nextPausedState, stage, stage, nextReason, nextReason, nextCode, completedStages ?? snapshot.CompletedStageCount);
                    handler = SnapshotChanged;
                    published = snapshot;
                }
                handler?.Invoke(this, published);

                await signal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                bool transitionToTakeover;
                lock (sync) transitionToTakeover = takeoverRequested;
                if (transitionToTakeover)
                {
                    lock (sync)
                    {
                        nextPausedState = ObservationRunState.ManualTakeover;
                        nextReason = requestedPauseReason ?? "Operator requested manual takeover.";
                        nextCode = "MANUAL_TAKEOVER";
                    }
                    continue;
                }
                SetState(plan.ObservationRunId, ObservationRunState.Validating, stage, stage, "Resume requested; revalidating all stale gates.", null, "RESUME_REVALIDATING", completedStages ?? snapshot.CompletedStageCount);

                var revalidation = await runner.RevalidateAsync(context, cancellationToken).ConfigureAwait(false);
                var horizon = revalidation.Disposition == GateDisposition.Passed
                    ? HorizonCalculator.Evaluate(plan with
                    {
                        PlannedStartUtc = DateTimeOffset.UtcNow,
                        PlannedDuration = context.RemainingWorstCaseDuration ?? plan.PlannedDuration,
                    }).ToGateResult()
                    : null;
                var failedGate = revalidation.Disposition != GateDisposition.Passed ? revalidation : horizon;
                if (failedGate is not null && failedGate.Disposition != GateDisposition.Passed)
                {
                    lock (sync)
                    {
                        pauseRequested = true;
                        takeoverRequested = false;
                        requestedPauseReason = failedGate.Message;
                    }
                    nextPausedState = ObservationRunState.PausedNeedsAttention;
                    nextReason = failedGate.Message;
                    nextCode = failedGate.Code;
                    continue;
                }

                bool pauseAgain;
                lock (sync) pauseAgain = pauseRequested;
                if (pauseAgain)
                {
                    lock (sync)
                    {
                        nextPausedState = takeoverRequested ? ObservationRunState.ManualTakeover : ObservationRunState.Paused;
                        nextReason = requestedPauseReason ?? "Operator requested pause during revalidation.";
                        nextCode = takeoverRequested ? "MANUAL_TAKEOVER" : "RUN_PAUSED";
                    }
                    continue;
                }

                await runner.OnResumingAsync(context, cancellationToken).ConfigureAwait(false);
                AddEvent(ObservationRunState.Validating, stage, "RUN_RESUMED", "Run resumed after stale gates passed.");
                return;
            }
        }
        finally
        {
            lock (sync) resumeSignal = null;
            pauseEpochGate.Release();
        }
    }

    private static bool CanControl(ObservationRunState state) => state is
        ObservationRunState.Validating or
        ObservationRunState.RunningAuto or
        ObservationRunState.PauseRequested or
        ObservationRunState.Paused or
        ObservationRunState.PausedNeedsAttention or
        ObservationRunState.ManualTakeover;

    private static bool CanCancel(ObservationRunState state) =>
        CanControl(state) || state == ObservationRunState.Finalizing;

    private ObservationSnapshot CreateCompletionSnapshotAtCommitPoint(
        string runId,
        CancellationToken cancellationToken)
    {
        lock (sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.State == ObservationRunState.Cancelling)
            {
                throw new OperationCanceledException(
                    "Cancellation won before the durable completion commit point.",
                    cancellationToken);
            }
            if (snapshot.State != ObservationRunState.Finalizing)
            {
                throw new InvalidOperationException(
                    $"A completion snapshot can only be prepared from Finalizing, not {snapshot.State}.");
            }
            if (completionCommitPointReached)
            {
                throw new InvalidOperationException("The observation completion commit point was already crossed.");
            }

            completionCommitPointReached = true;
            var evt = new ObservationEvent(
                DateTimeOffset.UtcNow,
                ObservationRunState.Completed,
                null,
                "RUN_COMPLETED",
                "Observation completed and its final manifest was durably committed.");
            var completedEvents = events.Append(evt).TakeLast(200).ToArray();
            return new ObservationSnapshot(
                runId,
                ObservationRunState.Completed,
                null,
                null,
                evt.Message,
                null,
                Stages.Count,
                Stages.Count,
                evt.TimestampUtc,
                completedEvents);
        }
    }

    private void PublishCommittedCompletion(ObservationSnapshot committed, string runId)
    {
        EventHandler<ObservationSnapshot>? handler;
        lock (sync)
        {
            if (!completionCommitPointReached ||
                committed.State != ObservationRunState.Completed ||
                !string.Equals(committed.ObservationRunId, runId, StringComparison.Ordinal) ||
                committed.CompletedStageCount != Stages.Count)
            {
                throw new InvalidOperationException(
                    "The completion committer did not acknowledge the exact terminal run identity and stage count.");
            }

            events.Clear();
            events.AddRange(committed.RecentEvents.TakeLast(200));
            snapshot = committed;
            handler = SnapshotChanged;
        }
        handler?.Invoke(this, committed);
    }

    private void SetState(
        string? runId,
        ObservationRunState state,
        ObservationStage? stage,
        ObservationStage? nextStage,
        string message,
        string? pauseReason,
        string eventCode,
        int completedStages)
    {
        EventHandler<ObservationSnapshot>? handler;
        ObservationSnapshot next;
        lock (sync)
        {
            var evt = new ObservationEvent(DateTimeOffset.UtcNow, state, stage, eventCode, message);
            events.Add(evt);
            TrimEvents();
            next = new ObservationSnapshot(runId, state, stage, nextStage, message, pauseReason, completedStages, Stages.Count, evt.TimestampUtc, events.ToArray());
            snapshot = next;
            handler = SnapshotChanged;
        }
        handler?.Invoke(this, next);
    }

    private void AddEvent(ObservationRunState state, ObservationStage? stage, string code, string message, string? evidencePath = null)
    {
        EventHandler<ObservationSnapshot>? handler;
        ObservationSnapshot next;
        lock (sync)
        {
            var evt = new ObservationEvent(DateTimeOffset.UtcNow, state, stage, code, message, evidencePath);
            events.Add(evt);
            TrimEvents();
            next = snapshot with { UpdatedUtc = evt.TimestampUtc, RecentEvents = events.ToArray() };
            snapshot = next;
            handler = SnapshotChanged;
        }
        handler?.Invoke(this, next);
    }

    private void PublishLocked(
        ObservationRunState state,
        ObservationStage? stage,
        ObservationStage? nextStage,
        string message,
        string? pauseReason,
        string code,
        int? completedStages = null)
    {
        var evt = new ObservationEvent(DateTimeOffset.UtcNow, state, stage, code, message);
        events.Add(evt);
        TrimEvents();
        snapshot = snapshot with
        {
            State = state,
            CurrentStage = stage,
            NextStage = nextStage,
            StatusMessage = message,
            PauseReason = pauseReason,
            CompletedStageCount = completedStages ?? snapshot.CompletedStageCount,
            UpdatedUtc = evt.TimestampUtc,
            RecentEvents = events.ToArray()
        };
    }

    private void TrimEvents()
    {
        const int maximumEvents = 200;
        if (events.Count > maximumEvents) events.RemoveRange(0, events.Count - maximumEvents);
    }
}
