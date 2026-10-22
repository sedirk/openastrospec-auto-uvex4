using System.IO;
using NINA.Core.Enum;
using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private string? startupGuideBoundaryIssue;
    private Task? startupGuidePendingOperation;
    private bool startupGuideRetirementIncomplete;

    private async Task TryRetireTerminalGuideStateAtStartupAsync(ObservationContext context, CancellationToken token)
    {
        var ownersPrepared = false;
        NativeCameraCaptureLease? captureLease = null;
        G3FieldMountBinding? fieldWitness = null;
        Phd2IdentityValidation? fieldOwner = null;
        long? fieldConnectionEpoch = null;
        object? SequenceOwner() => NativeTarget is not null ? nativeSequenceCaptureOwner?.Invoke() : null;
        var deadline = context.Plan.PlannedStartUtc + context.Plan.PlannedDuration;
        if (configuration.SequencePlan?.TargetDeadlineUtc is { } targetEnd && targetEnd < deadline) deadline = targetEnd;
        if (configuration.SequencePlan?.DeadlineUtc is { } nightEnd && nightEnd < deadline) deadline = nightEnd;
        using var window = CancellationTokenSource.CreateLinkedTokenSource(token);
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) window.Cancel();
        else window.CancelAfter(remaining);
        var boundaryToken = window.Token;

        Task Check(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            if (SequenceStopGate() is { } stop) throw new PhysicalActionGateException(stop);
            if (DateTimeOffset.UtcNow >= deadline) throw new OperationCanceledException("原目标/整夜截止已到。", ct);
            var ownership = host.RealRunOwnershipGate();
            if (ownership.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(ownership);
            var owners = ValidateNinaProfileOwnerSelections(context.Plan);
            if (owners.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(owners);
            var solver = PlateSolverRunConfiguration.CaptureCurrent(profileService.ActiveProfile.PlateSolveSettings, configuration.PlateSolver);
            if (!configuration.MatchesCurrentProfile(settings, solver, profileService.ActiveProfile.ImageFileSettings.FilePattern,
                    CaptureCurrentEnvironmentDeviceSelection(), out _, out var drift))
                throw new PhysicalActionGateException(GateResult.Unknown("REAL_PROFILE_DRIFT", drift));
            if (startupGuideBoundaryIssue is { } issue)
                throw new PhysicalActionGateException(GateResult.Unknown("GUIDE_STARTUP_STOP_UNCONFIRMED", issue));
            if (ownersPrepared)
            {
                context.RemainingWorstCaseDuration = deadline - DateTimeOffset.UtcNow;
                var gate = ValidateImmediatePhysicalActionGates(context);
                if (gate.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(gate);
                var camera = cameraMediator.GetInfo();
                var mount = telescopeMediator.GetInfo();
                var focus = focuserMediator.GetInfo();
                if (!camera.Connected || camera.DeviceId != configuration.NightSetup.AtrStableId || camera.IsExposing || camera.LiveViewEnabled ||
                    camera.CameraState is not (CameraStates.Idle or CameraStates.NoState) || captureLease?.IsCurrent(SequenceOwner()) != true ||
                    !mount.Connected || mount.DeviceId != configuration.ExpectedTelescopeId || mount.Slewing || mount.IsPulseGuiding ||
                    mount.SideOfPier.ToString() is not ("pierEast" or "pierWest") || !focus.Connected || focus.IsMoving || focus.IsSettling ||
                    !activeQhyJobs.IsEmpty || !pendingQhyRequests.IsEmpty || automaticFocusBoundaryIssue is not null)
                    throw new PhysicalActionGateException(GateResult.Unknown("GUIDE_STARTUP_OWNER_NOT_IDLE", "当前设备身份或实际停止状态未确认；旧控制记录保留。"));
            }
            return Task.CompletedTask;
        }

        async Task Prepare(CancellationToken ct)
        {
            await Check(ct).ConfigureAwait(false);
            var issues = ValidateStaticConfiguration(context.Plan);
            if (issues.Count != 0) throw new PhysicalActionGateException(GateResult.Unknown("REAL_CONFIGURATION_INCOMPLETE", string.Join(" ", issues)));
            var loaded = await RealCommissioningPresetLoader.LoadAsync(configuration, ct).ConfigureAwait(false);
            if (loaded.Preset is null || SameHash(loaded.Preset.Sha256, invalidatedCommissioningSha256 ?? string.Empty))
                throw new PhysicalActionGateException(GateResult.Unknown("COMMISSIONING_PRESET_UNTRUSTED", string.Join(" ", loaded.Issues)));
            commissioning = loaded.Preset;
            var lockedNight = await LockedNightSetupSnapshotLoader.LoadAsync(configuration, context.Plan, commissioning, ct).ConfigureAwait(false);
            if (lockedNight.Snapshot is null)
                throw new PhysicalActionGateException(GateResult.Unknown("NIGHT_SETUP_SNAPSHOT_UNTRUSTED", string.Join(" ", lockedNight.Issues)));
            nightSetup = lockedNight.Snapshot;
            // Reuse the existing sole-owner connection path, but do not start
            // cooling or the usual NightSetup actions (clock repair, opening,
            // tracking, UVEX movement, catalogue slew or focus).
            var connection = await EnsureNinaEquipmentConnectedAsync(context, ct, startAtrCooling: false).ConfigureAwait(false);
            if (connection.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(connection);
            await EnsurePhdConnectedAsync(ct).ConfigureAwait(false);
            captureLease = NativeCameraCaptureLease.TryAcquire(cameraMediator, SequenceOwner());
            ownersPrepared = true;
            await Check(ct).ConfigureAwait(false);
            var c11 = ReadC11MainFocusOwner();
            var focusGate = c11RunFocusLock.CaptureOrValidate(c11, nightSetup.Value);
            if (focusGate.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(focusGate);
            currentC11MainFocusOwner = c11;
        }

        async Task Capture(CancellationToken ct)
        {
            await Check(ct).ConfigureAwait(false);
            var before = CaptureG3FrameMountReadback();
            var identity = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), ct).ConfigureAwait(false);
            var owner = phd2.Snapshot;
            if (!identity.IsValid || !owner.IsConnected || owner.AppState != Phd2AppState.Stopped)
                throw new PhysicalActionGateException(GateResult.Unknown("GUIDE_STARTUP_OWNER_CHANGED", "PHD2 原生单帧前同一连接/相机身份及停止状态未确认。"));
            var path = ReserveRunEvidencePath("terminal-guide-current-field-witness", ".fit");
            var start = DateTimeOffset.UtcNow;
            // Native single-frame capture only: this route has no Looping
            // takeover, guide request, settle, calibration or lock shift.
            var captureBoundary = new StartupGuideCaptureBoundary();
            Phd2SingleFrameResult frame;
            try
            {
                frame = await captureBoundary.RunAsync(captureToken => phd2.CaptureSingleFrameWithParametersAsync(new Phd2SingleFrameRequest(
                    configuration.G3.ExposureMilliseconds, configuration.G3.Binning, configuration.G3.GainPercent, path), captureToken),
                    stopToken => phd2.StopPreviewAndConfirmAsync(owner.ConnectionEpoch, stopToken), Check, ct).ConfigureAwait(false);
            }
            finally
            {
                startupGuideBoundaryIssue ??= captureBoundary.StopIssue;
                startupGuidePendingOperation ??= captureBoundary.UnjoinedOperation;
            }
            await Check(ct).ConfigureAwait(false);
            var freshIdentity = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), ct).ConfigureAwait(false);
            if (!freshIdentity.IsValid || phd2.Snapshot.ConnectionEpoch != owner.ConnectionEpoch ||
                freshIdentity.Profile != identity.Profile || freshIdentity.Equipment != identity.Equipment ||
                await phd2.GetAppStateAsync(ct).ConfigureAwait(false) != Phd2AppState.Stopped)
                throw new PhysicalActionGateException(GateResult.Unknown("GUIDE_STARTUP_OWNER_CHANGED", "单帧前后连接历元或物理相机身份改变，未归档。"));
            var after = CaptureG3FrameMountReadback();
            if (!string.Equals(Path.GetFullPath(frame.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path) || frame.CompletedUtc < start || frame.CompletedUtc > DateTimeOffset.UtcNow ||
                frame.UsedLoopSaveFallback || !frame.RequestedParametersApplied || frame.VerifiedExposureMilliseconds != configuration.G3.ExposureMilliseconds)
                throw new PhysicalActionGateException(GateResult.Unknown("GUIDE_STARTUP_FRESH_FRAME_UNCONFIRMED", "原生单帧文件、参数或完成时刻未确认；没有归档。"));
            var sha = await ComputeFileSha256Async(path, ct).ConfigureAwait(false);
            var binding = CreateG3FieldMountBinding(context, path, sha, frame.CompletedUtc, after);
            var stable = G3FieldMountBindingPolicy.ValidateForMotion(binding, context.Plan.ObservationRunId,
                configuration.ActionConfigurationSha256, commissioning!.Sha256, path, sha,
                before.RightAscensionDegrees, before.DeclinationDegrees, before.CoordinateEpoch, before.PierSide,
                MountCommandArrivalToleranceArcseconds);
            if (stable.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(stable);
            await Check(ct).ConfigureAwait(false);
            if (!SameHash(sha, await ComputeFileSha256Async(path, ct).ConfigureAwait(false)))
                throw new PhysicalActionGateException(GateResult.Unknown("GUIDE_STARTUP_FRAME_CHANGED", "新帧文件在边界证明期间改变。"));
            fieldWitness = binding;
            fieldOwner = identity;
            fieldConnectionEpoch = owner.ConnectionEpoch;
            await PublishRunJsonEvidenceAsync("terminal-guide-current-field-witness",
                "Fresh stationary current-pointing frame; NOT solved, target identified, on-slit or return verified",
                new { frame, sha, before, after, binding, stable, motionSent = false, returnVerified = false,
                    targetIdentityVerified = false, targetOnSlitVerified = false, budgetsReset = false, deadline }, path, ct).ConfigureAwait(false);
        }

        async Task<GuideBoundaryWitness> Witness(CancellationToken ct)
        {
            await Check(ct).ConfigureAwait(false);
            // Empty dictionaries in this NEW runner say nothing about the old
            // remote owner. Query its already-bound health, camera identity and
            // global idle state; do not connect, adopt or cancel foreign work.
            if (!string.IsNullOrWhiteSpace(context.Plan.ExpectedQhyCameraId) ||
                !string.IsNullOrWhiteSpace(configuration.NightSetup.QhyStableId))
            {
                var service = await ValidateQhyServiceConfigurationAsync(context.Plan, ct).ConfigureAwait(false);
                if (service.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(service);
                var remote = await qhy.GetCameraAsync(ct).ConfigureAwait(false);
                if (remote is null || remote.Connected && (remote.Identity is null || remote.Identity.StableId != context.Plan.ExpectedQhyCameraId) ||
                    remote.Identity is not null && remote.Identity.StableId != configuration.NightSetup.QhyStableId ||
                    remote.TimestampUtc > DateTimeOffset.UtcNow || DateTimeOffset.UtcNow - remote.TimestampUtc > TimeSpan.FromSeconds(10))
                    throw new PhysicalActionGateException(GateResult.Unknown("GUIDE_STARTUP_REMOTE_OWNER_CHANGED", "测光设备新鲜只读身份与锁定绑定不一致，未归档旧责任。"));
                var globalIdle = await qhy.ReadGlobalIdleAsync(ct).ConfigureAwait(false);
                if (globalIdle.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(globalIdle);
            }
            if (fieldWitness is { } bound)
            {
                var fresh = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), ct).ConfigureAwait(false);
                if (!fresh.IsValid || fieldConnectionEpoch != phd2.Snapshot.ConnectionEpoch ||
                    fresh.Profile != fieldOwner!.Profile || fresh.Equipment != fieldOwner.Equipment)
                    throw new PhysicalActionGateException(GateResult.Unknown("GUIDE_STARTUP_OWNER_CHANGED", "新帧之后 PHD2 所有者发生变化。"));
                var current = CaptureG3FrameMountReadback();
                var hash = await ComputeFileSha256Async(bound.FramePath, ct).ConfigureAwait(false);
                var gate = G3FieldMountBindingPolicy.ValidateForMotion(bound, context.Plan.ObservationRunId,
                    configuration.ActionConfigurationSha256, commissioning!.Sha256, bound.FramePath, hash,
                    current.RightAscensionDegrees, current.DeclinationDegrees, current.CoordinateEpoch, current.PierSide,
                    MountCommandArrivalToleranceArcseconds);
                if (gate.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(gate);
            }
            var witness = await ReadTerminalGuideWitnessAsync(ct).ConfigureAwait(false);
            await Check(ct).ConfigureAwait(false);
            return witness;
        }

        try
        {
            // Even an authentication/debt failure before Connect is not a new
            // authorization for lifecycle fallback to replay any old vector.
            startupGuideRetirementIncomplete = true;
            var archives = await TerminalGuideStartupRetirement.TryRetireAsync(SlitPlacementObservationsRoot(), context.Plan.ObservationRunId,
                LockedMetadata, Prepare, Capture, Witness, Check, boundaryToken).ConfigureAwait(false);
            if (archives is not null)
            {
                await WriteAuditBestEffortAsync("terminal-guide-startup-retired", new
                { archives, currentRunId = context.Plan.ObservationRunId, motionSent = false, returnVerified = false, budgetsReset = false }).ConfigureAwait(false);
                Report("旧目标已经终止：原设备停止与当前指向新帧已确认，旧导星控制记录按原字节归档；没有回位或光学成功声明，新目标从正常取场开始。");
            }
            startupGuideRetirementIncomplete = false;
        }
        finally { captureLease?.Dispose(); }
    }
}
