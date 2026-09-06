using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;

namespace UvexAdv.Nina.Plugin;

internal sealed record ObservationAutomationCommandBinding(
    string Name,
    ICommand Command,
    bool CanInvokeWhileBridgeDisabled,
    bool RequiresRealControlArm,
    bool RequiresOperatorAttestation,
    bool IsRecoveryAction,
    string Description);

internal sealed record ObservationAutomationSnapshot(
    int ProtocolVersion,
    long Revision,
    DateTimeOffset CapturedUtc,
    string BridgeInstanceId,
    int ProcessId,
    string PluginVersion,
    string PluginBuildSha256,
    string? ObservationRunId,
    DateTimeOffset RunUpdatedUtc,
    string? CurrentStageCode,
    string? NextStageCode,
    int CompletedStageCount,
    int TotalStageCount,
    string StatusTechnicalMessage,
    string? PauseTechnicalReason,
    ObservationAutomationRunEventSnapshot? LatestRunEvent,
    string RunState,
    string CurrentStage,
    string NextStage,
    double ProgressPercent,
    string StatusMessage,
    string PauseReason,
    string FailureCode,
    string FailureMessage,
    string FailureRecovery,
    string FailureEvidencePath,
    string LatestEvidencePath,
    string RunManifestPath,
    string UiError,
    string UiErrorTechnicalDetails,
    string UiOperatorNotice,
    string TargetName,
    string TargetCatalogId,
    double TargetRightAscensionDegrees,
    double TargetDeclinationDegrees,
    bool RealModeSelected,
    bool BridgeEnabled,
    bool RealControlArmedForThisNinaSession,
    IReadOnlyList<string> AvailableCommands,
    IReadOnlyDictionary<string, string> UnavailableCommands,
    IReadOnlyList<string> SuggestedRecoveryCommands,
    string RequiredOperatorAction,
    string Endpoint,
    long InvocationCount,
    long RecoveryInvocationCount,
    string LastInvocation,
    IReadOnlyList<ObservationAutomationGateSnapshot> QualityGates,
    IReadOnlyList<ObservationAutomationTimelineSnapshot> RecentTimeline,
    IReadOnlyList<ObservationAutomationEvidenceSnapshot> EvidenceFiles,
    bool SupervisedSlitQualityWarningAuthorized = false);

internal sealed record ObservationAutomationInvocationResult(
    bool Accepted,
    string Code,
    string Message,
    bool RecoveryInvocation);

internal sealed record ObservationAutomationRunEventSnapshot(
    DateTimeOffset TimestampUtc,
    string State,
    string? Stage,
    string Code,
    string Message,
    string? EvidencePath);

internal sealed record ObservationAutomationActivity(
    DateTimeOffset TimestampUtc,
    string RequestId,
    string Operation,
    string? Command,
    bool Accepted,
    string Code,
    string Message,
    bool RecoveryInvocation);

internal sealed record ObservationAutomationGateSnapshot(
    string Stage,
    string State,
    string Code,
    string Message,
    string TechnicalMessage,
    string Metrics,
    string Disposition,
    string Severity);

internal sealed record ObservationAutomationTimelineSnapshot(
    string Time,
    string Stage,
    string Code,
    string Message,
    string TechnicalMessage,
    string EvidencePath);

internal sealed record ObservationAutomationEvidenceSnapshot(
    string Time,
    string Kind,
    string FileName,
    string AbsolutePath);

/// <summary>
/// Current-user-only, local automation adapter for model-driven UI closure.
/// It never owns equipment and never calls a stage runner directly: every
/// mutation is dispatched through the exact ICommand instance bound to the
/// visible N.I.N.A. button. The coordinator and all of its safety, evidence,
/// ownership and bounded-motion gates therefore remain authoritative.
/// </summary>
internal sealed class ObservationAutomationBridge : IDisposable
{
    public const int ProtocolVersion = 1;
    public const string PipeName = "OpenAstroSpec.UVEX4.ObservationAutomation.v1";
    public const string Endpoint = @"\\.\pipe\OpenAstroSpec.UVEX4.ObservationAutomation.v1";
    public const string RealControlOperatorAttestation = "OPERATOR-ATTESTS-ROOF-OPEN-CLEAR-SKY-DEVICE-MOTION-AUTHORIZED";
    public const string SlitQualityOperatorAttestation = "OPERATOR-ACCEPTS-SLIT-PRECISION-WARNING-SUPERVISED-ATR-PROBE";
    private const int MaximumRequestCharacters = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Func<long, ObservationAutomationSnapshot> snapshotFactory;
    private readonly Func<string, string?, ObservationTargetDraft?, ObservationAutomationInvocationResult> commandInvoker;
    private readonly Action<ObservationAutomationActivity> activitySink;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object revisionSync = new();
    private TaskCompletionSource revisionChanged = NewRevisionSignal();
    private readonly string descriptorPath;
    private readonly string auditPath;
    private readonly Task serverTask;
    private readonly string instanceId = Guid.NewGuid().ToString("N");
    private readonly string pluginVersion;
    private readonly string pluginBuildSha256;
    private long revision = 1;
    private int enabled;
    private int realControlArmed;
    private bool disposed;

    public ObservationAutomationBridge(
        Func<long, ObservationAutomationSnapshot> snapshotFactory,
        Func<string, string?, ObservationTargetDraft?, ObservationAutomationInvocationResult> commandInvoker,
        Action<ObservationAutomationActivity> activitySink,
        bool initiallyEnabled)
    {
        this.snapshotFactory = snapshotFactory ?? throw new ArgumentNullException(nameof(snapshotFactory));
        this.commandInvoker = commandInvoker ?? throw new ArgumentNullException(nameof(commandInvoker));
        this.activitySink = activitySink ?? throw new ArgumentNullException(nameof(activitySink));
        enabled = initiallyEnabled ? 1 : 0;
        var pluginAssembly = typeof(ObservationAutomationBridge).Assembly;
        pluginVersion = pluginAssembly.GetName().Version?.ToString() ?? "unknown";
        pluginBuildSha256 = ComputeAssemblySha256(pluginAssembly);
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UVEX-ADV",
            "automation");
        Directory.CreateDirectory(root);
        descriptorPath = Path.Combine(root, "bridge.json");
        auditPath = Path.Combine(root, "audit.ndjson");
        WriteDescriptorBestEffort(online: true);
        serverTask = Task.Run(RunServerAsync);
    }

    public bool Enabled => Volatile.Read(ref enabled) != 0;
    public bool RealControlArmed => Volatile.Read(ref realControlArmed) != 0;
    public long Revision => Interlocked.Read(ref revision);
    public string InstanceId => instanceId;
    public string PluginVersion => pluginVersion;
    public string PluginBuildSha256 => pluginBuildSha256;

    public void UpdateAccess(bool nextEnabled, bool nextRealControlArmed)
    {
        Interlocked.Exchange(ref enabled, nextEnabled ? 1 : 0);
        // A disabled bridge can never retain a latent real-equipment grant.
        Interlocked.Exchange(ref realControlArmed, nextEnabled && nextRealControlArmed ? 1 : 0);
        NotifyStateChanged();
        WriteDescriptorBestEffort(online: true);
    }

    public void NotifyStateChanged()
    {
        TaskCompletionSource previous;
        lock (revisionSync)
        {
            Interlocked.Increment(ref revision);
            previous = revisionChanged;
            revisionChanged = NewRevisionSignal();
        }
        previous.TrySetResult();
    }

    private async Task RunServerAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
                await ServeClientAsync(pipe, lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                PublishActivity(new ObservationAutomationActivity(
                    DateTimeOffset.UtcNow,
                    "bridge",
                    "server",
                    null,
                    false,
                    "BRIDGE_SERVER_ERROR",
                    ex.Message,
                    false));
                try { await Task.Delay(500, lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task ServeClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) return;
            object response;
            if (line.Length > MaximumRequestCharacters)
            {
                response = await BuildResponseAsync(
                    requestId: "unknown",
                    ok: false,
                    code: "REQUEST_TOO_LARGE",
                    message: $"One request may contain at most {MaximumRequestCharacters} characters.",
                    waitAfterRevision: null,
                    waitTimeoutMilliseconds: 0,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                response = await HandleRequestAsync(line, cancellationToken).ConfigureAwait(false);
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions)).ConfigureAwait(false);
        }
    }

    private async Task<object> HandleRequestAsync(string line, CancellationToken cancellationToken)
    {
        AutomationRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<AutomationRequest>(line, JsonOptions);
        }
        catch (JsonException ex)
        {
            return await BuildResponseAsync("unknown", false, "INVALID_JSON", ex.Message, null, 0, cancellationToken).ConfigureAwait(false);
        }
        var requestId = string.IsNullOrWhiteSpace(request?.RequestId)
            ? Guid.NewGuid().ToString("N")
            : request.RequestId.Trim();
        if (request is null || request.ProtocolVersion != ProtocolVersion)
            return await BuildResponseAsync(requestId, false, "PROTOCOL_VERSION_UNSUPPORTED", $"protocolVersion must be {ProtocolVersion}.", null, 0, cancellationToken).ConfigureAwait(false);

        var operation = request.Operation?.Trim().ToLowerInvariant();
        if (operation == "snapshot")
            return await BuildResponseAsync(requestId, true, "SNAPSHOT", "Current UI/coordinator snapshot.", null, 0, cancellationToken).ConfigureAwait(false);
        if (operation == "wait")
        {
            var timeout = Math.Clamp(request.TimeoutMilliseconds ?? 30_000, 0, 60_000);
            return await BuildResponseAsync(requestId, true, "WAIT_COMPLETE", "State changed or the bounded wait expired.", request.AfterRevision ?? -1, timeout, cancellationToken).ConfigureAwait(false);
        }
        if (operation != "invoke")
            return await BuildResponseAsync(requestId, false, "OPERATION_UNSUPPORTED", "operation must be snapshot, wait, or invoke.", null, 0, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(request.Command))
            return await InvokeResponseAsync(requestId, null, false, "COMMAND_REQUIRED", "An invoke request requires a command name.", false, cancellationToken).ConfigureAwait(false);

        var command = request.Command.Trim();
        ObservationAutomationInvocationResult invocation;
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                invocation = new ObservationAutomationInvocationResult(false, "UI_DISPATCHER_UNAVAILABLE", "The N.I.N.A. UI dispatcher is unavailable.", false);
            }
            else
            {
                invocation = await dispatcher.InvokeAsync(() =>
                {
                    // Keep the optimistic-concurrency check and command dispatch
                    // in one UI-thread turn.  A matching revision can therefore
                    // never authorize a command against a later visible state.
                    if (request.ExpectedRevision is not null && request.ExpectedRevision.Value != Revision)
                    {
                        return new ObservationAutomationInvocationResult(
                            false,
                            "STALE_REVISION",
                            $"The UI changed after revision {request.ExpectedRevision.Value}; read a fresh snapshot before invoking a command.",
                            false);
                    }

                    return commandInvoker(command, request.OperatorAttestation, request.TargetDraft);
                }).Task.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            invocation = new ObservationAutomationInvocationResult(false, "COMMAND_DISPATCH_FAILED", ex.Message, false);
        }
        return await InvokeResponseAsync(
            requestId,
            command,
            invocation.Accepted,
            invocation.Code,
            invocation.Message,
            invocation.RecoveryInvocation,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> InvokeResponseAsync(
        string requestId,
        string? command,
        bool accepted,
        string code,
        string message,
        bool recoveryInvocation,
        CancellationToken cancellationToken)
    {
        PublishActivity(new ObservationAutomationActivity(
            DateTimeOffset.UtcNow,
            requestId,
            "invoke",
            command,
            accepted,
            code,
            message,
            recoveryInvocation));
        NotifyStateChanged();
        return await BuildResponseAsync(requestId, accepted, code, message, null, 0, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> BuildResponseAsync(
        string requestId,
        bool ok,
        string code,
        string message,
        long? waitAfterRevision,
        int waitTimeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        Task? signal = null;
        if (waitAfterRevision is not null && waitTimeoutMilliseconds > 0)
        {
            lock (revisionSync)
            {
                if (Revision <= waitAfterRevision.Value)
                    signal = revisionChanged.Task;
            }
        }
        if (signal is not null)
        {
            try
            {
                await signal.WaitAsync(TimeSpan.FromMilliseconds(waitTimeoutMilliseconds), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException) { }
        }
        var snapshot = await CaptureSnapshotAsync().ConfigureAwait(false);
        return new
        {
            protocolVersion = ProtocolVersion,
            requestId,
            ok,
            code,
            message,
            snapshot,
        };
    }

    private async Task<ObservationAutomationSnapshot> CaptureSnapshotAsync()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            throw new InvalidOperationException("The N.I.N.A. UI dispatcher is unavailable.");
        return dispatcher.CheckAccess()
            ? snapshotFactory(Revision)
            : await dispatcher.InvokeAsync(() => snapshotFactory(Revision)).Task.ConfigureAwait(false);
    }

    private void PublishActivity(ObservationAutomationActivity activity)
    {
        try { activitySink(activity); } catch { }
        try
        {
            var entry = JsonSerializer.Serialize(new
            {
                protocolVersion = ProtocolVersion,
                processId = Environment.ProcessId,
                activity.TimestampUtc,
                activity.RequestId,
                activity.Operation,
                activity.Command,
                activity.Accepted,
                activity.Code,
                activity.Message,
                activity.RecoveryInvocation,
            }, JsonOptions);
            File.AppendAllText(auditPath, entry + Environment.NewLine, new UTF8Encoding(false));
        }
        catch { }
    }

    private void WriteDescriptorBestEffort(bool online)
    {
        try
        {
            var descriptor = JsonSerializer.Serialize(new
            {
                protocolVersion = ProtocolVersion,
                transport = "windows-named-pipe-jsonl",
                endpoint = Endpoint,
                pipeName = PipeName,
                processId = Environment.ProcessId,
                processName = Process.GetCurrentProcess().ProcessName,
                bridgeInstanceId = instanceId,
                pluginVersion,
                pluginBuildSha256,
                online,
                enabled = Enabled,
                realControlArmedForThisNinaSession = RealControlArmed,
                maximumRequestCharacters = MaximumRequestCharacters,
                updatedUtc = DateTimeOffset.UtcNow,
                auditPath,
            }, JsonOptions);
            var temporary = descriptorPath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, descriptor, new UTF8Encoding(false));
            File.Move(temporary, descriptorPath, overwrite: true);
        }
        catch { }
    }

    private static TaskCompletionSource NewRevisionSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string ComputeAssemblySha256(Assembly assembly)
    {
        try
        {
            var path = assembly.Location;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "unavailable";
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return "unavailable";
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Interlocked.Exchange(ref realControlArmed, 0);
        WriteDescriptorBestEffort(online: false);
        lifetime.Cancel();
        try { serverTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        lifetime.Dispose();
    }

    private sealed record AutomationRequest(
        int ProtocolVersion,
        string? RequestId,
        string? Operation,
        string? Command,
        string? OperatorAttestation,
        ObservationTargetDraft? TargetDraft,
        long? ExpectedRevision,
        long? AfterRevision,
        int? TimeoutMilliseconds);
}
