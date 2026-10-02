using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private int phd2CoarseHandoffRecoveryAttempts;
    private Phd2LockShiftPendingState? phd2SettledHandoffBudget;

    private async Task<StageResult> WaitForPostCalibrationMountIdleAsync(
        Phd2DependencyRebuildStopProof stopProof, string expectedPierSide,
        CancellationToken cancellationToken)
    {
        Report("PHD2 校准已通过并停止采集；等待赤道仪导星脉冲结束及连续静止读回，再重新获取目标（最多 15 秒）");
        var samples = new List<object>();
        var gate = await Phd2MountIdleWait.WaitAsync(async token =>
        {
            await phd2.GetAppStateAsync(token).ConfigureAwait(false);
            if (!stopProof.IsCurrent(phd2.Snapshot))
                return GateResult.Unknown("PHD2_REBUILD_STOP_UNCONFIRMED",
                    "The stopped PHD2 connection/guide epoch changed during the mount handoff; no capture or motion was issued.");
            var mountGate = ValidateG3SearchMountState(expectedPierSide);
            samples.Add(new { timestampUtc = DateTimeOffset.UtcNow, mountGate.Code,
                connectionEpoch = phd2.Snapshot.ConnectionEpoch, guideEpoch = phd2.Snapshot.GuideEpoch });
            return mountGate;
        }, cancellationToken).ConfigureAwait(false);
        var evidence = await PublishRunJsonEvidenceAsync("phd2-post-calibration-mount-idle",
            "Read-only mount pulse drain after confirmed PHD2 stop",
            new { gate, samples, expectedPierSide, stopProof.ConnectionEpoch, stopProof.GuideEpoch,
                maximumWaitSeconds = Phd2MountIdleWait.MaximumWait.TotalSeconds,
                requiredQuietSeconds = Phd2MountIdleWait.QuietPeriod.TotalSeconds,
                originalBudgetsPreserved = true, motionOrCaptureIssued = false },
            stopProof.EvidencePath, cancellationToken).ConfigureAwait(false);
        return new StageResult(gate, evidence);
    }

    private double Phd2HandoffResidualPixels(G3FieldState field,
        Phd2SlitPlacementCommissioningPreset preset)
    {
        var initial = preset.HandoffResidualPixels(
            field.Gate.Disposition == GateDisposition.Passed &&
            field.Solve?.Result.Success == true && field.Solve.Result.Coordinates is not null &&
            field.TargetIdentification.HasCatalogPositionRefinement);
        return phd2SettledHandoffBudget is not { } spent ? initial :
            Phd2CoarseHandoffPolicy.LimitToRemainingLedger(initial, preset.BuildMotionLimits(),
                preset.CalibrationQualityPolicy.DegradedMaximumLockShiftScale, preset.MaximumResidualGrowthPixels,
                spent.AttemptsUsed, spent.CumulativeCommandedPixels, DateTimeOffset.UtcNow - spent.StartedUtc);
    }

    private bool NeedsG3CoarseCentering(G3FieldState field) =>
        commissioning?.Value.Phd2SlitPlacement is { } preset &&
        field.TargetIdentification.Target is { } target &&
        field.SlitDetection.Gate.Disposition == GateDisposition.Passed &&
        G3WcsRecoveryPolicy.NeedsCoarseCentering(field.Gate,
            field.Solve?.Result.Success == true && field.Solve.Result.Coordinates is not null,
            PixelDistance(target.Centroid, field.SlitDetection.Geometry.AcquisitionPoint) +
            field.TargetIdentification.CatalogPositionSpreadPixels, Phd2HandoffResidualPixels(field, preset));

    private async Task<StageResult> HandleDeniedPhd2AcquisitionBudgetAsync(
        ObservationContext context, Phd2SlitPlacementSession session,
        Phd2SlitPlacementCommissioningPreset preset, Phd2LockShiftAcquisitionBudget budget,
        int postCalibrationReacquisitionDepth, int lostLockReacquisitionDepth,
        CancellationToken cancellationToken)
    {
        // This gate runs only after existing read-only completion and explicit
        // quality-warning paths. A denial cannot dispatch another exact lock.
        // A non-settled lineage still owns its original return before any
        // coarse handoff, while an accepted historical endpoint is not debt.
        if (pendingPhd2LockShift is { Phase: not Phd2LockShiftPendingPhase.SettledBudgetLedger } outstanding)
            return await ReturnPhd2LockToOriginAsync(context, session,
                outstanding with { Phase = Phd2LockShiftPendingPhase.ReturnRequired },
                $"{budget.Code}: {budget.Message}", cancellationToken).ConfigureAwait(false);
        var residual = PointDistance(session.LastMeasurement.Measurement.TargetCentroid,
            session.LastMeasurement.Measurement.RecognizedSlitAcquisitionPoint);
        if (Phd2HandoffRecoveryPolicy.ShouldReacquire(budget, residual,
            preset.BuildMotionLimits().TargetOnSlitTolerancePixels * session.Quality.RequiredResidualToleranceScale +
            preset.MaximumResidualGrowthPixels))
            return await ReacquireG3ForPhd2HandoffAsync(context, budget.Code, budget.Message,
                postCalibrationReacquisitionDepth, lostLockReacquisitionDepth, cancellationToken).ConfigureAwait(false);
        var stopped = await EnsurePhdStoppedForAutomaticRebuildAsync(
            ObservationStage.PlaceTargetOnSlit, budget.Code, cancellationToken).ConfigureAwait(false);
        if (stopped.Disposition != GateDisposition.Passed) return new StageResult(stopped, session.LastMeasurement.Frame.Path);
        return new StageResult(GateResult.Unknown(budget.Code,
            $"{budget.Message} No new exact-lock command was issued; the existing ledger and limits were retained."),
            session.LastMeasurement.Frame.Path);
    }

    private async Task<StageResult> ReacquireG3ForPhd2HandoffAsync(
        ObservationContext context, string sourceCode, string reason,
        int postCalibrationReacquisitionDepth, int lostLockReacquisitionDepth,
        CancellationToken cancellationToken)
    {
        // This is a pre-motion handoff repair, not a relabelled LostLock and
        // never a budget replenishment. An outstanding exact-lock command must
        // follow its own verified return path instead.
        if (pendingPhd2LockShift is { Phase: not Phd2LockShiftPendingPhase.SettledBudgetLedger })
            return new StageResult(GateResult.Unknown("PHD2_COARSE_HANDOFF_RETURN_REQUIRED",
                $"{sourceCode}: {reason} 尚有未结精调动作；必须先按原账本核验回程。"), lastG3Field?.FramePath);
        if (phd2CoarseHandoffRecoveryAttempts >= 1)
        {
            var stop = await EnsurePhdStoppedForAutomaticRebuildAsync(
                ObservationStage.PlaceTargetOnSlit, sourceCode, cancellationToken).ConfigureAwait(false);
            if (stop.Disposition != GateDisposition.Passed) return new StageResult(stop);
            return new StageResult(GateResult.Unknown("PHD2_COARSE_HANDOFF_RECOVERY_EXHAUSTED",
                $"{sourceCode}: {reason} 本轮已完成一次有界 WCS／精调交接重建，仍未满足条件；保留原预算与证据，未发出新的精调。"), lastG3Field?.FramePath);
        }
        phd2CoarseHandoffRecoveryAttempts++;
        var evidence = await PublishRunJsonEvidenceAsync("phd2-coarse-handoff-rebuild",
            "Pre-motion guide/WCS handoff reconciliation within the existing run",
            new { sourceCode, reason, attempts = phd2CoarseHandoffRecoveryAttempts, maximumAttempts = 1,
                exactLockCommandIssued = false, durableMotionBudgetsReset = false,
                freshFormalWcsRequiredForCoarseMotion = true }, lastG3Field?.FramePath, cancellationToken).ConfigureAwait(false);
        var stopped = await EnsurePhdStoppedForAutomaticRebuildAsync(
            ObservationStage.PlaceTargetOnSlit, sourceCode, cancellationToken).ConfigureAwait(false);
        if (stopped.Disposition != GateDisposition.Passed) return new StageResult(stopped, evidence);
        phd2SlitPlacementSession = null;
        lastG3Field = null;
        Report($"WCS／导星交接需重建：{reason} 先重拍并按原账本完成粗定位，不让精调走到半途耗尽回程预算。");
        var acquired = await AcquireG3SlitFieldAsync(context, cancellationToken,
            allowChargedCurrentPositionHandoff: true).ConfigureAwait(false);
        if (!acquired.CanAdvance) return acquired;
        return await PlaceTargetOnSlitWithPhd2Async(context, cancellationToken,
            postCalibrationReacquisitionDepth, lostLockReacquisitionDepth).ConfigureAwait(false);
    }
}
