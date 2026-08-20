using System.IO;
using System.Security.Cryptography;
using System.Threading.Channels;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal interface IObservationRunProvenanceSource
{
    ObservationRunLockedMetadata LockedMetadata { get; }
}

/// <summary>
/// Serializes UI/coordinator notifications into the durable Observatory
/// manifest. Completion is a two-phase operation: all earlier records drain,
/// then the exact Completed snapshot is atomically committed and acknowledged.
/// The first writer failure is permanent for the lifetime of the run.
/// </summary>
internal sealed class ObservationRunPersistenceSession : IAsyncDisposable
{
    private enum SessionState
    {
        Active,
        FinalizationQueued,
        CompletionCommitted,
        Failed,
        Completing,
        Disposed,
    }

    private sealed record PersistenceOperation(
        Func<CancellationToken, Task> Execute,
        Action? OnSucceeded = null,
        Action<Exception>? OnFailed = null,
        bool RestoreActiveOnCancellation = false);

    private readonly ObservationRunJournalStore store;
    private readonly Channel<PersistenceOperation> operations;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Action<Exception> onFailure;
    private readonly Task writerLoop;
    private readonly object stateSync = new();
    private Exception? failure;
    private ObservationSnapshot? committedCompletion;
    private SessionState state = SessionState.Active;
    private int completionRequested;
    private int disposed;

    private ObservationRunPersistenceSession(
        ObservationRunJournalStore store,
        Action<Exception> onFailure)
    {
        this.store = store;
        this.onFailure = onFailure;
        operations = Channel.CreateUnbounded<PersistenceOperation>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        writerLoop = RunWriterAsync(lifetime.Token);
    }

    public string ManifestPath => store.ManifestPath;

    public Exception? Failure => Volatile.Read(ref failure);

    public bool IsHealthy
    {
        get
        {
            lock (stateSync)
            {
                return Failure is null && state is
                    SessionState.Active or
                    SessionState.FinalizationQueued or
                    SessionState.CompletionCommitted;
            }
        }
    }

    public bool TryResume(Func<bool> resume)
    {
        ArgumentNullException.ThrowIfNull(resume);
        lock (stateSync)
        {
            // Linearize Resume against LatchFailure. If failure wins this lock,
            // Resume is rejected; if Resume wins, a later failure immediately
            // requests a new pause through the latched failure callback.
            if (Failure is not null || state != SessionState.Active) return false;
            return resume();
        }
    }

    public static async Task<ObservationRunPersistenceSession> CreateAsync(
        ObservationPlan plan,
        ObservationRunLockedMetadata lockedMetadata,
        Action<Exception> onFailure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(lockedMetadata);
        ArgumentNullException.ThrowIfNull(onFailure);
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UVEX-ADV",
            "observations",
            SanitizePathSegment(plan.ObservationRunId));
        var store = new ObservationRunJournalStore(Path.Combine(root, "manifest.json"));
        await store.InitializeAsync(plan, lockedMetadata, cancellationToken).ConfigureAwait(false);
        return new ObservationRunPersistenceSession(store, onFailure);
    }

    public bool PublishSnapshot(ObservationSnapshot snapshot, ObservationRunCounters counters)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (stateSync)
        {
            if (state == SessionState.CompletionCommitted &&
                snapshot.State == ObservationRunState.Completed &&
                ReferenceEquals(snapshot, committedCompletion))
            {
                // The coordinator publishes the exact already-committed snapshot
                // only after the final write acknowledgement.
                return true;
            }
            if (state == SessionState.FinalizationQueued &&
                snapshot.State == ObservationRunState.Cancelling)
            {
                // Cancellation is decided by the final operation's commit-point
                // callback. If it wins, the later Cancelled snapshot is persisted;
                // if the commit point wins, this transient state must not regress
                // the already-durable Completed manifest.
                return true;
            }
        }

        if (snapshot.State == ObservationRunState.Completed)
        {
            return RejectRecord(
                "A Completed snapshot was offered outside the durable completion acknowledgement path.");
        }
        var frozenSnapshot = snapshot with { RecentEvents = snapshot.RecentEvents.ToArray() };
        var frozenCounters = FreezeCounters(counters);
        return Enqueue(new PersistenceOperation(
            token => store.PublishSnapshotAsync(frozenSnapshot, frozenCounters, token)));
    }

    public bool PublishGate(ObservationStage stage, GateResult gate)
    {
        var frozenGate = gate with
        {
            Metrics = gate.Metrics?.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal),
        };
        return Enqueue(new PersistenceOperation(
            token => store.PublishGateAsync(stage, frozenGate, token)));
    }

    public bool PublishCounters(ObservationRunCounters counters)
    {
        var frozenCounters = FreezeCounters(counters);
        return Enqueue(new PersistenceOperation(
            token => store.PublishCountersAsync(frozenCounters, token)));
    }

    public bool PublishEvidence(
        string kind,
        string absolutePath,
        string? knownSha256 = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var frozenMetadata = metadata?.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        return Enqueue(new PersistenceOperation(async token =>
        {
            EvidenceReference evidence;
            if (string.IsNullOrWhiteSpace(knownSha256))
            {
                evidence = await ObservationManifestWriter.DescribeEvidenceAsync(
                    kind,
                    absolutePath,
                    frozenMetadata,
                    token).ConfigureAwait(false);
            }
            else
            {
                if (!Path.IsPathFullyQualified(absolutePath) || !File.Exists(absolutePath))
                {
                    throw new FileNotFoundException("Immutable observation evidence is missing.", absolutePath);
                }
                await using var stream = new FileStream(
                    absolutePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1 << 20,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var actualSha256 = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
                if (!string.Equals(
                    actualSha256.Replace("-", string.Empty, StringComparison.Ordinal),
                    knownSha256.Replace("-", string.Empty, StringComparison.Ordinal),
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException(
                        $"Immutable evidence SHA-256 mismatch for '{absolutePath}'. Expected {knownSha256}, actual {actualSha256}.");
                }
                evidence = new EvidenceReference(
                    kind,
                    Path.GetFullPath(absolutePath),
                    actualSha256,
                    File.GetLastWriteTimeUtc(absolutePath),
                    frozenMetadata);
            }
            await store.PublishEvidenceAsync(evidence, token).ConfigureAwait(false);
        }));
    }

    public async Task<ObservationSnapshot> CommitCompletionAsync(
        Func<ObservationSnapshot> createCompletedSnapshot,
        ObservationRunCounters counters,
        IReadOnlyDictionary<ObservationStage, GateResult> gates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(createCompletedSnapshot);
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(gates);
        var finalCounters = FreezeCounters(counters);
        var finalGates = gates.ToDictionary(
            pair => pair.Key,
            pair => pair.Value with
            {
                Metrics = pair.Value.Metrics?.ToDictionary(
                    metric => metric.Key,
                    metric => metric.Value,
                    StringComparer.Ordinal),
            });
        var completion = new TaskCompletionSource<ObservationSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ObservationSnapshot? candidate = null;

        var operation = new PersistenceOperation(
            async _ =>
            {
                // Cancellation is linearized before the coordinator's commit-point
                // callback. Once that callback returns, the single atomic terminal
                // write is deliberately shielded from late cancellation.
                cancellationToken.ThrowIfCancellationRequested();
                candidate = createCompletedSnapshot();
                if (candidate.State != ObservationRunState.Completed)
                {
                    throw new InvalidOperationException(
                        "The coordinator completion callback did not produce a Completed snapshot.");
                }
                await store.CommitCompletionAsync(
                    candidate,
                    finalCounters,
                    finalGates,
                    CancellationToken.None).ConfigureAwait(false);
                lock (stateSync)
                {
                    if (state != SessionState.FinalizationQueued)
                    {
                        throw new InvalidOperationException(
                            $"Completion committed while the persistence session was {state}.");
                    }
                    committedCompletion = candidate;
                    state = SessionState.CompletionCommitted;
                    completion.TrySetResult(candidate);
                }
            },
            OnFailed: exception =>
            {
                if (exception is OperationCanceledException cancelled)
                {
                    completion.TrySetCanceled(cancelled.CancellationToken);
                }
                else
                {
                    completion.TrySetException(exception);
                }
            },
            RestoreActiveOnCancellation: true);

        Exception? rejected = null;
        lock (stateSync)
        {
            if (Failure is { } existing)
            {
                rejected = new IOException(
                    "The observation manifest writer has already failed and cannot finalize.",
                    existing);
            }
            else if (state != SessionState.Active)
            {
                rejected = new InvalidOperationException(
                    $"The observation manifest cannot begin finalization while it is {state}.");
            }
            else
            {
                state = SessionState.FinalizationQueued;
                if (!operations.Writer.TryWrite(operation))
                {
                    rejected = new IOException(
                        "Observation manifest writer rejected the terminal commit before completion.");
                }
            }
        }

        if (rejected is not null)
        {
            LatchFailure(rejected);
            throw new IOException(
                $"Observation manifest persistence failed at '{ManifestPath}'.",
                Failure ?? rejected);
        }

        return await completion.Task.ConfigureAwait(false);
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref completionRequested, 1) == 0)
        {
            lock (stateSync)
            {
                if (state is not (SessionState.Failed or SessionState.Disposed))
                {
                    state = SessionState.Completing;
                }
                operations.Writer.TryComplete(Failure);
            }
        }
        await writerLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (Failure is not null)
        {
            throw new IOException(
                $"Observation manifest persistence failed at '{ManifestPath}'.",
                Failure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            await CompleteAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (stateSync) state = SessionState.Disposed;
            lifetime.Cancel();
            lifetime.Dispose();
        }
    }

    private bool Enqueue(PersistenceOperation operation)
    {
        Exception? rejected = null;
        lock (stateSync)
        {
            if (Failure is not null) return false;
            if (state != SessionState.Active)
            {
                rejected = new IOException(
                    $"Observation manifest rejected a late record while it was {state}.");
            }
            else if (!operations.Writer.TryWrite(operation))
            {
                rejected = new IOException(
                    "Observation manifest writer rejected a record before completion.");
            }
        }

        if (rejected is null) return true;
        LatchFailure(rejected);
        return false;
    }

    private bool RejectRecord(string message)
    {
        LatchFailure(new IOException(message));
        return false;
    }

    private async Task RunWriterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var operation in operations.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Failure is { } priorFailure)
                {
                    operation.OnFailed?.Invoke(priorFailure);
                    continue;
                }

                try
                {
                    await operation.Execute(cancellationToken).ConfigureAwait(false);
                    operation.OnSucceeded?.Invoke();
                }
                catch (OperationCanceledException ex) when (operation.RestoreActiveOnCancellation)
                {
                    lock (stateSync)
                    {
                        if (state == SessionState.FinalizationQueued) state = SessionState.Active;
                    }
                    operation.OnFailed?.Invoke(ex);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LatchFailure(ex);
                    operation.OnFailed?.Invoke(ex);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (Failure is not null)
        {
            // The channel is completed with the first latched failure. Buffered
            // operations are failed above; the terminal channel exception adds no
            // new information and must never replace that first cause.
            _ = ex;
        }
        catch (Exception ex)
        {
            LatchFailure(ex);
        }
    }

    private void LatchFailure(Exception exception)
    {
        lock (stateSync)
        {
            if (failure is not null) return;
            Volatile.Write(ref failure, exception);
            state = SessionState.Failed;
        }
        operations.Writer.TryComplete(exception);
        try { onFailure(exception); }
        catch
        {
            // Persistence failure is already represented by the first exception.
            // UI notification failures cannot replace it.
        }
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "unnamed-run" : sanitized;
    }

    private static ObservationRunCounters FreezeCounters(ObservationRunCounters counters) =>
        counters with
        {
            Additional = counters.Additional?.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal),
        };
}
