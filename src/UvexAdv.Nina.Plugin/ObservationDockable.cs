using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.ViewModel;
using Microsoft.Win32;
using UvexAdv.Core;
using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

[Export(typeof(IDockableVM))]
[SupportedOSPlatform("windows")]
public sealed class ObservationDockable : DockableVM, IDisposable
{
    private static readonly IReadOnlyList<UvexSlitChoice> SlitChoices =
    [
        new(1, "槽位 1 · 标称 300 µm"),
        new(2, "槽位 2 · 标称 15 µm"),
        new(3, "槽位 3 · 标称 25 µm"),
        new(4, "槽位 4 · 标称 35 µm"),
    ];

    private static readonly IReadOnlyList<TargetObservabilityChoice> TargetObservabilityChoices =
    [
        new(TargetObservabilityClass.DirectStellar, "可直接识别的恒星", "用目标星质心复核 WCS 预测。"),
        new(TargetObservabilityClass.FaintPointSource, "暗点源 / 类星体", "以目录 WCS 几何为准，不要求 G3 中看见目标核。"),
        new(TargetObservabilityClass.CompactExtended, "紧致星云 / 行星状星云", "以目录中心入缝；发射线 SNR 优先于连续谱。"),
        new(TargetObservabilityClass.ExtendedNebula, "扩展星云", "以计划坐标作为取样位置，不把星云误当恒星。"),
        new(TargetObservabilityClass.InvisibleInG3, "G3 中不可见", "允许目标峰完全不可见；依赖目录 WCS、旁星与光谱信号。"),
    ];

    private static readonly IReadOnlyList<PreparationOptionChoice> SpectralRegionChoices =
    [
        new("VisibleWide", "UVEX 可见光宽谱（推荐）", "中心波长和上下限由实际波长标定回填，界面不猜测步数。"),
        new("HAlphaRed", "Hα 红区", "用于 Hα 附近观测；仍需由实际波长标定确认范围。"),
        new("ExistingLocked", "沿用已锁定配置", "从既有 schema-2 Night Setup 读取波段，不手工重录。"),
    ];

    private static readonly IReadOnlyList<PreparationOptionChoice> CalibrationReferenceChoices =
    [
        new("Vega", "亮标准星（Vega 等）", "使用可识别的亮参考星进行波长/响应复核。"),
        new("CompactEmission", "紧致发射线天体 / PN", "使用已知发射线的紧致目标。"),
        new("ExternalLamp", "外部标定灯", "使用独立标定灯采集参考谱。"),
        new("NightSky", "夜天光谱线", "用夜天光线作辅助标定。"),
    ];

    private static readonly IReadOnlyList<PreparationOptionChoice> SafetyCapabilityChoices =
    [
        new("NinaSafetyStack", "N.I.N.A. 安全链（无人值守）", "启动时必须回读安全监视器、天气、屋顶和光路盖状态。"),
        new("OperatorWeakSupervision", "有人弱监督（默认）", "四项适配器缺失或未连接时只警告并继续；已连接设备明确报告危险或关闭时仍阻断。绝不授予无人值守权限。"),
    ];

    private static readonly IReadOnlyList<string> ManualUvexDevices = ["UVEX4 / COM5"];

    private static readonly JsonSerializerOptions CaseInsensitiveJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IProfileService activeProfileService;
    private readonly UvexPluginSettings settings;
    private readonly InputTarget targetDraft;
    private readonly ObservationCoordinatorHost host;
    private readonly RealObservationStageRunnerFactory realRunnerFactory;
    private readonly ObservationTargetImportService targetImportService;
    private readonly ICameraMediator cameraMediator;
    private readonly IImagingMediator imagingMediator;
    private readonly ITelescopeMediator telescopeMediator;
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
    private readonly SimpleCommand showManualUvexControlCommand;
    private readonly SimpleCommand showAdvancedSettingsCommand;
    private readonly SimpleCommand autoFillConnectedNinaDevicesCommand;
    private readonly SimpleCommand selectNightSetupSnapshotCommand;
    private readonly SimpleCommand createNightSetupDraftCommand;
    private readonly SimpleCommand openPreparationDraftFolderCommand;
    private readonly SimpleCommand importCommissioningBindingsCommand;
    private readonly SimpleCommand refreshCommissioningProfilesCommand;
    private readonly SimpleCommand applySelectedCommissioningProfileCommand;
    private readonly SimpleCommand refreshProfileOwnershipCommand;
    private readonly SimpleCommand enableMountTrackingCommand;
    private readonly SimpleCommand applyRecommendedImageFilePatternCommand;
    private readonly SimpleCommand restorePreviousImageFilePatternCommand;
    private readonly SimpleAsyncCommand importFromFramingAssistantCommand;
    private readonly SimpleAsyncCommand importFromPlanetariumCommand;
    private readonly SimpleCommand bindCurrentAtrCameraCommand;
    private readonly SimpleCommand refreshAtrManualStatusCommand;
    private readonly SimpleAsyncCommand captureManualAtrSpectrumCommand;
    private readonly SimpleAsyncCommand refreshManualUvexStatusCommand;
    private readonly SimpleAsyncCommand connectManualUvexCommand;
    private readonly SimpleAsyncCommand disconnectManualUvexCommand;
    private readonly SimpleAsyncCommand releaseManualUvexComPortCommand;
    private readonly SimpleAsyncCommand selectManualSlit1Command;
    private readonly SimpleAsyncCommand selectManualSlit2Command;
    private readonly SimpleAsyncCommand selectManualSlit3Command;
    private readonly SimpleAsyncCommand selectManualSlit4Command;
    private readonly SimpleAsyncCommand moveManualM2NegativeCommand;
    private readonly SimpleAsyncCommand moveManualM2PositiveCommand;
    private readonly SimpleAsyncCommand manualSlitLightOnCommand;
    private readonly SimpleAsyncCommand manualSlitLightOffCommand;
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
    private string phd2CalibrationPolicyText = "等待读取实际标定策略";
    private string phd2CommissioningRouteText = "路线：等待读取设备标定方案";
    private string phd2CalibrationPermissionText = "验证导星：等待 · exact-lock：等待 · 无人值守科学：否";
    private string phd2CalibrationScaleText = "步长/残差缩放：等待 PostSettle 证据";
    private string phd2CalibrationReasonText = "当前生产路径只评估 PHD2 当前 active calibration（单候选）；本轮真实 settle 和 fresh residual 到齐后才授予 exact-lock 或科学权限。";
    private string phd2CalibrationOverviewGradeText = "尚未评估";
    private string phd2CalibrationOverviewText = "启动导星后自动评估；评估前不执行精调或科学曝光。";
    private string ghostCalibrationSummaryText = "标定：尚未读取经过 SHA-256 验证的设备标定方案。";
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
    private string mountTrackingManualStatus = "尚未请求；正式自动流程会在目录转向前自行启用并核验。";
    private string manualUvexConnectionStatus = "尚未读取 UVEX 服务状态。";
    private string manualUvexPositionStatus = "狭缝、M2 与光栅位置尚未读取。";
    private string manualUvexLastAction = "设备选择已保存；打开本页不会连接 COM5。请先点击“连接”。";
    private string manualUvexError = string.Empty;
    private bool isManualUvexBusy;
    private bool hasManualUvexStatus;
    private bool manualUvexPositionKnown;
    private DeviceConnectionState manualUvexConnectionState = DeviceConnectionState.Disconnected;
    private int selectedWorkspaceTabIndex;
    private string? previousNinaImageFilePattern;
    private Guid? previousNinaImageFilePatternProfileId;
    private IReadOnlyList<CommissioningProfileChoice> commissioningProfiles = [];
    private IReadOnlyList<DeviceIdentityChoice> telescopeCandidates = [];
    private IReadOnlyList<DeviceIdentityChoice> atrCameraCandidates = [];
    private IReadOnlyList<DeviceIdentityChoice> g3CameraCandidates = [];
    private IReadOnlyList<DeviceIdentityChoice> qhyCameraCandidates = [];
    private CommissioningProfileChoice? selectedCommissioningProfile;
    private DeviceIdentityChoice? selectedTelescopeCandidate;
    private DeviceIdentityChoice? selectedAtrCameraCandidate;
    private DeviceIdentityChoice? selectedG3CameraCandidate;
    private DeviceIdentityChoice? selectedQhyCameraCandidate;
    private string commissioningProfileLoadStatus = "尚未读取台站配置方案。";
    private string preparationDraftPath = string.Empty;
    private string preparationDraftStatus = "尚未生成本次观测配置草稿。";
    private AutomaticPreparationEvidenceReport preparationEvidence = new(false, 0, 0, 0, 0, [], []);

    [ImportingConstructor]
    public ObservationDockable(
        IProfileService profileService,
        ObservationCoordinatorHost host,
        RealObservationStageRunnerFactory realRunnerFactory,
        IFramingAssistantVM framingAssistant,
        IPlanetariumFactory planetariumFactory,
        ICameraMediator cameraMediator,
        IImagingMediator imagingMediator,
        ITelescopeMediator telescopeMediator)
        : base(profileService)
    {
        activeProfileService = profileService;
        settings = new UvexPluginSettings(profileService);
        targetDraft = CreateNativeTargetDraft(profileService, settings);
        this.host = host;
        this.realRunnerFactory = realRunnerFactory;
        this.cameraMediator = cameraMediator;
        this.imagingMediator = imagingMediator;
        this.telescopeMediator = telescopeMediator;
        targetImportService = ObservationTargetImportNinaSources.CreateService(
            framingAssistant,
            planetariumFactory,
            () =>
            {
                var planetarium = profileService.ActiveProfile.PlanetariumSettings;
                if (!string.Equals(planetarium.PreferredPlanetarium.ToString(), "STELLARIUM", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                var hostName = string.IsNullOrWhiteSpace(planetarium.StellariumHost)
                    ? "localhost"
                    : planetarium.StellariumHost.Trim();
                return new UriBuilder(Uri.UriSchemeHttp, hostName, planetarium.StellariumPort).Uri;
            });
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
        showManualUvexControlCommand = new SimpleCommand(() => SelectedWorkspaceTabIndex = 1);
        showObservationPlanCommand = new SimpleCommand(() => SelectedWorkspaceTabIndex = 2);
        showStartupRequirementsCommand = new SimpleCommand(() => SelectedWorkspaceTabIndex = 3);
        showAdvancedSettingsCommand = new SimpleCommand(() => SelectedWorkspaceTabIndex = 7);
        autoFillConnectedNinaDevicesCommand = new SimpleCommand(
            AutoFillConnectedNinaDevices,
            CanEditTargetPlan);
        selectNightSetupSnapshotCommand = new SimpleCommand(
            SelectNightSetupSnapshot,
            CanEditTargetPlan);
        createNightSetupDraftCommand = new SimpleCommand(
            CreateNightSetupPreparationDraft,
            CanEditTargetPlan);
        openPreparationDraftFolderCommand = new SimpleCommand(
            () => OpenContainingDirectory(PreparationDraftPath, "本次观测配置草稿"),
            () => PathExists(PreparationDraftPath));
        importCommissioningBindingsCommand = new SimpleCommand(ImportCommissioningBindings);
        refreshCommissioningProfilesCommand = new SimpleCommand(
            () => RefreshCommissioningProfileCatalog(applySelected: false));
        applySelectedCommissioningProfileCommand = new SimpleCommand(
            ApplySelectedCommissioningProfile,
            () => CanEditTargetPlan() && SelectedCommissioningProfile is not null);
        refreshProfileOwnershipCommand = new SimpleCommand(RefreshProfileOwnership);
        enableMountTrackingCommand = new SimpleCommand(EnableMountTrackingForCommissioning);
        applyRecommendedImageFilePatternCommand = new SimpleCommand(
            ApplyRecommendedImageFilePattern,
            CanApplyRecommendedImageFilePattern);
        restorePreviousImageFilePatternCommand = new SimpleCommand(
            RestorePreviousImageFilePattern,
            CanRestorePreviousImageFilePattern);
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
        refreshManualUvexStatusCommand = new SimpleAsyncCommand(
            RefreshManualUvexStatusAsync,
            CanManageManualUvexConnection);
        connectManualUvexCommand = new SimpleAsyncCommand(
            ConnectManualUvexAsync,
            CanConnectManualUvex);
        disconnectManualUvexCommand = new SimpleAsyncCommand(
            DisconnectManualUvexAsync,
            CanDisconnectManualUvex);
        releaseManualUvexComPortCommand = new SimpleAsyncCommand(
            ReleaseManualUvexComPortAsync,
            CanManageManualUvexConnection);
        selectManualSlit1Command = CreateManualSlitCommand(1);
        selectManualSlit2Command = CreateManualSlitCommand(2);
        selectManualSlit3Command = CreateManualSlitCommand(3);
        selectManualSlit4Command = CreateManualSlitCommand(4);
        moveManualM2NegativeCommand = new SimpleAsyncCommand(
            () => MoveManualM2Async(-ManualM2StepSize),
            CanOperateManualUvex);
        moveManualM2PositiveCommand = new SimpleAsyncCommand(
            () => MoveManualM2Async(ManualM2StepSize),
            CanOperateManualUvex);
        manualSlitLightOnCommand = new SimpleAsyncCommand(
            () => SetManualSlitLightAsync(enabled: true),
            CanOperateManualUvex);
        manualSlitLightOffCommand = new SimpleAsyncCommand(
            () => SetManualSlitLightAsync(enabled: false),
            CanOperateManualUvex);

        LoadTargetImportDisplay();
        RefreshCommissioningProfileCatalog(applySelected: true);
        RefreshGhostCommissioningSummary();
        RefreshSlitIdentitySummary();
        RefreshAtrManualStatus();

        host.DashboardChanged += OnDashboardChanged;
        UvexRuntimeState.Changed += OnManualSpectrumChanged;
        activeProfileService.ProfileChanged += OnProfileChanged;
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
    public ICommand ShowManualUvexControlCommand => showManualUvexControlCommand;
    public ICommand ShowAdvancedSettingsCommand => showAdvancedSettingsCommand;
    public ICommand AutoFillConnectedNinaDevicesCommand => autoFillConnectedNinaDevicesCommand;
    public ICommand SelectNightSetupSnapshotCommand => selectNightSetupSnapshotCommand;
    public ICommand CreateNightSetupDraftCommand => createNightSetupDraftCommand;
    public ICommand OpenPreparationDraftFolderCommand => openPreparationDraftFolderCommand;
    public ICommand ImportCommissioningBindingsCommand => importCommissioningBindingsCommand;
    public ICommand RefreshCommissioningProfilesCommand => refreshCommissioningProfilesCommand;
    public ICommand ApplySelectedCommissioningProfileCommand => applySelectedCommissioningProfileCommand;
    public ICommand RefreshProfileOwnershipCommand => refreshProfileOwnershipCommand;
    public ICommand EnableMountTrackingCommand => enableMountTrackingCommand;
    public ICommand ApplyRecommendedImageFilePatternCommand => applyRecommendedImageFilePatternCommand;
    public ICommand RestorePreviousImageFilePatternCommand => restorePreviousImageFilePatternCommand;
    public ICommand ImportFromFramingAssistantCommand => importFromFramingAssistantCommand;
    public ICommand ImportFromPlanetariumCommand => importFromPlanetariumCommand;
    public ICommand BindCurrentAtrCameraCommand => bindCurrentAtrCameraCommand;
    public ICommand RefreshAtrManualStatusCommand => refreshAtrManualStatusCommand;
    public ICommand CaptureManualAtrSpectrumCommand => captureManualAtrSpectrumCommand;
    public ICommand RefreshManualUvexStatusCommand => refreshManualUvexStatusCommand;
    public ICommand ConnectManualUvexCommand => connectManualUvexCommand;
    public ICommand DisconnectManualUvexCommand => disconnectManualUvexCommand;
    public ICommand ReleaseManualUvexComPortCommand => releaseManualUvexComPortCommand;
    public ICommand SelectManualSlit1Command => selectManualSlit1Command;
    public ICommand SelectManualSlit2Command => selectManualSlit2Command;
    public ICommand SelectManualSlit3Command => selectManualSlit3Command;
    public ICommand SelectManualSlit4Command => selectManualSlit4Command;
    public ICommand MoveManualM2NegativeCommand => moveManualM2NegativeCommand;
    public ICommand MoveManualM2PositiveCommand => moveManualM2PositiveCommand;
    public ICommand ManualSlitLightOnCommand => manualSlitLightOnCommand;
    public ICommand ManualSlitLightOffCommand => manualSlitLightOffCommand;

    public ObservableCollection<ObservationGateRow> GateRows { get; } = new();
    public ObservableCollection<ObservationTimelineRow> TimelineRows { get; } = new();
    public ObservableCollection<ObservationEvidenceRow> EvidenceRows { get; } = new();

    public IReadOnlyList<CommissioningProfileChoice> CommissioningProfiles => commissioningProfiles;
    public IReadOnlyList<DeviceIdentityChoice> TelescopeCandidates => telescopeCandidates;
    public IReadOnlyList<DeviceIdentityChoice> AtrCameraCandidates => atrCameraCandidates;
    public IReadOnlyList<DeviceIdentityChoice> G3CameraCandidates => g3CameraCandidates;
    public IReadOnlyList<DeviceIdentityChoice> QhyCameraCandidates => qhyCameraCandidates;

    public CommissioningProfileChoice? SelectedCommissioningProfile
    {
        get => selectedCommissioningProfile;
        set
        {
            if (Equals(selectedCommissioningProfile, value)) return;
            selectedCommissioningProfile = value;
            if (value is not null)
            {
                settings.SelectedCommissioningProfileId = value.Id;
                settings.SelectedCommissioningProfilePath = value.BindingsPath ?? string.Empty;
            }
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(SelectedCommissioningProfileDescription));
            applySelectedCommissioningProfileCommand?.RaiseCanExecuteChanged();
        }
    }

    public string SelectedCommissioningProfileDescription =>
        SelectedCommissioningProfile?.Description ?? "未选择台站配置方案。";

    public string CommissioningProfileLoadStatus
    {
        get => commissioningProfileLoadStatus;
        private set { commissioningProfileLoadStatus = value; RaisePropertyChanged(); }
    }

    public IReadOnlyList<PreparationOptionChoice> PreparationSpectralRegionChoices => SpectralRegionChoices;
    public IReadOnlyList<PreparationOptionChoice> PreparationCalibrationReferenceChoices => CalibrationReferenceChoices;
    public IReadOnlyList<PreparationOptionChoice> PreparationSafetyCapabilityChoices => SafetyCapabilityChoices;

    public string SelectedPreparationSpectralRegion
    {
        get => settings.PreparationSpectralRegionPreset;
        set
        {
            if (string.Equals(settings.PreparationSpectralRegionPreset, value, StringComparison.Ordinal)) return;
            settings.PreparationSpectralRegionPreset = value;
            RaisePropertyChanged();
        }
    }

    public string SelectedPreparationCalibrationReference
    {
        get => settings.PreparationCalibrationReferencePreset;
        set
        {
            if (string.Equals(settings.PreparationCalibrationReferencePreset, value, StringComparison.Ordinal)) return;
            settings.PreparationCalibrationReferencePreset = value;
            RaisePropertyChanged();
        }
    }

    public string SelectedPreparationSafetyCapability
    {
        get => settings.PreparationSafetyCapabilityPreset;
        set
        {
            if (string.Equals(settings.PreparationSafetyCapabilityPreset, value, StringComparison.Ordinal)) return;
            settings.PreparationSafetyCapabilityPreset = value;
            ApplyPreparationSafetyCapability();
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(ModeText));
            RaisePropertyChanged(nameof(ModeDescription));
            RaisePropertyChanged(nameof(RealModeStatus));
            RaisePropertyChanged(nameof(RealModeStatusSummary));
            RaiseCommandStates();
        }
    }

    public bool PreparationOrderSortingFilterInstalled
    {
        get => settings.PreparationOrderSortingFilterInstalled;
        set
        {
            if (settings.PreparationOrderSortingFilterInstalled == value) return;
            settings.PreparationOrderSortingFilterInstalled = value;
            RaisePropertyChanged();
        }
    }

    public string PreparationEvidenceInventorySummary => preparationEvidence.InventorySummary;
    public string PreparationDraftPath => preparationDraftPath;
    public string PreparationDraftStatus
    {
        get => preparationDraftStatus;
        private set
        {
            preparationDraftStatus = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(PreparationDraftPath));
            openPreparationDraftFolderCommand?.RaiseCanExecuteChanged();
        }
    }

    public DeviceIdentityChoice? SelectedTelescopeCandidate
    {
        get => selectedTelescopeCandidate;
        set
        {
            if (Equals(selectedTelescopeCandidate, value)) return;
            selectedTelescopeCandidate = value;
            if (value is not null) settings.ExpectedTelescopeId = value.Id;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(ExpectedTelescopeId));
            RaiseCommandStates();
        }
    }

    public DeviceIdentityChoice? SelectedAtrCameraCandidate
    {
        get => selectedAtrCameraCandidate;
        set
        {
            if (Equals(selectedAtrCameraCandidate, value)) return;
            selectedAtrCameraCandidate = value;
            if (value is not null)
            {
                settings.BoundCameraId = value.Id;
                settings.ObservationExpectedAtrCameraId = value.Id;
            }
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(ExpectedAtrCameraId));
            RaisePropertyChanged(nameof(BoundAtrCameraId));
            RaiseCommandStates();
        }
    }

    public DeviceIdentityChoice? SelectedG3CameraCandidate
    {
        get => selectedG3CameraCandidate;
        set
        {
            if (Equals(selectedG3CameraCandidate, value)) return;
            selectedG3CameraCandidate = value;
            if (value is not null)
            {
                settings.Phd2ProfileId = value.Phd2ProfileId ?? settings.Phd2ProfileId;
                settings.Phd2ProfileName = value.Phd2ProfileName ?? settings.Phd2ProfileName;
                settings.ObservationExpectedG3ProfileName = value.Phd2ProfileName ?? settings.ObservationExpectedG3ProfileName;
                settings.Phd2CameraName = value.CameraName ?? settings.Phd2CameraName;
                settings.Phd2CameraStableId = value.Id;
                settings.Phd2MountName = value.MountName ?? settings.Phd2MountName;
                settings.Phd2RuntimeCameraName = "G3M2210M";
                settings.Phd2RuntimeMountName = "On-Step (ASCOM)";
                settings.Phd2ProfileEvidenceSha256 = value.ProfileEvidenceSha256 ?? settings.Phd2ProfileEvidenceSha256;
            }
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(ExpectedG3ProfileName));
            RaisePropertyChanged(nameof(Phd2ProfileEvidenceSha256));
            RaisePropertyChanged(nameof(Phd2RegistryCameraName));
            RaisePropertyChanged(nameof(Phd2RegistryMountName));
            RaisePropertyChanged(nameof(Phd2RuntimeCameraName));
            RaisePropertyChanged(nameof(Phd2RuntimeMountName));
            RaiseCommandStates();
        }
    }

    public DeviceIdentityChoice? SelectedQhyCameraCandidate
    {
        get => selectedQhyCameraCandidate;
        set
        {
            if (Equals(selectedQhyCameraCandidate, value)) return;
            selectedQhyCameraCandidate = value;
            if (value is not null) settings.ObservationExpectedQhyCameraId = value.Id;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(ExpectedQhyCameraId));
            RaiseCommandStates();
        }
    }

    public string TargetName
    {
        get => targetDraft.TargetName ?? string.Empty;
        set
        {
            targetDraft.TargetName = value ?? string.Empty;
            settings.ObservationTargetName = targetDraft.TargetName;
            MarkTargetAsManuallyEdited();
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(Target));
        }
    }

    public InputTarget Target => targetDraft;

    public string CatalogId
    {
        get => settings.ObservationCatalogId;
        set { settings.ObservationCatalogId = value; MarkTargetAsManuallyEdited(); RaisePropertyChanged(); }
    }

    public double RightAscensionDegrees
    {
        get => NativeTargetJ2000().RADegrees;
        set
        {
            SetNativeTargetCoordinates(value, DeclinationDegrees);
            settings.ObservationRightAscensionDegrees = value;
            MarkTargetAsManuallyEdited();
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(Target));
        }
    }

    public IReadOnlyList<TargetObservabilityChoice> AvailableTargetObservabilityClasses => TargetObservabilityChoices;

    public TargetObservabilityClass TargetObservability
    {
        get => settings.ObservationTargetObservability;
        set
        {
            settings.ObservationTargetObservability = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(TargetObservabilitySummary));
        }
    }

    public string TargetObservabilitySummary =>
        TargetObservabilityChoices.First(choice => choice.Value == TargetObservability).Description;

    public double DeclinationDegrees
    {
        get => NativeTargetJ2000().Dec;
        set
        {
            SetNativeTargetCoordinates(RightAscensionDegrees, value);
            settings.ObservationDeclinationDegrees = value;
            MarkTargetAsManuallyEdited();
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(Target));
        }
    }

    public double DurationMinutes
    {
        get => settings.ObservationDurationMinutes;
        set { settings.ObservationDurationMinutes = value; RaisePropertyChanged(); }
    }

    public string NightSetupId
    {
        get => settings.ObservationNightSetupId;
        set { settings.ObservationNightSetupId = value; RaisePropertyChanged(); RaisePreparationProperties(); }
    }

    public IReadOnlyList<UvexSlitChoice> UvexSlitChoices => SlitChoices;

    public int ExpectedUvexSlitPosition
    {
        get => settings.ExpectedUvexSlitPosition;
        set
        {
            settings.ExpectedUvexSlitPosition = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(RealModeStatus));
            RaiseCommandStates();
        }
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

    public double G3MaximumPlateSolveHintOffsetDegrees
    {
        get => settings.G3MaximumPlateSolveHintOffsetDegrees;
        set { settings.G3MaximumPlateSolveHintOffsetDegrees = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public string QhyParallelFilterSequenceCsv
    {
        get => settings.QhyParallelFilterSequenceCsv;
        set
        {
            settings.QhyParallelFilterSequenceCsv = value ?? string.Empty;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(QhyParallelFilterSequenceStatus));
            RaisePropertyChanged(nameof(RealModeStatus));
            RaiseCommandStates();
        }
    }

    public string QhyParallelFilterSequenceStatus => string.IsNullOrWhiteSpace(settings.QhyParallelFilterSequenceCsv)
        ? $"单滤镜同步测光：{settings.QhyFilterName}，{settings.QhyPhotometryExposureSeconds:G4}s。"
        : $"并行循环：{settings.QhyParallelFilterSequenceCsv}。滤镜轮与 QHYminiCam8M 始终由 QHY 服务单一持有；每个滤镜分别建立质量基线。";

    public int QhyMinimumDetectedStars
    {
        get => settings.QhyMinimumDetectedStars;
        set { settings.QhyMinimumDetectedStars = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double QhyMinimumTransparency
    {
        get => settings.QhyMinimumTransparency;
        set { settings.QhyMinimumTransparency = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
    }

    public double QhyMaximumSaturatedFraction
    {
        get => settings.QhyMaximumSaturatedFraction;
        set { settings.QhyMaximumSaturatedFraction = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
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

    public double G3WcsFreshSolveAuthorizationResidualArcseconds
    {
        get => settings.G3WcsFreshSolveAuthorizationResidualArcseconds;
        set { settings.G3WcsFreshSolveAuthorizationResidualArcseconds = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(RealModeStatus)); RaiseCommandStates(); }
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

    public string ModeText => UseRealMode
        ? settings.WeakSupervisionEnabled ? "自动观测：真实设备 · 有人弱监督" : "自动观测：真实设备 · 完整安全链"
        : "自动观测：模拟演练";
    public bool IsSimulationMode => !UseRealMode;
    public bool IsRealMode => UseRealMode;
    public string ModeDescription => UseRealMode
        ? settings.WeakSupervisionEnabled
            ? "有人弱监督：Safety Monitor、屋顶、天气和镜盖适配器缺失时记录警告并继续；已连接适配器明确报告危险或关闭时仍阻断。本模式不是无人值守。"
            : "完整安全链：Safety Monitor、屋顶、天气和镜盖必须实时通过。UVEX 切缝、狭缝灯和 M2 小步进仍可使用“设备手控”。"
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
                ? settings.WeakSupervisionEnabled
                    ? "⚠ 真实模式静态条件完整；当前为有人弱监督，缺失的四类环境适配器只警告，不能称为无人值守。"
                    : "✓ 真实模式启动条件已填写完整。点击启动后仍会重新读取并核验全部实时状态。"
                : $"真实模式当前有 {issues.Count} 个启动阻断项：{Environment.NewLine}• {string.Join($"{Environment.NewLine}• ", issues)}";
        }
    }
    public string RealModeStatusSummary
    {
        get
        {
            var issueCount = RealModeEligibilityIssues().Count;
            return issueCount == 0
                ? settings.WeakSupervisionEnabled
                    ? "⚠ 已就绪：有人弱监督；环境适配器缺失只警告，明确危险仍阻断。"
                    : "✓ 真实模式启动资料已填写；启动时仍会重新核验实时状态。"
                : "自动观测准备尚未完成；不影响“设备手控”。请在“自动准备”按红色分组处理。";
        }
    }

    public int AutomaticPreparationIssueCount => RealModeEligibilityIssues().Count;
    public bool IsTargetPreparationMissing =>
        string.IsNullOrWhiteSpace(TargetName) ||
        !double.IsFinite(RightAscensionDegrees) ||
        !double.IsFinite(DeclinationDegrees) ||
        RightAscensionDegrees is < 0 or >= 360 ||
        DeclinationDegrees is < -90 or > 90;
    public string TargetPreparationStatus => IsTargetPreparationMissing
        ? "未完成：请选择或导入目标，并确认 J2000 坐标。"
        : $"已选择：{TargetName} · J2000 RA {RightAscensionDegrees:F5}° / Dec {DeclinationDegrees:+0.00000;-0.00000;0.00000}°";
    public bool IsDevicePreparationMissing =>
        string.IsNullOrWhiteSpace(settings.ExpectedTelescopeId) ||
        string.IsNullOrWhiteSpace(settings.ObservationExpectedAtrCameraId) ||
        settings.ObservationExpectedAtrCameraId.StartsWith("SIM-", StringComparison.OrdinalIgnoreCase) ||
        string.IsNullOrWhiteSpace(settings.ObservationExpectedQhyCameraId) ||
        settings.ObservationExpectedQhyCameraId.StartsWith("SIM-", StringComparison.OrdinalIgnoreCase) ||
        settings.Phd2ProfileId < 0 ||
        string.IsNullOrWhiteSpace(settings.Phd2CameraStableId);
    public string DevicePreparationStatus => IsDevicePreparationMissing
        ? "未完成：请从下拉候选选择设备；候选来自 N.I.N.A.、PHD2 与 QHY 服务保存的配置，不需要先连接。"
        : $"已绑定：赤道仪 {settings.ExpectedTelescopeId} · ATR {settings.ObservationExpectedAtrCameraId} · QHY {settings.ObservationExpectedQhyCameraId} · PHD2 {settings.Phd2ProfileName}";
    public bool IsCommissioningPreparationMissing =>
        !settings.RealModeCommissioned ||
        string.IsNullOrWhiteSpace(settings.CommissioningPresetPath) ||
        !File.Exists(settings.CommissioningPresetPath) ||
        string.IsNullOrWhiteSpace(settings.CommissioningPresetId) ||
        string.IsNullOrWhiteSpace(settings.CommissioningPresetSha256) ||
        string.IsNullOrWhiteSpace(settings.CommissioningHardwareFingerprintSha256);
    public string CommissioningPreparationStatus => IsCommissioningPreparationMissing
        ? $"未完成：{preparationEvidence.InstallationStatus}"
        : $"已导入：{settings.CommissioningPresetId}";
    public bool IsNightSetupPreparationMissing =>
        string.IsNullOrWhiteSpace(settings.NightSetupSnapshotPath) ||
        !File.Exists(settings.NightSetupSnapshotPath) ||
        string.IsNullOrWhiteSpace(settings.NightSetupSnapshotSha256);
    public string NightSetupPreparationStatus => IsNightSetupPreparationMissing
        ? $"未完成：{preparationEvidence.NightSetupStatus}"
        : $"已选择：{settings.ObservationNightSetupId} · {Path.GetFileName(settings.NightSetupSnapshotPath)}";
    public bool IsSlitChoiceMissing => ExpectedUvexSlitPosition is < 1 or > 4;
    public bool IsAutomationPolicyPreparationMissing => AutomaticPreparationIssueCount > 0;
    public string AutomationPolicyPreparationStatus => AutomaticPreparationIssueCount == 0
        ? "已通过：后台配置结构、设备所有权和运动限额完整。"
        : $"尚未通过：当前还有 {AutomaticPreparationIssueCount} 项一致性检查未满足。这里仅汇总结果；请在上方导入锁定包或生成准备草稿，不需要逐项填写哈希和工程限额。";
    public string AutomaticPreparationSummary => AutomaticPreparationIssueCount == 0
        ? "✓ 表单已完成；启动时会自动读取实时状态并做最后复核。"
        : "准备尚未完成。先处理红色分组；内部校验不会再作为大段错误显示在主界面。";

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
    public string MountTrackingManualStatus
    {
        get => mountTrackingManualStatus;
        private set
        {
            if (string.Equals(mountTrackingManualStatus, value, StringComparison.Ordinal)) return;
            mountTrackingManualStatus = value;
            RaisePropertyChanged();
        }
    }
    public string BoundAtrCameraId => string.IsNullOrWhiteSpace(settings.BoundCameraId) ? "未绑定" : settings.BoundCameraId;
    public string AtrManualCapturePresetText =>
        $"{settings.ExposureSeconds:G6} s · Gain {settings.Gain} · Offset {settings.Offset} · {settings.Binning}×{settings.Binning}";
    public string ManualSpectrumSummary => UvexRuntimeState.MetricSummary;
    public PointCollection ManualSpectrumPoints => UvexRuntimeState.SpectrumPoints;
    public string ManualUvexServiceUrl => settings.ServiceUrl;
    public IReadOnlyList<string> ManualUvexDeviceChoices => ManualUvexDevices;
    public string SelectedManualUvexDevice
    {
        get
        {
            var selected = settings.ManualUvexSelectedDevice;
            return ManualUvexDevices.Contains(selected, StringComparer.Ordinal)
                ? selected
                : ManualUvexDevices[0];
        }
        set
        {
            var selected = ManualUvexDevices.Contains(value, StringComparer.Ordinal)
                ? value
                : ManualUvexDevices[0];
            if (string.Equals(settings.ManualUvexSelectedDevice, selected, StringComparison.Ordinal)) return;
            settings.ManualUvexSelectedDevice = selected;
            RaisePropertyChanged();
        }
    }
    public string ManualUvexConnectionStatus
    {
        get => manualUvexConnectionStatus;
        private set { manualUvexConnectionStatus = value; RaisePropertyChanged(); }
    }
    public string ManualUvexPositionStatus
    {
        get => manualUvexPositionStatus;
        private set { manualUvexPositionStatus = value; RaisePropertyChanged(); }
    }
    public string ManualUvexLastAction
    {
        get => manualUvexLastAction;
        private set { manualUvexLastAction = value; RaisePropertyChanged(); }
    }
    public string ManualUvexError
    {
        get => manualUvexError;
        private set
        {
            manualUvexError = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(HasManualUvexError));
        }
    }
    public bool HasManualUvexError => !string.IsNullOrWhiteSpace(ManualUvexError);
    public bool IsManualUvexBusy
    {
        get => isManualUvexBusy;
        private set
        {
            if (isManualUvexBusy == value) return;
            isManualUvexBusy = value;
            RaisePropertyChanged();
            RaiseCommandStates();
        }
    }
    public int ManualM2StepSize
    {
        get => settings.ManualM2StepSize;
        set
        {
            settings.ManualM2StepSize = Math.Clamp(value, 1, 2_000);
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(ManualM2NegativeButtonText));
            RaisePropertyChanged(nameof(ManualM2PositiveButtonText));
        }
    }
    public string ManualM2NegativeButtonText => $"M2 −{ManualM2StepSize} 步";
    public string ManualM2PositiveButtonText => $"M2 +{ManualM2StepSize} 步";
    public string NinaImageFilePatternCurrent =>
        activeProfileService.ActiveProfile.ImageFileSettings.FilePattern ?? string.Empty;
    public string NinaImageFilePatternRecommended => NinaImageFilePatternPolicy.RecommendedPattern;
    public string NinaImageFilePatternStatus =>
        NinaImageFilePatternPolicy.Assess(NinaImageFilePatternCurrent).Status;

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
        activeProfileService.ProfileChanged -= OnProfileChanged;
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
            ? "已选择真实设备自动观测。此按钮不是设备连接入口；观测前切缝和 M2 对焦请直接进入“设备手控”。"
            : "已选择模拟演练。模拟运行不会连接或移动任何真实设备。";
        Error = string.Empty;
    }

    private SimpleAsyncCommand CreateManualSlitCommand(int position) => new(
        () => SelectManualSlitAsync(position),
        CanOperateManualUvex);

    private async Task RefreshManualUvexStatusAsync()
    {
        if (!CanManageManualUvexConnection()) return;
        IsManualUvexBusy = true;
        ManualUvexError = string.Empty;
        ManualUvexLastAction = "正在读取 UVEX 服务与 COM5 状态…";
        try
        {
            using var client = new UvexServiceClient(settings.ServiceUrl);
            var status = await client.GetStatusAsync(lifetime.Token).ConfigureAwait(true)
                ?? throw new InvalidOperationException("UVEX 服务没有返回设备状态。");
            ApplyManualUvexStatus(status);
            ManualUvexLastAction = "状态已刷新；没有执行任何机械运动。";
        }
        catch (Exception ex)
        {
            InvalidateManualUvexStatus();
            ManualUvexError = $"无法读取 UVEX 服务：{ex.Message}";
            ManualUvexLastAction = "状态刷新失败；没有执行机械运动。";
        }
        finally
        {
            IsManualUvexBusy = false;
        }
    }

    private Task ConnectManualUvexAsync() => RunManualUvexActionAsync(
        "连接 UVEX4 / COM5",
        (lease, token) => lease.ConnectAndVerifyAsync(token));

    private Task DisconnectManualUvexAsync() => RunManualUvexActionAsync(
        "断开 UVEX4 / COM5",
        (lease, token) => lease.DisconnectAndVerifyAsync(token));

    private Task ReleaseManualUvexComPortAsync() => RunManualUvexActionAsync(
        "释放 COM5 给原厂软件",
        (lease, token) => lease.ReleaseComPortAndVerifyAsync(token));

    private Task SelectManualSlitAsync(int position) => RunManualUvexActionAsync(
        $"切换到狭缝槽位 {position}",
        (lease, token) => lease.SelectSlitAndVerifyAsync(position, token));

    private Task MoveManualM2Async(int deltaSteps) => RunManualUvexActionAsync(
        $"M2 {deltaSteps:+#;-#;0} 步",
        (lease, token) => lease.MoveFocusAndVerifyAsync(deltaSteps, token));

    private Task SetManualSlitLightAsync(bool enabled) => RunManualUvexActionAsync(
        enabled ? "打开狭缝照明灯" : "关闭狭缝照明灯",
        (lease, token) => lease.SetSlitIlluminationAsync(enabled, token));

    private async Task RunManualUvexActionAsync(
        string actionName,
        Func<UvexServiceClient.UvexLeaseSession, CancellationToken, Task<UvexDeviceStatus>> action)
    {
        if (!CanManageManualUvexConnection()) return;
        IsManualUvexBusy = true;
        ManualUvexError = string.Empty;
        ManualUvexLastAction = $"正在{actionName}…";
        try
        {
            using var client = new UvexServiceClient(settings.ServiceUrl);
            await using var lease = await client
                .AcquireLeaseAsync("N.I.N.A. OpenAstroSpec manual UVEX control", lifetime.Token)
                .ConfigureAwait(true);
            var status = await action(lease, lifetime.Token).ConfigureAwait(true);
            ApplyManualUvexStatus(status);
            ManualUvexLastAction = $"{actionName}完成，服务回读已核验。";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            ManualUvexLastAction = $"{actionName}已取消。";
        }
        catch (Exception ex)
        {
            InvalidateManualUvexStatus();
            ManualUvexError = $"{actionName}失败：{ex.Message}";
            ManualUvexLastAction = $"{actionName}未完成；请查看本页红色提示。";
        }
        finally
        {
            IsManualUvexBusy = false;
        }
    }

    private void ApplyManualUvexStatus(UvexDeviceStatus status)
    {
        hasManualUvexStatus = true;
        manualUvexConnectionState = status.ConnectionState;
        manualUvexPositionKnown = status.PositionKnown;
        var firmware = string.IsNullOrWhiteSpace(status.FirmwareVersion) ? "固件未知" : $"固件 {status.FirmwareVersion}";
        var trust = status.PositionKnown ? $"位置可信（{FormatManualUvexPositionTrust(status.PositionTrust)}）" : "位置未知";
        ManualUvexConnectionStatus =
            $"{FormatManualUvexConnectionState(status.ConnectionState)} · {status.PortName} · {firmware} · {trust}" +
            (string.IsNullOrWhiteSpace(status.LastError) ? string.Empty : $" · 设备报告：{status.LastError}");

        var slitName = status.SlitPosition is { } position
            ? status.Slits.FirstOrDefault(item => item.Position == position)?.Name
            : null;
        var slitNameSuffix = string.IsNullOrWhiteSpace(slitName) ? string.Empty : $"（{slitName}）";
        var slit = status.SlitPosition is { } slitPosition
            ? $"槽位 {slitPosition}{slitNameSuffix}"
            : "未知";
        var focus = status.FocusPositionSteps?.ToString() ?? "未知";
        var grating = status.GratingPositionSteps?.ToString() ?? "未知";
        ManualUvexPositionStatus =
            $"狭缝：{slit} · M2：{focus} 步 · 光栅：{grating} 步 · 照明灯：{status.SlitIlluminationLedState}";
        RaiseCommandStates();
    }

    private static string FormatManualUvexConnectionState(DeviceConnectionState state) => state switch
    {
        DeviceConnectionState.Disconnected => "未连接",
        DeviceConnectionState.Connecting => "正在连接",
        DeviceConnectionState.Initializing => "正在初始化",
        DeviceConnectionState.Ready => "已连接",
        DeviceConnectionState.Busy => "正在执行动作",
        DeviceConnectionState.Faulted => "连接故障",
        DeviceConnectionState.Maintenance => "已释放给原厂软件",
        _ => state.ToString(),
    };

    private static string FormatManualUvexPositionTrust(UvexPositionTrust trust) => trust switch
    {
        UvexPositionTrust.Live => "实时回读",
        UvexPositionTrust.LastKnown => "上次记录",
        UvexPositionTrust.Unknown => "未验证",
        _ => trust.ToString(),
    };

    private void InvalidateManualUvexStatus()
    {
        hasManualUvexStatus = false;
        manualUvexPositionKnown = false;
        manualUvexConnectionState = DeviceConnectionState.Faulted;
        RaiseCommandStates();
    }

    private void RefreshCommissioningProfileCatalog(bool applySelected)
    {
        try
        {
            var programDataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "UVEX-ADV");
            var result = CommissioningProfileCatalog.Discover(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "Profiles"),
                programDataRoot,
                settings.SelectedCommissioningProfilePath);
            preparationEvidence = AutomaticPreparationService.Discover(programDataRoot);
            commissioningProfiles = result.Profiles;
            telescopeCandidates = IncludeCurrentCandidate(result.Telescopes, settings.ExpectedTelescopeId, "当前保存的赤道仪", "OpenAstroSpec 当前配置");
            atrCameraCandidates = IncludeCurrentCandidate(result.AtrCameras, settings.ObservationExpectedAtrCameraId, "当前保存的 ATR585M", "OpenAstroSpec 当前配置");
            g3CameraCandidates = IncludeCurrentCandidate(result.G3Cameras, settings.Phd2CameraStableId, "当前保存的 G3M2210M", "OpenAstroSpec 当前配置");
            qhyCameraCandidates = IncludeCurrentCandidate(result.QhyCameras, settings.ObservationExpectedQhyCameraId, "当前保存的 QHYminiCam8M", "OpenAstroSpec 当前配置");

            RaisePropertyChanged(nameof(CommissioningProfiles));
            RaisePropertyChanged(nameof(TelescopeCandidates));
            RaisePropertyChanged(nameof(AtrCameraCandidates));
            RaisePropertyChanged(nameof(G3CameraCandidates));
            RaisePropertyChanged(nameof(QhyCameraCandidates));
            RaisePropertyChanged(nameof(PreparationEvidenceInventorySummary));
            RaisePropertyChanged(nameof(CommissioningPreparationStatus));
            RaisePropertyChanged(nameof(NightSetupPreparationStatus));

            selectedCommissioningProfile = applySelected
                ? CommissioningProfileCatalog.SelectStartupProfile(
                    commissioningProfiles,
                    settings.SelectedCommissioningProfileId,
                    settings.AutoLoadNewestCompleteCommissioningPackage)
                : commissioningProfiles.FirstOrDefault(item =>
                    string.Equals(item.Id, settings.SelectedCommissioningProfileId, StringComparison.OrdinalIgnoreCase))
                    ?? commissioningProfiles.FirstOrDefault(item => item.IsAutomatic)
                    ?? commissioningProfiles.FirstOrDefault();
            RaisePropertyChanged(nameof(SelectedCommissioningProfile));
            RaisePropertyChanged(nameof(SelectedCommissioningProfileDescription));
            MatchSelectedDeviceCandidates();

            CommissioningProfileLoadStatus =
                $"已发现 {commissioningProfiles.Count} 个台站方案、{telescopeCandidates.Count} 个赤道仪、{atrCameraCandidates.Count} 个 ATR、{g3CameraCandidates.Count} 个 G3/PHD2、{qhyCameraCandidates.Count} 个 QHY 候选；未连接任何设备。";
            if (applySelected) ApplySelectedCommissioningProfile(startup: true);
            else RaiseCommandStates();
        }
        catch (Exception ex)
        {
            CommissioningProfileLoadStatus = $"读取台站配置方案失败：{ex.Message}";
            Error = CommissioningProfileLoadStatus;
        }
    }

    private void ApplySelectedCommissioningProfile() => ApplySelectedCommissioningProfile(startup: false);

    private void ApplySelectedCommissioningProfile(bool startup)
    {
        if (SelectedCommissioningProfile is not { } profile) return;
        try
        {
            if (profile.IsAutomatic)
            {
                ApplyAutomaticSiteProfile(profile.BindingsPath);
            }
            else if (!string.IsNullOrWhiteSpace(profile.BindingsPath))
            {
                // A complete commissioning bundle supplies immutable evidence
                // and identities; the machine-local operational template
                // supplies the site's exposure ladders and bounded WCS/search
                // defaults.  Compose them in that order so selecting the
                // newest formal bundle does not resurrect zero-valued runtime
                // fields on a fresh N.I.N.A. Profile.  The formal bundle then
                // overwrites all evidence-bound values, and neither source
                // grants real-mode authority.
                var automatic = commissioningProfiles.FirstOrDefault(item => item.IsAutomatic);
                ApplyAutomaticSiteProfile(automatic?.BindingsPath);
                ApplyCommissioningBindings(profile.BindingsPath, rememberSelection: true);
            }
            else
            {
                throw new InvalidDataException("所选台站配置没有可加载的内容。");
            }

            settings.SelectedCommissioningProfileId = profile.Id;
            settings.SelectedCommissioningProfilePath = profile.BindingsPath ?? string.Empty;
            ApplyPreparationSafetyCapability();
            var approval = TryAutoApproveSelectedCommissioningPackage(profile);
            CommissioningProfileLoadStatus = startup
                ? $"启动时已自动加载“{profile.DisplayName}”。{approval}候选仅来自保存的配置，未连接任何设备。"
                : $"已加载“{profile.DisplayName}”。{approval}候选仅来自保存的配置，未连接任何设备。";
            Error = string.Empty;
            OperatorNotice = CommissioningProfileLoadStatus + " 点击开始真实观测后，程序仍会按设备所有权逐项实时复核。";
            MatchSelectedDeviceCandidates();
            RaisePropertyChanged(string.Empty);
            RaiseCommandStates();
        }
        catch (Exception ex)
        {
            Error = $"加载台站配置方案失败：{ex.Message}";
            CommissioningProfileLoadStatus = Error;
        }
    }

    private void ApplyAutomaticSiteProfile(string? operationalProfilePath)
    {
        // Identity discovery is owned by N.I.N.A., PHD2 and the QHY service.
        // Site-specific operational values are loaded from a machine-local file,
        // never compiled into the open-source plugin. Neither path manufactures
        // commissioning/Night Setup evidence or grants real-mode authority.
        settings.RealModeCommissioned = false;
        if (telescopeCandidates.FirstOrDefault() is { } telescope) SelectedTelescopeCandidate = telescope;
        if (atrCameraCandidates.FirstOrDefault() is { } atr) SelectedAtrCameraCandidate = atr;
        if (g3CameraCandidates.FirstOrDefault() is { } g3) SelectedG3CameraCandidate = g3;
        if (qhyCameraCandidates.FirstOrDefault() is { } qhy) SelectedQhyCameraCandidate = qhy;

        var site = activeProfileService.ActiveProfile.AstrometrySettings;
        settings.ObservatoryLatitudeDegrees = site.Latitude;
        settings.ObservatoryLongitudeDegreesEast = site.Longitude;
        settings.ObservatoryElevationMeters = site.Elevation;
        if (!string.IsNullOrWhiteSpace(operationalProfilePath))
        {
            ApplyOperationalProfileValues(operationalProfilePath);
        }
        ApplyPreparationSafetyCapability();
        settings.RealModeCommissioned = false;
    }

    private void ApplyPreparationSafetyCapability()
    {
        var weak = string.Equals(
            settings.PreparationSafetyCapabilityPreset,
            "OperatorWeakSupervision",
            StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                settings.PreparationSafetyCapabilityPreset,
                "OperatorSupervised",
                StringComparison.OrdinalIgnoreCase);
        settings.WeakSupervisionEnabled = weak;
        settings.RequireSafetyMonitor = !weak;
        settings.RequireOpenDomeOrRoof = !weak;
        settings.RequireWeatherData = !weak;
        settings.RequireOpenOpticalCover = !weak;
    }

    private string TryAutoApproveSelectedCommissioningPackage(CommissioningProfileChoice profile)
    {
        settings.RealModeCommissioned = false;
        if (profile.IsAutomatic || string.IsNullOrWhiteSpace(profile.BindingsPath)) return string.Empty;
        if (!settings.AutoApproveValidatedCommissioningPackage)
        {
            return "自动确认已关闭；请人工复核后确认。";
        }

        try
        {
            settings.RealModeCommissioned = true;
            var configuration = realRunnerFactory.CaptureConfiguration(settings);
            var plan = ObservationPlanFactory.FromSettings(settings, configuration);
            var staticIssues = RealModeEligibilityIssues().Concat(plan.Validate()).ToList();
            var loadedPreset = RealCommissioningPresetLoader
                .LoadAsync(configuration, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            staticIssues.AddRange(loadedPreset.Issues);
            if (loadedPreset.Preset is { } preset)
            {
                var loadedNight = LockedNightSetupSnapshotLoader
                    .LoadAsync(configuration, plan, preset, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                staticIssues.AddRange(loadedNight.Issues);
            }
            else
            {
                staticIssues.Add("设备标定方案未能形成可验证的锁定 preset。");
            }

            var failures = staticIssues
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (failures.Length == 0)
            {
                return settings.WeakSupervisionEnabled
                    ? "SHA-256、内部引用和设备身份静态校验通过，已自动确认；当前为有人弱监督，不是无人值守。"
                    : "SHA-256、内部引用和设备身份静态校验通过，已自动确认。";
            }

            settings.RealModeCommissioned = false;
            return $"自动确认未通过（{failures.Length} 项），保留为未确认；";
        }
        catch (Exception ex)
        {
            settings.RealModeCommissioned = false;
            return $"自动确认失败：{ex.Message}；";
        }
    }

    private int ApplyOperationalProfileValues(string path)
    {
        var values = CommissioningProfileCatalog.ReadProfileValues(path);
        var assignments = new List<(PropertyInfo Property, object Value)>();
        foreach (var item in values)
        {
            if (!CommissioningProfileCatalog.IsOperationalProfileSetting(item.Key))
            {
                throw new InvalidDataException($"本机运行模板包含不允许自动写入的设置 '{item.Key}'。");
            }
            var property = typeof(UvexPluginSettings).GetProperty(
                item.Key,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)
                ?? throw new InvalidDataException($"本机运行模板包含当前插件不认识的设置 '{item.Key}'。");
            if (!property.CanWrite)
            {
                throw new InvalidDataException($"本机运行模板设置 '{item.Key}' 不可写。");
            }
            assignments.Add((property, ConvertBindingValue(item.Value, property.PropertyType, item.Key)));
        }
        foreach (var assignment in assignments) assignment.Property.SetValue(settings, assignment.Value);
        return assignments.Count;
    }

    private void MatchSelectedDeviceCandidates()
    {
        selectedTelescopeCandidate = telescopeCandidates.FirstOrDefault(item => SameIdentity(item.Id, settings.ExpectedTelescopeId));
        selectedAtrCameraCandidate = atrCameraCandidates.FirstOrDefault(item => SameIdentity(item.Id, settings.ObservationExpectedAtrCameraId));
        selectedG3CameraCandidate = g3CameraCandidates.FirstOrDefault(item => SameIdentity(item.Id, settings.Phd2CameraStableId));
        selectedQhyCameraCandidate = qhyCameraCandidates.FirstOrDefault(item => SameIdentity(item.Id, settings.ObservationExpectedQhyCameraId));
        RaisePropertyChanged(nameof(SelectedTelescopeCandidate));
        RaisePropertyChanged(nameof(SelectedAtrCameraCandidate));
        RaisePropertyChanged(nameof(SelectedG3CameraCandidate));
        RaisePropertyChanged(nameof(SelectedQhyCameraCandidate));
    }

    private static IReadOnlyList<DeviceIdentityChoice> IncludeCurrentCandidate(
        IReadOnlyList<DeviceIdentityChoice> candidates,
        string currentId,
        string displayName,
        string source)
    {
        if (string.IsNullOrWhiteSpace(currentId) || currentId.StartsWith("SIM-", StringComparison.OrdinalIgnoreCase) ||
            candidates.Any(item => SameIdentity(item.Id, currentId))) return candidates;
        return candidates.Append(new DeviceIdentityChoice(currentId, displayName, source)).ToArray();
    }

    private static bool SameIdentity(string left, string right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private void AutoFillConnectedNinaDevices()
    {
        if (!CanEditTargetPlan()) return;
        var applied = new List<string>();
        var telescope = telescopeMediator.GetInfo();
        if (telescope.Connected && !string.IsNullOrWhiteSpace(telescope.DeviceId))
        {
            settings.ExpectedTelescopeId = telescope.DeviceId;
            applied.Add($"赤道仪 {telescope.DeviceId}");
        }

        var camera = cameraMediator.GetInfo();
        var cameraIdentity = string.Join('|', camera.Name, camera.DisplayName, camera.Description, camera.DeviceId);
        if (camera.Connected &&
            !string.IsNullOrWhiteSpace(camera.DeviceId) &&
            cameraIdentity.Contains(settings.ExpectedCameraName, StringComparison.OrdinalIgnoreCase))
        {
            settings.BoundCameraId = camera.DeviceId;
            settings.ObservationExpectedAtrCameraId = camera.DeviceId;
            applied.Add($"ATR585M {camera.DeviceId}");
        }

        var astrometry = activeProfileService.ActiveProfile.AstrometrySettings;
        settings.ObservatoryLatitudeDegrees = astrometry.Latitude;
        settings.ObservatoryLongitudeDegreesEast = astrometry.Longitude;
        settings.ObservatoryElevationMeters = astrometry.Elevation;
        applied.Add("站点坐标");
        RefreshProfileOwnership();
        RefreshAtrManualStatus();
        Error = string.Empty;
        OperatorNotice = applied.Count == 1
            ? "已同步 N.I.N.A. 配置的站点坐标；当前未连接可识别的赤道仪或 ATR585M。PHD2/G3、QHY 和不可变证据请通过台站配置方案一次导入。"
            : $"已从 N.I.N.A. 自动读取：{string.Join("、", applied)}。PHD2/G3、QHY 和不可变证据仍由台站配置方案一次导入。";
        RaisePropertyChanged(nameof(ExpectedTelescopeId));
        RaisePropertyChanged(nameof(ExpectedAtrCameraId));
        RaisePropertyChanged(nameof(BoundAtrCameraId));
        RaisePropertyChanged(nameof(SiteLatitudeDegrees));
        RaisePropertyChanged(nameof(SiteLongitudeDegreesEast));
        RaisePropertyChanged(nameof(SiteElevationMeters));
        RaisePropertyChanged(nameof(RealModeStatus));
        RaisePropertyChanged(nameof(RealModeStatusSummary));
        RaiseCommandStates();
    }

    private void CreateNightSetupPreparationDraft()
    {
        if (!CanEditTargetPlan()) return;
        try
        {
            var programDataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "UVEX-ADV");
            preparationEvidence = AutomaticPreparationService.Discover(programDataRoot);
            var input = new NightSetupPreparationDraftInput(
                TargetName,
                CatalogId,
                settings.ExpectedTelescopeId,
                settings.ObservationExpectedAtrCameraId,
                settings.Gain,
                settings.Offset,
                settings.Binning,
                settings.RoiX,
                settings.RoiY,
                settings.RoiWidth,
                settings.RoiHeight,
                settings.AtrTargetTemperatureC,
                settings.AtrReadoutModeIndex,
                settings.Phd2CameraStableId,
                settings.Phd2ProfileName,
                settings.G3ExposureMilliseconds,
                settings.G3GainPercent,
                settings.G3SaturationAdu,
                settings.ObservationExpectedQhyCameraId,
                settings.QhyGain,
                settings.QhyOffset,
                settings.QhyBinning,
                settings.QhyReadoutMode,
                settings.QhyRoiX,
                settings.QhyRoiY,
                settings.QhyRoiWidth,
                settings.QhyRoiHeight,
                double.IsFinite(settings.QhyTargetTemperatureC) ? settings.QhyTargetTemperatureC : null,
                settings.ExpectedUvexSlitPosition,
                settings.ExpectedUvexGratingPositionSteps == int.MinValue ? null : settings.ExpectedUvexGratingPositionSteps,
                settings.ExpectedUvexM2PositionSteps == int.MinValue ? null : settings.ExpectedUvexM2PositionSteps,
                settings.HorizonMinimumDegrees,
                settings.HorizonStartMarginDegrees,
                settings.HorizonContinueMarginDegrees,
                settings.PreparationSpectralRegionPreset,
                settings.PreparationCalibrationReferencePreset,
                settings.PreparationSafetyCapabilityPreset,
                settings.PreparationOrderSortingFilterInstalled,
                preparationEvidence);

            preparationDraftPath = AutomaticPreparationService.WriteDraft(programDataRoot, input);
            PreparationDraftStatus =
                $"已自动汇总并保存准备草稿：{Path.GetFileName(preparationDraftPath)}。它不会冒充锁定配置；缺失的实测证据已列在 UnresolvedItems 中。";
            Error = string.Empty;
            OperatorNotice = PreparationDraftStatus;
            RaisePropertyChanged(nameof(PreparationEvidenceInventorySummary));
            RaisePreparationProperties();
        }
        catch (Exception ex)
        {
            Error = $"生成本次观测配置草稿失败：{ex.Message}";
        }
    }

    private void SelectNightSetupSnapshot()
    {
        if (!CanEditTargetPlan()) return;
        try
        {
            var defaultDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "UVEX-ADV",
                "commissioning");
            var dialog = new OpenFileDialog
            {
                Title = "选择本夜光学设置快照",
                Filter = "本夜光学设置 JSON (*.json)|*.json",
                CheckFileExists = true,
                Multiselect = false,
                InitialDirectory = Directory.Exists(defaultDirectory) ? defaultDirectory : null,
            };
            if (dialog.ShowDialog() != true) return;

            var bytes = File.ReadAllBytes(dialog.FileName);
            var setup = JsonSerializer.Deserialize<NightSetupRecord>(bytes, CaseInsensitiveJson)
                ?? throw new InvalidDataException("Night Setup 文件为空。 ");
            var issues = setup.Validate();
            if (issues.Count > 0)
            {
                throw new InvalidDataException($"Night Setup 内容无效：{string.Join(" ", issues)}");
            }
            if (setup.SchemaVersion != NightSetupRecord.CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Night Setup schema {setup.SchemaVersion} 只能作为历史记录读取；真实自动观测必须使用含三焦域独立证据的 schema {NightSetupRecord.CurrentSchemaVersion}。 ");
            }

            settings.NightSetupSnapshotPath = Path.GetFullPath(dialog.FileName);
            settings.NightSetupSnapshotSha256 = Convert.ToHexString(SHA256.HashData(bytes));
            settings.ObservationNightSetupId = setup.NightSetupId;
            settings.ExpectedUvexSlitPosition = setup.SlitPosition;
            settings.ExpectedUvexGratingPositionSteps = setup.GratingPositionSteps;
            settings.ExpectedUvexM2PositionSteps = setup.M2PositionSteps;
            Error = string.Empty;
            OperatorNotice = $"已选择并校验 Night Setup“{setup.NightSetupId}”；文件哈希、狭缝、光栅和 M2 期望值已自动填写。";
            preparationDraftPath = string.Empty;
            PreparationDraftStatus = "已导入锁定的 schema-2 本夜配置；不再使用此前草稿。";
            RaisePropertyChanged(nameof(NightSetupSnapshotPath));
            RaisePropertyChanged(nameof(NightSetupSnapshotSha256));
            RaisePropertyChanged(nameof(NightSetupId));
            RaisePropertyChanged(nameof(ExpectedUvexSlitPosition));
            RaisePropertyChanged(nameof(RealModeStatus));
            RaiseCommandStates();
        }
        catch (Exception ex)
        {
            Error = $"选择 Night Setup 失败：{ex.Message}";
        }
    }

    private void EnableMountTrackingForCommissioning()
    {
        var before = telescopeMediator.GetInfo();
        if (!before.Connected)
        {
            MountTrackingManualStatus = "未执行：请先由 N.I.N.A. 连接赤道仪。";
            return;
        }
        if (before.AtPark)
        {
            MountTrackingManualStatus = "未执行：赤道仪仍处于停驻状态。";
            return;
        }

        var accepted = telescopeMediator.SetTrackingEnabled(true);
        var after = telescopeMediator.GetInfo();
        MountTrackingManualStatus = accepted && after.TrackingEnabled
            ? "已由 N.I.N.A. 启用并回读确认恒星时跟踪。"
            : $"启用失败：N.I.N.A. accepted={accepted}，回读 tracking={after.TrackingEnabled}。";
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
        targetDraft.TargetName = result.TargetName;
        SetNativeTargetCoordinates(result.RightAscensionDegrees, result.DeclinationDegrees);
        targetDraft.PositionAngle = result.PositionAngleDegrees ?? 0;
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
        RaisePropertyChanged(nameof(Target));

        HasTargetImport = true;
        TargetImportSummary = auditSummary;
        TargetImportDetails = result.Details;
            OperatorNotice = $"目标草稿已更新为 {result.TargetName}：RA {result.RightAscensionDegrees:F8}°，Dec {result.DeclinationDegrees:+0.00000000;-0.00000000;0.00000000}°（J2000）。本夜光学设置、设备标定、时长和安全限制未改变。";
        Error = string.Empty;
        RaisePreparationProperties();
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
        RaisePreparationProperties();
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
                SelectedWorkspaceTabIndex = 3;
                Error = "自动观测尚不能启动：请先完成“自动准备”中标红的字段。工程级详细原因仍可在“高级设置”查看。";
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

    private bool CanManageManualUvexConnection() => !IsManualUvexBusy && CanEditTargetPlan();

    private bool CanConnectManualUvex() =>
        CanManageManualUvexConnection() &&
        (!hasManualUvexStatus || manualUvexConnectionState is
            DeviceConnectionState.Disconnected or
            DeviceConnectionState.Faulted or
            DeviceConnectionState.Maintenance);

    private bool CanDisconnectManualUvex() =>
        CanManageManualUvexConnection() &&
        hasManualUvexStatus &&
        manualUvexConnectionState is not DeviceConnectionState.Disconnected;

    private bool CanOperateManualUvex() =>
        CanManageManualUvexConnection() &&
        hasManualUvexStatus &&
        manualUvexConnectionState == DeviceConnectionState.Ready &&
        manualUvexPositionKnown;

    private bool CanEditTargetPlan() => RunState is
        ObservationRunState.Idle or
        ObservationRunState.Completed or
        ObservationRunState.Cancelled or
        ObservationRunState.Faulted;

    private bool CanStartReal() => CanStart();

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
        issues.AddRange(NinaImageFilePatternPolicy.Assess(
            activeProfileService.ActiveProfile.ImageFileSettings.FilePattern).BlockingIssues);
        var capabilities = ObservationAutomationPolicy.ValidateFullAutomationCapabilities(
            settings.RequireSafetyMonitor,
            settings.RequireOpenDomeOrRoof,
            settings.RequireWeatherData,
            settings.RequireOpenOpticalCover,
            settings.WeakSupervisionEnabled);
        if (capabilities.Disposition != GateDisposition.Passed) issues.Add(capabilities.Message);
        if (!settings.RealModeCommissioned) issues.Add("真实模式尚未标记为已调试。");
        if (string.IsNullOrWhiteSpace(settings.CommissioningPresetPath) || !File.Exists(settings.CommissioningPresetPath)) issues.Add("缺少不可变设备标定文件。");
        if (string.IsNullOrWhiteSpace(settings.CommissioningPresetId)) issues.Add("缺少设备标定方案 ID。");
        if (string.IsNullOrWhiteSpace(settings.CommissioningPresetSha256)) issues.Add("缺少设备标定方案 SHA-256。");
        if (!string.IsNullOrWhiteSpace(settings.CommissioningPresetPath) && File.Exists(settings.CommissioningPresetPath) &&
            !string.IsNullOrWhiteSpace(settings.CommissioningPresetSha256))
            issues.AddRange(ValidateCommissioningPresetUiRequirements());
        if (string.IsNullOrWhiteSpace(settings.CommissioningHardwareFingerprintSha256)) issues.Add("缺少硬件指纹 SHA-256。");
        if (string.IsNullOrWhiteSpace(settings.NightSetupSnapshotPath) || !File.Exists(settings.NightSetupSnapshotPath)) issues.Add("缺少本夜光学设置快照文件。");
        if (string.IsNullOrWhiteSpace(settings.NightSetupSnapshotSha256)) issues.Add("缺少本夜光学设置 SHA-256。");
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
            issues.Add("G3 曝光/增益无效；它们必须由设备标定方案显式锁定。");
        }
        if (settings.G3CameraRecoveryDelayMilliseconds is < 250 or > 10_000)
        {
            issues.Add("G3 连续全幅采集的驱动恢复等待必须在 250–10000 ms；本机实测 ToupTek/G3 需要 3000 ms。");
        }
        try
        {
            var filterSequence = settings.ParseQhyParallelFilterSequence();
            if (filterSequence.Count > 32) issues.Add("QHY 并行滤镜循环不能超过 32 个步骤。");
            foreach (var step in filterSequence)
            {
                if (string.IsNullOrWhiteSpace(step.FilterName) || step.FilterName.Length > 32)
                    issues.Add("QHY 并行滤镜名必须是 1–32 个字符。");
                if (!double.IsFinite(step.ExposureSeconds) || step.ExposureSeconds <= 0)
                    issues.Add($"QHY {step.FilterName} 并行曝光必须是正有限秒数。");
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            issues.Add($"QHY 并行滤镜循环无法解析：{ex.Message}");
        }
        if (settings.QhyMinimumDetectedStars < 0)
            issues.Add("QHY 最少星数不能小于 0；0 表示只记录星数，不以它阻断。");
        if (!double.IsFinite(settings.QhyMinimumTransparency) || settings.QhyMinimumTransparency is < 0 or > 1)
            issues.Add("QHY 最低透明度必须在 [0,1]；0 表示只记录透明度，不以它阻断。");
        if (!double.IsFinite(settings.QhyMaximumSaturatedFraction) || settings.QhyMaximumSaturatedFraction is <= 0 or > 1)
            issues.Add("QHY 最大饱和比例必须在 (0,1]。");
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
        if (!double.IsFinite(settings.G3WcsFreshSolveAuthorizationResidualArcseconds) ||
            settings.G3WcsFreshSolveAuthorizationResidualArcseconds <= 2 ||
            settings.G3WcsFreshSolveAuthorizationResidualArcseconds > settings.G3WcsMaximumSingleCorrectionArcseconds)
        {
            issues.Add("G3 WCS fresh 验证帧终点残差必须大于 2 arcsec，且不超过 WCS 单次运动上限。");
        }
        if (!double.IsFinite(settings.G3MotionPostSlewSettleSeconds) || settings.G3MotionPostSlewSettleSeconds <= 0)
            issues.Add("G3 移动后稳定等待必须是经设备标定显式确认的正秒数。");
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
        // QHY is a no-motion WCS witness in the current production route.
        // Do not surface the retained legacy QHY movement envelope as a
        // startup requirement.
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
            issues.Add("G3 搜索步长超过设备标定的单次运动上限。");
        }
        if (settings.G3SearchMaximumCumulativeArcseconds > settings.MaximumCumulativeCorrectionArcseconds)
        {
            issues.Add("G3 搜索累计运动上限超过设备标定的累计运动上限。");
        }
        if (settings.G3SearchMaximumAttempts > settings.MaximumCorrectionAttempts)
        {
            issues.Add("G3 搜索尝试次数超过设备标定的总修正次数上限。");
        }
        if (settings.G3SearchMaximumMinutes > settings.MaximumAcquisitionMinutes)
        {
            issues.Add("G3 搜索耗时上限超过设备标定的总采集耗时上限。");
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
            SelectedWorkspaceTabIndex = 5;
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
        RaisePreparationProperties();
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
        refreshManualUvexStatusCommand.RaiseCanExecuteChanged();
        connectManualUvexCommand.RaiseCanExecuteChanged();
        disconnectManualUvexCommand.RaiseCanExecuteChanged();
        releaseManualUvexComPortCommand.RaiseCanExecuteChanged();
        selectManualSlit1Command.RaiseCanExecuteChanged();
        selectManualSlit2Command.RaiseCanExecuteChanged();
        selectManualSlit3Command.RaiseCanExecuteChanged();
        selectManualSlit4Command.RaiseCanExecuteChanged();
        moveManualM2NegativeCommand.RaiseCanExecuteChanged();
        moveManualM2PositiveCommand.RaiseCanExecuteChanged();
        manualSlitLightOnCommand.RaiseCanExecuteChanged();
        manualSlitLightOffCommand.RaiseCanExecuteChanged();
        autoFillConnectedNinaDevicesCommand.RaiseCanExecuteChanged();
        selectNightSetupSnapshotCommand.RaiseCanExecuteChanged();
        createNightSetupDraftCommand.RaiseCanExecuteChanged();
        openPreparationDraftFolderCommand.RaiseCanExecuteChanged();
        applySelectedCommissioningProfileCommand.RaiseCanExecuteChanged();
        applyRecommendedImageFilePatternCommand.RaiseCanExecuteChanged();
        restorePreviousImageFilePatternCommand.RaiseCanExecuteChanged();
    }

    private void RaisePreparationProperties()
    {
        RaisePropertyChanged(nameof(AutomaticPreparationIssueCount));
        RaisePropertyChanged(nameof(AutomaticPreparationSummary));
        RaisePropertyChanged(nameof(IsTargetPreparationMissing));
        RaisePropertyChanged(nameof(TargetPreparationStatus));
        RaisePropertyChanged(nameof(IsDevicePreparationMissing));
        RaisePropertyChanged(nameof(DevicePreparationStatus));
        RaisePropertyChanged(nameof(IsCommissioningPreparationMissing));
        RaisePropertyChanged(nameof(CommissioningPreparationStatus));
        RaisePropertyChanged(nameof(PreparationEvidenceInventorySummary));
        RaisePropertyChanged(nameof(IsNightSetupPreparationMissing));
        RaisePropertyChanged(nameof(NightSetupPreparationStatus));
        RaisePropertyChanged(nameof(PreparationDraftStatus));
        RaisePropertyChanged(nameof(IsSlitChoiceMissing));
        RaisePropertyChanged(nameof(IsAutomationPolicyPreparationMissing));
        RaisePropertyChanged(nameof(AutomationPolicyPreparationStatus));
    }

    private bool CanApplyRecommendedImageFilePattern() =>
        CanEditTargetPlan() &&
        !string.Equals(
            NinaImageFilePatternCurrent,
            NinaImageFilePatternPolicy.RecommendedPattern,
            StringComparison.Ordinal);

    private bool CanRestorePreviousImageFilePattern() =>
        CanEditTargetPlan() &&
        previousNinaImageFilePattern is not null &&
        previousNinaImageFilePatternProfileId == activeProfileService.ActiveProfile.Id;

    private void ApplyRecommendedImageFilePattern()
    {
        if (!CanApplyRecommendedImageFilePattern()) return;
        var profile = activeProfileService.ActiveProfile;
        var before = profile.ImageFileSettings.FilePattern ?? string.Empty;
        try
        {
            previousNinaImageFilePattern = before;
            previousNinaImageFilePatternProfileId = profile.Id;
            profile.ImageFileSettings.FilePattern = NinaImageFilePatternPolicy.RecommendedPattern;
            profile.Save();
            Error = string.Empty;
            OperatorNotice = $"已显式更新当前 N.I.N.A. Profile 的图像文件模板。原值：{before}。可在本次 N.I.N.A. 会话中点击“撤销本次模板修改”恢复。";
        }
        catch (Exception ex)
        {
            profile.ImageFileSettings.FilePattern = before;
            previousNinaImageFilePattern = null;
            previousNinaImageFilePatternProfileId = null;
            Error = $"保存 N.I.N.A. 图像文件模板失败：{ex.Message}";
        }
        RefreshImageFilePatternDisplay();
    }

    private void RestorePreviousImageFilePattern()
    {
        if (!CanRestorePreviousImageFilePattern()) return;
        var profile = activeProfileService.ActiveProfile;
        var restored = previousNinaImageFilePattern!;
        try
        {
            profile.ImageFileSettings.FilePattern = restored;
            profile.Save();
            previousNinaImageFilePattern = null;
            previousNinaImageFilePatternProfileId = null;
            Error = string.Empty;
            OperatorNotice = $"已恢复此前的 N.I.N.A. 图像文件模板：{restored}";
        }
        catch (Exception ex)
        {
            Error = $"恢复 N.I.N.A. 图像文件模板失败：{ex.Message}";
        }
        RefreshImageFilePatternDisplay();
    }

    private void OnProfileChanged(object? sender, EventArgs e)
    {
        previousNinaImageFilePattern = null;
        previousNinaImageFilePatternProfileId = null;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            LoadNativeTargetDraftFromSettings();
            RefreshCommissioningProfileCatalog(applySelected: true);
            RefreshImageFilePatternDisplay();
            RefreshProfileOwnership();
        }
        else
        {
            _ = dispatcher.BeginInvoke(() =>
            {
                LoadNativeTargetDraftFromSettings();
                RefreshCommissioningProfileCatalog(applySelected: true);
                RefreshImageFilePatternDisplay();
                RefreshProfileOwnership();
            });
        }
    }

    private void RefreshImageFilePatternDisplay()
    {
        RaisePropertyChanged(nameof(NinaImageFilePatternCurrent));
        RaisePropertyChanged(nameof(NinaImageFilePatternRecommended));
        RaisePropertyChanged(nameof(NinaImageFilePatternStatus));
        RaisePropertyChanged(nameof(RealModeStatus));
        RaisePropertyChanged(nameof(RealModeStatusSummary));
        RaiseCommandStates();
    }

    private static InputTarget CreateNativeTargetDraft(
        IProfileService profileService,
        UvexPluginSettings settings)
    {
        var astrometry = profileService.ActiveProfile.AstrometrySettings;
        return new InputTarget(
            Angle.ByDegree(astrometry.Latitude),
            Angle.ByDegree(astrometry.Longitude),
            astrometry.Horizon)
        {
            TargetName = settings.ObservationTargetName,
            PositionAngle = double.IsFinite(settings.ObservationTargetPositionAngleDegrees)
                ? settings.ObservationTargetPositionAngleDegrees
                : 0,
            InputCoordinates = new InputCoordinates(new Coordinates(
                settings.ObservationRightAscensionDegrees,
                settings.ObservationDeclinationDegrees,
                Epoch.J2000,
                Coordinates.RAType.Degrees)),
        };
    }

    private Coordinates NativeTargetJ2000() =>
        targetDraft.InputCoordinates.Coordinates.Transform(Epoch.J2000);

    private void SetNativeTargetCoordinates(double rightAscensionDegrees, double declinationDegrees)
    {
        if (!double.IsFinite(rightAscensionDegrees) || !double.IsFinite(declinationDegrees)) return;
        targetDraft.InputCoordinates = new InputCoordinates(new Coordinates(
            rightAscensionDegrees,
            declinationDegrees,
            Epoch.J2000,
            Coordinates.RAType.Degrees));
    }

    private void LoadNativeTargetDraftFromSettings()
    {
        targetDraft.TargetName = settings.ObservationTargetName;
        SetNativeTargetCoordinates(
            settings.ObservationRightAscensionDegrees,
            settings.ObservationDeclinationDegrees);
        targetDraft.PositionAngle = double.IsFinite(settings.ObservationTargetPositionAngleDegrees)
            ? settings.ObservationTargetPositionAngleDegrees
            : 0;
        RaisePropertyChanged(nameof(Target));
        RaisePropertyChanged(nameof(TargetName));
        RaisePropertyChanged(nameof(RightAscensionDegrees));
        RaisePropertyChanged(nameof(DeclinationDegrees));
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
            if (!TryGetProperty(root, "schemaVersion", out var schema) || !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 5)
                issues.Add("自动真实观测要求 schema 5 commissioning preset。");
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
                Title = "导入 OpenAstroSpec Auto — UVEX4 设备标定方案",
                Filter = "OpenAstroSpec 设备标定方案 (*.bindings.json)|*.bindings.json|JSON (*.json)|*.json",
                CheckFileExists = true,
                Multiselect = false,
                InitialDirectory = Directory.Exists(defaultDirectory) ? defaultDirectory : null,
            };
            if (dialog.ShowDialog() != true) return;
            var assignmentCount = ApplyCommissioningBindings(dialog.FileName, rememberSelection: true);
            RefreshCommissioningProfileCatalog(applySelected: false);
            selectedCommissioningProfile = commissioningProfiles.FirstOrDefault(item =>
                string.Equals(item.BindingsPath, Path.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase));
            ApplyPreparationSafetyCapability();
            var approval = selectedCommissioningProfile is { } importedProfile
                ? TryAutoApproveSelectedCommissioningPackage(importedProfile)
                : string.Empty;
            RaisePropertyChanged(nameof(SelectedCommissioningProfile));
            RaisePropertyChanged(nameof(SelectedCommissioningProfileDescription));
            OperatorNotice = $"已导入并保存 {assignmentCount} 项设备标定设置：{Path.GetFullPath(dialog.FileName)}。{approval}";
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
            Error = $"导入设备标定方案失败：{ex.Message}";
        }
    }

    private int ApplyCommissioningBindings(string path, bool rememberSelection)
    {
        var values = CommissioningProfileCatalog.ReadProfileValues(path);
        var assignments = new List<(PropertyInfo Property, object Value)>();
        foreach (var item in values)
        {
            if (IsTargetPlanOrProvenanceSetting(item.Key))
            {
                throw new InvalidDataException(
                    $"设备标定方案不得修改观测目标或目标导入来源（'{item.Key}'）。请在观测计划区编辑，或使用构图助手/第三方星图导入。");
            }
            var property = typeof(UvexPluginSettings).GetProperty(
                item.Key,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property is null || !property.CanWrite)
            {
                throw new InvalidDataException($"设备标定方案包含当前插件不认识的设置 '{item.Key}'。");
            }
            assignments.Add((property, ConvertBindingValue(item.Value, property.PropertyType, item.Key)));
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
            throw new InvalidDataException($"设备标定方案缺少关键设置：{string.Join(", ", missing)}。");
        }

        foreach (var assignment in assignments) assignment.Property.SetValue(settings, assignment.Value);
        if (rememberSelection)
        {
            settings.SelectedCommissioningProfileId = Path.GetFullPath(path);
            settings.SelectedCommissioningProfilePath = Path.GetFullPath(path);
        }
        return assignments.Count;
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
        nameof(UvexPluginSettings.ObservationTargetPositionAngleDegrees) or
        nameof(UvexPluginSettings.ObservationTargetObservability) => true,
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
        ObservationRunState.RunningAuto => "自动推进",
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

public sealed record TargetObservabilityChoice(
    TargetObservabilityClass Value,
    string Label,
    string Description);

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

public sealed record UvexSlitChoice(int Position, string DisplayName);
