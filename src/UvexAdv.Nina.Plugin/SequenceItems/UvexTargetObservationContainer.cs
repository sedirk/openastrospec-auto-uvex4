using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Runtime.Versioning;
using System.Windows;
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
public sealed class UvexTargetObservationContainer : SequenceContainer, IImmutableContainer
{
    private readonly ObservationCoordinatorHost host;
    private readonly UvexPluginSettings settings;
    private readonly RealObservationStageRunnerFactory realRunnerFactory;
    private SequencerStageBridge? activeBridge;
    private bool useRealMode;

    [ImportingConstructor]
    public UvexTargetObservationContainer(
        IProfileService profileService,
        ObservationCoordinatorHost host,
        RealObservationStageRunnerFactory realRunnerFactory)
        : base(new SequentialStrategy())
    {
        this.host = host;
        this.realRunnerFactory = realRunnerFactory;
        settings = new UvexPluginSettings(profileService);
        LoadDefaults();
        SeedStageItems();
    }

    private UvexTargetObservationContainer(UvexTargetObservationContainer copy)
        : base(new SequentialStrategy())
    {
        host = copy.host;
        settings = copy.settings;
        realRunnerFactory = copy.realRunnerFactory;
        CopyMetaData(copy);
        TargetName = copy.TargetName;
        CatalogId = copy.CatalogId;
        RightAscensionDegrees = copy.RightAscensionDegrees;
        DeclinationDegrees = copy.DeclinationDegrees;
        DurationMinutes = copy.DurationMinutes;
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
    }

    [JsonProperty]
    public string TargetName { get; set; } = string.Empty;

    [JsonProperty]
    public string CatalogId { get; set; } = string.Empty;

    [JsonProperty]
    public double RightAscensionDegrees { get; set; }

    [JsonProperty]
    public double DeclinationDegrees { get; set; }

    [JsonProperty]
    public double DurationMinutes { get; set; }

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
            useRealMode = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(ExecutionModeLabel));
            RaisePropertyChanged(nameof(ExecutionModeWarning));
        }
    }

    public string ExecutionModeLabel => UseRealMode
        ? "REAL · 将控制真实设备"
        : "SIMULATOR · 不接触硬件";

    public string ExecutionModeWarning => UseRealMode
        ? "执行时仍须当前 Profile 明确启用真实模式；历史授权不会自动复用。"
        : "此序列固定运行全流程模拟。";

    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
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
        EventHandler<ObservationDashboardSnapshot> dashboardHandler = (_, dashboard) =>
        {
            ApplyStageStatuses(dashboard);
            if (dashboard.Run.State is ObservationRunState.Cancelled or ObservationRunState.Faulted)
            {
                activeBridge?.Abort(new OperationCanceledException(
                    $"UVEX observation ended in {dashboard.Run.State}: {dashboard.Run.StatusMessage}"));
            }
        };
        host.DashboardChanged += dashboardHandler;
        ApplyStageStatuses(host.Dashboard);
        RealObservationStageRunner? realRunner = null;
        RealRunConfiguration? lockedConfiguration = null;
        if (UseRealMode) lockedConfiguration = realRunnerFactory.CaptureConfiguration(settings);
        var plan = BuildPlan(lockedConfiguration);
        IObservationStageRunner inner = UseRealMode
            ? realRunner = realRunnerFactory.Create(host, settings, progress, lockedConfiguration)
            : new SimulatedObservationStageRunner(host, Math.Clamp(SimulationStageMilliseconds, 250, 30_000), progress);
        using var bridge = new SequencerStageBridge(inner);
        activeBridge = bridge;
        var coordinatorRun = host.RunAsync(plan, bridge, token);
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
            await coordinatorRun.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            bridge.Abort(ex);
            host.Cancel();
            try { await coordinatorRun.ConfigureAwait(false); } catch { }
            throw;
        }
        finally
        {
            activeBridge = null;
            host.DashboardChanged -= dashboardHandler;
            ApplyStageStatuses(host.Dashboard);
            if (realRunner is not null) await realRunner.DisposeAsync().ConfigureAwait(false);
        }
        var final = host.Dashboard.Run;
        if (final.State != ObservationRunState.Completed)
        {
            throw new InvalidOperationException($"UVEX Target Observation ended in {final.State}: {final.StatusMessage}");
        }
        Report(progress, UseRealMode ? "UVEX Target Observation 真实流程完成" : "UVEX Target Observation 模拟完成", 1);
    }

    public override Task Interrupt()
    {
        activeBridge?.Abort(new OperationCanceledException("N.I.N.A. interrupted UVEX Target Observation."));
        host.Cancel();
        return Task.CompletedTask;
    }

    public override bool Validate()
    {
        var childrenValid = base.Validate();
        Issues.Clear();
        foreach (var issue in BuildPlan().Validate()) Issues.Add(issue);
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
            settings.ObservationUseRealMode,
            settings.RealModeCommissioned);
        if (authorization.Disposition != GateDisposition.Passed)
        {
            Issues.Add($"{authorization.Code}: {authorization.Message}");
        }
        if (UseRealMode)
        {
            var capabilities = ObservationAutomationPolicy.ValidateFullAutomationCapabilities(
                settings.RequireSafetyMonitor,
                settings.RequireOpenDomeOrRoof,
                settings.RequireWeatherData,
                settings.RequireOpenOpticalCover);
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
            lockedConfiguration?.Environment.RequireSafetyMonitor ?? settings.RequireSafetyMonitor);
    }

    private void LoadDefaults()
    {
        TargetName = settings.ObservationTargetName;
        CatalogId = settings.ObservationCatalogId;
        RightAscensionDegrees = settings.ObservationRightAscensionDegrees;
        DeclinationDegrees = settings.ObservationDeclinationDegrees;
        DurationMinutes = settings.ObservationDurationMinutes;
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

    private void ApplyStageStatuses(ObservationDashboardSnapshot dashboard)
    {
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

    internal Task ExecuteStageMarkerAsync(
        ObservationStage stage,
        IProgress<ApplicationStatus> progress,
        CancellationToken token) => activeBridge?.ExecuteMarkerAsync(stage, progress, token)
        ?? throw new InvalidOperationException("OpenAstroSpec stage item is not attached to an active coordinator bridge.");
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
