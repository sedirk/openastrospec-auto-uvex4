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

    private async Task<G3AcquisitionMotionState?> ReadSettledG3ExposureLedgerAsync(ObservationContext context, CancellationToken token)
    {
        if (!HasNoOutstandingGuideRecoveryMotion) return null;
        var loaded = await G3AcquisitionMotionStore.LoadAsync(G3AcquisitionMotionPath(context.Plan.ObservationRunId), token).ConfigureAwait(false);
        return loaded.Error is null && loaded.State is { Phase: G3AcquisitionMotionPhase.SettledBudgetLedger } state &&
            state.Validate().Count == 0 && ValidateG3AcquisitionMotionIdentity(context, state).Disposition == GateDisposition.Passed
                ? state : null;
    }

    private async Task RecordAtrExposureClockAsync(ObservationContext context,
        Phd2LockShiftPendingState? before, string captureToken, DateTimeOffset openedUtc,
        DateTimeOffset completedUtc, double requestedSeconds, double monotonicElapsedSeconds,
        CancellationToken token, G3AcquisitionMotionState? g3Before = null)
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
        if (g3Before is not null && HasNoOutstandingGuideRecoveryMotion)
        {
            var g3After = await ReadSettledG3ExposureLedgerAsync(context, token).ConfigureAwait(false);
            var g3Credited = g3After is null ? null : G3SettledScienceContinuationPolicy.CreditExposure(
                g3Before, g3After, openedUtc, completedUtc, requestedSeconds, monotonicElapsedSeconds);
            if (g3Credited is not null)
            {
                await PersistG3AcquisitionMotionAsync(g3Credited, token).ConfigureAwait(false);
                if (fineAcquisitionStartedUtc is { } start)
                    fineAcquisitionStartedUtc = start.AddSeconds(g3Credited.ExcludedAtrExposureSeconds - g3Before.ExcludedAtrExposureSeconds);
                await WriteAuditBestEffortAsync("g3-atr-exposure-clock-excluded", new
                {
                    context.Plan.ObservationRunId, captureToken, openedUtc, completedUtc, requestedSeconds,
                    g3Credited.BudgetLineageId, g3Credited.StartedUtc, g3Credited.ExcludedAtrExposureSeconds,
                    g3Credited.CorrectionAttempts, g3Credited.CumulativeMotionArcseconds,
                    activeMotionElapsedSeconds = (completedUtc - g3Credited.MotionClockStartedUtc).TotalSeconds,
                    motionBudgetReset = false, nightDeadlineChanged = false, scienceAccepted = false,
                }).ConfigureAwait(false);
            }
        }
    }

    private async Task<GateResult?> CheckScienceRebuildBudgetAsync(ObservationContext context, GateResult sourceGate, CancellationToken token)
    {
        var loaded = await Phd2LockShiftPendingStore.LoadAsync(
            Phd2LockShiftPendingPath(context.Plan.ObservationRunId), token).ConfigureAwait(false);
        if (loaded.Error is not null)
            return GateResult.Unknown("PHD2_LOCK_LEDGER_RECONCILIATION_REQUIRED", loaded.Error);
        if (loaded.State is not { } ledger) return null;
        var preset = commissioning?.Value.Phd2SlitPlacement;
        var binding = preset is null
            ? GateResult.Unknown("COMMISSIONING_PRESET_REQUIRED", "No trusted PHD2 placement preset is loaded.")
            : ValidateCurrentPhd2LockLedgerBinding(context, preset, ledger);
        return Phd2ScienceContinuationPolicy.RebuildBudgetGate(ledger, DateTimeOffset.UtcNow, sourceGate, binding);
    }
}
