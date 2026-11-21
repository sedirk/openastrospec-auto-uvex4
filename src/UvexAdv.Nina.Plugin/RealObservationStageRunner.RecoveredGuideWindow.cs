using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    // Passive verification only. The caller still goes through the ordinary
    // pre-shutter checks; a new epoch is never a synthetic native settle.
    private async Task<bool> TryAttestRecoveredFieldAsync(ObservationContext context,
        Phd2SlitPlacementSession session, Phd2SlitPlacementCommissioning preset,
        DateTimeOffset deadline, CancellationToken token)
    {
        var tolerance = preset.LockVerificationTolerancePixels;
        bool SameField(Phd2StateSnapshot s) => Phd2ScienceContinuationPolicy.IsNativeLossRecovery(
            s, session.ConnectionEpoch, session.GuideEpoch, session.Calibration.CalibrationChangeSequence,
            session.SelectedGuide, session.OriginLock, tolerance);
        if (!HasSupervisedScienceOptIn() || !session.Quality.IsLockShiftAuthority ||
            !HasNoOutstandingGuideRecoveryMotion || !SameField(phd2.Snapshot) || lastG3Field?.TargetIdentification.Target is null ||
            !GuideRecovery.TryConsumeRecoveredEpochWindow()) return false;

        var path = Phd2LockShiftPendingPath(context.Plan.ObservationRunId);
        var loaded = await Phd2LockShiftPendingStore.LoadAsync(path, token).ConfigureAwait(false);
        if (loaded.Error is not null || loaded.State is not { Phase: Phd2LockShiftPendingPhase.SettledBudgetLedger } ledger ||
            ledger.Validate().Count != 0 || ledger.ConnectionEpoch != session.ConnectionEpoch ||
            ledger.GuideEpoch != session.GuideEpoch) return false;
        var binding = ValidateCurrentPhd2LockLedgerBinding(context, preset, ledger);
        if (binding.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(binding);
        var actual = await phd2.GetLockPositionAsync(token).ConfigureAwait(false);
        if (actual is null || !(PointDistance(actual, session.OriginLock) <= tolerance) ||
            !(PointDistance(actual, new(ledger.CurrentLockX, ledger.CurrentLockY)) <= tolerance) ||
            !(PointDistance(actual, new(ledger.RequestedLockX, ledger.RequestedLockY)) <= tolerance)) return false;
        await RequireImmediatePhysicalActionGatesAsync(context, token).ConfigureAwait(false);
        var identity = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), token).ConfigureAwait(false);
        if (!identity.IsValid) throw new Phd2IdentityMismatchException(identity);
        var profile = ValidatePhdProfileBindingEvidence();
        if (profile.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(profile);
        var calibration = await phd2.ValidateCalibrationAsync(preset.CalibrationQualityPolicy.ApplyHardRejectionCeilings(
            PhdCalibrationRequirement(allowNativeCalibration: false)), token).ConfigureAwait(false);
        if (!calibration.IsValid || !calibration.Calibration.Calibrated || calibration.AllowNativeCalibration ||
            calibration.Profile != session.Calibration.Profile || calibration.ConnectionEpoch != session.ConnectionEpoch ||
            calibration.CalibrationChangeSequence != session.Calibration.CalibrationChangeSequence) return false;
        var before = phd2.Snapshot;
        if (!SameField(before) || DateTimeOffset.UtcNow >= deadline) return false;
        var proof = new Phd2PostLockGuidingObservation("native-loss-window-" + Guid.NewGuid().ToString("N"),
            before.ConnectionEpoch, before.GuideEpoch, before.EventSequence, actual, tolerance, DateTimeOffset.UtcNow,
            YieldedToFreshResiduals: true); // Warning-enabled profile only.
        IReadOnlyList<Phd2GuidingResidualState> frames;
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return false;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(remaining);
        try
        {
            frames = await CapturePhd2GuidingMeasurementsAsync(context, preset, session.Topology, actual,
                session.LastMeasurement.Measurement.TargetCentroid, session.LastMeasurement.RuntimeSlitLocal,
                session.GuideMode, Math.Max(3, preset.CalibrationQualityPolicy.RequiredFreshResidualsPerLockShiftStage),
                bounded.Token, referenceGuide: session.LastMeasurement.Measurement.GuideStar).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && bounded.IsCancellationRequested) { return false; }
        catch (Phd2GuidingFrameLostException) { return false; }
        catch (TargetQualityEvidenceException) { return false; }
        var end = phd2.Snapshot;
        if (!Phd2PlacementGuideWindowPolicy.HasUsableMeasuredWindow(frames.Select(f => f.Measurement).ToArray(),
                frames.Select(f => PointDistance(f.Measurement.GuideStar, actual)).ToArray(),
                SameField(end) && proof.IsCurrent(end), end.GuideOutput is { Failed: false, ConsecutiveMissingOutputs: 0 })) return false;
        proof = proof.AcceptResiduals(frames.Select(f => f.Frame).ToArray(), end);
        var errors = frames.Select(f => PointDistance(f.Measurement.TargetCentroid,
            f.Measurement.RecognizedSlitAcquisitionPoint)).ToArray();
        var precise = Phd2PlacementGuideWindowPolicy.AllWithinTolerance(errors,
            preset.BuildMotionLimits().TargetOnSlitTolerancePixels * session.Quality.RequiredResidualToleranceScale);
        if (!precise && !configuration.ScienceQuality.AllowsPreExposureWarning(true, true, errors,
                preset.MaximumAcquisitionResidualPixels)) return false;
        var last = frames[^1];
        var verifiedField = UpdateG3FieldFromGuidingResidual(lastG3Field, last, preset);
        var currentBinding = await ValidateG3FieldMountBindingForMotionAsync(context, verifiedField, token).ConfigureAwait(false);
        if (currentBinding.Code is "G3_FIELD_MOUNT_BINDING_STALE" or "G3_SPARSE_MEASURED_CONTINUITY_UNCONFIRMED") return false;
        if (currentBinding.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(currentBinding);
        await RequireImmediatePhysicalActionGatesAsync(context, token).ConfigureAwait(false);
        var latest = await Phd2LockShiftPendingStore.LoadAsync(path, token).ConfigureAwait(false);
        if (latest.Error is not null || latest.State != ledger || !HasNoOutstandingGuideRecoveryMotion ||
            !SameField(phd2.Snapshot) || !proof.HasAcceptedWindow(phd2.Snapshot)) return false;
        var rebound = Phd2ScienceContinuationPolicy.BindVerifiedLossWindow(ledger, proof, phd2.Snapshot,
            session.SelectedGuide, session.OriginLock, session.Calibration.CalibrationChangeSequence, last.Frame, DateTimeOffset.UtcNow);
        if (rebound is null || DateTimeOffset.UtcNow >= deadline) return false;
        await Phd2LockShiftPendingStore.WriteAtomicAsync(path, rebound, token).ConfigureAwait(false);
        phd2SettledHandoffBudget = rebound;
        if (pendingPhd2LockShift is not null) pendingPhd2LockShift = rebound;
        phd2SlitPlacementSession = session with { Calibration = calibration, GuideEpoch = proof.GuideEpoch,
            LastMeasurement = last, FreshGuidingWindowReplacedSettle = true, ReadOnlyPostLockObservation = proof,
            SlitPrecisionWarningActive = session.SlitPrecisionWarningActive || !precise };
        validatedG3GuideConnectionEpoch = proof.ConnectionEpoch;
        validatedG3GuideEpoch = proof.GuideEpoch;
        lastG3Field = verifiedField;
        await PublishRunJsonEvidenceAsync("phd2-native-loss-window-verified",
            "Same native lock after StarLost; three or more new target/guide/slit measurements; no WCS restart",
            new { session.GuideEpoch, newGuideEpoch = proof.GuideEpoch, proof.AcceptedResidualFrames,
                frames = frames.Select(f => f.Frame.Sha256), errors, precise, rebound.LineageId,
                motionIssued = false, budgetReset = false, scienceAccepted = false }, last.Frame.Path, token).ConfigureAwait(false);
        var finalLock = await phd2.GetLockPositionAsync(token).ConfigureAwait(false);
        var finalOwner = phd2.Snapshot;
        if (!SameField(finalOwner) || !Phd2ScienceContinuationPolicy.CanObserveAtSettledLock(rebound, finalOwner,
                proof.ConnectionEpoch, proof.GuideEpoch, IsGuidingStable(), finalLock, tolerance)) return false;
        preExposureReceipt = new(context.Plan.ObservationRunId, last.Frame.Sha256, last.Frame.CompletedUtc, finalOwner);
        return true;
    }
}
