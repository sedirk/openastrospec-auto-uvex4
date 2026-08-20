using System.ComponentModel.Composition;
using System.Runtime.Versioning;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.Interfaces;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
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
public sealed class RealObservationStageRunnerFactory
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
        IFlatDeviceMediator flatDeviceMediator)
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
            progress);

    internal RealRunConfiguration CaptureConfiguration(UvexPluginSettings settings)
    {
        var plateSettings = profileService.ActiveProfile.PlateSolveSettings;
        var primarySolver = plateSolverFactory.GetPlateSolver(plateSettings);
        var blindSolver = plateSolverFactory.GetBlindSolver(plateSettings);
        return RealRunConfiguration.Capture(
            settings,
            PlateSolverRunConfiguration.Capture(plateSettings, primarySolver, blindSolver));
    }
}
