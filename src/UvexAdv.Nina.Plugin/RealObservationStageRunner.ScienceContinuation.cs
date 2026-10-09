using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private Phd2PreExposureReceipt? preExposureReceipt;

    private async Task<bool> TryVerifyScienceInPlaceAsync(ObservationContext context, CancellationToken cancellationToken)
    {
        preExposureReceipt = null;
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
        if (!IsGuidingStable()) return false;
        var verifiedFrame = phd2SlitPlacementSession!.LastMeasurement.Frame;
        preExposureReceipt = new(context.Plan.ObservationRunId, verifiedFrame.Sha256,
            verifiedFrame.CompletedUtc, phd2.Snapshot);
        return true;
    }

    private async Task<Phd2LockShiftPendingState?> ReadSettledAtrExposureLedgerAsync(
        ObservationContext context, CancellationToken token)
    {
        if (phd2SlitPlacementSession is not { } session || pendingPhd2LockShift is not null) return null;
        var loaded = await Phd2LockShiftPendingStore.LoadAsync(
            Phd2LockShiftPendingPath(context.Plan.ObservationRunId), token).ConfigureAwait(false);
        if (loaded.Error is not null || loaded.State is not { } ledger) return null;
        var preset = commissioning!.Value.Phd2SlitPlacement!;
        if (ValidateCurrentPhd2LockLedgerBinding(context, preset, ledger).Disposition != GateDisposition.Passed) return null;
        var actual = await phd2.GetLockPositionAsync(token).ConfigureAwait(false);
        return Phd2ScienceContinuationPolicy.CanObserveAtSettledLock(ledger, phd2.Snapshot,
            session.ConnectionEpoch, session.GuideEpoch, IsGuidingStable(), actual,
            preset.BuildMotionLimits().LockVerificationTolerancePixels) ? ledger : null;
    }

    private async Task RecordAtrExposureClockAsync(ObservationContext context,
        Phd2LockShiftPendingState? before, string captureToken, DateTimeOffset openedUtc,
        DateTimeOffset completedUtc, double requestedSeconds, double monotonicElapsedSeconds,
        CancellationToken token)
    {
        if (before is null || pendingPhd2LockShift is not null) return;
        var path = Phd2LockShiftPendingPath(context.Plan.ObservationRunId);
        var loaded = await Phd2LockShiftPendingStore.LoadAsync(path, token).ConfigureAwait(false);
        if (loaded.Error is not null || loaded.State is not { } after) return;
        var credited = Phd2ScienceContinuationPolicy.CreditExposure(before, after,
            openedUtc, completedUtc, requestedSeconds, monotonicElapsedSeconds);
        if (credited is null) return;
        await Phd2LockShiftPendingStore.WriteAtomicAsync(path, credited, token).ConfigureAwait(false);
        phd2SettledHandoffBudget = credited;
        await WriteAuditBestEffortAsync("phd2-atr-exposure-clock-excluded", new
        {
            context.Plan.ObservationRunId, captureToken, openedUtc, completedUtc, requestedSeconds,
            credited.LineageId, credited.StartedUtc, credited.ExcludedAtrExposureSeconds,
            excludedThisCaptureSeconds = credited.ExcludedAtrExposureSeconds - before.ExcludedAtrExposureSeconds,
            credited.AttemptsUsed, credited.CumulativeCommandedPixels,
            activeMotionElapsedSeconds = (completedUtc - credited.MotionClockStartedUtc).TotalSeconds,
            motionBudgetReset = false, scienceAccepted = false,
        }).ConfigureAwait(false);
    }

    private async Task<GateResult?> CheckScienceRebuildBudgetAsync(ObservationContext context, GateResult sourceGate, CancellationToken token)
    {
        var loaded = await Phd2LockShiftPendingStore.LoadAsync(
            Phd2LockShiftPendingPath(context.Plan.ObservationRunId), token).ConfigureAwait(false);
        if (loaded.Error is not null)
            return GateResult.Unknown("PHD2_LOCK_LEDGER_RECONCILIATION_REQUIRED", loaded.Error);
        if (loaded.State is not { Phase: Phd2LockShiftPendingPhase.SettledBudgetLedger } ledger) return null;
        if (Phd2ScienceContinuationPolicy.HasReplacementBudget(ledger, DateTimeOffset.UtcNow)) return null;
        return GateResult.Unknown("PHD2_SCIENCE_RECOVERY_BUDGET_UNAVAILABLE",
            $"原始原因 {sourceGate.Code}: {sourceGate.Message} 导星/目标证据需要重建，但原精调账本已不能授权新的入缝运动；未启动回退定位。已保存的光谱保留原质量标记。" +
            $" 原精调消耗 {ledger.AttemptsUsed}/{ledger.MaximumAttempts} 次、{ledger.CumulativeCommandedPixels:F2}/{ledger.MaximumCumulativePixels:F2} px；" +
            $"精调计时 {(DateTimeOffset.UtcNow - ledger.MotionClockStartedUtc).TotalSeconds:F1}/{ledger.MaximumElapsedSeconds:F1} s；已排除确认的 ATR 曝光 {ledger.ExcludedAtrExposureSeconds:F1} s，原始起点未改。",
            new Dictionary<string, double> { ["attemptsUsed"] = ledger.AttemptsUsed,
                ["cumulativeCommandedPixels"] = ledger.CumulativeCommandedPixels,
                ["lineageWallSeconds"] = (DateTimeOffset.UtcNow - ledger.StartedUtc).TotalSeconds,
                ["excludedAtrExposureSeconds"] = ledger.ExcludedAtrExposureSeconds,
                ["activeMotionElapsedSeconds"] = (DateTimeOffset.UtcNow - ledger.MotionClockStartedUtc).TotalSeconds,
                ["maximumElapsedSeconds"] = ledger.MaximumElapsedSeconds });
    }
}
