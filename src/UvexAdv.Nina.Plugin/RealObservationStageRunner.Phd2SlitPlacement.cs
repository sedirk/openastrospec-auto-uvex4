using System.Globalization;
using System.IO;
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using UvexAdv.Core;
using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private bool phd2AgedCalibrationRefreshAttempted;
    private sealed record Phd2HomeBoundaryProof(string RunId, string TelescopeId, DateTimeOffset VerifiedUtc, string EvidencePath);
    private Phd2HomeBoundaryProof? phd2HomeBoundaryProof;
    private (Phd2CalibrationData Calibration, long ConnectionEpoch, DateTimeOffset StartedUtc)? localPhd2CalibrationProof;
    private StageResult ReusePhd2SlitPlacementGuiding(Phd2SlitPlacementSession session)
    {
        var snapshot = phd2.Snapshot;
        var residual = PointDistance(
            session.LastMeasurement.Measurement.TargetCentroid,
            session.LastMeasurement.Measurement.RecognizedSlitAcquisitionPoint);
        var metrics = Phd2QualityMetrics(session.Quality, session.SelectedGuide, session.Settle, residual);
        if ((!snapshot.HasCurrentSuccessfulSettle && !HasCurrentSupervisedGuidingWindow(session, snapshot)) ||
            snapshot.AppState != Phd2AppState.Guiding ||
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
        if (session.SlitPrecisionWarningActive)
        {
            metrics["slitPrecisionWarning"] = 1;
            metrics["phd2IsUnattendedScienceAuthority"] = 0;
            return Warning("PHD2_GUIDING_SUPERVISED_SLIT_PRECISION_WARNING",
                "保持已验证的原生导星周期；入缝精度仍为警告，仅在本次明确授权下由 ATR 实际光谱判断可用性，不宣称精确入缝。",
                metrics);
        }
        var requiresSupervision = RequiresSupervisedPhd2Science(session.Quality, session.GuideMode);
        var unattendedAuthority = IsUnattendedPhd2ScienceAuthority(session.Quality, session.GuideMode);
        metrics["phd2RequiresOperatorSupervision"] = requiresSupervision ? 1 : 0;
        metrics["phd2IsUnattendedScienceAuthority"] = unattendedAuthority ? 1 : 0;
        var supervisedOptIn = HasSupervisedScienceOptIn() &&
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
            return session.FreshGuidingWindowReplacedSettle
                ? Warning(
                    "PHD2_GUIDING_WIND_SAMPLED_SUPERVISED",
                    $"PHD2 remained in the same Guiding epoch but did not stay inside the configured settle circle. Fresh guide-step/FITS samples proved the live target/slit geometry, so this supervised run continues with a wind warning; calibration grade {session.Quality.Grade}.",
                    metrics,
                    commissioning is null ? null : Metadata(commissioning))
                : Passed(
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
            var topologyResolution = ResolvePhd2RuntimeTopology(preset, pierSide);
            if (!topologyResolution.IsAllowed || topologyResolution.RuntimeTopology is null)
                return Attention(ObservationStage.StartGuiding, topologyResolution.Code, topologyResolution.Message);
            var topology = topologyResolution.RuntimeTopology;

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
                if (preGuide.Selected.RequiresOperatorSupervision && !HasSupervisedScienceOptIn())
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
                !HasSupervisedScienceOptIn())
            {
                return Attention(
                    ObservationStage.StartGuiding,
                    "PHD2_DIRECT_TARGET_SUPERVISION_OPT_IN_REQUIRED",
                    "Fresh selection resolved to degraded direct-target guiding. This run has no explicit supervised-science opt-in, so no guide or lock command was sent.");
            }
            var target = choice.Field.TargetIdentification.Target!;
            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            Phd2LoopingStartResult loop = await phd2.StartLoopingAndWaitForFreshFrameAsync(
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
            (GuideStarSelection Selection, Phd2Point Requested, Phd2Point Selected) guideSelectionResult;
            try
            {
                guideSelectionResult = await SelectFreshPhd2GuideAsync(
                    choice,
                    preset,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Phd2NativeGuideSelectionExhaustedException exhausted)
            {
                await StopPhdAndWaitAsync(CancellationToken.None).ConfigureAwait(false);
                if (preset.GuideMode != Phd2SlitGuideMode.AutoPreferOffSlitThenDirectTarget ||
                    choice.Mode != Phd2SlitGuideMode.OffSlitGuideStar)
                {
                    return Attention(
                        ObservationStage.StartGuiding,
                        "PHD2_OFF_SLIT_NATIVE_SELECTION_EXHAUSTED",
                        $"Strict off-slit guiding stopped after bounded PHD2-native selection was exhausted; no coordinator-ranked substitute or guide command was sent. {exhausted.Message}");
                }
                if (pendingPhd2LockShift is { Phase: not Phd2LockShiftPendingPhase.SettledBudgetLedger })
                {
                    return Attention(
                        ObservationStage.StartGuiding,
                        "PHD2_DIRECT_TARGET_FALLBACK_MOTION_STATE_UNSAFE",
                        "An unreturned durable exact-lock lineage exists, so guide-mode fallback is prohibited until it is reconciled.");
                }
                if (!HasSupervisedScienceOptIn())
                {
                    return Attention(
                        ObservationStage.StartGuiding,
                        "PHD2_DIRECT_TARGET_SUPERVISION_OPT_IN_REQUIRED",
                        "PHD2-native off-slit selection was exhausted. Direct-target fallback requires explicit supervised-science opt-in; PHD2 was checked-stopped and no guide command was sent.");
                }

                var fallback = await PrepareDirectTargetFallbackAfterNativeExhaustionAsync(
                    context,
                    choice,
                    preset,
                    exhausted,
                    cancellationToken).ConfigureAwait(false);
                choice = fallback.Choice;
                lastG3Field = choice.Field;
                target = choice.Field.TargetIdentification.Target
                    ?? throw new InvalidOperationException("Fresh direct-target fallback frame passed without a target identity.");
                loop = fallback.Loop;
                guideSelectionResult = (fallback.Selection, fallback.Requested, fallback.Selected);
            }
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
            // Looping/exposure changes used to obtain the immutable selection
            // frame can legitimately emit ConfigurationChange.  The PHD2
            // client invalidates its cached calibration attestation on that
            // event, so refresh the attestation at the last possible point
            // before guide rather than treating a cleared cache as a failed
            // calibration.  A genuinely invalid readback still requests the
            // existing one-shot forced recalibration path.
            calibrationBefore = await phd2.ValidateCalibrationAsync(
                policy.ApplyHardRejectionCeilings(PhdCalibrationRequirement()),
                cancellationToken).ConfigureAwait(false);
            forceRecalibration = calibrationBefore.Status != Phd2ValidationStatus.Valid;
            if (forceRecalibration)
            {
                // Exposure/loop ConfigurationChange invalidates the earlier
                // identity attestation as well as the calibration cache. Recheck
                // actual profile/equipment after selection; never bypass the
                // client's forced-calibration identity prerequisite.
                identity = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), cancellationToken).ConfigureAwait(false);
                if (!identity.IsValid)
                    throw new Phd2IdentityMismatchException(identity);
            }
            Volatile.Write(ref phd2GuidingEverStarted, 1);
            var settle = await phd2.GuideAndSettleAsync(
                Phd2SettleCriteriaForSlitPlacement(preset),
                forceRecalibration,
                guideSelectionRoi,
                preserveSameEpochGuidingOnSettleTimeout: HasSupervisedScienceOptIn(),
                cancellationToken).ConfigureAwait(false);
            var proof = phd2.Snapshot;
            var windSampledSettle = CanReplaceSettleWithFreshGuidingWindow(settle, proof);
            if ((!settle.Succeeded && !windSampledSettle) ||
                (settle.Succeeded && !proof.HasCurrentSuccessfulSettle))
                throw new InvalidOperationException(settle.Error ?? "The local PHD2 guide operation did not leave a current settle attestation.");
            if (windSampledSettle)
                Report("warning：海风导致 PHD2 未进入 settle 圈；保持同一 Guiding epoch，改取 fresh GuideStep/FITS 窗口评估");
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
                windSampledSettle
                    ? Math.Max(3, policy.RequiredFreshResidualsPerLockShiftStage)
                    : policy.RequiredFreshResidualsPerLockShiftStage,
                cancellationToken).ConfigureAwait(false);
            var measurement = measurements[^1];
            var guideResidual = PointDistance(measurement.Measurement.GuideStar, lockPosition);
            var post = SelectPhd2CalibrationQuality(
                calibration,
                preset,
                Phd2CalibrationEvaluationPhase.PostSettle,
                CreateCalibrationSettleEvidence(settle, proof, windSampledSettle, measurements.Count),
                CreateCalibrationResidualEvidence(
                    measurement,
                    guideResidual,
                    preset,
                    topology,
                    choice.Mode,
                    windSampledSettle && choice.Mode == Phd2SlitGuideMode.DegradedDirectTargetGuiding
                        ? Math.Sqrt((double)preset.SensorWidthPixels * preset.SensorWidthPixels + (double)preset.SensorHeightPixels * preset.SensorHeightPixels)
                        : null),
                Phd2CalibrationSelectionPurpose.LockShift);
            var quality = post.Selected;
            if (quality?.IsLockShiftAuthority != true)
                throw new InvalidOperationException($"Post-settle graded guide authority failed: {CalibrationSelectionMessage(post)}");
            if (RequiresSupervisedPhd2Science(quality, choice.Mode) && !HasSupervisedScienceOptIn())
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
                forceRecalibration,
                windSampledSettle);
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
        // The real-science loader has required commissioning schema 5 since
        // optical slit identity became part of the same signed preset.  This
        // recovery guard was accidentally left at schema 3, so every valid
        // current preset was rejected only after the expensive G3 acquisition
        // had completed.  Reuse the already-loaded schema-5 preset here; the
        // nested PHD2 contract still performs its own full validation below.
        if (loaded is null || loaded.Value.SchemaVersion != 5 || preset is null || preset.Validate().Count > 0)
        {
            return Attention(
                ObservationStage.PlaceTargetOnSlit,
                "PHD2_LOCK_RECOVERY_SCHEMA5_REQUIRED",
                "A valid hash-bound schema-5 commissioning preset with PHD2 placement and optical slit identity is required before durable runtime-lock recovery.");
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
            if (await RetireCancelledPhd2TargetAfterFreshNewFieldAsync(context, selected, cancellationToken).ConfigureAwait(false))
                return null;
            var currentPierSide = telescopeMediator.GetInfo().SideOfPier.ToString();
            var currentTopologyResolution = ResolvePhd2RuntimeTopology(preset, currentPierSide);
            if (!currentTopologyResolution.IsAllowed || currentTopologyResolution.RuntimeTopology is null)
                return Attention(ObservationStage.PlaceTargetOnSlit, currentTopologyResolution.Code, currentTopologyResolution.Message);
            var currentTopologySha256 = currentTopologyResolution.RuntimeTopology.ComputeFingerprintSha256();
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
                    ComputeSlitRecoveryContextSha256(context),
                    currentTopologySha256);
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

            // A durable endpoint tuple equal to the recorded origin is not, by
            // itself, proof that the operation-bound settle and optical return
            // verification completed.  ReturnRequired is also written after a
            // failed final verification.  Therefore even a zero-length vector
            // must take the fresh verified recovery path below: it sends
            // no lock motion, obtains fresh exact-lock/field evidence, and only
            // then performs the atomic settled-budget handoff.
        }
        // A foreign unfinished lineage is a physical return obligation, not
        // authority to continue the old target's outbound placement. Its own
        // manifest has already reproduced and authenticated the original
        // target/site context above. Requiring the newly selected target to
        // have the same context hash made cross-run recovery unreachable.
        // Recovery instead keeps the action/config/policy/topology/limits hard
        // bindings and re-evaluates current safety, field and mount evidence
        // before every command inside RecoverPersistedPhd2LockToOriginAsync.
        var binding = selectedIsForeign
            ? ValidateForeignPhd2LockRecoveryBinding(context, preset, selected.State!)
            : ValidateCurrentPhd2LockLedgerBinding(context, preset, selected.State!);
        if (binding.Disposition != GateDisposition.Passed) return new StageResult(binding, selected.Path);
        var recovery = await RecoverPersistedPhd2LockToOriginAsync(
            context,
            preset,
            selected,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(recovery.Gate.Code, "PHD2_LOCK_FAILURE_RETURNED", StringComparison.Ordinal))
            return recovery;

        // The durable return has already been freshly verified, persisted and
        // checked-stopped.  Treat that as an internal recovery transition,
        // rebuild the invalidated G3/PL3/slit field immediately, then let the
        // ordinary placement path continue.  Returning an operator-facing
        // Attention result here created a deterministic dead end after every
        // otherwise successful cross-run return.
        Report("PHD2 旧锁点已验证回原并结清；自动重建 fresh G3/PL3/狭缝场后继续本轮入缝，不要求人工重启");
        var reacquired = await AcquireG3SlitFieldAsync(
            context,
            cancellationToken,
            allowChargedCurrentPositionHandoff: true).ConfigureAwait(false);
        await PublishRunJsonEvidenceAsync(
            "phd2-lock-return-auto-g3-reacquisition",
            "Verified PHD2 return automatically rebuilt fresh G3/PL3/slit authority",
            new
            {
                sourceRunId = selected.State!.ObservationRunId,
                currentRunId = context.Plan.ObservationRunId,
                recovery.Gate.Code,
                recovery.Gate.Message,
                reacquisitionCode = reacquired.Gate.Code,
                reacquisitionDisposition = reacquired.Gate.Disposition.ToString(),
                continuedWithoutOperatorResume = reacquired.CanAdvance,
                staleFieldReusedForMotion = false,
            },
            reacquired.EvidencePath,
            cancellationToken).ConfigureAwait(false);
        if (!reacquired.CanAdvance)
        {
            return new StageResult(
                GateResult.Unknown(
                    "PHD2_LOCK_RETURN_G3_REACQUISITION_BLOCKED",
                    $"The old PHD2 lock debt was safely returned and settled, but the single bounded fresh G3/PL3/slit rebuild did not pass: {reacquired.Gate.Code}: {reacquired.Gate.Message}"),
                reacquired.EvidencePath,
                reacquired.Metadata);
        }

        return null;
    }

    private async Task CapturePhd2HomeBoundaryBeforeCatalogSlewAsync(
        ObservationContext context, CancellationToken cancellationToken)
    {
        var initial = telescopeMediator.GetInfo();
        if (!initial.Connected || !initial.AtHome || initial.Slewing || initial.TrackingEnabled || initial.IsPulseGuiding)
            return;
        var expectedTelescopeId = initial.DeviceId;
        await EnsurePhdConnectedAsync(cancellationToken).ConfigureAwait(false);
        if (await phd2.GetAppStateAsync(cancellationToken).ConfigureAwait(false) != Phd2AppState.Stopped)
            return;
        await Task.Delay(750, cancellationToken).ConfigureAwait(false);
        var verified = telescopeMediator.GetInfo();
        if (!verified.Connected || !verified.AtHome || verified.Slewing || verified.TrackingEnabled || verified.IsPulseGuiding ||
            !string.Equals(expectedTelescopeId, verified.DeviceId, StringComparison.Ordinal))
            return;
        if (await phd2.GetAppStateAsync(cancellationToken).ConfigureAwait(false) != Phd2AppState.Stopped)
            return;
        var now = DateTimeOffset.UtcNow;
        var path = await PublishRunJsonEvidenceAsync(
            "phd2-new-run-verified-home-boundary",
            "New explicit run observed a stationary home before any catalogue slew; no home/motion command was issued by this check",
            new { context.Plan.ObservationRunId, verified.DeviceId, verified.AtHome, verified.Slewing,
                verified.TrackingEnabled, verified.IsPulseGuiding, verifiedUtc = now,
                phd2State = Phd2AppState.Stopped.ToString(), homeCommandIssued = false, budgetReset = false },
            null, cancellationToken).ConfigureAwait(false);
        phd2HomeBoundaryProof = new(context.Plan.ObservationRunId, verified.DeviceId, now, path);
    }

    private async Task<bool> RetireCancelledPhd2TargetAfterFreshNewFieldAsync(
        ObservationContext context,
        Phd2LockShiftPendingFileResult item,
        CancellationToken cancellationToken)
    {
        // A new target explicitly started after operator cancellation must not
        // inherit an old detector-pixel return vector after a catalogue slew.
        // Same-target resumes still take the original bounded recovery path.
        var source = await new ObservationRunJournalStore(
            Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(item.Path))!, "manifest.json"))
            .ReadAsync(cancellationToken).ConfigureAwait(false);
        if (source is null ||
            lastG3Field?.Gate.Disposition != GateDisposition.Passed ||
            lastG3Field.TargetIdentification.Target is null ||
            source.LockedMetadata.CommissioningPresetSha256 != commissioning!.Sha256)
            return false;
        var fieldBinding = await ValidateG3FieldMountBindingForMotionAsync(context, lastG3Field, cancellationToken).ConfigureAwait(false);
        if (fieldBinding.Disposition != GateDisposition.Passed) return false;
        var mount = telescopeMediator.GetInfo();
        if (!mount.Connected || mount.Slewing || mount.IsPulseGuiding ||
            source.LockedMetadata.Labels is null ||
            !source.LockedMetadata.Labels.TryGetValue("telescopeId", out var sourceTelescope) ||
            !string.Equals(sourceTelescope, mount.DeviceId, StringComparison.Ordinal))
            return false;
        var homeBoundary = phd2HomeBoundaryProof;
        var verifiedHomeRetirement = Phd2CancelledTargetRetirementPolicy.CanRetireAfterVerifiedHome(
            source.TerminalState, source.UpdatedUtc, homeBoundary?.VerifiedUtc, DateTimeOffset.UtcNow,
            homeBoundary is not null && homeBoundary.RunId == context.Plan.ObservationRunId &&
            string.Equals(homeBoundary.TelescopeId, sourceTelescope, StringComparison.Ordinal));
        if (!verifiedHomeRetirement && !Phd2CancelledTargetRetirementPolicy.CanRetire(
                source.TerminalState, source.Plan.Target, context.Plan.Target))
            return false;
        await EnsurePhdConnectedAsync(cancellationToken).ConfigureAwait(false);
        if (await phd2.GetAppStateAsync(cancellationToken).ConfigureAwait(false) != Phd2AppState.Stopped)
            return false;
        var identity = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), cancellationToken).ConfigureAwait(false);
        if (!identity.IsValid) return false;
        var prior = item.State!;
        var auditPath = await PublishRunJsonEvidenceAsync(
            "phd2-cancelled-target-retired",
            "Cancelled old-target lock intent archived after explicit new-target start and fresh optical acquisition; no return or science success claimed",
            new { priorState = prior, source.Plan.Target, source.TerminalState,
                newTarget = context.Plan.Target, newRunId = context.Plan.ObservationRunId,
                verifiedHomeRetirement, homeBoundary,
                freshField = lastG3Field.FramePath, fieldBinding,
                mount.DeviceId, mount.Slewing, mount.IsPulseGuiding,
                phd2State = phd2.Snapshot.AppState.ToString(),
                retiredAsFailure = true, oldCountersReset = false,
                oldOriginRewritten = false, returnMotionSent = false, oldTargetSuccess = false },
            lastG3Field.FramePath, cancellationToken).ConfigureAwait(false);
        await Phd2LockShiftPendingStore.WriteAtomicAsync(item.Path, prior with
        {
            Phase = Phd2LockShiftPendingPhase.RetiredCancelledObservation,
            UpdatedUtc = DateTimeOffset.UtcNow,
            LastReason = $"Cancelled observation retired by explicit run {context.Plan.ObservationRunId}; verified home boundary={verifiedHomeRetirement}; original budget/endpoints preserved, no return claimed. Audit: {auditPath}",
        }, cancellationToken).ConfigureAwait(false);
        Report(verifiedHomeRetirement
            ? "本轮转向前已验证赤道仪在零位、无跟踪且 PHD2 已停止，当前目标也已由新 G3 验证：旧取消运行的像素意图归档为未完成，不跨侧回放、不清零旧预算；本轮从新起点开始精调。"
            : "旧目标已取消，且新目标已由 fresh G3 验证：归档旧 PHD2 像素回程意图（未声称回程成功），原始位置和已用预算完整保留；新目标独立开始精调。");
        return true;
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
        var topologyResolution = ResolvePhd2RuntimeTopology(preset, pierSide);
        if (!topologyResolution.IsAllowed || topologyResolution.RuntimeTopology is null)
            return Attention(ObservationStage.PlaceTargetOnSlit, topologyResolution.Code, topologyResolution.Message);
        var topology = topologyResolution.RuntimeTopology;
        var topologySha256 = topology.ComputeFingerprintSha256();
        var zeroVectorAcrossPier = !string.Equals(state.ObservationRunId, context.Plan.ObservationRunId, StringComparison.Ordinal) &&
            Phd2ZeroVectorPierRecoveryPolicy.CanVerifyWithoutMotion(state, topology);
        if (!SameHash(topologySha256, state.TopologyFingerprintSha256) && !zeroVectorAcrossPier)
            return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RECOVERY_TOPOLOGY_MISMATCH", "Fresh PHD2 profile/camera/ROI/binning/rotation/install/mount/pier topology does not match the durable ledger. A meridian flip cannot reinterpret an outstanding lock-shift vector.");
        if (zeroVectorAcrossPier)
        {
            await PublishRunJsonEvidenceAsync(
                "phd2-zero-vector-pier-verification-declared",
                "Pier-only topology change permits fresh verification of an exactly zero return vector, never motion",
                new
                {
                    state.LineageId,
                    sourceTopologySha256 = state.TopologyFingerprintSha256,
                    currentTopologySha256 = topologySha256,
                    state.OriginLockX, state.OriginLockY,
                    state.CurrentLockX, state.CurrentLockY,
                    state.RequestedLockX, state.RequestedLockY,
                    state.AttemptsUsed, state.CumulativeCommandedPixels, state.StartedUtc,
                    returnMotionProhibited = true,
                    coordinateEqualityIsNotSettlementProof = true,
                    freshGuideTargetSlitProofStillRequired = true,
                }, state.LastFramePath, cancellationToken).ConfigureAwait(false);
        }

        // A PHD2 client epoch is process-local. After cancellation cleanup or
        // process restart, a numerically equal epoch is not continuity proof.
        // Establish a completely new, commissioned guide/settle epoch and
        // translate the old physical return vector into its fresh lock domain.
        if (phd2.Snapshot.AppState == Phd2AppState.Guiding)
        {
            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            await StopPhdAndWaitAsync(cancellationToken).ConfigureAwait(false);
        }
        // The immediately preceding G3 stage may already have produced the
        // exact current-run target/slit field intended for this handoff. Reuse
        // it only after re-hashing its immutable FITS and comparing its
        // capture-time mount binding with a fresh readback. Unconditionally
        // discarding that passed field forced a second 5/10/15 s PL3 ladder at
        // the same pointing; sparse target-on-slit fields then blocked durable
        // recovery even though the first field had already passed every gate.
        var reusableField = lastG3Field;
        var reusableFieldBinding = reusableField is
            {
                Gate.Disposition: GateDisposition.Passed,
                TargetIdentification.Target: not null,
            }
            ? await ValidateG3FieldMountBindingForMotionAsync(
                context,
                reusableField,
                cancellationToken).ConfigureAwait(false)
            : GateResult.Unknown(
                "PHD2_LOCK_RECOVERY_CURRENT_FIELD_UNAVAILABLE",
                "No passed current-run G3 target/slit field was available for durable recovery.");
        if (reusableField is not null && reusableFieldBinding.Disposition == GateDisposition.Passed)
        {
            lastG3Field = reusableField;
            await PublishRunJsonEvidenceAsync(
                "phd2-lock-recovery-current-field-reused",
                "Current passed G3 target/slit field retained for durable PHD2 recovery",
                new
                {
                    state.LineageId,
                    sourceObservationRunId = state.ObservationRunId,
                    currentObservationRunId = context.Plan.ObservationRunId,
                    reusableField.FramePath,
                    reusableField.Gate.Code,
                    reusableField.MountBinding,
                    target = reusableField.TargetIdentification.Target!.Centroid,
                    slit = reusableField.SlitDetection.Geometry.AcquisitionPoint,
                    targetSlitResidualPixels = PointDistance(
                        ToPhd2Domain(reusableField.TargetIdentification.Target.Centroid, preset),
                        ToPhd2Domain(reusableField.SlitDetection.Geometry.AcquisitionPoint, preset)),
                    fieldBindingGate = reusableFieldBinding.Code,
                    policy = "Reuse is allowed only for a passed current-run field whose immutable FITS hash, run/config/commissioning binding, epoch, pier and fresh mount readback all validate.",
                },
                reusableField.FramePath,
                cancellationToken).ConfigureAwait(false);
            Report("PHD2 旧锁点恢复复用刚刚通过质量门且重新验证过赤道仪绑定的 G3 场；不重复执行 PL3 曝光阶梯");
        }
        else
        {
            // After a process restart, a stale mount binding, or a missing
            // current field, durable recovery still needs a new catalogue/WCS
            // target rather than only 10/20 ms LED/OFF morphology. This is a
            // no-motion PL3 reacquisition; it remains the conservative fallback.
            lastG3Field = await CaptureAndAnalyzeG3WithSolveLadderAsync(
                context,
                cancellationToken).ConfigureAwait(false);
        }
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
            !HasSupervisedScienceOptIn())
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
        (GuideStarSelection Selection, Phd2Point Requested, Phd2Point Selected) guideSelectionResult;
        try
        {
            guideSelectionResult = await SelectFreshPhd2GuideAsync(
                guideChoice,
                preset,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Phd2NativeGuideSelectionExhaustedException exhausted)
        {
            await StopPhdAndWaitAsync(CancellationToken.None).ConfigureAwait(false);
            return Attention(
                ObservationStage.PlaceTargetOnSlit,
                "PHD2_LOCK_RECOVERY_NATIVE_GUIDE_EXHAUSTED",
                $"Durable exact-lock recovery retained its lineage and budget, but bounded PHD2-native guide selection was exhausted. Guide-mode substitution is prohibited while return debt exists. {exhausted.Message}");
        }
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
        // Selection looping can emit ConfigurationChange and invalidate only
        // the cached calibration attestation.  Match the normal placement path
        // by re-reading calibration at the last possible point before guide.
        activeCalibrationBeforeGuide = await phd2.ValidateCalibrationAsync(
            preset.CalibrationQualityPolicy.ApplyHardRejectionCeilings(PhdCalibrationRequirement()),
            cancellationToken).ConfigureAwait(false);
        if (activeCalibrationBeforeGuide.Status != Phd2ValidationStatus.Valid)
        {
            await StopPhdAndWaitAsync(cancellationToken).ConfigureAwait(false);
            return Attention(
                ObservationStage.PlaceTargetOnSlit,
                "PHD2_LOCK_RECOVERY_LAST_MOMENT_CALIBRATION_INVALID",
                $"The final calibration readback after selection looping is invalid; guiding was checked-stopped and no guide command was sent: {string.Join(" ", activeCalibrationBeforeGuide.Failures.Concat(activeCalibrationBeforeGuide.IndeterminateReasons))}");
        }
        Volatile.Write(ref phd2GuidingEverStarted, 1);
        var settle = await phd2.GuideAndSettleAsync(
            Phd2SettleCriteriaForSlitPlacement(preset),
            false,
            guideSelectionRoi,
            preserveSameEpochGuidingOnSettleTimeout: HasSupervisedScienceOptIn(),
            cancellationToken).ConfigureAwait(false);
        var snapshot = phd2.Snapshot;
        var windSampledSettle = CanReplaceSettleWithFreshGuidingWindow(settle, snapshot);
        if ((!settle.Succeeded && !windSampledSettle) ||
            (settle.Succeeded && !snapshot.HasCurrentSuccessfulSettle))
            return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RECOVERY_SETTLE_FAILED", settle.Error ?? "A fresh locally issued guide/settle epoch was not attested.");
        if (windSampledSettle)
            Report("warning：海风导致 PHD2 未进入 settle 圈；恢复路径保持同一 Guiding epoch，并改取 fresh GuideStep/FITS 窗口复核");

        var calibration = await phd2.ValidateCalibrationAsync(
            preset.CalibrationQualityPolicy.ApplyHardRejectionCeilings(PhdCalibrationRequirement()),
            cancellationToken).ConfigureAwait(false);
        if (calibration.Status != Phd2ValidationStatus.Valid)
            return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RECOVERY_CALIBRATION_INVALID", string.Join(" ", calibration.Failures.Concat(calibration.IndeterminateReasons)));
        var freshLockReadback = await phd2.GetLockPositionWithSameEpochRetryAsync(
            snapshot.ConnectionEpoch,
            snapshot.GuideEpoch,
            maximumAttempts: 3,
            cancellationToken).ConfigureAwait(false);
        if (!freshLockReadback.SameGuideEpoch)
        {
            return Attention(
                ObservationStage.PlaceTargetOnSlit,
                "PHD2_LOCK_RECOVERY_EPOCH_CHANGED_DURING_READBACK",
                $"The fresh recovery guide epoch changed during bounded lock readback after {freshLockReadback.Attempts} read-only attempt(s); no lock command was sent.");
        }
        var freshLock = freshLockReadback.Position;
        if (freshLock is null)
        {
            return Attention(
                ObservationStage.PlaceTargetOnSlit,
                "PHD2_LOCK_RECOVERY_POSITION_UNKNOWN",
                $"The new guide epoch reported no runtime lock position after {freshLockReadback.Attempts} bounded read-only attempts; no guide or lock command was retried.");
        }
        var freshMeasurements = await CapturePhd2GuidingMeasurementsAsync(
            context,
            preset,
            topology,
            freshLock,
            ToPhd2Domain(lastG3Field.TargetIdentification.Target!.Centroid, preset),
            lastG3Field.SlitDetection.Geometry,
            guideChoice.Mode,
            windSampledSettle
                ? Math.Max(3, preset.CalibrationQualityPolicy.RequiredFreshResidualsPerLockShiftStage)
                : preset.CalibrationQualityPolicy.RequiredFreshResidualsPerLockShiftStage,
            cancellationToken).ConfigureAwait(false);
        var initial = freshMeasurements[^1];
        // The accepted guiding residual is newer than the selection frame and
        // carries its own capture-time mount binding.  Promote it immediately
        // so every subsequent recovery/return authorization validates the
        // fresh frame instead of accumulating normal readback drift against a
        // much older guide-selection frame.
        lastG3Field = UpdateG3FieldFromGuidingResidual(lastG3Field, initial, preset);
        var qualitySelection = SelectPhd2CalibrationQuality(
            calibration,
            preset,
            Phd2CalibrationEvaluationPhase.PostSettle,
            CreateCalibrationSettleEvidence(settle, snapshot, windSampledSettle, freshMeasurements.Count),
            CreateCalibrationResidualEvidence(initial, PointDistance(initial.Measurement.GuideStar, freshLock), preset, topology, guideChoice.Mode),
            Phd2CalibrationSelectionPurpose.LockShift);
        if (qualitySelection.Selected?.IsLockShiftAuthority != true)
            return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RECOVERY_ACTIVE_CALIBRATION_REJECTED", CalibrationSelectionMessage(qualitySelection));
        if (RequiresSupervisedPhd2Science(qualitySelection.Selected, guideChoice.Mode) &&
            !HasSupervisedScienceOptIn())
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
        var foreignRun = !string.Equals(
            state.ObservationRunId,
            context.Plan.ObservationRunId,
            StringComparison.Ordinal);
        var foreignEndpointProof = foreignRun
            ? Phd2ForeignRecoveryEndpointPolicy.Evaluate(state, proofTolerance)
            : null;
        var currentRequestedLockError = foreignEndpointProof?.CurrentRequestedLockErrorPixels
            ?? PointDistance(storedCurrentLock, storedRequestedLock);
        var foreignEndpointPhaseProven = foreignEndpointProof?.EndpointPhaseProven ?? false;
        var foreignEndpointTranslationAuthorized = foreignEndpointProof?.Gate.Disposition == GateDisposition.Passed;

        Phd2Point provenOldEndpoint;
        if (foreignRun)
        {
            // Absolute detector positions from the old run describe its target
            // (for example Vega), not the newly selected target (for example
            // Deneb). They therefore cannot prove or disprove the new field.
            // The accepted post-dispatch/checked-return phases are useful only
            // when CurrentLock equals RequestedLock: ambiguous command windows
            // deliberately retain different values. Equality closes the
            // remaining crash-window ambiguity. The origin-current vector is
            // translation invariant across a fresh guide-star/target epoch.
            if (!foreignEndpointTranslationAuthorized)
            {
                return new StageResult(foreignEndpointProof!.Gate, initial.Frame.Path);
            }
            provenOldEndpoint = foreignEndpointProof!.ProvenEndpoint!;
        }
        else
        {
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
            if (currentMatches && requestedMatches && currentRequestedLockError > 2 * proofTolerance)
                return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RECOVERY_ENDPOINT_AMBIGUOUS", "Fresh target evidence cannot uniquely distinguish the two durable crash-window endpoints.");
            provenOldEndpoint = requestedMatches && (!currentMatches || requestedFit < currentFit)
                ? storedRequestedLock
                : storedCurrentLock;
        }
        var returnDelta = SubtractPoint(storedOriginLock, provenOldEndpoint);
        var translatedOrigin = AddPoint(freshLock, returnDelta);
        var initialTarget = observedTarget;
        var runtimeSlit = initial.RuntimeSlitLocal;
        var requiredFreshResiduals = qualitySelection.Selected.RequiredFreshResidualsPerLockShiftStage;

        await PublishRunJsonEvidenceAsync(
            "phd2-lock-recovery-endpoint-proof",
            foreignRun
                ? "Foreign PHD2 endpoint proven by durable verified readback for translated return"
                : "Current-run PHD2 endpoint proven by fresh absolute target/slit evidence",
            new
            {
                state.LineageId,
                sourceObservationRunId = state.ObservationRunId,
                currentObservationRunId = context.Plan.ObservationRunId,
                foreignRun,
                sourcePhase = state.Phase.ToString(),
                foreignEndpointPhaseProven,
                currentRequestedLockError,
                proofTolerance,
                foreignEndpointTranslationAuthorized,
                storedOriginLock,
                storedCurrentLock,
                storedRequestedLock,
                provenOldEndpoint,
                returnDelta,
                translatedOrigin,
                freshLock,
                observedTarget,
                observedSlit,
                oldAbsoluteTargetAndSlitUsedForForeignProof = false,
                motionBudgetReset = false,
                authority = foreignRun
                    ? "Verified old CurrentLock==RequestedLock in a post-dispatch durable phase; fresh identity/topology/calibration, target/slit field, mount binding and guide epoch; translated origin-current vector."
                    : "Fresh same-run target/slit geometry distinguishes the durable current/requested endpoint.",
            },
            initial.Frame.Path,
            cancellationToken).ConfigureAwait(false);

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
            recoveryEpisodeStartedUtc: DateTimeOffset.UtcNow,
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
                var sameEpochSlitStabilityError = PointDistance(
                    final.Measurement.RecognizedSlitAcquisitionPoint,
                    observedSlit);
                var exactOriginLockError = PointDistance(verifiedLock, translatedOrigin);
                var initialTargetLocal = ToFrameLocal(initialTarget, preset);
                var finalTargetLocal = ToFrameLocal(final.Measurement.TargetCentroid, preset);
                var fieldTranslation = Phd2RecoveryFieldTranslationPolicy.Evaluate(
                    initial.Candidates,
                    final.Candidates,
                    new Phd2Point(initialTargetLocal.X, initialTargetLocal.Y),
                    new Phd2Point(finalTargetLocal.X, finalTargetLocal.Y),
                    new Phd2Point(initial.RuntimeSlitLocal.AcquisitionPoint.X, initial.RuntimeSlitLocal.AcquisitionPoint.Y),
                    new Phd2Point(final.RuntimeSlitLocal.AcquisitionPoint.X, final.RuntimeSlitLocal.AcquisitionPoint.Y),
                    returnDelta,
                    proofTolerance);
                var returnVerification = Phd2RecoveryReturnVerificationPolicy.Evaluate(
                    foreignRun,
                    returnDelta,
                    measuredDelta,
                    exactOriginLockError,
                    originSlitError,
                    sameEpochSlitStabilityError,
                    final.Measurement.TargetIdentityConfirmed,
                    fieldTranslation,
                    proofTolerance);
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
                        exactOriginLockError,
                        originTargetError,
                        originSlitError,
                        sameEpochSlitStabilityError,
                        proofTolerance,
                        foreignRun,
                        foreignEndpointTranslationAuthorized,
                        fieldTranslation,
                        returnVerification.NoMotionReturn,
                        returnVerification.ExactOriginLockVerified,
                        returnVerification.TargetVectorVerified,
                        returnVerification.FieldTranslationVerified,
                        returnVerification.SlitVerified,
                        returnVerification.FreshSlitReacquisitionRequired,
                        oldAbsoluteTargetAndSlitUsedForForeignProof = false,
                        authoritativeChecks = foreignRun
                            ? "exact-lock readback + operation-bound settle + fresh target identity + target vector or >=3-star ordinary-field common translation; unstable slit fits may close only the old return debt and mandate checked-stop plus fresh G3/PL3/slit reacquisition before new placement; zero-distance reconciliation requires the same fresh proof without a motion command"
                            : "exact-lock readback + operation-bound settle + fresh target identity + durable slit reproduction + target vector or ordinary-star common translation; zero-distance reconciliation requires no new motion response",
                        final.Measurement,
                    },
                    final.Frame.Path,
                    token).ConfigureAwait(false);
                lastG3Field = UpdateG3FieldFromGuidingResidual(lastG3Field!, final, preset);
                return returnVerification.Gate;
            }, prohibitReturnMotion: zeroVectorAcrossPier).ConfigureAwait(false);
    }

    private async Task<StageResult> PlaceTargetOnSlitWithPhd2Async(
        ObservationContext context,
        CancellationToken cancellationToken,
        int postCalibrationReacquisitionDepth = 0,
        int lostLockReacquisitionDepth = 0)
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
        var topologyResolution = ResolvePhd2RuntimeTopology(preset, pierSide);
        if (!topologyResolution.IsAllowed || topologyResolution.RuntimeTopology is null)
            return Attention(ObservationStage.PlaceTargetOnSlit, topologyResolution.Code, topologyResolution.Message);
        var topology = topologyResolution.RuntimeTopology;

        var policy = preset.CalibrationQualityPolicy;
        if (preset.GuideMode == Phd2SlitGuideMode.DegradedDirectTargetGuiding &&
            !HasSupervisedScienceOptIn())
        {
            return Attention(
                ObservationStage.PlaceTargetOnSlit,
                "PHD2_DIRECT_TARGET_SUPERVISION_OPT_IN_REQUIRED",
                "The commissioned guide mode is degraded direct-target guiding. Explicit supervised-science opt-in is required before any guide or lock command.");
        }
        var hardRequirement = policy.ApplyHardRejectionCeilings(PhdCalibrationRequirement());
        var calibrationBefore = await phd2.ValidateCalibrationAsync(hardRequirement, cancellationToken).ConfigureAwait(false);
        var refreshAgedCalibration = Phd2CalibrationRefreshPolicy.ShouldRefresh(
            calibrationBefore.CalibrationAge, calibrationBefore.OrthogonalityErrorDegrees,
            policy.QualifiedMaximumAge, policy.QualifiedMaximumOrthogonalityErrorDegrees,
            phd2AgedCalibrationRefreshAttempted, postCalibrationReacquisitionDepth > 0,
            pendingPhd2LockShift is { Phase: not Phd2LockShiftPendingPhase.SettledBudgetLedger });
        if (refreshAgedCalibration)
        {
            phd2AgedCalibrationRefreshAttempted = true;
            await PublishRunJsonEvidenceAsync(
                "phd2-aged-calibration-refresh-intent",
                "One native recalibration requested for an aged calibration with poor measured axis geometry",
                new { calibrationBefore.CalibrationAge, calibrationBefore.OrthogonalityErrorDegrees,
                    policy.QualifiedMaximumAge, qualifiedOrthogonalityLimit = policy.QualifiedMaximumOrthogonalityErrorDegrees,
                    maximumRefreshAttempts = 1, exactLockCommandIssued = false, budgetReset = false },
                lastG3Field.FramePath, cancellationToken).ConfigureAwait(false);
            Report($"PHD2 旧标定已超合格年龄，且两轴不正交误差 {calibrationBefore.OrthogonalityErrorDegrees:F1}°；执行本轮唯一一次原生重标定，随后重新取场。");
        }
        var forceRecalibration = calibrationBefore.Status != Phd2ValidationStatus.Valid || refreshAgedCalibration;
        if (forceRecalibration && postCalibrationReacquisitionDepth > 0)
        {
            return Attention(
                ObservationStage.PlaceTargetOnSlit,
                "PHD2_RECALIBRATION_DID_NOT_BECOME_ACTIVE",
                "PHD2 still reports an invalid active calibration after the one allowed calibration/reacquisition cycle; no further guide, exposure or lock command is sent.");
        }
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
                !HasSupervisedScienceOptIn())
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
            !HasSupervisedScienceOptIn())
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
            (GuideStarSelection Selection, Phd2Point Requested, Phd2Point Selected) guideSelectionResult;
            try
            {
                guideSelectionResult = await SelectFreshPhd2GuideAsync(
                    guideChoice,
                    preset,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Phd2NativeGuideSelectionExhaustedException exhausted)
            {
                await StopPhdAndWaitAsync(CancellationToken.None).ConfigureAwait(false);
                if (preset.GuideMode != Phd2SlitGuideMode.AutoPreferOffSlitThenDirectTarget ||
                    guideChoice.Mode != Phd2SlitGuideMode.OffSlitGuideStar)
                {
                    return Attention(
                        ObservationStage.PlaceTargetOnSlit,
                        "PHD2_OFF_SLIT_NATIVE_SELECTION_EXHAUSTED",
                        $"Strict off-slit guiding stopped after bounded PHD2-native selection was exhausted; no coordinator-ranked substitute, guide, lock or mount command was sent. {exhausted.Message}");
                }
                if (pendingPhd2LockShift is { Phase: not Phd2LockShiftPendingPhase.SettledBudgetLedger })
                {
                    return Attention(
                        ObservationStage.PlaceTargetOnSlit,
                        "PHD2_DIRECT_TARGET_FALLBACK_MOTION_STATE_UNSAFE",
                        "PHD2-native off-slit selection was exhausted while an unreturned durable lock lineage exists. Guide-mode fallback is prohibited until the exact-lock origin is reconciled.");
                }
                if (!HasSupervisedScienceOptIn())
                {
                    return Attention(
                        ObservationStage.PlaceTargetOnSlit,
                        "PHD2_DIRECT_TARGET_SUPERVISION_OPT_IN_REQUIRED",
                        "PHD2-native off-slit selection was exhausted. Direct-target fallback requires explicit supervised-science opt-in; PHD2 was checked-stopped and no guide, lock or mount command was sent.");
                }

                var fallback = await PrepareDirectTargetFallbackAfterNativeExhaustionAsync(
                    context,
                    guideChoice,
                    preset,
                    exhausted,
                    cancellationToken).ConfigureAwait(false);
                guideChoice = fallback.Choice;
                lastG3Field = guideChoice.Field;
                initialTarget = guideChoice.Field.TargetIdentification.Target
                    ?? throw new InvalidOperationException("Fresh direct-target fallback frame passed without a target identity.");
                loop = fallback.Loop;
                guideSelectionResult = (fallback.Selection, fallback.Requested, fallback.Selected);
            }
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
            // The full-frame selection loop may emit ConfigurationChange and
            // therefore invalidate only the cached calibration attestation.
            // Re-read the actual calibration immediately before guide.  If
            // the hardware readback is valid, continue without recalibration;
            // if it is genuinely invalid, use the already-bounded forced
            // recalibration + fresh-G3 path instead of falling into the outer
            // non-LostLock failure handler.
            calibrationBefore = await phd2.ValidateCalibrationAsync(
                hardRequirement,
                cancellationToken).ConfigureAwait(false);
            forceRecalibration = calibrationBefore.Status != Phd2ValidationStatus.Valid || refreshAgedCalibration;
            if (forceRecalibration && postCalibrationReacquisitionDepth > 0)
            {
                await StopPhdAndWaitAsync(cancellationToken).ConfigureAwait(false);
                return Attention(
                    ObservationStage.PlaceTargetOnSlit,
                    "PHD2_RECALIBRATION_DID_NOT_BECOME_ACTIVE",
                    "The last-moment calibration readback is still invalid after the one allowed calibration/reacquisition cycle; guiding was checked-stopped and no further command was sent.");
            }
            if (forceRecalibration)
            {
                identity = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), cancellationToken).ConfigureAwait(false);
                if (!identity.IsValid)
                    throw new Phd2IdentityMismatchException(identity);
            }
            Volatile.Write(ref phd2GuidingEverStarted, 1);
            var recalibrationStartedUtc = forceRecalibration ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;
            var settle = await phd2.GuideAndSettleAsync(
                Phd2SettleCriteriaForSlitPlacement(preset),
                forceRecalibration,
                guideSelectionRoi,
                preserveSameEpochGuidingOnSettleTimeout: HasSupervisedScienceOptIn(),
                cancellationToken).ConfigureAwait(false);
            var guideProof = phd2.Snapshot;
            var windSampledSettle = CanReplaceSettleWithFreshGuidingWindow(settle, guideProof);
            if (!settle.Succeeded && !windSampledSettle)
                throw new InvalidOperationException(settle.Error ?? "PHD2 guide/settle failed.");
            if (settle.Succeeded && !guideProof.HasCurrentSuccessfulSettle)
                throw new InvalidOperationException("The locally issued guide operation did not leave a same-epoch successful settle attestation.");
            if (windSampledSettle)
                Report("warning：海风导致 PHD2 未进入 settle 圈；保持同一 Guiding epoch，改取 fresh GuideStep/FITS 窗口评估");

            var calibration = await phd2.ValidateCalibrationAsync(
                policy.ApplyHardRejectionCeilings(PhdCalibrationRequirement(
                    recalibrationStartedUtc)),
                cancellationToken).ConfigureAwait(false);
            if (calibration.Status != Phd2ValidationStatus.Valid)
            {
                throw new InvalidOperationException(
                    $"PHD2 calibration failed the policy hard rejection ceilings: {string.Join(" ", calibration.Failures.Concat(calibration.IndeterminateReasons))}");
            }

            if (forceRecalibration)
            {
                localPhd2CalibrationProof = (calibration.Calibration, phd2.Snapshot.ConnectionEpoch, recalibrationStartedUtc!.Value);
                // Calibration pulses invalidate every pre-calibration target,
                // slit and mount binding even when PHD2 normally returns very
                // close to its origin. This is the ordering used by both
                // unattended on-sky successes: stop the calibration guide,
                // reacquire fresh immutable G3/PL3 evidence, allow the normal
                // bounded WCS/overlapping-neighbour route to restore the field
                // if necessary, then enter placement again with the now-active
                // calibration. No exact-lock command has been issued yet.
                var calibrationReacquisitionEvidence = await PublishRunJsonEvidenceAsync(
                    "phd2-post-calibration-g3-reacquisition",
                    "PHD2 recalibration completed; pre-calibration G3 geometry was invalidated",
                    new
                    {
                        calibrationBefore.Status,
                        calibration.EvaluatedUtc,
                        calibration.OrthogonalityErrorDegrees,
                        calibration.Calibration.RaRatePixelsPerSecond,
                        calibration.Calibration.DecRatePixelsPerSecond,
                        preCalibrationFrame = lastG3Field?.FramePath,
                        exactLockCommandIssued = false,
                        nextAuthority = "fresh immutable G3 FITS + formal PL3 + runtime slit midpoint",
                    },
                    lastG3Field?.FramePath,
                    cancellationToken).ConfigureAwait(false);
                await CheckpointAndRejectStaleStageStackAsync(context, cancellationToken).ConfigureAwait(false);
                await StopPhdAndWaitAsync(cancellationToken).ConfigureAwait(false);
                var calibrationStopEvidence = await PublishRunJsonEvidenceAsync(
                    "phd2-calibration-stop-confirmed", "Owned native recalibration checked-stopped before fresh acquisition",
                    new { guideProof.ConnectionEpoch, stoppedConnectionEpoch = phd2.Snapshot.ConnectionEpoch,
                        stoppedGuideEpoch = phd2.Snapshot.GuideEpoch, state = phd2.Snapshot.AppState.ToString(),
                        originalBudgetsPreserved = true, freshFieldStillRequired = true },
                    calibrationReacquisitionEvidence, cancellationToken).ConfigureAwait(false);
                var calibrationStop = new Phd2DependencyRebuildStopProof(
                    guideProof.ConnectionEpoch, phd2.Snapshot.GuideEpoch, calibrationStopEvidence);
                if (!calibrationStop.IsCurrent(phd2.Snapshot))
                    return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_REBUILD_STOP_UNCONFIRMED",
                        "本轮原生标定后的停止状态或连接身份未确认；未开始重新取场。");
                automaticRebuildStopProof = calibrationStop;
                lastG3Field = null;
                var reacquired = await AcquireG3SlitFieldAsync(
                    context,
                    cancellationToken,
                    allowChargedCurrentPositionHandoff: true).ConfigureAwait(false);
                if (!reacquired.CanAdvance)
                {
                    return new StageResult(
                        GateResult.Unknown(
                            "POST_CALIBRATION_G3_REACQUISITION_BLOCKED",
                            $"PHD2 recalibration passed, but the mandatory fresh G3 acquisition route did not: {reacquired.Gate.Code}: {reacquired.Gate.Message}"),
                        reacquired.EvidencePath,
                        reacquired.Metadata);
                }
                return await PlaceTargetOnSlitWithPhd2Async(
                    context,
                    cancellationToken,
                    postCalibrationReacquisitionDepth + 1,
                    lostLockReacquisitionDepth).ConfigureAwait(false);
            }

            var lockOrigin = await phd2.GetLockPositionAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("PHD2 did not report a runtime lock position after settle.");
            var initialTargetDomain = ToPhd2Domain(initialTarget.Centroid, preset);
            var initialSlitLocal = lastG3Field.SlitDetection.Geometry;
            var expectedTarget = initialTargetDomain;
            var firstMeasurements = await CapturePhd2PlacementGuideWindowAsync(
                context,
                preset,
                topology,
                lockOrigin,
                expectedTarget,
                initialSlitLocal,
                guideChoice.Mode,
                windSampledSettle
                    ? Math.Max(3, policy.RequiredFreshResidualsPerLockShiftStage)
                    : policy.RequiredFreshResidualsPerLockShiftStage,
                DateTimeOffset.UtcNow.AddSeconds(preset.MaximumStageSeconds),
                cancellationToken).ConfigureAwait(false);
            var first = firstMeasurements[^1];
            // Motion planning must be bound to the residual that actually
            // authorized it.  Keeping lastG3Field on the older guide-selection
            // FITS made the strict 2-arcsec mount freshness gate age for the
            // entire multi-frame residual window and caused a false stale-frame
            // rebuild immediately before the first lock shift.
            lastG3Field = UpdateG3FieldFromGuidingResidual(lastG3Field, first, preset);
            var firstGuideResidual = PointDistance(first.Measurement.GuideStar, lockOrigin);
            var residualEvidence = CreateCalibrationResidualEvidence(first, firstGuideResidual, preset, topology, guideChoice.Mode);
            var settleEvidence = CreateCalibrationSettleEvidence(settle, guideProof, windSampledSettle, firstMeasurements.Count);
            var postGuide = SelectPhd2CalibrationQuality(
                calibration,
                preset,
                Phd2CalibrationEvaluationPhase.PostSettle,
                settleEvidence,
                windSampledSettle && guideChoice.Mode == Phd2SlitGuideMode.DegradedDirectTargetGuiding
                    ? CreateCalibrationResidualEvidence(
                        first,
                        firstGuideResidual,
                        preset,
                        topology,
                        guideChoice.Mode,
                        Math.Sqrt((double)preset.SensorWidthPixels * preset.SensorWidthPixels + (double)preset.SensorHeightPixels * preset.SensorHeightPixels))
                    : residualEvidence,
                Phd2CalibrationSelectionPurpose.LockShift);
            var quality = postGuide.Selected;
            if (quality?.IsLockShiftAuthority != true)
            {
                throw new InvalidOperationException(
                    $"Post-settle calibration quality does not authorize a lock shift: {CalibrationSelectionMessage(postGuide)}");
            }
            if (RequiresSupervisedPhd2Science(quality, guideChoice.Mode) && !HasSupervisedScienceOptIn())
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
                // The first fresh residual is the evidence that authorizes the
                // first stage; it has not already been consumed. Seeding the
                // ledger with that same hash made the planner reject its own
                // initial measurement as G3_FRAME_REUSED. Only an inherited,
                // previously settled lineage contributes an already-consumed
                // frame hash. Each dispatched stage consumes the frame that
                // authorized that motion. The genuinely new post-stage frame
                // must remain unconsumed so it can authorize the next stage.
                inheritedSettledBudget?.LastAcceptedFrameSha256);
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
                forceRecalibration,
                windSampledSettle);
            phd2SlitPlacementSession = session;

            var priorResidual = PointDistance(first.Measurement.TargetCentroid, first.Measurement.RecognizedSlitAcquisitionPoint);
            IReadOnlyList<Phd2GuidingResidualState> targetCompletionWindow = firstMeasurements;
            var completionWindowRetries = 0;
            var transientResidualGrowthWarning = false;
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
                var completionTolerance = preset.BuildMotionLimits().TargetOnSlitTolerancePixels *
                    session.Quality.RequiredResidualToleranceScale;
                var requiredCompletionFrames = Math.Max(3, session.Quality.RequiredFreshResidualsPerLockShiftStage);
                var completionResiduals = targetCompletionWindow.Select(item => PointDistance(
                    item.Measurement.TargetCentroid, item.Measurement.RecognizedSlitAcquisitionPoint)).ToArray();
                // A same-frame guide error can already explain the target error:
                // the existing lock is the desired destination. Wait for native
                // guiding instead of sending another shift or immediately tearing
                // down the guide session. This does not make the denied plan valid.
                var nativeCorrectionPending = !plan.IsAllowed &&
                    string.Equals(plan.Code, "FRESH_G3_RESIDUAL_REQUIRED", StringComparison.Ordinal);
                var completionWindowUnstable = plan.IsAllowed && plan.IsComplete &&
                    (targetCompletionWindow.Count < requiredCompletionFrames ||
                     !Phd2PlacementGuideWindowPolicy.AllWithinTolerance(completionResiduals, completionTolerance));
                var measuredSupervisedGeometry = HasSupervisedScienceOptIn() &&
                    targetCompletionWindow.All(item => item.Measurement.GuidePositionMeasuredInFrame &&
                        item.Measurement.TargetIdentityConfirmed &&
                        item.Measurement.TargetPositionAuthority != Phd2TargetPositionAuthority.CatalogWcsProjection);
                var canWaitWithoutNewMotion = Phd2PlacementGuideWindowPolicy.CanWaitWithoutNewMotion(plan.IsAllowed, plan.Code);
                // Near the slit, do not spend another exact-lock action chasing
                // each wind/seeing sample. This envelope authorizes only waiting,
                // never science or additional movement. Acceptance stays exact.
                var nearSlitTrackingWarning = measuredSupervisedGeometry &&
                    canWaitWithoutNewMotion && !plan.IsComplete &&
                    Phd2PlacementGuideWindowPolicy.AllWithinTolerance(completionResiduals,
                        completionTolerance + preset.MaximumResidualGrowthPixels);
                var slitApertureResiduals = targetCompletionWindow.Select(item =>
                    Phd2PlacementGuideWindowPolicy.ProjectOnMeasuredSlit(
                        new PixelPoint(item.Measurement.TargetCentroid.X, item.Measurement.TargetCentroid.Y),
                        item.RuntimeSlitLocal)).ToArray();
                var alongSlitPrecisionWarning = Phd2PlacementGuideWindowPolicy.CanProbeAlongSlitWithPrecisionWarning(
                    configuration.AllowSupervisedSlitQualityWarning, measuredSupervisedGeometry,
                    slitApertureResiduals, completionTolerance, preset.MaximumResidualGrowthPixels,
                    preset.MaximumAcquisitionResidualPixels);
                var supervisedSlitPrecisionWarning = canWaitWithoutNewMotion &&
                    !Phd2PlacementGuideWindowPolicy.AllWithinTolerance(completionResiduals, completionTolerance) &&
                    (alongSlitPrecisionWarning || Phd2PlacementGuideWindowPolicy.CanProbeWithPrecisionWarning(
                        configuration.AllowSupervisedSlitQualityWarning, measuredSupervisedGeometry,
                        completionResiduals, completionTolerance, preset.MaximumResidualGrowthPixels,
                        preset.MaximumAcquisitionResidualPixels));
                if (!supervisedSlitPrecisionWarning && (nativeCorrectionPending || completionWindowUnstable ||
                    nearSlitTrackingWarning || (transientResidualGrowthWarning && canWaitWithoutNewMotion)))
                {
                    var completionLimits = preset.BuildMotionLimits();
                    var recoveryDistanceUpper = PointDistance(ledger.OriginLockPosition, ledger.CurrentLockPosition) +
                        completionLimits.LockVerificationTolerancePixels;
                    var reservedReturnAttempts = recoveryDistanceUpper <= completionLimits.LockVerificationTolerancePixels
                        ? 0
                        : (int)Math.Ceiling(recoveryDistanceUpper /
                            (completionLimits.MaximumStagePixels - completionLimits.LockVerificationTolerancePixels));
                    var completionDeadline = ledger.StartedUtc + completionLimits.MaximumElapsed -
                        TimeSpan.FromTicks(completionLimits.MaximumStageDuration.Ticks * reservedReturnAttempts);
                    if (DateTimeOffset.UtcNow + completionLimits.MaximumStageDuration > completionDeadline)
                    {
                        const string reason = "PHD2_COMPLETION_WINDOW_RETURN_RESERVE: A further read-only completion window would consume the original worst-case return time; no new exposure or lock mutation was dispatched.";
                        if (pendingPhd2LockShift is { } returnReserved)
                            return await ReturnPhd2LockToOriginAsync(context, session, returnReserved, reason, cancellationToken).ConfigureAwait(false);
                        throw new InvalidOperationException(reason);
                    }
                    // Keep sampling while the original deadline still reserves
                    // the full return. Four windows are not a physical safety
                    // boundary and must not force an early wind-induced return.
                    completionWindowRetries++;
                    Report(transientResidualGrowthWarning || nearSlitTrackingWarning
                        ? "导星扰动警告：保持当前锁点和原生导星，先补取整组新帧；不因短时残差增长立即回程，也不追加追逐扰动的移锁。"
                        : nativeCorrectionPending
                        ? "目标偏差与原生导星偏差相符，现有锁点无需重复移动；保持 PHD2 导星并有界补取新帧，等待实际纠偏。"
                        : "最后一帧已接近狭缝，但整组尚未稳定；保持导星补取新帧，必要时在原账本内继续精调，不提前宣布入缝完成。");
                    targetCompletionWindow = await CapturePhd2PlacementGuideWindowAsync(
                        context, preset, topology, ledger.CurrentLockPosition,
                        session.LastMeasurement.Measurement.TargetCentroid,
                        session.LastMeasurement.RuntimeSlitLocal, session.GuideMode,
                        requiredCompletionFrames, completionDeadline,
                        cancellationToken).ConfigureAwait(false);
                    var completionMeasurement = targetCompletionWindow[^1];
                    session = session with { LastMeasurement = completionMeasurement };
                    phd2SlitPlacementSession = session;
                    lastG3Field = UpdateG3FieldFromGuidingResidual(lastG3Field, completionMeasurement, preset);
                    priorResidual = PointDistance(completionMeasurement.Measurement.TargetCentroid,
                        completionMeasurement.Measurement.RecognizedSlitAcquisitionPoint);
                    await PublishRunJsonEvidenceAsync("phd2-target-completion-window",
                        "Whole-window slit completion rechecked before committing the settled ledger",
                        new
                        {
                            completionWindowRetries, boundedByOriginalDeadline = true, nativeCorrectionPending,
                            nearSlitTrackingWarning, transientResidualGrowthWarning,
                            completionDeadline, reservedReturnAttempts,
                            plannerCode = plan.Code,
                            residuals = targetCompletionWindow.Select(item => PointDistance(
                                item.Measurement.TargetCentroid, item.Measurement.RecognizedSlitAcquisitionPoint)).ToArray(),
                            tolerance = completionTolerance, budgetReset = false,
                            newLockMotion = false, nextAction = "replan-from-last-real-fresh-frame",
                        }, completionMeasurement.Frame.Path, cancellationToken).ConfigureAwait(false);
                    transientResidualGrowthWarning = false;
                    continue;
                }
                if (!plan.IsAllowed && !supervisedSlitPrecisionWarning)
                {
                    if (pendingPhd2LockShift is { } outstanding)
                    {
                        return await ReturnPhd2LockToOriginAsync(
                            context, session,
                            outstanding with { Phase = Phd2LockShiftPendingPhase.ReturnRequired },
                            $"{plan.Code}: {plan.Message}", cancellationToken).ConfigureAwait(false);
                    }
                    throw new InvalidOperationException($"{plan.Code}: {plan.Message}");
                }
                if (plan.IsComplete || supervisedSlitPrecisionWarning)
                {
                    session = session with { SlitPrecisionWarningActive = supervisedSlitPrecisionWarning };
                    var settled = CreatePhd2PendingState(
                        context,
                        preset,
                        session,
                        ledger,
                        ledger.CurrentLockPosition,
                        Phd2LockShiftPendingPhase.SettledBudgetLedger,
                        intentEvidencePath: null,
                        supervisedSlitPrecisionWarning
                            ? "Known exact-lock endpoint and fresh optical identity retained for operator-authorized ATR probing with slit-precision warning; NOT exact slit completion or origin-return proof."
                            : "Target-on-slit completion was proven by fresh guiding-frame residual evidence.") with
                    {
                        // Persistent settled state records the fresh frame that
                        // proved completion. The process-local motion ledger
                        // continues to record only frames consumed by motion.
                        LastAcceptedFrameSha256 = session.LastMeasurement.Measurement.FrameSha256,
                        LastFramePath = session.LastMeasurement.Frame.Path,
                    };
                    await Phd2LockShiftPendingStore.WriteAtomicAsync(pendingPath, settled, cancellationToken).ConfigureAwait(false);
                    pendingPhd2LockShift = null;
                    phd2SlitPlacementSession = session;
                    lastG3Field = UpdateG3FieldFromGuidingResidual(lastG3Field, session.LastMeasurement, preset);
                    if (supervisedSlitPrecisionWarning)
                    {
                        var metrics = Phd2EffectiveQualityMetrics(session.Quality, session.GuideMode,
                            session.SelectedGuide, session.Settle, priorResidual);
                        metrics["slitPrecisionWarning"] = 1;
                        metrics["preciseSlitPlacementProven"] = 0;
                        metrics["slitResidualWindowMaximumPixels"] = completionResiduals.Max();
                        metrics["phd2IsUnattendedScienceAuthority"] = 0;
                        await PublishRunJsonEvidenceAsync("phd2-supervised-slit-warning-probe-authorized",
                            "Operator-authorized ATR probing without claiming exact slit placement",
                            new { completionResiduals, tolerance = completionTolerance, operatorConsent = true,
                                alongSlitPrecisionWarning, slitApertureResiduals,
                                slitGeometrySource = "validated per-frame runtime physical slit",
                                exactSlitPlacementProven = false, originReturnProven = false,
                                ledger.AttemptsUsed, ledger.CumulativeCommandedPixels, ledger.StartedUtc,
                                lockPosition = ledger.CurrentLockPosition, budgetReset = false,
                                nextAuthority = "actual ATR spectral-trace clipping, contrast and SNR" },
                            session.LastMeasurement.Frame.Path, cancellationToken).ConfigureAwait(false);
                        return Warning("PHD2_SLIT_PRECISION_WARNING_PROBE_AUTHORIZED",
                            $"目标已在狭缝附近，但连续残差（{string.Join(", ", completionResiduals.Select(value => value.ToString("F2", CultureInfo.InvariantCulture)))} px）未全部满足 {completionTolerance:F2} px；按本次授权警告后 ATR 试拍，由实际光谱判断，不宣称精确入缝。",
                            metrics, Metadata(loaded));
                    }
                    return session.FreshGuidingWindowReplacedSettle || session.Quality.RequiresOperatorSupervision
                        ? Warning(
                            "PHD2_TARGET_AT_SLIT_MIDPOINT_WIND_SAMPLED",
                            $"PHD2 placed the target at the runtime-recognized slit midpoint with residual {priorResidual:F2}px. Guiding/settle advisories remain; fresh same-epoch GuideStep/FITS measurements were accepted under explicit supervision, not as unattended authority.",
                            Phd2EffectiveQualityMetrics(session.Quality, session.GuideMode, session.SelectedGuide, session.Settle, priorResidual),
                            Metadata(loaded))
                        : Passed(
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
                    var relockStop = await EnsurePhdStoppedForAutomaticRebuildAsync(
                        ObservationStage.PlaceTargetOnSlit, preIntentFieldBinding.Code, cancellationToken).ConfigureAwait(false);
                    if (relockStop.Disposition != GateDisposition.Passed) return new StageResult(relockStop);
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
                    // The durable intent consumes the immutable residual that
                    // authorized this exact-lock request. Do not charge the
                    // fresh residual captured after the move: that is the
                    // evidence from which the next stage must replan.
                    LastAcceptedFrameSha256 = stage.SourceFrameSha256,
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
                var postExactSnapshot = phd2.Snapshot;
                if (!postExactSnapshot.IsConnected ||
                    postExactSnapshot.AppState != Phd2AppState.Guiding ||
                    postExactSnapshot.ConnectionEpoch != session.ConnectionEpoch)
                {
                    return await ReturnPhd2LockToOriginAsync(
                        context,
                        session,
                        pending with { Phase = Phd2LockShiftPendingPhase.ReturnRequired, LastReason = "The local guide session changed after verified exact-lock readback." },
                        "The local guide session changed after verified exact-lock readback.",
                        cancellationToken).ConfigureAwait(false);
                }
                pending = pending.RebindAfterLocallyAttestedGuideEpoch(
                    postExactSnapshot.ConnectionEpoch,
                    postExactSnapshot.GuideEpoch,
                    exact.Verified,
                    DateTimeOffset.UtcNow,
                    $"Exact runtime lock readback passed with {exact.VerificationErrorPixels:F3}px error; the locally advanced guide epoch was rebound without resetting motion debt or budget.") with
                {
                    Phase = Phd2LockShiftPendingPhase.AwaitingOperationBoundSettle,
                };
                session = session with { GuideEpoch = postExactSnapshot.GuideEpoch };
                await Phd2LockShiftPendingStore.WriteAtomicAsync(pendingPath, pending, CancellationToken.None).ConfigureAwait(false);
                pendingPhd2LockShift = pending;

                Phd2SettleResult stageSettle;
                Phd2StateSnapshot stageProof;
                var stageWindSampledSettle = false;
                Phd2PostLockGuidingObservation? stageReadOnlyObservation = null;
                try
                {
                    var calibrationBeforeStageSettle = await phd2.ValidateCalibrationAsync(
                        preset.CalibrationQualityPolicy.ApplyHardRejectionCeilings(PhdCalibrationRequirement()),
                        cancellationToken).ConfigureAwait(false);
                    if (calibrationBeforeStageSettle.Status != Phd2ValidationStatus.Valid)
                    {
                        throw new InvalidOperationException(
                            $"Last-moment calibration readback rejected the operation-bound settle; no guide command was sent: {string.Join(" ", calibrationBeforeStageSettle.Failures.Concat(calibrationBeforeStageSettle.IndeterminateReasons))}");
                    }
                    if (HasSupervisedScienceOptIn())
                    {
                        // set_lock_position already leaves PHD2 guiding. A new
                        // guide RPC per microstep broadcasts SettleDone failures
                        // to every client, including NINA's notification handler.
                        // The fresh-FITS owner call already waits for a new GuideStep.
                        // Establish its continuity baseline immediately, without
                        // first discarding another full guiding exposure. All three
                        // optical frames must still fit the original stage deadline.
                        Report("PHD2_POST_LOCK_OBSERVING");
                        stageReadOnlyObservation = Phd2PostLockGuidingObservation.BeginFreshResidualObservation(
                            phd2, exact, session.ConnectionEpoch, session.GuideEpoch,
                            preset.LockVerificationTolerancePixels,
                            supervised: true, cancellationToken);
                        stageSettle = session.Settle; // Original native result, not a fabricated SettleDone.
                        stageProof = phd2.Snapshot;
                        stageWindSampledSettle = true; // Does not authorize progress until three optical frames pass.
                    }
                    else
                    {
                        stageSettle = await phd2.GuideAndSettleAsync(
                            Phd2SettleCriteriaForSlitPlacement(preset),
                            forceRecalibration: false,
                            selectionRoi: null,
                            preserveSameEpochGuidingOnSettleTimeout: false,
                            cancellationToken).ConfigureAwait(false);
                        stageProof = phd2.Snapshot;
                        if (!stageSettle.Succeeded || !stageProof.HasCurrentSuccessfulSettle ||
                            stageProof.ConnectionEpoch != session.ConnectionEpoch ||
                            stageProof.AppState != Phd2AppState.Guiding)
                            throw new InvalidOperationException(stageSettle.Error ?? "Exact lock shift did not retain a current native settle attestation.");
                    }
                    pending = pending.RebindAfterLocallyAttestedGuideEpoch(
                        stageProof.ConnectionEpoch,
                        stageProof.GuideEpoch,
                        exact.Verified,
                        DateTimeOffset.UtcNow,
                        "Post-lock guide continuity was checked; durable lineage and charged motion budget were preserved.");
                    session = session with { GuideEpoch = stageProof.GuideEpoch };
                    await Phd2LockShiftPendingStore.WriteAtomicAsync(pendingPath, pending, CancellationToken.None).ConfigureAwait(false);
                    pendingPhd2LockShift = pending;
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
                    LastReason = stageReadOnlyObservation is null
                        ? "Operation-bound native settle passed; fresh immutable G3 residual evidence is required."
                        : "Native guiding was not restarted; read-only tracking observation ended and three fresh immutable G3 residuals are still required.",
                };
                await Phd2LockShiftPendingStore.WriteAtomicAsync(pendingPath, pending, CancellationToken.None).ConfigureAwait(false);
                pendingPhd2LockShift = pending;

                var expectedTargetAfter = AddPoint(
                    session.LastMeasurement.Measurement.TargetCentroid,
                    SubtractPoint(exact.Verified, stage.ExpectedCurrentLockPosition));
                IReadOnlyList<Phd2GuidingResidualState> measurements;
                try
                {
                    measurements = await CapturePhd2PlacementGuideWindowAsync(
                        context,
                        preset,
                        topology,
                        exact.Verified,
                        expectedTargetAfter,
                        session.LastMeasurement.RuntimeSlitLocal,
                        session.GuideMode,
                        stageWindSampledSettle
                            ? Math.Max(3, session.Quality.RequiredFreshResidualsPerLockShiftStage)
                            : session.Quality.RequiredFreshResidualsPerLockShiftStage,
                        exact.CompletedUtc.AddSeconds(preset.MaximumStageSeconds),
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
                targetCompletionWindow = measurements;
                completionWindowRetries = 0;
                // Each completed lock stage produces a new immutable residual
                // and mount binding.  Promote it before any failure/return path
                // or next-stage pre-intent gate can inspect current G3 state.
                lastG3Field = UpdateG3FieldFromGuidingResidual(lastG3Field, measured, preset);
                var residual = PointDistance(measured.Measurement.TargetCentroid, measured.Measurement.RecognizedSlitAcquisitionPoint);
                if (residual > priorResidual + preset.MaximumResidualGrowthPixels)
                {
                    transientResidualGrowthWarning = HasSupervisedScienceOptIn() &&
                        measured.Measurement.GuidePositionMeasuredInFrame && measured.Measurement.TargetIdentityConfirmed &&
                        measured.Measurement.TargetPositionAuthority != Phd2TargetPositionAuthority.CatalogWcsProjection;
                    if (!transientResidualGrowthWarning)
                        return await ReturnPhd2LockToOriginAsync(
                        context,
                        session,
                        pending with { Phase = Phd2LockShiftPendingPhase.ReturnRequired, LastReason = "Fresh target/slit residual worsened." },
                        $"Fresh target/slit residual worsened from {priorResidual:F3}px to {residual:F3}px.",
                        cancellationToken).ConfigureAwait(false);
                    await PublishRunJsonEvidenceAsync("phd2-transient-residual-growth-warning",
                        "Measured residual growth retained as a supervised warning before same-lock resampling",
                        new { priorResidual, residual, preset.MaximumResidualGrowthPixels,
                            newLockMotion = false, budgetReset = false, scienceAcceptanceUnchanged = true },
                        measured.Frame.Path, cancellationToken).ConfigureAwait(false);
                }

                var stageProofAfterFrame = phd2.Snapshot;
                if (stageReadOnlyObservation is not null)
                {
                    stageReadOnlyObservation = stageReadOnlyObservation.AcceptResiduals(
                        measurements.Select(item => item.Frame).ToArray(), stageProofAfterFrame);
                    await PublishRunJsonEvidenceAsync(
                        "phd2-post-lock-readonly-window",
                        "Supervised exact-lock continuation without another native guide/settle request",
                        new { observation = stageReadOnlyObservation,
                            originalNativeSettle = stageSettle,
                            nativeGuideCommandSent = false, syntheticSettleDone = false,
                            preciseSlitPlacementProven = false, unattendedAuthority = false,
                            budgetReset = false }, measured.Frame.Path, cancellationToken).ConfigureAwait(false);
                }
                var stageGuideResidual = PointDistance(measured.Measurement.GuideStar, exact.Verified);
                var stageQuality = SelectPhd2CalibrationQuality(
                    calibration,
                    preset,
                    Phd2CalibrationEvaluationPhase.PostSettle,
                    stageReadOnlyObservation?.ToCalibrationEvidence(stageSettle, stageProofAfterFrame) ??
                        CreateCalibrationSettleEvidence(stageSettle, stageProofAfterFrame, stageWindSampledSettle, measurements.Count),
                    CreateCalibrationResidualEvidence(
                        measured,
                        stageGuideResidual,
                        preset,
                        topology,
                        session.GuideMode,
                        stageWindSampledSettle && session.GuideMode == Phd2SlitGuideMode.DegradedDirectTargetGuiding
                            ? Math.Sqrt((double)preset.SensorWidthPixels * preset.SensorWidthPixels + (double)preset.SensorHeightPixels * preset.SensorHeightPixels)
                            : null),
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
                    FreshGuidingWindowReplacedSettle = session.FreshGuidingWindowReplacedSettle || stageWindSampledSettle,
                    ReadOnlyPostLockObservation = stageReadOnlyObservation,
                };
                phd2SlitPlacementSession = session;
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
            Exception failure = ex;
            if (IsStructuredPhd2GuideSessionLoss(ex) &&
                lostLockReacquisitionDepth == 0 &&
                (pendingPhd2LockShift is null ||
                 pendingPhd2LockShift.Phase == Phd2LockShiftPendingPhase.SettledBudgetLedger))
            {
                try
                {
                    // A lost guide session is an ordinary recoverable imaging
                    // condition, not a reason to abandon the observation.  No
                    // unreturned lock mutation exists here, so perform one
                    // bounded native reacquisition cycle: stop the stale PHD2
                    // session, rebuild the current G3/PL3/slit evidence, then
                    // let PHD2 select/confirm and guide again.  The depth bound
                    // prevents an endless relock loop while preserving the
                    // same durable motion budget and observation run.
                    var relockStop = await EnsurePhdStoppedForAutomaticRebuildAsync(
                        ObservationStage.PlaceTargetOnSlit, "PHD2_STRUCTURED_GUIDE_SESSION_LOSS", cancellationToken).ConfigureAwait(false);
                    if (relockStop.Disposition != GateDisposition.Passed) return new StageResult(relockStop);
                    phd2SlitPlacementSession = null;
                    lastG3Field = null;
                    Report($"warning：PHD2 导星会话失效（{ex.Message}）；执行一次有界的重新取场、原生选星和重锁");
                    var reacquired = await AcquireG3SlitFieldAsync(
                        context,
                        cancellationToken,
                        allowChargedCurrentPositionHandoff: true).ConfigureAwait(false);
                    if (!reacquired.CanAdvance)
                    {
                        return new StageResult(
                            GateResult.Unknown(
                                "PHD2_RELOCK_G3_REACQUISITION_BLOCKED",
                                $"PHD2 lost lock and the single bounded G3/PL3 reacquisition did not complete: {reacquired.Gate.Code}: {reacquired.Gate.Message}"),
                            reacquired.EvidencePath,
                            reacquired.Metadata);
                    }

                    return await PlaceTargetOnSlitWithPhd2Async(
                        context,
                        cancellationToken,
                        postCalibrationReacquisitionDepth,
                        lostLockReacquisitionDepth + 1).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception relockException)
                {
                    failure = new InvalidOperationException(
                        $"Initial guide/placement failed ({ex.Message}); the single bounded native relock also failed ({relockException.Message}).",
                        relockException);
                }
            }
            try { await StopPhdAndWaitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            phd2SlitPlacementSession = null;
            return Attention(
                ObservationStage.PlaceTargetOnSlit,
                "PHD2_SLIT_PLACEMENT_FAILED_SAFE",
                $"PHD2 slit placement stopped safely. A full G3/PL3 rebuild is attempted only for structured LostLock/disconnect evidence; this failure was not reclassified by message text. {failure.Message}");
        }
    }

    private async Task<Exception?> StopPhdAfterOriginReachedWithRetryAsync()
    {
        Exception? lastFailure = null;
        const int maximumAttempts = 2;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                await StopPhdAndWaitAsync(CancellationToken.None).ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                lastFailure = ex;
                if (attempt < maximumAttempts)
                {
                    Report("warning：PHD2 锁点已新鲜验证回到原点，但首次 checked-stop 未确认；仅重试一次幂等停止与读回，不发送 guide/lock/mount 命令");
                    await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        return lastFailure;
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
            recoveryEpisodeStartedUtc: null,
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
        DateTimeOffset? recoveryEpisodeStartedUtc,
        Func<Phd2Point, CancellationToken, Task<GateResult>>? finalVerification,
        bool prohibitReturnMotion = false)
    {
        var activeRecoveryStartedUtc = recoveryEpisodeStartedUtc ?? state.StartedUtc;
        state = state with { Phase = Phd2LockShiftPendingPhase.ReturnRequired, UpdatedUtc = DateTimeOffset.UtcNow, LastReason = reason };
        await Phd2LockShiftPendingStore.WriteAtomicAsync(path, state, CancellationToken.None).ConfigureAwait(false);
        pendingPhd2LockShift = state;
        if (recoveryEpisodeStartedUtc.HasValue)
        {
            await PublishRunJsonEvidenceAsync(
                "phd2-lock-recovery-episode-budget",
                "Explicit persisted-lock recovery received a fresh bounded active-time window",
                new
                {
                    state.LineageId,
                    sourceObservationRunId = state.ObservationRunId,
                    currentObservationRunId = context.Plan.ObservationRunId,
                    durableLineageStartedUtc = state.StartedUtc,
                    activeRecoveryStartedUtc,
                    passiveDowntimeSeconds = Math.Max(0, (activeRecoveryStartedUtc - state.StartedUtc).TotalSeconds),
                    maximumActiveRecoverySeconds = state.MaximumElapsedSeconds,
                    state.AttemptsUsed,
                    state.MaximumAttempts,
                    state.CumulativeCommandedPixels,
                    state.MaximumCumulativePixels,
                    durableLineageClockRewritten = false,
                    attemptBudgetReset = false,
                    cumulativeMotionBudgetReset = false,
                    outboundPlacementAuthorizedByRecoveryClock = false,
                    policy = "Only the required origin-return planner uses this episode clock. Every motion remains precharged against the original durable attempts and cumulative pixels.",
                },
                state.LastFramePath,
                cancellationToken).ConfigureAwait(false);
        }
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
            var actualReadback = await phd2.GetLockPositionWithSameEpochRetryAsync(
                state.ConnectionEpoch,
                state.GuideEpoch,
                maximumAttempts: 3,
                cancellationToken).ConfigureAwait(false);
            if (!actualReadback.SameGuideEpoch)
            {
                return Attention(
                    ObservationStage.PlaceTargetOnSlit,
                    "PHD2_LOCK_RETURN_EPOCH_CHANGED_DURING_READBACK",
                    $"PHD2 connection/guide epoch changed during bounded lock readback after {actualReadback.Attempts} read-only attempt(s); no return command was sent.");
            }
            var actual = actualReadback.Position;
            if (actual is null)
            {
                return Attention(
                    ObservationStage.PlaceTargetOnSlit,
                    "PHD2_LOCK_RETURN_POSITION_UNKNOWN",
                    $"PHD2 did not report a current lock position after {actualReadback.Attempts} bounded read-only attempts; no return command was sent.");
            }
            if (prohibitReturnMotion &&
                (actual.X != state.OriginLockX || actual.Y != state.OriginLockY))
                return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_ZERO_VECTOR_RETURN_POSITION_CHANGED",
                    "The verification-only recovery lock no longer equals its fresh origin. Cross-pier return motion is prohibited; the debt remains open.");
            state = state with { CurrentLockX = actual.X, CurrentLockY = actual.Y, UpdatedUtc = DateTimeOffset.UtcNow };
            var ledger = recoveryEpisodeStartedUtc.HasValue
                ? state.ToRecoveryEpisodePlannerLedger(activeRecoveryStartedUtc)
                : state.ToPlannerLedger();
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
            if (prohibitReturnMotion && !plan.IsComplete)
                return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_ZERO_VECTOR_RETURN_MOTION_PROHIBITED",
                    "The pier-only zero-vector verification path cannot dispatch a return stage. The debt and its original budgets remain open.");
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
                        topology.ComputeFingerprintSha256(),
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
                var returnStopSnapshot = phd2.Snapshot;
                pendingPhd2LockShift = null;
                phd2SlitPlacementSession = null;
                lastG3Field = null;
                var stopFailure = await StopPhdAfterOriginReachedWithRetryAsync().ConfigureAwait(false);
                if (stopFailure is not null)
                {
                    return Attention(
                        ObservationStage.PlaceTargetOnSlit,
                        "PHD2_LOCK_ORIGIN_REACHED_STOP_UNCONFIRMED",
                        $"Runtime lock origin was reached, but two bounded idempotent checked-stop attempts failed: {stopFailure.Message}");
                }
                // Returning the commanded PHD2 lock does not prove that the
                // mount matches its old coarse-WCS ledger. Retain the owned
                // stop receipt so the common G3 entry can perform a charged
                // mount return before attempting any new acquisition.
                var returnedStopEvidence = await PublishRunJsonEvidenceAsync(
                    "phd2-lock-origin-stop-confirmed", "Owned exact-lock origin returned and guiding checked-stopped",
                    new { state.LineageId, state.ObservationRunId, state.ConnectionEpoch, state.GuideEpoch,
                        currentRunId = context.Plan.ObservationRunId, actualLock = actual,
                        stopped = phd2.Snapshot.AppState.ToString(), stoppedGuideEpoch = phd2.Snapshot.GuideEpoch,
                        originalBudgetsPreserved = true, freshFieldStillRequired = true },
                    state.LastFramePath, cancellationToken).ConfigureAwait(false);
                var returnProof = new Phd2DependencyRebuildStopProof(
                    state.ConnectionEpoch, phd2.Snapshot.GuideEpoch, returnedStopEvidence);
                if (returnStopSnapshot.ConnectionEpoch != state.ConnectionEpoch ||
                    returnStopSnapshot.GuideEpoch != state.GuideEpoch ||
                    returnStopSnapshot.AppState != Phd2AppState.Guiding ||
                    returnStopSnapshot.AutomationPaused || returnStopSnapshot.Phd2Paused ||
                    returnStopSnapshot.PendingSettleOperationId is not null || !returnProof.IsCurrent(phd2.Snapshot))
                    return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_ORIGIN_REACHED_STOP_CONTEXT_CHANGED",
                        "锁点已返回并停止，但原导星连接或运行上下文发生变化；不生成重新取场的运动授权。");
                automaticRebuildStopProof = returnProof;
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
                    durableLineageStartedUtc = state.StartedUtc,
                    activeRecoveryStartedUtc = ledger.StartedUtc,
                    recoveryEpisodeClockUsed = recoveryEpisodeStartedUtc.HasValue,
                    attemptsAndCumulativeMotionPreserved = true,
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
                var dispatchReadback = await phd2.GetLockPositionWithSameEpochRetryAsync(
                    state.ConnectionEpoch,
                    state.GuideEpoch,
                    maximumAttempts: 3,
                    cancellationToken).ConfigureAwait(false);
                if (!dispatchReadback.SameGuideEpoch)
                {
                    await Phd2LockShiftPendingStore.WriteAtomicAsync(
                        path,
                        state with { LastReason = "PHD2 connection/guide epoch changed during the bounded pre-dispatch readback; no command was sent." },
                        CancellationToken.None).ConfigureAwait(false);
                    return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RETURN_DISPATCH_EPOCH_CHANGED", "PHD2 connection/guide epoch changed during the bounded pre-dispatch readback; no exact-lock command was sent.");
                }
                var dispatchLock = dispatchReadback.Position;
                if (dispatchLock is null)
                {
                    await Phd2LockShiftPendingStore.WriteAtomicAsync(
                        path,
                        state with { LastReason = $"Pre-dispatch lock position remained unknown after {dispatchReadback.Attempts} read-only attempts; no command was sent." },
                        CancellationToken.None).ConfigureAwait(false);
                    return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RETURN_DISPATCH_POSITION_UNKNOWN", "The pre-dispatch runtime lock position remained unknown after bounded read-only retries; no exact-lock command was sent.");
                }
                if (PointDistance(dispatchLock, stage.ExpectedCurrentLockPosition) > preset.LockPreconditionTolerancePixels)
                {
                    // The durable command budget was precharged before this
                    // readback.  Preserve that charge and replan from the fresh
                    // actual lock; never resend the stale exact-lock request.
                    state = state with
                    {
                        CurrentLockX = dispatchLock.X,
                        CurrentLockY = dispatchLock.Y,
                        UpdatedUtc = DateTimeOffset.UtcNow,
                        LastReason = "Fresh runtime lock changed before dispatch. No command was sent; the precharged attempt/budget remains consumed and the bounded loop will replan from the actual lock.",
                    };
                    await Phd2LockShiftPendingStore.WriteAtomicAsync(
                        path,
                        state,
                        CancellationToken.None).ConfigureAwait(false);
                    pendingPhd2LockShift = state;
                    continue;
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
                var ambiguousSnapshot = phd2.Snapshot;
                if (!ambiguousSnapshot.IsConnected ||
                    ambiguousSnapshot.AppState != Phd2AppState.Guiding ||
                    ambiguousSnapshot.ConnectionEpoch != state.ConnectionEpoch)
                {
                    return Attention(
                        ObservationStage.PlaceTargetOnSlit,
                        "PHD2_LOCK_RETURN_AMBIGUOUS_EPOCH_CHANGED",
                        "The exact-lock response was ambiguous and the original connected Guiding epoch cannot be reconciled automatically; the command was not resent.");
                }
                var ambiguousReadback = ambiguous.Observed is not null
                    ? new Phd2SameEpochLockReadback(
                        Position: ambiguous.Observed,
                        Attempts: 0,
                        MaximumAttempts: 3,
                        SameGuideEpoch: true,
                        ConnectionEpoch: ambiguousSnapshot.ConnectionEpoch,
                        GuideEpoch: ambiguousSnapshot.GuideEpoch,
                        AppState: ambiguousSnapshot.AppState)
                    : await phd2.GetLockPositionWithSameEpochRetryAsync(
                        ambiguousSnapshot.ConnectionEpoch,
                        ambiguousSnapshot.GuideEpoch,
                        maximumAttempts: 3,
                        cancellationToken).ConfigureAwait(false);
                if (!ambiguousReadback.SameGuideEpoch)
                {
                    return Attention(
                        ObservationStage.PlaceTargetOnSlit,
                        "PHD2_LOCK_RETURN_AMBIGUOUS_READBACK_EPOCH_CHANGED",
                        "The exact-lock response was ambiguous and its bounded read-only reconciliation crossed a guide epoch; the command was not resent.");
                }
                verified = ambiguousReadback.Position
                    ?? throw new InvalidOperationException(
                        "The ambiguous exact-lock request could not be reconciled after bounded read-only lock-position attempts.",
                        ambiguous);
                // The ambiguous request is never resent.  The next iteration
                // replans a recovery-only vector from this fresh actual lock.
                state = state.RebindAfterLocallyAttestedGuideEpoch(
                    ambiguousReadback.ConnectionEpoch,
                    ambiguousReadback.GuideEpoch,
                    verified,
                    DateTimeOffset.UtcNow,
                    $"Ambiguous recovery response reconciled at ({verified.X:F3},{verified.Y:F3}); no resend was attempted and the charged budget was preserved.");
                await Phd2LockShiftPendingStore.WriteAtomicAsync(path, state, CancellationToken.None).ConfigureAwait(false);
                pendingPhd2LockShift = state;
                continue;
            }
            var postReturnExactSnapshot = phd2.Snapshot;
            if (!postReturnExactSnapshot.IsConnected ||
                postReturnExactSnapshot.AppState != Phd2AppState.Guiding ||
                postReturnExactSnapshot.ConnectionEpoch != state.ConnectionEpoch)
            {
                state = state with
                {
                    CurrentLockX = verified.X,
                    CurrentLockY = verified.Y,
                    LastReason = "Exact-lock readback passed, but the connected Guiding epoch changed before operation-bound settle.",
                };
                await Phd2LockShiftPendingStore.WriteAtomicAsync(path, state, CancellationToken.None).ConfigureAwait(false);
                return Attention(
                    ObservationStage.PlaceTargetOnSlit,
                    "PHD2_LOCK_RETURN_POST_EXACT_EPOCH_CHANGED",
                    "Exact-lock readback passed, but the connected Guiding epoch changed before operation-bound settle; no guide command was sent.");
            }
            state = state.RebindAfterLocallyAttestedGuideEpoch(
                postReturnExactSnapshot.ConnectionEpoch,
                postReturnExactSnapshot.GuideEpoch,
                verified,
                DateTimeOffset.UtcNow,
                "Verified recovery exact-lock mutation advanced the local guide epoch; durable return debt and budget were preserved.");
            await Phd2LockShiftPendingStore.WriteAtomicAsync(path, state, CancellationToken.None).ConfigureAwait(false);
            pendingPhd2LockShift = state;
            // LockPositionSet invalidates settle authority but not calibration
            // authority.  Still refresh the actual calibration immediately
            // before the guide/settle RPC, matching every other guide path.
            var calibrationBeforeReturnSettle = await phd2.ValidateCalibrationAsync(
                preset.CalibrationQualityPolicy.ApplyHardRejectionCeilings(PhdCalibrationRequirement()),
                cancellationToken).ConfigureAwait(false);
            if (calibrationBeforeReturnSettle.Status != Phd2ValidationStatus.Valid)
            {
                state = state with
                {
                    CurrentLockX = verified.X,
                    CurrentLockY = verified.Y,
                    LastReason = "Recovery lock readback passed, but the last-moment calibration readback rejected guide/settle; no guide command was sent.",
                };
                await Phd2LockShiftPendingStore.WriteAtomicAsync(path, state, CancellationToken.None).ConfigureAwait(false);
                return Attention(
                    ObservationStage.PlaceTargetOnSlit,
                    "PHD2_LOCK_RETURN_PRE_SETTLE_CALIBRATION_INVALID",
                    string.Join(" ", calibrationBeforeReturnSettle.Failures.Concat(calibrationBeforeReturnSettle.IndeterminateReasons)));
            }
            var settle = await phd2.GuideAndSettleAsync(
                Phd2SettleCriteriaForSlitPlacement(preset),
                forceRecalibration: false,
                selectionRoi: null,
                preserveSameEpochGuidingOnSettleTimeout: HasSupervisedScienceOptIn(),
                cancellationToken).ConfigureAwait(false);
            var settledSnapshot = phd2.Snapshot;
            var windSampledSettle = CanReplaceSettleWithFreshGuidingWindow(
                settle,
                settledSnapshot,
                state.ConnectionEpoch);
            if (settledSnapshot.IsConnected &&
                settledSnapshot.AppState == Phd2AppState.Guiding &&
                settledSnapshot.ConnectionEpoch == state.ConnectionEpoch)
            {
                state = state.RebindAfterLocallyAttestedGuideEpoch(
                    settledSnapshot.ConnectionEpoch,
                    settledSnapshot.GuideEpoch,
                    verified,
                    DateTimeOffset.UtcNow,
                    "The locally issued recovery settle advanced the guide epoch; durable return debt and charged budget were preserved.");
                await Phd2LockShiftPendingStore.WriteAtomicAsync(path, state, CancellationToken.None).ConfigureAwait(false);
                pendingPhd2LockShift = state;
            }
            if ((!settle.Succeeded && !windSampledSettle) ||
                (settle.Succeeded && !settledSnapshot.HasCurrentSuccessfulSettle) ||
                settledSnapshot.ConnectionEpoch != state.ConnectionEpoch || settledSnapshot.GuideEpoch != state.GuideEpoch)
            {
                state = state with { CurrentLockX = verified.X, CurrentLockY = verified.Y, LastReason = "Recovery lock readback passed but operation-bound settle failed." };
                await Phd2LockShiftPendingStore.WriteAtomicAsync(path, state, CancellationToken.None).ConfigureAwait(false);
                return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RETURN_SETTLE_FAILED", settle.Error ?? "Recovery settle was not attested.");
            }
            if (windSampledSettle)
                Report("warning：回程锁点在海风中未进入 settle 圈；保持同一 Guiding epoch，并改取 fresh GuideStep/FITS 窗口复核");

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
        CancellationToken cancellationToken,
        Phd2ReadOnlyFrameGraceBudget? readoutGrace = null)
    {
        if (count <= 0) throw new InvalidOperationException("The commissioned fresh-residual count must be positive.");
        using var uvex = new UvexServiceClient(configuration.UvexServiceUrl);
        var measurements = new List<Phd2GuidingResidualState>(count);
        var captureAttempt = 0;
        var maximumCaptureAttempts = Phd2FreshSlitFrameRetryPolicy.MaximumCaptureAttempts(count);
        while (measurements.Count < count && captureAttempt < maximumCaptureAttempts)
        {
            captureAttempt++;
            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            var slitFocusBefore = ReadC11MainFocusOwner();
            var slitUvexBefore = await uvex.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            var frameRequest = new Phd2GuidingFrameRequest(
                ReserveRunEvidencePath("g3-phd2-lock-residual", ".fit"),
                TimeSpan.FromSeconds(preset.FreshGuidingFrameTimeoutSeconds));
            var beforeFrameWait = phd2.Snapshot;
            Phd2GuidingFrameResult result;
            try
            {
                result = await phd2.SaveCurrentGuidingFrameAsync(frameRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (Phd2CommandTimeoutException ex) when (
                readoutGrace?.TryConsume(ex, beforeFrameWait, phd2.Snapshot) == true &&
                !File.Exists(frameRequest.DestinationPath))
            {
                // Only the pre-ATR no-motion caller supplies this single-use budget.
                // The named timeout is before save_image, so there is no uncertain
                // save to repeat. The native guiding loop is left untouched.
                await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
                Report("光谱曝光前等待新导星帧超时；当前同一导星会话和锁点未变，仅增加一次最多 20 秒的只读等待，不重启导星、不复用旧帧。");
                await PublishRunJsonEvidenceAsync("phd2-pre-atr-readout-grace",
                    "One bounded same-epoch fresh-frame wait before exposure; no device command retry",
                    new { initialTimeoutSeconds = ex.Timeout.TotalSeconds,
                        extraWaitSeconds = Phd2ReadOnlyFrameGraceBudget.ExtraWait.TotalSeconds,
                        beforeFrameWait.ConnectionEpoch, beforeFrameWait.GuideEpoch,
                        lockPosition = beforeFrameWait.LockPosition, lockMutation = false,
                        guidingRestarted = false, budgetReset = false },
                    lastG3Field?.FramePath, cancellationToken).ConfigureAwait(false);
                result = await phd2.SaveCurrentGuidingFrameAsync(
                    frameRequest with { FreshGuideStepTimeout = Phd2ReadOnlyFrameGraceBudget.ExtraWait },
                    cancellationToken).ConfigureAwait(false);
                if (!Phd2ReadOnlyFrameGraceBudget.SameGuiding(beforeFrameWait, phd2.Snapshot))
                    throw new InvalidOperationException("PHD2_PRE_ATR_READOUT_EPOCH_CHANGED: Guide ownership or lock changed during the extra read-only wait; no exposure is authorized.");
            }
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
            double? saturatedIntegratedFluxDiagnostic = null;
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
            else if (lastG3Field?.TargetIdentification.Authority == TargetIdentificationAuthority.CatalogWcsProjection)
            {
                // The fresh guide frame is exposure-only and remains bound to
                // the same mount position as the formal PL3 field. Preserve the
                // catalogue-WCS target coordinate for both native off-slit
                // selection and degraded direct-target guiding. Saturated wings,
                // diffraction structure and calibrated ghosts may create several
                // similar local maxima; that lower-authority morphology must not
                // revoke an already established catalogue identity.
                var sourceTopologyAnalysis = SaturatedTargetGhostTopologyAnalyzer.Analyze(
                    frame,
                    expectedTargetLocal,
                    preset.TargetSearchRadiusPixels);
                if (sourceTopologyAnalysis.Gate.Disposition == GateDisposition.Passed &&
                    sourceTopologyAnalysis.Target is { } topologyTarget)
                {
                    targetLocal = topologyTarget.Centroid;
                    // Catalogue/PL3 remains the identity authority, while this
                    // fresh guide frame supplies a measured detector position
                    // from an unambiguous filled saturated core. Integrated ADU
                    // is exposure- and component-area-dependent after saturation,
                    // so it cannot be compared with the ordinary stellar-flux
                    // envelope. Record an explicit N/A claim instead of either
                    // fabricating flux or misclassifying the centroid as a plain
                    // stellar detection.
                    targetFlux = 0;
                    targetPositionAuthority = Phd2TargetPositionAuthority.CatalogWcsIdentityWithSaturatedTopologyCentroid;
                    Report($"PL3 保持目标身份；fresh 导星帧以实心饱和核更新像素位置，排除 {sourceTopologyAnalysis.Ghosts.Count} 个空心环鬼影；饱和目标不套用普通恒星总光通量上限。");
                }
                else
                {
                    // A formerly bright target may no longer saturate near the
                    // slit. Try the normal, unique stellar centroid as well;
                    // retain its real flux and the ordinary hard flux envelope.
                    var freshStellar = SlitTargetIdentifier.Identify(
                        frame, candidates, expectedTargetLocal,
                        Math.Min(preset.TargetSearchRadiusPixels, preset.MaximumAcquisitionResidualPixels),
                        preset.MinimumTargetSignalToNoise, preset.MinimumTargetUniquenessRatio);
                    if (Phd2FreshTargetFluxPolicy.CanRefineCatalogPositionWithStellarCentroid(
                        freshStellar, preset.MaximumAcquisitionResidualPixels))
                    {
                        targetLocal = freshStellar.Target!.Centroid;
                        targetFlux = freshStellar.Target.FluxAdu;
                        targetPositionAuthority = Phd2TargetPositionAuthority.DetectedTargetCentroid;
                        Report("PL3 保持目标身份；本帧未饱和目标已由唯一恒星质心接替预测位置，保留真实通量及原通量门限。");
                    }
                    else
                    {
                        targetLocal = expectedTargetLocal;
                        targetFlux = 0;
                        targetPositionAuthority = Phd2TargetPositionAuthority.CatalogWcsProjection;
                        Report($"PL3 保持目标身份；本帧尚无实测目标位置：{sourceTopologyAnalysis.Gate.Code} / {freshStellar.Gate.Code}。");
                    }
                }
                targetEvidence = $"catalog-wcs:{context.Plan.Target.CatalogId}:{lastG3Field.Solve?.SolverIdentity}:{lastG3Field.FramePath};topology:{sourceTopologyAnalysis.Gate.Code}";
            }
            else
            {
                var targetId = SlitTargetIdentifier.Identify(
                    frame,
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
                var formalCatalogSolve = lastG3Field?.Solve;
                if (formalCatalogSolve is { Result.Success: true, Result.Coordinates: not null } &&
                    !string.IsNullOrWhiteSpace(formalCatalogSolve.EvidencePath) &&
                    Phd2FreshTargetFluxPolicy.UsesSaturatedTopologyFluxNotApplicable(
                        targetId, hasFormalCatalogWcsChain: true))
                {
                    // The common identifier can promote a fresh frame from
                    // ordinary stellar morphology to a filled saturated core.
                    // Preserve that typed result instead of mislabelling its
                    // nonlinear component sum as ordinary stellar photometry.
                    saturatedIntegratedFluxDiagnostic = targetFlux;
                    targetFlux = 0;
                    targetPositionAuthority = Phd2TargetPositionAuthority.CatalogWcsIdentityWithSaturatedTopologyCentroid;
                    targetEvidence += $";fresh-topology:{targetId.Gate.Code};catalog-wcs:{formalCatalogSolve.EvidencePath}";
                }
            }

            // A neighbour-field motion forecast is acquisition guidance, not a
            // measured target/slit residual. It must be replaced by a target
            // measured in this immutable frame before any PHD2 placement step.
            if (lastG3Field?.Solve?.Result.Success != true &&
                targetPositionAuthority == Phd2TargetPositionAuthority.CatalogWcsProjection)
                throw new InvalidOperationException(
                    "G3_POST_WCS_TARGET_NOT_MEASURED: The target field did not solve and this fresh guide frame did not identify the target; the predicted coordinate cannot authorize a lock shift or prove slit placement.");

            PixelPoint guideLocal;
            var guidePositionAuthority = "FreshDirectTargetCentroid";
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
                if (result.NativeMeasuredGuidePosition is { } nativeGuide &&
                    result.NativeLockPosition is { } nativeLock &&
                    PointDistance(nativeLock, currentLock) <= preset.LockVerificationTolerancePixels)
                {
                    guideLocal = ToFrameLocal(nativeGuide, preset);
                    guidePositionAuthority = "SameFramePhd2NativeCameraOffset";
                    if (guideId.Gate.Disposition != GateDisposition.Passed)
                        Report($"PHD2 本帧实测导星偏差 ({result.NativeGuideStep!.DxPixels:F2}, {result.NativeGuideStep.DyPixels:F2})px；本地星形仅作诊断：{guideId.Gate.Code}");
                }
                else if (guideId.Gate.Disposition == GateDisposition.Passed && guideId.Target is not null)
                {
                    guideLocal = guideId.Target.Centroid;
                    guidePositionAuthority = "FreshLocalGuideCentroid";
                }
                else
                {
                    throw new InvalidOperationException(
                        $"PHD2_FRESH_GUIDE_POSITION_UNPROVEN: Neither frame-bound native camera offsets nor a fresh local guide centroid are available ({guideId.Gate.Code}); the requested lock position cannot substitute for a measured star.");
                }
            }
            // ADR-0003/0006: the LED sequence measures the physical aperture.
            // A new residual means a new star measurement relative to that
            // state-bound aperture, not another dark-line search on starlight.
            var runLedSlit = g3SlitGeometryRunCache
                ?? throw new InvalidOperationException("G3_RUN_LED_SLIT_GEOMETRY_INVALID: No LED slit measurement exists for this run.");
            var slitUvexAfter = await uvex.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            var slitFocusAfter = ReadC11MainFocusOwner();
            var slitReferenceContext = new G3RunLedSlitGeometryContext(
                context.Plan.ObservationRunId, configuration.ActionConfigurationSha256,
                commissioning?.Sha256 ?? string.Empty, nightSetup?.Sha256 ?? string.Empty,
                topology.CameraStableId, topology.Binning, properties.Width, properties.Height,
                nightSetup?.Value.SlitPosition ?? -1, nightSetup?.Value.SlitWidthMicrometers ?? double.NaN,
                phd2.Snapshot.ConnectionEpoch, preset.RoiX == 0 && preset.RoiY == 0,
                ValidateUvexStatus(slitUvexBefore).Disposition == GateDisposition.Passed &&
                    ValidateUvexStatus(slitUvexAfter).Disposition == GateDisposition.Passed,
                slitUvexBefore?.SlitIlluminationLedState == UvexOutputState.Off &&
                    slitUvexAfter?.SlitIlluminationLedState == UvexOutputState.Off,
                nightSetup is not null &&
                    C11MainFocusPolicy.ValidateLockedPosition(slitFocusBefore, nightSetup.Value).Disposition == GateDisposition.Passed &&
                    C11MainFocusPolicy.ValidateLockedPosition(slitFocusAfter, nightSetup.Value).Disposition == GateDisposition.Passed &&
                    slitFocusBefore.PositionSteps == slitFocusAfter.PositionSteps,
                await ComputeFileSha256Async(runLedSlit.SourceFramePath, cancellationToken).ConfigureAwait(false),
                await ComputeFileSha256Async(runLedSlit.SlitIdentityEvidencePath, cancellationToken).ConfigureAwait(false),
                result.CompletedUtc);
            var slitDetection = G3RunLedSlitGeometryPolicy.Evaluate(runLedSlit, slitReferenceContext);
            if (slitDetection.Gate.Disposition != GateDisposition.Passed)
                throw new InvalidOperationException($"{slitDetection.Gate.Code}: {slitDetection.Gate.Message}");
            var target = ToPhd2Domain(targetLocal, preset);
            var guide = ToPhd2Domain(guideLocal, preset);
            // Fresh target/guide positions are paired with this run's independent
            // LED-measured midpoint, not a historical overlay or starlight ridge.
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
                targetPositionAuthority switch
                {
                    Phd2TargetPositionAuthority.CatalogWcsProjection => "CATALOG_WCS_TARGET_FLUX_NOT_APPLICABLE",
                    Phd2TargetPositionAuthority.CatalogWcsIdentityWithSaturatedTopologyCentroid => "SATURATED_TARGET_TOPOLOGY_FLUX_NOT_APPLICABLE",
                    _ => guideMode == Phd2SlitGuideMode.DegradedDirectTargetGuiding ? "DEGRADED_DIRECT_TARGET_FLUX" : "TARGET_FLUX",
                },
                targetFlux,
                $"fresh-target-slit={targetResidual:F4}px;fresh-guide-lock={guideResidual:F4}px",
                targetPositionAuthority,
                GuidePositionMeasuredInFrame: true);
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
            var acceptedOrdinal = measurements.Count + 1;
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
                $"Fresh PHD2 guiding residual {acceptedOrdinal}/{count}",
                new
                {
                    formula = "desiredGuideLock = guide + (recognizedSlitAcquisitionPoint - targetCentroid)",
                    measurement,
                    expectedCurrentLock = currentLock,
                    expectedTarget,
                    targetSlitResidualPixels = targetResidual,
                    targetSlitMidpointResidualPixels = targetResidual,
                    guideLockResidualPixels = guideResidual,
                    guidePositionAuthority,
                    result.NativeGuideStep,
                    result.NativeLockPosition,
                    saturatedIntegratedFluxDiagnostic,
                    result.TriggerGuideFrame,
                    result.EventSequence,
                    result.GuideStepUtc,
                    result.GuidingWasInterrupted,
                    result.ExposureChanged,
                    result.CaptureLoopStarted,
                    captureAttempt,
                    rejectedSlitFrames = 0,
                    maximumCaptureAttempts,
                    slitDetectionGate = slitDetection.Gate,
                    slitGeometryAuthority = "RUN_LED_OFF_ON_OFF",
                    slitMeasuredUtc = runLedSlit.CapturedUtc,
                    slitSourceFrame = runLedSlit.SourceFramePath,
                    slitSourceFrameSha256 = runLedSlit.SourceFrameSha256,
                    slitIdentityEvidencePath = runLedSlit.SlitIdentityEvidencePath,
                    slitIdentityEvidenceSha256 = runLedSlit.SlitIdentityEvidenceSha256,
                    slitReferenceContext,
                    immutableRunSlitAnchorUsed = true,
                    registryProfileMutated = false,
                },
                result.Path,
                cancellationToken).ConfigureAwait(false);
            PublishG3Preview(image,
                $"新帧星位 + 本轮 LED 实测缝位：目标距缝中点 {targetResidual:F2}px；缝位 ({slit.X:F2}, {slit.Y:F2})，不要求星场中看见暗缝。",
                slitDetection.Geometry, targetLocal, guideLocal);
            runtimeSlitLocal = slitDetection.Geometry;
            expectedTarget = target;
        }
        if (measurements.Count != count)
        {
            throw new InvalidOperationException(
                $"PHD2_FRESH_SLIT_REACQUISITION_EXHAUSTED: Fresh residual capture used {captureAttempt}/{maximumCaptureAttempts} attempts " +
                $"but obtained only {measurements.Count}/{count} accepted slit frames.");
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

        // PHD2 owns normal full-frame selection. A bad edge/halo/slit choice is
        // a candidate rejection, not authority for the coordinator to rank and
        // substitute another ordinary star. Wait for another fresh full frame
        // and ask PHD2 again; after the bounded attempts the caller must either
        // stop the strict off-slit route or explicitly enter a commissioned
        // degraded guide mode before any guide/lock/mount mutation.
        var target = choice.Field.TargetIdentification.Target
            ?? throw new InvalidOperationException("PHD2 native guide validation has no fresh target identity.");
        var nativePolicy = new GuideStarSelectionPolicy(MinimumSignalToNoise: preset.MinimumGuideSignalToNoise);
        var exclusionFrame = choice.Field.Frame
            ?? throw new InvalidOperationException("Native guide geometry requires its immutable full selection frame.");
        var exclusionFramePath = choice.Field.FramePath;
        var saturatedStructureExclusions = Phd2NativeGuideSaturatedRegions.Measure(exclusionFrame, nativePolicy);

        async Task RefreshNativeSelectionGeometryAsync(string evidenceRole)
        {
            var fresh = await phd2.SaveNextLoopingFrameAsync(
                new Phd2SingleFrameRequest(preset.ExposureFor(choice.Mode), configuration.G3.Binning,
                    configuration.G3.GainPercent, ReserveRunEvidencePath(evidenceRole, ".fit")),
                cancellationToken).ConfigureAwait(false);
            var freshImage = await imageDataFactory.CreateFromFile(fresh.Path, 16, false,
                RawConverterEnum.FREEIMAGE, cancellationToken).ConfigureAwait(false);
            if (freshImage.Properties.Width != preset.RoiWidth || freshImage.Properties.Height != preset.RoiHeight ||
                fresh.VerifiedExposureMilliseconds != preset.ExposureFor(choice.Mode))
                throw new InvalidOperationException("Fresh native-guide exclusion frame changed detector/exposure binding.");
            exclusionFrame = G3FrameInputPolicy.Create(freshImage.Properties.Width, freshImage.Properties.Height,
                freshImage.Data.FlatArray, configuration.G3);
            exclusionFramePath = fresh.Path;
            saturatedStructureExclusions = Phd2NativeGuideSaturatedRegions.Measure(exclusionFrame, nativePolicy);
        }
        var targetIsUltraBright = target.FwhmPixels <= 0 ||
            target.SignalToNoise >= nativePolicy.BrightTargetSignalToNoiseThreshold ||
            target.SaturatedFraction >= nativePolicy.BrightTargetSaturatedFractionThreshold;
        var targetGuard = targetIsUltraBright
            ? Math.Max(nativePolicy.TargetGuardPixels, nativePolicy.BrightTargetHaloGuardPixels)
            : nativePolicy.TargetGuardPixels;
        const int maximumNativeSelectionAttempts = 4;
        var rejected = new List<string>(maximumNativeSelectionAttempts);
        var rejectedPoints = new List<PixelPoint>();
        var searchedRegions = new List<Phd2Rectangle>();
        for (var attempt = 1; attempt <= maximumNativeSelectionAttempts; attempt++)
        {
            Phd2Point selectedNative;
            Phd2Rectangle? nativeSearchRoi = null;
            var selectedInsideRequestedRoi = true;
            try
            {
                if (attempt == 1)
                    selectedNative = await phd2.FindGuideStarAsync(cancellationToken).ConfigureAwait(false);
                else
                {
                    // Repeating full-frame find_star selected the same halo
                    // every time. Exclude physical geometry, not star scores;
                    // PHD2 still chooses the star inside each untried region.
                    var regions = Phd2NativeGuideSearchRegions.Build(
                        preset.RoiWidth, preset.RoiHeight, target.Centroid, targetGuard,
                        choice.Field.SlitDetection.Geometry, nativePolicy.MinimumEdgeDistancePixels,
                        nativePolicy.SlitGuardPixels, rejectedPoints, searchedRegions,
                        saturatedStructureExclusions.Select(item => item.Region).ToArray());
                    if (regions.Count == 0)
                    {
                        rejected.Add($"attempt {attempt}: no untried geometrically safe search region remains");
                        break;
                    }
                    var localRoi = regions[0];
                    searchedRegions.Add(localRoi);
                    var origin = ToPhd2Domain(new PixelPoint(localRoi.X, localRoi.Y), preset);
                    var searchRoi = new Phd2Rectangle((int)origin.X, (int)origin.Y, localRoi.Width, localRoi.Height);
                    nativeSearchRoi = searchRoi;
                    Report($"PHD2 排除已拒绝区域后原生重选星 {attempt}/{maximumNativeSelectionAttempts}：ROI ({searchRoi.X},{searchRoi.Y},{searchRoi.Width},{searchRoi.Height})");
                    selectedNative = await phd2.FindGuideStarInRoiAsync(searchRoi, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Phd2GuideStarOutsideRoiException outsideRoi)
            {
                // Native centroid refinement can leave the ROI that supplied
                // its seed. The RPC completed, so reject this candidate under
                // the same finite selection budget; do not start guiding it.
                selectedNative = outsideRoi.Selected;
                selectedInsideRequestedRoi = false;
            }
            catch (Phd2NoGuideStarException noGuideStar)
            {
                // A successful find_star response with a null result means only
                // that this immutable looping frame supplied no native candidate.
                // Transport, protocol and RPC exceptions intentionally bypass
                // this catch and remain hard failures with uncertain state.
                var noCandidateReason =
                    $"attempt {attempt}: {noGuideStar.Message}";
                rejected.Add(noCandidateReason);
                Report($"warning：PHD2 本帧未找到导星候选（{noCandidateReason}）；等待 fresh 全帧后重新选星");
                if (attempt < maximumNativeSelectionAttempts)
                {
                    await RefreshNativeSelectionGeometryAsync($"g3-phd2-native-no-candidate-{attempt}").ConfigureAwait(false);
                    continue;
                }

                break;
            }
            var selectedLocal = ToFrameLocal(selectedNative, preset);
            var edgeDistance = Math.Min(
                Math.Min(selectedLocal.X, preset.RoiWidth - 1 - selectedLocal.X),
                Math.Min(selectedLocal.Y, preset.RoiHeight - 1 - selectedLocal.Y));
            var targetDistance = PixelDistance(selectedLocal, target.Centroid);
            var slitDistance = GuideStarSelector.DistanceToSlit(selectedLocal, choice.Field.SlitDetection.Geometry);
            var insideFrame = selectedLocal.X >= 0 && selectedLocal.X < preset.RoiWidth &&
                              selectedLocal.Y >= 0 && selectedLocal.Y < preset.RoiHeight;
            var insideSaturatedStructure = saturatedStructureExclusions.Any(item =>
                Phd2NativeGuideSaturatedRegions.Contains(item.Region, selectedLocal));
            var geometryAccepted = insideFrame && selectedInsideRequestedRoi &&
                                   !insideSaturatedStructure &&
                                   edgeDistance >= nativePolicy.MinimumEdgeDistancePixels &&
                                   targetDistance >= targetGuard &&
                                   slitDistance >= choice.Field.SlitDetection.Geometry.WidthPixels / 2 + nativePolicy.SlitGuardPixels;
            await PublishRunJsonEvidenceAsync(
                "phd2-native-guide-candidate",
                "PHD2 native candidate checked against unchanged physical geometry",
                new { attempt, maximumNativeSelectionAttempts, nativeSearchRoi, selectedNative, selectedLocal,
                    insideFrame, selectedInsideRequestedRoi, edgeDistance, targetDistance, targetGuard, slitDistance, geometryAccepted,
                    insideSaturatedStructure, saturatedStructureExclusions,
                    candidateRankingByCoordinator = false, rejectedPoints, searchedRegions },
                exclusionFramePath, cancellationToken).ConfigureAwait(false);
            if (geometryAccepted)
            {
                var validation = GuideStarSelector.ValidateNativeSelection(
                    choice.Field.Candidates,
                    choice.Field.SlitDetection.Geometry,
                    target,
                    selectedLocal,
                    preset.GuideSearchRadiusPixels,
                    nativePolicy);
                if (validation.Gate.Disposition == GateDisposition.Passed)
                {
                    return (validation, selectedNative, selectedNative);
                }
                // Local morphology is explicitly diagnostic. Geometry passed,
                // so retain PHD2's native choice as a warning rather than
                // restarting the entire G3/PL3 acquisition path.
                return (
                    new GuideStarSelection(
                        GateResult.Warn(
                            "PHD2_NATIVE_GUIDE_ACCEPTED_MORPHOLOGY_WARNING",
                            $"PHD2 native guide ({selectedNative.X:F1},{selectedNative.Y:F1}) passed detector-edge/target/slit geometry; local morphology is advisory: {validation.Gate.Code}: {validation.Gate.Message}"),
                        validation.Star,
                        validation.Score),
                    selectedNative,
                    selectedNative);
            }

            var reason =
                $"attempt {attempt}: selected=({selectedNative.X:F1},{selectedNative.Y:F1}), insideFrame={insideFrame}, insideRequestedRoi={selectedInsideRequestedRoi}, clippedStructure={insideSaturatedStructure}, edge={edgeDistance:F1}px/{nativePolicy.MinimumEdgeDistancePixels:F1}px, target={targetDistance:F1}px/{targetGuard:F1}px, slit={slitDistance:F1}px/{choice.Field.SlitDetection.Geometry.WidthPixels / 2 + nativePolicy.SlitGuardPixels:F1}px";
            rejected.Add(reason);
            rejectedPoints.Add(selectedLocal);
            Report($"warning：PHD2 候选落入边缘、目标光晕、饱和扩展结构或狭缝（{reason}）；拍摄新全帧后原生重选，不直接阻断整轮");
            if (attempt < maximumNativeSelectionAttempts)
            {
                await RefreshNativeSelectionGeometryAsync($"g3-phd2-native-reselection-{attempt}").ConfigureAwait(false);
            }
        }

        throw new Phd2NativeGuideSelectionExhaustedException(
            maximumNativeSelectionAttempts,
            rejected.AsReadOnly());
    }

    private async Task<Phd2PreparedGuideSelection> PrepareDirectTargetFallbackAfterNativeExhaustionAsync(
        ObservationContext context,
        Phd2PlacementGuideChoice exhaustedOffSlitChoice,
        Phd2SlitPlacementCommissioningPreset preset,
        Phd2NativeGuideSelectionExhaustedException exhausted,
        CancellationToken cancellationToken)
    {
        await PublishRunJsonEvidenceAsync(
            "phd2-guide-mode-transition",
            "PHD2 native off-slit selection exhausted; entering supervised direct-target fallback",
            new
            {
                code = "PHD2_OFF_SLIT_NATIVE_EXHAUSTED_DIRECT_TARGET_FALLBACK",
                from = Phd2SlitGuideMode.OffSlitGuideStar.ToString(),
                to = Phd2SlitGuideMode.DegradedDirectTargetGuiding.ToString(),
                exhausted.Attempts,
                exhausted.Rejections,
                exactLockOrMountMutationIssued = false,
                coordinatorRankedSubstituteUsed = false,
                supervisedScienceOptIn = true,
            },
            exhaustedOffSlitChoice.Field.FramePath,
            cancellationToken).ConfigureAwait(false);
        Report("warning：PHD2 原生旁星有界重选耗尽；已确认无 lock/mount 动作，切换 fresh 最短曝光直导目标");

        var directChoice = await CaptureAndSelectPhd2GuideAtExposureAsync(
            context,
            exhaustedOffSlitChoice.Field,
            preset,
            Phd2SlitGuideMode.DegradedDirectTargetGuiding,
            cancellationToken).ConfigureAwait(false);
        if (directChoice.Selection.Gate.Disposition != GateDisposition.Passed ||
            directChoice.Selection.Star is null)
        {
            throw new InvalidOperationException(
                $"{directChoice.Selection.Gate.Code}: Fresh direct-target fallback did not establish guide authority: {directChoice.Selection.Gate.Message}");
        }

        await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
        var loop = await phd2.StartLoopingAndWaitForFreshFrameAsync(
            new Phd2LoopingStartRequest(TimeSpan.FromSeconds(preset.FreshLoopFrameTimeoutSeconds)),
            cancellationToken).ConfigureAwait(false);
        if (!loop.LeavesLoopingForGuideTakeover || loop.StopCommandSent || loop.ExposureChanged)
            throw new InvalidOperationException("PHD2 direct-target fallback loop did not preserve the commissioned guide-takeover contract.");

        var binding = await ValidateG3FieldMountBindingForMotionAsync(
            context,
            directChoice.Field,
            cancellationToken).ConfigureAwait(false);
        if (binding.Disposition != GateDisposition.Passed)
        {
            await StopPhdAndWaitAsync(CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException($"{binding.Code}: {binding.Message}");
        }

        var selected = await SelectFreshPhd2GuideAsync(
            directChoice,
            preset,
            cancellationToken).ConfigureAwait(false);
        return new Phd2PreparedGuideSelection(
            directChoice,
            loop,
            selected.Selection,
            selected.Requested,
            selected.Selected);
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
        var capture = await CaptureG3FullFrameForAcquisitionAsync(
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
        else if (seedField.TargetIdentification.Authority == TargetIdentificationAuthority.CatalogWcsProjection)
        {
            var predictedTarget = seedField.TargetIdentification.Target?.Centroid
                ?? seedField.TargetIdentification.PredictedPoint;
            var topology = SaturatedTargetGhostTopologyAnalyzer.Analyze(
                frame,
                predictedTarget,
                preset.TargetSearchRadiusPixels);
            identification = topology.Gate.Disposition == GateDisposition.Passed && topology.Target is { } topologyTarget
                ? new TargetIdentification(
                    GateResult.Pass(
                        "PHD2_GUIDE_FRAME_CATALOG_TARGET_TOPOLOGY_REFINED",
                        $"Fresh {resolvedMode} retains mount-bound catalogue identity and refines its detector position from one filled saturated core; {topology.Ghosts.Count} hollow annular ghost(s) were excluded.",
                        topology.Gate.Metrics),
                    topologyTarget.Source,
                    predictedTarget,
                    topologyTarget.DistanceToPredictionPixels,
                    topology.UniquenessRatio,
                    TargetIdentificationAuthority.CatalogWcsProjection)
                : TargetIdentification.FromCatalogWcs(
                    predictedTarget,
                    properties.Width,
                    properties.Height,
                    $"Fresh {resolvedMode} selection retains the mount-bound catalogue-WCS target geometry; saturated local peaks and ghosts do not re-decide target identity. Saturated-topology diagnostic: {topology.Gate.Code}.");
        }
        else
        {
            var predictedTarget = seedField.TargetIdentification.Target?.Centroid
                ?? seedField.TargetIdentification.PredictedPoint;
            identification = SlitTargetIdentifier.Identify(
                frame,
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
                GateResult.Warn(
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

    private Phd2PierAdaptiveTopologyResolution ResolvePhd2RuntimeTopology(
        Phd2SlitPlacementCommissioningPreset preset,
        string currentPierSide)
    {
        var commissionedSource = BuildPhd2SensorTopology(preset, preset.PierSide);
        return Phd2PierAdaptiveTopologyPolicy.Resolve(
            commissionedSource,
            preset.LockedTopologyFingerprintSha256,
            currentPierSide,
            preset.CalibrationPierSideEvidenceComplete);
    }

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
            topology.ComputeFingerprintSha256(),
            pierSide,
            DateTimeOffset.UtcNow,
            PlateSolveRotationSeedDegrees: null,
            new Phd2LockShiftQualificationLimits(
                preset.CalibrationQualityPolicy.DegradedMaximumAge,
                Phd2CalibrationValidationFreshness(preset.CalibrationQualityPolicy),
                preset.CalibrationQualityPolicy.DegradedMaximumOrthogonalityErrorDegrees,
                preset.MinimumAxisRatePixelsPerSecond,
                preset.MaximumAxisRatePixelsPerSecond),
            quality));

    private static TimeSpan Phd2CalibrationValidationFreshness(
        Phd2CalibrationQualityPolicy policy)
    {
        // A G3 residual frame has a deliberately short motion-planning age,
        // but that is not the lifetime of the read-only PHD2 calibration
        // validation snapshot.  Reusing MaximumMeasurementAgeSeconds here
        // made a valid snapshot expire while the required multi-frame residual
        // window was still being captured (5 seconds at the commissioned
        // site).  Keep calibration validation in the same bounded freshness
        // envelope as the settle/residual evidence that grades it.
        var freshness = policy.MaximumSettleEvidenceAge <= policy.MaximumResidualEvidenceAge
            ? policy.MaximumSettleEvidenceAge
            : policy.MaximumResidualEvidenceAge;
        return freshness > TimeSpan.Zero ? freshness : TimeSpan.FromMinutes(5);
    }

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
        Phd2StateSnapshot snapshot,
        bool freshGuidingWindowAccepted = false,
        int freshGuidingSampleCount = 0) => new(
        $"settle-operation-{snapshot.LastSettleOperationId}",
        settle,
        snapshot.LastSettleCommandAccepted,
        SettleBeginObserved: snapshot.LastSettleOperationId.HasValue,
        SameConnectionEpoch: snapshot.LastSettleConnectionEpoch == snapshot.ConnectionEpoch,
        SameGuideEpoch: snapshot.LastSettleGuideEpoch == snapshot.GuideEpoch,
        DateTimeOffset.UtcNow,
        freshGuidingWindowAccepted,
        freshGuidingSampleCount);

    private Phd2CalibrationResidualEvidence CreateCalibrationResidualEvidence(
        Phd2GuidingResidualState residual,
        double guideLockResidual,
        Phd2SlitPlacementCommissioningPreset preset,
        Phd2SensorTopology topology,
        Phd2SlitGuideMode guideMode,
        double? maximumResidualOverridePixels = null) => new(
        residual.Frame.Sha256,
        residual.Frame.GuideStepUtc,
        guideLockResidual,
        maximumResidualOverridePixels ??
            (guideMode == Phd2SlitGuideMode.DegradedDirectTargetGuiding
                ? preset.MaximumDegradedDirectTargetGuideLockResidualPixels
                : preset.MaximumGuideLockResidualPixels),
        residual.Measurement.TargetIdentityConfirmed,
        string.Equals(residual.Measurement.TopologyFingerprintSha256, topology.ComputeFingerprintSha256(), StringComparison.OrdinalIgnoreCase),
        NoUnvalidatedCalibrationOrLockShiftAfterMeasurement: true,
        DateTimeOffset.UtcNow,
        IsSupervisedGuideLockResidual: guideMode == Phd2SlitGuideMode.OffSlitGuideStar &&
            HasSupervisedScienceOptIn() && residual.Measurement.GuidePositionMeasuredInFrame &&
            residual.Measurement.TargetPositionAuthority != Phd2TargetPositionAuthority.CatalogWcsProjection);

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
                    ? "PHD2 native full-frame/geometric-ROI find_star; coordinator validates the exact returned point and never ranks a substitute"
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

    private Phd2SettleCriteria Phd2SettleCriteriaForSlitPlacement(
        Phd2SlitPlacementCommissioningPreset preset)
    {
        var configured = Phd2SettleCriteriaFromConfiguration();
        if (!HasSupervisedScienceOptIn()) return configured;

        // An exact lock change needs time for the actual star to follow it.
        // The former three-second shortcut could expire after just one guide
        // correction and then repeatedly add the still-outstanding offset.
        // Keep the commissioned stage envelope, reserving 40% for new frames
        // and readbacks; neither spatial tolerances nor motion budgets change.
        return Phd2SlitSettleTiming.ForSupervisedPlacement(configured, preset.MaximumStageSeconds);
    }

    private bool CanReplaceSettleWithFreshGuidingWindow(
        Phd2SettleResult settle,
        Phd2StateSnapshot snapshot,
        long? requiredConnectionEpoch = null,
        long? requiredGuideEpoch = null) =>
        HasSupervisedScienceOptIn() &&
        !settle.Succeeded &&
        snapshot.IsConnected &&
        !snapshot.AutomationPaused &&
        !snapshot.Phd2Paused &&
        snapshot.AppState == Phd2AppState.Guiding &&
        snapshot.LastSettle == settle &&
        snapshot.LastSettleOperationId.HasValue &&
        snapshot.LastSettleCommandAccepted &&
        snapshot.LastSettleConnectionEpoch == snapshot.ConnectionEpoch &&
        snapshot.LastSettleGuideEpoch == snapshot.GuideEpoch &&
        (!requiredConnectionEpoch.HasValue || snapshot.ConnectionEpoch == requiredConnectionEpoch.Value) &&
        (!requiredGuideEpoch.HasValue || snapshot.GuideEpoch == requiredGuideEpoch.Value);

    private bool HasCurrentSupervisedGuidingWindow(Phd2SlitPlacementSession session, Phd2StateSnapshot snapshot) =>
        HasSupervisedScienceOptIn() && session.FreshGuidingWindowReplacedSettle &&
        (session.ReadOnlyPostLockObservation is { } observation
            ? observation.HasAcceptedWindow(snapshot)
            : CanReplaceSettleWithFreshGuidingWindow(session.Settle, snapshot, session.ConnectionEpoch, session.GuideEpoch));

    private async Task<IReadOnlyList<Phd2GuidingResidualState>> CapturePhd2PlacementGuideWindowAsync(
        ObservationContext context,
        Phd2SlitPlacementCommissioningPreset preset,
        Phd2SensorTopology topology,
        Phd2Point currentLock,
        Phd2Point expectedTarget,
        SlitGeometry runtimeSlitLocal,
        Phd2SlitGuideMode guideMode,
        int count,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken)
    {
        var baseline = phd2.Snapshot;
        const int maximumWindows = 4;
        var tolerance = preset.MaximumGuideLockResidualPixels;
        var freshWindowDeadlineExpired = false;
        for (var window = 1; window <= maximumWindows; window++)
        {
            var remaining = deadlineUtc - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                freshWindowDeadlineExpired = true;
                break;
            }
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(remaining);
            IReadOnlyList<Phd2GuidingResidualState> measurements;
            try
            {
                measurements = await CapturePhd2GuidingMeasurementsAsync(
                    context, preset, topology, currentLock, expectedTarget,
                    runtimeSlitLocal, guideMode, count, bounded.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                freshWindowDeadlineExpired = true;
                break;
            }
            var snapshot = phd2.Snapshot;
            var sameEpochAndLock = snapshot.IsConnected && snapshot.AppState == Phd2AppState.Guiding &&
                snapshot.ConnectionEpoch == baseline.ConnectionEpoch && snapshot.GuideEpoch == baseline.GuideEpoch &&
                snapshot.LockPosition is { } actual && PointDistance(actual, currentLock) <= preset.LockVerificationTolerancePixels;
            var residuals = measurements.Select(item => PointDistance(item.Measurement.GuideStar, currentLock)).ToArray();
            var withinAdvisoryThreshold = Phd2PlacementGuideWindowPolicy.AllWithinTolerance(residuals, tolerance);
            var supervisedMeasuredGeometry = HasSupervisedScienceOptIn() && measurements.All(item =>
                item.Measurement.GuidePositionMeasuredInFrame &&
                item.Measurement.TargetPositionAuthority != Phd2TargetPositionAuthority.CatalogWcsProjection) &&
                residuals.All(double.IsFinite);
            var accepted = sameEpochAndLock &&
                (guideMode != Phd2SlitGuideMode.OffSlitGuideStar ||
                 withinAdvisoryThreshold || supervisedMeasuredGeometry);
            var guideTrackingWarning = accepted && !withinAdvisoryThreshold &&
                guideMode == Phd2SlitGuideMode.OffSlitGuideStar;
            await PublishRunJsonEvidenceAsync(
                "phd2-placement-guide-window",
                "Fresh guide-position window checked without issuing another lock or guide command",
                new { accepted, guideTrackingWarning, withinAdvisoryThreshold, supervisedMeasuredGeometry,
                    window, maximumWindows, residuals, tolerance, sameEpochAndLock,
                    deadlineUtc, lockMutation = false, guidingRestarted = false, budgetReset = false },
                measurements[^1].Frame.Path, cancellationToken).ConfigureAwait(false);
            if (!sameEpochAndLock)
                throw new InvalidOperationException("PHD2_GUIDE_WINDOW_EPOCH_CHANGED: The guide epoch or lock changed during read-only resampling.");
            if (accepted)
            {
                if (guideTrackingWarning)
                    Report($"导星警告：本帧导星偏差 {string.Join(", ", residuals.Select(value => value.ToString("F2", CultureInfo.InvariantCulture)))}px 超过 {tolerance:F2}px 建议值；按同帧实测星位继续有人监督精调，目标入缝残差独立判断。");
                return measurements;
            }
            var latest = measurements[^1];
            lastG3Field = UpdateG3FieldFromGuidingResidual(
                lastG3Field ?? throw new InvalidOperationException("G3 identity chain was discarded during the guide window."),
                latest, preset);
            expectedTarget = latest.Measurement.TargetCentroid;
            runtimeSlitLocal = latest.RuntimeSlitLocal;
            Report($"PHD2 导星残差窗口 {window}/{maximumWindows}（{string.Join(", ", residuals.Select(value => value.ToString("F2", CultureInfo.InvariantCulture)))}px）未全部满足 {tolerance:F2}px；在剩余时间内保持锁点，等待整组新帧。");
        }
        if (freshWindowDeadlineExpired)
            throw new InvalidOperationException(
                "PHD2_FRESH_GUIDE_WINDOW_DEADLINE: The unchanged post-lock stage deadline expired before a complete fresh optical window was available; this is not evidence that a complete window failed the guide precision threshold.");
        throw new InvalidOperationException(
            "PHD2_GUIDE_WINDOW_NOT_STABLE: The bounded same-lock fresh-frame windows did not all meet the commissioned guide residual; no extra lock movement or budget reset was allowed.");
    }

    private async Task VerifyWindSampledGuidingBeforeAtrAsync(
        ObservationContext context,
        CancellationToken cancellationToken)
    {
        var session = phd2SlitPlacementSession;
        if (session is null || (!session.FreshGuidingWindowReplacedSettle &&
            !configuration.AllowSupervisedSlitQualityWarning)) return;
        if (!IsGuidingStable() || lastG3Field is null)
            throw new PhysicalActionGateException(GateResult.Unknown(
                "PHD2_SCIENCE_GUIDE_EPOCH_CHANGED", "The supervised guiding epoch changed before the ATR exposure; no new exposure was started."));
        var preset = commissioning?.Value.Phd2SlitPlacement
            ?? throw new InvalidOperationException("PHD2 science-frame commissioning is missing.");
        var currentLock = await phd2.GetLockPositionAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("PHD2 science-frame lock readback is unavailable.");
        var tolerance = preset.BuildMotionLimits().TargetOnSlitTolerancePixels *
            session.Quality.RequiredResidualToleranceScale;
        const int maximumWindows = 4;
        var readoutGrace = new Phd2ReadOnlyFrameGraceBudget();
        for (var windowAttempt = 1; windowAttempt <= maximumWindows; windowAttempt++)
        {
            // A rejected optical sample does not imply lost lock. Keep the
            // same owner/guide epoch and wait for a bounded new window, without
            // moving the lock, restarting capture, or relaxing the tolerance.
            var measurements = await CapturePhd2GuidingMeasurementsAsync(
                context, preset, session.Topology, currentLock,
                session.LastMeasurement.Measurement.TargetCentroid,
                session.LastMeasurement.RuntimeSlitLocal, session.GuideMode,
                Math.Max(3, preset.CalibrationQualityPolicy.RequiredFreshResidualsPerLockShiftStage),
                cancellationToken, readoutGrace).ConfigureAwait(false);
            var residuals = measurements.Select(item => PointDistance(
                item.Measurement.TargetCentroid, item.Measurement.RecognizedSlitAcquisitionPoint)).ToArray();
            var snapshot = phd2.Snapshot;
            var sameEpoch = IsGuidingStable() &&
                snapshot.ConnectionEpoch == session.ConnectionEpoch && snapshot.GuideEpoch == session.GuideEpoch;
            var identityConfirmed = measurements.All(item => item.Measurement.TargetIdentityConfirmed);
            var precisionPassed = sameEpoch && identityConfirmed && double.IsFinite(tolerance) && tolerance > 0 &&
                residuals.All(value => double.IsFinite(value) && value <= tolerance);
            var precisionWarning = !precisionPassed && configuration.AllowSupervisedSlitQualityWarning &&
                HasSupervisedScienceOptIn() && sameEpoch && identityConfirmed &&
                measurements.All(item => item.Measurement.GuidePositionMeasuredInFrame &&
                    item.Measurement.TargetPositionAuthority != Phd2TargetPositionAuthority.CatalogWcsProjection) &&
                Phd2PlacementGuideWindowPolicy.AllWithinTolerance(residuals, preset.MaximumAcquisitionResidualPixels);
            var accepted = precisionPassed || precisionWarning;
            await PublishRunJsonEvidenceAsync(
                "phd2-supervised-pre-atr-window",
                "Fresh same-epoch guiding frames checked immediately before the supervised ATR exposure",
                new { accepted, precisionPassed, precisionWarning,
                    operatorConsent = configuration.AllowSupervisedSlitQualityWarning,
                    windowAttempt, maximumWindows, residuals, tolerance,
                    session.ConnectionEpoch, session.GuideEpoch,
                    unattendedAuthority = false, guidingStopped = false, newLockMotion = false },
                measurements[^1].Frame.Path, cancellationToken).ConfigureAwait(false);
            if (accepted)
            {
                var last = measurements[^1];
                phd2SlitPlacementSession = session with
                {
                    LastMeasurement = last,
                    SlitPrecisionWarningActive = session.SlitPrecisionWarningActive || precisionWarning,
                };
                lastG3Field = UpdateG3FieldFromGuidingResidual(lastG3Field, last, preset);
                if (precisionWarning)
                    Report($"ATR 监督试拍警告：实测入缝残差 {string.Join(", ", residuals.Select(value => value.ToString("F2", CultureInfo.InvariantCulture)))} px；保留全部测量，按实际光谱信号评估，不声明精确入缝。");
                return;
            }
            if (!sameEpoch || !identityConfirmed || windowAttempt == maximumWindows)
                throw new PhysicalActionGateException(GateResult.Unknown(
                    "PHD2_SCIENCE_FRESH_SLIT_WINDOW_REJECTED",
                    $"Fresh supervised pre-exposure window {windowAttempt}/{maximumWindows} residuals ({string.Join(", ", residuals.Select(value => value.ToString("F2", CultureInfo.InvariantCulture)))}) did not all meet {tolerance:F2}px with valid target identity in the same guiding epoch; no ATR exposure was started."));
            Report($"ATR 曝光前遇到短时风扰：第 {windowAttempt}/{maximumWindows} 组新帧未全部满足 {tolerance:F2}px；保持 PHD2 导星，重采下一组，不移动锁点");
        }
    }

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
        var binding = ValidatePhd2LockLedgerOperationalBinding(context, preset, state);
        if (binding.Disposition != GateDisposition.Passed) return binding;
        return SameHash(state.RecoveryContextSha256, ComputeSlitRecoveryContextSha256(context))
            ? GateResult.Pass("PHD2_LOCK_LEDGER_BINDING_VALID", "The canonical PHD2 lock ledger matches the immutable run/config/context/policy/topology bindings.")
            : GateResult.Unknown("PHD2_LOCK_LEDGER_BINDING_CHANGED", "Durable PHD2 lock lineage cannot continue outbound placement because the target/site/horizon/Night-Setup/telescope context changed.");
    }

    private GateResult ValidateForeignPhd2LockRecoveryBinding(
        ObservationContext context,
        Phd2SlitPlacementCommissioningPreset preset,
        Phd2LockShiftPendingState state)
    {
        var resolution = ResolvePhd2RuntimeTopology(preset, telescopeMediator.GetInfo().SideOfPier.ToString());
        var zeroVectorAcrossPier = resolution.IsAllowed && resolution.RuntimeTopology is { } topology &&
            Phd2ZeroVectorPierRecoveryPolicy.CanVerifyWithoutMotion(state, topology);
        var binding = ValidatePhd2LockLedgerOperationalBinding(context, preset, state,
            requireMatchingTopology: !zeroVectorAcrossPier);
        return binding.Disposition == GateDisposition.Passed
            ? GateResult.Pass(
                "PHD2_LOCK_FOREIGN_RECOVERY_BINDING_VALID",
                "The foreign PHD2 return debt retains its authenticated source manifest and matches the current action/configuration, policy, topology and bounded-motion contracts; the current target context will be freshly reacquired before recovery motion.")
            : binding;
    }

    private GateResult ValidatePhd2LockLedgerOperationalBinding(
        ObservationContext context,
        Phd2SlitPlacementCommissioningPreset preset,
        Phd2LockShiftPendingState state,
        bool requireMatchingTopology = true)
    {
        var failures = new List<string>();
        if (!SameHash(state.ActionConfigurationSha256, configuration.ActionConfigurationSha256)) failures.Add("action configuration hash changed");
        if (!SameHash(state.CommissioningPresetSha256, commissioning!.Sha256)) failures.Add("commissioning preset hash changed");
        if (!string.Equals(state.CalibrationQualityPolicyId, preset.CalibrationQualityPolicy.PolicyId, StringComparison.Ordinal) ||
            !SameHash(state.CalibrationQualityPolicySha256, preset.CalibrationQualityPolicySha256)) failures.Add("calibration-quality policy changed");
        var currentPierSide = telescopeMediator.GetInfo().SideOfPier.ToString();
        var topologyResolution = ResolvePhd2RuntimeTopology(preset, currentPierSide);
        if (!topologyResolution.IsAllowed || topologyResolution.RuntimeTopology is null)
            failures.Add($"runtime topology unavailable: {topologyResolution.Message}");
        else if (requireMatchingTopology && !SameHash(state.TopologyFingerprintSha256, topologyResolution.RuntimeTopology.ComputeFingerprintSha256()))
            failures.Add("sensor topology or operation pier side changed");
        if (Math.Abs(state.MaximumStagePixels - preset.MaximumStagePixels) > 1e-9 ||
            Math.Abs(state.MaximumCumulativePixels - preset.MaximumCumulativePixels) > 1e-9 ||
            state.MaximumAttempts != preset.MaximumAttempts ||
            Math.Abs(state.MaximumElapsedSeconds - preset.MaximumElapsedSeconds) > 1e-9) failures.Add("bounded-motion limits changed");
        return failures.Count == 0
            ? GateResult.Pass("PHD2_LOCK_LEDGER_OPERATIONAL_BINDING_VALID", "The PHD2 lock ledger matches the current action/configuration, policy, topology and bounded-motion contracts.")
            : GateResult.Unknown("PHD2_LOCK_LEDGER_BINDING_CHANGED", $"Durable PHD2 lock lineage cannot be operated: {string.Join("; ", failures)}.");
    }

    private async Task<GateResult> PersistCurrentRunPhd2BudgetHandoffAsync(
        ObservationContext context,
        Phd2LockShiftPendingState settledForeignState,
        string currentTopologyFingerprintSha256,
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
                DateTimeOffset.UtcNow,
                currentTopologyFingerprintSha256);
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
                currentRecoveryContext,
                currentTopologyFingerprintSha256);
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

    private bool IsStructuredPhd2GuideSessionLoss(Exception failure)
    {
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            if (current is Phd2DisconnectedException) return true;
        }

        var snapshot = phd2.Snapshot;
        return !snapshot.IsConnected || snapshot.AppState == Phd2AppState.LostLock;
    }

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

internal static class Phd2FreshSlitFrameRetryPolicy
{
    // Fresh guiding-frame capture is read-only with respect to the PHD2 lock and
    // mount. Two extra frames cover a transient wind/seeing threshold crossing
    // without turning an unavailable or persistently obscured slit into a loop.
    internal const int MaximumAdditionalFreshFrames = 2;

    internal static int MaximumCaptureAttempts(int requiredFreshResiduals)
    {
        if (requiredFreshResiduals <= 0)
            throw new ArgumentOutOfRangeException(nameof(requiredFreshResiduals));
        return checked(requiredFreshResiduals + MaximumAdditionalFreshFrames);
    }

    internal static bool CanRetry(
        GateResult slitGate,
        int captureAttempts,
        int acceptedFreshResiduals,
        int requiredFreshResiduals)
    {
        ArgumentNullException.ThrowIfNull(slitGate);
        if (captureAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(captureAttempts));
        if (acceptedFreshResiduals < 0 || acceptedFreshResiduals >= requiredFreshResiduals)
            throw new ArgumentOutOfRangeException(nameof(acceptedFreshResiduals));
        var remainingAttempts = MaximumCaptureAttempts(requiredFreshResiduals) - captureAttempts;
        var remainingAcceptedFrames = requiredFreshResiduals - acceptedFreshResiduals;
        return string.Equals(slitGate.Code, "SLIT_LOCUS_LOW_CONFIDENCE", StringComparison.Ordinal) &&
               remainingAttempts >= remainingAcceptedFrames;
    }
}

internal sealed record Phd2FreshSlitTemporalSample(
    string FrameSha256,
    long TriggerGuideFrame,
    long EventSequence,
    DateTimeOffset GuideStepUtc,
    bool GuidingWasInterrupted,
    bool ExposureChanged,
    bool CaptureLoopStarted,
    G3FieldMountBinding MountBinding,
    SlitLocusDetection Detection);

internal sealed record Phd2FreshSlitTemporalConsensus(
    GateResult Gate,
    SlitLocusDetection? Detection,
    double MinimumMemberContrastSigma,
    double MedianMemberContrastSigma,
    double MaximumPointSpanPixels,
    double AngleSpanDegrees,
    double MaximumMountSpanArcseconds);

/// <summary>
/// Bounded rescue for a physical dark slit that is persistent but falls just
/// below the commissioned single-frame contrast threshold.  It never lowers
/// the ordinary one-frame gate: three consecutive immutable guide frames must
/// independently recover the same same-run LED-authorized line, while PHD2's
/// guide epoch, lock, exposure and the mount position remain unchanged.
/// </summary>
internal static class Phd2FreshSlitTemporalConsensusPolicy
{
    internal const int RequiredConsecutiveFrames = 3;
    internal const double MinimumMemberThresholdFraction = 5d / 6d;
    internal const double MinimumMedianThresholdFraction = 0.9d;
    // The commissioned 15 µm aperture measures 2.5 px wide.  Consensus must
    // remain within one physical slit width across all three detections.
    internal const double MaximumPointSpanPixels = 2.5d;
    internal const double MaximumAngleSpanDegrees = 2d;

    internal static Phd2FreshSlitTemporalConsensus Evaluate(
        IReadOnlyList<Phd2FreshSlitTemporalSample> samples,
        SlitGeometry authorizedSeed,
        double singleFrameThresholdSigma,
        double maximumMountSpanArcseconds,
        bool sameRunSlitSeedAuthorized,
        bool guideEpochAndLockStayedFixed)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(authorizedSeed);
        if (!double.IsFinite(singleFrameThresholdSigma) || singleFrameThresholdSigma <= 0)
            throw new ArgumentOutOfRangeException(nameof(singleFrameThresholdSigma));
        if (!double.IsFinite(maximumMountSpanArcseconds) || maximumMountSpanArcseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumMountSpanArcseconds));

        Phd2FreshSlitTemporalConsensus Unknown(string code, string message, IReadOnlyDictionary<string, double>? metrics = null) =>
            new(GateResult.Unknown(code, message, metrics), null, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);

        if (!sameRunSlitSeedAuthorized)
        {
            return Unknown(
                "PHD2_FRESH_SLIT_CONSENSUS_SEED_UNAUTHORIZED",
                "Temporal slit consensus requires the current run's passed LED differential geometry and passed slit-wheel identity.");
        }
        if (!guideEpochAndLockStayedFixed)
        {
            return Unknown(
                "PHD2_FRESH_SLIT_CONSENSUS_GUIDE_EPOCH_CHANGED",
                "PHD2's guiding epoch or verified lock changed during fresh slit reacquisition.");
        }
        if (samples.Count != RequiredConsecutiveFrames)
        {
            return Unknown(
                "PHD2_FRESH_SLIT_CONSENSUS_FRAME_COUNT",
                $"Temporal slit consensus requires exactly {RequiredConsecutiveFrames} consecutive low-confidence fresh frames; received {samples.Count}.",
                new Dictionary<string, double>
                {
                    ["freshFrameCount"] = samples.Count,
                    ["requiredFreshFrameCount"] = RequiredConsecutiveFrames,
                });
        }

        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            if (string.IsNullOrWhiteSpace(sample.FrameSha256) || !hashes.Add(NormalizeHash(sample.FrameSha256)) ||
                !string.Equals(sample.FrameSha256, sample.MountBinding.FrameSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(sample.MountBinding.BindingSha256, sample.MountBinding.ComputeBindingSha256(), StringComparison.OrdinalIgnoreCase))
            {
                return Unknown(
                    "PHD2_FRESH_SLIT_CONSENSUS_FRAME_IDENTITY_INVALID",
                    "Temporal slit consensus requires three distinct immutable FITS hashes with valid capture-time mount bindings.");
            }
            if (sample.GuidingWasInterrupted || sample.ExposureChanged || sample.CaptureLoopStarted)
            {
                return Unknown(
                    "PHD2_FRESH_SLIT_CONSENSUS_CAPTURE_MUTATED",
                    "A candidate frame interrupted guiding or changed PHD2 exposure/capture state.");
            }
            if (!string.Equals(sample.Detection.Gate.Code, "SLIT_LOCUS_LOW_CONFIDENCE", StringComparison.Ordinal) ||
                !double.IsFinite(sample.Detection.ContrastSigma) ||
                !double.IsFinite(sample.Detection.Geometry.AcquisitionPoint.X) ||
                !double.IsFinite(sample.Detection.Geometry.AcquisitionPoint.Y) ||
                !double.IsFinite(sample.Detection.Geometry.AngleDegrees))
            {
                return Unknown(
                    "PHD2_FRESH_SLIT_CONSENSUS_MEMBER_INVALID",
                    "Every temporal-consensus member must be a finite, independently detected low-confidence slit candidate.");
            }
            if (index == 0) continue;
            var previous = samples[index - 1];
            if (sample.TriggerGuideFrame <= previous.TriggerGuideFrame ||
                sample.EventSequence <= previous.EventSequence ||
                sample.GuideStepUtc <= previous.GuideStepUtc)
            {
                return Unknown(
                    "PHD2_FRESH_SLIT_CONSENSUS_ORDER_INVALID",
                    "Fresh slit candidates are not strictly ordered by PHD2 guide frame, event sequence and timestamp.");
            }
        }

        var firstBinding = samples[0].MountBinding;
        if (samples.Any(sample =>
                !string.Equals(sample.MountBinding.ObservationRunId, firstBinding.ObservationRunId, StringComparison.Ordinal) ||
                !string.Equals(sample.MountBinding.ActionConfigurationSha256, firstBinding.ActionConfigurationSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(sample.MountBinding.CommissioningPresetSha256, firstBinding.CommissioningPresetSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(sample.MountBinding.CoordinateEpoch, firstBinding.CoordinateEpoch, StringComparison.Ordinal) ||
                !string.Equals(sample.MountBinding.PierSide, firstBinding.PierSide, StringComparison.OrdinalIgnoreCase)))
        {
            return Unknown(
                "PHD2_FRESH_SLIT_CONSENSUS_CONTEXT_CHANGED",
                "The observation run, action configuration, commissioning preset, coordinate epoch or pier side changed between candidate frames.");
        }

        var maximumMountSpan = 0d;
        var maximumPointSpan = 0d;
        for (var left = 0; left < samples.Count; left++)
        for (var right = left + 1; right < samples.Count; right++)
        {
            maximumMountSpan = Math.Max(
                maximumMountSpan,
                G3AcquisitionMotionPlanner.AngularSeparationArcseconds(
                    samples[left].MountBinding.RightAscensionDegrees,
                    samples[left].MountBinding.DeclinationDegrees,
                    samples[right].MountBinding.RightAscensionDegrees,
                    samples[right].MountBinding.DeclinationDegrees));
            maximumPointSpan = Math.Max(
                maximumPointSpan,
                Distance(
                    samples[left].Detection.Geometry.AcquisitionPoint,
                    samples[right].Detection.Geometry.AcquisitionPoint));
        }
        var minimumContrast = samples.Min(sample => sample.Detection.ContrastSigma);
        var medianContrast = Median(samples.Select(sample => sample.Detection.ContrastSigma));
        var minimumMemberContrast = singleFrameThresholdSigma * MinimumMemberThresholdFraction;
        var minimumMedianContrast = singleFrameThresholdSigma * MinimumMedianThresholdFraction;
        var minimumAngle = samples.Min(sample => sample.Detection.Geometry.AngleDegrees);
        var maximumAngle = samples.Max(sample => sample.Detection.Geometry.AngleDegrees);
        var angleSpan = maximumAngle - minimumAngle;
        var metrics = new Dictionary<string, double>
        {
            ["freshFrameCount"] = samples.Count,
            ["minimumMemberContrastSigma"] = minimumContrast,
            ["medianMemberContrastSigma"] = medianContrast,
            ["normalSingleFrameThresholdSigma"] = singleFrameThresholdSigma,
            ["minimumConsensusMemberContrastSigma"] = minimumMemberContrast,
            ["minimumConsensusMedianContrastSigma"] = minimumMedianContrast,
            ["maximumSlitPointSpanPixels"] = maximumPointSpan,
            ["maximumAllowedSlitPointSpanPixels"] = MaximumPointSpanPixels,
            ["slitAngleSpanDegrees"] = angleSpan,
            ["maximumAllowedSlitAngleSpanDegrees"] = MaximumAngleSpanDegrees,
            ["mountSpanArcseconds"] = maximumMountSpan,
            ["maximumAllowedMountSpanArcseconds"] = maximumMountSpanArcseconds,
        };
        if (!double.IsFinite(maximumMountSpan) || maximumMountSpan > maximumMountSpanArcseconds + 1e-9 ||
            minimumContrast + 1e-9 < minimumMemberContrast ||
            medianContrast + 1e-9 < minimumMedianContrast ||
            maximumPointSpan > MaximumPointSpanPixels + 1e-9 ||
            angleSpan > MaximumAngleSpanDegrees + 1e-9)
        {
            return new Phd2FreshSlitTemporalConsensus(
                GateResult.Unknown(
                    "PHD2_FRESH_SLIT_CONSENSUS_NOT_ESTABLISHED",
                    "Three fresh frames did not satisfy the bounded contrast, detector-locus, angle and mount-stability consensus gates.",
                    metrics),
                null,
                minimumContrast,
                medianContrast,
                maximumPointSpan,
                angleSpan,
                maximumMountSpan);
        }

        var centerX = Median(samples.Select(sample => sample.Detection.Geometry.AcquisitionPoint.X));
        var centerY = Median(samples.Select(sample => sample.Detection.Geometry.AcquisitionPoint.Y));
        var centerAngle = Median(samples.Select(sample => sample.Detection.Geometry.AngleDegrees));
        var center = new PixelPoint(centerX, centerY);
        var maximumCenterResidual = samples.Max(sample => Distance(sample.Detection.Geometry.AcquisitionPoint, center));
        var geometry = authorizedSeed with
        {
            AcquisitionPoint = center,
            AngleDegrees = centerAngle,
            UncertaintyPixels = Math.Max(authorizedSeed.UncertaintyPixels, maximumCenterResidual),
        };
        var gate = GateResult.Pass(
            "PHD2_FRESH_SLIT_TEMPORAL_CONSENSUS",
            $"Three independent fresh guide frames preserved the same LED-authorized physical slit: minimum {minimumContrast:F2}σ, median {medianContrast:F2}σ, point span {maximumPointSpan:F2}px, angle span {angleSpan:F2}°, mount span {maximumMountSpan:F2} arcsec.",
            metrics);
        return new Phd2FreshSlitTemporalConsensus(
            gate,
            new SlitLocusDetection(
                gate,
                geometry,
                medianContrast,
                Median(samples.Select(sample => sample.Detection.PerpendicularOffsetPixels)),
                centerAngle - authorizedSeed.AngleDegrees),
            minimumContrast,
            medianContrast,
            maximumPointSpan,
            angleSpan,
            maximumMountSpan);
    }

    private static string NormalizeHash(string value) =>
        value.Replace("-", string.Empty, StringComparison.Ordinal).Trim();

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        var center = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[center - 1] + ordered[center]) / 2d
            : ordered[center];
    }

    private static double Distance(PixelPoint left, PixelPoint right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

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

internal sealed class Phd2NativeGuideSelectionExhaustedException : Exception
{
    public const string FailureCode = "PHD2_NATIVE_GUIDE_RESELECTION_EXHAUSTED";

    public Phd2NativeGuideSelectionExhaustedException(
        int attempts,
        IReadOnlyList<string> rejections)
        : base($"{FailureCode}: PHD2 could not produce a guide outside the detector-edge, target/halo and physical-slit guards after {attempts} fresh-frame attempts. {string.Join(" | ", rejections)}")
    {
        Attempts = attempts;
        Rejections = rejections;
    }

    public int Attempts { get; }

    public IReadOnlyList<string> Rejections { get; }
}

internal sealed record Phd2PreparedGuideSelection(
    Phd2PlacementGuideChoice Choice,
    Phd2LoopingStartResult Loop,
    GuideStarSelection Selection,
    Phd2Point Requested,
    Phd2Point Selected);

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
    bool ForcedRecalibration,
    bool FreshGuidingWindowReplacedSettle = false,
    bool SlitPrecisionWarningActive = false,
    Phd2PostLockGuidingObservation? ReadOnlyPostLockObservation = null);
