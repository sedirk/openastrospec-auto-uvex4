using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Runtime.Versioning;
using System.Windows;
using NINA.Astrometry;
using NINA.Astrometry.Interfaces;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.Container.ExecutionStrategy;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Validations;
using Newtonsoft.Json;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin.SequenceItems;

[ExportMetadata("Name", "OpenAstroSpec · UVEX4 目标观测")]
[ExportMetadata("Description", "OpenAstroSpec Auto 的 UVEX4 单目标观测容器；质量门失败时自动暂停并等待人工处理")]
[ExportMetadata("Category", "OpenAstroSpec Auto")]
[Export(typeof(ISequenceItem))]
[Export(typeof(ISequenceContainer))]
[JsonObject(MemberSerialization.OptIn)]
[SupportedOSPlatform("windows")]
public sealed partial class UvexTargetObservationContainer : SequenceContainer, IImmutableContainer, IDeepSkyObjectContainer
{
    private readonly IProfileService profileService;
    private readonly INighttimeCalculator nighttimeCalculator;
    private readonly ObservationCoordinatorHost host;
    private readonly UvexPluginSettings settings;
    private readonly RealObservationStageRunnerFactory realRunnerFactory;
    private readonly Func<IObservationStageRunner>? simulationRunnerFactory;
    private SequencerStageBridge? activeBridge;
    private CancellationTokenSource? targetLifetime;
    private CancellationToken parentExecutionToken;
    private TaskCompletionSource? targetJoined;
    private int operatorSkipRequested;
    private bool endedByNativeCondition;
    private bool useRealMode;
    private InputTarget target;
    private bool loadingTarget = true;
    private int scienceFrames, maximumScienceAttempts;
    private double fixedExposureSeconds, durationMinutes;
    private double lastFixedExposureSeconds = 120;
    private bool forceGuideCalibration;
    private bool automaticGuideRecovery = true;
    private int guideStarWaitSeconds = 300;
    private SpectroscopyTargetFailurePolicy failurePolicy = SpectroscopyTargetFailurePolicy.SkipQualityFailure;

    [ImportingConstructor]
    public UvexTargetObservationContainer(
        IProfileService profileService,
        INighttimeCalculator nighttimeCalculator,
        ObservationCoordinatorHost host,
        RealObservationStageRunnerFactory realRunnerFactory,
        NINA.WPF.Base.Interfaces.ViewModel.IFramingAssistantVM framingAssistant,
        NINA.WPF.Base.Interfaces.Mediator.IApplicationMediator applicationMediator,
        NINA.Equipment.Interfaces.IPlanetariumFactory planetariumFactory)
        : this(profileService, nighttimeCalculator, host, realRunnerFactory, new UvexPluginSettings(profileService),
            framingAssistant: framingAssistant, applicationMediator: applicationMediator, planetariumFactory: planetariumFactory) { }

    internal UvexTargetObservationContainer(IProfileService profileService, INighttimeCalculator nighttimeCalculator,
        ObservationCoordinatorHost host, RealObservationStageRunnerFactory realRunnerFactory, UvexPluginSettings settings,
        Func<IObservationStageRunner>? simulationRunnerFactory = null,
        NINA.WPF.Base.Interfaces.ViewModel.IFramingAssistantVM? framingAssistant = null,
        NINA.WPF.Base.Interfaces.Mediator.IApplicationMediator? applicationMediator = null,
        NINA.Equipment.Interfaces.IPlanetariumFactory? planetariumFactory = null)
        : base(new SequentialStrategy())
    {
        this.profileService = profileService;
        this.nighttimeCalculator = nighttimeCalculator;
        this.host = host;
        this.realRunnerFactory = realRunnerFactory;
        this.settings = settings;
        this.simulationRunnerFactory = simulationRunnerFactory;
        this.framingAssistant = framingAssistant;
        this.applicationMediator = applicationMediator;
        this.planetariumFactory = planetariumFactory;
        target = CreateTarget(profileService);
        AttachTarget(target);
        NighttimeData = nighttimeCalculator.Calculate(null);
        nighttimeCalculator.OnReferenceDayChanged += OnReferenceDayChanged;
        LoadDefaults();
        SeedStageItems();
        loadingTarget = false;
        InitializeTargetEditorCommands();
    }

    private UvexTargetObservationContainer(UvexTargetObservationContainer copy)
        : base(new SequentialStrategy())
    {
        profileService = copy.profileService;
        nighttimeCalculator = copy.nighttimeCalculator;
        host = copy.host;
        settings = copy.settings;
        realRunnerFactory = copy.realRunnerFactory;
        simulationRunnerFactory = copy.simulationRunnerFactory;
        framingAssistant = copy.framingAssistant;
        applicationMediator = copy.applicationMediator;
        planetariumFactory = copy.planetariumFactory;
        target = CreateTarget(profileService);
        AttachTarget(target);
        NighttimeData = nighttimeCalculator.Calculate(null);
        nighttimeCalculator.OnReferenceDayChanged += OnReferenceDayChanged;
        CopyMetaData(copy);
        TargetName = copy.TargetName;
        CatalogId = copy.CatalogId;
        TargetObservability = copy.TargetObservability;
        CatalogMetadata = copy.CatalogMetadata;
        RightAscensionDegrees = copy.RightAscensionDegrees;
        DeclinationDegrees = copy.DeclinationDegrees;
        Target.PositionAngle = copy.Target.PositionAngle;
        Target.Expanded = copy.Target.Expanded;
        DurationMinutes = copy.DurationMinutes;
        ScienceFrames = copy.ScienceFrames;
        MaximumScienceAttempts = copy.MaximumScienceAttempts;
        FixedExposureSeconds = copy.FixedExposureSeconds;
        lastFixedExposureSeconds = copy.lastFixedExposureSeconds;
        FailurePolicy = copy.FailurePolicy;
        ForceGuideCalibration = copy.ForceGuideCalibration;
        AutomaticGuideRecovery = copy.AutomaticGuideRecovery;
        GuideStarWaitSeconds = copy.GuideStarWaitSeconds;
        NightSetupId = copy.NightSetupId;
        SiteLatitudeDegrees = copy.SiteLatitudeDegrees;
        SiteLongitudeDegreesEast = copy.SiteLongitudeDegreesEast;
        SiteElevationMeters = copy.SiteElevationMeters;
        HorizonMinimumDegrees = copy.HorizonMinimumDegrees;
        HorizonStartMarginDegrees = copy.HorizonStartMarginDegrees;
        HorizonContinueMarginDegrees = copy.HorizonContinueMarginDegrees;
        ExpectedAtrCameraId = copy.ExpectedAtrCameraId;
        ExpectedG3ProfileName = copy.ExpectedG3ProfileName;
        ExpectedQhyCameraId = copy.ExpectedQhyCameraId;
        SimulationStageMilliseconds = copy.SimulationStageMilliseconds;
        UseRealMode = copy.UseRealMode;
        Items = new ObservableCollection<ISequenceItem>(copy.Items.Select(item => (ISequenceItem)item.Clone()));
        Conditions = new ObservableCollection<ISequenceCondition>(copy.Conditions.Select(item => (ISequenceCondition)item.Clone()));
        Triggers = new ObservableCollection<ISequenceTrigger>(copy.Triggers.Select(item => (ISequenceTrigger)item.Clone()));
        AttachChildren();
        loadingTarget = false;
        InitializeTargetEditorCommands();
    }

    [JsonProperty]
    public InputTarget Target
    {
        get => target;
        set
        {
            if (ReferenceEquals(target, value) || value is null) return;
            RequireEditablePlan();
            DetachTarget(target);
            target = value;
            AttachTarget(target);
            InvalidateTargetCatalog();
            RaiseTargetPropertiesChanged();
        }
    }

    [JsonIgnore]
    public NighttimeData NighttimeData { get; private set; }

    // Kept as serialized proxies so existing .astroproj files remain readable.
    // The native InputTarget above is the single source of truth.
    [JsonProperty]
    public string TargetName
    {
        get => Target.TargetName ?? string.Empty;
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(Target.TargetName, normalized, StringComparison.Ordinal)) return;
            RequireEditablePlan();
            Target.TargetName = normalized;
            RaisePropertyChanged();
        }
    }

    [JsonProperty]
    public string CatalogId { get; set; } = string.Empty;

    [JsonProperty]
    public TargetObservabilityClass TargetObservability { get; set; }

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public TargetCatalogMetadata? CatalogMetadata { get; set; }

    [JsonIgnore]
    public IReadOnlyList<TargetObservabilityChoice> AvailableTargetObservabilityClasses =>
        ObservationDockable.TargetObservabilityChoices;

    [JsonProperty]
    public double RightAscensionDegrees
    {
        get => Target.InputCoordinates.Coordinates.Transform(Epoch.J2000).RADegrees;
        set => SetJ2000Coordinates(value, DeclinationDegrees);
    }

    [JsonProperty]
    public double DeclinationDegrees
    {
        get => Target.InputCoordinates.Coordinates.Transform(Epoch.J2000).Dec;
        set => SetJ2000Coordinates(RightAscensionDegrees, value);
    }

    [JsonProperty]
    public double DurationMinutes { get => durationMinutes; set => SetPlanValue(ref durationMinutes, value); }

    [JsonProperty] public int ScienceFrames { get => scienceFrames; set => SetPlanValue(ref scienceFrames, value); }
    [JsonProperty] public int MaximumScienceAttempts { get => maximumScienceAttempts; set => SetPlanValue(ref maximumScienceAttempts, value); }
    // Keep the existing serialized contract: zero means the shared probe ladder.
    // The UI mode is a view of it, not a second independently saved policy.
    [JsonProperty]
    public double FixedExposureSeconds
    {
        get => fixedExposureSeconds;
        set
        {
            SetPlanValue(ref fixedExposureSeconds, value);
            if (double.IsFinite(value) && value is > 0 and <= 3600) lastFixedExposureSeconds = value;
            RaisePropertyChanged(nameof(ExposureMode));
            RaisePropertyChanged(nameof(IsFixedExposure));
            RaisePropertyChanged(nameof(ExposureModeDescription));
        }
    }
    [JsonIgnore]
    public SpectroscopyExposureMode ExposureMode
    {
        get => FixedExposureSeconds == 0 ? SpectroscopyExposureMode.Automatic : SpectroscopyExposureMode.Fixed;
        set
        {
            RequireEditablePlan();
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (value == ExposureMode) return;
            FixedExposureSeconds = value == SpectroscopyExposureMode.Automatic ? 0 : lastFixedExposureSeconds;
        }
    }
    [JsonIgnore] public bool IsFixedExposure => ExposureMode == SpectroscopyExposureMode.Fixed;
    [JsonIgnore] public IReadOnlyList<SpectroscopyExposureModeChoice> AvailableExposureModes => SpectroscopyExposureModeChoice.Choices;
    [JsonIgnore] public string ExposureModeDescription => UiText(IsFixedExposure
        ? "固定：按指定秒数试拍与采集；仍保留饱和、信号及安全检查。"
        : "自动：先试拍，沿用观测计划中的曝光阶梯选档；不修改其他目标或全局设置。");
    [JsonProperty] public SpectroscopyTargetFailurePolicy FailurePolicy { get => failurePolicy; set => SetPlanValue(ref failurePolicy, value); }
    [JsonProperty] public bool ForceGuideCalibration { get => forceGuideCalibration; set => SetPlanValue(ref forceGuideCalibration, value); }
    [JsonProperty] public bool AutomaticGuideRecovery { get => automaticGuideRecovery; set => SetPlanValue(ref automaticGuideRecovery, value); }
    [JsonProperty] public int GuideStarWaitSeconds { get => guideStarWaitSeconds; set => SetPlanValue(ref guideStarWaitSeconds, value); }
    [JsonIgnore] public bool CanEditPlan => targetLifetime is null && Parent is not UvexNightSequenceContainer { IsActive: true };
    [JsonIgnore] public string TargetSummary => $"{TargetName} · {ScienceFrames} × {(FixedExposureSeconds > 0 ? $"{FixedExposureSeconds:0.###} s" : "AUTO")}";
    [JsonIgnore] public IReadOnlyList<SpectroscopyFailurePolicyChoice> AvailableFailurePolicies => SpectroscopyFailurePolicyChoice.Choices;
    private string lastOutcome = "未执行";
    [JsonIgnore] public string LastOutcome { get => lastOutcome; private set { lastOutcome = value; RaisePropertyChanged(nameof(LastOutcomeLabel)); } }
    [JsonIgnore] public string LastOutcomeLabel => LastOutcome == "未执行" ? UiText("未执行") : LastOutcome;
    private static string UiText(string value) => ObservationStaticTextLocalization.Translate(value, ObservationStaticTextLocalization.EffectiveCulture);

    private NativeSequencePlan TargetSequencePlan() => new(ScienceFrames, MaximumScienceAttempts,
        FixedExposureSeconds, LatitudeDegrees: SiteLatitudeDegrees, LongitudeDegrees: SiteLongitudeDegreesEast,
        ForceGuideCalibration: ForceGuideCalibration, AutomaticGuideRecovery: AutomaticGuideRecovery,
        GuideStarWaitSeconds: GuideStarWaitSeconds);

    private void RequireEditablePlan()
    {
        if (!loadingTarget && !CanEditPlan) throw new InvalidOperationException("观测运行中不可修改目标；请先停止整个序列。");
    }

    private void SetPlanValue<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        RequireEditablePlan();
        field = value;
        RaisePropertyChanged(property);
        RefreshTargetSummary();
    }

    private void RefreshTargetSummary()
    {
        RaisePropertyChanged(nameof(TargetSummary));
        if (loadingTarget) return;
        // Refresh generator-owned captions; leave an operator's custom label alone.
        var prefix = System.Text.RegularExpressions.Regex.Match(Name ?? "", @"^\d+\s*·\s*");
        if (prefix.Success) Name = prefix.Value + TargetSummary;
    }

    private void InvalidateTargetCatalog()
    {
        if (!loadingTarget)
        {
            CatalogMetadata = null;
            CatalogId = string.Empty;
            RaisePropertyChanged(nameof(CatalogMetadata));
            RaisePropertyChanged(nameof(CatalogId));
        }
        RefreshTargetSummary();
    }

    internal void RefreshPlanEditability()
    {
        Interlocked.Increment(ref targetEditorGeneration);
        RefreshTargetEditorCommands();
    }

    [JsonProperty]
    public string NightSetupId { get; set; } = string.Empty;

    [JsonProperty]
    public double SiteLatitudeDegrees { get; set; }

    [JsonProperty]
    public double SiteLongitudeDegreesEast { get; set; }

    [JsonProperty]
    public double SiteElevationMeters { get; set; }

    [JsonProperty]
    public double HorizonMinimumDegrees { get; set; }

    [JsonProperty]
    public double HorizonStartMarginDegrees { get; set; }

    [JsonProperty]
    public double HorizonContinueMarginDegrees { get; set; }

    [JsonProperty]
    public string ExpectedAtrCameraId { get; set; } = string.Empty;

    [JsonProperty]
    public string ExpectedG3ProfileName { get; set; } = string.Empty;

    [JsonProperty]
    public string ExpectedQhyCameraId { get; set; } = string.Empty;

    [JsonProperty]
    public int SimulationStageMilliseconds { get; set; }

    [JsonProperty]
    public bool UseRealMode
    {
        get => useRealMode;
        set
        {
            if (useRealMode == value) return;
            RequireEditablePlan();
            useRealMode = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(ExecutionModeLabel));
            RaisePropertyChanged(nameof(ExecutionModeWarning));
        }
    }

    public string ExecutionModeLabel => UiText(UseRealMode
        ? "REAL · 将控制真实设备"
        : "SIMULATOR · 不接触硬件");

    public string ExecutionModeWarning => UiText(UseRealMode
        ? "执行时仍须当前 Profile 明确启用真实模式；历史授权不会自动复用。"
        : "此序列固定运行全流程模拟。");

    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        progress = ObservationStatusProgress.Wrap(progress);
        // The coordinator finishes each acquisition segment before a flip.
        // Keep a target-wide token so native Interrupt also stops that handoff.
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (Interlocked.CompareExchange(ref targetLifetime, lifetime, null) is not null)
            throw new InvalidOperationException("该光谱目标已有执行中的任务。");
        parentExecutionToken = token;
        operatorSkipRequested = 0;
        endedByNativeCondition = false;
        var joined = targetJoined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RefreshPlanEditability();
        try { await ExecuteOwnedAsync(progress, lifetime.Token).ConfigureAwait(false); }
        finally
        {
            Interlocked.CompareExchange(ref targetLifetime, null, lifetime);
            joined.TrySetResult();
            RefreshPlanEditability();
        }
    }

    private async Task ExecuteOwnedAsync(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        var night = Parent as UvexNightSequenceContainer;
        if (night is { IsActive: false }) throw new InvalidOperationException("整夜容器没有取得执行权。");
        if (night?.StopFollowingTargets == true)
        {
            LastOutcome = "未执行：本夜已停止。";
            RaisePropertyChanged(nameof(LastOutcome));
            return;
        }
        if (night is not null && night.ApplyNight(TargetSequencePlan()).StopGateNow() is null)
        {
            token.ThrowIfCancellationRequested();
            NinaInstancePolicy.RequireMaster(settings);
            var authorization = ObservationAutomationPolicy.AuthorizeExecutionMode(
                UseRealMode, settings.ObservationUseRealMode, settings.RealModeCommissioned);
            if (authorization.Disposition != GateDisposition.Passed)
                throw new InvalidOperationException($"{authorization.Code}: {authorization.Message}");
            var configuration = UseRealMode ? realRunnerFactory.CaptureConfiguration(settings) : null;
            if (configuration is not null) configuration = night.CheckConfiguration(configuration);
            var candidate = BuildPlan(configuration);
            if (NativeSequencePlan.PreflightHorizonSkip(candidate, FailurePolicy) is { } horizon)
            {
                await night.RecordPreflightHorizonSkipAsync(candidate, horizon, token).ConfigureAwait(false);
                LastOutcome = $"未观测：高度窗口不足，已跳过（{horizon.Code}）。";
                RaisePropertyChanged(nameof(LastOutcome));
                Report(progress, LastOutcome);
                return;
            }
        }
        night?.BeginTarget(TargetName);
        NativeTargetSession? session = null;
        try
        {
            var scopeIssues = SpectroscopyMeridianFlipTrigger.ScopeIssues(this).ToArray();
            if (scopeIssues.Length != 0) throw new InvalidOperationException(string.Join(" ", scopeIssues));
            var scope = SpectroscopyMeridianFlipTrigger.InScope(this).ToArray();
            var flip = scope.OfType<SpectroscopyMeridianFlipTrigger>().SingleOrDefault();
            var flipConfiguration = flip?.ConfigurationKey;
            if (flip is not null && !flip.Validate()) throw new InvalidOperationException(string.Join(" ", flip.Issues));
            var nativePlan = night?.ApplyNight(TargetSequencePlan()) ?? TargetSequencePlan();
            var realMode = UseRealMode;
            void VerifyScope()
            {
                if (UseRealMode != realMode || flip?.ConfigurationKey != flipConfiguration ||
                    !scope.SequenceEqual(SpectroscopyMeridianFlipTrigger.InScope(this)))
                    throw new InvalidOperationException("目标的模式或翻转触发器作用域已改变；不继续启动曝光。");
            }
            NativeMeridianSession? meridian = realMode && flip is not null ? new(nativePlan, seconds =>
            {
                VerifyScope();
                return flip.IsDue(seconds);
            }) { VerifyScope = VerifyScope } : null;
            session = meridian ?? (night is not null ? new NativeTargetSession(nativePlan) : null);
            // The second segment is a new production runner with no old optical
            // state. The night retains the exclusive lease throughout the flip.
            while (await ExecuteTargetAsync(progress, token, night, session, meridian, flip, flipConfiguration).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                foreach (var item in Items) item.ResetProgress();
            }
            LastOutcome = "完成";
        }
        catch when (night is { HasRecordedCurrentTarget: true } && host.Dashboard.Run.State is ObservationRunState.Faulted or ObservationRunState.Cancelled)
        {
            // The original manifest/gate and stop readbacks have already been
            // recorded by the night owner. Native child sequencing may proceed
            // only when that owner explicitly accepted the quality skip.
            LastOutcome = night.StopFollowingTargets ? "已停止：查看原始质量门与本夜记录。" :
                Volatile.Read(ref operatorSkipRequested) != 0 ?
                    endedByNativeCondition ? "原生条件已结束本目标；已确认停止，保留已有帧。" : "操作员已跳过本目标；已确认停止，保留已有帧。" :
                "本目标未完成，已确认退出并跳过；查看本夜处理记录。";
            if (session is { Accepted: > 0 })
                LastOutcome += $" 已保留 {session.Accepted}/{session.Original.ScienceFrames} 张，累计 {session.AcceptedSeconds:0.##} 秒；剩余未完成。";
        }
        finally
        {
            if (Volatile.Read(ref operatorSkipRequested) != 0)
                foreach (var item in Items.Where(x => x.Status is SequenceEntityStatus.CREATED or SequenceEntityStatus.RUNNING)) item.Skip();
            RaisePropertyChanged(nameof(LastOutcome));
        }
    }

    private async Task<bool> ExecuteTargetAsync(IProgress<ApplicationStatus> progress, CancellationToken token,
        UvexNightSequenceContainer? night, NativeTargetSession? session = null, NativeMeridianSession? meridian = null,
        SpectroscopyMeridianFlipTrigger? flip = null, string? flipConfiguration = null)
    {
        NinaInstancePolicy.RequireMaster(settings);
        var authorization = ObservationAutomationPolicy.AuthorizeExecutionMode(
            UseRealMode,
            settings.ObservationUseRealMode,
            settings.RealModeCommissioned);
        if (authorization.Disposition != GateDisposition.Passed)
        {
            throw new InvalidOperationException($"{authorization.Code}: {authorization.Message}");
        }
        Report(progress, UseRealMode
            ? "启动 UVEX Target Observation 真实自动流程"
            : "启动 UVEX Target Observation 全流程模拟");
        string? activeRunId = null;
        EventHandler<ObservationDashboardSnapshot> dashboardHandler = (_, dashboard) =>
        {
            // Dashboard reset can briefly publish the previous target's terminal
            // snapshot before the new coordinator starts. Never abort this bridge
            // because its predecessor was a (correctly skipped) failure.
            if (dashboard.Run.ObservationRunId != activeRunId) return;
            ApplyStageStatuses(dashboard);
            if (dashboard.Run.State is ObservationRunState.Cancelled or ObservationRunState.Faulted)
            {
                activeBridge?.Abort(new OperationCanceledException(
                    $"UVEX observation ended in {dashboard.Run.State}: {dashboard.Run.StatusMessage}"));
            }
        };
        RealObservationStageRunner? realRunner = null;
        RealRunConfiguration? lockedConfiguration = null;
        var sequencePlan = session?.RemainingPlan() ?? night?.ApplyNight(TargetSequencePlan()) ?? TargetSequencePlan();
        if (UseRealMode)
        {
            lockedConfiguration = realRunnerFactory.CaptureConfiguration(settings);
            if (night is not null) lockedConfiguration = night.CheckConfiguration(lockedConfiguration);
            // Remaining frames/attempts change the segment hash, not the locked
            // shared setup to which the one-use clock receipt is bound.
            if (session is not null)
                session.SharedActionConfigurationSha256 = lockedConfiguration.ActionConfigurationSha256;
            lockedConfiguration = lockedConfiguration.WithSequencePlan(sequencePlan);
        }
        var plan = session?.TargetPlan is { } originalTarget
            ? originalTarget with { ObservationRunId = Guid.NewGuid().ToString("N"), PlannedStartUtc = DateTimeOffset.UtcNow }
            : BuildPlan(lockedConfiguration);
        activeRunId = plan.ObservationRunId;
        if (session is not null) session.TargetPlan ??= plan;
        if (session?.HasBudgetedContinuation == true && sequencePlan.TargetDeadlineUtc is { } targetEnd)
            plan = plan with { PlannedDuration = targetEnd > plan.PlannedStartUtc ? targetEnd - plan.PlannedStartUtc : TimeSpan.FromSeconds(1) };
        var originalPlan = session?.TargetPlan ?? plan;
        var originalTargetEnd = originalPlan.PlannedStartUtc + originalPlan.PlannedDuration;
        if (sequencePlan.TargetDeadlineUtc is null || originalTargetEnd < sequencePlan.TargetDeadlineUtc)
            sequencePlan = sequencePlan with { TargetDeadlineUtc = originalTargetEnd };
        lockedConfiguration = lockedConfiguration?.WithSequencePlan(sequencePlan);
        IObservationStageRunner inner = UseRealMode
            ? realRunner = realRunnerFactory.Create(host, settings, progress, lockedConfiguration)
            : simulationRunnerFactory?.Invoke() ?? new SimulatedObservationStageRunner(host, Math.Clamp(SimulationStageMilliseconds, 250, 30_000), progress);
        var boundaryRunner = inner as INativeTargetBoundary;
        boundaryRunner?.AttachNativeTarget(session);
        using var bridge = new SequencerStageBridge(inner)
        { EndRunOnGateFailure = night is not null, BoundaryGate = sequencePlan.StopGateNow };
        activeBridge = bridge;
        host.DashboardChanged += dashboardHandler;
        ApplyStageStatuses(host.Dashboard);
        var coordinatorRun = host.RunAsync(plan, bridge, token, night);
        var continueAfterFlip = false;
        var continueAfterRecovery = false;
        string? boundaryFailure = null;
        try
        {
            // This is deliberately base.Execute: N.I.N.A.'s SequentialStrategy,
            // Conditions, Triggers and child statuses remain part of execution.
            var ninaSequenceRun = base.Execute(progress, token);
            var first = await Task.WhenAny(ninaSequenceRun, coordinatorRun).ConfigureAwait(false);
            if (ReferenceEquals(first, coordinatorRun) && !ninaSequenceRun.IsCompleted)
            {
                // Cancel/Fault can complete the coordinator while the current
                // marker is waiting for a post-pause retry. Completing the bridge
                // releases N.I.N.A.'s SequentialStrategy instead of deadlocking.
                bridge.Abort(new OperationCanceledException(
                    $"UVEX coordinator ended in {host.Dashboard.Run.State}: {host.Dashboard.Run.StatusMessage}"));
            }
            await ninaSequenceRun.ConfigureAwait(false);
            if (!bridge.HasCompletedFinalMarker && !bridge.HasTerminalGateResult && !coordinatorRun.IsCompleted)
            {
                // A native condition may end its block without executing every
                // marker. Join the coordinator; never leave it waiting forever.
                endedByNativeCondition = true;
                RequestTargetSkip();
            }
            await coordinatorRun.ConfigureAwait(false);
            if (session is not null && boundaryRunner is not null)
                boundaryRunner.RecordMeridianSegment(session, plan.ObservationRunId);
            if (meridian is not null && realRunner is not null && host.Dashboard.Run.State == ObservationRunState.Completed)
            {
                if (meridian.Requested)
                {
                    LastOutcome = $"中天翻转中 · 已接受 {meridian.Accepted}/{meridian.Original.ScienceFrames} 张，保留已用尝试 {meridian.Attempts} 次";
                    RaisePropertyChanged(nameof(LastOutcome));
                    try
                    {
                        var remainingClockBudget = realRunner.CaptureClockContinuationBudget();
                        await realRunner.PerformNativeMeridianFlipAsync(flip!, meridian, flipConfiguration!, token).ConfigureAwait(false);
                        session!.RetainClockBudget(remainingClockBudget);
                        continueAfterFlip = true;
                    }
                    catch (Exception ex)
                    {
                        boundaryFailure = $"NATIVE_MERIDIAN_HANDOFF_FAILED: {ex.Message}";
                        LastOutcome = boundaryFailure;
                        RaisePropertyChanged(nameof(LastOutcome));
                        throw;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Native local interruption is intent, not an execution fault. The
            // independent stop verifier below still decides whether to continue.
            if (ex is not OperationCanceledException)
                boundaryFailure ??= $"NATIVE_TARGET_EXECUTION_FAILED: {ex.Message}";
            bridge.Abort(ex);
            host.Cancel();
            try { await coordinatorRun.ConfigureAwait(false); } catch { }
            throw;
        }
        finally
        {
            // SequenceItem.Skip() is non-virtual. Its local token is cancelled,
            // but the containing night remains live; distinguish it from root abort.
            if (Status == SequenceEntityStatus.SKIPPED && night is { StopFollowingTargets: false })
                Interlocked.Exchange(ref operatorSkipRequested, 1);
            activeBridge = null;
            host.DashboardChanged -= dashboardHandler;
            ApplyStageStatuses(host.Dashboard);
            try
            {
                if (night is not null && !continueAfterFlip) continueAfterRecovery = await night.RecordTargetAsync(TargetName, host.Dashboard, boundaryRunner,
                    FailurePolicy == SpectroscopyTargetFailurePolicy.SkipQualityFailure, boundaryFailure, plan, session,
                    Volatile.Read(ref operatorSkipRequested) != 0 &&
                    (!parentExecutionToken.IsCancellationRequested ||
                        Status == SequenceEntityStatus.SKIPPED && !night.StopFollowingTargets), endedByNativeCondition).ConfigureAwait(false);
            }
            finally { if (realRunner is not null) await realRunner.DisposeAsync().ConfigureAwait(false); }
        }
        var final = host.Dashboard.Run;
        if (continueAfterRecovery)
        {
            LastOutcome = $"边界恢复已验证 · 已接受 {session!.Accepted}/{session.Original.ScienceFrames} 张；按原剩余预算重新定位、入缝和导星。";
            RaisePropertyChanged(nameof(LastOutcome));
            Report(progress, LastOutcome);
            return true;
        }
        if (final.State != ObservationRunState.Completed)
        {
            throw new InvalidOperationException($"UVEX Target Observation ended in {final.State}: {final.StatusMessage}");
        }
        Report(progress, continueAfterFlip ? "翻转完成；重新执行定位/入缝并续拍剩余科学帧" :
            UseRealMode ? "UVEX Target Observation 真实流程完成" : "UVEX Target Observation 模拟完成", continueAfterFlip ? 0 : 1);
        return continueAfterFlip;
    }

    private void RequestTargetSkip()
    {
        if (targetLifetime is null) return;
        Interlocked.Exchange(ref operatorSkipRequested, 1);
        try { Volatile.Read(ref targetLifetime)?.Cancel(); }
        catch (ObjectDisposedException) { /* The same target just completed. */ }
        activeBridge?.Abort(new OperationCanceledException("N.I.N.A. interrupted UVEX Target Observation."));
        host.Cancel();
        RefreshPlanEditability();
    }

    public override Task Interrupt()
    {
        if (targetLifetime is null) return Task.CompletedTask;
        var joined = targetJoined?.Task ?? Task.CompletedTask;
        RequestTargetSkip();
        return joined;
    }

    public override bool Validate()
    {
        if (realRunnerFactory is { } nativeFactory) nativeFactory.ObserveNativeSequenceLifetime();
        var childrenValid = base.Validate();
        Issues.Clear();
        foreach (var issue in SpectroscopyMeridianFlipTrigger.ScopeIssues(this)) Issues.Add(issue);
        foreach (var issue in BuildPlan().Validate()) Issues.Add(issue);
        foreach (var issue in TargetSequencePlan().Validate()) Issues.Add(issue);
        if (SimulationStageMilliseconds is < 250 or > 30_000)
        {
            Issues.Add("Simulation stage duration must be between 250 and 30000 milliseconds.");
        }
        var expectedStages = ObservationRunCoordinator.Stages;
        if (Items.Count != expectedStages.Count || Items.Where((item, index) =>
                item is not ObservationStageMarkerItem marker || marker.Stage != expectedStages[index]).Any())
        {
            Issues.Add("UVEX Target Observation must contain exactly one ordered child item for every canonical stage.");
        }
        var authorization = ObservationAutomationPolicy.AuthorizeExecutionMode(
            UseRealMode,
            settings.ObservationUseRealMode || SequenceStartupScope.HasRealPreparation(this),
            settings.RealModeCommissioned);
        if (authorization.Disposition != GateDisposition.Passed)
        {
            Issues.Add(UseRealMode && !settings.RealModeCommissioned
                ? host.CommissioningFailureMessage(profileService.ActiveProfile.Id)
                : $"{authorization.Code}: {authorization.Message}");
        }
        if (UseRealMode)
        {
            foreach (var issue in NinaImageFilePatternPolicy.Assess(
                         profileService.ActiveProfile.ImageFileSettings.FilePattern).BlockingIssues)
            {
                Issues.Add(issue);
            }
            var capabilities = ObservationAutomationPolicy.ValidateFullAutomationCapabilities(
                settings.RequireSafetyMonitor,
                settings.RequireOpenDomeOrRoof,
                settings.RequireWeatherData,
                settings.RequireOpenOpticalCover,
                settings.WeakSupervisionEnabled,
                realRunnerFactory.CaptureConfiguration(settings).Environment.SafetyMonitorOwnsWeather);
            if (capabilities.Disposition != GateDisposition.Passed)
            {
                Issues.Add($"{capabilities.Code}: {capabilities.Message}");
            }
        }
        return childrenValid && Issues.Count == 0;
    }

    public override object Clone() => new UvexTargetObservationContainer(this);

    private ObservationPlan BuildPlan(RealRunConfiguration? lockedConfiguration = null)
    {
        var binding = lockedConfiguration?.Commissioning;
        var motion = binding is null
            ? new MotionLimits(
                settings.MaximumSingleCorrectionArcseconds / 3600d,
                settings.MaximumCumulativeCorrectionArcseconds / 3600d,
                settings.MaximumCorrectionAttempts,
                TimeSpan.FromMinutes(settings.MaximumAcquisitionMinutes))
            : new MotionLimits(
                binding.MaximumSingleCorrectionArcseconds / 3600d,
                binding.MaximumCumulativeCorrectionArcseconds / 3600d,
                binding.MaximumCorrectionAttempts,
                TimeSpan.FromMinutes(binding.MaximumAcquisitionMinutes));
        return ObservationPlanFactory.Create(
            TargetName,
            CatalogId,
            RightAscensionDegrees,
            DeclinationDegrees,
            DurationMinutes,
            NightSetupId,
            SiteLatitudeDegrees,
            SiteLongitudeDegreesEast,
            SiteElevationMeters,
            HorizonMinimumDegrees,
            HorizonStartMarginDegrees,
            HorizonContinueMarginDegrees,
            ExpectedAtrCameraId,
            ExpectedG3ProfileName,
            ExpectedQhyCameraId,
            motion,
            lockedConfiguration?.Environment.RequireSafetyMonitor ?? settings.RequireSafetyMonitor,
            TargetObservability,
            CatalogMetadata);
    }

    private void LoadDefaults()
    {
        TargetName = settings.ObservationTargetName;
        CatalogId = settings.ObservationCatalogId;
        TargetObservability = settings.ObservationTargetObservability;
        CatalogMetadata = settings.ObservationTargetCatalogMetadata;
        RightAscensionDegrees = settings.ObservationRightAscensionDegrees;
        DeclinationDegrees = settings.ObservationDeclinationDegrees;
        DurationMinutes = settings.ObservationDurationMinutes;
        ScienceFrames = settings.AtrScienceFrameCount;
        MaximumScienceAttempts = settings.AtrScienceMaximumAttempts;
        NightSetupId = settings.ObservationNightSetupId;
        SiteLatitudeDegrees = settings.ObservatoryLatitudeDegrees;
        SiteLongitudeDegreesEast = settings.ObservatoryLongitudeDegreesEast;
        SiteElevationMeters = settings.ObservatoryElevationMeters;
        HorizonMinimumDegrees = settings.HorizonMinimumDegrees;
        HorizonStartMarginDegrees = settings.HorizonStartMarginDegrees;
        HorizonContinueMarginDegrees = settings.HorizonContinueMarginDegrees;
        ExpectedAtrCameraId = settings.ObservationExpectedAtrCameraId;
        ExpectedG3ProfileName = settings.ObservationExpectedG3ProfileName;
        ExpectedQhyCameraId = settings.ObservationExpectedQhyCameraId;
        SimulationStageMilliseconds = settings.ObservationSimulationStageMilliseconds;
        UseRealMode = settings.ObservationUseRealMode;
    }

    private static InputTarget CreateTarget(IProfileService profileService)
    {
        var astrometry = profileService.ActiveProfile.AstrometrySettings;
        var created = new InputTarget(
            Angle.ByDegree(astrometry.Latitude),
            Angle.ByDegree(astrometry.Longitude),
            astrometry.Horizon)
        {
            TargetName = string.Empty,
            InputCoordinates = new InputCoordinates(
                new Coordinates(0, 0, Epoch.J2000, Coordinates.RAType.Degrees)),
        };
        return created;
    }

    private void SetJ2000Coordinates(double rightAscensionDegrees, double declinationDegrees)
    {
        RequireEditablePlan();
        if (!double.IsFinite(rightAscensionDegrees) || rightAscensionDegrees is < 0 or >= 360 ||
            !double.IsFinite(declinationDegrees) || declinationDegrees is < -90 or > 90)
            throw new ArgumentOutOfRangeException(nameof(rightAscensionDegrees), "J2000 坐标须为 RA 0–360°（不含 360），Dec −90–90°。");
        var current = Target.InputCoordinates.Coordinates.Transform(Epoch.J2000);
        if (Math.Abs(current.RADegrees - rightAscensionDegrees) < 1e-12 &&
            Math.Abs(current.Dec - declinationDegrees) < 1e-12)
        {
            return;
        }
        Target.InputCoordinates = new InputCoordinates(
            new Coordinates(rightAscensionDegrees, declinationDegrees, Epoch.J2000, Coordinates.RAType.Degrees));
        InvalidateTargetCatalog();
        RaisePropertyChanged(nameof(RightAscensionDegrees));
        RaisePropertyChanged(nameof(DeclinationDegrees));
    }

    private void AttachTarget(InputTarget value)
    {
        value.CoordinatesChanged += OnTargetCoordinatesChanged;
        value.PropertyChanged += OnTargetPropertyChanged;
    }

    private void DetachTarget(InputTarget value)
    {
        value.CoordinatesChanged -= OnTargetCoordinatesChanged;
        value.PropertyChanged -= OnTargetPropertyChanged;
    }

    private void OnTargetCoordinatesChanged(object? sender, EventArgs e)
    {
        InvalidateTargetCatalog();
        RaisePropertyChanged(nameof(RightAscensionDegrees));
        RaisePropertyChanged(nameof(DeclinationDegrees));
    }

    private void OnTargetPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(InputTarget.TargetName), StringComparison.Ordinal))
        {
            InvalidateTargetCatalog();
            RaisePropertyChanged(nameof(TargetName));
        }
    }

    private void RaiseTargetPropertiesChanged()
    {
        RaisePropertyChanged(nameof(Target));
        RaisePropertyChanged(nameof(TargetName));
        RaisePropertyChanged(nameof(RightAscensionDegrees));
        RaisePropertyChanged(nameof(DeclinationDegrees));
        RefreshTargetSummary();
    }

    private void OnReferenceDayChanged(object? sender, EventArgs e)
    {
        NighttimeData = nighttimeCalculator.Calculate(null);
        RaisePropertyChanged(nameof(NighttimeData));
    }

    private void SeedStageItems()
    {
        foreach (var stage in ObservationRunCoordinator.Stages)
        {
            Add(new ObservationStageMarkerItem { Stage = stage });
        }
    }

    private void AttachChildren()
    {
        foreach (var item in Items) item.AttachNewParent(this);
        foreach (var condition in Conditions) condition.AttachNewParent(this);
        foreach (var trigger in Triggers) trigger.AttachNewParent(this);
    }

    [System.Runtime.Serialization.OnDeserializing]
    private void ClearFactoryDefaultsForTemplate(System.Runtime.Serialization.StreamingContext context)
    {
        loadingTarget = true;
        fixedExposureSeconds = 0;
        lastFixedExposureSeconds = 120;
        // An old template must not inherit the MEF prototype's checked switch.
        forceGuideCalibration = false;
        automaticGuideRecovery = true;
        guideStarWaitSeconds = 300;
        failurePolicy = SpectroscopyTargetFailurePolicy.SkipQualityFailure;
        // N.I.N.A. clones a populated MEF prototype before Json.NET Populate.
        // Without this, each template load appends another eleven markers.
        Items.Clear(); Conditions.Clear(); Triggers.Clear();
        // Populate must never mutate the immutable record shared by MEF clones,
        // or inherit a different target's optional coordinate provenance.
        CatalogMetadata = null;
    }

    [System.Runtime.Serialization.OnDeserialized]
    private void ReattachTemplateChildren(System.Runtime.Serialization.StreamingContext context)
    {
        AttachChildren();
        loadingTarget = false;
        RefreshTargetSummary();
    }

    private void ApplyStageStatuses(ObservationDashboardSnapshot dashboard)
    {
        // SequentialStrategy selects CREATED children. Publishing RUNNING on
        // a not-yet-dispatched marker races that selector and skips a stage.
        // While bridged, native Run alone owns the executable child statuses.
        if (activeBridge is not null) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(() => ApplyStageStatuses(dashboard));
            return;
        }

        var run = dashboard.Run;
        for (var index = 0; index < Items.Count; index++)
        {
            if (Items[index] is not ObservationStageMarkerItem marker) continue;
            marker.Status = index < run.CompletedStageCount
                ? SequenceEntityStatus.FINISHED
                : run.State == ObservationRunState.Completed
                    ? SequenceEntityStatus.FINISHED
                    : run.State == ObservationRunState.Faulted && run.CurrentStage == marker.Stage
                        ? SequenceEntityStatus.FAILED
                        : run.CurrentStage == marker.Stage && run.State is not ObservationRunState.Idle
                            ? SequenceEntityStatus.RUNNING
                            : SequenceEntityStatus.CREATED;
        }
    }

    private static void Report(IProgress<ApplicationStatus> progress, string message, double value = -1) =>
        progress.Report(new ApplicationStatus { Source = "OpenAstroSpec Auto", Status = message, Progress = value });

    internal async Task ExecuteStageMarkerAsync(
        ObservationStage stage,
        IProgress<ApplicationStatus> progress,
        CancellationToken token)
    {
        try
        {
            await (activeBridge?.ExecuteMarkerAsync(stage, progress, token)
                ?? throw new InvalidOperationException("OpenAstroSpec stage item is not attached to an active coordinator bridge.")).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested &&
            targetLifetime is { IsCancellationRequested: false } &&
            Items.OfType<ObservationStageMarkerItem>().Any(x => x.Stage == stage && x.Status == SequenceEntityStatus.SKIPPED))
        {
            // Native leaf Skip cancels only its own token. Never advance a
            // dependent optical stage or promote it to whole-night cancellation.
            // Native SkipCurrentRunningItems enumerates a locked live list.
            // Cancellation can unwind inline and remove this leaf reentrantly;
            // yield before returning so native removal happens after enumeration.
            await Task.Yield();
            RequestTargetSkip();
            throw;
        }
    }
}

[ExportMetadata("Name", "OpenAstroSpec 自动观测阶段（内部）")]
[ExportMetadata("Description", "OpenAstroSpec · UVEX4 目标观测容器中的可视阶段标记")]
[ExportMetadata("Category", "OpenAstroSpec Auto / 内部")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
[SupportedOSPlatform("windows")]
public sealed class ObservationStageMarkerItem : SequenceItem, IValidatable
{
    public ObservationStageMarkerItem()
    {
    }

    private ObservationStageMarkerItem(ObservationStageMarkerItem copy)
    {
        CopyMetaData(copy);
        Stage = copy.Stage;
    }

    [JsonProperty]
    public ObservationStage Stage { get; set; }

    [JsonIgnore]
    public string DisplayName => SimulatedObservationStageRunner.StageDisplayName(Stage);

    public IList<string> Issues { get; } = new ObservableCollection<string>();

    public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        if (Parent is not UvexTargetObservationContainer container)
        {
            throw new InvalidOperationException("This internal stage marker must run inside UVEX Target Observation.");
        }
        return container.ExecuteStageMarkerAsync(Stage, progress, token);
    }

    public bool Validate()
    {
        Issues.Clear();
        if (Parent is not UvexTargetObservationContainer)
        {
            Issues.Add("The stage marker is only valid inside UVEX Target Observation.");
        }
        return Issues.Count == 0;
    }

    public override object Clone() => new ObservationStageMarkerItem(this);
}
