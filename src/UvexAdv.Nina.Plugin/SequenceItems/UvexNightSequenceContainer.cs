using System.ComponentModel.Composition;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Windows.Input;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.Container.ExecutionStrategy;
using NINA.Sequencer.SequenceItem;
using Newtonsoft.Json;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin.SequenceItems;

[ExportMetadata("Name", "OpenAstroSpec · 光谱整夜序列")]
[ExportMetadata("Description", "原生高级序列：多个 UVEX4 目标，共享夜间设备占用，目标间不关顶，最后统一收口。")]
[ExportMetadata("Category", "OpenAstroSpec Auto")]
[Export(typeof(ISequenceItem))]
[Export(typeof(ISequenceContainer))]
[JsonObject(MemberSerialization.OptIn)]
[SupportedOSPlatform("windows")]
public sealed class UvexNightSequenceContainer : SequenceContainer
{
    private readonly ObservationCoordinatorHost host;
    private readonly RealObservationStageRunnerFactory factory;
    private readonly UvexPluginSettings settings;
    private readonly IProfileService profiles;
    private CancellationTokenSource? lifetime;
    private CancellationTokenSource? closeoutLifetime;
    private DateTimeOffset? lockedDeadline;
    private NightDeadlineSchedule? lockedSchedule;
    private bool lockedStopAtDawn;
    private double lockedDawnAltitude;
    private bool lockedHomeBeforePark;
    private int lockedHomeTimeoutSeconds;
    private bool lockedHoldOpenAfterTargets;
    private double lockedInitialSettlePixels;
    private int lockedInitialStableSeconds, lockedInitialTimeoutSeconds;
    private NativeSequencePlan? standbyPlan;
    private bool endRequested, unsafeTripped, blocked, operatorCancelled;
    private string state = "未开始；将 UVEX4 目标观测拖入此容器，可复制、排序并保存为 N.I.N.A. 模板。";
    private RealRunConfiguration? lockedConfiguration;
    private readonly List<object> outcomes = new();
    private readonly List<NativeNightTargetBoundaryFailure> targetBoundaryFailures = new();
    private bool targetsIdle = true;
    private int attemptedTargets;

    [ImportingConstructor]
    public UvexNightSequenceContainer(IProfileService profiles, ObservationCoordinatorHost host,
        RealObservationStageRunnerFactory factory) : this(profiles, host, factory, new UvexPluginSettings(profiles)) { }

    internal UvexNightSequenceContainer(IProfileService profiles, ObservationCoordinatorHost host,
        RealObservationStageRunnerFactory factory, UvexPluginSettings settings) : base(new SequentialStrategy())
    {
        this.profiles = profiles; this.host = host; this.factory = factory;
        this.settings = settings;
        EndNightCommand = new EndCommand(this);
    }

    [JsonProperty] public bool StopAtDawn { get; set; } = true;
    [JsonProperty] public double DawnSunAltitudeDegrees { get; set; } = -12;
    [JsonProperty] public bool HomeBeforeParkOnFinalize { get; set; }
    [JsonProperty] public int MountHomeTimeoutSeconds { get; set; } = 180;
    [JsonProperty] public bool HoldOpenAfterTargets { get; set; }
    [JsonProperty] public double InitialGuideSettlePixels { get; set; }
    [JsonProperty] public int InitialGuideStableSeconds { get; set; } = 5;
    [JsonProperty] public int InitialGuideTimeoutSeconds { get; set; } = 180;
    // Keep the wire name so older plugins reject HH:mm rather than ignoring a
    // new field and running without a deadline. Legacy offset dates still work.
    [JsonProperty, Newtonsoft.Json.JsonConverter(typeof(OffsetDateTimeTextConverter))]
    public string EndAtIso8601 { get; set; } = string.Empty;
    [JsonIgnore] public string NightState { get => state; private set { state = value; RaisePropertyChanged(); } }
    [JsonIgnore] public ICommand EndNightCommand { get; }
    [JsonIgnore] public string JournalPath { get; private set; } = string.Empty;
    [JsonIgnore] internal bool StopFollowingTargets => blocked || endRequested || unsafeTripped;
    [JsonIgnore] internal bool IsActive => lifetime is not null;
    [JsonIgnore] public bool CanEditPlan => !IsActive;
    [JsonIgnore] internal bool HasRecordedCurrentTarget => outcomes.Count == attemptedTargets;
    [JsonIgnore] internal bool EffectiveHoldOpenAfterTargets =>
        Items.OfType<UvexObservatoryCloseoutItem>().SingleOrDefault() is { } closeout
            ? !closeout.CloseAfterTargets : HoldOpenAfterTargets;

    internal void StopAtNativeConditionBoundary() { endRequested = true; blocked = true; }

    internal void OnNightUnsafe()
    {
        unsafeTripped = true; blocked = true; lifetime?.Cancel(); host.Cancel();
    }

    internal void LockDeadline(DateTimeOffset now, TimeZoneInfo zone)
    {
        if (IsActive) throw new InvalidOperationException("活动观测夜不能重新计算截止时间。");
        lockedSchedule = NightDeadlineSchedule.Resolve(EndAtIso8601, now, zone);
        lockedDeadline = lockedSchedule.DeadlineUtc;
    }

    public override bool Validate()
    {
        var valid = base.Validate(); Issues.Clear();
        var closeouts = Items.OfType<UvexObservatoryCloseoutItem>().ToArray();
        if (!Items.OfType<UvexTargetObservationContainer>().Any() ||
            Items.Any(x => x is not (UvexTargetObservationContainer or UvexObservatoryCloseoutItem)) ||
            closeouts.Length > 1 || (closeouts.Length == 1 && !ReferenceEquals(Items.Last(), closeouts[0])))
            Issues.Add("整夜容器接受顺序 UVEX4 目标和最多一个末尾关顶收口指令；准备动作放在容器前，禁止并行目标。");
        foreach (var issue in SpectroscopyMeridianFlipTrigger.ScopeIssues(this)) Issues.Add(issue);
        foreach (var child in Items.OfType<UvexTargetObservationContainer>())
            foreach (var issue in SpectroscopyMeridianFlipTrigger.ScopeIssues(child)) Issues.Add(issue);
        if (Items.OfType<UvexTargetObservationContainer>().Select(x => x.UseRealMode).Distinct().Count() > 1)
            Issues.Add("同一整夜序列不能混合模拟与真实目标。");
        if (!double.IsFinite(DawnSunAltitudeDegrees) || DawnSunAltitudeDegrees is < -18 or > 0)
            Issues.Add("晨光停止太阳高度须为 −18° 至 0°。");
        if (MountHomeTimeoutSeconds is < 30 or > 600) Issues.Add("回零超时须为 30–600 秒。");
        foreach (var issue in new NativeSequencePlan(1, 1, 0,
            InitialGuideSettlePixels: InitialGuideSettlePixels,
            InitialGuideStableSeconds: InitialGuideStableSeconds,
            InitialGuideTimeoutSeconds: InitialGuideTimeoutSeconds).Validate()) Issues.Add(issue);
        if ((closeouts.Length == 1 ? !closeouts[0].CloseAfterTargets : HoldOpenAfterTargets) && string.IsNullOrWhiteSpace(EndAtIso8601))
            Issues.Add("测试后保持开顶须设置截止时间（例如 05:00）；等待期间仍保留天气保护。");
        if (HomeBeforeParkOnFinalize && Items.OfType<UvexTargetObservationContainer>().Any(x => x.UseRealMode) &&
            !settings.RoofNeverInterferesWithMount)
            Issues.Add("收口 Home → Park 仅适用于已确认全姿态无干涉的台站；先关顶，回零失败不再停放。");
        if (!NightDeadlineSchedule.IsValid(EndAtIso8601)) Issues.Add(NightDeadlineSchedule.InvalidText);
        if (Items.OfType<UvexTargetObservationContainer>().Any(x => x.UseRealMode) &&
            settings.WeakSupervisionEnabled)
            Issues.Add("整夜自动收口须先配置完整 N.I.N.A. 安全链；有人弱监督不能承诺自动关顶。");
        return valid && Issues.Count == 0;
    }

    internal NativeSequencePlan ApplyNight(NativeSequencePlan target) => target with
    {
        DeferObservatoryCloseout = true, DeadlineUtc = lockedDeadline, StopAtDawn = lockedStopAtDawn,
        DawnSunAltitudeDegrees = lockedDawnAltitude,
        HomeBeforeParkOnFinalize = lockedHomeBeforePark, MountHomeTimeoutSeconds = lockedHomeTimeoutSeconds,
        InitialGuideSettlePixels = lockedInitialSettlePixels, InitialGuideStableSeconds = lockedInitialStableSeconds,
        InitialGuideTimeoutSeconds = lockedInitialTimeoutSeconds,
    };

    internal void CheckConfiguration(RealRunConfiguration configuration)
    {
        if (lockedConfiguration is null || configuration.ActionConfigurationSha256 != lockedConfiguration.ActionConfigurationSha256)
            throw new InvalidOperationException("本夜共享设备配置已改变；结束本夜，不沿用旧授权启动下一目标。");
    }

    internal void BeginTarget(string target)
    {
        if (StopFollowingTargets) throw new InvalidOperationException("本夜已停止，不再开始新目标。");
        attemptedTargets++; targetsIdle = false;
        NightState = $"目标 {attemptedTargets}/{Items.OfType<UvexTargetObservationContainer>().Count()}：{target} · 正在执行；目标间保持屋顶与镜盖状态。";
    }

    internal async Task RecordPreflightHorizonSkipAsync(ObservationPlan plan, GateResult gate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsActive || StopFollowingTargets || !targetsIdle || gate.Code != "HORIZON_BLOCKED" ||
            gate.Disposition == GateDisposition.Passed)
            throw new InvalidOperationException("当前整夜边界不能记录未启动目标的高度跳过。");
        attemptedTargets++;
        // Preserve prior target idle/settled checks and durable motion ownership.
        // No target runner was constructed and no acquisition was requested.
        outcomes.Add(new { target = plan.Target.Name, outcome = "SkippedPreflightHorizon",
            gate = gate.Code, message = gate.Message, metrics = gate.Metrics,
            evaluatedPlan = plan, noTargetRunnerCreated = true });
        NightState = $"目标 {attemptedTargets}/{Items.OfType<UvexTargetObservationContainer>().Count()}：{plan.Target.Name} · 高度窗口不足，未启动设备动作并跳过。";
        await SaveJournalAsync("Targets").ConfigureAwait(false);
    }

    internal async Task RecordTargetAsync(string target, ObservationDashboardSnapshot result,
        RealObservationStageRunner? runner, bool qualitySkipRequested, string? boundaryFailure = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        var idleErrors = runner is null ? Array.Empty<string>() :
            (await runner.ConfirmSequenceIdleAsync(timeout.Token, requireSettledMotion: false).ConfigureAwait(false)).ToArray();
        targetsIdle = idleErrors.Length == 0;
        var settledErrors = !targetsIdle || runner is null ? idleErrors :
            (await runner.ConfirmSequenceIdleAsync(timeout.Token).ConfigureAwait(false)).ToArray();
        RecordUnsettledTargetBoundary(target, result.Run.ObservationRunId, settledErrors);
        var failedGate = result.Gates.Values.LastOrDefault(x => x.Disposition != GateDisposition.Passed);
        var completed = result.Run.State == ObservationRunState.Completed && boundaryFailure is null;
        if (result.Run.State == ObservationRunState.Cancelled && !endRequested && !unsafeTripped) operatorCancelled = true;
        var skip = result.Run.State == ObservationRunState.Faulted && boundaryFailure is null &&
            qualitySkipRequested && !operatorCancelled && targetsIdle && settledErrors.Length == 0 &&
            NativeSequencePlan.IsSkippableQualityFailure(failedGate) &&
            result.Run.RecentEvents.LastOrDefault()?.Code == failedGate?.Code && !unsafeTripped && !endRequested;
        if (!completed && !skip) blocked = true;
        if (!targetsIdle || settledErrors.Length != 0) blocked = true;
        outcomes.Add(new { target, runId = result.Run.ObservationRunId, result.ManifestPath,
            outcome = completed ? "Completed" : skip ? "SkippedQualityFailure" : "Stopped",
            gate = failedGate?.Code, failureScope = failedGate?.FailureScope.ToString(), boundaryFailure, idleErrors, settledErrors,
            qualitySkipRequested, endRequested, unsafeTripped, operatorCancelled,
            terminalCode = result.Run.RecentEvents.LastOrDefault()?.Code, result.AcquisitionProgress });
        NightState = $"目标 {attemptedTargets}/{Items.OfType<UvexTargetObservationContainer>().Count()}：{target} · " +
            (completed ? "完成" : skip ? $"质量失败，已确认停止并跳过（{failedGate?.Code}）" : $"停止（{boundaryFailure ?? failedGate?.Code ?? result.Run.State.ToString()}）");
        await SaveJournalAsync("Targets").ConfigureAwait(false);
        if (!completed && (result.Run.State == ObservationRunState.Faulted ||
            boundaryFailure is not null || idleErrors.Length > 0 || settledErrors.Length > 0))
            host.NotifyNativeTargetBoundary(this, result.Run.ObservationRunId, target,
                failedGate?.Code ?? result.Run.State.ToString(), skip,
                boundaryFailure ?? (settledErrors.Length > 0 ? string.Join("; ", settledErrors) : null));
    }

    internal void RecordUnsettledTargetBoundary(string target, string? runId, IReadOnlyList<string> errors)
    {
        if (errors.Count == 0) return;
        // Closing a roof or parking a mount is not evidence that a target's
        // interrupted motion/flip ledger was reconciled. Retain this separately
        // from physical idleness, which still permits emergency roof closeout.
        targetBoundaryFailures.Add(new(target, runId, errors.ToArray()));
        blocked = true;
    }

    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        if (!Validate()) throw new InvalidOperationException(string.Join(" ", Issues));
        LockDeadline(DateTimeOffset.UtcNow, TimeZoneInfo.Local);
        lockedStopAtDawn = StopAtDawn; lockedDawnAltitude = DawnSunAltitudeDegrees;
        lockedHomeBeforePark = HomeBeforeParkOnFinalize; lockedHomeTimeoutSeconds = MountHomeTimeoutSeconds;
        lockedHoldOpenAfterTargets = EffectiveHoldOpenAfterTargets;
        lockedInitialSettlePixels = InitialGuideSettlePixels;
        lockedInitialStableSeconds = InitialGuideStableSeconds; lockedInitialTimeoutSeconds = InitialGuideTimeoutSeconds;
        var firstTarget = Items.OfType<UvexTargetObservationContainer>().First();
        standbyPlan = ApplyNight(new NativeSequencePlan(1, 1, 0,
            LatitudeDegrees: firstTarget.SiteLatitudeDegrees, LongitudeDegrees: firstTarget.SiteLongitudeDegreesEast));
        var real = Items.OfType<UvexTargetObservationContainer>().First().UseRealMode;
        NinaInstancePolicy.RequireMaster(settings);
        using var reservation = await host.ReserveNightAsync(this, real, token).ConfigureAwait(false);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime = cancellation; endRequested = unsafeTripped = blocked = operatorCancelled = false;
        try { await ExecuteReservedNightAsync(progress, token, cancellation, real).ConfigureAwait(false); }
        finally
        {
            // Cover initialization as well as execution/closeout. A failed UI
            // notification or configuration capture must not leave an active
            // night pointing at a disposed CancellationTokenSource.
            lifetime = null;
            RefreshPlanEditability();
        }
    }

    private async Task ExecuteReservedNightAsync(IProgress<ApplicationStatus> progress, CancellationToken token,
        CancellationTokenSource cancellation, bool real)
    {
        RefreshPlanEditability();
        attemptedTargets = 0; targetsIdle = true; outcomes.Clear(); targetBoundaryFailures.Clear();
        JournalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UVEX-ADV", "nights", $"night-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.json");
        lockedConfiguration = real ? factory.CaptureConfiguration(settings) : null;
        using var safety = real ? factory.WatchNightSafety(OnNightUnsafe) : null;
        Exception? failure = null;
        Exception? reconciliationFailure = null;
        try
        {
            await SaveJournalAsync("Running").ConfigureAwait(false);
            // Native ordering, conditions, child statuses and template persistence.
            try { await base.Execute(progress, cancellation.Token).ConfigureAwait(false); }
            catch (Exception ex) { failure = ex; blocked = true; }
            // Stay inside the same night reservation and safety subscription.
            // This is idle standby, not an ended night with an unprotected roof.
            // A target blocker prevents more targets, not protected idle standby.
            // Never release the reservation/safety subscription with an open roof.
            if (lockedHoldOpenAfterTargets && !endRequested && !unsafeTripped && targetsIdle &&
                !operatorCancelled && !token.IsCancellationRequested)
                await HoldOpenUntilCloseoutAsync(progress, cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { failure ??= ex; blocked = true; }
        finally
        {
            var ordinaryCancel = (token.IsCancellationRequested || operatorCancelled) && !endRequested && !unsafeTripped;
            var closeErrors = new List<string>();
            var physicalCloseoutConfirmed = false;
            if (!ordinaryCancel && targetsIdle)
            {
                NightState = lockedConfiguration?.Environment.RoofNeverInterferesWithMount == true
                    ? lockedHomeBeforePark
                        ? "本夜结束：关盖 → 停止运动 → 关顶 → Home → Park；逐项核验，失败不重复运动。"
                        : "本夜结束：无干涉屋顶收口（关盖 → 停止运动 → 关顶 → 独立停放）；分别核对结果。"
                    : "本夜结束：正在统一收口（关盖 → 已标定停放位 → 关顶）；以设备回读为准。";
                try
                {
                    if (real)
                    {
                        await using var closer = factory.Create(host, settings, progress,
                            lockedConfiguration!.WithSequencePlan(ApplyNight(new NativeSequencePlan(
                                lockedConfiguration.Atr.ScienceFrameCount, lockedConfiguration.Atr.MaximumScienceAttempts, 0))));
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(
                            lockedConfiguration.Environment.RoofCloseoutTimeoutSeconds +
                            lockedConfiguration.Environment.OpticalCoverTransitionTimeoutSeconds + 180 +
                            (lockedHomeBeforePark ? lockedHomeTimeoutSeconds : 0)));
                        closeoutLifetime = timeout;
                        try
                        {
                            // Cancel can arrive between the branch check and token publication.
                            if (operatorCancelled && !unsafeTripped) timeout.Cancel();
                            closeErrors.AddRange(await closer.CloseSequenceNightAsync(timeout.Token).ConfigureAwait(false));
                            physicalCloseoutConfirmed = closeErrors.Count == 0;
                        }
                        finally { closeoutLifetime = null; }
                    }
                }
                catch (Exception ex) { closeErrors.Add(ex.Message); }
            }
            else if (!targetsIdle) closeErrors.Add("采集或设备停止未确认，未执行机械收口。");
            NightState = DescribeTerminalState(real, ordinaryCancel, physicalCloseoutConfirmed, closeErrors,
                targetBoundaryFailures);
            outcomes.Add(new { terminal = NightState, closeErrors, unsafeTripped, ordinaryCancel,
                failure = failure?.Message, physicalHardware = real, physicalCloseoutConfirmed,
                targetBoundariesSettled = targetBoundaryFailures.Count == 0,
                targetBoundaryFailures = targetBoundaryFailures.ToArray() });
            await SaveJournalAsync("Terminal").ConfigureAwait(false);
            progress.Report(new ApplicationStatus { Source = "OpenAstroSpec 整夜", Status = NightState });
            if (closeErrors.Count > 0) throw new InvalidOperationException(NightState, failure);
            if (!ordinaryCancel && targetBoundaryFailures.Count > 0)
                reconciliationFailure = new InvalidOperationException(NightState);
        }
        if (failure is not null && !endRequested) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        // Preserve the original cancellation/failure when present. This reports
        // unresolved responsibility only; it never retries closeout or clears a ledger.
        if (reconciliationFailure is not null) throw reconciliationFailure;
    }

    private void RefreshPlanEditability()
    {
        RaisePropertyChanged(nameof(CanEditPlan));
        foreach (var target in Items.OfType<UvexTargetObservationContainer>()) target.RefreshPlanEditability();
        foreach (var closeout in Items.OfType<UvexObservatoryCloseoutItem>()) closeout.RefreshPlanEditability();
    }

    internal static string DescribeTerminalState(bool physicalHardware, bool ordinaryCancel,
        bool physicalCloseoutConfirmed, IReadOnlyList<string> closeErrors,
        IReadOnlyList<NativeNightTargetBoundaryFailure> targetBoundaryFailures)
    {
        var unresolved = targetBoundaryFailures.Count == 0 ? string.Empty :
            " 观测运动/交接责任未结清，禁止继续目标或声称整夜全部成功；原始账本保留，需检查各目标记录。";
        if (ordinaryCancel) return (closeErrors.Count > 0
            ? $"已请求取消；停止/空闲未确认，未执行机械收口：{string.Join("；", closeErrors)}"
            : "已取消；只停止采集，不执行机械收口。") + unresolved;
        if (closeErrors.Count > 0) return $"物理收口未完成：{string.Join("；", closeErrors)}" + unresolved;
        if (!physicalHardware) return "整夜模拟结束：未操作硬件；请查看各目标结果。" + unresolved;
        return (physicalCloseoutConfirmed
            ? "物理收口已确认：关盖、停放、关顶已回读；请查看各目标结果。"
            : "物理收口未确认；不能声称整夜收口成功。") + unresolved;
    }

    private async Task HoldOpenUntilCloseoutAsync(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        NightState = blocked
            ? "目标故障已停止：保持开顶待处理，不继续目标、不清除未结账本；天气保护仍生效。可单独点“关顶与整夜收口”；截止/晨光/Unsafe 自动收口。"
            : "测试目标已结束：保持开顶、无新曝光；天气保护仍生效。可单独点“关顶与整夜收口”；截止/晨光/Unsafe 自动收口。";
        await SaveJournalAsync("TestStandby").ConfigureAwait(false);
        progress.Report(new ApplicationStatus { Source = "OpenAstroSpec 整夜", Status = NightState });
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (lockedConfiguration is not null && !factory.IsNightSafetyCurrent(lockedConfiguration))
            {
                outcomes.Add(new { standbyStop = "NIGHT_SAFETY_UNAVAILABLE", message = "安全监视器不安全、断连或绑定改变，结束待机。" });
                OnNightUnsafe();
                token.ThrowIfCancellationRequested();
            }
            if (standbyPlan!.StopGateNow() is { } gate)
            {
                outcomes.Add(new { standbyStop = gate.Code, message = gate.Message });
                return;
            }
            await Task.Delay(500, token).ConfigureAwait(false);
        }
    }

    private async Task SaveJournalAsync(string phase)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
        var json = System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = 1, phase,
            updatedUtc = DateTimeOffset.UtcNow, state = NightState,
            homeBeforeParkOnFinalize = lockedHomeBeforePark, mountHomeTimeoutSeconds = lockedHomeTimeoutSeconds,
            holdOpenAfterTargets = lockedHoldOpenAfterTargets, deadlineUtc = lockedDeadline,
            deadlineSchedule = lockedSchedule,
            initialGuideSettlePixels = lockedInitialSettlePixels, initialGuideStableSeconds = lockedInitialStableSeconds,
            initialGuideTimeoutSeconds = lockedInitialTimeoutSeconds,
            targetBoundaryFailures = targetBoundaryFailures.ToArray(),
            outcomes }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(JournalPath + ".tmp", json).ConfigureAwait(false);
        File.Move(JournalPath + ".tmp", JournalPath, overwrite: true);
    }

    public override Task Interrupt()
    {
        operatorCancelled = true; lifetime?.Cancel(); closeoutLifetime?.Cancel(); host.Cancel();
        return Task.CompletedTask;
    }
    public override object Clone()
    {
        var clone = new UvexNightSequenceContainer(profiles, host, factory, settings)
        { StopAtDawn = StopAtDawn, DawnSunAltitudeDegrees = DawnSunAltitudeDegrees, EndAtIso8601 = EndAtIso8601,
            HomeBeforeParkOnFinalize = HomeBeforeParkOnFinalize, MountHomeTimeoutSeconds = MountHomeTimeoutSeconds,
            HoldOpenAfterTargets = HoldOpenAfterTargets, InitialGuideSettlePixels = InitialGuideSettlePixels,
            InitialGuideStableSeconds = InitialGuideStableSeconds, InitialGuideTimeoutSeconds = InitialGuideTimeoutSeconds };
        clone.CopyMetaData(this);
        foreach (var item in Items) clone.Add((ISequenceItem)item.Clone());
        foreach (var condition in Conditions)
        {
            var child = (NINA.Sequencer.Conditions.ISequenceCondition)condition.Clone();
            clone.Conditions.Add(child); child.AttachNewParent(clone);
        }
        foreach (var trigger in Triggers)
        {
            var child = (NINA.Sequencer.Trigger.ISequenceTrigger)trigger.Clone();
            clone.Triggers.Add(child); child.AttachNewParent(clone);
        }
        return clone;
    }

    [System.Runtime.Serialization.OnDeserializing]
    private void ClearFactoryDefaultsForTemplate(System.Runtime.Serialization.StreamingContext context)
    { Items.Clear(); Conditions.Clear(); Triggers.Clear(); }

    [System.Runtime.Serialization.OnDeserialized]
    private void ReattachTemplateChildren(System.Runtime.Serialization.StreamingContext context)
    {
        foreach (var item in Items) item.AttachNewParent(this);
        foreach (var condition in Conditions) condition.AttachNewParent(this);
        foreach (var trigger in Triggers) trigger.AttachNewParent(this);
    }

    private sealed class EndCommand(UvexNightSequenceContainer owner) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter)
        {
            if (owner.lifetime is null) return;
            owner.endRequested = true; owner.blocked = true;
            owner.lifetime.Cancel(); owner.host.Cancel();
        }
    }
}

internal sealed record NativeNightTargetBoundaryFailure(string Target, string? RunId, IReadOnlyList<string> Errors);
