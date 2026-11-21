using System.Runtime.CompilerServices;
using NINA.Core.Enum;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

// A cancelled wait need not mean the native driver has returned. New runners
// and the independent safety owner share the same N.I.N.A. mount mediator.
internal sealed class NightStartHomeDriverBoundary
{
    private static readonly ConditionalWeakTable<ITelescopeMediator, NightStartHomeDriverBoundary> Owners = new();
    internal static NightStartHomeDriverBoundary For(ITelescopeMediator mount) => Owners.GetOrCreateValue(mount);
    private Task? pending;
    internal void Retain(Task actual) => Volatile.Write(ref pending, actual);
    internal GateResult? Unjoined => Volatile.Read(ref pending) is { IsCompleted: false }
        ? GateResult.Unknown("NIGHT_START_HOME_OWNER_BUSY", "原赤道仪回零或停跟踪调用尚未退出；不能宣布设备空闲或启动下一条机械指令。") : null;
}

internal sealed partial class RealObservationStageRunner
{
    private async Task<GateResult> PrepareNightStartHomeAsync(ObservationContext context, CancellationToken token)
    {
        var plan = configuration.SequencePlan!;
        var once = NativeTarget?.StartHome;
        if (once is null || !plan.DeferObservatoryCloseout)
            return GateResult.Unknown("NIGHT_START_HOME_SCOPE_INVALID", "未取得本次原生整夜的回零凭据；不转向。");
        // A later target/clock or flip segment uses its ordinary pipeline. It
        // must not stop tracking, inspect a new home position, or reset a budget.
        if (once.Verified) return GateResult.Pass("NIGHT_START_HOME_ALREADY_VERIFIED", "本轮开夜回零已核验，不重复。");
        if (NativeTarget!.ClockBudget is not null || NativeTarget.HasBudgetedContinuation || NativeTarget.SegmentRunIds.Count > 0 ||
            NativeMeridian?.Requested == true || NativeMeridian?.Flipped == true ||
            lastG3Field is not null || lastQhyAcquisition is not null || lastQhySolve is not null ||
            attemptedAtrFrames != 0 || attemptedAtrProbeFrames != 0 || correctionAttempts != 0 ||
            pendingQhyCoarseReturn is not null || pendingG3SearchReturn is not null || pendingSlitPlacement is not null ||
            pendingPhd2LockShift is not null || durableG3AcquisitionMotion is not null)
            return GateResult.Unknown("NIGHT_START_HOME_NOT_INITIAL", "开夜回零不能在翻转、恢复或已有光学/运动状态时补发。");
        var owner = NightStartHomeDriverBoundary.For(telescopeMediator);
        if (owner.Unjoined is { } pending) return pending;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromSeconds(plan.MountHomeTimeoutSeconds));
        bool arrivalRequired = false;
        long? stoppedGuideConnection = null;
        GateResult? Guard()
        {
            if (SequenceStopGate() is { } stop) return stop;
            if (configuration.Environment.WeakSupervisionEnabled || !configuration.Environment.RequireSafetyMonitor ||
                (!configuration.Environment.RequireWeatherData && !configuration.Environment.SafetyMonitorOwnsWeather) ||
                !configuration.Environment.RequireOpenDomeOrRoof ||
                host.Dashboard.Run is not { State: ObservationRunState.RunningAuto } run || run.ObservationRunId != context.Plan.ObservationRunId ||
                Volatile.Read(ref resumeRecoveryRequired) != 0 ||
                host.RealRunOwnershipGate().Disposition != GateDisposition.Passed ||
                NativeMeridian?.Requested == true || !environmentSafetyShutdownTask.IsCompleted)
                return GateResult.Unknown("NIGHT_START_HOME_AUTHORITY_CHANGED", "完整安全链或本轮唯一设备控制权已改变；不再开夜回零。");
            // Observe roof ownership, not the generic roof-COMMAND interlock:
            // the latter deliberately blocks OTHER owners while our Home is in flight.
            if (RoofConnection.PendingGate is { } roof) return roof;
            if (NinaRoofReadback.Position(domeMediator.GetInfo(), BoundRoofId) != BoundRoofPosition.Open)
                return GateResult.Unknown("NIGHT_START_HOME_ROOF_NOT_OPEN", "原绑定屋顶尚未确认完全打开；不回零。");
            var gate = ValidateImmediatePhysicalActionGates(context);
            if (gate.Disposition != GateDisposition.Passed) return gate;
            var mount = telescopeMediator.GetInfo();
            var camera = cameraMediator.GetInfo();
            var focus = focuserMediator.GetInfo();
            if (!mount.Connected || mount.DeviceId != configuration.ExpectedTelescopeId || !mount.CanFindHome ||
                mount.AtPark || mount.IsPulseGuiding || !camera.Connected || camera.DeviceId != context.Plan.ExpectedAtrCameraId ||
                camera.IsExposing || camera.LiveViewEnabled || camera.CameraState is not (CameraStates.Idle or CameraStates.NoState) ||
                !cameraMediator.IsFreeToCapture(nativeSequenceCaptureOwner?.Invoke() ?? this) ||
                !focus.Connected || focus.DeviceId != NinaProfileOwnerPreflight.C11FocuserDeviceId || focus.IsMoving || focus.IsSettling ||
                !activeQhyJobs.IsEmpty || !pendingQhyRequests.IsEmpty || !phd2.IsConnected ||
                stoppedGuideConnection is { } epoch && (phd2.Snapshot.ConnectionEpoch != epoch || phd2.Snapshot.AppState != Phd2AppState.Stopped) ||
                phd2.Snapshot.PendingSettleOperationId is not null || phd2.Snapshot.Phd2Paused || phd2.Snapshot.AutomationPaused ||
                activeSlitIlluminationLease is not null || slitIlluminationSafetyIssue is not null)
                return GateResult.Unknown("NIGHT_START_HOME_DEVICE_CHANGED", "原设备身份、回零能力或相机/导星/调焦空闲未确认；不继续。");
            if (arrivalRequired && (!mount.AtHome || mount.Slewing || mount.TrackingEnabled))
                return GateResult.Unknown("NIGHT_START_HOME_ARRIVAL_LOST", "写入完成记录之前，零位或停止读回已改变。");
            return null;
        }
        async Task<GateResult> Boundary(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await RequireImmediatePhysicalActionGatesAsync(context, ct).ConfigureAwait(false);
            if (Guard() is { } changed) return changed;
            if (NativeMeridianJournalStore.PendingGate(NativeMeridianJournalPath) is { } flip) return flip;
            // All three stores were authenticated by the stage preflight. Home
            // may not invalidate even a recoverable prior return vector.
            var root = SlitPlacementObservationsRoot();
            if ((await G3AcquisitionMotionStore.DiscoverAsync(root, ct).ConfigureAwait(false)).Any(x =>
                    x.Error is not null || x.State?.Phase != G3AcquisitionMotionPhase.SettledBudgetLedger) ||
                (await SlitPlacementPendingStore.DiscoverAsync(root, ct).ConfigureAwait(false)).Any(x =>
                    x.Error is not null || x.State?.Phase != SlitPlacementPendingPhase.SettledBudgetLedger) ||
                (await Phd2LockShiftPendingStore.DiscoverAsync(root, ct).ConfigureAwait(false)).Any(x =>
                    x.Error is not null || x.State?.Phase != Phd2LockShiftPendingPhase.SettledBudgetLedger))
                return GateResult.Unknown("NIGHT_START_HOME_PENDING_MOTION", "仍有未结或不可读的运动记录；不以回零替代复核或回程。");
            var photometry = await ReadStartupClockPhotometryIdleAsync(context, ct).ConfigureAwait(false);
            if (photometry.Disposition != GateDisposition.Passed) return photometry;
            var witness = await ReadTerminalGuideWitnessAsync(ct).ConfigureAwait(false);
            if (!witness.CameraIdle || !witness.MountStationary || !witness.ExclusiveRunOwner || !witness.OwnersIdle ||
                !witness.Phd2IdentityMatches || witness.Phd2State != Phd2AppState.Stopped || witness.TelescopeId != configuration.ExpectedTelescopeId)
                return GateResult.Unknown("NIGHT_START_HOME_NOT_IDLE", "原赤道仪、ATR、测光和 PHD2 未能确认均空闲；不会抢占其他操作。");
            stoppedGuideConnection ??= phd2.Snapshot.ConnectionEpoch;
            return Guard() ?? GateResult.Pass("NIGHT_START_HOME_BOUNDARY_CLEAR", "已开顶、完整安全链和原所有者停止状态有效。");
        }
        try
        {
            Report("开夜准备：首个实际目标前核验赤道仪零位；整轮最多一次，已到位不移动");
            return await once.EnsureAsync(context.Plan.ObservationRunId, configuration.ExpectedTelescopeId,
                configuration.ActionConfigurationSha256, Boundary, async ct =>
                {
                    var result = await HomeAndVerifyMountAsync(telescopeMediator, configuration.ExpectedTelescopeId, progress,
                        TimeSpan.FromSeconds(plan.MountHomeTimeoutSeconds), TimeSpan.FromSeconds(1), ct, Guard, owner.Retain).ConfigureAwait(false);
                    arrivalRequired = result.Disposition == GateDisposition.Passed;
                    return result;
                }, bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && bounded.IsCancellationRequested)
        {
            return GateResult.Unknown("NIGHT_START_HOME_TIMEOUT", "开夜回零和核验超过原时限，不转向、不自动重发；请检查赤道仪是否已停止。");
        }
    }
}
