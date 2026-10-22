using System.Runtime.ExceptionServices;
using NINA.Core.Enum;
using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    // Restricts the existing capture path; it grants no cover or mount movement.
    private bool clockCurrentFieldProbeActive;
    private volatile bool clockCurrentFieldOwnerUncertain;

    private GateResult RejectClockCurrentFieldPhdOwner(string reason)
    {
        // Sticky across probe completion and generic fault cleanup: a newer
        // preview/guide session is not ours merely because this client connects.
        clockCurrentFieldOwnerUncertain = true;
        startupGuideBoundaryIssue ??= reason;
        return GateResult.Unknown("CLOCK_CURRENT_FIELD_OWNER_CHANGED", reason);
    }

    private IClockRecoveryFieldPort CreateClockRecoveryFieldPort() => new ClockRecoveryFieldPort(this);

    private ClockRecoveryPositionSnapshot ReadClockRecoveryPositionSnapshot(bool ownersStoppedAndJoined)
    {
        var mount = telescopeMediator.GetInfo();
        var guide = phd2.Snapshot;
        var target = NativeTarget?.TargetPlan?.Target ?? new EquatorialTarget("", "", double.NaN, double.NaN);
        G3FrameMountReadback position;
        try { position = CaptureG3FrameMountReadback(); }
        catch (Exception)
        {
            // An unreadable optional witness declines the optimization; it
            // must neither reuse a stale coordinate nor prevent clock repair.
            position = new(double.NaN, double.NaN, "", mount.SideOfPier.ToString(), DateTimeOffset.UtcNow);
            ownersStoppedAndJoined = false;
        }
        return new(new(target, NativeTarget?.SharedActionConfigurationSha256 ?? "",
                commissioning?.Sha256 ?? "", mount.DeviceId ?? "", profileService.ActiveProfile.Id,
                $"{configuration.Phd2Host}:{configuration.Phd2Port}", guide.Profile?.Id ?? -1,
                guide.Profile?.Name ?? "", configuration.Phd2.CameraStableId),
            position, mount.Connected,
            !mount.Slewing && !mount.IsPulseGuiding, mount.TrackingEnabled,
            ownersStoppedAndJoined, mount.AtPark);
    }

    internal static bool ClockCurrentFieldAllowsOpticalFallback(GateResult gate) =>
        gate.Code == "G3_MAIN_FOCUS_UNVERIFIED" && gate.FailureScope == GateFailureScope.TargetQuality ||
        IsRecoverableG3SearchGate(gate) || gate.Code is
        "G3_CLOUD_OR_TRANSPARENCY_INVALID" or "G3_CATALOG_SHORT_POSITION_UNCONFIRMED" or
        "G3_POST_WCS_SHORT_UNCONFIRMED" or "G3_POST_WCS_TARGET_NOT_MEASURED" or
        "G3_COARSE_HANDOFF_REQUIRED" or G3PostWcsShortHandoffPolicy.OutsideCode or
        G3SolveProbeContentAnalyzer.OverexposedGateCode or
        "G3_FOCUS_STARS_NOT_DETECTED" or "G3_FOCUS_STARS_UNUSABLE" or "G3_FOCUS_SATURATED" or
        "G3_FOCUS_STARS_INSUFFICIENT" or "G3_FOCUS_STARS_TOO_BROAD" or "G3_FOCUS_STARS_TOO_ELONGATED" or
        "G3_FOCUS_SHAPE_INCONSISTENT" or "G3_FOCUS_CONFIDENCE_LOW" or
        "SLIT_LOCUS_LOW_CONFIDENCE" or "SLIT_LOCUS_LOCAL_BACKGROUND_LOW_CONFIDENCE" or
        "SLIT_LED_PAIR_LOW_CONTRAST" or "SLIT_LED_PAIR_NOT_THROUGHGOING";

    private sealed class ClockRecoveryFieldPort(RealObservationStageRunner run) : IClockRecoveryFieldPort
    {
        private bool stoppedAndJoined;
        private Phd2IdentityValidation? owner;
        private long? ownerEpoch;
        private ClockRecoveryPositionSnapshot? capturePosition;
        private DateTimeOffset captureStartedUtc;
        private G3FieldState? freshField;
        private string? intentPath;
        private NativeCameraCaptureLease? captureLease;
        private readonly object idleCaptureProbe = new();

        private object? NativeCaptureOwner => run.NativeTarget is not null ? run.nativeSequenceCaptureOwner?.Invoke() : null;
        private DateTimeOffset Deadline => run.NativeTarget?.ClockPositionCheck?.OriginalRecoveryDeadlineUtc ?? DateTimeOffset.MinValue;

        public ClockRecoveryPositionSnapshot ReadSnapshot() => run.ReadClockRecoveryPositionSnapshot(stoppedAndJoined);

        // No CleanupAfterFailure/ConfirmSequenceIdle call here: the previous
        // segment has already joined. This is a read-only proof for the NEW run.
        public async Task<GateResult> ValidateStoppedBoundaryAsync(ObservationContext context, CancellationToken token)
        {
            stoppedAndJoined = false;
            token.ThrowIfCancellationRequested();
            var gate = CheckPhysicalBoundary(context, requireIdleIllumination: true);
            if (gate.Disposition != GateDisposition.Passed) return gate;
            var (identity, state) = await ReadPhdStoppedIdentityAsync(token).ConfigureAwait(false);
            if (!identity.IsValid || !run.phd2.Snapshot.IsConnected || state != Phd2AppState.Stopped ||
                ownerEpoch is { } epoch && (run.phd2.Snapshot.ConnectionEpoch != epoch ||
                    identity.Profile != owner?.Profile || identity.Equipment != owner?.Equipment))
                return run.RejectClockCurrentFieldPhdOwner("当前位置复核需要同一 PHD2 所有者的新鲜身份及原生停止状态；不以缓存空闲放行。");
            var profile = run.ValidatePhdProfileBindingEvidence();
            if (profile.Disposition != GateDisposition.Passed)
            {
                run.RejectClockCurrentFieldPhdOwner(profile.Message);
                return profile;
            }
            if (!string.IsNullOrWhiteSpace(context.Plan.ExpectedQhyCameraId))
            {
                var service = await run.ValidateQhyServiceConfigurationAsync(context.Plan, token).ConfigureAwait(false);
                if (service.Disposition != GateDisposition.Passed) return service;
                var camera = await run.qhy.GetCameraAsync(token).ConfigureAwait(false);
                if (camera is null)
                    return GateResult.Unknown("CLOCK_CURRENT_FIELD_REMOTE_OWNER_CHANGED", "测光所有者没有返回当前相机状态，未证明全局空闲。");
                if ((camera.Connected || camera.Identity is not null) &&
                    !string.Equals(camera.Identity?.StableId, context.Plan.ExpectedQhyCameraId, StringComparison.Ordinal) ||
                    camera.TimestampUtc > DateTimeOffset.UtcNow || DateTimeOffset.UtcNow - camera.TimestampUtc > TimeSpan.FromSeconds(10))
                    return GateResult.Unknown("CLOCK_CURRENT_FIELD_REMOTE_OWNER_CHANGED", "测光所有者的新鲜只读身份不匹配，不能以新运行的空作业字典证明旧任务已停止。");
                var idle = await run.qhy.ReadGlobalIdleAsync(token).ConfigureAwait(false);
                if (idle.Disposition != GateDisposition.Passed) return idle;
            }
            gate = CheckPhysicalBoundary(context, requireIdleIllumination: true);
            if (gate.Disposition != GateDisposition.Passed) return gate;
            // Re-read after the remote proof; an RPC round trip is not an owner lock.
            var (after, afterState) = await ReadPhdStoppedIdentityAsync(token).ConfigureAwait(false);
            if (!after.IsValid || after.Profile != identity.Profile || after.Equipment != identity.Equipment ||
                afterState != Phd2AppState.Stopped)
                return run.RejectClockCurrentFieldPhdOwner("停止证明期间 PHD2 身份或状态改变，未放行复核。");
            if (ownerEpoch is { } previous && run.phd2.Snapshot.ConnectionEpoch != previous)
                return run.RejectClockCurrentFieldPhdOwner("复核前后 PHD2 连接历元改变，不能继承本次证据。");
            owner ??= after;
            ownerEpoch ??= run.phd2.Snapshot.ConnectionEpoch;
            gate = CheckPhysicalBoundary(context, requireIdleIllumination: true);
            stoppedAndJoined = gate.Disposition == GateDisposition.Passed;
            return gate;
        }

        private async Task<(Phd2IdentityValidation Identity, Phd2AppState State)> ReadPhdStoppedIdentityAsync(CancellationToken token)
        {
            try
            {
                var identity = await run.phd2.ValidateIdentityAsync(run.PhdIdentityRequirement(), token).ConfigureAwait(false);
                var state = await run.phd2.GetAppStateAsync(token).ConfigureAwait(false);
                return (identity, state);
            }
            catch (Exception)
            {
                run.RejectClockCurrentFieldPhdOwner("当前位置复核未能读回原 PHD2 身份及停止状态；通用清理不接管其他会话。");
                throw;
            }
        }

        private GateResult CheckPhysicalBoundary(ObservationContext context, bool requireIdleIllumination)
        {
            if (run.SequenceStopGate() is { } stop) return stop;
            if (run.startupGuideBoundaryIssue is { } startup)
                return GateResult.Unknown("GUIDE_STARTUP_STOP_UNCONFIRMED", startup);
            if (run.mountClockReconnectBoundary.BoundaryGate is { } clock) return clock;
            if (run.automaticFocusBoundaryIssue is { } focusIssue)
                return GateResult.Unknown("AUTO_FOCUS_BOUNDARY_UNSETTLED", focusIssue);
            if (run.startupGuidePendingOperation is { IsCompleted: false } ||
                !run.environmentSafetyShutdownTask.IsCompleted || run.environmentSafetyTrip is not null)
                return GateResult.Unknown("CLOCK_CURRENT_FIELD_STOP_UNCONFIRMED", "所有者或安全收口任务仍未结束，不能开始采集或回退运动。");
            var ownership = run.host.RealRunOwnershipGate();
            if (ownership.Disposition != GateDisposition.Passed) return ownership;
            var immediate = run.ValidateImmediatePhysicalActionGates(context);
            if (immediate.Disposition != GateDisposition.Passed) return immediate;
            var camera = run.cameraMediator.GetInfo();
            var mount = run.telescopeMediator.GetInfo();
            var focus = run.focuserMediator.GetInfo();
            if (!camera.Connected || camera.DeviceId != run.configuration.NightSetup.AtrStableId ||
                camera.IsExposing || camera.LiveViewEnabled || camera.CameraState is not (CameraStates.Idle or CameraStates.NoState) ||
                (captureLease is not null ? !captureLease.IsCurrent(NativeCaptureOwner) :
                    !run.cameraMediator.IsFreeToCapture(NativeCaptureOwner ?? idleCaptureProbe)) ||
                !mount.Connected || mount.DeviceId != run.configuration.ExpectedTelescopeId || mount.AtPark ||
                mount.Slewing || mount.IsPulseGuiding || !mount.TrackingEnabled ||
                mount.SideOfPier.ToString() is not ("pierEast" or "pierWest") ||
                !focus.Connected || focus.IsMoving || focus.IsSettling ||
                !run.activeQhyJobs.IsEmpty || !run.pendingQhyRequests.IsEmpty)
                return GateResult.Unknown("CLOCK_CURRENT_FIELD_STOP_UNCONFIRMED", "科学相机、主镜调焦、赤道仪或测光任务的原所有权/实际停止边界不成立。");
            if (run.nightSetup is null)
                return GateResult.Unknown("NIGHT_SETUP_SNAPSHOT_UNTRUSTED", "本段缺少锁定的光学设置。");
            var mainFocus = run.c11RunFocusLock.Validate(run.ReadC11MainFocusOwner(), run.nightSetup.Value);
            if (mainFocus.Disposition != GateDisposition.Passed) return mainFocus;
            if (NativeMeridianJournalStore.PendingGate(run.NativeMeridianJournalPath) is { } flip) return flip;
            if (run.pendingSlitPlacement is { Phase: not SlitPlacementPendingPhase.SettledBudgetLedger } ||
                run.pendingPhd2LockShift is { Phase: not Phd2LockShiftPendingPhase.SettledBudgetLedger } ||
                run.durableG3AcquisitionMotion is { Phase: not G3AcquisitionMotionPhase.SettledBudgetLedger } ||
                run.pendingQhyCoarseReturn is not null || run.pendingG3SearchReturn is not null)
                return GateResult.Unknown("CLOCK_CURRENT_FIELD_MOTION_UNSETTLED", "仍有未结清的运动/回程责任，不能使用当前位置复核或回退运动。");
            if (run.slitIlluminationSafetyIssue is { } illumination)
                return GateResult.Unknown("SLIT_ILLUMINATION_STOP_UNCONFIRMED", illumination);
            if (requireIdleIllumination && (run.activeSlitIlluminationLease is not null || run.activeSlitIlluminationSequenceId is not null))
                return GateResult.Unknown("SLIT_ILLUMINATION_STOP_UNCONFIRMED", "新段 LED 测量尚未结束并确认关灯。");
            if (ownerEpoch is { } epoch && (!run.phd2.Snapshot.IsConnected || run.phd2.Snapshot.ConnectionEpoch != epoch))
                return run.RejectClockCurrentFieldPhdOwner("当前位置复核期间 PHD2 所有者连接改变。");
            if (capturePosition is { } position && PositionContinuityGate(position) is { } changed) return changed;
            if (DateTimeOffset.UtcNow >= Deadline || run.NativeTarget?.ClockBudget?.HasRemainingBudget(DateTimeOffset.UtcNow) != true)
                return BudgetExhausted();
            return GateResult.Pass("CLOCK_CURRENT_FIELD_STOPPED_BOUNDARY_VALID", "原所有者、实际停止、完整安全链和原目标剩余预算有效；未发出运动。");
        }

        private static GateResult BudgetExhausted() => GateResult.TargetQuality("TARGET_CONTINUATION_TIME_LIMIT",
            "校时后当前位置新图复核已达到原目标/取场/导星剩余预算；未增加时间或运动额度，确认停止后按原策略结束本目标。");

        public async Task PersistAttemptAsync(ClockRecoveryPositionCheck receipt, ObservationContext context, CancellationToken token)
        {
            capturePosition = ReadSnapshot();
            intentPath = await run.PublishRunJsonEvidenceAsync("clock-current-field-intent", "Clock repair current-field check intent",
                new { receipt.SourceRunId, receipt.AttemptedRunId, receipt.ConsumedRunId, receipt.IssuedUtc,
                    receipt.OriginalRecoveryDeadlineUtc, receipt.MaximumSeparationArcseconds, receipt.Before, receipt.After,
                    current = capturePosition, motionIssued = false, inheritedOpticalEvidence = false,
                    originalBudget = run.NativeTarget?.ClockBudget }, null, token).ConfigureAwait(false);
        }

        public async Task<ClockRecoveryFieldProbeResult> CaptureFreshAsync(ObservationContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var before = await ValidateStoppedBoundaryAsync(context, token).ConfigureAwait(false);
            if (before.Disposition != GateDisposition.Passed) return new(new(before), false);
            captureLease = NativeCameraCaptureLease.TryAcquire(run.cameraMediator, NativeCaptureOwner);
            if (captureLease is null) return new(new(GateResult.Unknown("CLOCK_CURRENT_FIELD_STOP_UNCONFIRMED", "未取得当前原生序列的科学相机空闲锁。")), false);
            using var window = CancellationTokenSource.CreateLinkedTokenSource(token);
            var remaining = Deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) window.Cancel(); else window.CancelAfter(remaining);
            Task<G3FieldState>? operation = null;
            Exception? failure = null;
            GateResult? stopFailure = null;
            G3FieldState? field = null;
            var originalDeadlineCancelled = false;
            captureStartedUtc = DateTimeOffset.UtcNow;
            try
            {
                window.Token.ThrowIfCancellationRequested();
                run.clockCurrentFieldProbeActive = true;
                operation = run.CaptureAndAnalyzeG3WithSolveLadderAsync(context, window.Token, currentPositionCheck: true);
                while (!operation.IsCompleted)
                {
                    await Task.WhenAny(operation, Task.Delay(250, window.Token)).ConfigureAwait(false);
                    window.Token.ThrowIfCancellationRequested();
                    var gate = CheckPhysicalBoundary(context, requireIdleIllumination: false);
                    if (gate.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(gate);
                }
                field = await operation.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex;
                originalDeadlineCancelled = !token.IsCancellationRequested && DateTimeOffset.UtcNow >= Deadline;
            }
            finally
            {
                window.Cancel();
                if (failure is not null || operation is { IsCompleted: false })
                    stopFailure = await StopAndJoinAsync(operation).ConfigureAwait(false);
                if (operation is not { IsCompleted: false } && run.startupGuidePendingOperation is not { IsCompleted: false })
                {
                    run.clockCurrentFieldProbeActive = false;
                    captureLease?.Dispose();
                    captureLease = null;
                }
                // An unjoined task retains both the restrictive capture mode
                // and the ATR lease; a timeout is not release of ownership.
            }
            token.ThrowIfCancellationRequested();
            if (stopFailure is not null) return new(new(stopFailure), false);
            if (failure is PhysicalActionGateException physical) return new(new(physical.Gate), false);
            if (failure is OperationCanceledException && originalDeadlineCancelled)
                return new(new(run.SequenceStopGate() ?? BudgetExhausted()), false);
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            var after = await ValidateStoppedBoundaryAsync(context, token).ConfigureAwait(false);
            if (after.Disposition != GateDisposition.Passed) return new(new(after), false);
            if (field is null) throw new InvalidOperationException("The current-field probe returned no field.");
            if (field.Gate.Disposition != GateDisposition.Passed)
                return new(new(field.Gate, field.FramePath), ClockCurrentFieldAllowsOpticalFallback(field.Gate));
            freshField = field;
            var fresh = await ValidateFreshFieldAsync(context, token).ConfigureAwait(false);
            if (fresh.Disposition != GateDisposition.Passed) return new(new(fresh), false);
            var accepted = run.G3FieldPassed(field, intentPath ?? throw new InvalidOperationException("Current-field intent is missing."), 0, null);
            return new(accepted, !accepted.CanAdvance && ClockCurrentFieldAllowsOpticalFallback(accepted.Gate));
        }

        // The original operation is joined, not replaced with a cancelled wait.
        // Reuse the existing persistent ownership latch and cleanup integration.
        private async Task<GateResult?> StopAndJoinAsync(Task? operation)
        {
            // The shared native capture owns its own cancellation stop/finally.
            // First join that exact operation. An epoch alone does NOT prove
            // that a later Looping/Selected session belongs to this probe.
            if (operation is not null)
            {
                try { await operation.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false); }
                catch (Exception) { /* Preserve the original capture exception; actual completion is checked below. */ }
            }
            string? issue = null;
            if (operation is { IsCompleted: false })
            {
                run.startupGuidePendingOperation ??= operation;
                issue = "校时后复核原采集任务的取消清理未结束；保留原任务及设备责任，不接管另一个预览/导星会话。";
                run.RejectClockCurrentFieldPhdOwner(issue);
            }
            else
            {
                using var window = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                Task? readback = null;
                try
                {
                    // Completed or rejected-before-mutation work gets ONLY a
                    // readback, never a stop of an operator's newer session.
                    readback = ReadStoppedAsync(window.Token);
                    await readback.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    issue = ex.Message;
                    run.RejectClockCurrentFieldPhdOwner("原 PHD2 所有者停止读回失败；通用清理不接管其他会话：" + issue);
                }
                if (readback is { IsCompleted: false })
                {
                    run.startupGuidePendingOperation ??= readback;
                    issue = "原所有者的停止读回任务尚未结束，不能重新证明空闲或释放责任。";
                }
            }
            async Task ReadStoppedAsync(CancellationToken token)
            {
                var identity = await run.phd2.ValidateIdentityAsync(run.PhdIdentityRequirement(), token).ConfigureAwait(false);
                var state = await run.phd2.GetAppStateAsync(token).ConfigureAwait(false);
                if (ownerEpoch is not { } epoch || !identity.IsValid || identity.Profile != owner?.Profile ||
                    identity.Equipment != owner?.Equipment || run.phd2.Snapshot.ConnectionEpoch != epoch ||
                    !run.phd2.Snapshot.IsConnected || state != Phd2AppState.Stopped)
                {
                    issue = "原 PHD2 身份及停止状态未确认；未停止或接管后来出现的预览/导星会话。";
                    run.RejectClockCurrentFieldPhdOwner(issue);
                }
            }
            if (run.slitIlluminationSafetyIssue is { } led) issue = led;
            if (run.activeSlitIlluminationLease is not null || run.activeSlitIlluminationSequenceId is not null)
                issue ??= "采集结束后 LED 测量租约尚未完成关灯/释放确认。";
            if (issue is null) return null;
            run.startupGuideBoundaryIssue ??= issue;
            return GateResult.Unknown("GUIDE_STARTUP_STOP_UNCONFIRMED", issue);
        }

        public async Task<GateResult> ValidateFreshFieldAsync(ObservationContext context, CancellationToken token)
        {
            if (freshField is not { } field || field.Gate.Disposition != GateDisposition.Passed ||
                field.MountBinding is not { } binding || binding.FrameCompletedUtc < captureStartedUtc ||
                run.g3SlitGeometryRunCache is not { } slit || slit.CapturedUtc < captureStartedUtc ||
                !run.TryGetCurrentG3SlitGeometryRunCache(context, out _))
                return GateResult.Unknown("CLOCK_CURRENT_FIELD_FRESH_EVIDENCE_REQUIRED", "本次复核缺少同一新段新拍目标及狭缝证据；不继承旧图、旧缝或旧控制坐标。");
            if (capturePosition is not { } before) return GateResult.Unknown("CLOCK_CURRENT_FIELD_POSITION_REQUIRED", "复核前新鲜指向见证缺失。");
            if (PositionContinuityGate(before) is { } changed) return changed;
            return await run.ValidateG3FieldMountBindingForMotionAsync(context, field, token).ConfigureAwait(false);
        }

        private GateResult? PositionContinuityGate(ClockRecoveryPositionSnapshot before)
        {
            var current = ReadSnapshot();
            if (current.Identity.Phd2Endpoint != before.Identity.Phd2Endpoint ||
                current.Identity.Phd2ProfileId != before.Identity.Phd2ProfileId ||
                current.Identity.Phd2ProfileName != before.Identity.Phd2ProfileName ||
                current.Identity.G3StableCameraId != before.Identity.G3StableCameraId)
                return run.RejectClockCurrentFieldPhdOwner("新帧复核期间 PHD2/导星相机身份改变；未接管新会话。");
            var separation = G3AcquisitionMotionPlanner.AngularSeparationArcseconds(before.Mount.RightAscensionDegrees,
                before.Mount.DeclinationDegrees, current.Mount.RightAscensionDegrees, current.Mount.DeclinationDegrees);
            if (current.Identity != before.Identity || !current.Stationary || current.Parked || !current.Tracking ||
                current.Mount.PierSide != before.Mount.PierSide || current.Mount.CoordinateEpoch != before.Mount.CoordinateEpoch ||
                !double.IsFinite(separation) || separation > MountCommandArrivalToleranceArcseconds)
                return GateResult.Unknown("CLOCK_CURRENT_FIELD_POSITION_CHANGED", "新帧复核期间设备、镜侧或实报指向改变；不授权沿用当前位置或旧光学证据。");
            return null;
        }

        public async Task RecordDecisionAsync(string decision, StageResult? result, ObservationContext context, CancellationToken token)
        {
            await run.PublishRunJsonEvidenceAsync("clock-current-field-decision", "Clock repair current-field decision",
                new { decision, gate = result?.Gate, metadata = result?.Metadata, intentPath,
                    sourceRunId = run.NativeTarget?.ClockPositionCheck?.SourceRunId,
                    destinationRunId = context.Plan.ObservationRunId, originalDeadlineUtc = Deadline,
                    originalBudget = run.NativeTarget?.ClockBudget, motionIssued = false,
                    usedOnlyNewSegmentOpticalEvidence = result?.CanAdvance == true }, result?.EvidencePath, token).ConfigureAwait(false);
            if (decision == "optical-fallback")
            {
                freshField = null;
                run.lastG3Field = null;
                // Only detector-fixed geometry from THIS segment may remain;
                // the canonical route must obtain its own fresh target frame.
                if (!run.TryGetCurrentG3SlitGeometryRunCache(context, out var slit) ||
                    slit is null || slit.CapturedUtc < captureStartedUtc)
                    run.g3SlitGeometryRunCache = null;
                run.Report("校时后当前位置新图未通过；已确认停止，按原剩余预算回到完整取场，不增加额度。");
            }
        }
    }
}
