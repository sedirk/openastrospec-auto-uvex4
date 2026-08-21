using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NINA.Core.Model;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.ViewModel;
using Microsoft.Win32;
using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

[Export(typeof(IDockableVM))]
[SupportedOSPlatform("windows")]
public sealed class ObservationDockable : DockableVM, IDisposable
{
    private static readonly JsonSerializerOptions CaseInsensitiveJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IProfileService activeProfileService;
    private readonly UvexPluginSettings settings;
    private readonly ObservationCoordinatorHost host;
    private readonly RealObservationStageRunnerFactory realRunnerFactory;
    private readonly ObservationTargetImportService targetImportService;
    private readonly ICameraMediator cameraMediator;
    private readonly IImagingMediator imagingMediator;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SimpleAsyncCommand startSelectedModeCommand;
    private readonly SimpleAsyncCommand startSimulationCommand;
    private readonly SimpleAsyncCommand startRealCommand;
    private readonly SimpleCommand selectSimulationModeCommand;
    private readonly SimpleCommand selectRealModeCommand;
    private readonly SimpleCommand pauseCommand;
    private readonly SimpleCommand resumeCommand;
    private readonly SimpleCommand cancelCommand;
    private readonly SimpleCommand takeoverCommand;
    private readonly SimpleCommand openQhyPreviewCommand;
    private readonly SimpleCommand openG3PreviewCommand;
    private readonly SimpleCommand openAtrPreviewCommand;
    private readonly SimpleCommand openFailurePreviewCommand;
    private readonly SimpleCommand openFailureEvidenceDirectoryCommand;
    private readonly SimpleCommand openLatestEvidenceDirectoryCommand;
    private readonly SimpleCommand openRunDirectoryCommand;
    private readonly SimpleCommand showObservationPlanCommand;
    private readonly SimpleCommand showStartupRequirementsCommand;
    private readonly SimpleCommand importCommissioningBindingsCommand;
    private readonly SimpleCommand refreshProfileOwnershipCommand;
    private readonly SimpleAsyncCommand importFromFramingAssistantCommand;
    private readonly SimpleAsyncCommand importFromPlanetariumCommand;
    private readonly SimpleCommand bindCurrentAtrCameraCommand;
    private readonly SimpleCommand refreshAtrManualStatusCommand;
    private readonly SimpleAsyncCommand captureManualAtrSpectrumCommand;
    private string stateText = "空闲";
    private string currentStageText = "—";
    private string nextStageText = "—";
    private string statusMessage = "尚未开始自动观测。";
    private string pauseReason = string.Empty;
    private string runManifestPath = string.Empty;
    private string operatorNotice = "先选择运行模式。选择真实模式本身不会连接或移动设备。";
    private string error = string.Empty;
    private double progressPercent;
    private ImageSource? qhyPreviewImage;
    private ImageSource? g3PreviewImage;
    private ImageSource? atrPreviewImage;
    private string qhyPreviewCaption = string.Empty;
    private string g3PreviewCaption = string.Empty;
    private string atrPreviewCaption = string.Empty;
    private string lastFailureHeadline = "目前没有失败记录。";
    private string lastFailureCode = "—";
    private string lastFailureMessage = "运行开始后，这里会显示最近未通过的质量门及其完整原因。";
    private string lastFailureMetrics = "—";
    private string lastFailureRecommendation = "三路预览、质量门、时间线和证据文件会在运行中持续更新。";
    private string lastFailureEvidencePath = string.Empty;
    private string lastFailurePreviewLabel = "没有需要检查的失败图像";
    private string latestEvidencePath = string.Empty;
    private string latestEvidenceSummary = "尚未生成运行证据。";
    private string phd2CalibrationGradeText = "尚未评估";
    private string phd2CalibrationPolicyText = "等待读取实际 commissioning policy";
    private string phd2CommissioningRouteText = "路线：等待读取 commissioning preset";
    private string phd2CalibrationPermissionText = "验证导星：等待 · exact-lock：等待 · 无人值守科学：否";
    private string phd2CalibrationScaleText = "步长/残差缩放：等待 PostSettle 证据";
    private string phd2CalibrationReasonText = "当前生产路径只评估 PHD2 当前 active calibration（单候选）；本轮真实 settle 和 fresh residual 到齐后才授予 exact-lock 或科学权限。";
    private string phd2CalibrationOverviewGradeText = "尚未评估";
    private string phd2CalibrationOverviewText = "启动导星后自动评估；评估前不执行精调或科学曝光。";
    private string ghostCalibrationSummaryText = "标定：尚未读取经过 SHA-256 验证的 commissioning preset。";
    private string slitIdentityStatusText = "狭缝光学身份：尚未读取经过 SHA-256 验证的四槽位 LED 宽度指纹。";
    private string ghostApplicabilityText = "适用性：尚未运行；需同时核对新鲜 OFF 帧、相机/Profile、ROI/binning、方向、pier side、外部目录身份和独立 C11 焦点。";
    private string ghostDecisionText = "决定：尚未运行。Skip/Auto 继续既有 WCS/居中/搜索；只有 Require 无效时暂停。";
    private string ghostAssistanceModeText = "关闭";
    private string ghostOverviewText = "已关闭；目标定位使用常规 WCS、居中与有界搜索。";
    private ObservationPreviewChannel? lastFailurePreviewChannel;
    private bool hasFailure;
    private bool hasTargetImport;
    private bool importFramingCenter;
    private bool isTargetImportBusy;
    private int targetEditGeneration;
    private string targetImportSummary = "目标由手工输入。";
    private string targetImportDetails = "尚未从构图助手或第三方星图导入。";
    private string atrManualCameraStatus = "尚未检查 N.I.N.A. 当前相机。";
    private string atrManualCaptureStatus = "尚未采集单帧检查光谱。";
    private string atrManualCaptureError = string.Empty;
    private int selectedWorkspaceTabIndex;

    [ImportingConstructor]
    public ObservationDockable(
        IProfileService profileService,
        ObservationCoordinatorHost host,
        RealObservationStageRunnerFactory realRunnerFactory,
        IFramingAssistantVM framingAssistant,
        IPlanetariumFactory planetariumFactory,
        ICameraMediator cameraMediator,
        IImagingMediator imagingMediator)
        : base(profileService)
    {
        activeProfileService = profileService;
        settings = new UvexPluginSettings(profileService);
        this.host = host;
        this.realRunnerFactory = realRunnerFactory;
        this.cameraMediator = cameraMediator;
        this.imagingMediator = imagingMediator;
        targetImportService = ObservationTargetImportNinaSources.CreateService(
            framingAssistant,
            planetariumFactory);
        Title = "OpenAstroSpec 自动观测";
        var icon = new GeometryGroup();
        icon.Children.Add(Geometry.Parse("M1,14 L5,9 8,11 12,4 15,6 M2,3 L2,7 M0,5 L4,5"));
        icon.Freeze();
        ImageGeometry = icon;

        startSelectedModeCommand = new SimpleAsyncCommand(StartSelectedModeAsync, CanStart);
        startSimulationCommand = new SimpleAsyncCommand(StartSimulationAsync, CanStart);
        startRealCommand = new SimpleAsyncCommand(StartRealAsync, CanStartReal);
        selectSimulationModeCommand = new SimpleCommand(() => SelectMode(useRealMode: false), CanStart);
        selectRealModeCommand = new SimpleCommand(() => SelectMode(useRealMode: true), CanStart);
        pauseCommand = new SimpleCommand(
            () => host.RequestPause("操作员从实时面板请求暂停。"),
            () => IsControllable && RunState is not ObservationRunState.PauseRequested);
        resumeCommand = new SimpleCommand(Resume, () => RunState is
            ObservationRunState.Paused or ObservationRunState.PausedNeedsAttention or ObservationRunState.ManualTakeover);
        cancelCommand = new SimpleCommand(host.Cancel, () => IsControllable);
        takeoverCommand = new SimpleCommand(
            () => host.RequestTakeover("操作员从实时面板请求人工接管。"),
            () => IsControllable && RunState is not ObservationRunState.ManualTakeover);
        openQhyPreviewCommand = new SimpleCommand(
            () => OpenPreview("GS350 / QHY 广域取景与测光", QhyPreviewImage, QhyPreviewCaption),
            () => QhyPreviewImage is not null);
        openG3PreviewCommand = new SimpleCommand(
            () => OpenPreview("PHD2 / G3 狭缝、目标与导星", G3PreviewImage, G3PreviewCaption),
            () => G3PreviewImage is not null);
        openAtrPreviewCommand = new SimpleCommand(
            () => OpenPreview("ATR585M 2D 光谱与即时 1D", AtrPreviewImage, AtrPreviewCaption),
            () => AtrPreviewImage is not null);
        openFailurePreviewCommand = new SimpleCommand(OpenFailurePreview, FailurePreviewAvailable);
        openFailureEvidenceDirectoryCommand = new SimpleCommand(
            () => OpenContainingDirectory(LastFailureEvidencePath, "失败证据"),
            () => PathExists(LastFailureEvidencePath));
        openLatestEvidenceDirectoryCommand = new SimpleCommand(
            () => OpenContainingDirectory(LatestEvidencePath, "最近证据"),
            () => PathExists(LatestEvidencePath));
        openRunDirectoryCommand = new SimpleCommand(
            () => OpenContainingDirectory(RunManifestPath, "运行清单"),
            () => PathExists(RunManifestPath));
        showObservationPlanCommand = new SimpleCommand(() => SelectedWorkspaceTabIndex = 1);
        showStartupRequirementsCommand = new SimpleCommand(() => SelectedWorkspaceTabIndex = 5);
        importCommissioningBindingsCommand = new SimpleCommand(ImportCommissioningBindings);
        refreshProfileOwnershipCommand = new SimpleCommand(RefreshProfileOwnership);
        importFromFramingAssistantCommand = new SimpleAsyncCommand(
            ImportFromFramingAssistantAsync,
            CanImportTarget);
        importFromPlanetariumCommand = new SimpleAsyncCommand(
            ImportFromPlanetariumAsync,
            CanImportTarget);
        bindCurrentAtrCameraCommand = new SimpleCommand(BindCurrentAtrCamera, CanUseManualAtrTools);
        refreshAtrManualStatusCommand = new SimpleCommand(RefreshAtrManualStatus);
        captureManualAtrSpectrumCommand = new SimpleAsyncCommand(
            CaptureManualAtrSpectrumAsync,
            CanUseManualAtrTools);

        LoadTargetImportDisplay();
        RefreshGhostCommissioningSummary();
        RefreshSlitIdentitySummary();
        RefreshAtrManualStatus();

        host.DashboardChanged += OnDashboardChanged;
        UvexRuntimeState.Changed += OnManualSpectrumChanged;
        ApplyDashboard(host.Dashboard);
    }

    public ICommand StartSelectedModeCommand => startSelectedModeCommand;
    public ICommand StartSimulationCommand => startSimulationCommand;
    public ICommand StartRealCommand => startRealCommand;
    public ICommand SelectSimulationModeCommand => selectSimulationModeCommand;
    public ICommand SelectRealModeCommand => selectRealModeCommand;
    public ICommand PauseCommand => pauseCommand;
    public ICommand ResumeCommand => resumeCommand;
    public ICommand CancelCommand => cancelCommand;
    public ICommand TakeoverCommand => takeoverCommand;
    public ICommand OpenQhyPreviewCommand => openQhyPreviewCommand;
    public ICommand OpenG3PreviewCommand => openG3PreviewCommand;
    public ICommand OpenAtrPreviewCommand => openAtrPreviewCommand;
    public ICommand OpenFailurePreviewCommand => openFailurePreviewCommand;
    public ICommand OpenFailureEvidenceDirectoryCommand => openFailureEvidenceDirectoryCommand;
    public ICommand OpenLatestEvidenceDirectoryCommand => openLatestEvidenceDirectoryCommand;
    public ICommand OpenRunDirectoryCommand => openRunDirectoryCommand;
    public ICommand ShowObservationPlanCommand => showObservationPlanCommand;
    public ICommand ShowStartupRequirementsCommand => showStartupRequirementsCommand;
    public ICommand ImportCommissioningBindingsCommand => importCommissioningBindingsCommand;
    public ICommand RefreshProfileOwnershipCommand => refreshProfileOwnershipCommand;
    public ICommand ImportFromFramingAssistantCommand => importFromFramingAssistantCommand;
    public ICommand ImportFromPlanetariumCommand => importFromPlanetariumCommand;
    public ICommand BindCurrentAtrCameraCommand => bindCurrentAtrCameraCommand;
    public ICommand RefreshAtrManualStatusCommand => refreshAtrManualStatusCommand;
    public ICommand CaptureManualAtrSpectrumCommand => captureManualAtrSpectrumCommand;

    public ObservableCollection<ObservationGateRow> GateRows { get; } = new();
    public ObservableCollection<ObservationTimelineRow> TimelineRows { get; } = new();
    public ObservableCollection<ObservationEvidenceRow> EvidenceRows { get; } = new();

    public string TargetName
    {
        get => settings.ObservationTargetName;
        set { settings.ObservationTargetName = value; MarkTargetAsManuallyEdited(); RaisePropertyChanged(); }
    }

    public string CatalogId
    {
        get => settings.ObservationCatalogId;
        set { settings.ObservationCatalogId = value; MarkTargetAsManuallyEdited(); RaisePropertyChanged(); }
    }

    public double RightAscensionDegrees
    {
        get => settings.ObservationRightAscensionDegrees;
        set { settings.ObservationRightAscensionDegrees = value; MarkTargetAsManuallyEdited(); RaisePropertyChanged(); }
    }

    public double DeclinationDegrees
    {
        get => settings.ObservationDeclinationDegrees;
        set { settings.ObservationDeclinationDegrees = value; MarkTargetAsManuallyEdited(); RaisePropertyChanged(); }
    }

    public double DurationMinutes
    {
        get => settings.ObservationDurationMinutes;
        set { settings.ObservationDurationMinutes = value; RaisePropertyChanged(); }
    }

    public string NightSetupId
    {
        get => settings.ObservationNightSetupId;
        set { settings.ObservationNightSetupId = value; RaisePropertyChanged(); }
    }

    public double SiteLatitudeDegrees
    {
        get => settings.ObservatoryLatitudeDegrees;
        set { settings.ObservatoryLatitudeDegrees = value; RaisePropertyChanged(); }
    }

    public double SiteLongitudeDegreesEast
    {
        get => settings.ObservatoryLongitudeDegreesEast;
        set { settings.ObservatoryLongitudeDegreesEast = value; RaisePropertyChanged(); }
    }

    public double SiteElevationMeters
    {
        get => settings.ObservatoryElevationMeters;
        set { settings.ObservatoryElevationMeters = value; RaisePropertyChanged(); }
    }

    public double HorizonMinimumDegrees
    {
        get => settings.HorizonMinimumDegrees;
        set { settings.HorizonMinimumDegrees = value; RaisePropertyChanged(); }
    }

    public double HorizonStartMarginDegrees
    {
        get => settings.HorizonStartMarginDegrees;
        set { settings.HorizonStartMarginDegrees = value; RaisePropertyChanged(); }
    }

    public double HorizonContinueMarginDegrees
    {
        get => settings.HorizonContinueMarginDegrees;
        set { settings.HorizonContinueMarginDegrees = value; RaisePropertyChanged(); }
    }

    public string ExpectedAtrCameraId
    {
        get => settings.ObservationExpectedAtrCameraId;
        set { settings.ObservationExpectedAtrCameraId = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public string ExpectedG3ProfileName
    {
        get => settings.ObservationExpectedG3ProfileName;
        set { settings.ObservationExpectedG3ProfileName = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public string ExpectedQhyCameraId
    {
        get => settings.ObservationExpectedQhyCameraId;
        set { settings.ObservationExpectedQhyCameraId = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public int SimulationStageMilliseconds
    {
        get => settings.ObservationSimulationStageMilliseconds;
        set { settings.ObservationSimulationStageMilliseconds = value; RaisePropertyChanged(); }
    }

    public bool UseRealMode
    {
        get => settings.ObservationUseRealMode;
        set
        {
            settings.ObservationUseRealMode = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(ModeText));
            RaisePropertyChanged(nameof(ModeDescription));
            RaisePropertyChanged(nameof(StartButtonText));
            RaisePropertyChanged(nameof(RealModeStatus));
            RaisePropertyChanged(nameof(IsSimulationMode));
            RaisePropertyChanged(nameof(IsRealMode));
            RaiseCommandStates();
        }
    }

    public bool RealModeCommissioned
    {
        get => settings.RealModeCommissioned;
        set { settings.RealModeCommissioned = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public string CommissioningPresetPath
    {
        get => settings.CommissioningPresetPath;
        set { settings.CommissioningPresetPath = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaisePropertyChanged(nameof(Phd2CommissioningRouteText)); RefreshGhostCommissioningSummary(); RefreshSlitIdentitySummary(); RaiseCommandStates(); }
    }

    public string CommissioningPresetId
    {
        get => settings.CommissioningPresetId;
        set { settings.CommissioningPresetId = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public string CommissioningPresetSha256
    {
        get => settings.CommissioningPresetSha256;
        set { settings.CommissioningPresetSha256 = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RefreshGhostCommissioningSummary(); RefreshSlitIdentitySummary(); RaiseCommandStates(); }
    }

    public string CommissioningHardwareFingerprintSha256
    {
        get => settings.CommissioningHardwareFingerprintSha256;
        set { settings.CommissioningHardwareFingerprintSha256 = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public string NightSetupSnapshotPath
    {
        get => settings.NightSetupSnapshotPath;
        set { settings.NightSetupSnapshotPath = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public string NightSetupSnapshotSha256
    {
        get => settings.NightSetupSnapshotSha256;
        set { settings.NightSetupSnapshotSha256 = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public string ExpectedTelescopeId
    {
        get => settings.ExpectedTelescopeId;
        set { settings.ExpectedTelescopeId = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public string Phd2ProfileEvidenceSha256
    {
        get => settings.Phd2ProfileEvidenceSha256;
        set { settings.Phd2ProfileEvidenceSha256 = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public string WideToSlitTransferModeText => settings.WideToSlitTransferMode.ToString();

    public string WideToSlitTransferStatus => settings.WideToSlitTransferMode == WideToSlitTransferMode.Skip
        ? "自动预定位仍为 Skip：只使用 G3 直接解算/有界搜索。可另行启用下方“快速双解算配对”来生成不可自动移动的版本化候选；绝不复用入缝用的 G3 像素→赤道仪矩阵。"
        : $"当前选择 {settings.WideToSlitTransferMode}，但 runner 尚无 Verified/Active 记录导入与适用性执行路径，真实模式将在任何预置运动前阻断。";

    public bool QhyG3FastPairEnabled
    {
        get => settings.QhyG3FastPairEnabled;
        set { settings.QhyG3FastPairEnabled = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(QhyG3FastPairStatus)); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public int QhyG3FastPairSchemaVersion => settings.QhyG3FastPairSchemaVersion;
    public string QhyG3FastPairPolicyId
    {
        get => settings.QhyG3FastPairPolicyId;
        set { settings.QhyG3FastPairPolicyId = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(QhyG3FastPairStatus)); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }
    public double QhyG3FastPairExposureSeconds
    {
        get => settings.QhyG3FastPairExposureSeconds;
        set { settings.QhyG3FastPairExposureSeconds = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(QhyG3FastPairStatus)); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }
    public double QhyG3FastPairMaximumCachedAgeSeconds
    {
        get => settings.QhyG3FastPairMaximumCachedAgeSeconds;
        set { settings.QhyG3FastPairMaximumCachedAgeSeconds = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(QhyG3FastPairStatus)); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }
    public double QhyG3FastPairMaximumMidpointSeparationSeconds
    {
        get => settings.QhyG3FastPairMaximumMidpointSeparationSeconds;
        set { settings.QhyG3FastPairMaximumMidpointSeparationSeconds = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }
    public double QhyG3FastPairMaximumWallClockSeconds
    {
        get => settings.QhyG3FastPairMaximumWallClockSeconds;
        set { settings.QhyG3FastPairMaximumWallClockSeconds = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }
    public double QhyG3FastPairMaximumMountSpanArcseconds
    {
        get => settings.QhyG3FastPairMaximumMountSpanArcseconds;
        set { settings.QhyG3FastPairMaximumMountSpanArcseconds = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }
    public double QhyG3FastPairCandidateValidityHours
    {
        get => settings.QhyG3FastPairCandidateValidityHours;
        set { settings.QhyG3FastPairCandidateValidityHours = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }
    public double QhyG3FastPairMaximumCandidateUncertaintyArcseconds
    {
        get => settings.QhyG3FastPairMaximumCandidateUncertaintyArcseconds;
        set { settings.QhyG3FastPairMaximumCandidateUncertaintyArcseconds = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public string QhyG3FastPairStatus => settings.QhyG3FastPairEnabled
        ? $"已启用候选学习：优先复用 ≤{settings.QhyG3FastPairMaximumCachedAgeSeconds:G4}s 的同位置 QHY WCS；否则只拍 1 张 {settings.QhyG3FastPairExposureSeconds:G4}s QHY 帧。全过程 0 次赤道仪命令，候选不能直接授权运动。"
        : "默认关闭：不会因 G3 解算成功而额外启动 QHY 曝光。启用后仍只生成 Candidate，不自动改写或激活两镜变换。";

    public int G3PlateSolveExposurePresetSchemaVersion => settings.G3PlateSolveExposurePresetSchemaVersion;

    public string G3PlateSolveExposurePresetId
    {
        get => settings.G3PlateSolveExposurePresetId;
        set { settings.G3PlateSolveExposurePresetId = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public string G3PlateSolveExposureMillisecondsCsv
    {
        get => settings.G3PlateSolveExposureMillisecondsCsv;
        set { settings.G3PlateSolveExposureMillisecondsCsv = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public int G3WcsCenteringSchemaVersion => settings.G3WcsCenteringSchemaVersion;

    public double G3WcsMaximumSingleCorrectionArcseconds
    {
        get => settings.G3WcsMaximumSingleCorrectionArcseconds;
        set { settings.G3WcsMaximumSingleCorrectionArcseconds = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double G3WcsMaximumRadiusArcseconds
    {
        get => settings.G3WcsMaximumRadiusArcseconds;
        set { settings.G3WcsMaximumRadiusArcseconds = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double G3WcsMaximumCumulativeMotionArcseconds
    {
        get => settings.G3WcsMaximumCumulativeMotionArcseconds;
        set { settings.G3WcsMaximumCumulativeMotionArcseconds = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public int G3WcsMaximumCorrectionAttempts
    {
        get => settings.G3WcsMaximumCorrectionAttempts;
        set { settings.G3WcsMaximumCorrectionAttempts = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double G3WcsMaximumCenteringMinutes
    {
        get => settings.G3WcsMaximumCenteringMinutes;
        set { settings.G3WcsMaximumCenteringMinutes = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double G3TargetInsideFieldMarginPixels
    {
        get => settings.G3TargetInsideFieldMarginPixels;
        set { settings.G3TargetInsideFieldMarginPixels = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double G3MotionWorstCaseActionSeconds
    {
        get => settings.G3MotionWorstCaseActionSeconds;
        set { settings.G3MotionWorstCaseActionSeconds = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double G3MotionPostSlewSettleSeconds
    {
        get => settings.G3MotionPostSlewSettleSeconds;
        set { settings.G3MotionPostSlewSettleSeconds = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double G3SearchStepArcseconds
    {
        get => settings.G3SearchStepArcseconds;
        set { settings.G3SearchStepArcseconds = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double G3SearchMaximumRadiusArcseconds
    {
        get => settings.G3SearchMaximumRadiusArcseconds;
        set { settings.G3SearchMaximumRadiusArcseconds = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double G3SearchMaximumCumulativeArcseconds
    {
        get => settings.G3SearchMaximumCumulativeArcseconds;
        set { settings.G3SearchMaximumCumulativeArcseconds = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public int G3SearchMaximumAttempts
    {
        get => settings.G3SearchMaximumAttempts;
        set { settings.G3SearchMaximumAttempts = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double G3SearchMaximumMinutes
    {
        get => settings.G3SearchMaximumMinutes;
        set { settings.G3SearchMaximumMinutes = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public int QhyCoarseCenteringSchemaVersion => settings.QhyCoarseCenteringSchemaVersion;

    public double QhyCoarseMaximumSingleCorrectionArcseconds
    {
        get => settings.QhyCoarseMaximumSingleCorrectionArcseconds;
        set { settings.QhyCoarseMaximumSingleCorrectionArcseconds = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double QhyCoarseMaximumCumulativeCorrectionArcseconds
    {
        get => settings.QhyCoarseMaximumCumulativeCorrectionArcseconds;
        set { settings.QhyCoarseMaximumCumulativeCorrectionArcseconds = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public int QhyCoarseMaximumCorrectionAttempts
    {
        get => settings.QhyCoarseMaximumCorrectionAttempts;
        set { settings.QhyCoarseMaximumCorrectionAttempts = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double QhyCoarseMaximumCenteringMinutes
    {
        get => settings.QhyCoarseMaximumCenteringMinutes;
        set { settings.QhyCoarseMaximumCenteringMinutes = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public string Phd2RegistryCameraName => settings.Phd2CameraName;
    public string Phd2RegistryMountName => settings.Phd2MountName;

    public string Phd2RuntimeCameraName
    {
        get => settings.Phd2RuntimeCameraName;
        set { settings.Phd2RuntimeCameraName = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public string Phd2RuntimeMountName
    {
        get => settings.Phd2RuntimeMountName;
        set { settings.Phd2RuntimeMountName = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public int G3ExposureMilliseconds
    {
        get => settings.G3ExposureMilliseconds;
        set { settings.G3ExposureMilliseconds = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public int G3GainPercent
    {
        get => settings.G3GainPercent;
        set { settings.G3GainPercent = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public int G3CameraRecoveryDelayMilliseconds
    {
        get => settings.G3CameraRecoveryDelayMilliseconds;
        set { settings.G3CameraRecoveryDelayMilliseconds = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public IReadOnlyList<GhostAssistanceMode> GhostAssistanceModes { get; } =
        Enum.GetValues<GhostAssistanceMode>();

    public GhostAssistanceMode GhostAssistanceMode
    {
        get => settings.GhostAssistanceMode;
        set
        {
            settings.GhostAssistanceMode = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(RealModeStatus));
            RefreshGhostCommissioningSummary();
            RaiseCommandStates();
        }
    }

    public bool BrightTargetWingCentroidEnabled
    {
        get => settings.BrightTargetWingCentroidEnabled;
        set { settings.BrightTargetWingCentroidEnabled = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public int BrightTargetMinimumG3ExposureMilliseconds
    {
        get => settings.BrightTargetMinimumG3ExposureMilliseconds;
        set { settings.BrightTargetMinimumG3ExposureMilliseconds = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double BrightTargetMaximumQhyWcsAgeMinutes
    {
        get => settings.BrightTargetMaximumQhyWcsAgeMinutes;
        set { settings.BrightTargetMaximumQhyWcsAgeMinutes = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double BrightTargetMaximumG3FrameAgeMinutes
    {
        get => settings.BrightTargetMaximumG3FrameAgeMinutes;
        set { settings.BrightTargetMaximumG3FrameAgeMinutes = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double BrightTargetMaximumQhyResidualArcseconds
    {
        get => settings.BrightTargetMaximumQhyResidualArcseconds;
        set { settings.BrightTargetMaximumQhyResidualArcseconds = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double BrightTargetMaximumCatalogMismatchArcseconds
    {
        get => settings.BrightTargetMaximumCatalogMismatchArcseconds;
        set { settings.BrightTargetMaximumCatalogMismatchArcseconds = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double BrightTargetMinimumC11FocusConfidence
    {
        get => settings.BrightTargetMinimumC11FocusConfidence;
        set { settings.BrightTargetMinimumC11FocusConfidence = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public int BrightTargetMinimumSaturatedCorePixels
    {
        get => settings.BrightTargetMinimumSaturatedCorePixels;
        set { settings.BrightTargetMinimumSaturatedCorePixels = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public int BrightTargetMaximumSaturatedCorePixels
    {
        get => settings.BrightTargetMaximumSaturatedCorePixels;
        set { settings.BrightTargetMaximumSaturatedCorePixels = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public int BrightTargetWingRadiusPixels
    {
        get => settings.BrightTargetWingRadiusPixels;
        set { settings.BrightTargetWingRadiusPixels = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double BrightTargetMinimumWingProminenceSigma
    {
        get => settings.BrightTargetMinimumWingProminenceSigma;
        set { settings.BrightTargetMinimumWingProminenceSigma = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double BrightTargetMaximumWingLevelFraction
    {
        get => settings.BrightTargetMaximumWingLevelFraction;
        set { settings.BrightTargetMaximumWingLevelFraction = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public int BrightTargetMinimumWingPixels
    {
        get => settings.BrightTargetMinimumWingPixels;
        set { settings.BrightTargetMinimumWingPixels = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double BrightTargetMinimumWingSignalToNoise
    {
        get => settings.BrightTargetMinimumWingSignalToNoise;
        set { settings.BrightTargetMinimumWingSignalToNoise = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double BrightTargetMinimumAngularCoverageFraction
    {
        get => settings.BrightTargetMinimumAngularCoverageFraction;
        set { settings.BrightTargetMinimumAngularCoverageFraction = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double BrightTargetMinimumOpposedWingBalance
    {
        get => settings.BrightTargetMinimumOpposedWingBalance;
        set { settings.BrightTargetMinimumOpposedWingBalance = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double BrightTargetMaximumWingCentroidDisagreementPixels
    {
        get => settings.BrightTargetMaximumWingCentroidDisagreementPixels;
        set { settings.BrightTargetMaximumWingCentroidDisagreementPixels = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public int BrightTargetEdgeMarginPixels
    {
        get => settings.BrightTargetEdgeMarginPixels;
        set { settings.BrightTargetEdgeMarginPixels = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double BrightTargetNearbySaturatedCoreRadiusPixels
    {
        get => settings.BrightTargetNearbySaturatedCoreRadiusPixels;
        set { settings.BrightTargetNearbySaturatedCoreRadiusPixels = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double BrightTargetMinimumUniquenessRatio
    {
        get => settings.BrightTargetMinimumUniquenessRatio;
        set { settings.BrightTargetMinimumUniquenessRatio = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double BrightTargetMaximumSecondaryPeakRatio
    {
        get => settings.BrightTargetMaximumSecondaryPeakRatio;
        set { settings.BrightTargetMaximumSecondaryPeakRatio = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public string NinaFilterWheelOwnershipStatus
    {
        get
        {
            var selected = activeProfileService.ActiveProfile.FilterWheelSettings.Id ?? string.Empty;
            return string.Equals(
                selected,
                NinaProfileOwnerPreflight.NoPhysicalFilterWheelDeviceId,
                StringComparison.Ordinal)
                ? "✓ N.I.N.A. 滤镜轮为 No_Device；QHY 物理滤镜轮只归独立 QHY 服务所有。"
                : $"✗ N.I.N.A. 当前滤镜轮为“{selected}”；真实模式必须先在 N.I.N.A. 设备选择器中改为 No_Device，避免重复打开 QHY 物理轮。";
        }
    }

    public string NinaGuiderOwnershipStatus
    {
        get
        {
            var selected = activeProfileService.ActiveProfile.GuiderSettings.GuiderName ?? string.Empty;
            return string.Equals(
                selected,
                NinaProfileOwnerPreflight.Phd2GuiderName,
                StringComparison.Ordinal)
                ? "✓ N.I.N.A. 导星适配器为稳定 ID PHD2_Single。"
                : $"✗ N.I.N.A. 当前导星适配器为“{selected}”；真实模式要求设备选择器中的精确稳定 ID PHD2_Single（不是显示名 PHD2）。";
        }
    }

    public string ModeText => UseRealMode ? "已选择：真实设备控制" : "已选择：模拟演练";
    public bool IsSimulationMode => !UseRealMode;
    public bool IsRealMode => UseRealMode;
    public string ModeDescription => UseRealMode
        ? "只有点击下方启动按钮后才会进入真实流程；启动前仍会逐项检查不可变证据、设备身份、安全门和运动限额。仅切换到此模式不会连接或移动设备。"
        : "模拟演练不会连接相机、赤道仪、PHD2 或 UVEX，可用于熟悉自动推进、暂停、恢复、取消、诊断和证据界面。";
    public string StartButtonText => UseRealMode
        ? "启动真实设备自动观测"
        : "启动模拟演练（不连接设备）";
    public string RealModeStatus
    {
        get
        {
            var issues = RealModeEligibilityIssues();
            return issues.Count == 0
                ? "✓ 真实模式启动条件已填写完整。点击启动后仍会重新读取并核验全部实时状态。"
                : $"真实模式当前有 {issues.Count} 个启动阻断项：{Environment.NewLine}• {string.Join($"{Environment.NewLine}• ", issues)}";
        }
    }
    public string RealModeStatusSummary
    {
        get
        {
            var issueCount = RealModeEligibilityIssues().Count;
            return issueCount == 0
                ? "✓ 真实模式启动资料已填写；启动时仍会重新核验实时状态。"
                : $"真实模式：{issueCount} 个启动阻断项。完整清单已移到“高级设置”。";
        }
    }

    public string StateText { get => stateText; private set { stateText = value; RaisePropertyChanged(); } }
    public string CurrentStageText { get => currentStageText; private set { currentStageText = value; RaisePropertyChanged(); } }
    public string NextStageText { get => nextStageText; private set { nextStageText = value; RaisePropertyChanged(); } }
    public string StatusMessage { get => statusMessage; private set { statusMessage = value; RaisePropertyChanged(); } }
    public string Phd2CalibrationGradeText { get => phd2CalibrationGradeText; private set { phd2CalibrationGradeText = value; RaisePropertyChanged(); } }
    public string Phd2CalibrationPolicyText { get => phd2CalibrationPolicyText; private set { phd2CalibrationPolicyText = value; RaisePropertyChanged(); } }
    public string Phd2CommissioningRouteText { get => phd2CommissioningRouteText; private set { phd2CommissioningRouteText = value; RaisePropertyChanged(); } }
    public string Phd2CalibrationPermissionText { get => phd2CalibrationPermissionText; private set { phd2CalibrationPermissionText = value; RaisePropertyChanged(); } }
    public string Phd2CalibrationScaleText { get => phd2CalibrationScaleText; private set { phd2CalibrationScaleText = value; RaisePropertyChanged(); } }
    public string Phd2CalibrationReasonText { get => phd2CalibrationReasonText; private set { phd2CalibrationReasonText = value; RaisePropertyChanged(); } }
    public string Phd2CalibrationOverviewGradeText { get => phd2CalibrationOverviewGradeText; private set { phd2CalibrationOverviewGradeText = value; RaisePropertyChanged(); } }
    public string Phd2CalibrationOverviewText { get => phd2CalibrationOverviewText; private set { phd2CalibrationOverviewText = value; RaisePropertyChanged(); } }
    public string PauseReason { get => pauseReason; private set { pauseReason = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(HasPauseReason)); } }
    public string RunManifestPath { get => runManifestPath; private set { runManifestPath = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(HasRunManifest)); } }
    public string OperatorNotice { get => operatorNotice; private set { operatorNotice = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(HasOperatorNotice)); } }
    public string Error { get => error; private set { error = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(HasError)); } }
    public double ProgressPercent { get => progressPercent; private set { progressPercent = value; RaisePropertyChanged(); } }
    public ImageSource? QhyPreviewImage { get => qhyPreviewImage; private set { qhyPreviewImage = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(HasQhyPreview)); RaisePropertyChanged(nameof(HasNoQhyPreview)); RaisePropertyChanged(nameof(HasFailurePreview)); } }
    public ImageSource? G3PreviewImage { get => g3PreviewImage; private set { g3PreviewImage = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(HasG3Preview)); RaisePropertyChanged(nameof(HasNoG3Preview)); RaisePropertyChanged(nameof(HasFailurePreview)); } }
    public ImageSource? AtrPreviewImage { get => atrPreviewImage; private set { atrPreviewImage = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(HasAtrPreview)); RaisePropertyChanged(nameof(HasNoAtrPreview)); RaisePropertyChanged(nameof(HasFailurePreview)); } }
    public string QhyPreviewCaption { get => qhyPreviewCaption; private set { qhyPreviewCaption = value; RaisePropertyChanged(); } }
    public string G3PreviewCaption { get => g3PreviewCaption; private set { g3PreviewCaption = value; RaisePropertyChanged(); } }
    public string AtrPreviewCaption { get => atrPreviewCaption; private set { atrPreviewCaption = value; RaisePropertyChanged(); } }
    public string LastFailureHeadline { get => lastFailureHeadline; private set { lastFailureHeadline = value; RaisePropertyChanged(); } }
    public string LastFailureCode { get => lastFailureCode; private set { lastFailureCode = value; RaisePropertyChanged(); } }
    public string LastFailureMessage { get => lastFailureMessage; private set { lastFailureMessage = value; RaisePropertyChanged(); } }
    public string LastFailureMetrics { get => lastFailureMetrics; private set { lastFailureMetrics = value; RaisePropertyChanged(); } }
    public string LastFailureRecommendation { get => lastFailureRecommendation; private set { lastFailureRecommendation = value; RaisePropertyChanged(); } }
    public string LastFailureEvidencePath { get => lastFailureEvidencePath; private set { lastFailureEvidencePath = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(HasFailureEvidence)); } }
    public string LastFailurePreviewLabel { get => lastFailurePreviewLabel; private set { lastFailurePreviewLabel = value; RaisePropertyChanged(); } }
    public string LatestEvidencePath { get => latestEvidencePath; private set { latestEvidencePath = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(HasLatestEvidence)); } }
    public string LatestEvidenceSummary { get => latestEvidenceSummary; private set { latestEvidenceSummary = value; RaisePropertyChanged(); } }
    public string GhostCalibrationSummaryText { get => ghostCalibrationSummaryText; private set { ghostCalibrationSummaryText = value; RaisePropertyChanged(); } }
    public string SlitIdentityStatusText { get => slitIdentityStatusText; private set { slitIdentityStatusText = value; RaisePropertyChanged(); } }
    public string GhostApplicabilityText { get => ghostApplicabilityText; private set { ghostApplicabilityText = value; RaisePropertyChanged(); } }
    public string GhostDecisionText { get => ghostDecisionText; private set { ghostDecisionText = value; RaisePropertyChanged(); } }
    public string GhostAssistanceModeText { get => ghostAssistanceModeText; private set { ghostAssistanceModeText = value; RaisePropertyChanged(); } }
    public string GhostOverviewText { get => ghostOverviewText; private set { ghostOverviewText = value; RaisePropertyChanged(); } }
    public bool HasFailure
    {
        get => hasFailure;
        private set
        {
            if (hasFailure == value) return;
            hasFailure = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(HasNoFailure));
            RaisePropertyChanged(nameof(HasFailurePreview));
        }
    }
    public bool HasNoFailure => !HasFailure;
    public bool HasQhyPreview => QhyPreviewImage is not null;
    public bool HasNoQhyPreview => !HasQhyPreview;
    public bool HasG3Preview => G3PreviewImage is not null;
    public bool HasNoG3Preview => !HasG3Preview;
    public bool HasAtrPreview => AtrPreviewImage is not null;
    public bool HasNoAtrPreview => !HasAtrPreview;
    public bool HasFailurePreview => HasFailure && FailurePreviewAvailable();
    public bool HasFailureEvidence => !string.IsNullOrWhiteSpace(LastFailureEvidencePath) && PathExists(LastFailureEvidencePath);
    public bool HasLatestEvidence => !string.IsNullOrWhiteSpace(LatestEvidencePath) && PathExists(LatestEvidencePath);
    public bool HasRunManifest => !string.IsNullOrWhiteSpace(RunManifestPath) && PathExists(RunManifestPath);
    public bool HasOperatorNotice => !string.IsNullOrWhiteSpace(OperatorNotice);
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool HasPauseReason => !string.IsNullOrWhiteSpace(PauseReason);
    public bool IsTargetPlanEditable => !IsTargetImportBusy && CanEditTargetPlan();
    public bool ImportFramingCenter
    {
        get => importFramingCenter;
        set
        {
            if (importFramingCenter == value) return;
            importFramingCenter = value;
            RaisePropertyChanged();
        }
    }
    public bool IsTargetImportBusy
    {
        get => isTargetImportBusy;
        private set
        {
            if (isTargetImportBusy == value) return;
            isTargetImportBusy = value;
            RaisePropertyChanged();
            RaiseCommandStates();
        }
    }
    public bool HasTargetImport
    {
        get => hasTargetImport;
        private set
        {
            if (hasTargetImport == value) return;
            hasTargetImport = value;
            RaisePropertyChanged();
        }
    }
    public string TargetImportSummary
    {
        get => targetImportSummary;
        private set
        {
            if (string.Equals(targetImportSummary, value, StringComparison.Ordinal)) return;
            targetImportSummary = value;
            RaisePropertyChanged();
        }
    }
    public string TargetImportDetails
    {
        get => targetImportDetails;
        private set
        {
            if (string.Equals(targetImportDetails, value, StringComparison.Ordinal)) return;
            targetImportDetails = value;
            RaisePropertyChanged();
        }
    }
    public int SelectedWorkspaceTabIndex
    {
        get => selectedWorkspaceTabIndex;
        set
        {
            if (selectedWorkspaceTabIndex == value) return;
            selectedWorkspaceTabIndex = value;
            RaisePropertyChanged();
        }
    }
    public bool IsSimulationOnly => false;

    public string AtrManualCameraStatus
    {
        get => atrManualCameraStatus;
        private set
        {
            if (string.Equals(atrManualCameraStatus, value, StringComparison.Ordinal)) return;
            atrManualCameraStatus = value;
            RaisePropertyChanged();
        }
    }

    public string AtrManualCaptureStatus
    {
        get => atrManualCaptureStatus;
        private set
        {
            if (string.Equals(atrManualCaptureStatus, value, StringComparison.Ordinal)) return;
            atrManualCaptureStatus = value;
            RaisePropertyChanged();
        }
    }

    public string AtrManualCaptureError
    {
        get => atrManualCaptureError;
        private set
        {
            if (string.Equals(atrManualCaptureError, value, StringComparison.Ordinal)) return;
            atrManualCaptureError = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(HasAtrManualCaptureError));
        }
    }

    public bool HasAtrManualCaptureError => !string.IsNullOrWhiteSpace(AtrManualCaptureError);
    public string BoundAtrCameraId => string.IsNullOrWhiteSpace(settings.BoundCameraId) ? "未绑定" : settings.BoundCameraId;
    public string AtrManualCapturePresetText =>
        $"{settings.ExposureSeconds:G6} s · Gain {settings.Gain} · Offset {settings.Offset} · {settings.Binning}×{settings.Binning}";
    public string ManualSpectrumSummary => UvexRuntimeState.MetricSummary;
    public PointCollection ManualSpectrumPoints => UvexRuntimeState.SpectrumPoints;

    private ObservationRunState RunState => host.Dashboard.Run.State;

    private bool IsControllable => RunState is
        ObservationRunState.Validating or
        ObservationRunState.RunningAuto or
        ObservationRunState.PauseRequested or
        ObservationRunState.Paused or
        ObservationRunState.PausedNeedsAttention or
        ObservationRunState.ManualTakeover;

    public void Dispose()
    {
        host.DashboardChanged -= OnDashboardChanged;
        UvexRuntimeState.Changed -= OnManualSpectrumChanged;
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private Task StartSelectedModeAsync() => UseRealMode
        ? StartRealAsync()
        : StartSimulationAsync();

    private void SelectMode(bool useRealMode)
    {
        UseRealMode = useRealMode;
        OperatorNotice = useRealMode
            ? "已选择真实设备控制。此选择本身不会连接或移动设备；请在“高级设置”查看并处理完整启动阻断项。"
            : "已选择模拟演练。模拟运行不会连接或移动任何真实设备。";
        Error = string.Empty;
    }

    private Task ImportFromFramingAssistantAsync() => RunTargetImportAsync(
        () => Task.FromResult(targetImportService.ImportFromFramingAssistant(ImportFramingCenter)));

    private Task ImportFromPlanetariumAsync() => RunTargetImportAsync(
        () => targetImportService.ImportFromPlanetariumAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(15)));

    private async Task RunTargetImportAsync(
        Func<Task<ObservationTargetImportResult>> import)
    {
        if (!CanImportTarget()) return;

        var editGeneration = targetEditGeneration;
        IsTargetImportBusy = true;
        Error = string.Empty;
        try
        {
            var result = await import().ConfigureAwait(true);
            if (!CanEditTargetPlan())
            {
                throw new ObservationTargetImportException(
                    "TARGET_IMPORT_RUN_STARTED",
                    "读取目标期间观测流程已经启动；为避免修改活动计划，本次结果未应用。请结束运行后重新导入。");
            }
            if (editGeneration != targetEditGeneration)
            {
                throw new ObservationTargetImportException(
                    "TARGET_IMPORT_EDIT_CONFLICT",
                    "读取目标期间目标草稿已被手工编辑；为避免覆盖人工修改，本次结果未应用。请确认草稿后重新导入。");
            }

            ApplyImportedTarget(result);
        }
        catch (TimeoutException)
        {
            Error = "目标导入失败 [PLANETARIUM_TIMEOUT]：等待 N.I.N.A. 第三方星图返回当前目标超过 15 秒。请确认 Stellarium 已启动并已选中目标。";
        }
        catch (ObservationTargetImportException ex)
        {
            Error = $"目标导入失败 [{ex.Code}]：{ex.Message}";
        }
        catch (Exception ex)
        {
            Error = $"目标导入失败 [TARGET_IMPORT_UNEXPECTED]：{ex.Message}";
        }
        finally
        {
            IsTargetImportBusy = false;
        }
    }

    private void ApplyImportedTarget(ObservationTargetImportResult result)
    {
        // Commit the detached target snapshot as one UI-thread operation. Do not
        // route through the public setters: those setters intentionally mark any
        // later operator edit as manual and invalidate this provenance record.
        settings.ObservationTargetName = result.TargetName;
        settings.ObservationCatalogId = result.CatalogId;
        settings.ObservationRightAscensionDegrees = result.RightAscensionDegrees;
        settings.ObservationDeclinationDegrees = result.DeclinationDegrees;
        settings.ObservationCoordinateEpoch = result.Epoch;
        settings.ObservationTargetImportSource = result.Source;
        settings.ObservationTargetImportedUtc = result.ImportedUtc.ToUniversalTime().ToString("O");
        settings.ObservationTargetPositionAngleDegrees = result.PositionAngleDegrees ?? double.NaN;

        var auditSummary = FormatTargetImportSummary(
            result.Source,
            result.TargetName,
            result.CatalogId,
            result.RightAscensionDegrees,
            result.DeclinationDegrees,
            result.PositionAngleDegrees,
            result.ImportedUtc);
        settings.ObservationTargetImportDetails = $"{auditSummary}{Environment.NewLine}{result.Details}";

        RaisePropertyChanged(nameof(TargetName));
        RaisePropertyChanged(nameof(CatalogId));
        RaisePropertyChanged(nameof(RightAscensionDegrees));
        RaisePropertyChanged(nameof(DeclinationDegrees));

        HasTargetImport = true;
        TargetImportSummary = auditSummary;
        TargetImportDetails = result.Details;
        OperatorNotice = $"目标草稿已更新为 {result.TargetName}：RA {result.RightAscensionDegrees:F8}°，Dec {result.DeclinationDegrees:+0.00000000;-0.00000000;0.00000000}°（J2000）。Night Setup、commissioning、时长和安全限制未改变。";
        Error = string.Empty;
    }

    private void LoadTargetImportDisplay()
    {
        var source = settings.ObservationTargetImportSource;
        if (!string.Equals(source, "手工输入", StringComparison.Ordinal) &&
            DateTimeOffset.TryParse(settings.ObservationTargetImportedUtc, out var importedUtc))
        {
            HasTargetImport = true;
            var summary = FormatTargetImportSummary(
                source,
                settings.ObservationTargetName,
                settings.ObservationCatalogId,
                settings.ObservationRightAscensionDegrees,
                settings.ObservationDeclinationDegrees,
                double.IsFinite(settings.ObservationTargetPositionAngleDegrees)
                    ? settings.ObservationTargetPositionAngleDegrees
                    : null,
                importedUtc);
            TargetImportSummary = summary;
            var persistedDetails = settings.ObservationTargetImportDetails;
            var duplicatedPrefix = $"{summary}{Environment.NewLine}";
            TargetImportDetails = persistedDetails.StartsWith(duplicatedPrefix, StringComparison.Ordinal)
                ? persistedDetails[duplicatedPrefix.Length..]
                : persistedDetails;
            return;
        }

        HasTargetImport = false;
        TargetImportSummary = "目标由手工输入。";
        TargetImportDetails = settings.ObservationTargetImportDetails;
    }

    private static string FormatTargetImportSummary(
        string source,
        string targetName,
        string catalogId,
        double rightAscensionDegrees,
        double declinationDegrees,
        double? positionAngleDegrees,
        DateTimeOffset importedUtc)
    {
        var catalogText = string.IsNullOrWhiteSpace(catalogId) ? "无（已清空）" : catalogId;
        var positionAngleText = positionAngleDegrees is { } pa && double.IsFinite(pa)
            ? $"{pa:0.###}°（仅记录）"
            : "未提供";
        return $"已从 {source} 导入 · {targetName} · 目录 ID：{catalogText} · "
            + $"J2000 RA {rightAscensionDegrees:F8}° / Dec {declinationDegrees:+0.00000000;-0.00000000;0.00000000}° · "
            + $"PA：{positionAngleText} · {importedUtc.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC";
    }

    private void MarkTargetAsManuallyEdited()
    {
        targetEditGeneration++;
        settings.ObservationCoordinateEpoch = ObservationTargetImportService.J2000Epoch;
        settings.ObservationTargetImportSource = "手工输入";
        settings.ObservationTargetImportedUtc = string.Empty;
        settings.ObservationTargetImportDetails = "目标名称、目录 ID 或坐标已被操作员手工编辑；先前外部导入的来源证明已失效。";
        settings.ObservationTargetPositionAngleDegrees = double.NaN;
        HasTargetImport = false;
        TargetImportSummary = "目标由手工输入。";
        TargetImportDetails = settings.ObservationTargetImportDetails;
    }

    private async Task StartSimulationAsync()
    {
        try
        {
            Error = string.Empty;
            settings.ObservationUseRealMode = false;
            RaisePropertyChanged(nameof(UseRealMode));
            RaisePropertyChanged(nameof(ModeText));
            RaisePropertyChanged(nameof(ModeDescription));
            RaisePropertyChanged(nameof(StartButtonText));
            RaisePropertyChanged(nameof(IsSimulationMode));
            RaisePropertyChanged(nameof(IsRealMode));
            var plan = ObservationPlanFactory.FromSettings(settings);
            var issues = plan.Validate();
            if (issues.Count > 0)
            {
                Error = string.Join(" ", issues);
                return;
            }
            await host.RunSimulationAsync(
                plan,
                SimulationStageMilliseconds,
                new Progress<ApplicationStatus>(),
                CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task StartRealAsync()
    {
        try
        {
            Error = string.Empty;
            var eligibility = RealModeEligibilityIssues();
            if (eligibility.Count > 0)
            {
                Error = string.Join(" ", eligibility);
                return;
            }
            settings.ObservationUseRealMode = true;
            RaisePropertyChanged(nameof(UseRealMode));
            RaisePropertyChanged(nameof(ModeText));
            RaisePropertyChanged(nameof(ModeDescription));
            RaisePropertyChanged(nameof(StartButtonText));
            RaisePropertyChanged(nameof(IsSimulationMode));
            RaisePropertyChanged(nameof(IsRealMode));
            var lockedConfiguration = realRunnerFactory.CaptureConfiguration(settings);
            var plan = ObservationPlanFactory.FromSettings(settings, lockedConfiguration);
            var issues = plan.Validate();
            if (issues.Count > 0)
            {
                Error = string.Join(" ", issues);
                return;
            }
            await using var runner = realRunnerFactory.Create(
                host,
                settings,
                new Progress<ApplicationStatus>(),
                lockedConfiguration);
            await host.RunAsync(plan, runner, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private void Resume()
    {
        Error = host.Resume() ? string.Empty : "当前状态不能恢复；请检查暂停原因或先取消。";
    }

    private bool CanStart() => !IsTargetImportBusy && CanEditTargetPlan();

    private bool CanImportTarget() => !IsTargetImportBusy && CanEditTargetPlan();

    private bool CanUseManualAtrTools() => !IsTargetImportBusy && CanEditTargetPlan();

    private bool CanEditTargetPlan() => RunState is
        ObservationRunState.Idle or
        ObservationRunState.Completed or
        ObservationRunState.Cancelled or
        ObservationRunState.Faulted;

    private bool CanStartReal() => CanStart() && RealModeEligibilityIssues().Count == 0;

    private void BindCurrentAtrCamera()
    {
        try
        {
            var info = cameraMediator.GetInfo();
            if (!info.Connected || string.IsNullOrWhiteSpace(info.DeviceId))
            {
                throw new InvalidOperationException("请先在 N.I.N.A. 的相机页连接 ATR585M。");
            }

            var identity = string.Join('|', info.Name, info.DisplayName, info.Description, info.DeviceId);
            if (!identity.Contains(settings.ExpectedCameraName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"当前设备“{info.DisplayName ?? info.Name}”不是配置的 {settings.ExpectedCameraName}；不会按相机列表顺序猜测。");
            }

            settings.BoundCameraId = info.DeviceId;
            AtrManualCaptureError = string.Empty;
            RaisePropertyChanged(nameof(BoundAtrCameraId));
            RefreshAtrManualStatus();
        }
        catch (Exception ex)
        {
            AtrManualCaptureError = ex.Message;
            RefreshAtrManualStatus();
        }
    }

    private void RefreshAtrManualStatus()
    {
        var info = cameraMediator.GetInfo();
        if (!info.Connected)
        {
            AtrManualCameraStatus = "N.I.N.A. 当前没有连接相机。";
        }
        else if (string.IsNullOrWhiteSpace(settings.BoundCameraId))
        {
            AtrManualCameraStatus = $"已连接 {info.DisplayName ?? info.Name}；尚未绑定稳定 DeviceId。";
        }
        else if (string.Equals(info.DeviceId, settings.BoundCameraId, StringComparison.Ordinal))
        {
            AtrManualCameraStatus = $"已连接并匹配：{info.DisplayName ?? info.Name}。";
        }
        else
        {
            AtrManualCameraStatus = $"当前相机 {info.DisplayName ?? info.Name} 与已绑定 ATR585M 不匹配。";
        }

        RaisePropertyChanged(nameof(BoundAtrCameraId));
        RaisePropertyChanged(nameof(AtrManualCapturePresetText));
        captureManualAtrSpectrumCommand.RaiseCanExecuteChanged();
    }

    private async Task CaptureManualAtrSpectrumAsync()
    {
        try
        {
            AtrManualCaptureError = string.Empty;
            AtrManualCaptureStatus = "正在通过 N.I.N.A. 采集一帧检查光谱…";
            RefreshAtrManualStatus();
            var progress = new Progress<ApplicationStatus>();
            var capture = new NinaSpectrumCapture(cameraMediator, imagingMediator, settings, progress);
            var spectrum = await capture.CaptureAsync(lifetime.Token).ConfigureAwait(true);
            AtrManualCaptureStatus = $"单帧检查完成：{spectrum.Flux.Length} 个色散采样点。";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            AtrManualCaptureStatus = "单帧检查已取消。";
        }
        catch (Exception ex)
        {
            AtrManualCaptureStatus = "单帧检查失败。";
            AtrManualCaptureError = ex.Message;
        }
        finally
        {
            if (!lifetime.IsCancellationRequested) RefreshAtrManualStatus();
        }
    }

    private void OnManualSpectrumChanged(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            RaisePropertyChanged(nameof(ManualSpectrumSummary));
            RaisePropertyChanged(nameof(ManualSpectrumPoints));
        }
        else
        {
            _ = dispatcher.BeginInvoke(() =>
            {
                RaisePropertyChanged(nameof(ManualSpectrumSummary));
                RaisePropertyChanged(nameof(ManualSpectrumPoints));
            });
        }
    }

    private IReadOnlyList<string> RealModeEligibilityIssues()
    {
        var issues = new List<string>();
        var capabilities = ObservationAutomationPolicy.ValidateFullAutomationCapabilities(
            settings.RequireSafetyMonitor,
            settings.RequireOpenDomeOrRoof,
            settings.RequireWeatherData,
            settings.RequireOpenOpticalCover);
        if (capabilities.Disposition != GateDisposition.Passed) issues.Add(capabilities.Message);
        if (!settings.RealModeCommissioned) issues.Add("真实模式尚未标记为已调试。");
        if (string.IsNullOrWhiteSpace(settings.CommissioningPresetPath) || !File.Exists(settings.CommissioningPresetPath)) issues.Add("缺少 commissioning preset 文件。");
        if (string.IsNullOrWhiteSpace(settings.CommissioningPresetId)) issues.Add("缺少 commissioning preset ID。");
        if (string.IsNullOrWhiteSpace(settings.CommissioningPresetSha256)) issues.Add("缺少 commissioning preset SHA-256。");
        if (!string.IsNullOrWhiteSpace(settings.CommissioningPresetPath) && File.Exists(settings.CommissioningPresetPath) &&
            !string.IsNullOrWhiteSpace(settings.CommissioningPresetSha256))
            issues.AddRange(ValidateCommissioningPresetUiRequirements());
        if (string.IsNullOrWhiteSpace(settings.CommissioningHardwareFingerprintSha256)) issues.Add("缺少硬件 fingerprint SHA-256。");
        if (string.IsNullOrWhiteSpace(settings.NightSetupSnapshotPath) || !File.Exists(settings.NightSetupSnapshotPath)) issues.Add("缺少 Night Setup 快照文件。");
        if (string.IsNullOrWhiteSpace(settings.NightSetupSnapshotSha256)) issues.Add("缺少 Night Setup SHA-256。");
        if (string.IsNullOrWhiteSpace(settings.ExpectedTelescopeId)) issues.Add("缺少赤道仪 DeviceId。");
        if (string.IsNullOrWhiteSpace(settings.ObservationExpectedAtrCameraId) || settings.ObservationExpectedAtrCameraId.StartsWith("SIM-", StringComparison.OrdinalIgnoreCase)) issues.Add("缺少真实 ATR585M DeviceId。");
        if (string.IsNullOrWhiteSpace(settings.ObservationExpectedQhyCameraId) || settings.ObservationExpectedQhyCameraId.StartsWith("SIM-", StringComparison.OrdinalIgnoreCase)) issues.Add("缺少真实 QHY StableId。");
        if (settings.Phd2ProfileId < 0 || string.IsNullOrWhiteSpace(settings.Phd2ProfileName) ||
            string.IsNullOrWhiteSpace(settings.Phd2CameraName) || string.IsNullOrWhiteSpace(settings.Phd2CameraStableId) ||
            string.IsNullOrWhiteSpace(settings.Phd2MountName) || string.IsNullOrWhiteSpace(settings.Phd2RuntimeCameraName) ||
            string.IsNullOrWhiteSpace(settings.Phd2RuntimeMountName))
        {
            issues.Add("PHD2 Profile、注册表设备名、JSON-RPC 运行时设备名和 G3 稳定身份不完整。");
        }
        if (settings.G3ExposureMilliseconds <= 0 || settings.G3GainPercent is < 0 or > 100)
        {
            issues.Add("G3 曝光/增益无效；它们必须由 commissioning bindings 显式锁定。");
        }
        if (settings.G3CameraRecoveryDelayMilliseconds is < 250 or > 10_000)
        {
            issues.Add("G3 连续全幅采集的驱动恢复等待必须在 250–10000 ms；本机实测 ToupTek/G3 需要 3000 ms。");
        }
        try
        {
            var solvePreset = new G3PlateSolveExposurePreset(
                settings.G3PlateSolveExposurePresetSchemaVersion,
                settings.G3PlateSolveExposurePresetId.Trim(),
                settings.ParseG3PlateSolveExposureLadder());
            issues.AddRange(solvePreset.Validate().Select(issue => $"G3 长曝光解算：{issue}"));
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            issues.Add($"G3 长曝光解算档位无法解析：{ex.Message}");
        }
        var wcsMinutes = double.IsFinite(settings.G3WcsMaximumCenteringMinutes) &&
                         settings.G3WcsMaximumCenteringMinutes <= TimeSpan.MaxValue.TotalMinutes
            ? settings.G3WcsMaximumCenteringMinutes
            : 0;
        var wcsCentering = new G3WcsCenteringLimits(
            settings.G3WcsCenteringSchemaVersion,
            settings.G3WcsMaximumSingleCorrectionArcseconds,
            settings.G3WcsMaximumRadiusArcseconds,
            settings.G3WcsMaximumCumulativeMotionArcseconds,
            settings.G3WcsMaximumCorrectionAttempts,
            TimeSpan.FromMinutes(wcsMinutes),
            settings.G3TargetInsideFieldMarginPixels);
        issues.AddRange(wcsCentering.Validate().Select(issue => $"G3 WCS 居中：{issue}"));
        if (!double.IsFinite(settings.G3MotionPostSlewSettleSeconds) || settings.G3MotionPostSlewSettleSeconds <= 0)
            issues.Add("G3 移动后稳定等待必须是经 commissioning 显式确认的正秒数。");
        if (!double.IsFinite(settings.G3MotionWorstCaseActionSeconds) ||
            settings.G3MotionWorstCaseActionSeconds <= settings.G3MotionPostSlewSettleSeconds)
            issues.Add("G3 单动作最坏时长必须大于移动后稳定等待，并覆盖移动、等待与复核。");
        if (settings.BrightTargetWingCentroidEnabled)
        {
            if (settings.BrightTargetMinimumG3ExposureMilliseconds <= 0 ||
                settings.BrightTargetMinimumG3ExposureMilliseconds > settings.G3ExposureMilliseconds)
                issues.Add("亮目标最短 G3 曝光必须显式设为正数，且不能长于常规 G3 曝光。");
            if (!double.IsFinite(settings.BrightTargetMaximumQhyWcsAgeMinutes) || settings.BrightTargetMaximumQhyWcsAgeMinutes <= 0 ||
                !double.IsFinite(settings.BrightTargetMaximumG3FrameAgeMinutes) || settings.BrightTargetMaximumG3FrameAgeMinutes <= 0)
                issues.Add("亮目标 QHY WCS/G3 帧新鲜度必须显式设为正数。");
            if (!double.IsFinite(settings.BrightTargetMaximumQhyResidualArcseconds) || settings.BrightTargetMaximumQhyResidualArcseconds <= 0 ||
                !double.IsFinite(settings.BrightTargetMaximumCatalogMismatchArcseconds) || settings.BrightTargetMaximumCatalogMismatchArcseconds <= 0)
                issues.Add("亮目标 QHY 残差与目录坐标差门必须显式设为正数。");
            if (!double.IsFinite(settings.BrightTargetMinimumC11FocusConfidence) || settings.BrightTargetMinimumC11FocusConfidence is <= 0 or > 1)
                issues.Add("亮目标独立 C11 焦点证据置信度门必须在 (0,1]。");
            var morphology = new BrightTargetCentroidOptions(
                settings.BrightTargetMinimumSaturatedCorePixels,
                settings.BrightTargetMaximumSaturatedCorePixels,
                settings.BrightTargetWingRadiusPixels,
                settings.BrightTargetMinimumWingProminenceSigma,
                settings.BrightTargetMaximumWingLevelFraction,
                settings.BrightTargetMinimumWingPixels,
                settings.BrightTargetMinimumWingSignalToNoise,
                settings.BrightTargetMinimumAngularCoverageFraction,
                settings.BrightTargetMinimumOpposedWingBalance,
                settings.BrightTargetMaximumWingCentroidDisagreementPixels,
                settings.BrightTargetEdgeMarginPixels,
                settings.BrightTargetNearbySaturatedCoreRadiusPixels,
                settings.BrightTargetMinimumUniquenessRatio,
                settings.BrightTargetMaximumSecondaryPeakRatio);
            issues.AddRange(BrightTargetWingCentroidAnalyzer.ValidateOptions(morphology)
                .Select(issue => $"亮目标翼部质心：{issue}"));
        }
        if (string.IsNullOrWhiteSpace(settings.Phd2ProfileEvidenceSha256)) issues.Add("缺少 PHD2 注册表证据 SHA-256。");
        if (!string.Equals(
                activeProfileService.ActiveProfile.FilterWheelSettings.Id,
                NinaProfileOwnerPreflight.NoPhysicalFilterWheelDeviceId,
                StringComparison.Ordinal))
        {
            issues.Add("N.I.N.A. Profile 的滤镜轮必须为 No_Device；QHY 物理滤镜轮由独立 QHY 服务独占。");
        }
        if (!string.Equals(
                activeProfileService.ActiveProfile.GuiderSettings.GuiderName,
                NinaProfileOwnerPreflight.Phd2GuiderName,
                StringComparison.Ordinal))
        {
            issues.Add("N.I.N.A. Profile 的导星适配器必须精确为稳定 ID PHD2_Single；显示名 PHD2 不被接受。");
        }
        if (settings.WideToSlitTransferMode != WideToSlitTransferMode.Skip)
        {
            issues.Add("当前 runner 尚无经独立验证的 QHY→G3 Active 记录导入/激活路径，WideToSlitTransferMode 必须为 Skip；快速双解算 Candidate 不授权运动。");
        }
        var fastPair = new QhyG3FastPairPolicy(
            settings.QhyG3FastPairSchemaVersion,
            settings.QhyG3FastPairPolicyId.Trim(),
            settings.QhyG3FastPairEnabled,
            settings.QhyG3FastPairExposureSeconds,
            QhyG3FastPairPolicy.ValidationTimeSpanFromSeconds(settings.QhyG3FastPairMaximumCachedAgeSeconds),
            QhyG3FastPairPolicy.ValidationTimeSpanFromSeconds(settings.QhyG3FastPairMaximumMidpointSeparationSeconds),
            QhyG3FastPairPolicy.ValidationTimeSpanFromSeconds(settings.QhyG3FastPairMaximumWallClockSeconds),
            settings.QhyG3FastPairMaximumMountSpanArcseconds,
            QhyG3FastPairPolicy.ValidationTimeSpanFromHours(settings.QhyG3FastPairCandidateValidityHours),
            settings.QhyG3FastPairMaximumCandidateUncertaintyArcseconds);
        issues.AddRange(fastPair.Validate().Select(issue => $"QHY/G3 快速配对：{issue}"));
        var coarseLimits = new QhyCoarseCenteringLimits(
            settings.QhyCoarseCenteringSchemaVersion,
            settings.QhyCoarseMaximumSingleCorrectionArcseconds,
            settings.QhyCoarseMaximumCumulativeCorrectionArcseconds,
            settings.QhyCoarseMaximumCorrectionAttempts,
            TimeSpan.FromMinutes(double.IsFinite(settings.QhyCoarseMaximumCenteringMinutes)
                ? settings.QhyCoarseMaximumCenteringMinutes
                : 0));
        issues.AddRange(coarseLimits.Validate().Select(issue => $"QHY 广域粗居中：{issue}"));
        var searchLimits = new G3LocalSearchLimits(
            settings.G3SearchPattern,
            settings.G3SearchStepArcseconds,
            settings.G3SearchMaximumRadiusArcseconds,
            settings.G3SearchMaximumCumulativeArcseconds,
            settings.G3SearchMaximumAttempts,
            TimeSpan.FromMinutes(double.IsFinite(settings.G3SearchMaximumMinutes)
                ? settings.G3SearchMaximumMinutes
                : 0));
        issues.AddRange(searchLimits.Validate().Select(issue => $"G3 有界搜索：{issue}"));
        if (settings.G3SearchStepArcseconds > settings.MaximumSingleCorrectionArcseconds)
        {
            issues.Add("G3 搜索步长超过 commissioning 的单次运动上限。");
        }
        if (settings.G3SearchMaximumCumulativeArcseconds > settings.MaximumCumulativeCorrectionArcseconds)
        {
            issues.Add("G3 搜索累计运动上限超过 commissioning 的累计运动上限。");
        }
        if (settings.G3SearchMaximumAttempts > settings.MaximumCorrectionAttempts)
        {
            issues.Add("G3 搜索尝试次数超过 commissioning 的总修正次数上限。");
        }
        if (settings.G3SearchMaximumMinutes > settings.MaximumAcquisitionMinutes)
        {
            issues.Add("G3 搜索耗时上限超过 commissioning 的总采集耗时上限。");
        }
        return issues;
    }

    private void OnDashboardChanged(object? sender, ObservationDashboardSnapshot dashboard)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyDashboard(dashboard);
        }
        else
        {
            _ = dispatcher.BeginInvoke(() => ApplyDashboard(dashboard));
        }
    }

    private void ApplyDashboard(ObservationDashboardSnapshot dashboard)
    {
        var run = dashboard.Run;
        StateText = RunStateDisplayName(run.State);
        CurrentStageText = run.CurrentStage is { } current ? SimulatedObservationStageRunner.StageDisplayName(current) : "—";
        NextStageText = run.NextStage is { } next ? SimulatedObservationStageRunner.StageDisplayName(next) : "—";
        StatusMessage = run.StatusMessage;
        PauseReason = run.PauseReason ?? string.Empty;
        RunManifestPath = dashboard.ManifestPath ?? string.Empty;
        ProgressPercent = run.TotalStageCount == 0 ? 0 : 100d * run.CompletedStageCount / run.TotalStageCount;

        GateRows.Clear();
        foreach (var stage in ObservationRunCoordinator.Stages)
        {
            dashboard.Gates.TryGetValue(stage, out var gate);
            GateRows.Add(new ObservationGateRow(
                SimulatedObservationStageRunner.StageDisplayName(stage),
                gate is null ? "等待" : GateDisplayName(gate.Disposition),
                gate?.Code ?? "—",
                gate?.Message ?? "尚未执行",
                FormatMetrics(gate?.Metrics),
                gate?.Disposition ?? GateDisposition.Indeterminate));
        }

        TimelineRows.Clear();
        foreach (var item in run.RecentEvents.TakeLast(80))
        {
            TimelineRows.Add(new ObservationTimelineRow(
                item.TimestampUtc.ToLocalTime().ToString("HH:mm:ss"),
                item.Stage is { } eventStage ? SimulatedObservationStageRunner.StageDisplayName(eventStage) : "运行",
                item.Code,
                item.Message,
                item.EvidencePath ?? string.Empty));
        }

        EvidenceRows.Clear();
        foreach (var item in dashboard.Evidence.TakeLast(60).Reverse())
        {
            EvidenceRows.Add(new ObservationEvidenceRow(
                item.PublishedUtc.ToLocalTime().ToString("HH:mm:ss"),
                item.Kind,
                Path.GetFileName(item.AbsolutePath),
                item.AbsolutePath));
        }
        var latestEvidence = dashboard.Evidence.LastOrDefault();
        LatestEvidencePath = latestEvidence?.AbsolutePath ?? string.Empty;
        LatestEvidenceSummary = latestEvidence is null
            ? "尚未生成运行证据。"
            : $"{latestEvidence.Kind} · {Path.GetFileName(latestEvidence.AbsolutePath)} · {latestEvidence.PublishedUtc.ToLocalTime():HH:mm:ss}";

        ApplyPhd2CalibrationQuality(dashboard);
        ApplyGhostAssistanceStatus(dashboard);
        ApplySlitIdentityStatus(dashboard);

        ApplyPreview(dashboard, ObservationPreviewChannel.QhyWideField);
        ApplyPreview(dashboard, ObservationPreviewChannel.G3SlitField);
        ApplyPreview(dashboard, ObservationPreviewChannel.AtrSpectrum);
        ApplyFailureDiagnostic(dashboard);
        RaiseCommandStates();
    }

    private void ApplyFailureDiagnostic(ObservationDashboardSnapshot dashboard)
    {
        ObservationEvent? failureEvent = null;
        ObservationStage? failureStage = null;
        GateResult? failureGate = null;

        for (var index = dashboard.Run.RecentEvents.Count - 1; index >= 0; index--)
        {
            var candidate = dashboard.Run.RecentEvents[index];
            if (candidate.Stage is not { } stage ||
                !dashboard.Gates.TryGetValue(stage, out var gate) ||
                gate.Disposition == GateDisposition.Passed ||
                !string.Equals(candidate.Code, gate.Code, StringComparison.Ordinal))
            {
                continue;
            }
            failureEvent = candidate;
            failureStage = stage;
            failureGate = gate;
            break;
        }

        if (failureGate is null &&
            dashboard.Run.State is ObservationRunState.Faulted or ObservationRunState.PausedNeedsAttention &&
            dashboard.Run.RecentEvents.LastOrDefault() is { } terminalEvent)
        {
            failureEvent = terminalEvent;
            failureStage = terminalEvent.Stage ?? dashboard.Run.CurrentStage;
            failureGate = failureStage is { } stage && dashboard.Gates.TryGetValue(stage, out var stageGate)
                ? stageGate
                : GateResult.Unknown(terminalEvent.Code, terminalEvent.Message);
        }

        if (failureGate is null || failureStage is null)
        {
            HasFailure = false;
            LastFailureHeadline = "目前没有失败记录。";
            LastFailureCode = "—";
            LastFailureMessage = "运行开始后，这里会显示最近未通过的质量门及其完整原因。";
            LastFailureMetrics = "—";
            LastFailureRecommendation = "三路预览、质量门、时间线和证据文件会在运行中持续更新。";
            LastFailureEvidencePath = string.Empty;
            LastFailurePreviewLabel = "没有需要检查的失败图像";
            lastFailurePreviewChannel = null;
            RaisePropertyChanged(nameof(HasFailurePreview));
            return;
        }

        var wasAlreadyShowingFailure = HasFailure;
        HasFailure = true;
        if (!wasAlreadyShowingFailure)
        {
            SelectedWorkspaceTabIndex = 3;
        }
        var guidance = ObservationOperatorGuidance.For(failureStage.Value, failureGate);
        LastFailureHeadline = $"{SimulatedObservationStageRunner.StageDisplayName(failureStage.Value)} · {GateDisplayName(failureGate.Disposition)}";
        LastFailureCode = failureGate.Code;
        LastFailureMessage = failureGate.Message;
        LastFailureMetrics = FormatMetrics(failureGate.Metrics);
        LastFailureRecommendation = guidance.Recommendation;
        LastFailureEvidencePath = failureEvent?.EvidencePath ?? string.Empty;
        LastFailurePreviewLabel = guidance.PreviewChannel is null
            ? "此类联锁失败通常没有天文图像；请检查启动条件和时间线"
            : $"打开 {guidance.PreviewLabel}";
        lastFailurePreviewChannel = guidance.PreviewChannel;
        RaisePropertyChanged(nameof(HasFailurePreview));
    }

    private void ApplyPreview(ObservationDashboardSnapshot dashboard, ObservationPreviewChannel channel)
    {
        if (!dashboard.Previews.TryGetValue(channel, out var preview)) return;
        switch (channel)
        {
            case ObservationPreviewChannel.QhyWideField:
                QhyPreviewImage = preview.Image;
                QhyPreviewCaption = preview.Caption;
                break;
            case ObservationPreviewChannel.G3SlitField:
                G3PreviewImage = preview.Image;
                G3PreviewCaption = preview.Caption;
                break;
            case ObservationPreviewChannel.AtrSpectrum:
                AtrPreviewImage = preview.Image;
                AtrPreviewCaption = preview.Caption;
                break;
        }
    }

    private void RaiseCommandStates()
    {
        RaisePropertyChanged(nameof(IsTargetPlanEditable));
        RaisePropertyChanged(nameof(RealModeStatusSummary));
        startSelectedModeCommand.RaiseCanExecuteChanged();
        startSimulationCommand.RaiseCanExecuteChanged();
        startRealCommand.RaiseCanExecuteChanged();
        selectSimulationModeCommand.RaiseCanExecuteChanged();
        selectRealModeCommand.RaiseCanExecuteChanged();
        pauseCommand.RaiseCanExecuteChanged();
        resumeCommand.RaiseCanExecuteChanged();
        cancelCommand.RaiseCanExecuteChanged();
        takeoverCommand.RaiseCanExecuteChanged();
        openQhyPreviewCommand.RaiseCanExecuteChanged();
        openG3PreviewCommand.RaiseCanExecuteChanged();
        openAtrPreviewCommand.RaiseCanExecuteChanged();
        openFailurePreviewCommand.RaiseCanExecuteChanged();
        openFailureEvidenceDirectoryCommand.RaiseCanExecuteChanged();
        openLatestEvidenceDirectoryCommand.RaiseCanExecuteChanged();
        openRunDirectoryCommand.RaiseCanExecuteChanged();
        importFromFramingAssistantCommand.RaiseCanExecuteChanged();
        importFromPlanetariumCommand.RaiseCanExecuteChanged();
        bindCurrentAtrCameraCommand.RaiseCanExecuteChanged();
        captureManualAtrSpectrumCommand.RaiseCanExecuteChanged();
    }

    private void RefreshProfileOwnership()
    {
        RaisePropertyChanged(nameof(NinaFilterWheelOwnershipStatus));
        RaisePropertyChanged(nameof(NinaGuiderOwnershipStatus));
        RaisePropertyChanged(nameof(RealModeStatus));
        RaiseCommandStates();
    }

    private void ApplyPhd2CalibrationQuality(ObservationDashboardSnapshot dashboard)
    {
        var commissioningSummary = ReadPhd2CommissioningSummary();
        Phd2CalibrationPolicyText = commissioningSummary.Policy;
        Phd2CommissioningRouteText = commissioningSummary.Route;
        dashboard.Gates.TryGetValue(ObservationStage.StartGuiding, out var guidingGate);
        dashboard.Gates.TryGetValue(ObservationStage.PlaceTargetOnSlit, out var placementGate);
        var gate = guidingGate?.Metrics?.ContainsKey("phd2CalibrationGrade") == true
            ? guidingGate
            : placementGate;
        if (gate?.Metrics is null ||
            !gate.Metrics.TryGetValue("phd2CalibrationGrade", out var numericGrade) ||
            !double.IsFinite(numericGrade))
        {
            Phd2CalibrationGradeText = gate is null ? "尚未评估" : "尚未获得 PostSettle 权限";
            Phd2CalibrationOverviewGradeText = Phd2CalibrationGradeText;
            Phd2CalibrationPermissionText = "验证导星：等待 · exact-lock：等待 · 无人值守科学：否";
            Phd2CalibrationScaleText = "步长/残差缩放：等待 PostSettle 证据";
            Phd2CalibrationReasonText = gate?.Message ?? "当前生产路径只评估 PHD2 当前 active calibration（单候选，并非历史择优）；本轮真实 settle 和 fresh residual 到齐后才授予 exact-lock 或科学权限。";
            Phd2CalibrationOverviewText = gate is null
                ? "启动导星后自动评估；评估前不执行精调或科学曝光。"
                : "等待本轮导星稳定和新的入缝残差；暂不继续精调或科学曝光。";
            return;
        }

        var gradeValue = (int)Math.Round(numericGrade);
        var grade = Enum.IsDefined(typeof(Phd2CalibrationQualityGrade), gradeValue)
            ? (Phd2CalibrationQualityGrade?)gradeValue
            : null;
        Phd2CalibrationGradeText = grade?.ToString() ?? $"未知等级 {numericGrade:G4}";
        Phd2CalibrationOverviewGradeText = grade switch
        {
            Phd2CalibrationQualityGrade.Excellent => "优秀",
            Phd2CalibrationQualityGrade.Qualified => "合格",
            Phd2CalibrationQualityGrade.DegradedSupervised => "降级（需人工监督）",
            Phd2CalibrationQualityGrade.Rejected => "不可用",
            _ => "未知等级",
        };
        Phd2CalibrationPermissionText =
            $"验证导星：{Permission(gate.Metrics, "phd2CanAttemptValidationGuide")} · " +
            $"exact-lock：{Permission(gate.Metrics, "phd2IsLockShiftAuthority")} · " +
            $"无人值守科学：{Permission(gate.Metrics, "phd2IsUnattendedScienceAuthority")}";
        Phd2CalibrationScaleText =
            $"步长缩放：{Metric(gate.Metrics, "phd2MaximumLockShiftScale")} · " +
            $"残差门缩放：{Metric(gate.Metrics, "phd2RequiredResidualToleranceScale")}";
        Phd2CalibrationReasonText = gate.Message;
        Phd2CalibrationOverviewText =
            $"精确入缝：{Permission(gate.Metrics, "phd2IsLockShiftAuthority")} · " +
            $"无人值守拍摄：{Permission(gate.Metrics, "phd2IsUnattendedScienceAuthority")}";
    }

    private IReadOnlyList<string> ValidateCommissioningPresetUiRequirements()
    {
        var issues = new List<string>();
        try
        {
            var bytes = File.ReadAllBytes(settings.CommissioningPresetPath);
            var actualSha256 = Convert.ToHexString(SHA256.HashData(bytes));
            var expectedSha256 = NormalizeDisplayHash(settings.CommissioningPresetSha256);
            if (expectedSha256.Length != 64 ||
                !string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add("commissioning preset 文件与锁定 SHA-256 不一致。");
                return issues;
            }
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (!TryGetProperty(root, "schemaVersion", out var schema) || !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 4)
                issues.Add("自动真实观测要求 schema 4 commissioning preset。");
            if (!TryGetProperty(root, "slitWheelIdentity", out var identity) || identity.ValueKind != JsonValueKind.Object ||
                !TryGetProperty(identity, "fingerprints", out var fingerprints) || fingerprints.ValueKind != JsonValueKind.Array ||
                fingerprints.GetArrayLength() != SlitWheelIdentityCalibration.RequiredWheelPositionCount)
            {
                issues.Add("commissioning preset 缺少四个独立 LED 狭缝宽度指纹。");
            }
            else
            {
                var calibration = JsonSerializer.Deserialize<SlitWheelIdentityCalibration>(identity.GetRawText(), CaseInsensitiveJson);
                if (calibration is null)
                    issues.Add("commissioning preset 的狭缝光学身份记录为空。");
                else
                    issues.AddRange(calibration.Validate().Select(issue => $"狭缝光学身份无效：{issue}"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            issues.Add($"commissioning preset 无法复核：{ex.Message}");
        }
        return issues;
    }

    private (string Policy, string Route) ReadPhd2CommissioningSummary()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(settings.CommissioningPresetPath) || !File.Exists(settings.CommissioningPresetPath))
                return ("commissioning policy 未加载", "路线：commissioning preset 不可读");
            var bytes = File.ReadAllBytes(settings.CommissioningPresetPath);
            var actualSha256 = Convert.ToHexString(SHA256.HashData(bytes));
            var expectedSha256 = NormalizeDisplayHash(settings.CommissioningPresetSha256);
            if (expectedSha256.Length != 64 ||
                !string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return (
                    "PHD2 policy 已隐藏：preset SHA-256 未验证",
                    $"路线：禁止显示或使用未验证 preset 的策略内容（actual {ShortHash(actualSha256)}）");
            }
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var authority = TryGetProperty(root, "fineMotionAuthority", out var authorityNode)
                ? authorityNode.ToString()
                : "未声明";
            if (!TryGetProperty(root, "phd2SlitPlacement", out var phd) || phd.ValueKind != JsonValueKind.Object)
                return ("PHD2 policy 未配置", $"路线：{authority} · 无 PHD2 slit-placement preset");
            var mode = TryGetProperty(phd, "guideMode", out var modeNode) ? modeNode.ToString() : "未声明";
            var offExposure = TryGetProperty(phd, "offSlitGuidingExposureMilliseconds", out var offNode) ? offNode.ToString() : "—";
            var directExposure = TryGetProperty(phd, "directTargetGuidingExposureMilliseconds", out var directNode) ? directNode.ToString() : "—";
            var policySha = TryGetProperty(phd, "calibrationQualityPolicySha256", out var shaNode) ? shaNode.GetString() ?? "缺失" : "缺失";
            var policyId = "缺失";
            if (TryGetProperty(phd, "calibrationQualityPolicy", out var policy) &&
                policy.ValueKind == JsonValueKind.Object &&
                TryGetProperty(policy, "policyId", out var idNode))
                policyId = idNode.GetString() ?? "缺失";
            return (
                $"{policyId} · SHA-256 {policySha} · 当前 active calibration 单候选评估",
                $"路线：{authority} · guide {mode} · 普通星 {offExposure} ms · 亮目标降级 {directExposure} ms · 当前没有历史候选择优 · Auto 时仅在独立 transform 也完成 commissioning 后回退");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return ("commissioning policy 读取失败", $"路线：无法解析 preset（{ex.Message}）");
        }
    }

    private void ApplyGhostAssistanceStatus(ObservationDashboardSnapshot dashboard)
    {
        RefreshGhostCommissioningSummary();
        var evidence = dashboard.Evidence.LastOrDefault(item =>
            string.Equals(item.Kind, "g3-ghost-assistance", StringComparison.Ordinal));
        if (evidence?.Metadata is not { } metadata) return;
        GhostApplicabilityText =
            $"适用性：{MetadataValue(metadata, "templateApplicability", "未知")} · " +
            $"外部身份：{MetadataValue(metadata, "externalIdentityAuthority", "none")} · " +
            $"新鲜同曝光 OFF 帧：{MetadataValue(metadata, "sourceFrameCount", "0")}。";
        GhostDecisionText =
            $"决定：{MetadataValue(metadata, "decision", "未知")} · " +
            $"门：{MetadataValue(metadata, "decisionGate", "未知")} · " +
            $"权限：{MetadataValue(metadata, "ghostAuthority", "None")}（不能建立身份或授权运动）。";
        GhostOverviewText = GhostDecisionOverview(MetadataValue(metadata, "decision", "未知"));
    }

    private void RefreshGhostCommissioningSummary()
    {
        var mode = settings.GhostAssistanceMode;
        GhostAssistanceModeText = mode switch
        {
            GhostAssistanceMode.Skip => "关闭",
            GhostAssistanceMode.AutoIfValidElseSkip => "自动（无效时跳过）",
            GhostAssistanceMode.RequireValid => "必须有效",
            _ => "未知模式",
        };
        GhostOverviewText = mode switch
        {
            GhostAssistanceMode.Skip => "已关闭；目标定位使用常规 WCS、居中与有界搜索。",
            GhostAssistanceMode.AutoIfValidElseSkip => "自动辅助；无效时继续常规 WCS、居中与有界搜索。",
            GhostAssistanceMode.RequireValid => "强制辅助；证据无效时暂停并等待处理。",
            _ => $"模式 {mode}；启动前需要复核。",
        };
        if (string.IsNullOrWhiteSpace(settings.CommissioningPresetPath) ||
            !File.Exists(settings.CommissioningPresetPath))
        {
            GhostCalibrationSummaryText = $"模式：{mode} · 标定：commissioning preset 不可读。";
            return;
        }
        try
        {
            var bytes = File.ReadAllBytes(settings.CommissioningPresetPath);
            var actualSha256 = Convert.ToHexString(SHA256.HashData(bytes));
            var expectedSha256 = NormalizeDisplayHash(settings.CommissioningPresetSha256);
            if (expectedSha256.Length != 64 ||
                !string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                GhostCalibrationSummaryText = $"模式：{mode} · 标定：preset SHA-256 未验证（actual {ShortHash(actualSha256)}）。";
                return;
            }
            using var document = JsonDocument.Parse(bytes);
            if (!TryGetProperty(document.RootElement, "ghostAssistance", out var ghost) ||
                ghost.ValueKind != JsonValueKind.Object ||
                !TryGetProperty(ghost, "calibration", out var calibration) ||
                calibration.ValueKind != JsonValueKind.Object)
            {
                GhostCalibrationSummaryText = $"模式：{mode} · 标定：已验证 preset {ShortHash(actualSha256)} 未包含 GhostAssistance。";
                return;
            }
            var calibrationId = JsonText(calibration, "calibrationId", "缺失");
            var calibrationSha256 = JsonText(calibration, "calibrationSha256", "缺失");
            var policyId = TryGetProperty(ghost, "matchPolicy", out var policy) && policy.ValueKind == JsonValueKind.Object
                ? JsonText(policy, "policyId", "缺失")
                : "缺失";
            var policySha256 = JsonText(ghost, "matchPolicySha256", "缺失");
            GhostCalibrationSummaryText =
                $"模式：{mode} · 标定：{calibrationId} / {ShortHash(calibrationSha256)} · " +
                $"策略：{policyId} / {ShortHash(policySha256)} · preset {ShortHash(actualSha256)}。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            GhostCalibrationSummaryText = $"模式：{mode} · 标定：读取失败（{ex.Message}）。";
        }
    }

    private void ApplySlitIdentityStatus(ObservationDashboardSnapshot dashboard)
    {
        RefreshSlitIdentitySummary();
        var evidence = dashboard.Evidence.LastOrDefault(item =>
            string.Equals(item.Kind, "slit-wheel-optical-identity", StringComparison.Ordinal));
        if (evidence?.Metadata is not { } metadata) return;
        SlitIdentityStatusText =
            $"本轮光学身份：{MetadataValue(metadata, "slitIdentityGate", "未知")} · " +
            $"匹配轮位 {MetadataValue(metadata, "slitIdentityMatchedPosition", "—")} · " +
            $"实测 {MetadataValue(metadata, "slitIdentityMeasuredWidthPixels", "—")} px。";
    }

    private void RefreshSlitIdentitySummary()
    {
        if (string.IsNullOrWhiteSpace(settings.CommissioningPresetPath) ||
            !File.Exists(settings.CommissioningPresetPath))
        {
            SlitIdentityStatusText = "狭缝光学身份：commissioning preset 不可读；机械轮位不能单独证明物理宽度。";
            return;
        }
        try
        {
            var bytes = File.ReadAllBytes(settings.CommissioningPresetPath);
            var actualSha256 = Convert.ToHexString(SHA256.HashData(bytes));
            var expectedSha256 = NormalizeDisplayHash(settings.CommissioningPresetSha256);
            if (expectedSha256.Length != 64 ||
                !string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                SlitIdentityStatusText = $"狭缝光学身份：preset SHA-256 未验证（actual {ShortHash(actualSha256)}）。";
                return;
            }
            using var document = JsonDocument.Parse(bytes);
            if (!TryGetProperty(document.RootElement, "slitWheelIdentity", out var identity) ||
                identity.ValueKind != JsonValueKind.Object)
            {
                SlitIdentityStatusText = $"狭缝光学身份：已验证 preset {ShortHash(actualSha256)}，但没有四槽位 HDR 暗孔径指纹；真实流程将阻断。";
                return;
            }
            var calibrationId = JsonText(identity, "calibrationId", "缺失");
            var calibrationSha = JsonText(identity, "calibrationSha256", "缺失");
            var count = TryGetProperty(identity, "fingerprints", out var fingerprints) && fingerprints.ValueKind == JsonValueKind.Array
                ? fingerprints.GetArrayLength()
                : 0;
            var calibration = JsonSerializer.Deserialize<SlitWheelIdentityCalibration>(identity.GetRawText(), CaseInsensitiveJson);
            var calibrationIssues = calibration?.Validate() ?? ["狭缝光学身份记录为空。"];
            if (calibrationIssues.Count > 0)
            {
                SlitIdentityStatusText =
                    $"狭缝光学身份：preset {ShortHash(actualSha256)} 内的四槽库无效；真实流程将阻断。" +
                    $" 首项：{calibrationIssues[0]}";
                return;
            }
            SlitIdentityStatusText =
                $"狭缝光学身份：{calibrationId} / {ShortHash(calibrationSha)} · {count}/4 个独立 HDR 暗孔径指纹 · " +
                "运行时仍会用 fresh 10/20 ms OFF/ON/OFF 双边实测复核，不会自动改写轮位映射。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            SlitIdentityStatusText = $"狭缝光学身份：读取失败（{ex.Message}）。";
        }
    }

    private static string MetadataValue(
        IReadOnlyDictionary<string, string> metadata,
        string key,
        string fallback) => metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : fallback;

    private static string GhostDecisionOverview(string decision) => decision switch
    {
        nameof(GhostAssistanceDecision.UseCalibratedAuxiliaryEstimate) =>
            "本轮使用已标定的辅助质心；目标身份和设备移动仍由常规证据授权。",
        nameof(GhostAssistanceDecision.ContinueLongExposureWcsFallback) =>
            "本轮辅助定位无效；已继续使用常规 WCS、居中与有界搜索。",
        nameof(GhostAssistanceDecision.PauseNeedsAttention) =>
            "辅助定位证据无效；已暂停并等待处理。",
        _ => "本轮辅助定位结果未知；请打开详细策略核对证据。",
    };

    private static string JsonText(JsonElement node, string name, string fallback) =>
        TryGetProperty(node, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static string NormalizeDisplayHash(string? value) =>
        (value ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).Trim();

    private static string ShortHash(string? value)
    {
        var normalized = NormalizeDisplayHash(value);
        return normalized.Length >= 12 ? normalized[..12] : normalized;
    }

    private static string Permission(IReadOnlyDictionary<string, double> metrics, string key) =>
        metrics.TryGetValue(key, out var value) && double.IsFinite(value)
            ? value >= 0.5 ? "是" : "否"
            : "未知";

    private static string Metric(IReadOnlyDictionary<string, double> metrics, string key) =>
        metrics.TryGetValue(key, out var value) && double.IsFinite(value)
            ? value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
            : "未知";

    private void ImportCommissioningBindings()
    {
        try
        {
            var defaultDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "UVEX-ADV",
                "commissioning");
            var dialog = new OpenFileDialog
            {
                Title = "导入 OpenAstroSpec Auto — UVEX4 commissioning bindings",
                Filter = "OpenAstroSpec commissioning bindings (*.bindings.json)|*.bindings.json|JSON (*.json)|*.json",
                CheckFileExists = true,
                Multiselect = false,
                InitialDirectory = Directory.Exists(defaultDirectory) ? defaultDirectory : null,
            };
            if (dialog.ShowDialog() != true) return;

            using var document = JsonDocument.Parse(File.ReadAllBytes(dialog.FileName));
            if (!TryGetProperty(document.RootElement, "ninaProfileValues", out var values) ||
                values.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("所选文件不含 NinaProfileValues 对象。");
            }

            var assignments = new List<(PropertyInfo Property, object Value)>();
            foreach (var item in values.EnumerateObject())
            {
                if (IsTargetPlanOrProvenanceSetting(item.Name))
                {
                    throw new InvalidDataException(
                        $"commissioning bindings 不得修改观测目标或目标导入来源（'{item.Name}'）。请在观测计划区手工编辑，或使用构图助手/第三方星图导入。");
                }
                var property = typeof(UvexPluginSettings).GetProperty(
                    item.Name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (property is null || !property.CanWrite)
                {
                    throw new InvalidDataException($"bindings 包含当前插件不认识的设置 '{item.Name}'。");
                }
                assignments.Add((property, ConvertBindingValue(item.Value, property.PropertyType, item.Name)));
            }

            var required = new[]
            {
                nameof(UvexPluginSettings.CommissioningPresetPath),
                nameof(UvexPluginSettings.CommissioningPresetId),
                nameof(UvexPluginSettings.CommissioningPresetSha256),
                nameof(UvexPluginSettings.CommissioningHardwareFingerprintSha256),
                nameof(UvexPluginSettings.NightSetupSnapshotPath),
                nameof(UvexPluginSettings.NightSetupSnapshotSha256),
                nameof(UvexPluginSettings.Phd2ProfileEvidenceSha256),
                nameof(UvexPluginSettings.Phd2RuntimeCameraName),
                nameof(UvexPluginSettings.Phd2RuntimeMountName),
                nameof(UvexPluginSettings.G3ExposureMilliseconds),
                nameof(UvexPluginSettings.G3GainPercent),
                nameof(UvexPluginSettings.ObservationExpectedAtrCameraId),
                nameof(UvexPluginSettings.ObservationExpectedQhyCameraId),
                nameof(UvexPluginSettings.ExpectedTelescopeId),
            };
            var supplied = assignments.Select(item => item.Property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = required.Where(name => !supplied.Contains(name)).ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidDataException($"bindings 缺少关键设置：{string.Join(", ", missing)}。");
            }

            foreach (var assignment in assignments) assignment.Property.SetValue(settings, assignment.Value);
            OperatorNotice = $"已导入并保存 {assignments.Count} 项锁定设置：{Path.GetFullPath(dialog.FileName)}";
            Error = string.Empty;
            RaisePropertyChanged(string.Empty);
            RaisePropertyChanged(nameof(ModeText));
            RaisePropertyChanged(nameof(ModeDescription));
            RaisePropertyChanged(nameof(StartButtonText));
            RaisePropertyChanged(nameof(RealModeStatus));
            RaiseCommandStates();
        }
        catch (Exception ex)
        {
            Error = $"导入 commissioning bindings 失败：{ex.Message}";
        }
    }

    private static bool IsTargetPlanOrProvenanceSetting(string name) => name switch
    {
        nameof(UvexPluginSettings.ObservationTargetName) or
        nameof(UvexPluginSettings.ObservationCatalogId) or
        nameof(UvexPluginSettings.ObservationRightAscensionDegrees) or
        nameof(UvexPluginSettings.ObservationDeclinationDegrees) or
        nameof(UvexPluginSettings.ObservationCoordinateEpoch) or
        nameof(UvexPluginSettings.ObservationTargetImportSource) or
        nameof(UvexPluginSettings.ObservationTargetImportedUtc) or
        nameof(UvexPluginSettings.ObservationTargetImportDetails) or
        nameof(UvexPluginSettings.ObservationTargetPositionAngleDegrees) => true,
        _ => false,
    };

    private static object ConvertBindingValue(JsonElement value, Type targetType, string name) =>
        CommissioningBindingValueConverter.Convert(value, targetType, name);

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static void OpenPreview(string title, ImageSource? image, string caption)
    {
        if (image is null) return;
        var window = new InteractivePreviewWindow(title, image, caption)
        {
            Owner = Application.Current?.MainWindow,
        };
        window.Show();
    }

    private bool FailurePreviewAvailable()
    {
        if (lastFailurePreviewChannel is not { } channel) return false;
        return channel switch
        {
            ObservationPreviewChannel.QhyWideField => QhyPreviewImage is not null,
            ObservationPreviewChannel.G3SlitField => G3PreviewImage is not null,
            ObservationPreviewChannel.AtrSpectrum => AtrPreviewImage is not null,
            _ => false,
        };
    }

    private void OpenFailurePreview()
    {
        switch (lastFailurePreviewChannel)
        {
            case ObservationPreviewChannel.QhyWideField:
                OpenPreview("失败诊断 · GS350 / QHY 广域解算", QhyPreviewImage, QhyPreviewCaption);
                break;
            case ObservationPreviewChannel.G3SlitField:
                OpenPreview("失败诊断 · PHD2 / G3 狭缝与导星", G3PreviewImage, G3PreviewCaption);
                break;
            case ObservationPreviewChannel.AtrSpectrum:
                OpenPreview("失败诊断 · N.I.N.A. / ATR585M 光谱", AtrPreviewImage, AtrPreviewCaption);
                break;
        }
    }

    private void OpenContainingDirectory(string path, string description)
    {
        try
        {
            if (!PathExists(path))
            {
                Error = $"{description}路径不存在或尚未生成：{path}";
                return;
            }
            var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                Error = $"找不到{description}所在目录：{path}";
                return;
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
            Error = string.Empty;
        }
        catch (Exception ex)
        {
            Error = $"打开{description}目录失败：{ex.Message}";
        }
    }

    private static bool PathExists(string path) =>
        !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path));

    private static string FormatMetrics(IReadOnlyDictionary<string, double>? metrics)
    {
        if (metrics is null || metrics.Count == 0) return "—";
        return string.Join(
            " · ",
            metrics.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value:0.####}"));
    }

    private static string GateDisplayName(GateDisposition disposition) => disposition switch
    {
        GateDisposition.Passed => "通过",
        GateDisposition.Failed => "失败/已暂停",
        GateDisposition.Indeterminate => "不确定/已暂停",
        _ => disposition.ToString(),
    };

    private static string RunStateDisplayName(ObservationRunState state) => state switch
    {
        ObservationRunState.Idle => "空闲",
        ObservationRunState.Validating => "正在验证",
        ObservationRunState.RunningAuto => "无人值守自动推进",
        ObservationRunState.PauseRequested => "请求暂停（等待有界动作结束）",
        ObservationRunState.Paused => "已暂停",
        ObservationRunState.PausedNeedsAttention => "质量门失败，等待人工处理",
        ObservationRunState.ManualTakeover => "人工接管",
        ObservationRunState.Cancelling => "正在取消",
        ObservationRunState.Finalizing => "正在持久化最终清单",
        ObservationRunState.Completed => "已完成",
        ObservationRunState.Cancelled => "已取消",
        ObservationRunState.Faulted => "故障",
        _ => state.ToString(),
    };
}

public sealed record ObservationGateRow(
    string Stage,
    string State,
    string Code,
    string Message,
    string Metrics,
    GateDisposition Disposition)
{
    public bool HasDetails =>
        !string.Equals(Code, "—", StringComparison.Ordinal) ||
        !string.Equals(Message, "尚未执行", StringComparison.Ordinal) ||
        !string.Equals(Metrics, "—", StringComparison.Ordinal);
}

public sealed record ObservationTimelineRow(
    string Time,
    string Stage,
    string Code,
    string Message,
    string EvidencePath)
{
    public bool HasEvidence => !string.IsNullOrWhiteSpace(EvidencePath);
}

public sealed record ObservationEvidenceRow(
    string Time,
    string Kind,
    string FileName,
    string AbsolutePath);
