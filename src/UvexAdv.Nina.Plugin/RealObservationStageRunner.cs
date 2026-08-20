using System.Globalization;
using System.IO;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using UvexAdv.Core;
using UvexAdv.Observatory;
using UvexAdv.Phd2;
using UvexAdv.Qhy.Core;
using UvexAdv.Spectroscopy;

namespace UvexAdv.Nina.Plugin;

[SupportedOSPlatform("windows")]
internal sealed partial class RealObservationStageRunner : ObservationStageRunnerBase, IObservationRunProvenanceSource, IRealObservationRunOwnershipSource, IAsyncDisposable
{
    // Command-acceptance tolerance, not an optical-centering tolerance. A
    // stopped slew farther away cannot be treated as an attained waypoint.
    private const double MountCommandArrivalToleranceArcseconds = 2d;

    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly ObservationCoordinatorHost host;
    private readonly UvexPluginSettings settings;
    private readonly RealRunConfiguration configuration;
    private readonly ITelescopeMediator telescopeMediator;
    private readonly IGuiderMediator guiderMediator;
    private readonly IFocuserMediator focuserMediator;
    private readonly ICameraMediator cameraMediator;
    private readonly IImagingMediator imagingMediator;
    private readonly IImageSaveMediator imageSaveMediator;
    private readonly IImageSolver imageSolver;
    private readonly string solverIdentity;
    private readonly IImageDataFactory imageDataFactory;
    private readonly IProfileService profileService;
    private readonly ISafetyMonitorMediator safetyMonitorMediator;
    private readonly IDomeMediator domeMediator;
    private readonly IWeatherDataMediator weatherDataMediator;
    private readonly IFlatDeviceMediator flatDeviceMediator;
    private readonly IProgress<ApplicationStatus> progress;
    private readonly WindowsFocusDomainEvidence focusDomainEvidence = new();
    private readonly QhyServiceClient qhy;
    private readonly Lazy<QhyServiceHealth> lockedQhyServiceHealth;
    private readonly Phd2Client phd2;
    private readonly ConcurrentDictionary<Guid, byte> activeQhyJobs = new();
    private readonly ConcurrentDictionary<Guid, (long Attempted, long Accepted)> qhyFrameTotals = new();
    private readonly ConcurrentDictionary<Guid, byte> publishedQhyFrames = new();
    private readonly ConcurrentDictionary<string, byte> publishedEvidencePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PendingQhyRequest> pendingQhyRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, string> qhyLeaseFailures = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<G3SlitIlluminationCommandEvidence>> slitIlluminationEvidence = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource qhyLeaseLifetime = new();
    private readonly Task qhyLeaseRenewalLoop;
    private readonly SemaphoreSlim slitIlluminationGate = new(1, 1);
    private DateTimeOffset? fineAcquisitionStartedUtc;
    private LoadedCommissioningPreset? commissioning;
    private LoadedNightSetupSnapshot? nightSetup;
    private Phd2ProfileBindingSnapshot? phdProfileEvidence;
    private Guid? qhyAcquisitionJobId;
    private int qhyAcquisitionAttempt;
    private QhyJobSnapshot? lastQhyAcquisition;
    private Guid? qhyAcquisitionMountReadbackJobId;
    private G3FrameMountReadback? qhyAcquisitionBeforeJobMountReadback;
    private QhyAcceptedFrameMountBinding? lastQhyAcceptedFrameMountBinding;
    private LiveFocusMetricState? currentQhyFocusMetric;
    private PlateSolveEvidence? lastQhySolve;
    private G3FieldState? lastG3Field;
    private C11MainFocusOwnerSnapshot? currentC11MainFocusOwner;
    private long? validatedG3GuideConnectionEpoch;
    private long? validatedG3GuideEpoch;
    private Phd2SlitPlacementSession? phd2SlitPlacementSession;
    private Phd2LockShiftPendingState? pendingPhd2LockShift;
    private Guid? photometryJobId;
    private int qhyPhotometryAttempt;
    private double cumulativeCorrectionDegrees;
    private int correctionAttempts;
    private double qhyCoarseCumulativeArcseconds;
    private int qhyCoarseCorrectionAttempts;
    private DateTimeOffset? qhyCoarseStartedUtc;
    private QhyPendingCoarseReturn? pendingQhyCoarseReturn;
    private double? selectedAtrExposureSeconds;
    private int savedAtrFrames;
    private int attemptedAtrFrames;
    private int retainedAtrScienceFrames;
    private int attemptedAtrProbeFrames;
    private int retainedAtrProbeFrames;
    private int acceptedAtrProbeFrames;
    private bool atrReprobeRequired;
    private string? invalidatedCommissioningSha256;
    private string? commissioningInvalidReason;
    private string? observationRunId;
    private int evidenceOrdinal;
    private int executingStage = -1;
    private int resumeRecoveryRequired;
    private int phd2GuidingEverStarted;
    private UvexServiceClient? activeSlitIlluminationClient;
    private UvexServiceClient.UvexLeaseSession? activeSlitIlluminationLease;
    private string? activeSlitIlluminationSequenceId;
    private string? slitIlluminationSafetyIssue;
    private string? wideToSlitTransferEvidencePath;
    private QhyToG3TransferCandidate? latestQhyG3TransferCandidate;
    private string? latestQhyG3TransferCandidateEvidencePath;
    private string qhyG3FastPairOutcome = "NotAttempted";
    private int qhyG3FastPairAttempt;
    private G3PendingSearchReturn? pendingG3SearchReturn;
    private G3AcquisitionMotionState? durableG3AcquisitionMotion;
    private readonly SemaphoreSlim g3AcquisitionRecoveryLock = new(1, 1);
    private SlitPlacementPendingState? pendingSlitPlacement;
    private string? slitPlacementBudgetLineageId;
    private readonly SemaphoreSlim slitPlacementRecoveryLock = new(1, 1);
    private readonly AsyncLocal<int> slitPlacementRecoveryDepth = new();
    private bool disposed;

    string IRealObservationRunOwnershipSource.RealObservationOwnershipLockPath => Path.Combine(
        SlitPlacementObservationsRoot(),
        "control",
        "real-observation-owner.lock");

    public RealObservationStageRunner(
        ObservationCoordinatorHost host,
        UvexPluginSettings settings,
        RealRunConfiguration configuration,
        ITelescopeMediator telescopeMediator,
        IGuiderMediator guiderMediator,
        IFocuserMediator focuserMediator,
        ICameraMediator cameraMediator,
        IImagingMediator imagingMediator,
        IImageSaveMediator imageSaveMediator,
        IPlateSolverFactory plateSolverFactory,
        IImageDataFactory imageDataFactory,
        IProfileService profileService,
        ISafetyMonitorMediator safetyMonitorMediator,
        IDomeMediator domeMediator,
        IWeatherDataMediator weatherDataMediator,
        IFlatDeviceMediator flatDeviceMediator,
        IProgress<ApplicationStatus> progress)
    {
        this.host = host;
        this.settings = settings;
        this.telescopeMediator = telescopeMediator;
        this.guiderMediator = guiderMediator;
        this.focuserMediator = focuserMediator;
        this.cameraMediator = cameraMediator;
        this.imagingMediator = imagingMediator;
        this.imageSaveMediator = imageSaveMediator;
        this.imageDataFactory = imageDataFactory;
        this.profileService = profileService;
        this.safetyMonitorMediator = safetyMonitorMediator;
        this.domeMediator = domeMediator;
        this.weatherDataMediator = weatherDataMediator;
        this.flatDeviceMediator = flatDeviceMediator;
        this.progress = progress;
        var plateSettings = profileService.ActiveProfile.PlateSolveSettings;
        var primarySolver = plateSolverFactory.GetPlateSolver(plateSettings);
        var blindSolver = plateSolverFactory.GetBlindSolver(plateSettings);
        imageSolver = plateSolverFactory.GetImageSolver(primarySolver, blindSolver);
        solverIdentity = $"{primarySolver.GetType().FullName}, {primarySolver.GetType().Assembly.GetName().Version}";
        this.configuration = configuration;
        qhy = new QhyServiceClient(configuration.QhyServiceUrl);
        lockedQhyServiceHealth = new Lazy<QhyServiceHealth>(
            LoadInitialQhyServiceHealth,
            LazyThreadSafetyMode.ExecutionAndPublication);
        phd2 = new Phd2Client(new Phd2ClientOptions
        {
            Host = configuration.Phd2Host,
            Port = configuration.Phd2Port,
            AllowNonLoopbackEndpoint = false,
        });
        qhyLeaseRenewalLoop = RenewQhyLeasesAsync(qhyLeaseLifetime.Token);
    }

    public ObservationRunLockedMetadata LockedMetadata
    {
        get
        {
            // The manifest is initialized before the first stage. Fetching this
            // read-only loopback proof here is therefore the only point at which
            // the exact service configuration can become an immutable run binding.
            // The first interlock fetches it again and rejects any drift.
            var qhyHealth = lockedQhyServiceHealth.Value;
            var qhyProof = qhyHealth.Configuration;
            var hashes = new Dictionary<string, string>
            {
                ["actionConfigurationSha256"] = configuration.ActionConfigurationSha256,
            };
            AddIfPresent(
                hashes,
                "commissioningHardwareFingerprintSha256",
                configuration.Commissioning.HardwareFingerprintSha256);
            AddIfPresent(hashes, "qhyNativeSdkSha256", qhyProof.NativeSdkSha256);
            AddIfPresent(hashes, "qhyNativeFilterPositionsSha256", qhyProof.NativeFilterPositionsSha256);
            var labels = new Dictionary<string, string> { ["adapter"] = "real" };
            AddIfPresent(labels, "nightSetupId", configuration.NightSetup.NightSetupId);
            AddIfPresent(labels, "commissioningPresetId", configuration.Commissioning.PresetId);
            AddIfPresent(labels, "plateSolver", configuration.PlateSolver.PrimarySolverSelection);
            AddIfPresent(labels, "blindSolver", configuration.PlateSolver.BlindSolverSelection);
            AddIfPresent(labels, "atrCameraId", configuration.NightSetup.AtrStableId);
            AddIfPresent(labels, "g3CameraId", configuration.Phd2.CameraStableId);
            labels["g3SaturationAdu"] = configuration.G3.SaturationAdu.ToString(CultureInfo.InvariantCulture);
            labels["g3ExposureMilliseconds"] = configuration.G3.ExposureMilliseconds.ToString(CultureInfo.InvariantCulture);
            labels["g3GainPercent"] = configuration.G3.GainPercent.ToString(CultureInfo.InvariantCulture);
            labels["g3PlateSolveExposurePresetSchemaVersion"] = configuration.G3.PlateSolveExposurePreset.SchemaVersion.ToString(CultureInfo.InvariantCulture);
            labels["g3PlateSolveExposurePresetId"] = configuration.G3.PlateSolveExposurePreset.PresetId;
            labels["g3PlateSolveExposureMilliseconds"] = string.Join(",", configuration.G3.PlateSolveExposurePreset.ExposureMilliseconds);
            labels["g3WcsCenteringSchemaVersion"] = configuration.G3.WcsCentering.SchemaVersion.ToString(CultureInfo.InvariantCulture);
            labels["g3WcsMaximumSingleArcseconds"] = configuration.G3.WcsCentering.MaximumSingleCorrectionArcseconds.ToString("R", CultureInfo.InvariantCulture);
            labels["g3WcsMaximumRadiusArcseconds"] = configuration.G3.WcsCentering.MaximumRadiusArcseconds.ToString("R", CultureInfo.InvariantCulture);
            labels["g3WcsMaximumCumulativeArcseconds"] = configuration.G3.WcsCentering.MaximumCumulativeMotionArcseconds.ToString("R", CultureInfo.InvariantCulture);
            labels["g3WcsMaximumAttempts"] = configuration.G3.WcsCentering.MaximumCorrectionAttempts.ToString(CultureInfo.InvariantCulture);
            labels["g3WcsMaximumElapsedSeconds"] = configuration.G3.WcsCentering.MaximumElapsedTime.TotalSeconds.ToString("R", CultureInfo.InvariantCulture);
            labels["g3MotionWorstCaseActionSeconds"] = configuration.G3.MotionWorstCaseActionSeconds.ToString("R", CultureInfo.InvariantCulture);
            labels["g3MotionPostSlewSettleSeconds"] = configuration.G3.MotionPostSlewSettleSeconds.ToString("R", CultureInfo.InvariantCulture);
            labels["brightTargetWingCentroidEnabled"] = configuration.G3.EffectiveBrightTarget.Enabled.ToString();
            labels["brightTargetMinimumG3ExposureMilliseconds"] = configuration.G3.EffectiveBrightTarget.MinimumG3ExposureMilliseconds.ToString(CultureInfo.InvariantCulture);
            labels["ghostAssistanceMode"] = configuration.G3.GhostAssistanceMode.ToString();
            labels["allowDegradedSupervisedScience"] = configuration.AllowDegradedSupervisedScience.ToString(CultureInfo.InvariantCulture);
            labels["wideToSlitTransferMode"] = configuration.G3.WideToSlitTransferMode.ToString();
            labels["qhyG3FastPairEnabled"] = configuration.G3.EffectiveFastSolvePair.Enabled.ToString();
            labels["qhyG3FastPairPolicyId"] = configuration.G3.EffectiveFastSolvePair.PolicyId;
            labels["qhyG3FastPairQuickExposureSeconds"] = configuration.G3.EffectiveFastSolvePair.QuickQhyExposureSeconds.ToString("R", CultureInfo.InvariantCulture);
            labels["qhyG3FastPairMaximumMidpointSeparationSeconds"] = configuration.G3.EffectiveFastSolvePair.MaximumPairMidpointSeparation.TotalSeconds.ToString("R", CultureInfo.InvariantCulture);
            labels["qhyG3FastPairMaximumWallClockSeconds"] = configuration.G3.EffectiveFastSolvePair.MaximumPairWallClock.TotalSeconds.ToString("R", CultureInfo.InvariantCulture);
            labels["qhyG3FastPairMaximumMountSpanArcseconds"] = configuration.G3.EffectiveFastSolvePair.MaximumMountSpanArcseconds.ToString("R", CultureInfo.InvariantCulture);
            labels["g3SearchPattern"] = configuration.G3.Search.Pattern.ToString();
            labels["g3SearchStepArcseconds"] = configuration.G3.Search.StepArcseconds.ToString("R", CultureInfo.InvariantCulture);
            labels["g3SearchMaximumRadiusArcseconds"] = configuration.G3.Search.MaximumRadiusArcseconds.ToString("R", CultureInfo.InvariantCulture);
            labels["g3SearchMaximumCumulativeArcseconds"] = configuration.G3.Search.MaximumCumulativeMotionArcseconds.ToString("R", CultureInfo.InvariantCulture);
            labels["g3SearchMaximumAttempts"] = configuration.G3.Search.MaximumAttempts.ToString(CultureInfo.InvariantCulture);
            labels["g3SearchMaximumElapsedSeconds"] = configuration.G3.Search.MaximumElapsedTime.TotalSeconds.ToString("R", CultureInfo.InvariantCulture);
            labels["qhyCoarseCenteringSchemaVersion"] = configuration.Qhy.CoarseCenteringLimits.SchemaVersion.ToString(CultureInfo.InvariantCulture);
            labels["qhyCoarseMaximumSingleArcseconds"] = configuration.Qhy.CoarseCenteringLimits.MaximumSingleCorrectionArcseconds.ToString("R", CultureInfo.InvariantCulture);
            labels["qhyCoarseMaximumCumulativeArcseconds"] = configuration.Qhy.CoarseCenteringLimits.MaximumCumulativeCorrectionArcseconds.ToString("R", CultureInfo.InvariantCulture);
            labels["qhyCoarseMaximumAttempts"] = configuration.Qhy.CoarseCenteringLimits.MaximumCorrectionAttempts.ToString(CultureInfo.InvariantCulture);
            labels["qhyCoarseMaximumElapsedSeconds"] = configuration.Qhy.CoarseCenteringLimits.MaximumElapsedTime.TotalSeconds.ToString("R", CultureInfo.InvariantCulture);
            AddIfPresent(labels, "ninaFilterWheelSelection", profileService.ActiveProfile.FilterWheelSettings.Id);
            AddIfPresent(labels, "ninaGuiderAdapterSelection", profileService.ActiveProfile.GuiderSettings.GuiderName);
            AddIfPresent(labels, "phd2RegistryCameraName", configuration.Phd2.CameraName);
            AddIfPresent(labels, "phd2RegistryMountName", configuration.Phd2.MountName);
            AddIfPresent(labels, "phd2RuntimeCameraName", configuration.Phd2.RuntimeCameraName);
            AddIfPresent(labels, "phd2RuntimeMountName", configuration.Phd2.RuntimeMountName);
            AddIfPresent(labels, "qhyCameraId", configuration.NightSetup.QhyStableId);
            AddIfPresent(labels, "qhyServiceAdapter", qhyProof.Adapter);
            AddIfPresent(labels, "qhyExpectedModel", qhyProof.ExpectedModel);
            labels["qhyServiceSimulator"] = qhyProof.Simulator.ToString(CultureInfo.InvariantCulture);
            labels["qhyNativeReadoutMode"] = qhyProof.NativeReadoutMode.ToString(CultureInfo.InvariantCulture);
            AddIfPresent(labels, "telescopeId", configuration.ExpectedTelescopeId);
            return new ObservationRunLockedMetadata(
                NightSetupSha256: configuration.NightSetup.SnapshotSha256,
                CommissioningPresetSha256: configuration.Commissioning.PresetSha256,
                Phd2ProfileEvidenceSha256: configuration.Phd2.ProfileEvidenceSha256,
                QhyConfigurationSha256: qhyProof.ConfigurationSha256,
                AdditionalHashes: hashes,
                Labels: labels);
        }
    }

    public override async Task<StageResult> ExecuteStageAsync(
        ObservationStage stage,
        ObservationContext context,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var existingRunId = Interlocked.CompareExchange(
            ref observationRunId,
            context.Plan.ObservationRunId,
            null);
        if (existingRunId is not null &&
            !string.Equals(existingRunId, context.Plan.ObservationRunId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"A real stage runner bound to run '{existingRunId}' cannot execute run '{context.Plan.ObservationRunId}'.");
        }
        while (true)
        {
            try
            {
                Volatile.Write(ref executingStage, (int)stage);
                // This invocation is already the fresh outer stage stack. Consume
                // the preceding pause epoch before durable recovery so recovery
                // checkpoints do not reject themselves forever. A new pause sets
                // the flag again and still invalidates this stack.
                var resumeRecoveryWasRequired = Interlocked.Exchange(ref resumeRecoveryRequired, 0) != 0;
                var durableG3Recovery = await RecoverDurableG3AcquisitionBeforeStageAsync(
                    stage,
                    context,
                    cancellationToken).ConfigureAwait(false);
                if (durableG3Recovery is not null) return durableG3Recovery;
                var durableSlitRecovery = await RecoverDurableSlitPlacementBeforeStageAsync(
                    stage,
                    context,
                    cancellationToken).ConfigureAwait(false);
                if (durableSlitRecovery is not null) return durableSlitRecovery;
                resumeRecoveryWasRequired |= Interlocked.Exchange(ref resumeRecoveryRequired, 0) != 0;
                var resumeRecovery = await RecoverInterruptedStageAsync(
                    stage,
                    context,
                    resumeRecoveryWasRequired,
                    cancellationToken).ConfigureAwait(false);
                if (resumeRecovery is not null) return resumeRecovery;
                return stage switch
                {
                    ObservationStage.ValidateNightSetup => await ValidateNightSetupAsync(context, cancellationToken).ConfigureAwait(false),
                    ObservationStage.SlewToCatalogTarget => await SlewToCatalogTargetAsync(context, cancellationToken).ConfigureAwait(false),
                    ObservationStage.AcquireQhyWideField => await AcquireQhyWideFieldAsync(context, cancellationToken).ConfigureAwait(false),
                    ObservationStage.CoarseCenter => await CoarseCenterAsync(context, cancellationToken).ConfigureAwait(false),
                    ObservationStage.AcquireG3SlitField => await AcquireG3SlitFieldAsync(context, cancellationToken).ConfigureAwait(false),
                    ObservationStage.PlaceTargetOnSlit => await PlaceTargetOnSlitAsync(context, cancellationToken).ConfigureAwait(false),
                    ObservationStage.StartGuiding => await StartGuidingAsync(context, cancellationToken).ConfigureAwait(false),
                    ObservationStage.StartQhyPhotometry => await StartQhyPhotometryAsync(context, cancellationToken).ConfigureAwait(false),
                    ObservationStage.SelectAtrExposure => await SelectAtrExposureAsync(context, cancellationToken).ConfigureAwait(false),
                    ObservationStage.RunScienceBlock => await RunScienceBlockAsync(context, cancellationToken).ConfigureAwait(false),
                    ObservationStage.FinalizeObservation => await FinalizeObservationAsync(context, cancellationToken).ConfigureAwait(false),
                    _ => Attention(stage, "STAGE_UNSUPPORTED", $"No real adapter exists for {stage}.")
                };
            }
            catch (ResumeStageRestartException)
            {
                await WriteAuditBestEffortAsync("resume-stale-stage-stack-discarded", new
                {
                    stage = stage.ToString(),
                    reason = "Pause/takeover completed while this stage was suspended; pre-checkpoint local calculations were discarded.",
                }).ConfigureAwait(false);
                continue;
            }
            catch (PhysicalActionGateException ex)
            {
                await WriteAuditBestEffortAsync("physical-action-withheld", new
                {
                    stage = stage.ToString(),
                    ex.Gate.Code,
                    disposition = ex.Gate.Disposition.ToString(),
                    ex.Gate.Message,
                }).ConfigureAwait(false);
                return new StageResult(ex.Gate);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    $"{stage} surfaced a non-cancellation exception after run cancellation; stop-only cancellation cleanup remains authoritative.",
                    ex,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                var cleanup = await CleanupAfterFailureAsync(
                    $"{stage}: {ex.Message}",
                    cancellationToken,
                    allowMechanicalActions: true).ConfigureAwait(false);
                InvalidateStageState(stage);
                var suffix = cleanup.Count == 0 ? string.Empty : $" Cleanup: {string.Join("; ", cleanup)}";
                await WriteAuditBestEffortAsync("real-stage-exception", new
                {
                    stage = stage.ToString(),
                    exception = ex.ToString(),
                    cleanup,
                }).ConfigureAwait(false);
                return Attention(stage, "STAGE_EXCEPTION", $"{stage} failed without being treated as success: {ex.Message}.{suffix}");
            }
        }
    }

    public override async Task<GateResult> RevalidateAsync(ObservationContext context, CancellationToken cancellationToken)
    {
        var validation = await EvaluateInterlocksAsync(context, connectQhy: false, cancellationToken).ConfigureAwait(false);
        return validation;
    }

    public override async Task OnPausedAsync(ObservationContext context, CancellationToken cancellationToken)
    {
        MarkResumeRecoveryRequired();
        var failures = new List<string>();
        var slitOff = await EnsureSlitIlluminationOffAsync(
            "coordinator pause",
            releaseLeaseOnSuccess: true,
            cancellationToken).ConfigureAwait(false);
        if (slitOff.Issue is not null) failures.Add(slitOff.Issue);
        Phd2StopCaptureResult? phdStop = null;
        if (phd2.IsConnected)
        {
            try
            {
                phdStop = await phd2.PauseAutomationAndStopCaptureAsync(cancellationToken).ConfigureAwait(false);
                ValidateConfirmedPhdStop(phdStop, "pause");
            }
            catch (Exception ex)
            {
                failures.Add($"PHD2 pause/stop was not confirmed: {ex.Message}");
            }
        }
        else if (Volatile.Read(ref phd2GuidingEverStarted) != 0)
        {
            failures.Add("PHD2 is disconnected after guiding was started; pause cannot confirm stop_capture reached Stopped/Selected.");
        }
        // Pause is a hard no-new-motion boundary. Any durable mount or PHD2
        // lock-return ledger remains authoritative on disk and is reconciled
        // by OnResumingAsync before any device is resumed.
        foreach (var id in activeQhyJobs.Keys.ToArray())
        {
            try
            {
                var before = await qhy.GetJobAsync(id, cancellationToken).ConfigureAwait(false);
                if (before is not null) ObserveQhySnapshot(before);
                if (before?.State is QhyJobState.Completed or QhyJobState.Cancelled or QhyJobState.Faulted or QhyJobState.TakenOver)
                {
                    activeQhyJobs.TryRemove(id, out _);
                    continue;
                }
                var after = before?.State is QhyJobState.Running or QhyJobState.Queued or QhyJobState.Pausing
                    ? await qhy.PauseAsync(id, cancellationToken).ConfigureAwait(false)
                    : before;
                if (after?.State == QhyJobState.Pausing)
                {
                    after = await qhy.WaitForPausedOrTerminalAsync(
                        id,
                        TimeSpan.FromSeconds(20),
                        cancellationToken).ConfigureAwait(false);
                }
                if (after is not null) ObserveQhySnapshot(after);
                if (after is null || after.State is not (QhyJobState.Paused or QhyJobState.PausedNeedsAttention or QhyJobState.Completed or QhyJobState.Cancelled or QhyJobState.Faulted or QhyJobState.TakenOver))
                {
                    failures.Add($"QHY job {id:D} pause state is {after?.State.ToString() ?? "missing"}.");
                }
            }
            catch (Exception ex) { failures.Add($"QHY job {id:D}: {ex.Message}"); }
        }
        await WriteAuditBestEffortAsync("real-run-paused", new
        {
            phd2Connected = phd2.IsConnected,
            phd2State = phd2.Snapshot.AppState.ToString(),
            phd2AutomationPaused = phd2.IsAutomationPaused,
            phd2Stop = phdStop is null ? null : new
            {
                initialState = phdStop.InitialState.ToString(),
                finalState = phdStop.FinalState.ToString(),
                phdStop.StopCommandSent,
                phdStop.ConfirmedIdle,
                phdStop.CompletedUtc,
            },
            activeQhyJobs = activeQhyJobs.Keys.ToArray(),
            durableSlitPendingRetained = pendingSlitPlacement is not null,
            durablePhd2LockPendingRetained = pendingPhd2LockShift is not null,
            automaticMountOrLockReturnAttempted = false,
            recoveryDeferredUntilExplicitResume = true,
            failures,
        }).ConfigureAwait(false);
        if (failures.Count > 0) throw new InvalidOperationException($"Pause interlock failed: {string.Join("; ", failures)}");
    }

    public override async Task OnResumingAsync(ObservationContext context, CancellationToken cancellationToken)
    {
        var slitRecovery = await ReturnDurableSlitPlacementForLifecycleAsync(
            context,
            "coordinator resume before device resumption",
            cancellationToken).ConfigureAwait(false);
        if (slitRecovery is { CanAdvance: false })
        {
            throw new InvalidOperationException(
                $"Resume is withheld by pending slit recovery {slitRecovery.Gate.Code}: {slitRecovery.Gate.Message} Use explicit manual takeover if automatic reported-position return cannot be authorized.");
        }

        var failures = new List<string>();
        foreach (var id in activeQhyJobs.Keys.ToArray())
        {
            try
            {
                var before = await qhy.GetJobAsync(id, cancellationToken).ConfigureAwait(false);
                if (before is not null) ObserveQhySnapshot(before);
                if (before?.State is QhyJobState.Completed or QhyJobState.Cancelled or QhyJobState.Faulted or QhyJobState.TakenOver)
                {
                    activeQhyJobs.TryRemove(id, out _);
                    if (photometryJobId == id)
                    {
                        photometryJobId = null;
                        qhyPhotometryAttempt++;
                    }
                    continue;
                }
                QhyJobSnapshot? after;
                if (before?.Kind == QhyJobKind.Photometry)
                {
                    // Photometry stays paused until a fresh G3 frame, target/slit
                    // placement and new PHD2 guide epoch have all passed.
                    after = before;
                }
                else if (before?.State is QhyJobState.Paused or QhyJobState.PausedNeedsAttention)
                {
                    var immediate = ValidateImmediatePhysicalActionGates(context);
                    if (immediate.Disposition != GateDisposition.Passed)
                    {
                        failures.Add($"QHY job {id:D} resume withheld: {immediate.Code}: {immediate.Message}");
                        continue;
                    }
                    after = await qhy.ResumeAsync(id, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    after = before;
                }
                if (after is not null) ObserveQhySnapshot(after);
                var validResumeState = before?.Kind == QhyJobKind.Photometry
                    ? after?.State is QhyJobState.Paused or QhyJobState.PausedNeedsAttention
                    : after?.State is QhyJobState.Running or QhyJobState.Queued or QhyJobState.Completed or QhyJobState.Cancelled;
                if (!validResumeState)
                {
                    failures.Add($"QHY job {id:D} resume state is {after?.State.ToString() ?? "missing"}.");
                }
            }
            catch (Exception ex) { failures.Add($"QHY job {id:D}: {ex.Message}"); }
        }
        if (failures.Count > 0) throw new InvalidOperationException($"Resume interlock failed: {string.Join("; ", failures)}");
        phd2.ResumeAutomation();
    }

    public override async Task OnTakeoverAsync(ObservationContext context, CancellationToken cancellationToken)
    {
        MarkResumeRecoveryRequired();
        var failures = new List<string>();
        var slitOff = await EnsureSlitIlluminationOffAsync(
            "manual takeover",
            releaseLeaseOnSuccess: true,
            cancellationToken).ConfigureAwait(false);
        if (slitOff.Issue is not null) failures.Add(slitOff.Issue);
        Phd2StopCaptureResult? phdStop = null;
        if (phd2.IsConnected)
        {
            try
            {
                phdStop = await phd2.PauseAutomationAndStopCaptureAsync(cancellationToken).ConfigureAwait(false);
                ValidateConfirmedPhdStop(phdStop, "takeover");
            }
            catch (Exception ex)
            {
                failures.Add($"PHD2 takeover stop was not confirmed: {ex.Message}");
            }
        }
        else if (Volatile.Read(ref phd2GuidingEverStarted) != 0)
        {
            failures.Add("PHD2 is disconnected after guiding was started; takeover cannot confirm stop_capture reached Stopped/Selected.");
        }
        string? slitTakeoverRecovery = null;
        try
        {
            var slitRecovery = await ReturnDurableSlitPlacementForLifecycleAsync(
                context,
                "manual takeover",
                cancellationToken).ConfigureAwait(false);
            if (slitRecovery is { CanAdvance: false })
            {
                // Explicit takeover remains available so the operator is never
                // locked out of a mount that requires manual recovery. The
                // durable pending file is deliberately retained and Resume
                // will still refuse to forget or cross it automatically.
                slitTakeoverRecovery = $"{slitRecovery.Gate.Code}: {slitRecovery.Gate.Message}";
            }
        }
        catch (Exception ex) { slitTakeoverRecovery = ex.Message; }
        foreach (var id in activeQhyJobs.Keys.ToArray())
        {
            try
            {
                var before = await qhy.GetJobAsync(id, cancellationToken).ConfigureAwait(false);
                if (before is not null) ObserveQhySnapshot(before);
                if (before?.State is QhyJobState.Completed or QhyJobState.Cancelled or QhyJobState.Faulted or QhyJobState.TakenOver)
                {
                    activeQhyJobs.TryRemove(id, out _);
                    continue;
                }
                var after = await qhy.TakeoverAsync(id, "UVEX operator requested manual takeover.", cancellationToken).ConfigureAwait(false);
                ObserveQhySnapshot(after);
                if (after.State is not (QhyJobState.TakenOver or QhyJobState.Completed or QhyJobState.Cancelled))
                {
                    failures.Add($"QHY job {id:D} takeover state is {after.State}.");
                }
            }
            catch (Exception ex) { failures.Add($"QHY job {id:D}: {ex.Message}"); }
        }
        await WriteAuditBestEffortAsync("real-run-takeover", new
        {
            phd2State = phd2.Snapshot.AppState.ToString(),
            guidingWasStoppedAndConfirmed = phdStop?.ConfirmedIdle == true,
            phd2FinalState = phdStop?.FinalState.ToString(),
            slitTakeoverRecovery,
            durableSlitPendingRetainedForResume = slitTakeoverRecovery is not null,
            failures,
        }).ConfigureAwait(false);
        if (failures.Count > 0) throw new InvalidOperationException($"Takeover interlock failed: {string.Join("; ", failures)}");
    }

    public override async Task OnCancelledAsync(ObservationContext context, CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        // Cancellation is a stop boundary, not fresh motion authorization.
        // Existing mount/PHD lock ledgers remain the sole recovery authority;
        // Resume or explicit takeover may reconcile them later.
        if (pendingPhd2LockShift is { } phdPending &&
            phdPending.Phase != Phd2LockShiftPendingPhase.SettledBudgetLedger)
        {
            try
            {
                phdPending = phdPending with
                {
                    Phase = Phd2LockShiftPendingPhase.ReturnRequired,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    LastReason = "Run cancellation retained this lineage for later explicit recovery; cancellation sent no lock command.",
                };
                await Phd2LockShiftPendingStore.WriteAtomicAsync(
                    Phd2LockShiftPendingPath(phdPending.ObservationRunId),
                    phdPending,
                    CancellationToken.None).ConfigureAwait(false);
                pendingPhd2LockShift = phdPending;
            }
            catch (Exception ex) { failures.Add($"PHD2 pending-ledger retention during cancellation: {ex.Message}"); }
        }
        await WriteAuditBestEffortAsync("real-run-cancellation-no-motion", new
        {
            context.Plan.ObservationRunId,
            mountSlitPendingRetained = pendingSlitPlacement is not null,
            phd2LockPendingRetained = pendingPhd2LockShift is not null,
            automaticMountOrLockReturnAttempted = false,
        }).ConfigureAwait(false);
        failures.AddRange(await CleanupAfterFailureAsync(
            "Run cancelled by operator or N.I.N.A.",
            cancellationToken,
            allowMechanicalActions: false).ConfigureAwait(false));
        if (failures.Count > 0)
        {
            throw new InvalidOperationException($"Real-run cleanup after cancellation was incomplete: {string.Join("; ", failures)}");
        }
    }

    public override async Task OnFaultedAsync(ObservationContext context, Exception cause, CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        try
        {
            var slitRecovery = await ReturnDurableSlitPlacementForLifecycleAsync(
                context,
                $"coordinator fault: {cause.Message}",
                cancellationToken).ConfigureAwait(false);
            if (slitRecovery is { CanAdvance: false }) failures.Add($"Pending slit segment: {slitRecovery.Gate.Code}: {slitRecovery.Gate.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await WriteAuditBestEffortAsync("real-run-fault-recovery-cancelled-no-further-motion", new
            {
                context.Plan.ObservationRunId,
                originalFault = cause.Message,
                durableSlitPendingRetained = pendingSlitPlacement is not null,
                cancellationStoppedFurtherReturnSegments = true,
            }).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) { failures.Add($"Pending slit segment recovery during fault handling: {ex.Message}"); }
        failures.AddRange(await CleanupAfterFailureAsync(
            $"Coordinator fault: {cause.Message}",
            cancellationToken,
            allowMechanicalActions: true).ConfigureAwait(false));
        if (failures.Count > 0)
        {
            throw new InvalidOperationException($"Real-run cleanup after fault was incomplete: {string.Join("; ", failures)}", cause);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        qhyLeaseLifetime.Cancel();
        try { await qhyLeaseRenewalLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _ = await CleanupAfterFailureAsync(
            "Real runner disposed.",
            CancellationToken.None,
            allowMechanicalActions: false).ConfigureAwait(false);
        await DisposeActiveSlitIlluminationResourcesAsync().ConfigureAwait(false);
        await phd2.DisposeAsync().ConfigureAwait(false);
        qhy.Dispose();
        qhyLeaseLifetime.Dispose();
        slitIlluminationGate.Dispose();
    }

    private async Task<StageResult> ValidateNightSetupAsync(ObservationContext context, CancellationToken cancellationToken)
    {
        var gate = await EvaluateInterlocksAsync(context, connectQhy: true, cancellationToken).ConfigureAwait(false);
        if (gate.Disposition != GateDisposition.Passed) return new StageResult(gate);
        var preset = commissioning!;
        return Passed(
            "REAL_NIGHT_SETUP_LOCKED",
            $"Night Setup '{context.Plan.NightSetupId}' passed identity, UVEX, roof/weather, horizon and commissioning-provenance gates.",
            new Dictionary<string, double>
            {
                ["commissioningPresetAgeHours"] = (DateTimeOffset.UtcNow - preset.Value.CreatedUtc).TotalHours,
                ["motionSingleLimitArcseconds"] = preset.Value.Motion.MaximumSingleCorrectionArcseconds,
                ["motionCumulativeLimitArcseconds"] = preset.Value.Motion.MaximumCumulativeCorrectionArcseconds,
            },
            Metadata(preset));
    }

    private async Task<GateResult> EvaluateInterlocksAsync(
        ObservationContext context,
        bool connectQhy,
        CancellationToken cancellationToken)
    {
        var plan = context.Plan;
        var staticIssues = ValidateStaticConfiguration(plan);
        if (staticIssues.Count > 0)
        {
            return GateResult.Unknown("REAL_CONFIGURATION_INCOMPLETE", string.Join(" ", staticIssues));
        }
        if (Volatile.Read(ref slitIlluminationSafetyIssue) is { } slitIssue)
        {
            return GateResult.Unknown(
                "SLIT_ILLUMINATION_OFF_UNVERIFIED",
                $"Automatic resume is blocked until a checked slit-illumination OFF succeeds: {slitIssue}");
        }
        var currentPlateSolver = PlateSolverRunConfiguration.CaptureCurrent(
            profileService.ActiveProfile.PlateSolveSettings,
            configuration.PlateSolver);
        if (!configuration.MatchesCurrentProfile(settings, currentPlateSolver, out var currentConfigurationSha256))
        {
            return GateResult.Unknown(
                "REAL_PROFILE_DRIFT",
                $"An action-bearing N.I.N.A. Profile value changed after the real runner was created. Locked {configuration.ActionConfigurationSha256}, current {currentConfigurationSha256}. Start a new run after deliberate revalidation.");
        }
        if (!qhyLeaseFailures.IsEmpty)
        {
            return GateResult.Unknown("QHY_CONTROL_LEASE_UNHEALTHY", string.Join(" ", qhyLeaseFailures.Values));
        }

        var qhyServiceConfigurationGate = await ValidateQhyServiceConfigurationAsync(plan, cancellationToken).ConfigureAwait(false);
        if (qhyServiceConfigurationGate.Disposition != GateDisposition.Passed) return qhyServiceConfigurationGate;

        var loaded = await RealCommissioningPresetLoader.LoadAsync(configuration, cancellationToken).ConfigureAwait(false);
        if (loaded.Preset is null)
        {
            return GateResult.Unknown("COMMISSIONING_PRESET_UNTRUSTED", string.Join(" ", loaded.Issues));
        }
        if (SameHash(loaded.Preset.Sha256, invalidatedCommissioningSha256 ?? string.Empty))
        {
            return GateResult.Fail(
                "COMMISSIONING_PRESET_INVALIDATED_THIS_RUN",
                $"Commissioning preset {loaded.Preset.Sha256} was permanently invalidated for this run: {commissioningInvalidReason}. Start a new run with a newly validated preset; Resume is not permitted to reuse it.");
        }
        commissioning = loaded.Preset;

        var lockedNight = await LockedNightSetupSnapshotLoader.LoadAsync(
            configuration,
            plan,
            commissioning,
            cancellationToken).ConfigureAwait(false);
        if (lockedNight.Snapshot is null)
        {
            return GateResult.Unknown("NIGHT_SETUP_SNAPSHOT_UNTRUSTED", string.Join(" ", lockedNight.Issues));
        }
        nightSetup = lockedNight.Snapshot;

        var connectionGate = await EnsureNinaEquipmentConnectedAsync(context, cancellationToken).ConfigureAwait(false);
        if (connectionGate.Disposition != GateDisposition.Passed) return connectionGate;

        var camera = cameraMediator.GetInfo();
        if (!camera.Connected) return GateResult.Unknown("ATR_NOT_CONNECTED", "N.I.N.A. ATR585M did not remain connected after the bounded connection attempt.");
        if (!string.Equals(camera.DeviceId, plan.ExpectedAtrCameraId, StringComparison.Ordinal))
        {
            return GateResult.Fail("ATR_IDENTITY_MISMATCH", $"Connected N.I.N.A. camera DeviceId '{camera.DeviceId}' does not match '{plan.ExpectedAtrCameraId}'.");
        }
        var cameraText = string.Join('|', camera.Name, camera.DisplayName, camera.Description, camera.DeviceId);
        if (!cameraText.Contains("ATR585", StringComparison.OrdinalIgnoreCase))
        {
            return GateResult.Fail("ATR_MODEL_MISMATCH", $"Connected N.I.N.A. camera '{camera.DisplayName ?? camera.Name}' is not identifiable as ATR585M.");
        }
        if (camera.Gain != configuration.Atr.Gain || camera.Offset != configuration.Atr.Offset || camera.BinX != configuration.Atr.Binning || camera.BinY != configuration.Atr.Binning)
        {
            return GateResult.Fail(
                "ATR_NIGHT_SETUP_MISMATCH",
                $"ATR live gain/offset/bin is {camera.Gain}/{camera.Offset}/{camera.BinX}x{camera.BinY}; expected {configuration.Atr.Gain}/{configuration.Atr.Offset}/{configuration.Atr.Binning}x{configuration.Atr.Binning}.");
        }

        var telescope = telescopeMediator.GetInfo();
        if (!telescope.Connected) return GateResult.Unknown("TELESCOPE_NOT_CONNECTED", "N.I.N.A. telescope did not remain connected after the bounded connection attempt.");
        if (!string.Equals(telescope.DeviceId, configuration.ExpectedTelescopeId, StringComparison.Ordinal))
        {
            return GateResult.Fail("TELESCOPE_IDENTITY_MISMATCH", $"Connected telescope DeviceId '{telescope.DeviceId}' does not match '{configuration.ExpectedTelescopeId}'.");
        }
        if (!telescope.CanSlew) return GateResult.Fail("TELESCOPE_CANNOT_SLEW", "The connected mount does not report slew capability.");
        var mountClockGate = ValidateMountClock();
        if (mountClockGate.Disposition != GateDisposition.Passed) return mountClockGate;

        var c11FocusOwner = ReadC11MainFocusOwner();
        var c11FocusOwnerGate = C11MainFocusPolicy.ValidateLockedPosition(c11FocusOwner, nightSetup.Value);
        if (c11FocusOwnerGate.Disposition != GateDisposition.Passed) return c11FocusOwnerGate;
        currentC11MainFocusOwner = c11FocusOwner;

        var guider = guiderMediator.GetInfo();
        if (!guider.Connected) return GateResult.Unknown("NINA_GUIDER_NOT_CONNECTED", "N.I.N.A. did not remain connected to its PHD2 guider adapter after the bounded connection attempt.");
        var guiderText = string.Join('|', guider.Name, guider.DisplayName, guider.Description, guider.DeviceId);
        if (!guiderText.Contains("PHD", StringComparison.OrdinalIgnoreCase))
        {
            return GateResult.Fail("NINA_GUIDER_IDENTITY_MISMATCH", $"N.I.N.A. guider '{guider.DisplayName ?? guider.Name}' is not identifiable as PHD2.");
        }

        UvexDeviceStatus? uvexStatus;
        using (var uvex = new UvexServiceClient(configuration.UvexServiceUrl))
        {
            uvexStatus = await uvex.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            var uvexGate = ValidateUvexStatus(uvexStatus);
            if (uvexGate.Disposition != GateDisposition.Passed) return uvexGate;
        }

        var environmentGate = ValidateEnvironment(plan);
        if (environmentGate.Disposition != GateDisposition.Passed) return environmentGate;

        var qhyState = connectQhy
            ? await ConnectQhyAtCheckpointAsync(context, cancellationToken).ConfigureAwait(false)
            : await qhy.GetCameraAsync(cancellationToken).ConfigureAwait(false);
        if (qhyState is null || !qhyState.Connected || qhyState.Identity is null)
        {
            return GateResult.Unknown("QHY_NOT_CONNECTED", "The dedicated QHY service does not report a connected camera.");
        }
        if (!string.Equals(qhyState.Identity.StableId, plan.ExpectedQhyCameraId, StringComparison.Ordinal))
        {
            return GateResult.Fail("QHY_IDENTITY_MISMATCH", $"QHY StableId '{qhyState.Identity.StableId}' does not match '{plan.ExpectedQhyCameraId}'.");
        }
        if (!qhyState.Identity.Model.Contains("QHYminiCam8", StringComparison.OrdinalIgnoreCase))
        {
            return GateResult.Fail("QHY_MODEL_MISMATCH", $"QHY service reports unexpected model '{qhyState.Identity.Model}'.");
        }

        await EnsurePhdConnectedAsync(cancellationToken).ConfigureAwait(false);
        var identity = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), cancellationToken).ConfigureAwait(false);
        if (identity.Status != Phd2ValidationStatus.Valid)
        {
            return identity.Status == Phd2ValidationStatus.Invalid
                ? GateResult.Fail("PHD2_IDENTITY_MISMATCH", string.Join(" ", identity.Failures))
                : GateResult.Unknown("PHD2_IDENTITY_INDETERMINATE", string.Join(" ", identity.IndeterminateReasons));
        }

        var profileEvidenceGate = ValidatePhdProfileBindingEvidence();
        if (profileEvidenceGate.Disposition != GateDisposition.Passed) return profileEvidenceGate;

        var c11Focus = ReadC11MainFocusOwner();
        var c11OwnerGate = C11MainFocusPolicy.ValidateLockedPosition(c11Focus, nightSetup.Value);
        if (c11OwnerGate.Disposition != GateDisposition.Passed) return c11OwnerGate;

        var focusEvidence = focusDomainEvidence.BuildLiveFocusDomains(
            new WindowsFocusDomainEvidenceInput(
                C11Connected: c11Focus.Connected,
                C11LogicalDeviceId: c11Focus.DeviceId,
                C11PositionSteps: c11Focus.Connected ? c11Focus.PositionSteps : null,
                Gs350Owner: FocusDomainConventions.Gs350Owner,
                Gs350LogicalDeviceId: FocusDomainConventions.Gs350LogicalDeviceId,
                Gs350PositionSteps: null,
                CurrentQhyMetric: currentQhyFocusMetric,
                UvexM2PositionSteps: uvexStatus!.PositionKnown &&
                    uvexStatus.PositionTrust == UvexPositionTrust.Live
                        ? uvexStatus.FocusPositionSteps
                        : null));
        var focusIdentityIssue = focusEvidence.EvidenceGates
            .FirstOrDefault(gate => gate.Disposition != GateDisposition.Passed);
        if (focusIdentityIssue is not null) return focusIdentityIssue;

        var liveSetupGates = LockedNightSetupSnapshotLoader.EvaluateLive(
            nightSetup,
            camera,
            uvexStatus!,
            phdProfileEvidence!,
            qhyState,
            focusDomains: focusEvidence.FocusDomains,
            evaluatedUtc: DateTimeOffset.UtcNow);
        var incompatibleSetup = liveSetupGates.FirstOrDefault(gate =>
            gate.Disposition != GateDisposition.Passed &&
            !CanDeferInitialGs350Metric(gate, nightSetup.Value, currentQhyFocusMetric));
        if (incompatibleSetup is not null) return incompatibleSetup;

        await WriteAuditBestEffortAsync("night-setup-revalidated", new
        {
            nightSetupId = nightSetup.Value.NightSetupId,
            nightSetupSha256 = nightSetup.Sha256,
            commissioningPresetSha256 = commissioning.Sha256,
            commissioningValidUntilUtc = commissioning.Value.ValidUntilUtc,
            commissioningHardwareFingerprintSha256 = commissioning.Value.HardwareFingerprint!.Sha256,
            configuration.ActionConfigurationSha256,
            phd2ProfileEvidenceSha256 = phdProfileEvidence!.Sha256,
            phd2ProfileEvidenceSource = phdProfileEvidence.EvidenceSource,
            qhyStableId = qhyState.Identity.StableId,
            atrDeviceId = camera.DeviceId,
            uvexPort = uvexStatus!.PortName,
            c11FocuserDeviceId = c11Focus.DeviceId,
            c11FocuserPositionSteps = c11Focus.PositionSteps,
            g3SaturationAdu = configuration.G3.SaturationAdu,
            gs350FocusMetricDeferred = currentQhyFocusMetric is null,
        }).ConfigureAwait(false);
        context.Set("realRunConfigurationSha256", configuration.ActionConfigurationSha256);
        context.Set("commissioningPresetSha256", commissioning.Sha256);

        var horizon = HorizonCalculator.Evaluate(plan with { PlannedStartUtc = DateTimeOffset.UtcNow }).ToGateResult();
        if (horizon.Disposition != GateDisposition.Passed) return horizon;
        var coverGate = await EnsureOpticalCoverOpenAsync(context, cancellationToken).ConfigureAwait(false);
        if (coverGate.Disposition != GateDisposition.Passed) return coverGate;
        return GateResult.Pass(
            "REAL_INTERLOCKS_VALID",
            "All real-mode identities, mount UTC, optical-cover state, safety states, setup values and commissioning provenance passed.",
            mountClockGate.Metrics);
    }

    private QhyServiceHealth LoadInitialQhyServiceHealth()
    {
        var health = qhy.GetHealthAsync(CancellationToken.None).GetAwaiter().GetResult();
        var issues = ValidateQhyHealthEnvelope(health);
        if (issues.Count > 0)
        {
            throw new InvalidOperationException(
                $"QHY service configuration proof cannot be locked into the run manifest: {string.Join(" ", issues)}");
        }
        return health;
    }

    private static bool CanDeferInitialGs350Metric(
        GateResult gate,
        NightSetupRecord setup,
        LiveFocusMetricState? currentMetric)
    {
        if (currentMetric is not null) return false;
        if (gate.Code is not ("FOCUS_GS350_WIDE_FIELD_LIVE_METRIC" or
            "FOCUS_GS350_WIDE_FIELD_POSITION"))
        {
            return false;
        }

        var binding = setup.FocusDomains?
            .SingleOrDefault(candidate => candidate.Role == FocusDomainRole.Gs350WideField);
        return binding?.Limits is
        {
            MaximumSingleMoveSteps: 0,
            MaximumCumulativeMoveSteps: 0,
        };
    }

    private static LiveFocusMetricState? BuildQhyFocusMetric(
        QhyFrameRecord accepted,
        string qhyStableDeviceId)
    {
        var fwhm = accepted.Metrics.MedianFwhmPixels;
        if (fwhm is not { } finiteFwhm || !double.IsFinite(finiteFwhm) || finiteFwhm <= 0 ||
            accepted.Metrics.QualityFlags.Count != 0 ||
            string.IsNullOrWhiteSpace(accepted.Sha256))
        {
            return null;
        }

        return new LiveFocusMetricState(
            new FocusMetricEvidence(
                FocusMetricKind.QhyStellarShapeAndPlateSolve,
                qhyStableDeviceId,
                finiteFwhm,
                "FWHM pixels",
                accepted.Sha256),
            accepted.ExposureEndedUtc,
            accepted.ExposureEndedUtc.AddMinutes(30),
            GateDisposition.Passed);
    }

    private async Task<GateResult> ValidateQhyServiceConfigurationAsync(
        ObservationPlan plan,
        CancellationToken cancellationToken)
    {
        QhyServiceHealth current;
        try
        {
            current = await qhy.GetHealthAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return GateResult.Unknown(
                "QHY_SERVICE_HEALTH_UNAVAILABLE",
                $"The loopback QHY service health/configuration proof could not be read: {ex.Message}");
        }

        var issues = ValidateQhyHealthEnvelope(current);
        if (issues.Count > 0)
        {
            return GateResult.Fail("QHY_SERVICE_CONFIGURATION_PROOF_INVALID", string.Join(" ", issues));
        }

        var proof = current.Configuration;
        var locked = lockedQhyServiceHealth.Value.Configuration;
        if (!SameHash(proof.ConfigurationSha256, locked.ConfigurationSha256))
        {
            return GateResult.Fail(
                "QHY_SERVICE_CONFIGURATION_DRIFT",
                $"QHY service canonical configuration changed after the run manifest was locked: {locked.ConfigurationSha256} -> {proof.ConfigurationSha256}.");
        }
        if (proof.Simulator)
        {
            return GateResult.Fail(
                "QHY_SERVICE_SIMULATOR_IN_REAL_RUN",
                "The real observation runner refuses a QHY service configured for simulation.");
        }
        if (!string.Equals(proof.Adapter, "qhy-native", StringComparison.Ordinal))
        {
            return GateResult.Fail(
                "QHY_SERVICE_ADAPTER_MISMATCH",
                $"QHY service adapter '{proof.Adapter}' is not the commissioned qhy-native adapter.");
        }
        if (!string.Equals(proof.ExpectedModel, "QHYminiCam8M", StringComparison.OrdinalIgnoreCase))
        {
            return GateResult.Fail(
                "QHY_SERVICE_MODEL_MISMATCH",
                $"QHY service expected model '{proof.ExpectedModel}' is not QHYminiCam8M.");
        }
        if (!string.Equals(proof.ExpectedStableId, plan.ExpectedQhyCameraId, StringComparison.Ordinal) ||
            !string.Equals(proof.ExpectedStableId, configuration.NightSetup.QhyStableId, StringComparison.Ordinal))
        {
            return GateResult.Fail(
                "QHY_SERVICE_STABLE_ID_MISMATCH",
                $"QHY service is pinned to '{proof.ExpectedStableId}', not the locked observation/Night Setup identity '{plan.ExpectedQhyCameraId}'.");
        }
        if (!QhyServiceConfigurationProof.IsSha256(proof.NativeSdkSha256))
        {
            return GateResult.Fail(
                "QHY_SERVICE_SDK_HASH_MISSING",
                "The hardware QHY service does not advertise a valid pinned native SDK SHA-256.");
        }
        if (proof.NativeReadoutMode != configuration.Qhy.ReadoutMode)
        {
            return GateResult.Fail(
                "QHY_SERVICE_READOUT_MODE_MISMATCH",
                $"QHY service native readout mode {proof.NativeReadoutMode} does not match the frozen run configuration mode {configuration.Qhy.ReadoutMode}.");
        }

        return GateResult.Pass(
            "QHY_SERVICE_CONFIGURATION_VALID",
            $"QHY qhy-native service configuration {proof.ConfigurationSha256} is locked to {proof.ExpectedModel}/{proof.ExpectedStableId}, readout mode {proof.NativeReadoutMode}, SDK {proof.NativeSdkSha256}, and filter map {proof.NativeFilterPositionsSha256}.");
    }

    private static IReadOnlyList<string> ValidateQhyHealthEnvelope(QhyServiceHealth health)
    {
        var issues = new List<string>();
        if (!string.Equals(health.Service, "UVEX-ADV-QHY", StringComparison.Ordinal))
        {
            issues.Add($"Unexpected QHY service identity '{health.Service}'.");
        }
        if (!string.Equals(health.Status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add($"QHY service health status is '{health.Status}'.");
        }
        if (!health.LoopbackOnly) issues.Add("QHY service does not attest loopback-only binding.");
        if (health.TimestampUtc == default ||
            health.TimestampUtc < DateTimeOffset.UtcNow.AddMinutes(-2) ||
            health.TimestampUtc > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            issues.Add($"QHY service health timestamp {health.TimestampUtc:O} is stale or implausible.");
        }
        if (health.Configuration is null)
        {
            issues.Add("QHY service configuration proof is missing.");
        }
        else
        {
            issues.AddRange(health.Configuration.Validate());
        }
        return issues;
    }

    private async Task<GateResult> EnsureNinaEquipmentConnectedAsync(
        ObservationContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            // This gate must precede every physical Connect. In particular, do
            // not open a Profile-selected QHY or G3 camera and only discover
            // the ATR mismatch afterwards: that would already violate the
            // single-owner invariant.
            var ownerGate = ValidateNinaProfileOwnerSelections(context.Plan);
            if (ownerGate.Disposition != GateDisposition.Passed) return ownerGate;

            // Validate the on-disk PHD2 profile/G3 binding before asking the
            // N.I.N.A. PHD2 adapter to connect. This is a read-only registry
            // proof and cannot open the guide camera.
            var phdProfileGate = ValidatePhdProfileBindingEvidence();
            if (phdProfileGate.Disposition != GateDisposition.Passed) return phdProfileGate;

            if (!cameraMediator.GetInfo().Connected)
            {
                await CheckpointAndRejectStaleStageStackAsync(context, cancellationToken).ConfigureAwait(false);
                ownerGate = ValidateNinaProfileOwnerSelections(context.Plan);
                if (ownerGate.Disposition != GateDisposition.Passed) return ownerGate;
                Report("按当前 N.I.N.A. Profile 自动连接 ATR585M");
                if (!await cameraMediator.Connect().ConfigureAwait(false))
                {
                    return GateResult.Unknown("ATR_CONNECT_FAILED", "N.I.N.A. could not connect the camera selected in the locked Profile.");
                }
            }
            if (!telescopeMediator.GetInfo().Connected)
            {
                await CheckpointAndRejectStaleStageStackAsync(context, cancellationToken).ConfigureAwait(false);
                ownerGate = ValidateNinaProfileOwnerSelections(context.Plan);
                if (ownerGate.Disposition != GateDisposition.Passed) return ownerGate;
                Report("按当前 N.I.N.A. Profile 自动连接赤道仪");
                if (!await telescopeMediator.Connect().ConfigureAwait(false))
                {
                    return GateResult.Unknown("TELESCOPE_CONNECT_FAILED", "N.I.N.A. could not connect the telescope selected in the locked Profile.");
                }
            }
            if (!focuserMediator.GetInfo().Connected)
            {
                await CheckpointAndRejectStaleStageStackAsync(context, cancellationToken).ConfigureAwait(false);
                ownerGate = ValidateNinaProfileOwnerSelections(context.Plan);
                if (ownerGate.Disposition != GateDisposition.Passed) return ownerGate;
                Report("按当前 N.I.N.A. Profile 自动连接 C11 Star Focuser Pro（只读核验，不运动）");
                if (!await focuserMediator.Connect().ConfigureAwait(false))
                {
                    return GateResult.Unknown(
                        "C11_MAIN_FOCUSER_CONNECT_FAILED",
                        "N.I.N.A. could not connect the C11 focuser selected in the locked Profile. No focus motion was attempted.");
                }
            }
            if (!guiderMediator.GetInfo().Connected)
            {
                await CheckpointAndRejectStaleStageStackAsync(context, cancellationToken).ConfigureAwait(false);
                ownerGate = ValidateNinaProfileOwnerSelections(context.Plan);
                if (ownerGate.Disposition != GateDisposition.Passed) return ownerGate;
                Report("按当前 N.I.N.A. Profile 自动连接 PHD2 guider");
                if (!await guiderMediator.Connect().ConfigureAwait(false))
                {
                    return GateResult.Unknown("GUIDER_CONNECT_FAILED", "N.I.N.A. could not connect the PHD2 guider selected in the locked Profile.");
                }
            }
            if (configuration.Environment.RequireOpenOpticalCover && !flatDeviceMediator.GetInfo().Connected)
            {
                await CheckpointAndRejectStaleStageStackAsync(context, cancellationToken).ConfigureAwait(false);
                ownerGate = ValidateNinaProfileOwnerSelections(context.Plan);
                if (ownerGate.Disposition != GateDisposition.Passed) return ownerGate;
                Report("按当前 N.I.N.A. Profile 自动连接主光路电动镜盖");
                if (!await flatDeviceMediator.Connect().ConfigureAwait(false))
                {
                    return GateResult.Unknown("OPTICAL_COVER_CONNECT_FAILED", "N.I.N.A. could not connect the flat-device/cover selected in the locked Profile.");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return GateResult.Pass("NINA_EQUIPMENT_CONNECTED", "N.I.N.A. ATR, telescope, C11 Star Focuser Pro, PHD2 guider and required optical-cover adapters are connected; identity gates still apply.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return GateResult.Unknown("NINA_EQUIPMENT_CONNECT_EXCEPTION", $"N.I.N.A. equipment connection failed: {ex.Message}");
        }
    }

    private GateResult ValidateNinaProfileOwnerSelections(ObservationPlan plan)
    {
        var profile = profileService.ActiveProfile;
        return NinaProfileOwnerPreflight.Validate(
            new NinaProfileOwnerSelection(
                profile.CameraSettings.Id,
                profile.TelescopeSettings.Id,
                profile.FocuserSettings.Id,
                profile.FlatDeviceSettings.Id,
                profile.FilterWheelSettings.Id,
                profile.GuiderSettings.GuiderName),
            new NinaProfileOwnerExpectation(
                plan.ExpectedAtrCameraId,
                configuration.ExpectedTelescopeId,
                NinaProfileOwnerPreflight.C11FocuserDeviceId,
                NinaProfileOwnerPreflight.OpticalCoverDeviceId,
                NinaProfileOwnerPreflight.NoPhysicalFilterWheelDeviceId,
                NinaProfileOwnerPreflight.Phd2GuiderName,
                configuration.Environment.RequireOpenOpticalCover));
    }

    private GateResult ValidateMountClock()
    {
        try
        {
            var systemBefore = DateTimeOffset.UtcNow;
            var reported = telescopeMediator.GetInfo().UTCDate;
            var systemAfter = DateTimeOffset.UtcNow;
            var systemMidpoint = systemBefore + TimeSpan.FromTicks((systemAfter - systemBefore).Ticks / 2);
            return MountClockGate.Evaluate(
                reported,
                systemMidpoint,
                TimeSpan.FromSeconds(configuration.Environment.MountClockMaximumOffsetSeconds));
        }
        catch (Exception ex)
        {
            return GateResult.Unknown(
                "MOUNT_CLOCK_UNAVAILABLE",
                $"N.I.N.A. could not read the telescope UTCDate: {ex.Message}. Mount motion is prohibited.");
        }
    }

    private C11MainFocusOwnerSnapshot ReadC11MainFocusOwner()
    {
        var focuser = focuserMediator.GetInfo();
        return new C11MainFocusOwnerSnapshot(
            focuser.Connected,
            focuser.DeviceId ?? string.Empty,
            focuser.Position,
            focuser.Name,
            focuser.DisplayName,
            focuser.DriverInfo,
            focuser.DriverVersion,
            DateTimeOffset.UtcNow);
    }

    private async Task RequireImmediatePhysicalActionGatesAsync(
        ObservationContext context,
        CancellationToken cancellationToken)
    {
        await CheckpointAndRejectStaleStageStackAsync(context, cancellationToken).ConfigureAwait(false);
        var gate = ValidateImmediatePhysicalActionGates(context);
        if (gate.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(gate);
    }

    private async Task CheckpointAndRejectStaleStageStackAsync(
        ObservationContext context,
        CancellationToken cancellationToken)
    {
        await context.CheckpointAsync(cancellationToken).ConfigureAwait(false);
        if (Volatile.Read(ref resumeRecoveryRequired) != 0)
        {
            throw new ResumeStageRestartException();
        }
    }

    private GateResult ValidateImmediatePhysicalActionGates(ObservationContext context)
    {
        var prerequisites = ValidateCurrentActionPrerequisites(context);
        if (prerequisites.Disposition != GateDisposition.Passed) return prerequisites;
        var cover = ValidateOpticalCoverOpen();
        return ObservationAutomationPolicy.CombineImmediateActionGates(prerequisites, cover);
    }

    private GateResult ValidateCurrentActionPrerequisites(ObservationContext context)
    {
        var authorization = ObservationAutomationPolicy.AuthorizeExecutionMode(
            true,
            settings.ObservationUseRealMode,
            settings.RealModeCommissioned);
        if (authorization.Disposition != GateDisposition.Passed) return authorization;

        var currentPlateSolver = PlateSolverRunConfiguration.CaptureCurrent(
            profileService.ActiveProfile.PlateSolveSettings,
            configuration.PlateSolver);
        if (!configuration.MatchesCurrentProfile(settings, currentPlateSolver, out var currentConfigurationSha256))
        {
            return GateResult.Unknown(
                "REAL_PROFILE_DRIFT",
                $"An action-bearing N.I.N.A. Profile value changed after this run was locked. Locked {configuration.ActionConfigurationSha256}, current {currentConfigurationSha256}. No physical action is permitted.");
        }
        var ownerSelections = ValidateNinaProfileOwnerSelections(context.Plan);
        if (ownerSelections.Disposition != GateDisposition.Passed) return ownerSelections;
        var commissioningGate = ValidateLoadedAutomaticScienceCommissioning();
        if (commissioningGate.Disposition != GateDisposition.Passed) return commissioningGate;

        var protectedPlan = context.Plan with
        {
            PlannedStartUtc = DateTimeOffset.UtcNow,
            PlannedDuration = context.RemainingWorstCaseDuration ?? context.Plan.PlannedDuration,
        };
        var environment = ValidateEnvironment(protectedPlan);
        if (environment.Disposition != GateDisposition.Passed) return environment;
        var clock = ValidateMountClock();
        if (clock.Disposition != GateDisposition.Passed) return clock;
        return GateResult.Pass(
            "CURRENT_ACTION_PREREQUISITES_VALID",
            "Current real-mode authorization, immutable Profile, safety monitor, roof, weather, horizon and mount UTC passed immediately before the action.",
            clock.Metrics);
    }

    private GateResult ValidateLoadedAutomaticScienceCommissioning()
    {
        var loaded = commissioning;
        if (loaded is null || loaded.Value.SchemaVersion != 4)
        {
            return GateResult.Unknown(
                "REAL_SCIENCE_SCHEMA4_REQUIRED",
                "Automatic real science requires a hash-verified commissioning preset schema 4, including four-slot optical slit identity, before any physical action.");
        }
        if (!SameHash(loaded.Sha256, configuration.Commissioning.PresetSha256))
        {
            return GateResult.Unknown(
                "REAL_SCIENCE_PRESET_HASH_CHANGED",
                "The loaded commissioning preset SHA-256 no longer matches the immutable action configuration.");
        }
        if (loaded.Value.Phd2SlitPlacement is not { } phd2Preset)
        {
            return GateResult.Unknown(
                "REAL_SCIENCE_PHD2_COMMISSIONING_REQUIRED",
                "Every automatic real-science authority requires a complete PHD2 slit-placement commissioning payload.");
        }
        if (loaded.Value.SlitWheelIdentity is not { } slitIdentity)
        {
            return GateResult.Unknown(
                "REAL_SCIENCE_SLIT_IDENTITY_REQUIRED",
                "Every automatic real-science route requires four independently measured LED slit-width fingerprints; a mechanical wheel ordinal alone is not physical identity.");
        }
        var issues = phd2Preset.Validate().Concat(slitIdentity.Validate()).ToArray();
        return issues.Length == 0
            ? GateResult.Pass(
                "REAL_SCIENCE_SCHEMA4_COMMISSIONING_VALID",
                "The loaded schema-4 preset, PHD2 slit-placement payload and four-slot optical slit-identity library remain valid and hash-bound.")
            : GateResult.Unknown(
                "REAL_SCIENCE_PHD2_COMMISSIONING_INVALID",
                string.Join(" ", issues));
    }

    private GateResult ValidateOpticalCoverOpen()
    {
        if (!configuration.Environment.RequireOpenOpticalCover)
        {
            return GateResult.Unknown(
                "FULL_AUTOMATION_OPTICAL_COVER_DISABLED",
                "A full REAL observation cannot disable the optical-cover interlock. Use the separate supervised manual commissioning procedure instead.");
        }
        try
        {
            var cover = flatDeviceMediator.GetInfo();
            if (!cover.Connected) return GateResult.Unknown("OPTICAL_COVER_DISCONNECTED", "The required N.I.N.A. flat-device/cover is disconnected.");
            if (!cover.SupportsOpenClose) return GateResult.Fail("OPTICAL_COVER_UNSUPPORTED", $"The connected flat device '{cover.DisplayName ?? cover.Name}' cannot open/close its cover.");
            return cover.CoverState switch
            {
                CoverState.Open => GateResult.Pass("OPTICAL_COVER_OPEN", $"Optical cover '{cover.DisplayName ?? cover.Name}' is open."),
                CoverState.Closed => GateResult.Fail("OPTICAL_COVER_CLOSED", "The main optical-path cover is closed; no mount motion or C11 optical exposure is permitted."),
                CoverState.Error => GateResult.Fail("OPTICAL_COVER_ERROR", "The main optical-path cover reports an error; no mount motion or C11 optical exposure is permitted."),
                CoverState.NotPresent => GateResult.Fail("OPTICAL_COVER_NOT_PRESENT", "The selected N.I.N.A. flat device reports that its required cover is not present."),
                _ => GateResult.Unknown("OPTICAL_COVER_STATE_UNKNOWN", $"The main optical-path cover state is {cover.CoverState}; no mount motion or C11 optical exposure is permitted."),
            };
        }
        catch (Exception ex)
        {
            return GateResult.Unknown("OPTICAL_COVER_STATE_UNAVAILABLE", $"N.I.N.A. could not read the main optical-path cover state: {ex.Message}");
        }
    }

    private async Task<GateResult> EnsureOpticalCoverOpenAsync(
        ObservationContext context,
        CancellationToken cancellationToken)
    {
        var current = ValidateOpticalCoverOpen();
        if (current.Disposition == GateDisposition.Passed ||
            !configuration.Environment.RequireOpenOpticalCover)
        {
            return current;
        }
        if (current.Code != "OPTICAL_COVER_CLOSED") return current;

        await CheckpointAndRejectStaleStageStackAsync(context, cancellationToken).ConfigureAwait(false);
        var prerequisites = ValidateCurrentActionPrerequisites(context);
        if (prerequisites.Disposition != GateDisposition.Passed) return prerequisites;
        current = ValidateOpticalCoverOpen();
        if (current.Disposition == GateDisposition.Passed) return current;
        if (current.Code != "OPTICAL_COVER_CLOSED") return current;
        try
        {
            Report("全部安全门通过，自动打开主光路镜盖（不操作屋顶）");
            await flatDeviceMediator.OpenCover(progress, cancellationToken).ConfigureAwait(false);
            return await WaitForOpticalCoverStateAsync(CoverState.Open, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return GateResult.Unknown("OPTICAL_COVER_OPEN_FAILED", $"N.I.N.A. could not open the main optical-path cover: {ex.Message}");
        }
    }

    private async Task<GateResult> WaitForOpticalCoverStateAsync(
        CoverState expected,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(configuration.Environment.OpticalCoverTransitionTimeoutSeconds);
        while (DateTimeOffset.UtcNow <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = flatDeviceMediator.GetInfo();
            if (!info.Connected) return GateResult.Unknown("OPTICAL_COVER_DISCONNECTED", "The optical cover disconnected during a commanded transition.");
            if (info.CoverState == expected)
            {
                return GateResult.Pass(
                    expected == CoverState.Open ? "OPTICAL_COVER_OPEN" : "OPTICAL_COVER_CLOSED",
                    $"N.I.N.A. attested optical cover state {expected}.");
            }
            if (info.CoverState == CoverState.Error)
            {
                return GateResult.Fail("OPTICAL_COVER_ERROR", "The optical cover entered Error during a commanded transition.");
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        return GateResult.Unknown(
            "OPTICAL_COVER_TRANSITION_TIMEOUT",
            $"The optical cover did not reach {expected} within {configuration.Environment.OpticalCoverTransitionTimeoutSeconds} seconds.");
    }

    private async Task<string?> CloseOpticalCoverAsync(string reason, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = flatDeviceMediator.GetInfo();
            if (!info.Connected) return "Optical cover is disconnected; closed state cannot be attested.";
            if (!info.SupportsOpenClose) return $"Flat device '{info.DisplayName ?? info.Name}' does not support cover close.";
            if (info.CoverState == CoverState.Closed) return null;
            if (info.CoverState is CoverState.Error or CoverState.NotPresent)
            {
                return $"Optical cover cannot be closed from state {info.CoverState}.";
            }
            await flatDeviceMediator.CloseCover(progress, cancellationToken).ConfigureAwait(false);
            var gate = await WaitForOpticalCoverStateAsync(CoverState.Closed, cancellationToken).ConfigureAwait(false);
            await WriteAuditBestEffortAsync("optical-cover-close", new
            {
                reason,
                gate.Code,
                disposition = gate.Disposition.ToString(),
                gate.Message,
                roofWasNotCommanded = true,
            }).ConfigureAwait(false);
            return gate.Disposition == GateDisposition.Passed ? null : $"{gate.Code}: {gate.Message}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Optical cover close failed: {ex.Message}";
        }
    }

    private async Task<QhyCameraStatus> ConnectQhyAtCheckpointAsync(ObservationContext context, CancellationToken cancellationToken)
    {
        var current = await qhy.GetCameraAsync(cancellationToken).ConfigureAwait(false);
        if (current?.Connected == true) return current;
        // Connecting the dedicated service opens the physical QHY device; the caller must already
        // be in a commissioned real run. This validation stage is itself a bounded atomic action.
        await CheckpointAndRejectStaleStageStackAsync(context, cancellationToken).ConfigureAwait(false);
        return await qhy.EnsureCameraConnectedAsync(cancellationToken).ConfigureAwait(false);
    }

    private GateResult ValidateUvexStatus(UvexDeviceStatus? status)
    {
        if (status is null) return GateResult.Unknown("UVEX_STATUS_UNAVAILABLE", "UVEX service returned no status.");
        if (!string.Equals(status.PortName, "COM5", StringComparison.OrdinalIgnoreCase))
        {
            return GateResult.Fail("UVEX_PORT_MISMATCH", $"UVEX service reports '{status.PortName}', not COM5.");
        }
        if (status.ConnectionState != DeviceConnectionState.Ready || !status.PositionKnown)
        {
            return GateResult.Unknown("UVEX_NOT_READY", $"UVEX state is {status.ConnectionState}; trusted position is {status.PositionKnown}.");
        }
        if (!string.IsNullOrWhiteSpace(status.LastError)) return GateResult.Fail("UVEX_FAULT", status.LastError);
        if (configuration.ExpectedUvexSlitPosition is < 1 or > 4 ||
            configuration.ExpectedUvexGratingPositionSteps == int.MinValue ||
            configuration.ExpectedUvexM2PositionSteps == int.MinValue)
        {
            return GateResult.Unknown("UVEX_NIGHT_SETUP_UNBOUND", "Expected slit, grating and M2 positions must be bound before real mode.");
        }
        if (status.SlitPosition != configuration.ExpectedUvexSlitPosition)
        {
            return GateResult.Fail("UVEX_SLIT_MISMATCH", $"UVEX slit is {status.SlitPosition}; expected {configuration.ExpectedUvexSlitPosition}.");
        }
        if (status.GratingPositionSteps is not { } grating || Math.Abs(grating - configuration.ExpectedUvexGratingPositionSteps) > configuration.UvexPositionToleranceSteps)
        {
            return GateResult.Fail("UVEX_GRATING_MISMATCH", $"UVEX grating is {status.GratingPositionSteps}; expected {configuration.ExpectedUvexGratingPositionSteps}±{configuration.UvexPositionToleranceSteps} steps.");
        }
        if (status.FocusPositionSteps is not { } focus || Math.Abs(focus - configuration.ExpectedUvexM2PositionSteps) > configuration.UvexPositionToleranceSteps)
        {
            return GateResult.Fail("UVEX_M2_MISMATCH", $"UVEX M2 is {status.FocusPositionSteps}; expected {configuration.ExpectedUvexM2PositionSteps}±{configuration.UvexPositionToleranceSteps} steps.");
        }
        return GateResult.Pass("UVEX_NIGHT_SETUP_MATCH", "UVEX is Ready on COM5 and its slit, grating and M2 match the locked setup.");
    }

    private GateResult ValidateEnvironment(ObservationPlan plan)
    {
        var capability = ObservationAutomationPolicy.ValidateFullAutomationCapabilities(
            configuration.Environment.RequireSafetyMonitor,
            configuration.Environment.RequireOpenDomeOrRoof,
            configuration.Environment.RequireWeatherData,
            configuration.Environment.RequireOpenOpticalCover);
        if (capability.Disposition != GateDisposition.Passed) return capability;

        var safety = safetyMonitorMediator.GetInfo();
        if (!safety.Connected)
        {
            return GateResult.Unknown("SAFETY_MONITOR_MISSING", "A connected safety monitor is required for a full REAL observation.");
        }
        if (!safety.IsSafe) return GateResult.Fail("SAFETY_MONITOR_UNSAFE", "N.I.N.A. safety monitor reports unsafe.");

        var dome = domeMediator.GetInfo();
        if (!dome.Connected)
        {
            return GateResult.Unknown("ROOF_STATE_UNKNOWN", "A connected dome/roof adapter is required for a full REAL observation; roof state will not be guessed.");
        }
        if (dome.ShutterStatus != ShutterState.ShutterOpen)
        {
            return GateResult.Fail("ROOF_NOT_OPEN", $"Dome/roof shutter state is {dome.ShutterStatus}, not open.");
        }

        var weather = weatherDataMediator.GetInfo();
        if (!weather.Connected)
        {
            return GateResult.Unknown("WEATHER_STATE_UNKNOWN", "A connected weather adapter is required for a full REAL observation; weather will not be guessed.");
        }
        var missing = new List<string>();
        if (!double.IsFinite(weather.RainRate)) missing.Add("rain rate");
        if (!double.IsFinite(weather.CloudCover)) missing.Add("cloud cover");
        if (!double.IsFinite(weather.Humidity)) missing.Add("humidity");
        if (!double.IsFinite(weather.WindSpeed)) missing.Add("wind speed");
        if (missing.Count > 0) return GateResult.Unknown("WEATHER_METRICS_MISSING", $"Weather adapter does not provide {string.Join(", ", missing)}.");
        if (weather.RainRate > 0) return GateResult.Fail("RAIN_DETECTED", $"Rain rate is {weather.RainRate:F3}.");
        if (weather.CloudCover > configuration.Environment.MaximumCloudCoverPercent) return GateResult.Fail("CLOUD_LIMIT", $"Cloud cover {weather.CloudCover:F1}% exceeds {configuration.Environment.MaximumCloudCoverPercent:F1}%.");
        if (weather.Humidity > configuration.Environment.MaximumHumidityPercent) return GateResult.Fail("HUMIDITY_LIMIT", $"Humidity {weather.Humidity:F1}% exceeds {configuration.Environment.MaximumHumidityPercent:F1}%.");
        if (weather.WindSpeed > configuration.Environment.MaximumWindSpeedMetersPerSecond) return GateResult.Fail("WIND_LIMIT", $"Wind speed {weather.WindSpeed:F1} exceeds {configuration.Environment.MaximumWindSpeedMetersPerSecond:F1} m/s.");

        return HorizonCalculator.Evaluate(plan with { PlannedStartUtc = DateTimeOffset.UtcNow }).ToGateResult();
    }

    private static bool IsKnownPierSide(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.IndexOf("unknown", StringComparison.OrdinalIgnoreCase) < 0;

    /// <summary>
    /// Evaluates the exact coordinate about to be sent to the mount across the
    /// remaining protected duration.  The normal immediate gate protects the
    /// catalog target; local/coarse corrections require this second gate so an
    /// azimuth-dependent horizon cannot be crossed by the correction itself.
    /// </summary>
    private static GateResult ValidateCommandCoordinateHorizon(
        ObservationContext context,
        Coordinates commanded,
        string action)
    {
        if (!double.IsFinite(commanded.RADegrees) ||
            commanded.RADegrees is < 0 or >= 360 ||
            !double.IsFinite(commanded.Dec) ||
            commanded.Dec is < -90 or > 90)
        {
            return GateResult.Fail(
                "COMMAND_COORDINATE_INVALID",
                $"{action} produced an invalid commanded coordinate; mount motion is withheld.");
        }

        Coordinates commandedJ2000;
        try
        {
            commandedJ2000 = commanded.Epoch == Epoch.J2000
                ? commanded
                : commanded.Transform(Epoch.J2000);
        }
        catch (Exception ex)
        {
            return GateResult.Unknown(
                "COMMAND_COORDINATE_EPOCH_CONVERSION_FAILED",
                $"{action} coordinate epoch '{commanded.Epoch}' could not be transformed to J2000 for the horizon gate: {ex.Message}");
        }
        if (!double.IsFinite(commandedJ2000.RADegrees) ||
            commandedJ2000.RADegrees is < 0 or >= 360 ||
            !double.IsFinite(commandedJ2000.Dec) ||
            commandedJ2000.Dec is < -90 or > 90)
        {
            return GateResult.Unknown(
                "COMMAND_COORDINATE_EPOCH_CONVERSION_INVALID",
                $"{action} epoch conversion did not produce a finite J2000 coordinate; mount motion is withheld.");
        }

        var protectedPlan = context.Plan with
        {
            Target = context.Plan.Target with
            {
                RightAscensionDegrees = commandedJ2000.RADegrees,
                DeclinationDegrees = commandedJ2000.Dec,
            },
            PlannedStartUtc = DateTimeOffset.UtcNow,
            PlannedDuration = context.RemainingWorstCaseDuration ?? context.Plan.PlannedDuration,
        };
        var horizon = HorizonCalculator.Evaluate(protectedPlan).ToGateResult();
        return horizon.Disposition switch
        {
            GateDisposition.Passed => GateResult.Pass(
                "COMMAND_COORDINATE_HORIZON_CLEAR",
                $"{action} coordinate passed the current and remaining-duration horizon envelope.",
                horizon.Metrics),
            GateDisposition.Failed => GateResult.Fail(
                "COMMAND_COORDINATE_HORIZON_BLOCKED",
                $"{action} coordinate is outside the protected horizon envelope: {horizon.Message}",
                horizon.Metrics),
            _ => GateResult.Unknown(
                "COMMAND_COORDINATE_HORIZON_UNKNOWN",
                $"{action} coordinate horizon could not be proven: {horizon.Message}",
                horizon.Metrics),
        };
    }

    private async Task<StageResult> SlewToCatalogTargetAsync(ObservationContext context, CancellationToken cancellationToken)
    {
        var interlock = await EvaluateInterlocksAsync(context, connectQhy: false, cancellationToken).ConfigureAwait(false);
        if (interlock.Disposition != GateDisposition.Passed) return new StageResult(interlock);
        var info = telescopeMediator.GetInfo();
        if (info.AtPark)
        {
            // EvaluateInterlocks has already proved safety, open-roof, weather,
            // horizon, identity and immutable setup gates. Unparking is still a
            // separate checkpointed action; the pipeline never opens the roof.
            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            Report("安全门通过，按计划自动解除赤道仪停驻（不操作屋顶）");
            if (!await telescopeMediator.UnparkTelescope(progress, cancellationToken).ConfigureAwait(false) || telescopeMediator.GetInfo().AtPark)
            {
                return Attention(ObservationStage.SlewToCatalogTarget, "TELESCOPE_UNPARK_FAILED", "N.I.N.A. did not attest a successful unpark; no slew was started.");
            }
            await WriteAuditBestEffortAsync("telescope-unparked", new
            {
                automatic = true,
                roofWasNotCommanded = true,
                finalizationWillNotAutoPark = true,
            }).ConfigureAwait(false);
        }
        var target = TargetCoordinates(context.Plan);
        await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
        Report("按目录坐标进行计划内初始转向");
        if (!await telescopeMediator.SlewToCoordinatesAsync(target, cancellationToken).ConfigureAwait(false))
        {
            return Failed(ObservationStage.SlewToCatalogTarget, "CATALOG_SLEW_REJECTED", "N.I.N.A. telescope mediator rejected the catalog-coordinate slew.");
        }
        await telescopeMediator.WaitForSlew(cancellationToken).ConfigureAwait(false);
        var solved = telescopeMediator.GetCurrentPosition();
        var residual = AngularSeparationArcseconds(target, solved);
        await WriteAuditBestEffortAsync("catalog-slew", new
        {
            requestedRaDegrees = target.RADegrees,
            requestedDecDegrees = target.Dec,
            reportedRaDegrees = solved.RADegrees,
            reportedDecDegrees = solved.Dec,
            residualArcseconds = residual,
        }).ConfigureAwait(false);
        return Passed(
            "CATALOG_SLEW_COMPLETED",
            $"Initial catalog slew completed; mount-reported residual is {residual:F1} arcsec. QHY WCS must independently verify it.",
            new Dictionary<string, double> { ["mountReportedResidualArcseconds"] = residual });
    }

    private async Task<StageResult> AcquireQhyWideFieldAsync(ObservationContext context, CancellationToken cancellationToken)
    {
        // A previous accepted frame cannot attest a new acquisition attempt.
        // The replacement metric is installed only after this frame is both
        // quality-accepted and plate-solved.
        currentQhyFocusMetric = null;
        var state = await AcquireOrContinueQhyAcquisitionAsync(context, cancellationToken).ConfigureAwait(false);
        if (state.State == QhyJobState.PausedNeedsAttention)
        {
            return Attention(ObservationStage.AcquireQhyWideField, "QHY_ACQUISITION_NEEDS_ATTENTION", state.AttentionReason ?? "QHY acquisition quality gate requires attention.");
        }
        if (state.State != QhyJobState.Completed)
        {
            return Failed(ObservationStage.AcquireQhyWideField, "QHY_ACQUISITION_FAILED", state.Error ?? $"QHY acquisition ended in {state.State}.");
        }
        if (state.AcceptedFrameId is not { } acceptedFrameId)
        {
            return Failed(ObservationStage.AcquireQhyWideField, "QHY_ACCEPTED_FRAME_MISSING", "Completed QHY acquisition did not identify the quality-gated AcceptedFrameId.");
        }
        var accepted = state.Frames.SingleOrDefault(frame => frame.FrameId == acceptedFrameId);
        if (accepted is null)
        {
            return Failed(ObservationStage.AcquireQhyWideField, "QHY_ACCEPTED_FRAME_NOT_FOUND", $"AcceptedFrameId {acceptedFrameId:D} is not present in the immutable frame manifest.");
        }
        var afterAcceptedFrameMountReadback = CaptureG3FrameMountReadback();
        if (qhyAcquisitionMountReadbackJobId != state.Id || qhyAcquisitionBeforeJobMountReadback is null)
        {
            lastQhyAcquisition = null;
            lastQhySolve = null;
            lastQhySolveMountBinding = null;
            lastQhyAcceptedFrameMountBinding = null;
            return Attention(
                ObservationStage.AcquireQhyWideField,
                "QHY_CAPTURE_PREJOB_MOUNT_BINDING_MISSING",
                "This accepted QHY frame was adopted without an in-process pre-job mount readback. It remains immutable evidence, but cannot authorize coarse centering or ghost identity; start a fresh acquisition.");
        }
        lastQhyAcceptedFrameMountBinding = CreateQhyAcceptedFrameMountBinding(
            context,
            state,
            accepted,
            qhyAcquisitionBeforeJobMountReadback,
            afterAcceptedFrameMountReadback);
        var captureBindingGate = lastQhyAcceptedFrameMountBinding.Validate(
            context.Plan.ObservationRunId,
            configuration.ActionConfigurationSha256,
            commissioning?.Sha256 ?? string.Empty,
            state.Id,
            accepted.FrameId,
            accepted.Sha256,
            MountCommandArrivalToleranceArcseconds);
        if (captureBindingGate.Disposition != GateDisposition.Passed)
        {
            lastQhyAcquisition = null;
            lastQhySolve = null;
            lastQhySolveMountBinding = null;
            lastQhyAcceptedFrameMountBinding = null;
            return new StageResult(captureBindingGate, accepted.FitsPath);
        }
        lastQhyAcquisition = state;
        activeQhyJobs.TryRemove(state.Id, out _);
        lastQhySolveMountBinding = null;
        lastQhySolve = await SolveExternalFitsAsync(
            accepted.FitsPath,
            accepted.Settings.BitDepth,
            configuration.Qhy.FocalLengthMillimeters,
            configuration.Qhy.PixelSizeMicrometers,
            accepted.Settings.BinningX,
            TargetCoordinates(context.Plan),
            "QHY/GS350 coarse field",
            cancellationToken).ConfigureAwait(false);
        if (!lastQhySolve.Result.Success)
        {
            return Attention(ObservationStage.AcquireQhyWideField, "QHY_PLATE_SOLVE_FAILED", "N.I.N.A. plate solver did not solve the immutable QHY FITS; no centering correction is permitted.");
        }
        lastQhySolveMountBinding = CaptureGhostQhySolveMountBinding(context, accepted, lastQhySolve, lastQhyAcceptedFrameMountBinding);
        currentQhyFocusMetric = BuildQhyFocusMetric(accepted, context.Plan.ExpectedQhyCameraId);
        if (currentQhyFocusMetric is null)
        {
            return Attention(
                ObservationStage.AcquireQhyWideField,
                "GS350_FOCUS_METRIC_UNAVAILABLE",
                "The accepted QHY frame lacks a finite FWHM, clean quality flags, or immutable SHA-256; GS350/ToupTek AAF focus cannot be attested and no mount correction is permitted.");
        }
        var fullFocusInterlock = await EvaluateInterlocksAsync(
            context,
            connectQhy: false,
            cancellationToken).ConfigureAwait(false);
        if (fullFocusInterlock.Disposition != GateDisposition.Passed)
        {
            return new StageResult(fullFocusInterlock, accepted.FitsPath);
        }
        var residual = AngularSeparationArcseconds(TargetCoordinates(context.Plan), lastQhySolve.Result.Coordinates);
        return Passed(
            "QHY_WIDE_FIELD_SOLVED",
            $"QHY accepted frame {accepted.FrameId:D} solved with {residual:F1} arcsec target residual.",
            new Dictionary<string, double>
            {
                ["solveResidualArcseconds"] = residual,
                ["detectedStars"] = accepted.Metrics.DetectedStars,
                ["saturatedFraction"] = accepted.Metrics.SaturatedFraction,
                ["gs350MedianFwhmPixels"] = accepted.Metrics.MedianFwhmPixels!.Value,
                ["gs350MedianEllipticity"] = accepted.Metrics.MedianEllipticity ?? 0,
            },
            new Dictionary<string, string>
            {
                ["acceptedFrameId"] = accepted.FrameId.ToString("D"),
                ["fitsSha256"] = accepted.Sha256,
                ["solver"] = lastQhySolve.SolverIdentity,
            });
    }

    private async Task<QhyJobSnapshot> AcquireOrContinueQhyAcquisitionAsync(ObservationContext context, CancellationToken cancellationToken)
    {
        QhyJobSnapshot job;
        if (qhyAcquisitionJobId is { } existing)
        {
            job = await qhy.GetJobAsync(existing, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"QHY acquisition job {existing:D} disappeared.");
            ObserveQhySnapshot(job);
            if (job.State is QhyJobState.Cancelled or QhyJobState.Faulted or QhyJobState.TakenOver)
            {
                activeQhyJobs.TryRemove(existing, out _);
                qhyAcquisitionJobId = null;
                lastQhyAcquisition = null;
                lastQhySolve = null;
                lastQhySolveMountBinding = null;
                lastQhyAcceptedFrameMountBinding = null;
                qhyAcquisitionMountReadbackJobId = null;
                qhyAcquisitionBeforeJobMountReadback = null;
                qhyAcquisitionAttempt++;
                return await AcquireOrContinueQhyAcquisitionAsync(context, cancellationToken).ConfigureAwait(false);
            }
            if (job.State == QhyJobState.Paused)
            {
                await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
                job = await qhy.ResumeAsync(existing, cancellationToken).ConfigureAwait(false);
                ObserveQhySnapshot(job);
            }
        }
        else
        {
            var camera = await ConnectQhyAtCheckpointAsync(context, cancellationToken).ConfigureAwait(false);
            if (camera.Identity is null || !string.Equals(camera.Identity.StableId, context.Plan.ExpectedQhyCameraId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("QHY identity changed before acquisition.");
            }
            await CheckpointAndRejectStaleStageStackAsync(context, cancellationToken).ConfigureAwait(false);
            var clientRequestId = $"{context.Plan.ObservationRunId}:acquire-qhy-wide-field:{qhyAcquisitionAttempt}";
            var request = new AcquisitionJobRequest(
                context.Plan.ObservationRunId,
                context.Plan.Target.Name,
                configuration.Qhy.AcquisitionExposureLadderSeconds,
                configuration.Qhy.Gain,
                configuration.Qhy.Offset,
                MaximumAttempts: 4,
                BinningX: configuration.Qhy.Binning,
                BinningY: configuration.Qhy.Binning,
                ReadoutMode: configuration.Qhy.ReadoutMode,
                FilterName: configuration.Qhy.FilterName,
                TargetTemperatureC: configuration.Qhy.TargetTemperatureC,
                QualityThresholds: configuration.Qhy.QualityThresholds,
                RoiX: configuration.Qhy.RoiX,
                RoiY: configuration.Qhy.RoiY,
                RoiWidth: configuration.Qhy.RoiWidth,
                RoiHeight: configuration.Qhy.RoiHeight,
                ClientRequestId: clientRequestId,
                TargetRightAscensionDegrees: context.Plan.Target.RightAscensionDegrees,
                TargetDeclinationDegrees: context.Plan.Target.DeclinationDegrees,
                CoordinateEpoch: "ICRS",
                ControlLeaseSeconds: 120);
            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            pendingQhyRequests[clientRequestId] = new PendingQhyRequest(context.Plan.ObservationRunId, QhyJobKind.Acquisition, clientRequestId, request);
            var beforeJobMountReadback = CaptureG3FrameMountReadback();
            job = await qhy.StartOrAdoptAcquisitionAsync(request, cancellationToken).ConfigureAwait(false);
            qhyAcquisitionJobId = job.Id;
            qhyAcquisitionMountReadbackJobId = job.Id;
            qhyAcquisitionBeforeJobMountReadback = beforeJobMountReadback;
            lastQhyAcceptedFrameMountBinding = null;
            RegisterActiveQhyJob(job);
            pendingQhyRequests.TryRemove(clientRequestId, out _);
        }

        job = await qhy.WaitForQuiescentOrTerminalAsync(job.Id, snapshot => PublishQhyPreviewAsync(snapshot, cancellationToken), cancellationToken).ConfigureAwait(false);
        return job;
    }

    private async Task<StageResult> CoarseCenterAsync(ObservationContext context, CancellationToken cancellationToken)
    {
        var pendingReturn = await CompletePendingQhyCoarseReturnAsync(context, cancellationToken).ConfigureAwait(false);
        if (pendingReturn is not null) return pendingReturn;
        if (lastQhySolve is null || lastQhyAcquisition is null)
        {
            return Attention(ObservationStage.CoarseCenter, "QHY_SOLVE_REQUIRED", "No completed, accepted and solved QHY acquisition is available.");
        }
        var limits = configuration.Qhy.CoarseCenteringLimits;
        var limitIssues = limits.Validate();
        if (limitIssues.Count > 0)
        {
            return Attention(
                ObservationStage.CoarseCenter,
                "QHY_COARSE_LIMITS_INVALID",
                string.Join(" ", limitIssues));
        }
        var previousWorstCaseDuration = context.RemainingWorstCaseDuration;
        context.RemainingWorstCaseDuration =
            (previousWorstCaseDuration ?? context.Plan.PlannedDuration) + limits.MaximumElapsedTime;
        try
        {
        var target = TargetCoordinates(context.Plan);
        var solve = lastQhySolve;
        if (lastQhyAcquisition.AcceptedFrameId is not { } sourceFrameId ||
            lastQhyAcquisition.Frames.SingleOrDefault(frame => frame.FrameId == sourceFrameId) is not { } sourceFrame)
        {
            return Attention(ObservationStage.CoarseCenter, "QHY_COARSE_SOURCE_FRAME_MISSING", "The solved QHY source is not the accepted frame in the immutable acquisition manifest.");
        }
        var sourceBindingGate = await ValidateQhyAcceptedFrameMountBindingForMotionAsync(
            context,
            lastQhyAcquisition,
            sourceFrame,
            solve,
            cancellationToken).ConfigureAwait(false);
        if (sourceBindingGate.Disposition != GateDisposition.Passed)
        {
            lastQhySolve = null;
            lastQhySolveMountBinding = null;
            lastQhyAcceptedFrameMountBinding = null;
            return new StageResult(sourceBindingGate, sourceFrame.FitsPath);
        }
        var residual = AngularSeparationArcseconds(target, solve.Result.Coordinates);
        var origin = telescopeMediator.GetCurrentPosition();
        var originPierSide = telescopeMediator.GetInfo().SideOfPier.ToString();
        var mountGate = ValidateQhyCoarseMountState(originPierSide);
        if (mountGate.Disposition != GateDisposition.Passed) return new StageResult(mountGate, solve.SourcePath);
        qhyCoarseStartedUtc ??= DateTimeOffset.UtcNow;
        var state = new QhyPendingCoarseReturn(
            origin,
            originPierSide,
            CurrentRaTangentOffsetArcseconds: 0,
            CurrentDeclinationOffsetArcseconds: 0,
            qhyCoarseCumulativeArcseconds,
            qhyCoarseStartedUtc.Value,
            DeclaredEvidencePath: string.Empty);
        var declaredPath = await PublishRunJsonEvidenceAsync(
            "qhy-coarse-centering-declared",
            "Independent QHY/GS350 coarse-centering envelope locked before motion",
            new
            {
                schemaVersion = limits.SchemaVersion,
                limits.MaximumSingleCorrectionArcseconds,
                limits.MaximumCumulativeCorrectionArcseconds,
                limits.MaximumCorrectionAttempts,
                maximumElapsedSeconds = limits.MaximumElapsedTime.TotalSeconds,
                configuration.Qhy.CenteringToleranceArcseconds,
                initialResidualArcseconds = residual,
                solve = new
                {
                    solve.SolverIdentity,
                    solve.SourcePath,
                    solvedRaDegrees = solve.Result.Coordinates.RADegrees,
                    solvedDecDegrees = solve.Result.Coordinates.Dec,
                    targetRaDegrees = target.RADegrees,
                    targetDecDegrees = target.Dec,
                },
                origin = new { raDegrees = origin.RADegrees, decDegrees = origin.Dec, pierSide = originPierSide },
                returnPolicy = "reserve straight-line no-larger-than-coarse-single-limit return before every outward correction",
                fineG3SlitMotionEnvelopeWasNotUsed = true,
            },
            solve.SourcePath,
            cancellationToken).ConfigureAwait(false);
        state = state with { DeclaredEvidencePath = declaredPath };
        while (residual > configuration.Qhy.CenteringToleranceArcseconds)
        {
            var priorResidual = residual;
            var (fullRaArcseconds, fullDecArcseconds) = SignedTangentOffsetArcseconds(solve.Result.Coordinates, target);
            var requestedMagnitude = Math.Sqrt(
                fullRaArcseconds * fullRaArcseconds +
                fullDecArcseconds * fullDecArcseconds);
            if (!double.IsFinite(requestedMagnitude) || requestedMagnitude <= 0)
            {
                return Attention(
                    ObservationStage.CoarseCenter,
                    "QHY_COARSE_CORRECTION_INVALID",
                    "The solved QHY tangent-plane correction is not positive and finite.");
            }
            var moveMagnitude = Math.Min(requestedMagnitude, limits.MaximumSingleCorrectionArcseconds);
            var scale = moveMagnitude / requestedMagnitude;
            var raArcseconds = fullRaArcseconds * scale;
            var decArcseconds = fullDecArcseconds * scale;
            var currentCommanded = telescopeMediator.GetCurrentPosition();
            var correctedCommanded = ApplySkyCorrection(currentCommanded, raArcseconds, decArcseconds);
            var (nextOriginRa, nextOriginDec) = SignedTangentOffsetArcseconds(origin, correctedCommanded);
            var nextRadius = Math.Sqrt(nextOriginRa * nextOriginRa + nextOriginDec * nextOriginDec);
            var bounded = ValidateQhyCoarseMoveAndReturnReserve(state, moveMagnitude, nextRadius);
            if (bounded.Disposition != GateDisposition.Passed)
            {
                return await StopQhyCoarseAndReturnAsync(
                    context,
                    state,
                    bounded.Code,
                    bounded.Message,
                    solve.SourcePath,
                    cancellationToken).ConfigureAwait(false);
            }
            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            mountGate = ValidateQhyCoarseMountState(originPierSide);
            if (mountGate.Disposition != GateDisposition.Passed)
            {
                return await StopQhyCoarseAndReturnAsync(
                    context,
                    state,
                    mountGate.Code,
                    mountGate.Message,
                    solve.SourcePath,
                    cancellationToken).ConfigureAwait(false);
            }
            sourceBindingGate = await ValidateQhyAcceptedFrameMountBindingForMotionAsync(
                context,
                lastQhyAcquisition,
                sourceFrame,
                solve,
                cancellationToken).ConfigureAwait(false);
            if (sourceBindingGate.Disposition != GateDisposition.Passed)
            {
                lastQhySolve = null;
                lastQhySolveMountBinding = null;
                lastQhyAcceptedFrameMountBinding = null;
                return await StopQhyCoarseAndReturnAsync(
                    context,
                    state,
                    sourceBindingGate.Code,
                    sourceBindingGate.Message,
                    solve.SourcePath,
                    cancellationToken).ConfigureAwait(false);
            }
            var moveIntentPath = await PublishRunJsonEvidenceAsync(
                "qhy-coarse-centering-move-intent",
                $"QHY coarse-centering move {qhyCoarseCorrectionAttempts + 1}",
                new
                {
                    schemaVersion = limits.SchemaVersion,
                    priorResidualArcseconds = residual,
                    requestedFullCorrectionArcseconds = requestedMagnitude,
                    commandedCorrectionArcseconds = moveMagnitude,
                    raTangentOffsetArcseconds = raArcseconds,
                    decOffsetArcseconds = decArcseconds,
                    correctedCommandedRaDegrees = correctedCommanded.RADegrees,
                    correctedCommandedDecDegrees = correctedCommanded.Dec,
                    reservedReturnRadiusArcseconds = nextRadius,
                    state.DeclaredEvidencePath,
                    qhySolveRequired = true,
                    safetyAndHorizonCheckpointPassedImmediatelyBeforeIntent = true,
                    fineG3SlitMotionEnvelopeWasNotUsed = true,
                },
                solve.SourcePath,
                cancellationToken).ConfigureAwait(false);
            // Evidence publication can take long enough for weather, pause or
            // horizon state to change.  Re-check immediately before arming the
            // pending move, and evaluate the coordinate that will actually be
            // commanded rather than only the catalog target.
            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            sourceBindingGate = await ValidateQhyAcceptedFrameMountBindingForMotionAsync(
                context,
                lastQhyAcquisition,
                sourceFrame,
                solve,
                cancellationToken).ConfigureAwait(false);
            if (sourceBindingGate.Disposition != GateDisposition.Passed)
            {
                lastQhySolve = null;
                lastQhySolveMountBinding = null;
                lastQhyAcceptedFrameMountBinding = null;
                return await StopQhyCoarseAndReturnAsync(
                    context,
                    state,
                    sourceBindingGate.Code,
                    sourceBindingGate.Message,
                    solve.SourcePath,
                    cancellationToken).ConfigureAwait(false);
            }
            var reportedBeforeDispatch = telescopeMediator.GetCurrentPosition();
            EnsureFiniteReportedCoordinates(reportedBeforeDispatch);
            if (!string.Equals(reportedBeforeDispatch.Epoch.ToString(), currentCommanded.Epoch.ToString(), StringComparison.Ordinal) ||
                AngularSeparationArcseconds(reportedBeforeDispatch, currentCommanded) > MountCommandArrivalToleranceArcseconds)
            {
                lastQhySolve = null;
                lastQhySolveMountBinding = null;
                lastQhyAcceptedFrameMountBinding = null;
                return await StopQhyCoarseAndReturnAsync(
                    context,
                    state,
                    "QHY_COARSE_SOURCE_POSITION_CHANGED",
                    "The fresh reported mount position changed beyond the arrival tolerance after the QHY solve; the stale correction was withheld and the field must be reacquired.",
                    solve.SourcePath,
                    cancellationToken).ConfigureAwait(false);
            }
            var commandHorizon = ValidateCommandCoordinateHorizon(context, correctedCommanded, "QHY coarse-centering outbound move");
            if (commandHorizon.Disposition != GateDisposition.Passed)
            {
                return await StopQhyCoarseAndReturnAsync(
                    context,
                    state,
                    commandHorizon.Code,
                    commandHorizon.Message,
                    solve.SourcePath,
                    cancellationToken).ConfigureAwait(false);
            }
            Report($"QHY WCS 独立粗修正 {moveMagnitude:F1} arcsec（残差 {residual:F1}，RA* {raArcseconds:+0.0;-0.0;0.0}，Dec {decArcseconds:+0.0;-0.0;0.0}）");
            // Persist the conservative, anticipated outbound state before the
            // asynchronous command.  If the call is interrupted after the
            // mount accepted motion, Resume will return from this waypoint;
            // a rejected command merely consumes budget conservatively.
            RegisterQhyCoarseCorrection(moveMagnitude);
            state = state with
            {
                CurrentRaTangentOffsetArcseconds = nextOriginRa,
                CurrentDeclinationOffsetArcseconds = nextOriginDec,
                CumulativeMotionArcseconds = qhyCoarseCumulativeArcseconds,
            };
            pendingQhyCoarseReturn = state;
            if (!await telescopeMediator.SlewToCoordinatesAsync(correctedCommanded, cancellationToken).ConfigureAwait(false))
            {
                return await StopQhyCoarseAndReturnAsync(
                    context,
                    state,
                    "QHY_CORRECTION_REJECTED",
                    "N.I.N.A. telescope mediator rejected the independently bounded QHY correction.",
                    solve.SourcePath,
                    cancellationToken).ConfigureAwait(false);
            }
            await telescopeMediator.WaitForSlew(cancellationToken).ConfigureAwait(false);
            state = ReanchorQhyCoarseStateFromReportedPosition(state);
            pendingQhyCoarseReturn = state;
            var reportedAfterMove = telescopeMediator.GetCurrentPosition();
            var commandResidualArcseconds = AngularSeparationArcseconds(reportedAfterMove, correctedCommanded);
            if (!double.IsFinite(commandResidualArcseconds) ||
                commandResidualArcseconds > MountCommandArrivalToleranceArcseconds)
            {
                return await StopQhyCoarseAndReturnAsync(
                    context,
                    state,
                    "QHY_COARSE_COMMAND_NOT_REACHED",
                    $"The mount stopped {commandResidualArcseconds:F2} arcsec from the QHY coarse command. The reported position was adopted; no reacquisition is permitted before bounded return.",
                    solve.SourcePath,
                    cancellationToken).ConfigureAwait(false);
            }
            await PublishRunJsonEvidenceAsync(
                "qhy-coarse-centering-move-completed",
                $"QHY coarse-centering move {qhyCoarseCorrectionAttempts} completed",
                new
                {
                    moveIntentPath,
                    requestedTargetRaDegrees = target.RADegrees,
                    requestedTargetDecDegrees = target.Dec,
                    solvedCenterRaDegrees = solve.Result.Coordinates.RADegrees,
                    solvedCenterDecDegrees = solve.Result.Coordinates.Dec,
                    raTangentOffsetArcseconds = raArcseconds,
                    decOffsetArcseconds = decArcseconds,
                    priorCommandedRaDegrees = currentCommanded.RADegrees,
                    priorCommandedDecDegrees = currentCommanded.Dec,
                    correctedCommandedRaDegrees = correctedCommanded.RADegrees,
                    correctedCommandedDecDegrees = correctedCommanded.Dec,
                    reportedRaDegrees = reportedAfterMove.RADegrees,
                    reportedDecDegrees = reportedAfterMove.Dec,
                    commandResidualArcseconds,
                    priorResidualArcseconds = residual,
                    qhyCoarseCumulativeArcseconds,
                    qhyCoarseCorrectionAttempts,
                    solve.SolverIdentity,
                },
                solve.SourcePath,
                cancellationToken).ConfigureAwait(false);
            await WriteAuditBestEffortAsync("qhy-wcs-coarse-correction", new
            {
                requestedTargetRaDegrees = target.RADegrees,
                requestedTargetDecDegrees = target.Dec,
                solvedCenterRaDegrees = solve.Result.Coordinates.RADegrees,
                solvedCenterDecDegrees = solve.Result.Coordinates.Dec,
                raTangentOffsetArcseconds = raArcseconds,
                decOffsetArcseconds = decArcseconds,
                priorCommandedRaDegrees = currentCommanded.RADegrees,
                priorCommandedDecDegrees = currentCommanded.Dec,
                correctedCommandedRaDegrees = correctedCommanded.RADegrees,
                correctedCommandedDecDegrees = correctedCommanded.Dec,
                residualArcseconds = residual,
                independentCoarseSchemaVersion = limits.SchemaVersion,
                solve.SolverIdentity,
            }).ConfigureAwait(false);

            qhyAcquisitionJobId = null;
            qhyAcquisitionMountReadbackJobId = null;
            qhyAcquisitionBeforeJobMountReadback = null;
            lastQhyAcquisition = null;
            lastQhySolve = null;
            lastQhySolveMountBinding = null;
            lastQhyAcceptedFrameMountBinding = null;
            qhyAcquisitionAttempt++;
            var reacquired = await AcquireQhyWideFieldAsync(context, cancellationToken).ConfigureAwait(false);
            if (!reacquired.CanAdvance)
            {
                return await StopQhyCoarseAndReturnAsync(
                    context,
                    state,
                    "QHY_COARSE_REACQUIRE_FAILED",
                    $"QHY reacquisition after a coarse move did not pass: {reacquired.Gate.Code}: {reacquired.Gate.Message}",
                    reacquired.EvidencePath ?? solve.SourcePath,
                    cancellationToken).ConfigureAwait(false);
            }
            solve = lastQhySolve!;
            sourceFrameId = lastQhyAcquisition!.AcceptedFrameId!.Value;
            sourceFrame = lastQhyAcquisition.Frames.Single(frame => frame.FrameId == sourceFrameId);
            residual = AngularSeparationArcseconds(target, solve.Result.Coordinates);
            if (residual > configuration.Qhy.CenteringToleranceArcseconds && residual > priorResidual * 1.25)
            {
                var returned = await ReturnQhyCoarseToOriginAsync(context, state, cancellationToken).ConfigureAwait(false);
                if (!returned.ReturnedToOrigin)
                {
                    return new StageResult(
                        GateResult.Unknown(
                            "QHY_COARSE_RESPONSE_INVALID_RETURN_BLOCKED",
                            $"QHY residual worsened from {priorResidual:F1} to {residual:F1} arcsec and safe return is blocked: {returned.Message}"),
                        returned.EvidencePath);
                }
                var invalid = await InvalidateCommissioningAsync(
                    "COMMISSIONING_COARSE_RESPONSE_INVALID",
                    $"A signed QHY WCS correction worsened the target residual from {priorResidual:F1} to {residual:F1} arcsec, exceeding the locked response tolerance.").ConfigureAwait(false);
                return new StageResult(invalid, returned.EvidencePath);
            }
        }
        pendingQhyCoarseReturn = null;
        return Passed(
            "QHY_COARSE_CENTERED",
            $"QHY WCS residual {residual:F1} arcsec is within {configuration.Qhy.CenteringToleranceArcseconds:F1} arcsec.",
            new Dictionary<string, double>
            {
                ["residualArcseconds"] = residual,
                ["qhyCoarseCumulativeCorrectionArcseconds"] = qhyCoarseCumulativeArcseconds,
                ["qhyCoarseCorrectionAttempts"] = qhyCoarseCorrectionAttempts,
            },
            new Dictionary<string, string>
            {
                ["solver"] = solve.SolverIdentity,
                ["qhyCoarseCenteringSchemaVersion"] = limits.SchemaVersion.ToString(CultureInfo.InvariantCulture),
                ["qhyCoarseDeclaredEvidencePath"] = declaredPath,
                ["fineG3SlitMotionEnvelopeWasNotUsed"] = bool.TrueString,
            });
        }
        finally
        {
            context.RemainingWorstCaseDuration = previousWorstCaseDuration;
        }
    }

    private GateResult ValidateQhyCoarseMountState(string expectedPierSide)
    {
        var mount = telescopeMediator.GetInfo();
        if (!mount.Connected) return GateResult.Unknown("QHY_COARSE_MOUNT_DISCONNECTED", "The mount disconnected before a QHY coarse-centering action.");
        if (mount.AtPark) return GateResult.Fail("QHY_COARSE_MOUNT_PARKED", "The mount is parked; QHY coarse-centering motion is prohibited.");
        if (mount.Slewing) return GateResult.Unknown("QHY_COARSE_MOUNT_SLEWING", "The mount is already slewing; QHY coarse-centering motion is prohibited.");
        if (!mount.TrackingEnabled) return GateResult.Unknown("QHY_COARSE_TRACKING_DISABLED", "Mount tracking is disabled; QHY coarse-centering motion is prohibited.");
        if (mount.IsPulseGuiding) return GateResult.Unknown("QHY_COARSE_PULSE_GUIDING_ACTIVE", "The mount reports active pulse guiding; QHY coarse-centering motion is prohibited.");
        var currentPierSide = mount.SideOfPier.ToString();
        if (!IsKnownPierSide(expectedPierSide) || !IsKnownPierSide(currentPierSide))
        {
            return GateResult.Unknown(
                "QHY_COARSE_PIER_SIDE_UNKNOWN",
                $"A known exact pier side is required for QHY coarse centering (saved '{expectedPierSide}', current '{currentPierSide}').");
        }
        if (!string.Equals(currentPierSide, expectedPierSide, StringComparison.Ordinal))
        {
            return GateResult.Fail(
                "QHY_COARSE_PIER_SIDE_CHANGED",
                $"The mount pier side changed from '{expectedPierSide}' to '{currentPierSide}' during QHY coarse centering.");
        }
        return GateResult.Pass(
            "QHY_COARSE_MOUNT_STATE_VALID",
            "Mount is connected, unparked, tracking, idle, not pulse-guiding and remains on the saved pier side.");
    }

    private GateResult ValidateQhyCoarseMoveAndReturnReserve(
        QhyPendingCoarseReturn state,
        double moveMagnitudeArcseconds,
        double returnRadiusArcseconds)
    {
        var limits = configuration.Qhy.CoarseCenteringLimits;
        if (DateTimeOffset.UtcNow - state.StartedUtc >= limits.MaximumElapsedTime)
        {
            return GateResult.Fail(
                "QHY_COARSE_TIME_LIMIT",
                $"QHY coarse centering reached its {limits.MaximumElapsedTime.TotalMinutes:F1} minute limit.");
        }
        if (!double.IsFinite(moveMagnitudeArcseconds) || moveMagnitudeArcseconds <= 0 ||
            moveMagnitudeArcseconds > limits.MaximumSingleCorrectionArcseconds + 1e-9)
        {
            return GateResult.Fail(
                "QHY_COARSE_SINGLE_LIMIT",
                $"Requested coarse move {moveMagnitudeArcseconds:F2} arcsec is invalid or exceeds {limits.MaximumSingleCorrectionArcseconds:F2} arcsec.");
        }
        if (!double.IsFinite(returnRadiusArcseconds) || returnRadiusArcseconds < 0)
        {
            return GateResult.Fail("QHY_COARSE_RETURN_RESERVE_INVALID", "The computed return-to-origin radius is invalid.");
        }
        var returnMoves = returnRadiusArcseconds <= 1e-9
            ? 0
            : checked((int)Math.Ceiling(returnRadiusArcseconds / limits.MaximumSingleCorrectionArcseconds));
        var requiredCumulative = qhyCoarseCumulativeArcseconds + moveMagnitudeArcseconds + returnRadiusArcseconds;
        if (requiredCumulative > limits.MaximumCumulativeCorrectionArcseconds + 1e-9)
        {
            return GateResult.Fail(
                "QHY_COARSE_CUMULATIVE_RETURN_RESERVE_LIMIT",
                $"The next coarse move plus straight-line return would require {requiredCumulative:F2} arcsec, exceeding {limits.MaximumCumulativeCorrectionArcseconds:F2} arcsec.");
        }
        if (qhyCoarseCorrectionAttempts + 1 + returnMoves > limits.MaximumCorrectionAttempts)
        {
            return GateResult.Fail(
                "QHY_COARSE_ATTEMPT_RETURN_RESERVE_LIMIT",
                $"The next coarse move plus {returnMoves} return move(s) would exceed the independent attempt limit {limits.MaximumCorrectionAttempts}.");
        }
        return GateResult.Pass(
            "QHY_COARSE_MOVE_AND_RETURN_RESERVED",
            "The QHY coarse move and a no-larger-than-single-limit return are reserved inside the independent schema-1 envelope.");
    }

    private void RegisterQhyCoarseCorrection(double magnitudeArcseconds)
    {
        qhyCoarseCumulativeArcseconds += magnitudeArcseconds;
        qhyCoarseCorrectionAttempts++;
    }

    private async Task<QhyCoarseReturnResult> ReturnQhyCoarseToOriginAsync(
        ObservationContext context,
        QhyPendingCoarseReturn state,
        CancellationToken cancellationToken)
    {
        var limits = configuration.Qhy.CoarseCenteringLimits;
        try
        {
            state = ReanchorQhyCoarseStateFromReportedPosition(state);
            pendingQhyCoarseReturn = state;
        }
        catch (Exception ex)
        {
            return new QhyCoarseReturnResult(
                false,
                state,
                state.DeclaredEvidencePath,
                $"The reported mount position could not be adopted for safe QHY return: {ex.Message}");
        }
        if (state.CurrentRadiusArcseconds > limits.MaximumCumulativeCorrectionArcseconds + 1e-9)
        {
            return new QhyCoarseReturnResult(
                false,
                state,
                state.DeclaredEvidencePath,
                $"The reported position is {state.CurrentRadiusArcseconds:F2} arcsec from the saved QHY origin, outside the declared {limits.MaximumCumulativeCorrectionArcseconds:F2} arcsec envelope. Automatic return after external/manual motion is prohibited.");
        }
        if (state.CurrentRadiusArcseconds <= MountCommandArrivalToleranceArcseconds)
        {
            pendingQhyCoarseReturn = null;
            var atOriginPath = await PublishRunJsonEvidenceAsync(
                "qhy-coarse-return-summary",
                "QHY coarse centering was already at its saved origin",
                new
                {
                    returnedToOrigin = true,
                    state.DeclaredEvidencePath,
                    qhyCoarseCumulativeArcseconds,
                    qhyCoarseCorrectionAttempts,
                },
                sourcePath: null,
                cancellationToken).ConfigureAwait(false);
            return new QhyCoarseReturnResult(true, state, atOriginPath, "Already at the saved QHY coarse origin.");
        }

        var totalMoves = checked((int)Math.Ceiling(
            state.CurrentRadiusArcseconds / limits.MaximumSingleCorrectionArcseconds));
        var initialRa = state.CurrentRaTangentOffsetArcseconds;
        var initialDec = state.CurrentDeclinationOffsetArcseconds;
        var current = state;
        for (var move = 1; move <= totalMoves; move++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingFraction = (double)(totalMoves - move) / totalMoves;
            var nextRa = initialRa * remainingFraction;
            var nextDec = initialDec * remainingFraction;
            var deltaRa = nextRa - current.CurrentRaTangentOffsetArcseconds;
            var deltaDec = nextDec - current.CurrentDeclinationOffsetArcseconds;
            var magnitude = Math.Sqrt(deltaRa * deltaRa + deltaDec * deltaDec);
            if (!double.IsFinite(magnitude) || magnitude <= 0 || magnitude > limits.MaximumSingleCorrectionArcseconds + 1e-9 ||
                qhyCoarseCumulativeArcseconds + magnitude > limits.MaximumCumulativeCorrectionArcseconds + 1e-9 ||
                qhyCoarseCorrectionAttempts >= limits.MaximumCorrectionAttempts)
            {
                var invalidPath = await PublishRunJsonEvidenceAsync(
                    "qhy-coarse-return-blocked",
                    "QHY coarse return withheld by its independent motion envelope",
                    new
                    {
                        move,
                        totalMoves,
                        magnitudeArcseconds = magnitude,
                        qhyCoarseCumulativeArcseconds,
                        qhyCoarseCorrectionAttempts,
                        limits,
                    },
                    sourcePath: null,
                    cancellationToken).ConfigureAwait(false);
                return new QhyCoarseReturnResult(false, current, invalidPath, "The reserved return no longer fits its independent single/cumulative/attempt envelope.");
            }
            try
            {
                await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (ResumeStageRestartException)
            {
                throw;
            }
            catch (PhysicalActionGateException ex)
            {
                var gatePath = await PublishRunJsonEvidenceAsync(
                    "qhy-coarse-return-blocked",
                    "QHY coarse return blocked by a current safety/horizon gate",
                    new { ex.Gate.Code, ex.Gate.Message, move, totalMoves },
                    sourcePath: null,
                    CancellationToken.None).ConfigureAwait(false);
                return new QhyCoarseReturnResult(false, current, gatePath, $"{ex.Gate.Code}: {ex.Gate.Message}");
            }
            var mountGate = ValidateQhyCoarseMountState(current.OriginPierSide);
            if (mountGate.Disposition != GateDisposition.Passed)
            {
                var mountPath = await PublishRunJsonEvidenceAsync(
                    "qhy-coarse-return-blocked",
                    "QHY coarse return blocked by mount state",
                    new { mountGate.Code, mountGate.Message, move, totalMoves },
                    sourcePath: null,
                    CancellationToken.None).ConfigureAwait(false);
                return new QhyCoarseReturnResult(false, current, mountPath, $"{mountGate.Code}: {mountGate.Message}");
            }

            var commanded = ApplySkyCorrection(current.Origin, nextRa, nextDec);
            var commandHorizon = ValidateCommandCoordinateHorizon(context, commanded, "QHY coarse-centering return move");
            if (commandHorizon.Disposition != GateDisposition.Passed)
            {
                var horizonPath = await PublishRunJsonEvidenceAsync(
                    "qhy-coarse-return-blocked",
                    "QHY coarse return blocked by the actual command-coordinate horizon",
                    new { commandHorizon.Code, commandHorizon.Message, move, totalMoves },
                    sourcePath: null,
                    CancellationToken.None).ConfigureAwait(false);
                return new QhyCoarseReturnResult(false, current, horizonPath, $"{commandHorizon.Code}: {commandHorizon.Message}");
            }
            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            Report($"QHY 粗居中安全回原点 {move}/{totalMoves}：剩余 RA* {nextRa:+0.0;-0.0;0.0}″，Dec {nextDec:+0.0;-0.0;0.0}″");
            if (!await telescopeMediator.SlewToCoordinatesAsync(commanded, cancellationToken).ConfigureAwait(false))
            {
                var rejectPath = await PublishRunJsonEvidenceAsync(
                    "qhy-coarse-return-blocked",
                    "QHY coarse return move rejected by N.I.N.A.",
                    new { move, totalMoves, commandedRaDegrees = commanded.RADegrees, commandedDecDegrees = commanded.Dec },
                    sourcePath: null,
                    CancellationToken.None).ConfigureAwait(false);
                return new QhyCoarseReturnResult(false, current, rejectPath, $"N.I.N.A. rejected return move {move}/{totalMoves}.");
            }
            await telescopeMediator.WaitForSlew(cancellationToken).ConfigureAwait(false);
            var priorRa = current.CurrentRaTangentOffsetArcseconds;
            var priorDec = current.CurrentDeclinationOffsetArcseconds;
            RegisterQhyCoarseCorrection(magnitude);
            current = ReanchorQhyCoarseStateFromReportedPosition(current) with
            {
                CumulativeMotionArcseconds = qhyCoarseCumulativeArcseconds,
            };
            pendingQhyCoarseReturn = current;
            var actualMoveArcseconds = Math.Sqrt(
                Math.Pow(current.CurrentRaTangentOffsetArcseconds - priorRa, 2) +
                Math.Pow(current.CurrentDeclinationOffsetArcseconds - priorDec, 2));
            var reportedAfterReturnMove = telescopeMediator.GetCurrentPosition();
            var commandResidualArcseconds = AngularSeparationArcseconds(reportedAfterReturnMove, commanded);
            if (!double.IsFinite(actualMoveArcseconds) ||
                actualMoveArcseconds > limits.MaximumSingleCorrectionArcseconds + MountCommandArrivalToleranceArcseconds ||
                !double.IsFinite(commandResidualArcseconds) ||
                commandResidualArcseconds > MountCommandArrivalToleranceArcseconds)
            {
                return new QhyCoarseReturnResult(
                    false,
                    current,
                    current.DeclaredEvidencePath,
                    $"Return move {move}/{totalMoves} stopped at a reported position inconsistent with its bounded command (actual move {actualMoveArcseconds:F2} arcsec, command residual {commandResidualArcseconds:F2} arcsec). The reported position was adopted; no further automatic motion is permitted.");
            }
            await PublishRunJsonEvidenceAsync(
                "qhy-coarse-return-move",
                $"QHY coarse safe return move {move}/{totalMoves}",
                new
                {
                    schemaVersion = limits.SchemaVersion,
                    move,
                    totalMoves,
                    magnitudeArcseconds = magnitude,
                    remainingRaTangentOffsetArcseconds = current.CurrentRaTangentOffsetArcseconds,
                    remainingDeclinationOffsetArcseconds = current.CurrentDeclinationOffsetArcseconds,
                    actualMoveArcseconds,
                    commandResidualArcseconds,
                    qhyCoarseCumulativeArcseconds,
                    qhyCoarseCorrectionAttempts,
                    commandedRaDegrees = commanded.RADegrees,
                    commandedDecDegrees = commanded.Dec,
                    reportedRaDegrees = reportedAfterReturnMove.RADegrees,
                    reportedDecDegrees = reportedAfterReturnMove.Dec,
                    safetyAndHorizonCheckpointPassedImmediatelyBeforeMove = true,
                },
                sourcePath: null,
                cancellationToken).ConfigureAwait(false);
        }

        current = ReanchorQhyCoarseStateFromReportedPosition(current);
        if (current.CurrentRadiusArcseconds > MountCommandArrivalToleranceArcseconds)
        {
            pendingQhyCoarseReturn = current;
            return new QhyCoarseReturnResult(
                false,
                current,
                current.DeclaredEvidencePath,
                $"All planned QHY return segments ended, but the reported position remains {current.CurrentRadiusArcseconds:F2} arcsec from the saved origin.");
        }
        pendingQhyCoarseReturn = null;
        var summaryPath = await PublishRunJsonEvidenceAsync(
            "qhy-coarse-return-summary",
            "QHY coarse centering returned to its saved origin",
            new
            {
                returnedToOrigin = true,
                schemaVersion = limits.SchemaVersion,
                current.DeclaredEvidencePath,
                qhyCoarseCumulativeArcseconds,
                qhyCoarseCorrectionAttempts,
                originRaDegrees = current.Origin.RADegrees,
                originDecDegrees = current.Origin.Dec,
                current.OriginPierSide,
            },
            sourcePath: null,
            cancellationToken).ConfigureAwait(false);
        return new QhyCoarseReturnResult(true, current, summaryPath, "Every bounded return move to the saved QHY coarse-origin coordinates completed.");
    }

    private async Task<StageResult> StopQhyCoarseAndReturnAsync(
        ObservationContext context,
        QhyPendingCoarseReturn state,
        string reasonCode,
        string reason,
        string? sourcePath,
        CancellationToken cancellationToken)
    {
        var returned = await ReturnQhyCoarseToOriginAsync(context, state, cancellationToken).ConfigureAwait(false);
        if (returned.ReturnedToOrigin)
        {
            // Any WCS obtained away from the saved origin is stale after the
            // return.  Clearing it prevents a nonstandard caller from applying
            // that residual to the origin before a fresh QHY acquisition.
            lastQhyAcquisition = null;
            lastQhySolve = null;
            lastQhySolveMountBinding = null;
            lastQhyAcceptedFrameMountBinding = null;
            currentQhyFocusMetric = null;
            if (qhyAcquisitionJobId is not { } activeId || !activeQhyJobs.ContainsKey(activeId))
            {
                qhyAcquisitionJobId = null;
                qhyAcquisitionAttempt++;
            }
        }
        var gate = returned.ReturnedToOrigin
            ? GateResult.Unknown(
                "QHY_COARSE_STOPPED_RETURNED",
                $"{reasonCode}: {reason} The mount returned to the saved QHY coarse origin and automation is paused for inspection.")
            : GateResult.Unknown(
                "QHY_COARSE_RETURN_BLOCKED",
                $"{reasonCode}: {reason} Safe return to the saved QHY coarse origin is blocked: {returned.Message}");
        return new StageResult(gate, returned.EvidencePath, new Dictionary<string, string>
        {
            ["qhyCoarseStopReasonCode"] = reasonCode,
            ["qhyCoarseSourcePath"] = sourcePath ?? string.Empty,
            ["qhyCoarseReturnOutcome"] = returned.ReturnedToOrigin ? "Returned" : "Blocked",
            ["qhyCoarseCenteringSchemaVersion"] = configuration.Qhy.CoarseCenteringLimits.SchemaVersion.ToString(CultureInfo.InvariantCulture),
        });
    }

    private async Task<StageResult?> CompletePendingQhyCoarseReturnAsync(
        ObservationContext context,
        CancellationToken cancellationToken)
    {
        var pending = pendingQhyCoarseReturn;
        if (pending is null) return null;
        var returned = await ReturnQhyCoarseToOriginAsync(context, pending, cancellationToken).ConfigureAwait(false);
        if (!returned.ReturnedToOrigin)
        {
            return new StageResult(
                GateResult.Unknown(
                    "QHY_COARSE_PENDING_RETURN_BLOCKED",
                    $"An interrupted QHY coarse correction still requires safe return to its saved origin: {returned.Message}"),
                returned.EvidencePath);
        }

        qhyAcquisitionJobId = null;
        qhyAcquisitionMountReadbackJobId = null;
        qhyAcquisitionBeforeJobMountReadback = null;
        lastQhyAcquisition = null;
        lastQhySolve = null;
        lastQhySolveMountBinding = null;
        lastQhyAcceptedFrameMountBinding = null;
        currentQhyFocusMetric = null;
        qhyAcquisitionAttempt++;
        var reacquired = await AcquireQhyWideFieldAsync(context, cancellationToken).ConfigureAwait(false);
        return reacquired.CanAdvance ? null : reacquired;
    }

    private async Task<StageResult> AcquireG3SlitFieldAsync(ObservationContext context, CancellationToken cancellationToken)
    {
        var interlock = await EvaluateInterlocksAsync(context, connectQhy: false, cancellationToken).ConfigureAwait(false);
        if (interlock.Disposition != GateDisposition.Passed) return new StageResult(interlock);
        fineAcquisitionStartedUtc ??= DateTimeOffset.UtcNow;
        if (configuration.G3.FocalLengthMillimeters <= 0 || configuration.G3.PixelSizeMicrometers <= 0)
        {
            return Attention(ObservationStage.AcquireG3SlitField, "G3_OPTICS_UNCOMMISSIONED", "G3 focal length and pixel size must be measured and configured; plate scale will not be guessed.");
        }
        if (configuration.G3.ExpectedWcsFlipped)
        {
            return Attention(ObservationStage.AcquireG3SlitField, "G3_FLIPPED_WCS_UNIMPLEMENTED", "A flipped G3 WCS is configured, but its detector-axis parity mapping has not been commissioned in this adapter.");
        }

        var pendingReturn = await CompletePendingG3SearchReturnAsync(context, cancellationToken).ConfigureAwait(false);
        if (pendingReturn is not null) return pendingReturn;

        var transferEvidencePath = await EnsureWideToSlitTransferSkippedEvidenceAsync(
            context,
            cancellationToken).ConfigureAwait(false);

        lastG3Field = await CaptureAndAnalyzeG3WithSolveLadderAsync(context, cancellationToken).ConfigureAwait(false);
        if (lastG3Field.Gate.Disposition == GateDisposition.Passed)
        {
            return G3FieldPassed(lastG3Field, transferEvidencePath, searchAttempts: 0, searchEvidencePath: null);
        }
        if (lastG3Field.Gate.Code == "G3_SOLVED_TARGET_OUTSIDE" && lastG3Field.Solve?.Result.Success == true)
        {
            return await RunG3WcsCenteringAsync(
                context,
                lastG3Field,
                transferEvidencePath,
                cancellationToken).ConfigureAwait(false);
        }
        if (!IsRecoverableG3SearchGate(lastG3Field.Gate))
        {
            return new StageResult(
                lastG3Field.Gate,
                lastG3Field.FramePath,
                new Dictionary<string, string>
                {
                    ["wideToSlitTransferMode"] = WideToSlitTransferMode.Skip.ToString(),
                    ["transferOutcome"] = "TransferSkipped",
                    ["transferEvidencePath"] = transferEvidencePath,
                    ["slitIdentityGate"] = lastG3Field.SlitIdentity?.Gate.Code ?? "SLIT_LED_IDENTITY_NOT_RECORDED",
                    ["slitIdentityEvidencePath"] = lastG3Field.SlitIdentityEvidencePath ?? string.Empty,
                });
        }

        return await RunBoundedG3LocalSearchAsync(
            context,
            lastG3Field,
            transferEvidencePath,
            cancellationToken).ConfigureAwait(false);
    }

    private StageResult G3FieldPassed(
        G3FieldState field,
        string transferEvidencePath,
        int searchAttempts,
        string? searchEvidencePath,
        int wcsCenteringAttempts = 0,
        string? wcsCenteringEvidencePath = null)
    {
        var brightTarget = field.BrightTargetAnalysis is not null && field.BrightTargetAuthority is not null;
        var ghostTarget = field.GhostAssistance is { Result.Decision: GhostAssistanceDecision.UseCalibratedAuxiliaryEstimate };
        var metadata = new Dictionary<string, string>
        {
            ["g3Frame"] = field.FramePath,
            ["solver"] = field.Solve?.SolverIdentity ?? "none",
            ["g3PlateSolveSucceeded"] = (field.Solve?.Result.Success == true).ToString(),
            ["slitCalibrationId"] = field.SlitDetection.Geometry.CalibrationId,
            ["slitAuthority"] = "paired-led-median-composites",
            ["slitIdentityGate"] = field.SlitIdentity?.Gate.Code ?? "SLIT_LED_IDENTITY_NOT_RECORDED",
            ["slitIdentityCalibrationId"] = field.SlitIdentity?.CalibrationId ?? string.Empty,
            ["slitIdentityMatchedPosition"] = field.SlitIdentity?.MatchedCandidate?.WheelPosition.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            ["slitIdentityMeasuredWidthPixels"] = field.SlitIdentity?.MeasuredWidthPixels.ToString("F3", CultureInfo.InvariantCulture) ?? string.Empty,
            ["wideToSlitTransferMode"] = WideToSlitTransferMode.Skip.ToString(),
            ["transferOutcome"] = "TransferSkipped",
            ["transferEvidencePath"] = transferEvidencePath,
            ["g3SearchAttempts"] = searchAttempts.ToString(CultureInfo.InvariantCulture),
            ["g3WcsCenteringAttempts"] = wcsCenteringAttempts.ToString(CultureInfo.InvariantCulture),
            ["qhyG3FastPairOutcome"] = qhyG3FastPairOutcome,
        };
        AddIfPresent(metadata, "g3SearchEvidencePath", searchEvidencePath);
        AddIfPresent(metadata, "g3WcsCenteringEvidencePath", wcsCenteringEvidencePath);
        AddIfPresent(metadata, "qhyG3FastPairEvidencePath", latestQhyG3TransferCandidateEvidencePath);
        AddIfPresent(metadata, "qhyG3FastPairCandidateId", latestQhyG3TransferCandidate?.CalibrationId);
        AddIfPresent(metadata, "qhyG3FastPairCandidateSha256", latestQhyG3TransferCandidate?.CandidateSha256);
        AddIfPresent(metadata, "brightTargetEvidencePath", field.BrightTargetEvidencePath);
        AddIfPresent(metadata, "ghostAssistanceEvidencePath", field.GhostAssistance?.EvidencePath);
        AddIfPresent(metadata, "slitIdentityEvidencePath", field.SlitIdentityEvidencePath);
        if (brightTarget)
        {
            metadata["targetIdentityAuthority"] = "fresh-qhy-wcs+catalog-target+independent-c11-focus+g3-unsaturated-wings";
            metadata["focusEligible"] = bool.FalseString;
        }
        if (ghostTarget)
        {
            metadata["targetIdentityAuthority"] = field.GhostAssistance!.ExternalIdentity?.Authority.ToString() ?? "external-wcs-unavailable";
            metadata["ghostAuthority"] = GhostLocatorAuthority.CalibratedAuxiliaryOnly.ToString();
            metadata["ghostCanEstablishIdentity"] = bool.FalseString;
            metadata["ghostCanAuthorizeMotion"] = bool.FalseString;
            metadata["focusEligible"] = bool.FalseString;
            AddIfPresent(metadata, "ghostCalibrationId", field.GhostAssistance.CalibrationId);
            AddIfPresent(metadata, "ghostCalibrationSha256", field.GhostAssistance.CalibrationSha256);
            AddIfPresent(metadata, "ghostMatchPolicyId", field.GhostAssistance.MatchPolicyId);
            AddIfPresent(metadata, "ghostMatchPolicySha256", field.GhostAssistance.MatchPolicySha256);
        }
        var message = ghostTarget
            ? $"Hash-bound ghost assistance supplied only an auxiliary target centroid/covariance from {field.GhostAssistance!.Extractions.Count} fresh OFF frames; catalogue identity remains {field.GhostAssistance.ExternalIdentity?.Authority}, paired slit contrast is {field.SlitDetection.ContrastSigma:F2}σ, and fresh slit/PHD2 residual authority is still required."
            : brightTarget
            ? $"G3 bright-target branch identified one unique saturated target from its unsaturated wings after {searchAttempts} bounded search attempt(s); short-frame plate solve success={field.Solve?.Result.Success == true}, paired slit contrast {field.SlitDetection.ContrastSigma:F2}σ. The target frame is excluded from focus."
            : wcsCenteringAttempts > 0
                ? $"G3 WCS recentering placed the catalog target inside the field after {wcsCenteringAttempts} bounded N.I.N.A. correction(s); fresh paired slit contrast {field.SlitDetection.ContrastSigma:F2}σ and target residual {field.TargetIdentification.PredictionResidualPixels:F2} px."
            : searchAttempts == 0
                ? $"G3 direct field solved after an explicit QHY→G3 Skip; paired slit contrast {field.SlitDetection.ContrastSigma:F2}σ and target residual {field.TargetIdentification.PredictionResidualPixels:F2} px."
                : $"G3 bounded search identified the target after {searchAttempts} recovery attempt(s); paired slit contrast {field.SlitDetection.ContrastSigma:F2}σ and target residual {field.TargetIdentification.PredictionResidualPixels:F2} px.";
        var mainFocusMetric = ghostTarget
            ? field.GhostAssistance!.C11Focus?.Metric.Value ?? 0
            : brightTarget
            ? field.BrightTargetAuthority!.C11FocusMetricValue
            : field.MainFocusMeasurement?.MedianFwhmPixels ?? 0;
        var mainFocusConfidence = ghostTarget
            ? field.GhostAssistance!.C11Focus?.Confidence ?? 0
            : brightTarget
            ? field.BrightTargetAuthority!.C11FocusConfidence
            : field.MainFocusMeasurement?.Confidence ?? 0;
        return Passed(
            ghostTarget
                ? "G3_GHOST_AUXILIARY_FIELD_IDENTIFIED"
                : brightTarget
                ? "G3_BRIGHT_TARGET_IDENTIFIED"
                : wcsCenteringAttempts > 0
                    ? "G3_WCS_CENTERED_FIELD_IDENTIFIED"
                    : searchAttempts == 0 ? "G3_SLIT_FIELD_IDENTIFIED" : "G3_BOUNDED_SEARCH_IDENTIFIED",
            message,
            new Dictionary<string, double>
            {
                ["slitContrastSigma"] = field.SlitDetection.ContrastSigma,
                ["targetPredictionResidualPixels"] = field.TargetIdentification.PredictionResidualPixels,
                ["detectedStars"] = field.Candidates.Count,
                ["mainFocusMedianFwhmPixels"] = mainFocusMetric,
                ["mainFocusMedianEllipticity"] = brightTarget || ghostTarget ? 0 : field.MainFocusMeasurement?.MedianEllipticity ?? 0,
                ["mainFocusConfidence"] = mainFocusConfidence,
                ["g3SearchAttempts"] = searchAttempts,
                ["g3WcsCenteringAttempts"] = wcsCenteringAttempts,
                ["g3PlateSolveSucceeded"] = field.Solve?.Result.Success == true ? 1 : 0,
                ["brightTargetFrameFocusEligible"] = brightTarget || ghostTarget ? 0 : 1,
                ["ghostTargetUncertaintyPixels"] = ghostTarget && double.IsFinite(field.GhostAssistance!.Result.TargetUncertaintyPixels)
                    ? field.GhostAssistance.Result.TargetUncertaintyPixels
                    : 0,
                ["ghostCanEstablishIdentity"] = 0,
                ["ghostCanAuthorizeMotion"] = 0,
                ["qhyG3PairMidpointSeparationSeconds"] = latestQhyG3TransferCandidate?.PairMidpointSeparationSeconds ?? 0,
                ["qhyG3PairMountSpanArcseconds"] = latestQhyG3TransferCandidate?.MaximumObservedMountSpanArcseconds ?? 0,
                ["qhyG3PairPrepositionMagnitudeArcseconds"] = latestQhyG3TransferCandidate?.Model.PredictedPrepositionMagnitudeArcseconds ?? 0,
                ["qhyG3PairPredictionUncertaintyArcseconds"] = latestQhyG3TransferCandidate?.Model.PredictionUncertaintyArcseconds ?? 0,
            },
            metadata);
    }

    private async Task<string> EnsureWideToSlitTransferSkippedEvidenceAsync(
        ObservationContext context,
        CancellationToken cancellationToken)
    {
        if (configuration.G3.WideToSlitTransferMode != WideToSlitTransferMode.Skip)
        {
            throw new PhysicalActionGateException(GateResult.Fail(
                "WIDE_TO_SLIT_TRANSFER_MODE_UNSUPPORTED",
                "The runner has no independently verified Active QHY-to-G3 transfer loaded. A paired-WCS Candidate cannot authorize motion, so real acquisition requires WideToSlitTransferMode=Skip."));
        }
        if (wideToSlitTransferEvidencePath is not null) return wideToSlitTransferEvidencePath;

        var payload = new
        {
            selectedMode = WideToSlitTransferMode.Skip.ToString(),
            outcome = "TransferSkipped",
            reasonCode = "QHY_TO_G3_ACTIVE_TRANSFER_UNAVAILABLE",
            reason = "No independently verified Active QHY-to-G3 transfer is loaded. Paired-WCS collection may create a Candidate only; proceed from QHY coarse centering to a fresh G3 solve and bounded recovery.",
            transferRecordId = (string?)null,
            transferRecordSha256 = (string?)null,
            predictedMoveArcseconds = (double?)null,
            commandedPrepositionMoveArcseconds = (double?)null,
            mountTransformReused = false,
            prohibitedSubstitute = "The commissioned G3 pixel-to-mount transform is reserved for final slit placement and was not used as an optical-axis offset.",
            requestedTarget = new
            {
                context.Plan.Target.Name,
                context.Plan.Target.CatalogId,
                context.Plan.Target.RightAscensionDegrees,
                context.Plan.Target.DeclinationDegrees,
            },
        };
        var evidencePath = await PublishRunJsonEvidenceAsync(
            "wide-to-slit-transfer-skipped",
            "Explicit QHY-to-G3 pre-positioning Skip provenance",
            payload,
            sourcePath: null,
            cancellationToken).ConfigureAwait(false);
        await WriteAuditBestEffortAsync("wide-to-slit-transfer-skipped", payload).ConfigureAwait(false);
        context.Set("wideToSlitTransferMode", WideToSlitTransferMode.Skip.ToString());
        context.Set("wideToSlitTransferOutcome", "TransferSkipped");
        wideToSlitTransferEvidencePath = evidencePath;
        return evidencePath;
    }

    private async Task<G3FieldState> CaptureAndAnalyzeG3WithSolveLadderAsync(
        ObservationContext context,
        CancellationToken cancellationToken)
    {
        var probe = await CaptureG3PlateSolveLadderAsync(context, cancellationToken).ConfigureAwait(false);
        if (probe.MountBinding is not null && !string.IsNullOrWhiteSpace(probe.FramePath))
        {
            var probeBindingGate = await ValidateG3ProbeMountBindingForMotionAsync(
                context,
                probe,
                cancellationToken).ConfigureAwait(false);
            if (probeBindingGate.Disposition != GateDisposition.Passed)
            {
                return G3FieldState.Failed(
                    probeBindingGate,
                    probe.FramePath,
                    probe.Image,
                    probe.Solve,
                    probe.MountBinding);
            }
        }
        if (probe.Solve?.Result.Success == true && probe.Solve.Result.Coordinates is not null)
        {
            await TryCollectQhyG3FastSolvePairAsync(context, probe, cancellationToken).ConfigureAwait(false);
        }
        if (probe.Gate.Disposition != GateDisposition.Passed)
        {
            if (probe.Gate.Code == "G3_PLATE_SOLVE_LADDER_EXHAUSTED_STRUCTURED_FIELD")
            {
                // A coherent (including saturated) source proves this is not a
                // featureless exposure. Run the existing deterministic
                // OFF/ON/OFF analysis so the bright-target or sparse-field
                // branch can decide; the solve-only image is never promoted.
                return await CaptureAndAnalyzeG3Async(context, cancellationToken).ConfigureAwait(false);
            }
            return G3FieldState.Failed(probe.Gate, probe.FramePath, probe.Image, probe.Solve, probe.MountBinding);
        }

        // The solve-only ladder proves that this reported mount position has a
        // usable WCS and contains the catalog target. Slit geometry and target
        // morphology still require a fresh OFF/ON/OFF detector-fixed sequence;
        // no probe frame is silently promoted to slit-placement evidence.
        return await CaptureAndAnalyzeG3Async(context, cancellationToken).ConfigureAwait(false);
    }

    private async Task<G3PlateSolveProbeState> CaptureG3PlateSolveLadderAsync(
        ObservationContext context,
        CancellationToken cancellationToken)
    {
        var preset = configuration.G3.PlateSolveExposurePreset;
        var presetIssues = preset.Validate();
        if (presetIssues.Count > 0)
        {
            return G3PlateSolveProbeState.Failed(GateResult.Unknown(
                "G3_PLATE_SOLVE_EXPOSURE_PRESET_INVALID",
                string.Join(" ", presetIssues)));
        }

        await EnsurePhdConnectedAsync(cancellationToken).ConfigureAwait(false);
        var identity = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), cancellationToken).ConfigureAwait(false);
        if (!identity.IsValid)
        {
            return G3PlateSolveProbeState.Failed(GateResult.Unknown(
                "PHD2_IDENTITY_STALE",
                string.Join(" ", identity.Failures.Concat(identity.IndeterminateReasons))));
        }
        var profileEvidenceGate = ValidatePhdProfileBindingEvidence();
        if (profileEvidenceGate.Disposition != GateDisposition.Passed)
        {
            return G3PlateSolveProbeState.Failed(profileEvidenceGate);
        }
        var coverGate = await EnsureOpticalCoverOpenAsync(context, cancellationToken).ConfigureAwait(false);
        if (coverGate.Disposition != GateDisposition.Passed)
        {
            return G3PlateSolveProbeState.Failed(coverGate);
        }
        var slitOff = await EnsureSlitIlluminationOffAsync(
            "G3 plate-solve exposure ladder",
            releaseLeaseOnSuccess: true,
            cancellationToken).ConfigureAwait(false);
        if (slitOff.Issue is not null)
        {
            return G3PlateSolveProbeState.Failed(GateResult.Unknown(
                "G3_SOLVE_LADDER_SLIT_LED_OFF_UNCONFIRMED",
                slitOff.Issue));
        }

        var attempts = new List<G3PlateSolveAttemptEvidence>(preset.ExposureMilliseconds.Count);
        G3PlateSolveProbeState? latest = null;
        for (var index = 0; index < preset.ExposureMilliseconds.Count; index++)
        {
            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            var exposureMilliseconds = preset.ExposureMilliseconds[index];
            var beforeProbeMountReadback = CaptureG3FrameMountReadback();
            var path = ReserveRunEvidencePath(
                $"g3-plate-solve-probe-{index + 1:D2}-{exposureMilliseconds}ms",
                ".fit");
            Report($"PHD2/G3 解算曝光档 {index + 1}/{preset.ExposureMilliseconds.Count}：{exposureMilliseconds} ms");
            var captured = await phd2.CaptureFullFrameAsync(
                new Phd2SingleFrameRequest(
                    exposureMilliseconds,
                    configuration.G3.Binning,
                    configuration.G3.GainPercent,
                    path),
                cancellationToken).ConfigureAwait(false);
            var probeMountReadback = CaptureG3FrameMountReadback();
            var sha256 = await ComputeFileSha256Async(captured.Path, cancellationToken).ConfigureAwait(false);
            var probeMountBinding = CreateG3FieldMountBinding(
                context,
                captured.Path,
                sha256,
                captured.CompletedUtc,
                probeMountReadback);
            PublishEvidencePathOnce(
                "g3-plate-solve-probe-fits",
                captured.Path,
                new Dictionary<string, string>
                {
                    ["exposurePresetSchemaVersion"] = preset.SchemaVersion.ToString(CultureInfo.InvariantCulture),
                    ["exposurePresetId"] = preset.PresetId,
                    ["ladderIndex"] = (index + 1).ToString(CultureInfo.InvariantCulture),
                    ["exposureMilliseconds"] = exposureMilliseconds.ToString(CultureInfo.InvariantCulture),
                    ["gainPercent"] = configuration.G3.GainPercent.ToString(CultureInfo.InvariantCulture),
                    ["binning"] = configuration.G3.Binning.ToString(CultureInfo.InvariantCulture),
                    ["requestedParametersApplied"] = captured.RequestedParametersApplied.ToString(),
                    ["exposureApplied"] = captured.ExposureApplied.ToString(),
                    ["gainAndBinningAppliedByJsonRpc"] = captured.GainAndBinningApplied.ToString(),
                    ["gainAndBinningAuthority"] = "hash-locked-windows-phd2-profile+fits-headers-when-exposed",
                    ["phd2ProfileEvidenceSha256"] = phdProfileEvidence?.Sha256 ?? string.Empty,
                    ["mountBindingSha256"] = probeMountBinding.BindingSha256,
                },
                sha256);

            var image = await imageDataFactory.CreateFromFile(
                captured.Path,
                16,
                false,
                RawConverterEnum.FREEIMAGE,
                cancellationToken).ConfigureAwait(false);
            var imageGate = ValidateG3SolveProbeImage(captured, image, exposureMilliseconds);
            if (imageGate.Disposition != GateDisposition.Passed)
            {
                var invalidAttemptPath = await PublishRunJsonEvidenceAsync(
                    "g3-plate-solve-ladder-attempt",
                    $"G3 plate-solve exposure ladder attempt {index + 1}",
                    new
                    {
                        preset.SchemaVersion,
                        preset.PresetId,
                        ladderIndex = index + 1,
                        exposureMilliseconds,
                        disposition = imageGate.Disposition.ToString(),
                        imageGate.Code,
                        imageGate.Message,
                        solveSucceeded = false,
                        mountBinding = probeMountBinding,
                    },
                    captured.Path,
                    cancellationToken).ConfigureAwait(false);
                attempts.Add(new G3PlateSolveAttemptEvidence(
                    index + 1,
                    exposureMilliseconds,
                    imageGate.Code,
                    imageGate.Disposition,
                    false,
                    captured.Path,
                    invalidAttemptPath,
                    MountBinding: probeMountBinding));
                latest = new G3PlateSolveProbeState(imageGate, captured.Path, image, null, null, attempts.AsReadOnly(), MountBinding: probeMountBinding, BeforeExposureMountReadback: beforeProbeMountReadback);
                continue;
            }

            var properties = image.Properties;
            var raw = image.Data.FlatArray;
            if (raw.Length != properties.Width * properties.Height)
            {
                var bufferGate = GateResult.Unknown(
                    "G3_SOLVE_PROBE_PIXEL_BUFFER_UNSUPPORTED",
                    "N.I.N.A. returned an unsupported G3 pixel buffer for solve-only content validation.");
                var invalidAttemptPath = await PublishRunJsonEvidenceAsync(
                    "g3-plate-solve-ladder-attempt",
                    $"G3 plate-solve exposure ladder attempt {index + 1}",
                    new
                    {
                        preset.SchemaVersion,
                        preset.PresetId,
                        ladderIndex = index + 1,
                        exposureMilliseconds,
                        disposition = bufferGate.Disposition.ToString(),
                        bufferGate.Code,
                        bufferGate.Message,
                        solveSucceeded = false,
                        mountBinding = probeMountBinding,
                    },
                    captured.Path,
                    cancellationToken).ConfigureAwait(false);
                attempts.Add(new G3PlateSolveAttemptEvidence(
                    index + 1,
                    exposureMilliseconds,
                    bufferGate.Code,
                    bufferGate.Disposition,
                    false,
                    captured.Path,
                    invalidAttemptPath,
                    ContentGateCode: bufferGate.Code,
                    CoherentSourceCount: 0,
                    MountBinding: probeMountBinding));
                latest = new G3PlateSolveProbeState(bufferGate, captured.Path, image, null, null, attempts.AsReadOnly(), MountBinding: probeMountBinding, BeforeExposureMountReadback: beforeProbeMountReadback);
                continue;
            }

            var content = G3SolveProbeContentAnalyzer.Analyze(
                G3FrameInputPolicy.Create(properties.Width, properties.Height, raw, configuration.G3));
            if (!content.HasCoherentSource)
            {
                var cloudAttemptPath = await PublishRunJsonEvidenceAsync(
                    "g3-plate-solve-ladder-attempt",
                    $"G3 plate-solve exposure ladder attempt {index + 1}",
                    new
                    {
                        preset.SchemaVersion,
                        preset.PresetId,
                        ladderIndex = index + 1,
                        exposureMilliseconds,
                        disposition = content.Gate.Disposition.ToString(),
                        content.Gate.Code,
                        content.Gate.Message,
                        content.BackgroundMedianAdu,
                        content.BackgroundNoiseSigmaAdu,
                        content.RobustDynamicRangeSigma,
                        content.SaturatedPixelFraction,
                        coherentSourceCount = content.StellarMeasurement.DetectedStarCount,
                        usableSourceCount = content.StellarMeasurement.StarCount,
                        solveSucceeded = false,
                        mountMotionAuthorized = false,
                        mountBinding = probeMountBinding,
                    },
                    captured.Path,
                    cancellationToken).ConfigureAwait(false);
                attempts.Add(new G3PlateSolveAttemptEvidence(
                    index + 1,
                    exposureMilliseconds,
                    content.Gate.Code,
                    content.Gate.Disposition,
                    false,
                    captured.Path,
                    cloudAttemptPath,
                    content.Gate.Code,
                    content.StellarMeasurement.DetectedStarCount,
                    probeMountBinding));
                latest = new G3PlateSolveProbeState(content.Gate, captured.Path, image, null, content, attempts.AsReadOnly(), MountBinding: probeMountBinding, BeforeExposureMountReadback: beforeProbeMountReadback);
                continue;
            }

            var targetCoordinates = TargetCoordinates(context.Plan);
            var solve = await SolveImageAsync(
                image,
                configuration.G3.FocalLengthMillimeters,
                configuration.G3.PixelSizeMicrometers,
                configuration.G3.Binning,
                targetCoordinates,
                $"PHD2/G3 solve-only exposure ladder {preset.PresetId} tier {index + 1}",
                captured.Path,
                cancellationToken).ConfigureAwait(false);
            GateResult resultGate;
            PixelPoint? projectedTarget = null;
            if (!solve.Result.Success || solve.Result.Coordinates is null)
            {
                resultGate = GateResult.Unknown(
                    "G3_PLATE_SOLVE_TIER_FAILED",
                    $"G3 plate-solve exposure tier {index + 1}/{preset.ExposureMilliseconds.Count} ({exposureMilliseconds} ms) did not solve.");
            }
            else if (solve.Result.Flipped != configuration.G3.ExpectedWcsFlipped)
            {
                resultGate = await InvalidateCommissioningAsync(
                    "COMMISSIONING_G3_PARITY_INVALID",
                    $"Solve-only G3 WCS flipped={solve.Result.Flipped}, expected {configuration.G3.ExpectedWcsFlipped}.").ConfigureAwait(false);
            }
            else
            {
                var projected = targetCoordinates.XYProjection(
                    solve.Result.Coordinates,
                    new Point(properties.Width / 2d, properties.Height / 2d),
                    solve.Result.Pixscale,
                    solve.Result.Pixscale,
                    solve.Result.PositionAngle);
                projectedTarget = new PixelPoint(projected.X, projected.Y);
                resultGate = G3SolvedFieldPolicy.TargetInsideField(
                    projected.X,
                    projected.Y,
                    properties.Width,
                    properties.Height,
                    configuration.G3.WcsCentering.TargetInsideFieldMarginPixels);
                PublishG3Preview(
                    image,
                    resultGate.Code == "G3_SOLVED_TARGET_OUTSIDE"
                        ? $"G3 长曝光已解算，但目标投影 ({projected.X:F1},{projected.Y:F1}) 在画外；下一步为有界 N.I.N.A. WCS 居中。"
                        : $"G3 长曝光已解算，目标投影 ({projected.X:F1},{projected.Y:F1}) 在可用画幅内；将拍新照明序列确认狭缝和目标。",
                    target: projectedTarget);
            }

            var attemptPath = await PublishRunJsonEvidenceAsync(
                "g3-plate-solve-ladder-attempt",
                $"G3 plate-solve exposure ladder attempt {index + 1}",
                new
                {
                    preset.SchemaVersion,
                    preset.PresetId,
                    ladderIndex = index + 1,
                    exposureMilliseconds,
                    disposition = resultGate.Disposition.ToString(),
                    resultGate.Code,
                    resultGate.Message,
                    solveSucceeded = solve.Result.Success,
                    solve.ResidualArcseconds,
                    solvedRaDegrees = solve.Result.Coordinates?.RADegrees,
                    solvedDecDegrees = solve.Result.Coordinates?.Dec,
                    projectedTarget,
                    mountBinding = probeMountBinding,
                },
                captured.Path,
                cancellationToken).ConfigureAwait(false);
            attempts.Add(new G3PlateSolveAttemptEvidence(
                index + 1,
                exposureMilliseconds,
                resultGate.Code,
                resultGate.Disposition,
                solve.Result.Success,
                captured.Path,
                attemptPath,
                content.Gate.Code,
                content.StellarMeasurement.DetectedStarCount,
                probeMountBinding));
            latest = new G3PlateSolveProbeState(
                resultGate,
                captured.Path,
                image,
                solve,
                content,
                attempts.AsReadOnly(),
                MountBinding: probeMountBinding,
                BeforeExposureMountReadback: beforeProbeMountReadback);
            if (solve.Result.Success) return latest;
        }

        var hasStructuredContent = attempts.Any(attempt => attempt.CoherentSourceCount > 0);
        var summaryPath = await PublishRunJsonEvidenceAsync(
            "g3-plate-solve-ladder-summary",
            "G3 plate-solve exposure ladder exhausted",
            new
            {
                preset.SchemaVersion,
                preset.PresetId,
                exposureMilliseconds = preset.ExposureMilliseconds,
                attempts,
                outcome = "NoWcs",
                hasStructuredContent,
                nextRecovery = hasStructuredContent
                    ? "deterministic-bright-target-or-sparse-field-analysis"
                    : "PausedNeedsAttention",
                mountMotionAuthorized = hasStructuredContent,
            },
            latest?.FramePath,
            cancellationToken).ConfigureAwait(false);
        var exhausted = hasStructuredContent
            ? GateResult.Unknown(
                "G3_PLATE_SOLVE_LADDER_EXHAUSTED_STRUCTURED_FIELD",
                $"All {preset.ExposureMilliseconds.Count} versioned G3 exposure tier(s) failed to produce WCS, but at least one contained a coherent source. The deterministic bright-target/sparse-field analysis may run before any bounded search; no target identity or optical offset was inferred.")
            : GateResult.Unknown(
                "G3_CLOUD_OR_TRANSPARENCY_INVALID",
                $"All {preset.ExposureMilliseconds.Count} versioned G3 exposure tier(s) lacked a coherent source or valid pixel evidence. Cloud, lost transparency and an empty field cannot be distinguished safely, so no mount search motion is authorized.");
        return latest is null
            ? G3PlateSolveProbeState.Failed(exhausted)
            : latest with
            {
                Gate = exhausted,
                Attempts = attempts.AsReadOnly(),
                SummaryEvidencePath = summaryPath,
            };
    }

    private GateResult ValidateG3SolveProbeImage(
        Phd2SingleFrameResult captured,
        IImageData image,
        int requestedExposureMilliseconds)
    {
        return G3SolveProbeCapturePolicy.Validate(
            captured,
            image.MetaData.Image.ExposureTime * 1000,
            image.MetaData.Camera.BinX,
            image.MetaData.Camera.BinY,
            image.MetaData.Camera.Gain,
            requestedExposureMilliseconds,
            configuration.G3.Binning,
            configuration.G3.GainPercent,
            phdProfileEvidence);
    }

    private static bool IsRecoverableG3SearchGate(GateResult gate) => gate.Code is
        "G3_PLATE_SOLVE_FAILED" or
        "G3_PLATE_SOLVE_LADDER_EXHAUSTED_STRUCTURED_FIELD" or
        "G3_SOLVED_TARGET_OUTSIDE" or
        "G3_STAR_FIELD_SPARSE_VALID_EXPOSURE" or
        "TARGET_NOT_FOUND" or
        "TARGET_AMBIGUOUS" or
        "BRIGHT_TARGET_SATURATED_CORE_NOT_FOUND" or
        "BRIGHT_TARGET_WINGS_UNUSABLE" or
        "BRIGHT_TARGET_AMBIGUOUS";

    private static bool IsRecoverableSparseG3Field(
        G3StellarFocusMeasurement focusMeasurement,
        SlitIlluminationPairAnalysis pairAnalysis,
        C11MainFocusOwnerSnapshot before,
        C11MainFocusOwnerSnapshot after) =>
        before.PositionSteps == after.PositionSteps &&
        pairAnalysis.Gate.Disposition == GateDisposition.Passed &&
        focusMeasurement.DetectedStarCount > 0 &&
        focusMeasurement.SaturatedStarFraction <= 0.25 &&
        focusMeasurement.Gate.Code == "G3_FOCUS_STARS_INSUFFICIENT";

    private string G3AcquisitionMotionPath(string runId) => Path.Combine(
        SlitPlacementObservationsRoot(),
        SanitizeRunPathSegment(runId),
        "control",
        "g3-acquisition-motion.json");

    private async Task<G3AcquisitionMotionState> BeginG3AcquisitionMotionAsync(
        ObservationContext context,
        G3AcquisitionMotionKind kind,
        Coordinates origin,
        string pierSide,
        double maximumSingleArcseconds,
        double maximumRadiusArcseconds,
        double maximumCumulativeArcseconds,
        int maximumAttempts,
        TimeSpan maximumElapsed,
        string declaredEvidencePath,
        CancellationToken cancellationToken,
        double? continuationFamilyAdditionalCumulativeArcseconds = null,
        int? continuationFamilyAdditionalAttempts = null,
        TimeSpan? continuationFamilyAdditionalElapsed = null)
    {
        if (commissioning is null) throw new InvalidOperationException("Commissioning preset is not loaded.");
        var now = DateTimeOffset.UtcNow;
        if (durableG3AcquisitionMotion is { } existing)
        {
            var identity = ValidateG3AcquisitionMotionIdentity(context, existing);
            if (identity.Disposition != GateDisposition.Passed)
            {
                throw new InvalidOperationException($"{identity.Code}: {identity.Message}");
            }
            if (existing.Phase != G3AcquisitionMotionPhase.SettledBudgetLedger)
            {
                throw new InvalidOperationException("An outstanding durable G3 motion must return before another motion family can begin.");
            }
            if (!string.Equals(existing.PierSide, pierSide, StringComparison.Ordinal) ||
                !string.Equals(existing.CoordinateEpoch, origin.Epoch.ToString(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Pier side or coordinate epoch changed before continuing the durable G3 budget lineage.");
            }
            var inheritedOriginOffset = G3AcquisitionMotionPlanner.SignedTangentOffsetArcseconds(
                existing.OriginRaDegrees,
                existing.OriginDeclinationDegrees,
                NormalizeDegrees(origin.RADegrees),
                origin.Dec);
            var inheritedOriginRadius = G3AcquisitionMotionPlanner.AngularSeparationArcseconds(
                existing.OriginRaDegrees,
                existing.OriginDeclinationDegrees,
                NormalizeDegrees(origin.RADegrees),
                origin.Dec);
            if (!double.IsFinite(inheritedOriginRadius) || inheritedOriginRadius > existing.ArrivalToleranceArcseconds)
            {
                throw new InvalidOperationException(
                    $"The new G3 motion family is {inheritedOriginRadius:F2} arcsec from the durable lineage origin; budget cannot be reset or rebased automatically.");
            }
            var continued = G3AcquisitionMotionPlanner.ContinueSettledLedger(
                existing,
                context.Plan.ObservationRunId,
                kind,
                declaredEvidencePath,
                now,
                familyMaximumSingleCorrectionArcseconds: maximumSingleArcseconds,
                familyMaximumRadiusArcseconds: maximumRadiusArcseconds,
                familyAdditionalCumulativeMotionArcseconds: continuationFamilyAdditionalCumulativeArcseconds,
                familyAdditionalCorrectionAttempts: continuationFamilyAdditionalAttempts,
                familyAdditionalElapsedTime: continuationFamilyAdditionalElapsed) with
            {
                PriorReportedRaDegrees = NormalizeDegrees(origin.RADegrees),
                PriorReportedDeclinationDegrees = origin.Dec,
                CommandedRaDegrees = NormalizeDegrees(origin.RADegrees),
                CommandedDeclinationDegrees = origin.Dec,
                CurrentRaTangentOffsetArcseconds = inheritedOriginOffset.RaArcseconds,
                CurrentDeclinationOffsetArcseconds = inheritedOriginOffset.DecArcseconds,
            };
            await PersistG3AcquisitionMotionAsync(continued, cancellationToken).ConfigureAwait(false);
            return continued;
        }
        var state = new G3AcquisitionMotionState(
            G3AcquisitionMotionState.CurrentSchemaVersion,
            G3AcquisitionMotionState.CurrentTangentProjectionId,
            context.Plan.ObservationRunId,
            Guid.NewGuid().ToString("N"),
            configuration.ActionConfigurationSha256,
            ComputeSlitRecoveryContextSha256(context),
            commissioning.Sha256,
            kind,
            G3AcquisitionMotionPhase.SettledBudgetLedger,
            pierSide,
            origin.Epoch.ToString(),
            NormalizeDegrees(origin.RADegrees),
            origin.Dec,
            NormalizeDegrees(origin.RADegrees),
            origin.Dec,
            NormalizeDegrees(origin.RADegrees),
            origin.Dec,
            0,
            0,
            0,
            maximumSingleArcseconds,
            maximumRadiusArcseconds,
            maximumCumulativeArcseconds,
            maximumAttempts,
            MountCommandArrivalToleranceArcseconds,
            configuration.G3.MotionWorstCaseActionSeconds,
            maximumElapsed.TotalSeconds,
            0,
            0,
            now,
            now,
            now,
            declaredEvidencePath,
            "Motion envelope declared at a reported mount origin; no command is outstanding.");
        await PersistG3AcquisitionMotionAsync(state, cancellationToken).ConfigureAwait(false);
        return state;
    }

    private async Task PersistG3AcquisitionMotionAsync(
        G3AcquisitionMotionState state,
        CancellationToken cancellationToken)
    {
        await G3AcquisitionMotionStore.WriteAtomicAsync(
            G3AcquisitionMotionPath(state.ObservationRunId),
            state,
            cancellationToken).ConfigureAwait(false);
        durableG3AcquisitionMotion = state;
    }

    private static G3AcquisitionMotionState ReanchorG3AcquisitionMotionFromReportedPosition(
        G3AcquisitionMotionState state,
        Coordinates reported)
    {
        if (!string.Equals(state.CoordinateEpoch, reported.Epoch.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Durable G3 motion uses epoch '{state.CoordinateEpoch}', but the mount reports '{reported.Epoch}'.");
        }
        var (ra, dec) = G3AcquisitionMotionPlanner.SignedTangentOffsetArcseconds(
            state.OriginRaDegrees,
            state.OriginDeclinationDegrees,
            NormalizeDegrees(reported.RADegrees),
            reported.Dec);
        if (!double.IsFinite(ra) || !double.IsFinite(dec))
        {
            throw new InvalidOperationException("The mount's reported position cannot be expressed in the durable G3 tangent plane.");
        }
        return state with
        {
            PriorReportedRaDegrees = NormalizeDegrees(reported.RADegrees),
            PriorReportedDeclinationDegrees = reported.Dec,
            CurrentRaTangentOffsetArcseconds = ra,
            CurrentDeclinationOffsetArcseconds = dec,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
    }

    private async Task<G3PostSlewStabilityResult> WaitForG3PostSlewStabilityAsync(
        ObservationContext context,
        Coordinates commanded,
        Coordinates reportedImmediatelyAfterSlew,
        string expectedPierSide,
        string expectedEpoch,
        string operation,
        CancellationToken cancellationToken)
    {
        var settleSeconds = configuration.G3.MotionPostSlewSettleSeconds;
        if (!double.IsFinite(settleSeconds) || settleSeconds <= 0)
        {
            return G3PostSlewStabilityResult.Blocked(
                "G3_POST_SLEW_SETTLE_UNCOMMISSIONED",
                "A positive commissioned G3 post-slew settle time is required before a fresh image.");
        }

        var settleStartedUtc = DateTimeOffset.UtcNow;
        await Task.Delay(TimeSpan.FromSeconds(settleSeconds), cancellationToken).ConfigureAwait(false);
        await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
        var settled = telescopeMediator.GetCurrentPosition();
        var mountGate = ValidateG3SearchMountState(expectedPierSide);
        if (mountGate.Disposition != GateDisposition.Passed)
        {
            return G3PostSlewStabilityResult.Blocked(mountGate.Code, mountGate.Message);
        }
        if (!string.Equals(expectedEpoch, settled.Epoch.ToString(), StringComparison.Ordinal))
        {
            return G3PostSlewStabilityResult.Blocked(
                "G3_POST_SLEW_EPOCH_CHANGED",
                $"Mount epoch changed during the commissioned {settleSeconds:F2}s G3 settle interval.");
        }
        var horizonGate = ValidateCommandCoordinateHorizon(context, settled, $"{operation} post-slew settled position");
        if (horizonGate.Disposition != GateDisposition.Passed)
        {
            return G3PostSlewStabilityResult.Blocked(horizonGate.Code, horizonGate.Message);
        }
        var drift = AngularSeparationArcseconds(reportedImmediatelyAfterSlew, settled);
        var commandResidual = AngularSeparationArcseconds(settled, commanded);
        if (!double.IsFinite(drift) || !double.IsFinite(commandResidual) ||
            drift > MountCommandArrivalToleranceArcseconds ||
            commandResidual > MountCommandArrivalToleranceArcseconds)
        {
            return new G3PostSlewStabilityResult(
                GateResult.Unknown(
                    "G3_POST_SLEW_POSITION_UNSTABLE",
                    $"After waiting {settleSeconds:F2}s, reported drift was {drift:F2} arcsec and command residual was {commandResidual:F2} arcsec; no fresh G3 frame is authorized."),
                settled,
                settleStartedUtc,
                DateTimeOffset.UtcNow,
                drift,
                commandResidual);
        }
        return new G3PostSlewStabilityResult(
            GateResult.Pass(
                "G3_POST_SLEW_POSITION_STABLE",
                $"The mount remained within {MountCommandArrivalToleranceArcseconds:F2} arcsec through the commissioned {settleSeconds:F2}s settle interval."),
            settled,
            settleStartedUtc,
            DateTimeOffset.UtcNow,
            drift,
            commandResidual);
    }

    private async Task<G3AcquisitionMotionReturnResult> ReturnDurableG3AcquisitionToOriginAsync(
        ObservationContext context,
        G3AcquisitionMotionState state,
        CancellationToken cancellationToken)
    {
        var path = G3AcquisitionMotionPath(state.ObservationRunId);
        while (true)
        {
            Coordinates reported;
            try
            {
                reported = telescopeMediator.GetCurrentPosition();
                state = ReanchorG3AcquisitionMotionFromReportedPosition(state, reported);
            }
            catch (Exception ex)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, ex.Message);
            }
            var mountGate = ValidateG3SearchMountState(state.PierSide);
            if (mountGate.Disposition != GateDisposition.Passed)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, $"{mountGate.Code}: {mountGate.Message}");
            }
            var step = G3AcquisitionMotionPlanner.PlanNextReturnStep(
                state,
                NormalizeDegrees(reported.RADegrees),
                reported.Dec,
                MountCommandArrivalToleranceArcseconds,
                DateTimeOffset.UtcNow);
            if (step.Gate.Disposition != GateDisposition.Passed)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, $"{step.Gate.Code}: {step.Gate.Message}");
            }
            if (step.AlreadyAtOrigin)
            {
                state = state with
                {
                    Phase = G3AcquisitionMotionPhase.SettledBudgetLedger,
                    PriorReportedRaDegrees = state.OriginRaDegrees,
                    PriorReportedDeclinationDegrees = state.OriginDeclinationDegrees,
                    CommandedRaDegrees = state.OriginRaDegrees,
                    CommandedDeclinationDegrees = state.OriginDeclinationDegrees,
                    CurrentRaTangentOffsetArcseconds = 0,
                    CurrentDeclinationOffsetArcseconds = 0,
                    CommandMagnitudeArcseconds = 0,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    LastReason = "The durable G3 acquisition return reached its reported origin.",
                };
                await PersistG3AcquisitionMotionAsync(state, CancellationToken.None).ConfigureAwait(false);
                return new G3AcquisitionMotionReturnResult(true, state, path, state.LastReason!);
            }

            try
            {
                await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (ResumeStageRestartException) { throw; }
            catch (PhysicalActionGateException ex)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, $"{ex.Gate.Code}: {ex.Gate.Message}");
            }
            var commanded = new Coordinates(
                step.CommandedRaDegrees,
                step.CommandedDeclinationDegrees,
                reported.Epoch,
                Coordinates.RAType.Degrees);
            var reportedBeforeIntent = telescopeMediator.GetCurrentPosition();
            var intentMountGate = ValidateG3SearchMountState(state.PierSide);
            if (intentMountGate.Disposition != GateDisposition.Passed)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, $"{intentMountGate.Code}: {intentMountGate.Message}");
            }
            if (!string.Equals(state.CoordinateEpoch, reportedBeforeIntent.Epoch.ToString(), StringComparison.Ordinal) ||
                AngularSeparationArcseconds(reported, reportedBeforeIntent) > state.ArrivalToleranceArcseconds)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, "The fresh reported mount coordinate changed beyond the reserved arrival tolerance before the durable return intent; the intent was not written.");
            }
            var reportedIntentHorizon = ValidateCommandCoordinateHorizon(
                context,
                reportedBeforeIntent,
                "durable G3 acquisition fresh reported position before return intent");
            if (reportedIntentHorizon.Disposition != GateDisposition.Passed)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, $"{reportedIntentHorizon.Code}: {reportedIntentHorizon.Message}");
            }
            var horizonGate = ValidateCommandCoordinateHorizon(context, commanded, "durable G3 acquisition return move");
            if (horizonGate.Disposition != GateDisposition.Passed)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, $"{horizonGate.Code}: {horizonGate.Message}");
            }
            var sphericalIntentGate = G3AcquisitionMotionPlanner.ValidateSphericalCommand(
                state,
                NormalizeDegrees(reportedBeforeIntent.RADegrees),
                reportedBeforeIntent.Dec,
                NormalizeDegrees(commanded.RADegrees),
                commanded.Dec,
                step.CommandMagnitudeArcseconds);
            if (sphericalIntentGate.Gate.Disposition != GateDisposition.Passed)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, $"{sphericalIntentGate.Gate.Code}: {sphericalIntentGate.Gate.Message}");
            }

            // Conservatively consume the action and declared distance before
            // persisting the return intent and before the asynchronous command.
            state = state with
            {
                Phase = G3AcquisitionMotionPhase.ReturnIntent,
                PriorReportedRaDegrees = NormalizeDegrees(reportedBeforeIntent.RADegrees),
                PriorReportedDeclinationDegrees = reportedBeforeIntent.Dec,
                CommandedRaDegrees = NormalizeDegrees(commanded.RADegrees),
                CommandedDeclinationDegrees = commanded.Dec,
                CommandMagnitudeArcseconds = step.CommandMagnitudeArcseconds,
                CumulativeMotionArcseconds = state.CumulativeMotionArcseconds +
                    step.CommandMagnitudeArcseconds + state.ArrivalToleranceArcseconds,
                CorrectionAttempts = state.CorrectionAttempts + 1,
                UpdatedUtc = DateTimeOffset.UtcNow,
                LastReason = $"Durable return intent precharged for {step.CommandMagnitudeArcseconds:F2} arcsec.",
            };
            await PersistG3AcquisitionMotionAsync(state, CancellationToken.None).ConfigureAwait(false);

            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            var immediatelyBefore = telescopeMediator.GetCurrentPosition();
            var precommandMountGate = ValidateG3SearchMountState(state.PierSide);
            if (precommandMountGate.Disposition != GateDisposition.Passed)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, $"{precommandMountGate.Code}: {precommandMountGate.Message}");
            }
            if (!string.Equals(state.CoordinateEpoch, immediatelyBefore.Epoch.ToString(), StringComparison.Ordinal))
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, "The mount coordinate epoch changed after the durable return intent; the command was withheld.");
            }
            if (AngularSeparationArcseconds(reportedBeforeIntent, immediatelyBefore) > MountCommandArrivalToleranceArcseconds)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, "The reported mount position changed after the durable return intent; the stale absolute command was withheld.");
            }
            var freshReportedHorizon = ValidateCommandCoordinateHorizon(
                context,
                immediatelyBefore,
                "durable G3 acquisition fresh reported position before return command");
            if (freshReportedHorizon.Disposition != GateDisposition.Passed)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, $"{freshReportedHorizon.Code}: {freshReportedHorizon.Message}");
            }
            var freshSphericalGate = G3AcquisitionMotionPlanner.ValidateSphericalCommand(
                state,
                NormalizeDegrees(immediatelyBefore.RADegrees),
                immediatelyBefore.Dec,
                NormalizeDegrees(commanded.RADegrees),
                commanded.Dec,
                step.CommandMagnitudeArcseconds);
            if (freshSphericalGate.Gate.Disposition != GateDisposition.Passed)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, $"{freshSphericalGate.Gate.Code}: {freshSphericalGate.Gate.Message}");
            }
            var finalHorizonGate = ValidateCommandCoordinateHorizon(context, commanded, "durable G3 acquisition final return check");
            if (finalHorizonGate.Disposition != GateDisposition.Passed)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, $"{finalHorizonGate.Code}: {finalHorizonGate.Message}");
            }
            if (!await telescopeMediator.SlewToCoordinatesAsync(commanded, cancellationToken).ConfigureAwait(false))
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, "N.I.N.A. rejected the durable G3 return command; its precharged intent remains inspectable.");
            }
            await telescopeMediator.WaitForSlew(cancellationToken).ConfigureAwait(false);
            var after = telescopeMediator.GetCurrentPosition();
            if (!string.Equals(state.CoordinateEpoch, after.Epoch.ToString(), StringComparison.Ordinal))
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, "The mount coordinate epoch changed after the return command; no arrival was accepted.");
            }
            var afterMountGate = ValidateG3SearchMountState(state.PierSide);
            if (afterMountGate.Disposition != GateDisposition.Passed)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, $"{afterMountGate.Code}: {afterMountGate.Message}");
            }
            var reportedHorizonGate = ValidateCommandCoordinateHorizon(context, after, "durable G3 acquisition reported return arrival");
            if (reportedHorizonGate.Disposition != GateDisposition.Passed)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, $"{reportedHorizonGate.Code}: {reportedHorizonGate.Message}");
            }
            var commandResidual = AngularSeparationArcseconds(after, commanded);
            state = ReanchorG3AcquisitionMotionFromReportedPosition(state, after) with
            {
                LastReason = $"Return command completed with {commandResidual:F2} arcsec reported residual.",
            };
            await PersistG3AcquisitionMotionAsync(state, CancellationToken.None).ConfigureAwait(false);
            if (!double.IsFinite(commandResidual) || commandResidual > MountCommandArrivalToleranceArcseconds)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, "The mount did not attain the durable G3 return command; reported coordinates were retained and further automatic motion stopped.");
            }
        }
    }

    private async Task<(bool RunIsTerminal, GateResult? Error)> ValidateG3AcquisitionMotionManifestAsync(
        G3AcquisitionMotionFileResult item,
        CancellationToken cancellationToken)
    {
        var state = item.State!;
        var controlDirectory = Path.GetDirectoryName(item.Path);
        var runDirectory = controlDirectory is null ? null : Path.GetDirectoryName(controlDirectory);
        if (runDirectory is null)
        {
            return (false, GateResult.Unknown(
                "G3_MOTION_MANIFEST_PATH_INVALID",
                $"Cannot derive an immutable run manifest from durable G3 motion '{item.Path}'."));
        }
        var manifestPath = Path.Combine(runDirectory, "manifest.json");
        ObservationRunManifest? manifest;
        try
        {
            manifest = await new ObservationRunJournalStore(manifestPath)
                .ReadAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return (false, GateResult.Unknown(
                "G3_MOTION_MANIFEST_UNREADABLE",
                $"Run manifest '{manifestPath}' cannot attest the G3 motion lineage: {ex.Message}"));
        }
        if (manifest is null)
        {
            return (false, GateResult.Unknown(
                "G3_MOTION_MANIFEST_MISSING",
                $"Run manifest '{manifestPath}' is missing; automatic G3 motion adoption is prohibited."));
        }
        if (!string.Equals(manifest.ObservationRunId, state.ObservationRunId, StringComparison.Ordinal))
        {
            return (false, GateResult.Unknown(
                "G3_MOTION_MANIFEST_RUN_MISMATCH",
                $"Run manifest '{manifestPath}' does not belong to durable G3 run '{state.ObservationRunId}'."));
        }
        if (manifest.LockedMetadata.Labels is null ||
            !manifest.LockedMetadata.Labels.TryGetValue("telescopeId", out var telescopeId) ||
            string.IsNullOrWhiteSpace(telescopeId) ||
            !SameHash(
                state.RecoveryContextSha256,
                ComputeSlitRecoveryContextSha256(manifest.Plan, telescopeId)))
        {
            return (false, GateResult.Unknown(
                "G3_MOTION_MANIFEST_CONTEXT_MISMATCH",
                $"Run manifest '{manifestPath}' does not reproduce the target/site/horizon/Night-Setup/telescope recovery context."));
        }
        if (manifest.LockedMetadata.AdditionalHashes is null ||
            !manifest.LockedMetadata.AdditionalHashes.TryGetValue("actionConfigurationSha256", out var actionHash) ||
            !SameHash(state.ActionConfigurationSha256, actionHash) ||
            manifest.LockedMetadata.CommissioningPresetSha256 is null ||
            !SameHash(state.CommissioningPresetSha256, manifest.LockedMetadata.CommissioningPresetSha256))
        {
            return (false, GateResult.Unknown(
                "G3_MOTION_MANIFEST_BINDING_MISMATCH",
                $"Run manifest '{manifestPath}' does not reproduce the durable action-configuration and commissioning hashes."));
        }
        return (manifest.TerminalState is not null, null);
    }

    private GateResult ValidateG3AcquisitionMotionIdentity(
        ObservationContext context,
        G3AcquisitionMotionState state)
    {
        if (!SameHash(state.ActionConfigurationSha256, configuration.ActionConfigurationSha256) ||
            !SameHash(state.CommissioningPresetSha256, configuration.Commissioning.PresetSha256) ||
            !SameHash(state.RecoveryContextSha256, ComputeSlitRecoveryContextSha256(context)))
        {
            return GateResult.Unknown(
                "G3_MOTION_RECOVERY_CONTEXT_CHANGED",
                "Durable G3 motion does not match the current action configuration, commissioning preset, target, site, horizon, Night Setup or telescope identity. Automatic motion is prohibited.");
        }
        return GateResult.Pass(
            "G3_MOTION_RECOVERY_IDENTITY_VALID",
            "Durable G3 motion exactly matches the immutable current recovery context.");
    }

    private async Task<StageResult?> RecoverDurableG3AcquisitionBeforeStageAsync(
        ObservationStage stage,
        ObservationContext context,
        CancellationToken cancellationToken)
    {
        await g3AcquisitionRecoveryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var discovered = await G3AcquisitionMotionStore.DiscoverAsync(
                SlitPlacementObservationsRoot(),
                cancellationToken).ConfigureAwait(false);
            var unreadable = discovered.Where(item => item.Error is not null || item.State is null).ToArray();
            if (unreadable.Length > 0)
            {
                return new StageResult(GateResult.Unknown(
                    "G3_MOTION_EVIDENCE_UNREADABLE",
                    $"Durable G3 acquisition evidence is unreadable at {string.Join(", ", unreadable.Select(item => item.Path))}: {string.Join("; ", unreadable.Select(item => item.Error ?? "missing state"))}. Automatic mount motion is prohibited."));
            }
            foreach (var item in discovered)
            {
                var expectedPath = G3AcquisitionMotionPath(item.State!.ObservationRunId);
                if (!string.Equals(Path.GetFullPath(item.Path), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
                {
                    return new StageResult(GateResult.Unknown(
                        "G3_MOTION_PATH_IDENTITY_MISMATCH",
                        $"Durable G3 motion '{item.Path}' does not match its run-bound path '{expectedPath}'."));
                }
            }
            if (durableG3AcquisitionMotion is not null && !discovered.Any(item =>
                string.Equals(item.State!.ObservationRunId, durableG3AcquisitionMotion.ObservationRunId, StringComparison.Ordinal) &&
                string.Equals(item.State.BudgetLineageId, durableG3AcquisitionMotion.BudgetLineageId, StringComparison.Ordinal)))
            {
                return new StageResult(GateResult.Unknown(
                    "G3_MOTION_DURABILITY_LOST",
                    "In-memory G3 motion authority has no matching canonical durable file. Automatic mount motion is prohibited."));
            }

            var active = new List<G3AcquisitionMotionFileResult>();
            var terminalSettled = new List<G3AcquisitionMotionFileResult>();
            foreach (var item in discovered)
            {
                var manifest = await ValidateG3AcquisitionMotionManifestAsync(item, cancellationToken).ConfigureAwait(false);
                if (manifest.Error is not null) return new StageResult(manifest.Error, item.Path);
                if (manifest.RunIsTerminal)
                {
                    if (item.State!.Phase != G3AcquisitionMotionPhase.SettledBudgetLedger)
                    {
                        var terminalIdentity = ValidateG3AcquisitionMotionIdentity(context, item.State);
                        if (terminalIdentity.Disposition != GateDisposition.Passed)
                        {
                            return new StageResult(terminalIdentity, item.Path);
                        }
                        // Recovery is entered only by a new explicit stage
                        // start. Cancellation/terminalization itself never
                        // issues a command, but an exactly bound outstanding
                        // intent must remain returnable on that later start.
                        active.Add(item);
                        continue;
                    }
                    terminalSettled.Add(item);
                    continue;
                }
                var itemIdentity = ValidateG3AcquisitionMotionIdentity(context, item.State!);
                if (itemIdentity.Disposition != GateDisposition.Passed)
                {
                    return new StageResult(itemIdentity, item.Path);
                }
                active.Add(item);
            }

            foreach (var terminal in terminalSettled)
            {
                var terminalState = terminal.State!;
                var sameBoundLineage = active.Where(item =>
                    string.Equals(item.State!.BudgetLineageId, terminalState.BudgetLineageId, StringComparison.Ordinal) &&
                    SameHash(item.State.ActionConfigurationSha256, terminalState.ActionConfigurationSha256) &&
                    SameHash(item.State.RecoveryContextSha256, terminalState.RecoveryContextSha256) &&
                    SameHash(item.State.CommissioningPresetSha256, terminalState.CommissioningPresetSha256)).ToArray();
                var sameClosedLineage = sameBoundLineage.Where(item =>
                    string.Equals(item.State!.TangentProjectionId, terminalState.TangentProjectionId, StringComparison.Ordinal) &&
                    string.Equals(item.State.PierSide, terminalState.PierSide, StringComparison.Ordinal) &&
                    string.Equals(item.State.CoordinateEpoch, terminalState.CoordinateEpoch, StringComparison.Ordinal) &&
                    Math.Abs(item.State.OriginRaDegrees - terminalState.OriginRaDegrees) <= 1e-12 &&
                    Math.Abs(item.State.OriginDeclinationDegrees - terminalState.OriginDeclinationDegrees) <= 1e-12 &&
                    (terminalState.UpdatedUtc >= item.State.UpdatedUtc
                        ? terminalState.MaximumSingleCorrectionArcseconds <= item.State.MaximumSingleCorrectionArcseconds + 1e-12 &&
                          terminalState.MaximumRadiusArcseconds <= item.State.MaximumRadiusArcseconds + 1e-12 &&
                          terminalState.MaximumCumulativeMotionArcseconds <= item.State.MaximumCumulativeMotionArcseconds + 1e-12 &&
                          terminalState.MaximumCorrectionAttempts <= item.State.MaximumCorrectionAttempts &&
                          terminalState.MaximumElapsedSeconds <= item.State.MaximumElapsedSeconds + 1e-12
                        : item.State.MaximumSingleCorrectionArcseconds <= terminalState.MaximumSingleCorrectionArcseconds + 1e-12 &&
                          item.State.MaximumRadiusArcseconds <= terminalState.MaximumRadiusArcseconds + 1e-12 &&
                          item.State.MaximumCumulativeMotionArcseconds <= terminalState.MaximumCumulativeMotionArcseconds + 1e-12 &&
                          item.State.MaximumCorrectionAttempts <= terminalState.MaximumCorrectionAttempts &&
                          item.State.MaximumElapsedSeconds <= terminalState.MaximumElapsedSeconds + 1e-12) &&
                    Math.Abs(item.State.ArrivalToleranceArcseconds - terminalState.ArrivalToleranceArcseconds) <= 1e-12 &&
                    Math.Abs(item.State.WorstCaseActionSeconds - terminalState.WorstCaseActionSeconds) <= 1e-12).ToArray();
                if (sameClosedLineage.Length != sameBoundLineage.Length)
                {
                    return new StageResult(GateResult.Unknown(
                        "G3_MOTION_TERMINAL_HANDOFF_INCONSISTENT",
                        $"Terminal G3 lineage {terminalState.BudgetLineageId} disagrees with an older bound copy on projection, origin, pier/epoch or limits. Automatic motion is prohibited."), terminal.Path);
                }
                var terminalClosesCopies = sameClosedLineage.All(item =>
                    item.State!.UpdatedUtc <= terminalState.UpdatedUtc &&
                    item.State.CumulativeMotionArcseconds <= terminalState.CumulativeMotionArcseconds + 1e-9 &&
                    item.State.CorrectionAttempts <= terminalState.CorrectionAttempts);
                if (terminalClosesCopies)
                {
                    // A later settled copy with monotonic counters is the
                    // trustworthy handoff terminus. Older outstanding copies
                    // cannot be resurrected after it.
                    active.RemoveAll(item => sameClosedLineage.Contains(item));
                }
            }

            var lineages = active
                .GroupBy(item => item.State!.BudgetLineageId, StringComparer.Ordinal)
                .ToArray();
            if (lineages.Length > 1)
            {
                return new StageResult(GateResult.Unknown(
                    "G3_MOTION_MULTIPLE_ACTIVE_LINEAGES",
                    $"{lineages.Length} non-terminal durable G3 motion lineages are active. Their budgets cannot be merged automatically."));
            }
            if (active.Count == 0)
            {
                durableG3AcquisitionMotion = null;
                return null;
            }
            var outstanding = active
                .Where(item => item.State!.Phase != G3AcquisitionMotionPhase.SettledBudgetLedger)
                .ToArray();
            if (outstanding.Length > 1)
            {
                return new StageResult(GateResult.Unknown(
                    "G3_MOTION_MULTIPLE_OUTSTANDING",
                    $"{outstanding.Length} G3 motion intents remain outstanding in one lineage; automatic return is ambiguous."));
            }
            var selected = outstanding.SingleOrDefault() ?? active
                .OrderByDescending(item => string.Equals(
                    item.State!.ObservationRunId,
                    context.Plan.ObservationRunId,
                    StringComparison.Ordinal))
                .ThenByDescending(item => item.State!.UpdatedUtc)
                .First();
            var lineageCopies = active
                .Where(item => string.Equals(
                    item.State!.BudgetLineageId,
                    selected.State!.BudgetLineageId,
                    StringComparison.Ordinal))
                .Select(item => item.State!)
                .ToArray();
            var lineageAnchor = lineageCopies[0];
            if (lineageCopies.Any(copy =>
                !string.Equals(copy.TangentProjectionId, lineageAnchor.TangentProjectionId, StringComparison.Ordinal) ||
                !SameHash(copy.ActionConfigurationSha256, lineageAnchor.ActionConfigurationSha256) ||
                !SameHash(copy.RecoveryContextSha256, lineageAnchor.RecoveryContextSha256) ||
                !SameHash(copy.CommissioningPresetSha256, lineageAnchor.CommissioningPresetSha256) ||
                !string.Equals(copy.PierSide, lineageAnchor.PierSide, StringComparison.Ordinal) ||
                !string.Equals(copy.CoordinateEpoch, lineageAnchor.CoordinateEpoch, StringComparison.Ordinal) ||
                Math.Abs(copy.OriginRaDegrees - lineageAnchor.OriginRaDegrees) > 1e-12 ||
                Math.Abs(copy.OriginDeclinationDegrees - lineageAnchor.OriginDeclinationDegrees) > 1e-12 ||
                Math.Abs(copy.ArrivalToleranceArcseconds - lineageAnchor.ArrivalToleranceArcseconds) > 1e-12 ||
                Math.Abs(copy.WorstCaseActionSeconds - lineageAnchor.WorstCaseActionSeconds) > 1e-12))
            {
                return new StageResult(GateResult.Unknown(
                    "G3_MOTION_LINEAGE_INCONSISTENT",
                    $"Durable G3 lineage {lineageAnchor.BudgetLineageId} contains copies with inconsistent immutable context, origin or global limits. Automatic adoption is prohibited."));
            }
            var state = selected.State! with
            {
                // Family handoffs may only consume or tighten authority.
                // Aggregate every limit by minimum, every consumed counter by
                // maximum, and retain the earliest lineage clock.
                MaximumSingleCorrectionArcseconds = lineageCopies.Min(copy => copy.MaximumSingleCorrectionArcseconds),
                MaximumRadiusArcseconds = lineageCopies.Min(copy => copy.MaximumRadiusArcseconds),
                MaximumCumulativeMotionArcseconds = lineageCopies.Min(copy => copy.MaximumCumulativeMotionArcseconds),
                MaximumCorrectionAttempts = lineageCopies.Min(copy => copy.MaximumCorrectionAttempts),
                MaximumElapsedSeconds = lineageCopies.Min(copy => copy.MaximumElapsedSeconds),
                CumulativeMotionArcseconds = lineageCopies.Max(copy => copy.CumulativeMotionArcseconds),
                CorrectionAttempts = lineageCopies.Max(copy => copy.CorrectionAttempts),
                StartedUtc = lineageCopies.Min(copy => copy.StartedUtc),
                UpdatedUtc = DateTimeOffset.UtcNow,
                LastReason = $"Monotonic G3 lineage aggregate adopted from {lineageCopies.Length} trustworthy durable copy/copies without counter or clock rollback.",
            };
            var aggregateIssues = state.Validate().ToList();
            if ((DateTimeOffset.UtcNow - state.StartedUtc).TotalSeconds > state.MaximumElapsedSeconds + 1e-9)
            {
                aggregateIssues.Add("The strictest durable lineage elapsed-time limit is already consumed.");
            }
            if (aggregateIssues.Count > 0)
            {
                return new StageResult(GateResult.Unknown(
                    "G3_MOTION_LINEAGE_AGGREGATE_INVALID",
                    string.Join(" ", aggregateIssues)), selected.Path);
            }
            await G3AcquisitionMotionStore.WriteAtomicAsync(selected.Path, state, cancellationToken).ConfigureAwait(false);
            var identity = ValidateG3AcquisitionMotionIdentity(context, state);
            if (identity.Disposition != GateDisposition.Passed) return new StageResult(identity, selected.Path);

            cumulativeCorrectionDegrees = Math.Max(
                cumulativeCorrectionDegrees,
                state.CumulativeMotionArcseconds / 3600d);
            correctionAttempts = Math.Max(correctionAttempts, state.CorrectionAttempts);
            fineAcquisitionStartedUtc = fineAcquisitionStartedUtc is null || state.StartedUtc < fineAcquisitionStartedUtc
                ? state.StartedUtc
                : fineAcquisitionStartedUtc;
            durableG3AcquisitionMotion = state;
            if (state.Phase == G3AcquisitionMotionPhase.SettledBudgetLedger)
            {
                if (!string.Equals(state.ObservationRunId, context.Plan.ObservationRunId, StringComparison.Ordinal))
                {
                    state = state with
                    {
                        ObservationRunId = context.Plan.ObservationRunId,
                        UpdatedUtc = DateTimeOffset.UtcNow,
                        LastReason = $"Settled G3 motion lineage adopted from run {state.ObservationRunId} without resetting its counters.",
                    };
                    await PersistG3AcquisitionMotionAsync(state, cancellationToken).ConfigureAwait(false);
                }
                return null;
            }

            var returned = await ReturnDurableG3AcquisitionToOriginAsync(
                context,
                state,
                cancellationToken).ConfigureAwait(false);
            if (!returned.ReturnedToOrigin)
            {
                return new StageResult(GateResult.Unknown(
                    "G3_MOTION_CRASH_RETURN_BLOCKED",
                    $"Durable G3 motion recovery could not return to its reported origin: {returned.Message}"), returned.Path);
            }
            state = returned.State;
            if (!string.Equals(state.ObservationRunId, context.Plan.ObservationRunId, StringComparison.Ordinal))
            {
                state = state with
                {
                    ObservationRunId = context.Plan.ObservationRunId,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    LastReason = $"Recovered G3 motion lineage handed off from run {state.ObservationRunId} after returning to its origin.",
                };
                await PersistG3AcquisitionMotionAsync(state, cancellationToken).ConfigureAwait(false);
            }
            lastG3Field = null;
            pendingG3SearchReturn = null;
            if (stage > ObservationStage.AcquireG3SlitField)
            {
                Interlocked.Exchange(ref resumeRecoveryRequired, 1);
            }
            await PublishRunJsonEvidenceAsync(
                "g3-acquisition-crash-recovery",
                "Durable G3 acquisition motion returned before stage execution",
                new
                {
                    stage = stage.ToString(),
                    state.BudgetLineageId,
                    state.Kind,
                    state.CumulativeMotionArcseconds,
                    state.CorrectionAttempts,
                    returned.Message,
                },
                sourcePath: null,
                cancellationToken).ConfigureAwait(false);
            return null;
        }
        finally
        {
            g3AcquisitionRecoveryLock.Release();
        }
    }

    private async Task<StageResult> RunG3WcsCenteringAsync(
        ObservationContext context,
        G3FieldState solvedOutsideField,
        string transferEvidencePath,
        CancellationToken cancellationToken)
    {
        var limits = configuration.G3.WcsCentering;
        var limitIssues = limits.Validate();
        if (limitIssues.Count > 0)
        {
            return Attention(
                ObservationStage.AcquireG3SlitField,
                "G3_WCS_CENTERING_LIMITS_INVALID",
                string.Join(" ", limitIssues));
        }
        if (solvedOutsideField.Solve?.Result.Success != true ||
            solvedOutsideField.Solve.Result.Coordinates is null)
        {
            return Attention(
                ObservationStage.AcquireG3SlitField,
                "G3_WCS_CENTERING_SOLVE_REQUIRED",
                "A fresh successful G3 WCS is required before WCS-derived centering.");
        }

        var sourceBindingGate = await ValidateG3FieldMountBindingForMotionAsync(
            context,
            solvedOutsideField,
            cancellationToken).ConfigureAwait(false);
        if (sourceBindingGate.Disposition != GateDisposition.Passed)
        {
            return new StageResult(sourceBindingGate, solvedOutsideField.FramePath);
        }

        var origin = telescopeMediator.GetCurrentPosition();
        var pierSide = telescopeMediator.GetInfo().SideOfPier.ToString();
        var mountGate = ValidateG3SearchMountState(pierSide);
        if (mountGate.Disposition != GateDisposition.Passed) return new StageResult(mountGate, solvedOutsideField.FramePath);
        var declaredPath = await PublishRunJsonEvidenceAsync(
            "g3-wcs-centering-declared",
            "G3 WCS target-outside-field recentering envelope",
            new
            {
                solveEvidencePath = solvedOutsideField.Solve.EvidencePath,
                solvedOutsideField.Solve.ResidualArcseconds,
                exposurePreset = configuration.G3.PlateSolveExposurePreset,
                limits,
                postSlewSettleSeconds = configuration.G3.MotionPostSlewSettleSeconds,
                worstCaseActionSeconds = configuration.G3.MotionWorstCaseActionSeconds,
                origin = new
                {
                    raDegrees = origin.RADegrees,
                    decDegrees = origin.Dec,
                    epoch = origin.Epoch.ToString(),
                    pierSide,
                },
                authority = "fresh G3 WCS plus catalog target; no QHY-to-G3 optical offset, ghost or learned image interpretation",
                returnPolicy = "every outbound and return command is precharged and atomically persisted before N.I.N.A. receives it",
            },
            solvedOutsideField.FramePath,
            cancellationToken).ConfigureAwait(false);
        var state = await BeginG3AcquisitionMotionAsync(
            context,
            G3AcquisitionMotionKind.WcsCentering,
            origin,
            pierSide,
            limits.MaximumSingleCorrectionArcseconds,
            limits.MaximumRadiusArcseconds,
            limits.MaximumCumulativeMotionArcseconds,
            limits.MaximumCorrectionAttempts,
            limits.MaximumElapsedTime,
            declaredPath,
            cancellationToken,
            continuationFamilyAdditionalCumulativeArcseconds: limits.MaximumCumulativeMotionArcseconds,
            continuationFamilyAdditionalAttempts: limits.MaximumCorrectionAttempts,
            continuationFamilyAdditionalElapsed: limits.MaximumElapsedTime).ConfigureAwait(false);
        var currentField = solvedOutsideField;
        var attempts = 0;
        var priorResidual = solvedOutsideField.Solve.ResidualArcseconds;
        var stopReason = "The WCS-centering envelope was exhausted before the target entered the usable G3 field.";

        while (currentField.Gate.Code == "G3_SOLVED_TARGET_OUTSIDE" &&
               currentField.Solve?.Result.Success == true &&
               currentField.Solve.Result.Coordinates is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reported = telescopeMediator.GetCurrentPosition();
            try { state = ReanchorG3AcquisitionMotionFromReportedPosition(state, reported); }
            catch (Exception ex) { stopReason = ex.Message; break; }
            mountGate = ValidateG3SearchMountState(state.PierSide);
            if (mountGate.Disposition != GateDisposition.Passed)
            {
                stopReason = $"{mountGate.Code}: {mountGate.Message}";
                break;
            }
            var targetCoordinates = TargetCoordinates(context.Plan);
            var targetCorrection = G3AcquisitionMotionPlanner.SignedTangentOffsetArcseconds(
                NormalizeDegrees(currentField.Solve.Result.Coordinates.RADegrees),
                currentField.Solve.Result.Coordinates.Dec,
                NormalizeDegrees(targetCoordinates.RADegrees),
                targetCoordinates.Dec);
            var fullMagnitude = Math.Sqrt(
                targetCorrection.RaArcseconds * targetCorrection.RaArcseconds +
                targetCorrection.DecArcseconds * targetCorrection.DecArcseconds);
            if (!double.IsFinite(fullMagnitude) || fullMagnitude <= 0)
            {
                stopReason = "The fresh solved-center-to-target correction is invalid.";
                break;
            }
            var maximumCommand = state.MaximumSingleCorrectionArcseconds - state.ArrivalToleranceArcseconds;
            if (maximumCommand <= 0)
            {
                stopReason = "The WCS single-motion limit does not exceed the required arrival tolerance.";
                break;
            }
            var scale = Math.Min(1, maximumCommand / fullMagnitude);
            var commandedCoordinate = G3AcquisitionMotionPlanner.ApplyTangentOffsetArcseconds(
                NormalizeDegrees(reported.RADegrees),
                reported.Dec,
                targetCorrection.RaArcseconds * scale,
                targetCorrection.DecArcseconds * scale);
            if (!double.IsFinite(commandedCoordinate.RaDegrees) || !double.IsFinite(commandedCoordinate.DecDegrees))
            {
                stopReason = "The spherical G3 WCS-centering command coordinate is invalid.";
                break;
            }
            var commanded = new Coordinates(
                commandedCoordinate.RaDegrees,
                commandedCoordinate.DecDegrees,
                reported.Epoch,
                Coordinates.RAType.Degrees);
            var nextOffset = G3AcquisitionMotionPlanner.SignedTangentOffsetArcseconds(
                state.OriginRaDegrees,
                state.OriginDeclinationDegrees,
                NormalizeDegrees(commanded.RADegrees),
                commanded.Dec);
            var reserve = G3AcquisitionMotionPlanner.ValidateOutboundAndReturnReserve(
                state,
                nextOffset.RaArcseconds,
                nextOffset.DecArcseconds,
                DateTimeOffset.UtcNow);
            if (reserve.Gate.Disposition != GateDisposition.Passed)
            {
                stopReason = $"{reserve.Gate.Code}: {reserve.Gate.Message}";
                break;
            }
            var horizonGate = ValidateCommandCoordinateHorizon(context, commanded, "G3 WCS-centering outbound move");
            if (horizonGate.Disposition != GateDisposition.Passed)
            {
                stopReason = $"{horizonGate.Code}: {horizonGate.Message}";
                break;
            }
            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            mountGate = ValidateG3SearchMountState(state.PierSide);
            if (mountGate.Disposition != GateDisposition.Passed)
            {
                stopReason = $"{mountGate.Code}: {mountGate.Message}";
                break;
            }
            sourceBindingGate = await ValidateG3FieldMountBindingForMotionAsync(
                context,
                currentField,
                cancellationToken).ConfigureAwait(false);
            if (sourceBindingGate.Disposition != GateDisposition.Passed)
            {
                stopReason = $"{sourceBindingGate.Code}: {sourceBindingGate.Message}";
                break;
            }
            var reportedBeforeIntent = telescopeMediator.GetCurrentPosition();
            if (!string.Equals(state.CoordinateEpoch, reportedBeforeIntent.Epoch.ToString(), StringComparison.Ordinal) ||
                AngularSeparationArcseconds(reported, reportedBeforeIntent) > state.ArrivalToleranceArcseconds)
            {
                stopReason = "The fresh reported mount coordinate changed beyond the reserved arrival tolerance before the WCS outbound intent; the intent was not written.";
                break;
            }
            var reportedIntentHorizon = ValidateCommandCoordinateHorizon(
                context,
                reportedBeforeIntent,
                "G3 WCS-centering fresh reported position before outbound intent");
            if (reportedIntentHorizon.Disposition != GateDisposition.Passed)
            {
                stopReason = $"{reportedIntentHorizon.Code}: {reportedIntentHorizon.Message}";
                break;
            }
            var sphericalIntentGate = G3AcquisitionMotionPlanner.ValidateSphericalCommand(
                state,
                NormalizeDegrees(reportedBeforeIntent.RADegrees),
                reportedBeforeIntent.Dec,
                NormalizeDegrees(commanded.RADegrees),
                commanded.Dec,
                reserve.MoveFromCurrentArcseconds);
            if (sphericalIntentGate.Gate.Disposition != GateDisposition.Passed)
            {
                stopReason = $"{sphericalIntentGate.Gate.Code}: {sphericalIntentGate.Gate.Message}";
                break;
            }

            // Precharge the commanded distance plus the allowed arrival error,
            // then make the canonical durable write before any asynchronous
            // mount call. A crash cannot make this action disappear.
            state = state with
            {
                Phase = G3AcquisitionMotionPhase.OutboundIntent,
                PriorReportedRaDegrees = NormalizeDegrees(reportedBeforeIntent.RADegrees),
                PriorReportedDeclinationDegrees = reportedBeforeIntent.Dec,
                CommandedRaDegrees = NormalizeDegrees(commanded.RADegrees),
                CommandedDeclinationDegrees = commanded.Dec,
                CurrentRaTangentOffsetArcseconds = nextOffset.RaArcseconds,
                CurrentDeclinationOffsetArcseconds = nextOffset.DecArcseconds,
                CommandMagnitudeArcseconds = reserve.MoveFromCurrentArcseconds,
                CumulativeMotionArcseconds = state.CumulativeMotionArcseconds +
                    reserve.MoveFromCurrentArcseconds + state.ArrivalToleranceArcseconds,
                CorrectionAttempts = state.CorrectionAttempts + 1,
                UpdatedUtc = DateTimeOffset.UtcNow,
                LastReason = $"WCS outbound intent {attempts + 1} precharged before N.I.N.A. slew.",
            };
            await PersistG3AcquisitionMotionAsync(state, CancellationToken.None).ConfigureAwait(false);

            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            var immediatelyBefore = telescopeMediator.GetCurrentPosition();
            mountGate = ValidateG3SearchMountState(state.PierSide);
            if (mountGate.Disposition != GateDisposition.Passed)
            {
                stopReason = $"{mountGate.Code}: {mountGate.Message}";
                break;
            }
            sourceBindingGate = await ValidateG3FieldMountBindingForMotionAsync(
                context,
                currentField,
                cancellationToken).ConfigureAwait(false);
            if (sourceBindingGate.Disposition != GateDisposition.Passed)
            {
                stopReason = $"{sourceBindingGate.Code}: {sourceBindingGate.Message}";
                break;
            }
            if (!string.Equals(state.CoordinateEpoch, immediatelyBefore.Epoch.ToString(), StringComparison.Ordinal))
            {
                stopReason = "Mount coordinate epoch changed after the durable WCS outbound intent; the command was withheld.";
                break;
            }
            if (AngularSeparationArcseconds(reportedBeforeIntent, immediatelyBefore) > MountCommandArrivalToleranceArcseconds)
            {
                stopReason = "Mount position changed after the durable WCS outbound intent; the stale absolute command was withheld.";
                break;
            }
            var freshReportedHorizon = ValidateCommandCoordinateHorizon(
                context,
                immediatelyBefore,
                "G3 WCS-centering fresh reported position before outbound command");
            if (freshReportedHorizon.Disposition != GateDisposition.Passed)
            {
                stopReason = $"{freshReportedHorizon.Code}: {freshReportedHorizon.Message}";
                break;
            }
            var freshSphericalGate = G3AcquisitionMotionPlanner.ValidateSphericalCommand(
                state,
                NormalizeDegrees(immediatelyBefore.RADegrees),
                immediatelyBefore.Dec,
                NormalizeDegrees(commanded.RADegrees),
                commanded.Dec,
                reserve.MoveFromCurrentArcseconds);
            if (freshSphericalGate.Gate.Disposition != GateDisposition.Passed)
            {
                stopReason = $"{freshSphericalGate.Gate.Code}: {freshSphericalGate.Gate.Message}";
                break;
            }
            horizonGate = ValidateCommandCoordinateHorizon(context, commanded, "G3 WCS-centering final outbound check");
            if (horizonGate.Disposition != GateDisposition.Passed)
            {
                stopReason = $"{horizonGate.Code}: {horizonGate.Message}";
                break;
            }
            Report($"G3 WCS 有界居中 {attempts + 1}：{reserve.MoveFromCurrentArcseconds:F1}″，随后新拍 G3 解算验证");
            if (!await telescopeMediator.SlewToCoordinatesAsync(commanded, cancellationToken).ConfigureAwait(false))
            {
                stopReason = "N.I.N.A. rejected the WCS-centering command; its durable precharged intent remains authoritative.";
                break;
            }
            await telescopeMediator.WaitForSlew(cancellationToken).ConfigureAwait(false);
            var after = telescopeMediator.GetCurrentPosition();
            mountGate = ValidateG3SearchMountState(state.PierSide);
            if (mountGate.Disposition != GateDisposition.Passed)
            {
                stopReason = $"{mountGate.Code}: {mountGate.Message}";
                break;
            }
            if (!string.Equals(state.CoordinateEpoch, after.Epoch.ToString(), StringComparison.Ordinal))
            {
                stopReason = "Mount coordinate epoch changed after WCS centering; no arrival or fresh solve was accepted.";
                break;
            }
            var reportedHorizon = ValidateCommandCoordinateHorizon(context, after, "G3 WCS-centering reported arrival");
            if (reportedHorizon.Disposition != GateDisposition.Passed)
            {
                stopReason = $"{reportedHorizon.Code}: {reportedHorizon.Message}";
                break;
            }
            var initialCommandResidual = AngularSeparationArcseconds(after, commanded);
            if (!double.IsFinite(initialCommandResidual) || initialCommandResidual > state.ArrivalToleranceArcseconds)
            {
                state = ReanchorG3AcquisitionMotionFromReportedPosition(state, after) with
                {
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    LastReason = $"WCS command stopped {initialCommandResidual:F2} arcsec from its endpoint; fresh solve was withheld.",
                };
                await PersistG3AcquisitionMotionAsync(state, CancellationToken.None).ConfigureAwait(false);
                stopReason = $"The mount stopped {initialCommandResidual:F2} arcsec from the WCS command; reported coordinates were retained and fresh solve was withheld.";
                break;
            }

            var stability = await WaitForG3PostSlewStabilityAsync(
                context,
                commanded,
                after,
                state.PierSide,
                state.CoordinateEpoch,
                "G3 WCS-centering",
                cancellationToken).ConfigureAwait(false);
            if (stability.Gate.Disposition != GateDisposition.Passed || stability.Reported is null)
            {
                if (stability.Reported is not null)
                {
                    state = ReanchorG3AcquisitionMotionFromReportedPosition(state, stability.Reported) with
                    {
                        UpdatedUtc = DateTimeOffset.UtcNow,
                        LastReason = $"Post-slew stability gate blocked fresh WCS evidence: {stability.Gate.Code}.",
                    };
                    await PersistG3AcquisitionMotionAsync(state, CancellationToken.None).ConfigureAwait(false);
                }
                stopReason = $"{stability.Gate.Code}: {stability.Gate.Message}";
                break;
            }
            var settledAfter = stability.Reported;
            var commandResidual = stability.CommandResidualArcseconds;
            state = ReanchorG3AcquisitionMotionFromReportedPosition(state, settledAfter) with
            {
                Phase = G3AcquisitionMotionPhase.AwaitingFreshSolve,
                UpdatedUtc = DateTimeOffset.UtcNow,
                LastReason = $"WCS outbound command remained stable for {configuration.G3.MotionPostSlewSettleSeconds:F2}s with {commandResidual:F2} arcsec residual; awaiting a fresh G3 ladder.",
            };
            await PersistG3AcquisitionMotionAsync(state, CancellationToken.None).ConfigureAwait(false);

            attempts++;
            currentField = await CaptureAndAnalyzeG3WithSolveLadderAsync(context, cancellationToken).ConfigureAwait(false);
            var currentResidual = currentField.Solve?.ResidualArcseconds ?? double.NaN;
            var attemptEvidence = await PublishRunJsonEvidenceAsync(
                "g3-wcs-centering-attempt",
                $"G3 WCS centering and fresh validation attempt {attempts}",
                new
                {
                    attempts,
                    reserve.MoveFromCurrentArcseconds,
                    initialCommandResidualArcseconds = initialCommandResidual,
                    commandResidualArcseconds = commandResidual,
                    postSlewSettleSeconds = configuration.G3.MotionPostSlewSettleSeconds,
                    stability.StartedUtc,
                    stability.CompletedUtc,
                    stability.ReportedDriftArcseconds,
                    priorTargetCenterResidualArcseconds = priorResidual,
                    freshTargetCenterResidualArcseconds = double.IsFinite(currentResidual) ? currentResidual : (double?)null,
                    gate = new
                    {
                        disposition = currentField.Gate.Disposition.ToString(),
                        currentField.Gate.Code,
                        currentField.Gate.Message,
                    },
                    currentField.FramePath,
                    durableMotionPath = G3AcquisitionMotionPath(state.ObservationRunId),
                },
                currentField.FramePath,
                cancellationToken).ConfigureAwait(false);

            if (currentField.Gate.Disposition == GateDisposition.Passed)
            {
                state = ReanchorG3AcquisitionMotionFromReportedPosition(state, telescopeMediator.GetCurrentPosition()) with
                {
                    Phase = G3AcquisitionMotionPhase.SettledBudgetLedger,
                    CommandMagnitudeArcseconds = 0,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    LastReason = $"Fresh G3 validation passed after WCS-centering attempt {attempts}; evidence {attemptEvidence}.",
                };
                await PersistG3AcquisitionMotionAsync(state, CancellationToken.None).ConfigureAwait(false);
                return G3FieldPassed(
                    currentField,
                    transferEvidencePath,
                    searchAttempts: 0,
                    searchEvidencePath: null,
                    wcsCenteringAttempts: attempts,
                    wcsCenteringEvidencePath: attemptEvidence);
            }

            if (currentField.Gate.Code == "G3_SOLVED_TARGET_OUTSIDE" &&
                double.IsFinite(currentResidual) &&
                (!double.IsFinite(priorResidual) || currentResidual < priorResidual - state.ArrivalToleranceArcseconds))
            {
                state = ReanchorG3AcquisitionMotionFromReportedPosition(state, telescopeMediator.GetCurrentPosition()) with
                {
                    Phase = G3AcquisitionMotionPhase.AwaitingFreshSolve,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    LastReason = $"Fresh G3 WCS improved from {priorResidual:F2} to {currentResidual:F2} arcsec but the target is still outside; the durable return obligation remains outstanding while another bounded segment is planned.",
                };
                await PersistG3AcquisitionMotionAsync(state, CancellationToken.None).ConfigureAwait(false);
                priorResidual = currentResidual;
                continue;
            }

            stopReason = $"Fresh G3 validation after WCS centering did not pass or prove improvement: {currentField.Gate.Code}: {currentField.Gate.Message}";
            break;
        }

        var returned = await ReturnDurableG3AcquisitionToOriginAsync(context, state, cancellationToken).ConfigureAwait(false);
        var summaryPath = await PublishRunJsonEvidenceAsync(
            "g3-wcs-centering-summary",
            returned.ReturnedToOrigin
                ? "G3 WCS centering stopped and returned to its reported origin"
                : "G3 WCS centering stopped with return blocked",
            new
            {
                attempts,
                stopReason,
                returned.ReturnedToOrigin,
                returned.Message,
                returned.State.CumulativeMotionArcseconds,
                returned.State.CorrectionAttempts,
                durableMotionPath = returned.Path,
                nextRecovery = returned.ReturnedToOrigin ? "bounded-local-search" : "PausedNeedsAttention",
            },
            currentField.FramePath,
            cancellationToken).ConfigureAwait(false);
        if (!returned.ReturnedToOrigin)
        {
            return new StageResult(
                GateResult.Unknown(
                    "G3_WCS_CENTERING_RETURN_BLOCKED",
                    $"{stopReason} Safe return is blocked: {returned.Message}"),
                summaryPath);
        }

        if (!IsRecoverableG3SearchGate(currentField.Gate))
        {
            return new StageResult(
                currentField.Gate,
                summaryPath,
                new Dictionary<string, string>
                {
                    ["g3WcsCenteringOutcome"] = "ReturnedWithoutLocalSearch",
                    ["g3LocalSearchAuthorized"] = bool.FalseString,
                    ["g3LocalSearchBlockedBy"] = currentField.Gate.Code,
                });
        }

        // The failed solve was captured away from the origin and can never
        // authorize an origin-relative local-search command. Reacquire after
        // the attested return so the first local intent is bound to a fresh
        // frame at the actual search origin.
        await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
        var originField = await CaptureAndAnalyzeG3WithSolveLadderAsync(context, cancellationToken).ConfigureAwait(false);
        lastG3Field = originField;
        if (originField.Gate.Disposition == GateDisposition.Passed)
        {
            return G3FieldPassed(
                originField,
                transferEvidencePath,
                searchAttempts: 0,
                searchEvidencePath: summaryPath,
                wcsCenteringAttempts: attempts,
                wcsCenteringEvidencePath: summaryPath);
        }
        if (!IsRecoverableG3SearchGate(originField.Gate))
        {
            return new StageResult(originField.Gate, originField.FramePath, new Dictionary<string, string>
            {
                ["g3WcsCenteringOutcome"] = "ReturnedAndFreshOriginCaptureBlocked",
                ["g3WcsCenteringEvidencePath"] = summaryPath,
                ["g3LocalSearchAuthorized"] = bool.FalseString,
                ["g3LocalSearchBlockedBy"] = originField.Gate.Code,
            });
        }

        // The deterministic recovery order is WCS recenter first, attested
        // return, fresh origin capture, then independently bounded search.
        return await RunBoundedG3LocalSearchAsync(
            context,
            originField,
            transferEvidencePath,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<StageResult> RunBoundedG3LocalSearchAsync(
        ObservationContext context,
        G3FieldState directField,
        string transferEvidencePath,
        CancellationToken cancellationToken)
    {
        if (commissioning is null) throw new InvalidOperationException("Commissioning preset is not loaded.");
        var limits = configuration.G3.Search;
        var waypoints = G3LocalSearchPlanner.Build(limits);
        if (waypoints.Count == 0)
        {
            return Attention(
                ObservationStage.AcquireG3SlitField,
                "G3_SEARCH_PLAN_EMPTY",
                "The configured bounded search has no waypoint inside its declared radius.");
        }

        var origin = telescopeMediator.GetCurrentPosition();
        var originPierSide = telescopeMediator.GetInfo().SideOfPier.ToString();
        var mountGate = ValidateG3SearchMountState(originPierSide);
        if (mountGate.Disposition != GateDisposition.Passed) return new StageResult(mountGate, directField.FramePath);
        var motionAuthorityField = directField;
        var sourceBindingGate = await ValidateG3FieldMountBindingForMotionAsync(
            context,
            motionAuthorityField,
            cancellationToken).ConfigureAwait(false);
        if (sourceBindingGate.Disposition != GateDisposition.Passed)
        {
            return new StageResult(sourceBindingGate, directField.FramePath);
        }

        var startedUtc = DateTimeOffset.UtcNow;
        var previousWorstCaseDuration = context.RemainingWorstCaseDuration;
        context.RemainingWorstCaseDuration =
            (previousWorstCaseDuration ?? context.Plan.PlannedDuration) + limits.MaximumElapsedTime;
        var attempts = new List<G3SearchAttemptEvidence>();
        var declaredEvidencePath = await PublishRunJsonEvidenceAsync(
            "g3-bounded-search-declared",
            "G3 direct-solve recovery plan locked before the first search move",
            new
            {
                transferMode = WideToSlitTransferMode.Skip.ToString(),
                transferOutcome = "TransferSkipped",
                transferEvidencePath,
                directFailure = new
                {
                    disposition = directField.Gate.Disposition.ToString(),
                    directField.Gate.Code,
                    directField.Gate.Message,
                    directField.FramePath,
                    solveSucceeded = directField.Solve?.Result.Success,
                },
                origin = new
                {
                    raDegrees = origin.RADegrees,
                    decDegrees = origin.Dec,
                    pierSide = originPierSide,
                },
                limits = new
                {
                    pattern = limits.Pattern.ToString(),
                    limits.StepArcseconds,
                    limits.MaximumRadiusArcseconds,
                    limits.MaximumCumulativeMotionArcseconds,
                    limits.MaximumAttempts,
                    maximumElapsedSeconds = limits.MaximumElapsedTime.TotalSeconds,
                    postSlewSettleSeconds = configuration.G3.MotionPostSlewSettleSeconds,
                    worstCaseActionSeconds = configuration.G3.MotionWorstCaseActionSeconds,
                    commissionedFineSingleArcseconds = commissioning.MotionLimits.MaximumSingleCorrectionDegrees * 3600d,
                    commissionedFineCumulativeArcseconds = commissioning.MotionLimits.MaximumCumulativeCorrectionDegrees * 3600d,
                    commissionedFineAttempts = commissioning.MotionLimits.MaximumCorrectionAttempts,
                    returnPolicy = "reserve straight-line no-larger-than-step return before every outward move",
                },
                waypoints,
            },
            directField.FramePath,
            cancellationToken).ConfigureAwait(false);

        var search = new G3PendingSearchReturn(
            origin,
            originPierSide,
            CurrentRaTangentOffsetArcseconds: 0,
            CurrentDeclinationOffsetArcseconds: 0,
            CumulativeSearchMotionArcseconds: 0,
            StartedUtc: startedUtc,
            DeclaredEvidencePath: declaredEvidencePath);
        var fineStarted = fineAcquisitionStartedUtc ?? startedUtc;
        var elapsedFineSeconds = Math.Max(0, (startedUtc - fineStarted).TotalSeconds);
        var durableMaximumSingleArcseconds = Math.Min(
            commissioning.MotionLimits.MaximumSingleCorrectionDegrees * 3600d,
            limits.StepArcseconds + MountCommandArrivalToleranceArcseconds);
        var durableMaximumCumulativeArcseconds = Math.Min(
            commissioning.MotionLimits.MaximumCumulativeCorrectionDegrees * 3600d,
            cumulativeCorrectionDegrees * 3600d + limits.MaximumCumulativeMotionArcseconds);
        var durableMaximumElapsedSeconds = Math.Min(
            commissioning.MotionLimits.EffectiveMaximumAcquisitionTime.TotalSeconds,
            elapsedFineSeconds + limits.MaximumElapsedTime.TotalSeconds);
        var durableMaximumAttempts = Math.Min(
            commissioning.MotionLimits.MaximumCorrectionAttempts,
            checked(correctionAttempts + limits.MaximumAttempts));
        if (durableMaximumSingleArcseconds <= 2 * MountCommandArrivalToleranceArcseconds ||
            durableMaximumCumulativeArcseconds < 2 * durableMaximumSingleArcseconds ||
            durableMaximumElapsedSeconds <= elapsedFineSeconds + configuration.G3.MotionWorstCaseActionSeconds)
        {
            return Attention(
                ObservationStage.AcquireG3SlitField,
                "G3_SEARCH_DURABLE_RESERVE_INVALID",
                "The remaining commissioned fine-motion/time envelope cannot initialize a durable search ledger with an outbound and guaranteed-progress return.");
        }
        var durableSearch = await BeginG3AcquisitionMotionAsync(
            context,
            G3AcquisitionMotionKind.LocalSearch,
            origin,
            originPierSide,
            durableMaximumSingleArcseconds,
            limits.MaximumRadiusArcseconds,
            durableMaximumCumulativeArcseconds,
            durableMaximumAttempts,
            TimeSpan.FromSeconds(durableMaximumElapsedSeconds),
            declaredEvidencePath,
            cancellationToken,
            continuationFamilyAdditionalCumulativeArcseconds: limits.MaximumCumulativeMotionArcseconds,
            continuationFamilyAdditionalAttempts: limits.MaximumAttempts,
            continuationFamilyAdditionalElapsed: limits.MaximumElapsedTime).ConfigureAwait(false);
        durableSearch = durableSearch with
        {
            CumulativeMotionArcseconds = Math.Max(
                durableSearch.CumulativeMotionArcseconds,
                cumulativeCorrectionDegrees * 3600d),
            CorrectionAttempts = Math.Max(durableSearch.CorrectionAttempts, correctionAttempts),
            StartedUtc = durableSearch.StartedUtc < fineStarted ? durableSearch.StartedUtc : fineStarted,
            UpdatedUtc = DateTimeOffset.UtcNow,
            LastReason = "Durable local-search ledger inherited the already-consumed fine-motion budget.",
        };
        await PersistG3AcquisitionMotionAsync(durableSearch, cancellationToken).ConfigureAwait(false);
        // The legacy in-memory marker is only a convenience for lifecycle
        // cleanup. Never publish it before the canonical durable ledger exists;
        // a pre-ledger validation failure must leave the next explicit attempt
        // free to declare a new envelope.
        pendingG3SearchReturn = search;
        var stopCode = "G3_SEARCH_ATTEMPTS_EXHAUSTED";
        var stopReason = $"All {waypoints.Count} configured search waypoint(s) were attempted without identifying the target.";

        try
        {
            foreach (var waypoint in waypoints)
            {
                if (DateTimeOffset.UtcNow - startedUtc >= limits.MaximumElapsedTime)
                {
                    stopCode = "G3_SEARCH_TIME_EXHAUSTED";
                    stopReason = $"The G3 search reached its {limits.MaximumElapsedTime.TotalMinutes:F1} minute elapsed-time limit.";
                    break;
                }

                var reserveGate = ValidateG3SearchMoveAndReturnReserve(search, waypoint, commissioning.MotionLimits);
                if (reserveGate.Disposition != GateDisposition.Passed)
                {
                    stopCode = reserveGate.Code;
                    stopReason = reserveGate.Message;
                    break;
                }
                var reportedBeforeOutbound = telescopeMediator.GetCurrentPosition();
                try
                {
                    durableSearch = ReanchorG3AcquisitionMotionFromReportedPosition(
                        durableSearch,
                        reportedBeforeOutbound);
                }
                catch (Exception ex)
                {
                    stopCode = "G3_SEARCH_DURABLE_REANCHOR_FAILED";
                    stopReason = ex.Message;
                    break;
                }
                var durableReserve = G3AcquisitionMotionPlanner.ValidateOutboundAndReturnReserve(
                    durableSearch,
                    waypoint.RaTangentOffsetArcseconds,
                    waypoint.DeclinationOffsetArcseconds,
                    DateTimeOffset.UtcNow);
                if (durableReserve.Gate.Disposition != GateDisposition.Passed)
                {
                    stopCode = durableReserve.Gate.Code;
                    stopReason = durableReserve.Gate.Message;
                    break;
                }
                var commandedCoordinate = G3AcquisitionMotionPlanner.ApplyTangentOffsetArcseconds(
                    NormalizeDegrees(origin.RADegrees),
                    origin.Dec,
                    waypoint.RaTangentOffsetArcseconds,
                    waypoint.DeclinationOffsetArcseconds);
                if (!double.IsFinite(commandedCoordinate.RaDegrees) || !double.IsFinite(commandedCoordinate.DecDegrees))
                {
                    stopCode = "G3_SEARCH_SPHERICAL_COMMAND_INVALID";
                    stopReason = "The bounded-search TAN waypoint cannot be mapped to a finite spherical coordinate.";
                    break;
                }
                var commanded = new Coordinates(
                    commandedCoordinate.RaDegrees,
                    commandedCoordinate.DecDegrees,
                    reportedBeforeOutbound.Epoch,
                    Coordinates.RAType.Degrees);
                var budget = ValidateCorrectionBudget(
                    commissioning.MotionLimits,
                    (durableReserve.MoveFromCurrentArcseconds + MountCommandArrivalToleranceArcseconds) / 3600d);
                if (budget.Disposition != GateDisposition.Passed)
                {
                    stopCode = budget.Code;
                    stopReason = budget.Message;
                    break;
                }

                await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
                mountGate = ValidateG3SearchMountState(originPierSide);
                if (mountGate.Disposition != GateDisposition.Passed)
                {
                    throw new PhysicalActionGateException(mountGate);
                }
                var commandHorizon = ValidateCommandCoordinateHorizon(context, commanded, "G3 bounded-search outbound move");
                if (commandHorizon.Disposition != GateDisposition.Passed)
                {
                    stopCode = commandHorizon.Code;
                    stopReason = commandHorizon.Message;
                    break;
                }
                await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
                var reportedBeforeIntent = telescopeMediator.GetCurrentPosition();
                mountGate = ValidateG3SearchMountState(originPierSide);
                if (mountGate.Disposition != GateDisposition.Passed)
                {
                    stopCode = mountGate.Code;
                    stopReason = mountGate.Message;
                    break;
                }
                sourceBindingGate = await ValidateG3FieldMountBindingForMotionAsync(
                    context,
                    motionAuthorityField,
                    cancellationToken).ConfigureAwait(false);
                if (sourceBindingGate.Disposition != GateDisposition.Passed)
                {
                    stopCode = sourceBindingGate.Code;
                    stopReason = sourceBindingGate.Message;
                    break;
                }
                if (!string.Equals(durableSearch.CoordinateEpoch, reportedBeforeIntent.Epoch.ToString(), StringComparison.Ordinal) ||
                    AngularSeparationArcseconds(reportedBeforeOutbound, reportedBeforeIntent) > durableSearch.ArrivalToleranceArcseconds)
                {
                    stopCode = "G3_SEARCH_PREINTENT_POSITION_CHANGED";
                    stopReason = "The fresh reported mount coordinate changed beyond the reserved arrival tolerance before the search intent; the intent was not written.";
                    break;
                }
                var reportedIntentHorizon = ValidateCommandCoordinateHorizon(
                    context,
                    reportedBeforeIntent,
                    "G3 bounded-search fresh reported position before outbound intent");
                if (reportedIntentHorizon.Disposition != GateDisposition.Passed)
                {
                    stopCode = reportedIntentHorizon.Code;
                    stopReason = reportedIntentHorizon.Message;
                    break;
                }
                var sphericalIntentGate = G3AcquisitionMotionPlanner.ValidateSphericalCommand(
                    durableSearch,
                    NormalizeDegrees(reportedBeforeIntent.RADegrees),
                    reportedBeforeIntent.Dec,
                    NormalizeDegrees(commanded.RADegrees),
                    commanded.Dec,
                    durableReserve.MoveFromCurrentArcseconds);
                if (sphericalIntentGate.Gate.Disposition != GateDisposition.Passed)
                {
                    stopCode = sphericalIntentGate.Gate.Code;
                    stopReason = sphericalIntentGate.Gate.Message;
                    break;
                }
                Report(
                    $"G3 有界搜索 {waypoint.Attempt}/{waypoints.Count}：偏移 RA* {waypoint.RaTangentOffsetArcseconds:+0.0;-0.0;0.0}″，Dec {waypoint.DeclinationOffsetArcseconds:+0.0;-0.0;0.0}″");
                // Canonically persist and conservatively precharge the
                // anticipated waypoint plus arrival error before the
                // asynchronous N.I.N.A. slew.
                durableSearch = durableSearch with
                {
                    Phase = G3AcquisitionMotionPhase.OutboundIntent,
                    PriorReportedRaDegrees = NormalizeDegrees(reportedBeforeIntent.RADegrees),
                    PriorReportedDeclinationDegrees = reportedBeforeIntent.Dec,
                    CommandedRaDegrees = NormalizeDegrees(commanded.RADegrees),
                    CommandedDeclinationDegrees = commanded.Dec,
                    CurrentRaTangentOffsetArcseconds = waypoint.RaTangentOffsetArcseconds,
                    CurrentDeclinationOffsetArcseconds = waypoint.DeclinationOffsetArcseconds,
                    CommandMagnitudeArcseconds = durableReserve.MoveFromCurrentArcseconds,
                    CumulativeMotionArcseconds = durableSearch.CumulativeMotionArcseconds +
                        durableReserve.MoveFromCurrentArcseconds + durableSearch.ArrivalToleranceArcseconds,
                    CorrectionAttempts = durableSearch.CorrectionAttempts + 1,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    LastReason = $"Local-search outbound intent {waypoint.Attempt} precharged before N.I.N.A. slew.",
                };
                await PersistG3AcquisitionMotionAsync(durableSearch, CancellationToken.None).ConfigureAwait(false);
                RegisterCorrection(
                    (durableReserve.MoveFromCurrentArcseconds + MountCommandArrivalToleranceArcseconds) / 3600d);
                search = search with
                {
                    CurrentRaTangentOffsetArcseconds = waypoint.RaTangentOffsetArcseconds,
                    CurrentDeclinationOffsetArcseconds = waypoint.DeclinationOffsetArcseconds,
                    CumulativeSearchMotionArcseconds = search.CumulativeSearchMotionArcseconds +
                        durableReserve.MoveFromCurrentArcseconds + MountCommandArrivalToleranceArcseconds,
                };
                pendingG3SearchReturn = search;
                await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
                var immediatelyBeforeOutbound = telescopeMediator.GetCurrentPosition();
                mountGate = ValidateG3SearchMountState(originPierSide);
                if (mountGate.Disposition != GateDisposition.Passed)
                {
                    stopCode = mountGate.Code;
                    stopReason = mountGate.Message;
                    break;
                }
                sourceBindingGate = await ValidateG3FieldMountBindingForMotionAsync(
                    context,
                    motionAuthorityField,
                    cancellationToken).ConfigureAwait(false);
                if (sourceBindingGate.Disposition != GateDisposition.Passed)
                {
                    stopCode = sourceBindingGate.Code;
                    stopReason = sourceBindingGate.Message;
                    break;
                }
                if (!string.Equals(durableSearch.CoordinateEpoch, immediatelyBeforeOutbound.Epoch.ToString(), StringComparison.Ordinal))
                {
                    stopCode = "G3_SEARCH_PRECOMMAND_EPOCH_CHANGED";
                    stopReason = "Mount epoch changed after the durable search intent; the command was withheld.";
                    break;
                }
                if (AngularSeparationArcseconds(reportedBeforeIntent, immediatelyBeforeOutbound) > MountCommandArrivalToleranceArcseconds)
                {
                    stopCode = "G3_SEARCH_PRECOMMAND_POSITION_CHANGED";
                    stopReason = "Mount position changed after the durable search intent; the stale absolute command was withheld.";
                    break;
                }
                var freshReportedHorizon = ValidateCommandCoordinateHorizon(
                    context,
                    immediatelyBeforeOutbound,
                    "G3 bounded-search fresh reported position before outbound command");
                if (freshReportedHorizon.Disposition != GateDisposition.Passed)
                {
                    stopCode = freshReportedHorizon.Code;
                    stopReason = freshReportedHorizon.Message;
                    break;
                }
                var freshSphericalGate = G3AcquisitionMotionPlanner.ValidateSphericalCommand(
                    durableSearch,
                    NormalizeDegrees(immediatelyBeforeOutbound.RADegrees),
                    immediatelyBeforeOutbound.Dec,
                    NormalizeDegrees(commanded.RADegrees),
                    commanded.Dec,
                    durableReserve.MoveFromCurrentArcseconds);
                if (freshSphericalGate.Gate.Disposition != GateDisposition.Passed)
                {
                    stopCode = freshSphericalGate.Gate.Code;
                    stopReason = freshSphericalGate.Gate.Message;
                    break;
                }
                commandHorizon = ValidateCommandCoordinateHorizon(context, commanded, "G3 bounded-search final outbound check");
                if (commandHorizon.Disposition != GateDisposition.Passed)
                {
                    stopCode = commandHorizon.Code;
                    stopReason = commandHorizon.Message;
                    break;
                }
                if (!await telescopeMediator.SlewToCoordinatesAsync(commanded, cancellationToken).ConfigureAwait(false))
                {
                    stopCode = "G3_SEARCH_MOVE_REJECTED";
                    stopReason = $"N.I.N.A. rejected bounded G3 search move {waypoint.Attempt}.";
                    break;
                }
                await telescopeMediator.WaitForSlew(cancellationToken).ConfigureAwait(false);
                var reportedAfterMove = telescopeMediator.GetCurrentPosition();
                mountGate = ValidateG3SearchMountState(originPierSide);
                if (mountGate.Disposition != GateDisposition.Passed)
                {
                    stopCode = mountGate.Code;
                    stopReason = mountGate.Message;
                    break;
                }
                if (!string.Equals(durableSearch.CoordinateEpoch, reportedAfterMove.Epoch.ToString(), StringComparison.Ordinal))
                {
                    stopCode = "G3_SEARCH_POSTCOMMAND_EPOCH_CHANGED";
                    stopReason = "Mount epoch changed after the bounded search command; no arrival or solve was accepted.";
                    break;
                }
                var reportedHorizon = ValidateCommandCoordinateHorizon(context, reportedAfterMove, "G3 bounded-search reported arrival");
                if (reportedHorizon.Disposition != GateDisposition.Passed)
                {
                    stopCode = reportedHorizon.Code;
                    stopReason = reportedHorizon.Message;
                    break;
                }
                var initialCommandResidualArcseconds = AngularSeparationArcseconds(reportedAfterMove, commanded);
                if (!double.IsFinite(initialCommandResidualArcseconds) ||
                    initialCommandResidualArcseconds > MountCommandArrivalToleranceArcseconds)
                {
                    stopCode = "G3_SEARCH_COMMAND_NOT_REACHED";
                    stopReason = $"The mount stopped {initialCommandResidualArcseconds:F2} arcsec from search waypoint {waypoint.Attempt}. The reported position was adopted and no solve is attempted before bounded return.";
                    durableSearch = ReanchorG3AcquisitionMotionFromReportedPosition(durableSearch, reportedAfterMove) with
                    {
                        UpdatedUtc = DateTimeOffset.UtcNow,
                        LastReason = stopReason,
                    };
                    await PersistG3AcquisitionMotionAsync(durableSearch, CancellationToken.None).ConfigureAwait(false);
                    break;
                }

                var stability = await WaitForG3PostSlewStabilityAsync(
                    context,
                    commanded,
                    reportedAfterMove,
                    originPierSide,
                    durableSearch.CoordinateEpoch,
                    "G3 bounded-search",
                    cancellationToken).ConfigureAwait(false);
                if (stability.Gate.Disposition != GateDisposition.Passed || stability.Reported is null)
                {
                    stopCode = stability.Gate.Code;
                    stopReason = stability.Gate.Message;
                    if (stability.Reported is not null)
                    {
                        durableSearch = ReanchorG3AcquisitionMotionFromReportedPosition(durableSearch, stability.Reported) with
                        {
                            UpdatedUtc = DateTimeOffset.UtcNow,
                            LastReason = $"Post-slew stability gate blocked fresh search evidence: {stability.Gate.Code}.",
                        };
                        await PersistG3AcquisitionMotionAsync(durableSearch, CancellationToken.None).ConfigureAwait(false);
                    }
                    break;
                }
                var settledAfterMove = stability.Reported;
                var commandResidualArcseconds = stability.CommandResidualArcseconds;
                search = ReanchorG3SearchStateFromReportedPosition(search);
                pendingG3SearchReturn = search;
                durableSearch = ReanchorG3AcquisitionMotionFromReportedPosition(durableSearch, settledAfterMove) with
                {
                    Phase = G3AcquisitionMotionPhase.AwaitingFreshSolve,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    LastReason = $"Search command {waypoint.Attempt} remained stable for {configuration.G3.MotionPostSlewSettleSeconds:F2}s with {commandResidualArcseconds:F2} arcsec residual; awaiting fresh G3 evidence.",
                };
                await PersistG3AcquisitionMotionAsync(durableSearch, CancellationToken.None).ConfigureAwait(false);

                var moveEvidencePath = await PublishRunJsonEvidenceAsync(
                    "g3-bounded-search-move",
                    $"G3 bounded search move {waypoint.Attempt}",
                    new
                    {
                        waypoint,
                        commandedRaDegrees = commanded.RADegrees,
                        commandedDecDegrees = commanded.Dec,
                        reportedRaDegrees = reportedAfterMove.RADegrees,
                        reportedDecDegrees = reportedAfterMove.Dec,
                        settledReportedRaDegrees = settledAfterMove.RADegrees,
                        settledReportedDecDegrees = settledAfterMove.Dec,
                        initialCommandResidualArcseconds,
                        commandResidualArcseconds,
                        postSlewSettleSeconds = configuration.G3.MotionPostSlewSettleSeconds,
                        stability.StartedUtc,
                        stability.CompletedUtc,
                        stability.ReportedDriftArcseconds,
                        search.CumulativeSearchMotionArcseconds,
                        globalFineCumulativeArcseconds = cumulativeCorrectionDegrees * 3600d,
                        globalFineCorrectionAttempts = correctionAttempts,
                        safetyGates = "checkpoint + immutable-profile + weather/roof/safety/horizon + mount UTC + cover + connected/tracking/not-slewing/not-pulse-guiding + unchanged pier side",
                    },
                    sourcePath: null,
                    cancellationToken).ConfigureAwait(false);

                await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
                lastG3Field = await CaptureAndAnalyzeG3WithSolveLadderAsync(context, cancellationToken).ConfigureAwait(false);
                motionAuthorityField = lastG3Field;
                var attemptEvidencePath = await PublishRunJsonEvidenceAsync(
                    "g3-bounded-search-attempt",
                    $"G3 bounded search solve/identity attempt {waypoint.Attempt}",
                    new
                    {
                        waypoint,
                        moveEvidencePath,
                        gate = new
                        {
                            disposition = lastG3Field.Gate.Disposition.ToString(),
                            lastG3Field.Gate.Code,
                            lastG3Field.Gate.Message,
                        },
                        lastG3Field.FramePath,
                        solveSucceeded = lastG3Field.Solve?.Result.Success,
                        solveResidualArcseconds = lastG3Field.Solve?.ResidualArcseconds,
                        solvedRaDegrees = lastG3Field.Solve?.Result.Coordinates?.RADegrees,
                        solvedDecDegrees = lastG3Field.Solve?.Result.Coordinates?.Dec,
                        targetPredictionResidualPixels = lastG3Field.TargetIdentification.PredictionResidualPixels,
                        detectedStars = lastG3Field.Candidates.Count,
                    },
                    lastG3Field.FramePath,
                    cancellationToken).ConfigureAwait(false);
                attempts.Add(new G3SearchAttemptEvidence(
                    waypoint,
                    lastG3Field.Gate.Code,
                    lastG3Field.Gate.Disposition,
                    lastG3Field.FramePath,
                    moveEvidencePath,
                    attemptEvidencePath));
                durableSearch = ReanchorG3AcquisitionMotionFromReportedPosition(
                    durableSearch,
                    telescopeMediator.GetCurrentPosition()) with
                {
                    Phase = G3AcquisitionMotionPhase.AwaitingFreshSolve,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    LastReason = $"Fresh G3 ladder attempt {waypoint.Attempt} completed with {lastG3Field.Gate.Code}; the target is not yet identified, so the durable return obligation remains outstanding.",
                };
                await PersistG3AcquisitionMotionAsync(durableSearch, CancellationToken.None).ConfigureAwait(false);

                if (lastG3Field.Gate.Disposition == GateDisposition.Passed)
                {
                    durableSearch = durableSearch with
                    {
                        Phase = G3AcquisitionMotionPhase.SettledBudgetLedger,
                        CommandMagnitudeArcseconds = 0,
                        UpdatedUtc = DateTimeOffset.UtcNow,
                        LastReason = $"Fresh G3 ladder attempt {waypoint.Attempt} identified the target; the acquisition position is accepted and no return remains outstanding.",
                    };
                    await PersistG3AcquisitionMotionAsync(durableSearch, CancellationToken.None).ConfigureAwait(false);
                    pendingG3SearchReturn = null;
                    var successEvidencePath = await PublishG3SearchSummaryAsync(
                        "TargetIdentified",
                        directField,
                        search,
                        attempts,
                        returnedToOrigin: false,
                        finalReason: lastG3Field.Gate.Message,
                        cancellationToken).ConfigureAwait(false);
                    return G3FieldPassed(
                        lastG3Field,
                        transferEvidencePath,
                        attempts.Count,
                        successEvidencePath);
                }
                if (!IsRecoverableG3SearchGate(lastG3Field.Gate))
                {
                    stopCode = "G3_SEARCH_NONRECOVERABLE_GATE";
                    stopReason = $"Search stopped on non-recoverable gate {lastG3Field.Gate.Code}: {lastG3Field.Gate.Message}";
                    break;
                }
            }

            var durableReturnResult = await ReturnDurableG3AcquisitionToOriginAsync(
                context,
                durableSearch,
                cancellationToken).ConfigureAwait(false);
            durableSearch = durableReturnResult.State;
            cumulativeCorrectionDegrees = Math.Max(
                cumulativeCorrectionDegrees,
                durableSearch.CumulativeMotionArcseconds / 3600d);
            correctionAttempts = Math.Max(correctionAttempts, durableSearch.CorrectionAttempts);
            if (durableReturnResult.ReturnedToOrigin)
            {
                search = search with
                {
                    CurrentRaTangentOffsetArcseconds = 0,
                    CurrentDeclinationOffsetArcseconds = 0,
                };
                pendingG3SearchReturn = null;
            }
            else
            {
                try { search = ReanchorG3SearchStateFromReportedPosition(search); }
                catch { /* Durable return evidence remains authoritative. */ }
                pendingG3SearchReturn = search;
            }
            var returnResult = new G3SearchReturnResult(
                durableReturnResult.ReturnedToOrigin,
                search,
                durableReturnResult.Message);
            var summaryPath = await PublishG3SearchSummaryAsync(
                returnResult.ReturnedToOrigin ? "ExhaustedReturned" : "ReturnBlocked",
                directField,
                search,
                attempts,
                returnResult.ReturnedToOrigin,
                $"{stopCode}: {stopReason} {returnResult.Message}",
                cancellationToken).ConfigureAwait(false);
            var gate = returnResult.ReturnedToOrigin
                ? GateResult.Unknown(
                    "G3_BOUNDED_SEARCH_EXHAUSTED_RETURNED",
                    $"{stopReason} The mount was returned to the saved search origin; automatic acquisition is paused for inspection.",
                    new Dictionary<string, double>
                    {
                        ["searchAttempts"] = attempts.Count,
                        ["searchCumulativeMotionArcseconds"] = search.CumulativeSearchMotionArcseconds,
                    })
                : GateResult.Unknown(
                    "G3_SEARCH_RETURN_BLOCKED",
                    $"{stopReason} Safe return to the saved origin could not be completed: {returnResult.Message} Automatic acquisition remains paused at the reported position.",
                    new Dictionary<string, double>
                    {
                        ["searchAttempts"] = attempts.Count,
                        ["remainingOriginOffsetArcseconds"] = search.CurrentRadiusArcseconds,
                    });
            return new StageResult(gate, summaryPath, new Dictionary<string, string>
            {
                ["wideToSlitTransferMode"] = WideToSlitTransferMode.Skip.ToString(),
                ["transferOutcome"] = "TransferSkipped",
                ["transferEvidencePath"] = transferEvidencePath,
                ["searchOutcome"] = returnResult.ReturnedToOrigin ? "ExhaustedReturned" : "ReturnBlocked",
                ["searchOriginPierSide"] = search.OriginPierSide,
            });
        }
        finally
        {
            context.RemainingWorstCaseDuration = previousWorstCaseDuration;
        }
    }

    private GateResult ValidateG3SearchMountState(string expectedPierSide)
    {
        var mount = telescopeMediator.GetInfo();
        if (!mount.Connected)
        {
            return GateResult.Unknown("G3_SEARCH_MOUNT_DISCONNECTED", "The mount disconnected before a bounded G3 search action.");
        }
        if (mount.AtPark)
        {
            return GateResult.Fail("G3_SEARCH_MOUNT_PARKED", "The mount is parked; bounded G3 search motion is prohibited.");
        }
        if (mount.Slewing)
        {
            return GateResult.Unknown("G3_SEARCH_MOUNT_SLEWING", "The mount is already slewing; bounded G3 search motion is prohibited.");
        }
        if (!mount.TrackingEnabled)
        {
            return GateResult.Unknown("G3_SEARCH_TRACKING_DISABLED", "Mount tracking is disabled; bounded G3 search motion is prohibited.");
        }
        if (mount.IsPulseGuiding)
        {
            return GateResult.Unknown("G3_SEARCH_PULSE_GUIDING_ACTIVE", "The mount reports active pulse guiding; bounded G3 search motion is prohibited.");
        }
        var currentPierSide = mount.SideOfPier.ToString();
        if (!IsKnownPierSide(expectedPierSide) || !IsKnownPierSide(currentPierSide))
        {
            return GateResult.Unknown(
                "G3_SEARCH_PIER_SIDE_UNKNOWN",
                $"A known exact pier side is required for bounded G3 search (saved '{expectedPierSide}', current '{currentPierSide}').");
        }
        if (!string.Equals(currentPierSide, expectedPierSide, StringComparison.Ordinal))
        {
            return GateResult.Fail(
                "G3_SEARCH_PIER_SIDE_CHANGED",
                $"The mount pier side changed from '{expectedPierSide}' to '{currentPierSide}' during bounded G3 search; no further search motion is permitted.");
        }
        return GateResult.Pass(
            "G3_SEARCH_MOUNT_STATE_VALID",
            "Mount is connected, unparked, tracking, idle, not pulse-guiding and remains on the saved pier side.");
    }

    private GateResult ValidateG3SearchMoveAndReturnReserve(
        G3PendingSearchReturn state,
        G3LocalSearchWaypoint waypoint,
        MotionLimits fineLimits)
    {
        var searchLimits = configuration.G3.Search;
        var guaranteedReturnProgressArcseconds = searchLimits.StepArcseconds - MountCommandArrivalToleranceArcseconds;
        if (guaranteedReturnProgressArcseconds <= 0)
        {
            return GateResult.Fail(
                "G3_SEARCH_RETURN_PROGRESS_INVALID",
                "The configured search step does not exceed the commissioned arrival tolerance, so a return cannot guarantee progress.");
        }
        var returnMoves = waypoint.RadiusArcseconds <= 0
            ? 0
            : checked((int)Math.Ceiling(waypoint.RadiusArcseconds / guaranteedReturnProgressArcseconds));
        var localRequired = state.CumulativeSearchMotionArcseconds +
            waypoint.MoveFromPreviousArcseconds + MountCommandArrivalToleranceArcseconds +
            returnMoves * (searchLimits.StepArcseconds + MountCommandArrivalToleranceArcseconds);
        if (localRequired > searchLimits.MaximumCumulativeMotionArcseconds + 1e-9)
        {
            return GateResult.Fail(
                "G3_SEARCH_CUMULATIVE_RESERVE_LIMIT",
                $"Search move {waypoint.Attempt} plus a straight-line return would require {localRequired:F2} arcsec, exceeding the declared {searchLimits.MaximumCumulativeMotionArcseconds:F2} arcsec search envelope.");
        }
        if (waypoint.MoveFromPreviousArcseconds > fineLimits.MaximumSingleCorrectionDegrees * 3600d + 1e-9)
        {
            return GateResult.Fail(
                "G3_SEARCH_FINE_SINGLE_LIMIT",
                $"Search step {waypoint.MoveFromPreviousArcseconds:F2} arcsec exceeds the commissioned fine single-motion limit {fineLimits.MaximumSingleCorrectionDegrees * 3600d:F2} arcsec.");
        }
        var globalRequiredDegrees = cumulativeCorrectionDegrees +
            (waypoint.MoveFromPreviousArcseconds + MountCommandArrivalToleranceArcseconds +
             returnMoves * (searchLimits.StepArcseconds + MountCommandArrivalToleranceArcseconds)) / 3600d;
        if (globalRequiredDegrees > fineLimits.MaximumCumulativeCorrectionDegrees + 1e-12)
        {
            return GateResult.Fail(
                "G3_SEARCH_FINE_CUMULATIVE_RESERVE_LIMIT",
                $"Search move {waypoint.Attempt} plus its reserved return would exceed the commissioned fine cumulative limit {fineLimits.MaximumCumulativeCorrectionDegrees * 3600d:F2} arcsec.");
        }
        if (correctionAttempts + 1 + returnMoves > fineLimits.MaximumCorrectionAttempts)
        {
            return GateResult.Fail(
                "G3_SEARCH_FINE_ATTEMPT_RESERVE_LIMIT",
                $"Search move {waypoint.Attempt} plus {returnMoves} reserved return move(s) would exceed the commissioned fine attempt limit {fineLimits.MaximumCorrectionAttempts}.");
        }
        return GateResult.Pass(
            "G3_SEARCH_MOVE_AND_RETURN_RESERVED",
            "The next search step and its no-larger-than-step return are reserved inside both search and commissioned fine-motion envelopes.");
    }

    [Obsolete("Unjournaled G3 return is prohibited; use ReturnDurableG3AcquisitionToOriginAsync.", error: true)]
    private async Task<G3SearchReturnResult> ReturnG3SearchToOriginAsync(
        ObservationContext context,
        G3PendingSearchReturn state,
        CancellationToken cancellationToken)
    {
        if (commissioning is null)
        {
            return new G3SearchReturnResult(false, state, "The commissioning preset is unavailable, so return limits cannot be attested.");
        }
        if (durableG3AcquisitionMotion is { Kind: G3AcquisitionMotionKind.LocalSearch } durable &&
            string.Equals(durable.ObservationRunId, context.Plan.ObservationRunId, StringComparison.Ordinal))
        {
            var returned = await ReturnDurableG3AcquisitionToOriginAsync(
                context,
                durable,
                cancellationToken).ConfigureAwait(false);
            cumulativeCorrectionDegrees = Math.Max(
                cumulativeCorrectionDegrees,
                returned.State.CumulativeMotionArcseconds / 3600d);
            correctionAttempts = Math.Max(correctionAttempts, returned.State.CorrectionAttempts);
            var mapped = state with
            {
                CurrentRaTangentOffsetArcseconds = returned.State.CurrentRaTangentOffsetArcseconds,
                CurrentDeclinationOffsetArcseconds = returned.State.CurrentDeclinationOffsetArcseconds,
                CumulativeSearchMotionArcseconds = Math.Max(
                    state.CumulativeSearchMotionArcseconds,
                    returned.State.CumulativeMotionArcseconds),
            };
            pendingG3SearchReturn = returned.ReturnedToOrigin ? null : mapped;
            return new G3SearchReturnResult(
                returned.ReturnedToOrigin,
                mapped,
                returned.Message);
        }
        try
        {
            state = ReanchorG3SearchStateFromReportedPosition(state);
            pendingG3SearchReturn = state;
        }
        catch (Exception ex)
        {
            return new G3SearchReturnResult(false, state, $"The reported mount position could not be adopted for safe G3 return: {ex.Message}");
        }
        var limits = configuration.G3.Search;
        if (state.CurrentRadiusArcseconds > limits.MaximumRadiusArcseconds + MountCommandArrivalToleranceArcseconds)
        {
            return new G3SearchReturnResult(
                false,
                state,
                $"The reported position is {state.CurrentRadiusArcseconds:F2} arcsec from the G3 search origin, outside the declared {limits.MaximumRadiusArcseconds:F2} arcsec radius. Automatic return after external/manual motion is prohibited.");
        }
        if (state.CurrentRadiusArcseconds <= MountCommandArrivalToleranceArcseconds)
        {
            pendingG3SearchReturn = null;
            return new G3SearchReturnResult(true, state with
            {
                CurrentRaTangentOffsetArcseconds = 0,
                CurrentDeclinationOffsetArcseconds = 0,
            }, "The search was already at its saved origin.");
        }
        var totalMoves = G3LocalSearchPlanner.RequiredReturnMoves(
            state.CurrentRadiusArcseconds,
            limits.StepArcseconds);
        var initialRa = state.CurrentRaTangentOffsetArcseconds;
        var initialDec = state.CurrentDeclinationOffsetArcseconds;
        var current = state;
        for (var move = 1; move <= totalMoves; move++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fractionRemaining = (double)(totalMoves - move) / totalMoves;
            var nextRa = initialRa * fractionRemaining;
            var nextDec = initialDec * fractionRemaining;
            var deltaRa = nextRa - current.CurrentRaTangentOffsetArcseconds;
            var deltaDec = nextDec - current.CurrentDeclinationOffsetArcseconds;
            var magnitude = Math.Sqrt(deltaRa * deltaRa + deltaDec * deltaDec);
            if (!double.IsFinite(magnitude) || magnitude <= 0 || magnitude > limits.StepArcseconds + 1e-9)
            {
                return new G3SearchReturnResult(
                    false,
                    current,
                    $"Computed return step {move}/{totalMoves} is invalid or exceeds the declared {limits.StepArcseconds:F2} arcsec step.");
            }
            if (current.CumulativeSearchMotionArcseconds + magnitude > limits.MaximumCumulativeMotionArcseconds + 1e-9 ||
                cumulativeCorrectionDegrees + magnitude / 3600d > commissioning.MotionLimits.MaximumCumulativeCorrectionDegrees + 1e-12 ||
                correctionAttempts >= commissioning.MotionLimits.MaximumCorrectionAttempts)
            {
                return new G3SearchReturnResult(
                    false,
                    current,
                    "A previously reserved return no longer fits the locked search/fine cumulative or attempt envelope; motion was withheld.");
            }

            try
            {
                await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (ResumeStageRestartException)
            {
                throw;
            }
            catch (PhysicalActionGateException ex)
            {
                return new G3SearchReturnResult(false, current, $"{ex.Gate.Code}: {ex.Gate.Message}");
            }
            var mountGate = ValidateG3SearchMountState(current.OriginPierSide);
            if (mountGate.Disposition != GateDisposition.Passed)
            {
                return new G3SearchReturnResult(false, current, $"{mountGate.Code}: {mountGate.Message}");
            }

            var commanded = ApplySkyCorrection(current.Origin, nextRa, nextDec);
            var commandHorizon = ValidateCommandCoordinateHorizon(context, commanded, "G3 bounded-search return move");
            if (commandHorizon.Disposition != GateDisposition.Passed)
            {
                return new G3SearchReturnResult(false, current, $"{commandHorizon.Code}: {commandHorizon.Message}");
            }
            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            Report($"G3 有界搜索安全回原点 {move}/{totalMoves}：剩余 RA* {nextRa:+0.0;-0.0;0.0}″，Dec {nextDec:+0.0;-0.0;0.0}″");
            if (!await telescopeMediator.SlewToCoordinatesAsync(commanded, cancellationToken).ConfigureAwait(false))
            {
                return new G3SearchReturnResult(false, current, $"N.I.N.A. rejected return move {move}/{totalMoves}.");
            }
            await telescopeMediator.WaitForSlew(cancellationToken).ConfigureAwait(false);
            var priorRa = current.CurrentRaTangentOffsetArcseconds;
            var priorDec = current.CurrentDeclinationOffsetArcseconds;
            RegisterCorrection(magnitude / 3600d);
            current = ReanchorG3SearchStateFromReportedPosition(current) with
            {
                CumulativeSearchMotionArcseconds = current.CumulativeSearchMotionArcseconds + magnitude,
            };
            pendingG3SearchReturn = current;
            var actualMoveArcseconds = Math.Sqrt(
                Math.Pow(current.CurrentRaTangentOffsetArcseconds - priorRa, 2) +
                Math.Pow(current.CurrentDeclinationOffsetArcseconds - priorDec, 2));
            var reportedAfterReturnMove = telescopeMediator.GetCurrentPosition();
            var commandResidualArcseconds = AngularSeparationArcseconds(reportedAfterReturnMove, commanded);
            if (!double.IsFinite(actualMoveArcseconds) ||
                actualMoveArcseconds > limits.StepArcseconds + MountCommandArrivalToleranceArcseconds ||
                !double.IsFinite(commandResidualArcseconds) ||
                commandResidualArcseconds > MountCommandArrivalToleranceArcseconds)
            {
                return new G3SearchReturnResult(
                    false,
                    current,
                    $"Return move {move}/{totalMoves} stopped at a reported position inconsistent with its bounded command (actual move {actualMoveArcseconds:F2} arcsec, command residual {commandResidualArcseconds:F2} arcsec). The reported position was adopted; no further automatic motion is permitted.");
            }
            await PublishRunJsonEvidenceAsync(
                "g3-bounded-search-return-move",
                $"G3 bounded search safe return move {move}/{totalMoves}",
                new
                {
                    move,
                    totalMoves,
                    magnitudeArcseconds = magnitude,
                    remainingRaTangentOffsetArcseconds = current.CurrentRaTangentOffsetArcseconds,
                    remainingDeclinationOffsetArcseconds = current.CurrentDeclinationOffsetArcseconds,
                    actualMoveArcseconds,
                    commandResidualArcseconds,
                    current.CumulativeSearchMotionArcseconds,
                    commandedRaDegrees = commanded.RADegrees,
                    commandedDecDegrees = commanded.Dec,
                    reportedRaDegrees = reportedAfterReturnMove.RADegrees,
                    reportedDecDegrees = reportedAfterReturnMove.Dec,
                },
                sourcePath: null,
                cancellationToken).ConfigureAwait(false);
        }

        current = ReanchorG3SearchStateFromReportedPosition(current);
        if (current.CurrentRadiusArcseconds > MountCommandArrivalToleranceArcseconds)
        {
            pendingG3SearchReturn = current;
            return new G3SearchReturnResult(
                false,
                current,
                $"All planned G3 return segments ended, but the reported position remains {current.CurrentRadiusArcseconds:F2} arcsec from the saved origin.");
        }
        pendingG3SearchReturn = null;
        return new G3SearchReturnResult(true, current, "N.I.N.A. completed every bounded return move to the saved search-origin command coordinates.");
    }

    private async Task<StageResult?> CompletePendingG3SearchReturnAsync(
        ObservationContext context,
        CancellationToken cancellationToken)
    {
        var pending = pendingG3SearchReturn;
        if (pending is null) return null;
        var durableSettled = durableG3AcquisitionMotion is
        {
            Phase: G3AcquisitionMotionPhase.SettledBudgetLedger,
        };
        if (durableSettled) pendingG3SearchReturn = null;
        var evidencePath = await PublishRunJsonEvidenceAsync(
            "g3-bounded-search-resume-return",
            durableSettled
                ? "Legacy in-memory G3 return marker reconciled by the settled durable ledger"
                : "Legacy in-memory G3 return marker has no settled durable authority",
            new
            {
                durableSettled,
                automaticLegacyReturnAttempted = false,
                originRaDegrees = pending.Origin.RADegrees,
                originDecDegrees = pending.Origin.Dec,
                pending.OriginPierSide,
                pending.CurrentRaTangentOffsetArcseconds,
                pending.CurrentDeclinationOffsetArcseconds,
                pending.CumulativeSearchMotionArcseconds,
            },
            sourcePath: null,
            cancellationToken).ConfigureAwait(false);
        if (durableSettled) return null;
        return new StageResult(
            GateResult.Unknown(
                "G3_SEARCH_DURABLE_RETURN_AUTHORITY_MISSING",
                "An interrupted G3 search marker remains, but no settled durable G3 ledger attests recovery. Legacy unjournaled return motion is prohibited."),
            evidencePath,
            new Dictionary<string, string>
            {
                ["searchOutcome"] = "PendingReturnBlocked",
                ["searchOriginPierSide"] = pending.OriginPierSide,
            });
    }

    private async Task<string> PublishG3SearchSummaryAsync(
        string outcome,
        G3FieldState directField,
        G3PendingSearchReturn state,
        IReadOnlyList<G3SearchAttemptEvidence> attempts,
        bool returnedToOrigin,
        string finalReason,
        CancellationToken cancellationToken)
    {
        var path = await PublishRunJsonEvidenceAsync(
            "g3-bounded-search-summary",
            $"G3 bounded search outcome: {outcome}",
            new
            {
                outcome,
                returnedToOrigin,
                finalReason,
                transferMode = WideToSlitTransferMode.Skip.ToString(),
                transferOutcome = "TransferSkipped",
                directFailure = new
                {
                    disposition = directField.Gate.Disposition.ToString(),
                    directField.Gate.Code,
                    directField.Gate.Message,
                    directField.FramePath,
                },
                origin = new
                {
                    raDegrees = state.Origin.RADegrees,
                    decDegrees = state.Origin.Dec,
                    state.OriginPierSide,
                },
                finalOffset = new
                {
                    state.CurrentRaTangentOffsetArcseconds,
                    state.CurrentDeclinationOffsetArcseconds,
                    radiusArcseconds = state.CurrentRadiusArcseconds,
                },
                state.CumulativeSearchMotionArcseconds,
                elapsedSeconds = (DateTimeOffset.UtcNow - state.StartedUtc).TotalSeconds,
                state.DeclaredEvidencePath,
                attempts,
            },
            directField.FramePath,
            cancellationToken).ConfigureAwait(false);
        await WriteAuditBestEffortAsync("g3-bounded-search-summary", new
        {
            outcome,
            returnedToOrigin,
            finalReason,
            evidencePath = path,
            attempts = attempts.Count,
            remainingOffsetArcseconds = state.CurrentRadiusArcseconds,
        }).ConfigureAwait(false);
        return path;
    }

    private async Task<G3FieldState> CaptureAndAnalyzeG3Async(ObservationContext context, CancellationToken cancellationToken)
    {
        if (commissioning is null) throw new InvalidOperationException("Commissioning preset is not loaded.");
        var slitSeed = commissioning.SlitGeometry;
        var focusOwnerBefore = ReadC11MainFocusOwner();
        if (nightSetup is null) throw new InvalidOperationException("Night Setup snapshot is not loaded.");
        var focusOwnerBeforeGate = C11MainFocusPolicy.ValidateLockedPosition(focusOwnerBefore, nightSetup.Value);
        if (focusOwnerBeforeGate.Disposition != GateDisposition.Passed)
        {
            return G3FieldState.Failed(focusOwnerBeforeGate);
        }
        currentC11MainFocusOwner = focusOwnerBefore;
        await EnsurePhdConnectedAsync(cancellationToken).ConfigureAwait(false);
        var identity = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), cancellationToken).ConfigureAwait(false);
        if (!identity.IsValid)
        {
            return G3FieldState.Failed(
                GateResult.Unknown("PHD2_IDENTITY_STALE", string.Join(" ", identity.Failures.Concat(identity.IndeterminateReasons))));
        }
        var profileEvidenceGate = ValidatePhdProfileBindingEvidence();
        if (profileEvidenceGate.Disposition != GateDisposition.Passed)
        {
            return G3FieldState.Failed(profileEvidenceGate);
        }
        var coverGate = await EnsureOpticalCoverOpenAsync(context, cancellationToken).ConfigureAwait(false);
        if (coverGate.Disposition != GateDisposition.Passed)
        {
            return G3FieldState.Failed(coverGate);
        }

        var slitIdentityCalibration = commissioning.Value.SlitWheelIdentity!;
        var shortSequence = await CaptureG3SlitIlluminationSequenceAsync(
            context,
            slitIdentityCalibration.ShortExposureMilliseconds,
            "hdr-short",
            cancellationToken).ConfigureAwait(false);
        var longSequence = await CaptureG3SlitIlluminationSequenceAsync(
            context,
            slitIdentityCalibration.LongExposureMilliseconds,
            "hdr-long",
            cancellationToken).ConfigureAwait(false);
        var sequence = shortSequence;
        var identitySequence = new G3SlitIlluminationSequence(
            $"{shortSequence.SequenceId}+{longSequence.SequenceId}",
            shortSequence.Frames.Concat(longSequence.Frames).ToArray(),
            shortSequence.Commands.Concat(longSequence.Commands).ToArray(),
            shortSequence.Completed && longSequence.Completed,
            string.Join(" ", new[] { shortSequence.Failure, longSequence.Failure }.Where(value => !string.IsNullOrWhiteSpace(value))));
        var sequenceMountGate = ValidateG3SlitSequenceMountBindings(context, identitySequence);
        if (sequenceMountGate.Disposition != GateDisposition.Passed)
        {
            return G3FieldState.Failed(
                sequenceMountGate,
                identitySequence.Frames.FirstOrDefault()?.Capture.Path ?? string.Empty,
                mountBinding: identitySequence.Frames.FirstOrDefault()?.MountBinding);
        }
        var loaded = new List<G3LoadedIlluminationFrame>(identitySequence.Frames.Count);
        foreach (var captured in identitySequence.Frames)
        {
            var loadedImage = await imageDataFactory.CreateFromFile(
                captured.Capture.Path,
                16,
                false,
                RawConverterEnum.FREEIMAGE,
                cancellationToken).ConfigureAwait(false);
            var imageGate = ValidateG3SequenceImage(captured, loadedImage);
            if (imageGate.Disposition != GateDisposition.Passed)
            {
                return G3FieldState.Failed(imageGate, captured.Capture.Path, loadedImage);
            }
            var imageProperties = loadedImage.Properties;
            var raw = loadedImage.Data.FlatArray;
            if (raw.Length != imageProperties.Width * imageProperties.Height)
            {
                return G3FieldState.Failed(
                    GateResult.Unknown(
                        "G3_PIXEL_BUFFER_UNSUPPORTED",
                        $"N.I.N.A. returned an unsupported G3 pixel buffer for {captured.Role}."),
                    captured.Capture.Path,
                    loadedImage);
            }
            loaded.Add(new G3LoadedIlluminationFrame(
                captured,
                loadedImage,
                G3FrameInputPolicy.Create(imageProperties.Width, imageProperties.Height, raw, configuration.G3)));
        }

        var shortLoaded = loaded.Where(frame => frame.Captured.SequenceId == shortSequence.SequenceId).ToArray();
        var longLoaded = loaded.Where(frame => frame.Captured.SequenceId == longSequence.SequenceId).ToArray();
        var offFrames = shortLoaded
            .Where(frame => frame.Captured.Phase is G3SlitIlluminationPhase.OffBefore or G3SlitIlluminationPhase.OffAfter)
            .Select(frame => frame.Frame)
            .ToArray();
        var onFrames = shortLoaded
            .Where(frame => frame.Captured.Phase == G3SlitIlluminationPhase.On)
            .Select(frame => frame.Frame)
            .ToArray();
        if (offFrames.Length != G3SlitIlluminationPolicy.FramesPerPhase * 2 ||
            onFrames.Length != G3SlitIlluminationPolicy.FramesPerPhase)
        {
            return G3FieldState.Failed(GateResult.Unknown(
                "G3_SLIT_LED_SEQUENCE_INCOMPLETE",
                $"The slit sequence retained {offFrames.Length} OFF and {onFrames.Length} ON frames; 6 OFF and 3 ON are mandatory."));
        }

        var longOffFrames = longLoaded
            .Where(frame => frame.Captured.Phase is G3SlitIlluminationPhase.OffBefore or G3SlitIlluminationPhase.OffAfter)
            .Select(frame => frame.Frame)
            .ToArray();
        var longOnFrames = longLoaded
            .Where(frame => frame.Captured.Phase == G3SlitIlluminationPhase.On)
            .Select(frame => frame.Frame)
            .ToArray();
        if (longOffFrames.Length != G3SlitIlluminationPolicy.FramesPerPhase * 2 ||
            longOnFrames.Length != G3SlitIlluminationPolicy.FramesPerPhase)
        {
            return G3FieldState.Failed(GateResult.Unknown(
                "G3_SLIT_LED_HDR_SEQUENCE_INCOMPLETE",
                $"The long HDR slit sequence retained {longOffFrames.Length} OFF and {longOnFrames.Length} ON frames; 6 OFF and 3 ON are mandatory."));
        }

        MonochromeFrame offComposite;
        MonochromeFrame onComposite;
        MonochromeFrame longOffComposite;
        MonochromeFrame longOnComposite;
        try
        {
            offComposite = G3SlitIlluminationPolicy.MedianComposite(offFrames);
            onComposite = G3SlitIlluminationPolicy.MedianComposite(onFrames);
            longOffComposite = G3SlitIlluminationPolicy.MedianComposite(longOffFrames);
            longOnComposite = G3SlitIlluminationPolicy.MedianComposite(longOnFrames);
        }
        catch (ArgumentException ex)
        {
            return G3FieldState.Failed(GateResult.Unknown(
                "G3_SLIT_LED_COMPOSITE_INVALID",
                $"The no-motion median composites could not be formed: {ex.Message}"));
        }

        var maximumIdentityWidthPixels = slitIdentityCalibration.Fingerprints.Max(item =>
            item.MeasuredWidthPixels + slitIdentityCalibration.MaximumNormalizedResidual * item.WidthUncertaintyPixels);
        var pairOptions = new SlitIlluminationPairOptions(
            MaximumPerpendicularSearchPixels: Math.Max(
                commissioning.Value.Phd2SlitPlacement!.SlitMaximumPerpendicularSearchPixels,
                maximumIdentityWidthPixels),
            MaximumAngleSearchDegrees: commissioning.Value.Phd2SlitPlacement.SlitMaximumAngleSearchDegrees,
            MaximumMeasuredWidthPixels: Math.Max(1, (int)Math.Ceiling(maximumIdentityWidthPixels + 2)),
            MinimumContrastSigma: commissioning.Value.Phd2SlitPlacement.SlitMinimumContrastSigma);
        // Fit every independently commissioned physical-width family without
        // trusting the controller's name or ordinal.  A single unconstrained
        // two-edge fit can spend its second component on the broad shoulder of
        // the specular ridge and miss the 300 um far edge.  Family windows are
        // derived only from immutable optical fingerprints; the matcher below
        // still fails (and never remaps) when the optically selected family is
        // not the wheel position reported by UVEX4.
        var apertureCandidates = slitIdentityCalibration.Fingerprints
            .Select(fingerprint =>
            {
                var halfWindow = Math.Max(
                    1.25,
                    slitIdentityCalibration.MaximumNormalizedResidual * fingerprint.WidthUncertaintyPixels + 0.5);
                var minimumWidth = Math.Max(1.5, fingerprint.MeasuredWidthPixels - halfWindow);
                var maximumWidth = Math.Max(minimumWidth + 0.5, fingerprint.MeasuredWidthPixels + halfWindow);
                var analysis = SlitDarkApertureHdrAnalyzer.Analyze(
                    offComposite,
                    onComposite,
                    longOffComposite,
                    longOnComposite,
                    slitSeed,
                    new SlitDarkApertureHdrOptions(
                        MaximumPerpendicularSearchPixels: pairOptions.MaximumPerpendicularSearchPixels,
                        MaximumAngleSearchDegrees: pairOptions.MaximumAngleSearchDegrees,
                        MinimumApertureWidthPixels: minimumWidth,
                        MaximumApertureWidthPixels: maximumWidth,
                        EdgePsfAlphaPixels: slitIdentityCalibration.EdgePsfAlphaPixels,
                        EdgePsfBeta: slitIdentityCalibration.EdgePsfBeta,
                        SharedPsfIsCommissioned: true));
                var identity = SlitWheelIdentityMatcher.Match(
                    slitIdentityCalibration,
                    analysis,
                    nightSetup.Value.SlitPosition,
                    nightSetup.Value.SlitWidthMicrometers,
                    configuration.Phd2.CameraStableId,
                    configuration.G3.Binning,
                    configuration.G3.Binning,
                    offComposite.Width,
                    offComposite.Height);
                return (Fingerprint: fingerprint, Analysis: analysis, Identity: identity);
            })
            .ToArray();
        var selectedAperture = apertureCandidates
            .Where(candidate => candidate.Analysis.Gate.Disposition == GateDisposition.Passed &&
                candidate.Identity.MatchedCandidate is not null)
            .OrderBy(candidate => candidate.Identity.MatchedCandidate!.NormalizedResidual)
            .ThenByDescending(candidate => candidate.Analysis.DeltaBic)
            .FirstOrDefault();
        if (selectedAperture.Analysis is null)
        {
            selectedAperture = apertureCandidates
                .OrderByDescending(candidate =>
                    candidate.Analysis.Gate.Metrics?.TryGetValue("deltaBic", out var deltaBic) == true
                        ? deltaBic
                        : double.NegativeInfinity)
                .First();
        }
        var darkAperture = selectedAperture.Analysis;
        var slitIdentity = selectedAperture.Identity;
        var pairAnalysis = new SlitIlluminationPairAnalysis(
            darkAperture.Gate,
            darkAperture.Geometry,
            SlitIlluminationPolarity.Dark,
            darkAperture.Gate.Metrics?.TryGetValue("fitSignalToNoise", out var darkSnr) == true ? darkSnr : 0,
            darkAperture.ReflectiveEdgeToApertureCenterPixels,
            darkAperture.Geometry.AngleDegrees - slitSeed.AngleDegrees,
            darkAperture.ApertureWidthPixels,
            darkAperture.Gate.Disposition == GateDisposition.Passed ? 1 : 0,
            darkAperture.Gate.Disposition == GateDisposition.Passed ? 1000 : 0,
            darkAperture.LongExposureValidFraction,
            darkAperture.LongExposureSaturatedFraction,
            0);
        // Use the middle OFF-before frame for WCS/image display while all star
        // detection and slit authority use robust composites. The capture loop
        // issues no mount, focus, M2 or camera-owner-changing command.
        var reference = shortLoaded.Single(frame =>
            frame.Captured.Phase == G3SlitIlluminationPhase.OffBefore &&
            frame.Captured.PhaseIndex == 2);
        var image = reference.Image;
        var slitIdentityEvidencePath = await PublishSlitWheelIdentityEvidenceAsync(
            context,
            identitySequence,
            darkAperture,
            slitIdentity,
            reference.Captured.Capture.Path,
            cancellationToken).ConfigureAwait(false);
        var slitDetection = new SlitLocusDetection(
            pairAnalysis.Gate,
            pairAnalysis.Geometry,
            pairAnalysis.ContrastSigma,
            pairAnalysis.PerpendicularOffsetPixels,
            pairAnalysis.AngleOffsetDegrees);
        if (slitIdentity.Gate.Disposition != GateDisposition.Passed)
        {
            PublishG3Preview(image, slitIdentity.Gate.Message, pairAnalysis.Geometry);
            return new G3FieldState(
                slitIdentity.Gate,
                reference.Captured.Capture.Path,
                image,
                null,
                offComposite,
                Array.Empty<StarCandidate>(),
                slitDetection,
                EmptyTargetIdentification(),
                MountBinding: reference.Captured.MountBinding,
                SlitIdentity: slitIdentity,
                SlitIdentityEvidencePath: slitIdentityEvidencePath);
        }
        var focusOwnerAfter = ReadC11MainFocusOwner();
        var focusOwnerAfterGate = C11MainFocusPolicy.ValidateLockedPosition(focusOwnerAfter, nightSetup.Value);
        if (focusOwnerAfterGate.Disposition != GateDisposition.Passed)
        {
            return G3FieldState.Failed(
                focusOwnerAfterGate,
                reference.Captured.Capture.Path,
                image,
                mountBinding: reference.Captured.MountBinding,
                slitIdentity: slitIdentity,
                slitIdentityEvidencePath: slitIdentityEvidencePath);
        }
        currentC11MainFocusOwner = focusOwnerAfter;

        var focusMeasurement = G3StellarFocusAnalyzer.Analyze(offComposite);
        var focusGate = C11MainFocusPolicy.ToObservationGate(focusMeasurement);
        if (focusOwnerAfter.PositionSteps != focusOwnerBefore.PositionSteps)
        {
            focusGate = GateResult.Unknown(
                "G3_MAIN_FOCUS_UNVERIFIED",
                $"C11 Star Focuser Pro moved from {focusOwnerBefore.PositionSteps} to {focusOwnerAfter.PositionSteps} steps while the six OFF frames were being combined, so their main-focus metric is not a single immutable optical state. Only Star Focuser Pro/Gemini on COM8 may be adjusted; UVEX M2 and the GS350 ToupTek AAF are prohibited substitutes.",
                C11MainFocusPolicy.MeasurementMetrics(focusMeasurement));
        }
        await PublishG3MainFocusEvidenceAsync(
            context,
            sequence,
            focusOwnerBefore,
            focusOwnerAfter,
            focusMeasurement,
            focusGate,
            reference.Captured.Capture.Path,
            cancellationToken).ConfigureAwait(false);
        if (focusGate.Disposition != GateDisposition.Passed)
        {
            var ghostTarget = await TryAcquireTargetFromGhostAsync(
                context,
                sequence,
                loaded,
                reference,
                offComposite,
                pairAnalysis,
                slitSeed,
                focusOwnerBefore,
                focusOwnerAfter,
                focusMeasurement,
                g3Solve: null,
                cancellationToken).ConfigureAwait(false);
            if (ghostTarget is not null) return ghostTarget with { SlitIdentity = slitIdentity, SlitIdentityEvidencePath = slitIdentityEvidencePath };
            if (FocusFailureMayBeSaturationDominated(focusMeasurement, offComposite))
            {
                var brightTarget = await TryAcquireBrightTargetFromWingsAsync(
                    context,
                    sequence,
                    reference,
                    pairAnalysis,
                    slitSeed,
                    focusOwnerBefore,
                    focusOwnerAfter,
                    focusMeasurement,
                    cancellationToken).ConfigureAwait(false);
                if (brightTarget is not null) return brightTarget with { SlitIdentity = slitIdentity, SlitIdentityEvidencePath = slitIdentityEvidencePath };
            }
            if (IsRecoverableSparseG3Field(focusMeasurement, pairAnalysis, focusOwnerBefore, focusOwnerAfter))
            {
                var sparseGate = GateResult.Unknown(
                    "G3_STAR_FIELD_SPARSE_VALID_EXPOSURE",
                    $"The nine-frame G3 exposure/binning, stable locked Star Focuser Pro position, paired LED slit geometry and {focusMeasurement.DetectedStarCount} detected sky source(s) passed, but the local field contains too few usable stars to solve. A configured bounded direct-G3 search may try adjacent fields; a zero-star/featureless field never enters this route and this gate does not authorize slit placement or guiding.",
                    C11MainFocusPolicy.MeasurementMetrics(focusMeasurement));
                PublishG3Preview(image, sparseGate.Message, pairAnalysis.Geometry);
                return new G3FieldState(
                    sparseGate,
                    reference.Captured.Capture.Path,
                    image,
                    null,
                    offComposite,
                    focusMeasurement.Stars,
                    slitDetection,
                    EmptyTargetIdentification(),
                    focusMeasurement,
                    MountBinding: reference.Captured.MountBinding,
                    SlitIdentity: slitIdentity,
                    SlitIdentityEvidencePath: slitIdentityEvidencePath);
            }
            PublishG3Preview(
                image,
                focusGate.Message,
                pairAnalysis.Geometry);
            return new G3FieldState(
                focusGate,
                reference.Captured.Capture.Path,
                image,
                null,
                offComposite,
                focusMeasurement.Stars,
                slitDetection,
                EmptyTargetIdentification(),
                focusMeasurement,
                MountBinding: reference.Captured.MountBinding,
                SlitIdentity: slitIdentity,
                SlitIdentityEvidencePath: slitIdentityEvidencePath);
        }

        var solve = await SolveImageAsync(
            image,
            configuration.G3.FocalLengthMillimeters,
            configuration.G3.PixelSizeMicrometers,
            configuration.G3.Binning,
            TargetCoordinates(context.Plan),
            "PHD2/G3 paired-illumination OFF reference",
            reference.Captured.Capture.Path,
            cancellationToken).ConfigureAwait(false);
        if (!solve.Result.Success)
        {
            var ghostTarget = await TryAcquireTargetFromGhostAsync(
                context,
                sequence,
                loaded,
                reference,
                offComposite,
                pairAnalysis,
                slitSeed,
                focusOwnerBefore,
                focusOwnerAfter,
                focusMeasurement,
                solve,
                cancellationToken).ConfigureAwait(false);
            if (ghostTarget is not null) return ghostTarget with { SlitIdentity = slitIdentity, SlitIdentityEvidencePath = slitIdentityEvidencePath };
            var brightTarget = await TryAcquireBrightTargetFromWingsAsync(
                context,
                sequence,
                reference,
                pairAnalysis,
                slitSeed,
                focusOwnerBefore,
                focusOwnerAfter,
                focusMeasurement,
                cancellationToken).ConfigureAwait(false);
            if (brightTarget is not null) return brightTarget with { SlitIdentity = slitIdentity, SlitIdentityEvidencePath = slitIdentityEvidencePath };
            PublishG3Preview(image, "G3 九帧照明序列已保留，但 OFF 星场解算失败；禁止入缝运动。", pairAnalysis.Geometry);
            await PublishG3AnalysisEvidenceAsync(
                context,
                sequence,
                image,
                solve,
                Array.Empty<StarCandidate>(),
                pairAnalysis,
                slitSeed,
                EmptyTargetIdentification(),
                predictedPoint: null,
                cancellationToken).ConfigureAwait(false);
            return G3FieldState.Failed(
                GateResult.Unknown(
                    "G3_PLATE_SOLVE_FAILED",
                    "N.I.N.A. image solver failed on the median-supported G3 OFF field; target identity and mount correction are disabled."),
                reference.Captured.Capture.Path,
                image,
                solve,
                reference.Captured.MountBinding,
                slitIdentity,
                slitIdentityEvidencePath);
        }
        if (solve.Result.Flipped != configuration.G3.ExpectedWcsFlipped)
        {
            PublishG3Preview(image, $"G3 WCS parity={solve.Result.Flipped} 与 commissioning preset 不符。");
            var invalid = await InvalidateCommissioningAsync(
                "COMMISSIONING_G3_PARITY_INVALID",
                $"Solved G3 WCS flipped={solve.Result.Flipped}, expected {configuration.G3.ExpectedWcsFlipped}.").ConfigureAwait(false);
            await PublishG3AnalysisEvidenceAsync(
                context,
                sequence,
                image,
                solve,
                Array.Empty<StarCandidate>(),
                pairAnalysis,
                slitSeed,
                EmptyTargetIdentification(),
                predictedPoint: null,
                cancellationToken).ConfigureAwait(false);
            return G3FieldState.Failed(
                invalid,
                reference.Captured.Capture.Path,
                image,
                solve,
                reference.Captured.MountBinding,
                slitIdentity,
                slitIdentityEvidencePath);
        }

        var properties = image.Properties;
        var candidates = focusMeasurement.Stars;
        if (pairAnalysis.Gate.Disposition != GateDisposition.Passed)
        {
            PublishG3Preview(image, pairAnalysis.Gate.Message, pairAnalysis.Geometry);
            await PublishG3AnalysisEvidenceAsync(
                context,
                sequence,
                image,
                solve,
                candidates,
                pairAnalysis,
                slitSeed,
                EmptyTargetIdentification(),
                predictedPoint: null,
                cancellationToken).ConfigureAwait(false);
            return new G3FieldState(
                pairAnalysis.Gate,
                reference.Captured.Capture.Path,
                image,
                solve,
                offComposite,
                candidates,
                slitDetection,
                EmptyTargetIdentification(),
                focusMeasurement,
                MountBinding: reference.Captured.MountBinding,
                SlitIdentity: slitIdentity,
                SlitIdentityEvidencePath: slitIdentityEvidencePath);
        }
        var maximumCommissionedSlitOffset = Math.Max(
            slitSeed.UncertaintyPixels * 3,
            configuration.Slit.PlacementTolerancePixels * 2);
        if (Math.Abs(slitDetection.PerpendicularOffsetPixels) > maximumCommissionedSlitOffset ||
            Math.Abs(slitDetection.AngleOffsetDegrees) > 3)
        {
            var invalid = await InvalidateCommissioningAsync(
                "COMMISSIONING_SLIT_GEOMETRY_INVALID",
                $"Measured slit residual was {slitDetection.PerpendicularOffsetPixels:F1}px/{slitDetection.AngleOffsetDegrees:F1}°, outside the locked {maximumCommissionedSlitOffset:F1}px/3.0° envelope.").ConfigureAwait(false);
            PublishG3Preview(image, invalid.Message, slitDetection.Geometry);
            await PublishG3AnalysisEvidenceAsync(
                context,
                sequence,
                image,
                solve,
                candidates,
                pairAnalysis,
                slitSeed,
                EmptyTargetIdentification(),
                predictedPoint: null,
                cancellationToken).ConfigureAwait(false);
            return new G3FieldState(
                invalid,
                reference.Captured.Capture.Path,
                image,
                solve,
                offComposite,
                candidates,
                slitDetection,
                EmptyTargetIdentification(),
                focusMeasurement,
                MountBinding: reference.Captured.MountBinding,
                SlitIdentity: slitIdentity,
                SlitIdentityEvidencePath: slitIdentityEvidencePath);
        }

        var targetCoordinates = TargetCoordinates(context.Plan);
        var predicted = targetCoordinates.XYProjection(
            solve.Result.Coordinates,
            new Point(properties.Width / 2d, properties.Height / 2d),
            solve.Result.Pixscale,
            solve.Result.Pixscale,
            solve.Result.PositionAngle);
        var predictedPoint = new PixelPoint(predicted.X, predicted.Y);
        var rawIdentification = SlitTargetIdentifier.Identify(
            candidates,
            predictedPoint,
            configuration.Slit.TargetPredictionTolerancePixels);
        var identified = rawIdentification;
        if (rawIdentification.Gate.Disposition != GateDisposition.Passed && candidates.Count <= 1)
        {
            identified = rawIdentification with
            {
                Gate = GateResult.Unknown(
                    "G3_MAIN_FOCUS_UNVERIFIED",
                    "The median OFF field does not contain enough stellar morphology for a reliable target centroid. Main-telescope/Star Focuser Pro focus must be verified by the operator; UVEX M2 is not an allowed substitute."),
            };
        }
        if (identified.Gate.Disposition != GateDisposition.Passed)
        {
            var ghostTarget = await TryAcquireTargetFromGhostAsync(
                context,
                sequence,
                loaded,
                reference,
                offComposite,
                pairAnalysis,
                slitSeed,
                focusOwnerBefore,
                focusOwnerAfter,
                focusMeasurement,
                solve,
                cancellationToken).ConfigureAwait(false);
            if (ghostTarget is not null) return ghostTarget with { SlitIdentity = slitIdentity, SlitIdentityEvidencePath = slitIdentityEvidencePath };
            var brightTarget = await TryAcquireBrightTargetFromWingsAsync(
                context,
                sequence,
                reference,
                pairAnalysis,
                slitSeed,
                focusOwnerBefore,
                focusOwnerAfter,
                focusMeasurement,
                cancellationToken).ConfigureAwait(false);
            if (brightTarget is not null) return brightTarget with { SlitIdentity = slitIdentity, SlitIdentityEvidencePath = slitIdentityEvidencePath };
        }
        var caption = identified.Target is { } target
            ? $"G3 paired LED: target ({target.Centroid.X:F1},{target.Centroid.Y:F1}), measured slit ({slitDetection.Geometry.AcquisitionPoint.X:F1},{slitDetection.Geometry.AcquisitionPoint.Y:F1}), WCS residual {identified.PredictionResidualPixels:F2}px, stars {candidates.Count}."
            : $"G3 paired LED: target unresolved; predicted ({predictedPoint.X:F1},{predictedPoint.Y:F1}), stars {candidates.Count}.";
        PublishG3Preview(
            image,
            caption,
            slitDetection.Geometry,
            identified.Target?.Centroid);
        await PublishG3AnalysisEvidenceAsync(
            context,
            sequence,
            image,
            solve,
            candidates,
            pairAnalysis,
            slitSeed,
            identified,
            predictedPoint,
            cancellationToken).ConfigureAwait(false);
        var gate = identified.Gate.Disposition == GateDisposition.Passed
            ? GateResult.Pass("G3_FIELD_ANALYZED", caption, new Dictionary<string, double>
            {
                ["targetPredictionResidualPixels"] = identified.PredictionResidualPixels,
                ["slitContrastSigma"] = slitDetection.ContrastSigma,
                ["slitConfidence"] = pairAnalysis.Confidence,
                ["detectedStars"] = candidates.Count,
                ["mainFocusMedianFwhmPixels"] = focusMeasurement.MedianFwhmPixels,
                ["mainFocusMedianEllipticity"] = focusMeasurement.MedianEllipticity,
                ["mainFocusConfidence"] = focusMeasurement.Confidence,
            })
            : identified.Gate;
        return new G3FieldState(
            gate,
            reference.Captured.Capture.Path,
            image,
            solve,
            offComposite,
            candidates,
            slitDetection,
            identified,
            focusMeasurement,
            MountBinding: reference.Captured.MountBinding,
            SlitIdentity: slitIdentity,
            SlitIdentityEvidencePath: slitIdentityEvidencePath);
    }

    private static bool FocusFailureMayBeSaturationDominated(
        G3StellarFocusMeasurement measurement,
        MonochromeFrame frame)
    {
        if (measurement.SaturatedStarFraction > 0 || measurement.Gate.Code == "G3_FOCUS_SATURATED") return true;
        for (var y = 0; y < frame.Height; y++)
        for (var x = 0; x < frame.Width; x++)
            if (frame[x, y] >= frame.SaturationLevel) return true;
        return false;
    }

    /// <summary>
    /// Exceptional, explicitly enabled path for a catalog target whose core is
    /// still saturated at the configured minimum G3 exposure. It never treats
    /// that frame as focus evidence and never introduces a QHY-to-G3 offset.
    /// </summary>
    private async Task<G3FieldState?> TryAcquireBrightTargetFromWingsAsync(
        ObservationContext context,
        G3SlitIlluminationSequence sequence,
        G3LoadedIlluminationFrame reference,
        SlitIlluminationPairAnalysis pairAnalysis,
        SlitGeometry slitSeed,
        C11MainFocusOwnerSnapshot focusOwnerBefore,
        C11MainFocusOwnerSnapshot focusOwnerAfter,
        G3StellarFocusMeasurement rejectedCurrentFocusMeasurement,
        CancellationToken cancellationToken)
    {
        var branch = configuration.G3.EffectiveBrightTarget;
        if (!branch.Enabled) return null;
        if (commissioning is null || nightSetup is null)
            return G3FieldState.Failed(GateResult.Unknown(
                "BRIGHT_TARGET_LOCKS_UNAVAILABLE",
                "Commissioning and Night Setup locks are required before the bright-target branch can capture another G3 frame."));

        var configurationIssues = branch.Validate(configuration.G3.ExposureMilliseconds);
        if (configurationIssues.Count > 0)
        {
            return G3FieldState.Failed(GateResult.Fail(
                "BRIGHT_TARGET_CONFIGURATION_INVALID",
                string.Join(" ", configurationIssues)));
        }
        if (!sequence.Completed || !sequence.Commands.Any(command =>
                command.Phase.StartsWith("safety-off:", StringComparison.Ordinal) &&
                command.LedState == UvexOutputState.Off))
        {
            return G3FieldState.Failed(GateResult.Unknown(
                "BRIGHT_TARGET_SLIT_LED_OFF_UNPROVEN",
                "The paired slit sequence did not prove its final LED-OFF safety readback; a short target frame is withheld."));
        }
        if (pairAnalysis.Gate.Disposition != GateDisposition.Passed)
        {
            return new G3FieldState(
                pairAnalysis.Gate,
                reference.Captured.Capture.Path,
                reference.Image,
                null,
                null,
                rejectedCurrentFocusMeasurement.Stars,
                new SlitLocusDetection(
                    pairAnalysis.Gate,
                    pairAnalysis.Geometry,
                    pairAnalysis.ContrastSigma,
                    pairAnalysis.PerpendicularOffsetPixels,
                    pairAnalysis.AngleOffsetDegrees),
                EmptyTargetIdentification(),
                rejectedCurrentFocusMeasurement);
        }
        var maximumCommissionedSlitOffset = Math.Max(
            slitSeed.UncertaintyPixels * 3,
            configuration.Slit.PlacementTolerancePixels * 2);
        if (Math.Abs(pairAnalysis.PerpendicularOffsetPixels) > maximumCommissionedSlitOffset ||
            Math.Abs(pairAnalysis.AngleOffsetDegrees) > 3)
        {
            var invalid = await InvalidateCommissioningAsync(
                "COMMISSIONING_SLIT_GEOMETRY_INVALID",
                $"Measured slit residual was {pairAnalysis.PerpendicularOffsetPixels:F1}px/{pairAnalysis.AngleOffsetDegrees:F1}°, outside the locked {maximumCommissionedSlitOffset:F1}px/3.0° envelope.").ConfigureAwait(false);
            return G3FieldState.Failed(invalid, reference.Captured.Capture.Path, reference.Image);
        }

        if (lastQhyAcquisition?.AcceptedFrameId is not { } acceptedFrameId || lastQhySolve is null)
        {
            return G3FieldState.Failed(GateResult.Unknown(
                "BRIGHT_TARGET_QHY_WCS_REQUIRED",
                "No current run-bound accepted QHY frame and WCS evidence are available; morphology alone cannot identify the bright target."));
        }
        var accepted = lastQhyAcquisition.Frames.SingleOrDefault(frame => frame.FrameId == acceptedFrameId);
        if (accepted is null ||
            !string.Equals(Path.GetFullPath(accepted.FitsPath), Path.GetFullPath(lastQhySolve.SourcePath), StringComparison.OrdinalIgnoreCase))
        {
            return G3FieldState.Failed(GateResult.Unknown(
                "BRIGHT_TARGET_QHY_WCS_SOURCE_MISMATCH",
                "The latest QHY WCS is not bound to the accepted immutable QHY FITS frame."));
        }
        string currentAcceptedQhySha256;
        string currentQhyWcsEvidenceSha256;
        try
        {
            currentAcceptedQhySha256 = await ComputeFileSha256Async(
                accepted.FitsPath,
                cancellationToken).ConfigureAwait(false);
            currentQhyWcsEvidenceSha256 = await ComputeFileSha256Async(
                lastQhySolve.EvidencePath,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return G3FieldState.Failed(GateResult.Unknown(
                "BRIGHT_TARGET_QHY_EVIDENCE_UNREADABLE",
                $"The accepted QHY FITS or its WCS evidence could not be re-hashed immediately before the bright-target exposure: {ex.Message}"));
        }
        if (!SameHash(currentAcceptedQhySha256, accepted.Sha256) ||
            !SameHash(currentQhyWcsEvidenceSha256, lastQhySolve.EvidenceSha256))
        {
            return G3FieldState.Failed(GateResult.Fail(
                "BRIGHT_TARGET_QHY_EVIDENCE_HASH_MISMATCH",
                "The accepted QHY FITS or its WCS evidence changed after acceptance; target authority is revoked before any additional G3 exposure."));
        }
        var focusBindings = nightSetup.Value.FocusDomains?
            .Where(binding => binding.Role == FocusDomainRole.C11Main)
            .ToArray() ?? [];
        if (focusBindings.Length != 1)
        {
            return G3FieldState.Failed(GateResult.Unknown(
                "BRIGHT_TARGET_C11_FOCUS_REQUIRED",
                $"The locked Night Setup has {focusBindings.Length} independent C11 focus bindings; exactly one is required."));
        }
        if (focusOwnerBefore.PositionSteps != focusOwnerAfter.PositionSteps)
        {
            return G3FieldState.Failed(GateResult.Unknown(
                "BRIGHT_TARGET_C11_FOCUS_MOVED",
                "Star Focuser Pro moved during the paired slit sequence; the independent focus attestation is stale."));
        }

        await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
        var shortPath = ReserveRunEvidencePath("g3-bright-target-minimum-exposure", ".fit");
        Report($"PHD2/G3 超亮目标最短曝光证据 {branch.MinimumG3ExposureMilliseconds} ms（不用于对焦）");
        var captured = await phd2.CaptureFullFrameAsync(
            new Phd2SingleFrameRequest(
                branch.MinimumG3ExposureMilliseconds,
                configuration.G3.Binning,
                configuration.G3.GainPercent,
                shortPath),
            cancellationToken).ConfigureAwait(false);
        var brightTargetMountReadback = CaptureG3FrameMountReadback();
        var image = await imageDataFactory.CreateFromFile(
            captured.Path,
            16,
            false,
            RawConverterEnum.FREEIMAGE,
            cancellationToken).ConfigureAwait(false);
        var imageGate = ValidateBrightTargetG3Image(captured, image, branch.MinimumG3ExposureMilliseconds);
        if (imageGate.Disposition != GateDisposition.Passed)
            return G3FieldState.Failed(imageGate, captured.Path, image);
        var properties = image.Properties;
        var raw = image.Data.FlatArray;
        if (raw.Length != properties.Width * properties.Height)
        {
            return G3FieldState.Failed(
                GateResult.Unknown("BRIGHT_TARGET_G3_PIXEL_BUFFER_UNSUPPORTED", "N.I.N.A. returned an unsupported G3 pixel buffer for the minimum-exposure bright-target frame."),
                captured.Path,
                image);
        }
        var frame = G3FrameInputPolicy.Create(properties.Width, properties.Height, raw, configuration.G3);
        var frameSha256 = await ComputeFileSha256Async(captured.Path, cancellationToken).ConfigureAwait(false);
        var brightTargetMountBinding = CreateG3FieldMountBinding(
            context,
            captured.Path,
            frameSha256,
            captured.CompletedUtc,
            brightTargetMountReadback);
        PublishEvidencePathOnce(
            "g3-bright-target-minimum-exposure",
            captured.Path,
            new Dictionary<string, string>
            {
                ["exposureMilliseconds"] = branch.MinimumG3ExposureMilliseconds.ToString(CultureInfo.InvariantCulture),
                ["gainPercentRequested"] = configuration.G3.GainPercent.ToString(CultureInfo.InvariantCulture),
                ["binningRequested"] = configuration.G3.Binning.ToString(CultureInfo.InvariantCulture),
                ["focusEligible"] = bool.FalseString,
                ["purpose"] = "bright-target-unsaturated-wing-centroid-only",
            },
            frameSha256);

        var shortSolve = await SolveImageAsync(
            image,
            configuration.G3.FocalLengthMillimeters,
            configuration.G3.PixelSizeMicrometers,
            configuration.G3.Binning,
            TargetCoordinates(context.Plan),
            "PHD2/G3 bright-target minimum-exposure frame",
            captured.Path,
            cancellationToken).ConfigureAwait(false);
        if (shortSolve.Result.Success && shortSolve.Result.Flipped != configuration.G3.ExpectedWcsFlipped)
        {
            var invalid = await InvalidateCommissioningAsync(
                "COMMISSIONING_G3_PARITY_INVALID",
                $"Bright-target G3 solve reported flipped={shortSolve.Result.Flipped}, expected {configuration.G3.ExpectedWcsFlipped}.").ConfigureAwait(false);
            return G3FieldState.Failed(invalid, captured.Path, image, shortSolve);
        }

        var currentFocusOwner = ReadC11MainFocusOwner();
        var currentFocusGate = C11MainFocusPolicy.ValidateLockedPosition(currentFocusOwner, nightSetup.Value);
        if (currentFocusGate.Disposition != GateDisposition.Passed || currentFocusOwner.PositionSteps != focusOwnerAfter.PositionSteps)
        {
            return G3FieldState.Failed(
                GateResult.Unknown(
                    "BRIGHT_TARGET_C11_FOCUS_STALE",
                    $"The Star Focuser Pro position changed or lost its lock while the minimum-exposure G3 frame was captured: {currentFocusGate.Message}"),
                captured.Path,
                image,
                shortSolve);
        }
        currentC11MainFocusOwner = currentFocusOwner;

        var focus = focusBindings[0];
        var evaluatedUtc = DateTimeOffset.UtcNow;
        var authority = new BrightTargetAuthorityEvidence(
            Enabled: branch.Enabled,
            ObservationRunId: context.Plan.ObservationRunId,
            CatalogTarget: context.Plan.Target,
            QhyObservationRunId: lastQhyAcquisition.ObservationRunId,
            QhyRequestedTarget: lastQhyAcquisition.RequestedTarget,
            QhyRequestedRightAscensionDegrees: lastQhyAcquisition.TargetRightAscensionDegrees,
            QhyRequestedDeclinationDegrees: lastQhyAcquisition.TargetDeclinationDegrees,
            QhyCoordinateEpoch: lastQhyAcquisition.CoordinateEpoch,
            QhyAcceptedFrameSha256: accepted.Sha256,
            QhyFrameCompletedUtc: accepted.ExposureEndedUtc,
            QhyWcsSucceeded: lastQhySolve.Result.Success,
            QhyWcsRequestedRightAscensionDegrees: lastQhySolve.Requested.RADegrees,
            QhyWcsRequestedDeclinationDegrees: lastQhySolve.Requested.Dec,
            QhyWcsResidualArcseconds: lastQhySolve.ResidualArcseconds,
            QhyWcsEvidenceSha256: lastQhySolve.EvidenceSha256,
            C11FocusEvidenceSha256: focus.Metric.EvidenceSha256,
            C11FocusMetricKind: focus.Metric.Kind,
            C11FocusSourceCameraStableId: focus.Metric.SourceCameraStableDeviceId,
            ExpectedG3SourceCameraStableId: configuration.Phd2.CameraStableId,
            C11FocusMetricValue: focus.Metric.Value,
            C11FocusVerifiedUtc: focus.VerifiedUtc,
            C11FocusValidUntilUtc: focus.ValidUntilUtc,
            C11FocusConfidence: focus.Confidence,
            C11LockedPositionSteps: focus.StartPositionSteps,
            C11CurrentPositionSteps: currentFocusOwner.PositionSteps,
            G3FrameSha256: frameSha256,
            G3FrameCompletedUtc: captured.CompletedUtc,
            G3ExposureMilliseconds: branch.MinimumG3ExposureMilliseconds,
            ConfiguredMinimumG3ExposureMilliseconds: branch.MinimumG3ExposureMilliseconds,
            G3FrameUsedForFocus: false,
            EvaluatedUtc: evaluatedUtc);
        var authorityGate = BrightTargetAuthorityGate.Evaluate(authority, branch.AuthorityOptions);
        var analysis = BrightTargetWingCentroidAnalyzer.Analyze(frame, branch.CentroidOptions);
        var branchGate = authorityGate.Disposition != GateDisposition.Passed
            ? authorityGate
            : analysis.Gate;
        var evidencePath = await PublishBrightTargetEvidenceAsync(
            context,
            sequence,
            accepted,
            lastQhySolve,
            captured,
            frameSha256,
            shortSolve,
            focus,
            authority,
            authorityGate,
            analysis,
            pairAnalysis,
            rejectedCurrentFocusMeasurement,
            cancellationToken).ConfigureAwait(false);
        if (branchGate.Disposition != GateDisposition.Passed || analysis.Target is null)
        {
            PublishG3Preview(
                image,
                $"超亮目标分支未通过：{branchGate.Code} · {branchGate.Message}",
                pairAnalysis.Geometry,
                analysis.Target?.Centroid);
            return new G3FieldState(
                branchGate,
                captured.Path,
                image,
                shortSolve,
                frame,
                rejectedCurrentFocusMeasurement.Stars,
                new SlitLocusDetection(
                    pairAnalysis.Gate,
                    pairAnalysis.Geometry,
                    pairAnalysis.ContrastSigma,
                    pairAnalysis.PerpendicularOffsetPixels,
                    pairAnalysis.AngleOffsetDegrees),
                EmptyTargetIdentification(),
                rejectedCurrentFocusMeasurement,
                analysis,
                authority,
                evidencePath,
                MountBinding: brightTargetMountBinding);
        }

        var wing = analysis.Target;
        var targetCandidate = new StarCandidate(
            wing.Centroid,
            frame.SaturationLevel,
            wing.WingFluxAdu,
            wing.WingSignalToNoise,
            0,
            0,
            1,
            wing.EdgeDistancePixels);
        var identityGate = GateResult.Pass(
            "BRIGHT_TARGET_IDENTIFIED_FROM_WINGS",
            $"The run-bound catalog/QHY/focus chain identified one unique bright target at ({wing.Centroid.X:F2}, {wing.Centroid.Y:F2}) px from unsaturated wings; G3 solve success={shortSolve.Result.Success}. The frame is excluded from focus.",
            new Dictionary<string, double>
            {
                ["wingCentroidX"] = wing.Centroid.X,
                ["wingCentroidY"] = wing.Centroid.Y,
                ["wingSignalToNoise"] = wing.WingSignalToNoise,
                ["wingAngularCoverage"] = wing.AngularCoverageFraction,
                ["g3PlateSolveSucceeded"] = shortSolve.Result.Success ? 1 : 0,
                ["focusEligible"] = 0,
            });
        var identified = new TargetIdentification(
            identityGate,
            targetCandidate,
            wing.Centroid,
            0,
            double.IsFinite(analysis.UniquenessRatio) ? analysis.UniquenessRatio : double.MaxValue);
        var slitDetection = new SlitLocusDetection(
            pairAnalysis.Gate,
            pairAnalysis.Geometry,
            pairAnalysis.ContrastSigma,
            pairAnalysis.PerpendicularOffsetPixels,
            pairAnalysis.AngleOffsetDegrees);
        var caption = $"G3 超亮目标：翼部质心 ({wing.Centroid.X:F1},{wing.Centroid.Y:F1})，狭缝 ({pairAnalysis.Geometry.AcquisitionPoint.X:F1},{pairAnalysis.Geometry.AcquisitionPoint.Y:F1})，短帧 PS3={(shortSolve.Result.Success ? "成功" : "失败但未被伪装成成功")}；焦点证据来自独立 SHA。";
        PublishG3Preview(image, caption, pairAnalysis.Geometry, wing.Centroid);
        return new G3FieldState(
            GateResult.Pass(
                "G3_BRIGHT_TARGET_FIELD_IDENTIFIED",
                caption,
                identityGate.Metrics),
            captured.Path,
            image,
            shortSolve,
            frame,
            rejectedCurrentFocusMeasurement.Stars,
            slitDetection,
            identified,
            rejectedCurrentFocusMeasurement,
            analysis,
            authority,
            evidencePath,
            MountBinding: brightTargetMountBinding);
    }

    private GateResult ValidateBrightTargetG3Image(
        Phd2SingleFrameResult captured,
        IImageData image,
        int expectedExposureMilliseconds)
    {
        if (!captured.ExposureApplied)
            return GateResult.Unknown("BRIGHT_TARGET_G3_EXPOSURE_NOT_APPLIED", "PHD2 did not attest the configured minimum G3 exposure.");
        var exposureDelta = Math.Abs(image.MetaData.Image.ExposureTime * 1000 - expectedExposureMilliseconds);
        if (exposureDelta > Math.Max(10, expectedExposureMilliseconds * 0.02))
        {
            return GateResult.Unknown(
                "BRIGHT_TARGET_G3_EXPOSURE_MISMATCH",
                $"G3 FITS exposure {image.MetaData.Image.ExposureTime * 1000:F0} ms does not match configured minimum {expectedExposureMilliseconds} ms.");
        }
        if (image.MetaData.Camera.BinX > 0 &&
            (image.MetaData.Camera.BinX != configuration.G3.Binning || image.MetaData.Camera.BinY != configuration.G3.Binning))
        {
            return GateResult.Fail(
                "BRIGHT_TARGET_G3_BINNING_MISMATCH",
                $"G3 FITS reports {image.MetaData.Camera.BinX}x{image.MetaData.Camera.BinY}; locked binning is {configuration.G3.Binning}x{configuration.G3.Binning}.");
        }
        return GateResult.Pass(
            "BRIGHT_TARGET_G3_FRAME_VALID",
            "The minimum-exposure G3 FITS metadata passed and remains explicitly excluded from focus.");
    }

    private Task<string> PublishBrightTargetEvidenceAsync(
        ObservationContext context,
        G3SlitIlluminationSequence sequence,
        QhyFrameRecord acceptedQhy,
        PlateSolveEvidence qhySolve,
        Phd2SingleFrameResult g3Capture,
        string g3FrameSha256,
        PlateSolveEvidence g3Solve,
        FocusDomainBinding focus,
        BrightTargetAuthorityEvidence authority,
        GateResult authorityGate,
        BrightTargetCentroidAnalysis analysis,
        SlitIlluminationPairAnalysis slit,
        G3StellarFocusMeasurement rejectedCurrentFocus,
        CancellationToken cancellationToken) =>
        PublishRunJsonEvidenceAsync(
            "g3-bright-target-wing-centroid",
            "Exceptional saturated-target identity and unsaturated-wing slit-placement evidence",
            new
            {
                policy = new
                {
                    explicitlyEnabled = configuration.G3.EffectiveBrightTarget.Enabled,
                    targetSpecificConstant = (string?)null,
                    opticalAxisOffset = (string?)null,
                    focusEligible = false,
                    saturatedCoreUse = "identity morphology only",
                    centroidUse = "unsaturated wings only",
                    g3PlateSolveMayFail = true,
                },
                requestedTarget = context.Plan.Target,
                qhy = new
                {
                    lastQhyAcquisition!.ObservationRunId,
                    lastQhyAcquisition.RequestedTarget,
                    lastQhyAcquisition.TargetRightAscensionDegrees,
                    lastQhyAcquisition.TargetDeclinationDegrees,
                    lastQhyAcquisition.CoordinateEpoch,
                    acceptedQhy.FrameId,
                    acceptedQhy.FitsPath,
                    acceptedQhy.Sha256,
                    acceptedQhy.ExposureEndedUtc,
                    wcsSuccess = qhySolve.Result.Success,
                    qhySolve.ResidualArcseconds,
                    qhySolve.SolverIdentity,
                    qhySolve.EvidencePath,
                    qhySolve.EvidenceSha256,
                },
                c11Focus = new
                {
                    focus.Role,
                    focus.Owner,
                    focus.LogicalDeviceId,
                    focus.StartPositionSteps,
                    focus.Metric,
                    focus.VerifiedUtc,
                    focus.ValidUntilUtc,
                    focus.Confidence,
                    currentPositionSteps = authority.C11CurrentPositionSteps,
                    currentSaturatedFrameWasUsedForFocus = false,
                    currentRejectedFocusGate = new
                    {
                        disposition = rejectedCurrentFocus.Gate.Disposition.ToString(),
                        rejectedCurrentFocus.Gate.Code,
                        rejectedCurrentFocus.Gate.Message,
                    },
                },
                slit = new
                {
                    sequence.SequenceId,
                    slit.Geometry.CalibrationId,
                    centerX = slit.Geometry.AcquisitionPoint.X,
                    centerY = slit.Geometry.AcquisitionPoint.Y,
                    slit.Geometry.AngleDegrees,
                    slit.Geometry.WidthPixels,
                    slit.Confidence,
                    slit.ContrastSigma,
                },
                g3 = new
                {
                    g3Capture.Path,
                    g3FrameSha256,
                    g3Capture.CompletedUtc,
                    exposureMilliseconds = configuration.G3.EffectiveBrightTarget.MinimumG3ExposureMilliseconds,
                    configuration.G3.GainPercent,
                    configuration.G3.Binning,
                    g3PlateSolveSuccess = g3Solve.Result.Success,
                    g3Solve.ResidualArcseconds,
                    g3Solve.SolverIdentity,
                    g3Solve.EvidencePath,
                    g3Solve.EvidenceSha256,
                    focusEligible = false,
                },
                authority = new
                {
                    disposition = authorityGate.Disposition.ToString(),
                    authorityGate.Code,
                    authorityGate.Message,
                    authorityGate.Metrics,
                },
                morphology = new
                {
                    disposition = analysis.Gate.Disposition.ToString(),
                    analysis.Gate.Code,
                    analysis.Gate.Message,
                    analysis.BackgroundAdu,
                    analysis.BackgroundSigmaAdu,
                    uniquenessRatio = double.IsFinite(analysis.UniquenessRatio) ? analysis.UniquenessRatio : (double?)null,
                    focusEligible = false,
                    target = analysis.Target is null ? null : new
                    {
                        centroidX = (double?)analysis.Target.Centroid.X,
                        centroidY = (double?)analysis.Target.Centroid.Y,
                        saturatedCorePixels = (int?)analysis.Target.SaturatedCorePixels,
                        wingPixels = (int?)analysis.Target.WingPixels,
                        wingFluxAdu = (double?)analysis.Target.WingFluxAdu,
                        wingSignalToNoise = (double?)analysis.Target.WingSignalToNoise,
                        angularCoverageFraction = (double?)analysis.Target.AngularCoverageFraction,
                        opposedWingBalance = (double?)analysis.Target.OpposedWingBalance,
                        wingCentroidDisagreementPixels = (double?)analysis.Target.WingCentroidDisagreementPixels,
                        edgeDistancePixels = (double?)analysis.Target.EdgeDistancePixels,
                        nearestOtherSaturatedCorePixels = double.IsFinite(analysis.Target.NearestOtherSaturatedCorePixels)
                            ? analysis.Target.NearestOtherSaturatedCorePixels
                            : (double?)null,
                        secondaryPeakRatio = (double?)analysis.Target.SecondaryPeakRatio,
                    },
                    candidates = analysis.Candidates.Select(candidate => new
                    {
                        disposition = candidate.Gate.Disposition.ToString(),
                        candidate.Gate.Code,
                        candidate.Gate.Message,
                        centroidX = candidate.Centroid.X,
                        centroidY = candidate.Centroid.Y,
                        candidate.SaturatedCorePixels,
                        candidate.WingPixels,
                        candidate.WingFluxAdu,
                        candidate.WingSignalToNoise,
                        candidate.AngularCoverageFraction,
                        candidate.OpposedWingBalance,
                        candidate.WingCentroidDisagreementPixels,
                        candidate.EdgeDistancePixels,
                        nearestOtherSaturatedCorePixels = double.IsFinite(candidate.NearestOtherSaturatedCorePixels)
                            ? candidate.NearestOtherSaturatedCorePixels
                            : (double?)null,
                        candidate.SecondaryPeakRatio,
                    }).ToArray(),
                },
            },
            g3Capture.Path,
            cancellationToken);

    private async Task<G3SlitIlluminationSequence> CaptureG3SlitIlluminationSequenceAsync(
        ObservationContext context,
        int exposureMilliseconds,
        string exposureRole,
        CancellationToken cancellationToken)
    {
        if (exposureMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(exposureMilliseconds));
        if (string.IsNullOrWhiteSpace(exposureRole)) throw new ArgumentException("HDR exposure role is required.", nameof(exposureRole));
        var sequenceId = $"{exposureRole}-{Guid.NewGuid():N}";
        var frames = new List<G3CapturedIlluminationFrame>(G3SlitIlluminationPolicy.FramesPerPhase * 3);
        Exception? failure = null;

        try
        {
            await BeginSlitIlluminationSequenceAsync(sequenceId, cancellationToken).ConfigureAwait(false);

            var offBefore = await CommandActiveSlitIlluminationAsync(
                enabled: false,
                "off-before",
                cancellationToken).ConfigureAwait(false);
            _ = offBefore;
            await CaptureG3IlluminationPhaseAsync(
                context,
                sequenceId,
                G3SlitIlluminationPhase.OffBefore,
                frames,
                exposureMilliseconds,
                cancellationToken).ConfigureAwait(false);

            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            var maximumOnDuration = TimeSpan.FromMilliseconds(
                checked((long)(exposureMilliseconds + 30_000) *
                    G3SlitIlluminationPolicy.FramesPerPhase));
            await G3AtomicLedOnBlock.ExecuteAsync(
                G3SlitIlluminationPolicy.FramesPerPhase,
                maximumOnDuration,
                TimeSpan.FromSeconds(20),
                async token =>
                {
                    _ = await CommandActiveSlitIlluminationAsync(
                        enabled: true,
                        "on",
                        token).ConfigureAwait(false);
                },
                (index, token) => CaptureG3IlluminationFrameAsync(
                    context,
                    sequenceId,
                    G3SlitIlluminationPhase.On,
                    index,
                    frames,
                    exposureMilliseconds,
                    cooperativePauseCheckpoint: false,
                    token),
                async token =>
                {
                    _ = await CommandActiveSlitIlluminationAsync(
                        enabled: false,
                        "off-after",
                        token).ConfigureAwait(false);
                },
                token => CheckpointAndRejectStaleStageStackAsync(context, token),
                cancellationToken).ConfigureAwait(false);

            await CaptureG3IlluminationPhaseAsync(
                context,
                sequenceId,
                G3SlitIlluminationPhase.OffAfter,
                frames,
                exposureMilliseconds,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            using var safetyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var finalOff = await EnsureSlitIlluminationOffAsync(
                $"G3 sequence {sequenceId} finally",
                releaseLeaseOnSuccess: true,
                safetyTimeout.Token).ConfigureAwait(false);
            if (finalOff.Issue is not null)
            {
                failure = new SlitIlluminationSafetyException(finalOff.Issue, failure);
            }
        }

        var hashedFrames = new List<G3CapturedIlluminationFrame>(frames.Count);
        foreach (var frame in frames)
        {
            try
            {
                var sha256 = await ComputeFileSha256Async(
                    frame.Capture.Path,
                    CancellationToken.None).ConfigureAwait(false);
                if (frame.MountReadback is null)
                    throw new InvalidOperationException("G3 frame is missing its immediate post-capture mount readback.");
                var hashed = frame with
                {
                    Sha256 = sha256,
                    MountBinding = CreateG3FieldMountBinding(
                        context,
                        frame.Capture.Path,
                        sha256,
                        frame.Capture.CompletedUtc,
                        frame.MountReadback),
                };
                hashedFrames.Add(hashed);
                PublishEvidencePathOnce(
                    "g3-slit-illumination-raw-fits",
                    hashed.Capture.Path,
                    new Dictionary<string, string>
                    {
                        ["sequenceId"] = sequenceId,
                        ["phase"] = hashed.Phase.ToString(),
                        ["phaseIndex"] = hashed.PhaseIndex.ToString(CultureInfo.InvariantCulture),
                        ["role"] = hashed.Role,
                        ["transitionCandidate"] = hashed.TransitionCandidate.ToString(),
                        ["exposureMilliseconds"] = frame.ExposureMilliseconds.ToString(CultureInfo.InvariantCulture),
                        ["exposureRole"] = exposureRole,
                        ["binning"] = configuration.G3.Binning.ToString(CultureInfo.InvariantCulture),
                        ["saturationAdu"] = configuration.G3.SaturationAdu.ToString(CultureInfo.InvariantCulture),
                        ["requestedParametersApplied"] = hashed.Capture.RequestedParametersApplied.ToString(),
                        ["mountBindingSha256"] = hashed.MountBinding.BindingSha256,
                    },
                    sha256);
            }
            catch (Exception ex)
            {
                failure = new IOException(
                    $"Could not hash/publish immutable G3 frame '{frame.Capture.Path}': {ex.Message}",
                    failure ?? ex);
                hashedFrames.Add(frame);
            }
        }

        var commands = slitIlluminationEvidence.TryGetValue(sequenceId, out var commandQueue)
            ? commandQueue.ToArray()
            : Array.Empty<G3SlitIlluminationCommandEvidence>();
        var sequence = new G3SlitIlluminationSequence(
            sequenceId,
            hashedFrames,
            commands,
            Completed: failure is null &&
                hashedFrames.Count == G3SlitIlluminationPolicy.FramesPerPhase * 3 &&
                hashedFrames.Count(frame => frame.Phase == G3SlitIlluminationPhase.OffBefore) == G3SlitIlluminationPolicy.FramesPerPhase &&
                hashedFrames.Count(frame => frame.Phase == G3SlitIlluminationPhase.On) == G3SlitIlluminationPolicy.FramesPerPhase &&
                hashedFrames.Count(frame => frame.Phase == G3SlitIlluminationPhase.OffAfter) == G3SlitIlluminationPolicy.FramesPerPhase &&
                hashedFrames.All(frame => !string.IsNullOrWhiteSpace(frame.Sha256)) &&
                commands.Any(command => command.Phase == "off-before" && command.LedState == UvexOutputState.Off) &&
                commands.Any(command => command.Phase == "on" && command.LedState == UvexOutputState.On) &&
                commands.Any(command => command.Phase == "off-after" && command.LedState == UvexOutputState.Off) &&
                commands.Any(command => command.Phase.StartsWith("safety-off:", StringComparison.Ordinal) && command.LedState == UvexOutputState.Off),
            Failure: failure?.ToString());
        await PublishG3SlitSequenceEvidenceAsync(sequence, CancellationToken.None).ConfigureAwait(false);
        slitIlluminationEvidence.TryRemove(sequenceId, out _);

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw new InvalidOperationException("Unreachable after rethrowing the G3 slit-illumination failure.");
        }
        if (!sequence.Completed)
        {
            throw new InvalidOperationException(
                "The G3 slit-illumination sequence did not retain and hash exactly 3 OFF-before, 3 ON and 3 OFF-after frames.");
        }
        return sequence;
    }

    private async Task CaptureG3IlluminationPhaseAsync(
        ObservationContext context,
        string sequenceId,
        G3SlitIlluminationPhase phase,
        ICollection<G3CapturedIlluminationFrame> frames,
        int exposureMilliseconds,
        CancellationToken cancellationToken)
    {
        if (phase == G3SlitIlluminationPhase.On)
        {
            throw new InvalidOperationException(
                "LED-ON frames must run through G3AtomicLedOnBlock so OFF precedes every cooperative pause wait.");
        }
        for (var index = 1; index <= G3SlitIlluminationPolicy.FramesPerPhase; index++)
        {
            await CaptureG3IlluminationFrameAsync(
                context,
                sequenceId,
                phase,
                index,
                frames,
                exposureMilliseconds,
                cooperativePauseCheckpoint: true,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CaptureG3IlluminationFrameAsync(
        ObservationContext context,
        string sequenceId,
        G3SlitIlluminationPhase phase,
        int index,
        ICollection<G3CapturedIlluminationFrame> frames,
        int exposureMilliseconds,
        bool cooperativePauseCheckpoint,
        CancellationToken cancellationToken)
    {
        if (cooperativePauseCheckpoint)
        {
            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            var immediate = ValidateImmediatePhysicalActionGates(context);
            if (immediate.Disposition != GateDisposition.Passed)
            {
                throw new PhysicalActionGateException(immediate);
            }
        }

        var role = $"{PhaseEvidenceName(phase)}-{index:D2}";
        var path = ReserveRunEvidencePath($"g3-slit-{exposureMilliseconds}ms-{role}", ".fit");
        Report($"PHD2/G3 狭缝照明 HDR {exposureMilliseconds} ms · {PhaseDisplayName(phase)} {index}/{G3SlitIlluminationPolicy.FramesPerPhase}");
        var captured = await phd2.CaptureFullFrameAsync(
            new Phd2SingleFrameRequest(
                exposureMilliseconds,
                configuration.G3.Binning,
                configuration.G3.GainPercent,
                path),
            cancellationToken).ConfigureAwait(false);
        var mountReadback = CaptureG3FrameMountReadback();
        if (!captured.ExposureApplied)
        {
            throw new InvalidOperationException(
                $"PHD2 did not attest that the commissioned exposure was applied for {role}.");
        }
        frames.Add(new G3CapturedIlluminationFrame(
            sequenceId,
            phase,
            index,
            role,
            TransitionCandidate: phase == G3SlitIlluminationPhase.On && index == 1,
            captured,
            Sha256: string.Empty,
            ExposureMilliseconds: exposureMilliseconds,
            mountReadback,
            MountBinding: null));

        // PHD2's ToupTek path queues the next exposure before stop_capture is
        // observed. The commissioned delay prevents a rapid loop/stop/loop
        // sequence from leaving G3 blocked in its driver. Keep the final frame
        // of each phase delay-free: the intervening LED command/readback is a
        // separate bounded operation and already supplies recovery time.
        if (index < G3SlitIlluminationPolicy.FramesPerPhase)
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(configuration.G3.CameraRecoveryDelayMilliseconds),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private GateResult ValidateG3SequenceImage(
        G3CapturedIlluminationFrame captured,
        IImageData image)
    {
        if (!captured.Capture.ExposureApplied)
        {
            return GateResult.Unknown(
                "G3_EXPOSURE_NOT_APPLIED",
                $"PHD2 did not attest the commissioned exposure for {captured.Role}.");
        }
        var exposureDelta = Math.Abs(
            image.MetaData.Image.ExposureTime * 1000 - captured.ExposureMilliseconds);
        if (exposureDelta > Math.Max(1, captured.ExposureMilliseconds * 0.02))
        {
            return GateResult.Unknown(
                "G3_EXPOSURE_MISMATCH",
                $"G3 {captured.Role} FITS exposure {image.MetaData.Image.ExposureTime * 1000:F0} ms does not match commissioned HDR {captured.ExposureMilliseconds} ms.");
        }
        if (image.MetaData.Camera.BinX > 0 &&
            (image.MetaData.Camera.BinX != configuration.G3.Binning ||
             image.MetaData.Camera.BinY != configuration.G3.Binning))
        {
            return GateResult.Fail(
                "G3_BINNING_MISMATCH",
                $"G3 {captured.Role} FITS reports {image.MetaData.Camera.BinX}x{image.MetaData.Camera.BinY}; commissioned profile is {configuration.G3.Binning}x{configuration.G3.Binning}.");
        }
        return GateResult.Pass(
            "G3_SEQUENCE_FRAME_VALID",
            $"G3 {captured.Role} exposure/binning metadata passed.");
    }

    private async Task PublishG3SlitSequenceEvidenceAsync(
        G3SlitIlluminationSequence sequence,
        CancellationToken cancellationToken)
    {
        await PublishRunJsonEvidenceAsync(
            "g3-slit-illumination-sequence",
            "PHD2/G3 OFF-before x3, ON x3, OFF-after x3 acquisition and LED readbacks",
            new
            {
                sequence.SequenceId,
                sequence.Completed,
                sequence.Failure,
                capturePolicy = new
                {
                    exposureMilliseconds = sequence.Frames.Select(frame => frame.ExposureMilliseconds).Distinct().SingleOrDefault(),
                    framesPerPhase = G3SlitIlluminationPolicy.FramesPerPhase,
                    phases = new[] { "off-before", "on", "off-after" },
                    onFrame1 = "transition-candidate; included in robust three-frame median",
                    motionDuringSequence = "none commanded by UVEX-ADV",
                    cameraRecoveryDelayMilliseconds = configuration.G3.CameraRecoveryDelayMilliseconds,
                    saturationAdu = configuration.G3.SaturationAdu,
                    composite = "detector-fixed per-pixel median; ON x3 versus combined OFF-before/OFF-after x6; no registration or geometric warp is applied to the fixed slit structure",
                },
                commands = sequence.Commands.Select(command => new
                {
                    command.Phase,
                    command.Enabled,
                    ledState = command.LedState.ToString(),
                    command.CommandedUtc,
                    command.StatusTimestampUtc,
                    command.SlitPhotodiodeValue,
                    command.SlitPhotodiodeThreshold,
                    command.SlitPhotodiodeEnabled,
                }).ToArray(),
                frames = sequence.Frames.Select(frame => new
                {
                    frame.Role,
                    phase = frame.Phase.ToString(),
                    frame.PhaseIndex,
                    frame.TransitionCandidate,
                    absolutePath = Path.GetFullPath(frame.Capture.Path),
                    frame.Sha256,
                    frame.Capture.CompletedUtc,
                    frame.ExposureMilliseconds,
                    frame.Capture.ExposureApplied,
                    frame.Capture.RequestedParametersApplied,
                    frame.Capture.UsedLoopSaveFallback,
                    frame.MountBinding,
                }).ToArray(),
            },
            sourcePath: null,
            cancellationToken).ConfigureAwait(false);
    }

    private static string PhaseEvidenceName(G3SlitIlluminationPhase phase) => phase switch
    {
        G3SlitIlluminationPhase.OffBefore => "led-off-before",
        G3SlitIlluminationPhase.On => "led-on",
        G3SlitIlluminationPhase.OffAfter => "led-off-after",
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };

    private static string PhaseDisplayName(G3SlitIlluminationPhase phase) => phase switch
    {
        G3SlitIlluminationPhase.OffBefore => "OFF-before",
        G3SlitIlluminationPhase.On => "ON",
        G3SlitIlluminationPhase.OffAfter => "OFF-after",
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };

    private static string SlitPlacementObservationsRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UVEX-ADV",
        "observations");

    private string SlitPlacementPendingPath(string runId) => Path.Combine(
        SlitPlacementObservationsRoot(),
        SanitizeRunPathSegment(runId),
        "control",
        "slit-placement-pending.json");

    private string Phd2LockShiftPendingPath(string runId) => Path.Combine(
        SlitPlacementObservationsRoot(),
        SanitizeRunPathSegment(runId),
        "control",
        "phd2-lock-shift-pending.json");

    private string ComputeSlitRecoveryContextSha256(ObservationContext context) =>
        ComputeSlitRecoveryContextSha256(context.Plan, configuration.ExpectedTelescopeId);

    private static string ComputeSlitRecoveryContextSha256(
        ObservationPlan plan,
        string expectedTelescopeId)
    {
        var payload = JsonSerializer.Serialize(new
        {
            ExpectedTelescopeId = expectedTelescopeId,
            plan.NightSetupId,
            plan.Target,
            plan.Site,
            plan.PlannedDuration,
            plan.Horizon,
            plan.Motion,
            plan.ExpectedAtrCameraId,
            plan.ExpectedG3ProfileName,
            plan.ExpectedQhyCameraId,
            plan.RequireSafetyMonitor,
        }, EvidenceJsonOptions);
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload)));
    }

    private async Task<(bool RunIsTerminal, GateResult? Error)> ValidateSlitPendingManifestAsync(
        SlitPlacementPendingFileResult item,
        CancellationToken cancellationToken)
    {
        var state = item.State!;
        var controlDirectory = Path.GetDirectoryName(item.Path);
        var runDirectory = controlDirectory is null ? null : Path.GetDirectoryName(controlDirectory);
        if (runDirectory is null)
        {
            return (false, GateResult.Unknown(
                "SLIT_PENDING_MANIFEST_PATH_INVALID",
                $"Cannot derive the observation manifest path from durable slit state '{item.Path}'."));
        }
        var manifestPath = Path.Combine(runDirectory, "manifest.json");
        ObservationRunManifest? manifest;
        try
        {
            manifest = await new ObservationRunJournalStore(manifestPath)
                .ReadAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return (false, GateResult.Unknown(
                "SLIT_PENDING_MANIFEST_UNREADABLE",
                $"Run manifest '{manifestPath}' cannot prove the slit budget lineage: {ex.Message}"));
        }
        if (manifest is null)
        {
            return (false, GateResult.Unknown(
                "SLIT_PENDING_MANIFEST_MISSING",
                $"Run manifest '{manifestPath}' is missing; automatic slit-budget adoption is prohibited."));
        }
        if (!string.Equals(manifest.ObservationRunId, state.ObservationRunId, StringComparison.Ordinal))
        {
            return (false, GateResult.Unknown(
                "SLIT_PENDING_MANIFEST_RUN_MISMATCH",
                $"Run manifest '{manifestPath}' does not belong to durable slit run '{state.ObservationRunId}'."));
        }
        if (manifest.LockedMetadata.Labels is null ||
            !manifest.LockedMetadata.Labels.TryGetValue("telescopeId", out var manifestTelescopeId) ||
            string.IsNullOrWhiteSpace(manifestTelescopeId))
        {
            return (false, GateResult.Unknown(
                "SLIT_PENDING_MANIFEST_TELESCOPE_MISSING",
                $"Run manifest '{manifestPath}' lacks its immutable telescope identity."));
        }
        if (!SameHash(
                state.RecoveryContextSha256,
                ComputeSlitRecoveryContextSha256(manifest.Plan, manifestTelescopeId)))
        {
            return (false, GateResult.Unknown(
                "SLIT_PENDING_MANIFEST_CONTEXT_MISMATCH",
                $"Run manifest '{manifestPath}' does not reproduce the durable slit recovery-context hash."));
        }
        if (manifest.LockedMetadata.AdditionalHashes is null ||
            !manifest.LockedMetadata.AdditionalHashes.TryGetValue("actionConfigurationSha256", out var manifestActionHash) ||
            !SameHash(state.ActionConfigurationSha256, manifestActionHash) ||
            manifest.LockedMetadata.CommissioningPresetSha256 is null ||
            !SameHash(state.CommissioningPresetSha256, manifest.LockedMetadata.CommissioningPresetSha256))
        {
            return (false, GateResult.Unknown(
                "SLIT_PENDING_MANIFEST_BINDING_MISMATCH",
                $"Run manifest '{manifestPath}' does not reproduce the durable action-configuration and commissioning bindings."));
        }
        return (manifest.TerminalState is not null, null);
    }

    private async Task<(SlitPlacementPendingResolution? Resolution, GateResult? Error)> ResolveSlitPlacementPendingAsync(
        ObservationContext context,
        CancellationToken cancellationToken)
    {
        var discovered = await SlitPlacementPendingStore.DiscoverAsync(
            SlitPlacementObservationsRoot(),
            cancellationToken).ConfigureAwait(false);
        var unreadable = discovered.Where(item => item.Error is not null || item.State is null).ToArray();
        if (unreadable.Length > 0)
        {
            return (null, GateResult.Unknown(
                "SLIT_PENDING_EVIDENCE_UNREADABLE",
                $"Durable slit-placement evidence is unreadable at {string.Join(", ", unreadable.Select(item => item.Path))}: {string.Join("; ", unreadable.Select(item => item.Error ?? "missing state"))} Automatic mount motion is prohibited; explicit manual takeover is required."));
        }

        foreach (var item in discovered)
        {
            var expectedPath = SlitPlacementPendingPath(item.State!.ObservationRunId);
            if (!string.Equals(Path.GetFullPath(item.Path), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
            {
                return (null, GateResult.Unknown(
                    "SLIT_PENDING_PATH_IDENTITY_MISMATCH",
                    $"Durable slit state '{item.Path}' does not match its run-bound path '{expectedPath}'. Automatic mount motion is prohibited."));
            }
        }
        if (pendingSlitPlacement is not null && !discovered.Any(item =>
            string.Equals(item.State!.ObservationRunId, pendingSlitPlacement.ObservationRunId, StringComparison.Ordinal)))
        {
            return (null, GateResult.Unknown(
                "SLIT_PENDING_DURABILITY_LOST",
                "In-memory slit recovery authority has no matching durable file. Automatic mount motion is prohibited; explicit manual takeover is required."));
        }

        var quarantined = discovered.Where(item =>
            item.State!.Phase == SlitPlacementPendingPhase.SettledBudgetLedger &&
            item.State.TransformInvalidated &&
            SameHash(item.State.CommissioningPresetSha256, configuration.Commissioning.PresetSha256)).ToArray();
        if (quarantined.Length > 0)
        {
            return (null, GateResult.Fail(
                "SLIT_TRANSFORM_DURABLY_INVALIDATED",
                $"Commissioning preset {configuration.Commissioning.PresetSha256} was durably disproved by a fresh G3 slit response: {string.Join("; ", quarantined.Select(item => item.State!.TransformInvalidReason))} Install a new independently commissioned transform or use explicit manual takeover; this transform will not be reused."));
        }

        var candidates = new List<SlitPlacementBudgetCandidate>(discovered.Count);
        foreach (var item in discovered)
        {
            var isCurrentRun = string.Equals(
                item.State!.ObservationRunId,
                context.Plan.ObservationRunId,
                StringComparison.Ordinal);
            var terminal = false;
            if (!isCurrentRun)
            {
                var manifest = await ValidateSlitPendingManifestAsync(item, cancellationToken).ConfigureAwait(false);
                if (manifest.Error is not null) return (null, manifest.Error);
                terminal = manifest.RunIsTerminal;
            }
            candidates.Add(new SlitPlacementBudgetCandidate(item.Path, item.State!, terminal));
        }

        var selection = SlitPlacementBudgetLineageResolver.Resolve(
            candidates,
            context.Plan.ObservationRunId);
        if (selection.Gate.Disposition != GateDisposition.Passed)
        {
            return (null, selection.Gate);
        }
        if (selection.Candidate is { } selected)
        {
            return (new SlitPlacementPendingResolution(
                selected.Path,
                selected.State,
                !string.Equals(selected.State.ObservationRunId, context.Plan.ObservationRunId, StringComparison.Ordinal)), null);
        }
        return (null, null);
    }

    private async Task<StageResult?> RecoverDurableSlitPlacementBeforeStageAsync(
        ObservationStage stage,
        ObservationContext context,
        CancellationToken cancellationToken)
    {
        var ownsRecoveryLock = slitPlacementRecoveryDepth.Value == 0;
        if (ownsRecoveryLock)
        {
            await slitPlacementRecoveryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        slitPlacementRecoveryDepth.Value++;
        try
        {
            var resolved = await ResolveSlitPlacementPendingAsync(context, cancellationToken).ConfigureAwait(false);
            if (resolved.Error is not null) return new StageResult(resolved.Error);
            if (resolved.Resolution is null)
            {
                pendingSlitPlacement = null;
                return null;
            }
            var resolution = resolved.Resolution;
            var state = resolution.State;
            var path = resolution.Path;
            var identityGate = ValidateSlitPendingIdentity(context, state, resolution.ForeignRun);
            if (identityGate.Disposition != GateDisposition.Passed) return new StageResult(identityGate, path);
            if (state.Phase == SlitPlacementPendingPhase.SettledBudgetLedger)
            {
                if (resolution.ForeignRun)
                {
                    var handoff = await PersistCurrentRunSlitBudgetHandoffAsync(
                        context,
                        state,
                        path,
                        cancellationToken).ConfigureAwait(false);
                    if (handoff.Error is not null) return new StageResult(handoff.Error, path);
                    state = handoff.State!;
                }
                AdoptDurableSlitBudget(state);
                pendingSlitPlacement = null;
                if (stage >= ObservationStage.PlaceTargetOnSlit && lastG3Field is null)
                {
                    Interlocked.Exchange(ref resumeRecoveryRequired, 1);
                }
                return null;
            }
            pendingSlitPlacement = state;

            var interlocks = await EvaluateInterlocksAsync(context, connectQhy: false, cancellationToken).ConfigureAwait(false);
            if (interlocks.Disposition != GateDisposition.Passed) return new StageResult(interlocks, path);
            identityGate = ValidateSlitPendingIdentity(context, state, resolution.ForeignRun);
            if (identityGate.Disposition != GateDisposition.Passed) return new StageResult(identityGate, path);

            var returned = await ReturnPendingSlitPlacementLockedAsync(
                context,
                state,
                resolution.ForeignRun ? "process-restart adoption of prior-run pending move" : "stage/resume recovery",
                cancellationToken,
                allowForeignRunRecovery: resolution.ForeignRun).ConfigureAwait(false);
            if (!returned.CanAdvance) return returned;
            if (resolution.ForeignRun)
            {
                var handoff = await PersistSettledForeignSlitBudgetAfterReturnAsync(
                    context,
                    path,
                    cancellationToken).ConfigureAwait(false);
                if (handoff is not null) return new StageResult(handoff, path);
            }

            // The pre-move image is stale after a recovery return. Reacquire it
            // automatically before a stage that depends on slit placement; no
            // operator confirmation gate is introduced.
            lastG3Field = null;
            validatedG3GuideConnectionEpoch = null;
            validatedG3GuideEpoch = null;
            if (stage >= ObservationStage.PlaceTargetOnSlit)
            {
                Interlocked.Exchange(ref resumeRecoveryRequired, 1);
            }
            return null;
        }
        finally
        {
            slitPlacementRecoveryDepth.Value--;
            if (ownsRecoveryLock) slitPlacementRecoveryLock.Release();
        }
    }

    private async Task<StageResult?> ReturnDurableSlitPlacementForLifecycleAsync(
        ObservationContext context,
        string reason,
        CancellationToken cancellationToken)
    {
        var ownsRecoveryLock = slitPlacementRecoveryDepth.Value == 0;
        if (ownsRecoveryLock)
        {
            await slitPlacementRecoveryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        slitPlacementRecoveryDepth.Value++;
        try
        {
            var resolved = await ResolveSlitPlacementPendingAsync(context, cancellationToken).ConfigureAwait(false);
            if (resolved.Error is not null) return new StageResult(resolved.Error);
            if (resolved.Resolution is null)
            {
                pendingSlitPlacement = null;
                return null;
            }
            var resolution = resolved.Resolution;
            var state = resolution.State;
            var path = resolution.Path;
            var identity = ValidateSlitPendingIdentity(context, state, resolution.ForeignRun);
            if (identity.Disposition != GateDisposition.Passed) return new StageResult(identity, path);
            if (state.Phase == SlitPlacementPendingPhase.SettledBudgetLedger)
            {
                if (resolution.ForeignRun)
                {
                    var handoff = await PersistCurrentRunSlitBudgetHandoffAsync(
                        context,
                        state,
                        path,
                        cancellationToken).ConfigureAwait(false);
                    if (handoff.Error is not null) return new StageResult(handoff.Error, path);
                    state = handoff.State!;
                }
                AdoptDurableSlitBudget(state);
                pendingSlitPlacement = null;
                return null;
            }
            pendingSlitPlacement = state;
            var interlocks = await EvaluateInterlocksAsync(context, connectQhy: false, cancellationToken).ConfigureAwait(false);
            if (interlocks.Disposition != GateDisposition.Passed) return new StageResult(interlocks, path);
            identity = ValidateSlitPendingIdentity(context, state, resolution.ForeignRun);
            if (identity.Disposition != GateDisposition.Passed) return new StageResult(identity, path);
            var returned = await ReturnPendingSlitPlacementLockedAsync(
                context,
                state,
                reason,
                cancellationToken,
                lifecycleRecovery: true,
                allowForeignRunRecovery: resolution.ForeignRun).ConfigureAwait(false);
            if (!returned.CanAdvance || !resolution.ForeignRun) return returned;
            var handoffError = await PersistSettledForeignSlitBudgetAfterReturnAsync(
                context,
                path,
                cancellationToken).ConfigureAwait(false);
            return handoffError is null ? returned : new StageResult(handoffError, path);
        }
        finally
        {
            slitPlacementRecoveryDepth.Value--;
            if (ownsRecoveryLock) slitPlacementRecoveryLock.Release();
        }
    }

    private GateResult ValidateSlitPendingIdentity(
        ObservationContext context,
        SlitPlacementPendingState state,
        bool allowForeignRunRecovery = false)
    {
        if (!string.Equals(state.ObservationRunId, context.Plan.ObservationRunId, StringComparison.Ordinal))
        {
            if (!allowForeignRunRecovery)
            {
                return GateResult.Fail("SLIT_PENDING_RUN_MISMATCH", "Durable slit-placement state belongs to a different observation run.");
            }
        }
        if (slitPlacementBudgetLineageId is not null &&
            !string.Equals(slitPlacementBudgetLineageId, state.BudgetLineageId, StringComparison.Ordinal))
        {
            return GateResult.Fail(
                "SLIT_BUDGET_LINEAGE_MISMATCH",
                "The runner already adopted a different slit-placement budget lineage; automatic counter merging is prohibited.");
        }
        if (!SameHash(state.ActionConfigurationSha256, configuration.ActionConfigurationSha256))
        {
            return GateResult.Fail("SLIT_PENDING_CONFIGURATION_MISMATCH", "Action-bearing configuration changed after the pending slit move was persisted.");
        }
        if (!SameHash(state.RecoveryContextSha256, ComputeSlitRecoveryContextSha256(context)))
        {
            return GateResult.Fail(
                "SLIT_PENDING_RECOVERY_CONTEXT_MISMATCH",
                "Observatory site, horizon policy, safety requirement, telescope identity or Night Setup changed after the pending slit move was persisted. Automatic prior-run adoption is prohibited; use explicit manual takeover.");
        }
        var locked = configuration.Commissioning;
        if (!SameHash(state.CommissioningPresetSha256, locked.PresetSha256) ||
            !string.Equals(state.TransformCalibrationId, locked.MountTransformCalibrationId, StringComparison.Ordinal) ||
            !string.Equals(state.PierSide, locked.MountTransformPierSide, StringComparison.Ordinal) ||
            Math.Abs(state.MaximumSingleCorrectionDegrees - locked.MaximumSingleCorrectionArcseconds / 3600d) > 1e-12 ||
            Math.Abs(state.MaximumCumulativeCorrectionDegrees - locked.MaximumCumulativeCorrectionArcseconds / 3600d) > 1e-12 ||
            state.MaximumCorrectionAttempts != locked.MaximumCorrectionAttempts ||
            Math.Abs(state.MaximumAcquisitionSeconds - locked.MaximumAcquisitionMinutes * 60d) > 1e-6)
        {
            return GateResult.Fail(
                "SLIT_PENDING_LOCKED_BINDING_MISMATCH",
                "Durable slit-placement commissioning hash, transform, pier side or motion envelope does not match the run-locked binding.");
        }
        if (commissioning is not null)
        {
            if (!SameHash(state.CommissioningPresetSha256, commissioning.Sha256))
            {
                return GateResult.Fail("SLIT_PENDING_COMMISSIONING_MISMATCH", "Commissioning evidence changed after the pending slit move was persisted.");
            }
            if (!string.Equals(state.TransformCalibrationId, commissioning.MountTransform.CalibrationId, StringComparison.Ordinal))
            {
                return GateResult.Fail("SLIT_PENDING_TRANSFORM_MISMATCH", "Pixel-to-mount transform identity changed after the pending slit move was persisted.");
            }
            if (!string.Equals(state.PierSide, commissioning.MountTransform.PierSide, StringComparison.Ordinal) ||
                Math.Abs(state.MaximumSingleCorrectionDegrees - commissioning.MotionLimits.MaximumSingleCorrectionDegrees) > 1e-12 ||
                Math.Abs(state.MaximumCumulativeCorrectionDegrees - commissioning.MotionLimits.MaximumCumulativeCorrectionDegrees) > 1e-12 ||
                state.MaximumCorrectionAttempts != commissioning.MotionLimits.MaximumCorrectionAttempts ||
                Math.Abs(state.MaximumAcquisitionSeconds - commissioning.MotionLimits.EffectiveMaximumAcquisitionTime.TotalSeconds) > 1e-6)
            {
                return GateResult.Fail(
                    "SLIT_PENDING_ENVELOPE_MISMATCH",
                    "Durable slit-placement pier-side or motion-envelope values do not exactly match the locked commissioning preset.");
            }
        }
        return GateResult.Pass("SLIT_PENDING_IDENTITY_VALID", "Pending slit-placement run, configuration and commissioning identities match.");
    }

    private void AdoptDurableSlitBudget(SlitPlacementPendingState state)
    {
        if (slitPlacementBudgetLineageId is not null &&
            !string.Equals(slitPlacementBudgetLineageId, state.BudgetLineageId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Cannot adopt two different slit-placement budget lineages in one runner.");
        }
        slitPlacementBudgetLineageId = state.BudgetLineageId;
        cumulativeCorrectionDegrees = Math.Max(cumulativeCorrectionDegrees, state.CumulativeCorrectionDegrees);
        correctionAttempts = Math.Max(correctionAttempts, state.CorrectionAttempts);
        fineAcquisitionStartedUtc = fineAcquisitionStartedUtc is { } current && current <= state.FineAcquisitionStartedUtc
            ? current
            : state.FineAcquisitionStartedUtc;
    }

    private async Task<(SlitPlacementPendingState? State, GateResult? Error)> PersistCurrentRunSlitBudgetHandoffAsync(
        ObservationContext context,
        SlitPlacementPendingState foreignSettledState,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        if (foreignSettledState.Phase != SlitPlacementPendingPhase.SettledBudgetLedger)
        {
            return (null, GateResult.Unknown(
                "SLIT_BUDGET_HANDOFF_NOT_SETTLED",
                "A foreign slit budget cannot be handed off until its reported-position recovery is durably settled."));
        }
        if (string.Equals(
                foreignSettledState.ObservationRunId,
                context.Plan.ObservationRunId,
                StringComparison.Ordinal))
        {
            return (foreignSettledState, null);
        }

        var currentPath = SlitPlacementPendingPath(context.Plan.ObservationRunId);
        var currentLoad = await SlitPlacementPendingStore.LoadAsync(currentPath, cancellationToken).ConfigureAwait(false);
        if (currentLoad.Error is not null)
        {
            return (null, GateResult.Unknown(
                "SLIT_BUDGET_HANDOFF_CURRENT_UNREADABLE",
                $"Current-run slit budget ledger '{currentPath}' is unreadable: {currentLoad.Error}"));
        }
        var candidates = new List<SlitPlacementBudgetCandidate>
        {
            new(sourcePath, foreignSettledState, RunIsTerminal: false),
        };
        if (currentLoad.State is { } currentState)
        {
            candidates.Add(new SlitPlacementBudgetCandidate(currentPath, currentState, RunIsTerminal: false));
        }
        var selection = SlitPlacementBudgetLineageResolver.Resolve(candidates, context.Plan.ObservationRunId);
        if (selection.Gate.Disposition != GateDisposition.Passed || selection.Candidate is null)
        {
            return (null, selection.Gate);
        }

        var now = DateTimeOffset.UtcNow;
        var handoff = selection.Candidate.State with
        {
            ObservationRunId = context.Plan.ObservationRunId,
            Phase = SlitPlacementPendingPhase.SettledBudgetLedger,
            CreatedUtc = currentLoad.State?.CreatedUtc ?? now,
            UpdatedUtc = now,
            LastReason =
                $"Process-restart budget handoff from '{sourcePath}'. Cumulative distance, attempts and the earliest fine-acquisition start remain monotonic.",
        };
        var identity = ValidateSlitPendingIdentity(context, handoff);
        if (identity.Disposition != GateDisposition.Passed) return (null, identity);
        await SlitPlacementPendingStore.WriteAtomicAsync(
            currentPath,
            handoff,
            CancellationToken.None).ConfigureAwait(false);
        AdoptDurableSlitBudget(handoff);
        return (handoff, null);
    }

    private async Task<GateResult?> PersistSettledForeignSlitBudgetAfterReturnAsync(
        ObservationContext context,
        string foreignPath,
        CancellationToken cancellationToken)
    {
        var loaded = await SlitPlacementPendingStore.LoadAsync(foreignPath, cancellationToken).ConfigureAwait(false);
        if (loaded.Error is not null || loaded.State is null)
        {
            return GateResult.Unknown(
                "SLIT_BUDGET_HANDOFF_SOURCE_UNREADABLE",
                $"Returned foreign slit state '{foreignPath}' cannot be adopted: {loaded.Error ?? "missing settled state"}");
        }
        if (loaded.State.Phase != SlitPlacementPendingPhase.SettledBudgetLedger)
        {
            return GateResult.Unknown(
                "SLIT_BUDGET_HANDOFF_SOURCE_NOT_SETTLED",
                "The foreign slit move returned successfully in memory but its durable source is not a settled budget ledger.");
        }
        var handoff = await PersistCurrentRunSlitBudgetHandoffAsync(
            context,
            loaded.State,
            foreignPath,
            cancellationToken).ConfigureAwait(false);
        return handoff.Error;
    }

    private async Task<StageResult> ReturnPendingSlitPlacementLockedAsync(
        ObservationContext context,
        SlitPlacementPendingState state,
        string reason,
        CancellationToken cancellationToken,
        bool lifecycleRecovery = false,
        bool allowForeignRunRecovery = false)
    {
        var path = SlitPlacementPendingPath(state.ObservationRunId);
        state = state with
        {
            Phase = SlitPlacementPendingPhase.ReturnRequired,
            UpdatedUtc = DateTimeOffset.UtcNow,
            LastReason = reason,
        };
        await SlitPlacementPendingStore.WriteAtomicAsync(path, state, CancellationToken.None).ConfigureAwait(false);
        pendingSlitPlacement = state;
        AdoptDurableSlitBudget(state);
        cancellationToken.ThrowIfCancellationRequested();
        for (var move = 1; move <= state.MaximumCorrectionAttempts; move++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identityGate = ValidateSlitPendingIdentity(context, state, allowForeignRunRecovery);
            if (identityGate.Disposition != GateDisposition.Passed) return new StageResult(identityGate, path);
            var reported = telescopeMediator.GetCurrentPosition();
            try { EnsureFiniteReportedCoordinates(reported); }
            catch (Exception ex)
            {
                return Attention(ObservationStage.PlaceTargetOnSlit, "SLIT_RETURN_POSITION_UNKNOWN", ex.Message);
            }
            if (!string.Equals(state.CoordinateEpoch, reported.Epoch.ToString(), StringComparison.Ordinal))
            {
                return Attention(
                    ObservationStage.PlaceTargetOnSlit,
                    "SLIT_RETURN_COORDINATE_EPOCH_CHANGED",
                    $"The durable segment origin uses epoch '{state.CoordinateEpoch}', but the mount now reports '{reported.Epoch}'. Automatic return is prohibited.");
            }
            var step = SlitPlacementRecoveryPlanner.PlanNextReturnStep(
                state,
                reported.RADegrees,
                reported.Dec,
                MountCommandArrivalToleranceArcseconds);
            if (step.AlreadyAtOrigin)
            {
                var completedEvidencePath = await PublishRunJsonEvidenceAsync(
                    "slit-placement-return-completed",
                    "Pending slit-placement segment returned to its reported pre-segment origin",
                    new
                    {
                        reason,
                        moveCount = move - 1,
                        step.CurrentRadiusArcseconds,
                        state.TransformCalibrationId,
                        state.MoveIntentEvidencePath,
                        state.CumulativeCorrectionDegrees,
                        state.CorrectionAttempts,
                    },
                    sourcePath: null,
                    cancellationToken).ConfigureAwait(false);
                state = state with
                {
                    Phase = SlitPlacementPendingPhase.SettledBudgetLedger,
                    PriorReportedRaDegrees = state.SegmentOriginRaDegrees,
                    PriorReportedDeclinationDegrees = state.SegmentOriginDeclinationDegrees,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    LastReason = $"{reason}; reported pre-segment origin reached; evidence {completedEvidencePath}.",
                };
                await SlitPlacementPendingStore.WriteAtomicAsync(path, state, CancellationToken.None).ConfigureAwait(false);
                pendingSlitPlacement = null;
                if (state.TransformInvalidated)
                {
                    invalidatedCommissioningSha256 = state.CommissioningPresetSha256;
                    commissioningInvalidReason = state.TransformInvalidReason ?? "The pixel-to-mount transform was durably invalidated by a fresh G3 response.";
                }
                return Passed(
                    "SLIT_PENDING_RETURNED",
                    "The mount's reported position is back at the durable pre-segment origin; stale G3 evidence was discarded.");
            }
            if (step.Gate.Disposition != GateDisposition.Passed)
            {
                state = state with
                {
                    Phase = SlitPlacementPendingPhase.ReturnRequired,
                    PriorReportedRaDegrees = reported.RADegrees,
                    PriorReportedDeclinationDegrees = reported.Dec,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    LastReason = $"{reason}: {step.Gate.Code}: {step.Gate.Message}",
                };
                await SlitPlacementPendingStore.WriteAtomicAsync(path, state, CancellationToken.None).ConfigureAwait(false);
                pendingSlitPlacement = state;
                return new StageResult(step.Gate, path);
            }

            var commanded = new Coordinates(
                step.CommandedRaDegrees,
                step.CommandedDeclinationDegrees,
                reported.Epoch,
                Coordinates.RAType.Degrees);
            var returnIntentEvidencePath = await PublishRunJsonEvidenceAsync(
                "slit-placement-return-intent",
                $"Reported-position slit-placement return move {move}",
                new
                {
                    reason,
                    state.ObservationRunId,
                    state.ActionConfigurationSha256,
                    state.CommissioningPresetSha256,
                    state.TransformCalibrationId,
                    state.PierSide,
                    state.CoordinateEpoch,
                    state.SegmentOriginRaDegrees,
                    state.SegmentOriginDeclinationDegrees,
                    priorReportedRaDegrees = reported.RADegrees,
                    priorReportedDecDegrees = reported.Dec,
                    commandedRaDegrees = commanded.RADegrees,
                    commandedDecDegrees = commanded.Dec,
                    commandMagnitudeArcseconds = step.CommandMagnitudeDegrees * 3600,
                    currentRadiusArcseconds = step.CurrentRadiusArcseconds,
                    cumulativeBeforeArcseconds = state.CumulativeCorrectionDegrees * 3600,
                    attemptsBefore = state.CorrectionAttempts,
                },
                sourcePath: null,
                cancellationToken).ConfigureAwait(false);
            state = state with
            {
                Phase = SlitPlacementPendingPhase.ReturnRequired,
                PriorReportedRaDegrees = reported.RADegrees,
                PriorReportedDeclinationDegrees = reported.Dec,
                CommandedRaDegrees = commanded.RADegrees,
                CommandedDeclinationDegrees = commanded.Dec,
                CommandMagnitudeDegrees = step.CommandMagnitudeDegrees,
                // Consume the declared action before the command can be
                // accepted. Crash-before-command is conservatively charged;
                // crash-after-command can therefore never disappear from the
                // durable cumulative/attempt ledger.
                CumulativeCorrectionDegrees = state.CumulativeCorrectionDegrees + step.CommandMagnitudeDegrees,
                CorrectionAttempts = state.CorrectionAttempts + 1,
                UpdatedUtc = DateTimeOffset.UtcNow,
                LastReason = $"{reason}; return intent {returnIntentEvidencePath}",
            };
            await SlitPlacementPendingStore.WriteAtomicAsync(path, state, CancellationToken.None).ConfigureAwait(false);
            pendingSlitPlacement = state;

            if (lifecycleRecovery)
            {
                var immediate = ValidateImmediatePhysicalActionGates(context);
                if (immediate.Disposition != GateDisposition.Passed) return new StageResult(immediate, path);
            }
            else
            {
                await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            }
            var mountGate = ValidateG3SearchMountState(state.PierSide);
            if (mountGate.Disposition != GateDisposition.Passed) return new StageResult(mountGate, path);
            var horizonGate = ValidateCommandCoordinateHorizon(context, commanded, "slit-placement failure return");
            if (horizonGate.Disposition != GateDisposition.Passed) return new StageResult(horizonGate, path);
            if (lifecycleRecovery)
            {
                var immediate = ValidateImmediatePhysicalActionGates(context);
                if (immediate.Disposition != GateDisposition.Passed) return new StageResult(immediate, path);
            }
            else
            {
                await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            }
            mountGate = ValidateG3SearchMountState(state.PierSide);
            if (mountGate.Disposition != GateDisposition.Passed) return new StageResult(mountGate, path);
            horizonGate = ValidateCommandCoordinateHorizon(context, commanded, "slit-placement failure return immediately before command");
            if (horizonGate.Disposition != GateDisposition.Passed) return new StageResult(horizonGate, path);

            var immediatelyBeforeReturn = telescopeMediator.GetCurrentPosition();
            try { EnsureFiniteReportedCoordinates(immediatelyBeforeReturn); }
            catch (Exception ex)
            {
                return Attention(ObservationStage.PlaceTargetOnSlit, "SLIT_RETURN_PRECOMMAND_POSITION_UNKNOWN", ex.Message);
            }
            if (!string.Equals(state.CoordinateEpoch, immediatelyBeforeReturn.Epoch.ToString(), StringComparison.Ordinal))
            {
                state = state with
                {
                    PriorReportedRaDegrees = immediatelyBeforeReturn.RADegrees,
                    PriorReportedDeclinationDegrees = immediatelyBeforeReturn.Dec,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    LastReason = $"{reason}; mount epoch changed immediately before return command.",
                };
                await SlitPlacementPendingStore.WriteAtomicAsync(path, state, CancellationToken.None).ConfigureAwait(false);
                pendingSlitPlacement = state;
                return Attention(
                    ObservationStage.PlaceTargetOnSlit,
                    "SLIT_RETURN_PRECOMMAND_EPOCH_CHANGED",
                    $"Mount epoch changed from durable '{state.CoordinateEpoch}' to '{immediatelyBeforeReturn.Epoch}' immediately before command; the old absolute command was not sent.");
            }
            var precommandDriftArcseconds = AngularSeparationArcseconds(immediatelyBeforeReturn, reported);
            if (!double.IsFinite(precommandDriftArcseconds) ||
                precommandDriftArcseconds > MountCommandArrivalToleranceArcseconds)
            {
                state = state with
                {
                    PriorReportedRaDegrees = immediatelyBeforeReturn.RADegrees,
                    PriorReportedDeclinationDegrees = immediatelyBeforeReturn.Dec,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    LastReason = $"{reason}; reported position changed {precommandDriftArcseconds:F2} arcsec after return intent.",
                };
                await SlitPlacementPendingStore.WriteAtomicAsync(path, state, CancellationToken.None).ConfigureAwait(false);
                pendingSlitPlacement = state;
                return Attention(
                    ObservationStage.PlaceTargetOnSlit,
                    "SLIT_RETURN_PRECOMMAND_POSITION_CHANGED",
                    $"The reported mount position changed {precommandDriftArcseconds:F2} arcsec after the durable return intent. The stale absolute command was not sent; Resume must replan from the new reported position or the operator must take over.");
            }
            mountGate = ValidateG3SearchMountState(state.PierSide);
            if (mountGate.Disposition != GateDisposition.Passed) return new StageResult(mountGate, path);

            cancellationToken.ThrowIfCancellationRequested();
            var accepted = await telescopeMediator.SlewToCoordinatesAsync(commanded, cancellationToken).ConfigureAwait(false);
            if (!accepted)
            {
                try
                {
                    var afterRejection = telescopeMediator.GetCurrentPosition();
                    EnsureFiniteReportedCoordinates(afterRejection);
                    state = state with
                    {
                        PriorReportedRaDegrees = afterRejection.RADegrees,
                        PriorReportedDeclinationDegrees = afterRejection.Dec,
                        UpdatedUtc = DateTimeOffset.UtcNow,
                        LastReason = $"{reason}; N.I.N.A. rejected the return command; the post-rejection reported position was persisted.",
                    };
                    await SlitPlacementPendingStore.WriteAtomicAsync(path, state, CancellationToken.None).ConfigureAwait(false);
                    pendingSlitPlacement = state;
                }
                catch
                {
                    // The precharged intent remains authoritative even if the
                    // post-rejection position cannot be read or persisted.
                }
                return Attention(
                    ObservationStage.PlaceTargetOnSlit,
                    "SLIT_RETURN_COMMAND_REJECTED",
                    "N.I.N.A. rejected the durable reported-position return command; pending recovery remains on disk.");
            }
            await telescopeMediator.WaitForSlew(cancellationToken).ConfigureAwait(false);
            var after = telescopeMediator.GetCurrentPosition();
            try { EnsureFiniteReportedCoordinates(after); }
            catch (Exception ex)
            {
                return Attention(ObservationStage.PlaceTargetOnSlit, "SLIT_RETURN_POSITION_UNKNOWN", ex.Message);
            }
            if (!string.Equals(state.CoordinateEpoch, after.Epoch.ToString(), StringComparison.Ordinal))
            {
                state = state with
                {
                    PriorReportedRaDegrees = after.RADegrees,
                    PriorReportedDeclinationDegrees = after.Dec,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    LastReason = $"{reason}; mount epoch changed after return command.",
                };
                await SlitPlacementPendingStore.WriteAtomicAsync(path, state, CancellationToken.None).ConfigureAwait(false);
                pendingSlitPlacement = state;
                return Attention(
                    ObservationStage.PlaceTargetOnSlit,
                    "SLIT_RETURN_POSTCOMMAND_EPOCH_CHANGED",
                    $"Mount epoch changed from durable '{state.CoordinateEpoch}' to '{after.Epoch}' after the command. No coordinate residual or settled ledger was accepted.");
            }
            var commandResidual = AngularSeparationArcseconds(after, commanded);
            var updatedCumulative = state.CumulativeCorrectionDegrees;
            var updatedAttempts = state.CorrectionAttempts;
            state = state with
            {
                PriorReportedRaDegrees = after.RADegrees,
                PriorReportedDeclinationDegrees = after.Dec,
                CumulativeCorrectionDegrees = updatedCumulative,
                CorrectionAttempts = updatedAttempts,
                UpdatedUtc = DateTimeOffset.UtcNow,
                LastReason = $"{reason}; return command residual {commandResidual:F2} arcsec",
            };
            await SlitPlacementPendingStore.WriteAtomicAsync(path, state, CancellationToken.None).ConfigureAwait(false);
            pendingSlitPlacement = state;
            cumulativeCorrectionDegrees = Math.Max(cumulativeCorrectionDegrees, updatedCumulative);
            correctionAttempts = Math.Max(correctionAttempts, updatedAttempts);
            await PublishRunJsonEvidenceAsync(
                "slit-placement-return-move",
                $"Reported-position slit-placement return move {move} completed",
                new
                {
                    returnIntentEvidencePath,
                    reportedRaDegrees = after.RADegrees,
                    reportedDecDegrees = after.Dec,
                    commandedRaDegrees = commanded.RADegrees,
                    commandedDecDegrees = commanded.Dec,
                    commandResidualArcseconds = commandResidual,
                    cumulativeCorrectionArcseconds = updatedCumulative * 3600,
                    correctionAttempts = updatedAttempts,
                },
                sourcePath: null,
                cancellationToken).ConfigureAwait(false);
            if (!double.IsFinite(commandResidual) || commandResidual > MountCommandArrivalToleranceArcseconds)
            {
                return Attention(
                    ObservationStage.PlaceTargetOnSlit,
                    "SLIT_RETURN_COMMAND_NOT_REACHED",
                    $"The return command stopped {commandResidual:F2} arcsec from its command. The actual position was persisted; no further automatic motion is permitted.");
            }
        }

        return Attention(
            ObservationStage.PlaceTargetOnSlit,
            "SLIT_RETURN_LOOP_LIMIT",
            "Durable slit recovery exhausted its finite return-loop bound; pending recovery remains on disk.");
    }

    private async Task<StageResult> PlaceTargetOnSlitAsync(ObservationContext context, CancellationToken cancellationToken)
    {
        var ownsRecoveryLock = slitPlacementRecoveryDepth.Value == 0;
        if (ownsRecoveryLock)
        {
            await slitPlacementRecoveryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        slitPlacementRecoveryDepth.Value++;
        var previousWorstCaseDuration = context.RemainingWorstCaseDuration;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var fineDeadline = (fineAcquisitionStartedUtc ?? now) +
                TimeSpan.FromMinutes(configuration.Commissioning.MaximumAcquisitionMinutes);
            var remainingFine = fineDeadline > now ? fineDeadline - now : TimeSpan.Zero;
            context.RemainingWorstCaseDuration =
                (previousWorstCaseDuration ?? context.Plan.PlannedDuration) + remainingFine;
            return await PlaceTargetOnSlitLockedAsync(context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            context.RemainingWorstCaseDuration = previousWorstCaseDuration;
            slitPlacementRecoveryDepth.Value--;
            if (ownsRecoveryLock) slitPlacementRecoveryLock.Release();
        }
    }

    private async Task<StageResult> PlaceTargetOnSlitLockedAsync(ObservationContext context, CancellationToken cancellationToken)
    {
        if (commissioning is null) return Attention(ObservationStage.PlaceTargetOnSlit, "COMMISSIONING_PRESET_REQUIRED", "Trusted slit geometry, transform and motion limits are not loaded.");
        var phd2Recovery = await RecoverOutstandingPhd2LockBeforePlacementAsync(context, cancellationToken).ConfigureAwait(false);
        if (phd2Recovery is not null) return phd2Recovery;
        if (lastG3Field?.TargetIdentification.Target is null || lastG3Field.Gate.Disposition != GateDisposition.Passed)
        {
            return Attention(ObservationStage.PlaceTargetOnSlit, "G3_TARGET_REQUIRED", "A quality-gated G3 target identification is required before movement.");
        }
        var entryFieldBinding = await ValidateG3FieldMountBindingForMotionAsync(
            context,
            lastG3Field,
            cancellationToken).ConfigureAwait(false);
        if (entryFieldBinding.Disposition != GateDisposition.Passed)
        {
            var stalePath = lastG3Field.FramePath;
            lastG3Field = null;
            return new StageResult(entryFieldBinding, stalePath);
        }
        if (commissioning.Value.FineMotionAuthority is RealSlitPlacementAuthority.Phd2CalibrationLockShift or RealSlitPlacementAuthority.AutoPreferPhd2ThenIndependent)
        {
            var phd2Result = await PlaceTargetOnSlitWithPhd2Async(context, cancellationToken).ConfigureAwait(false);
            if (phd2Result.CanAdvance ||
                commissioning.Value.FineMotionAuthority == RealSlitPlacementAuthority.Phd2CalibrationLockShift ||
                !CanUseIndependentFallbackAfterPhd2Preflight(phd2Result.Gate.Code))
            {
                return phd2Result;
            }
            await PublishRunJsonEvidenceAsync(
                "slit-placement-authority-selection",
                "PHD2 candidate rejected before guide/motion; selecting separately commissioned independent transform",
                new
                {
                    selectedAuthority = RealSlitPlacementAuthority.IndependentMountTransform.ToString(),
                    rejectedAuthority = RealSlitPlacementAuthority.Phd2CalibrationLockShift.ToString(),
                    rejectedCode = phd2Result.Gate.Code,
                    rejectedReason = phd2Result.Gate.Message,
                    independentTransformCalibrationId = commissioning.MountTransform.CalibrationId,
                    independentTransformPierSide = commissioning.MountTransform.PierSide,
                    qhyToG3OpticalOffsetUsed = false,
                    hardCodedBoresightOffsetUsed = false,
                },
                lastG3Field.FramePath,
                cancellationToken).ConfigureAwait(false);
        }
        while (true)
        {
            var current = lastG3Field;
            var target = current.TargetIdentification.Target!;
            var slit = current.SlitDetection.Geometry;
            var residualPixels = Distance(target.Centroid, slit.AcquisitionPoint);
            if (residualPixels <= configuration.Slit.PlacementTolerancePixels)
            {
                return Passed(
                    "TARGET_ON_SLIT",
                    $"Target/slit residual {residualPixels:F2}px is within {configuration.Slit.PlacementTolerancePixels:F2}px.",
                    new Dictionary<string, double>
                    {
                        ["slitResidualPixels"] = residualPixels,
                        ["cumulativeCorrectionArcseconds"] = cumulativeCorrectionDegrees * 3600,
                        ["correctionAttempts"] = correctionAttempts,
                    },
                    Metadata(commissioning));
            }

            var pierSide = telescopeMediator.GetInfo().SideOfPier.ToString();
            if (!IsKnownPierSide(pierSide) || !IsKnownPierSide(commissioning.MountTransform.PierSide))
            {
                return Attention(ObservationStage.PlaceTargetOnSlit, "TRANSFORM_PIER_SIDE_UNKNOWN", $"A known exact pier side is required for slit placement (current '{pierSide}', transform '{commissioning.MountTransform.PierSide}').");
            }
            if (!string.Equals(pierSide, commissioning.MountTransform.PierSide, StringComparison.OrdinalIgnoreCase))
            {
                return Attention(ObservationStage.PlaceTargetOnSlit, "TRANSFORM_PIER_SIDE_MISMATCH", $"Current pier side '{pierSide}' does not match transform '{commissioning.MountTransform.PierSide}'.");
            }
            var correction = SlitCorrectionCalculator.Calculate(
                target.Centroid,
                slit,
                commissioning.MountTransform,
                commissioning.MotionLimits,
                cumulativeCorrectionDegrees,
                correctionAttempts);
            if (correction.Gate.Disposition != GateDisposition.Passed) return new StageResult(correction.Gate);
            var budget = ValidateCorrectionBudget(commissioning.MotionLimits, correction.MagnitudeDegrees);
            if (budget.Disposition != GateDisposition.Passed) return new StageResult(budget);
            var returnReserve = SlitPlacementRecoveryPlanner.ValidateOutboundAndReturnReserve(
                commissioning.MotionLimits,
                cumulativeCorrectionDegrees,
                correctionAttempts,
                correction.MagnitudeDegrees);
            if (returnReserve.Disposition != GateDisposition.Passed) return new StageResult(returnReserve, current.FramePath);

            var preIntentFieldBinding = await ValidateG3FieldMountBindingForMotionAsync(
                context,
                current,
                cancellationToken).ConfigureAwait(false);
            if (preIntentFieldBinding.Disposition != GateDisposition.Passed)
            {
                lastG3Field = null;
                return new StageResult(preIntentFieldBinding, current.FramePath);
            }

            var currentCoordinates = telescopeMediator.GetCurrentPosition();
            try { EnsureFiniteReportedCoordinates(currentCoordinates); }
            catch (Exception ex)
            {
                return Attention(ObservationStage.PlaceTargetOnSlit, "SLIT_PREMOVE_POSITION_UNKNOWN", ex.Message);
            }
            var correctedCoordinates = ApplySkyCorrection(
                currentCoordinates,
                correction.DeltaRaArcseconds,
                correction.DeltaDecArcseconds);
            slitPlacementBudgetLineageId ??= Guid.NewGuid().ToString("N");
            var moveIntentEvidencePath = await PublishRunJsonEvidenceAsync(
                "slit-placement-segment-intent",
                $"Closed-loop slit-placement segment {correctionAttempts + 1}",
                new
                {
                    transformCalibrationId = commissioning.MountTransform.CalibrationId,
                    commissioningPresetSha256 = commissioning.Sha256,
                    configuration.ActionConfigurationSha256,
                    budgetLineageId = slitPlacementBudgetLineageId,
                    recoveryContextSha256 = ComputeSlitRecoveryContextSha256(context),
                    priorReportedRaDegrees = currentCoordinates.RADegrees,
                    priorReportedDecDegrees = currentCoordinates.Dec,
                    commandedRaDegrees = correctedCoordinates.RADegrees,
                    commandedDecDegrees = correctedCoordinates.Dec,
                    commandMagnitudeArcseconds = correction.MagnitudeDegrees * 3600,
                    requestedFullMagnitudeArcseconds = correction.RequestedMagnitudeDegrees * 3600,
                    residualPixels,
                    cumulativeBeforeArcseconds = cumulativeCorrectionDegrees * 3600,
                    attemptsBefore = correctionAttempts,
                    failureReturnReserved = true,
                },
                current.FramePath,
                cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var pendingPath = SlitPlacementPendingPath(context.Plan.ObservationRunId);
            var segmentPending = new SlitPlacementPendingState(
                SlitPlacementPendingState.CurrentSchemaVersion,
                context.Plan.ObservationRunId,
                slitPlacementBudgetLineageId,
                configuration.ActionConfigurationSha256,
                ComputeSlitRecoveryContextSha256(context),
                commissioning.Sha256,
                commissioning.MountTransform.CalibrationId,
                commissioning.MountTransform.PierSide,
                currentCoordinates.Epoch.ToString(),
                currentCoordinates.RADegrees,
                currentCoordinates.Dec,
                currentCoordinates.RADegrees,
                currentCoordinates.Dec,
                correctedCoordinates.RADegrees,
                correctedCoordinates.Dec,
                correction.MagnitudeDegrees,
                residualPixels,
                commissioning.MotionLimits.MaximumSingleCorrectionDegrees,
                commissioning.MotionLimits.MaximumCumulativeCorrectionDegrees,
                commissioning.MotionLimits.MaximumCorrectionAttempts,
                commissioning.MotionLimits.EffectiveMaximumAcquisitionTime.TotalSeconds,
                cumulativeCorrectionDegrees + correction.MagnitudeDegrees,
                correctionAttempts + 1,
                fineAcquisitionStartedUtc ?? now,
                now,
                now,
                SlitPlacementPendingPhase.MoveIntent,
                moveIntentEvidencePath,
                "Outbound segment has not yet produced a fresh validated G3 field.");
            await SlitPlacementPendingStore.WriteAtomicAsync(pendingPath, segmentPending, cancellationToken).ConfigureAwait(false);
            pendingSlitPlacement = segmentPending;

            try
            {
                await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
                var commandHorizon = ValidateCommandCoordinateHorizon(context, correctedCoordinates, "segmented slit-placement move");
                if (commandHorizon.Disposition != GateDisposition.Passed)
                {
                    var returned = await ReturnPendingSlitPlacementLockedAsync(context, segmentPending, commandHorizon.Message, cancellationToken).ConfigureAwait(false);
                    return returned.CanAdvance ? new StageResult(commandHorizon, current.FramePath) : returned;
                }
                await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
                var mountGate = ValidateG3SearchMountState(commissioning.MountTransform.PierSide);
                if (mountGate.Disposition != GateDisposition.Passed)
                {
                    var returned = await ReturnPendingSlitPlacementLockedAsync(context, segmentPending, mountGate.Message, cancellationToken).ConfigureAwait(false);
                    return returned.CanAdvance ? new StageResult(mountGate, current.FramePath) : returned;
                }
                commandHorizon = ValidateCommandCoordinateHorizon(context, correctedCoordinates, "segmented slit-placement move immediately before command");
                if (commandHorizon.Disposition != GateDisposition.Passed)
                {
                    var returned = await ReturnPendingSlitPlacementLockedAsync(context, segmentPending, commandHorizon.Message, cancellationToken).ConfigureAwait(false);
                    return returned.CanAdvance ? new StageResult(commandHorizon, current.FramePath) : returned;
                }
                var immediatelyBeforeOutbound = telescopeMediator.GetCurrentPosition();
                EnsureFiniteReportedCoordinates(immediatelyBeforeOutbound);
                var preDispatchFieldBinding = await ValidateG3FieldMountBindingForMotionAsync(
                    context,
                    current,
                    cancellationToken).ConfigureAwait(false);
                if (preDispatchFieldBinding.Disposition != GateDisposition.Passed)
                {
                    var returned = await ReturnPendingSlitPlacementLockedAsync(
                        context,
                        segmentPending,
                        $"G3 field mount binding became stale after intent: {preDispatchFieldBinding.Code}: {preDispatchFieldBinding.Message}",
                        cancellationToken).ConfigureAwait(false);
                    lastG3Field = null;
                    return returned.CanAdvance ? new StageResult(preDispatchFieldBinding, current.FramePath) : returned;
                }
                if (!string.Equals(segmentPending.CoordinateEpoch, immediatelyBeforeOutbound.Epoch.ToString(), StringComparison.Ordinal))
                {
                    segmentPending = segmentPending with
                    {
                        PriorReportedRaDegrees = immediatelyBeforeOutbound.RADegrees,
                        PriorReportedDeclinationDegrees = immediatelyBeforeOutbound.Dec,
                        UpdatedUtc = DateTimeOffset.UtcNow,
                        Phase = SlitPlacementPendingPhase.ReturnRequired,
                        LastReason = "Mount epoch changed after outbound intent; stale absolute command was not sent.",
                    };
                    await SlitPlacementPendingStore.WriteAtomicAsync(pendingPath, segmentPending, CancellationToken.None).ConfigureAwait(false);
                    pendingSlitPlacement = segmentPending;
                    return Attention(
                        ObservationStage.PlaceTargetOnSlit,
                        "SLIT_CORRECTION_PRECOMMAND_EPOCH_CHANGED",
                        $"Mount epoch changed from durable '{segmentPending.CoordinateEpoch}' to '{immediatelyBeforeOutbound.Epoch}' after the outbound intent. Resume recovery or explicit takeover is required.");
                }
                var outboundPrecommandDriftArcseconds = AngularSeparationArcseconds(immediatelyBeforeOutbound, currentCoordinates);
                if (!double.IsFinite(outboundPrecommandDriftArcseconds) ||
                    outboundPrecommandDriftArcseconds > MountCommandArrivalToleranceArcseconds)
                {
                    var returned = await ReturnPendingSlitPlacementLockedAsync(
                        context,
                        segmentPending,
                        $"Reported position changed {outboundPrecommandDriftArcseconds:F2} arcsec after outbound intent; stale command withheld.",
                        cancellationToken).ConfigureAwait(false);
                    return returned.CanAdvance
                        ? Attention(
                            ObservationStage.PlaceTargetOnSlit,
                            "SLIT_CORRECTION_PRECOMMAND_POSITION_CHANGED",
                            $"The mount changed {outboundPrecommandDriftArcseconds:F2} arcsec after the durable outbound intent and was returned to the saved segment origin; a fresh G3 field is required.")
                        : returned;
                }
                mountGate = ValidateG3SearchMountState(commissioning.MountTransform.PierSide);
                if (mountGate.Disposition != GateDisposition.Passed)
                {
                    var returned = await ReturnPendingSlitPlacementLockedAsync(context, segmentPending, mountGate.Message, cancellationToken).ConfigureAwait(false);
                    return returned.CanAdvance ? new StageResult(mountGate, current.FramePath) : returned;
                }
                Report(correction.IsSegmented
                    ? $"入缝闭环分段 {correction.MagnitudeDegrees * 3600:F2}/{correction.RequestedMagnitudeDegrees * 3600:F2} arcsec；本段 RA {correction.DeltaRaArcseconds:F2}\" / Dec {correction.DeltaDecArcseconds:F2}\"，完成后重新测量"
                    : $"入缝有界修正 RA {correction.DeltaRaArcseconds:F2}\" / Dec {correction.DeltaDecArcseconds:F2}\"");
                if (!await telescopeMediator.SlewToCoordinatesAsync(correctedCoordinates, cancellationToken).ConfigureAwait(false))
                {
                    var returned = await ReturnPendingSlitPlacementLockedAsync(context, segmentPending, "N.I.N.A. rejected the outbound slit segment.", cancellationToken).ConfigureAwait(false);
                    return returned.CanAdvance
                        ? Failed(ObservationStage.PlaceTargetOnSlit, "SLIT_CORRECTION_REJECTED", "N.I.N.A. rejected the bounded slit correction; the reported position is at the saved segment origin.")
                        : returned;
                }
                await telescopeMediator.WaitForSlew(cancellationToken).ConfigureAwait(false);
                var reportedCoordinates = telescopeMediator.GetCurrentPosition();
                EnsureFiniteReportedCoordinates(reportedCoordinates);
                if (!string.Equals(segmentPending.CoordinateEpoch, reportedCoordinates.Epoch.ToString(), StringComparison.Ordinal))
                {
                    segmentPending = segmentPending with
                    {
                        PriorReportedRaDegrees = reportedCoordinates.RADegrees,
                        PriorReportedDeclinationDegrees = reportedCoordinates.Dec,
                        UpdatedUtc = DateTimeOffset.UtcNow,
                        Phase = SlitPlacementPendingPhase.ReturnRequired,
                        LastReason = "Mount epoch changed after outbound command; no command residual was accepted.",
                    };
                    await SlitPlacementPendingStore.WriteAtomicAsync(pendingPath, segmentPending, CancellationToken.None).ConfigureAwait(false);
                    pendingSlitPlacement = segmentPending;
                    return Attention(
                        ObservationStage.PlaceTargetOnSlit,
                        "SLIT_CORRECTION_POSTCOMMAND_EPOCH_CHANGED",
                        $"Mount epoch changed from durable '{segmentPending.CoordinateEpoch}' to '{reportedCoordinates.Epoch}' after the command. The actual coordinates remain durable and no settled ledger was accepted.");
                }
                var commandResidualArcseconds = AngularSeparationArcseconds(reportedCoordinates, correctedCoordinates);
                segmentPending = segmentPending with
                {
                    PriorReportedRaDegrees = reportedCoordinates.RADegrees,
                    PriorReportedDeclinationDegrees = reportedCoordinates.Dec,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    Phase = SlitPlacementPendingPhase.AwaitingFreshField,
                    LastReason = $"Outbound command residual {commandResidualArcseconds:F2} arcsec; awaiting fresh G3 field.",
                };
                await SlitPlacementPendingStore.WriteAtomicAsync(pendingPath, segmentPending, CancellationToken.None).ConfigureAwait(false);
                pendingSlitPlacement = segmentPending;
                cumulativeCorrectionDegrees = Math.Max(cumulativeCorrectionDegrees, segmentPending.CumulativeCorrectionDegrees);
                correctionAttempts = Math.Max(correctionAttempts, segmentPending.CorrectionAttempts);
                if (!double.IsFinite(commandResidualArcseconds) ||
                    commandResidualArcseconds > MountCommandArrivalToleranceArcseconds)
                {
                    var returned = await ReturnPendingSlitPlacementLockedAsync(
                        context,
                        segmentPending,
                        $"Outbound command residual {commandResidualArcseconds:F2} arcsec exceeded tolerance.",
                        cancellationToken).ConfigureAwait(false);
                    return returned.CanAdvance
                        ? Attention(ObservationStage.PlaceTargetOnSlit, "SLIT_CORRECTION_COMMAND_NOT_REACHED", $"The mount stopped {commandResidualArcseconds:F2} arcsec from the bounded slit segment and was returned to its reported pre-segment origin.")
                        : returned;
                }

                lastG3Field = await CaptureAndAnalyzeG3Async(context, cancellationToken).ConfigureAwait(false);
                if (lastG3Field.Gate.Disposition != GateDisposition.Passed)
                {
                    var failedField = lastG3Field;
                    var returned = await ReturnPendingSlitPlacementLockedAsync(
                        context,
                        segmentPending,
                        $"Fresh G3 validation failed: {failedField.Gate.Code}: {failedField.Gate.Message}",
                        cancellationToken).ConfigureAwait(false);
                    lastG3Field = null;
                    return returned.CanAdvance ? new StageResult(failedField.Gate, failedField.FramePath) : returned;
                }
                var measuredResidual = Distance(
                    lastG3Field.TargetIdentification.Target!.Centroid,
                    lastG3Field.SlitDetection.Geometry.AcquisitionPoint);
                if (measuredResidual >= residualPixels - 0.25)
                {
                    var grossWorsening = measuredResidual > residualPixels * 1.25;
                    GateResult? durableInvalidation = null;
                    if (grossWorsening)
                    {
                        var invalidReason =
                            $"A commissioned pixel-to-mount correction worsened slit residual from {residualPixels:F2}px to {measuredResidual:F2}px.";
                        segmentPending = segmentPending with
                        {
                            Phase = SlitPlacementPendingPhase.ReturnRequired,
                            TransformInvalidated = true,
                            TransformInvalidReason = invalidReason,
                            UpdatedUtc = DateTimeOffset.UtcNow,
                            LastReason = $"{invalidReason} Saved-origin return is required; the transform is quarantined independently of return success.",
                        };
                        await SlitPlacementPendingStore.WriteAtomicAsync(pendingPath, segmentPending, CancellationToken.None).ConfigureAwait(false);
                        pendingSlitPlacement = segmentPending;
                        durableInvalidation = await InvalidateCommissioningAsync(
                            "COMMISSIONING_MOUNT_TRANSFORM_INVALID",
                            invalidReason).ConfigureAwait(false);
                    }
                    var returned = await ReturnPendingSlitPlacementLockedAsync(
                        context,
                        segmentPending,
                        $"Fresh G3 response did not prove improvement ({residualPixels:F2}px to {measuredResidual:F2}px).",
                        cancellationToken).ConfigureAwait(false);
                    lastG3Field = null;
                    if (!returned.CanAdvance) return returned;
                    if (grossWorsening)
                    {
                        return new StageResult(durableInvalidation!, returned.EvidencePath);
                    }
                    return Attention(
                        ObservationStage.PlaceTargetOnSlit,
                        "SLIT_CORRECTION_NO_PROVEN_IMPROVEMENT",
                        $"Fresh G3 residual changed from {residualPixels:F2}px to {measuredResidual:F2}px; the mount was returned to the pre-segment origin and automation paused.");
                }

                var validatedEvidencePath = await PublishRunJsonEvidenceAsync(
                    "slit-placement-segment-validated",
                    "Fresh G3 field validated one closed-loop slit-placement segment",
                    new
                    {
                        moveIntentEvidencePath,
                        priorResidualPixels = residualPixels,
                        measuredResidualPixels = measuredResidual,
                        commandResidualArcseconds,
                        cumulativeCorrectionArcseconds = cumulativeCorrectionDegrees * 3600,
                        correctionAttempts,
                    },
                    lastG3Field.FramePath,
                    cancellationToken).ConfigureAwait(false);
                segmentPending = segmentPending with
                {
                    Phase = SlitPlacementPendingPhase.SettledBudgetLedger,
                    PriorReportedRaDegrees = reportedCoordinates.RADegrees,
                    PriorReportedDeclinationDegrees = reportedCoordinates.Dec,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    LastReason = $"Fresh G3 residual improved from {residualPixels:F2}px to {measuredResidual:F2}px; evidence {validatedEvidencePath}.",
                };
                await SlitPlacementPendingStore.WriteAtomicAsync(pendingPath, segmentPending, CancellationToken.None).ConfigureAwait(false);
                pendingSlitPlacement = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                segmentPending = (pendingSlitPlacement ?? segmentPending) with
                {
                    Phase = SlitPlacementPendingPhase.ReturnRequired,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    LastReason = "User cancellation retained the durable pre-segment origin; no automatic mount-return command was sent.",
                };
                await SlitPlacementPendingStore.WriteAtomicAsync(pendingPath, segmentPending, CancellationToken.None).ConfigureAwait(false);
                pendingSlitPlacement = segmentPending;
                throw;
            }
            catch (ResumeStageRestartException)
            {
                // A cooperative pause/takeover may have returned this segment
                // re-entrantly while the stage was stopped at a checkpoint.
                // Let the outer execution loop reload the durable ledger before
                // reacquiring G3 evidence; never reuse this stale local stack.
                throw;
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested)
            {
                segmentPending = (pendingSlitPlacement ?? segmentPending) with
                {
                    Phase = SlitPlacementPendingPhase.ReturnRequired,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    LastReason = $"Cancellation was observed through a non-cancellation exception ({ex.Message}); no automatic mount-return command was sent.",
                };
                await SlitPlacementPendingStore.WriteAtomicAsync(pendingPath, segmentPending, CancellationToken.None).ConfigureAwait(false);
                pendingSlitPlacement = segmentPending;
                throw new OperationCanceledException(
                    "Independent slit placement was cancelled; durable return-required state was retained without issuing a recovery command.",
                    ex,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                var returned = await ReturnPendingSlitPlacementLockedAsync(
                    context,
                    pendingSlitPlacement ?? segmentPending,
                    $"Slit-placement exception: {ex.Message}",
                    cancellationToken,
                    lifecycleRecovery: true).ConfigureAwait(false);
                lastG3Field = null;
                return returned.CanAdvance
                    ? Attention(ObservationStage.PlaceTargetOnSlit, "SLIT_SEGMENT_EXCEPTION_RETURNED", $"The segment failed: {ex.Message}. The mount returned to its reported pre-segment origin.")
                    : returned;
            }
        }
    }

    private async Task<StageResult> StartGuidingAsync(ObservationContext context, CancellationToken cancellationToken)
    {
        var interlock = await EvaluateInterlocksAsync(context, connectQhy: false, cancellationToken).ConfigureAwait(false);
        if (interlock.Disposition != GateDisposition.Passed) return new StageResult(interlock);
        if (phd2SlitPlacementSession is { } placedSession)
        {
            return ReusePhd2SlitPlacementGuiding(placedSession);
        }
        if (commissioning?.Value.FineMotionAuthority == RealSlitPlacementAuthority.Phd2CalibrationLockShift)
        {
            return Attention(
                ObservationStage.StartGuiding,
                "PHD2_PLACEMENT_GUIDE_SESSION_REQUIRED",
                "The selected PHD2 fine-motion authority did not leave a current operation-bound settled guide session. StartGuiding will not silently restart or switch authority.");
        }
        if (commissioning?.Value.FineMotionAuthority is
            RealSlitPlacementAuthority.AutoPreferPhd2ThenIndependent or
            RealSlitPlacementAuthority.IndependentMountTransform)
        {
            return await StartGradedPhd2GuidingAfterIndependentPlacementAsync(context, cancellationToken).ConfigureAwait(false);
        }
        if (lastG3Field?.TargetIdentification.Target is null || lastG3Field.Gate.Disposition != GateDisposition.Passed)
        {
            return Attention(ObservationStage.StartGuiding, "G3_FIELD_REQUIRED", "A current quality-gated G3 slit field is required before guide-star selection.");
        }
        var selection = GuideStarSelector.Select(
            lastG3Field.Candidates,
            lastG3Field.SlitDetection.Geometry,
            lastG3Field.TargetIdentification.Target.Centroid);
        if (selection.Gate.Disposition != GateDisposition.Passed || selection.Star is null) return new StageResult(selection.Gate);

        await CheckpointAndRejectStaleStageStackAsync(context, cancellationToken).ConfigureAwait(false);
        await EnsurePhdConnectedAsync(cancellationToken).ConfigureAwait(false);
        var selectionLoop = await phd2.StartLoopingAndWaitForFreshFrameAsync(
            new Phd2LoopingStartRequest(TimeSpan.FromSeconds(configuration.Phd2.SettleTimeoutSeconds)),
            cancellationToken).ConfigureAwait(false);
        if (!selectionLoop.LeavesLoopingForGuideTakeover || selectionLoop.StopCommandSent || selectionLoop.ExposureChanged)
        {
            return Attention(
                ObservationStage.StartGuiding,
                "PHD2_FULL_FRAME_SELECTION_CONTRACT_FAILED",
                "PHD2 did not retain the fresh full-frame Looping state required for normal guide-star takeover; no selection was sent.");
        }
        var selected = await phd2.SelectGuideStarAsync(
            new Phd2Point(selection.Star.Centroid.X, selection.Star.Centroid.Y),
            cancellationToken).ConfigureAwait(false);
        if (lastG3Field.Image is not null)
        {
            PublishG3Preview(
                lastG3Field.Image,
                $"G3 目标/狭缝已确认；选择导星星 ({selected.X:F1},{selected.Y:F1})。",
                lastG3Field.SlitDetection.Geometry,
                lastG3Field.TargetIdentification.Target.Centroid,
                new PixelPoint(selected.X, selected.Y));
        }
        await PublishGuideSelectionEvidenceAsync(
            context,
            lastG3Field,
            selection,
            selected,
            "initial-guide-selection",
            cancellationToken).ConfigureAwait(false);
        var calibrationBefore = await phd2.ValidateCalibrationAsync(
            PhdCalibrationRequirement(),
            cancellationToken).ConfigureAwait(false);
        var forceRecalibration = calibrationBefore.Status != Phd2ValidationStatus.Valid;
        var recalibrationStartedUtc = DateTimeOffset.UtcNow;
        await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
        Report(forceRecalibration
            ? "旧 PHD2 校准未通过，自动强制重标定并等待稳定"
            : "PHD2 使用已验证校准启动导星并等待稳定");
        Volatile.Write(ref phd2GuidingEverStarted, 1);
        var settle = await phd2.GuideAndSettleAsync(
            new Phd2SettleCriteria(
                configuration.Phd2.SettlePixels,
                configuration.Phd2.SettleStableSeconds,
                configuration.Phd2.SettleTimeoutSeconds),
            forceRecalibration,
            cancellationToken).ConfigureAwait(false);
        if (!settle.Succeeded)
        {
            return Failed(ObservationStage.StartGuiding, "PHD2_SETTLE_FAILED", settle.Error ?? "PHD2 did not settle.");
        }
        if (forceRecalibration)
        {
            var calibrationAfter = await phd2.ValidateCalibrationAsync(
                PhdCalibrationRequirement(recalibrationStartedUtc),
                cancellationToken).ConfigureAwait(false);
            if (calibrationAfter.Status != Phd2ValidationStatus.Valid)
            {
                var reasons = calibrationAfter.Failures.Concat(calibrationAfter.IndeterminateReasons);
                return Attention(
                    ObservationStage.StartGuiding,
                    "PHD2_RECALIBRATION_INVALID",
                    $"PHD2 forced recalibration completed but immediate validation did not pass: {string.Join(" ", reasons)}");
            }
            await WriteAuditBestEffortAsync("phd2-forced-recalibration", new
            {
                previousStatus = calibrationBefore.Status.ToString(),
                previousReasons = calibrationBefore.Failures.Concat(calibrationBefore.IndeterminateReasons).ToArray(),
                recalibrationStartedUtc,
                calibrationAfter.EvaluatedUtc,
                calibrationAfter.OrthogonalityErrorDegrees,
                calibrationAfter.Calibration.RaRatePixelsPerSecond,
                calibrationAfter.Calibration.DecRatePixelsPerSecond,
            }).ConfigureAwait(false);

            // The calibration moved the mount. Stop guiding in a checked state,
            // acquire a new immutable G3 full frame, and prove the target still
            // lies on the commissioned slit before any QHY/ATR stage may run.
            await CheckpointAndRejectStaleStageStackAsync(context, cancellationToken).ConfigureAwait(false);
            await StopPhdAndWaitAsync(cancellationToken).ConfigureAwait(false);
            var postCalibrationField = await CaptureAndAnalyzeG3Async(context, cancellationToken).ConfigureAwait(false);
            lastG3Field = postCalibrationField;
            if (postCalibrationField.Gate.Disposition != GateDisposition.Passed || postCalibrationField.TargetIdentification.Target is null)
            {
                return Attention(
                    ObservationStage.StartGuiding,
                    "POST_CALIBRATION_G3_INVALID",
                    $"PHD2 recalibration passed, but the mandatory post-calibration G3 field did not: {postCalibrationField.Gate.Message}");
            }
            var postCalibrationSlitResidual = Distance(
                postCalibrationField.TargetIdentification.Target.Centroid,
                postCalibrationField.SlitDetection.Geometry.AcquisitionPoint);
            if (postCalibrationSlitResidual > configuration.Slit.PlacementTolerancePixels)
            {
                Report($"PHD2 重标定后目标偏离狭缝 {postCalibrationSlitResidual:F2}px，自动进入同一有界入缝修正循环");
                var replacement = await PlaceTargetOnSlitAsync(context, cancellationToken).ConfigureAwait(false);
                if (!replacement.CanAdvance) return replacement;
                postCalibrationField = lastG3Field!;
            }

            if (postCalibrationField.TargetIdentification.Target is not { } postCalibrationTarget)
            {
                return Attention(
                    ObservationStage.StartGuiding,
                    "POST_CALIBRATION_TARGET_IDENTITY_LOST",
                    "The bounded post-calibration placement loop no longer has a quality-gated target identity.");
            }
            var refreshedSelection = GuideStarSelector.Select(
                postCalibrationField.Candidates,
                postCalibrationField.SlitDetection.Geometry,
                postCalibrationTarget.Centroid);
            if (refreshedSelection.Gate.Disposition != GateDisposition.Passed || refreshedSelection.Star is null)
            {
                return new StageResult(refreshedSelection.Gate, postCalibrationField.FramePath);
            }
            await CheckpointAndRejectStaleStageStackAsync(context, cancellationToken).ConfigureAwait(false);
            selected = await phd2.SelectGuideStarAsync(
                new Phd2Point(refreshedSelection.Star.Centroid.X, refreshedSelection.Star.Centroid.Y),
                cancellationToken).ConfigureAwait(false);
            if (postCalibrationField.Image is not null)
            {
                PublishG3Preview(
                    postCalibrationField.Image,
                    $"重标定后重新入缝并选择导星星 ({selected.X:F1},{selected.Y:F1})。",
                    postCalibrationField.SlitDetection.Geometry,
                    postCalibrationField.TargetIdentification.Target.Centroid,
                    new PixelPoint(selected.X, selected.Y));
            }
            await PublishGuideSelectionEvidenceAsync(
                context,
                postCalibrationField,
                refreshedSelection,
                selected,
                "post-calibration-guide-selection",
                cancellationToken).ConfigureAwait(false);
            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref phd2GuidingEverStarted, 1);
            settle = await phd2.GuideAndSettleAsync(
                new Phd2SettleCriteria(
                    configuration.Phd2.SettlePixels,
                    configuration.Phd2.SettleStableSeconds,
                    configuration.Phd2.SettleTimeoutSeconds),
                forceRecalibration: false,
                cancellationToken).ConfigureAwait(false);
            if (!settle.Succeeded)
            {
                return Failed(ObservationStage.StartGuiding, "PHD2_POST_CALIBRATION_SETTLE_FAILED", settle.Error ?? "PHD2 did not settle after the mandatory post-calibration G3 verification.");
            }
        }
        var guideProof = phd2.Snapshot;
        if (!guideProof.HasCurrentSuccessfulSettle)
        {
            return Attention(
                ObservationStage.StartGuiding,
                "PHD2_GUIDE_EPOCH_UNATTESTED",
                "PHD2 reported settle completion, but the current connection/guide epoch is not attested as settled.");
        }
        validatedG3GuideConnectionEpoch = guideProof.ConnectionEpoch;
        validatedG3GuideEpoch = guideProof.GuideEpoch;
        return Passed(
            "PHD2_GUIDING_STABLE",
            $"PHD2 selected off-slit star ({selected.X:F1},{selected.Y:F1}) and settled with {settle.DroppedFrames}/{settle.TotalFrames} dropped frames.",
            new Dictionary<string, double>
            {
                ["guideStarX"] = selected.X,
                ["guideStarY"] = selected.Y,
                ["settleFrames"] = settle.TotalFrames,
                ["settleDroppedFrames"] = settle.DroppedFrames,
                ["forcedRecalibration"] = forceRecalibration ? 1 : 0,
            });
    }

    private async Task<StageResult> StartQhyPhotometryAsync(ObservationContext context, CancellationToken cancellationToken)
    {
        if (!IsGuidingStable())
        {
            return Attention(ObservationStage.StartQhyPhotometry, "GUIDING_NOT_STABLE", "PHD2 is not in a verified settled-guiding state.");
        }
        if (photometryJobId is { } existing)
        {
            var current = await qhy.GetJobAsync(existing, cancellationToken).ConfigureAwait(false);
            if (current is null) return Attention(ObservationStage.StartQhyPhotometry, "QHY_PHOTOMETRY_JOB_LOST", $"QHY job {existing:D} disappeared.");
            ObserveQhySnapshot(current);
            if (current.State == QhyJobState.PausedNeedsAttention)
            {
                return Attention(ObservationStage.StartQhyPhotometry, "QHY_PHOTOMETRY_NEEDS_ATTENTION", current.AttentionReason ?? "QHY photometry needs attention.");
            }
            if (current.State is QhyJobState.Running or QhyJobState.Queued)
            {
                return Passed("QHY_PHOTOMETRY_RUNNING", $"Existing QHY photometry job {existing:D} is {current.State}.");
            }
            if (current.State is QhyJobState.Pausing or QhyJobState.Paused)
            {
                return Attention(ObservationStage.StartQhyPhotometry, "QHY_PHOTOMETRY_PAUSED", $"QHY photometry job {existing:D} is {current.State}; it cannot cover new ATR frames until it is resumed after guide recovery.");
            }
            if (current.State == QhyJobState.Completed)
            {
                activeQhyJobs.TryRemove(existing, out _);
                photometryJobId = null;
                qhyPhotometryAttempt++;
                return await StartQhyPhotometryAsync(context, cancellationToken).ConfigureAwait(false);
            }
            if (current.State is QhyJobState.Cancelled or QhyJobState.Faulted or QhyJobState.TakenOver)
            {
                activeQhyJobs.TryRemove(existing, out _);
                photometryJobId = null;
                qhyPhotometryAttempt++;
                return await StartQhyPhotometryAsync(context, cancellationToken).ConfigureAwait(false);
            }
            return Failed(ObservationStage.StartQhyPhotometry, "QHY_PHOTOMETRY_NOT_RUNNING", current.Error ?? $"Existing QHY photometry job is {current.State}.");
        }

        var camera = await ConnectQhyAtCheckpointAsync(context, cancellationToken).ConfigureAwait(false);
        if (camera.Identity is null || !string.Equals(camera.Identity.StableId, context.Plan.ExpectedQhyCameraId, StringComparison.Ordinal))
        {
            return Failed(ObservationStage.StartQhyPhotometry, "QHY_IDENTITY_CHANGED", "QHY identity changed before photometry.");
        }
        var cadence = Math.Max(configuration.Qhy.PhotometryExposureSeconds, configuration.Qhy.PhotometryCadenceSeconds);
        var count = Math.Max(1, (int)Math.Ceiling(context.Plan.PlannedDuration.TotalSeconds / cadence));
        await CheckpointAndRejectStaleStageStackAsync(context, cancellationToken).ConfigureAwait(false);
        var clientRequestId = $"{context.Plan.ObservationRunId}:qhy-photometry:{qhyPhotometryAttempt}";
        var request = new PhotometryJobRequest(
            context.Plan.ObservationRunId,
            context.Plan.Target.Name,
            configuration.Qhy.PhotometryExposureSeconds,
            configuration.Qhy.Gain,
            configuration.Qhy.Offset,
            count,
            cadence,
            BinningX: configuration.Qhy.Binning,
            BinningY: configuration.Qhy.Binning,
            ReadoutMode: configuration.Qhy.ReadoutMode,
            FilterName: configuration.Qhy.FilterName,
            TargetTemperatureC: configuration.Qhy.TargetTemperatureC,
            PauseOnQualityFailure: true,
            QualityThresholds: configuration.Qhy.QualityThresholds,
            RoiX: configuration.Qhy.RoiX,
            RoiY: configuration.Qhy.RoiY,
            RoiWidth: configuration.Qhy.RoiWidth,
            RoiHeight: configuration.Qhy.RoiHeight,
            ClientRequestId: clientRequestId,
            TargetRightAscensionDegrees: context.Plan.Target.RightAscensionDegrees,
            TargetDeclinationDegrees: context.Plan.Target.DeclinationDegrees,
            CoordinateEpoch: "ICRS",
            ControlLeaseSeconds: 120);
        await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
        pendingQhyRequests[clientRequestId] = new PendingQhyRequest(context.Plan.ObservationRunId, QhyJobKind.Photometry, clientRequestId, request);
        var started = await qhy.StartOrAdoptPhotometryAsync(request, cancellationToken).ConfigureAwait(false);
        photometryJobId = started.Id;
        RegisterActiveQhyJob(started);
        pendingQhyRequests.TryRemove(clientRequestId, out _);
        var observed = await qhy.WaitForFirstFrameOrTerminalAsync(
            started.Id,
            snapshot => PublishQhyPreviewAsync(snapshot, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (observed.State == QhyJobState.PausedNeedsAttention)
        {
            return Attention(ObservationStage.StartQhyPhotometry, "QHY_PHOTOMETRY_NEEDS_ATTENTION", observed.AttentionReason ?? "First QHY photometry frame failed its quality gate.");
        }
        if (observed.State is QhyJobState.Faulted or QhyJobState.Cancelled or QhyJobState.TakenOver)
        {
            return Failed(ObservationStage.StartQhyPhotometry, "QHY_PHOTOMETRY_START_FAILED", observed.Error ?? $"QHY photometry entered {observed.State}.");
        }
        return Passed(
            "QHY_PHOTOMETRY_STARTED",
            $"QHY synchronized photometry job {started.Id:D} started; {observed.Frames.Count} frame(s) are currently immutable on disk.",
            new Dictionary<string, double>
            {
                ["requestedFrames"] = count,
                ["exposureSeconds"] = configuration.Qhy.PhotometryExposureSeconds,
                ["cadenceSeconds"] = cadence,
            });
    }

    private async Task<StageResult> SelectAtrExposureAsync(ObservationContext context, CancellationToken cancellationToken)
    {
        var identity = ValidateAtrCameraIdentity(context.Plan.ExpectedAtrCameraId);
        if (identity.Disposition != GateDisposition.Passed) return new StageResult(identity);
        if (!IsGuidingStable()) return Attention(ObservationStage.SelectAtrExposure, "GUIDING_UNSTABLE", "PHD2 is not in the settled-guiding state required for a probe exposure.");
        var ladder = configuration.Atr.ExposureLadderSeconds;
        if (ladder.Count == 0) return Attention(ObservationStage.SelectAtrExposure, "ATR_EXPOSURE_LADDER_EMPTY", "ATR exposure ladder contains no positive tier.");
        if (!ladder.Any(value => Math.Abs(value - configuration.Atr.ProbeExposureSeconds) < 1e-9))
        {
            return Attention(ObservationStage.SelectAtrExposure, "ATR_PROBE_NOT_IN_LADDER", "ATR probe exposure must be one of the configured tiers.");
        }

        var coverGate = await EnsureOpticalCoverOpenAsync(context, cancellationToken).ConfigureAwait(false);
        if (coverGate.Disposition != GateDisposition.Passed) return new StageResult(coverGate);
        await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
        attemptedAtrProbeFrames++;
        PublishFrameCounters();
        var probe = await CaptureAtrImageAsync(
            context,
            configuration.Atr.ProbeExposureSeconds,
            CaptureSequence.ImageTypes.SNAPSHOT,
            "UVEX-ADV spectral exposure probe",
            cancellationToken).ConfigureAwait(false);
        PublishAtrPreview(probe.Image, $"ATR probe {configuration.Atr.ProbeExposureSeconds:G4}s · p99.9 {probe.Metrics.HighPercentileAdu:F0} ADU · saturation {probe.Metrics.SaturatedFraction:P3} · SNR {probe.Metrics.LineSnrPerResolutionElement:F1}");
        var decision = ExposureTierSelector.Select(
            probe.Metrics,
            new ExposureTierOptions(ladder));
        await SaveAtrImageAsync(
            probe,
            attemptedAtrProbeFrames,
            decision.Accepted,
            decision.Accepted ? GateDisposition.Passed : GateDisposition.Indeterminate,
            decision.Code,
            decision.Reason,
            cancellationToken).ConfigureAwait(false);
        retainedAtrProbeFrames++;
        if (decision.Accepted) acceptedAtrProbeFrames++;
        PublishFrameCounters();
        if (!decision.Accepted)
        {
            return Attention(ObservationStage.SelectAtrExposure, decision.Code, decision.Reason, decision.Metrics);
        }
        selectedAtrExposureSeconds = decision.SelectedExposureSeconds;
        atrReprobeRequired = false;
        context.Set("atrSelectedExposureSeconds", decision.SelectedExposureSeconds);
        UpdateRemainingScienceDuration(context, decision.SelectedExposureSeconds);
        return Passed(decision.Code, decision.Reason, decision.Metrics);
    }

    private async Task<StageResult> RunScienceBlockAsync(ObservationContext context, CancellationToken cancellationToken)
    {
        if (atrReprobeRequired)
        {
            var reprobe = await SelectAtrExposureAsync(context, cancellationToken).ConfigureAwait(false);
            if (!reprobe.CanAdvance) return reprobe;
        }
        if (selectedAtrExposureSeconds is not { } exposure)
        {
            return Attention(ObservationStage.RunScienceBlock, "ATR_TIER_NOT_SELECTED", "No quality-gated ATR exposure tier is available.");
        }
        if (configuration.Atr.ScienceFrameCount <= 0)
        {
            return Attention(ObservationStage.RunScienceBlock, "ATR_FRAME_COUNT_INVALID", "ATR science frame count must be positive.");
        }

        while (savedAtrFrames < configuration.Atr.ScienceFrameCount)
        {
            if (attemptedAtrFrames >= configuration.Atr.MaximumScienceAttempts)
            {
                return Failed(
                    ObservationStage.RunScienceBlock,
                    "ATR_ATTEMPT_LIMIT",
                    $"Accepted {savedAtrFrames}/{configuration.Atr.ScienceFrameCount} frames after {attemptedAtrFrames} bounded attempts.");
            }
            UpdateRemainingScienceDuration(context, exposure);
            var protectedPlan = context.Plan with { PlannedDuration = context.RemainingWorstCaseDuration ?? context.Plan.PlannedDuration };
            var environment = ValidateEnvironment(protectedPlan);
            if (environment.Disposition != GateDisposition.Passed) return new StageResult(environment);
            var coverGate = await EnsureOpticalCoverOpenAsync(context, cancellationToken).ConfigureAwait(false);
            if (coverGate.Disposition != GateDisposition.Passed) return new StageResult(coverGate);
            if (!IsGuidingStable()) return Attention(ObservationStage.RunScienceBlock, "GUIDING_LOST", "PHD2 no longer reports a settled guiding state; no new ATR exposure was started.");
            var qhyGate = await CheckPhotometryHealthAsync(context, cancellationToken).ConfigureAwait(false);
            if (qhyGate.Disposition != GateDisposition.Passed) return new StageResult(qhyGate);

            // One fresh fail-closed checkpoint immediately before every individual ATR frame.
            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            attemptedAtrFrames++;
            // An exposure attempt is provenance even when capture, conversion, or save
            // subsequently fails. Persist it before opening the shutter.
            PublishFrameCounters();
            var acceptedIndex = savedAtrFrames + 1;
            Report($"ATR 科学曝光 accepted {acceptedIndex}/{configuration.Atr.ScienceFrameCount} · attempt {attemptedAtrFrames}/{configuration.Atr.MaximumScienceAttempts} · {exposure:G4}s", savedAtrFrames / (double)configuration.Atr.ScienceFrameCount);
            var captured = await CaptureAtrImageAsync(
                context,
                exposure,
                CaptureSequence.ImageTypes.LIGHT,
                $"UVEX-ADV science accepted-candidate {acceptedIndex}/{configuration.Atr.ScienceFrameCount} attempt {attemptedAtrFrames}",
                cancellationToken).ConfigureAwait(false);
            PublishAtrPreview(captured.Image, $"ATR science candidate {acceptedIndex}/{configuration.Atr.ScienceFrameCount} · attempt {attemptedAtrFrames} · {exposure:G4}s · p99.9 {captured.Metrics.HighPercentileAdu:F0} · saturation {captured.Metrics.SaturatedFraction:P3} · line SNR {captured.Metrics.LineSnrPerResolutionElement:F1}");
            var quality = ValidateAtrScienceMetrics(captured.Metrics);
            await SaveAtrImageAsync(
                captured,
                attemptedAtrFrames,
                quality.Disposition == GateDisposition.Passed,
                quality.Disposition,
                quality.Code,
                quality.Message,
                cancellationToken).ConfigureAwait(false);
            retainedAtrScienceFrames++;
            context.Set("atrAttemptedFrames", attemptedAtrFrames);
            if (quality.Disposition != GateDisposition.Passed)
            {
                PublishFrameCounters();
                atrReprobeRequired = true;
                selectedAtrExposureSeconds = null;
                await WriteAuditBestEffortAsync("atr-science-frame-rejected", new
                {
                    context.Plan.ObservationRunId,
                    attemptedAtrFrames,
                    acceptedFrames = savedAtrFrames,
                    quality.Code,
                    quality.Message,
                    immutableFrameRetained = true,
                }).ConfigureAwait(false);
                return new StageResult(quality);
            }
            savedAtrFrames++;
            context.Set("atrSavedFrames", savedAtrFrames);
            context.Set("atrAcceptedFrames", savedAtrFrames);
            PublishFrameCounters();
            UpdateRemainingScienceDuration(context, exposure);
        }

        Report("ATR 科学曝光块完成", 1);
        return Passed(
            "ATR_SCIENCE_BLOCK_COMPLETE",
            $"Accepted {savedAtrFrames}/{retainedAtrScienceFrames} retained ATR585M science FITS through N.I.N.A. ImageSaveMediator at {exposure:G4}s.",
            new Dictionary<string, double>
            {
                ["acceptedFrames"] = savedAtrFrames,
                ["retainedFrames"] = retainedAtrScienceFrames,
                ["attemptedFrames"] = attemptedAtrFrames,
                ["exposureSeconds"] = exposure,
            });
    }

    private async Task<StageResult> FinalizeObservationAsync(ObservationContext context, CancellationToken cancellationToken)
    {
        var cleanupIssues = new List<string>();
        if (photometryJobId is { } jobId)
        {
            var snapshot = await qhy.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (snapshot is not null) ObserveQhySnapshot(snapshot);
            if (snapshot is not null && snapshot.State is not (QhyJobState.Completed or QhyJobState.Cancelled or QhyJobState.Faulted or QhyJobState.TakenOver))
            {
                await CheckpointAndRejectStaleStageStackAsync(context, cancellationToken).ConfigureAwait(false);
                try
                {
                    var cancelling = await qhy.CancelAsync(jobId, cancellationToken).ConfigureAwait(false);
                    ObserveQhySnapshot(cancelling);
                    snapshot = await qhy.WaitForCheckedTerminalAsync(
                        jobId,
                        TimeSpan.FromSeconds(15),
                        observed =>
                        {
                            ObserveQhySnapshot(observed);
                            return Task.CompletedTask;
                        },
                        cancellationToken).ConfigureAwait(false);
                    ObserveQhySnapshot(snapshot);
                }
                catch (Exception ex) { cleanupIssues.Add($"QHY photometry stop failed: {ex.Message}"); }
            }
            if (snapshot?.State is QhyJobState.Completed or QhyJobState.Cancelled or QhyJobState.Faulted or QhyJobState.TakenOver) activeQhyJobs.TryRemove(jobId, out _);
            else cleanupIssues.Add($"QHY photometry job {jobId:D} did not reach a checked terminal state.");
        }
        // The terminal QHY snapshot is the authoritative final frame index.  Re-publish
        // the aggregate after reconciliation so the observation manifest cannot lag the
        // service by the frames captured between the last science health poll and stop.
        PublishFrameCounters();
        if (phd2.IsConnected)
        {
            await CheckpointAndRejectStaleStageStackAsync(context, cancellationToken).ConfigureAwait(false);
            try
            {
                await StopPhdAndWaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) { cleanupIssues.Add($"PHD2 stop failed: {ex.Message}"); }
        }
        if (configuration.Environment.CloseOpticalCoverOnFinalize)
        {
            await CheckpointAndRejectStaleStageStackAsync(context, cancellationToken).ConfigureAwait(false);
            var coverIssue = await CloseOpticalCoverAsync(
                "Normal observation finalization.",
                cancellationToken).ConfigureAwait(false);
            if (coverIssue is not null) cleanupIssues.Add(coverIssue);
        }
        await WriteAuditBestEffortAsync("real-observation-finalized", new
        {
            context.Plan.ObservationRunId,
            acceptedAtrFrames = savedAtrFrames,
            retainedAtrScienceFrames,
            attemptedAtrFrames,
            attemptedAtrProbeFrames,
            retainedAtrProbeFrames,
            acceptedAtrProbeFrames,
            selectedAtrExposureSeconds,
            cumulativeCorrectionArcseconds = cumulativeCorrectionDegrees * 3600,
            correctionAttempts,
            cleanupIssues,
        }).ConfigureAwait(false);
        if (cleanupIssues.Count > 0)
        {
            return Attention(ObservationStage.FinalizeObservation, "FINALIZE_INCOMPLETE", string.Join(" ", cleanupIssues));
        }
        return Passed(
            "OBSERVATION_FINALIZED",
            $"Observation finalized with {savedAtrFrames} accepted / {retainedAtrScienceFrames} retained ATR science frame(s); QHY and PHD2 terminal states were checked.",
            new Dictionary<string, double>
            {
                ["atrAcceptedFrames"] = savedAtrFrames,
                ["atrRetainedFrames"] = retainedAtrScienceFrames,
                ["atrAttemptedFrames"] = attemptedAtrFrames,
                ["cumulativeCorrectionArcseconds"] = cumulativeCorrectionDegrees * 3600,
                ["correctionAttempts"] = correctionAttempts,
            });
    }

    private IReadOnlyList<string> ValidateStaticConfiguration(ObservationPlan plan)
    {
        var issues = new List<string>();
        var binding = configuration.Commissioning;
        if (!configuration.RealModeAuthorized) issues.Add("The immutable real-run configuration was captured while the Profile did not authorize REAL mode.");
        if (!binding.RealModeCommissioned) issues.Add("Real mode was not commissioned when this immutable run configuration was captured.");
        var capability = ObservationAutomationPolicy.ValidateFullAutomationCapabilities(
            configuration.Environment.RequireSafetyMonitor,
            configuration.Environment.RequireOpenDomeOrRoof,
            configuration.Environment.RequireWeatherData,
            configuration.Environment.RequireOpenOpticalCover);
        if (capability.Disposition != GateDisposition.Passed) issues.Add(capability.Message);

        var lockedMotion = new MotionLimits(
            binding.MaximumSingleCorrectionArcseconds / 3600d,
            binding.MaximumCumulativeCorrectionArcseconds / 3600d,
            binding.MaximumCorrectionAttempts,
            TimeSpan.FromMinutes(binding.MaximumAcquisitionMinutes));
        var planBinding = ObservationAutomationPolicy.ValidateLockedPlanSafety(
            plan,
            lockedMotion,
            configuration.Environment.RequireSafetyMonitor);
        if (planBinding.Disposition != GateDisposition.Passed) issues.Add(planBinding.Message);
        if (string.IsNullOrWhiteSpace(binding.PresetPath) ||
            string.IsNullOrWhiteSpace(binding.PresetId) ||
            string.IsNullOrWhiteSpace(binding.PresetSha256))
        {
            issues.Add("A path, ID and SHA-256 for the commissioning preset are required.");
        }
        if (string.IsNullOrWhiteSpace(binding.HardwareFingerprintSha256)) issues.Add("A commissioning hardware-fingerprint SHA-256 is required.");
        if (string.IsNullOrWhiteSpace(configuration.NightSetup.SnapshotPath) || string.IsNullOrWhiteSpace(configuration.NightSetup.SnapshotSha256))
        {
            issues.Add("A path and SHA-256 for the immutable Night Setup snapshot are required.");
        }
        if (string.IsNullOrWhiteSpace(configuration.ExpectedTelescopeId)) issues.Add("Expected telescope DeviceId is required.");
        if (string.IsNullOrWhiteSpace(plan.ExpectedAtrCameraId) || plan.ExpectedAtrCameraId.StartsWith("SIM-", StringComparison.OrdinalIgnoreCase)) issues.Add("A real ATR585M DeviceId is required.");
        if (string.IsNullOrWhiteSpace(plan.ExpectedQhyCameraId) || plan.ExpectedQhyCameraId.StartsWith("SIM-", StringComparison.OrdinalIgnoreCase)) issues.Add("A real QHY StableId is required.");
        if (string.IsNullOrWhiteSpace(plan.ExpectedG3ProfileName) || plan.ExpectedG3ProfileName.StartsWith("SIM-", StringComparison.OrdinalIgnoreCase)) issues.Add("A real PHD2/G3 profile binding is required.");
        if (plan.NightSetupId.StartsWith("SIM-", StringComparison.OrdinalIgnoreCase)) issues.Add("A real locked Night Setup ID is required.");
        if (configuration.Phd2.ProfileId < 0 || string.IsNullOrWhiteSpace(configuration.Phd2.ProfileName) ||
            string.IsNullOrWhiteSpace(configuration.Phd2.CameraName) || string.IsNullOrWhiteSpace(configuration.Phd2.CameraStableId) ||
            string.IsNullOrWhiteSpace(configuration.Phd2.MountName) || string.IsNullOrWhiteSpace(configuration.Phd2.RuntimeCameraName) ||
            string.IsNullOrWhiteSpace(configuration.Phd2.RuntimeMountName))
        {
            issues.Add("PHD2 profile ID/name, registry G3/mount names, runtime JSON-RPC G3/mount names, and G3 stable ID are required.");
        }
        if (!string.Equals(plan.ExpectedG3ProfileName, configuration.Phd2.ProfileName, StringComparison.Ordinal)) issues.Add("Observation PHD2 profile binding does not match the detailed PHD2 profile name.");
        if (!DateTimeOffset.TryParse(configuration.Phd2.CalibrationTimestampUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _)) issues.Add("A parseable PHD2 calibration UTC timestamp is required.");
        if (string.IsNullOrWhiteSpace(configuration.Phd2.ProfileEvidenceSha256)) issues.Add("A locked PHD2 registry profile-evidence SHA-256 is required.");
        if (!double.IsFinite(configuration.Atr.TargetTemperatureC) || configuration.Atr.ReadoutModeIndex < 0) issues.Add("ATR target temperature and readout-mode index are required.");
        if (configuration.G3.ExposureMilliseconds <= 0 || configuration.G3.Binning <= 0 || configuration.G3.GainPercent is < 0 or > 100) issues.Add("G3 exposure, binning or gain is invalid.");
        if (configuration.G3.SaturationAdu is <= 0 or > ushort.MaxValue) issues.Add("G3 saturation ADU is outside the unsigned 16-bit FITS container range.");
        if (configuration.G3.FocalLengthMillimeters <= 0 || configuration.G3.PixelSizeMicrometers <= 0) issues.Add("Measured G3 focal length and pixel size are required.");
        issues.AddRange(configuration.G3.PlateSolveExposurePreset.Validate());
        issues.AddRange(configuration.G3.WcsCentering.Validate());
        if (!double.IsFinite(configuration.G3.MotionWorstCaseActionSeconds) || configuration.G3.MotionWorstCaseActionSeconds <= 0)
        {
            issues.Add("G3 worst-case duration per mount action must be positive and finite for outbound/return time reservation.");
        }
        if (!double.IsFinite(configuration.G3.MotionPostSlewSettleSeconds) || configuration.G3.MotionPostSlewSettleSeconds <= 0)
        {
            issues.Add("A positive commissioned G3 post-slew settle time is required before fresh acquisition evidence.");
        }
        else if (configuration.G3.MotionWorstCaseActionSeconds <= configuration.G3.MotionPostSlewSettleSeconds)
        {
            issues.Add("G3 worst-case action time must be greater than the commissioned post-slew settle time.");
        }
        if (configuration.G3.WcsCentering.MaximumSingleCorrectionArcseconds <= 2 * MountCommandArrivalToleranceArcseconds)
        {
            issues.Add("G3 WCS single-correction limit must exceed twice the arrival tolerance to guarantee return progress.");
        }
        if (configuration.G3.WideToSlitTransferMode != WideToSlitTransferMode.Skip)
        {
            issues.Add("No independently verified Active QHY-to-G3 transfer can be loaded by this runner; WideToSlitTransferMode must remain Skip and paired-WCS Candidates cannot authorize motion.");
        }
        issues.AddRange(configuration.G3.EffectiveFastSolvePair.Validate());
        issues.AddRange(configuration.G3.Search.Validate());
        if (configuration.G3.Search.StepArcseconds > binding.MaximumSingleCorrectionArcseconds)
        {
            issues.Add("G3 search step exceeds the commissioned single-correction limit.");
        }
        if (configuration.G3.Search.MaximumCumulativeMotionArcseconds > binding.MaximumCumulativeCorrectionArcseconds)
        {
            issues.Add("G3 search cumulative-motion limit exceeds the commissioned cumulative-correction limit.");
        }
        if (configuration.G3.Search.MaximumAttempts > binding.MaximumCorrectionAttempts)
        {
            issues.Add("G3 search attempt limit exceeds the commissioned total-correction attempt limit.");
        }
        if (configuration.G3.Search.MaximumElapsedTime > TimeSpan.FromMinutes(binding.MaximumAcquisitionMinutes))
        {
            issues.Add("G3 search elapsed-time limit exceeds the commissioned total-acquisition limit.");
        }
        issues.AddRange(configuration.G3.EffectiveBrightTarget
            .Validate(configuration.G3.ExposureMilliseconds));
        if (configuration.Qhy.FocalLengthMillimeters <= 0 || configuration.Qhy.PixelSizeMicrometers <= 0) issues.Add("Measured GS350/QHY focal length and pixel size are required.");
        if (configuration.Qhy.CenteringToleranceArcseconds <= 0) issues.Add("QHY centering tolerance must be positive.");
        issues.AddRange(configuration.Qhy.CoarseCenteringLimits.Validate());
        if (configuration.Qhy.ReadoutMode < 0 || configuration.Qhy.RoiX < 0 || configuration.Qhy.RoiY < 0 || configuration.Qhy.RoiWidth < 0 || configuration.Qhy.RoiHeight < 0) issues.Add("QHY readout mode and ROI are invalid.");
        if (configuration.Atr.ScienceFrameCount <= 0 || configuration.Atr.MaximumScienceAttempts < configuration.Atr.ScienceFrameCount) issues.Add("ATR science frame count must be positive and maximum attempts must cover every accepted frame.");
        if (!double.IsFinite(configuration.Environment.MountClockMaximumOffsetSeconds) ||
            configuration.Environment.MountClockMaximumOffsetSeconds is <= 0 or > 300)
        {
            issues.Add("Mount-clock maximum offset must be within (0, 300] seconds.");
        }
        if (configuration.Environment.OpticalCoverTransitionTimeoutSeconds is < 5 or > 300)
        {
            issues.Add("Optical-cover transition timeout must be within [5, 300] seconds.");
        }
        if (configuration.Qhy.AcquisitionExposureLadderSeconds.Count == 0) issues.Add("QHY exposure ladder is empty.");
        if (configuration.Atr.ExposureLadderSeconds.Count == 0) issues.Add("ATR exposure ladder is empty.");
        if (binding.MaximumSingleCorrectionArcseconds <= 0 ||
            binding.MaximumCumulativeCorrectionArcseconds < binding.MaximumSingleCorrectionArcseconds ||
            binding.MaximumCorrectionAttempts <= 0 || binding.MaximumAcquisitionMinutes <= 0)
        {
            issues.Add("Locked motion limits are invalid.");
        }
        if (!Uri.TryCreate(configuration.QhyServiceUrl, UriKind.Absolute, out var qhyUri) || !qhyUri.IsLoopback) issues.Add("QHY service URL must be an absolute loopback URL.");
        return issues;
    }

    private Phd2IdentityRequirement PhdIdentityRequirement() => new(
        configuration.Phd2.ProfileId,
        configuration.Phd2.ProfileName,
        configuration.Phd2.RuntimeCameraName,
        configuration.Phd2.RuntimeMountName,
        RequireConnected: true,
        StableCameraId: null);

    private GateResult ValidatePhdProfileBindingEvidence()
    {
        if (commissioning is null)
        {
            return GateResult.Unknown("COMMISSIONING_PRESET_REQUIRED", "PHD2 profile evidence cannot be checked without a trusted commissioning preset.");
        }

        Phd2ProfileBindingValidation validation;
        try
        {
            validation = WindowsPhd2ProfileEvidence.ReadAndValidate(new Phd2ProfileBindingRequirement(
                configuration.Phd2.ProfileId,
                configuration.Phd2.ProfileName,
                configuration.Phd2.CameraName,
                configuration.Phd2.CameraStableId,
                configuration.Phd2.MountName,
                configuration.G3.Binning,
                configuration.G3.GainPercent));
        }
        catch (Exception ex)
        {
            return GateResult.Unknown("PHD2_PROFILE_EVIDENCE_UNAVAILABLE", $"PHD2 registry evidence could not be read: {ex.Message}");
        }
        if (validation.Status != Phd2ValidationStatus.Valid || validation.Evidence is null)
        {
            return validation.Status == Phd2ValidationStatus.Invalid
                ? GateResult.Fail("PHD2_PROFILE_EVIDENCE_INVALID", string.Join(" ", validation.Failures))
                : GateResult.Unknown("PHD2_PROFILE_EVIDENCE_INDETERMINATE", string.Join(" ", validation.IndeterminateReasons));
        }
        if (!SameHash(validation.Evidence.Sha256, configuration.Phd2.ProfileEvidenceSha256) ||
            !SameHash(validation.Evidence.Sha256, commissioning.Value.Phd2ProfileEvidenceSha256))
        {
            return GateResult.Fail(
                "PHD2_PROFILE_EVIDENCE_HASH_MISMATCH",
                $"PHD2 registry evidence hash {validation.Evidence.Sha256} does not match the locked Profile and commissioning preset.");
        }
        phdProfileEvidence = validation.Evidence;
        return GateResult.Pass(
            "PHD2_PROFILE_EVIDENCE_VALID",
            $"PHD2 profile {validation.Evidence.ProfileId}/{validation.Evidence.ProfileName} is bound to the commissioned G3 USB instance, gain and binning.");
    }

    private Phd2CalibrationRequirement PhdCalibrationRequirement(DateTimeOffset? runtimeCalibrationTimestampUtc = null)
    {
        _ = DateTimeOffset.TryParse(
            configuration.Phd2.CalibrationTimestampUtc,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var timestamp);
        return new Phd2CalibrationRequirement(
            configuration.Phd2.ProfileId,
            configuration.Phd2.ProfileName,
            runtimeCalibrationTimestampUtc ?? (timestamp == default ? null : timestamp.ToUniversalTime()),
            TimeSpan.FromHours(configuration.Phd2.CalibrationMaximumAgeHours),
            RequireKnownAge: true);
    }

    private async Task EnsurePhdConnectedAsync(CancellationToken cancellationToken)
    {
        if (!phd2.IsConnected) await phd2.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<PlateSolveEvidence> SolveExternalFitsAsync(
        string path,
        int bitDepth,
        double focalLengthMillimeters,
        double pixelSizeMicrometers,
        int binning,
        Coordinates requested,
        string role,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Immutable FITS selected for solving does not exist.", path);
        var image = await imageDataFactory.CreateFromFile(
            path,
            bitDepth,
            false,
            RawConverterEnum.FREEIMAGE,
            cancellationToken).ConfigureAwait(false);
        return await SolveImageAsync(
            image,
            focalLengthMillimeters,
            pixelSizeMicrometers,
            binning,
            requested,
            role,
            path,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PlateSolveEvidence> SolveImageAsync(
        IImageData image,
        double focalLengthMillimeters,
        double pixelSizeMicrometers,
        int binning,
        Coordinates requested,
        string role,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        if (focalLengthMillimeters <= 0 || pixelSizeMicrometers <= 0 || binning <= 0)
        {
            throw new InvalidOperationException($"{role} optical parameters are not commissioned.");
        }
        var parameter = new PlateSolveParameter
        {
            FocalLength = focalLengthMillimeters,
            PixelSize = pixelSizeMicrometers,
            Binning = binning,
            SearchRadius = configuration.PlateSolver.SearchRadiusDegrees,
            Regions = configuration.PlateSolver.Regions,
            DownSampleFactor = configuration.PlateSolver.DownSampleFactor,
            MaxObjects = configuration.PlateSolver.MaximumObjects,
            BlindFailoverEnabled = configuration.PlateSolver.BlindFailoverEnabled,
            Coordinates = requested,
        };
        var result = await imageSolver.Solve(image, parameter, progress, cancellationToken).ConfigureAwait(false);
        var residual = result.Success && result.Coordinates is not null
            ? AngularSeparationArcseconds(requested, result.Coordinates)
            : double.NaN;
        await WriteAuditBestEffortAsync("plate-solve", new
        {
            role,
            sourcePath,
            requestedRaDegrees = requested.RADegrees,
            requestedDecDegrees = requested.Dec,
            solvedRaDegrees = result.Coordinates?.RADegrees,
            solvedDecDegrees = result.Coordinates?.Dec,
            residualArcseconds = residual,
            result.Success,
            result.Pixscale,
            result.PositionAngle,
            result.Flipped,
            solverIdentity,
        }).ConfigureAwait(false);
        var solveEvidencePath = await PublishRunJsonEvidenceAsync(
            "plate-solve-evidence",
            role,
            new
            {
                role,
                solver = solverIdentity,
                requested = new
                {
                    raDegrees = requested.RADegrees,
                    decDegrees = requested.Dec,
                    epoch = requested.Epoch.ToString(),
                },
                solved = result.Success && result.Coordinates is not null
                    ? new
                    {
                        raDegrees = (double?)result.Coordinates.RADegrees,
                        decDegrees = (double?)result.Coordinates.Dec,
                        pixelScaleArcseconds = (double?)result.Pixscale,
                        positionAngleDegrees = (double?)result.PositionAngle,
                        flipped = (bool?)result.Flipped,
                    }
                    : null,
                success = result.Success,
                residualArcseconds = double.IsFinite(residual) ? residual : (double?)null,
                actionConfigurationSha256 = configuration.ActionConfigurationSha256,
            },
            sourcePath,
            cancellationToken).ConfigureAwait(false);
        var solveEvidenceSha256 = await ComputeFileSha256Async(
            solveEvidencePath,
            cancellationToken).ConfigureAwait(false);
        return new PlateSolveEvidence(
            result,
            requested,
            residual,
            solverIdentity,
            sourcePath,
            solveEvidencePath,
            solveEvidenceSha256,
            image.Properties.Width,
            image.Properties.Height,
            binning,
            DateTimeOffset.UtcNow);
    }

    private async Task PublishQhyPreviewAsync(QhyJobSnapshot snapshot, CancellationToken cancellationToken)
    {
        ObserveQhySnapshot(snapshot);
        try
        {
            var bytes = await qhy.GetPreviewPngAsync(snapshot.Id, cancellationToken).ConfigureAwait(false);
            if (bytes is null) return;
            var image = LoadPng(bytes);
            var last = snapshot.Frames.LastOrDefault();
            var metrics = last is null
                ? string.Empty
                : $" · stars {last.Metrics.DetectedStars}, sat {last.Metrics.SaturatedFraction:P3}, transparency {last.Metrics.Transparency?.ToString("F2", CultureInfo.InvariantCulture) ?? "?"}";
            host.PublishPreview(
                ObservationPreviewChannel.QhyWideField,
                image,
                $"QHY {snapshot.Kind} {snapshot.State} · {snapshot.Frames.Count} immutable FITS{metrics}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            host.PublishPreview(ObservationPreviewChannel.QhyWideField, null, $"QHY preview unavailable: {ex.Message}");
        }
    }

    private void PublishG3Preview(
        IImageData image,
        string caption,
        SlitGeometry? slit = null,
        PixelPoint? target = null,
        PixelPoint? guideStar = null) =>
        host.PublishPreview(
            ObservationPreviewChannel.G3SlitField,
            ObservationPreviewRenderer.RenderG3(image, slit, target, guideStar),
            caption);

    private void PublishAtrPreview(IImageData image, string caption) =>
        host.PublishPreview(
            ObservationPreviewChannel.AtrSpectrum,
            ObservationPreviewRenderer.RenderAtr(image, configuration.Atr.Roi),
            caption);

    private Task PublishG3MainFocusEvidenceAsync(
        ObservationContext context,
        G3SlitIlluminationSequence sequence,
        C11MainFocusOwnerSnapshot ownerBefore,
        C11MainFocusOwnerSnapshot ownerAfter,
        G3StellarFocusMeasurement measurement,
        GateResult observationGate,
        string sourcePath,
        CancellationToken cancellationToken) =>
        PublishRunJsonEvidenceAsync(
            "g3-c11-main-focus-analysis",
            "G3 stellar-shape evidence for the C11 Star Focuser Pro/Gemini main-focus domain",
            new
            {
                sequenceId = sequence.SequenceId,
                requestedTarget = new
                {
                    context.Plan.Target.Name,
                    context.Plan.Target.CatalogId,
                    context.Plan.Target.RightAscensionDegrees,
                    context.Plan.Target.DeclinationDegrees,
                },
                opticalDomain = new
                {
                    role = FocusDomainRole.C11Main.ToString(),
                    owner = FocusDomainConventions.C11Owner,
                    logicalDeviceId = FocusDomainConventions.C11LogicalDeviceId,
                    mechanism = FocusMechanism.Gemini.ToString(),
                    endpoint = FocusDomainConventions.C11ConnectionEndpoint,
                    sourceCameraStableDeviceId = configuration.Phd2.CameraStableId,
                    prohibitedSubstitutes = new[]
                    {
                        $"{FocusDomainConventions.UvexLogicalDeviceId}/{FocusMechanism.UvexM2}",
                        $"{FocusDomainConventions.Gs350LogicalDeviceId}/{FocusMechanism.ToupTekAaf}",
                    },
                },
                ownerBefore,
                ownerAfter,
                sourceFrames = sequence.Frames
                    .Where(frame => frame.Phase is G3SlitIlluminationPhase.OffBefore or G3SlitIlluminationPhase.OffAfter)
                    .Select(frame => new
                    {
                        frame.Role,
                        phase = frame.Phase.ToString(),
                        frame.PhaseIndex,
                        absolutePath = Path.GetFullPath(frame.Capture.Path),
                        frame.Sha256,
                        frame.Capture.CompletedUtc,
                    })
                    .ToArray(),
                analyzerGate = new
                {
                    disposition = measurement.Gate.Disposition.ToString(),
                    measurement.Gate.Code,
                    measurement.Gate.Message,
                },
                observationGate = new
                {
                    disposition = observationGate.Disposition.ToString(),
                    observationGate.Code,
                    observationGate.Message,
                },
                measurement = new
                {
                    measurement.MedianFwhmPixels,
                    measurement.MedianEllipticity,
                    measurement.StarCount,
                    measurement.DetectedStarCount,
                    measurement.SaturatedStarFraction,
                    measurement.MedianSignalToNoise,
                    measurement.RelativeFwhmMad,
                    measurement.Confidence,
                },
            },
            sourcePath,
            cancellationToken);

    private Task<string> PublishSlitWheelIdentityEvidenceAsync(
        ObservationContext context,
        G3SlitIlluminationSequence sequence,
        SlitDarkApertureHdrAnalysis aperture,
        SlitWheelIdentityResult result,
        string sourcePath,
        CancellationToken cancellationToken) =>
        PublishRunJsonEvidenceAsync(
            "slit-wheel-optical-identity",
            "Fresh LED-width optical verification of UVEX slit-wheel physical identity",
            new
            {
                sequence.SequenceId,
                requestedTarget = new
                {
                    context.Plan.Target.Name,
                    context.Plan.Target.CatalogId,
                },
                authority = new
                {
                    mechanicalReadbackIsDeclarationOnly = true,
                    freshLedWidthIsIndependentOpticalCheck = true,
                    automaticRemappingPermitted = false,
                    failedOrAmbiguousIdentityAuthorizesMotion = false,
                    measurementModel = SlitDarkApertureHdrAnalyzer.MeasurementModelId,
                    reflectedRidgeFwhmCanAuthorizeWidth = false,
                },
                gate = new
                {
                    disposition = result.Gate.Disposition.ToString(),
                    result.Gate.Code,
                    result.Gate.Message,
                    result.Gate.Metrics,
                },
                calibration = new
                {
                    result.CalibrationId,
                    result.CalibrationSha256,
                    schemaVersion = commissioning?.Value.SlitWheelIdentity?.SchemaVersion,
                    installationEpochId = commissioning?.Value.SlitWheelIdentity?.InstallationEpochId,
                    cameraStableId = commissioning?.Value.SlitWheelIdentity?.CameraStableId,
                    binningX = commissioning?.Value.SlitWheelIdentity?.BinningX,
                    binningY = commissioning?.Value.SlitWheelIdentity?.BinningY,
                    imageWidthPixels = commissioning?.Value.SlitWheelIdentity?.ImageWidthPixels,
                    imageHeightPixels = commissioning?.Value.SlitWheelIdentity?.ImageHeightPixels,
                    shortExposureMilliseconds = commissioning?.Value.SlitWheelIdentity?.ShortExposureMilliseconds,
                    longExposureMilliseconds = commissioning?.Value.SlitWheelIdentity?.LongExposureMilliseconds,
                    edgePsfAlphaPixels = commissioning?.Value.SlitWheelIdentity?.EdgePsfAlphaPixels,
                    edgePsfBeta = commissioning?.Value.SlitWheelIdentity?.EdgePsfBeta,
                },
                declaration = new
                {
                    result.ReportedWheelPosition,
                    result.ReportedNominalWidthMicrometers,
                },
                measurement = new
                {
                    result.MeasuredWidthPixels,
                    result.MeasurementUncertaintyPixels,
                    resolution = aperture.Resolution.ToString(),
                    aperture.ReflectiveEdgeToApertureCenterPixels,
                    aperture.SecondaryEdgeAmplitudeRatio,
                    aperture.DeltaBic,
                    aperture.ShortExposureSaturatedFraction,
                    aperture.LongExposureSaturatedFraction,
                    aperture.LongExposureValidFraction,
                    aperture.LongExposureDynamicRangeAdu,
                    physicalApertureGeometry = aperture.Geometry,
                    reflectedEdgeGeometry = aperture.ReflectiveEdgeGeometry,
                },
                matched = result.MatchedCandidate,
                candidates = result.Candidates,
                sourceFrames = sequence.Frames.Select(frame => new
                {
                    frame.Role,
                    phase = frame.Phase.ToString(),
                    frame.PhaseIndex,
                    absolutePath = Path.GetFullPath(frame.Capture.Path),
                    frame.Sha256,
                    frame.Capture.CompletedUtc,
                    frame.ExposureMilliseconds,
                }).ToArray(),
            },
            sourcePath,
            cancellationToken,
            new Dictionary<string, string>
            {
                ["slitIdentityGate"] = result.Gate.Code,
                ["slitIdentitySummary"] = result.Gate.Message,
                ["slitIdentityCalibrationId"] = result.CalibrationId,
                ["slitIdentityMatchedPosition"] = result.MatchedCandidate?.WheelPosition.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ["slitIdentityMeasuredWidthPixels"] = result.MeasuredWidthPixels.ToString("F3", CultureInfo.InvariantCulture),
            });

    private async Task PublishG3AnalysisEvidenceAsync(
        ObservationContext context,
        G3SlitIlluminationSequence sequence,
        IImageData image,
        PlateSolveEvidence solve,
        IReadOnlyList<StarCandidate> candidates,
        SlitIlluminationPairAnalysis slit,
        SlitGeometry slitSeed,
        TargetIdentification target,
        PixelPoint? predictedPoint,
        CancellationToken cancellationToken)
    {
        await PublishRunJsonEvidenceAsync(
            "g3-target-slit-analysis",
            "PHD2/G3 detector-fixed paired-illumination target identity and slit-locus decision",
            new
            {
                sequenceId = sequence.SequenceId,
                authority = "SlitDarkApertureHdrAnalyzer over detector-fixed 10/20 ms median composites; reflected-ridge FWHM is diagnostic only and the physical dark-aperture midpoint authorizes placement",
                compositePolicy = new
                {
                    offReference = "per-pixel median of OFF-before x3 and OFF-after x3",
                    onSignal = "per-pixel median of ON x3; ON-1 is tagged transition-candidate",
                    registration = "none; the slit/LED structure is fixed in G3 detector coordinates and is never warped to align stars",
                    minimumAcceptedConfidence = G3SlitIlluminationPolicy.MinimumAcceptedConfidence,
                },
                sourceFrames = sequence.Frames.Select(frame => new
                {
                    frame.Role,
                    phase = frame.Phase.ToString(),
                    frame.PhaseIndex,
                    frame.TransitionCandidate,
                    absolutePath = Path.GetFullPath(frame.Capture.Path),
                    frame.Sha256,
                    frame.Capture.CompletedUtc,
                }).ToArray(),
                illuminationReadbacks = sequence.Commands.Select(command => new
                {
                    command.Phase,
                    command.Enabled,
                    ledState = command.LedState.ToString(),
                    command.CommandedUtc,
                    command.StatusTimestampUtc,
                    command.SlitPhotodiodeValue,
                    command.SlitPhotodiodeThreshold,
                    command.SlitPhotodiodeEnabled,
                }).ToArray(),
                requestedTarget = new
                {
                    context.Plan.Target.Name,
                    context.Plan.Target.CatalogId,
                    context.Plan.Target.RightAscensionDegrees,
                    context.Plan.Target.DeclinationDegrees,
                },
                wcs = new
                {
                    success = solve.Result.Success,
                    solvedRaDegrees = solve.Result.Coordinates?.RADegrees,
                    solvedDecDegrees = solve.Result.Coordinates?.Dec,
                    solve.Result.Pixscale,
                    solve.Result.PositionAngle,
                    solve.Result.Flipped,
                    solve.ResidualArcseconds,
                    solve.SolverIdentity,
                },
                candidateCount = candidates.Count,
                predictedTarget = predictedPoint is null ? null : new { predictedPoint.X, predictedPoint.Y },
                slit = new
                {
                    gate = slit.Gate.Disposition.ToString(),
                    slit.Gate.Code,
                    slit.Gate.Message,
                    seed = new
                    {
                        slitSeed.CalibrationId,
                        centerX = slitSeed.AcquisitionPoint.X,
                        centerY = slitSeed.AcquisitionPoint.Y,
                        slitSeed.AngleDegrees,
                        slitSeed.LengthPixels,
                        slitSeed.WidthPixels,
                        slitSeed.UncertaintyPixels,
                    },
                    slit.Geometry.CalibrationId,
                    centerX = slit.Geometry.AcquisitionPoint.X,
                    centerY = slit.Geometry.AcquisitionPoint.Y,
                    slit.Geometry.AngleDegrees,
                    slit.Geometry.LengthPixels,
                    slit.Geometry.WidthPixels,
                    slit.Geometry.UncertaintyPixels,
                    slit.ContrastSigma,
                    slit.PerpendicularOffsetPixels,
                    slit.AngleOffsetDegrees,
                    slit.Polarity,
                    slit.MeasuredWidthPixels,
                    slit.Confidence,
                    slit.UniquenessRatio,
                    slit.ValidFraction,
                    slit.SaturatedFraction,
                    slit.BadPixelFraction,
                    slit.AlongSignalFraction,
                    slit.AlongSpanFraction,
                },
                target = new
                {
                    gate = target.Gate.Disposition.ToString(),
                    target.Gate.Code,
                    target.Gate.Message,
                    centroidX = target.Target?.Centroid.X,
                    centroidY = target.Target?.Centroid.Y,
                    target.PredictionResidualPixels,
                    target.UniquenessRatio,
                },
            },
            solve.SourcePath,
            cancellationToken).ConfigureAwait(false);

        var overlay = ObservationPreviewRenderer.RenderG3(
            image,
            slit.Geometry,
            target.Target?.Centroid);
        PublishRunPngEvidence(
            "g3-target-slit-overlay",
            "G3 WCS target and paired-illumination measured slit overlay",
            overlay,
            new Dictionary<string, string>
            {
                ["sequenceId"] = sequence.SequenceId,
                ["sourceFits"] = Path.GetFullPath(solve.SourcePath),
                ["targetGate"] = target.Gate.Code,
                ["slitGate"] = slit.Gate.Code,
                ["slitAuthority"] = "paired-led-detector-fixed-median-composites",
            });
    }

    private async Task PublishGuideSelectionEvidenceAsync(
        ObservationContext context,
        G3FieldState field,
        GuideStarSelection selection,
        Phd2Point selected,
        string phase,
        CancellationToken cancellationToken)
    {
        var target = field.TargetIdentification.Target
            ?? throw new InvalidOperationException("Guide selection evidence requires a quality-gated target.");
        var candidate = selection.Star
            ?? throw new InvalidOperationException("Guide selection evidence requires a selected candidate.");
        await PublishRunJsonEvidenceAsync(
            "g3-guide-selection",
            phase,
            new
            {
                requestedTarget = new
                {
                    context.Plan.Target.Name,
                    context.Plan.Target.CatalogId,
                    context.Plan.Target.RightAscensionDegrees,
                    context.Plan.Target.DeclinationDegrees,
                },
                targetCentroid = new { target.Centroid.X, target.Centroid.Y },
                slit = new
                {
                    centerX = field.SlitDetection.Geometry.AcquisitionPoint.X,
                    centerY = field.SlitDetection.Geometry.AcquisitionPoint.Y,
                    field.SlitDetection.Geometry.AngleDegrees,
                    field.SlitDetection.Geometry.WidthPixels,
                    field.SlitDetection.Geometry.CalibrationId,
                },
                selectedCandidate = new
                {
                    candidate.Centroid.X,
                    candidate.Centroid.Y,
                    candidate.SignalToNoise,
                    candidate.FwhmPixels,
                    candidate.Ellipticity,
                    candidate.SaturatedFraction,
                    selection.Score,
                },
                phd2AcceptedPosition = new { selected.X, selected.Y },
                exclusionPolicy = "off-slit, off-target, non-edge, non-saturated, shape/SNR quality gated",
                phase,
            },
            field.FramePath,
            cancellationToken).ConfigureAwait(false);

        if (field.Image is not null)
        {
            var overlay = ObservationPreviewRenderer.RenderG3(
                field.Image,
                field.SlitDetection.Geometry,
                target.Centroid,
                new PixelPoint(selected.X, selected.Y));
            PublishRunPngEvidence(
                "g3-guide-selection-overlay",
                phase,
                overlay,
                new Dictionary<string, string>
                {
                    ["sourceFits"] = Path.GetFullPath(field.FramePath),
                    ["phase"] = phase,
                });
        }
    }

    private void ObserveQhySnapshot(QhyJobSnapshot snapshot)
    {
        qhyFrameTotals.AddOrUpdate(
            snapshot.Id,
            (snapshot.TotalFrameCount, snapshot.TotalAcceptedFrameCount),
            (_, current) => (
                Math.Max(current.Attempted, snapshot.TotalFrameCount),
                Math.Max(current.Accepted, snapshot.TotalAcceptedFrameCount)));
        foreach (var frame in snapshot.Frames)
        {
            if (!publishedQhyFrames.TryAdd(frame.FrameId, 0)) continue;
            host.PublishEvidence(
                snapshot.Kind == QhyJobKind.Acquisition ? "qhy-acquisition-fits" : "qhy-photometry-fits",
                frame.FitsPath,
                frame.Sha256,
                new Dictionary<string, string>
                {
                    ["jobId"] = snapshot.Id.ToString("D"),
                    ["frameId"] = frame.FrameId.ToString("D"),
                    ["sequenceNumber"] = frame.SequenceNumber.ToString(CultureInfo.InvariantCulture),
                    ["role"] = frame.Role,
                });
        }
        if (snapshot.State is QhyJobState.Completed or QhyJobState.Cancelled or QhyJobState.Faulted or QhyJobState.TakenOver)
        {
            PublishEvidencePathOnce("qhy-job-manifest", snapshot.ManifestPath, new Dictionary<string, string>
            {
                ["jobId"] = snapshot.Id.ToString("D"),
                ["state"] = snapshot.State.ToString(),
            });
            if (!string.IsNullOrWhiteSpace(snapshot.FrameIndexPath))
            {
                PublishEvidencePathOnce("qhy-frame-index", snapshot.FrameIndexPath, new Dictionary<string, string>
                {
                    ["jobId"] = snapshot.Id.ToString("D"),
                });
            }
        }
        PublishFrameCounters();
    }

    private void PublishEvidencePathOnce(
        string kind,
        string path,
        IReadOnlyDictionary<string, string>? metadata = null,
        string? knownSha256 = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch { return; }
        if (!publishedEvidencePaths.TryAdd(fullPath, 0)) return;
        host.PublishEvidence(kind, fullPath, knownSha256, metadata);
    }

    private void PublishFrameCounters()
    {
        var totals = qhyFrameTotals.Values.ToArray();
        host.PublishCounters(new ObservationRunCounters(
            AtrAttemptedFrames: Volatile.Read(ref attemptedAtrFrames),
            AtrAcceptedFrames: Volatile.Read(ref savedAtrFrames),
            QhyAttemptedFrames: totals.Sum(item => item.Attempted),
            QhyAcceptedFrames: totals.Sum(item => item.Accepted),
            Additional: new Dictionary<string, long>
            {
                ["atrScienceRetainedFrames"] = Volatile.Read(ref retainedAtrScienceFrames),
                ["atrProbeAttemptedFrames"] = Volatile.Read(ref attemptedAtrProbeFrames),
                ["atrProbeRetainedFrames"] = Volatile.Read(ref retainedAtrProbeFrames),
                ["atrProbeAcceptedFrames"] = Volatile.Read(ref acceptedAtrProbeFrames),
            }));
    }

    private async Task<AtrCapture> CaptureAtrImageAsync(
        ObservationContext context,
        double exposureSeconds,
        string imageType,
        string reason,
        CancellationToken cancellationToken)
    {
        var identity = ValidateAtrCameraIdentity(context.Plan.ExpectedAtrCameraId);
        if (identity.Disposition != GateDisposition.Passed) throw new InvalidOperationException(identity.Message);
        var sequence = new CaptureSequence
        {
            ExposureTime = exposureSeconds,
            ImageType = imageType,
            Binning = new BinningMode(configuration.Atr.Binning, configuration.Atr.Binning),
            Gain = configuration.Atr.Gain,
            Offset = configuration.Atr.Offset,
            TotalExposureCount = 1,
            Dither = false,
        };
        var exposure = await imagingMediator.CaptureImage(sequence, cancellationToken, progress, reason).ConfigureAwait(false);
        var image = await exposure.ToImageData(progress, cancellationToken).ConfigureAwait(false);
        image.MetaData.Target.Name = context.Plan.Target.Name;
        image.MetaData.Target.Coordinates = TargetCoordinates(context.Plan);
        var captureToken = Guid.NewGuid().ToString("N");
        image.MetaData.GenericHeaders.Add(new StringMetaDataHeader(
            "OBSRUNID",
            context.Plan.ObservationRunId,
            "UVEX-ADV observation run identifier"));
        image.MetaData.GenericHeaders.Add(new StringMetaDataHeader(
            "UVEXSTG",
            imageType,
            "UVEX-ADV acquisition role"));
        image.MetaData.GenericHeaders.Add(new StringMetaDataHeader(
            "UVEXCID",
            captureToken,
            "UVEX-ADV image-save correlation identifier"));
        image.MetaData.GenericHeaders.Add(new StringMetaDataHeader(
            "NIGHTSET",
            context.Plan.NightSetupId,
            "Locked UVEX-ADV Night Setup identifier"));
        var degradedSupervised = IsDegradedSupervisedScience();
        image.MetaData.GenericHeaders.Add(new StringMetaDataHeader(
            "UVEXDGR",
            degradedSupervised.ToString(CultureInfo.InvariantCulture),
            "UVEX-ADV degraded supervised science label"));
        if (phd2SlitPlacementSession is { } placementSession)
        {
            image.MetaData.GenericHeaders.Add(new StringMetaDataHeader(
                "PHD2GRD",
                placementSession.Quality.Grade.ToString(),
                "PHD2 calibration quality grade"));
        }
        var metrics = MeasureSpectralProbe(image, exposureSeconds);
        return new AtrCapture(image, metrics, captureToken, imageType);
    }

    private async Task<string> SaveAtrImageAsync(
        AtrCapture capture,
        int attemptNumber,
        bool qualityAccepted,
        GateDisposition qualityDisposition,
        string qualityCode,
        string qualityMessage,
        CancellationToken cancellationToken)
    {
        var saved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnImageSaved(object? _, ImageSavedEventArgs args)
        {
            var marker = args.MetaData.GenericHeaders
                .OfType<IGenericMetaDataHeader<string>>()
                .FirstOrDefault(header => string.Equals(header.Key, "UVEXCID", StringComparison.Ordinal));
            if (!string.Equals(marker?.Value, capture.CaptureToken, StringComparison.Ordinal)) return;
            if (!args.PathToImage.IsFile || string.IsNullOrWhiteSpace(args.PathToImage.LocalPath))
            {
                saved.TrySetException(new IOException("N.I.N.A. reported an ATR image without an absolute file path."));
                return;
            }
            saved.TrySetResult(Path.GetFullPath(args.PathToImage.LocalPath));
        }

        imageSaveMediator.ImageSaved += OnImageSaved;
        try
        {
            var rendered = Task.FromResult(capture.Image.RenderImage());
            await imageSaveMediator.Enqueue(capture.Image, rendered, progress, cancellationToken).ConfigureAwait(false);
            var path = await saved.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            var metrics = capture.Metrics;
            host.PublishEvidence(
                capture.Role == CaptureSequence.ImageTypes.LIGHT ? "atr-science-fits" : "atr-probe-fits",
                path,
                metadata: new Dictionary<string, string>
                {
                    ["captureId"] = capture.CaptureToken,
                    ["role"] = capture.Role,
                    ["attemptNumber"] = attemptNumber.ToString(CultureInfo.InvariantCulture),
                    ["qualityAccepted"] = qualityAccepted.ToString(CultureInfo.InvariantCulture),
                    ["qualityDisposition"] = qualityDisposition.ToString(),
                    ["qualityCode"] = qualityCode,
                    ["qualityMessage"] = qualityMessage,
                    ["exposureSeconds"] = metrics.ExposureSeconds.ToString("R", CultureInfo.InvariantCulture),
                    ["biasLevelAdu"] = metrics.BiasLevelAdu.ToString("R", CultureInfo.InvariantCulture),
                    ["highPercentileAdu"] = metrics.HighPercentileAdu.ToString("R", CultureInfo.InvariantCulture),
                    ["fullScaleAdu"] = metrics.FullScaleAdu.ToString("R", CultureInfo.InvariantCulture),
                    ["saturatedFraction"] = metrics.SaturatedFraction.ToString("R", CultureInfo.InvariantCulture),
                    ["continuumSnrPerResolutionElement"] = metrics.ContinuumSnrPerResolutionElement.ToString("R", CultureInfo.InvariantCulture),
                    ["lineSnrPerResolutionElement"] = metrics.LineSnrPerResolutionElement.ToString("R", CultureInfo.InvariantCulture),
                    ["targetToSkyContrast"] = metrics.TargetToSkyContrast.ToString("R", CultureInfo.InvariantCulture),
                    ["guidingStable"] = metrics.GuidingStable.ToString(CultureInfo.InvariantCulture),
                    ["degradedSupervisedScience"] = IsDegradedSupervisedScience().ToString(CultureInfo.InvariantCulture),
                    ["phd2CalibrationGrade"] = phd2SlitPlacementSession?.Quality.Grade.ToString() ?? "legacy-independent",
                    ["phd2IsUnattendedScienceAuthority"] = (phd2SlitPlacementSession is { } session &&
                        IsUnattendedPhd2ScienceAuthority(session.Quality, session.GuideMode)).ToString(CultureInfo.InvariantCulture),
                });
            return path;
        }
        finally
        {
            imageSaveMediator.ImageSaved -= OnImageSaved;
        }
    }

    private SpectralProbeMetrics MeasureSpectralProbe(IImageData image, double exposureSeconds)
    {
        var properties = image.Properties;
        var values = image.Data.FlatArray;
        if (values.Length != properties.Width * properties.Height) throw new InvalidOperationException("ATR image buffer dimensions do not match.");
        var roi = configuration.Atr.Roi;
        roi.Validate(properties.Width, properties.Height);
        var sample = new List<double>(Math.Min(250_000, roi.Width * roi.Height));
        var stride = Math.Max(1, (roi.Width * roi.Height) / 250_000);
        var counter = 0;
        var saturated = 0L;
        var total = 0L;
        var fullScale = Math.Pow(2, Math.Clamp(properties.BitDepth, 1, 16)) - 1;
        for (var y = roi.Y; y < roi.Y + roi.Height; y++)
            for (var x = roi.X; x < roi.X + roi.Width; x++)
            {
                var value = values[y * properties.Width + x];
                if (value >= fullScale * 0.999) saturated++;
                if (counter++ % stride == 0) sample.Add(value);
                total++;
            }
        sample.Sort();
        var median = Percentile(sample, 0.5);
        var p90 = Percentile(sample, 0.9);
        var p999 = Percentile(sample, 0.999);
        var deviations = sample.Select(value => Math.Abs(value - median)).OrderBy(value => value).ToArray();
        var sigma = Math.Max(1, Percentile(deviations, 0.5) * 1.4826);
        var continuumSnr = Math.Max(0, (p90 - median) / sigma);
        var lineSnr = Math.Max(0, (p999 - median) / sigma);
        var contrast = median > 0 ? p90 / median : 0;
        return new SpectralProbeMetrics(
            exposureSeconds,
            median,
            p999,
            fullScale,
            saturated / (double)Math.Max(1, total),
            continuumSnr,
            lineSnr,
            contrast,
            IsGuidingStable(),
            Guid.NewGuid().ToString("N"));
    }

    private GateResult ValidateAtrScienceMetrics(SpectralProbeMetrics metrics)
    {
        if (metrics.SaturatedFraction > configuration.Atr.MaximumSaturatedFraction) return GateResult.Fail("ATR_SATURATION_LIMIT", $"ATR science frame saturated fraction {metrics.SaturatedFraction:P3} exceeds {configuration.Atr.MaximumSaturatedFraction:P3}.");
        if (metrics.TargetToSkyContrast < configuration.Atr.MinimumTargetToSkyContrast) return GateResult.Unknown("ATR_TARGET_CONTRAST_LOW", $"ATR target/sky proxy {metrics.TargetToSkyContrast:F2} is below {configuration.Atr.MinimumTargetToSkyContrast:F2}.");
        if (metrics.LineSnrPerResolutionElement < configuration.Atr.MinimumLineSnr && metrics.ContinuumSnrPerResolutionElement < configuration.Atr.MinimumContinuumSnr) return GateResult.Unknown("ATR_SNR_LOW", "ATR science frame has insufficient continuum and line SNR proxies.");
        return GateResult.Pass("ATR_FRAME_QUALITY_VALID", "ATR science frame passed saturation, contrast and SNR gates.");
    }

    private GateResult ValidateAtrCameraIdentity(string expectedDeviceId)
    {
        var camera = cameraMediator.GetInfo();
        if (!camera.Connected) return GateResult.Unknown("ATR_NOT_CONNECTED", "N.I.N.A. ATR camera is disconnected.");
        if (!string.Equals(camera.DeviceId, expectedDeviceId, StringComparison.Ordinal)) return GateResult.Fail("ATR_IDENTITY_CHANGED", $"ATR DeviceId changed to '{camera.DeviceId}'.");
        return GateResult.Pass("ATR_IDENTITY_VALID", "N.I.N.A. still owns the bound ATR585M.");
    }

    private bool IsGuidingStable()
    {
        var snapshot = phd2.Snapshot;
        return snapshot.HasCurrentSuccessfulSettle &&
            validatedG3GuideConnectionEpoch == snapshot.ConnectionEpoch &&
            validatedG3GuideEpoch == snapshot.GuideEpoch &&
            phd2SlitPlacementSession is not null &&
            (IsUnattendedPhd2ScienceAuthority(phd2SlitPlacementSession.Quality, phd2SlitPlacementSession.GuideMode) ||
                (configuration.AllowDegradedSupervisedScience &&
                    phd2SlitPlacementSession.Quality.IsLockShiftAuthority));
    }

    private bool IsDegradedSupervisedScience() =>
        configuration.AllowDegradedSupervisedScience &&
        phd2SlitPlacementSession is { Quality.IsLockShiftAuthority: true } session &&
        (RequiresSupervisedPhd2Science(session.Quality, session.GuideMode) ||
            !IsUnattendedPhd2ScienceAuthority(session.Quality, session.GuideMode));

    private async Task<GateResult> CheckPhotometryHealthAsync(
        ObservationContext context,
        CancellationToken cancellationToken)
    {
        if (!qhyLeaseFailures.IsEmpty)
        {
            return GateResult.Unknown("QHY_CONTROL_LEASE_UNHEALTHY", string.Join(" ", qhyLeaseFailures.Values));
        }
        if (photometryJobId is not { } jobId) return GateResult.Unknown("QHY_PHOTOMETRY_MISSING", "No synchronized QHY photometry job is registered.");
        var snapshot = await qhy.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null) return GateResult.Unknown("QHY_PHOTOMETRY_LOST", $"QHY photometry job {jobId:D} disappeared.");
        await PublishQhyPreviewAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (snapshot.State == QhyJobState.Completed)
        {
            // The ATR block is still inside its acceptance loop, so a completed
            // photometry job cannot cover the next spectrum. Start a new
            // idempotent continuation before another ATR shutter-open action.
            var continuation = await StartQhyPhotometryAsync(context, cancellationToken).ConfigureAwait(false);
            return continuation.Gate;
        }
        return snapshot.State switch
        {
            QhyJobState.Running or QhyJobState.Queued => GateResult.Pass("QHY_PHOTOMETRY_HEALTHY", $"QHY photometry is {snapshot.State} with {snapshot.Frames.Count} immutable FITS."),
            QhyJobState.PausedNeedsAttention => GateResult.Unknown("QHY_PHOTOMETRY_NEEDS_ATTENTION", snapshot.AttentionReason ?? "QHY photometry quality gate failed."),
            QhyJobState.Paused or QhyJobState.Pausing => GateResult.Unknown("QHY_PHOTOMETRY_PAUSED", "QHY photometry is paused."),
            _ => GateResult.Fail("QHY_PHOTOMETRY_FAILED", snapshot.Error ?? $"QHY photometry is {snapshot.State}.")
        };
    }

    private void MarkResumeRecoveryRequired()
    {
        var rawStage = Volatile.Read(ref executingStage);
        if (rawStage < (int)ObservationStage.SlewToCatalogTarget ||
            rawStage > (int)ObservationStage.RunScienceBlock)
        {
            return;
        }

        var stage = (ObservationStage)rawStage;
        var recovery = ObservationResumeRecoveryPolicy.ForStage(stage);
        if (recovery.InvalidateQhySolution)
        {
            qhyAcquisitionJobId = null;
            qhyAcquisitionMountReadbackJobId = null;
            qhyAcquisitionBeforeJobMountReadback = null;
            lastQhyAcquisition = null;
            lastQhySolve = null;
            lastQhySolveMountBinding = null;
            lastQhyAcceptedFrameMountBinding = null;
            qhyAcquisitionAttempt++;
        }
        if (recovery.InvalidateG3AndGuideEpoch)
        {
            lastG3Field = null;
            validatedG3GuideConnectionEpoch = null;
            validatedG3GuideEpoch = null;
        }
        Interlocked.Exchange(ref resumeRecoveryRequired, 1);
    }

    private async Task<StageResult?> RecoverInterruptedStageAsync(
        ObservationStage stage,
        ObservationContext context,
        bool recoveryRequired,
        CancellationToken cancellationToken)
    {
        if (!recoveryRequired) return null;

        var recovery = ObservationResumeRecoveryPolicy.ForStage(stage);
        await WriteAuditBestEffortAsync("resume-stage-recovery-started", new
        {
            stage = stage.ToString(),
            recovery.ReacquireQhy,
            recovery.ReacquireG3,
            recovery.ReplaceTargetOnSlit,
            recovery.RestartGuiding,
            recovery.RestorePhotometry,
            oldG3AndGuideEpochDiscarded = recovery.InvalidateG3AndGuideEpoch,
        }).ConfigureAwait(false);

        if (!recovery.RequiresPreStageRecovery) return null;

        if (recovery.ReacquireQhy)
        {
            var qhyField = await AcquireQhyWideFieldAsync(context, cancellationToken).ConfigureAwait(false);
            if (!qhyField.CanAdvance) return qhyField;
        }
        if (recovery.ReacquireG3)
        {
            var g3 = await AcquireG3SlitFieldAsync(context, cancellationToken).ConfigureAwait(false);
            if (!g3.CanAdvance) return g3;
        }
        if (recovery.ReplaceTargetOnSlit)
        {
            var slit = await PlaceTargetOnSlitAsync(context, cancellationToken).ConfigureAwait(false);
            if (!slit.CanAdvance) return slit;
        }
        if (recovery.RestartGuiding)
        {
            var guiding = await StartGuidingAsync(context, cancellationToken).ConfigureAwait(false);
            if (!guiding.CanAdvance) return guiding;
        }
        if (recovery.RestorePhotometry)
        {
            var photometry = await RestorePhotometryAfterResumeAsync(context, cancellationToken).ConfigureAwait(false);
            if (!photometry.CanAdvance) return photometry;
        }

        await WriteAuditBestEffortAsync("resume-stage-recovery-completed", new
        {
            stage = stage.ToString(),
            phd2ConnectionEpoch = validatedG3GuideConnectionEpoch,
            phd2GuideEpoch = validatedG3GuideEpoch,
            photometryJobId,
        }).ConfigureAwait(false);
        return null;
    }

    private async Task<StageResult> RestorePhotometryAfterResumeAsync(
        ObservationContext context,
        CancellationToken cancellationToken)
    {
        if (photometryJobId is { } existing)
        {
            var current = await qhy.GetJobAsync(existing, cancellationToken).ConfigureAwait(false);
            if (current is not null) ObserveQhySnapshot(current);
            if (current is null)
            {
                return Attention(
                    ObservationStage.StartQhyPhotometry,
                    "QHY_PHOTOMETRY_JOB_LOST",
                    $"QHY photometry job {existing:D} disappeared during pause recovery.");
            }

            if (current.State is QhyJobState.Completed or QhyJobState.Cancelled or QhyJobState.Faulted or QhyJobState.TakenOver)
            {
                activeQhyJobs.TryRemove(existing, out _);
                photometryJobId = null;
                qhyPhotometryAttempt++;
            }
            else if (current.State is QhyJobState.Paused or QhyJobState.PausedNeedsAttention)
            {
                await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
                var resumed = await qhy.ResumeAsync(existing, cancellationToken).ConfigureAwait(false);
                ObserveQhySnapshot(resumed);
                if (resumed.State is QhyJobState.Running or QhyJobState.Queued)
                {
                    return Passed(
                        "QHY_PHOTOMETRY_RESUMED_AFTER_GUIDE_RECOVERY",
                        $"QHY photometry job {existing:D} resumed only after fresh G3/slit/guide evidence passed.");
                }
                if (resumed.State is QhyJobState.Completed or QhyJobState.Cancelled or QhyJobState.Faulted or QhyJobState.TakenOver)
                {
                    activeQhyJobs.TryRemove(existing, out _);
                    photometryJobId = null;
                    qhyPhotometryAttempt++;
                }
                else
                {
                    return Attention(
                        ObservationStage.StartQhyPhotometry,
                        "QHY_PHOTOMETRY_RESUME_UNCONFIRMED",
                        $"QHY photometry job {existing:D} returned {resumed.State} after resume.");
                }
            }
            else if (current.State is QhyJobState.Running or QhyJobState.Queued)
            {
                return Passed(
                    "QHY_PHOTOMETRY_RUNNING_AFTER_GUIDE_RECOVERY",
                    $"QHY photometry job {existing:D} is {current.State} after fresh G3/slit/guide evidence passed.");
            }
            else
            {
                return Attention(
                    ObservationStage.StartQhyPhotometry,
                    "QHY_PHOTOMETRY_RESUME_UNCONFIRMED",
                    $"QHY photometry job {existing:D} is {current.State} after pause recovery.");
            }
        }

        // Completed, cancelled, faulted and taken-over jobs are immutable terminal
        // records. A new idempotency attempt provides continuation coverage rather
        // than reusing a stale terminal job as if it were still active.
        return await StartQhyPhotometryAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateConfirmedPhdStop(Phd2StopCaptureResult result, string operation)
    {
        if (!result.ConfirmedIdle || result.FinalState is not (Phd2AppState.Stopped or Phd2AppState.Selected))
        {
            throw new InvalidOperationException(
                $"PHD2 {operation} did not prove idle; confirmed={result.ConfirmedIdle}, final={result.FinalState}.");
        }
    }

    private GateResult ValidateCorrectionBudget(MotionLimits limits, double requestedDegrees)
    {
        if (commissioning is null) return GateResult.Unknown("COMMISSIONING_PRESET_REQUIRED", "No trusted commissioning preset is loaded.");
        fineAcquisitionStartedUtc ??= DateTimeOffset.UtcNow;
        if (DateTimeOffset.UtcNow - fineAcquisitionStartedUtc.Value > limits.EffectiveMaximumAcquisitionTime)
        {
            return GateResult.Fail("ACQUISITION_TIME_LIMIT", $"Acquisition/correction elapsed time exceeded {limits.EffectiveMaximumAcquisitionTime.TotalMinutes:F1} minutes.");
        }
        if (correctionAttempts >= limits.MaximumCorrectionAttempts)
        {
            return GateResult.Fail("CORRECTION_ATTEMPT_LIMIT", $"Correction attempt limit {limits.MaximumCorrectionAttempts} has been reached.");
        }
        if (!double.IsFinite(requestedDegrees) || requestedDegrees <= 0)
        {
            return GateResult.Fail("CORRECTION_INVALID", "Requested correction magnitude is not positive and finite.");
        }
        if (requestedDegrees > limits.MaximumSingleCorrectionDegrees)
        {
            return GateResult.Fail("MOTION_SINGLE_LIMIT", $"Requested correction {requestedDegrees * 3600:F2} arcsec exceeds {limits.MaximumSingleCorrectionDegrees * 3600:F2} arcsec.");
        }
        if (cumulativeCorrectionDegrees + requestedDegrees > limits.MaximumCumulativeCorrectionDegrees)
        {
            return GateResult.Fail("MOTION_CUMULATIVE_LIMIT", $"Correction would exceed cumulative limit {limits.MaximumCumulativeCorrectionDegrees * 3600:F2} arcsec.");
        }
        return GateResult.Pass("MOTION_BUDGET_VALID", "Commissioned single, cumulative, attempt and elapsed-time limits passed.");
    }

    private void RegisterCorrection(double magnitudeDegrees)
    {
        cumulativeCorrectionDegrees += magnitudeDegrees;
        correctionAttempts++;
    }

    private async Task<GateResult> InvalidateCommissioningAsync(string code, string reason)
    {
        var sha256 = commissioning?.Sha256 ?? configuration.Commissioning.PresetSha256;
        invalidatedCommissioningSha256 ??= sha256;
        commissioningInvalidReason ??= reason;
        commissioning = null;
        await WriteAuditBestEffortAsync("commissioning-preset-invalidated", new
        {
            code,
            reason,
            sha256 = invalidatedCommissioningSha256,
            permanentForCurrentRun = true,
            resumeMayNotReuse = true,
        }).ConfigureAwait(false);
        return GateResult.Fail(code, $"{reason} The loaded commissioning preset is permanently invalid for this run; Resume cannot reuse it.");
    }

    private void InvalidateStageState(ObservationStage stage)
    {
        if (stage <= ObservationStage.CoarseCenter)
        {
            qhyAcquisitionJobId = null;
            qhyAcquisitionMountReadbackJobId = null;
            qhyAcquisitionBeforeJobMountReadback = null;
            lastQhyAcquisition = null;
            lastQhySolve = null;
            lastQhySolveMountBinding = null;
            lastQhyAcceptedFrameMountBinding = null;
            qhyAcquisitionAttempt++;
        }
        if (stage is ObservationStage.AcquireG3SlitField or ObservationStage.PlaceTargetOnSlit or ObservationStage.StartGuiding)
        {
            lastG3Field = null;
        }
        if (stage >= ObservationStage.StartQhyPhotometry && stage <= ObservationStage.RunScienceBlock)
        {
            photometryJobId = null;
            qhyPhotometryAttempt++;
        }
        if (stage is ObservationStage.SelectAtrExposure or ObservationStage.RunScienceBlock)
        {
            selectedAtrExposureSeconds = null;
            atrReprobeRequired = true;
        }
    }

    private void UpdateRemainingScienceDuration(ObservationContext context, double exposureSeconds)
    {
        // Covers every still-available bounded attempt, N.I.N.A. save/metadata
        // latency, guide/slit revalidation margin, and checked finalization.
        var remainingAttempts = Math.Max(1, configuration.Atr.MaximumScienceAttempts - attemptedAtrFrames);
        var seconds = remainingAttempts * (exposureSeconds + 45d) + 120d;
        context.RemainingWorstCaseDuration = TimeSpan.FromSeconds(seconds);
    }

    private void RegisterActiveQhyJob(QhyJobSnapshot snapshot)
    {
        ObserveQhySnapshot(snapshot);
        if (snapshot.State is QhyJobState.Completed or QhyJobState.Cancelled or QhyJobState.Faulted or QhyJobState.TakenOver) return;
        if (!qhy.HasOwnerSession(snapshot.Id) || snapshot.LeaseExpiresUtc is null)
        {
            throw new InvalidOperationException($"QHY job {snapshot.Id:D} has no private in-memory owner session; it cannot be controlled by the real runner.");
        }
        activeQhyJobs.TryAdd(snapshot.Id, 0);
        qhyLeaseFailures.TryRemove(snapshot.Id, out _);
    }

    private async Task BeginSlitIlluminationSequenceAsync(
        string sequenceId,
        CancellationToken cancellationToken)
    {
        UvexServiceClient? client = new(configuration.UvexServiceUrl);
        UvexServiceClient.UvexLeaseSession? lease = null;
        try
        {
            lease = await client.AcquireLeaseAsync(
                $"nina-g3-slit-{observationRunId ?? "unbound"}-{sequenceId}",
                cancellationToken).ConfigureAwait(false);
            await slitIlluminationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (activeSlitIlluminationLease is not null)
                {
                    throw new InvalidOperationException(
                        $"Slit-illumination sequence '{activeSlitIlluminationSequenceId}' still owns the UVEX lease.");
                }
                if (!slitIlluminationEvidence.TryAdd(
                    sequenceId,
                    new ConcurrentQueue<G3SlitIlluminationCommandEvidence>()))
                {
                    throw new InvalidOperationException(
                        $"Slit-illumination evidence sequence '{sequenceId}' already exists.");
                }
                activeSlitIlluminationClient = client;
                activeSlitIlluminationLease = lease;
                activeSlitIlluminationSequenceId = sequenceId;
                client = null;
                lease = null;
            }
            finally
            {
                slitIlluminationGate.Release();
            }
        }
        finally
        {
            if (lease is not null)
            {
                try { await lease.DisposeAsync().ConfigureAwait(false); }
                catch { /* No command was issued through this rejected lease. */ }
            }
            client?.Dispose();
        }
    }

    private async Task<UvexDeviceStatus> CommandActiveSlitIlluminationAsync(
        bool enabled,
        string phase,
        CancellationToken cancellationToken)
    {
        await slitIlluminationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lease = activeSlitIlluminationLease
                ?? throw new InvalidOperationException(
                    $"Cannot command slit illumination {phase}; no active UVEX lease remains.");
            var status = await lease.SetSlitIlluminationAsync(enabled, cancellationToken).ConfigureAwait(false);
            if (activeSlitIlluminationSequenceId is { } sequenceId &&
                slitIlluminationEvidence.TryGetValue(sequenceId, out var evidence))
            {
                evidence.Enqueue(G3SlitIlluminationCommandEvidence.FromStatus(phase, enabled, status));
            }
            await WriteAuditBestEffortAsync("g3-slit-illumination-command", new
            {
                sequenceId = activeSlitIlluminationSequenceId,
                phase,
                enabled,
                ledState = status.SlitIlluminationLedState.ToString(),
                status.SlitIlluminationLedCommandedUtc,
                status.SlitPhotodiodeValue,
                status.SlitPhotodiodeThreshold,
                status.SlitPhotodiodeEnabled,
                status.TimestampUtc,
            }).ConfigureAwait(false);
            return status;
        }
        finally
        {
            slitIlluminationGate.Release();
        }
    }

    private async Task<SlitIlluminationOffAttempt> EnsureSlitIlluminationOffAsync(
        string reason,
        bool releaseLeaseOnSuccess,
        CancellationToken cancellationToken)
    {
        await slitIlluminationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (activeSlitIlluminationLease is null)
            {
                return new SlitIlluminationOffAttempt(null, null);
            }

            var sequenceId = activeSlitIlluminationSequenceId;
            UvexDeviceStatus status;
            try
            {
                status = await activeSlitIlluminationLease
                    .SetSlitIlluminationAsync(enabled: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var issue =
                    $"UVEX slit illumination OFF was not command-completed/readback-verified for sequence '{sequenceId}' during {reason}: {ex.Message}";
                Volatile.Write(ref slitIlluminationSafetyIssue, issue);
                await WriteAuditBestEffortAsync("g3-slit-illumination-off-unverified", new
                {
                    sequenceId,
                    reason,
                    exception = ex.ToString(),
                    pausedNeedsAttention = true,
                }).ConfigureAwait(false);
                // Retain the live lease so cancellation/fault/pause cleanup can
                // make another checked OFF attempt rather than merely releasing
                // an output whose state is unknown.
                return new SlitIlluminationOffAttempt(null, issue);
            }

            string? releaseIssue = null;
            if (sequenceId is not null &&
                slitIlluminationEvidence.TryGetValue(sequenceId, out var evidence))
            {
                evidence.Enqueue(G3SlitIlluminationCommandEvidence.FromStatus(
                    $"safety-off:{reason}",
                    false,
                    status));
            }
            if (releaseLeaseOnSuccess)
            {
                var lease = activeSlitIlluminationLease;
                var client = activeSlitIlluminationClient;
                activeSlitIlluminationLease = null;
                activeSlitIlluminationClient = null;
                activeSlitIlluminationSequenceId = null;
                try
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    releaseIssue =
                        $"UVEX slit illumination is verified OFF, but its sequence lease could not be cleanly released during {reason}: {ex.Message}";
                }
                finally
                {
                    client?.Dispose();
                }
            }

            await WriteAuditBestEffortAsync("g3-slit-illumination-off-verified", new
            {
                sequenceId,
                reason,
                status.SlitIlluminationLedCommandedUtc,
                status.SlitPhotodiodeValue,
                status.SlitPhotodiodeThreshold,
                status.SlitPhotodiodeEnabled,
                status.TimestampUtc,
                leaseReleased = releaseLeaseOnSuccess && releaseIssue is null,
                releaseIssue,
            }).ConfigureAwait(false);
            Volatile.Write(ref slitIlluminationSafetyIssue, releaseIssue);
            return new SlitIlluminationOffAttempt(status, releaseIssue);
        }
        finally
        {
            slitIlluminationGate.Release();
        }
    }

    private async Task DisposeActiveSlitIlluminationResourcesAsync()
    {
        await slitIlluminationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var lease = activeSlitIlluminationLease;
            var client = activeSlitIlluminationClient;
            activeSlitIlluminationLease = null;
            activeSlitIlluminationClient = null;
            activeSlitIlluminationSequenceId = null;
            if (lease is not null)
            {
                try { await lease.DisposeAsync().ConfigureAwait(false); }
                catch { /* The preceding checked OFF cleanup records the safety failure. */ }
            }
            client?.Dispose();
        }
        finally
        {
            slitIlluminationGate.Release();
        }
    }

    private async Task RenewQhyLeasesAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
            foreach (var id in activeQhyJobs.Keys.ToArray())
            {
                try
                {
                    var snapshot = await qhy.GetJobAsync(id, cancellationToken).ConfigureAwait(false);
                    if (snapshot is null)
                    {
                        qhyLeaseFailures[id] = $"QHY job {id:D} disappeared while renewing its lease.";
                        continue;
                    }
                    ObserveQhySnapshot(snapshot);
                    if (snapshot.State is QhyJobState.Completed or QhyJobState.Cancelled or QhyJobState.Faulted or QhyJobState.TakenOver)
                    {
                        activeQhyJobs.TryRemove(id, out _);
                        qhyLeaseFailures.TryRemove(id, out _);
                        continue;
                    }
                    if (!qhy.HasOwnerSession(id))
                    {
                        qhyLeaseFailures[id] = $"QHY job {id:D} no longer has its private in-memory owner session.";
                        continue;
                    }
                    var renewed = await qhy.RenewLeaseAsync(id, 120, cancellationToken).ConfigureAwait(false);
                    ObserveQhySnapshot(renewed);
                    if (renewed.LeaseExpiresUtc is null || renewed.LeaseExpiresUtc <= DateTimeOffset.UtcNow.AddSeconds(30))
                    {
                        qhyLeaseFailures[id] = $"QHY job {id:D} returned an invalid lease expiry.";
                    }
                    else
                    {
                        qhyLeaseFailures.TryRemove(id, out _);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    qhyLeaseFailures[id] = $"QHY job {id:D} lease renewal failed: {ex.Message}";
                }
            }
        }
    }

    private async Task<IReadOnlyList<string>> CleanupAfterFailureAsync(
        string reason,
        CancellationToken cancellationToken,
        bool allowMechanicalActions)
    {
        var failures = new List<string>();
        var slitOff = await EnsureSlitIlluminationOffAsync(
            reason,
            releaseLeaseOnSuccess: true,
            cancellationToken).ConfigureAwait(false);
        if (slitOff.Issue is not null) failures.Add(slitOff.Issue);
        foreach (var pending in pendingQhyRequests.Values.ToArray())
        {
            using var recovery = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            recovery.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var discovered = pending.Request switch
                {
                    AcquisitionJobRequest acquisition =>
                        await qhy.RecoverAndCancelAcceptedStartAsync(acquisition, recovery.Token).ConfigureAwait(false),
                    PhotometryJobRequest photometry =>
                        await qhy.RecoverAndCancelAcceptedStartAsync(photometry, recovery.Token).ConfigureAwait(false),
                    _ => throw new InvalidOperationException(
                        $"QHY pending request {pending.ClientRequestId} has unsupported type {pending.Request.GetType().Name}."),
                };
                if (discovered is not null)
                {
                    ObserveQhySnapshot(discovered);
                    if (discovered.State is QhyJobState.Completed or QhyJobState.Cancelled or QhyJobState.Faulted or QhyJobState.TakenOver)
                    {
                        activeQhyJobs.TryRemove(discovered.Id, out _);
                    }
                }
                pendingQhyRequests.TryRemove(pending.ClientRequestId, out _);
            }
            catch (Exception ex)
            {
                failures.Add($"QHY request {pending.ClientRequestId}: recovery failed: {ex.Message}");
            }
        }
        foreach (var id in activeQhyJobs.Keys.ToArray())
        {
            try
            {
                var state = await qhy.GetJobAsync(id, cancellationToken).ConfigureAwait(false);
                if (state is not null) ObserveQhySnapshot(state);
                if (state is not null && state.State is not (QhyJobState.Completed or QhyJobState.Cancelled or QhyJobState.Faulted or QhyJobState.TakenOver))
                {
                    var cancelling = await qhy.CancelAsync(id, cancellationToken).ConfigureAwait(false);
                    ObserveQhySnapshot(cancelling);
                    state = await qhy.WaitForCheckedTerminalAsync(
                        id,
                        TimeSpan.FromSeconds(15),
                        observed =>
                        {
                            ObserveQhySnapshot(observed);
                            return Task.CompletedTask;
                        },
                        cancellationToken).ConfigureAwait(false);
                    ObserveQhySnapshot(state);
                }
                if (state?.State is QhyJobState.Completed or QhyJobState.Cancelled or QhyJobState.Faulted or QhyJobState.TakenOver)
                {
                    activeQhyJobs.TryRemove(id, out _);
                }
                else
                {
                    failures.Add($"QHY job {id:D} did not reach a checked terminal state.");
                }
            }
            catch (Exception ex) { failures.Add($"QHY job {id:D}: {ex.Message}"); }
        }

        if (phd2.IsConnected)
        {
            try
            {
                using var phdSafetyStop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var stopped = await phd2.StopCaptureAndConfirmAsync(phdSafetyStop.Token).ConfigureAwait(false);
                ValidateConfirmedPhdStop(stopped, "cleanup");
            }
            catch (Exception ex) { failures.Add($"PHD2 cleanup: {ex.Message}"); }
        }
        else if (Volatile.Read(ref phd2GuidingEverStarted) != 0)
        {
            failures.Add("PHD2 cleanup could not confirm Stopped/Selected because the event-server connection was lost after guiding started.");
        }
        if (allowMechanicalActions && configuration.Environment.CloseOpticalCoverOnFailure)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var coverRecovery = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            coverRecovery.CancelAfter(TimeSpan.FromSeconds(configuration.Environment.OpticalCoverTransitionTimeoutSeconds + 5));
            try
            {
                var coverIssue = await CloseOpticalCoverAsync(reason, coverRecovery.Token).ConfigureAwait(false);
                if (coverIssue is not null) failures.Add(coverIssue);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                failures.Add("Optical cover close exceeded its bounded recovery timeout.");
            }
        }

        await WriteAuditBestEffortAsync("real-run-cleanup", new
        {
            reason,
            failures,
            phd2Connected = phd2.IsConnected,
            phd2State = phd2.Snapshot.AppState.ToString(),
            remainingQhyJobs = activeQhyJobs.Keys.ToArray(),
            allowMechanicalActions,
            opticalCoverCloseAttempted = allowMechanicalActions && configuration.Environment.CloseOpticalCoverOnFailure,
        }).ConfigureAwait(false);
        PublishFrameCounters();
        return failures;
    }

    private async Task StopPhdAndWaitAsync(CancellationToken cancellationToken)
    {
        var stopped = await phd2.StopCaptureAndConfirmAsync(cancellationToken).ConfigureAwait(false);
        ValidateConfirmedPhdStop(stopped, "stop");
    }

    private static Coordinates TargetCoordinates(ObservationPlan plan) => new(
        plan.Target.RightAscensionDegrees,
        plan.Target.DeclinationDegrees,
        Epoch.J2000,
        Coordinates.RAType.Degrees);

    private static Coordinates ApplySkyCorrection(Coordinates current, double raArcseconds, double decArcseconds)
    {
        var cosDec = Math.Cos(current.Dec * Math.PI / 180d);
        if (Math.Abs(cosDec) < 1e-6) throw new InvalidOperationException("RA correction is singular near the celestial pole.");
        var ra = NormalizeDegrees(current.RADegrees + raArcseconds / (3600d * cosDec));
        var dec = current.Dec + decArcseconds / 3600d;
        if (dec is < -90 or > 90) throw new InvalidOperationException("Bounded correction would leave the declination range.");
        return new Coordinates(ra, dec, current.Epoch, Coordinates.RAType.Degrees);
    }

    private QhyPendingCoarseReturn ReanchorQhyCoarseStateFromReportedPosition(QhyPendingCoarseReturn state)
    {
        var reported = telescopeMediator.GetCurrentPosition();
        EnsureFiniteReportedCoordinates(reported);
        var (ra, dec) = SignedTangentOffsetArcseconds(state.Origin, reported);
        return state with
        {
            CurrentRaTangentOffsetArcseconds = ra,
            CurrentDeclinationOffsetArcseconds = dec,
        };
    }

    private G3PendingSearchReturn ReanchorG3SearchStateFromReportedPosition(G3PendingSearchReturn state)
    {
        var reported = telescopeMediator.GetCurrentPosition();
        EnsureFiniteReportedCoordinates(reported);
        var (ra, dec) = G3AcquisitionMotionPlanner.SignedTangentOffsetArcseconds(
            NormalizeDegrees(state.Origin.RADegrees),
            state.Origin.Dec,
            NormalizeDegrees(reported.RADegrees),
            reported.Dec);
        if (!double.IsFinite(ra) || !double.IsFinite(dec))
        {
            throw new InvalidOperationException("The reported G3 search coordinate cannot be represented in the versioned TAN projection.");
        }
        return state with
        {
            CurrentRaTangentOffsetArcseconds = ra,
            CurrentDeclinationOffsetArcseconds = dec,
        };
    }

    private static void EnsureFiniteReportedCoordinates(Coordinates reported)
    {
        if (!double.IsFinite(reported.RADegrees) || reported.RADegrees is < 0 or >= 360 ||
            !double.IsFinite(reported.Dec) || reported.Dec is < -90 or > 90)
        {
            throw new InvalidOperationException("The mount did not report a finite in-range coordinate.");
        }
    }

    private static double AngularSeparationArcseconds(Coordinates a, Coordinates b)
    {
        var ra1 = a.RADegrees * Math.PI / 180d;
        var ra2 = b.RADegrees * Math.PI / 180d;
        var dec1 = a.Dec * Math.PI / 180d;
        var dec2 = b.Dec * Math.PI / 180d;
        var cosine = Math.Sin(dec1) * Math.Sin(dec2) + Math.Cos(dec1) * Math.Cos(dec2) * Math.Cos(ra1 - ra2);
        return Math.Acos(Math.Clamp(cosine, -1, 1)) * 180d / Math.PI * 3600d;
    }

    private static (double RaTangentArcseconds, double DecArcseconds) SignedTangentOffsetArcseconds(
        Coordinates solvedCenter,
        Coordinates target)
    {
        var deltaRaDegrees = target.RADegrees - solvedCenter.RADegrees;
        if (deltaRaDegrees > 180) deltaRaDegrees -= 360;
        if (deltaRaDegrees < -180) deltaRaDegrees += 360;
        var referenceDecRadians = (target.Dec + solvedCenter.Dec) * 0.5 * Math.PI / 180d;
        return (
            deltaRaDegrees * Math.Cos(referenceDecRadians) * 3600d,
            (target.Dec - solvedCenter.Dec) * 3600d);
    }

    private static double Distance(PixelPoint a, PixelPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static async Task<string> ComputeFileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Immutable evidence file is missing.", fullPath);
        }
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1 << 20,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
    }

    private static TargetIdentification EmptyTargetIdentification() => new(
        GateResult.Unknown("TARGET_NOT_ANALYZED", "Target was not analyzed."),
        null,
        new PixelPoint(double.NaN, double.NaN),
        double.NaN,
        0);

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0;
        var index = Math.Clamp(percentile, 0, 1) * (sorted.Count - 1);
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        return lower == upper ? sorted[lower] : sorted[lower] + (index - lower) * (sorted[upper] - sorted[lower]);
    }

    private static BitmapSource LoadPng(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private static string SanitizeRunPathSegment(string value)
    {
        var sanitized = SanitizePathSegment(value).TrimEnd(' ', '.');
        var reservedBase = sanitized.Split('.', 2)[0];
        var reserved = reservedBase.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            reservedBase.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            reservedBase.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            reservedBase.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (reservedBase.Length == 4 &&
             (reservedBase.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
              reservedBase.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
             reservedBase[3] is >= '1' and <= '9');
        if (sanitized.Length > 0 && sanitized is not "." and not ".." && !reserved) return sanitized;
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        return "run-" + hash[..16];
    }

    private async Task<string> PublishRunJsonEvidenceAsync(
        string kind,
        string label,
        object payload,
        string? sourcePath,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? dashboardMetadata = null)
    {
        var runId = observationRunId ?? throw new InvalidOperationException("Observation run id is not bound.");
        string? sourceSha256 = null;
        string? normalizedSourcePath = null;
        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            normalizedSourcePath = Path.GetFullPath(sourcePath);
            if (!File.Exists(normalizedSourcePath))
            {
                throw new FileNotFoundException("Evidence source file is missing.", normalizedSourcePath);
            }
            await using var source = new FileStream(
                normalizedSourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1 << 20,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            sourceSha256 = Convert.ToHexString(
                await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        }

        var path = ReserveRunEvidencePath(kind, ".json");
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1 << 16,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new
                    {
                        schemaVersion = 1,
                        observationRunId = runId,
                        timestampUtc = DateTimeOffset.UtcNow,
                        kind,
                        label,
                        actionConfigurationSha256 = configuration.ActionConfigurationSha256,
                        source = normalizedSourcePath is null
                            ? null
                            : new { absolutePath = normalizedSourcePath, sha256 = sourceSha256 },
                        payload,
                    },
                    EvidenceJsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        var publishedMetadata = new Dictionary<string, string>
        {
            ["label"] = label,
            ["sourceSha256"] = sourceSha256 ?? string.Empty,
        };
        if (dashboardMetadata is not null)
        {
            foreach (var item in dashboardMetadata) publishedMetadata[item.Key] = item.Value;
        }
        host.PublishEvidence(kind, path, metadata: publishedMetadata);
        return path;
    }

    private void PublishRunPngEvidence(
        string kind,
        string label,
        BitmapSource image,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var path = ReserveRunEvidencePath(kind, ".png");
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1 << 16,
                FileOptions.WriteThrough))
            {
                encoder.Save(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        var labels = metadata is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(metadata);
        labels["label"] = label;
        host.PublishEvidence(kind, path, metadata: labels);
    }

    private string ReserveRunEvidencePath(string kind, string extension)
    {
        var runId = observationRunId ?? throw new InvalidOperationException("Observation run id is not bound.");
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UVEX-ADV",
            "observations",
            SanitizeRunPathSegment(runId),
            "evidence");
        Directory.CreateDirectory(directory);
        var ordinal = Interlocked.Increment(ref evidenceOrdinal);
        var safeKind = SanitizePathSegment(kind);
        return Path.Combine(
            directory,
            $"{ordinal:D5}-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{safeKind}{extension}");
    }

    private static double NormalizeDegrees(double value) => ((value % 360) + 360) % 360;

    private void Report(string message, double value = -1) => progress.Report(new ApplicationStatus
    {
        Source = "OpenAstroSpec Auto（真实）",
        Status = message,
        Progress = value,
    });

    private static StageResult Passed(
        string code,
        string message,
        IReadOnlyDictionary<string, double>? metrics = null,
        IReadOnlyDictionary<string, string>? metadata = null) =>
        new(GateResult.Pass(code, message, metrics), Metadata: metadata);

    private static StageResult Failed(
        ObservationStage stage,
        string code,
        string message,
        IReadOnlyDictionary<string, double>? metrics = null) =>
        new(GateResult.Fail(code, $"{SimulatedObservationStageRunner.StageDisplayName(stage)}: {message}", metrics));

    private static StageResult Attention(
        ObservationStage stage,
        string code,
        string message,
        IReadOnlyDictionary<string, double>? metrics = null) =>
        new(GateResult.Unknown(code, $"{SimulatedObservationStageRunner.StageDisplayName(stage)}: {message}", metrics));

    private static IReadOnlyDictionary<string, string> Metadata(LoadedCommissioningPreset preset) =>
        new Dictionary<string, string>
        {
            ["commissioningPresetId"] = preset.Value.PresetId,
            ["commissioningPresetSha256"] = preset.Sha256,
            ["commissioningProvenance"] = preset.Value.Provenance,
        };

    private static void AddIfPresent(
        IDictionary<string, string> values,
        string key,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) values[key] = value.Trim();
    }

    private static bool SameHash(string left, string right) => string.Equals(
        left.Replace("-", string.Empty, StringComparison.Ordinal).Trim(),
        right.Replace("-", string.Empty, StringComparison.Ordinal).Trim(),
        StringComparison.OrdinalIgnoreCase);

    private static async Task WriteAuditBestEffortAsync(string kind, object payload)
    {
        try { await LoopRunLogger.WriteAsync(kind, payload, CancellationToken.None).ConfigureAwait(false); }
        catch { /* Safety behavior cannot depend on local diagnostic storage. */ }
    }
}

internal sealed class PhysicalActionGateException(GateResult gate) : Exception($"{gate.Code}: {gate.Message}")
{
    public GateResult Gate { get; } = gate;
}

internal sealed class ResumeStageRestartException : Exception
{
}

internal sealed class SlitIlluminationSafetyException(string message, Exception? innerException = null)
    : Exception(message, innerException)
{
}

internal sealed record PlateSolveEvidence(
    PlateSolveResult Result,
    Coordinates Requested,
    double ResidualArcseconds,
    string SolverIdentity,
    string SourcePath,
    string EvidencePath,
    string EvidenceSha256,
    int SourceWidthPixels,
    int SourceHeightPixels,
    int SourceBinning,
    DateTimeOffset CompletedUtc);

internal enum G3SlitIlluminationPhase
{
    OffBefore,
    On,
    OffAfter,
}

internal sealed record G3CapturedIlluminationFrame(
    string SequenceId,
    G3SlitIlluminationPhase Phase,
    int PhaseIndex,
    string Role,
    bool TransitionCandidate,
    Phd2SingleFrameResult Capture,
    string Sha256,
    int ExposureMilliseconds,
    G3FrameMountReadback? MountReadback = null,
    G3FieldMountBinding? MountBinding = null);

internal sealed record G3LoadedIlluminationFrame(
    G3CapturedIlluminationFrame Captured,
    IImageData Image,
    MonochromeFrame Frame);

internal sealed record G3SlitIlluminationCommandEvidence(
    string Phase,
    bool Enabled,
    UvexOutputState LedState,
    DateTimeOffset? CommandedUtc,
    DateTimeOffset StatusTimestampUtc,
    int? SlitPhotodiodeValue,
    int? SlitPhotodiodeThreshold,
    bool? SlitPhotodiodeEnabled)
{
    public static G3SlitIlluminationCommandEvidence FromStatus(
        string phase,
        bool enabled,
        UvexDeviceStatus status) => new(
            phase,
            enabled,
            status.SlitIlluminationLedState,
            status.SlitIlluminationLedCommandedUtc,
            status.TimestampUtc,
            status.SlitPhotodiodeValue,
            status.SlitPhotodiodeThreshold,
            status.SlitPhotodiodeEnabled);
}

internal sealed record G3SlitIlluminationSequence(
    string SequenceId,
    IReadOnlyList<G3CapturedIlluminationFrame> Frames,
    IReadOnlyList<G3SlitIlluminationCommandEvidence> Commands,
    bool Completed,
    string? Failure);

internal sealed record SlitIlluminationOffAttempt(
    UvexDeviceStatus? Status,
    string? Issue);

internal sealed record G3FieldState(
    GateResult Gate,
    string FramePath,
    IImageData? Image,
    PlateSolveEvidence? Solve,
    MonochromeFrame? Frame,
    IReadOnlyList<StarCandidate> Candidates,
    SlitLocusDetection SlitDetection,
    TargetIdentification TargetIdentification,
    G3StellarFocusMeasurement? MainFocusMeasurement = null,
    BrightTargetCentroidAnalysis? BrightTargetAnalysis = null,
    BrightTargetAuthorityEvidence? BrightTargetAuthority = null,
    string? BrightTargetEvidencePath = null,
    GhostRunnerAssistanceEvidence? GhostAssistance = null,
    G3FieldMountBinding? MountBinding = null,
    SlitWheelIdentityResult? SlitIdentity = null,
    string? SlitIdentityEvidencePath = null)
{
    public static G3FieldState Failed(
        GateResult gate,
        string framePath = "",
        IImageData? image = null,
        PlateSolveEvidence? solve = null,
        G3FieldMountBinding? mountBinding = null,
        SlitWheelIdentityResult? slitIdentity = null,
        string? slitIdentityEvidencePath = null) => new(
            gate,
            framePath,
            image,
            solve,
            null,
            Array.Empty<StarCandidate>(),
            new SlitLocusDetection(
                GateResult.Unknown("SLIT_NOT_ANALYZED", "Slit was not analyzed."),
                new SlitGeometry("none", new PixelPoint(0, 0), 0, 0, 0, double.PositiveInfinity, "none", 1, 1),
                double.NaN,
                double.NaN,
                double.NaN),
            new TargetIdentification(
                GateResult.Unknown("TARGET_NOT_ANALYZED", "Target was not analyzed."),
                null,
                new PixelPoint(double.NaN, double.NaN),
                double.NaN,
                0),
            MountBinding: mountBinding,
            SlitIdentity: slitIdentity,
            SlitIdentityEvidencePath: slitIdentityEvidencePath);
}

internal sealed record AtrCapture(
    IImageData Image,
    SpectralProbeMetrics Metrics,
    string CaptureToken,
    string Role);

internal sealed record PendingQhyRequest(
    string ObservationRunId,
    QhyJobKind Kind,
    string ClientRequestId,
    object Request);

internal sealed record SlitPlacementPendingResolution(
    string Path,
    SlitPlacementPendingState State,
    bool ForeignRun);

internal sealed record G3PendingSearchReturn(
    Coordinates Origin,
    string OriginPierSide,
    double CurrentRaTangentOffsetArcseconds,
    double CurrentDeclinationOffsetArcseconds,
    double CumulativeSearchMotionArcseconds,
    DateTimeOffset StartedUtc,
    string DeclaredEvidencePath)
{
    public double CurrentRadiusArcseconds => Math.Sqrt(
        CurrentRaTangentOffsetArcseconds * CurrentRaTangentOffsetArcseconds +
        CurrentDeclinationOffsetArcseconds * CurrentDeclinationOffsetArcseconds);
}

internal sealed record G3SearchAttemptEvidence(
    G3LocalSearchWaypoint Waypoint,
    string GateCode,
    GateDisposition Disposition,
    string FramePath,
    string MoveEvidencePath,
    string AttemptEvidencePath);

internal sealed record G3SearchReturnResult(
    bool ReturnedToOrigin,
    G3PendingSearchReturn State,
    string Message);

internal sealed record G3AcquisitionMotionReturnResult(
    bool ReturnedToOrigin,
    G3AcquisitionMotionState State,
    string Path,
    string Message);

internal sealed record G3PostSlewStabilityResult(
    GateResult Gate,
    Coordinates? Reported,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    double ReportedDriftArcseconds,
    double CommandResidualArcseconds)
{
    public static G3PostSlewStabilityResult Blocked(string code, string message) => new(
        GateResult.Unknown(code, message),
        null,
        null,
        null,
        double.NaN,
        double.NaN);
}

internal sealed record G3PlateSolveAttemptEvidence(
    int LadderIndex,
    int ExposureMilliseconds,
    string GateCode,
    GateDisposition Disposition,
    bool SolveSucceeded,
    string FramePath,
    string AttemptEvidencePath,
    string? ContentGateCode = null,
    int CoherentSourceCount = 0,
    G3FieldMountBinding? MountBinding = null);

internal sealed record G3PlateSolveProbeState(
    GateResult Gate,
    string FramePath,
    IImageData? Image,
    PlateSolveEvidence? Solve,
    G3SolveProbeContentAssessment? ContentAssessment,
    IReadOnlyList<G3PlateSolveAttemptEvidence> Attempts,
    string? SummaryEvidencePath = null,
    G3FieldMountBinding? MountBinding = null,
    G3FrameMountReadback? BeforeExposureMountReadback = null)
{
    public static G3PlateSolveProbeState Failed(GateResult gate) => new(
        gate,
        string.Empty,
        null,
        null,
        null,
        Array.Empty<G3PlateSolveAttemptEvidence>());
}

internal sealed record QhyPendingCoarseReturn(
    Coordinates Origin,
    string OriginPierSide,
    double CurrentRaTangentOffsetArcseconds,
    double CurrentDeclinationOffsetArcseconds,
    double CumulativeMotionArcseconds,
    DateTimeOffset StartedUtc,
    string DeclaredEvidencePath)
{
    public double CurrentRadiusArcseconds => Math.Sqrt(
        CurrentRaTangentOffsetArcseconds * CurrentRaTangentOffsetArcseconds +
        CurrentDeclinationOffsetArcseconds * CurrentDeclinationOffsetArcseconds);
}

internal sealed record QhyCoarseReturnResult(
    bool ReturnedToOrigin,
    QhyPendingCoarseReturn State,
    string EvidencePath,
    string Message);
