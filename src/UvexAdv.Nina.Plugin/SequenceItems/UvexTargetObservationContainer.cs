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
public sealed class UvexTargetObservationContainer : SequenceContainer, IImmutableContainer, IDeepSkyObjectContainer
{
    private readonly IProfileService profileService;
    private readonly INighttimeCalculator nighttimeCalculator;
    private readonly ObservationCoordinatorHost host;
    private readonly UvexPluginSettings settings;
    private readonly RealObservationStageRunnerFactory realRunnerFactory;
    private SequencerStageBridge? activeBridge;
    private CancellationTokenSource? targetLifetime;
    private bool useRealMode;
    private InputTarget target;

    [ImportingConstructor]
    public UvexTargetObservationContainer(
        IProfileService profileService,
        INighttimeCalculator nighttimeCalculator,
        ObservationCoordinatorHost host,
        RealObservationStageRunnerFactory realRunnerFactory)
        : this(profileService, nighttimeCalculator, host, realRunnerFactory, new UvexPluginSettings(profileService)) { }

    internal UvexTargetObservationContainer(IProfileService profileService, INighttimeCalculator nighttimeCalculator,
        ObservationCoordinatorHost host, RealObservationStageRunnerFactory realRunnerFactory, UvexPluginSettings settings)
        : base(new SequentialStrategy())
    {
        this.profileService = profileService;
        this.nighttimeCalculator = nighttimeCalculator;
        this.host = host;
        this.realRunnerFactory = realRunnerFactory;
        this.settings = settings;
        target = CreateTarget(profileService);
        AttachTarget(target);
        NighttimeData = nighttimeCalculator.Calculate(null);
        nighttimeCalculator.OnReferenceDayChanged += OnReferenceDayChanged;
        LoadDefaults();
        SeedStageItems();
    }

    private UvexTargetObservationContainer(UvexTargetObservationContainer copy)
        : base(new SequentialStrategy())
    {
        profileService = copy.profileService;
        nighttimeCalculator = copy.nighttimeCalculator;
        host = copy.host;
        settings = copy.settings;
        realRunnerFactory = copy.realRunnerFactory;
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
        DurationMinutes = copy.DurationMinutes;
        ScienceFrames = copy.ScienceFrames;
        MaximumScienceAttempts = copy.MaximumScienceAttempts;
        FixedExposureSeconds = copy.FixedExposureSeconds;
        FailurePolicy = copy.FailurePolicy;
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
    public InputTarget Target
    {
        get => target;
        set
        {
            if (ReferenceEquals(target, value) || value is null) return;
            DetachTarget(target);
            target = value;
            AttachTarget(target);
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
            Target.TargetName = normalized;
            RaisePropertyChanged();
        }
    }

    [JsonProperty]
    public string CatalogId { get; set; } = string.Empty;

    [JsonProperty]
    public TargetObservabilityClass TargetObservability { get; set; }

    [JsonProperty]
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
    public double DurationMinutes { get; set; }

    [JsonProperty] public int ScienceFrames { get; set; }
    [JsonProperty] public int MaximumScienceAttempts { get; set; }
    [JsonProperty] public double FixedExposureSeconds { get; set; }
    [JsonProperty] public SpectroscopyTargetFailurePolicy FailurePolicy { get; set; }
    [JsonIgnore] public IReadOnlyList<SpectroscopyFailurePolicyChoice> AvailableFailurePolicies => SpectroscopyFailurePolicyChoice.Choices;
    [JsonIgnore] public string LastOutcome { get; private set; } = "未执行";

    private NativeSequencePlan TargetSequencePlan() => new(ScienceFrames, MaximumScienceAttempts,
        FixedExposureSeconds, LatitudeDegrees: SiteLatitudeDegrees, LongitudeDegrees: SiteLongitudeDegreesEast);

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
        // The coordinator finishes each acquisition segment before a flip.
        // Keep a target-wide token so native Interrupt also stops that handoff.
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (Interlocked.CompareExchange(ref targetLifetime, lifetime, null) is not null)
            throw new InvalidOperationException("该光谱目标已有执行中的任务。");
        try { await ExecuteOwnedAsync(progress, lifetime.Token).ConfigureAwait(false); }
        finally { Interlocked.CompareExchange(ref targetLifetime, null, lifetime); }
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
        night?.BeginTarget(TargetName);
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
            // The second segment is a new production runner with no old optical
            // state. The night retains the exclusive lease throughout the flip.
            while (await ExecuteTargetAsync(progress, token, night, meridian, flip, flipConfiguration).ConfigureAwait(false))
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
            LastOutcome = night.StopFollowingTargets ? "已停止：查看原始质量门与本夜记录。" : "质量失败，已确认停止并跳过。";
        }
        finally { RaisePropertyChanged(nameof(LastOutcome)); }
    }

    private async Task<bool> ExecuteTargetAsync(IProgress<ApplicationStatus> progress, CancellationToken token,
        UvexNightSequenceContainer? night, NativeMeridianSession? meridian = null,
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
        EventHandler<ObservationDashboardSnapshot> dashboardHandler = (_, dashboard) =>
        {
            ApplyStageStatuses(dashboard);
            if (dashboard.Run.State is ObservationRunState.Cancelled or ObservationRunState.Faulted)
            {
                activeBridge?.Abort(new OperationCanceledException(
                    $"UVEX observation ended in {dashboard.Run.State}: {dashboard.Run.StatusMessage}"));
            }
        };
        RealObservationStageRunner? realRunner = null;
        RealRunConfiguration? lockedConfiguration = null;
        var sequencePlan = meridian?.RemainingPlan() ?? night?.ApplyNight(TargetSequencePlan()) ?? TargetSequencePlan();
        if (UseRealMode)
        {
            lockedConfiguration = realRunnerFactory.CaptureConfiguration(settings);
            night?.CheckConfiguration(lockedConfiguration);
            lockedConfiguration = lockedConfiguration.WithSequencePlan(sequencePlan);
        }
        var plan = meridian?.TargetPlan is { } originalTarget
            ? originalTarget with { ObservationRunId = Guid.NewGuid().ToString("N"), PlannedStartUtc = DateTimeOffset.UtcNow }
            : BuildPlan(lockedConfiguration);
        if (meridian is not null) meridian.TargetPlan ??= plan;
        IObservationStageRunner inner = UseRealMode
            ? realRunner = realRunnerFactory.Create(host, settings, progress, lockedConfiguration)
            : new SimulatedObservationStageRunner(host, Math.Clamp(SimulationStageMilliseconds, 250, 30_000), progress);
        if (realRunner is not null) realRunner.NativeMeridian = meridian;
        using var bridge = new SequencerStageBridge(inner)
        { EndRunOnGateFailure = night is not null, BoundaryGate = sequencePlan.StopGateNow };
        activeBridge = bridge;
        host.DashboardChanged += dashboardHandler;
        ApplyStageStatuses(host.Dashboard);
        var coordinatorRun = host.RunAsync(plan, bridge, token, night);
        var continueAfterFlip = false;
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
            if (!bridge.HasCompletedFinalMarker && !coordinatorRun.IsCompleted)
            {
                // A native condition may end its block without executing every
                // marker. Join the coordinator; never leave it waiting forever.
                night?.StopAtNativeConditionBoundary();
                host.Cancel();
                bridge.Abort(new OperationCanceledException("Native sequence conditions ended the target block."));
            }
            await coordinatorRun.ConfigureAwait(false);
            if (meridian is not null && realRunner is not null && host.Dashboard.Run.State == ObservationRunState.Completed)
            {
                realRunner.RecordMeridianSegment(meridian, plan.ObservationRunId);
                if (meridian.Requested)
                {
                    LastOutcome = $"中天翻转中 · 已接受 {meridian.Accepted}/{meridian.Original.ScienceFrames} 张，保留已用尝试 {meridian.Attempts} 次";
                    RaisePropertyChanged(nameof(LastOutcome));
                    try
                    {
                        await realRunner.PerformNativeMeridianFlipAsync(flip!, meridian, flipConfiguration!, token).ConfigureAwait(false);
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
            try
            {
                if (night is not null && !continueAfterFlip) await night.RecordTargetAsync(TargetName, host.Dashboard, realRunner,
                    FailurePolicy == SpectroscopyTargetFailurePolicy.SkipQualityFailure, boundaryFailure).ConfigureAwait(false);
            }
            finally { if (realRunner is not null) await realRunner.DisposeAsync().ConfigureAwait(false); }
        }
        var final = host.Dashboard.Run;
        if (final.State != ObservationRunState.Completed)
        {
            throw new InvalidOperationException($"UVEX Target Observation ended in {final.State}: {final.StatusMessage}");
        }
        Report(progress, continueAfterFlip ? "翻转完成；重新执行定位/入缝并续拍剩余科学帧" :
            UseRealMode ? "UVEX Target Observation 真实流程完成" : "UVEX Target Observation 模拟完成", continueAfterFlip ? 0 : 1);
        return continueAfterFlip;
    }

    public override Task Interrupt()
    {
        try { Volatile.Read(ref targetLifetime)?.Cancel(); }
        catch (ObjectDisposedException) { /* The same target just completed. */ }
        activeBridge?.Abort(new OperationCanceledException("N.I.N.A. interrupted UVEX Target Observation."));
        host.Cancel();
        // During the flip the segment dashboard is already Completed. Tell
        // the night explicitly this is ordinary cancel, not normal closeout.
        if (Parent is UvexNightSequenceContainer { IsActive: true } night) return night.Interrupt();
        return Task.CompletedTask;
    }

    public override bool Validate()
    {
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
            settings.ObservationUseRealMode,
            settings.RealModeCommissioned);
        if (authorization.Disposition != GateDisposition.Passed)
        {
            Issues.Add($"{authorization.Code}: {authorization.Message}");
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
                settings.WeakSupervisionEnabled);
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
        if (!double.IsFinite(rightAscensionDegrees) || !double.IsFinite(declinationDegrees)) return;
        var current = Target.InputCoordinates.Coordinates.Transform(Epoch.J2000);
        if (Math.Abs(current.RADegrees - rightAscensionDegrees) < 1e-12 &&
            Math.Abs(current.Dec - declinationDegrees) < 1e-12)
        {
            return;
        }
        Target.InputCoordinates = new InputCoordinates(
            new Coordinates(rightAscensionDegrees, declinationDegrees, Epoch.J2000, Coordinates.RAType.Degrees));
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
        RaisePropertyChanged(nameof(RightAscensionDegrees));
        RaisePropertyChanged(nameof(DeclinationDegrees));
    }

    private void OnTargetPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(InputTarget.TargetName), StringComparison.Ordinal))
        {
            RaisePropertyChanged(nameof(TargetName));
        }
    }

    private void RaiseTargetPropertiesChanged()
    {
        RaisePropertyChanged(nameof(Target));
        RaisePropertyChanged(nameof(TargetName));
        RaisePropertyChanged(nameof(RightAscensionDegrees));
        RaisePropertyChanged(nameof(DeclinationDegrees));
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
        // N.I.N.A. clones a populated MEF prototype before Json.NET Populate.
        // Without this, each template load appends another eleven markers.
        Items.Clear(); Conditions.Clear(); Triggers.Clear();
    }

    [System.Runtime.Serialization.OnDeserialized]
    private void ReattachTemplateChildren(System.Runtime.Serialization.StreamingContext context) => AttachChildren();

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
