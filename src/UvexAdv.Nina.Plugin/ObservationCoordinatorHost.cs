using System.ComponentModel.Composition;
using System.IO;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Media;
using NINA.Core.Model;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

public enum ObservationPreviewChannel
{
    QhyWideField,
    G3SlitField,
    AtrSpectrum,
}

public sealed record ObservationPreview(
    ObservationPreviewChannel Channel,
    ImageSource? Image,
    string Caption,
    DateTimeOffset UpdatedUtc);

public sealed record ObservationDashboardEvidence(
    string Kind,
    string AbsolutePath,
    DateTimeOffset PublishedUtc,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record ObservationDashboardSnapshot(
    ObservationSnapshot Run,
    IReadOnlyDictionary<ObservationStage, GateResult> Gates,
    IReadOnlyDictionary<ObservationPreviewChannel, ObservationPreview> Previews,
    IReadOnlyList<ObservationDashboardEvidence> Evidence,
    string? ManifestPath);

[Export(typeof(ObservationCoordinatorHost))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class ObservationCoordinatorHost : IDisposable
{
    private readonly object sync = new();
    private readonly ObservationRunCoordinator coordinator = new();
    private readonly Dictionary<ObservationStage, GateResult> gates = new();
    private readonly Dictionary<ObservationPreviewChannel, ObservationPreview> previews = new();
    private readonly List<ObservationDashboardEvidence> evidence = new();
    private readonly SemaphoreSlim runGate = new(1, 1);
    private readonly ObservationAttentionNotificationTracker attentionNotificationTracker = new();
    private readonly IObservationAttentionNotifier attentionNotifier;
    private ObservationRunPersistenceSession? persistence;
    private ObservationRunCounters counters = ObservationRunCounters.Empty;
    private string? manifestPath;
    private object? activeRunReservation;
    private RealObservationRunOwnershipLease? realRunOwnershipLease;
    private bool persistenceFailureLatched;
    private bool disposed;

    public ObservationCoordinatorHost()
        : this(new NinaAndWindowsObservationAttentionNotifier())
    {
    }

    internal ObservationCoordinatorHost(IObservationAttentionNotifier attentionNotifier)
    {
        this.attentionNotifier = attentionNotifier ?? throw new ArgumentNullException(nameof(attentionNotifier));
        coordinator.SnapshotChanged += OnCoordinatorSnapshotChanged;
        previews[ObservationPreviewChannel.QhyWideField] = EmptyPreview(
            ObservationPreviewChannel.QhyWideField,
            "等待 QHY/GS350 广域取景数据");
        previews[ObservationPreviewChannel.G3SlitField] = EmptyPreview(
            ObservationPreviewChannel.G3SlitField,
            "等待 PHD2/G3 狭缝视场数据");
        previews[ObservationPreviewChannel.AtrSpectrum] = EmptyPreview(
            ObservationPreviewChannel.AtrSpectrum,
            "等待 N.I.N.A./ATR585M 光谱数据");
    }

    public event EventHandler<ObservationDashboardSnapshot>? DashboardChanged;

    public ObservationDashboardSnapshot Dashboard
    {
        get
        {
            lock (sync)
            {
                return CreateDashboardLocked();
            }
        }
    }

    public async Task RunSimulationAsync(
        ObservationPlan plan,
        int stageDurationMilliseconds,
        IProgress<ApplicationStatus>? progress,
        CancellationToken cancellationToken)
    {
        var runner = new SimulatedObservationStageRunner(
            this,
            Math.Clamp(stageDurationMilliseconds, 250, 30_000),
            progress);
        await RunAsync(plan, runner, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared entry point for the later N.I.N.A., PHD2 and QHY composite stage runner.
    /// The host adds dashboard gate publication but never opens an equipment device itself.
    /// </summary>
    public async Task RunAsync(
        ObservationPlan plan,
        IObservationStageRunner runner,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(runner);
        if (!await runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("An observation run is already active.");
        }
        var reservation = new object();
        ObservationRunPersistenceSession? runPersistence = null;
        RealObservationRunOwnershipLease? runOwnershipLease = null;
        lock (sync)
        {
            // Reserve the host before provenance lookup or manifest creation. A
            // concurrent Resume/Run can therefore never bind to the previous
            // run while this run is still being prepared.
            if (activeRunReservation is not null)
            {
                runGate.Release();
                throw new InvalidOperationException("An observation run is already reserved.");
            }
            activeRunReservation = reservation;
            persistence = null;
            counters = ObservationRunCounters.Empty;
            manifestPath = null;
            persistenceFailureLatched = false;
        }
        try
        {
            if (runner is IRealObservationRunOwnershipSource realRunner)
            {
                var ownership = RealObservationRunOwnershipLease.TryAcquire(
                    realRunner.RealObservationOwnershipLockPath);
                if (!ownership.Acquired)
                {
                    throw new InvalidOperationException(
                        ownership.Failure ?? "Another process owns the real-observation equipment lease.");
                }
                runOwnershipLease = ownership.Lease!;
                lock (sync)
                {
                    if (!ReferenceEquals(activeRunReservation, reservation))
                    {
                        throw new InvalidOperationException("The observation run reservation was lost before acquiring the real-equipment owner lease.");
                    }
                    realRunOwnershipLease = runOwnershipLease;
                }
            }
            var lockedMetadata = runner is IObservationRunProvenanceSource provenance
                ? provenance.LockedMetadata
                : new ObservationRunLockedMetadata(Labels: new Dictionary<string, string>
                {
                    ["adapter"] = "unknown",
                });
            runPersistence = await ObservationRunPersistenceSession.CreateAsync(
                plan,
                lockedMetadata,
                ex => OnPersistenceFailure(reservation, ex),
                cancellationToken).ConfigureAwait(false);
            lock (sync)
            {
                if (!ReferenceEquals(activeRunReservation, reservation))
                {
                    throw new InvalidOperationException("The observation run reservation was lost during initialization.");
                }
                persistence = runPersistence;
                manifestPath = runPersistence.ManifestPath;
            }
            ResetDashboardForRun();
            await coordinator.StartAsync(
                plan,
                new DashboardStageRunner(this, runner),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (runPersistence is not null)
                {
                    await runPersistence.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                lock (sync)
                {
                    if (ReferenceEquals(persistence, runPersistence)) persistence = null;
                    if (ReferenceEquals(realRunOwnershipLease, runOwnershipLease)) realRunOwnershipLease = null;
                    if (ReferenceEquals(activeRunReservation, reservation)) activeRunReservation = null;
                }
                runOwnershipLease?.Dispose();
                runGate.Release();
            }
        }
    }

    public void RequestPause(string reason = "操作员请求暂停。") => coordinator.RequestPause(reason);

    public bool Resume()
    {
        ObservationRunPersistenceSession? current;
        lock (sync)
        {
            if (activeRunReservation is null ||
                persistence is null ||
                persistenceFailureLatched ||
                persistence.Failure is not null)
            {
                return false;
            }
            current = persistence;
        }
        return current.TryResume(coordinator.Resume);
    }

    public void Cancel() => coordinator.Cancel();

    public void RequestTakeover(string reason = "操作员请求人工接管。") => coordinator.RequestTakeover(reason);

    /// <summary>
    /// Data-entry point for the later real QHY, PHD2/G3 and ATR adapters. The host never opens a camera.
    /// </summary>
    public void PublishPreview(ObservationPreviewChannel channel, ImageSource? image, string caption)
    {
        if (image is Freezable freezable && freezable.CanFreeze && !freezable.IsFrozen)
        {
            freezable.Freeze();
        }

        EventHandler<ObservationDashboardSnapshot>? handler;
        ObservationDashboardSnapshot dashboard;
        lock (sync)
        {
            previews[channel] = new ObservationPreview(channel, image, caption, DateTimeOffset.UtcNow);
            dashboard = CreateDashboardLocked();
            handler = DashboardChanged;
        }
        handler?.Invoke(this, dashboard);
    }

    public void PublishGate(ObservationStage stage, GateResult gate)
    {
        EventHandler<ObservationDashboardSnapshot>? handler;
        ObservationDashboardSnapshot dashboard;
        lock (sync)
        {
            gates[stage] = gate;
            persistence?.PublishGate(stage, gate);
            dashboard = CreateDashboardLocked();
            handler = DashboardChanged;
        }
        handler?.Invoke(this, dashboard);
    }

    public void PublishCounters(ObservationRunCounters next)
    {
        ObservationRunPersistenceSession? current;
        ObservationRunCounters monotonic;
        lock (sync)
        {
            var additional = next.Additional ?? counters.Additional;
            monotonic = new ObservationRunCounters(
                Math.Max(counters.AtrAttemptedFrames, next.AtrAttemptedFrames),
                Math.Max(counters.AtrAcceptedFrames, next.AtrAcceptedFrames),
                Math.Max(counters.QhyAttemptedFrames, next.QhyAttemptedFrames),
                Math.Max(counters.QhyAcceptedFrames, next.QhyAcceptedFrames),
                additional is null
                    ? null
                    : additional.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
            counters = monotonic;
            current = persistence;
            current?.PublishCounters(monotonic);
        }
    }

    public void PublishEvidence(
        string kind,
        string absolutePath,
        string? knownSha256 = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        EventHandler<ObservationDashboardSnapshot>? handler;
        ObservationDashboardSnapshot dashboard;
        lock (sync)
        {
            evidence.Add(new ObservationDashboardEvidence(
                kind,
                absolutePath,
                DateTimeOffset.UtcNow,
                metadata is null
                    ? null
                    : new Dictionary<string, string>(metadata, StringComparer.Ordinal)));
            if (evidence.Count > 200)
            {
                evidence.RemoveRange(0, evidence.Count - 200);
            }
            persistence?.PublishEvidence(kind, absolutePath, knownSha256, metadata);
            dashboard = CreateDashboardLocked();
            handler = DashboardChanged;
        }
        handler?.Invoke(this, dashboard);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        coordinator.SnapshotChanged -= OnCoordinatorSnapshotChanged;
        coordinator.Dispose();
        attentionNotifier.Dispose();
    }

    internal GateResult PersistenceHealthGate()
    {
        ObservationRunPersistenceSession? current;
        bool failureLatched;
        lock (sync)
        {
            current = persistence;
            failureLatched = persistenceFailureLatched;
        }
        if (current is null)
        {
            return GateResult.Unknown(
                "RUN_MANIFEST_SESSION_MISSING",
                "The observation run has no active durable manifest session.");
        }
        var failure = current.Failure;
        return failure is null && !failureLatched
            ? GateResult.Pass(
                "RUN_MANIFEST_HEALTHY",
                $"Observation manifest is active at '{current.ManifestPath}'.")
            : GateResult.Unknown(
                "RUN_MANIFEST_WRITE_FAILED",
                $"Observation manifest persistence failed and this run cannot be resumed: {failure?.Message ?? "latched writer failure"}");
    }

    internal GateResult RealRunOwnershipGate()
    {
        lock (sync)
        {
            return activeRunReservation is not null && realRunOwnershipLease is not null
                ? GateResult.Pass(
                    "REAL_OBSERVATION_OWNER_LEASE_HELD",
                    "This host holds the live machine-wide real-observation owner lease.")
                : GateResult.Unknown(
                    "REAL_OBSERVATION_OWNER_LEASE_MISSING",
                    "This host cannot prove exclusive ownership of the real observation runner; foreign durable recovery is prohibited.");
        }
    }

    internal Task<ObservationSnapshot> CommitCompletionAsync(
        Func<ObservationSnapshot> createCompletedSnapshot,
        CancellationToken cancellationToken)
    {
        lock (sync)
        {
            var current = persistence ?? throw new InvalidOperationException(
                "The observation run has no durable manifest session to finalize.");
            if (persistenceFailureLatched || current.Failure is not null)
            {
                throw new IOException(
                    "The observation manifest writer failed before completion and cannot be resumed or finalized.",
                    current.Failure);
            }
            var finalCounters = counters with
            {
                Additional = counters.Additional is null
                    ? null
                    : counters.Additional.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value,
                        StringComparer.Ordinal),
            };
            var finalGates = gates.ToDictionary(
                pair => pair.Key,
                pair => pair.Value with
                {
                    Metrics = pair.Value.Metrics is null
                        ? null
                        : pair.Value.Metrics.ToDictionary(
                            metric => metric.Key,
                            metric => metric.Value,
                            StringComparer.Ordinal),
                });
            // CommitCompletionAsync transitions the session to
            // FinalizationQueued synchronously before its first await. Invoke it
            // while holding the same host lock used by every Publish* method so
            // no gate/counter/evidence record can slip between the final capture
            // and the writer freeze.
            return current.CommitCompletionAsync(
                createCompletedSnapshot,
                finalCounters,
                finalGates,
                cancellationToken);
        }
    }

    private void ResetDashboardForRun()
    {
        EventHandler<ObservationDashboardSnapshot>? handler;
        ObservationDashboardSnapshot dashboard;
        lock (sync)
        {
            gates.Clear();
            evidence.Clear();
            previews[ObservationPreviewChannel.QhyWideField] = EmptyPreview(
                ObservationPreviewChannel.QhyWideField,
                "运行已就绪，等待 QHY/GS350 广域取景阶段");
            previews[ObservationPreviewChannel.G3SlitField] = EmptyPreview(
                ObservationPreviewChannel.G3SlitField,
                "运行已就绪，等待 PHD2/G3 狭缝视场阶段");
            previews[ObservationPreviewChannel.AtrSpectrum] = EmptyPreview(
                ObservationPreviewChannel.AtrSpectrum,
                "运行已就绪，等待 N.I.N.A./ATR585M 光谱阶段");
            dashboard = CreateDashboardLocked();
            handler = DashboardChanged;
        }
        handler?.Invoke(this, dashboard);
    }

    private void OnCoordinatorSnapshotChanged(object? sender, ObservationSnapshot snapshot)
    {
        EventHandler<ObservationDashboardSnapshot>? handler;
        ObservationDashboardSnapshot dashboard;
        ObservationRunPersistenceSession? current;
        ObservationRunCounters currentCounters;
        ObservationAttentionNotificationEvaluation notificationEvaluation;
        lock (sync)
        {
            current = persistence;
            currentCounters = counters;
            current?.PublishSnapshot(snapshot, currentCounters);
            gates.TryGetValue(snapshot.CurrentStage ?? ObservationStage.ValidateNightSetup, out var currentGate);
            notificationEvaluation = attentionNotificationTracker.Evaluate(snapshot, currentGate);
            dashboard = CreateDashboardLocked(snapshot);
            handler = DashboardChanged;
        }
        handler?.Invoke(this, dashboard);
        if (notificationEvaluation.ClearActiveIndicator)
        {
            attentionNotifier.ClearActiveIndicator();
        }
        if (notificationEvaluation.Notification is { } notification)
        {
            attentionNotifier.Notify(notification);
        }
    }

    private void OnPersistenceFailure(object reservation, Exception exception)
    {
        bool active;
        lock (sync)
        {
            active = ReferenceEquals(activeRunReservation, reservation);
            if (active) persistenceFailureLatched = true;
        }
        if (active)
        {
            coordinator.RequestPause($"运行清单写入失败：{exception.Message}");
        }
    }

    private ObservationDashboardSnapshot CreateDashboardLocked(ObservationSnapshot? run = null) => new(
        run ?? coordinator.Snapshot,
        new Dictionary<ObservationStage, GateResult>(gates),
        new Dictionary<ObservationPreviewChannel, ObservationPreview>(previews),
        evidence.ToArray(),
        manifestPath);

    private static ObservationPreview EmptyPreview(ObservationPreviewChannel channel, string caption) =>
        new(channel, null, caption, DateTimeOffset.UtcNow);
}

internal sealed class DashboardStageRunner(
    ObservationCoordinatorHost host,
    IObservationStageRunner inner) : IObservationStageRunner, IObservationRunCompletionCommitter
{
    public async Task<StageResult> ExecuteStageAsync(
        ObservationStage stage,
        ObservationContext context,
        CancellationToken cancellationToken)
    {
        var persistenceBefore = host.PersistenceHealthGate();
        if (persistenceBefore.Disposition != GateDisposition.Passed)
        {
            host.PublishGate(stage, persistenceBefore);
            return new StageResult(persistenceBefore);
        }
        var result = await inner.ExecuteStageAsync(stage, context, cancellationToken).ConfigureAwait(false);
        var persistenceAfter = host.PersistenceHealthGate();
        if (persistenceAfter.Disposition != GateDisposition.Passed)
        {
            host.PublishGate(stage, persistenceAfter);
            return new StageResult(persistenceAfter);
        }
        host.PublishGate(stage, result.Gate);
        return result;
    }

    public async Task<GateResult> RevalidateAsync(ObservationContext context, CancellationToken cancellationToken)
    {
        var persistenceBefore = host.PersistenceHealthGate();
        var stage = host.Dashboard.Run.CurrentStage ?? ObservationStage.ValidateNightSetup;
        if (persistenceBefore.Disposition != GateDisposition.Passed)
        {
            host.PublishGate(stage, persistenceBefore);
            return persistenceBefore;
        }
        var result = await inner.RevalidateAsync(context, cancellationToken).ConfigureAwait(false);
        var persistenceAfter = host.PersistenceHealthGate();
        var published = persistenceAfter.Disposition == GateDisposition.Passed ? result : persistenceAfter;
        host.PublishGate(stage, published);
        return published;
    }

    public Task OnPausedAsync(ObservationContext context, CancellationToken cancellationToken) =>
        inner.OnPausedAsync(context, cancellationToken);

    public Task OnResumingAsync(ObservationContext context, CancellationToken cancellationToken) =>
        inner.OnResumingAsync(context, cancellationToken);

    public Task OnTakeoverAsync(ObservationContext context, CancellationToken cancellationToken) =>
        inner.OnTakeoverAsync(context, cancellationToken);

    public Task OnCancelledAsync(ObservationContext context, CancellationToken cancellationToken) =>
        inner.OnCancelledAsync(context, cancellationToken);

    public Task OnFaultedAsync(ObservationContext context, Exception cause, CancellationToken cancellationToken) =>
        inner.OnFaultedAsync(context, cause, cancellationToken);

    public Task<ObservationSnapshot> CommitCompletionAsync(
        Func<ObservationSnapshot> createCompletedSnapshot,
        CancellationToken cancellationToken) =>
        host.CommitCompletionAsync(createCompletedSnapshot, cancellationToken);
}

/// <summary>
/// Bridges the core coordinator to N.I.N.A.'s real SequentialStrategy. A stage
/// request is completed only by the corresponding child sequence item, so the
/// container's Conditions, Triggers, status model and any N.I.N.A. execution
/// semantics remain authoritative. Failed gates keep the same child item active
/// across Pause/Resume and are retried only after coordinator revalidation.
/// </summary>
internal sealed class SequencerStageBridge : IObservationStageRunner, IObservationRunProvenanceSource, IDisposable
{
    private readonly IObservationStageRunner inner;
    private readonly Channel<StageRequest> requests = Channel.CreateUnbounded<StageRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });
    private readonly object sync = new();
    private StageRequest? pending;
    private bool disposed;

    public SequencerStageBridge(IObservationStageRunner inner) => this.inner = inner;

    public ObservationRunLockedMetadata LockedMetadata =>
        inner is IObservationRunProvenanceSource provenance
            ? provenance.LockedMetadata
            : ObservationRunLockedMetadata.Empty;

    public async Task<StageResult> ExecuteStageAsync(
        ObservationStage stage,
        ObservationContext context,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var request = new StageRequest(stage, context, cancellationToken);
        lock (sync)
        {
            if (pending is not null) throw new InvalidOperationException("A N.I.N.A. sequence stage request is already pending.");
            pending = request;
        }
        try
        {
            await requests.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            return await request.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (sync)
            {
                if (ReferenceEquals(pending, request)) pending = null;
            }
        }
    }

    public async Task ExecuteMarkerAsync(
        ObservationStage expectedStage,
        IProgress<ApplicationStatus> progress,
        CancellationToken markerToken)
    {
        while (true)
        {
            var request = await requests.Reader.ReadAsync(markerToken).ConfigureAwait(false);
            if (request.Stage != expectedStage)
            {
                var error = new InvalidOperationException($"N.I.N.A. stage order mismatch: marker {expectedStage}, coordinator {request.Stage}.");
                request.Completion.TrySetException(error);
                throw error;
            }

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(markerToken, request.CoordinatorToken);
                progress.Report(new ApplicationStatus
                {
                    Source = "OpenAstroSpec Auto",
                    Status = SimulatedObservationStageRunner.StageDisplayName(expectedStage),
                });
                var result = await inner.ExecuteStageAsync(request.Stage, request.Context, linked.Token).ConfigureAwait(false);
                request.Completion.TrySetResult(result);
                if (result.CanAdvance) return;
            }
            catch (Exception ex)
            {
                request.Completion.TrySetException(ex);
                throw;
            }
        }
    }

    public Task<GateResult> RevalidateAsync(ObservationContext context, CancellationToken cancellationToken) =>
        inner.RevalidateAsync(context, cancellationToken);

    public Task OnPausedAsync(ObservationContext context, CancellationToken cancellationToken) =>
        inner.OnPausedAsync(context, cancellationToken);

    public Task OnResumingAsync(ObservationContext context, CancellationToken cancellationToken) =>
        inner.OnResumingAsync(context, cancellationToken);

    public Task OnTakeoverAsync(ObservationContext context, CancellationToken cancellationToken) =>
        inner.OnTakeoverAsync(context, cancellationToken);

    public Task OnCancelledAsync(ObservationContext context, CancellationToken cancellationToken) =>
        inner.OnCancelledAsync(context, cancellationToken);

    public Task OnFaultedAsync(ObservationContext context, Exception cause, CancellationToken cancellationToken) =>
        inner.OnFaultedAsync(context, cause, cancellationToken);

    public void Abort(Exception cause)
    {
        requests.Writer.TryComplete(cause);
        lock (sync) pending?.Completion.TrySetException(cause);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Abort(new OperationCanceledException("N.I.N.A. sequence stage bridge was disposed."));
    }

    private sealed record StageRequest(
        ObservationStage Stage,
        ObservationContext Context,
        CancellationToken CoordinatorToken)
    {
        public TaskCompletionSource<StageResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal sealed class SimulatedObservationStageRunner(
    ObservationCoordinatorHost host,
    int stageDurationMilliseconds,
    IProgress<ApplicationStatus>? progress) : ObservationStageRunnerBase, IObservationRunProvenanceSource
{
    public ObservationRunLockedMetadata LockedMetadata { get; } = new(
        Labels: new Dictionary<string, string>
        {
            ["adapter"] = "simulator",
            ["physicalHardwareOpened"] = "false",
        });

    public override async Task<StageResult> ExecuteStageAsync(
        ObservationStage stage,
        ObservationContext context,
        CancellationToken cancellationToken)
    {
        var culture = ObservationStaticTextLocalization.EffectiveCulture;
        const int ticks = 6;
        for (var tick = 1; tick <= ticks; tick++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await context.CheckpointAsync(cancellationToken).ConfigureAwait(false);
            var fraction = tick / (double)ticks;
            progress?.Report(new ApplicationStatus
            {
                Source = ObservationUiPresentation.Text("OpenAstroSpec Auto（模拟）", "OpenAstroSpec Auto (simulation)", culture),
                Status = $"{ObservationUiPresentation.StageName(stage, culture)} · {fraction:P0}",
                Progress = fraction,
            });
            PublishSimulatedPreview(stage, fraction);
            await Task.Delay(stageDurationMilliseconds / ticks, cancellationToken).ConfigureAwait(false);
        }

        var gate = GateResult.Pass(
            $"SIM_{stage.ToString().ToUpperInvariant()}",
            ObservationUiPresentation.Text(
                $"{ObservationUiPresentation.StageName(stage, culture)}：模拟质量门通过。",
                $"{ObservationUiPresentation.StageName(stage, culture)}: simulated quality gate passed.",
                culture),
            new Dictionary<string, double> { ["simulationConfidence"] = 1 });
        return new StageResult(gate, Metadata: new Dictionary<string, string>
        {
            ["adapter"] = "simulator",
            ["observationRunId"] = context.Plan.ObservationRunId,
        });
    }

    public override Task<GateResult> RevalidateAsync(
        ObservationContext context,
        CancellationToken cancellationToken)
    {
        var gate = GateResult.Pass(
            "SIM_REVALIDATED",
            ObservationUiPresentation.Text(
                "模拟设备身份、安全、目标高度、解算、入缝和导星状态已重新验证。",
                "Simulated device identity, safety, target altitude, solve, slit placement and guiding state were revalidated.",
                ObservationStaticTextLocalization.EffectiveCulture),
            new Dictionary<string, double> { ["simulationConfidence"] = 1 });
        return Task.FromResult(gate);
    }

    public override Task OnPausedAsync(ObservationContext context, CancellationToken cancellationToken)
    {
        progress?.Report(new ApplicationStatus
        {
            Source = ObservationUiPresentation.Text("OpenAstroSpec Auto（模拟）", "OpenAstroSpec Auto (simulation)", ObservationStaticTextLocalization.EffectiveCulture),
            Status = ObservationUiPresentation.Text("已在有界动作边界暂停；不会开始下一动作。", "Paused at a bounded action boundary; no next action will start.", ObservationStaticTextLocalization.EffectiveCulture),
        });
        return Task.CompletedTask;
    }

    public override Task OnTakeoverAsync(ObservationContext context, CancellationToken cancellationToken)
    {
        progress?.Report(new ApplicationStatus
        {
            Source = ObservationUiPresentation.Text("OpenAstroSpec Auto（模拟）", "OpenAstroSpec Auto (simulation)", ObservationStaticTextLocalization.EffectiveCulture),
            Status = ObservationUiPresentation.Text("已进入人工接管；模拟器未持有任何实体设备。", "Manual takeover is active; the simulator owns no physical equipment.", ObservationStaticTextLocalization.EffectiveCulture),
        });
        return Task.CompletedTask;
    }

    public override Task OnCancelledAsync(ObservationContext context, CancellationToken cancellationToken)
    {
        progress?.Report(new ApplicationStatus
        {
            Source = ObservationUiPresentation.Text("OpenAstroSpec Auto（模拟）", "OpenAstroSpec Auto (simulation)", ObservationStaticTextLocalization.EffectiveCulture),
            Status = ObservationUiPresentation.Text("模拟观测已取消。", "The simulated observation was cancelled.", ObservationStaticTextLocalization.EffectiveCulture),
        });
        return Task.CompletedTask;
    }

    private void PublishSimulatedPreview(ObservationStage stage, double fraction)
    {
        var channel = stage switch
        {
            ObservationStage.SlewToCatalogTarget or ObservationStage.AcquireQhyWideField or ObservationStage.CoarseCenter or
                ObservationStage.StartQhyPhotometry or ObservationStage.FinalizeObservation =>
                ObservationPreviewChannel.QhyWideField,
            ObservationStage.AcquireG3SlitField or ObservationStage.PlaceTargetOnSlit or
                ObservationStage.StartGuiding => ObservationPreviewChannel.G3SlitField,
            ObservationStage.SelectAtrExposure or ObservationStage.RunScienceBlock =>
                ObservationPreviewChannel.AtrSpectrum,
            _ => ObservationPreviewChannel.QhyWideField,
        };
        host.PublishPreview(
            channel,
            SimulatedPreviewFactory.Create(channel, fraction),
            ObservationUiPresentation.Text(
                $"{ObservationUiPresentation.StageName(stage, ObservationStaticTextLocalization.EffectiveCulture)} · 模拟实时帧 {fraction:P0}",
                $"{ObservationUiPresentation.StageName(stage, ObservationStaticTextLocalization.EffectiveCulture)} · simulated live frame {fraction:P0}",
                ObservationStaticTextLocalization.EffectiveCulture));
    }

    internal static string StageDisplayName(ObservationStage stage) =>
        ObservationUiPresentation.StageName(stage, ObservationStaticTextLocalization.EffectiveCulture);
}

internal static class SimulatedPreviewFactory
{
    public static ImageSource Create(ObservationPreviewChannel channel, double fraction)
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(6, 13, 24)),
            null,
            new RectangleGeometry(new Rect(0, 0, 320, 180))));
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(45, 148, 163, 184)), 1);
        for (var x = 0; x <= 320; x += 40)
        {
            group.Children.Add(new GeometryDrawing(null, gridPen, new LineGeometry(new Point(x, 0), new Point(x, 180))));
        }
        for (var y = 0; y <= 180; y += 30)
        {
            group.Children.Add(new GeometryDrawing(null, gridPen, new LineGeometry(new Point(0, y), new Point(320, y))));
        }

        switch (channel)
        {
            case ObservationPreviewChannel.QhyWideField:
                DrawWideField(group, fraction);
                break;
            case ObservationPreviewChannel.G3SlitField:
                DrawSlitField(group, fraction);
                break;
            case ObservationPreviewChannel.AtrSpectrum:
                DrawSpectrum(group, fraction);
                break;
        }

        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static void DrawWideField(DrawingGroup group, double fraction)
    {
        var starBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240));
        foreach (var point in new[]
        {
            new Point(35, 42), new Point(78, 132), new Point(118, 62), new Point(177, 119),
            new Point(229, 38), new Point(278, 142), new Point(301, 79), new Point(201, 73),
        })
        {
            group.Children.Add(new GeometryDrawing(starBrush, null, new EllipseGeometry(point, 2.4, 2.4)));
        }
        var target = new Point(160 + (1 - fraction) * 45, 90 - (1 - fraction) * 24);
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(34, 211, 238)), 2);
        group.Children.Add(new GeometryDrawing(null, pen, new EllipseGeometry(target, 12, 12)));
        group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new Point(target.X - 18, target.Y), new Point(target.X + 18, target.Y))));
        group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new Point(target.X, target.Y - 18), new Point(target.X, target.Y + 18))));
    }

    private static void DrawSlitField(DrawingGroup group, double fraction)
    {
        var slitPen = new Pen(new SolidColorBrush(Color.FromRgb(148, 163, 184)), 5);
        group.Children.Add(new GeometryDrawing(null, slitPen, new LineGeometry(new Point(160, 12), new Point(160, 168))));
        var target = new Point(160 + (1 - fraction) * 55, 90);
        var targetBrush = new SolidColorBrush(Color.FromRgb(250, 204, 21));
        group.Children.Add(new GeometryDrawing(targetBrush, null, new EllipseGeometry(target, 7, 7)));
        var residualPen = new Pen(new SolidColorBrush(Color.FromRgb(248, 113, 113)), 2);
        group.Children.Add(new GeometryDrawing(null, residualPen, new LineGeometry(target, new Point(160, 90))));
    }

    private static void DrawSpectrum(DrawingGroup group, double fraction)
    {
        var traceBrush = new SolidColorBrush(Color.FromArgb(115, 56, 189, 248));
        group.Children.Add(new GeometryDrawing(traceBrush, null, new RectangleGeometry(new Rect(12, 78, 296, 24))));
        var linePen = new Pen(new SolidColorBrush(Color.FromRgb(125, 211, 252)), 2 + fraction * 2);
        foreach (var x in new[] { 54d, 93d, 151d, 218d, 273d })
        {
            group.Children.Add(new GeometryDrawing(null, linePen, new LineGeometry(new Point(x, 47), new Point(x, 133))));
        }
        var profilePen = new Pen(new SolidColorBrush(Color.FromRgb(45, 212, 191)), 2);
        var figure = new PathFigure { StartPoint = new Point(12, 160), IsClosed = false };
        for (var x = 12; x <= 308; x += 4)
        {
            var value = 11 * Math.Sin(x / 22d) + 7 * Math.Sin(x / 9d) * fraction;
            figure.Segments.Add(new LineSegment(new Point(x, 150 - value), true));
        }
        group.Children.Add(new GeometryDrawing(null, profilePen, new PathGeometry(new[] { figure })));
    }
}
