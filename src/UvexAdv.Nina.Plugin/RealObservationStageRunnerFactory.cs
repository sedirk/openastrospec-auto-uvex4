using System.ComponentModel.Composition;
using System.Runtime.Versioning;
using NINA.Core.Model;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.Interfaces;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.Mediator;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// MEF composition boundary for the real runner. Constructing the factory does
/// not connect equipment; physical owners are contacted only by stage methods
/// after commissioning and checkpoint gates pass.
/// </summary>
[Export(typeof(RealObservationStageRunnerFactory))]
[PartCreationPolicy(CreationPolicy.Shared)]
[SupportedOSPlatform("windows")]
public sealed class RealObservationStageRunnerFactory : IDisposable
{
    private readonly ITelescopeMediator telescopeMediator;
    private readonly IGuiderMediator guiderMediator;
    private readonly IFocuserMediator focuserMediator;
    private readonly ICameraMediator cameraMediator;
    private readonly IImagingMediator imagingMediator;
    private readonly IImageSaveMediator imageSaveMediator;
    private readonly IPlateSolverFactory plateSolverFactory;
    private readonly IImageDataFactory imageDataFactory;
    private readonly IProfileService profileService;
    private readonly ISafetyMonitorMediator safetyMonitorMediator;
    private readonly IDomeMediator domeMediator;
    private readonly IWeatherDataMediator weatherDataMediator;
    private readonly IFlatDeviceMediator flatDeviceMediator;
    private readonly IFilterWheelMediator filterWheelMediator;
    private readonly IDomeFollower domeFollower;
    private readonly ISequenceMediator? sequences;
    private object? nativeSequenceCaptureOwner;
    private bool sequenceEventsAttached;
    private readonly object sequenceEventsSync = new();

    [ImportingConstructor]
    public RealObservationStageRunnerFactory(
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
        IFilterWheelMediator filterWheelMediator,
        IDomeFollower domeFollower,
        ISequenceMediator? sequences = null)
    {
        this.telescopeMediator = telescopeMediator;
        this.guiderMediator = guiderMediator;
        this.focuserMediator = focuserMediator;
        this.cameraMediator = cameraMediator;
        this.imagingMediator = imagingMediator;
        this.imageSaveMediator = imageSaveMediator;
        this.plateSolverFactory = plateSolverFactory;
        this.imageDataFactory = imageDataFactory;
        this.profileService = profileService;
        this.safetyMonitorMediator = safetyMonitorMediator;
        this.domeMediator = domeMediator;
        this.weatherDataMediator = weatherDataMediator;
        this.flatDeviceMediator = flatDeviceMediator;
        this.filterWheelMediator = filterWheelMediator;
        this.domeFollower = domeFollower;
        this.sequences = sequences;
    }

    // MEF constructs sequence items while NINA is still building its
    // navigation. Subscribe during native validation, before SequenceStarting,
    // not from the importing constructor or a device-action method.
    internal void ObserveNativeSequenceLifetime()
    {
        lock (sequenceEventsSync)
        {
            if (sequences is null || sequenceEventsAttached) return;
            try { if (!sequences.Initialized) return; }
            catch (NullReferenceException) { return; } // NINA 3.2: navigation not registered yet.
            sequences.SequenceStarting += OnSequenceStarting;
            sequences.SequenceFinished += OnSequenceFinished;
            sequenceEventsAttached = true;
        }
    }

    private Task OnSequenceStarting(object sender, EventArgs args)
    {
        Volatile.Write(ref nativeSequenceCaptureOwner, sender);
        return Task.CompletedTask;
    }

    private Task OnSequenceFinished(object sender, EventArgs args)
    {
        Interlocked.CompareExchange(ref nativeSequenceCaptureOwner, null, sender);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (sequenceEventsSync)
        {
            if (sequences is null || !sequenceEventsAttached) return;
            sequences.SequenceStarting -= OnSequenceStarting;
            sequences.SequenceFinished -= OnSequenceFinished;
            sequenceEventsAttached = false;
            Volatile.Write(ref nativeSequenceCaptureOwner, null);
        }
    }

    internal RealObservationStageRunner Create(
        ObservationCoordinatorHost host,
        UvexPluginSettings settings,
        IProgress<ApplicationStatus> progress,
        RealRunConfiguration? lockedConfiguration = null) => new(
            host,
            settings,
            lockedConfiguration ?? CaptureConfiguration(settings),
            telescopeMediator,
            guiderMediator,
            focuserMediator,
            cameraMediator,
            imagingMediator,
            imageSaveMediator,
            plateSolverFactory,
            imageDataFactory,
            profileService,
            safetyMonitorMediator,
            domeMediator,
            weatherDataMediator,
            flatDeviceMediator,
            filterWheelMediator,
            domeFollower,
            progress,
            () => sequences?.IsAdvancedSequenceRunning() == true ? Volatile.Read(ref nativeSequenceCaptureOwner) : null);

    internal RealRunConfiguration CaptureConfiguration(UvexPluginSettings settings)
    {
        NinaInstancePolicy.RequireMaster(settings);
        var plateSettings = profileService.ActiveProfile.PlateSolveSettings;
        var primarySolver = plateSolverFactory.GetPlateSolver(plateSettings);
        var blindSolver = plateSolverFactory.GetBlindSolver(plateSettings);
        return RealRunConfiguration.Capture(
            settings,
            PlateSolverRunConfiguration.Capture(plateSettings, primarySolver, blindSolver),
            profileService.ActiveProfile.ImageFileSettings.FilePattern,
            CaptureEnvironmentDeviceSelection());
    }

    internal NinaSepMainFocusHardware CreateMainFocusHardware(UvexPluginSettings settings, UvexAdv.Observatory.SepMainFocusOptions options)
        => new(profileService, focuserMediator, telescopeMediator, cameraMediator, domeMediator,
            flatDeviceMediator, safetyMonitorMediator, imageDataFactory, settings, options);

    internal IDisposable WatchNightSafety(Action unsafeCallback)
    {
        EventHandler<IsSafeEventArgs> handler = (_, e) =>
        {
            if (e.IsSafe) return;
            try { cameraMediator.AbortExposure(); }
            catch { /* The owner records stop/closeout readback failures. */ }
            unsafeCallback();
        };
        safetyMonitorMediator.IsSafeChanged += handler;
        return new SafetySubscription(() => safetyMonitorMediator.IsSafeChanged -= handler);
    }

    private sealed class SafetySubscription(Action unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe();
    }

    internal bool IsNightSafetyCurrent(RealRunConfiguration configuration)
    {
        var info = safetyMonitorMediator.GetInfo();
        return info.Connected && info.IsSafe &&
            string.Equals(info.DeviceId, configuration.Environment.Devices.SafetyMonitorId, StringComparison.Ordinal) &&
            CaptureEnvironmentDeviceSelection() == configuration.Environment.Devices;
    }

    private NinaEnvironmentDeviceSelection CaptureEnvironmentDeviceSelection()
    {
        var profile = profileService.ActiveProfile;
        return new NinaEnvironmentDeviceSelection(
            profile.SafetyMonitorSettings.Id ?? NinaEnvironmentDeviceSelection.NoDeviceId,
            profile.DomeSettings.Id ?? NinaEnvironmentDeviceSelection.NoDeviceId,
            profile.WeatherDataSettings.Id ?? NinaEnvironmentDeviceSelection.NoDeviceId,
            profile.FlatDeviceSettings.Id ?? NinaEnvironmentDeviceSelection.NoDeviceId);
    }
}
