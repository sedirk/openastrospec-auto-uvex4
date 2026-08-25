using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UvexAdv.Qhy.Core;

public sealed class QhyJobCoordinator : IAsyncDisposable
{
    private readonly IQhyCameraAdapter adapter;
    private readonly QhyCoordinatorOptions options;
    private readonly QhyRunStore store;
    private readonly ConcurrentDictionary<Guid, JobExecution> jobs = new();
    private readonly ConcurrentDictionary<string, Guid> requestIndex = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, QhyPreview> latestPreviews = new();
    private readonly SemaphoreSlim cameraGate = new(1, 1);
    private readonly object activeGate = new();
    private Guid? activeJobId;

    public QhyJobCoordinator(IQhyCameraAdapter adapter, QhyCoordinatorOptions options)
    {
        this.adapter = adapter;
        this.options = options;
        if (string.IsNullOrWhiteSpace(options.ExpectedStableId))
        {
            throw new ArgumentException("QHY ExpectedStableId must be configured; ordinal camera selection is forbidden.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.ExpectedModel))
        {
            throw new ArgumentException("QHY ExpectedModel must be configured.", nameof(options));
        }

        store = new QhyRunStore(options.DataRoot);
    }

    public event Action<QhyJobSnapshot>? JobChanged;

    public QhyCameraStatus CameraStatus => adapter.Status;

    private DateTimeOffset UtcNow => options.TimeProvider.GetUtcNow();

    public IReadOnlyList<QhyJobSnapshot> RecentJobs(int count = 50) =>
        jobs.Values
            .Select(static execution => execution.GetSnapshot())
            .OrderByDescending(static snapshot => snapshot.CreatedUtc)
            .Take(Math.Clamp(count, 1, 200))
            .ToArray();

    public QhyJobSnapshot? GetJob(Guid id) => jobs.TryGetValue(id, out var execution) ? execution.GetSnapshot() : null;

    public QhyPreview? GetLatestPreview(Guid id) => latestPreviews.GetValueOrDefault(id);

    public QhyJobControlResponse StartAcquisition(AcquisitionJobRequest request)
    {
        Validate(request);
        var (execution, created) = CreateExecution(
            request.ObservationRunId,
            request.RequestedTarget,
            QhyJobKind.Acquisition,
            request.ClientRequestId,
            Fingerprint(request),
            request.TargetRightAscensionDegrees,
            request.TargetDeclinationDegrees,
            request.CoordinateEpoch,
            request.ControlLeaseSeconds);
        if (created)
        {
            execution.Worker = RunJobAsync(execution, cancellationToken => RunAcquisitionAsync(execution, request, cancellationToken));
        }
        return execution.GetControlResponse();
    }

    public QhyJobControlResponse StartPhotometry(PhotometryJobRequest request)
    {
        Validate(request);
        var (execution, created) = CreateExecution(
            request.ObservationRunId,
            request.RequestedTarget,
            QhyJobKind.Photometry,
            request.ClientRequestId,
            Fingerprint(request),
            request.TargetRightAscensionDegrees,
            request.TargetDeclinationDegrees,
            request.CoordinateEpoch,
            request.ControlLeaseSeconds);
        if (created)
        {
            execution.Worker = RunJobAsync(execution, cancellationToken => RunPhotometryAsync(execution, request, cancellationToken));
        }
        return execution.GetControlResponse();
    }

    public QhyJobSnapshot? FindByClientRequest(string observationRunId, QhyJobKind kind, string clientRequestId)
    {
        if (string.IsNullOrWhiteSpace(observationRunId) || string.IsNullOrWhiteSpace(clientRequestId)) return null;
        return requestIndex.TryGetValue(RequestKey(observationRunId, kind, clientRequestId), out var id)
            ? GetJob(id)
            : null;
    }

    public Task<QhyJobSnapshot> PauseAsync(Guid id, CancellationToken cancellationToken) =>
        PauseAsync(id, new QhyOwnerControlRequest(string.Empty, "legacy-anonymous"), cancellationToken);

    public async Task<QhyJobSnapshot> PauseAsync(
        Guid id,
        QhyOwnerControlRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateActor(request.Actor);
        var execution = RequireJob(id);
        await DemandOwnerAsync(execution, request.OwnerToken, "pause").ConfigureAwait(false);
        var snapshot = execution.MutateAuthorized(request.OwnerToken, current =>
        {
            if (current.State == QhyJobState.Running)
            {
                // Close the gate while holding the same lock that protects state. A
                // checkpoint can therefore never observe Pausing with an open gate.
                execution.PauseGate.Reset();
                return WithEvent(
                    current with { State = QhyJobState.Pausing },
                    "owner.pause",
                    $"Owner '{request.Actor}' requested pause; the in-flight frame will be retained.");
            }

            if (current.State is QhyJobState.Paused or QhyJobState.Pausing or QhyJobState.PausedNeedsAttention)
            {
                execution.PauseGate.Reset();
                return current;
            }

            throw new InvalidOperationException($"Job {id} cannot be paused from state {current.State}.");
        });
        await PersistAndPublishAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
        return snapshot;
    }

    public Task<QhyJobSnapshot> ResumeAsync(Guid id, CancellationToken cancellationToken) =>
        ResumeAsync(id, new QhyResumeRequest(string.Empty, Actor: "legacy-anonymous"), cancellationToken);

    public async Task<QhyJobSnapshot> ResumeAsync(
        Guid id,
        QhyResumeRequest request,
        CancellationToken cancellationToken)
    {
        ValidateActor(request.Actor);
        var execution = RequireJob(id);
        await DemandOwnerAsync(execution, request.OwnerToken, "resume").ConfigureAwait(false);
        var current = execution.GetSnapshot();
        if (current.State is not (QhyJobState.Paused or QhyJobState.PausedNeedsAttention or QhyJobState.Pausing))
        {
            throw new InvalidOperationException($"Job {id} cannot be resumed from state {current.State}.");
        }
        var leaseSeconds = request.LeaseSeconds ?? current.ControlLeaseSeconds;
        ValidateLease(leaseSeconds);

        try
        {
            await EnsureCameraConnectedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failed = execution.MutateAuthorized(request.OwnerToken, snapshot =>
            {
                if (snapshot.State is not (QhyJobState.Paused or QhyJobState.PausedNeedsAttention or QhyJobState.Pausing))
                {
                    return snapshot;
                }
                return WithEvent(
                    snapshot with
                    {
                        State = QhyJobState.PausedNeedsAttention,
                        Error = $"Resume revalidation failed: {ex.Message}",
                        AttentionReason = $"Resume revalidation failed: {ex.Message}",
                    },
                    "owner.resume-revalidation-failed",
                    $"Owner '{request.Actor}' resume was withheld because the camera identity/connection gate failed: {ex.Message}");
            });
            await PersistAndPublishAsync(failed, CancellationToken.None).ConfigureAwait(false);
            return failed;
        }

        var snapshot = execution.MutateAuthorized(request.OwnerToken, current =>
        {
            if (current.State is not (QhyJobState.Paused or QhyJobState.PausedNeedsAttention or QhyJobState.Pausing))
            {
                throw new InvalidOperationException($"Job {id} cannot be resumed from state {current.State}.");
            }

            // Open the gate under the state lock so a checkpoint cannot miss resume
            // or observe Running while still blocked on the old pause request.
            execution.PauseGate.Set();
            return WithEvent(
                current with
                {
                    State = QhyJobState.Running,
                    Error = null,
                    AttentionReason = null,
                    LeaseExpiresUtc = UtcNow.AddSeconds(leaseSeconds),
                    ControlLeaseSeconds = leaseSeconds,
                },
                "owner.resume-and-renew",
                $"Owner '{request.Actor}' atomically renewed the lease for {leaseSeconds} seconds and resumed automatic progression.");
        });
        await PersistAndPublishAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
        return snapshot;
    }

    public Task<QhyJobSnapshot> CancelAsync(Guid id, CancellationToken cancellationToken) =>
        CancelAsync(id, new QhyOwnerControlRequest(string.Empty, "legacy-anonymous"), cancellationToken);

    public async Task<QhyJobSnapshot> CancelAsync(
        Guid id,
        QhyOwnerControlRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateActor(request.Actor);
        var execution = RequireJob(id);
        await DemandOwnerAsync(execution, request.OwnerToken, "cancel").ConfigureAwait(false);
        var shouldCancel = false;
        var snapshot = execution.MutateAuthorized(request.OwnerToken, current =>
        {
            if (IsTerminal(current.State)) return current;
            if (current.State == QhyJobState.Cancelling) return current;
            shouldCancel = true;
            execution.PauseGate.Set();
            return WithEvent(
                current with { State = QhyJobState.Cancelling },
                "owner.cancel",
                $"Owner '{request.Actor}' requested cancellation.");
        });
        if (shouldCancel) execution.Cancellation.Cancel();
        // Device-control semantics must not depend on whether the HTTP caller remains
        // connected after the cancellation request has been accepted.
        await PersistAndPublishAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
        return snapshot;
    }

    public async Task<QhyJobSnapshot> RenewLeaseAsync(
        Guid id,
        QhyLeaseRenewalRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateActor(request.Actor);
        var execution = RequireJob(id);
        await DemandOwnerAsync(execution, request.OwnerToken, "lease renewal").ConfigureAwait(false);
        var snapshot = execution.MutateAuthorized(request.OwnerToken, current =>
        {
            if (IsTerminal(current.State) || current.State == QhyJobState.Cancelling)
            {
                throw new InvalidOperationException($"Job {id} is stopping or terminal and has no renewable control lease.");
            }
            var seconds = request.LeaseSeconds ?? current.ControlLeaseSeconds;
            ValidateLease(seconds);
            return WithEvent(
                current with
                {
                    LeaseExpiresUtc = UtcNow.AddSeconds(seconds),
                    ControlLeaseSeconds = seconds,
                },
                "owner.lease-renewed",
                $"Owner '{request.Actor}' renewed the control lease for {seconds} seconds without changing job state.");
        });
        await PersistAndPublishAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
        return snapshot;
    }

    public async Task<QhyJobSnapshot> TakeOverAsync(
        Guid id,
        OperatorTakeoverRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var execution = RequireJob(id);
        if (!request.Confirmed)
        {
            var denied = execution.Mutate(current => WithEvent(
                current,
                "operator.takeover-denied",
                "Rejected operator takeover because explicit confirmation was absent."));
            await PersistAndPublishAsync(denied, CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("Explicit confirmation is required for operator takeover.");
        }
        if (string.IsNullOrWhiteSpace(request.Operator) || string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new ArgumentException("Operator and reason are required for takeover.", nameof(request));
        }

        ValidateActor(request.Operator);
        if (request.Reason.Length > 512) throw new ArgumentException("Takeover reason is too long.", nameof(request));
        var shouldCancel = false;
        var stopping = execution.Mutate(current =>
        {
            if (IsTerminal(current.State))
            {
                throw new InvalidOperationException($"Terminal job {id} cannot be taken over.");
            }
            execution.PauseGate.Set();
            shouldCancel = true;
            return WithEvent(
                current with { State = QhyJobState.Cancelling },
                "operator.takeover-requested",
                $"Confirmed operator '{request.Operator}' requested takeover: {request.Reason}");
        });
        if (shouldCancel) execution.Cancellation.Cancel();
        await PersistAndPublishAsync(stopping, CancellationToken.None).ConfigureAwait(false);
        if (execution.Worker is not null)
        {
            try
            {
                await execution.Worker.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException("The QHY capture did not stop within 15 seconds; the service kept ownership and refused takeover.");
            }
        }

        await DisconnectCameraAsync(CancellationToken.None).ConfigureAwait(false);
        var snapshot = execution.Mutate(current =>
        {
            if (current.State is not (QhyJobState.Cancelled or QhyJobState.Cancelling))
            {
                throw new InvalidOperationException($"Takeover stop ended in unexpected state {current.State}; device ownership was not released.");
            }
            return WithEvent(
                current with { State = QhyJobState.TakenOver, CompletedUtc = UtcNow },
                "operator.takeover-completed",
                $"Device released to confirmed operator '{request.Operator}': {request.Reason}");
        });
        await PersistAndPublishAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
        return snapshot;
    }

    public async Task<QhyCameraStatus> ConnectCameraAsync(CancellationToken cancellationToken)
    {
        await EnsureCameraConnectedAsync(cancellationToken).ConfigureAwait(false);
        return adapter.Status;
    }

    public async Task<QhyCameraStatus> DisconnectCameraAsync(CancellationToken cancellationToken)
    {
        lock (activeGate)
        {
            if (activeJobId is { } id && jobs.TryGetValue(id, out var execution) && !IsTerminal(execution.GetSnapshot().State))
            {
                throw new InvalidOperationException($"QHY job {id} is still active; cancel it before releasing the camera.");
            }
        }

        await cameraGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await adapter.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            return adapter.Status;
        }
        finally
        {
            cameraGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var execution in jobs.Values)
        {
            execution.PauseGate.Set();
            execution.Cancellation.Cancel();
        }

        var workers = jobs.Values.Select(static execution => execution.Worker).Where(static worker => worker is not null).Cast<Task>();
        try
        {
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Adapter disposal below is the final bounded shutdown action.
        }

        await adapter.DisposeAsync().ConfigureAwait(false);
        cameraGate.Dispose();
        foreach (var execution in jobs.Values) execution.Dispose();
    }

    private (JobExecution Execution, bool Created) CreateExecution(
        string observationRunId,
        string requestedTarget,
        QhyJobKind kind,
        string? clientRequestId,
        string fingerprint,
        double? targetRightAscensionDegrees,
        double? targetDeclinationDegrees,
        string coordinateEpoch,
        int controlLeaseSeconds)
    {
        lock (activeGate)
        {
            var requestKey = string.IsNullOrWhiteSpace(clientRequestId)
                ? null
                : RequestKey(observationRunId, kind, clientRequestId);
            if (requestKey is not null && requestIndex.TryGetValue(requestKey, out var existingId) && jobs.TryGetValue(existingId, out var existing))
            {
                var existingSnapshot = existing.GetSnapshot();
                if (!string.Equals(existingSnapshot.ClientRequestFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Client request ID '{clientRequestId}' was already used with different QHY job parameters.");
                }
                return (existing, false);
            }

            if (activeJobId is { } currentId && jobs.TryGetValue(currentId, out var current) && !IsTerminal(current.GetSnapshot().State))
            {
                throw new InvalidOperationException($"QHY job {currentId} is already active; one service owns one camera handle.");
            }

            var id = Guid.NewGuid();
            var manifestPath = store.GetManifestPath(observationRunId, id);
            var createdUtc = UtcNow;
            var ownerToken = CreateOwnerToken();
            var snapshot = new QhyJobSnapshot(
                id,
                observationRunId,
                kind,
                QhyJobState.Queued,
                createdUtc,
                null,
                null,
                requestedTarget,
                options.ExpectedStableId,
                null,
                null,
                [],
                [new QhyJobEvent(DateTimeOffset.UtcNow, "job.created", $"{kind} job created.")],
                manifestPath,
                AcceptedFrameId: null,
                Revision: 1,
                TotalFrameCount: 0,
                TotalAcceptedFrameCount: 0,
                FrameIndexPath: store.GetFrameIndexPath(observationRunId, id),
                ClientRequestId: string.IsNullOrWhiteSpace(clientRequestId) ? null : clientRequestId,
                ClientRequestFingerprint: fingerprint,
                TargetRightAscensionDegrees: targetRightAscensionDegrees,
                TargetDeclinationDegrees: targetDeclinationDegrees,
                CoordinateEpoch: coordinateEpoch,
                ControlLeaseId: null,
                LeaseExpiresUtc: createdUtc.AddSeconds(controlLeaseSeconds),
                ControlLeaseSeconds: controlLeaseSeconds);
            var execution = new JobExecution(snapshot, ownerToken);
            jobs[id] = execution;
            if (requestKey is not null) requestIndex[requestKey] = id;
            activeJobId = id;
            execution.InitialPersistence = PersistAndPublishAsync(snapshot, CancellationToken.None);
            return (execution, true);
        }
    }

    private async Task RunJobAsync(JobExecution execution, Func<CancellationToken, Task> action)
    {
        try
        {
            await execution.InitialPersistence.ConfigureAwait(false);
            await EnsureCameraConnectedWithRecoveryAsync(execution, execution.Cancellation.Token).ConfigureAwait(false);
            await SetStateAsync(execution, QhyJobState.Running, "job.started", "Camera identity verified; automatic progression started.")
                .ConfigureAwait(false);
            await action(execution.Cancellation.Token).ConfigureAwait(false);
            await SetTerminalStateAsync(execution, QhyJobState.Completed, null, "job.completed", "Job completed.")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (execution.Cancellation.IsCancellationRequested)
        {
            await SetTerminalStateAsync(execution, QhyJobState.Cancelled, null, "job.cancelled", "Job cancelled; retained frames remain immutable.")
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SetTerminalStateAsync(execution, QhyJobState.Faulted, ex.Message, "job.faulted", ex.Message)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (activeGate)
            {
                if (activeJobId == execution.GetSnapshot().Id) activeJobId = null;
            }
        }
    }

    private async Task EnsureCameraConnectedWithRecoveryAsync(JobExecution execution, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await EnsureCameraConnectedAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await PauseForAttentionAsync(
                    execution,
                    $"Camera identity/connect gate failed: {ex.Message}",
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunAcquisitionAsync(
        JobExecution execution,
        AcquisitionJobRequest request,
        CancellationToken cancellationToken)
    {
        var thresholds = request.QualityThresholds ?? options.DefaultQualityThresholds;
        var sequence = 0;
        while (true)
        {
            for (var attempt = 0; attempt < request.MaximumAttempts; attempt++)
            {
                await CheckpointAsync(execution, cancellationToken).ConfigureAwait(false);
                var exposure = request.ExposureLadderSeconds[Math.Min(attempt, request.ExposureLadderSeconds.Count - 1)];
                var settings = CreateSettings(request, exposure);
                var (frame, metrics) = await CaptureWithRecoveryAsync(execution, settings, thresholds, null, cancellationToken)
                    .ConfigureAwait(false);
                sequence++;
                var storedFrame = await StoreFrameAsync(execution, frame, metrics, sequence, "ACQUISITION", cancellationToken).ConfigureAwait(false);
                await CheckpointAsync(execution, cancellationToken).ConfigureAwait(false);
                if (QhyFrameAnalyzer.PassesAcquisitionGate(metrics, thresholds))
                {
                    var accepted = execution.Mutate(current => WithEvent(
                        current with { AcceptedFrameId = storedFrame.FrameId },
                        "acquisition.frame-accepted",
                        $"Frame {storedFrame.FrameId:D} passed the bounded acquisition quality gate."));
                    await PersistAndPublishAsync(accepted, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            await PauseForAttentionAsync(
                execution,
                $"Acquisition quality gate failed after {request.MaximumAttempts} bounded attempts; no mount command was issued.",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunPhotometryAsync(
        JobExecution execution,
        PhotometryJobRequest request,
        CancellationToken cancellationToken)
    {
        var thresholds = request.QualityThresholds ?? options.DefaultQualityThresholds;
        var baselineStarFluxByFilter = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (var sequence = 1; sequence <= request.FrameCount; sequence++)
        {
            await CheckpointAsync(execution, cancellationToken).ConfigureAwait(false);
            var filterStep = EffectivePhotometryFilterStep(request, sequence);
            var settings = CreateSettings(request, filterStep);
            baselineStarFluxByFilter.TryGetValue(filterStep.FilterName, out var baselineStarFlux);
            var (frame, metrics) = await CaptureWithRecoveryAsync(
                    execution,
                    settings,
                    thresholds,
                    baselineStarFluxByFilter.ContainsKey(filterStep.FilterName) ? baselineStarFlux : null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (metrics.MedianStarFlux is { } measuredBaseline)
                baselineStarFluxByFilter.TryAdd(filterStep.FilterName, measuredBaseline);
            await StoreFrameAsync(execution, frame, metrics, sequence, $"PHOTOMETRY-{filterStep.FilterName}", cancellationToken).ConfigureAwait(false);
            await CheckpointAsync(execution, cancellationToken).ConfigureAwait(false);

            if (request.PauseOnQualityFailure && metrics.QualityFlags.Count > 0)
            {
                await PauseForAttentionAsync(
                    execution,
                    $"Photometry quality gate requested attention: {string.Join(", ", metrics.QualityFlags)}.",
                    cancellationToken).ConfigureAwait(false);
            }

            if (sequence < request.FrameCount)
            {
                // Cadence is exposure-start to exposure-start, not an additional
                // post-exposure delay. If exposure/readout already consumed the
                // interval, continue at the next safe frame boundary without trying
                // to issue overlapping or catch-up exposures.
                var nextStartUtc = frame.ExposureStartedUtc + TimeSpan.FromSeconds(request.CadenceSeconds);
                var remaining = nextStartUtc - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero)
                {
                    await DelayWithCheckpointsAsync(execution, remaining, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<(QhyFrame Frame, QhyFrameMetrics Metrics)> CaptureWithRecoveryAsync(
        JobExecution execution,
        QhyFrameSettings settings,
        QhyQualityThresholds thresholds,
        double? baselineStarFlux,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                var frame = await adapter.CaptureSingleFrameAsync(settings, cancellationToken).ConfigureAwait(false);
                var metrics = QhyFrameAnalyzer.Analyze(frame, thresholds, baselineStarFlux);
                return (frame, metrics);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await PauseForAttentionAsync(execution, $"Capture failed: {ex.Message}", cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<QhyFrameRecord> StoreFrameAsync(
        JobExecution execution,
        QhyFrame frame,
        QhyFrameMetrics metrics,
        int sequence,
        string role,
        CancellationToken cancellationToken)
    {
        var before = execution.GetSnapshot();
        var stored = await store.StoreFrameAsync(before, frame, metrics, sequence, role, cancellationToken).ConfigureAwait(false);
        latestPreviews[before.Id] = stored.Preview;
        var passedQualityGate = stored.Record.Metrics.QualityFlags.Count == 0;
        var snapshot = execution.Mutate(current => WithEvent(
            current with
            {
                Frames = [.. current.Frames.Append(stored.Record).TakeLast(32)],
                TotalFrameCount = current.TotalFrameCount + 1,
                TotalAcceptedFrameCount = current.TotalAcceptedFrameCount + (passedQualityGate ? 1 : 0),
                FrameIndexPath = current.FrameIndexPath ?? store.GetFrameIndexPath(current.ObservationRunId, current.Id),
                LastEvaluatedFrameId = stored.Record.FrameId,
                LastFramePassedQualityGate = passedQualityGate,
            },
            "frame.saved",
            $"{role} frame {sequence} saved; stars={metrics.DetectedStars}, saturation={metrics.SaturatedFraction:P3}."));
        await PersistAndPublishAsync(snapshot, cancellationToken).ConfigureAwait(false);
        return stored.Record;
    }

    private async Task PauseForAttentionAsync(JobExecution execution, string reason, CancellationToken cancellationToken)
    {
        var snapshot = execution.Mutate(current =>
        {
            if (execution.Cancellation.IsCancellationRequested || current.State == QhyJobState.Cancelling)
            {
                throw new OperationCanceledException(execution.Cancellation.Token);
            }
            if (IsTerminal(current.State))
            {
                throw new InvalidOperationException($"Terminal job {current.Id} cannot enter attention pause.");
            }
            execution.PauseGate.Reset();
            return WithEvent(
                current with { State = QhyJobState.PausedNeedsAttention, AttentionReason = reason, Error = reason },
                "job.needs-attention",
                reason);
        });
        await PersistAndPublishAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
        await CheckpointAsync(execution, cancellationToken).ConfigureAwait(false);
    }

    private async Task CheckpointAsync(JobExecution execution, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var checkpoint = execution.BeginCheckpoint(UtcNow, cancellationToken);
            if (checkpoint.Transitioned)
            {
                await PersistAndPublishAsync(checkpoint.Snapshot, CancellationToken.None).ConfigureAwait(false);
            }
            if (checkpoint.Authorized) return;
            await checkpoint.WaitTask.ConfigureAwait(false);
            // A wake-up is only a hint. Resume, lease expiry, cancellation and
            // takeover can race with Set(); always loop through the locked state
            // and lease checks before authorizing another frame or delay segment.
        }
    }

    public Task<QhyFilterWheelStatus> ReadFilterWheelStatusAsync(CancellationToken cancellationToken)
    {
        if (!adapter.Status.Connected)
        {
            throw new InvalidOperationException(
                "QHY camera is disconnected; connect the exact configured camera before reading the physical filter-wheel position.");
        }

        return adapter.ReadFilterWheelStatusAsync(cancellationToken);
    }

    public async Task<QhyFilterWheelStatus> SelectFilterAsync(
        string filterName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filterName))
        {
            throw new ArgumentException("An explicit QHY filter name is required.", nameof(filterName));
        }

        lock (activeGate)
        {
            if (activeJobId is { } id && jobs.TryGetValue(id, out var execution) && !IsTerminal(execution.GetSnapshot().State))
            {
                throw new InvalidOperationException(
                    $"QHY job {id} is still active; the integrated filter wheel cannot be moved independently.");
            }
        }

        await EnsureCameraConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await adapter.SelectFilterAsync(filterName.Trim(), cancellationToken).ConfigureAwait(false);
    }

    private async Task DelayWithCheckpointsAsync(JobExecution execution, TimeSpan delay, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + delay;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await CheckpointAsync(execution, cancellationToken).ConfigureAwait(false);
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining > TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : remaining, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task EnsureCameraConnectedAsync(CancellationToken cancellationToken)
    {
        await cameraGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (adapter.Status.Connected)
            {
                ValidateIdentity(adapter.Status.Identity ?? throw new InvalidOperationException("Connected QHY adapter did not report identity."));
                if (string.IsNullOrWhiteSpace(adapter.Status.LastError)) return;
                await adapter.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            }

            var identity = await adapter.ConnectExactAsync(options.ExpectedStableId, options.ExpectedModel, cancellationToken)
                .ConfigureAwait(false);
            ValidateIdentity(identity);
        }
        finally
        {
            cameraGate.Release();
        }
    }

    private void ValidateIdentity(QhyCameraIdentity identity)
    {
        if (!string.Equals(identity.StableId, options.ExpectedStableId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"QHY identity mismatch. Expected exact stable ID '{options.ExpectedStableId}', received '{identity.StableId}'.");
        }

        if (!string.Equals(identity.Model, options.ExpectedModel, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"QHY model mismatch. Expected '{options.ExpectedModel}', received '{identity.Model}'.");
        }
    }

    private async Task SetStateAsync(JobExecution execution, QhyJobState state, string eventKind, string message)
    {
        var snapshot = execution.Mutate(current =>
        {
            if (execution.Cancellation.IsCancellationRequested || current.State == QhyJobState.Cancelling)
            {
                throw new OperationCanceledException(execution.Cancellation.Token);
            }
            if (IsTerminal(current.State)) return current;
            return WithEvent(
                current with { State = state, StartedUtc = current.StartedUtc ?? UtcNow },
                eventKind,
                message);
        });
        await PersistAndPublishAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task SetTerminalStateAsync(
        JobExecution execution,
        QhyJobState state,
        string? error,
        string eventKind,
        string message)
    {
        var snapshot = execution.Mutate(current =>
        {
            if (IsTerminal(current.State)) return current;
            if (state == QhyJobState.Completed &&
                (current.State == QhyJobState.Cancelling || execution.Cancellation.IsCancellationRequested))
            {
                return WithEvent(
                    current with { State = QhyJobState.Cancelled, Error = null, CompletedUtc = UtcNow },
                    "job.cancelled-after-completion-race",
                    "Cancellation won the terminal-state race; Completed was not permitted to overwrite Cancelling.");
            }
            if (current.State == QhyJobState.Cancelling && state != QhyJobState.Cancelled)
            {
                state = QhyJobState.Cancelled;
                error = null;
                eventKind = "job.cancelled";
                message = "Cancellation remained authoritative during terminal-state resolution.";
            }
            return WithEvent(
                current with { State = state, Error = error, CompletedUtc = UtcNow },
                eventKind,
                message);
        });
        await PersistAndPublishAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task PersistAndPublishAsync(QhyJobSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (await store.PersistManifestAsync(snapshot, cancellationToken).ConfigureAwait(false)) JobChanged?.Invoke(snapshot);
    }

    private async Task DemandOwnerAsync(JobExecution execution, string? ownerToken, string action)
    {
        if (execution.IsOwnerToken(ownerToken)) return;
        var denied = execution.Mutate(current => WithEvent(
            current,
            "control.denied",
            $"Rejected unauthenticated owner request for {action}; no control credential was recorded."));
        await PersistAndPublishAsync(denied, CancellationToken.None).ConfigureAwait(false);
        throw new UnauthorizedAccessException($"A valid QHY owner token is required to {action} job {denied.Id}.");
    }

    private JobExecution RequireJob(Guid id) =>
        jobs.TryGetValue(id, out var execution) ? execution : throw new KeyNotFoundException($"QHY job {id} was not found.");

    private static QhyJobSnapshot WithEvent(QhyJobSnapshot snapshot, string kind, string message)
    {
        var recentEvents = snapshot.Events
            .Append(new QhyJobEvent(DateTimeOffset.UtcNow, kind, message))
            .TakeLast(256)
            .ToArray();
        return snapshot with { Events = recentEvents, Revision = snapshot.Revision + 1 };
    }

    private static string RequestKey(string observationRunId, QhyJobKind kind, string clientRequestId) =>
        $"{observationRunId.Trim()}\u001f{kind}\u001f{clientRequestId.Trim()}";

    private static string Fingerprint<T>(T request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));

    private static string CreateOwnerToken()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return token.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool IsTerminal(QhyJobState state) =>
        state is QhyJobState.Cancelled or QhyJobState.Completed or QhyJobState.Faulted or QhyJobState.TakenOver;

    private static QhyFrameSettings CreateSettings(AcquisitionJobRequest request, double exposureSeconds) =>
        new(
            exposureSeconds,
            request.Gain,
            request.Offset,
            request.BinningX,
            request.BinningY,
            request.RoiX,
            request.RoiY,
            request.RoiWidth,
            request.RoiHeight,
            ReadoutMode: request.ReadoutMode,
            BitDepth: request.BitDepth,
            UsbTraffic: request.UsbTraffic,
            FilterName: request.FilterName,
            TargetTemperatureC: request.TargetTemperatureC);

    private static QhyPhotometryFilterStep EffectivePhotometryFilterStep(PhotometryJobRequest request, int sequence)
    {
        if (request.FilterSequence is not { Count: > 0 })
            return new QhyPhotometryFilterStep(request.FilterName, request.ExposureSeconds);
        return request.FilterSequence[(sequence - 1) % request.FilterSequence.Count];
    }

    private static QhyFrameSettings CreateSettings(
        PhotometryJobRequest request,
        QhyPhotometryFilterStep filterStep) =>
        new(
            filterStep.ExposureSeconds,
            request.Gain,
            request.Offset,
            request.BinningX,
            request.BinningY,
            request.RoiX,
            request.RoiY,
            request.RoiWidth,
            request.RoiHeight,
            ReadoutMode: request.ReadoutMode,
            BitDepth: request.BitDepth,
            UsbTraffic: request.UsbTraffic,
            FilterName: filterStep.FilterName,
            TargetTemperatureC: request.TargetTemperatureC);

    private static void Validate(AcquisitionJobRequest request)
    {
        ValidateCommon(request.ObservationRunId, request.RequestedTarget, request.Gain, request.Offset, request.BinningX, request.BinningY);
        ValidateTargetCoordinates(request.TargetRightAscensionDegrees, request.TargetDeclinationDegrees, request.CoordinateEpoch);
        ValidateLease(request.ControlLeaseSeconds);
        ValidateFilterName(request.FilterName);
        if (request.ExposureLadderSeconds.Count == 0 || request.ExposureLadderSeconds.Any(static exposure => !double.IsFinite(exposure) || exposure <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Acquisition exposure ladder must contain positive finite values.");
        }

        if (request.MaximumAttempts is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(request.MaximumAttempts));
        ValidateFrameConfiguration(
            request.RoiX,
            request.RoiY,
            request.RoiWidth,
            request.RoiHeight,
            request.BitDepth,
            request.UsbTraffic,
            request.QualityThresholds);
    }

    private static void Validate(PhotometryJobRequest request)
    {
        ValidateCommon(request.ObservationRunId, request.RequestedTarget, request.Gain, request.Offset, request.BinningX, request.BinningY);
        ValidateTargetCoordinates(request.TargetRightAscensionDegrees, request.TargetDeclinationDegrees, request.CoordinateEpoch);
        ValidateLease(request.ControlLeaseSeconds);
        ValidateFilterName(request.FilterName);
        if (!double.IsFinite(request.ExposureSeconds) || request.ExposureSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(request.ExposureSeconds));
        if (request.FilterSequence is { Count: > 0 })
        {
            if (request.FilterSequence.Count > 32) throw new ArgumentOutOfRangeException(nameof(request.FilterSequence));
            foreach (var step in request.FilterSequence)
            {
                ValidateFilterName(step.FilterName);
                if (!double.IsFinite(step.ExposureSeconds) || step.ExposureSeconds <= 0)
                    throw new ArgumentOutOfRangeException(nameof(request.FilterSequence), "Every filter-sequence exposure must be positive and finite.");
            }
        }
        if (request.FrameCount is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(request.FrameCount));
        if (!double.IsFinite(request.CadenceSeconds) || request.CadenceSeconds < 0 || request.CadenceSeconds > 86_400)
        {
            throw new ArgumentOutOfRangeException(nameof(request.CadenceSeconds));
        }

        ValidateFrameConfiguration(
            request.RoiX,
            request.RoiY,
            request.RoiWidth,
            request.RoiHeight,
            request.BitDepth,
            request.UsbTraffic,
            request.QualityThresholds);
    }

    private static void ValidateCommon(string runId, string target, int gain, int offset, int binningX, int binningY)
    {
        if (string.IsNullOrWhiteSpace(runId)) throw new ArgumentException("ObservationRunId is required.", nameof(runId));
        if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("RequestedTarget is required.", nameof(target));
        if (gain < 0) throw new ArgumentOutOfRangeException(nameof(gain));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (binningX is < 1 or > 8 || binningY is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(binningX));
    }

    private static void ValidateTargetCoordinates(double? rightAscensionDegrees, double? declinationDegrees, string coordinateEpoch)
    {
        if (rightAscensionDegrees.HasValue != declinationDegrees.HasValue)
        {
            throw new ArgumentException("QHY target right ascension and declination must be supplied together.");
        }
        if (rightAscensionDegrees is { } ra && (!double.IsFinite(ra) || ra is < 0 or >= 360))
        {
            throw new ArgumentOutOfRangeException(nameof(rightAscensionDegrees), "Target right ascension must be finite and within [0, 360) degrees.");
        }
        if (declinationDegrees is { } dec && (!double.IsFinite(dec) || dec is < -90 or > 90))
        {
            throw new ArgumentOutOfRangeException(nameof(declinationDegrees), "Target declination must be finite and within [-90, 90] degrees.");
        }
        if (string.IsNullOrWhiteSpace(coordinateEpoch)) throw new ArgumentException("Coordinate epoch/frame is required.", nameof(coordinateEpoch));
    }

    private static void ValidateLease(int seconds)
    {
        if (seconds is < 15 or > 600) throw new ArgumentOutOfRangeException(nameof(seconds), "QHY control lease must be within 15-600 seconds.");
    }

    private static void ValidateFilterName(string filterName)
    {
        if (string.IsNullOrWhiteSpace(filterName) || filterName.Length > 32)
        {
            throw new ArgumentException(
                "QHY filter name must be an explicit non-empty configured name of at most 32 characters.",
                nameof(filterName));
        }
    }

    private static void ValidateActor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 128)
        {
            throw new ArgumentException("A non-empty QHY control actor of at most 128 characters is required.", nameof(actor));
        }
    }

    private static void ValidateFrameConfiguration(
        int roiX,
        int roiY,
        int roiWidth,
        int roiHeight,
        int bitDepth,
        int usbTraffic,
        QhyQualityThresholds? thresholds)
    {
        if (roiX < 0 || roiY < 0 || roiWidth < 0 || roiHeight < 0 || (roiWidth == 0) != (roiHeight == 0))
        {
            throw new ArgumentOutOfRangeException(nameof(roiWidth), "ROI origin and size must be non-negative; width and height are both zero for full frame or both positive.");
        }

        if (bitDepth is not (8 or 16)) throw new ArgumentOutOfRangeException(nameof(bitDepth));
        if (usbTraffic < 0) throw new ArgumentOutOfRangeException(nameof(usbTraffic));
        if (thresholds is null) return;
        if (thresholds.MinimumDetectedStars < 0 ||
            !double.IsFinite(thresholds.MaximumSaturatedFraction) || thresholds.MaximumSaturatedFraction is < 0 or > 1 ||
            !double.IsFinite(thresholds.MinimumTransparency) || thresholds.MinimumTransparency is < 0 or > 1 ||
            !double.IsFinite(thresholds.SaturationAdu) || thresholds.SaturationAdu is <= 0 or > ushort.MaxValue ||
            !double.IsFinite(thresholds.DetectionSigma) || thresholds.DetectionSigma is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(thresholds), "QHY quality thresholds are outside safe numeric ranges.");
        }
    }

    private sealed class JobExecution(QhyJobSnapshot snapshot, string ownerToken) : IDisposable
    {
        private readonly object gate = new();
        private readonly string ownerToken = ownerToken;
        private readonly byte[] ownerTokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(ownerToken));
        private QhyJobSnapshot snapshot = snapshot;

        public CancellationTokenSource Cancellation { get; } = new();

        public AsyncManualResetEvent PauseGate { get; } = new(initialState: true);

        public Task? Worker { get; set; }

        public Task InitialPersistence { get; set; } = Task.CompletedTask;

        public QhyJobSnapshot GetSnapshot()
        {
            lock (gate) return snapshot;
        }

        public QhyJobControlResponse GetControlResponse()
        {
            lock (gate)
            {
                return new QhyJobControlResponse(
                    snapshot,
                    ownerToken,
                    snapshot.LeaseExpiresUtc ?? throw new InvalidOperationException("QHY job has no control-lease expiry."),
                    snapshot.ControlLeaseSeconds);
            }
        }

        public bool IsOwnerToken(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return false;
            var candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
            return CryptographicOperations.FixedTimeEquals(ownerTokenHash, candidateHash);
        }

        public QhyJobSnapshot Mutate(Func<QhyJobSnapshot, QhyJobSnapshot> update)
        {
            lock (gate)
            {
                var previous = snapshot;
                var updated = update(previous);
                if (updated != previous && updated.Revision <= previous.Revision)
                {
                    updated = updated with { Revision = previous.Revision + 1 };
                }
                snapshot = updated;
                return snapshot;
            }
        }

        public QhyJobSnapshot MutateAuthorized(
            string? candidateOwnerToken,
            Func<QhyJobSnapshot, QhyJobSnapshot> update)
        {
            lock (gate)
            {
                if (!IsOwnerToken(candidateOwnerToken))
                {
                    throw new UnauthorizedAccessException("QHY owner token mismatch.");
                }
                var previous = snapshot;
                var updated = update(previous);
                if (updated != previous && updated.Revision <= previous.Revision)
                {
                    updated = updated with { Revision = previous.Revision + 1 };
                }
                snapshot = updated;
                return snapshot;
            }
        }

        public (QhyJobSnapshot Snapshot, bool Transitioned, bool Authorized, Task WaitTask) BeginCheckpoint(
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            lock (gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Cancellation.IsCancellationRequested || snapshot.State == QhyJobState.Cancelling)
                {
                    throw new OperationCanceledException(Cancellation.Token);
                }
                if (IsTerminal(snapshot.State))
                {
                    throw new InvalidOperationException($"Terminal job {snapshot.Id} cannot authorize another checkpoint.");
                }

                var transitioned = false;
                if (snapshot.State == QhyJobState.Running &&
                    (snapshot.LeaseExpiresUtc is not { } leaseExpiry || leaseExpiry <= now))
                {
                    PauseGate.Reset();
                    snapshot = WithEvent(
                        snapshot with
                        {
                            State = QhyJobState.PausedNeedsAttention,
                            AttentionReason = "QHY control lease expired; the client must renew and explicitly resume.",
                            Error = "QHY control lease expired; no additional frame will start.",
                        },
                        "lease.expired",
                        "Control lease expired at a frame boundary; automatic acquisition paused.");
                    transitioned = true;
                }
                if (snapshot.State == QhyJobState.Pausing)
                {
                    PauseGate.Reset();
                    snapshot = WithEvent(
                        snapshot with { State = QhyJobState.Paused },
                        "job.paused",
                        "Job paused at a frame boundary.");
                    transitioned = true;
                }

                if (snapshot.State == QhyJobState.Running)
                {
                    return (snapshot, transitioned, true, Task.CompletedTask);
                }
                if (snapshot.State is QhyJobState.Paused or QhyJobState.PausedNeedsAttention)
                {
                    PauseGate.Reset();
                    return (snapshot, transitioned, false, PauseGate.WaitAsync(cancellationToken));
                }

                throw new InvalidOperationException(
                    $"Job {snapshot.Id} cannot authorize a checkpoint from state {snapshot.State}.");
            }
        }

        public void Dispose() => Cancellation.Dispose();
    }
}
