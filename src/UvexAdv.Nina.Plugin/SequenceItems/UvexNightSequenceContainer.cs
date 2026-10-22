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
    private bool targetResponsibilitiesSettled = true;
    private int attemptedTargets;
    private string? standbyJournalPath;
    private NightMountClockRecovery clockRecovery = new();
    private readonly List<object> clockContinuations = [];
    private int focusScanReservation;
    private readonly List<object> focusRecoveries = [];

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
    [JsonIgnore] internal bool StopFollowingTargets => blocked || endRequested || unsafeTripped || operatorCancelled || lifetime?.IsCancellationRequested == true;
    [JsonIgnore] internal bool IsActive => lifetime is not null;
    [JsonIgnore] public bool CanEditPlan => !IsActive;
    // Transient proof for optional native end instructions, never serialized or cloned.
    [JsonIgnore] internal bool NativeEndInstructionsReady { get; private set; }
    [JsonIgnore] internal bool HasRecordedCurrentTarget => outcomes.Count == attemptedTargets;
    [JsonIgnore] internal bool EffectiveHoldOpenAfterTargets =>
        NativeNightEndArea.Choices(this).FirstOrDefault() is { } closeout
            ? !closeout.CloseAfterTargets : HoldOpenAfterTargets;

    internal void StopAtNativeConditionBoundary() { endRequested = true; blocked = true; }

    internal void OnNightUnsafe()
    {
        if (!IsActive) return; // A disposed previous-night callback cannot cancel a new run.
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
        factory?.ObserveNativeSequenceLifetime();
        var valid = base.Validate(); Issues.Clear();
        var closeouts = NativeNightEndArea.Choices(this).ToArray();
        if (!Items.OfType<UvexTargetObservationContainer>().Any() ||
            Items.Any(x => x is not (UvexTargetObservationContainer or UvexObservatoryCloseoutItem)) ||
            closeouts.Length > 1 || Items.OfType<UvexObservatoryCloseoutItem>().Any(x => !ReferenceEquals(Items.Last(), x)))
            Issues.Add("整夜容器接受顺序 UVEX4 目标；收口选择放原生结束区域或兼容旧版末尾，不能重复。禁止并行目标。");
        foreach (var issue in NativeNightEndArea.Issues(this)) Issues.Add(issue);
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

    internal RealRunConfiguration CheckConfiguration(RealRunConfiguration configuration)
    {
        if (lockedConfiguration is not null)
            configuration = configuration.WithLockedMainFocusOptions(lockedConfiguration.MainFocusOptionsJson);
        if (lockedConfiguration is null || configuration.ActionConfigurationSha256 != lockedConfiguration.ActionConfigurationSha256)
            throw new InvalidOperationException("本夜共享设备配置已改变；结束本夜，不沿用旧授权启动下一目标。");
        return configuration;
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

    internal async Task<bool> RecordTargetAsync(string target, ObservationDashboardSnapshot result,
        INativeTargetBoundary? runner, bool qualitySkipRequested, string? boundaryFailure = null,
        ObservationPlan? failedPlan = null, NativeTargetSession? session = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        var idleErrors = runner is null ? (result.LockedRunAdapter == "real"
            ? new[] { "NATIVE_TARGET_STOP_PROOF_MISSING: real target has no owner boundary verifier." } : Array.Empty<string>()) :
            (await runner.ConfirmSequenceIdleAsync(timeout.Token, requireSettledMotion: false).ConfigureAwait(false)).ToArray();
        targetsIdle = idleErrors.Length == 0;
        var settledErrors = !targetsIdle || runner is null ? idleErrors :
            (await runner.ConfirmSequenceIdleAsync(timeout.Token).ConfigureAwait(false)).ToArray();
        RecordUnsettledTargetBoundary(target, result.Run.ObservationRunId, settledErrors,
            runner?.CanRecheckSequenceIdle == true);
        var failedGate = NativeSequencePlan.TerminalFailure(result);
        var completed = result.Run.State == ObservationRunState.Completed && boundaryFailure is null;
        if (result.Run.State == ObservationRunState.Cancelled && !endRequested && !unsafeTripped) operatorCancelled = true;
        GateResult? clockRepair = null;
        var recoveryBoundaryEligible = result.Run.State == ObservationRunState.Faulted && boundaryFailure is null &&
            qualitySkipRequested && !operatorCancelled && targetsIdle && settledErrors.Length == 0 &&
            !unsafeTripped && !endRequested && runner is not null && failedPlan is not null && lifetime is { IsCancellationRequested: false } &&
            result.Run.RecentEvents.LastOrDefault()?.Code == failedGate?.Code && standbyPlan is not null && standbyPlan.StopGateNow() is null;
        if (recoveryBoundaryEligible && NightMountClockRecovery.IsClockFailure(failedGate))
        {
            using var recoveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime!.Token);
            recoveryTimeout.CancelAfter(TimeSpan.FromSeconds(90));
            try
            {
                clockRepair = await clockRecovery.TryRecoverAsync(failedGate, true,
                    _ => SaveJournalAsync("ClockRecoveryIntent"),
                    ct => runner!.RecoverMountClockAtIdleBoundaryAsync(failedPlan!, ct), recoveryTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                clockRepair = GateResult.Unknown("NIGHT_MOUNT_CLOCK_RECOVERY_CANCELLED", "校时取消或超时，未批准后续目标。");
            }
            // The earlier idle proof predates native disconnect/connect. Even
            // a timeout must rejoin those operations before standby or a new
            // target can own the mount; never reuse the pre-reconnect witness.
            using var clockStopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
            try
            {
                idleErrors = (await runner!.ConfirmSequenceIdleAsync(clockStopTimeout.Token, false).ConfigureAwait(false)).ToArray();
                targetsIdle = idleErrors.Length == 0;
                settledErrors = !targetsIdle ? idleErrors :
                    (await runner.ConfirmSequenceIdleAsync(clockStopTimeout.Token).ConfigureAwait(false)).ToArray();
            }
            catch (Exception ex)
            {
                targetsIdle = false;
                idleErrors = settledErrors = [$"NIGHT_MOUNT_CLOCK_RECOVERY_NOT_IDLE: {ex.Message}"];
            }
            RecordUnsettledTargetBoundary(target, result.Run.ObservationRunId, settledErrors, runner!.CanRecheckSequenceIdle);
            if (!targetsIdle || settledErrors.Length != 0)
                clockRepair = GateResult.Unknown("NIGHT_MOUNT_CLOCK_RECOVERY_NOT_IDLE", string.Join("; ", settledErrors));
        }
        var clockRecovered = clockRepair?.Disposition == GateDisposition.Passed &&
            !unsafeTripped && !endRequested && !operatorCancelled && lifetime?.IsCancellationRequested != true &&
            standbyPlan?.StopGateNow() is null;
        if (clockRecovered && session is not null && runner is not null &&
            session.SegmentRunIds.Contains(result.Run.ObservationRunId, StringComparer.Ordinal) &&
            session.TryContinueAfterClockRepair(runner.CaptureClockContinuationBudget(), DateTimeOffset.UtcNow))
        {
            clockContinuations.Add(new { target, runId = result.Run.ObservationRunId, result.ManifestPath,
                clockRepair, session.Accepted, session.Attempts, session.ReusedProbes, session.AcceptedSeconds,
                remaining = session.RemainingPlan(), budget = session.ClockBudget,
                currentFieldReceipt = session.ClockPositionCheck is { } check ? new
                { check.SourceRunId, check.Before, check.After, check.IssuedUtc,
                    check.OriginalRecoveryDeadlineUtc, check.MaximumSeparationArcseconds } : null,
                outcome = "ReacquireSameTargetAfterClockRecovery", oldOpticalAuthorityReused = false });
            NightState = $"{target} · 校时恢复，保留已接受帧和原预算，先尝试当前位置新图复核，必要时重新定位；失败再跳过。";
            await SaveJournalAsync("ClockContinuationIntent").ConfigureAwait(false);
            return true; // Not a terminal target outcome; do not increment/skip the native card.
        }
        GateResult? focusRepair = null;
        var focusCandidate = recoveryBoundaryEligible && IsSevereDefocusCandidate(result.Run.CurrentStage, failedGate);
        if (focusCandidate && !FocusBoundarySafe())
            focusRepair = GateResult.Unknown("NATIVE_FOCUS_SAFETY_CHANGED", "取场失败后的实时安全边界不再有效；不启动对焦或后续目标。");
        else if (focusCandidate && Volatile.Read(ref focusScanReservation) == 0 && session is not null && runner is not null &&
            session.SegmentRunIds.Contains(result.Run.ObservationRunId, StringComparer.Ordinal))
        {
            var budget = runner.CaptureClockContinuationBudget();
            if (session.TryBeginFocusRecovery(budget, DateTimeOffset.UtcNow))
            {
                var scanReservedHere = false;
                using var recoveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime!.Token);
                var remaining = session.RecoveryDeadline(budget) - DateTimeOffset.UtcNow;
                recoveryTimeout.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                try
                {
                    NightState = $"{target} · 严重失焦复核：先检查两张同星新帧，不移动；仍受原预算和安全链约束。";
                    await SaveJournalAsync("FocusDiagnosticIntent").ConfigureAwait(false);
                    focusRepair = await runner.TryRecoverSevereDefocusAtIdleBoundaryAsync(failedPlan!, async ct =>
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!FocusBoundarySafe() ||
                            !session.CanRecoverWithinOriginalBudget(budget, DateTimeOffset.UtcNow)) return false;
                        // Precharge before movement. A failed/ambiguous write never refunds it.
                        if (Interlocked.CompareExchange(ref focusScanReservation, 1, 0) != 0) return false;
                        scanReservedHere = true;
                        focusRecoveries.Add(new { target, runId = result.Run.ObservationRunId,
                            outcome = "FocusScanReserved", budget, deadlineUtc = session.RecoveryDeadline(budget) });
                        NightState = $"{target} · 原预算内自动对焦：本夜至多一次扫描；验证通过后重新取场，不沿用旧光学状态。";
                        await SaveJournalAsync("FocusRecoveryIntent").ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        return FocusBoundarySafe() && session.CanRecoverWithinOriginalBudget(budget, DateTimeOffset.UtcNow);
                    }, recoveryTimeout.Token).ConfigureAwait(false);
                    if (focusRepair?.Disposition == GateDisposition.Passed && !scanReservedHere)
                        focusRepair = GateResult.Unknown("NATIVE_FOCUS_SCAN_RESERVATION_MISSING", "对焦恢复未经过整夜唯一扫描预约，未批准重新取场。");
                }
                catch (Exception ex)
                {
                    focusRepair = GateResult.Unknown("NATIVE_FOCUS_RECOVERY_UNCONFIRMED", $"对焦恢复取消或发生异常，未批准后续目标：{ex.Message}");
                }
                // The old run's stop proof predates diagnostic exposures/focus motion.
                // Obtain a new owner proof even after cancel; never reuse the old one.
                using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
                try
                {
                    idleErrors = (await runner.ConfirmSequenceIdleAsync(stopTimeout.Token, false).ConfigureAwait(false)).ToArray();
                    targetsIdle = idleErrors.Length == 0;
                    settledErrors = !targetsIdle ? idleErrors :
                        (await runner.ConfirmSequenceIdleAsync(stopTimeout.Token).ConfigureAwait(false)).ToArray();
                }
                catch (Exception ex)
                {
                    targetsIdle = false;
                    idleErrors = settledErrors = [$"NATIVE_FOCUS_STOP_UNCONFIRMED: {ex.Message}"];
                }
                RecordUnsettledTargetBoundary(target, result.Run.ObservationRunId, settledErrors, runner.CanRecheckSequenceIdle);
                if (!targetsIdle || settledErrors.Length != 0)
                    focusRepair = GateResult.Unknown("NATIVE_FOCUS_STOP_UNCONFIRMED", $"对焦复核后的停止未确认，未批准后续目标：{string.Join("; ", settledErrors)}");
                else if (!FocusBoundarySafe())
                    focusRepair = GateResult.Unknown("NATIVE_FOCUS_SAFETY_CHANGED", "对焦恢复后的安全、截止或取消状态已改变，未批准后续目标。");
                focusRecoveries.Add(new { target, runId = result.Run.ObservationRunId, focusRepair,
                    scanReservedHere, session.FocusAttempted, session.Accepted, session.Attempts,
                    session.ReusedProbes, session.AcceptedSeconds, budget, idleErrors, settledErrors,
                    oldOpticalAuthorityReused = false });
                if (focusRepair?.Disposition == GateDisposition.Passed && targetsIdle && settledErrors.Length == 0 &&
                    session.TryContinueAfterFocusRepair(runner.CaptureClockContinuationBudget(), DateTimeOffset.UtcNow))
                {
                    NightState = $"{target} · 失焦恢复已验证，保留已接受帧和原预算，重新取场、入缝和导星；失败再跳过。";
                    await SaveJournalAsync("FocusContinuationIntent").ConfigureAwait(false);
                    return true;
                }
            }
        }
        var focusHardStop = focusRepair is { Disposition: not GateDisposition.Passed, FailureScope: not GateFailureScope.TargetQuality };
        var skip = result.Run.State == ObservationRunState.Faulted && boundaryFailure is null &&
            qualitySkipRequested && !focusHardStop && !operatorCancelled && targetsIdle && settledErrors.Length == 0 &&
            (NativeSequencePlan.IsSkippableQualityFailure(failedGate) || clockRecovered) &&
            result.Run.RecentEvents.LastOrDefault()?.Code == failedGate?.Code && !unsafeTripped && !endRequested &&
            lifetime is { IsCancellationRequested: false } && standbyPlan?.StopGateNow() is null;
        if (!completed && !skip) blocked = true;
        if (!targetsIdle || settledErrors.Length != 0) blocked = true;
        outcomes.Add(new { target, runId = result.Run.ObservationRunId, result.ManifestPath,
            outcome = completed ? "Completed" : clockRecovered ? "SkippedClockContinuationBudgetExhausted" : skip ? "SkippedQualityFailure" : "Stopped",
            clockRepair, clockRecoveryAttempted = clockRecovery.Attempted,
            focusRepair, focusAttempted = session?.FocusAttempted, focusContinuationUsed = session?.FocusContinuationUsed,
            gate = failedGate?.Code, failureScope = failedGate?.FailureScope.ToString(), boundaryFailure, idleErrors, settledErrors,
            qualitySkipRequested, endRequested, unsafeTripped, operatorCancelled,
            terminalCode = result.Run.RecentEvents.LastOrDefault()?.Code, result.AcquisitionProgress,
            // The final segment may fail during reacquisition, before it has a
            // local ATR progress object. Record already-counted logical totals,
            // not Aggregate(local), which would count this segment twice.
            retainedScience = session is null ? null : new
            {
                requestedFrames = session.Original.ScienceFrames,
                acceptedFrames = session.Accepted, attemptedFrames = session.Attempts,
                reusedProbeFrames = session.ReusedProbes, acceptedExposureSeconds = session.AcceptedSeconds,
                segmentRunIds = session.SegmentRunIds.ToArray(),
            } });
        NightState = $"目标 {attemptedTargets}/{Items.OfType<UvexTargetObservationContainer>().Count()}：{target} · " +
            (completed ? "完成" : focusHardStop ? $"对焦恢复停止（{focusRepair!.Code}）：{focusRepair.Message}" :
                clockRecovered ? "校时已恢复，但本目标无剩余恢复预算，保留原帧并跳过。" : skip ? $"质量失败，已确认停止并跳过（{failedGate?.Code}）" : $"停止（{boundaryFailure ?? failedGate?.Code ?? result.Run.State.ToString()}）");
        if (!completed && session is { Accepted: > 0 })
            NightState += $" · 已保留 {session.Accepted}/{session.Original.ScienceFrames} 张，累计 {session.AcceptedSeconds:0.##} 秒；剩余未完成。";
        await SaveJournalAsync("Targets").ConfigureAwait(false);
        if (!completed && (result.Run.State == ObservationRunState.Faulted ||
            boundaryFailure is not null || idleErrors.Length > 0 || settledErrors.Length > 0))
            host.NotifyNativeTargetBoundary(this, result.Run.ObservationRunId, target,
                failedGate?.Code ?? result.Run.State.ToString(), skip,
                boundaryFailure ?? (settledErrors.Length > 0 ? string.Join("; ", settledErrors) : focusHardStop ? focusRepair!.Code : null));
        return false;
    }

    private bool FocusBoundarySafe()
    {
        try
        {
            return !StopFollowingTargets && standbyPlan is not null && standbyPlan.StopGateNow() is null &&
                (lockedConfiguration is null || !settings.WeakSupervisionEnabled && factory.IsNightSafetyCurrent(lockedConfiguration));
        }
        catch { return false; } // An unreadable safety monitor cannot authorize focus movement.
    }

    internal static bool IsSevereDefocusCandidate(ObservationStage? stage, GateResult? gate) =>
        stage is ObservationStage.AcquireG3SlitField or ObservationStage.PlaceTargetOnSlit &&
        NativeSequencePlan.IsSkippableQualityFailure(gate) && gate!.Code is
            "G3_MAIN_FOCUS_UNVERIFIED" or "G3_CATALOG_SHORT_POSITION_UNCONFIRMED" or
            "G3_POST_WCS_SHORT_UNCONFIRMED" or "G3_POST_WCS_TARGET_NOT_MEASURED" or
            "G3_BOUNDED_SEARCH_EXHAUSTED_RETURNED";

    internal void RecordUnsettledTargetBoundary(string target, string? runId, IReadOnlyList<string> errors,
        bool canRecheckIdle = false)
    {
        if (errors.Count == 0) return;
        targetResponsibilitiesSettled &= canRecheckIdle;
        // Closing a roof or parking a mount is not evidence that a target's
        // interrupted motion/flip ledger was reconciled. Retain this separately
        // from physical idleness, which still permits emergency roof closeout.
        targetBoundaryFailures.Add(new(target, runId, errors.ToArray()));
        blocked = true;
    }

    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        NativeEndInstructionsReady = false;
        progress = ObservationStatusProgress.Wrap(progress);
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
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime = cancellation; endRequested = unsafeTripped = blocked = operatorCancelled = false;
        try
        {
            NightState = "正在开始本轮序列；尚未执行目标。";
            JournalPath = string.Empty; standbyJournalPath = null; lockedConfiguration = null;
            attemptedTargets = 0; targetsIdle = targetResponsibilitiesSettled = true; outcomes.Clear(); targetBoundaryFailures.Clear();
            clockRecovery = new();
            clockContinuations.Clear();
            focusScanReservation = 0; focusRecoveries.Clear();
            RefreshPlanEditability();
            JournalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UVEX-ADV", "nights", $"night-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.json");
            lockedConfiguration = real ? factory.CaptureConfiguration(settings) : null;
            using var reservation = await host.ReserveNightAsync(this, real, token,
                real ? () => factory.WatchNightSafety(OnNightUnsafe) : null,
                lockedConfiguration?.ActionConfigurationSha256).ConfigureAwait(false);
            await ExecuteReservedNightAsync(progress, token, cancellation, real).ConfigureAwait(false);
        }
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
        Exception? failure = null;
        Exception? reconciliationFailure = null;
        try
        {
            await SaveJournalAsync("Running").ConfigureAwait(false);
            // Native ordering, conditions, child statuses and template persistence.
            try { await base.Execute(progress, cancellation.Token).ConfigureAwait(false); }
            catch (Exception ex) { failure = ex; blocked = true; }
        }
        catch (Exception ex) { failure ??= ex; blocked = true; }
        finally
        {
            var ordinaryCancel = (token.IsCancellationRequested || operatorCancelled) && !endRequested && !unsafeTripped;
            var closeErrors = new List<string>();
            var physicalCloseoutConfirmed = false;
            var handedToStandby = false;
            // Cancellation stops observing, not weather protection. Transfer an
            // immutable request and exclusive lease; never await the standby task.
            if ((lockedHoldOpenAfterTargets || ordinaryCancel) && !endRequested && !unsafeTripped)
            {
                try
                {
                    var request = CreateStandbyRequest(real);
                    var standby = await host.BeginSafetyStandbyAsync(this, request).ConfigureAwait(false);
                    standbyJournalPath = standby.Snapshot.JournalPath;
                    handedToStandby = true;
                }
                catch (Exception ex) { closeErrors.Add("独立安全待机未能接管：" + ex.Message); }
            }
            if (!handedToStandby && !ordinaryCancel && targetsIdle)
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
            NightState = handedToStandby
                ? (ordinaryCancel ? "观测序列已取消（取消本身不执行机械收口）" : "观测序列已结束") +
                    "；独立安全待机已接管（AIWeather／晨光／原截止）。目标结果和未结责任仍保留，未声称关顶或全部观测成功。" +
                    (targetBoundaryFailures.Count > 0 ? " 观测运动/交接责任未结清，不能继续目标；原始账本保留。" : string.Empty) +
                    (closeErrors.Count > 0 ? " " + string.Join("；", closeErrors) : string.Empty)
                : DescribeTerminalState(real, ordinaryCancel, physicalCloseoutConfirmed, closeErrors, targetBoundaryFailures);
            outcomes.Add(new { terminal = NightState, closeErrors, unsafeTripped, ordinaryCancel,
                failure = failure?.Message, physicalHardware = real, physicalCloseoutConfirmed,
                handedToStandby, standbyJournalPath,
                targetBoundariesSettled = targetBoundaryFailures.Count == 0,
                targetBoundaryFailures = targetBoundaryFailures.ToArray() });
            await SaveJournalAsync("Terminal").ConfigureAwait(false);
            progress.Report(new ApplicationStatus { Source = "OpenAstroSpec 整夜", Status = NightState });
            if (closeErrors.Count > 0) throw new InvalidOperationException(NightState, failure);
            if (!ordinaryCancel && targetBoundaryFailures.Count > 0)
                reconciliationFailure = new InvalidOperationException(NightState);
            NativeEndInstructionsReady = !blocked && !endRequested && !unsafeTripped && !ordinaryCancel &&
                !token.IsCancellationRequested && !handedToStandby && targetsIdle &&
                closeErrors.Count == 0 && targetBoundaryFailures.Count == 0 &&
                (physicalCloseoutConfirmed || !real);
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
        foreach (var closeout in NativeNightEndArea.Choices(this)) closeout.RefreshPlanEditability();
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

    private NightSafetyStandbyRequest CreateStandbyRequest(bool real)
    {
        // No closure over mutable night options: the same native card can be
        // edited/reused as soon as Execute returns. The guardian keeps this run.
        var configuration = lockedConfiguration;
        var plan = standbyPlan!;
        var idle = targetsIdle;
        var closeConfiguration = configuration?.WithSequencePlan(plan);
        var timeoutSeconds = configuration is null ? 30 :
            configuration.Environment.RoofCloseoutTimeoutSeconds +
            configuration.Environment.OpticalCoverTransitionTimeoutSeconds + 180 +
            (plan.HomeBeforeParkOnFinalize ? plan.MountHomeTimeoutSeconds : 0);
        return new(real, configuration?.ActionConfigurationSha256 ?? string.Empty,
            plan.DeadlineUtc, JournalPath, idle && targetBoundaryFailures.Count == 0,
            callback => real ? factory.WatchNightSafety(callback) : null,
            () => configuration is not null && !factory.IsNightSafetyCurrent(configuration)
                ? GateResult.Unknown("NIGHT_SAFETY_UNAVAILABLE", "安全监视器不安全、断连或绑定改变。")
                : plan.StopGateNow(),
            async token =>
            {
                if (!idle) return new[] { "目标停止未确认；未发起机械收口。请人工核对设备。" };
                if (!real) return Array.Empty<string>();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                await using var closer = factory.Create(host, settings, new Progress<ApplicationStatus>(), closeConfiguration);
                return await closer.CloseSequenceNightAsync(timeout.Token).ConfigureAwait(false);
            }, idle ? null : " 停止未确认：目前仅保留监控与报警，机械收口未获准。请确认设备连接和停止后手动重试；未结运动责任不会自动清除。",
            real && configuration is not null && targetResponsibilitiesSettled
                ? token => factory.RecheckStandbyIdleAsync(configuration, token) : null);
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
            clockRecoveryAttempted = clockRecovery.Attempted, clockContinuations,
            focusScanReserved = Volatile.Read(ref focusScanReservation) != 0, focusRecoveries,
            outcomes }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(JournalPath + ".tmp", json).ConfigureAwait(false);
        File.Move(JournalPath + ".tmp", JournalPath, overwrite: true);
    }

    public override Task Interrupt()
    {
        if (lifetime is null) return Task.CompletedTask;
        operatorCancelled = true; lifetime?.Cancel(); closeoutLifetime?.Cancel(); host.Cancel();
        return Task.CompletedTask;
    }
    public override void ResetProgress()
    {
        NativeEndInstructionsReady = false;
        base.ResetProgress();
    }
    public override object Clone()
    {
        var clone = new UvexNightSequenceContainer(profiles, host, factory, settings)
        { StopAtDawn = StopAtDawn, DawnSunAltitudeDegrees = DawnSunAltitudeDegrees, EndAtIso8601 = EndAtIso8601,
            HomeBeforeParkOnFinalize = HomeBeforeParkOnFinalize, MountHomeTimeoutSeconds = MountHomeTimeoutSeconds,
            HoldOpenAfterTargets = EffectiveHoldOpenAfterTargets, InitialGuideSettlePixels = InitialGuideSettlePixels,
            InitialGuideStableSeconds = InitialGuideStableSeconds, InitialGuideTimeoutSeconds = InitialGuideTimeoutSeconds };
        clone.CopyMetaData(this);
        foreach (var item in Items) clone.Add((ISequenceItem)item.Clone());
        // Retain an external choice in the legacy fallback when copying only
        // this night. Do not synthesize another card that could compete with
        // the End area when this copy is placed into another complete sequence.
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
            if (owner.lifetime is null) { owner.host.RequestSafetyStandbyCloseout(); return; }
            owner.endRequested = true; owner.blocked = true;
            owner.lifetime.Cancel(); owner.host.Cancel();
        }
    }
}

internal sealed record NativeNightTargetBoundaryFailure(string Target, string? RunId, IReadOnlyList<string> Errors);
