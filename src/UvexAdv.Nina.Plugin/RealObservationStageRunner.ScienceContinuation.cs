using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private async Task<bool> TryVerifyScienceInPlaceAsync(ObservationContext context, CancellationToken cancellationToken)
    {
        if (phd2SlitPlacementSession is not { } session || lastG3Field is null || !IsGuidingStable()) return false;
        var loaded = await Phd2LockShiftPendingStore.LoadAsync(
            Phd2LockShiftPendingPath(context.Plan.ObservationRunId), cancellationToken).ConfigureAwait(false);
        if (loaded.Error is not null || loaded.State is not { } ledger) return false;
        var preset = commissioning!.Value.Phd2SlitPlacement!;
        if (ValidateCurrentPhd2LockLedgerBinding(context, preset, ledger).Disposition != GateDisposition.Passed) return false;
        var actual = await phd2.GetLockPositionAsync(cancellationToken).ConfigureAwait(false);
        if (!Phd2ScienceContinuationPolicy.CanObserveAtSettledLock(ledger, phd2.Snapshot,
            session.ConnectionEpoch, session.GuideEpoch, IsGuidingStable(), actual,
            preset.BuildMotionLimits().LockVerificationTolerancePixels)) return false;
        var identity = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), cancellationToken).ConfigureAwait(false);
        if (!identity.IsValid || ValidatePhdProfileBindingEvidence().Disposition != GateDisposition.Passed) return false;
        await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
        Report("保持当前导星与锁点：原位复核新的目标/狭缝帧，不重新定位、不增加微调动作。");
        await VerifyWindSampledGuidingBeforeAtrAsync(context, cancellationToken, forceFreshWindow: true).ConfigureAwait(false);
        await PublishRunJsonEvidenceAsync("phd2-science-in-place-verified",
            "Fresh optical verification at the existing settled lock; no motion or new budget",
            new { ledger.LineageId, ledger.StartedUtc, ledger.AttemptsUsed, ledger.CumulativeCommandedPixels,
                sameGuideEpoch = phd2.Snapshot.GuideEpoch == session.GuideEpoch,
                guidingStopped = false, motionIssued = false, budgetReset = false,
                phd2.Snapshot.LastConfigurationChange },
            phd2SlitPlacementSession!.LastMeasurement.Frame.Path, cancellationToken).ConfigureAwait(false);
        return IsGuidingStable();
    }

    private async Task<GateResult?> CheckScienceRebuildBudgetAsync(ObservationContext context, CancellationToken token)
    {
        var loaded = await Phd2LockShiftPendingStore.LoadAsync(
            Phd2LockShiftPendingPath(context.Plan.ObservationRunId), token).ConfigureAwait(false);
        if (loaded.Error is not null)
            return GateResult.Unknown("PHD2_LOCK_LEDGER_RECONCILIATION_REQUIRED", loaded.Error);
        if (loaded.State is not { Phase: Phd2LockShiftPendingPhase.SettledBudgetLedger } ledger) return null;
        if (Phd2ScienceContinuationPolicy.HasReplacementBudget(ledger, DateTimeOffset.UtcNow)) return null;
        return GateResult.Unknown("PHD2_SCIENCE_RECOVERY_BUDGET_UNAVAILABLE",
            "导星/目标证据需要重建，但原精调账本已不能授权新的入缝运动；未启动回退定位。已保存的光谱保留原质量标记。" +
            $" 原精调消耗 {ledger.AttemptsUsed}/{ledger.MaximumAttempts} 次、{ledger.CumulativeCommandedPixels:F2}/{ledger.MaximumCumulativePixels:F2} px；" +
            $"自首次精调已过 {(DateTimeOffset.UtcNow - ledger.StartedUtc).TotalSeconds:F1} s（含正常曝光），新运动时限 {ledger.MaximumElapsedSeconds:F1} s。",
            new Dictionary<string, double> { ["attemptsUsed"] = ledger.AttemptsUsed,
                ["cumulativeCommandedPixels"] = ledger.CumulativeCommandedPixels,
                ["lineageWallSeconds"] = (DateTimeOffset.UtcNow - ledger.StartedUtc).TotalSeconds,
                ["maximumElapsedSeconds"] = ledger.MaximumElapsedSeconds });
    }
}
