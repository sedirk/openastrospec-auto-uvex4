using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace UvexAdv.Nina.Plugin.UiHarness;

public sealed record ScreenshotScenario(
    string Name,
    int Width,
    int Height,
    ObservationDockMockViewModel ViewModel);

public static class ScenarioCatalog
{
    private static readonly IReadOnlyList<ScreenshotScenario> Scenarios =
    [
        new("idle", 1180, 800, ObservationDockMockViewModel.Idle()),
        new("startup-requirements", 1180, 900, ObservationDockMockViewModel.StartupRequirements()),
        new("running", 1180, 800, ObservationDockMockViewModel.Running()),
        new("failure", 1180, 800, ObservationDockMockViewModel.Failure()),
        new("phd2-degraded", 1180, 850, ObservationDockMockViewModel.Phd2Degraded()),
        new("phd2-direct-target", 1180, 880, ObservationDockMockViewModel.Phd2DirectTarget()),
        new("ghost-assistance", 1180, 900, ObservationDockMockViewModel.GhostAssistance()),
        new("qhy-g3-fast-pair", 1180, 900, ObservationDockMockViewModel.QhyG3FastPair()),
        new("narrow", 540, 900, ObservationDockMockViewModel.Narrow()),
        new("advanced", 1180, 1000, ObservationDockMockViewModel.Advanced())
    ];

    public static IReadOnlyCollection<string> Names => Scenarios.Select(item => item.Name).ToArray();

    public static IReadOnlyList<ScreenshotScenario> Select(string? name) =>
        name is null
            ? Scenarios
            : Scenarios.Where(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
}

public sealed class ObservationDockMockViewModel
{
    private static readonly ICommand EnabledCommand = new NoOpCommand(true);
    private static readonly ICommand DisabledCommand = new NoOpCommand(false);

    public string ModeText { get; init; } = "模拟演练";
    public string ModeDescription { get; init; } = "只运行模拟状态机，不连接相机、赤道仪、PHD2 或 COM5。";
    public string RealModeStatus { get; init; } = "真实模式有 3 个启动阻断项；当前演练不受影响。";
    public string RealModeStatusSummary { get; init; } = "真实模式：3 个启动阻断项。完整清单已移到“高级设置”。";
    public string StartButtonText { get; init; } = "启动模拟演练（不连接设备）";
    public string StateText { get; init; } = "空闲";
    public string StatusMessage { get; init; } = "尚未启动；可以先检查计划与运行模式。";
    public string PauseReason { get; init; } = string.Empty;
    public string CurrentStageText { get; init; } = "尚未开始";
    public string NextStageText { get; init; } = "锁定 Night Setup";
    public double ProgressPercent { get; init; }
    public string RunManifestPath { get; init; } = string.Empty;
    public string OperatorNotice { get; init; } = "离线截图数据；没有加载任何设备控制对象。";
    public string Error { get; init; } = string.Empty;
    public string Phd2CalibrationGradeText { get; init; } = "尚未评估";
    public string Phd2CalibrationPolicyText { get; init; } = "phd2-calibration-quality-v1";
    public string Phd2CommissioningRouteText { get; init; } = "路线：Phd2CalibrationLockShift · guide AutoPreferOffSlitThenDirectTarget · 普通星 2000 ms · 亮目标降级 10 ms";
    public string Phd2CalibrationPermissionText { get; init; } = "验证导星：等待 · exact-lock：等待 · 无人值守科学：否";
    public string Phd2CalibrationScaleText { get; init; } = "步长/残差缩放：等待 PostSettle 证据";
    public string Phd2CalibrationReasonText { get; init; } = "当前生产路径只评估 PHD2 当前 active calibration（单候选，并非历史择优）；真实 settle 和 fresh residual 到齐后再复评。";
    public string Phd2CalibrationOverviewGradeText { get; init; } = "尚未评估";
    public string Phd2CalibrationOverviewText { get; init; } = "启动导星后自动评估；评估前不执行精调或科学曝光。";
    public string GhostCalibrationSummaryText { get; init; } = "模式：Skip · 标定：commissioning preset 未包含 GhostAssistance。";
    public string GhostApplicabilityText { get; init; } = "适用性：尚未运行。";
    public string GhostDecisionText { get; init; } = "决定：尚未运行；Skip/Auto 继续原 WCS/居中/搜索。";
    public string GhostAssistanceModeText { get; init; } = "关闭";
    public string GhostOverviewText { get; init; } = "已关闭；目标定位使用常规 WCS、居中与有界搜索。";

    public bool IsSimulationMode { get; init; } = true;
    public bool IsRealMode => !IsSimulationMode;
    public bool IsRunActive { get; init; }
    public bool IsIdle => !IsRunActive && !HasFailure;
    public bool IsRunning => IsRunActive && !HasFailure;
    public bool IsPausedNeedsAttention => HasFailure;
    public bool HasFailure { get; init; }
    public bool HasNoFailure => !HasFailure;
    public bool HasActiveFailure => HasFailure;
    public bool HasPauseReason => !string.IsNullOrWhiteSpace(PauseReason);
    public bool HasOperatorNotice => !string.IsNullOrWhiteSpace(OperatorNotice);
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public Visibility FailureVisibility => HasFailure ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoFailureVisibility => HasFailure ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FailurePanelVisibility => FailureVisibility;
    public Visibility IdlePanelVisibility => NoFailureVisibility;
    public int SelectedWorkspaceTabIndex { get; init; }
    public int SelectedMainTabIndex => SelectedWorkspaceTabIndex;
    public int SelectedPreviewTabIndex { get; init; }

    public string TargetName { get; init; } = "Vega";
    public string CatalogId { get; init; } = "HD 172167";
    public string NightSetupId { get; init; } = "night-20260818-a";
    public double RightAscensionDegrees { get; init; } = 279.23473479;
    public double DeclinationDegrees { get; init; } = 38.78368896;
    public double DurationMinutes { get; init; } = 30;
    public string TargetImportSummary { get; init; } = string.Empty;
    public string TargetImportDetails { get; init; } = string.Empty;
    public bool HasTargetImport { get; init; }
    public bool IsTargetImportBusy { get; init; }
    public bool IsTargetPlanEditable => !IsRunActive && !IsPausedNeedsAttention && !IsTargetImportBusy;
    public bool ImportFramingCenter { get; init; }
    public double SiteLatitudeDegrees { get; init; } = 30.0;
    public double SiteLongitudeDegreesEast { get; init; } = 120.0;
    public double SiteElevationMeters { get; init; } = 50;
    public double HorizonMinimumDegrees { get; init; } = 40;
    public double HorizonStartMarginDegrees { get; init; } = 5;
    public double HorizonContinueMarginDegrees { get; init; } = 3;
    public int SimulationStageMilliseconds { get; init; } = 500;

    public string LastFailureHeadline { get; init; } = "当前没有失败或待处理质量门";
    public string LastFailureCode { get; init; } = string.Empty;
    public string LastFailureMessage { get; init; } = string.Empty;
    public string LastFailureMetrics { get; init; } = string.Empty;
    public string LastFailureRecommendation { get; init; } = string.Empty;
    public string LastFailureEvidencePath { get; init; } = string.Empty;
    public string LastFailurePreviewLabel { get; init; } = "本次运行没有失败帧";
    public string LatestEvidenceSummary { get; init; } = "尚未生成运行证据";
    public string LatestEvidencePath { get; init; } = string.Empty;

    public ImageSource? QhyPreviewImage { get; init; }
    public ImageSource? G3PreviewImage { get; init; }
    public ImageSource? AtrPreviewImage { get; init; }
    public bool HasQhyPreview => QhyPreviewImage is not null;
    public bool HasG3Preview => G3PreviewImage is not null;
    public bool HasAtrPreview => AtrPreviewImage is not null;
    public bool HasNoQhyPreview => !HasQhyPreview;
    public bool HasNoG3Preview => !HasG3Preview;
    public bool HasNoAtrPreview => !HasAtrPreview;
    public bool HasFailureEvidence => HasFailure && !string.IsNullOrWhiteSpace(LastFailureEvidencePath);
    public bool HasFailurePreview => HasFailure && G3PreviewImage is not null;
    public bool HasLatestEvidence => EvidenceRows.Count > 0;
    public Visibility QhyPreviewVisibility => HasQhyPreview ? Visibility.Visible : Visibility.Collapsed;
    public Visibility QhyEmptyVisibility => HasQhyPreview ? Visibility.Collapsed : Visibility.Visible;
    public Visibility G3PreviewVisibility => HasG3Preview ? Visibility.Visible : Visibility.Collapsed;
    public Visibility G3EmptyVisibility => HasG3Preview ? Visibility.Collapsed : Visibility.Visible;
    public Visibility AtrPreviewVisibility => HasAtrPreview ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AtrEmptyVisibility => HasAtrPreview ? Visibility.Collapsed : Visibility.Visible;
    public string QhyPreviewCaption { get; init; } = "尚无 QHY 预览：自动观测尚未启动；QHY 服务未请求帧。";
    public string G3PreviewCaption { get; init; } = "尚无 G3 预览：PHD2 没有为本次运行提供帧。";
    public string AtrPreviewCaption { get; init; } = "尚无 ATR 预览：尚未执行探测曝光。";
    public string QhyPreviewMetadata { get; init; } = "最后一帧：无";
    public string G3PreviewMetadata { get; init; } = "最后一帧：无";
    public string AtrPreviewMetadata { get; init; } = "最后一帧：无";

    public IReadOnlyList<MockGateRow> GateRows { get; init; } = [];
    public IReadOnlyList<MockTimelineRow> TimelineRows { get; init; } = [];
    public IReadOnlyList<MockEvidenceRow> EvidenceRows { get; init; } = [];

    public bool RealModeCommissioned { get; init; }
    public string CommissioningPresetPath { get; init; } = string.Empty;
    public string CommissioningPresetId { get; init; } = string.Empty;
    public string CommissioningPresetSha256 { get; init; } = string.Empty;
    public string CommissioningHardwareFingerprintSha256 { get; init; } = string.Empty;
    public string NightSetupSnapshotPath { get; init; } = string.Empty;
    public string NightSetupSnapshotSha256 { get; init; } = string.Empty;
    public string ExpectedTelescopeId { get; init; } = string.Empty;
    public string ExpectedAtrCameraId { get; init; } = string.Empty;
    public string ExpectedG3ProfileName { get; init; } = string.Empty;
    public string Phd2ProfileEvidenceSha256 { get; init; } = string.Empty;
    public string ExpectedQhyCameraId { get; init; } = string.Empty;
    public string NinaFilterWheelOwnershipStatus { get; init; } = "✓ N.I.N.A. 滤镜轮为 No_Device；QHY 物理滤镜轮只归独立 QHY 服务所有。";
    public string NinaGuiderOwnershipStatus { get; init; } = "✓ N.I.N.A. 导星适配器为 PHD2_Single。";
    public string WideToSlitTransferModeText { get; init; } = "Skip";
    public string WideToSlitTransferStatus { get; init; } = "自动预定位仍为 Skip；快速双解算只生成不能直接授权运动的候选。";
    public bool QhyG3FastPairEnabled { get; init; }
    public int QhyG3FastPairSchemaVersion { get; init; } = 1;
    public string QhyG3FastPairPolicyId { get; init; } = "qhy-g3-fast-pair-v1";
    public double QhyG3FastPairExposureSeconds { get; init; } = 2;
    public double QhyG3FastPairMaximumCachedAgeSeconds { get; init; } = 15;
    public double QhyG3FastPairMaximumMidpointSeparationSeconds { get; init; } = 20;
    public double QhyG3FastPairMaximumWallClockSeconds { get; init; } = 30;
    public double QhyG3FastPairMaximumMountSpanArcseconds { get; init; } = 2;
    public double QhyG3FastPairCandidateValidityHours { get; init; } = 24;
    public double QhyG3FastPairMaximumCandidateUncertaintyArcseconds { get; init; } = 20;
    public string QhyG3FastPairStatus { get; init; } = "未启用；G3 解算后直接进入原有 WCS/搜索路线。";
    public int QhyCoarseCenteringSchemaVersion { get; init; } = 1;
    public double QhyCoarseMaximumSingleCorrectionArcseconds { get; init; } = 600;
    public double QhyCoarseMaximumCumulativeCorrectionArcseconds { get; init; } = 2400;
    public int QhyCoarseMaximumCorrectionAttempts { get; init; } = 8;
    public double QhyCoarseMaximumCenteringMinutes { get; init; } = 10;
    public double G3SearchStepArcseconds { get; init; } = 10;
    public double G3SearchMaximumRadiusArcseconds { get; init; } = 40;
    public double G3SearchMaximumCumulativeArcseconds { get; init; } = 120;
    public int G3SearchMaximumAttempts { get; init; } = 8;
    public double G3SearchMaximumMinutes { get; init; } = 8;
    public string Phd2RegistryCameraName { get; init; } = "G3M2210M";
    public string Phd2RegistryMountName { get; init; } = "On-Step (ASCOM)";
    public string Phd2RuntimeCameraName { get; init; } = "G3M2210M";
    public string Phd2RuntimeMountName { get; init; } = "On-Step (ASCOM)";
    public int G3ExposureMilliseconds { get; init; } = 10_000;
    public int G3GainPercent { get; init; } = 100;
    public IReadOnlyList<string> GhostAssistanceModes { get; init; } = ["Skip", "AutoIfValidElseSkip", "RequireValid"];
    public string GhostAssistanceMode { get; init; } = "Skip";
    public bool BrightTargetWingCentroidEnabled { get; init; }
    public int BrightTargetMinimumG3ExposureMilliseconds { get; init; }
    public double BrightTargetMaximumQhyWcsAgeMinutes { get; init; }
    public double BrightTargetMaximumG3FrameAgeMinutes { get; init; }
    public double BrightTargetMaximumQhyResidualArcseconds { get; init; }
    public double BrightTargetMaximumCatalogMismatchArcseconds { get; init; } = 1;
    public double BrightTargetMinimumC11FocusConfidence { get; init; } = 0.7;
    public int BrightTargetMinimumSaturatedCorePixels { get; init; } = 3;
    public int BrightTargetMaximumSaturatedCorePixels { get; init; } = 20_000;
    public int BrightTargetWingRadiusPixels { get; init; } = 24;
    public double BrightTargetMinimumWingProminenceSigma { get; init; } = 6;
    public double BrightTargetMaximumWingLevelFraction { get; init; } = 0.92;
    public int BrightTargetMinimumWingPixels { get; init; } = 48;
    public double BrightTargetMinimumWingSignalToNoise { get; init; } = 20;
    public double BrightTargetMinimumAngularCoverageFraction { get; init; } = 0.75;
    public double BrightTargetMinimumOpposedWingBalance { get; init; } = 0.35;
    public double BrightTargetMaximumWingCentroidDisagreementPixels { get; init; } = 1.5;
    public int BrightTargetEdgeMarginPixels { get; init; } = 30;
    public double BrightTargetNearbySaturatedCoreRadiusPixels { get; init; } = 48;
    public double BrightTargetMinimumUniquenessRatio { get; init; } = 1.8;
    public double BrightTargetMaximumSecondaryPeakRatio { get; init; } = 0.35;

    public ICommand SelectSimulationModeCommand => EnabledCommand;
    public ICommand SelectRealModeCommand => EnabledCommand;
    public ICommand StartSelectedModeCommand => IsRunActive ? DisabledCommand : EnabledCommand;
    public ICommand PauseCommand => IsRunning ? EnabledCommand : DisabledCommand;
    public ICommand ResumeCommand => IsPausedNeedsAttention ? EnabledCommand : DisabledCommand;
    public ICommand TakeoverCommand => IsRunActive || IsPausedNeedsAttention ? EnabledCommand : DisabledCommand;
    public ICommand CancelCommand => IsRunActive || IsPausedNeedsAttention ? EnabledCommand : DisabledCommand;
    public ICommand ImportCommissioningBindingsCommand => EnabledCommand;
    public ICommand ShowObservationPlanCommand => EnabledCommand;
    public ICommand ShowStartupRequirementsCommand => EnabledCommand;
    public ICommand RefreshProfileOwnershipCommand => EnabledCommand;
    public ICommand ImportFromFramingAssistantCommand =>
        IsRunActive || IsPausedNeedsAttention || IsTargetImportBusy ? DisabledCommand : EnabledCommand;
    public ICommand ImportFromPlanetariumCommand =>
        IsRunActive || IsPausedNeedsAttention || IsTargetImportBusy ? DisabledCommand : EnabledCommand;
    public ICommand OpenRunDirectoryCommand => string.IsNullOrWhiteSpace(RunManifestPath) ? DisabledCommand : EnabledCommand;
    public ICommand OpenFailurePreviewCommand => HasFailure ? EnabledCommand : DisabledCommand;
    public ICommand OpenFailureEvidenceDirectoryCommand => HasFailure ? EnabledCommand : DisabledCommand;
    public ICommand OpenLatestEvidenceDirectoryCommand => EvidenceRows.Count > 0 ? EnabledCommand : DisabledCommand;
    public ICommand OpenQhyPreviewCommand => HasQhyPreview ? EnabledCommand : DisabledCommand;
    public ICommand OpenG3PreviewCommand => HasG3Preview ? EnabledCommand : DisabledCommand;
    public ICommand OpenAtrPreviewCommand => HasAtrPreview ? EnabledCommand : DisabledCommand;

    public static ObservationDockMockViewModel Idle() => new()
    {
        TargetName = "Deneb",
        CatalogId = string.Empty,
        RightAscensionDegrees = 310.35798,
        DeclinationDegrees = 45.28034,
        TargetImportSummary = "已从 N.I.N.A. 构图助手 / 本次构图矩形初始中心导入 · Deneb · 目录 ID：无（已清空） · J2000 RA 310.35798000° / Dec +45.28034000° · PA：未提供 · 2026-08-18 06:26:03 UTC",
        TargetImportDetails = "本次显式采用构图矩形初始中心；导入只更新目标草稿，不改变 Night Setup、commissioning、计划时长或安全设置。",
        HasTargetImport = true,
        SelectedWorkspaceTabIndex = 0,
        SelectedPreviewTabIndex = 0,
        GateRows = DefaultGates("等待"),
        TimelineRows = [],
        EvidenceRows = []
    };

    public static ObservationDockMockViewModel StartupRequirements() => new()
    {
        ModeText = "真实设备控制",
        ModeDescription = "只有点击固定标题栏中的启动按钮后才会进入真实流程；启动前仍会逐项检查不可变证据、设备身份、安全门和运动限额。仅切换到此模式不会连接或移动设备。",
        RealModeStatus = "真实模式当前有 12 个启动阻断项：\n• 真实模式尚未标记为已调试。\n• 缺少 commissioning preset 文件。\n• 缺少 commissioning preset ID。\n• 缺少 commissioning preset SHA-256。\n• 缺少硬件 fingerprint SHA-256。\n• 缺少 Night Setup 快照文件。\n• 缺少 Night Setup SHA-256。\n• 缺少赤道仪 DeviceId。\n• 缺少真实 ATR585M DeviceId。\n• 缺少真实 QHY StableId。\n• PHD2 Profile/G3/赤道仪身份不完整。\n• 缺少 PHD2 注册表证据 SHA-256。",
        RealModeStatusSummary = "真实模式：12 个启动阻断项。完整清单已移到“高级设置”。",
        StartButtonText = "启动真实设备自动观测",
        IsSimulationMode = false,
        SelectedWorkspaceTabIndex = 5,
        OperatorNotice = "离线启动条件截图；没有连接任何设备。",
    };

    public static ObservationDockMockViewModel Running() => new()
    {
        ModeText = "真实设备控制",
        ModeDescription = "全部启动硬门已经通过；流程正在自动推进，人工可随时暂停。",
        RealModeStatus = "✓ 真实模式启动条件已通过",
        RealModeStatusSummary = "真实模式：启动条件已通过。",
        StartButtonText = "真实设备自动观测正在运行",
        StateText = "运行中",
        StatusMessage = "G3 精解析通过，正在执行目标质心到狭缝的有界闭环。",
        CurrentStageText = "目标入缝",
        NextStageText = "选择导星星并等待稳定",
        ProgressPercent = 47,
        RunManifestPath = @"C:\UVEX-ADV\runs\simulated-running\manifest.json",
        IsSimulationMode = false,
        IsRunActive = true,
        SelectedWorkspaceTabIndex = 2,
        SelectedPreviewTabIndex = 1,
        QhyPreviewImage = PreviewImageFactory.CreateQhyField(),
        G3PreviewImage = PreviewImageFactory.CreateG3SlitField(false),
        AtrPreviewImage = PreviewImageFactory.CreateAtrSpectrum(),
        QhyPreviewCaption = "QHY 广域解算通过：目标误差 12.4 arcsec；R 滤镜；星数 148。",
        G3PreviewCaption = "G3 星场：黄色十字为预测位置，青圈为目标质心，白线为 LED 标定狭缝。",
        AtrPreviewCaption = "上一探测帧：谱线未饱和，即时 SNR 31.7。",
        QhyPreviewMetadata = "21:42:18 UTC · 10 s · gain 94% · R · 148 stars",
        G3PreviewMetadata = "21:42:31 UTC · 12 s · gain 98% · FWHM 6.2 px",
        AtrPreviewMetadata = "21:40:05 UTC · 30 s · gain 100 · ROI 1920×480",
        GateRows = RunningGates(),
        TimelineRows = RunningTimeline(),
        EvidenceRows = RunningEvidence(),
        LatestEvidenceSummary = "21:42:31 · g3-solve · g3-solve-overlay.png",
        LatestEvidencePath = @"C:\UVEX-ADV\runs\simulated-running\g3-solve-overlay.png"
    };

    public static ObservationDockMockViewModel Failure() => new()
    {
        ModeText = "真实设备控制",
        ModeDescription = "自动流程已停止发起新动作；人工检查后可恢复并重新验证全部失效门。",
        RealModeStatus = "✓ 已锁定本次运行的 Night Setup 与设备身份",
        RealModeStatusSummary = "真实模式：本次运行的启动条件已通过。",
        StartButtonText = "当前运行已暂停",
        StateText = "已暂停，需要处理",
        StatusMessage = "G3 主焦点质量门未通过，没有开始导星或 ATR 科学曝光。",
        PauseReason = "G3 星点仍然过大，主焦点拟合无法建立可信最小值。",
        CurrentStageText = "C11 主焦点",
        NextStageText = "重新拍摄 G3 验证帧",
        ProgressPercent = 31,
        RunManifestPath = @"C:\UVEX-ADV\runs\simulated-failure\manifest.json",
        IsSimulationMode = false,
        HasFailure = true,
        SelectedWorkspaceTabIndex = 3,
        SelectedPreviewTabIndex = 1,
        LastFailureHeadline = "C11 主焦点质量门未通过",
        LastFailureCode = "G3_FOCUS_STARS_TOO_BROAD",
        LastFailureMessage = "拟合没有找到可信最小值；系统没有开始导星或科学曝光。",
        LastFailureMetrics = "FWHM 14.8 px（要求 ≤ 8.0 px） · 星数 11 · SNR 28.4 · 饱和 0.1%",
        LastFailureRecommendation = "查看 G3 失败帧；扩大 Star Focuser Pro 搜索跨度并确认已越过 C11 内调焦空程。不得使用 UVEX M2 或 GS350 AAF 代偿。",
        LastFailureEvidencePath = @"C:\UVEX-ADV\runs\simulated-failure\g3-focus-failed.png",
        LastFailurePreviewLabel = "查看 G3 主焦点失败帧",
        LatestEvidenceSummary = "21:47:52 · gate-failure · g3-focus-analysis.json",
        LatestEvidencePath = @"C:\UVEX-ADV\runs\simulated-failure\g3-focus-analysis.json",
        QhyPreviewImage = PreviewImageFactory.CreateQhyField(),
        G3PreviewImage = PreviewImageFactory.CreateG3SlitField(true),
        QhyPreviewCaption = "QHY 解算通过；当前故障与广域解算无关。",
        G3PreviewCaption = "失败帧：星点明显膨胀；绿色圈为检测轮廓，红色标记为被拒绝的宽星点。",
        G3PreviewMetadata = "21:47:52 UTC · 12 s · gain 98% · FWHM 14.8 px",
        GateRows = FailureGates(),
        TimelineRows = FailureTimeline(),
        EvidenceRows = FailureEvidence()
    };

    public static ObservationDockMockViewModel Phd2Degraded() => new()
    {
        ModeText = "真实设备控制 · 有人监督",
        ModeDescription = "离线界面验收：显示降级校准的权限边界；没有连接或移动设备。",
        RealModeStatus = "PHD2 降级模式只能由本轮显式选择，不能成为无人值守科学权威。",
        RealModeStatusSummary = "真实模式：启动条件已通过；等待本轮监督选择。",
        StartButtonText = "启动真实设备自动观测",
        StateText = "已暂停，需要处理",
        StatusMessage = "PHD2 当前校准已完成导星稳定和新鲜入缝残差复评。",
        PauseReason = "等待操作员决定是否接受降级校准的有人监督短时观测。",
        CurrentStageText = "选择导星星并等待稳定",
        NextStageText = "有人监督精确入缝",
        ProgressPercent = 58,
        IsSimulationMode = false,
        HasFailure = true,
        SelectedWorkspaceTabIndex = 0,
        Phd2CalibrationGradeText = "DegradedSupervised",
        Phd2CalibrationOverviewGradeText = "降级（需人工监督）",
        Phd2CalibrationPolicyText = "phd2-calibration-quality-v1",
        Phd2CommissioningRouteText = "路线：Phd2CalibrationLockShift · guide OffSlitGuideStar · 普通星 2000 ms · 亮目标降级 10 ms",
        Phd2CalibrationPermissionText = "验证导星：是 · exact-lock：是（仅有人监督） · 无人值守科学：否",
        Phd2CalibrationScaleText = "步长缩放：0.5 · 残差门缩放：0.75",
        Phd2CalibrationReasonText = "正交误差 11.7° 高于 Qualified 10.0°，但低于 Degraded 30.0°；同 epoch settle 与 fresh residual 已通过。",
        Phd2CalibrationOverviewText = "精确入缝：是（仅有人监督） · 无人值守拍摄：否",
        GateRows =
        [
            new("PHD2 校准复评", "降级可用", "PHD2_CALIBRATION_DEGRADED_SUPERVISED", "11.7° 候选只允许有人监督", "stage 0.5 · residual 0.75", "Indeterminate", true, false),
        ],
    };

    public static ObservationDockMockViewModel Phd2DirectTarget() => new()
    {
        ModeText = "真实设备控制 · 有人监督",
        ModeDescription = "离线界面验收：普通导星星不可用时，显示短曝光直接导目标星的退化路线；没有连接或移动设备。",
        RealModeStatus = "直接导目标星始终需要本轮显式监督许可，校准本身合格也不能获得无人值守权限。",
        RealModeStatusSummary = "真实模式：启动条件已通过；等待本轮监督选择。",
        StartButtonText = "启动真实设备自动观测",
        StateText = "已暂停，需要处理",
        StatusMessage = "普通星 2000 ms 同帧选择失败；10 ms 目标专用帧已唯一识别目标，等待显式监督许可。",
        PauseReason = "等待操作员接受短曝光直接导目标星；未接受前不启动导星或精确入缝。",
        CurrentStageText = "选择导星星并等待稳定",
        NextStageText = "短曝光直接导目标星并分段入缝",
        ProgressPercent = 56,
        IsSimulationMode = false,
        HasFailure = true,
        SelectedWorkspaceTabIndex = 0,
        Phd2CalibrationGradeText = "Qualified",
        Phd2CalibrationOverviewGradeText = "合格",
        Phd2CalibrationPolicyText = "phd2-calibration-quality-v1",
        Phd2CommissioningRouteText = "路线：Phd2CalibrationLockShift · guide DegradedDirectTargetGuiding · 普通星 2000 ms 未找到 · 目标短曝 10 ms · 有人监督",
        Phd2CalibrationPermissionText = "验证导星：是 · exact-lock：等待本轮监督许可 · 无人值守科学：否",
        Phd2CalibrationScaleText = "校准步长缩放：1.0 · direct-target 残差门使用更严格专用上限",
        Phd2CalibrationReasonText = "活动校准为 Qualified；但直接导目标星路线本身始终 RequiresOperatorSupervision，不能因校准合格而升级为无人值守。",
        Phd2CalibrationOverviewText = "精确入缝：等待监督许可 · 无人值守拍摄：否",
        GateRows =
        [
            new("PHD2 导星星选择", "等待监督许可", "PHD2_DIRECT_TARGET_SUPERVISION_REQUIRED", "普通星不可用；目标短曝已通过唯一性与边缘门", "ordinary 2000 ms · direct 10 ms", "Indeterminate", true, false),
        ],
    };

    public static ObservationDockMockViewModel GhostAssistance() => new()
    {
        ModeText = "真实设备控制",
        ModeDescription = "离线界面验收：已显示确定性鬼影辅助的完整权限边界；没有连接或移动设备。",
        RealModeStatus = "✓ schema 4 commissioning preset、四槽位光学身份与 action configuration 已锁定",
        RealModeStatusSummary = "真实模式：本次运行的启动条件已通过。",
        StartButtonText = "真实设备自动观测正在运行",
        StateText = "运行中",
        StatusMessage = "普通/翼部身份定位失败后，2 张新鲜同曝光 OFF 帧通过了鬼影模板适用性与一致性门。",
        CurrentStageText = "G3 精解析与目标定位",
        NextStageText = "新鲜狭缝/PHD2 残差闭环",
        ProgressPercent = 45,
        IsSimulationMode = false,
        IsRunActive = true,
        SelectedWorkspaceTabIndex = 0,
        GhostAssistanceMode = "AutoIfValidElseSkip",
        GhostAssistanceModeText = "自动（无效时跳过）",
        GhostCalibrationSummaryText = "模式：AutoIfValidElseSkip · 标定：g3-ghost-install-20260818 / A47D9C0F11B2 · 策略：ghost-match-v1 / 16BDA0397F22 · preset 8C64B121A05E。",
        GhostApplicabilityText = "适用性：GHOST_TEMPLATE_APPLICABLE · 外部身份：CatalogBoundG3Wcs · 新鲜同曝光 OFF 帧：2。",
        GhostDecisionText = "决定：UseCalibratedAuxiliaryEstimate · 门：GHOST_AUXILIARY_TARGET_ESTIMATE_VALID · 权限：CalibratedAuxiliaryOnly（不能建立身份或授权运动）。",
        GhostOverviewText = "本轮使用已标定的辅助质心；目标身份和设备移动仍由常规证据授权。",
        Phd2CalibrationGradeText = "Qualified",
        Phd2CalibrationOverviewGradeText = "合格",
        Phd2CommissioningRouteText = "路线：Phd2CalibrationLockShift · guide AutoPreferOffSlitThenDirectTarget · 普通星 2000 ms · 亮目标降级 10 ms",
        Phd2CalibrationPermissionText = "验证导星：是 · exact-lock：是 · 无人值守科学：等待新鲜残差",
        Phd2CalibrationScaleText = "步长缩放：1.0 · 残差门缩放：1.0",
        Phd2CalibrationOverviewText = "精确入缝：是 · 无人值守拍摄：等待新的入缝残差",
        GateRows = DefaultGates("等待"),
        TimelineRows =
        [
            new("22:11:04", "G3 精解析", "GHOST_AUXILIARY_TARGET_ESTIMATE_VALID", "鬼影只提供质心与协方差；身份仍来自当前 G3 WCS", "g3-ghost-assistance.json"),
        ],
        EvidenceRows =
        [
            new("22:11:04", "g3-ghost-assistance", "g3-ghost-assistance.json", @"C:\UVEX-ADV\runs\ghost-ui\g3-ghost-assistance.json"),
        ],
        LatestEvidenceSummary = "22:11:04 · g3-ghost-assistance · g3-ghost-assistance.json",
        LatestEvidencePath = @"C:\UVEX-ADV\runs\ghost-ui\g3-ghost-assistance.json",
    };

    public static ObservationDockMockViewModel QhyG3FastPair() => new()
    {
        ModeText = "真实设备控制 · 候选学习",
        ModeDescription = "离线界面验收：G3 解算后立即配对 QHY WCS；没有连接、曝光或移动设备。",
        RealModeStatus = "✓ 快速双解算 policy 已进入不可变 action configuration",
        RealModeStatusSummary = "真实模式：本次运行的启动条件已通过。",
        StartButtonText = "启动真实设备自动观测",
        StateText = "运行中",
        StatusMessage = "G3 WCS 成功；复用同一指向下 8.4 秒前完成的 QHY WCS，未增加曝光。",
        CurrentStageText = "G3 精解析与快速双解算配对",
        NextStageText = "继续 G3 目标识别 / 原有有界路线",
        ProgressPercent = 44,
        IsSimulationMode = false,
        IsRunActive = true,
        SelectedWorkspaceTabIndex = 5,
        QhyG3FastPairEnabled = true,
        QhyG3FastPairStatus = "已启用候选学习：优先复用 ≤15s 的同位置 QHY WCS；否则只拍 1 张 2s QHY 帧。全过程 0 次赤道仪命令，候选不能直接授权运动。",
        WideToSlitTransferStatus = "自动预定位仍为 Skip；本轮 paired-WCS 只生成 Candidate，需多样本独立验证后才能激活。",
        OperatorNotice = "示例候选 qhy-g3-pair-20260819T021504Z · midpoint 8.4 s · mount span 0.62″ · uncertainty 3.1″ · MotionAuthority=false。",
    };

    public static ObservationDockMockViewModel Narrow()
    {
        var running = Running();
        return new ObservationDockMockViewModel
        {
            ModeText = running.ModeText,
            ModeDescription = running.ModeDescription,
            RealModeStatus = running.RealModeStatus,
            StartButtonText = running.StartButtonText,
            StateText = running.StateText,
            StatusMessage = running.StatusMessage,
            CurrentStageText = running.CurrentStageText,
            NextStageText = running.NextStageText,
            ProgressPercent = running.ProgressPercent,
            RunManifestPath = running.RunManifestPath,
            CatalogId = string.Empty,
            TargetImportSummary = "已从 N.I.N.A. 第三方星图 / Stellarium 导入 · Vega · 目录 ID：无（已清空） · J2000 RA 279.23473479° / Dec +38.78368896° · PA：未提供 · 2026-08-18 13:39:42 UTC",
            TargetImportDetails = "单次目标快照；运行期间禁止再次导入。Night Setup、commissioning 与计划时长保持不变。",
            HasTargetImport = true,
            IsSimulationMode = running.IsSimulationMode,
            IsRunActive = true,
            SelectedWorkspaceTabIndex = 1,
            SelectedPreviewTabIndex = 0,
            QhyPreviewImage = running.QhyPreviewImage,
            G3PreviewImage = running.G3PreviewImage,
            AtrPreviewImage = running.AtrPreviewImage,
            QhyPreviewCaption = running.QhyPreviewCaption,
            G3PreviewCaption = running.G3PreviewCaption,
            AtrPreviewCaption = running.AtrPreviewCaption,
            QhyPreviewMetadata = running.QhyPreviewMetadata,
            G3PreviewMetadata = running.G3PreviewMetadata,
            AtrPreviewMetadata = running.AtrPreviewMetadata,
            GateRows = running.GateRows,
            TimelineRows = running.TimelineRows,
            EvidenceRows = running.EvidenceRows,
            LatestEvidenceSummary = running.LatestEvidenceSummary,
            LatestEvidencePath = running.LatestEvidencePath
        };
    }

    public static ObservationDockMockViewModel Advanced() => new()
    {
        ModeText = "真实设备控制",
        ModeDescription = "离线界面验收；不连接任何设备。",
        RealModeStatus = "超亮目标例外分支已显示；本截图不授权设备动作。",
        StartButtonText = "启动真实设备自动观测",
        IsSimulationMode = false,
        SelectedWorkspaceTabIndex = 5,
        BrightTargetWingCentroidEnabled = true,
        BrightTargetMinimumG3ExposureMilliseconds = 125,
        BrightTargetMaximumQhyWcsAgeMinutes = 5,
        BrightTargetMaximumG3FrameAgeMinutes = 2,
        BrightTargetMaximumQhyResidualArcseconds = 20,
        OperatorNotice = "离线高级设置截图；示例数值只用于排版验收，不是 commissioning 结论。",
    };

    private static IReadOnlyList<MockGateRow> DefaultGates(string state) =>
    [
        new("Night Setup 与安全", state, string.Empty, "尚未执行", string.Empty, "Pending", false, false),
        new("QHY 广域解算", state, string.Empty, "尚未执行", string.Empty, "Pending", false, false),
        new("G3 精解析与入缝", state, string.Empty, "尚未执行", string.Empty, "Pending", false, false),
        new("ATR 科学曝光", state, string.Empty, "尚未执行", string.Empty, "Pending", false, false)
    ];

    private static IReadOnlyList<MockGateRow> RunningGates() =>
    [
        new("Night Setup 与安全", "通过", "NIGHT_SETUP_LOCKED", "设备身份和安全门通过", "setup night-20260818-a", "Passed", false, false),
        new("QHY 广域解算", "通过", "QHY_SOLVE_OK", "广域 WCS 已锁定", "148 stars · residual 0.42 px", "Passed", false, false),
        new("G3 精解析", "通过", "G3_SOLVE_OK", "目标身份和视场匹配", "confidence 0.97", "Passed", false, false),
        new("目标入缝", "执行中", "SLIT_CENTERING", "正在发送有界微调", "residual 2.8 px · limit 12 px", "Running", true, false),
        new("导星稳定", "等待", string.Empty, "尚未执行", string.Empty, "Pending", false, false)
    ];

    private static IReadOnlyList<MockGateRow> FailureGates() =>
    [
        new("Night Setup 与安全", "通过", "NIGHT_SETUP_LOCKED", "设备身份和安全门通过", "setup night-20260818-a", "Passed", false, false),
        new("QHY 广域解算", "通过", "QHY_SOLVE_OK", "广域 WCS 已锁定", "148 stars", "Passed", false, false),
        new("C11 主焦点", "失败", "G3_FOCUS_STARS_TOO_BROAD", "主焦点拟合没有可信最小值", "FWHM 14.8 px · limit 8.0 px", "Failed", true, true),
        new("G3 精解析", "等待", string.Empty, "前置质量门未通过", string.Empty, "Pending", false, false),
        new("导星稳定", "等待", string.Empty, "没有开始导星", string.Empty, "Pending", false, false)
    ];

    private static IReadOnlyList<MockTimelineRow> RunningTimeline() =>
    [
        new("21:41:03", "Night Setup", "NIGHT_SETUP_LOCKED", "锁定本次设备配置", "night-setup.json"),
        new("21:42:18", "QHY 广域解算", "QHY_SOLVE_OK", "解算并完成粗居中", "qhy-solve-overlay.png"),
        new("21:42:31", "G3 精解析", "G3_SOLVE_OK", "目标身份置信度 0.97", "g3-solve-overlay.png")
    ];

    private static IReadOnlyList<MockTimelineRow> FailureTimeline() =>
    [
        .. RunningTimeline(),
        new("21:47:52", "C11 主焦点", "G3_FOCUS_STARS_TOO_BROAD", "质量门失败，自动进入暂停", "g3-focus-analysis.json")
    ];

    private static IReadOnlyList<MockEvidenceRow> RunningEvidence() =>
    [
        new("21:42:18", "qhy-solve", "qhy-solve-overlay.png", @"C:\UVEX-ADV\runs\simulated-running\qhy-solve-overlay.png"),
        new("21:42:31", "g3-solve", "g3-solve-overlay.png", @"C:\UVEX-ADV\runs\simulated-running\g3-solve-overlay.png")
    ];

    private static IReadOnlyList<MockEvidenceRow> FailureEvidence() =>
    [
        new("21:47:52", "g3-frame", "g3-focus-failed.png", @"C:\UVEX-ADV\runs\simulated-failure\g3-focus-failed.png"),
        new("21:47:52", "gate-failure", "g3-focus-analysis.json", @"C:\UVEX-ADV\runs\simulated-failure\g3-focus-analysis.json")
    ];
}

public sealed record MockGateRow(
    string Stage,
    string State,
    string Code,
    string Message,
    string Metrics,
    string Disposition,
    bool IsCurrent,
    bool IsFailed)
{
    public string StateGlyph => IsFailed ? "✕" : State == "通过" ? "✓" : IsCurrent ? "▶" : "○";
    public string Summary => $"{StateGlyph} {Stage}  {State}";
    public bool HasDetails => IsCurrent || IsFailed || !string.IsNullOrWhiteSpace(Code);
    public Visibility DetailsVisibility => IsCurrent || IsFailed ? Visibility.Visible : Visibility.Collapsed;
}

public sealed record MockTimelineRow(
    string Time,
    string Stage,
    string Code,
    string Message,
    string EvidencePath)
{
    public bool HasEvidence => !string.IsNullOrWhiteSpace(EvidencePath);
}

public sealed record MockEvidenceRow(
    string Time,
    string Kind,
    string FileName,
    string AbsolutePath);

internal sealed class NoOpCommand(bool canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => canExecute;

    public void Execute(object? parameter)
    {
    }
}
