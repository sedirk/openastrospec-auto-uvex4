using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

// Complement the finite-owner/native execution tests with exact production
// attachment points. These guards prove wiring, not real-device acquisition.
public sealed class ClockRecoveryFieldSourceTests
{
    [Fact]
    public void RealCanonicalDispatcherInvokesTheSameOptionalWorkflowBeforeItsExistingStageSwitch()
    {
        var source = Source("RealObservationStageRunner.cs");
        var dispatch = Section(source, "var result = (clockRecoveryField is null ? null :", "// Some owner adapters report");
        Assert.Contains("await clockRecoveryField.TryExecuteAsync(stage, context, cancellationToken)", dispatch);
        Assert.Contains("?? stage switch", dispatch);
        foreach (var mapping in new[]
        {
            "ObservationStage.ValidateNightSetup => await ValidateNightSetupAsync",
            "ObservationStage.SlewToCatalogTarget => await SlewToCatalogTargetAsync",
            "ObservationStage.AcquireQhyWideField => await AcquireOptionalQhyWideFieldAsync",
            "ObservationStage.CoarseCenter => await CoarseCenterAsync",
            "ObservationStage.AcquireG3SlitField => await AcquireG3SlitFieldAsync",
            "ObservationStage.PlaceTargetOnSlit => await PlaceTargetOnSlitAsync",
            "ObservationStage.StartGuiding => await StartGuidingAsync",
            "ObservationStage.SelectAtrExposure => await SelectAtrExposureAsync",
            "ObservationStage.RunScienceBlock => await RunScienceBlockAsync",
        }) Assert.Contains(mapping, dispatch);
        var native = Source("RealObservationStageRunner.NativeSequence.cs");
        var attach = Section(native, "internal void AttachNativeTarget(", "internal ClockContinuationBudget CaptureClockContinuationBudget");
        Assert.Contains("clockRecoveryField = session?.ClockContinuationUsed == true", attach);
        Assert.Contains("? new(session.ClockPositionCheck, CreateClockRecoveryFieldPort()) : null", attach);
        Assert.Contains("fineAcquisitionStartedUtc = spent.FineStartedUtc", attach);
        Assert.Contains("correctionAttempts = spent.CorrectionAttempts", attach);
        Assert.Contains("atrProbe.InheritedAttempts = spent.ProbeAttempts", attach);
    }

    [Fact]
    public void SharedIdentityIsCapturedBeforeRemainingScienceCountsAlterTheSegmentConfigurationHash()
    {
        var target = Source(Path.Combine("SequenceItems", "UvexTargetObservationContainer.cs"));
        var capture = Section(target, "lockedConfiguration = realRunnerFactory.CaptureConfiguration(settings);", "var plan = session?.TargetPlan");
        Ordered(capture, "night.CheckConfiguration(lockedConfiguration)",
            "session.SharedActionConfigurationSha256 = lockedConfiguration.ActionConfigurationSha256",
            "lockedConfiguration.WithSequencePlan(sequencePlan)");
        var native = Source("RealObservationStageRunner.NativeSequence.cs");
        var repair = Section(native, "internal async Task<GateResult> RecoverMountClockAtIdleBoundaryAsync", "private GateResult? SequenceStopGate()");
        Ordered(repair, "var idle = await ConfirmSequenceIdleAsync(token)",
            "var before = ReadClockRecoveryPositionSnapshot(ownersStoppedAndJoined: true)",
            "await EnsureMountClockSynchronizedAsync", "var afterIdle = await ConfirmSequenceIdleAsync(token)",
            "if (afterIdle.Count != 0)", "var after = ReadClockRecoveryPositionSnapshot(ownersStoppedAndJoined: true)",
            "ClockRecoveryPositionCheck.TryCreate");
        Assert.Contains("session.RecoveryDeadline(CaptureClockContinuationBudget())", repair);
        Assert.Contains("MountCommandArrivalToleranceArcseconds", repair);
        Assert.Contains("reusedOpticalEvidence = false, motionIssued = false", repair);
    }

    [Fact]
    public void NightRechecksPostReconnectNativeOwnerBeforeGrantingAContinuationOrFreshIdleStandby()
    {
        var source = Source(Path.Combine("SequenceItems", "UvexNightSequenceContainer.cs"));
        var recovery = Section(source, "clockRepair = await clockRecovery.TryRecoverAsync", "GateResult? focusRepair = null");
        Ordered(recovery, "RecoverMountClockAtIdleBoundaryAsync", "using var clockStopTimeout",
            "runner!.ConfirmSequenceIdleAsync(clockStopTimeout.Token, false)",
            "runner.ConfirmSequenceIdleAsync(clockStopTimeout.Token)",
            "RecordUnsettledTargetBoundary(target, result.Run.ObservationRunId, settledErrors, runner!.CanRecheckSequenceIdle)",
            "if (!targetsIdle || settledErrors.Length != 0)", "var clockRecovered =",
            "session.TryContinueAfterClockRepair");
        Assert.Contains("clockRepair = GateResult.Unknown(\"NIGHT_MOUNT_CLOCK_RECOVERY_NOT_IDLE\"", recovery);
        Assert.Contains("standbyPlan?.StopGateNow() is null", recovery);
    }

    [Fact]
    public void ProbeCoverGuardReturnsActualReadbackBeforeAnyPhysicalCoverCommand()
    {
        var source = Source("RealObservationStageRunner.cs");
        var cover = Section(source, "private async Task<GateResult> EnsureOpticalCoverOpenAsync", "private async Task<GateResult> WaitForOpticalCoverStateAsync");
        Ordered(cover, "var current = ValidateOpticalCoverOpen()", "if (clockCurrentFieldProbeActive) return current",
            "await CheckpointAndRejectStaleStageStackAsync", "await flatDeviceMediator.OpenCover");
    }

    [Fact]
    public void ActualProbeUsesTheExistingG3AnalysisWithOneSolveTierAndNoQhyPairCollection()
    {
        var source = Source("RealObservationStageRunner.cs");
        var capture = Section(source, "private async Task<G3FieldState> CaptureAndAnalyzeG3WithSolveLadderAsync",
            "private async Task<G3PlateSolveProbeState> CaptureG3PlateSolveLadderAsync");
        Ordered(capture, "var slitGeometryPreparation = await CaptureAndAnalyzeG3Async(",
            "slitGeometryOnly: true", "var probe = await CaptureG3PlateSolveLadderAsync(",
            "currentPositionCheck).ConfigureAwait(false)",
            "if (currentPositionCheck && (probe.Gate.Disposition != GateDisposition.Passed || probe.Solve?.Result.Success != true))",
            "return G3FieldState.Failed(probe.Gate, probe.FramePath, probe.Image, probe.Solve, probe.MountBinding)",
            "await ValidateG3ProbeMountBindingForMotionAsync(",
            "if (!currentPositionCheck && probe.Solve is { IsFormal: true, Result.Success: true } &&",
            "await TryCollectQhyG3FastSolvePairAsync(");
        Assert.Contains("trustedSolveProbe: probe", capture);
        Assert.Contains("return await RefineClippedCatalogPositionAsync(context, solvedField, cancellationToken)", capture);

        var ladder = Section(source, "private async Task<G3PlateSolveProbeState> CaptureG3PlateSolveLadderAsync",
            "private GateResult ValidateG3SolveProbeImage(");
        Ordered(ladder, "var tierCount = currentPositionCheck ? 1 : preset.ExposureMilliseconds.Count",
            "for (var index = 0; index < tierCount; index++)", "var beforeProbeMountReadback = CaptureG3FrameMountReadback()",
            "captured = await CaptureG3FullFrameForAcquisitionAsync(");
        Assert.Contains("exposureMilliseconds = preset.ExposureMilliseconds.Take(tierCount).ToArray()", ladder);
        Assert.Contains("mountMotionAuthorized = !currentPositionCheck && boundedSparseRecoveryAuthorized", ladder);
    }

    [Fact]
    public void RealAdapterPreservesOriginalDeadlineAndJoinsTheActualCaptureBeforeReadingOwnerStop()
    {
        var source = Source("RealObservationStageRunner.ClockRecoveryField.cs");
        Assert.Contains("private DateTimeOffset Deadline => run.NativeTarget?.ClockPositionCheck?.OriginalRecoveryDeadlineUtc", source);
        var capture = Section(source, "public async Task<ClockRecoveryFieldProbeResult> CaptureFreshAsync",
            "private async Task<GateResult?> StopAndJoinAsync");
        Ordered(capture, "await ValidateStoppedBoundaryAsync(context, token)",
            "captureLease = NativeCameraCaptureLease.TryAcquire(run.cameraMediator, NativeCaptureOwner)",
            "CancellationTokenSource.CreateLinkedTokenSource(token)", "var remaining = Deadline - DateTimeOffset.UtcNow",
            "window.CancelAfter(remaining)", "run.clockCurrentFieldProbeActive = true",
            "operation = run.CaptureAndAnalyzeG3WithSolveLadderAsync(context, window.Token, currentPositionCheck: true)",
            "while (!operation.IsCompleted)", "CheckPhysicalBoundary(context, requireIdleIllumination: false)",
            "field = await operation.ConfigureAwait(false)", "finally", "window.Cancel()",
            "stopFailure = await StopAndJoinAsync(operation)", "run.clockCurrentFieldProbeActive = false");
        Ordered(capture, "token.ThrowIfCancellationRequested();\n            if (stopFailure",
            "if (failure is OperationCanceledException && originalDeadlineCancelled)",
            "run.SequenceStopGate() ?? BudgetExhausted()", "var after = await ValidateStoppedBoundaryAsync(context, token)");

        var stop = Section(source, "private async Task<GateResult?> StopAndJoinAsync", "public async Task<GateResult> ValidateFreshFieldAsync");
        Ordered(stop, "await operation.WaitAsync(TimeSpan.FromSeconds(20))", "if (operation is { IsCompleted: false })",
            "run.startupGuidePendingOperation ??= operation", "readback = ReadStoppedAsync(window.Token)",
            "await readback.WaitAsync(TimeSpan.FromSeconds(20))", "if (readback is { IsCompleted: false })",
            "run.startupGuidePendingOperation ??= readback", "await run.phd2.ValidateIdentityAsync",
            "await run.phd2.GetAppStateAsync(token)", "run.phd2.Snapshot.ConnectionEpoch != epoch",
            "state != Phd2AppState.Stopped", "run.startupGuideBoundaryIssue ??= issue");
        Assert.DoesNotContain("StopPreviewAndConfirmAsync(", stop);
        Assert.DoesNotContain("StopCaptureAndConfirmAsync(", stop);
        Assert.Contains("return GateResult.Unknown(\"GUIDE_STARTUP_STOP_UNCONFIRMED\", issue)", stop);
    }

    [Fact]
    public void RealAdapterRevalidatesFreshFrameAndSlitBindingAndBothPhysicalOwnersBeforeSubstitution()
    {
        var source = Source("RealObservationStageRunner.ClockRecoveryField.cs");
        var stopped = Section(source, "public async Task<GateResult> ValidateStoppedBoundaryAsync",
            "private async Task<(Phd2IdentityValidation Identity, Phd2AppState State)> ReadPhdStoppedIdentityAsync");
        Ordered(stopped, "CheckPhysicalBoundary(context, requireIdleIllumination: true)",
            "var (identity, state) = await ReadPhdStoppedIdentityAsync(token)",
            "state != Phd2AppState.Stopped", "await run.ValidateQhyServiceConfigurationAsync",
            "await run.qhy.GetCameraAsync(token)", "await run.qhy.ReadGlobalIdleAsync(token)",
            "var (after, afterState) = await ReadPhdStoppedIdentityAsync(token)", "afterState != Phd2AppState.Stopped",
            "ownerEpoch ??= run.phd2.Snapshot.ConnectionEpoch",
            "CheckPhysicalBoundary(context, requireIdleIllumination: true)");
        var readback = Section(source, "private async Task<(Phd2IdentityValidation Identity, Phd2AppState State)> ReadPhdStoppedIdentityAsync",
            "private GateResult CheckPhysicalBoundary");
        Ordered(readback, "await run.phd2.ValidateIdentityAsync", "await run.phd2.GetAppStateAsync(token)",
            "return (identity, state)", "catch (Exception)", "run.RejectClockCurrentFieldPhdOwner(", "throw;");
        var physical = Section(source, "private GateResult CheckPhysicalBoundary", "private static GateResult BudgetExhausted");
        foreach (var guard in new[]
        {
            "run.SequenceStopGate()", "run.startupGuideBoundaryIssue", "run.mountClockReconnectBoundary.BoundaryGate",
            "run.automaticFocusBoundaryIssue", "run.startupGuidePendingOperation is { IsCompleted: false }",
            "run.host.RealRunOwnershipGate()", "run.ValidateImmediatePhysicalActionGates(context)",
            "captureLease.IsCurrent(NativeCaptureOwner)", "mount.Slewing || mount.IsPulseGuiding || !mount.TrackingEnabled",
            "run.c11RunFocusLock.Validate", "NativeMeridianJournalStore.PendingGate",
            "run.pendingQhyCoarseReturn is not null || run.pendingG3SearchReturn is not null",
            "run.slitIlluminationSafetyIssue", "PositionContinuityGate(position)",
            "DateTimeOffset.UtcNow >= Deadline", "run.NativeTarget?.ClockBudget?.HasRemainingBudget(DateTimeOffset.UtcNow)",
        }) Assert.Contains(guard, physical);
        var fresh = Section(source, "public async Task<GateResult> ValidateFreshFieldAsync", "private GateResult? PositionContinuityGate");
        Ordered(fresh, "binding.FrameCompletedUtc < captureStartedUtc", "slit.CapturedUtc < captureStartedUtc",
            "!run.TryGetCurrentG3SlitGeometryRunCache(context, out _)", "PositionContinuityGate(before)",
            "await run.ValidateG3FieldMountBindingForMotionAsync(context, field, token)");
        var workflow = Source("ClockRecoveryFieldWorkflow.cs");
        Ordered(workflow, "await port.ValidateStoppedBoundaryAsync(context, token)", "receipt.TryConsume(",
            "await port.PersistAttemptAsync(receipt, context, token)", "await port.CaptureFreshAsync(context, token)",
            "if (!probe.CanFallback) return probe.Result", "await port.ValidateStoppedBoundaryAsync(context, token)",
            "var fresh = await port.ValidateFreshFieldAsync(context, token)");
    }

    [Fact]
    public void ProbeConnectionAndBothCapturePrimitivesRejectExternalPreviewBeforeTakeoverOrRetry()
    {
        var source = Source("RealObservationStageRunner.cs");
        var connected = Section(source, "private async Task EnsurePhdConnectedAsync", "private void RequireClockCurrentFieldExistingPhdOwner");
        Ordered(connected, "if (clockCurrentFieldProbeActive)", "cancellationToken.ThrowIfCancellationRequested()",
            "if (!phd2.IsConnected || !phd2.Snapshot.IsConnected)",
            "throw new PhysicalActionGateException", "return;", "await phd2.ConnectAsync(cancellationToken)");
        var guard = Section(source, "private void RequireClockCurrentFieldExistingPhdOwner", "private async Task<PlateSolveEvidence> SolveExternalFitsAsync");
        Ordered(guard, "if (!clockCurrentFieldProbeActive) return", "var state = phd2.Snapshot",
            "(rejectedBeforeMutationState ?? state.AppState) != Phd2AppState.Stopped",
            "throw new PhysicalActionGateException");
        Assert.Contains("new PhysicalActionGateException(RejectClockCurrentFieldPhdOwner(", guard);
        foreach (var (start, end, capture) in new[]
        {
            ("private async Task<Phd2SingleFrameResult> CaptureG3FullFrameCoreAsync",
                "private async Task<Phd2SingleFrameResult> CaptureG3NativeSingleFrameForAcquisitionAsync", "phd2.CaptureFullFrameAsync"),
            ("private async Task<Phd2SingleFrameResult> CaptureG3NativeSingleFrameCoreAsync",
                "private GateResult ValidateCorrectionBudget", "phd2.CaptureSingleFrameWithParametersAsync"),
        })
        {
            var core = Section(source, start, end);
            Ordered(core, "RequireClockCurrentFieldExistingPhdOwner();", "var previewState = phd2.Snapshot.AppState",
                "RequireClockCurrentFieldExistingPhdOwner(previewState)", "await phd2.StopCaptureAndConfirmAsync",
                "return await " + capture, "catch (Phd2CaptureException ex)",
                "RequireClockCurrentFieldExistingPhdOwner(ex.RejectedBeforeMutationState)",
                "await phd2.StopCaptureAndConfirmAsync", "return await " + capture);
        }
    }

    [Fact]
    public void ProbeUsesCatalogHintWithoutEnteringTheExistingQhySyncAndGotoHintRecovery()
    {
        var source = Source("RealObservationStageRunner.cs");
        var ladder = Section(source, "private async Task<G3PlateSolveProbeState> CaptureG3PlateSolveLadderAsync",
            "private GateResult ValidateG3SolveProbeImage(");
        Ordered(ladder, "var solveHint = currentPositionCheck || clockCurrentFieldProbeActive",
            "? new G3PlateSolveHintSelection(TargetCoordinates(context.Plan), \"ClockCurrentFieldCatalogHintOnly\"",
            ": await SelectG3PlateSolveHintAsync(context, cancellationToken, motionPrediction)",
            "for (var index = 0; index < tierCount; index++)");
        // The guarded alternative really is the existing action-capable path,
        // not just a similarly named helper or a deleted capability.
        var ordinary = Section(Source("RealObservationStageRunner.QhyG3SolvePair.cs"),
            "private async Task<G3PlateSolveHintSelection> SelectG3PlateSolveHintAsync",
            "private async Task<QhyG3RunLocalSearchHint?> TryApplyRunLocalPairSearchHintAsync");
        Ordered(ordinary, "await ValidateQhyAcceptedFrameMountBindingForMotionAsync",
            "var coordinateRecovery = await RecoverMountCoordinatesFromQhyWcsIfRequiredAsync",
            "coordinateRecovery.SyncVerified");
    }

    [Fact]
    public void LostFastProbeOwnerLatchesGenericCleanupAgainstStoppingANewerExternalSession()
    {
        var source = Source("RealObservationStageRunner.cs");
        var adapter = Source("RealObservationStageRunner.ClockRecoveryField.cs");
        var latch = Section(adapter, "private GateResult RejectClockCurrentFieldPhdOwner(",
            "private IClockRecoveryFieldPort CreateClockRecoveryFieldPort()");
        Ordered(latch, "clockCurrentFieldOwnerUncertain = true", "startupGuideBoundaryIssue ??= reason",
            "return GateResult.Unknown(\"CLOCK_CURRENT_FIELD_OWNER_CHANGED\", reason)");
        var fault = Section(source, "public override async Task OnFaultedAsync(", "public async ValueTask DisposeAsync()");
        Ordered(fault, "clockCurrentFieldOwnerUncertain)", "await OnCancelledAsync(context, cancellationToken)",
            "return;", "await ReturnDurableSlitPlacementForLifecycleAsync(");
        var cleanup = Section(source, "private async Task<IReadOnlyList<string>> CleanupAfterFailureCoreAsync(",
            "private async Task StopPhdAndWaitAsync(");
        var uncertain = Section(cleanup, "if (clockCurrentFieldOwnerUncertain)", "else if (phd2.IsConnected)");
        Ordered(uncertain, "failures.Add(\"CLOCK_CURRENT_FIELD_OWNER_CHANGED:", "allowMechanicalActions = false");
        Assert.DoesNotContain("StopCaptureAndConfirmAsync", uncertain);
        Ordered(cleanup, "if (clockCurrentFieldOwnerUncertain)", "else if (phd2.IsConnected)",
            "await phd2.StopCaptureAndConfirmAsync(phdSafetyStop.Token)", "ValidateConfirmedPhdStop(stopped, \"cleanup\")",
            "if (allowMechanicalActions && (configuration.Environment.CloseOpticalCoverOnFailure || safetyCloseout))");
        var join = Section(adapter, "private async Task<GateResult?> StopAndJoinAsync(", "public async Task<GateResult> ValidateFreshFieldAsync(");
        Ordered(join, "if (operation is { IsCompleted: false })", "run.startupGuidePendingOperation ??= operation",
            "run.RejectClockCurrentFieldPhdOwner(issue)", "readback = ReadStoppedAsync(window.Token)",
            "catch (Exception ex)", "run.RejectClockCurrentFieldPhdOwner(");
        var native = Section(Source("RealObservationStageRunner.NativeSequence.cs"),
            "internal async Task<IReadOnlyList<string>> ConfirmSequenceIdleAsync(",
            "internal async Task<IReadOnlyList<string>> CloseSequenceNightAsync(");
        Ordered(native, "await CleanupAfterFailureAsync(", "if (startupGuideBoundaryIssue is { } startupIssue)",
            "errors.Add(\"GUIDE_STARTUP_STOP_UNCONFIRMED: \" + startupIssue)",
            "canRecheckSequenceIdle = canRecheckSequenceIdle && automaticFocusBoundaryIssue is null && startupGuideBoundaryIssue is null");
    }

    [Theory]
    [InlineData("G3_PLATE_SOLVE_FAILED", false, true)]
    [InlineData("G3_PLATE_SOLVE_LADDER_EXHAUSTED_STRUCTURED_FIELD", false, true)]
    [InlineData("G3_SOLVED_TARGET_OUTSIDE", false, true)]
    [InlineData("TARGET_AMBIGUOUS", false, true)]
    [InlineData("G3_CLOUD_OR_TRANSPARENCY_INVALID", false, true)]
    [InlineData("G3_CATALOG_SHORT_POSITION_UNCONFIRMED", true, true)]
    [InlineData("G3_POST_WCS_SHORT_UNCONFIRMED", true, true)]
    [InlineData("G3_POST_WCS_SHORT_HANDOFF_OUTSIDE", false, true)]
    [InlineData("G3_MAIN_FOCUS_UNVERIFIED", true, true)]
    [InlineData("G3_MAIN_FOCUS_UNVERIFIED", false, false)]
    [InlineData("G3_FOCUS_STARS_TOO_BROAD", false, true)]
    [InlineData("SLIT_LED_PAIR_LOW_CONTRAST", false, true)]
    [InlineData("SLIT_ILLUMINATION_STOP_UNCONFIRMED", true, false)]
    [InlineData("PHD2_IDENTITY_STALE", true, false)]
    [InlineData("CLOCK_CURRENT_FIELD_OWNER_CHANGED", true, false)]
    [InlineData("CLOCK_CURRENT_FIELD_STOP_UNCONFIRMED", true, false)]
    [InlineData("G3_FRAME_HASH_MISMATCH", true, false)]
    [InlineData("G3_FRAME_MOUNT_BINDING_MISMATCH", true, false)]
    [InlineData("TARGET_CONTINUATION_TIME_LIMIT", true, false)]
    [InlineData("NIGHT_END_TIME_REACHED", true, false)]
    [InlineData("UNREVIEWED_OPTICAL_FAILURE", true, false)]
    public void OnlyReviewedOpticalMissesAllowCanonicalFallbackNotGenericTargetQualityOrSameCodeOwnerFailures(
        string code, bool targetQuality, bool expected)
    {
        var gate = targetQuality ? GateResult.TargetQuality(code, "fixture") : GateResult.Unknown(code, "fixture");
        Assert.Equal(expected, RealObservationStageRunner.ClockCurrentFieldAllowsOpticalFallback(gate));
    }

    private static string Source(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
                return File.ReadAllText(Path.Combine(directory.FullName, "src", "UvexAdv.Nina.Plugin", relative))
                    .Replace("\r\n", "\n", StringComparison.Ordinal);
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string Section(string source, string start, string end)
    {
        var first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, $"Missing production boundary: {start}");
        var last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.True(last > first, $"Missing end of production boundary: {end}");
        return source[first..last];
    }

    private static void Ordered(string source, params string[] calls)
    {
        var previous = -1;
        foreach (var call in calls)
        {
            var current = source.IndexOf(call, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"Missing or out-of-order production step: {call}");
            previous = current;
        }
    }
}
