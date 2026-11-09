using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private (long ConnectionEpoch, Phd2Point Lock)? phd2PreMotionOutputOwner;
    private int phd2OutputReconnectAttempts;


    private async Task<StageResult> StopInitialGuideQualityFailureAsync(
        Phd2StateSnapshot terminal, Phd2SettleResult settle, CancellationToken cancellationToken)
    {
        var identity = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), cancellationToken).ConfigureAwait(false);
        if (!identity.IsValid) throw new Phd2IdentityMismatchException(identity);
        _ = await phd2.GetAppStateAsync(cancellationToken).ConfigureAwait(false);
        var actual = await phd2.GetLockPositionAsync(cancellationToken).ConfigureAwait(false);
        phd2.ThrowIfGuideOutputFailed();
        var lostStars = AdaptiveGuideRecoveryEnabled &&
            Phd2DependencyRebuildStopPolicy.CanClassifyInitialLostStarFailure(
                phd2.Snapshot, terminal.ConnectionEpoch, settle, pendingPhd2LockShift is not null);
        if ((!lostStars && !Phd2DependencyRebuildStopPolicy.CanClassifyInitialSettleQualityFailure(
                phd2.Snapshot, terminal.ConnectionEpoch, settle, pendingPhd2LockShift is not null)) ||
            !Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(phd2.Snapshot,
                Volatile.Read(ref phd2GuidingEverStarted) != 0, terminal.ConnectionEpoch, terminal.LockPosition, actual))
            throw new InvalidOperationException("Initial guide terminal/owner changed before checked stop; not a skippable quality failure.");
        var stopped = await phd2.StopCaptureAndConfirmAsync(cancellationToken).ConfigureAwait(false);
        ValidateConfirmedPhdStop(stopped, "initial guiding quality failure");
        var evidence = await PublishRunJsonEvidenceAsync("phd2-initial-settle-quality-stop",
            "Initial guiding did not settle; owner stopped before any exact-lock motion",
            new { settle, stopped, terminal.ConnectionEpoch, terminal.GuideEpoch,
                exactLockMotionIssued = false, slitPlacementProven = false, budgetReset = false },
            lastG3Field?.FramePath, cancellationToken).ConfigureAwait(false);
        automaticRebuildStopProof = new(terminal.ConnectionEpoch, phd2.Snapshot.GuideEpoch, evidence);
        return new StageResult(Phd2DependencyRebuildStopPolicy.AfterConfirmedInitialGuideStop(settle, lostStars), evidence);
    }

    private async Task<StageResult> HaltFailedPhd2GuideOutputAsync(
        ObservationContext context, CancellationToken cancellationToken,
        Phd2FailedInitialGuideStopReceipt? initialGuideStopReceipt = null)
    {
        var failure = phd2.Snapshot.GuideOutput
            ?? throw new InvalidOperationException("Missing structured guide-output evidence.");
        var pending = pendingPhd2LockShift;
        var outstanding = pending is { Phase: not Phd2LockShiftPendingPhase.SettledBudgetLedger };
        if (outstanding)
        {
            pending = pending! with
            {
                Phase = Phd2LockShiftPendingPhase.ReturnRequired,
                UpdatedUtc = DateTimeOffset.UtcNow,
                LastReason = $"{Phd2GuideOutputStatus.FailureCode}: No verified pulse output. " +
                    "Return responsibility retained; no return command or optical-arrival claim was made.",
            };
            await Phd2LockShiftPendingStore.WriteAtomicAsync(Phd2LockShiftPendingPath(pending.ObservationRunId),
                pending, CancellationToken.None).ConfigureAwait(false);
            pendingPhd2LockShift = pending;
        }
        var evidence = await PublishRunJsonEvidenceAsync("phd2-guide-output-failed",
            "Native requested corrections have no reported mount pulse output",
            new { failure, initialGuideStopReceipt, state = phd2.Snapshot,
                outstandingExactLock = outstanding, pendingLineage = pending?.LineageId,
                noOutputIsNotWindOrPrecision = true, budgetReset = false, mountMoveIssued = false,
                originReturnProven = false, reconnectAttempted = false },
            lastG3Field?.FramePath, cancellationToken).ConfigureAwait(false);
        var metrics = new Dictionary<string, double>
        {
            ["consecutiveMissingPulseFrames"] = failure.ConsecutiveMissingOutputs,
            ["missingPulseWindowSeconds"] = (failure.UpdatedUtc - failure.FirstMissingUtc).TotalSeconds,
            ["requestedRaCorrectionPixels"] = failure.LastStep.RaGuideDistancePixels ?? 0,
            ["requestedDecCorrectionPixels"] = failure.LastStep.DecGuideDistancePixels ?? 0,
            ["reportedRaPulseMilliseconds"] = failure.LastStep.RaDurationMilliseconds ?? 0,
            ["reportedDecPulseMilliseconds"] = failure.LastStep.DecDurationMilliseconds ?? 0,
            ["outstandingExactLock"] = outstanding ? 1 : 0,
        };
        var code = outstanding ? "PHD2_GUIDE_OUTPUT_RETURN_PENDING" : Phd2GuideOutputStatus.FailureCode;
        try
        {
            var identity = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), cancellationToken).ConfigureAwait(false);
            if (!identity.IsValid) throw new Phd2IdentityMismatchException(identity);
            var state = await phd2.GetAppStateAsync(cancellationToken).ConfigureAwait(false);
            var actual = await phd2.GetLockPositionAsync(cancellationToken).ConfigureAwait(false);
            var ownedEpoch = outstanding ? pending!.ConnectionEpoch
                : phd2SlitPlacementSession?.ConnectionEpoch ?? phd2PreMotionOutputOwner?.ConnectionEpoch;
            var expected = outstanding ? new Phd2Point(pending!.CurrentLockX, pending.CurrentLockY)
                : phd2SlitPlacementSession?.LastMeasurement.Frame.NativeLockPosition ?? phd2PreMotionOutputOwner?.Lock;
            var owned = Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(phd2.Snapshot,
                Volatile.Read(ref phd2GuidingEverStarted) != 0, ownedEpoch, expected, actual);
            if (!owned && Volatile.Read(ref phd2GuidingEverStarted) != 0)
                owned = Phd2DependencyRebuildStopPolicy.CanStopFailedInitialGuide(phd2.Snapshot,
                    initialGuideStopReceipt, phd2PreMotionOutputOwner?.ConnectionEpoch,
                    phd2PreMotionOutputOwner?.Lock, actual, pending is not null || phd2SlitPlacementSession is not null);
            if (!owned && outstanding)
                owned = Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(phd2.Snapshot,
                    Volatile.Read(ref phd2GuidingEverStarted) != 0, ownedEpoch,
                    new Phd2Point(pending!.RequestedLockX, pending.RequestedLockY), actual);
            if (!owned)
                throw new InvalidOperationException($"Current {state} guide session/lock could not be confirmed as this run's; no external session was stopped.");
            var stopped = await phd2.StopCaptureAndConfirmAsync(cancellationToken).ConfigureAwait(false);
            ValidateConfirmedPhdStop(stopped, "guide output failure");
            metrics["confirmedStopped"] = 1;
            await PublishRunJsonEvidenceAsync("phd2-guide-output-stop-confirmed",
                "Owned failed guide output checked-stopped without lock or mount return",
                new { stopped, evidence, outstandingExactLock = outstanding, budgetReset = false,
                    physicalReturnProven = false }, evidence, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new StageResult(GateResult.Unknown("PHD2_GUIDE_OUTPUT_STOP_UNCONFIRMED",
                $"{Phd2GuideOutputStatus.FailureCode}: {ex.Message} 未自动重连或发送回程；保留原账本。", metrics), evidence);
        }
        Report("PHD2 持续请求修正却无实际脉冲输出；已停止本轮导星，保留原账本，不再消耗粗定位预算。");
        return new StageResult(GateResult.Unknown(code,
            outstanding
                ? "导星脉冲输出失效，已确认停止；尚有未结精调责任，不能重连后丢弃旧锁点或宣称物理回程完成。"
                : "导星脉冲输出失效，已确认停止；尚未发出精调，可在身份和安全复核后执行一次有界原生重连。", metrics), evidence);
    }

    private async Task<StageResult> RecoverPhd2GuideOutputBeforeMotionAsync(
        ObservationContext context, int postCalibrationDepth, int lostLockDepth, CancellationToken cancellationToken,
        Phd2FailedInitialGuideStopReceipt? initialGuideStopReceipt = null)
    {
        var halted = await HaltFailedPhd2GuideOutputAsync(context, cancellationToken, initialGuideStopReceipt).ConfigureAwait(false);
        if (halted.Gate.Code != Phd2GuideOutputStatus.FailureCode) return halted;
        if (phd2OutputReconnectAttempts >= 1)
            return new StageResult(GateResult.Unknown("PHD2_GUIDE_OUTPUT_RECOVERY_EXHAUSTED",
                "本轮已完成一次原生重连，但仍没有有效导星脉冲；已停止，未再次重连或重置预算。", halted.Gate.Metrics), halted.EvidencePath);
        phd2OutputReconnectAttempts++;
        try
        {
            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            var profileGate = ValidatePhdProfileBindingEvidence();
            if (profileGate.Disposition != GateDisposition.Passed) return new StageResult(profileGate, halted.EvidencePath);
            await PublishRunJsonEvidenceAsync("phd2-guide-output-reconnect-intent",
                "One checked native equipment reconnect before any exact-lock mutation",
                new { attempt = phd2OutputReconnectAttempts, maximumAttempts = 1,
                    pendingExactLock = false, profileChanged = false, budgetReset = false,
                    cameraOwnerRemainsPhd2 = true }, halted.EvidencePath, cancellationToken).ConfigureAwait(false);
            var previousConnectionEpoch = phd2.Snapshot.ConnectionEpoch;
            var restored = await phd2.ReconnectEquipmentAfterOutputFailureAsync(PhdIdentityRequirement(), cancellationToken).ConfigureAwait(false);
            var receipt = await PublishRunJsonEvidenceAsync("phd2-guide-output-reconnect-confirmed",
                "Same native equipment reconnected; new optical and guide proof still required",
                new { restored, actualPulseResponseValidated = false, budgetReset = false },
                halted.EvidencePath, cancellationToken).ConfigureAwait(false);
            var retiredTrack = AcceptConfirmedPhd2OutputReconnect(context.Plan.ObservationRunId,
                previousConnectionEpoch, restored, receipt);
            await PublishRunJsonEvidenceAsync("phd2-guide-output-optical-context-retired",
                "Old optical track retired at the verified native reconnect boundary; fresh measurements required",
                new { previousConnectionEpoch, restored.ConnectionEpoch, retiredTrack,
                    oldPixelsAdopted = false, budgetReset = false, returnProven = false },
                receipt, cancellationToken).ConfigureAwait(false);
            var acquired = await AcquireG3SlitFieldAsync(context, cancellationToken,
                allowChargedCurrentPositionHandoff: true).ConfigureAwait(false);
            if (!acquired.CanAdvance) return acquired;
            return await PlaceTargetOnSlitWithPhd2Async(context, cancellationToken,
                postCalibrationDepth, lostLockDepth).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (ResumeStageRestartException) { throw; }
        catch (Exception ex)
        {
            return Phd2OutputRecoveryFailure(ex, halted.EvidencePath);
        }
    }

    internal string? AcceptConfirmedPhd2OutputReconnect(string runId, long previousConnectionEpoch,
        Phd2EquipmentReconnectResult restored, string receipt)
    {
        var current = phd2.Snapshot;
        if (!restored.ConfirmedStopped || restored.CaptureStarted || restored.MotionCommandIssued ||
            restored.ConnectionEpoch != previousConnectionEpoch + 1 ||
            current.ConnectionEpoch != restored.ConnectionEpoch || !current.IsConnected ||
            current.AppState != Phd2AppState.Stopped || current.AutomationPaused || current.Phd2Paused ||
            current.GuideOutput?.Failed == true || current.Profile != restored.Profile ||
            current.Equipment != restored.After ||
            pendingPhd2LockShift is { Phase: not Phd2LockShiftPendingPhase.SettledBudgetLedger })
            throw new Phd2Exception("Verified native reconnect/stop changed before rebuilding optical evidence.");
        if (stellarTargetTrack is { } track &&
            (track.RunId != runId || track.ConnectionEpoch != previousConnectionEpoch))
            throw new PhysicalActionGateException(GateResult.Unknown("G3_TARGET_TRACK_CONTEXT_CHANGED",
                "The retained target track is not owned by the reconnected run/epoch; it was not adopted or discarded."));

        // Only an explicitly checked equipment reconnect retires this epoch's
        // optical state. Unexpected owner changes still fail in TargetTrack.
        // Never re-stamp old pixels with the new epoch, clear motion ledgers,
        // replenish retries or reset coarse/fine/science/time budgets here.
        var retiredTrack = stellarTargetTrack?.Path;
        automaticRebuildStopProof = new(restored.ConnectionEpoch, current.GuideEpoch, receipt);
        stellarTargetTrack = null;
        phd2PreMotionOutputOwner = null;
        phd2SlitPlacementSession = null;
        lastG3Field = null;
        return retiredTrack;
    }

    internal static StageResult Phd2OutputRecoveryFailure(Exception error, string? evidencePath)
    {
        // A quality failure during fresh reacquisition is not a failed device
        // reconnect. Preserve the producer's code, scope, metrics and evidence;
        // the native sequence still requires an independently checked stop.
        var result = error is PhysicalActionGateException gate
            ? new StageResult(gate.Gate, evidencePath)
            : new StageResult(GateResult.Unknown("PHD2_GUIDE_OUTPUT_RECONNECT_FAILED", error.Message), evidencePath);
        return ObservationWorkflowResults.WithFailureContext(result,
            "导星输出故障后的有界重连/重新验证未完成；未重置任何动作预算。");
    }
}
