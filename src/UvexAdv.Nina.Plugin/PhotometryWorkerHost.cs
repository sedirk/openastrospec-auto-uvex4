using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Web;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.ViewModel.Sequencer;
using NINA.WPF.Base.Interfaces.Mediator;
using UvexAdv.Qhy.Core;

namespace UvexAdv.Nina.Plugin;

[Export(typeof(PhotometryWorkerHost))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class PhotometryWorkerHost : IDisposable
{
    private readonly IProfileService profiles;
    private readonly ICameraMediator camera;
    private readonly IFilterWheelMediator wheel;
    private readonly IFocuserMediator focuser;
    private readonly IImagingMediator imaging;
    private readonly IImageSaveMediator saves;
    private readonly ISequenceMediator sequences;
    private ISequence2VM? receiverSequence;
    private bool receiverExecuting;
    private bool sequenceEventsAttached;
    private string pluginSha256 = "";
    private readonly UvexPluginSettings settings;
    private QhyJobCoordinator? coordinator;
    private NinaPhotometryCameraAdapter? adapter;
    private string focusLedgerDirectory = "";
    private PhotometryPipeServer? server;
    private PhotometryDeviceClaims? deviceClaims;
    private QhyNinaWorkerIdentity? identity;
    private QhyServiceConfigurationProof? proof;
    private readonly Dictionary<Guid, QhyJobControlResponse> localControls = [];
    private Guid? activeMasterSession;
    private volatile bool operatorPaused;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> operatorJobs = new();
    private string sdkPath = "";
    private string sdkHash = "";
    private string status = T("尚未启用测光协同。", "Photometry collaboration is disabled.");

    [ImportingConstructor]
    public PhotometryWorkerHost(IProfileService profiles, ICameraMediator camera, IFilterWheelMediator wheel,
        IFocuserMediator focuser, IImagingMediator imaging, IImageSaveMediator saves, ISequenceMediator sequences)
    {
        this.profiles = profiles; this.camera = camera; this.wheel = wheel;
        this.focuser = focuser; this.imaging = imaging; this.saves = saves;
        this.sequences = sequences;
        settings = new UvexPluginSettings(profiles);
        profiles.ProfileChanged += OnProfileChanged;
    }

    public event Action? Changed;
    public string Status => status;
    public bool Enabled => server is not null;
    public bool OperatorPaused => operatorPaused;
    // Read-only setup feedback; no device connections, module loading or enabling.
    public string SetupIssue
    {
        get
        {
            try
            {
                ValidateSelectedDevices();
                if (!PhotometryUiPresentation.IsDifferentProfile(MasterProfileId, ProfileId))
                    return "Bind a different spectroscopy N.I.N.A. Profile ID.";
                return "";
            }
            catch (Exception ex) { return ex.Message; }
        }
    }
    public string ProfileId => profiles.ActiveProfile.Id.ToString();
    public string MasterProfileId { get => settings.PhotometryMasterProfileId; set => settings.PhotometryMasterProfileId = value.Trim(); }
    public string Endpoint => "nina://" + ProfileId;
    public QhyJobSnapshot? LatestJob => coordinator?.RecentJobs(1).FirstOrDefault();

    public void BindSelectedDevices()
    {
        if (Enabled) throw new InvalidOperationException("Disable the idle worker before rebinding devices.");
        NinaInstancePolicy.ValidateWorkerProfile(profiles, settings);
        var p = profiles.ActiveProfile;
        if (!p.CameraSettings.Id.Contains("QHYminiCam8M", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Select the exact native QHYminiCam8M camera, not ATR or an ordinal camera.");
        if (string.IsNullOrWhiteSpace(p.FilterWheelSettings.Id) || p.FilterWheelSettings.Id == "No_Device" ||
            string.IsNullOrWhiteSpace(p.FocuserSettings.Id) || p.FocuserSettings.Id == "No_Device")
            throw new InvalidOperationException("Select the photometry filter wheel and GS350 focuser first.");
        settings.PhotometryCameraId = p.CameraSettings.Id;
        settings.PhotometryFilterWheelId = p.FilterWheelSettings.Id;
        settings.PhotometryFocuserId = p.FocuserSettings.Id;
        Publish(T("已绑定测光相机、滤镜轮与电调焦（未连接）。", "Bound the three photometry devices without connecting."));
    }

    /// <summary>Explicit, session-only operator enablement. No device is connected
    /// until a bound spectroscopy master's authorized production stage requests it.</summary>
    public void Enable()
    {
        if (Enabled) return;
        if (coordinator is not null) throw new InvalidOperationException("Restart the idle worker after disabling or changing profiles; old jobs are not silently erased.");
        ValidateSelectedDevices();
        if (!sequences.Initialized || sequences.IsAdvancedSequenceRunning())
            throw new InvalidOperationException("PHOTOMETRY_ENABLE_WHILE_IDLE: Enable collaboration after N.I.N.A. initializes and before starting the worker receiver sequence.");
        var workerId = Guid.Parse(ProfileId);
        if (!Guid.TryParse(MasterProfileId, out var masterId) || masterId == Guid.Empty || masterId == workerId)
            throw new InvalidOperationException("Bind a different spectroscopy N.I.N.A. Profile ID.");
        // Read the vendor module already loaded by native N.I.N.A. selection.
        // This does not load a DLL, scan a camera, or connect equipment.
        using var process = Process.GetCurrentProcess();
        sdkPath = process.Modules.Cast<ProcessModule>()
            .SingleOrDefault(m => string.Equals(m.ModuleName, "qhyccd.dll", StringComparison.OrdinalIgnoreCase))?.FileName
            ?? throw new InvalidOperationException("Select N.I.N.A.'s native QHY driver first; its loaded SDK identity is unavailable.");
        sdkHash = HashFile(sdkPath);
        pluginSha256 = HashFile(typeof(PhotometryWorkerHost).Assembly.Location);
        proof = QhyServiceConfigurationProof.Create(false, NinaInstancePolicy.WorkerAdapter,
            "QHYminiCam8M", settings.PhotometryCameraId, sdkHash, settings.QhyReadoutMode, FilterPositions());
        identity = new(workerId, masterId, Guid.NewGuid(), Environment.ProcessId,
            process.StartTime.ToUniversalTime(), ConfigurationHash(), settings.PhotometryCameraId,
            settings.PhotometryFilterWheelId, settings.PhotometryFocuserId);
        var progress = new Progress<ApplicationStatus>(s => Publish(s.Status));
        deviceClaims = new PhotometryDeviceClaims(identity.CameraId, identity.FilterWheelId, identity.FocuserId);
        adapter = new NinaPhotometryCameraAdapter(profiles, camera, wheel, focuser, imaging, saves,
            settings, () =>
            {
                if (operatorPaused) throw new InvalidOperationException("PHOTOMETRY_OPERATOR_PAUSED: Local pause blocks every new device action.");
                ValidateActiveConfiguration();
            }, progress, () =>
            {
                if (receiverSequence is not null && !receiverExecuting)
                    throw new InvalidOperationException("PHOTOMETRY_RECEIVER_NOT_ACTIVE: Wait until the reviewed receiver item is running.");
                return receiverSequence;
            });
        coordinator = new QhyJobCoordinator(adapter, new QhyCoordinatorOptions
        {
            ExpectedStableId = identity.CameraId,
            TimeProvider = new MonotonicLeaseTimeProvider(),
            DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UVEX-ADV", "photometry", workerId.ToString("N"), identity.SessionId.ToString("N")),
        });
        focusLedgerDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UVEX-ADV", "photometry", workerId.ToString("N"), "focus-budgets");
        coordinator.JobChanged += _ => Changed?.Invoke();
        server = new PhotometryPipeServer(workerId, DispatchAsync);
        sequences.SequenceStarting += OnSequenceStarting;
        sequences.SequenceFinished += OnSequenceFinished;
        sequenceEventsAttached = true;
        Publish(T("本会话测光协同已启用：等待已绑定光谱主控。只允许相机、滤镜轮、电调焦。", "Worker enabled for the bound master; photometry devices only."));
    }

    internal async Task<PhotometryPipeResponse> DispatchAsync(PhotometryPipeRequest request, CancellationToken token)
    {
        if (identity is null || coordinator is null) throw new InvalidOperationException("Worker is not enabled.");
        PhotometryPipeResponse Reply(int code, object value, Dictionary<string, string>? headers = null) =>
            new(code, JsonSerializer.Serialize(value is QhyJobSnapshot job
                ? job with { OperatorInterventionRequired = operatorJobs.ContainsKey(job.Id) } : value,
                PhotometryPipeProtocol.Json), "application/json", headers ?? [], identity);
        T Body<T>() => JsonSerializer.Deserialize<T>(request.Body ?? "", PhotometryPipeProtocol.Json)
            ?? throw new InvalidDataException("Missing request body.");
        try
        {
            if (request.ProtocolVersion != 1 || request.ProfileId != identity.ProfileId ||
                request.MasterProfileId != identity.MasterProfileId || request.MasterSessionId == Guid.Empty)
                return Reply(403, new { error = "PHOTOMETRY_PEER_BINDING_MISMATCH" });
            var path = request.Path.Split('?')[0];
            if (request.Method == "GET" && path == "/api/v1/health")
            {
                ValidateActiveConfiguration();
                return Reply(200, new QhyServiceHealth("UVEX-ADV-QHY", "ok", true, proof!, DateTimeOffset.UtcNow, identity));
            }
            if (request.WorkerSessionId != identity.SessionId) return Reply(409, new { error = "PHOTOMETRY_WORKER_SESSION_CHANGED" });
            if (request.Method == "GET")
            {
                if (path == "/api/v1/camera") return Reply(200, coordinator.CameraStatus);
                if (path == "/api/v1/jobs/lookup")
                {
                    var query = HttpUtility.ParseQueryString(request.Path.Contains('?') ? request.Path[(request.Path.IndexOf('?') + 1)..] : "");
                    var found = coordinator.FindByClientRequest(query["observationRunId"] ?? "",
                        Enum.Parse<QhyJobKind>(query["kind"] ?? ""), query["clientRequestId"] ?? "");
                    return Reply(found is null ? 404 : 200, found ?? (object)new { error = "Not found" });
                }
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4 && parts[0] == "api" && parts[1] == "v1" && parts[2] == "jobs" && Guid.TryParse(parts[3], out var jobId))
                {
                    if (parts.Length == 5 && parts[4] == "preview")
                    {
                        var preview = coordinator.GetLatestPreview(jobId);
                        return preview is null ? Reply(404, new { error = "No frame" }) :
                            new(200, Convert.ToBase64String(preview.PngBytes), "image/png", [], identity);
                    }
                    if (parts.Length == 4 && coordinator.GetJob(jobId) is { } job) return Reply(200, job);
                }
                return Reply(404, new { error = "Read endpoint not found" });
            }
            if (request.Method != "POST") return Reply(405, new { error = "Method not allowed" });
            // Cancel/pause remains possible after a configuration fault. No action
            // that connects, resumes, renews permission or captures may bypass it.
            var isStopping = path.EndsWith("/cancel", StringComparison.Ordinal) || path.EndsWith("/pause", StringComparison.Ordinal);
            if (!isStopping) ValidateActiveConfiguration();
            var active = coordinator.RecentJobs().Any(j => !IsTerminal(j.State));
            if (active && activeMasterSession != request.MasterSessionId)
                return Reply(409, new { error = "PHOTOMETRY_MASTER_SESSION_CHANGED: local operator takeover required" });
            if (operatorPaused && !isStopping)
                return Reply(409, new { error = "PHOTOMETRY_OPERATOR_PAUSED: automation may not override manual pause" });
            if (path == "/api/v1/camera/connect") return Reply(200, await coordinator.ConnectCameraAsync(token));
            QhyJobControlResponse? started = null;
            if (path == "/api/v1/jobs/acquisition")
            {
                var acquisition = Body<AcquisitionJobRequest>();
                RequireJobBinding(acquisition.NightSetupId, acquisition.ClientRequestId);
                RequireCompatibleActiveJob(acquisition.ObservationRunId, QhyJobKind.Acquisition, acquisition.ClientRequestId!);
                BindFocusPolicy(acquisition.ObservationRunId, acquisition.FocusPolicy);
                activeMasterSession = request.MasterSessionId; started = StartControlled(() => coordinator.StartAcquisition(acquisition));
            }
            else if (path == "/api/v1/jobs/photometry")
            {
                var photometry = Body<PhotometryJobRequest>();
                RequireJobBinding(photometry.NightSetupId, photometry.ClientRequestId);
                RequireCompatibleActiveJob(photometry.ObservationRunId, QhyJobKind.Photometry, photometry.ClientRequestId!);
                BindFocusPolicy(photometry.ObservationRunId, photometry.FocusPolicy);
                activeMasterSession = request.MasterSessionId; started = StartControlled(() => coordinator.StartPhotometry(photometry));
            }
            if (started is not null)
            {
                return Reply(200, started.Job, new()
                {
                    [QhyControlProtocol.OwnerTokenHeaderName] = started.OwnerToken,
                    [QhyControlProtocol.LeaseExpiresUtcHeaderName] = started.LeaseExpiresUtc.ToString("O", CultureInfo.InvariantCulture),
                });
            }
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 5 && segments[0] == "api" && segments[1] == "v1" && segments[2] == "jobs" && Guid.TryParse(segments[3], out var id))
            {
                if (segments.Length == 5 && segments[4] == "pause") return Reply(200, await coordinator.PauseAsync(id, Body<QhyOwnerControlRequest>(), token));
                if (segments.Length == 5 && segments[4] == "cancel")
                {
                    var control = Body<QhyOwnerControlRequest>();
                    if (operatorPaused && control.YieldToAcquisitionRequestId is not null)
                        return Reply(409, new { error = "PHOTOMETRY_OPERATOR_PAUSED: Manual intervention takes precedence over acquisition priority." });
                    return Reply(200, await coordinator.CancelAsync(id, control, token));
                }
                if (segments.Length == 5 && segments[4] == "resume") return Reply(200, await coordinator.ResumeAsync(id, Body<QhyResumeRequest>(), token));
                if (segments.Length == 6 && segments[4] == "lease" && segments[5] == "renew") return Reply(200, await coordinator.RenewLeaseAsync(id, Body<QhyLeaseRenewalRequest>(), token));
            }
            return Reply(403, new { error = "PHOTOMETRY_COMMAND_FORBIDDEN: no arbitrary device, script, takeover or shared-equipment endpoint" });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { Publish(ex.Message); return Reply(409, new { error = ex.Message }); }
    }

    public async Task PauseFromUiAsync()
    {
        lock (localControls) operatorPaused = true;
        var job = LatestJob;
        if (job is not null) operatorJobs.TryAdd(job.Id, 0);
        if (coordinator is not null && job is not null && !IsTerminal(job.State))
        {
            QhyJobControlResponse control;
            lock (localControls) control = localControls[job.Id];
            await coordinator.PauseAsync(job.Id, new(control.OwnerToken, "worker-operator"), CancellationToken.None);
        }
        Publish(T("人工暂停：阻止自动恢复。", "Operator pause prevents automatic resume."));
    }

    public async Task StopFromUiAsync()
    {
        lock (localControls) operatorPaused = true;
        var job = LatestJob;
        if (job is not null) operatorJobs.TryAdd(job.Id, 0);
        if (coordinator is not null && job is not null && !IsTerminal(job.State))
        {
            QhyJobControlResponse control;
            lock (localControls) control = localControls[job.Id];
            await coordinator.CancelAsync(job.Id, new(control.OwnerToken, "worker-operator"), CancellationToken.None);
        }
        Publish(T("已请求停止；等待当前动作与保存终态。", "Stop requested; waiting for actual terminal state."));
    }

    public void AllowNewJobFromUi()
    {
        if (LatestJob is { } job && !IsTerminal(job.State)) throw new InvalidOperationException("End the previous job before authorizing a new one.");
        ValidateActiveConfiguration(); lock (localControls) operatorPaused = false;
        Publish(T("允许主控发起新作业；旧作业不自动恢复。", "New jobs permitted; old jobs are not resumed."));
    }

    public void ValidateActiveConfiguration()
    {
        ValidateSelectedDevices();
        if (receiverSequence is not null) PhotometryReceiverTemplatePolicy.Validate(receiverSequence.Sequencer.MainContainer);
        foreach (var legacy in Process.GetProcessesByName("UvexAdv.Qhy.Service"))
        {
            legacy.Dispose();
            throw new InvalidOperationException("PHOTOMETRY_LEGACY_OWNER_RUNNING: Stop and release the old QHY service before enabling native N.I.N.A. ownership; the worker never stops it automatically.");
        }
        if (identity is null || ConfigurationHash() != identity.ConfigurationSha256)
            throw new InvalidOperationException("PHOTOMETRY_CONFIGURATION_CHANGED: Stop and rebind at an idle boundary.");
    }

    private void ValidateSelectedDevices()
    {
        NinaInstancePolicy.ValidateWorkerProfile(profiles, settings);
        var p = profiles.ActiveProfile;
        if (p.ImageFileSettings.FileType != NINA.Core.Enum.FileTypeEnum.FITS ||
            p.ImageFileSettings.FITSCompressionType != NINA.Core.Enum.FITSCompressionTypeEnum.NONE ||
            string.IsNullOrWhiteSpace(p.ImageFileSettings.FilePath))
            throw new InvalidOperationException("PHOTOMETRY_NATIVE_FITS_REQUIRED: Configure a native output directory and uncompressed FITS before enabling the worker.");
        if (string.IsNullOrWhiteSpace(settings.PhotometryCameraId) || p.CameraSettings.Id != settings.PhotometryCameraId ||
            string.IsNullOrWhiteSpace(settings.PhotometryFilterWheelId) || p.FilterWheelSettings.Id != settings.PhotometryFilterWheelId ||
            string.IsNullOrWhiteSpace(settings.PhotometryFocuserId) || p.FocuserSettings.Id != settings.PhotometryFocuserId)
            throw new InvalidOperationException("PHOTOMETRY_DEVICE_BINDING_CHANGED: Camera, wheel and focuser selections must match the bound photometry devices.");
    }

    private Dictionary<string, int> FilterPositions() => profiles.ActiveProfile.FilterWheelSettings.FilterWheelFilters
        .ToDictionary(f => f.Name, f => (int)f.Position, StringComparer.OrdinalIgnoreCase);

    private string ConfigurationHash() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        ProfileId, MasterProfileId, settings.InstanceRole, settings.PhotometryCameraId,
        settings.PhotometryFilterWheelId, settings.PhotometryFocuserId, settings.QhyReadoutMode,
        Sdk = sdkHash, Plugin = pluginSha256, TemplateContract = PhotometryReceiverTemplatePolicy.Contract,
        Files = profiles.ActiveProfile.ImageFileSettings,
        Filters = profiles.ActiveProfile.FilterWheelSettings.FilterWheelFilters.Select(f => new { f.Name, f.Position, f.FocusOffset }).ToArray(),
        Focus = profiles.ActiveProfile.FocuserSettings,
        profiles.ActiveProfile.FilterWheelSettings.DisableGuidingOnFilterChange,
    }, PhotometryPipeProtocol.Json)));

    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    internal static bool IsTerminal(QhyJobState state) => state is QhyJobState.Completed or QhyJobState.Cancelled or QhyJobState.Faulted or QhyJobState.TakenOver;
    private void Publish(string message) { status = message; Changed?.Invoke(); }
    private async void OnProfileChanged(object? sender, EventArgs args)
    {
        if (!Enabled) return;
        try { await StopFromUiAsync(); } catch (Exception ex) { Publish(ex.Message); }
        server?.Dispose(); server = null;
        Publish(T("Profile 已改变；协同已停用，旧运行保留。", "Profile changed; worker disabled, old evidence retained."));
    }

    public void Dispose()
    {
        profiles.ProfileChanged -= OnProfileChanged;
        if (sequenceEventsAttached)
        {
            sequences.SequenceStarting -= OnSequenceStarting;
            sequences.SequenceFinished -= OnSequenceFinished;
        }
        server?.Dispose(); server = null;
        if (coordinator is not null) _ = coordinator.DisposeAsync();
        deviceClaims?.Dispose(); deviceClaims = null;
    }

    internal static void RequireJobBinding(string? nightSetupId, string? clientRequestId)
    {
        if (string.IsNullOrWhiteSpace(nightSetupId) || string.IsNullOrWhiteSpace(clientRequestId))
            throw new InvalidDataException("PHOTOMETRY_JOB_BINDING_REQUIRED: A shared Night Setup ID and idempotent request ID are required before acquisition.");
    }

    private void BindFocusPolicy(string runId, QhyFocusRunPolicy? policy)
    {
        if (policy is null) throw new InvalidOperationException("PHOTOMETRY_FOCUS_POLICY_REQUIRED: The master must supply the locked Night Setup focus policy.");
        adapter!.BindFocusPolicy(runId, policy, () => QhyFocusBudgetStore.Open(focusLedgerDirectory, runId, identity!.ConfigurationSha256, policy));
    }

    private void RequireCompatibleActiveJob(string runId, QhyJobKind kind, string clientRequestId)
    {
        var active = coordinator!.RecentJobs().FirstOrDefault(j => !IsTerminal(j.State));
        if (active is not null && (active.ObservationRunId != runId || active.Kind != kind || active.ClientRequestId != clientRequestId))
            throw new InvalidOperationException("PHOTOMETRY_BUSY: A different job still owns the devices and focus policy.");
    }

    private QhyJobControlResponse StartControlled(Func<QhyJobControlResponse> start)
    {
        lock (localControls)
        {
            if (operatorPaused) throw new InvalidOperationException("PHOTOMETRY_OPERATOR_PAUSED: Manual intervention won the start race.");
            var response = start(); localControls[response.Job.Id] = response;
            return response;
        }
    }

    private Task OnSequenceStarting(object sender, EventArgs args)
    {
        if (!Enabled) return Task.CompletedTask;
        ValidateActiveConfiguration();
        if (sender is not ISequence2VM advanced)
            throw new InvalidOperationException("PHOTOMETRY_TEMPLATE_FORBIDDEN: Use the reviewed Advanced Sequencer receiver, not an independent imaging sequence.");
        PhotometryReceiverTemplatePolicy.Validate(advanced.Sequencer.MainContainer);
        if (LatestJob is { } job && !IsTerminal(job.State))
            throw new InvalidOperationException("PHOTOMETRY_BUSY: End the direct worker job before starting a receiver sequence.");
        receiverSequence = advanced;
        return Task.CompletedTask;
    }

    private async Task OnSequenceFinished(object sender, EventArgs args)
    {
        if (receiverSequence is null) return;
        await StopFromUiAsync();
        receiverExecuting = false; receiverSequence = null;
    }

    internal void BeginReceiver()
    {
        if (!Enabled || receiverSequence is null)
            throw new InvalidOperationException("PHOTOMETRY_RECEIVER_NOT_ARMED: Enable this session in the Photometry dock before starting the reviewed receiver sequence.");
        PhotometryReceiverTemplatePolicy.Validate(receiverSequence.Sequencer.MainContainer);
        ValidateActiveConfiguration(); receiverExecuting = true;
    }

    internal async Task EndReceiverAsync()
    {
        await StopFromUiAsync();
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        while (LatestJob is { } job && !IsTerminal(job.State))
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("PHOTOMETRY_STOP_UNCONFIRMED: Inspect the native exposure/save operation; stop acknowledgement is not terminal proof.");
            await Task.Delay(100);
        }
        receiverExecuting = false;
    }

    private static string T(string zh, string en) => ObservationUiPresentation.Text(zh, en, ObservationStaticTextLocalization.EffectiveCulture);
}
