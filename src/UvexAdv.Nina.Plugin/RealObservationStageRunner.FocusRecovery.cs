using System.IO;
using System.Text.Json;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private readonly Guid focusRecoveryProfileId;
    private string? automaticFocusBoundaryIssue;
    private bool automaticFocusStopUnconfirmed;

    Task<GateResult?> INativeTargetBoundary.TryRecoverSevereDefocusAtIdleBoundaryAsync(
        ObservationPlan plan, Func<CancellationToken, Task<bool>> reserveScan, CancellationToken token) =>
        TryRecoverSevereDefocusAtIdleBoundaryAsync(plan, reserveScan, token);

    private async Task<GateResult?> TryRecoverSevereDefocusAtIdleBoundaryAsync(
        ObservationPlan failedPlan, Func<CancellationToken, Task<bool>> reserveScan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (configuration.SequencePlan?.DeferObservatoryCloseout != true || configuration.Environment.WeakSupervisionEnabled
            || failedPlan.ObservationRunId != observationRunId || disposed || NativeTarget is null)
            return null;
        SepMainFocusOptions? saved;
        try { saved = JsonSerializer.Deserialize<SepMainFocusOptions>(configuration.MainFocusOptionsJson); }
        catch (JsonException) { return null; }
        if (saved is not { AutomaticRecoveryEnabled: true, ConfigurationIssue: null }) return null;
        var bindings = nightSetup?.Value.FocusDomains?.Where(b => b.Role == FocusDomainRole.C11Main).ToArray();
        if (bindings is not { Length: 1 } || bindings[0].Owner != FocusDomainConventions.C11Owner
            || bindings[0].LogicalDeviceId != FocusDomainConventions.C11LogicalDeviceId
            || bindings[0].Limits is not { } limits
            || AutomaticSepFocusBoundary.Constrain(saved, limits) is not { } options)
        {
            await WriteAuditBestEffortAsync("auto-focus-not-authorized", new
            { reason = "Saved focus settings and commissioned C11 motion limits have no valid intersection.", motionIssued = false }).ConfigureAwait(false);
            return null;
        }
        var original = NativeTarget.TargetPlan ?? failedPlan;
        var deadline = original.PlannedStartUtc + original.PlannedDuration;
        var sequence = configuration.SequencePlan;
        if (sequence.TargetDeadlineUtc is { } targetEnd && targetEnd < deadline) deadline = targetEnd;
        if (sequence.DeadlineUtc is { } nightEnd && nightEnd < deadline) deadline = nightEnd;
        var debit = CaptureClockContinuationBudget();
        foreach (var clock in new[] { debit.G3, debit.Phd2 })
            if (clock is not null && clock.StartedUtc.AddSeconds(clock.MaximumSeconds) < deadline)
                deadline = clock.StartedUtc.AddSeconds(clock.MaximumSeconds);
        if (debit.FineStartedUtc is { } fine && fine.AddSeconds(debit.FineMaximumSeconds) < deadline)
            deadline = fine.AddSeconds(debit.FineMaximumSeconds);
        // Both diagnostic exposures and the runner's normal return/stop reserve
        // consume the ORIGINAL clocks. No new target/acquisition window exists.
        var diagnosticSeconds = 2 * (options.ExposureMilliseconds / 1000d + 15);
        if ((deadline - DateTimeOffset.UtcNow).TotalSeconds < diagnosticSeconds + 60 + AutomaticSepFocusBoundary.ReturnReserveSeconds)
        {
            await WriteAuditBestEffortAsync("auto-focus-not-attempted", new
            { reason = "原预算不足以完成两帧诊断、最小扫焦和停止回程预留。", deadline, diagnosticSeconds, motionIssued = false }).ConfigureAwait(false);
            return null;
        }
        var runtime = NinaSepMainFocusHardware.ReadOptionalRuntime(NinaSepMainFocusHardware.RuntimePath, out var runtimeIssue);
        if (runtime is null)
        {
            Report("本目标不尝试自动对焦：可选 SEP 对焦环境不可用；保持原失败跳目标规则。");
            await WriteAuditBestEffortAsync("auto-focus-not-attempted", new
            { reason = "Optional SEP focus runtime unavailable", runtimeIssue, motionIssued = false }).ConfigureAwait(false);
            return null;
        }
        var idle = await ConfirmSequenceIdleAsync(token).ConfigureAwait(false);
        if (idle.Count != 0 || !canRecheckSequenceIdle)
            return GateResult.Unknown("AUTO_FOCUS_BOUNDARY_NOT_IDLE", string.Join("; ", idle));
        var focusInfo = focuserMediator.GetInfo();
        if (!focusInfo.Connected || focusInfo.DeviceId != bindings[0].LogicalDeviceId || focusInfo.IsMoving || focusInfo.IsSettling)
            return GateResult.Unknown("AUTO_FOCUS_FOCUSER_NOT_IDLE", "主镜连接、身份及停止状态未确认，不启动自动焦点诊断。");
        var focusSettings = profileService.ActiveProfile.FocuserSettings;
        var mountInfo = telescopeMediator.GetInfo();
        if (focusInfo.TempComp || focusSettings.BacklashCompensationModel == NINA.Core.Enum.BacklashCompensationModel.ABSOLUTE
            || double.IsFinite(mountInfo.Altitude) && mountInfo.Altitude < options.MinimumAltitude)
        {
            Report("本目标不尝试自动对焦：高度或已保存调焦模式不适用；没有改变设置或移动。");
            await WriteAuditBestEffortAsync("auto-focus-not-attempted", new
            { reason = "Optional focus applicability: altitude, automatic temperature compensation or absolute backlash model.",
                mountInfo.Altitude, options.MinimumAltitude, focusInfo.TempComp, focusSettings.BacklashCompensationModel,
                motionIssued = false }).ConfigureAwait(false);
            return null;
        }
        var origin = focusInfo.Position;
        var context = new ObservationContext(failedPlan);
        Task Safety(CancellationToken ct)
        {
            token.ThrowIfCancellationRequested();
            ct.ThrowIfCancellationRequested();
            if (SequenceStopGate() is { } stop) throw new PhysicalActionGateException(stop);
            if (DateTimeOffset.UtcNow >= deadline) throw new OperationCanceledException("原目标/采集预算到达，停止自动对焦。", ct);
            if (profileService.ActiveProfile.Id != focusRecoveryProfileId)
                throw new PhysicalActionGateException(GateResult.Unknown("AUTO_FOCUS_PROFILE_CHANGED", "自动对焦不能跨 N.I.N.A. Profile。"));
            // This excludes C11RunFocusLock deliberately: the prior optical
            // segment is retired. It checks the unchanged owners, commissioning,
            // selected safety/weather/roof/cover, horizon and mount clock, not a
            // stale expected focus position or this scan's own IsMoving state.
            context.RemainingWorstCaseDuration = deadline - DateTimeOffset.UtcNow;
            var gate = ValidateImmediatePhysicalActionGates(context);
            if (gate.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(gate);
            var camera = cameraMediator.GetInfo();
            var mount = telescopeMediator.GetInfo();
            if (!camera.Connected || camera.DeviceId != configuration.NightSetup.AtrStableId
                || camera.IsExposing || camera.LiveViewEnabled || !mount.Connected
                || mount.DeviceId != configuration.ExpectedTelescopeId || mount.Slewing || mount.IsPulseGuiding)
                throw new PhysicalActionGateException(GateResult.Unknown("AUTO_FOCUS_DEVICE_BOUNDARY_CHANGED", "科学相机或赤道仪停止边界改变。"));
            return Task.CompletedTask;
        }
        await Safety(token).ConfigureAwait(false);
        await using var hardware = new NinaSepMainFocusHardware(profileService, focuserMediator, telescopeMediator,
            cameraMediator, domeMediator, flatDeviceMediator, safetyMonitorMediator, imageDataFactory, settings, options, runtime);
        var controlled = new AutomaticSepFocusBoundary(hardware, limits, options, Safety,
            hardware.StopAndConfirmAsync, reserveScan, deadline, token);
        SepMainFocusResult? result = null;
        GateResult? failure = null;
        async Task<SepMainFocusResult?> DiagnoseAndRunAsync()
        {
            var initial = await controlled.ReadReadyAsync(token).ConfigureAwait(false);
            long travel;
            try { travel = AutomaticSepFocusBoundary.RequiredTravel(options, initial, limits); }
            catch (InvalidOperationException ex)
            {
                await WriteAuditBestEffortAsync("auto-focus-not-attempted", new
                { reason = ex.Message, options, limits, initial, motionIssued = false }).ConfigureAwait(false);
                Report("本目标不尝试自动对焦：保存的扫焦范围超出已标定范围；没有移动。");
                return null;
            }
            if (initial.Position != origin)
                throw new InvalidOperationException("诊断前焦位已被外部改变，未覆盖人工状态。");
            if (Math.Max(initial.BacklashIn, initial.BacklashOut) > limits.BacklashCompensationSteps
                || travel > limits.MaximumCumulativeMoveSteps)
            {
                await WriteAuditBestEffortAsync("auto-focus-not-attempted", new
                { reason = "保存的扫焦与原生回差所需动作超出已标定单段/累计/回差限额。", options, limits, initial,
                    requiredTravel = travel, motionIssued = false }).ConfigureAwait(false);
                Report("本目标不尝试自动对焦：原生回差或扫焦行程超出已标定动作预算；没有移动。");
                return null;
            }
            var firstPath = ReserveRunEvidencePath("g3-severe-defocus-first", ".fit");
            var first = await controlled.CaptureAsync(firstPath, null, token).ConfigureAwait(false);
            _ = await controlled.ReadReadyAsync(token).ConfigureAwait(false);
            var firstHash = await ComputeFileSha256Async(firstPath, token).ConfigureAwait(false);
            var secondPath = ReserveRunEvidencePath("g3-severe-defocus-second", ".fit");
            var second = await controlled.CaptureAsync(secondPath, first.Reference, token).ConfigureAwait(false);
            var finalDiagnostic = await controlled.ReadReadyAsync(token).ConfigureAwait(false);
            var secondHash = await ComputeFileSha256Async(secondPath, token).ConfigureAwait(false);
            if (finalDiagnostic.Position != origin || SameHash(firstHash, secondHash)
                || !SameHash(firstHash, await ComputeFileSha256Async(firstPath, token).ConfigureAwait(false)))
                throw new InvalidDataException("严重离焦诊断不是同焦位的两张不可变独立新帧。");
            var decision = SevereDefocusPolicy.Evaluate(first, second);
            await PublishRunJsonEvidenceAsync("g3-severe-defocus-diagnostic", "Fixed-star severe-defocus candidate; not focus or target proof",
                new { firstPath, firstHash, first, secondPath, secondHash, second, decision, origin, options,
                    limits, deadline, debit, motionIssued = false, budgetReset = false }, secondPath, token).ConfigureAwait(false);
            if (!decision.ShouldAttemptFocus) return null;
            var seconds = Math.Min(options.MaximumSeconds,
                (int)Math.Floor((deadline - DateTimeOffset.UtcNow).TotalSeconds) - AutomaticSepFocusBoundary.ReturnReserveSeconds);
            if (seconds < 60) return null;
            options = options with { MaximumSeconds = seconds };
            var directory = Path.Combine(Path.GetDirectoryName(firstPath)!, "main-focus-" + Guid.NewGuid().ToString("N"));
            Report("同一星群两张新帧显示严重宽化：在原预算内尝试一次主镜自动对焦；保留完整安全链，失败不放宽目标形态门限。");
            var completed = await new SepMainFocusRunner(controlled).RunAsync(options, directory,
                new Progress<string>(message => Report(message)), token).ConfigureAwait(false);
            await WriteAuditBestEffortAsync("auto-focus-recovery-result", new
            { result = completed, controlled.ScanReserved, controlled.ChargedSteps, deadline, debit, oldFocusLockReset = false, originalNightSetupRewritten = false }).ConfigureAwait(false);
            return completed;
        }
        try { result = await DiagnoseAndRunAsync().ConfigureAwait(false); }
        catch (PhysicalActionGateException ex) { failure = ex.Gate; }
        catch (SepFocusMeasurementException ex) { failure = GateResult.Unknown("AUTO_FOCUS_MEASUREMENT_UNAVAILABLE", ex.Message); }
        catch (OperationCanceledException) { failure = GateResult.Unknown("AUTO_FOCUS_CANCELLED", "取消或原截止到达，停止新动作；未批准续拍。"); }
        catch (Exception ex) { failure = GateResult.Unknown("AUTO_FOCUS_OWNER_OR_DIAGNOSTIC_FAILED", ex.Message); }
        // The shared manual runner records failures and tries a bounded return.
        // That result must not erase an automatic safety/owner failure it caught.
        if (controlled.BoundaryFailure is PhysicalActionGateException physical) failure = physical.Gate;
        else if (controlled.BoundaryFailure is { } ownerFailure)
            failure = GateResult.Unknown("AUTO_FOCUS_OWNER_OR_STOP_CHANGED", ownerFailure.Message);
        if (controlled.UnjoinedOperation is { } pending)
        {
            hardware.PendingBoundaryOperation = pending;
            automaticFocusStopUnconfirmed = true;
            automaticFocusBoundaryIssue = "自动对焦完整设备/测量操作未结清。";
        }
        // Normal and exceptional paths both need explicit owner stop proof.
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        IReadOnlyList<string> stopErrors;
        try { stopErrors = await hardware.StopAndConfirmAsync(stopTimeout.Token).ConfigureAwait(false); }
        catch (Exception ex) { stopErrors = [ex.Message]; }
        if (stopErrors.Count > 0)
        {
            automaticFocusStopUnconfirmed = true;
            automaticFocusBoundaryIssue = string.Join("; ", stopErrors);
            return GateResult.Unknown("AUTO_FOCUS_STOP_UNCONFIRMED", string.Join("; ", stopErrors));
        }
        if (controlled.UnjoinedOperation is not null)
            return GateResult.Unknown("AUTO_FOCUS_OPERATION_UNJOINED", automaticFocusBoundaryIssue!);
        var finalFocus = focuserMediator.GetInfo();
        var outcome = AutomaticSepFocusBoundary.ClassifyOutcome(result, failure, origin, finalFocus.Position,
            finalFocus.Connected && finalFocus.DeviceId == bindings[0].LogicalDeviceId && !finalFocus.IsMoving && !finalFocus.IsSettling,
            DateTimeOffset.UtcNow >= deadline, token.IsCancellationRequested, SequenceStopGate());
        if (outcome?.Disposition == GateDisposition.Passed)
            await Safety(token).ConfigureAwait(false);
        if (outcome?.Code is "AUTO_FOCUS_POSITION_UNCONFIRMED" or "AUTO_FOCUS_ORIGIN_UNCONFIRMED"
            || finalFocus.Position != origin && result?.FocusVerified != true)
            automaticFocusBoundaryIssue = "已确认物理停止，但自动对焦最终位置/原位责任尚未结清。";
        return outcome;
    }
}
