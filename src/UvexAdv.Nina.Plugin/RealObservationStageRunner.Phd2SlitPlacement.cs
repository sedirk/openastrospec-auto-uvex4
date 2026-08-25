using System.Globalization;
using System.IO;
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private StageResult ReusePhd2SlitPlacementGuiding(Phd2SlitPlacementSession session)
    {
        var snapshot = phd2.Snapshot;
        var residual = PointDistance(
            session.LastMeasurement.Measurement.TargetCentroid,
            session.LastMeasurement.Measurement.RecognizedSlitAcquisitionPoint);
        var metrics = Phd2QualityMetrics(session.Quality, session.SelectedGuide, session.Settle, residual);
        if (!snapshot.HasCurrentSuccessfulSettle ||
            snapshot.ConnectionEpoch != session.ConnectionEpoch ||
            snapshot.GuideEpoch != session.GuideEpoch)
        {
            phd2SlitPlacementSession = null;
            return Attention(
                ObservationStage.StartGuiding,
                "PHD2_PLACEMENT_SETTLE_STALE",
                "The PHD2 slit-placement guide/settle epoch changed before StartGuiding. It will not be restarted from stale target/slit evidence.",
                metrics);
        }
        validatedG3GuideConnectionEpoch = snapshot.ConnectionEpoch;
        validatedG3GuideEpoch = snapshot.GuideEpoch;
        var requiresSupervision = RequiresSupervisedPhd2Science(session.Quality, session.GuideMode);
        var unattendedAuthority = IsUnattendedPhd2ScienceAuthority(session.Quality, session.GuideMode);
        metrics["phd2RequiresOperatorSupervision"] = requiresSupervision ? 1 : 0;
        metrics["phd2IsUnattendedScienceAuthority"] = unattendedAuthority ? 1 : 0;
        var supervisedOptIn = configuration.AllowDegradedSupervisedScience &&
            session.Quality.IsLockShiftAuthority;
        if ((requiresSupervision || !unattendedAuthority) &&
            !supervisedOptIn)
        {
            return Attention(
                ObservationStage.StartGuiding,
                "PHD2_GUIDING_SUPERVISED_ONLY",
                $"PHD2 guiding remains settled and slit placement is complete, but calibration grade {session.Quality.Grade} is supervised-only. ATR unattended science authority is withheld. {string.Join(" ", session.Quality.Reasons)}",
                metrics);
        }
        if (supervisedOptIn && (requiresSupervision || !unattendedAuthority))
        {
            metrics["degradedSupervisedScience"] = 1;
            return Passed(
                "PHD2_GUIDING_DEGRADED_SUPERVISED",
                $"StartGuiding reused the current same-epoch settle. This run explicitly opted into short, supervised degraded science with calibration grade {session.Quality.Grade}; unattended authority remains false and all evidence is labeled degraded.",
                metrics,
                commissioning is null ? null : Metadata(commissioning));
        }
        return Passed(
            "PHD2_GUIDING_REUSED_FROM_SLIT_PLACEMENT",
            $"StartGuiding reused the current operation-bound settled guide epoch from PHD2 slit placement; no select/guide/recalibration command was sent. Policy {session.Quality.PolicyId}, grade {session.Quality.Grade}.",
            metrics,
            commissioning is null ? null : Metadata(commissioning));
    }

    private async Task<StageResult> StartGradedPhd2GuidingAfterIndependentPlacementAsync(
        ObservationContext context,
        CancellationToken cancellationToken)
    {
        var loaded = commissioning
            ?? throw new InvalidOperationException("Commissioning preset is not loaded.");
        var preset = loaded.Value.Phd2SlitPlacement;
        if (preset is null || preset.Validate().Count > 0)
            return Attention(ObservationStage.StartGuiding, "PHD2_GRADED_GUIDING_COMMISSIONING_REQUIRED", "Auto fallback placed the target with the independent transform, but the hash-bound PHD2 guide/quality/exposure commissioning is absent or invalid.");
        if (lastG3Field?.TargetIdentification.Target is null || lastG3Field.Gate.Disposition != GateDisposition.Passed)
            return Attention(ObservationStage.StartGuiding, "PHD2_GRADED_GUIDING_FIELD_REQUIRED", "A current quality-gated target/slit field is required before graded PHD2 guiding.");

        try
        {
            await EnsurePhdConnectedAsync(cancellationToken).ConfigureAwait(false);
            var identity = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), cancellationToken).ConfigureAwait(false);
            if (!identity.IsValid)
                return Attention(ObservationStage.StartGuiding, "PHD2_GRADED_GUIDING_IDENTITY_INVALID", string.Join(" ", identity.Failures.Concat(identity.IndeterminateReasons)));
            var profileGate = ValidatePhdProfileBindingEvidence();
            if (profileGate.Disposition != GateDisposition.Passed) return new StageResult(profileGate);
            var pierSide = telescopeMediator.GetInfo().SideOfPier.ToString();
            if (!IsKnownPierSide(pierSide) || !string.Equals(pierSide, preset.PierSide, StringComparison.Ordinal))
                return Attention(ObservationStage.StartGuiding, "PHD2_GRADED_GUIDING_PIER_MISMATCH", $"Current pier '{pierSide}' does not match commissioned '{preset.PierSide}'.");
            var topology = BuildPhd2SensorTopology(preset, pierSide);
            if (!SameHash(topology.ComputeFingerprintSha256(), preset.LockedTopologyFingerprintSha256))
                return Attention(ObservationStage.StartGuiding, "PHD2_GRADED_GUIDING_TOPOLOGY_MISMATCH", "PHD2 camera/profile/ROI/binning/rotation/install-epoch/mount/pier topology changed.");

            var policy = preset.CalibrationQualityPolicy;
            var calibrationBefore = await phd2.ValidateCalibrationAsync(
                policy.ApplyHardRejectionCeilings(PhdCalibrationRequirement()),
                cancellationToken).ConfigureAwait(false);
            var forceRecalibration = calibrationBefore.Status != Phd2ValidationStatus.Valid;
            if (!forceRecalibration)
            {
                var preGuide = SelectPhd2CalibrationQuality(
                    calibrationBefore,
                    preset,
                    Phd2CalibrationEvaluationPhase.PreGuide,
                    null,
                    null,
                    Phd2CalibrationSelectionPurpose.ValidationGuide);
                if (preGuide.Selected?.CanAttemptValidationGuide != true)
                    return Attention(ObservationStage.StartGuiding, "PHD2_CALIBRATION_PRE_GUIDE_REJECTED", CalibrationSelectionMessage(preGuide));
                if (preGuide.Selected.RequiresOperatorSupervision && !configuration.AllowDegradedSupervisedScience)
                    return Attention(ObservationStage.StartGuiding, "PHD2_DEGRADED_SUPERVISION_OPT_IN_REQUIRED", $"Calibration grade {preGuide.Selected.Grade} requires this run's explicit supervised opt-in; no selection/guide command was sent.", Phd2QualityMetrics(preGuide.Selected, new Phd2Point(0, 0), new Phd2SettleResult(false, null, 0, 0, DateTimeOffset.MinValue), double.NaN));
            }

            var choice = await AcquireFreshPhd2PlacementGuideAsync(
                context,
                lastG3Field,
                preset,
                cancellationToken).ConfigureAwait(false);
            if (choice.Selection.Gate.Disposition != GateDisposition.Passed ||
                (choice.Mode == Phd2SlitGuideMode.DegradedDirectTargetGuiding && choice.Selection.Star is null))
                return new StageResult(choice.Selection.Gate, choice.Field.FramePath);
            lastG3Field = choice.Field;
            if (choice.Mode == Phd2SlitGuideMode.DegradedDirectTargetGuiding &&
                !configuration.AllowDegradedSupervisedScience)
            {
                return Attention(
                    ObservationStage.StartGuiding,
                    "PHD2_DIRECT_TARGET_SUPERVISION_OPT_IN_REQUIRED",
                    "Fresh selection resolved to degraded direct-target guiding. This run has no explicit supervised-science opt-in, so no guide or lock command was sent.");
            }
            var target = choice.Field.TargetIdentification.Target!;
            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            var loop = await phd2.StartLoopingAndWaitForFreshFrameAsync(
                new Phd2LoopingStartRequest(TimeSpan.FromSeconds(preset.FreshLoopFrameTimeoutSeconds)),
                cancellationToken).ConfigureAwait(false);
            if (!loop.LeavesLoopingForGuideTakeover || loop.StopCommandSent || loop.ExposureChanged)
                throw new InvalidOperationException("PHD2 full-frame guide takeover contract failed.");
            var preSelectBinding = await ValidateG3FieldMountBindingForMotionAsync(
                context,
                choice.Field,
                cancellationToken).ConfigureAwait(false);
            if (preSelectBinding.Disposition != GateDisposition.Passed)
            {
                await StopPhdAndWaitAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"{preSelectBinding.Code}: {preSelectBinding.Message}");
            }
            var guideSelectionResult = await SelectFreshPhd2GuideAsync(
                choice,
                preset,
                cancellationToken).ConfigureAwait(false);
            var selected = guideSelectionResult.Selected;
            var guideSelectionRoi = BuildPhd2GuideSelectionRoi(
                selected,
                preset.SensorWidthPixels,
                preset.SensorHeightPixels);
            await PublishPhd2GuideSelectionEvidenceAsync(
                context,
                choice.Field,
                guideSelectionResult.Selection,
                guideSelectionResult.Requested,
                selected,
                preset,
                choice.Mode,
                choice.Capture,
                loop,
                guideSelectionRoi,
                cancellationToken).ConfigureAwait(false);

            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            var preGuideBinding = await ValidateG3FieldMountBindingForMotionAsync(
                context,
                choice.Field,
                cancellationToken).ConfigureAwait(false);
            if (preGuideBinding.Disposition != GateDisposition.Passed)
            {
                await StopPhdAndWaitAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"{preGuideBinding.Code}: {preGuideBinding.Message}");
            }
            Volatile.Write(ref phd2GuidingEverStarted, 1);
            var settle = await phd2.GuideAndSettleAsync(
                Phd2SettleCriteriaFromConfiguration(),
                forceRecalibration,
                guideSelectionRoi,
                cancellationToken).ConfigureAwait(false);
            if (!settle.Succeeded || !phd2.Snapshot.HasCurrentSuccessfulSettle)
                throw new InvalidOperationException(settle.Error ?? "The local PHD2 guide operation did not leave a current settle attestation.");
            var proof = phd2.Snapshot;
            var calibration = await phd2.ValidateCalibrationAsync(
                policy.ApplyHardRejectionCeilings(PhdCalibrationRequirement(forceRecalibration ? DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1) : null)),
                cancellationToken).ConfigureAwait(false);
            if (calibration.Status != Phd2ValidationStatus.Valid)
                throw new InvalidOperationException($"Post-guide calibration failed policy hard ceilings: {string.Join(" ", calibration.Failures.Concat(calibration.IndeterminateReasons))}");
            var lockPosition = await phd2.GetLockPositionAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("PHD2 did not report the selected runtime lock position.");
            var measurements = await CapturePhd2GuidingMeasurementsAsync(
                context,
                preset,
                topology,
                lockPosition,
                ToPhd2Domain(target.Centroid, preset),
                choice.Field.SlitDetection.Geometry,
                choice.Mode,
                policy.RequiredFreshResidualsPerLockShiftStage,
                cancellationToken).ConfigureAwait(false);
            var measurement = measurements[^1];
            var guideResidual = PointDistance(measurement.Measurement.GuideStar, lockPosition);
            var post = SelectPhd2CalibrationQuality(
                calibration,
                preset,
                Phd2CalibrationEvaluationPhase.PostSettle,
                CreateCalibrationSettleEvidence(settle, proof),
                CreateCalibrationResidualEvidence(measurement, guideResidual, preset, topology, choice.Mode),
                Phd2CalibrationSelectionPurpose.LockShift);
            var quality = post.Selected;
            if (quality?.IsLockShiftAuthority != true)
                throw new InvalidOperationException($"Post-settle graded guide authority failed: {CalibrationSelectionMessage(post)}");
            if (RequiresSupervisedPhd2Science(quality, choice.Mode) && !configuration.AllowDegradedSupervisedScience)
            {
                await StopPhdAndWaitAsync(cancellationToken).ConfigureAwait(false);
                return Attention(ObservationStage.StartGuiding, "PHD2_DEGRADED_SUPERVISION_OPT_IN_REQUIRED", $"Post-settle grade {quality.Grade} is supervised-only. Guiding was stopped; no science exposure is authorized.", Phd2EffectiveQualityMetrics(quality, choice.Mode, selected, settle, PointDistance(measurement.Measurement.TargetCentroid, measurement.Measurement.RecognizedSlitAcquisitionPoint)));
            }
            var qualification = BuildPhd2LockShiftQualification(identity, calibration, topology, preset, quality, pierSide);
            if (!qualification.IsQualified)
                throw new InvalidOperationException($"Graded PHD2 guide qualification failed: {string.Join(" ", qualification.Failures)}");
            var session = new Phd2SlitPlacementSession(
                choice.Mode,
                topology,
                qualification,
                quality,
                calibration,
                selected,
                lockPosition,
                measurement.Measurement.TargetCentroid,
                measurement.RuntimeSlitLocal,
                measurement,
                settle,
                proof.ConnectionEpoch,
                proof.GuideEpoch,
                forceRecalibration);
            phd2SlitPlacementSession = session;
            lastG3Field = UpdateG3FieldFromGuidingResidual(choice.Field, measurement, preset);
            return ReusePhd2SlitPlacementGuiding(session);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            try { await StopPhdAndWaitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            phd2SlitPlacementSession = null;
            return Attention(ObservationStage.StartGuiding, "PHD2_GRADED_GUIDING_FAILED_SAFE", $"Graded PHD2 guiding stopped without retry: {ex.Message}");
        }
    }

    private async Task<StageResult?> RecoverOutstandingPhd2LockBeforePlacementAsync(
        ObservationContext context,
        CancellationToken cancellationToken)
    {
        var loaded = commissioning;
        var preset = loaded?.Value.Phd2SlitPlacement;
        if (loaded is null || loaded.Value.SchemaVersion != 3 || preset is null || preset.Validate().Count > 0)
        {
            return Attention(
                ObservationStage.PlaceTargetOnSlit,
                "PHD2_LOCK_RECOVERY_SCHEMA3_REQUIRED",
                "A valid hash-bound schema-4 PHD2/slit-identity commissioning preset is required before durable runtime-lock recovery.");
        }

        var discovered = await Phd2LockShiftPendingStore.DiscoverAsync(
            SlitPlacementObservationsRoot(),
            cancellationToken).ConfigureAwait(false);
        var unreadable = discovered.Where(item => item.Error is not null || item.State is null).ToArray();
        if (unreadable.Length > 0)
        {
            return Attention(
                ObservationStage.PlaceTargetOnSlit,
                "PHD2_LOCK_LEDGER_DISCOVERY_UNTRUSTED",
                $"{unreadable.Length} discovered PHD2 lock ledger(s) could not be validated; no recovery or new motion is allowed.");
        }

        var outstanding = new List<Phd2LockShiftPendingFileResult>();
        foreach (var item in discovered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = item.State!;
            var canonical = Phd2LockShiftPendingPath(state.ObservationRunId);
            if (!string.Equals(Path.GetFullPath(item.Path), Path.GetFullPath(canonical), StringComparison.OrdinalIgnoreCase))
            {
                return Attention(
                    ObservationStage.PlaceTargetOnSlit,
                    "PHD2_LOCK_LEDGER_PATH_IDENTITY_MISMATCH",
                    $"Durable PHD2 ledger '{item.Path}' is not its run-bound canonical path '{canonical}'.");
            }
            var manifest = await ValidatePhd2LockManifestAsync(item, cancellationToken).ConfigureAwait(false);
            if (manifest.Error is not null) return new StageResult(manifest.Error, item.Path);
            var isCurrentRun = string.Equals(state.ObservationRunId, context.Plan.ObservationRunId, StringComparison.Ordinal);
            if (!isCurrentRun && !manifest.RunIsTerminal)
            {
                // A hard-crashed process cannot make its manifest terminal.
                // Cross-run adoption is permitted only inside a newly and
                // explicitly started real RunAsync that holds the live
                // machine-wide owner lease. The immutable manifest/context
                // bindings below still have to match exactly; the old lineage,
                // counters and clock are retained by the recovery routine.
                var ownerGate = host.RealRunOwnershipGate();
                if (ownerGate.Disposition != GateDisposition.Passed)
                {
                    return new StageResult(ownerGate, item.Path);
                }
            }

            // SettledBudgetLedger is an accepted scientific endpoint, not a
            // latent return obligation. A later observation must never revive
            // it merely because the accepted lock differs from its start.
            var requiresRecovery = state.Phase != Phd2LockShiftPendingPhase.SettledBudgetLedger;
            if (requiresRecovery) outstanding.Add(item);
        }

        if (outstanding.Count == 0) return null;
        var lineages = outstanding.Select(item => item.State!.LineageId).Distinct(StringComparer.Ordinal).ToArray();
        if (lineages.Length != 1 || outstanding.Count != 1)
        {
            return Attention(
                ObservationStage.PlaceTargetOnSlit,
                "PHD2_LOCK_RECOVERY_LINEAGE_AMBIGUOUS",
                $"Durable discovery found {outstanding.Count} outstanding copy/copies across {lineages.Length} lineage(s); automatic exact-lock return is prohibited.");
        }

        var selected = outstanding[0];
        var selectedIsForeign = !string.Equals(
            selected.State!.ObservationRunId,
            context.Plan.ObservationRunId,
            StringComparison.Ordinal);
        if (selectedIsForeign)
        {
            var currentCopies = discovered.Where(item => string.Equals(
                item.State!.ObservationRunId,
                context.Plan.ObservationRunId,
                StringComparison.Ordinal)).ToArray();
            if (currentCopies.Length > 1)
            {
                return Attention(
                    ObservationStage.PlaceTargetOnSlit,
                    "PHD2_LOCK_HANDOFF_CURRENT_RUN_FORK",
                    $"Foreign lineage {selected.State.LineageId} found {currentCopies.Length} current-run copies; no recovery motion is allowed.");
            }
            if (currentCopies.Length == 1)
            {
                var currentCopy = currentCopies[0].State!;
                var handoffIssues = Phd2LockShiftBudgetHandoff.ValidateCompletedHandoff(
                    selected.State,
                    currentCopy,
                    context.Plan.ObservationRunId,
                    ComputeSlitRecoveryContextSha256(context));
                var currentBinding = ValidateCurrentPhd2LockLedgerBinding(context, preset, currentCopy);
                if (handoffIssues.Count > 0 || currentBinding.Disposition != GateDisposition.Passed)
                {
                    return Attention(
                        ObservationStage.PlaceTargetOnSlit,
                        "PHD2_LOCK_HANDOFF_COPY_INCONSISTENT",
                        $"A current-run PHD2 copy exists but cannot prove a completed same-lineage handoff: {string.Join("; ", handoffIssues.Append(currentBinding.Message))}. No recovery motion was sent.");
                }

                var reconciledSource = selected.State with
                {
                    CurrentLockX = selected.State.OriginLockX,
                    CurrentLockY = selected.State.OriginLockY,
                    RequestedLockX = selected.State.OriginLockX,
                    RequestedLockY = selected.State.OriginLockY,
                    Phase = Phd2LockShiftPendingPhase.SettledBudgetLedger,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    LastReason = $"Current run {context.Plan.ObservationRunId} already held the atomic same-lineage settled handoff; the foreign historical copy was closed without another command.",
                };
                await Phd2LockShiftPendingStore.WriteAtomicAsync(
                    selected.Path,
                    reconciledSource,
                    CancellationToken.None).ConfigureAwait(false);
                return Attention(
                    ObservationStage.PlaceTargetOnSlit,
                    "PHD2_LOCK_HANDOFF_CRASH_WINDOW_RECONCILED",
                    $"Foreign lineage {selected.State.LineageId} was already handed to this run with {currentCopy.AttemptsUsed} attempts and {currentCopy.CumulativeCommandedPixels:F3}px consumed. Its old copy was closed without motion; Resume will reacquire a fresh field using the inherited budget.");
            }
        }
        var binding = ValidateCurrentPhd2LockLedgerBinding(context, preset, selected.State!);
        if (binding.Disposition != GateDisposition.Passed) return new StageResult(binding, selected.Path);
        return await RecoverPersistedPhd2LockToOriginAsync(
            context,
            preset,
            selected,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<StageResult> RecoverPersistedPhd2LockToOriginAsync(
        ObservationContext context,
        Phd2SlitPlacementCommissioningPreset preset,
        Phd2LockShiftPendingFileResult item,
        CancellationToken cancellationToken)
    {
        var state = item.State!;
        await EnsurePhdConnectedAsync(cancellationToken).ConfigureAwait(false);
        var identity = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), cancellationToken).ConfigureAwait(false);
        if (!identity.IsValid)
            return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RECOVERY_IDENTITY_INVALID", string.Join(" ", identity.Failures.Concat(identity.IndeterminateReasons)));
        var profileGate = ValidatePhdProfileBindingEvidence();
        if (profileGate.Disposition != GateDisposition.Passed) return new StageResult(profileGate, item.Path);

        var pierSide = telescopeMediator.GetInfo().SideOfPier.ToString();
        if (!IsKnownPierSide(pierSide) || !string.Equals(pierSide, preset.PierSide, StringComparison.Ordinal))
            return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RECOVERY_PIER_MISMATCH", $"Fresh pier side '{pierSide}' does not match commissioned '{preset.PierSide}'.");
        var topology = BuildPhd2SensorTopology(preset, pierSide);
        var topologySha256 = topology.ComputeFingerprintSha256();
        if (!SameHash(topologySha256, state.TopologyFingerprintSha256) ||
            !SameHash(topologySha256, preset.LockedTopologyFingerprintSha256))
            return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RECOVERY_TOPOLOGY_MISMATCH", "Fresh PHD2 profile/camera/ROI/binning/rotation/install/mount/pier topology does not match the durable ledger and preset.");

        // A PHD2 client epoch is process-local. After cancellation cleanup or
        // process restart, a numerically equal epoch is not continuity proof.
        // Establish a completely new, commissioned guide/settle epoch and
        // translate the old physical return vector into its fresh lock domain.
        if (phd2.Snapshot.AppState == Phd2AppState.Guiding)
        {
            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            await StopPhdAndWaitAsync(cancellationToken).ConfigureAwait(false);
        }
        lastG3Field = await CaptureAndAnalyzeG3Async(context, cancellationToken).ConfigureAwait(false);
        if (lastG3Field.Gate.Disposition != GateDisposition.Passed || lastG3Field.TargetIdentification.Target is null)
            return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RECOVERY_FRESH_FIELD_REQUIRED", $"Fresh catalog target/slit reacquisition failed: {lastG3Field.Gate.Code}: {lastG3Field.Gate.Message}");
        var fieldBinding = await ValidateG3FieldMountBindingForMotionAsync(context, lastG3Field, cancellationToken).ConfigureAwait(false);
        if (fieldBinding.Disposition != GateDisposition.Passed) return new StageResult(fieldBinding, lastG3Field.FramePath);

        var guideChoice = await AcquireFreshPhd2PlacementGuideAsync(context, lastG3Field, preset, cancellationToken).ConfigureAwait(false);
        if (guideChoice.Selection.Gate.Disposition != GateDisposition.Passed ||
            (guideChoice.Mode == Phd2SlitGuideMode.DegradedDirectTargetGuiding && guideChoice.Selection.Star is null))
            return new StageResult(guideChoice.Selection.Gate, guideChoice.Field.FramePath);
        lastG3Field = guideChoice.Field;

        var activeCalibrationBeforeGuide = await phd2.ValidateCalibrationAsync(
            preset.CalibrationQualityPolicy.ApplyHardRejectionCeilings(PhdCalibrationRequirement()),
            cancellationToken).ConfigureAwait(false);
        if (activeCalibrationBeforeGuide.Status != Phd2ValidationStatus.Valid)
        {
            return Attention(
                ObservationStage.PlaceTargetOnSlit,
                "PHD2_LOCK_RECOVERY_PRE_GUIDE_CALIBRATION_INVALID",
                $"Durable recovery will not start a guide/recalibration command from an invalid active calibration: {string.Join(" ", activeCalibrationBeforeGuide.Failures.Concat(activeCalibrationBeforeGuide.IndeterminateReasons))}");
        }
        var recoveryPreGuide = SelectPhd2CalibrationQuality(
            activeCalibrationBeforeGuide,
            preset,
            Phd2CalibrationEvaluationPhase.PreGuide,
            settle: null,
            residual: null,
            Phd2CalibrationSelectionPurpose.ValidationGuide);
        if (recoveryPreGuide.Selected?.CanAttemptValidationGuide != true)
        {
            return Attention(
                ObservationStage.PlaceTargetOnSlit,
                "PHD2_LOCK_RECOVERY_PRE_GUIDE_REJECTED",
                CalibrationSelectionMessage(recoveryPreGuide));
        }
        if (RequiresSupervisedPhd2Science(recoveryPreGuide.Selected, guideChoice.Mode) &&
            !configuration.AllowDegradedSupervisedScience)
        {
            return Attention(
                ObservationStage.PlaceTargetOnSlit,
                "PHD2_LOCK_RECOVERY_SUPERVISION_OPT_IN_REQUIRED",
                "The fresh recovery guide mode/calibration is supervised-only. No select-guide, guide, recalibration or lock command was sent because this run lacks explicit supervised-science opt-in.");
        }

        await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
        var loop = await phd2.StartLoopingAndWaitForFreshFrameAsync(
            new Phd2LoopingStartRequest(TimeSpan.FromSeconds(preset.FreshLoopFrameTimeoutSeconds)),
            cancellationToken).ConfigureAwait(false);
        if (!loop.LeavesLoopingForGuideTakeover || loop.StopCommandSent || loop.ExposureChanged)
            return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RECOVERY_LOOP_CONTRACT_FAILED", "Fresh full-frame loop did not preserve the commissioned guide-takeover contract.");
        var preSelectBinding = await ValidateG3FieldMountBindingForMotionAsync(
            context,
            lastG3Field,
            cancellationToken).ConfigureAwait(false);
        if (preSelectBinding.Disposition != GateDisposition.Passed)
        {
            await StopPhdAndWaitAsync(cancellationToken).ConfigureAwait(false);
            return new StageResult(preSelectBinding, lastG3Field.FramePath);
        }
        var guideSelectionResult = await SelectFreshPhd2GuideAsync(
            guideChoice,
            preset,
            cancellationToken).ConfigureAwait(false);
        var selectedGuide = guideSelectionResult.Selected;
        var guideSelectionRoi = BuildPhd2GuideSelectionRoi(
            selectedGuide,
            preset.SensorWidthPixels,
            preset.SensorHeightPixels);
        await PublishPhd2GuideSelectionEvidenceAsync(
            context,
            lastG3Field,
            guideSelectionResult.Selection,
            guideSelectionResult.Requested,
            selectedGuide,
            preset,
            guideChoice.Mode,
            guideChoice.Capture,
            loop,
            guideSelectionRoi,
            cancellationToken).ConfigureAwait(false);
        await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
        var preGuideBinding = await ValidateG3FieldMountBindingForMotionAsync(
            context,
            lastG3Field,
            cancellationToken).ConfigureAwait(false);
        if (preGuideBinding.Disposition != GateDisposition.Passed)
        {
            await StopPhdAndWaitAsync(cancellationToken).ConfigureAwait(false);
            return new StageResult(preGuideBinding, lastG3Field.FramePath);
        }
        Volatile.Write(ref phd2GuidingEverStarted, 1);
        var settle = await phd2.GuideAndSettleAsync(
            Phd2SettleCriteriaFromConfiguration(),
            false,
            guideSelectionRoi,
            cancellationToken).ConfigureAwait(false);
        var snapshot = phd2.Snapshot;
        if (!settle.Succeeded || !snapshot.HasCurrentSuccessfulSettle)
            return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RECOVERY_SETTLE_FAILED", settle.Error ?? "A fresh locally issued guide/settle epoch was not attested.");

        var calibration = await phd2.ValidateCalibrationAsync(
            preset.CalibrationQualityPolicy.ApplyHardRejectionCeilings(PhdCalibrationRequirement()),
            cancellationToken).ConfigureAwait(false);
        if (calibration.Status != Phd2ValidationStatus.Valid)
            return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RECOVERY_CALIBRATION_INVALID", string.Join(" ", calibration.Failures.Concat(calibration.IndeterminateReasons)));
        var freshLock = await phd2.GetLockPositionAsync(cancellationToken).ConfigureAwait(false);
        if (freshLock is null)
            return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RECOVERY_POSITION_UNKNOWN", "The new guide epoch has no fresh runtime lock readback.");
        var freshMeasurements = await CapturePhd2GuidingMeasurementsAsync(
            context,
            preset,
            topology,
            freshLock,
            ToPhd2Domain(lastG3Field.TargetIdentification.Target!.Centroid, preset),
            lastG3Field.SlitDetection.Geometry,
            guideChoice.Mode,
            preset.CalibrationQualityPolicy.RequiredFreshResidualsPerLockShiftStage,
            cancellationToken).ConfigureAwait(false);
        var initial = freshMeasurements[^1];
        var qualitySelection = SelectPhd2CalibrationQuality(
            calibration,
            preset,
            Phd2CalibrationEvaluationPhase.PostSettle,
            CreateCalibrationSettleEvidence(settle, snapshot),
            CreateCalibrationResidualEvidence(initial, PointDistance(initial.Measurement.GuideStar, freshLock), preset, topology, guideChoice.Mode),
            Phd2CalibrationSelectionPurpose.LockShift);
        if (qualitySelection.Selected?.IsLockShiftAuthority != true)
            return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RECOVERY_ACTIVE_CALIBRATION_REJECTED", CalibrationSelectionMessage(qualitySelection));
        if (RequiresSupervisedPhd2Science(qualitySelection.Selected, guideChoice.Mode) &&
            !configuration.AllowDegradedSupervisedScience)
        {
            await StopPhdAndWaitAsync(cancellationToken).ConfigureAwait(false);
            return Attention(
                ObservationStage.PlaceTargetOnSlit,
                "PHD2_LOCK_RECOVERY_POST_GUIDE_SUPERVISION_REQUIRED",
                "The post-settle recovery authority is supervised-only. Guiding was stopped before any exact-lock return command.");
        }
        var qualification = BuildPhd2LockShiftQualification(
            identity,
            calibration,
            topology,
            preset,
            qualitySelection.Selected,
            pierSide);
        if (!qualification.IsQualified)
            return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RECOVERY_QUALIFICATION_FAILED", string.Join(" ", qualification.Failures));

        var storedOriginLock = new Phd2Point(state.OriginLockX, state.OriginLockY);
        var storedCurrentLock = new Phd2Point(state.CurrentLockX, state.CurrentLockY);
        var storedRequestedLock = new Phd2Point(state.RequestedLockX, state.RequestedLockY);
        var storedOriginTarget = new Phd2Point(state.OriginTargetX, state.OriginTargetY);
        var storedOriginSlit = new Phd2Point(state.OriginSlitX, state.OriginSlitY);
        var observedTarget = initial.Measurement.TargetCentroid;
        var observedSlit = initial.Measurement.RecognizedSlitAcquisitionPoint;
        var proofTolerance = Math.Max(preset.LockVerificationTolerancePixels, preset.MaximumResidualGrowthPixels);
        if (PointDistance(observedSlit, storedOriginSlit) > proofTolerance)
            return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RECOVERY_SLIT_STATE_CHANGED", "Fresh runtime slit position does not reproduce the durable pre-motion relative state.");
        var currentOffset = SubtractPoint(storedCurrentLock, storedOriginLock);
        var requestedOffset = SubtractPoint(storedRequestedLock, storedOriginLock);
        var currentFit = PointDistance(observedTarget, AddPoint(storedOriginTarget, currentOffset));
        var requestedFit = PointDistance(observedTarget, AddPoint(storedOriginTarget, requestedOffset));
        var currentMatches = currentFit <= proofTolerance;
        var requestedMatches = requestedFit <= proofTolerance;
        if (!currentMatches && !requestedMatches)
            return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RECOVERY_ENDPOINT_UNPROVEN", "Fresh target/slit evidence matches neither the durable verified endpoint nor the last precharged endpoint; manual reconciliation is required.");
        if (currentMatches && requestedMatches && PointDistance(storedCurrentLock, storedRequestedLock) > 2 * proofTolerance)
            return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RECOVERY_ENDPOINT_AMBIGUOUS", "Fresh target evidence cannot uniquely distinguish the two durable crash-window endpoints.");
        var provenOldEndpoint = requestedMatches && (!currentMatches || requestedFit < currentFit)
            ? storedRequestedLock
            : storedCurrentLock;
        var returnDelta = SubtractPoint(storedOriginLock, provenOldEndpoint);
        var translatedOrigin = AddPoint(freshLock, returnDelta);
        var initialTarget = observedTarget;
        var runtimeSlit = initial.RuntimeSlitLocal;
        var requiredFreshResiduals = qualitySelection.Selected.RequiredFreshResidualsPerLockShiftStage;

        state = state with
        {
            ConnectionEpoch = snapshot.ConnectionEpoch,
            GuideEpoch = snapshot.GuideEpoch,
            OriginLockX = translatedOrigin.X,
            OriginLockY = translatedOrigin.Y,
            CurrentLockX = freshLock.X,
            CurrentLockY = freshLock.Y,
            RequestedLockX = freshLock.X,
            RequestedLockY = freshLock.Y,
            GuideMode = guideChoice.Mode,
            LastAcceptedFrameSha256 = initial.Measurement.FrameSha256,
            LastFramePath = initial.Frame.Path,
            Phase = Phd2LockShiftPendingPhase.ReturnRequired,
            UpdatedUtc = DateTimeOffset.UtcNow,
            LastReason = "A later explicit Execute/Resume established a fresh commissioned guide epoch and translated the proven old lock-return vector without resetting lineage budget.",
        };
        await Phd2LockShiftPendingStore.WriteAtomicAsync(item.Path, state, cancellationToken).ConfigureAwait(false);
        pendingPhd2LockShift = state;
        return await ReturnPhd2LockToOriginCoreAsync(
            context,
            preset,
            topology,
            qualification,
            state,
            item.Path,
            $"Explicit Execute/Resume is recovering durable PHD2 lineage {state.LineageId} through a fresh guide epoch.",
            $"durable-return:{state.LineageId}",
            cancellationToken,
            async (verifiedLock, token) =>
            {
                var finalMeasurements = await CapturePhd2GuidingMeasurementsAsync(
                    context,
                    preset,
                    topology,
                    verifiedLock,
                    AddPoint(initialTarget, returnDelta),
                    runtimeSlit,
                    guideChoice.Mode,
                    requiredFreshResiduals,
                    token).ConfigureAwait(false);
                var final = finalMeasurements[^1];
                var measuredDelta = SubtractPoint(final.Measurement.TargetCentroid, initialTarget);
                var deltaError = PointDistance(measuredDelta, returnDelta);
                var originTargetError = PointDistance(final.Measurement.TargetCentroid, storedOriginTarget);
                var originSlitError = PointDistance(final.Measurement.RecognizedSlitAcquisitionPoint, storedOriginSlit);
                await PublishRunJsonEvidenceAsync(
                    "phd2-lock-shift-restart-return-verification",
                    "Fresh target/slit verification after translated cross-process exact-lock return",
                    new
                    {
                        state.LineageId,
                        oldConnectionEpoch = item.State!.ConnectionEpoch,
                        oldGuideEpoch = item.State.GuideEpoch,
                        newConnectionEpoch = snapshot.ConnectionEpoch,
                        newGuideEpoch = snapshot.GuideEpoch,
                        oldEpochNumbersUsedAsContinuityProof = false,
                        returnDelta,
                        measuredDelta,
                        deltaError,
                        originTargetError,
                        originSlitError,
                        proofTolerance,
                        final.Measurement,
                    },
                    final.Frame.Path,
                    token).ConfigureAwait(false);
                lastG3Field = UpdateG3FieldFromGuidingResidual(lastG3Field!, final, preset);
                return deltaError <= proofTolerance && originTargetError <= proofTolerance && originSlitError <= proofTolerance
                    ? GateResult.Pass("PHD2_LOCK_RESTART_RETURN_FRESHLY_VERIFIED", "Fresh target displacement and target/slit relative state reproduce the durable pre-motion state after translated exact-lock return.")
                    : GateResult.Unknown("PHD2_LOCK_RESTART_RETURN_RESIDUAL_MISMATCH", $"Translated return verification failed: delta {deltaError:F3}px, target {originTargetError:F3}px, slit {originSlitError:F3}px (limit {proofTolerance:F3}px).");
            }).ConfigureAwait(false);
    }

    private async Task<StageResult> PlaceTargetOnSlitWithPhd2Async(
        ObservationContext context,
        CancellationToken cancellationToken)
    {
        var loaded = commissioning
            ?? throw new InvalidOperationException("Commissioning preset is not loaded.");
        var preset = loaded.Value.Phd2SlitPlacement;
        if (preset is null)
        {
            return Attention(
                ObservationStage.PlaceTargetOnSlit,
                "PHD2_SLIT_COMMISSIONING_REQUIRED",
                "The schema-4 preset selected PHD2 lock-shift authority but omitted its topology, quality policy, exposure and bounded-motion commissioning values.");
        }
        var presetIssues = preset.Validate();
        if (presetIssues.Count > 0)
        {
            return Attention(
                ObservationStage.PlaceTargetOnSlit,
                "PHD2_SLIT_COMMISSIONING_INVALID",
                string.Join(" ", presetIssues));
        }
        if (lastG3Field?.TargetIdentification.Target is not { } initialTarget)
        {
            return Attention(
                ObservationStage.PlaceTargetOnSlit,
                "PHD2_SLIT_TARGET_REQUIRED",
                "PHD2 lock-shift placement requires a current catalog-bound G3 target centroid.");
        }

        var pendingPath = Phd2LockShiftPendingPath(context.Plan.ObservationRunId);
        var discovered = await Phd2LockShiftPendingStore.DiscoverAsync(
            SlitPlacementObservationsRoot(),
            cancellationToken).ConfigureAwait(false);
        var unreadable = discovered.Where(item => item.Error is not null).ToArray();
        if (unreadable.Length > 0)
        {
            return Attention(
                ObservationStage.PlaceTargetOnSlit,
                "PHD2_LOCK_LEDGER_DISCOVERY_UNTRUSTED",
                $"{unreadable.Length} discovered PHD2 lock ledger(s) could not be validated; no new runtime lock command is allowed.");
        }
        foreach (var item in discovered)
        {
            var state = item.State!;
            var canonical = Phd2LockShiftPendingPath(state.ObservationRunId);
            if (!string.Equals(Path.GetFullPath(item.Path), Path.GetFullPath(canonical), StringComparison.OrdinalIgnoreCase))
            {
                return Attention(
                    ObservationStage.PlaceTargetOnSlit,
                    "PHD2_LOCK_LEDGER_PATH_IDENTITY_MISMATCH",
                    $"Durable PHD2 ledger '{item.Path}' is not its run-bound canonical path '{canonical}'. No lock command is allowed.");
            }
            var manifest = await ValidatePhd2LockManifestAsync(item, cancellationToken).ConfigureAwait(false);
            if (manifest.Error is not null) return new StageResult(manifest.Error, item.Path);
            if (!string.Equals(state.ObservationRunId, context.Plan.ObservationRunId, StringComparison.Ordinal))
            {
                if (state.Phase == Phd2LockShiftPendingPhase.SettledBudgetLedger)
                {
                    // Historical accepted endpoints are neither return debt nor
                    // authority for this observation.
                    continue;
                }
                if (!manifest.RunIsTerminal)
                {
                    return Attention(
                        ObservationStage.PlaceTargetOnSlit,
                        "PHD2_LOCK_FOREIGN_OUTSTANDING_NOT_RECOVERED",
                        $"Observation run '{state.ObservationRunId}' still has outstanding PHD2 lock lineage {state.LineageId}. The pre-placement recovery pass did not settle it, so no new lock budget may be created.");
                }
                if (state.Phase != Phd2LockShiftPendingPhase.SettledBudgetLedger)
                {
                    return Attention(
                        ObservationStage.PlaceTargetOnSlit,
                        "PHD2_LOCK_TERMINAL_RUN_OUTSTANDING",
                        $"Terminal run '{state.ObservationRunId}' still contains non-settled PHD2 lock lineage {state.LineageId}; automatic handoff is prohibited.");
                }
            }
        }
        var currentCopies = discovered.Where(item => string.Equals(
            item.State!.ObservationRunId,
            context.Plan.ObservationRunId,
            StringComparison.Ordinal)).ToArray();
        if (currentCopies.Length > 1)
        {
            return Attention(
                ObservationStage.PlaceTargetOnSlit,
                "PHD2_LOCK_CURRENT_RUN_LINEAGE_FORK",
                $"Current run has {currentCopies.Length} durable PHD2 ledger copies. Budget lineage selection is ambiguous and no lock command is allowed.");
        }
        if (pendingPhd2LockShift is not null && currentCopies.All(item =>
            !string.Equals(item.State!.LineageId, pendingPhd2LockShift.LineageId, StringComparison.Ordinal)))
        {
            return Attention(
                ObservationStage.PlaceTargetOnSlit,
                "PHD2_LOCK_LEDGER_DURABILITY_LOST",
                "The in-memory PHD2 lock lineage has no matching canonical durable file. No new lock command is allowed.");
        }
        Phd2LockShiftPendingState? inheritedSettledBudget = null;
        var currentLedger = currentCopies.SingleOrDefault()?.State;
        if (currentLedger is not null)
        {
            var bindingGate = ValidateCurrentPhd2LockLedgerBinding(context, preset, currentLedger);
            if (bindingGate.Disposition != GateDisposition.Passed) return new StageResult(bindingGate, pendingPath);
            if (currentLedger.Phase == Phd2LockShiftPendingPhase.SettledBudgetLedger)
            {
                if (phd2SlitPlacementSession is { } settledSession &&
                    settledSession.ConnectionEpoch == currentLedger.ConnectionEpoch &&
                    settledSession.GuideEpoch == currentLedger.GuideEpoch &&
                    phd2.Snapshot.HasCurrentSuccessfulSettle)
                {
                    var residual = PointDistance(settledSession.LastMeasurement.Measurement.TargetCentroid, settledSession.LastMeasurement.Measurement.RecognizedSlitAcquisitionPoint);
                    return Passed(
                        "PHD2_LOCK_LEDGER_ALREADY_SETTLED",
                        $"The current run already completed PHD2 lock lineage {currentLedger.LineageId}; consumed attempts/pixels remain {currentLedger.AttemptsUsed}/{currentLedger.CumulativeCommandedPixels:F3} and were not reset.",
                        Phd2EffectiveQualityMetrics(settledSession.Quality, settledSession.GuideMode, settledSession.SelectedGuide, settledSession.Settle, residual),
                        Metadata(loaded));
                }
                var elapsedSeconds = Math.Max(0, (DateTimeOffset.UtcNow - currentLedger.StartedUtc).TotalSeconds);
                if (currentLedger.AttemptsUsed >= currentLedger.MaximumAttempts ||
                    currentLedger.CumulativeCommandedPixels >= currentLedger.MaximumCumulativePixels - 1e-9 ||
                    elapsedSeconds >= currentLedger.MaximumElapsedSeconds)
                {
                    return Attention(
                        ObservationStage.PlaceTargetOnSlit,
                        "PHD2_LOCK_INHERITED_BUDGET_EXHAUSTED",
                        $"Settled PHD2 lineage {currentLedger.LineageId} has no remaining inherited attempt, pixel or elapsed-time budget. A new full budget is prohibited.");
                }
                inheritedSettledBudget = currentLedger;
            }
            else
            {
                pendingPhd2LockShift = currentLedger;
                if (phd2SlitPlacementSession is not { } recoverable ||
                    recoverable.ConnectionEpoch != currentLedger.ConnectionEpoch ||
                    recoverable.GuideEpoch != currentLedger.GuideEpoch)
                    return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_LEDGER_RECONCILIATION_REQUIRED", "An outstanding runtime-lock intent exists, but this process cannot prove the same PHD2 connection/guide epoch. No new lock command is allowed.");
                return await ReturnPhd2LockToOriginAsync(
                    context,
                    recoverable,
                    currentLedger,
                    "Resuming an outstanding durable PHD2 lock-shift intent before any new placement.",
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await EnsurePhdConnectedAsync(cancellationToken).ConfigureAwait(false);
        var identity = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), cancellationToken).ConfigureAwait(false);
        if (!identity.IsValid)
        {
            return Attention(
                ObservationStage.PlaceTargetOnSlit,
                "PHD2_LOCK_IDENTITY_INVALID",
                string.Join(" ", identity.Failures.Concat(identity.IndeterminateReasons)));
        }
        var profileGate = ValidatePhdProfileBindingEvidence();
        if (profileGate.Disposition != GateDisposition.Passed) return new StageResult(profileGate);

        var pierSide = telescopeMediator.GetInfo().SideOfPier.ToString();
        if (!IsKnownPierSide(pierSide) || !string.Equals(pierSide, preset.PierSide, StringComparison.Ordinal))
        {
            return Attention(
                ObservationStage.PlaceTargetOnSlit,
                "PHD2_LOCK_PIER_MISMATCH",
                $"Current pier side '{pierSide}' does not exactly match commissioned PHD2 topology side '{preset.PierSide}'.");
        }
        var topology = BuildPhd2SensorTopology(preset, pierSide);
        if (!string.Equals(topology.ComputeFingerprintSha256(), preset.LockedTopologyFingerprintSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Attention(
                ObservationStage.PlaceTargetOnSlit,
                "PHD2_LOCK_TOPOLOGY_FINGERPRINT_MISMATCH",
                "The current profile/camera/ROI/binning/rotation/install-epoch/mount/pier topology does not match the commissioned PHD2 fingerprint.");
        }

        var policy = preset.CalibrationQualityPolicy;
        if (preset.GuideMode == Phd2SlitGuideMode.DegradedDirectTargetGuiding &&
            !configuration.AllowDegradedSupervisedScience)
        {
            return Attention(
                ObservationStage.PlaceTargetOnSlit,
                "PHD2_DIRECT_TARGET_SUPERVISION_OPT_IN_REQUIRED",
                "The commissioned guide mode is degraded direct-target guiding. Explicit supervised-science opt-in is required before any guide or lock command.");
        }
        var hardRequirement = policy.ApplyHardRejectionCeilings(PhdCalibrationRequirement());
        var calibrationBefore = await phd2.ValidateCalibrationAsync(hardRequirement, cancellationToken).ConfigureAwait(false);
        var forceRecalibration = calibrationBefore.Status != Phd2ValidationStatus.Valid;
        if (!forceRecalibration)
        {
            var preGuide = SelectPhd2CalibrationQuality(
                calibrationBefore,
                preset,
                Phd2CalibrationEvaluationPhase.PreGuide,
                settle: null,
                residual: null,
                Phd2CalibrationSelectionPurpose.ValidationGuide);
            if (preGuide.Selected?.CanAttemptValidationGuide != true)
            {
                return Attention(
                    ObservationStage.PlaceTargetOnSlit,
                    "PHD2_CALIBRATION_PRE_GUIDE_REJECTED",
                    CalibrationSelectionMessage(preGuide));
            }
            if (preGuide.Selected.RequiresOperatorSupervision &&
                !configuration.AllowDegradedSupervisedScience)
            {
                return Attention(
                    ObservationStage.PlaceTargetOnSlit,
                    "PHD2_DEGRADED_SUPERVISION_OPT_IN_REQUIRED",
                    $"Calibration grade {preGuide.Selected.Grade} is usable only under supervision. Enable this run's explicit degraded-supervised opt-in before guide/calibration or lock movement; no exposure, guide, or lock command was sent.",
                    Phd2QualityMetrics(preGuide.Selected, new Phd2Point(0, 0), new Phd2SettleResult(false, null, 0, 0, DateTimeOffset.MinValue), double.NaN));
            }
        }

        var guideChoice = await AcquireFreshPhd2PlacementGuideAsync(
            context,
            lastG3Field,
            preset,
            cancellationToken).ConfigureAwait(false);
        var guideSelection = guideChoice.Selection;
        if (guideSelection.Gate.Disposition != GateDisposition.Passed ||
            (guideChoice.Mode == Phd2SlitGuideMode.DegradedDirectTargetGuiding && guideSelection.Star is null))
            return new StageResult(guideSelection.Gate, guideChoice.Field.FramePath);
        if (guideChoice.Mode == Phd2SlitGuideMode.DegradedDirectTargetGuiding &&
            !configuration.AllowDegradedSupervisedScience)
        {
            return Attention(
                ObservationStage.PlaceTargetOnSlit,
                "PHD2_DIRECT_TARGET_SUPERVISION_OPT_IN_REQUIRED",
                "Fresh auto selection fell back to degraded direct-target guiding. This run has no explicit supervised-science opt-in, so no guide or lock command was sent.");
        }
        lastG3Field = guideChoice.Field;
        initialTarget = guideChoice.Field.TargetIdentification.Target
            ?? throw new InvalidOperationException("Fresh guide-selection frame passed without a target identity.");
        try
        {
            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            var loop = await phd2.StartLoopingAndWaitForFreshFrameAsync(
                new Phd2LoopingStartRequest(TimeSpan.FromSeconds(preset.FreshLoopFrameTimeoutSeconds)),
                cancellationToken).ConfigureAwait(false);
            if (!loop.LeavesLoopingForGuideTakeover || loop.StopCommandSent || loop.ExposureChanged)
                throw new InvalidOperationException("PHD2 full-frame selection loop did not preserve the commissioned takeover contract.");

            var preSelectBinding = await ValidateG3FieldMountBindingForMotionAsync(
                context,
                lastG3Field,
                cancellationToken).ConfigureAwait(false);
            if (preSelectBinding.Disposition != GateDisposition.Passed)
            {
                await StopPhdAndWaitAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"{preSelectBinding.Code}: {preSelectBinding.Message}");
            }
            var guideSelectionResult = await SelectFreshPhd2GuideAsync(
                guideChoice,
                preset,
                cancellationToken).ConfigureAwait(false);
            guideSelection = guideSelectionResult.Selection;
            var selectedGuide = guideSelectionResult.Selected;
            var guideSelectionRoi = BuildPhd2GuideSelectionRoi(
                selectedGuide,
                preset.SensorWidthPixels,
                preset.SensorHeightPixels);
            await PublishPhd2GuideSelectionEvidenceAsync(
                context,
                lastG3Field,
                guideSelection,
                guideSelectionResult.Requested,
                selectedGuide,
                preset,
                guideChoice.Mode,
                guideChoice.Capture,
                loop,
                guideSelectionRoi,
                cancellationToken).ConfigureAwait(false);

            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            var preGuideBinding = await ValidateG3FieldMountBindingForMotionAsync(
                context,
                lastG3Field,
                cancellationToken).ConfigureAwait(false);
            if (preGuideBinding.Disposition != GateDisposition.Passed)
            {
                await StopPhdAndWaitAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"{preGuideBinding.Code}: {preGuideBinding.Message}");
            }
            Volatile.Write(ref phd2GuidingEverStarted, 1);
            var settle = await phd2.GuideAndSettleAsync(
                Phd2SettleCriteriaFromConfiguration(),
                forceRecalibration,
                guideSelectionRoi,
                cancellationToken).ConfigureAwait(false);
            if (!settle.Succeeded)
                throw new InvalidOperationException(settle.Error ?? "PHD2 guide/settle failed.");
            var guideProof = phd2.Snapshot;
            if (!guideProof.HasCurrentSuccessfulSettle)
                throw new InvalidOperationException("The locally issued guide operation did not leave a same-epoch successful settle attestation.");

            var calibration = await phd2.ValidateCalibrationAsync(
                policy.ApplyHardRejectionCeilings(PhdCalibrationRequirement(
                    forceRecalibration ? DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1) : null)),
                cancellationToken).ConfigureAwait(false);
            if (calibration.Status != Phd2ValidationStatus.Valid)
            {
                throw new InvalidOperationException(
                    $"PHD2 calibration failed the policy hard rejection ceilings: {string.Join(" ", calibration.Failures.Concat(calibration.IndeterminateReasons))}");
            }

            var lockOrigin = await phd2.GetLockPositionAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("PHD2 did not report a runtime lock position after settle.");
            var initialTargetDomain = ToPhd2Domain(initialTarget.Centroid, preset);
            var initialSlitLocal = lastG3Field.SlitDetection.Geometry;
            var expectedTarget = initialTargetDomain;
            var firstMeasurements = await CapturePhd2GuidingMeasurementsAsync(
                context,
                preset,
                topology,
                lockOrigin,
                expectedTarget,
                initialSlitLocal,
                guideChoice.Mode,
                policy.RequiredFreshResidualsPerLockShiftStage,
                cancellationToken).ConfigureAwait(false);
            var first = firstMeasurements[^1];
            var firstGuideResidual = PointDistance(first.Measurement.GuideStar, lockOrigin);
            var residualEvidence = CreateCalibrationResidualEvidence(first, firstGuideResidual, preset, topology, guideChoice.Mode);
            var settleEvidence = CreateCalibrationSettleEvidence(settle, guideProof);
            var postGuide = SelectPhd2CalibrationQuality(
                calibration,
                preset,
                Phd2CalibrationEvaluationPhase.PostSettle,
                settleEvidence,
                residualEvidence,
                Phd2CalibrationSelectionPurpose.LockShift);
            var quality = postGuide.Selected;
            if (quality?.IsLockShiftAuthority != true)
            {
                throw new InvalidOperationException(
                    $"Post-settle calibration quality does not authorize a lock shift: {CalibrationSelectionMessage(postGuide)}");
            }
            if (RequiresSupervisedPhd2Science(quality, guideChoice.Mode) && !configuration.AllowDegradedSupervisedScience)
            {
                await StopPhdAndWaitAsync(cancellationToken).ConfigureAwait(false);
                return Attention(
                    ObservationStage.PlaceTargetOnSlit,
                    "PHD2_DEGRADED_SUPERVISION_OPT_IN_REQUIRED",
                    $"Post-settle calibration grade {quality.Grade} is supervised-only. Guiding was stopped before any exact-lock motion; explicitly opt in for this run to permit scaled degraded placement.",
                    Phd2EffectiveQualityMetrics(quality, guideChoice.Mode, selectedGuide, settle, PointDistance(first.Measurement.TargetCentroid, first.Measurement.RecognizedSlitAcquisitionPoint)));
            }

            var qualification = BuildPhd2LockShiftQualification(
                identity,
                calibration,
                topology,
                preset,
                quality,
                pierSide);
            if (!qualification.IsQualified)
                throw new InvalidOperationException(string.Join(" ", qualification.Failures));

            var startedUtc = inheritedSettledBudget?.StartedUtc ?? DateTimeOffset.UtcNow;
            var ledger = new Phd2LockShiftLedger(
                inheritedSettledBudget?.LineageId ?? Guid.NewGuid().ToString("N"),
                lockOrigin,
                lockOrigin,
                inheritedSettledBudget?.AttemptsUsed ?? 0,
                inheritedSettledBudget?.CumulativeCommandedPixels ?? 0,
                startedUtc,
                first.Measurement.FrameSha256);
            var session = new Phd2SlitPlacementSession(
                guideChoice.Mode,
                topology,
                qualification,
                quality,
                calibration,
                selectedGuide,
                lockOrigin,
                first.Measurement.TargetCentroid,
                first.RuntimeSlitLocal,
                first,
                settle,
                guideProof.ConnectionEpoch,
                guideProof.GuideEpoch,
                forceRecalibration);
            phd2SlitPlacementSession = session;

            var priorResidual = PointDistance(first.Measurement.TargetCentroid, first.Measurement.RecognizedSlitAcquisitionPoint);
            while (true)
            {
                var safety = BuildPhd2LockShiftSafetySnapshot(context, preset, topology.PierSide);
                var plan = Phd2SlitLockShiftPlanner.PlanOutboundStage(
                    session.Qualification,
                    guideChoice.Mode,
                    session.LastMeasurement.Measurement,
                    ledger,
                    safety,
                    topology,
                    preset.BuildMotionLimits(),
                    DateTimeOffset.UtcNow);
                if (!plan.IsAllowed)
                    throw new InvalidOperationException($"{plan.Code}: {plan.Message}");
                if (plan.IsComplete)
                {
                    var settled = CreatePhd2PendingState(
                        context,
                        preset,
                        session,
                        ledger,
                        ledger.CurrentLockPosition,
                        Phd2LockShiftPendingPhase.SettledBudgetLedger,
                        intentEvidencePath: null,
                        "Target-on-slit completion was proven by fresh guiding-frame residual evidence.");
                    await Phd2LockShiftPendingStore.WriteAtomicAsync(pendingPath, settled, cancellationToken).ConfigureAwait(false);
                    pendingPhd2LockShift = null;
                    phd2SlitPlacementSession = session;
                    lastG3Field = UpdateG3FieldFromGuidingResidual(lastG3Field, session.LastMeasurement, preset);
                    return Passed(
                        "PHD2_TARGET_AT_SLIT_MIDPOINT",
                        $"PHD2 graded calibration placed the target at the runtime-recognized slit midpoint with residual {priorResidual:F2}px; guiding remains settled for StartGuiding.",
                        Phd2EffectiveQualityMetrics(session.Quality, session.GuideMode, session.SelectedGuide, session.Settle, priorResidual),
                        Metadata(loaded));
                }

                var stage = plan.Stage!;
                var preIntentFieldBinding = await ValidateG3FieldMountBindingForMotionAsync(
                    context,
                    lastG3Field,
                    cancellationToken).ConfigureAwait(false);
                if (preIntentFieldBinding.Disposition != GateDisposition.Passed)
                {
                    lastG3Field = null;
                    if (pendingPhd2LockShift is { } outstanding)
                    {
                        return await ReturnPhd2LockToOriginAsync(
                            context,
                            session,
                            outstanding with { Phase = Phd2LockShiftPendingPhase.ReturnRequired, LastReason = preIntentFieldBinding.Message },
                            $"G3 field mount binding failed before PHD2 lock intent: {preIntentFieldBinding.Code}: {preIntentFieldBinding.Message}",
                            cancellationToken).ConfigureAwait(false);
                    }
                    try { await StopPhdAndWaitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                    phd2SlitPlacementSession = null;
                    return new StageResult(preIntentFieldBinding, stage.SourceFrameSha256);
                }
                await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
                var intentPath = await PublishRunJsonEvidenceAsync(
                    "phd2-lock-shift-stage-intent",
                    $"PHD2 exact runtime-lock stage {ledger.AttemptsUsed + 1}",
                    new
                    {
                        authority = stage.Authority.ToString(),
                        guideMode = stage.GuideMode.ToString(),
                        formula = "desiredGuideLock = guide + (recognizedSlitAcquisitionPoint - targetCentroid)",
                        expectedCurrent = stage.ExpectedCurrentLockPosition,
                        requested = stage.RequestedLockPosition,
                        fullDesired = stage.FullDesiredLockPosition,
                        targetToSlitDelta = stage.TargetToSlitDelta,
                        stage.StagePixels,
                        stage.ReservedRecoveryMotionPixels,
                        stage.ReservedRecoveryAttempts,
                        stage.CalibrationQualityPolicyId,
                        calibrationQualityGrade = stage.CalibrationQualityGrade.ToString(),
                        stage.RequiresOperatorSupervision,
                        stage.IsUnattendedScienceAuthority,
                        stage.AppliedLockShiftScale,
                        stage.AppliedResidualToleranceScale,
                        stage.RequiredFreshResiduals,
                        sourceFrameSha256 = stage.SourceFrameSha256,
                        topologyFingerprintSha256 = stage.TopologyFingerprintSha256,
                        registryProfileMutationAllowed = false,
                        automaticRetryAllowed = false,
                    },
                    session.LastMeasurement.Frame.Path,
                    cancellationToken).ConfigureAwait(false);
                var chargedLedger = ledger with
                {
                    AttemptsUsed = ledger.AttemptsUsed + 1,
                    CumulativeCommandedPixels = ledger.CumulativeCommandedPixels + stage.StagePixels,
                };
                var pending = CreatePhd2PendingState(
                    context,
                    preset,
                    session,
                    chargedLedger,
                    stage.RequestedLockPosition,
                    Phd2LockShiftPendingPhase.StageIntent,
                    intentPath,
                    "Exact lock-position request has not yet produced operation-bound settle and fresh G3 residual evidence.");
                await Phd2LockShiftPendingStore.WriteAtomicAsync(pendingPath, pending, cancellationToken).ConfigureAwait(false);
                pendingPhd2LockShift = pending;

                Phd2ExactLockPositionResult exact;
                try
                {
                    await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
                    var preDispatchFieldBinding = await ValidateG3FieldMountBindingForMotionAsync(
                        context,
                        lastG3Field,
                        cancellationToken).ConfigureAwait(false);
                    if (preDispatchFieldBinding.Disposition != GateDisposition.Passed)
                    {
                        lastG3Field = null;
                        return await ReturnPhd2LockToOriginAsync(
                            context,
                            session,
                            pending with { Phase = Phd2LockShiftPendingPhase.ReturnRequired, LastReason = preDispatchFieldBinding.Message },
                            $"G3 field mount binding failed after durable PHD2 intent and before dispatch: {preDispatchFieldBinding.Code}: {preDispatchFieldBinding.Message}",
                            cancellationToken).ConfigureAwait(false);
                    }
                    exact = await phd2.SetExactLockPositionAsync(
                        new Phd2ExactLockPositionRequest(
                            stage.ExpectedCurrentLockPosition,
                            stage.RequestedLockPosition,
                            preset.LockPreconditionTolerancePixels,
                            stage.StagePixels + 1e-9,
                            preset.LockVerificationTolerancePixels),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    pending = pending with
                    {
                        Phase = Phd2LockShiftPendingPhase.ReturnRequired,
                        UpdatedUtc = DateTimeOffset.UtcNow,
                        LastReason = "User cancellation occurred after the durable stage intent. No automatic lock-return command was sent.",
                    };
                    await Phd2LockShiftPendingStore.WriteAtomicAsync(pendingPath, pending, CancellationToken.None).ConfigureAwait(false);
                    pendingPhd2LockShift = pending;
                    throw;
                }
                catch (Exception ex)
                {
                    return await ReturnPhd2LockToOriginAsync(
                        context,
                        session,
                        pending with { Phase = Phd2LockShiftPendingPhase.ReturnRequired, LastReason = ex.Message },
                        $"Outbound exact-lock stage failed or became ambiguous: {ex.Message}",
                        cancellationToken).ConfigureAwait(false);
                }
                ledger = chargedLedger with { CurrentLockPosition = exact.Verified };
                pending = pending with
                {
                    CurrentLockX = exact.Verified.X,
                    CurrentLockY = exact.Verified.Y,
                    Phase = Phd2LockShiftPendingPhase.AwaitingOperationBoundSettle,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    LastReason = $"Exact runtime lock readback passed with {exact.VerificationErrorPixels:F3}px error; physical settle is still required.",
                };
                await Phd2LockShiftPendingStore.WriteAtomicAsync(pendingPath, pending, CancellationToken.None).ConfigureAwait(false);
                pendingPhd2LockShift = pending;

                Phd2SettleResult stageSettle;
                try
                {
                    stageSettle = await phd2.GuideAndSettleAsync(
                        Phd2SettleCriteriaFromConfiguration(),
                        forceRecalibration: false,
                        cancellationToken).ConfigureAwait(false);
                    if (!stageSettle.Succeeded) throw new InvalidOperationException(stageSettle.Error ?? "PHD2 did not settle after exact lock shift.");
                    var stageProof = phd2.Snapshot;
                    if (!stageProof.HasCurrentSuccessfulSettle ||
                        stageProof.ConnectionEpoch != session.ConnectionEpoch ||
                        stageProof.GuideEpoch != session.GuideEpoch)
                        throw new InvalidOperationException("Exact lock shift did not retain the original same-epoch locally attested guide session.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    pending = pending with
                    {
                        Phase = Phd2LockShiftPendingPhase.ReturnRequired,
                        UpdatedUtc = DateTimeOffset.UtcNow,
                        LastReason = "User cancellation occurred while awaiting the operation-bound settle. No automatic lock-return command was sent.",
                    };
                    await Phd2LockShiftPendingStore.WriteAtomicAsync(pendingPath, pending, CancellationToken.None).ConfigureAwait(false);
                    pendingPhd2LockShift = pending;
                    throw;
                }
                catch (Exception ex)
                {
                    return await ReturnPhd2LockToOriginAsync(
                        context,
                        session,
                        pending with { Phase = Phd2LockShiftPendingPhase.ReturnRequired, LastReason = ex.Message },
                        $"Post-lock operation-bound settle failed: {ex.Message}",
                        cancellationToken).ConfigureAwait(false);
                }

                pending = pending with
                {
                    Phase = Phd2LockShiftPendingPhase.AwaitingFreshResidual,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    LastReason = "Operation-bound settle passed; fresh immutable G3 residual evidence is required.",
                };
                await Phd2LockShiftPendingStore.WriteAtomicAsync(pendingPath, pending, CancellationToken.None).ConfigureAwait(false);
                pendingPhd2LockShift = pending;

                var expectedTargetAfter = AddPoint(
                    session.LastMeasurement.Measurement.TargetCentroid,
                    SubtractPoint(exact.Verified, stage.ExpectedCurrentLockPosition));
                IReadOnlyList<Phd2GuidingResidualState> measurements;
                try
                {
                    measurements = await CapturePhd2GuidingMeasurementsAsync(
                        context,
                        preset,
                        topology,
                        exact.Verified,
                        expectedTargetAfter,
                        session.LastMeasurement.RuntimeSlitLocal,
                        session.GuideMode,
                        session.Quality.RequiredFreshResidualsPerLockShiftStage,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    pending = pending with
                    {
                        Phase = Phd2LockShiftPendingPhase.ReturnRequired,
                        UpdatedUtc = DateTimeOffset.UtcNow,
                        LastReason = "User cancellation occurred while acquiring fresh post-stage residual evidence. No automatic lock-return command was sent.",
                    };
                    await Phd2LockShiftPendingStore.WriteAtomicAsync(pendingPath, pending, CancellationToken.None).ConfigureAwait(false);
                    pendingPhd2LockShift = pending;
                    throw;
                }
                catch (Exception ex)
                {
                    return await ReturnPhd2LockToOriginAsync(
                        context,
                        session,
                        pending with { Phase = Phd2LockShiftPendingPhase.ReturnRequired, LastReason = ex.Message },
                        $"Fresh post-stage G3 residual failed: {ex.Message}",
                        cancellationToken).ConfigureAwait(false);
                }
                var measured = measurements[^1];
                var residual = PointDistance(measured.Measurement.TargetCentroid, measured.Measurement.RecognizedSlitAcquisitionPoint);
                if (residual > priorResidual + preset.MaximumResidualGrowthPixels)
                {
                    return await ReturnPhd2LockToOriginAsync(
                        context,
                        session,
                        pending with { Phase = Phd2LockShiftPendingPhase.ReturnRequired, LastReason = "Fresh target/slit residual worsened." },
                        $"Fresh target/slit residual worsened from {priorResidual:F3}px to {residual:F3}px.",
                        cancellationToken).ConfigureAwait(false);
                }

                var stageProofAfterFrame = phd2.Snapshot;
                var stageGuideResidual = PointDistance(measured.Measurement.GuideStar, exact.Verified);
                var stageQuality = SelectPhd2CalibrationQuality(
                    calibration,
                    preset,
                    Phd2CalibrationEvaluationPhase.PostSettle,
                    CreateCalibrationSettleEvidence(stageSettle, stageProofAfterFrame),
                    CreateCalibrationResidualEvidence(measured, stageGuideResidual, preset, topology, session.GuideMode),
                    Phd2CalibrationSelectionPurpose.LockShift).Selected;
                if (stageQuality?.IsLockShiftAuthority != true)
                {
                    return await ReturnPhd2LockToOriginAsync(
                        context,
                        session,
                        pending with { Phase = Phd2LockShiftPendingPhase.ReturnRequired, LastReason = "Calibration quality was revoked after the fresh stage residual." },
                        "The post-stage calibration grade no longer grants lock-shift authority.",
                        cancellationToken).ConfigureAwait(false);
                }
                var stageQualification = BuildPhd2LockShiftQualification(identity, calibration, topology, preset, stageQuality, pierSide);
                session = session with
                {
                    Qualification = stageQualification,
                    Quality = stageQuality,
                    LastMeasurement = measured,
                    Settle = stageSettle,
                };
                phd2SlitPlacementSession = session;
                ledger = ledger with { LastAcceptedFrameSha256 = measured.Measurement.FrameSha256 };
                priorResidual = residual;
                pending = pending with
                {
                    CurrentLockX = exact.Verified.X,
                    CurrentLockY = exact.Verified.Y,
                    LastAcceptedFrameSha256 = measured.Measurement.FrameSha256,
                    LastFramePath = measured.Frame.Path,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    LastReason = $"Fresh residual {residual:F3}px accepted; the next stage must replan from this actual lock/frame pair.",
                };
                await Phd2LockShiftPendingStore.WriteAtomicAsync(pendingPath, pending, CancellationToken.None).ConfigureAwait(false);
                pendingPhd2LockShift = pending;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested)
        {
            if (pendingPhd2LockShift is { Phase: not Phd2LockShiftPendingPhase.SettledBudgetLedger } cancellationPending)
            {
                cancellationPending = cancellationPending with
                {
                    Phase = Phd2LockShiftPendingPhase.ReturnRequired,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    LastReason = $"Cancellation was observed through a non-cancellation exception ({ex.Message}); no automatic lock-return command was sent.",
                };
                await Phd2LockShiftPendingStore.WriteAtomicAsync(
                    Phd2LockShiftPendingPath(cancellationPending.ObservationRunId),
                    cancellationPending,
                    CancellationToken.None).ConfigureAwait(false);
                pendingPhd2LockShift = cancellationPending;
            }
            throw new OperationCanceledException(
                "PHD2 slit placement was cancelled; durable return-required state was retained without issuing a recovery command.",
                ex,
                cancellationToken);
        }
        catch (Exception ex)
        {
            try { await StopPhdAndWaitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            phd2SlitPlacementSession = null;
            return Attention(
                ObservationStage.PlaceTargetOnSlit,
                "PHD2_SLIT_PLACEMENT_FAILED_SAFE",
                $"PHD2 slit placement stopped before an unledgered retry: {ex.Message}");
        }
    }

    private Task<StageResult> ReturnPhd2LockToOriginAsync(
        ObservationContext context,
        Phd2SlitPlacementSession session,
        Phd2LockShiftPendingState state,
        string reason,
        CancellationToken cancellationToken)
    {
        var preset = commissioning?.Value.Phd2SlitPlacement;
        if (preset is null)
            return Task.FromResult(Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RETURN_COMMISSIONING_MISSING", "PHD2 lock-return commissioning is unavailable; no command was sent."));
        var path = Phd2LockShiftPendingPath(state.ObservationRunId);
        return ReturnPhd2LockToOriginCoreAsync(
            context,
            preset,
            session.Topology,
            session.Qualification,
            state,
            path,
            reason,
            session.LastMeasurement.Measurement.TargetIdentityEvidenceId,
            cancellationToken,
            finalVerification: null);
    }

    private async Task<StageResult> ReturnPhd2LockToOriginCoreAsync(
        ObservationContext context,
        Phd2SlitPlacementCommissioningPreset preset,
        Phd2SensorTopology topology,
        Phd2LockShiftQualification qualification,
        Phd2LockShiftPendingState state,
        string path,
        string reason,
        string targetIdentityEvidenceId,
        CancellationToken cancellationToken,
        Func<Phd2Point, CancellationToken, Task<GateResult>>? finalVerification)
    {
        state = state with { Phase = Phd2LockShiftPendingPhase.ReturnRequired, UpdatedUtc = DateTimeOffset.UtcNow, LastReason = reason };
        await Phd2LockShiftPendingStore.WriteAtomicAsync(path, state, CancellationToken.None).ConfigureAwait(false);
        pendingPhd2LockShift = state;
        cancellationToken.ThrowIfCancellationRequested();

        for (var recovery = 0; recovery <= state.MaximumAttempts; recovery++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = phd2.Snapshot;
            if (!snapshot.IsConnected || snapshot.ConnectionEpoch != state.ConnectionEpoch || snapshot.GuideEpoch != state.GuideEpoch || snapshot.AppState != Phd2AppState.Guiding)
            {
                return Attention(
                    ObservationStage.PlaceTargetOnSlit,
                    "PHD2_LOCK_RETURN_EPOCH_CHANGED",
                    "PHD2 connection/guide epoch or state changed while a durable lock return was pending. The actual lock must be reconciled; no automatic command was sent.");
            }
            var actual = await phd2.GetLockPositionAsync(cancellationToken).ConfigureAwait(false);
            if (actual is null)
                return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RETURN_POSITION_UNKNOWN", "PHD2 did not report a current lock position; no return command was sent.");
            state = state with { CurrentLockX = actual.X, CurrentLockY = actual.Y, UpdatedUtc = DateTimeOffset.UtcNow };
            var ledger = state.ToPlannerLedger();
            var safety = BuildPhd2LockShiftSafetySnapshot(context, preset, topology.PierSide);
            var plan = Phd2SlitLockShiftPlanner.PlanRecoveryStage(
                qualification,
                state.GuideMode,
                ledger,
                safety,
                topology,
                preset.BuildMotionLimits(),
                DateTimeOffset.UtcNow,
                state.LastAcceptedFrameSha256 ?? new string('0', 64),
                targetIdentityEvidenceId);
            if (!plan.IsAllowed)
            {
                await Phd2LockShiftPendingStore.WriteAtomicAsync(path, state with { LastReason = $"{reason} {plan.Code}: {plan.Message}" }, CancellationToken.None).ConfigureAwait(false);
                return Attention(ObservationStage.PlaceTargetOnSlit, plan.Code, plan.Message);
            }
            if (plan.IsComplete)
            {
                if (finalVerification is not null)
                {
                    var verification = await finalVerification(actual, cancellationToken).ConfigureAwait(false);
                    if (verification.Disposition != GateDisposition.Passed)
                    {
                        state = state with
                        {
                            CurrentLockX = actual.X,
                            CurrentLockY = actual.Y,
                            Phase = Phd2LockShiftPendingPhase.ReturnRequired,
                            UpdatedUtc = DateTimeOffset.UtcNow,
                            LastReason = $"Runtime lock reached the translated recovery origin, but fresh target/slit displacement proof failed: {verification.Code}: {verification.Message}",
                        };
                        await Phd2LockShiftPendingStore.WriteAtomicAsync(path, state, CancellationToken.None).ConfigureAwait(false);
                        pendingPhd2LockShift = state;
                        return new StageResult(verification, path);
                    }
                }
                var settledState = state with
                {
                    CurrentLockX = actual.X,
                    CurrentLockY = actual.Y,
                    RequestedLockX = state.OriginLockX,
                    RequestedLockY = state.OriginLockY,
                    Phase = Phd2LockShiftPendingPhase.SettledBudgetLedger,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    LastReason = $"{reason} Runtime lock origin reached and freshly verified.",
                };
                if (!string.Equals(
                        settledState.ObservationRunId,
                        context.Plan.ObservationRunId,
                        StringComparison.Ordinal))
                {
                    var handoffGate = await PersistCurrentRunPhd2BudgetHandoffAsync(
                        context,
                        settledState,
                        CancellationToken.None).ConfigureAwait(false);
                    if (handoffGate.Disposition != GateDisposition.Passed)
                    {
                        // The foreign source remains ReturnRequired on disk.
                        // No new movement is sent; the next explicit run must
                        // reconcile the same origin before any new budget.
                        pendingPhd2LockShift = state;
                        return new StageResult(handoffGate, path);
                    }
                }
                await Phd2LockShiftPendingStore.WriteAtomicAsync(path, settledState, CancellationToken.None).ConfigureAwait(false);
                state = settledState;
                pendingPhd2LockShift = null;
                phd2SlitPlacementSession = null;
                lastG3Field = null;
                try { await StopPhdAndWaitAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_ORIGIN_REACHED_STOP_UNCONFIRMED", $"Runtime lock origin was reached, but checked PHD2 stop failed: {ex.Message}");
                }
                return Attention(
                    ObservationStage.PlaceTargetOnSlit,
                    "PHD2_LOCK_FAILURE_RETURNED",
                    $"{reason} PHD2 runtime lock returned to its freshly read run origin and guiding was stopped; a new G3 acquisition is required.");
            }

            var stage = plan.Stage!;
            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            var intent = await PublishRunJsonEvidenceAsync(
                "phd2-lock-shift-return-intent",
                $"PHD2 runtime-lock recovery stage {recovery + 1}",
                new
                {
                    reason,
                    expectedCurrent = stage.ExpectedCurrentLockPosition,
                    requested = stage.RequestedLockPosition,
                    runtimeOrigin = ledger.OriginLockPosition,
                    stage.StagePixels,
                    automaticRetryAllowed = false,
                    registryProfileMutationAllowed = false,
                },
                state.LastFramePath,
                cancellationToken).ConfigureAwait(false);
            state = state with
            {
                RequestedLockX = stage.RequestedLockPosition.X,
                RequestedLockY = stage.RequestedLockPosition.Y,
                AttemptsUsed = state.AttemptsUsed + 1,
                CumulativeCommandedPixels = state.CumulativeCommandedPixels + stage.StagePixels,
                IntentEvidencePath = intent,
                UpdatedUtc = DateTimeOffset.UtcNow,
                LastReason = $"{reason} Recovery command is precharged before dispatch.",
            };
            await Phd2LockShiftPendingStore.WriteAtomicAsync(path, state, CancellationToken.None).ConfigureAwait(false);
            pendingPhd2LockShift = state;

            Phd2Point verified;
            try
            {
                await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
                var dispatchSnapshot = phd2.Snapshot;
                if (!dispatchSnapshot.IsConnected || dispatchSnapshot.AppState != Phd2AppState.Guiding ||
                    dispatchSnapshot.ConnectionEpoch != state.ConnectionEpoch || dispatchSnapshot.GuideEpoch != state.GuideEpoch)
                {
                    await Phd2LockShiftPendingStore.WriteAtomicAsync(
                        path,
                        state with { LastReason = "PHD2 connection/guide epoch changed after the durable recovery intent; no command was sent." },
                        CancellationToken.None).ConfigureAwait(false);
                    return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RETURN_DISPATCH_EPOCH_CHANGED", "PHD2 connection/guide epoch changed after recovery intent; no exact-lock command was sent.");
                }
                var dispatchSafety = BuildPhd2LockShiftSafetySnapshot(context, preset, topology.PierSide);
                if (!dispatchSafety.SafetyGatePassed)
                {
                    await Phd2LockShiftPendingStore.WriteAtomicAsync(
                        path,
                        state with { LastReason = "Fresh safety/horizon/pier evidence failed after the durable recovery intent; no command was sent." },
                        CancellationToken.None).ConfigureAwait(false);
                    return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RETURN_DISPATCH_SAFETY_CHANGED", "Fresh safety, horizon or pier evidence failed after recovery intent; no exact-lock command was sent.");
                }
                var dispatchLock = await phd2.GetLockPositionAsync(cancellationToken).ConfigureAwait(false);
                if (dispatchLock is null || PointDistance(dispatchLock, stage.ExpectedCurrentLockPosition) > preset.LockPreconditionTolerancePixels)
                {
                    await Phd2LockShiftPendingStore.WriteAtomicAsync(
                        path,
                        state with
                        {
                            CurrentLockX = dispatchLock?.X ?? state.CurrentLockX,
                            CurrentLockY = dispatchLock?.Y ?? state.CurrentLockY,
                            LastReason = "Fresh runtime lock readback changed after the durable recovery intent; no command was sent.",
                        },
                        CancellationToken.None).ConfigureAwait(false);
                    return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RETURN_DISPATCH_POSITION_CHANGED", "The fresh runtime lock readback no longer matches the recovery plan's expected current lock; no command was sent.");
                }
                cancellationToken.ThrowIfCancellationRequested();
                var exact = await phd2.SetExactLockPositionAsync(
                    new Phd2ExactLockPositionRequest(
                        stage.ExpectedCurrentLockPosition,
                        stage.RequestedLockPosition,
                        preset.LockPreconditionTolerancePixels,
                        stage.StagePixels + 1e-9,
                        preset.LockVerificationTolerancePixels),
                    cancellationToken).ConfigureAwait(false);
                verified = exact.Verified;
            }
            catch (Phd2LockPositionReconciliationRequiredException ambiguous)
            {
                verified = ambiguous.Observed
                    ?? await phd2.GetLockPositionAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        "The ambiguous exact-lock request could not be reconciled to a fresh actual lock position.",
                        ambiguous);
                // The ambiguous request is never resent.  The next iteration
                // replans a recovery-only vector from this fresh actual lock.
                state = state with
                {
                    CurrentLockX = verified.X,
                    CurrentLockY = verified.Y,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    LastReason = $"Ambiguous recovery response reconciled at ({verified.X:F3},{verified.Y:F3}); no resend was attempted.",
                };
                await Phd2LockShiftPendingStore.WriteAtomicAsync(path, state, CancellationToken.None).ConfigureAwait(false);
                continue;
            }
            var settle = await phd2.GuideAndSettleAsync(Phd2SettleCriteriaFromConfiguration(), false, cancellationToken).ConfigureAwait(false);
            var settledSnapshot = phd2.Snapshot;
            if (!settle.Succeeded || !settledSnapshot.HasCurrentSuccessfulSettle ||
                settledSnapshot.ConnectionEpoch != state.ConnectionEpoch || settledSnapshot.GuideEpoch != state.GuideEpoch)
            {
                state = state with { CurrentLockX = verified.X, CurrentLockY = verified.Y, LastReason = "Recovery lock readback passed but operation-bound settle failed." };
                await Phd2LockShiftPendingStore.WriteAtomicAsync(path, state, CancellationToken.None).ConfigureAwait(false);
                return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RETURN_SETTLE_FAILED", settle.Error ?? "Recovery settle was not attested.");
            }

            // Every recovery stage also retains one fresh in-session G3 frame.
            // Target/slit analysis may already be scientifically invalid during
            // a failure return, but the immutable frame and lock proof are kept.
            string? framePath = null;
            string? frameSha = state.LastAcceptedFrameSha256;
            try
            {
                var evidence = await phd2.SaveCurrentGuidingFrameAsync(
                    new Phd2GuidingFrameRequest(
                        ReserveRunEvidencePath("g3-phd2-lock-return-residual", ".fit"),
                        TimeSpan.FromSeconds(preset.FreshGuidingFrameTimeoutSeconds)),
                    cancellationToken).ConfigureAwait(false);
                framePath = evidence.Path;
                frameSha = evidence.Sha256;
                PublishEvidencePathOnce(
                    "g3-phd2-lock-return-residual",
                    evidence.Path,
                    new Dictionary<string, string>
                    {
                        ["purpose"] = "runtime-lock-origin-recovery-fresh-frame",
                        ["guideEpoch"] = state.GuideEpoch.ToString(CultureInfo.InvariantCulture),
                    },
                    evidence.Sha256);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await WriteAuditBestEffortAsync("phd2-lock-return-frame-failed", new { reason, error = ex.Message, verified }).ConfigureAwait(false);
            }
            state = state with
            {
                CurrentLockX = verified.X,
                CurrentLockY = verified.Y,
                LastAcceptedFrameSha256 = frameSha,
                LastFramePath = framePath ?? state.LastFramePath,
                UpdatedUtc = DateTimeOffset.UtcNow,
                LastReason = "Recovery stage lock readback and operation-bound settle passed; next step replans from the actual lock.",
            };
            await Phd2LockShiftPendingStore.WriteAtomicAsync(path, state, CancellationToken.None).ConfigureAwait(false);
        }
        return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RETURN_ATTEMPT_LIMIT", "The durable runtime-lock recovery exhausted its bounded attempts; the ledger remains pending.");
    }

    private async Task<IReadOnlyList<Phd2GuidingResidualState>> CapturePhd2GuidingMeasurementsAsync(
        ObservationContext context,
        Phd2SlitPlacementCommissioningPreset preset,
        Phd2SensorTopology topology,
        Phd2Point currentLock,
        Phd2Point expectedTarget,
        SlitGeometry runtimeSlitLocal,
        Phd2SlitGuideMode guideMode,
        int count,
        CancellationToken cancellationToken)
    {
        if (count <= 0) throw new InvalidOperationException("The commissioned fresh-residual count must be positive.");
        var measurements = new List<Phd2GuidingResidualState>(count);
        for (var index = 0; index < count; index++)
        {
            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            var result = await phd2.SaveCurrentGuidingFrameAsync(
                new Phd2GuidingFrameRequest(
                    ReserveRunEvidencePath("g3-phd2-lock-residual", ".fit"),
                    TimeSpan.FromSeconds(preset.FreshGuidingFrameTimeoutSeconds)),
                cancellationToken).ConfigureAwait(false);
            var residualMountReadback = CaptureG3FrameMountReadback();
            var actualSha = await ComputeFileSha256Async(result.Path, cancellationToken).ConfigureAwait(false);
            if (!SameHash(actualSha, result.Sha256)) throw new InvalidOperationException("Fresh PHD2 guiding FITS changed after its immutable copy hash was returned.");
            var residualMountBinding = CreateG3FieldMountBinding(
                context,
                result.Path,
                actualSha,
                result.CompletedUtc,
                residualMountReadback);
            var image = await imageDataFactory.CreateFromFile(
                result.Path,
                16,
                false,
                RawConverterEnum.FREEIMAGE,
                cancellationToken).ConfigureAwait(false);
            var properties = image.Properties;
            if (properties.Width != preset.RoiWidth || properties.Height != preset.RoiHeight)
                throw new InvalidOperationException($"Fresh guiding frame is {properties.Width}x{properties.Height}, not commissioned ROI {preset.RoiWidth}x{preset.RoiHeight}.");
            if (image.MetaData.Camera.BinX > 0 &&
                (image.MetaData.Camera.BinX != configuration.G3.Binning || image.MetaData.Camera.BinY != configuration.G3.Binning))
                throw new InvalidOperationException("Fresh guiding FITS binning does not match the locked run/topology.");
            var exposureMilliseconds = (int)Math.Round(image.MetaData.Image.ExposureTime * 1000);
            var expectedExposureMilliseconds = preset.ExposureFor(guideMode);
            var exposureTolerance = Math.Max(1, expectedExposureMilliseconds * 0.02);
            var exposureMatched = Math.Abs(exposureMilliseconds - expectedExposureMilliseconds) <= exposureTolerance;
            if (!exposureMatched)
                throw new InvalidOperationException($"Fresh guiding FITS exposure {exposureMilliseconds}ms does not match commissioned {expectedExposureMilliseconds}ms for {guideMode}.");
            var raw = image.Data.FlatArray;
            if (raw.Length != properties.Width * properties.Height)
                throw new InvalidOperationException("Fresh guiding FITS pixel buffer is unsupported.");
            var frame = G3FrameInputPolicy.Create(properties.Width, properties.Height, raw, configuration.G3);
            var candidates = StarFieldDetector.Detect(frame);
            var expectedTargetLocal = ToFrameLocal(expectedTarget, preset);
            PixelPoint targetLocal;
            double targetFlux;
            string targetEvidence;
            var targetPositionAuthority = Phd2TargetPositionAuthority.DetectedTargetCentroid;
            if (lastG3Field?.BrightTargetAuthority is not null)
            {
                var wing = BrightTargetWingCentroidAnalyzer.Analyze(frame, configuration.G3.EffectiveBrightTarget.CentroidOptions);
                if (wing.Gate.Disposition != GateDisposition.Passed || wing.Target is null)
                    throw new InvalidOperationException($"Fresh bright-target wing centroid failed: {wing.Gate.Code}: {wing.Gate.Message}");
                if (PixelDistance(wing.Target.Centroid, expectedTargetLocal) > preset.TargetSearchRadiusPixels)
                    throw new InvalidOperationException("Fresh bright-target centroid moved outside the commissioned continuity search radius.");
                targetLocal = wing.Target.Centroid;
                targetFlux = wing.Target.WingFluxAdu;
                targetEvidence = lastG3Field.BrightTargetEvidencePath ?? lastG3Field.BrightTargetAuthority.G3FrameSha256;
            }
            else if (guideMode == Phd2SlitGuideMode.OffSlitGuideStar &&
                     lastG3Field?.TargetIdentification.Authority == TargetIdentificationAuthority.CatalogWcsProjection)
            {
                targetLocal = expectedTargetLocal;
                targetFlux = 0;
                targetPositionAuthority = Phd2TargetPositionAuthority.CatalogWcsProjection;
                targetEvidence = $"catalog-wcs:{context.Plan.Target.CatalogId}:{lastG3Field.Solve?.SolverIdentity}:{lastG3Field.FramePath}";
            }
            else
            {
                var targetId = SlitTargetIdentifier.Identify(
                    candidates,
                    expectedTargetLocal,
                    preset.TargetSearchRadiusPixels,
                    preset.MinimumTargetSignalToNoise,
                    preset.MinimumTargetUniquenessRatio);
                if (targetId.Gate.Disposition != GateDisposition.Passed || targetId.Target is null)
                    throw new InvalidOperationException($"Fresh target continuity failed: {targetId.Gate.Code}: {targetId.Gate.Message}");
                targetLocal = targetId.Target.Centroid;
                targetFlux = targetId.Target.FluxAdu;
                targetEvidence = lastG3Field?.GhostAssistance is
                    { Result.Decision: GhostAssistanceDecision.UseCalibratedAuxiliaryEstimate } ghost
                    ? $"external-catalog:{context.Plan.Target.CatalogId};ghost-auxiliary:{ghost.EvidencePath};identity:{ghost.ExternalIdentity?.EvidenceSha256}"
                    : $"{context.Plan.Target.CatalogId}:{lastG3Field?.FramePath}";
            }

            PixelPoint guideLocal;
            if (guideMode == Phd2SlitGuideMode.DegradedDirectTargetGuiding)
            {
                guideLocal = targetLocal;
            }
            else
            {
                var guideId = SlitTargetIdentifier.Identify(
                    candidates,
                    ToFrameLocal(currentLock, preset),
                    preset.GuideSearchRadiusPixels,
                    preset.MinimumGuideSignalToNoise,
                    preset.MinimumTargetUniquenessRatio);
                if (guideId.Gate.Disposition != GateDisposition.Passed || guideId.Target is null)
                    throw new InvalidOperationException($"Fresh guide-star continuity failed: {guideId.Gate.Code}: {guideId.Gate.Message}");
                guideLocal = guideId.Target.Centroid;
            }
            var slitDetection = SlitLocusDetector.DetectDarkSlit(
                frame,
                runtimeSlitLocal,
                preset.SlitMaximumPerpendicularSearchPixels,
                preset.SlitMaximumAngleSearchDegrees,
                preset.SlitMinimumContrastSigma);
            if (slitDetection.Gate.Disposition != GateDisposition.Passed)
                throw new InvalidOperationException($"Fresh runtime slit recognition failed: {slitDetection.Gate.Code}: {slitDetection.Gate.Message}");
            var target = ToPhd2Domain(targetLocal, preset);
            var guide = ToPhd2Domain(guideLocal, preset);
            // The science destination is the midpoint measured from this fresh
            // physical dark-aperture detection. It is not a historical fixed
            // pixel and it is not the nearest point on the finite segment.
            var slit = ToPhd2Domain(slitDetection.Geometry.AcquisitionPoint, preset);
            var targetResidual = PointDistance(target, slit);
            var guideResidual = PointDistance(guide, currentLock);
            var measurement = new Phd2SlitFieldMeasurement(
                result.Sha256,
                result.GuideStepUtc,
                topology.ComputeFingerprintSha256(),
                guide,
                target,
                slit,
                GuideStarSelector.DistanceToSlit(guideLocal, slitDetection.Geometry),
                TargetIdentityConfirmed: true,
                exposureMilliseconds,
                CommissionedMinimumExposureApplied: exposureMatched,
                targetEvidence,
                targetPositionAuthority == Phd2TargetPositionAuthority.CatalogWcsProjection
                    ? "CATALOG_WCS_TARGET_FLUX_NOT_APPLICABLE"
                    : guideMode == Phd2SlitGuideMode.DegradedDirectTargetGuiding ? "DEGRADED_DIRECT_TARGET_FLUX" : "TARGET_FLUX",
                targetFlux,
                $"fresh-target-slit={targetResidual:F4}px;fresh-guide-lock={guideResidual:F4}px",
                targetPositionAuthority);
            var gate = GateResult.Pass(
                "PHD2_FRESH_GUIDING_RESIDUAL",
                $"Fresh guiding FITS proved target/slit-midpoint {targetResidual:F3}px and guide/lock {guideResidual:F3}px residuals.",
                new Dictionary<string, double>
                {
                    ["targetSlitResidualPixels"] = targetResidual,
                    ["targetSlitMidpointResidualPixels"] = targetResidual,
                    ["guideLockResidualPixels"] = guideResidual,
                    ["targetFluxMetric"] = targetFlux,
                    ["guideFrame"] = result.TriggerGuideFrame,
                });
            var state = new Phd2GuidingResidualState(gate, result, image, frame, candidates, slitDetection.Geometry, measurement, residualMountBinding);
            measurements.Add(state);
            PublishEvidencePathOnce(
                "g3-phd2-lock-residual",
                result.Path,
                new Dictionary<string, string>
                {
                    ["frameSha256"] = result.Sha256,
                    ["targetSlitResidualPixels"] = targetResidual.ToString("R", CultureInfo.InvariantCulture),
                    ["guideLockResidualPixels"] = guideResidual.ToString("R", CultureInfo.InvariantCulture),
                    ["guideMode"] = guideMode.ToString(),
                    ["topologyFingerprintSha256"] = topology.ComputeFingerprintSha256(),
                    ["exposureMilliseconds"] = exposureMilliseconds.ToString(CultureInfo.InvariantCulture),
                    ["mountBindingSha256"] = residualMountBinding.BindingSha256,
                },
                result.Sha256);
            await PublishRunJsonEvidenceAsync(
                "phd2-lock-shift-fresh-residual",
                $"Fresh PHD2 guiding residual {index + 1}/{count}",
                new
                {
                    formula = "desiredGuideLock = guide + (recognizedSlitAcquisitionPoint - targetCentroid)",
                    measurement,
                    expectedCurrentLock = currentLock,
                    expectedTarget,
                    targetSlitResidualPixels = targetResidual,
                    targetSlitMidpointResidualPixels = targetResidual,
                    guideLockResidualPixels = guideResidual,
                    result.TriggerGuideFrame,
                    result.EventSequence,
                    result.GuideStepUtc,
                    result.GuidingWasInterrupted,
                    result.ExposureChanged,
                    result.CaptureLoopStarted,
                    registryProfileMutated = false,
                },
                result.Path,
                cancellationToken).ConfigureAwait(false);
            PublishG3Preview(image, gate.Message, slitDetection.Geometry, targetLocal, guideLocal);
            runtimeSlitLocal = slitDetection.Geometry;
            expectedTarget = target;
        }
        return measurements.AsReadOnly();
    }

    private async Task<Phd2PlacementGuideChoice> AcquireFreshPhd2PlacementGuideAsync(
        ObservationContext context,
        G3FieldState seedField,
        Phd2SlitPlacementCommissioningPreset preset,
        CancellationToken cancellationToken)
    {
        if (preset.GuideMode == Phd2SlitGuideMode.OffSlitGuideStar)
        {
            return await CaptureAndSelectPhd2GuideAtExposureAsync(
                context,
                seedField,
                preset,
                Phd2SlitGuideMode.OffSlitGuideStar,
                cancellationToken).ConfigureAwait(false);
        }

        if (preset.GuideMode == Phd2SlitGuideMode.DegradedDirectTargetGuiding)
        {
            return await CaptureAndSelectPhd2GuideAtExposureAsync(
                context,
                seedField,
                preset,
                Phd2SlitGuideMode.DegradedDirectTargetGuiding,
                cancellationToken).ConfigureAwait(false);
        }

        if (preset.GuideMode == Phd2SlitGuideMode.AutoPreferDirectTargetThenOffSlit)
        {
            var direct = await CaptureAndSelectPhd2GuideAtExposureAsync(
                context,
                seedField,
                preset,
                Phd2SlitGuideMode.DegradedDirectTargetGuiding,
                cancellationToken).ConfigureAwait(false);
            if (direct.Selection.Gate.Disposition == GateDisposition.Passed)
            {
                return direct;
            }

            return await CaptureAndSelectPhd2GuideAtExposureAsync(
                context,
                direct.Field,
                preset,
                Phd2SlitGuideMode.OffSlitGuideStar,
                cancellationToken).ConfigureAwait(false);
        }

        if (preset.GuideMode == Phd2SlitGuideMode.AutoPreferOffSlitThenDirectTarget)
        {
            var ordinary = await CaptureAndSelectPhd2GuideAtExposureAsync(
                context,
                seedField,
                preset,
                Phd2SlitGuideMode.OffSlitGuideStar,
                cancellationToken).ConfigureAwait(false);
            if (ordinary.Selection.Gate.Disposition == GateDisposition.Passed)
            {
                return ordinary;
            }

            return await CaptureAndSelectPhd2GuideAtExposureAsync(
                context,
                ordinary.Field,
                preset,
                Phd2SlitGuideMode.DegradedDirectTargetGuiding,
                cancellationToken).ConfigureAwait(false);
        }

        return Phd2PlacementGuideChoice.Failed(
            seedField,
            GateResult.Fail("PHD2_GUIDE_MODE_INVALID", "Unknown commissioned PHD2 guide mode."),
            preset.GuideMode,
            "invalid guide mode");
    }

    private async Task<(GuideStarSelection Selection, Phd2Point Requested, Phd2Point Selected)> SelectFreshPhd2GuideAsync(
        Phd2PlacementGuideChoice choice,
        Phd2SlitPlacementCommissioningPreset preset,
        CancellationToken cancellationToken)
    {
        if (choice.Mode == Phd2SlitGuideMode.DegradedDirectTargetGuiding)
        {
            var star = choice.Selection.Star
                ?? throw new InvalidOperationException("Direct-target guiding has no fresh target centroid.");
            var requested = ToPhd2Domain(star.Centroid, preset);
            var selected = await phd2.SelectGuideStarAsync(requested, cancellationToken).ConfigureAwait(false);
            return (choice.Selection, requested, selected);
        }

        if (choice.Mode != Phd2SlitGuideMode.OffSlitGuideStar)
            throw new InvalidOperationException($"Resolved guide mode {choice.Mode} is not selectable.");

        // PHD2 owns full-frame guide-star choice.  Local morphology is only an
        // acceptance gate for the exact point returned by native find_star;
        // a rejected point is never replaced with a coordinator-ranked star.
        var selectedNative = await phd2.FindGuideStarAsync(cancellationToken).ConfigureAwait(false);
        var selectedLocal = ToFrameLocal(selectedNative, preset);
        var target = choice.Field.TargetIdentification.Target
            ?? throw new InvalidOperationException("PHD2 native guide validation has no fresh target identity.");
        var validation = GuideStarSelector.ValidateNativeSelection(
            choice.Field.Candidates,
            choice.Field.SlitDetection.Geometry,
            target,
            selectedLocal,
            preset.GuideSearchRadiusPixels,
            new GuideStarSelectionPolicy(MinimumSignalToNoise: preset.MinimumGuideSignalToNoise));
        if (validation.Gate.Disposition != GateDisposition.Passed)
        {
            await StopPhdAndWaitAsync(CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException($"{validation.Gate.Code}: {validation.Gate.Message}");
        }

        return (validation, selectedNative, selectedNative);
    }

    private async Task<Phd2PlacementGuideChoice> CaptureAndSelectPhd2GuideAtExposureAsync(
        ObservationContext context,
        G3FieldState seedField,
        Phd2SlitPlacementCommissioningPreset preset,
        Phd2SlitGuideMode resolvedMode,
        CancellationToken cancellationToken)
    {
        var exposureMilliseconds = preset.ExposureFor(resolvedMode);
        if (exposureMilliseconds <= 0)
        {
            return Phd2PlacementGuideChoice.Failed(
                seedField,
                GateResult.Fail("PHD2_GUIDE_EXPOSURE_NOT_COMMISSIONED", $"No positive commissioned exposure exists for {resolvedMode}."),
                resolvedMode,
                "missing commissioned exposure");
        }

        if (resolvedMode == Phd2SlitGuideMode.DegradedDirectTargetGuiding &&
            exposureMilliseconds != configuration.G3.EffectiveBrightTarget.MinimumG3ExposureMilliseconds)
        {
            return Phd2PlacementGuideChoice.Failed(
                seedField,
                GateResult.Unknown(
                    "PHD2_DIRECT_TARGET_EXPOSURE_BINDING_MISMATCH",
                    $"Direct-target guide exposure {exposureMilliseconds}ms is not the run-bound bright-target minimum {configuration.G3.EffectiveBrightTarget.MinimumG3ExposureMilliseconds}ms."),
                resolvedMode,
                "direct-target exposure binding mismatch");
        }

        await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
        var capture = await phd2.CaptureFullFrameAsync(
            new Phd2SingleFrameRequest(
                exposureMilliseconds,
                configuration.G3.Binning,
                configuration.G3.GainPercent,
                ReserveRunEvidencePath($"g3-phd2-guide-selection-{resolvedMode}", ".fit")),
            cancellationToken).ConfigureAwait(false);
        var selectionMountReadback = CaptureG3FrameMountReadback();
        if (capture.VerifiedExposureMilliseconds != exposureMilliseconds || capture.AutomaticRetryAllowed)
        {
            throw new InvalidOperationException(
                "PHD2 selection-frame exposure was not exactly read back or the capture incorrectly allowed an automatic retry.");
        }
        var image = await imageDataFactory.CreateFromFile(
            capture.Path,
            16,
            false,
            RawConverterEnum.FREEIMAGE,
            cancellationToken).ConfigureAwait(false);
        var properties = image.Properties;
        if (properties.Width != preset.RoiWidth || properties.Height != preset.RoiHeight)
            throw new InvalidOperationException($"Fresh PHD2 guide-selection frame is {properties.Width}x{properties.Height}, not commissioned ROI {preset.RoiWidth}x{preset.RoiHeight}.");
        var fitsExposureMilliseconds = (int)Math.Round(image.MetaData.Image.ExposureTime * 1000);
        var exposureTolerance = Math.Max(1, exposureMilliseconds * 0.02);
        if (Math.Abs(fitsExposureMilliseconds - exposureMilliseconds) > exposureTolerance)
            throw new InvalidOperationException($"Fresh PHD2 guide-selection FITS reports {fitsExposureMilliseconds}ms, not commissioned {exposureMilliseconds}ms.");
        var raw = image.Data.FlatArray;
        if (raw.Length != properties.Width * properties.Height)
            throw new InvalidOperationException("Fresh PHD2 guide-selection FITS pixel buffer is unsupported.");
        var frame = G3FrameInputPolicy.Create(properties.Width, properties.Height, raw, configuration.G3);
        var focus = G3StellarFocusAnalyzer.Analyze(frame);
        var candidates = focus.Stars;
        var frameSha256 = await ComputeFileSha256Async(capture.Path, cancellationToken).ConfigureAwait(false);
        var selectionMountBinding = CreateG3FieldMountBinding(
            context,
            capture.Path,
            frameSha256,
            capture.CompletedUtc,
            selectionMountReadback);
        PublishEvidencePathOnce(
            "g3-phd2-guide-selection-frame",
            capture.Path,
            new Dictionary<string, string>
            {
                ["guideMode"] = resolvedMode.ToString(),
                ["exposureMilliseconds"] = exposureMilliseconds.ToString(CultureInfo.InvariantCulture),
                ["verifiedExposureMilliseconds"] = capture.VerifiedExposureMilliseconds.Value.ToString(CultureInfo.InvariantCulture),
                ["automaticRetryAllowed"] = bool.FalseString,
                ["selectionMustUseThisFrame"] = bool.TrueString,
                ["mountBindingSha256"] = selectionMountBinding.BindingSha256,
            },
            frameSha256);

        TargetIdentification identification;
        BrightTargetCentroidAnalysis? brightAnalysis = null;
        BrightTargetAuthorityEvidence? brightAuthority = null;
        if (seedField.BrightTargetAuthority is { } priorBrightAuthority)
        {
            brightAnalysis = BrightTargetWingCentroidAnalyzer.Analyze(frame, configuration.G3.EffectiveBrightTarget.CentroidOptions);
            brightAuthority = priorBrightAuthority with
            {
                G3FrameSha256 = frameSha256,
                G3FrameCompletedUtc = capture.CompletedUtc,
                G3ExposureMilliseconds = exposureMilliseconds,
                ConfiguredMinimumG3ExposureMilliseconds = configuration.G3.EffectiveBrightTarget.MinimumG3ExposureMilliseconds,
                G3FrameUsedForFocus = false,
                EvaluatedUtc = DateTimeOffset.UtcNow,
            };
            var authorityGate = BrightTargetAuthorityGate.Evaluate(brightAuthority, configuration.G3.EffectiveBrightTarget.AuthorityOptions);
            if (authorityGate.Disposition != GateDisposition.Passed || brightAnalysis.Gate.Disposition != GateDisposition.Passed || brightAnalysis.Target is null)
            {
                var code = resolvedMode == Phd2SlitGuideMode.DegradedDirectTargetGuiding
                    ? "PHD2_DIRECT_TARGET_AUTHORITY_MISSING"
                    : "PHD2_OFF_SLIT_TARGET_CONTINUITY_FAILED";
                var gate = GateResult.Unknown(code, $"Fresh {resolvedMode} frame did not re-establish bright-target identity: {authorityGate.Code}; {brightAnalysis.Gate.Code}.");
                var failedField = seedField with { Gate = gate, FramePath = capture.Path, Image = image, Frame = frame, Candidates = candidates, MainFocusMeasurement = focus, BrightTargetAnalysis = brightAnalysis, BrightTargetAuthority = brightAuthority };
                return Phd2PlacementGuideChoice.Failed(failedField, gate, resolvedMode, gate.Message, capture);
            }
            var wing = brightAnalysis.Target;
            var candidate = new StarCandidate(
                wing.Centroid,
                frame.SaturationLevel,
                wing.WingFluxAdu,
                wing.WingSignalToNoise,
                0,
                0,
                1,
                wing.EdgeDistancePixels);
            identification = new TargetIdentification(
                GateResult.Pass("PHD2_GUIDE_FRAME_BRIGHT_TARGET_IDENTIFIED", "Fresh exposure-bound wing evidence re-identified the bright target."),
                candidate,
                seedField.TargetIdentification.Target?.Centroid ?? wing.Centroid,
                seedField.TargetIdentification.Target is { } priorTarget ? PixelDistance(wing.Centroid, priorTarget.Centroid) : 0,
                brightAnalysis.UniquenessRatio);
        }
        else if (resolvedMode == Phd2SlitGuideMode.OffSlitGuideStar &&
                 seedField.TargetIdentification.Authority == TargetIdentificationAuthority.CatalogWcsProjection)
        {
            var predictedTarget = seedField.TargetIdentification.Target?.Centroid
                ?? seedField.TargetIdentification.PredictedPoint;
            identification = TargetIdentification.FromCatalogWcs(
                predictedTarget,
                properties.Width,
                properties.Height,
                $"Fresh {resolvedMode} selection retains the catalogue-WCS target geometry; target flux continuity is not applicable.");
        }
        else
        {
            var predictedTarget = seedField.TargetIdentification.Target?.Centroid
                ?? seedField.TargetIdentification.PredictedPoint;
            identification = SlitTargetIdentifier.Identify(
                candidates,
                predictedTarget,
                preset.TargetSearchRadiusPixels,
                preset.MinimumTargetSignalToNoise,
                preset.MinimumTargetUniquenessRatio);
            if (identification.Gate.Disposition != GateDisposition.Passed || identification.Target is null)
            {
                var gate = GateResult.Unknown(
                    "PHD2_GUIDE_FRAME_TARGET_CONTINUITY_FAILED",
                    $"Fresh {resolvedMode} selection frame did not re-identify the target: {identification.Gate.Code}: {identification.Gate.Message}");
                var failedField = seedField with { Gate = gate, FramePath = capture.Path, Image = image, Frame = frame, Candidates = candidates, MainFocusMeasurement = focus, TargetIdentification = identification };
                return Phd2PlacementGuideChoice.Failed(failedField, gate, resolvedMode, gate.Message, capture);
            }
        }

        var field = seedField with
        {
            Gate = GateResult.Pass("PHD2_GUIDE_SELECTION_FRAME_VALID", $"Fresh {resolvedMode} frame at {exposureMilliseconds}ms passed target continuity."),
            FramePath = capture.Path,
            Image = image,
            Frame = frame,
            Candidates = candidates,
            TargetIdentification = identification,
            MainFocusMeasurement = focus,
            BrightTargetAnalysis = brightAnalysis,
            BrightTargetAuthority = brightAuthority,
            BrightTargetEvidencePath = brightAuthority is null ? seedField.BrightTargetEvidencePath : capture.Path,
            MountBinding = selectionMountBinding,
        };
        var target = identification.Target!;
        if (resolvedMode == Phd2SlitGuideMode.OffSlitGuideStar)
        {
            var diagnostic = GuideStarSelector.Select(candidates, field.SlitDetection.Geometry, target);
            return new Phd2PlacementGuideChoice(
                field,
                new GuideStarSelection(
                    GateResult.Pass(
                        "PHD2_NATIVE_GUIDE_SELECTION_DEFERRED",
                        $"Fresh exposure-bound target/slit evidence is valid. PHD2 native full-frame find_star will choose the off-slit guide after the fresh loop; local ranking is diagnostic only ({diagnostic.Gate.Code})."),
                    diagnostic.Star,
                    diagnostic.Score),
                resolvedMode,
                exposureMilliseconds,
                "PHD2 native full-frame selection is authoritative; coordinator candidate ranking is not used",
                capture);
        }
        return new Phd2PlacementGuideChoice(
            field,
            new GuideStarSelection(
                GateResult.Pass(
                    "PHD2_DEGRADED_DIRECT_TARGET_SELECTED",
                    "The fresh shortest-exposure frame re-established the explicit bright-target authority; the target itself is the supervised degraded guide."),
                target,
                target.SignalToNoise),
            resolvedMode,
            exposureMilliseconds,
            "ordinary guide rejected; fresh shortest-exposure direct-target fallback selected",
            capture);
    }

    private Phd2SensorTopology BuildPhd2SensorTopology(
        Phd2SlitPlacementCommissioningPreset preset,
        string pierSide) => new(
        preset.InstallationEpochId,
        configuration.Phd2.ProfileId,
        configuration.Phd2.ProfileName,
        configuration.Phd2.RuntimeCameraName,
        configuration.Phd2.CameraStableId,
        configuration.Phd2.RuntimeMountName,
        phdProfileEvidence?.Sha256 ?? configuration.Phd2.ProfileEvidenceSha256,
        preset.SensorWidthPixels,
        preset.SensorHeightPixels,
        configuration.G3.Binning,
        new Phd2Rectangle(preset.RoiX, preset.RoiY, preset.RoiWidth, preset.RoiHeight),
        preset.CoordinateDomain,
        preset.SensorRotationDegrees,
        preset.RotationAuthority,
        pierSide);

    private Phd2CalibrationCandidateSelection SelectPhd2CalibrationQuality(
        Phd2CalibrationValidation calibration,
        Phd2SlitPlacementCommissioningPreset preset,
        Phd2CalibrationEvaluationPhase phase,
        Phd2CalibrationSettleEvidence? settle,
        Phd2CalibrationResidualEvidence? residual,
        Phd2CalibrationSelectionPurpose purpose)
    {
        var candidate = new Phd2CalibrationQualityCandidate(
            $"profile-{calibration.Profile.Id}:{calibration.EvaluatedUtc:O}",
            calibration,
            phase,
            ProfileEvidenceMatched: phdProfileEvidence is not null && SameHash(phdProfileEvidence.Sha256, configuration.Phd2.ProfileEvidenceSha256),
            EquipmentIdentityMatched: true,
            CalibrationTopologyMatched: preset.CalibrationTopologyEvidenceComplete ? true : null,
            CalibrationPierSideMatched: preset.CalibrationPierSideEvidenceComplete ? true : null,
            preset.CalibrationProcessEvidenceComplete,
            preset.RaBidirectionalRateRatio,
            preset.DecBidirectionalRateRatio,
            settle,
            residual);
        return Phd2CalibrationQualityEvaluator.SelectBest(
            [candidate],
            preset.CalibrationQualityPolicy,
            DateTimeOffset.UtcNow,
            purpose);
    }

    private Phd2LockShiftQualification BuildPhd2LockShiftQualification(
        Phd2IdentityValidation identity,
        Phd2CalibrationValidation calibration,
        Phd2SensorTopology topology,
        Phd2SlitPlacementCommissioningPreset preset,
        Phd2CalibrationQualityAssessment quality,
        string pierSide) => Phd2SlitLockShiftPlanner.Qualify(new Phd2LockShiftQualificationRequest(
            SlitPlacementMappingAuthority.GradedPhd2CalibrationLockShift,
            identity,
            calibration,
            topology,
            preset.LockedTopologyFingerprintSha256,
            pierSide,
            DateTimeOffset.UtcNow,
            PlateSolveRotationSeedDegrees: null,
            new Phd2LockShiftQualificationLimits(
                preset.CalibrationQualityPolicy.DegradedMaximumAge,
                TimeSpan.FromSeconds(preset.MaximumMeasurementAgeSeconds),
                preset.CalibrationQualityPolicy.DegradedMaximumOrthogonalityErrorDegrees,
                preset.MinimumAxisRatePixelsPerSecond,
                preset.MaximumAxisRatePixelsPerSecond),
            quality));

    private Phd2LockShiftSafetySnapshot BuildPhd2LockShiftSafetySnapshot(
        ObservationContext context,
        Phd2SlitPlacementCommissioningPreset preset,
        string expectedPierSide)
    {
        var now = DateTimeOffset.UtcNow;
        var protectedPlan = context.Plan with
        {
            PlannedStartUtc = now,
            PlannedDuration = context.RemainingWorstCaseDuration ?? context.Plan.PlannedDuration,
        };
        var horizon = HorizonCalculator.Evaluate(protectedPlan);
        var currentHorizontal = HorizonCalculator.GetHorizontalCoordinates(context.Plan.Target, context.Plan.Site, now);
        var pier = telescopeMediator.GetInfo().SideOfPier.ToString();
        var immediate = ValidateImmediatePhysicalActionGates(context);
        return new Phd2LockShiftSafetySnapshot(
            immediate.Disposition == GateDisposition.Passed && horizon.Passed &&
                currentHorizontal.AltitudeDegrees >= preset.MinimumAltitudeDegrees &&
                horizon.MinimumAltitudeDegrees >= preset.MinimumAltitudeDegrees &&
                string.Equals(pier, expectedPierSide, StringComparison.Ordinal),
            currentHorizontal.AltitudeDegrees,
            horizon.MinimumAltitudeDegrees,
            preset.MinimumAltitudeDegrees,
            pier,
            now);
    }

    private Phd2CalibrationSettleEvidence CreateCalibrationSettleEvidence(
        Phd2SettleResult settle,
        Phd2StateSnapshot snapshot) => new(
        $"settle-operation-{snapshot.LastSettleOperationId}",
        settle,
        snapshot.LastSettleCommandAccepted,
        SettleBeginObserved: snapshot.LastSettleOperationId.HasValue,
        SameConnectionEpoch: snapshot.LastSettleConnectionEpoch == snapshot.ConnectionEpoch,
        SameGuideEpoch: snapshot.LastSettleGuideEpoch == snapshot.GuideEpoch,
        DateTimeOffset.UtcNow);

    private static Phd2CalibrationResidualEvidence CreateCalibrationResidualEvidence(
        Phd2GuidingResidualState residual,
        double guideLockResidual,
        Phd2SlitPlacementCommissioningPreset preset,
        Phd2SensorTopology topology,
        Phd2SlitGuideMode guideMode) => new(
        residual.Frame.Sha256,
        residual.Frame.GuideStepUtc,
        guideLockResidual,
        guideMode == Phd2SlitGuideMode.DegradedDirectTargetGuiding
            ? preset.MaximumDegradedDirectTargetGuideLockResidualPixels
            : preset.MaximumGuideLockResidualPixels,
        residual.Measurement.TargetIdentityConfirmed,
        string.Equals(residual.Measurement.TopologyFingerprintSha256, topology.ComputeFingerprintSha256(), StringComparison.OrdinalIgnoreCase),
        NoUnvalidatedCalibrationOrLockShiftAfterMeasurement: true,
        DateTimeOffset.UtcNow);

    private Phd2LockShiftPendingState CreatePhd2PendingState(
        ObservationContext context,
        Phd2SlitPlacementCommissioningPreset preset,
        Phd2SlitPlacementSession session,
        Phd2LockShiftLedger ledger,
        Phd2Point requested,
        Phd2LockShiftPendingPhase phase,
        string? intentEvidencePath,
        string reason)
    {
        var now = DateTimeOffset.UtcNow;
        var originTargetLocal = ToFrameLocal(session.InitialTarget, preset);
        var originSlitLocal = GuideStarSelector.ClosestPointOnSlit(
            originTargetLocal,
            session.InitialRuntimeSlitLocal);
        var originSlit = ToPhd2Domain(originSlitLocal, preset);
        return new Phd2LockShiftPendingState(
            Phd2LockShiftPendingState.CurrentSchemaVersion,
            context.Plan.ObservationRunId,
            ledger.LineageId,
            configuration.ActionConfigurationSha256,
            commissioning!.Sha256,
            ComputeSlitRecoveryContextSha256(context),
            preset.CalibrationQualityPolicy.PolicyId,
            preset.CalibrationQualityPolicySha256,
            session.Topology.ComputeFingerprintSha256(),
            session.GuideMode,
            session.ConnectionEpoch,
            session.GuideEpoch,
            ledger.OriginLockPosition.X,
            ledger.OriginLockPosition.Y,
            ledger.CurrentLockPosition.X,
            ledger.CurrentLockPosition.Y,
            requested.X,
            requested.Y,
            preset.MaximumStagePixels,
            preset.MaximumCumulativePixels,
            preset.MaximumAttempts,
            preset.MaximumElapsedSeconds,
            ledger.CumulativeCommandedPixels,
            ledger.AttemptsUsed,
            ledger.StartedUtc,
            now,
            now,
            phase,
            ledger.LastAcceptedFrameSha256,
            session.LastMeasurement.Frame.Path,
            intentEvidencePath,
            reason,
            session.InitialTarget.X,
            session.InitialTarget.Y,
            originSlit.X,
            originSlit.Y);
    }

    private async Task PublishPhd2GuideSelectionEvidenceAsync(
        ObservationContext context,
        G3FieldState field,
        GuideStarSelection selection,
        Phd2Point requested,
        Phd2Point selected,
        Phd2SlitPlacementCommissioningPreset preset,
        Phd2SlitGuideMode resolvedGuideMode,
        Phd2SingleFrameResult selectionFrame,
        Phd2LoopingStartResult loop,
        Phd2Rectangle guideSelectionRoi,
        CancellationToken cancellationToken)
    {
        var target = field.TargetIdentification.Target!;
        var exactPixelScaleArcsecondsPerPixel = await phd2.GetPixelScaleAsync(cancellationToken).ConfigureAwait(false);
        var nativeSelection = resolvedGuideMode == Phd2SlitGuideMode.OffSlitGuideStar;
        await PublishRunJsonEvidenceAsync(
            "phd2-full-frame-guide-takeover",
            "PHD2 full-frame loop selection followed by guide takeover",
            new
            {
                context.Plan.Target,
                configuredGuideMode = preset.GuideMode.ToString(),
                resolvedGuideMode = resolvedGuideMode.ToString(),
                selectionFrame.Path,
                selectionFrame.CompletedUtc,
                selectionFrame.VerifiedExposureMilliseconds,
                selectionFrame.AutomaticRetryAllowed,
                targetCentroid = target.Centroid,
                runtimeSlitAcquisitionPoint = field.SlitDetection.Geometry.AcquisitionPoint,
                requestedGuidePosition = requested,
                selectedGuidePosition = selected,
                guideSelectionRoi,
                exactPixelScaleArcsecondsPerPixel,
                guideSelectionAuthority = nativeSelection
                    ? "PHD2 native full-frame find_star; coordinator validates the exact returned point and never ranks a substitute"
                    : "commissioned direct target centroid selected through PHD2 point selection",
                candidateRankingByCoordinator = false,
                nativeFullFrameSelection = nativeSelection,
                selectionGate = selection.Gate,
                selectedCandidate = selection.Star,
                loop.InitialState,
                loop.Frame,
                loop.EventSequence,
                loop.ConnectionEpoch,
                loop.GuideEpoch,
                loop.StopCommandSent,
                loop.ExposureChanged,
                loop.LeavesLoopingForGuideTakeover,
                desiredLockFormula = "guide + (slit - target)",
                registryProfileMutationAllowed = false,
            },
            field.FramePath,
            cancellationToken).ConfigureAwait(false);
    }

    private static Phd2Rectangle BuildPhd2GuideSelectionRoi(
        Phd2Point selected,
        int sensorWidthPixels,
        int sensorHeightPixels)
    {
        const int commissionedSizePixels = 80;
        if (!double.IsFinite(selected.X) || !double.IsFinite(selected.Y))
            throw new ArgumentOutOfRangeException(nameof(selected), "Selected PHD2 guide position must be finite.");
        if (sensorWidthPixels <= 0 || sensorHeightPixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(sensorWidthPixels), "PHD2 sensor dimensions must be positive.");

        var width = Math.Min(commissionedSizePixels, sensorWidthPixels);
        var height = Math.Min(commissionedSizePixels, sensorHeightPixels);
        var x = Math.Clamp(
            (int)Math.Floor(selected.X - width / 2d),
            0,
            sensorWidthPixels - width);
        var y = Math.Clamp(
            (int)Math.Floor(selected.Y - height / 2d),
            0,
            sensorHeightPixels - height);
        return new Phd2Rectangle(x, y, width, height);
    }

    private Phd2SettleCriteria Phd2SettleCriteriaFromConfiguration() => new(
        configuration.Phd2.SettlePixels,
        configuration.Phd2.SettleStableSeconds,
        configuration.Phd2.SettleTimeoutSeconds);

    private static Dictionary<string, double> Phd2QualityMetrics(
        Phd2CalibrationQualityAssessment quality,
        Phd2Point selectedGuide,
        Phd2SettleResult settle,
        double residual) => new()
    {
        ["guideStarX"] = selectedGuide.X,
        ["guideStarY"] = selectedGuide.Y,
        ["settleFrames"] = settle.TotalFrames,
        ["settleDroppedFrames"] = settle.DroppedFrames,
        ["slitResidualPixels"] = residual,
        ["phd2CalibrationGrade"] = (int)quality.Grade,
        ["phd2CanAttemptValidationGuide"] = quality.CanAttemptValidationGuide ? 1 : 0,
        ["phd2IsLockShiftAuthority"] = quality.IsLockShiftAuthority ? 1 : 0,
        ["phd2IsUnattendedScienceAuthority"] = quality.IsUnattendedScienceAuthority ? 1 : 0,
        ["phd2MaximumLockShiftScale"] = quality.MaximumLockShiftScale,
        ["phd2RequiredResidualToleranceScale"] = quality.RequiredResidualToleranceScale,
        ["phd2EvaluatedCandidateCount"] = 1,
    };

    private static Dictionary<string, double> Phd2EffectiveQualityMetrics(
        Phd2CalibrationQualityAssessment quality,
        Phd2SlitGuideMode guideMode,
        Phd2Point selectedGuide,
        Phd2SettleResult settle,
        double residual)
    {
        var metrics = Phd2QualityMetrics(quality, selectedGuide, settle, residual);
        metrics["phd2RequiresOperatorSupervision"] = RequiresSupervisedPhd2Science(quality, guideMode) ? 1 : 0;
        metrics["phd2IsUnattendedScienceAuthority"] = IsUnattendedPhd2ScienceAuthority(quality, guideMode) ? 1 : 0;
        metrics["degradedDirectTargetGuiding"] = guideMode == Phd2SlitGuideMode.DegradedDirectTargetGuiding ? 1 : 0;
        return metrics;
    }

    private static string CalibrationSelectionMessage(Phd2CalibrationCandidateSelection selection) =>
        selection.Assessments.Count == 0
            ? "No active PHD2 calibration was evaluated."
            : $"Single active PHD2 calibration evaluated (production does not yet load or rank calibration history): {string.Join(" | ", selection.Assessments.Select(assessment =>
                $"{assessment.CandidateId}={assessment.Grade}: {string.Join("; ", assessment.HardFailures.Count > 0 ? assessment.HardFailures : assessment.Reasons)}"))}";

    private static bool RequiresSupervisedPhd2Science(
        Phd2CalibrationQualityAssessment quality,
        Phd2SlitGuideMode guideMode) =>
        quality.RequiresOperatorSupervision ||
        guideMode == Phd2SlitGuideMode.DegradedDirectTargetGuiding;

    private static bool IsUnattendedPhd2ScienceAuthority(
        Phd2CalibrationQualityAssessment quality,
        Phd2SlitGuideMode guideMode) =>
        quality.IsUnattendedScienceAuthority &&
        guideMode != Phd2SlitGuideMode.DegradedDirectTargetGuiding;

    private async Task<(bool RunIsTerminal, GateResult? Error)> ValidatePhd2LockManifestAsync(
        Phd2LockShiftPendingFileResult item,
        CancellationToken cancellationToken)
    {
        var state = item.State!;
        var controlDirectory = Path.GetDirectoryName(item.Path);
        var runDirectory = controlDirectory is null ? null : Path.GetDirectoryName(controlDirectory);
        if (runDirectory is null)
            return (false, GateResult.Unknown("PHD2_LOCK_MANIFEST_PATH_INVALID", $"Cannot derive a run manifest from '{item.Path}'."));
        var manifestPath = Path.Combine(runDirectory, "manifest.json");
        ObservationRunManifest? manifest;
        try
        {
            manifest = await new ObservationRunJournalStore(manifestPath).ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return (false, GateResult.Unknown("PHD2_LOCK_MANIFEST_UNREADABLE", $"Run manifest '{manifestPath}' cannot attest PHD2 lock lineage {state.LineageId}: {ex.Message}"));
        }
        if (manifest is null)
            return (false, GateResult.Unknown("PHD2_LOCK_MANIFEST_MISSING", $"Run manifest '{manifestPath}' is missing; automatic lineage adoption is prohibited."));
        if (!string.Equals(manifest.ObservationRunId, state.ObservationRunId, StringComparison.Ordinal))
            return (false, GateResult.Unknown("PHD2_LOCK_MANIFEST_RUN_MISMATCH", $"Run manifest '{manifestPath}' does not belong to ledger run '{state.ObservationRunId}'."));
        if (manifest.LockedMetadata.Labels is null ||
            !manifest.LockedMetadata.Labels.TryGetValue("telescopeId", out var telescopeId) ||
            string.IsNullOrWhiteSpace(telescopeId) ||
            !SameHash(state.RecoveryContextSha256, ComputeSlitRecoveryContextSha256(manifest.Plan, telescopeId)))
            return (false, GateResult.Unknown("PHD2_LOCK_MANIFEST_CONTEXT_MISMATCH", $"Run manifest '{manifestPath}' does not reproduce the target/site/horizon/Night-Setup/telescope context hash."));
        if (manifest.LockedMetadata.AdditionalHashes is null ||
            !manifest.LockedMetadata.AdditionalHashes.TryGetValue("actionConfigurationSha256", out var actionHash) ||
            !SameHash(state.ActionConfigurationSha256, actionHash) ||
            manifest.LockedMetadata.CommissioningPresetSha256 is null ||
            !SameHash(state.CommissioningPresetSha256, manifest.LockedMetadata.CommissioningPresetSha256))
            return (false, GateResult.Unknown("PHD2_LOCK_MANIFEST_BINDING_MISMATCH", $"Run manifest '{manifestPath}' does not reproduce the action/config commissioning hashes."));
        return (manifest.TerminalState is not null, null);
    }

    private GateResult ValidateCurrentPhd2LockLedgerBinding(
        ObservationContext context,
        Phd2SlitPlacementCommissioningPreset preset,
        Phd2LockShiftPendingState state)
    {
        var failures = new List<string>();
        if (!SameHash(state.ActionConfigurationSha256, configuration.ActionConfigurationSha256)) failures.Add("action configuration hash changed");
        if (!SameHash(state.CommissioningPresetSha256, commissioning!.Sha256)) failures.Add("commissioning preset hash changed");
        if (!SameHash(state.RecoveryContextSha256, ComputeSlitRecoveryContextSha256(context))) failures.Add("target/site/horizon/Night-Setup/telescope context changed");
        if (!string.Equals(state.CalibrationQualityPolicyId, preset.CalibrationQualityPolicy.PolicyId, StringComparison.Ordinal) ||
            !SameHash(state.CalibrationQualityPolicySha256, preset.CalibrationQualityPolicySha256)) failures.Add("calibration-quality policy changed");
        if (!SameHash(state.TopologyFingerprintSha256, preset.LockedTopologyFingerprintSha256)) failures.Add("sensor topology fingerprint changed");
        if (Math.Abs(state.MaximumStagePixels - preset.MaximumStagePixels) > 1e-9 ||
            Math.Abs(state.MaximumCumulativePixels - preset.MaximumCumulativePixels) > 1e-9 ||
            state.MaximumAttempts != preset.MaximumAttempts ||
            Math.Abs(state.MaximumElapsedSeconds - preset.MaximumElapsedSeconds) > 1e-9) failures.Add("bounded-motion limits changed");
        return failures.Count == 0
            ? GateResult.Pass("PHD2_LOCK_LEDGER_BINDING_VALID", "The canonical PHD2 lock ledger matches the immutable run/config/context/policy/topology bindings.")
            : GateResult.Unknown("PHD2_LOCK_LEDGER_BINDING_CHANGED", $"Durable PHD2 lock lineage cannot be adopted: {string.Join("; ", failures)}.");
    }

    private async Task<GateResult> PersistCurrentRunPhd2BudgetHandoffAsync(
        ObservationContext context,
        Phd2LockShiftPendingState settledForeignState,
        CancellationToken cancellationToken)
    {
        var currentPath = Phd2LockShiftPendingPath(context.Plan.ObservationRunId);
        var currentRecoveryContext = ComputeSlitRecoveryContextSha256(context);
        Phd2LockShiftPendingState handoff;
        try
        {
            handoff = Phd2LockShiftBudgetHandoff.CreateCurrentRunSettledCopy(
                settledForeignState,
                context.Plan.ObservationRunId,
                currentRecoveryContext,
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return GateResult.Unknown(
                "PHD2_LOCK_HANDOFF_CREATE_INVALID",
                $"The freshly verified foreign return could not create a current-run budget handoff: {ex.Message}");
        }

        var existing = await Phd2LockShiftPendingStore.LoadAsync(
            currentPath,
            cancellationToken).ConfigureAwait(false);
        if (existing.Error is not null)
        {
            return GateResult.Unknown(
                "PHD2_LOCK_HANDOFF_CURRENT_COPY_UNREADABLE",
                $"The current-run canonical PHD2 ledger cannot be validated: {existing.Error}");
        }
        if (existing.State is not null)
        {
            var issues = Phd2LockShiftBudgetHandoff.ValidateCompletedHandoff(
                settledForeignState,
                existing.State,
                context.Plan.ObservationRunId,
                currentRecoveryContext);
            if (issues.Count > 0)
            {
                return GateResult.Unknown(
                    "PHD2_LOCK_HANDOFF_CURRENT_COPY_INCONSISTENT",
                    $"The current-run canonical PHD2 ledger conflicts with foreign lineage {settledForeignState.LineageId}: {string.Join("; ", issues)}. No new budget or command is allowed.");
            }
            return GateResult.Pass(
                "PHD2_LOCK_HANDOFF_ALREADY_DURABLE",
                $"Current run already contains the same settled PHD2 lineage {handoff.LineageId} and inherited budget.");
        }

        await Phd2LockShiftPendingStore.WriteAtomicAsync(
            currentPath,
            handoff,
            cancellationToken).ConfigureAwait(false);
        return GateResult.Pass(
            "PHD2_LOCK_HANDOFF_DURABLE",
            $"Foreign PHD2 lineage {handoff.LineageId} was atomically handed to the current run without resetting {handoff.AttemptsUsed} attempts, {handoff.CumulativeCommandedPixels:F3}px or its {handoff.StartedUtc:O} clock.");
    }

    private static bool CanUseIndependentFallbackAfterPhd2Preflight(string code) => code is
        "GUIDE_STAR_NOT_FOUND" or
        "PHD2_DIRECT_TARGET_AUTHORITY_MISSING" or
        "PHD2_GUIDE_FRAME_TARGET_CONTINUITY_FAILED" or
        "PHD2_OFF_SLIT_TARGET_CONTINUITY_FAILED";

    private static Phd2Point ToPhd2Domain(PixelPoint local, Phd2SlitPlacementCommissioningPreset preset) =>
        preset.CoordinateDomain == Phd2ImageCoordinateDomain.FullSensorCoordinates
            ? new Phd2Point(local.X + preset.RoiX, local.Y + preset.RoiY)
            : new Phd2Point(local.X, local.Y);

    private static PixelPoint ToFrameLocal(Phd2Point domain, Phd2SlitPlacementCommissioningPreset preset) =>
        preset.CoordinateDomain == Phd2ImageCoordinateDomain.FullSensorCoordinates
            ? new PixelPoint(domain.X - preset.RoiX, domain.Y - preset.RoiY)
            : new PixelPoint(domain.X, domain.Y);

    private static double PointDistance(Phd2Point a, Phd2Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double PixelDistance(PixelPoint a, PixelPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static Phd2Point AddPoint(Phd2Point a, Phd2Point b) => new(a.X + b.X, a.Y + b.Y);
    private static Phd2Point SubtractPoint(Phd2Point a, Phd2Point b) => new(a.X - b.X, a.Y - b.Y);

    private static G3FieldState UpdateG3FieldFromGuidingResidual(
        G3FieldState previous,
        Phd2GuidingResidualState residual,
        Phd2SlitPlacementCommissioningPreset preset)
    {
        var targetLocal = ToFrameLocal(residual.Measurement.TargetCentroid, preset);
        var target = previous.TargetIdentification.Target! with
        {
            Centroid = targetLocal,
            FluxAdu = residual.Measurement.FluxMetric,
        };
        return previous with
        {
            Gate = residual.Gate,
            FramePath = residual.Frame.Path,
            Image = residual.Image,
            Frame = residual.MonochromeFrame,
            Candidates = residual.Candidates,
            SlitDetection = previous.SlitDetection with { Geometry = residual.RuntimeSlitLocal },
            TargetIdentification = previous.TargetIdentification with { Target = target },
            MountBinding = residual.MountBinding,
        };
    }
}

internal sealed record Phd2GuidingResidualState(
    GateResult Gate,
    Phd2GuidingFrameResult Frame,
    IImageData Image,
    MonochromeFrame MonochromeFrame,
    IReadOnlyList<StarCandidate> Candidates,
    SlitGeometry RuntimeSlitLocal,
    Phd2SlitFieldMeasurement Measurement,
    G3FieldMountBinding MountBinding);

internal sealed record Phd2PlacementGuideChoice(
    G3FieldState Field,
    GuideStarSelection Selection,
    Phd2SlitGuideMode Mode,
    int ExposureMilliseconds,
    string SelectionReason,
    Phd2SingleFrameResult Capture)
{
    public static Phd2PlacementGuideChoice Failed(
        G3FieldState field,
        GateResult gate,
        Phd2SlitGuideMode mode,
        string reason,
        Phd2SingleFrameResult? capture = null) => new(
            field,
            new GuideStarSelection(gate, null, 0),
            mode,
            0,
            reason,
            capture ?? new Phd2SingleFrameResult(string.Empty, true, false, DateTimeOffset.MinValue));
}

internal sealed record Phd2SlitPlacementSession(
    Phd2SlitGuideMode GuideMode,
    Phd2SensorTopology Topology,
    Phd2LockShiftQualification Qualification,
    Phd2CalibrationQualityAssessment Quality,
    Phd2CalibrationValidation Calibration,
    Phd2Point SelectedGuide,
    Phd2Point OriginLock,
    Phd2Point InitialTarget,
    SlitGeometry InitialRuntimeSlitLocal,
    Phd2GuidingResidualState LastMeasurement,
    Phd2SettleResult Settle,
    long ConnectionEpoch,
    long GuideEpoch,
    bool ForcedRecalibration);
