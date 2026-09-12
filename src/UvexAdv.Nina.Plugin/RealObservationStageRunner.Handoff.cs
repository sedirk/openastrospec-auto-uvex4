using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private int phd2CoarseHandoffRecoveryAttempts;

    private bool NeedsG3CoarseCentering(G3FieldState field) =>
        commissioning?.Value.Phd2SlitPlacement is { } preset &&
        field.TargetIdentification.Target is { } target &&
        field.SlitDetection.Gate.Disposition == GateDisposition.Passed &&
        G3WcsRecoveryPolicy.NeedsCoarseCentering(field.Gate,
            field.Solve?.Result.Success == true && field.Solve.Result.Coordinates is not null,
            PixelDistance(target.Centroid, field.SlitDetection.Geometry.AcquisitionPoint), preset.CoarseHandoffResidualPixels);

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
