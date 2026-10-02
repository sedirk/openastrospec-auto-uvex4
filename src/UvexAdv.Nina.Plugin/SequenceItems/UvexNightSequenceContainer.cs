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
    private bool lockedStopAtDawn;
    private double lockedDawnAltitude;
    private bool endRequested, unsafeTripped, blocked, operatorCancelled;
    private string state = "未开始；将 UVEX4 目标观测拖入此容器，可复制、排序并保存为 N.I.N.A. 模板。";
    private RealRunConfiguration? lockedConfiguration;
    private readonly List<object> outcomes = new();
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
    // ISO-8601 offset is compulsory: an ambiguous machine-local midnight is not a deadline.
    [JsonProperty] public string EndAtIso8601 { get; set; } = string.Empty;
    [JsonIgnore] public string NightState { get => state; private set { state = value; RaisePropertyChanged(); } }
    [JsonIgnore] public ICommand EndNightCommand { get; }
    [JsonIgnore] public string JournalPath { get; private set; } = string.Empty;
    [JsonIgnore] internal bool StopFollowingTargets => blocked || endRequested || unsafeTripped;
    [JsonIgnore] internal bool IsActive => lifetime is not null;
    [JsonIgnore] internal bool HasRecordedCurrentTarget => outcomes.Count == attemptedTargets;

    internal void StopAtNativeConditionBoundary() { endRequested = true; blocked = true; }

    private DateTimeOffset? Deadline => string.IsNullOrWhiteSpace(EndAtIso8601) ? null :
        DateTimeOffset.Parse(EndAtIso8601, System.Globalization.CultureInfo.InvariantCulture);

    public override bool Validate()
    {
        var valid = base.Validate(); Issues.Clear();
        if (Items.Count == 0 || Items.Any(x => x is not UvexTargetObservationContainer))
            Issues.Add("第一版整夜容器只接受 UVEX4 目标观测；准备动作放在本容器之前。禁止并行目标。");
        foreach (var issue in SpectroscopyMeridianFlipTrigger.ScopeIssues(this)) Issues.Add(issue);
        foreach (var child in Items.OfType<UvexTargetObservationContainer>())
            foreach (var issue in SpectroscopyMeridianFlipTrigger.ScopeIssues(child)) Issues.Add(issue);
        if (Items.OfType<UvexTargetObservationContainer>().Select(x => x.UseRealMode).Distinct().Count() > 1)
            Issues.Add("同一整夜序列不能混合模拟与真实目标。");
        if (!double.IsFinite(DawnSunAltitudeDegrees) || DawnSunAltitudeDegrees is < -18 or > 0)
            Issues.Add("晨光停止太阳高度须为 −18° 至 0°。");
        if (!string.IsNullOrWhiteSpace(EndAtIso8601) &&
            (!System.Text.RegularExpressions.Regex.IsMatch(EndAtIso8601, @"(Z|[+-]\d{2}:\d{2})$") ||
             !DateTimeOffset.TryParse(EndAtIso8601, System.Globalization.CultureInfo.InvariantCulture,
                 System.Globalization.DateTimeStyles.None, out _)))
            Issues.Add("截止时间必须包含时区，例如 2026-09-21T05:00:00+08:00；留空只按晨光与目标完成结束。");
        if (Items.OfType<UvexTargetObservationContainer>().Any(x => x.UseRealMode) &&
            (settings.WeakSupervisionEnabled || !settings.CloseDomeOrRoofOnFinalize || !settings.CloseOpticalCoverOnFinalize))
            Issues.Add("整夜自动收口须先配置完整 N.I.N.A. 安全链并启用结束关盖/关顶；有人弱监督不能承诺自动关顶。");
        return valid && Issues.Count == 0;
    }

    internal NativeSequencePlan ApplyNight(NativeSequencePlan target) => target with
    {
        DeferObservatoryCloseout = true, DeadlineUtc = lockedDeadline, StopAtDawn = lockedStopAtDawn,
        DawnSunAltitudeDegrees = lockedDawnAltitude,
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
        NightState = $"目标 {attemptedTargets}/{Items.Count}：{target} · 正在执行；目标间保持屋顶与镜盖状态。";
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
        var failedGate = result.Gates.Values.LastOrDefault(x => x.Disposition != GateDisposition.Passed);
        var completed = result.Run.State == ObservationRunState.Completed && boundaryFailure is null;
        if (result.Run.State == ObservationRunState.Cancelled && !endRequested && !unsafeTripped) operatorCancelled = true;
        var skip = !completed && boundaryFailure is null && qualitySkipRequested && targetsIdle && settledErrors.Length == 0 &&
            NativeSequencePlan.IsSkippableQualityFailure(failedGate?.Code) &&
            result.Run.RecentEvents.LastOrDefault()?.Code == failedGate?.Code && !unsafeTripped && !endRequested;
        if (!completed && !skip) blocked = true;
        if (!targetsIdle || settledErrors.Length != 0) blocked = true;
        outcomes.Add(new { target, runId = result.Run.ObservationRunId, result.ManifestPath,
            outcome = completed ? "Completed" : skip ? "SkippedQualityFailure" : "Stopped",
            gate = failedGate?.Code, boundaryFailure, idleErrors, settledErrors, result.AcquisitionProgress });
        NightState = $"目标 {attemptedTargets}/{Items.Count}：{target} · " +
            (completed ? "完成" : skip ? $"质量失败，已确认停止并跳过（{failedGate?.Code}）" : $"停止（{boundaryFailure ?? failedGate?.Code ?? result.Run.State.ToString()}）");
        await SaveJournalAsync("Targets").ConfigureAwait(false);
    }

    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        if (!Validate()) throw new InvalidOperationException(string.Join(" ", Issues));
        lockedDeadline = Deadline; lockedStopAtDawn = StopAtDawn; lockedDawnAltitude = DawnSunAltitudeDegrees;
        var real = Items.OfType<UvexTargetObservationContainer>().First().UseRealMode;
        NinaInstancePolicy.RequireMaster(settings);
        using var reservation = await host.ReserveNightAsync(this, real, token).ConfigureAwait(false);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime = cancellation; endRequested = unsafeTripped = blocked = operatorCancelled = false;
        attemptedTargets = 0; targetsIdle = true; outcomes.Clear();
        JournalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UVEX-ADV", "nights", $"night-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.json");
        lockedConfiguration = real ? factory.CaptureConfiguration(settings) : null;
        using var safety = real ? factory.WatchNightSafety(() =>
        {
            unsafeTripped = true; blocked = true; cancellation.Cancel(); host.Cancel();
        }) : null;
        Exception? failure = null;
        try
        {
            await SaveJournalAsync("Running").ConfigureAwait(false);
            // Native ordering, conditions, child statuses and template persistence.
            await base.Execute(progress, cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { failure = ex; blocked = true; }
        finally
        {
            var ordinaryCancel = (token.IsCancellationRequested || operatorCancelled) && !endRequested && !unsafeTripped;
            var closeErrors = new List<string>();
            if (!ordinaryCancel && targetsIdle)
            {
                NightState = "本夜结束：正在统一收口（关盖 → 已标定停放位 → 关顶）；以设备回读为准。";
                try
                {
                    if (real)
                    {
                        await using var closer = factory.Create(host, settings, progress,
                            lockedConfiguration!.WithSequencePlan(ApplyNight(new NativeSequencePlan(
                                lockedConfiguration.Atr.ScienceFrameCount, lockedConfiguration.Atr.MaximumScienceAttempts, 0))));
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(
                            lockedConfiguration.Environment.DomeOrRoofTransitionTimeoutSeconds +
                            lockedConfiguration.Environment.OpticalCoverTransitionTimeoutSeconds + 180));
                        closeoutLifetime = timeout;
                        try
                        {
                            // Cancel can arrive between the branch check and token publication.
                            if (operatorCancelled && !unsafeTripped) timeout.Cancel();
                            closeErrors.AddRange(await closer.CloseSequenceNightAsync(timeout.Token).ConfigureAwait(false));
                        }
                        finally { closeoutLifetime = null; }
                    }
                }
                catch (Exception ex) { closeErrors.Add(ex.Message); }
            }
            else if (!targetsIdle) closeErrors.Add("采集或设备停止未确认，未执行机械收口。");
            NightState = ordinaryCancel ? "已取消；只停止采集，不执行机械收口。" : closeErrors.Count > 0
                ? $"收口未完成：{string.Join("；", closeErrors)}" : real
                    ? "整夜已收口：关盖、停放、关顶已回读；请查看各目标结果。"
                    : "整夜模拟结束：未操作硬件；请查看各目标结果。";
            outcomes.Add(new { terminal = NightState, closeErrors, unsafeTripped, ordinaryCancel,
                failure = failure?.Message, physicalHardware = real });
            try { await SaveJournalAsync("Terminal").ConfigureAwait(false); }
            finally { lifetime = null; }
            progress.Report(new ApplicationStatus { Source = "OpenAstroSpec 整夜", Status = NightState });
            if (closeErrors.Count > 0) throw new InvalidOperationException(NightState, failure);
        }
        if (failure is not null && !endRequested) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task SaveJournalAsync(string phase)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
        var json = System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = 1, phase,
            updatedUtc = DateTimeOffset.UtcNow, state = NightState, outcomes }, new JsonSerializerOptions { WriteIndented = true });
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
        { StopAtDawn = StopAtDawn, DawnSunAltitudeDegrees = DawnSunAltitudeDegrees, EndAtIso8601 = EndAtIso8601 };
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
