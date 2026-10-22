using NINA.Astrometry;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner : INativeTargetBoundary
{
    void INativeTargetBoundary.AttachNativeTarget(NativeTargetSession? session) => AttachNativeTarget(session);
    void INativeTargetBoundary.RecordMeridianSegment(NativeTargetSession session, string runId) => RecordMeridianSegment(session, runId);
    ClockContinuationBudget INativeTargetBoundary.CaptureClockContinuationBudget() => CaptureClockContinuationBudget();
    Task<IReadOnlyList<string>> INativeTargetBoundary.ConfirmSequenceIdleAsync(CancellationToken token, bool requireSettledMotion) => ConfirmSequenceIdleAsync(token, requireSettledMotion);
    Task<GateResult> INativeTargetBoundary.RecoverMountClockAtIdleBoundaryAsync(ObservationPlan plan, CancellationToken token) => RecoverMountClockAtIdleBoundaryAsync(plan, token);
    internal NativeTargetSession? NativeTarget { get; private set; }
    bool INativeTargetBoundary.CanRecheckSequenceIdle => canRecheckSequenceIdle;
    private bool canRecheckSequenceIdle;
    private ClockContinuationBudget? retiredClockBudget;
    private ClockRecoveryFieldWorkflow? clockRecoveryField;

    internal void AttachNativeTarget(NativeTargetSession? session)
    {
        NativeTarget = session;
        NativeMeridian = session as NativeMeridianSession;
        clockRecoveryField = session?.ClockContinuationUsed == true
            ? new(session.ClockPositionCheck, CreateClockRecoveryFieldPort()) : null;
        if (session?.ClockBudget is not { } spent) return;
        fineAcquisitionStartedUtc = spent.FineStartedUtc;
        correctionAttempts = spent.CorrectionAttempts;
        cumulativeCorrectionDegrees = spent.CorrectionDegrees;
        phd2CoarseHandoffRecoveryAttempts = spent.CoarseHandoffAttempts;
        qhyMountCoordinateSyncPerformed = spent.CoordinateSyncAttempts;
        if (session.ClockContinuationUsed) mountClockRecoveryAttempted = 1; // Focus continuation is not a clock reconnect.
        atrProbe.InheritedAttempts = spent.ProbeAttempts;
    }

    internal ClockContinuationBudget CaptureClockContinuationBudget()
    {
        if (retiredClockBudget is { } retired) return retired;
        var old = NativeTarget?.ClockBudget;
        var g3 = durableG3AcquisitionMotion;
        var phd = pendingPhd2LockShift ?? phd2SettledHandoffBudget;
        return new(g3 is null ? old?.G3 : new(g3.CorrectionAttempts, g3.CumulativeMotionArcseconds,
                g3.MotionClockStartedUtc, g3.MaximumCorrectionAttempts, g3.MaximumCumulativeMotionArcseconds, g3.MaximumElapsedSeconds),
            phd is null ? old?.Phd2 : new(phd.AttemptsUsed, phd.CumulativeCommandedPixels,
                phd.MotionClockStartedUtc, phd.MaximumAttempts, phd.MaximumCumulativePixels, phd.MaximumElapsedSeconds),
            g3?.MaximumSingleCorrectionArcseconds ?? old?.G3Single ?? 0,
            g3?.MaximumRadiusArcseconds ?? old?.G3Radius ?? 0,
            fineAcquisitionStartedUtc, configuration.Commissioning.MaximumAcquisitionMinutes * 60,
            correctionAttempts, cumulativeCorrectionDegrees, atrProbe.InheritedAttempts + attemptedAtrProbeFrames,
            configuration.Atr.ExposureLadderSeconds.Where(x => double.IsFinite(x) && x > 0).Distinct().Count() + 1,
            phd2CoarseHandoffRecoveryAttempts, qhyMountCoordinateSyncPerformed);
    }

    internal async Task<GateResult> RecoverMountClockAtIdleBoundaryAsync(ObservationPlan failedPlan, CancellationToken token)
    {
        if (configuration.SequencePlan?.DeferObservatoryCloseout != true ||
            configuration.Environment.WeakSupervisionEnabled || failedPlan.ObservationRunId != observationRunId)
            return GateResult.Unknown("NIGHT_MOUNT_CLOCK_RECOVERY_SCOPE_INVALID", "没有同一整夜、同一失败运行的完整安全边界。");
        if (SequenceStopGate() is { } stop) return stop;
        var idle = await ConfirmSequenceIdleAsync(token).ConfigureAwait(false);
        if (idle.Count != 0) return GateResult.Unknown("NIGHT_MOUNT_CLOCK_RECOVERY_NOT_IDLE", string.Join("; ", idle));
        // The old runner will be disposed. A permitted continuation creates fresh
        // optical state through the same production route with retained debits.
        Report("目标已停止：由 N.I.N.A. 有界重连校时；成功且指向保持时先以新图复核，否则按原剩余预算重新定位。");
        var before = ReadClockRecoveryPositionSnapshot(ownersStoppedAndJoined: true);
        var repair = await EnsureMountClockSynchronizedAsync(new ObservationContext(failedPlan), token,
            verifiedIdleBoundary: true).ConfigureAwait(false);
        if (repair.Disposition != GateDisposition.Passed || NativeTarget is not { } session) return repair;
        // Independently confirm the post-reconnect owners/tasks, not just the
        // repaired UTC. This cleanup has no mechanical recovery authority.
        var afterIdle = await ConfirmSequenceIdleAsync(token).ConfigureAwait(false);
        if (afterIdle.Count != 0)
            return GateResult.Unknown("NIGHT_MOUNT_CLOCK_RECOVERY_NOT_IDLE", string.Join("; ", afterIdle));
        var after = ReadClockRecoveryPositionSnapshot(ownersStoppedAndJoined: true);
        session.ClockPositionCheck = ClockRecoveryPositionCheck.TryCreate(failedPlan.ObservationRunId,
            repair, before, after, DateTimeOffset.UtcNow, session.RecoveryDeadline(CaptureClockContinuationBudget()),
            MountCommandArrivalToleranceArcseconds, out var reason);
        await PublishRunJsonEvidenceAsync("clock-current-field-eligibility", "Clock repair current-field eligibility",
            new { reason, eligible = session.ClockPositionCheck is not null, before, after,
                recoveryDeadlineUtc = session.RecoveryDeadline(CaptureClockContinuationBudget()),
                reusedOpticalEvidence = false, motionIssued = false }, null, token).ConfigureAwait(false);
        return repair;
    }

    private GateResult? SequenceStopGate() => configuration.SequencePlan?.StopGate(DateTimeOffset.UtcNow,
        time => AstroUtil.GetSunAltitude(time.UtcDateTime, new ObserverInfo
        {
            Latitude = configuration.SequencePlan.LatitudeDegrees,
            Longitude = configuration.SequencePlan.LongitudeDegrees,
        }));

    internal async Task<IReadOnlyList<string>> ConfirmSequenceIdleAsync(CancellationToken token, bool requireSettledMotion = true)
    {
        canRecheckSequenceIdle = false;
        var errors = (await CleanupAfterFailureAsync("Native sequence target boundary", token,
            allowMechanicalActions: false).ConfigureAwait(false)).ToList();
        canRecheckSequenceIdle = errors.Count == 0 && activeQhyJobs.IsEmpty && pendingQhyRequests.IsEmpty &&
            (atrPreCoolingTask is null || atrPreCoolingTask.IsCompleted) && environmentSafetyShutdownTask.IsCompleted &&
            NativeMeridianJournalStore.PendingGate(NativeMeridianJournalPath) is null &&
            pendingSlitPlacement is null or { Phase: SlitPlacementPendingPhase.SettledBudgetLedger } &&
            pendingPhd2LockShift is null or { Phase: Phd2LockShiftPendingPhase.SettledBudgetLedger } &&
            durableG3AcquisitionMotion is null or { Phase: G3AcquisitionMotionPhase.SettledBudgetLedger };
        // Unconfirmed flip intent prohibits another target, not emergency
        // closeout after all owners and physical motion are confirmed idle.
        if (requireSettledMotion && NativeMeridianJournalStore.PendingGate(NativeMeridianJournalPath) is { } flipGate)
            errors.Add(flipGate.Message);
        var camera = cameraMediator.GetInfo();
        if (!camera.Connected) errors.Add("ATR 未连接，无法核验实际曝光状态；不代表已确认正在曝光。");
        else if (camera.IsExposing) errors.Add("ATR 仍报告正在曝光，尚未确认停止。");
        if (errors.Count == 0 && phd2.IsConnected)
        {
            var quiet = await Phd2MountIdleWait.WaitAfterTargetStopAsync(phd2, telescopeMediator,
                configuration.ExpectedTelescopeId, token).ConfigureAwait(false);
            await WriteAuditBestEffortAsync("native-target-mount-pulse-drain", new
            {
                quiet, maximumWaitSeconds = Phd2MountIdleWait.MaximumWait.TotalSeconds,
                quietSeconds = Phd2MountIdleWait.QuietPeriod.TotalSeconds, motionIssued = false,
            }).ConfigureAwait(false);
            if (quiet.Disposition != GateDisposition.Passed) errors.Add($"{quiet.Code}: {quiet.Message}");
        }
        var telescope = telescopeMediator.GetInfo();
        if (!telescope.Connected) errors.Add("赤道仪未连接，无法核验实际运动状态；不代表已确认正在运动。");
        else if (telescope.Slewing || telescope.IsPulseGuiding) errors.Add("赤道仪仍报告转向或导星脉冲，尚未确认停止。");
        if (focuserMediator.GetInfo().IsMoving || focuserMediator.GetInfo().IsSettling)
            errors.Add("主镜调焦尚未确认停止。");
        if ((requireSettledMotion || automaticFocusStopUnconfirmed) && automaticFocusBoundaryIssue is { } focusIssue)
            errors.Add("AUTO_FOCUS_BOUNDARY_UNSETTLED: " + focusIssue);
        if (startupGuideBoundaryIssue is { } startupIssue)
            errors.Add("GUIDE_STARTUP_STOP_UNCONFIRMED: " + startupIssue);
        if (activeQhyJobs.Count != 0 || pendingQhyRequests.Count != 0) errors.Add("测光作业尚未确认结束。");
        if (requireSettledMotion && (pendingSlitPlacement is { Phase: not SlitPlacementPendingPhase.SettledBudgetLedger } ||
            pendingPhd2LockShift is { Phase: not Phd2LockShiftPendingPhase.SettledBudgetLedger } ||
            durableG3AcquisitionMotion is { Phase: not G3AcquisitionMotionPhase.SettledBudgetLedger }))
            errors.Add("运动回程责任未结清，禁止跳目标或声称收口成功。");
        canRecheckSequenceIdle = canRecheckSequenceIdle && automaticFocusBoundaryIssue is null && startupGuideBoundaryIssue is null &&
            mountClockReconnectBoundary.BoundaryGate is null;
        return errors;
    }

    // The night owner calls this only after all target tasks/owners have joined.
    // Reuse the exact selected-adapter, commissioned roof-clearance and checked-terminal
    // implementation used by the single-target production route.
    internal async Task<IReadOnlyList<string>> CloseSequenceNightAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (mountClockReconnectBoundary.BoundaryGate is { } clockOwnerGate)
            return new[] { $"{clockOwnerGate.Code}: {clockOwnerGate.Message}" };
        var errors = new List<string>();
        if (!cameraMediator.GetInfo().Connected || cameraMediator.GetInfo().IsExposing ||
            !telescopeMediator.GetInfo().Connected || telescopeMediator.GetInfo().Slewing || telescopeMediator.GetInfo().IsPulseGuiding)
            return new[] { "设备仍忙；本次未发起整夜机械收口。" };
        // GuiderInfo has no live capture state. Use the existing bounded,
        // read-only PHD2 RPC check, including for a newly created standby closer.
        if (!guiderMediator.GetInfo().Connected ||
            focuserMediator.GetInfo().IsMoving || focuserMediator.GetInfo().IsSettling ||
            !await IndependentRoofGuiderIdleAsync(token).ConfigureAwait(false))
            return new[] { "导星或调焦未确认空闲；本次未发起整夜机械收口。" };
        // The explicit night closeout is independent of single-run preferences.
        // The night decides WHEN to call; this interface always checks a full closeout.
        if (!configuration.Environment.WeakSupervisionEnabled)
        {
            var issue = await CloseOpticalCoverAsync("Native sequence night closeout", token).ConfigureAwait(false);
            if (issue is not null) errors.Add(issue);
        }
        if (!configuration.Environment.WeakSupervisionEnabled)
        {
            token.ThrowIfCancellationRequested();
            var issue = await ParkMountAndCloseDomeOrRoofAsync("Native sequence night closeout", token).ConfigureAwait(false);
            if (issue is not null) errors.Add(issue);
        }
        await WriteAuditBestEffortAsync("native-sequence-night-closeout", new
        {
            errors, cover = flatDeviceMediator.GetInfo().CoverState.ToString(),
            parked = telescopeMediator.GetInfo().AtPark, roof = domeMediator.GetInfo().ShutterStatus.ToString(),
            weakSupervision = configuration.Environment.WeakSupervisionEnabled,
        }).ConfigureAwait(false);
        return errors;
    }
}
