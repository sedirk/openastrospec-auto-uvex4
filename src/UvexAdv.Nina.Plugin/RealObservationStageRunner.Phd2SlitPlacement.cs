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
    private bool placementGuidePrecisionWarningActive;
    private sealed record Phd2HomeBoundaryProof(string RunId, string TelescopeId, DateTimeOffset VerifiedUtc, string EvidencePath);
    private Phd2HomeBoundaryProof? phd2HomeBoundaryProof;
    private Phd2RuntimeCalibrationProof? localPhd2CalibrationProof;
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
            if (!calibrationBefore.IsValid)
                return Attention(ObservationStage.StartGuiding, "PHD2_OWNER_CALIBRATION_UNAVAILABLE",
                    string.Join(" ", calibrationBefore.Failures.Concat(calibrationBefore.IndeterminateReasons)));
            ReportPhd2OwnerCalibration(calibrationBefore);
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
                var prepared = await PrepareSnrOptimizedGuideSelectionAsync(
                    context, choice, preset, loop, cancellationToken).ConfigureAwait(false);
                choice = prepared.Choice;
                lastG3Field = choice.Field;
                target = choice.Field.TargetIdentification.Target!;
                loop = prepared.Loop;
                guideSelectionResult = (prepared.Selection, prepared.Requested, prepared.Selected);
            }
            catch (Phd2NativeGuideSelectionExhaustedException exhausted)
            {
                await StopPhdAndWaitAsync(CancellationToken.None).ConfigureAwait(false);
                if (preset.GuideMode != Phd2SlitGuideMode.AutoPreferOffSlitThenDirectTarget ||
                    choice.Mode != Phd2SlitGuideMode.OffSlitGuideStar)
                {
                    return exhausted.AfterCheckedStop();
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
            // calibration. Identity/owner-readback failure still stops; numeric
            // calibration quality is advisory under ADR-0020.
            calibrationBefore = await phd2.ValidateCalibrationAsync(
                policy.ApplyHardRejectionCeilings(PhdCalibrationRequirement()),
                cancellationToken).ConfigureAwait(false);
            if (!calibrationBefore.IsValid)
            {
                await StopPhdAndWaitAsync(cancellationToken).ConfigureAwait(false);
                return Attention(ObservationStage.StartGuiding, "PHD2_OWNER_CALIBRATION_UNAVAILABLE",
                    string.Join(" ", calibrationBefore.Failures.Concat(calibrationBefore.IndeterminateReasons)));
            }
            Volatile.Write(ref phd2GuidingEverStarted, 1);
            var settle = await phd2.GuideAndSettleAsync(
                Phd2SettleCriteriaForSlitPlacement(preset),
                forceRecalibration: false,
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
                PhdCalibrationRequirement(),
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
                cancellationToken, referenceGuide: selected,
                referenceTargetMeasured: Phd2FreshTargetFluxPolicy.HasMeasuredHandoffPosition(choice.Field.TargetIdentification)).ConfigureAwait(false);
            var measurement = measurements[^1];
            var guideResidual = PointDistance(measurement.Measurement.GuideStar, lockPosition);
            var post = SelectPhd2CalibrationQuality(
                calibration,
                preset,
                Phd2CalibrationEvaluationPhase.PostSettle,
                CreateCalibrationSettleEvidence(settle, proof, windSampledSettle, measurements.Count),
                Phd2PlacementGuideWindowPolicy.CalibrationResidualEvidence(
                    measurement, guideResidual, topology, DateTimeOffset.UtcNow),
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
                false,
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
            return ObservationWorkflowResults.WithFailureContext(reacquired,
                "The old PHD2 lock debt was safely returned and settled, but the bounded fresh G3/PL3/slit rebuild did not pass.");
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
        // ADR-0027 migration: authenticate the old target's ORIGINAL bindings,
        // not today's action hash. Retirement grants no movement or acceptance.
        if (source?.TerminalState is ObservationRunState.Completed or ObservationRunState.Faulted or ObservationRunState.Cancelled &&
            GuideStateBoundaryRetirement.IsDifferentLogicalTarget(source, context.Plan.ObservationRunId, LockedMetadata) &&
            lastG3Field?.Gate.Disposition == GateDisposition.Passed && lastG3Field.TargetIdentification.Target is not null &&
            (await ValidateG3FieldMountBindingForMotionAsync(context, lastG3Field, cancellationToken).ConfigureAwait(false)).Disposition == GateDisposition.Passed)
        {
            await GuideStateBoundaryRetirement.RetireAsync(SlitPlacementObservationsRoot(), source.ObservationRunId,
                ReadTerminalGuideWitnessAsync, cancellationToken).ConfigureAwait(false);
            Report("旧目标已结束且设备停止已确认：旧导星记录归档为历史，未冒充回位；本目标使用刚完成的新解算与定位。");
            return true;
        }
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
        Phd2SlitPlacementCommissioning preset,
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
        await PublishRunJsonEvidenceAsync(
            "phd2-lock-recovery-calibration-readback",
            "Fresh PHD2 owner readback with advisory calibration metadata before recovery guide",
            new { state.LineageId, calibration = activeCalibrationBeforeGuide,
                originalMotionLedgerPreserved = true, guideOrRecalibrationCommandSent = false },
            item.Path, cancellationToken).ConfigureAwait(false);
        if (activeCalibrationBeforeGuide.Status != Phd2ValidationStatus.Valid)
        {
            // PHD2_LOCK_RECOVERY_PRE_GUIDE_CALIBRATION_INVALID is old-debt
            // recovery, not a numerical calibration-quality rejection.
            return new StageResult(Phd2RecoveryCalibrationPresentation.Blocked(
                activeCalibrationBeforeGuide, state.ObservationRunId, state));
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
            var selected = await SelectFreshPhd2GuideAsync(
                guideChoice,
                preset,
                cancellationToken).ConfigureAwait(false);
            guideSelectionResult = (selected.Selection, selected.Requested, selected.Selected);
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
            forceRecalibration: false,
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
            cancellationToken, referenceGuide: selectedGuide,
            referenceTargetMeasured: Phd2FreshTargetFluxPolicy.HasMeasuredHandoffPosition(lastG3Field.TargetIdentification)).ConfigureAwait(false);
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
            Phd2PlacementGuideWindowPolicy.CalibrationResidualEvidence(
                initial, PointDistance(initial.Measurement.GuideStar, freshLock), topology, DateTimeOffset.UtcNow),
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
                    initialTarget,
                    runtimeSlit,
                    guideChoice.Mode,
                    requiredFreshResiduals,
                    token, referenceGuide: initial.Measurement.GuideStar).ConfigureAwait(false);
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
        // Each placement attempt needs its own locally completed guide RPC;
        // a new attempt/connection must never inherit an earlier stop receipt.
        phd2CompletedInitialGuideStopReceipt = null;
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
                var continuationLock = phd2.IsConnected
                    ? await phd2.GetLockPositionAsync(cancellationToken).ConfigureAwait(false) : null;
                if (phd2SlitPlacementSession is { } settledSession &&
                    Phd2ScienceContinuationPolicy.CanObserveAtSettledLock(currentLedger, phd2.Snapshot,
                        settledSession.ConnectionEpoch, settledSession.GuideEpoch,
                        phd2.Snapshot.HasCurrentSuccessfulSettle || HasCurrentSupervisedGuidingWindow(settledSession, phd2.Snapshot),
                        continuationLock, preset.BuildMotionLimits().LockVerificationTolerancePixels))
                {
                    await VerifyWindSampledGuidingBeforeAtrAsync(context, cancellationToken, forceFreshWindow: true).ConfigureAwait(false);
                    settledSession = phd2SlitPlacementSession!;
                    var residual = PointDistance(settledSession.LastMeasurement.Measurement.TargetCentroid, settledSession.LastMeasurement.Measurement.RecognizedSlitAcquisitionPoint);
                    return Passed(
                        "PHD2_LOCK_LEDGER_ALREADY_SETTLED",
                        $"The current run already completed PHD2 lock lineage {currentLedger.LineageId}; consumed attempts/pixels remain {currentLedger.AttemptsUsed}/{currentLedger.CumulativeCommandedPixels:F3} and were not reset.",
                        Phd2EffectiveQualityMetrics(settledSession.Quality, settledSession.GuideMode, settledSession.SelectedGuide, settledSession.Settle, residual),
                        Metadata(loaded));
                }
                var elapsedSeconds = Math.Max(0, (DateTimeOffset.UtcNow - currentLedger.MotionClockStartedUtc).TotalSeconds);
                if (currentLedger.AttemptsUsed >= currentLedger.MaximumAttempts ||
                    currentLedger.CumulativeCommandedPixels >= currentLedger.MaximumCumulativePixels - 1e-9 ||
                    elapsedSeconds >= currentLedger.MaximumElapsedSeconds)
                {
                    return Attention(
                        ObservationStage.PlaceTargetOnSlit,
                        "PHD2_LOCK_INHERITED_BUDGET_EXHAUSTED",
                        $"This run's settled PHD2 lineage {currentLedger.LineageId} cannot fund another placement: " +
                        $"attempts {currentLedger.AttemptsUsed}/{currentLedger.MaximumAttempts}, " +
                        $"motion {currentLedger.CumulativeCommandedPixels:F2}/{currentLedger.MaximumCumulativePixels:F2} px, " +
                        $"elapsed {elapsedSeconds:F1}/{currentLedger.MaximumElapsedSeconds:F1} s. " +
                        $"Original cause: {currentLedger.LastReason} No budget was reset.",
                        new Dictionary<string, double>
                        {
                            ["attemptsUsed"] = currentLedger.AttemptsUsed,
                            ["maximumAttempts"] = currentLedger.MaximumAttempts,
                            ["cumulativeCommandedPixels"] = currentLedger.CumulativeCommandedPixels,
                            ["maximumCumulativePixels"] = currentLedger.MaximumCumulativePixels,
                            ["elapsedSeconds"] = elapsedSeconds,
                            ["maximumElapsedSeconds"] = currentLedger.MaximumElapsedSeconds,
                        });
                }
                inheritedSettledBudget = currentLedger;
                phd2SettledHandoffBudget = currentLedger;
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
        // ADR-0020: PHD2 alone decides whether its calibration is reusable.
        // Ordinary native calibration is allowed before a motion lineage.
        // ADR-0024 adds the explicit bounded adaptive reservation below, never
        // a calibration inferred from advisory metadata or unsettled motion.
        var allowNativeCalibration = pendingPhd2LockShift is null && inheritedSettledBudget is null &&
            lostLockReacquisitionDepth == 0 && postCalibrationReacquisitionDepth == 0;
        var adaptiveForce = adaptiveCalibrationPending && !GuideRecovery.Expired(DateTimeOffset.UtcNow);
        if (adaptiveCalibrationPending && !adaptiveForce)
            return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_ADAPTIVE_CALIBRATION_EXPIRED",
                "导星恢复校准请求已过期；未发校准脉冲，未重发恢复预算。");
        // ADR-0024 permits one reserved automatic request after clear fresh
        // frames and failed reselection, only with every motion lineage settled.
        if (adaptiveForce && !HasNoOutstandingGuideRecoveryMotion)
            return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_ADAPTIVE_CALIBRATION_MOTION_PENDING",
                "仍有未结运动责任；自动校准不能替代原回程。");
        var forceCalibrationRequested = configuration.SequencePlan?.ForceGuideCalibration == true;
        nativeGuideCalibrationRequest.RequireBoundary(forceCalibrationRequested, allowNativeCalibration);
        // ADR-0023 explicit requests and ADR-0024 adaptive reservations share
        // the once-only allowance; a failed dispatch never restores it.
        allowNativeCalibration &= !nativeGuideCalibrationRequest.Consumed && !GuideRecovery.CalibrationConsumed;
        allowNativeCalibration |= adaptiveForce;
        var hardRequirement = PhdCalibrationRequirement(allowNativeCalibration: allowNativeCalibration);
        var calibrationBefore = await phd2.ValidateCalibrationAsync(hardRequirement, cancellationToken).ConfigureAwait(false);
        if (!calibrationBefore.CanStartNativeGuide)
            return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_OWNER_CALIBRATION_UNAVAILABLE",
                string.Join(" ", calibrationBefore.Failures.Concat(calibrationBefore.IndeterminateReasons)));
        ReportPhd2OwnerCalibration(calibrationBefore);
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
        var takeoverResidual = PixelDistance(initialTarget.Centroid, lastG3Field.SlitDetection.Geometry.AcquisitionPoint);
        if (calibrationBefore.Calibration.Calibrated && takeoverResidual + guideChoice.Field.TargetIdentification.CatalogPositionSpreadPixels >
            Phd2HandoffResidualPixels(guideChoice.Field, preset) &&
            guideChoice.Field.TargetIdentification.PredictionResidualPixels > preset.CoarseHandoffResidualPixels)
            return await ReacquireG3ForPhd2HandoffAsync(context, "PHD2_WCS_TAKEOVER_POSITION_CHANGED",
                $"接管新帧的实测目标距原预测 {guideChoice.Field.TargetIdentification.PredictionResidualPixels:F2}px，距狭缝 {takeoverResidual:F2}px，超出 {Phd2HandoffResidualPixels(guideChoice.Field, preset):F2}px 粗定位交接范围。",
                postCalibrationReacquisitionDepth, lostLockReacquisitionDepth, cancellationToken).ConfigureAwait(false);
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
                var prepared = await PrepareSnrOptimizedGuideSelectionAsync(
                    context, guideChoice, preset, loop, cancellationToken).ConfigureAwait(false);
                guideChoice = prepared.Choice;
                lastG3Field = guideChoice.Field;
                initialTarget = guideChoice.Field.TargetIdentification.Target!;
                loop = prepared.Loop;
                guideSelectionResult = (prepared.Selection, prepared.Requested, prepared.Selected);
            }
            catch (Phd2NativeGuideSelectionExhaustedException exhausted)
            {
                await StopPhdAndWaitAsync(CancellationToken.None).ConfigureAwait(false);
                if (preset.GuideMode != Phd2SlitGuideMode.AutoPreferOffSlitThenDirectTarget ||
                    guideChoice.Mode != Phd2SlitGuideMode.OffSlitGuideStar)
                {
                    return exhausted.AfterCheckedStop();
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
            var selectionConnectionEpoch = phd2.Snapshot.ConnectionEpoch;
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
            // Re-read actual owner identity/calibration immediately before
            // guide. PHD2 may calibrate only at this motion-free boundary;
            // advisory metadata never requests renewal (ADR-0020/0023).
            hardRequirement = PhdCalibrationRequirement(allowNativeCalibration: allowNativeCalibration);
            calibrationBefore = await phd2.ValidateCalibrationAsync(hardRequirement, cancellationToken).ConfigureAwait(false);
            if (!calibrationBefore.CanStartNativeGuide)
            {
                await StopPhdAndWaitAsync(cancellationToken).ConfigureAwait(false);
                return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_OWNER_CALIBRATION_UNAVAILABLE",
                    string.Join(" ", calibrationBefore.Failures.Concat(calibrationBefore.IndeterminateReasons)));
            }
            ReportPhd2OwnerCalibration(calibrationBefore);
            identity = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), cancellationToken).ConfigureAwait(false);
            if (!identity.IsValid)
                throw new Phd2IdentityMismatchException(identity);
            // Record our validated selection BEFORE waiting for guide/settle.
            // Initial settle can fail with no pulse output; recording ownership
            // only afterwards left that own session impossible to checked-stop.
            // This receipt grants stop authority only, never arrival/settle proof.
            _ = await phd2.GetAppStateAsync(cancellationToken).ConfigureAwait(false);
            var preGuideLock = await phd2.GetLockPositionAsync(cancellationToken).ConfigureAwait(false);
            if (!Phd2DependencyRebuildStopPolicy.CanRecordPreGuideOwner(
                phd2.Snapshot, selectionConnectionEpoch, selectedGuide, preGuideLock))
            {
                await PublishRunJsonEvidenceAsync("phd2-pre-guide-owner-rejected",
                    "Selection ownership readback rejected before guide",
                    new { selectedGuide, preGuideLock, selectionConnectionEpoch, current = phd2.Snapshot,
                        guideCommandSent = false }, lastG3Field?.FramePath, cancellationToken).ConfigureAwait(false);
                return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_PRE_GUIDE_OWNER_CHANGED",
                    "本轮选星后 PHD2 连接、状态或锁点发生变化；未接管外部导星，未发送 guide。");
            }
            phd2PreMotionOutputOwner = (selectionConnectionEpoch, preGuideLock!);
            Volatile.Write(ref phd2GuidingEverStarted, 1);
            var recalibrationStartedUtc = DateTimeOffset.UtcNow;
            var forceGuideCalibration = nativeGuideCalibrationRequest.Consume(
                forceCalibrationRequested, allowNativeCalibration);
            if (forceGuideCalibration) GuideRecovery.RecordExplicitCalibration();
            if (adaptiveForce)
            {
                // The target session reserved this once before reacquisition.
                // Clear before the native RPC, including timeout/cancel paths.
                adaptiveCalibrationPending = false;
                forceGuideCalibration = true;
            }
            if (forceGuideCalibration)
            {
                Report(adaptiveForce
                    ? "导星恢复：由 PHD2 强制校准一次，随后重新确认星场与入缝；不循环重校准。"
                    : "本目标已勾选强制导星校准：由 PHD2 执行一次，随后重新确认星场与入缝；不会循环重校准。");
                await PublishRunJsonEvidenceAsync(adaptiveForce ? "phd2-adaptive-calibration-request" : "phd2-explicit-calibration-request",
                    adaptiveForce ? "Clear fresh fields and failed reselection; reserved native calibration consumed before dispatch"
                        : "Saved per-target operator request consumed before native guide dispatch",
                    new { forceGuideCalibration, adaptiveForce, oncePerTarget = true, motionFree = allowNativeCalibration,
                        selectionConnectionEpoch, selectedGuide, preGuideLock },
                    lastG3Field.FramePath, cancellationToken).ConfigureAwait(false);
            }
            var settle = await phd2.GuideAndSettleAsync(
                InitialNativeGuideCriteria(Phd2SettleCriteriaForSlitPlacement(preset)),
                forceRecalibration: forceGuideCalibration,
                guideSelectionRoi,
                preserveSameEpochGuidingOnSettleTimeout: HasSupervisedScienceOptIn(),
                cancellationToken).ConfigureAwait(false);
            var guideProof = phd2.Snapshot;
            if (Phd2DependencyRebuildStopPolicy.CanRecordCompletedGuideOwner(
                guideProof, selectionConnectionEpoch, settle))
            {
                // PHD2's first guiding frame can legitimately re-centroid the
                // selection. Retain its operation-bound lock before classifying
                // failed settle/output; never widen arbitrary lock tolerances.
                phd2PreMotionOutputOwner = (guideProof.ConnectionEpoch, guideProof.LockPosition!);
                await PublishRunJsonEvidenceAsync("phd2-guide-operation-stop-owner",
                    "Locally completed native guide operation lock retained for checked stop only",
                    new { guideProof.ConnectionEpoch, guideProof.GuideEpoch,
                        guideProof.LastSettleOperationId, guideProof.LastSettleCommandAccepted,
                        selectedLock = preGuideLock, nativeGuideLock = guideProof.LockPosition,
                        settle, stopAuthorityOnly = true, placementProven = false },
                    lastG3Field.FramePath, cancellationToken).ConfigureAwait(false);
                phd2CompletedInitialGuideStopReceipt = Phd2DependencyRebuildStopPolicy.RecordCompletedInitialGuide(
                    guideProof, selectionConnectionEpoch, settle,
                    pendingPhd2LockShift is not null || currentLedger is not null);
            }
            var windSampledSettle = CanReplaceSettleWithFreshGuidingWindow(settle, guideProof);
            var calibrationNeedsFreshField = Phd2DependencyRebuildStopPolicy.CanReacquireAfterCalibrationSettleFailure(
                guideProof, selectionConnectionEpoch, settle,
                pendingPhd2LockShift is not null || currentLedger is not null);
            if (!settle.Succeeded && !windSampledSettle && !calibrationNeedsFreshField)
            {
                phd2.ThrowIfGuideOutputFailed();
                if (Phd2DependencyRebuildStopPolicy.CanClassifyInitialSettleQualityFailure(
                    guideProof, selectionConnectionEpoch, settle, pendingPhd2LockShift is not null) ||
                    (AdaptiveGuideRecoveryEnabled && Phd2DependencyRebuildStopPolicy.CanClassifyInitialLostStarFailure(
                        guideProof, selectionConnectionEpoch, settle, pendingPhd2LockShift is not null)))
                    return await StopInitialGuideQualityFailureAsync(guideProof, settle, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(settle.Error ?? "PHD2 guide/settle failed.");
            }
            if (settle.Succeeded && !guideProof.HasCurrentSuccessfulSettle)
                throw new InvalidOperationException("The locally issued guide operation did not leave a same-epoch successful settle attestation.");
            if (windSampledSettle)
                Report("warning：PHD2 原生稳定等待超时；保持同一 Guiding epoch，改取 fresh GuideStep/FITS 窗口评估，尚未确认天气原因");
            if (calibrationNeedsFreshField)
                Report("warning：PHD2 校准已完成，但校准后稳定等待未通过；确认停止后重新取场，再继续入缝。不把超时当成到位，不重复强制校准。");

            var calibration = await phd2.ValidateCalibrationAsync(
                policy.ApplyHardRejectionCeilings(PhdCalibrationRequirement(
                    settle.NativeCalibrationOccurred ? recalibrationStartedUtc : null)),
                cancellationToken).ConfigureAwait(false);
            if (calibration.Status != Phd2ValidationStatus.Valid)
            {
                throw new InvalidOperationException(
                    $"PHD2 current owner/calibration readback unavailable: {string.Join(" ", calibration.Failures.Concat(calibration.IndeterminateReasons))}");
            }

            if (settle.NativeCalibrationOccurred)
            {
                GuideRecovery.RecordExplicitCalibration();
                localPhd2CalibrationProof = new Phd2RuntimeCalibrationProof(
                    calibration.Profile, calibration.Calibration, calibration.ConnectionEpoch,
                    calibration.CalibrationChangeSequence, recalibrationStartedUtc);
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
                        nativeSettle = settle,
                        calibrationNeedsFreshField,
                        settleSucceeded = settle.Succeeded,
                        placementProven = false,
                        preCalibrationFrame = lastG3Field?.FramePath,
                        exactLockCommandIssued = false,
                        nextAuthority = "fresh immutable G3 FITS + formal PL3 + runtime slit midpoint",
                    },
                    lastG3Field?.FramePath,
                    cancellationToken).ConfigureAwait(false);
                await CheckpointAndRejectStaleStageStackAsync(context, cancellationToken).ConfigureAwait(false);
                var calibrationStopPierSide = telescopeMediator.GetInfo().SideOfPier.ToString();
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
                var mountIdle = await WaitForPostCalibrationMountIdleAsync(
                    calibrationStop, calibrationStopPierSide, cancellationToken).ConfigureAwait(false);
                if (!mountIdle.CanAdvance) return mountIdle;
                var reacquired = await AcquireG3SlitFieldAsync(
                    context,
                    cancellationToken,
                    allowChargedCurrentPositionHandoff: true).ConfigureAwait(false);
                if (!reacquired.CanAdvance)
                {
                    // Preserve the actual gate and evidence. Wrapping every
                    // result as an unknown calibration fault hid reviewed
                    // target-quality failures from the native skip policy.
                    // Unsafe/unknown gates remain non-skippable; idle and
                    // terminal-ledger checks still run independently.
                    return reacquired;
                }
                return await PlaceTargetOnSlitWithPhd2Async(
                    context,
                    cancellationToken,
                    postCalibrationReacquisitionDepth + 1,
                    lostLockReacquisitionDepth).ConfigureAwait(false);
            }

            var lockOrigin = await phd2.GetLockPositionAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("PHD2 did not report a runtime lock position after settle.");
            phd2PreMotionOutputOwner = (phd2.Snapshot.ConnectionEpoch, lockOrigin);
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
                Math.Max(3, policy.RequiredFreshResidualsPerLockShiftStage),
                DateTimeOffset.UtcNow.AddSeconds(preset.MaximumStageSeconds),
                cancellationToken, referenceGuide: selectedGuide,
                referenceTargetMeasured: Phd2FreshTargetFluxPolicy.HasMeasuredHandoffPosition(lastG3Field.TargetIdentification)).ConfigureAwait(false);
            var first = firstMeasurements[^1];
            // Motion planning must be bound to the residual that actually
            // authorized it.  Keeping lastG3Field on the older guide-selection
            // FITS made the strict 2-arcsec mount freshness gate age for the
            // entire multi-frame residual window and caused a false stale-frame
            // rebuild immediately before the first lock shift.
            lastG3Field = UpdateG3FieldFromGuidingResidual(lastG3Field, first, preset);
            var firstGuideResidual = PointDistance(first.Measurement.GuideStar, lockOrigin);
            var residualEvidence = Phd2PlacementGuideWindowPolicy.CalibrationResidualEvidence(
                first, firstGuideResidual, topology, DateTimeOffset.UtcNow);
            var settleEvidence = CreateCalibrationSettleEvidence(settle, guideProof, windSampledSettle, firstMeasurements.Count);
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

            var startedUtc = inheritedSettledBudget?.MotionClockStartedUtc ?? DateTimeOffset.UtcNow;
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
            ledger = NativeTarget?.ClockBudget?.Charge(ledger) ?? ledger;
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
                false,
                windSampledSettle,
                PrimaryStarSettleRecoveryAttempted: inheritedSettledBudget?.PrimaryStarSettleRecoveryAttempted == true,
                PrimaryStarSettleRecoveryStage: inheritedSettledBudget is { PrimaryStarSettleRecoveryAttempted: true } recovered
                    ? Math.Max(recovered.PrimaryStarSettleRecoveryStage, recovered.AttemptsUsed) : 0);
            phd2SlitPlacementSession = session;

            var acquisitionBudget = Phd2SlitLockShiftPlanner.EvaluateAcquisitionBudget(
                qualification, guideChoice.Mode, first.Measurement, ledger,
                BuildPhd2LockShiftSafetySnapshot(context, preset, topology.PierSide), topology,
                preset.BuildMotionLimits(), DateTimeOffset.UtcNow);
            await PublishRunJsonEvidenceAsync("phd2-acquisition-budget-preflight",
                "Whole fine-placement correction and return checked before the first lock shift",
                new { acquisitionBudget, ledger, fineMotionLimits = preset.BuildMotionLimits(),
                    qualification.MaximumLockShiftScale, first.Measurement.TargetCentroid,
                    first.Measurement.RecognizedSlitAcquisitionPoint, motionCommandIssued = false, budgetReset = false },
                first.Frame.Path, cancellationToken).ConfigureAwait(false);
            return await phd2Convergence.ConvergeAsync(context,
                new(session, ledger, firstMeasurements, preset, identity, pendingPath, Metadata(loaded),
                    postCalibrationReacquisitionDepth, lostLockReacquisitionDepth), cancellationToken).ConfigureAwait(false);
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
            if (phd2.Snapshot.GuideOutput?.Failed == true)
                return await RecoverPhd2GuideOutputBeforeMotionAsync(context,
                    postCalibrationReacquisitionDepth, lostLockReacquisitionDepth, cancellationToken).ConfigureAwait(false);
            Exception failure = ex;
            if (AdaptiveGuideRecoveryEnabled &&
                (IsStructuredPhd2GuideSessionLoss(ex) || Phd2StageFailurePolicy.FindLostFrame(ex) is not null) &&
                phd2.IsConnected && HasNoOutstandingGuideRecoveryMotion)
            {
                var triggerEvidence = await PublishRunJsonEvidenceAsync("phd2-adaptive-recovery-trigger",
                    "Original selection/guide failure before checked stop and star detection",
                    new { exceptionType = ex.GetType().Name, reason = ex.Message, state = phd2.Snapshot,
                        motionIssued = false, budgetReset = false }, lastG3Field?.FramePath,
                    cancellationToken).ConfigureAwait(false);
                var recoveryStop = await EnsurePhdStoppedForAutomaticRebuildAsync(
                    ObservationStage.PlaceTargetOnSlit, "PHD2_STRUCTURED_GUIDE_SESSION_LOSS", cancellationToken).ConfigureAwait(false);
                if (recoveryStop.Disposition != GateDisposition.Passed)
                    return new StageResult(recoveryStop with { Message = $"{recoveryStop.Message} Original failure: {ex.Message}" }, triggerEvidence);
                return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_STRUCTURED_GUIDE_SESSION_LOSS",
                    "本轮导星丢失，已确认停止；下一步新帧检测星点，区分等待与重新选星，不立即全场重建。");
            }
            if (!AdaptiveGuideRecoveryEnabled && IsStructuredPhd2GuideSessionLoss(ex) &&
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
                        return ObservationWorkflowResults.WithFailureContext(reacquired,
                            "PHD2 lost lock and the single bounded G3/PL3 reacquisition did not complete.");
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
            var lostFrameFailure = Phd2StageFailurePolicy.FindLostFrame(failure);
            var stopConfirmed = false;
            try
            {
                // The inner motion/settle/frame handlers must let typed StarLost
                // reach this boundary. Its guide epoch is no longer return
                // authority, even if PHD2 has already reacquired by this point.
                // Preserve all charged motion and the original endpoint/epoch;
                // stopping is not proof of arrival or a completed return.
                if (lostFrameFailure is not null && pendingPhd2LockShift is
                    { Phase: not Phd2LockShiftPendingPhase.SettledBudgetLedger } lostPending)
                {
                    pendingPhd2LockShift = lostPending with
                    {
                        Phase = Phd2LockShiftPendingPhase.ReturnRequired,
                        UpdatedUtc = DateTimeOffset.UtcNow,
                        LastReason = $"Native guide star lost with outstanding lock motion; no automatic return was attempted. {lostFrameFailure.Message}",
                    };
                    await Phd2LockShiftPendingStore.WriteAtomicAsync(
                        Phd2LockShiftPendingPath(lostPending.ObservationRunId),
                        pendingPhd2LockShift,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                // A persistence error must not prevent the existing checked stop.
                try
                {
                    await StopPhdAndWaitAsync(CancellationToken.None).ConfigureAwait(false);
                    stopConfirmed = true;
                }
                catch { }
                phd2SlitPlacementSession = null;
            }
            if (lostFrameFailure is not null)
            {
                var lost = lostFrameFailure.LostFrame;
                var metrics = new Dictionary<string, double>
                {
                    ["stopConfirmed"] = stopConfirmed ? 1 : 0,
                    ["outstandingLockMotion"] = pendingPhd2LockShift is not null &&
                        pendingPhd2LockShift.Phase != Phd2LockShiftPendingPhase.SettledBudgetLedger ? 1 : 0,
                };
                if (lost?.Frame is { } lostGuideFrame) metrics["lostGuideFrame"] = lostGuideFrame;
                if (lost?.Snr is { } lostGuideSnr && double.IsFinite(lostGuideSnr)) metrics["lostGuideSnr"] = lostGuideSnr;
                if (lost?.ErrorCode is { } lostGuideErrorCode) metrics["lostGuideErrorCode"] = lostGuideErrorCode;
                await PublishRunJsonEvidenceAsync("phd2-placement-star-lost",
                    "Native StarLost terminated placement; not a settle-only quality warning",
                    new { lostFrameFailure.Snapshot.ConnectionEpoch, lostFrameFailure.Snapshot.GuideEpoch,
                        lostFrame = lost, stopConfirmed, pendingPhase = pendingPhd2LockShift?.Phase,
                        previousOpticalFrame = lastG3Field?.FramePath,
                        windCauseConfirmed = false, slitArrivalConfirmed = false, budgetReset = false },
                    lastG3Field?.FramePath, cancellationToken).ConfigureAwait(false);
                return Attention(ObservationStage.PlaceTargetOnSlit,
                    Phd2StageFailurePolicy.PlacementStarLost,
                    $"PHD2 explicitly reported StarLost during fresh placement verification, not just a settle timeout. Stop confirmed: {stopConfirmed}. The original motion ledger is retained; no arrival, return or weather cause is inferred. {failure.Message}", metrics);
            }
            // Preserve the reviewed optical quality code, not the generic
            // placement wrapper. Native sequence skip still requires its own
            // idle/terminal-ledger checks; an unconfirmed stop is never a skip.
            if (failure is Phd2FreshTargetNotMeasuredException unmeasuredTarget)
                return unmeasuredTarget.AfterStop(stopConfirmed);
            if (failure is TargetQualityEvidenceException qualityFailure)
                return qualityFailure.AfterStop(stopConfirmed);
            if (failure is Phd2GuideSelectionUnavailableException selectionFailure)
                return new TargetQualityEvidenceException("PHD2_PRE_GUIDE_SELECTION_UNAVAILABLE",
                    selectionFailure.Message).AfterStop(stopConfirmed);
            if (failure is PhysicalActionGateException physicalFailure)
                return new StageResult(physicalFailure.Gate);
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
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, double>? completionFailureMetrics = null)
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
            finalVerification: null,
            completionFailureMetrics: completionFailureMetrics);
    }

    private async Task<StageResult> ReturnPhd2LockToOriginCoreAsync(
        ObservationContext context,
        Phd2SlitPlacementCommissioning preset,
        Phd2SensorTopology topology,
        Phd2LockShiftQualification qualification,
        Phd2LockShiftPendingState state,
        string path,
        string reason,
        string targetIdentityEvidenceId,
        CancellationToken cancellationToken,
        DateTimeOffset? recoveryEpisodeStartedUtc,
        Func<Phd2Point, CancellationToken, Task<GateResult>>? finalVerification,
        bool prohibitReturnMotion = false,
        IReadOnlyDictionary<string, double>? completionFailureMetrics = null)
    {
        if (phd2.Snapshot.GuideOutput?.Failed == true)
        {
            pendingPhd2LockShift = state;
            return await HaltFailedPhd2GuideOutputAsync(context, cancellationToken).ConfigureAwait(false);
        }
        var activeRecoveryStartedUtc = recoveryEpisodeStartedUtc ?? state.StartedUtc;
        Phd2Point? measuredReturnLock = null;
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
            if (phd2.Snapshot.GuideOutput?.Failed == true)
                return await HaltFailedPhd2GuideOutputAsync(context, cancellationToken).ConfigureAwait(false);
            var snapshot = phd2.Snapshot;
            if (!snapshot.IsConnected || snapshot.ConnectionEpoch != state.ConnectionEpoch || snapshot.GuideEpoch != state.GuideEpoch || snapshot.AppState != Phd2AppState.Guiding)
            {
                return Attention(
                    ObservationStage.PlaceTargetOnSlit,
                    "PHD2_LOCK_RETURN_EPOCH_CHANGED",
                    $"PHD2 connection/guide epoch or state changed while a durable lock return was pending (expected {state.ConnectionEpoch}/{state.GuideEpoch}, actual {snapshot.ConnectionEpoch}/{snapshot.GuideEpoch}, state {snapshot.AppState}). Original failure: {reason}. The actual lock must be reconciled; no automatic return command was sent.");
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
                return new StageResult(Phd2HandoffRecoveryPolicy.FailureGate(plan.Code, plan.Message), path);
            }
            if (plan.IsComplete)
            {
                // A retry may start with the lock already back at origin while
                // the star is still moving. Never settle the ledger from lock
                // coordinates alone. Foreign recovery has its own stronger
                // target/slit displacement verifier below.
                if (finalVerification is null && (measuredReturnLock is null ||
                    PointDistance(measuredReturnLock, actual) > preset.LockVerificationTolerancePixels))
                {
                    try
                    {
                        var deadline = activeRecoveryStartedUtc.AddSeconds(state.MaximumElapsedSeconds);
                        var remaining = Math.Min(preset.MaximumStageSeconds, (deadline - DateTimeOffset.UtcNow).TotalSeconds);
                        var frames = await Phd2MeasuredReturnWindow.ObserveAsync(phd2,
                            state.ConnectionEpoch, state.GuideEpoch, actual, preset.LockVerificationTolerancePixels,
                            Math.Max(3, Phd2SettleCriteriaForSlitPlacement(preset).Pixels),
                            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, remaining)),
                            () => ReserveRunEvidencePath("g3-phd2-origin-verification", ".fit"),
                            TimeSpan.FromSeconds(preset.FreshGuidingFrameTimeoutSeconds), cancellationToken,
                            (frame, token) => ReadPrimaryGuidePositionAsync(frame, preset, token)).ConfigureAwait(false);
                        state = state with { LastFramePath = frames[^1].Path, LastAcceptedFrameSha256 = frames[^1].Sha256 };
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (PhysicalActionGateException ex) { return new StageResult(ex.Gate, path); }
                    catch (Exception ex)
                    {
                        return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RETURN_MEASUREMENT_FAILED",
                            "锁点在原位，但新帧尚未证明星点回位；原回程记录保留。" + ex.Message);
                    }
                }
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
                // A completed return after exhausting the completion window is
                // not a LostLock repair. Re-solving cannot restore its original
                // clock. Keep the root cause instead of wasting a full rebuild.
                if (completionFailureMetrics is not null)
                    return Attention(ObservationStage.PlaceTargetOnSlit,
                        "PHD2_SLIT_COMPLETION_WINDOW_EXHAUSTED_RETURNED",
                        $"{reason} Slit completion did not meet the selected quality policy before the return reserve. " +
                        "The runtime lock origin was verified and guiding was stopped. No fresh G3 rebuild or new placement budget was issued.",
                        completionFailureMetrics);
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
            var recoveryDeadline = activeRecoveryStartedUtc.AddSeconds(state.MaximumElapsedSeconds);
            var availableReturnSeconds = (recoveryDeadline - DateTimeOffset.UtcNow).TotalSeconds -
                stage.ReservedRecoveryAttempts * preset.MaximumStageSeconds;
            var windowSeconds = Math.Min(preset.MaximumStageSeconds, availableReturnSeconds);
            if (!double.IsFinite(windowSeconds) || windowSeconds <= 0)
                return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RETURN_POSITION_UNCONFIRMED",
                    "原回程时限已用尽；保留未结记录，不重置预算或重发移动。");
            Report("PHD2 回程：保持当前导星，用三张新帧核对实际星点位置；不重复启动原生稳定等待。");
            IReadOnlyList<Phd2GuidingFrameResult> returnFrames;
            try
            {
                returnFrames = await Phd2MeasuredReturnWindow.ObserveAsync(
                    phd2, state.ConnectionEpoch, state.GuideEpoch, verified,
                    preset.LockVerificationTolerancePixels,
                    Math.Max(3, Phd2SettleCriteriaForSlitPlacement(preset).Pixels),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(windowSeconds),
                    () => ReserveRunEvidencePath("g3-phd2-lock-return-residual", ".fit"),
                    TimeSpan.FromSeconds(preset.FreshGuidingFrameTimeoutSeconds), cancellationToken,
                    (frame, token) => ReadPrimaryGuidePositionAsync(frame, preset, token)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (PhysicalActionGateException ex) { return new StageResult(ex.Gate, path); }
            catch (Exception ex)
            {
                state = state with { CurrentLockX = verified.X, CurrentLockY = verified.Y,
                    LastReason = $"Original failure: {reason} Fresh measured physical return not confirmed: {ex.Message}" };
                await Phd2LockShiftPendingStore.WriteAtomicAsync(path, state, CancellationToken.None).ConfigureAwait(false);
                pendingPhd2LockShift = state;
                return Attention(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RETURN_MEASUREMENT_FAILED",
                    $"回程锁点已读回，但同一导星会话的新帧尚未证明星点回位；保留停止后归档处理。原始原因：{reason} 回程核验：{ex.Message}");
            }
            await PublishRunJsonEvidenceAsync("phd2-lock-return-measured-window",
                "Three fresh frame-bound native centroids confirm physical return, not slit placement or native settle",
                new { state.LineageId, returnFrames, verified, recoveryDeadline,
                    nativeGuideCommandSent = false, syntheticSettleDone = false, slitPlacementProven = false,
                    state.AttemptsUsed, state.CumulativeCommandedPixels, budgetReset = false },
                returnFrames[^1].Path, cancellationToken).ConfigureAwait(false);
            measuredReturnLock = verified;
            state = state with
            {
                CurrentLockX = verified.X, CurrentLockY = verified.Y,
                LastAcceptedFrameSha256 = returnFrames[^1].Sha256, LastFramePath = returnFrames[^1].Path,
                UpdatedUtc = DateTimeOffset.UtcNow,
                LastReason = "Three fresh measured guide frames confirmed the recovery stage position; replan from actual lock.",
            };
            await Phd2LockShiftPendingStore.WriteAtomicAsync(path, state, CancellationToken.None).ConfigureAwait(false);
        }
        return new StageResult(GateResult.TargetQuality("PHD2_LOCK_RETURN_ATTEMPT_LIMIT", "The durable runtime-lock recovery exhausted its bounded attempts; the ledger remains pending."), path);
    }

    private async Task<Phd2Point?> ReadPrimaryGuidePositionAsync(Phd2GuidingFrameResult frame,
        Phd2SlitPlacementCommissioning preset, CancellationToken token)
    {
        var sha = await ComputeFileSha256Async(frame.Path, token).ConfigureAwait(false);
        if (!string.Equals(sha, frame.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("PHD2_RETURN_FRAME_HASH_CHANGED: Immutable guide frame changed.");
        var image = await imageDataFactory.CreateFromFile(frame.Path, 16, false,
            RawConverterEnum.FREEIMAGE, token).ConfigureAwait(false);
        if (image.Properties.Width != preset.RoiWidth || image.Properties.Height != preset.RoiHeight)
            throw new InvalidOperationException("PHD2_RETURN_FRAME_GEOMETRY_CHANGED: Guide frame geometry changed.");
        var point = frame.ResolvePrimaryStarPosition(image.Properties.Width, image.Properties.Height, image.Data.FlatArray);
        return point is null ? null : ToPhd2Domain(new PixelPoint(point.X, point.Y), preset);
    }

    private async Task<IReadOnlyList<Phd2GuidingResidualState>> CapturePhd2GuidingMeasurementsAsync(
        ObservationContext context,
        Phd2SlitPlacementCommissioning preset,
        Phd2SensorTopology topology,
        Phd2Point currentLock,
        Phd2Point expectedTarget,
        SlitGeometry runtimeSlitLocal,
        Phd2SlitGuideMode guideMode,
        int count,
        CancellationToken cancellationToken,
        Phd2ReadOnlyFrameGraceBudget? readoutGrace = null,
        bool rollingSample = false,
        Phd2Point? referenceGuide = null,
        bool referenceTargetMeasured = true)
    {
        if (count <= 0) throw new InvalidOperationException("The commissioned fresh-residual count must be positive.");
        using var uvex = new UvexServiceClient(configuration.UvexServiceUrl);
        var measurements = new List<Phd2GuidingResidualState>(count);
        var captureAttempt = 0;
        var targetWindow = new Phd2FreshTargetWindow(count);
        var windowOwner = phd2.Snapshot;
        var maximumCaptureAttempts = targetWindow.MaximumCaptures;
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
            var residualBeforeExposure = CaptureG3FrameMountReadback();
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
            var expectedExposureMilliseconds = GuideExposureFor(preset, guideMode);
            var exposureGate = Phd2GuidingExposurePolicy.Evaluate(
                image.MetaData.Image.ExposureTime, expectedExposureMilliseconds, guideMode.ToString());
            if (exposureGate.Disposition != GateDisposition.Passed)
            {
                await PublishRunJsonEvidenceAsync("phd2-guiding-exposure-mismatch",
                    "Fresh guide-frame exposure differs from the locked run; no setting override or motion retry",
                    new { exposureGate.Code, exposureGate.Message, exposureGate.Metrics,
                        guideMode = guideMode.ToString(), framePath = result.Path, frameSha256 = actualSha,
                        beforeFrameWait.ConnectionEpoch, beforeFrameWait.GuideEpoch,
                        settingsOverridden = false, budgetReset = false },
                    result.Path, cancellationToken).ConfigureAwait(false);
                Report(ObservationUiPresentation.Present(ObservationStage.SelectAtrExposure,
                    exposureGate, ObservationStaticTextLocalization.EffectiveCulture).Summary);
                throw new PhysicalActionGateException(exposureGate);
            }
            var exposureMilliseconds = (int)Math.Round(image.MetaData.Image.ExposureTime * 1000);
            var exposureMatched = true;
            var raw = image.Data.FlatArray;
            if (raw.Length != properties.Width * properties.Height)
                throw new InvalidOperationException("Fresh guiding FITS pixel buffer is unsupported.");
            var frame = G3FrameInputPolicy.Create(properties.Width, properties.Height, raw, configuration.G3);
            var candidates = StarFieldDetector.Detect(frame);
            var matchedPrimary = result.ResolvePrimaryStarPosition(properties.Width, properties.Height, raw);
            var continuityPrediction = guideMode == Phd2SlitGuideMode.OffSlitGuideStar &&
                referenceGuide is not null && matchedPrimary is not null &&
                result.NativeLockPosition is { } continuityLock &&
                PointDistance(continuityLock, currentLock) <= preset.LockVerificationTolerancePixels
                    ? Phd2TargetContinuityPolicy.Predict(expectedTarget, referenceGuide,
                        ToPhd2Domain(new(matchedPrimary.X, matchedPrimary.Y), preset)) : null;
            var expectedTargetLocal = ToFrameLocal(continuityPrediction ?? expectedTarget, preset);
            // A fresh native frame number/primary crop proves a new in-session
            // measurement, NOT its shutter start. Driver readout may already
            // have been in flight before this wait; preserve exact time unknown.
            var targetFrameTiming = new G3SparseExposureTiming(
                default, result.CompletedUtc,
                image.MetaData.Image.ExposureTime, DateTimeOffset.UtcNow);
            await RetainSparseG3FrameAsync(context, image, result.Path, actualSha, residualMountBinding,
                residualBeforeExposure, exposureMilliseconds, expectedTargetLocal, cancellationToken,
                captureTimingKnown: false).ConfigureAwait(false);
            var trackedTarget = await PredictStellarTargetTrackAsync(context, lastG3Field, frame, raw,
                expectedTargetLocal, runtimeSlitLocal, preset, result.Path, actualSha, targetFrameTiming,
                cancellationToken).ConfigureAwait(false);
            if (trackedTarget is not null) expectedTargetLocal = trackedTarget.Point;
            var targetRecognitionRadius = trackedTarget is null ? preset.TargetSearchRadiusPixels :
                Math.Min(preset.TargetSearchRadiusPixels, Math.Max(6, preset.MaximumResidualGrowthPixels));
            PixelPoint targetLocal;
            double targetFlux;
            string targetEvidence;
            SaturatedTargetGhostTopologyAnalysis? rejectedTopology = null;
            TargetIdentification? rejectedStellar = null;
            double? saturatedIntegratedFluxDiagnostic = null;
            var targetPositionAuthority = Phd2TargetPositionAuthority.DetectedTargetCentroid;
            if (UsesCatalogWcsTargetAuthority(context))
            {
                if (guideMode != Phd2SlitGuideMode.OffSlitGuideStar || lastG3Field is null)
                    throw new InvalidOperationException("G3_CATALOG_GUIDE_MODE: Catalogue-only placement requires an independent off-slit guide star.");
                var reference = await CatalogReferenceAsync(context, lastG3Field, cancellationToken).ConfigureAwait(false);
                var registered = await RegisterCatalogueFrameAsync(context, reference, frame, image.Data.FlatArray,
                    expectedTargetLocal, runtimeSlitLocal, preset, result.Path, cancellationToken).ConfigureAwait(false);
                targetLocal = registered.Target;
                targetFlux = 0;
                targetPositionAuthority = Phd2TargetPositionAuthority.CatalogWcsRegisteredField;
                targetEvidence = $"catalog-wcs:{context.Plan.Target.CatalogId}:{reference.FramePath};registered-frame:{result.Sha256};stars:{registered.MatchedStars};scatter:{registered.ScatterPixels:R}";
            }
            else if (lastG3Field?.BrightTargetAuthority is not null)
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
                    targetRecognitionRadius);
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
                    if (sourceTopologyAnalysis.Gate.Severity == GateSeverity.Warning)
                        Report("星像形态警告：中心亮但未完全饱和，按本帧核心位置继续验证；保留形态警告，不把饱和区总亮度当作线性通量。");
                }
                else
                {
                    // A formerly bright target may no longer saturate near the
                    // slit. Try the normal, unique stellar centroid as well;
                    // retain its real flux and the ordinary hard flux envelope.
                    var freshStellar = SlitTargetIdentifier.Identify(
                        frame, candidates, expectedTargetLocal,
                        targetRecognitionRadius,
                        preset.MinimumTargetSignalToNoise, preset.MinimumTargetUniquenessRatio);
                    if (Phd2FreshTargetFluxPolicy.CanRefineCatalogPositionWithStellarCentroid(
                        freshStellar, preset.TargetSearchRadiusPixels))
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
                        rejectedTopology = sourceTopologyAnalysis;
                        rejectedStellar = freshStellar;
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
                    targetRecognitionRadius,
                    preset.MinimumTargetSignalToNoise,
                    preset.MinimumTargetUniquenessRatio);
                targetLocal = targetId.Target?.Centroid ?? expectedTargetLocal;
                targetFlux = targetId.Target?.FluxAdu ?? 0;
                if (targetId.Gate.Disposition != GateDisposition.Passed || targetId.Target is null)
                {
                    targetPositionAuthority = Phd2TargetPositionAuthority.CatalogWcsProjection;
                    rejectedStellar = targetId;
                }
                targetEvidence = lastG3Field?.GhostAssistance is
                    { Result.Decision: GhostAssistanceDecision.UseCalibratedAuxiliaryEstimate } ghost
                    ? $"external-catalog:{context.Plan.Target.CatalogId};ghost-auxiliary:{ghost.EvidencePath};identity:{ghost.ExternalIdentity?.EvidenceSha256}"
                    : $"{context.Plan.Target.CatalogId}:{lastG3Field?.FramePath}";
                var catalogIdentityEvidence = Phd2FreshTargetFluxPolicy.CatalogIdentityEvidence(lastG3Field);
                if (targetPositionAuthority != Phd2TargetPositionAuthority.CatalogWcsProjection &&
                    !string.IsNullOrWhiteSpace(catalogIdentityEvidence) &&
                    Phd2FreshTargetFluxPolicy.UsesSaturatedTopologyFluxNotApplicable(
                        targetId, hasCatalogIdentityChain: true))
                {
                    // The common identifier can promote a fresh frame from
                    // ordinary stellar morphology to a filled saturated core.
                    // Preserve that typed result instead of mislabelling its
                    // nonlinear component sum as ordinary stellar photometry.
                    saturatedIntegratedFluxDiagnostic = targetFlux;
                    targetFlux = 0;
                    targetPositionAuthority = Phd2TargetPositionAuthority.CatalogWcsIdentityWithSaturatedTopologyCentroid;
                    targetEvidence += $";fresh-topology:{targetId.Gate.Code};catalog-identity-chain:{catalogIdentityEvidence}";
                }
            }

            if (targetPositionAuthority == Phd2TargetPositionAuthority.CatalogWcsProjection && trackedTarget is null &&
                targetFrameTiming.CaptureRequestedUtc != default)
            {
                var trackProbe = new G3PlateSolveProbeState(GateResult.Warn("G3_TRACK_SEARCH_ONLY", "Current pixels, not motion authority"),
                    result.Path, image, null, null, Array.Empty<G3PlateSolveAttemptEvidence>(),
                    MountBinding: residualMountBinding, BeforeExposureMountReadback: residualBeforeExposure)
                { ExposureStartedUtc = targetFrameTiming.CaptureRequestedUtc, ExposureEndedUtc = targetFrameTiming.CaptureCompletedUtc,
                    ExposureMillisecondsActual = exposureMilliseconds };
                var recovered = await TryWideFieldStellarMeasurementAsync(context, trackProbe, frame, candidates,
                    expectedTargetLocal, preset, cancellationToken).ConfigureAwait(false);
                if (recovered?.Target is { } measuredTarget)
                {
                    targetLocal = measuredTarget.Centroid;
                    targetFlux = measuredTarget.FluxAdu;
                    targetPositionAuthority = Phd2TargetPositionAuthority.DetectedTargetCentroid;
                    targetEvidence += ";wide-search-only:fresh-G3-measured";
                    if (Phd2FreshTargetFluxPolicy.UsesSaturatedTopologyFluxNotApplicable(recovered,
                        !string.IsNullOrWhiteSpace(Phd2FreshTargetFluxPolicy.CatalogIdentityEvidence(lastG3Field))))
                    {
                        saturatedIntegratedFluxDiagnostic = targetFlux; targetFlux = 0;
                        targetPositionAuthority = Phd2TargetPositionAuthority.CatalogWcsIdentityWithSaturatedTopologyCentroid;
                    }
                }
            }

            // An estimator/nearby-peak switch is not sky motion. Keep the last
            // measured target/primary separation until a fresh consistent core
            // is observed. A rejection uses the existing bounded optical window,
            // never the prediction itself, another solve or extra motion credit.
            var continuityAllowance = Math.Max(6, preset.MaximumResidualGrowthPixels);
            if (targetPositionAuthority != Phd2TargetPositionAuthority.CatalogWcsProjection &&
                RejectStellarTargetTrackCandidate(trackedTarget, frame, targetLocal,
                    image.MetaData.Image.ExposureTime, continuityAllowance, out _))
            {
                rejectedStellar = new TargetIdentification(GateResult.TargetQuality(
                    "PHD2_TARGET_TRACK_COMPONENT_DISAGREEMENT", "Fresh candidate does not match the last-trusted target component after independent field registration."),
                    null, expectedTargetLocal, PixelDistance(targetLocal, expectedTargetLocal), 0);
                targetPositionAuthority = Phd2TargetPositionAuthority.CatalogWcsProjection;
            }
            if (continuityPrediction is not null &&
                targetPositionAuthority != Phd2TargetPositionAuthority.CatalogWcsRegisteredField &&
                (Phd2TargetContinuityPolicy.RejectMeasuredDiscontinuity(
                    referenceTargetMeasured, targetLocal, expectedTargetLocal, continuityAllowance) ||
                 (trackedTarget is not null && Phd2TargetContinuityPolicy.RejectMeasuredDiscontinuity(
                     referenceTargetMeasured, targetLocal, ToFrameLocal(continuityPrediction,preset), continuityAllowance))))
            {
                rejectedStellar ??= new TargetIdentification(GateResult.TargetQuality(
                    "PHD2_TARGET_POSITION_DISCONTINUITY", "Fresh target/primary geometry disagrees with the preceding measured field."),
                    null, expectedTargetLocal, PixelDistance(targetLocal, expectedTargetLocal), 0);
                targetPositionAuthority = Phd2TargetPositionAuthority.CatalogWcsProjection;
            }

            // A formal WCS preserves catalogue identity, not a new star position
            // in this frame. Apply the SAME bounded optical retry to both formal
            // WCS and neighbour forecasts; otherwise a transient ambiguous core
            // enters the rolling window and fails later as an untyped exception.
            if (targetPositionAuthority == Phd2TargetPositionAuthority.CatalogWcsProjection)
            {
                // G3_POST_WCS_TARGET_NOT_MEASURED: prediction is only a search
                // hint. A saturated core can split for one frame; wait for a
                // new complete consecutive window under the caller's SAME
                // cancellation/deadline, without changing exposure, gain or lock.
                targetWindow.Observe(measured: false);
                var discardedAcceptedFrames = measurements.Count;
                measurements.Clear();
                var sameGuide = Phd2FreshTargetWindow.SameGuide(windowOwner, phd2.Snapshot,
                    currentLock, preset.LockVerificationTolerancePixels);
                var retry = !targetWindow.Exhausted && sameGuide;
                var evidence = await PublishRunJsonEvidenceAsync("phd2-fresh-target-rejected",
                    "Rejected fresh target position; bounded consecutive-window restart, no motion or setting change",
                    new { framePath = result.Path, frameSha256 = actualSha,
                        expectedTargetLocal, continuityPrediction, referenceGuide, referenceTargetMeasured, continuityAllowance,
                        freshMeasuredTarget = targetLocal, matchedPrimary,
                        targetPositionAuthority, sourceTopology = rejectedTopology,
                        stellar = rejectedStellar, targetWindow.Captures, targetWindow.RejectedFrames,
                        targetWindow.MaximumCaptures, discardedAcceptedFrames, requiredConsecutiveFrames = count,
                        sameGuide, retry, result.NativeGuideStep, result.NativeLockPosition,
                        windowOwner.ConnectionEpoch, windowOwner.GuideEpoch,
                        predictionUsedAsMeasurement = false, settingsChanged = false,
                        lockMutation = false, budgetReset = false }, result.Path, cancellationToken).ConfigureAwait(false);
                PublishG3Preview(image, "本帧目标位置不唯一；保留原帧，仅在原时限内等待新的连续有效帧，不用预测位置代替实测。",
                    runtimeSlitLocal, null, null);
                if (!sameGuide)
                    throw new InvalidOperationException("PHD2_GUIDE_WINDOW_EPOCH_CHANGED: Cannot retry an optical rejection after guide ownership, lock, calibration or output changed.");
                if (rollingSample)
                    throw new Phd2RollingWindowInterruptedException();
                if (retry)
                {
                    Report($"目标位置复核警告：{rejectedTopology?.Gate.Code} / {rejectedStellar?.Gate.Code}；已丢弃本窗口 {discardedAcceptedFrames} 张测量，重新等待连续 {count} 张有效新帧（识别补取 {targetWindow.RejectedFrames}/{Phd2FreshTargetWindow.MaximumRejectedFramesToRetry}），原时限不变。");
                    continue;
                }
                throw new Phd2FreshTargetNotMeasuredException(GateResult.TargetQuality(
                    Phd2FreshTargetWindow.FailureCode,
                    $"连续目标实测窗口未完成：{rejectedTopology?.Gate.Code} / {rejectedStellar?.Gate.Code}。已读取 {targetWindow.Captures} 张、拒绝 {targetWindow.RejectedFrames} 张；没有用预测位置或分裂亮块放行移锁/曝光。",
                    new Dictionary<string, double> { ["captureAttempts"] = targetWindow.Captures,
                        ["rejectedFrames"] = targetWindow.RejectedFrames,
                        ["requiredConsecutiveFrames"] = count }), evidence);
            }

            if (targetWindow.RejectedFrames > 0 && !Phd2FreshTargetWindow.SameGuide(
                windowOwner, phd2.Snapshot, currentLock, preset.LockVerificationTolerancePixels))
                throw new InvalidOperationException("PHD2_GUIDE_WINDOW_EPOCH_CHANGED: A restarted optical window cannot span changed guide ownership or lock.");

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
                if (matchedPrimary is { } nativeGuide &&
                    result.NativeLockPosition is { } nativeLock &&
                    PointDistance(nativeLock, currentLock) <= preset.LockVerificationTolerancePixels)
                {
                    guideLocal = new PixelPoint(nativeGuide.X, nativeGuide.Y);
                    guidePositionAuthority = "SameFramePhd2PrimaryStarImage";
                    if (guideId.Gate.Disposition != GateDisposition.Passed)
                        Report($"PHD2 主星原生小图与本帧逐像素匹配；多星综合修正不作主星坐标，本地星形仅作诊断：{guideId.Gate.Code}");
                }
                else
                {
                    targetWindow.Observe(measured: false);
                    measurements.Clear();
                    if (!Phd2FreshTargetWindow.SameGuide(windowOwner, phd2.Snapshot,
                        currentLock, preset.LockVerificationTolerancePixels))
                        throw new InvalidOperationException("PHD2_GUIDE_WINDOW_EPOCH_CHANGED: Primary-star verification cannot span changed ownership or lock.");
                    var evidence = await PublishRunJsonEvidenceAsync("phd2-primary-star-frame-rejected",
                        "Primary-star crop did not match the same immutable frame; no averaged-offset substitute",
                        new { result.TriggerGuideFrame, result.PrimaryStarImage, result.NativeGuideStep,
                            result.NativeLockPosition, targetWindow.Captures, targetWindow.RejectedFrames,
                            localDiagnostic = guideId.Gate.Code, budgetReset = false, lockMutation = false },
                        result.Path, cancellationToken).ConfigureAwait(false);
                    if (rollingSample) throw new Phd2RollingWindowInterruptedException();
                    if (!targetWindow.Exhausted)
                    {
                        Report("PHD2 主星小图与当前保存帧未匹配，保留原帧并在原时限内重取完整窗口；不以多星修正量冒充星位。");
                        continue;
                    }
                    throw new PhysicalActionGateException(GateResult.TargetQuality(
                        "PHD2_FRESH_GUIDE_POSITION_UNPROVEN",
                        "The bounded same-frame primary-star verification window was exhausted; no lock or multi-star offset was accepted as a star position. Evidence: " + evidence));
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
                    c11RunFocusLock.Validate(slitFocusBefore, nightSetup.Value).Disposition == GateDisposition.Passed &&
                    c11RunFocusLock.Validate(slitFocusAfter, nightSetup.Value).Disposition == GateDisposition.Passed &&
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
                GuideStarSelector.DistanceToGuideExclusion(guideLocal, slitDetection.Geometry),
                TargetIdentityConfirmed: true,
                exposureMilliseconds,
                CommissionedMinimumExposureApplied: exposureMatched,
                targetEvidence,
                targetPositionAuthority switch
                {
                    Phd2TargetPositionAuthority.CatalogWcsProjection => "CATALOG_WCS_TARGET_FLUX_NOT_APPLICABLE",
                    Phd2TargetPositionAuthority.CatalogWcsRegisteredField => "REGISTERED_CATALOG_WCS_TARGET_FLUX_NOT_APPLICABLE",
                    Phd2TargetPositionAuthority.CatalogWcsIdentityWithSaturatedTopologyCentroid => "SATURATED_TARGET_TOPOLOGY_FLUX_NOT_APPLICABLE",
                    _ => guideMode == Phd2SlitGuideMode.DegradedDirectTargetGuiding ? "DEGRADED_DIRECT_TARGET_FLUX" : "TARGET_FLUX",
                },
                targetFlux,
                $"fresh-target-slit={targetResidual:F4}px;fresh-guide-lock={guideResidual:F4}px",
                targetPositionAuthority,
                GuidePositionMeasuredInFrame: true);
            var fluxWarning = Phd2SlitLockShiftPlanner.IsMeasuredTargetFluxWarning(measurement, preset.BuildMotionLimits());
            var gate = new GateResult(
                fluxWarning ? "PHD2_FRESH_TARGET_FLUX_WARNING" : "PHD2_FRESH_GUIDING_RESIDUAL",
                GateDisposition.Passed,
                $"Fresh guiding FITS proved target/slit-midpoint {targetResidual:F3}px and guide/lock {guideResidual:F3}px residuals.",
                new Dictionary<string, double>
                {
                    ["targetSlitResidualPixels"] = targetResidual,
                    ["targetSlitMidpointResidualPixels"] = targetResidual,
                    ["guideLockResidualPixels"] = guideResidual,
                    ["targetFluxMetric"] = targetFlux,
                    ["targetFluxOutsidePreferredEnvelope"] = fluxWarning ? 1 : 0,
                    ["guideFrame"] = result.TriggerGuideFrame,
                }, fluxWarning ? GateSeverity.Warning : GateSeverity.Info);
            if (fluxWarning)
                Report("目标通量警告：本帧积分亮度超出参考范围，保留真实数值；已确认的同帧目标/主星位置继续用于有界精调，不把积分亮度当成运动安全限位。");
            var state = new Phd2GuidingResidualState(gate, result, image, frame, candidates, slitDetection.Geometry, measurement, residualMountBinding);
            if (targetPositionAuthority != Phd2TargetPositionAuthority.CatalogWcsRegisteredField)
                await RememberStellarTargetTrackAsync(context, frame, raw, targetLocal, result.Path, actualSha,
                    targetFrameTiming, image.MetaData.Image.ExposureTime, trackedTarget, cancellationToken).ConfigureAwait(false);
            var acceptedOrdinal = measurements.Count + 1;
            measurements.Add(state);
            targetWindow.Observe(measured: true);
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
                    continuityPrediction, referenceGuide, referenceTargetMeasured, continuityAllowance,
                    targetSlitResidualPixels = targetResidual,
                    targetSlitMidpointResidualPixels = targetResidual,
                    guideLockResidualPixels = guideResidual,
                    guidePositionAuthority,
                    result.NativeGuideStep,
                    result.NativeEffectiveGuidePosition,
                    result.NativeLockPosition,
                    result.PrimaryStarImage,
                    saturatedIntegratedFluxDiagnostic,
                    fluxWarning,
                    minimumFluxMetric = preset.BuildMotionLimits().MinimumFluxMetric,
                    maximumFluxMetric = preset.BuildMotionLimits().MaximumFluxMetric,
                    result.TriggerGuideFrame,
                    result.EventSequence,
                    result.GuideStepUtc,
                    result.GuidingWasInterrupted,
                    result.ExposureChanged,
                    result.CaptureLoopStarted,
                    captureAttempt,
                    rejectedSlitFrames = 0,
                    rejectedTargetFrames = targetWindow.RejectedFrames,
                    consecutiveTargetFrames = targetWindow.ConsecutiveFrames,
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
            referenceGuide = guide;
            referenceTargetMeasured = true;
        }
        if (measurements.Count != count)
        {
            throw new TargetQualityEvidenceException("PHD2_FRESH_SLIT_REACQUISITION_EXHAUSTED",
                $"Fresh residual capture used {captureAttempt}/{maximumCaptureAttempts} attempts " +
                $"but obtained only {measurements.Count}/{count} accepted slit frames.");
        }
        return measurements.AsReadOnly();
    }

    private async Task<Phd2PlacementGuideChoice> AcquireFreshPhd2PlacementGuideAsync(
        ObservationContext context,
        G3FieldState seedField,
        Phd2SlitPlacementCommissioning preset,
        CancellationToken cancellationToken)
    {
        if (UsesCatalogWcsTargetAuthority(context) && preset.GuideMode == Phd2SlitGuideMode.DegradedDirectTargetGuiding)
            return Phd2PlacementGuideChoice.Failed(seedField,
                GateResult.Unknown("G3_CATALOG_GUIDE_MODE", "目录定位不能直导不可见目标；请选择旁星或包含旁星的自动导星模式。"),
                preset.GuideMode, "catalogue-only requires off-slit guide");
        if (preset.GuideMode == Phd2SlitGuideMode.OffSlitGuideStar || UsesCatalogWcsTargetAuthority(context))
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

    private async Task<(GuideStarSelection Selection, Phd2Point Requested, Phd2Point Selected, int Attempts)> SelectFreshPhd2GuideAsync(
        Phd2PlacementGuideChoice choice,
        Phd2SlitPlacementCommissioning preset,
        CancellationToken cancellationToken,
        IReadOnlyList<PixelPoint>? excludedCandidates = null,
        int maximumNativeSelectionAttempts = 4)
    {
        if (choice.Mode == Phd2SlitGuideMode.DegradedDirectTargetGuiding)
        {
            var star = choice.Selection.Star
                ?? throw new InvalidOperationException("Direct-target guiding has no fresh target centroid.");
            var requested = ToPhd2Domain(star.Centroid, preset);
            var selected = await phd2.SelectGuideStarAsync(requested, cancellationToken).ConfigureAwait(false);
            return (choice.Selection, requested, selected, 1);
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
                new Phd2SingleFrameRequest(choice.ExposureMilliseconds, configuration.G3.Binning,
                    configuration.G3.GainPercent, ReserveRunEvidencePath(evidenceRole, ".fit")),
                cancellationToken).ConfigureAwait(false);
            var freshImage = await imageDataFactory.CreateFromFile(fresh.Path, 16, false,
                RawConverterEnum.FREEIMAGE, cancellationToken).ConfigureAwait(false);
            if (freshImage.Properties.Width != preset.RoiWidth || freshImage.Properties.Height != preset.RoiHeight ||
                fresh.VerifiedExposureMilliseconds != choice.ExposureMilliseconds)
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
        var rejected = new List<string>(maximumNativeSelectionAttempts);
        var rejectedPoints = excludedCandidates?.ToList() ?? new List<PixelPoint>();
        var searchedRegions = new List<Phd2Rectangle>();
        for (var attempt = 1; attempt <= maximumNativeSelectionAttempts; attempt++)
        {
            Phd2Point selectedNative;
            Phd2Rectangle? nativeSearchRoi = null;
            var selectedInsideRequestedRoi = true;
            try
            {
                if (attempt == 1 && rejectedPoints.Count == 0)
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
            var plannedEdgeDistance = Phd2NativeGuideSearchRegions.PlannedEdgeDistance(
                preset.RoiWidth, preset.RoiHeight, selectedLocal, target.Centroid,
                choice.Field.SlitDetection.Geometry.AcquisitionPoint);
            var targetDistance = PixelDistance(selectedLocal, target.Centroid);
            var slitDistance = GuideStarSelector.DistanceToGuideExclusion(selectedLocal, choice.Field.SlitDetection.Geometry);
            var insideFrame = selectedLocal.X >= 0 && selectedLocal.X < preset.RoiWidth &&
                              selectedLocal.Y >= 0 && selectedLocal.Y < preset.RoiHeight;
            var insideSaturatedStructure = saturatedStructureExclusions.Any(item =>
                Phd2NativeGuideSaturatedRegions.Contains(item.Region, selectedLocal));
            var geometryAccepted = insideFrame && selectedInsideRequestedRoi &&
                                   !insideSaturatedStructure &&
                                   plannedEdgeDistance >= nativePolicy.MinimumEdgeDistancePixels &&
                                   targetDistance >= targetGuard &&
                                   slitDistance >= choice.Field.SlitDetection.Geometry.WidthPixels / 2 + nativePolicy.SlitGuardPixels;
            await PublishRunJsonEvidenceAsync(
                "phd2-native-guide-candidate",
                "PHD2 native candidate checked against unchanged physical geometry",
                new { attempt, maximumNativeSelectionAttempts, nativeSearchRoi, selectedNative, selectedLocal,
                    insideFrame, selectedInsideRequestedRoi, edgeDistance, plannedEdgeDistance,
                    plannedGuideTranslation = new PixelPoint(
                        choice.Field.SlitDetection.Geometry.AcquisitionPoint.X - target.Centroid.X,
                        choice.Field.SlitDetection.Geometry.AcquisitionPoint.Y - target.Centroid.Y),
                    targetDistance, targetGuard, slitDistance, geometryAccepted,
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
                    return (validation, selectedNative, selectedNative, attempt);
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
                    selectedNative,
                    attempt);
            }

            var reason =
                $"attempt {attempt}: selected=({selectedNative.X:F1},{selectedNative.Y:F1}), insideFrame={insideFrame}, insideRequestedRoi={selectedInsideRequestedRoi}, clippedStructure={insideSaturatedStructure}, edge={edgeDistance:F1}px, plannedEdge={plannedEdgeDistance:F1}px/{nativePolicy.MinimumEdgeDistancePixels:F1}px, target={targetDistance:F1}px/{targetGuard:F1}px, slit={slitDistance:F1}px/{choice.Field.SlitDetection.Geometry.WidthPixels / 2 + nativePolicy.SlitGuardPixels:F1}px";
            rejected.Add(reason);
            rejectedPoints.Add(selectedLocal);
            Report($"warning：PHD2 候选或预计入缝路径靠近边缘、目标光晕、饱和扩展结构或狭缝（{reason}）；拍摄新全帧后原生重选，不直接阻断整轮");
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
        Phd2SlitPlacementCommissioning preset,
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
            if (directChoice.Selection.Gate.FailureScope == GateFailureScope.TargetQuality)
                throw new TargetQualityEvidenceException(directChoice.Selection.Gate.Code,
                    directChoice.Selection.Gate.Message);
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
        Phd2SlitPlacementCommissioning preset,
        Phd2SlitGuideMode resolvedMode,
        CancellationToken cancellationToken,
        int? selectedExposureMilliseconds = null)
    {
        var exposureMilliseconds = selectedExposureMilliseconds ?? GuideExposureFor(preset, resolvedMode);
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
        var targetCaptureRequestedUtc = DateTimeOffset.UtcNow;
        var selectionBeforeExposure = CaptureG3FrameMountReadback();
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
        G3CatalogRegistrationReference? catalogReference = seedField.CatalogRegistrationReference;
        var targetFrameTiming = new G3SparseExposureTiming(targetCaptureRequestedUtc, capture.CompletedUtc,
            image.MetaData.Image.ExposureTime, DateTimeOffset.UtcNow);
        var selectionTargetPrediction = seedField.TargetIdentification.Target?.Centroid ?? seedField.TargetIdentification.PredictedPoint;
        await RetainSparseG3FrameAsync(context, image, capture.Path, frameSha256, selectionMountBinding,
            selectionBeforeExposure, exposureMilliseconds, selectionTargetPrediction, cancellationToken,
            captureTiming: targetFrameTiming).ConfigureAwait(false);
        var trackedTarget = await PredictStellarTargetTrackAsync(context, seedField, frame, raw,
            selectionTargetPrediction, seedField.SlitDetection.Geometry, preset, capture.Path, frameSha256,
            targetFrameTiming, cancellationToken).ConfigureAwait(false);
        if (trackedTarget is not null) selectionTargetPrediction = trackedTarget.Point;
        var targetRecognitionRadius = trackedTarget is null ? preset.TargetSearchRadiusPixels :
            Math.Min(preset.TargetSearchRadiusPixels, Math.Max(6, preset.MaximumResidualGrowthPixels));
        var selectionTrackProbe = new G3PlateSolveProbeState(GateResult.Warn("G3_TRACK_SEARCH_ONLY", "Current pixels, not motion authority"),
            capture.Path, image, null, null, Array.Empty<G3PlateSolveAttemptEvidence>(),
            MountBinding: selectionMountBinding, BeforeExposureMountReadback: selectionBeforeExposure)
        { ExposureStartedUtc = targetFrameTiming.CaptureRequestedUtc, ExposureEndedUtc = targetFrameTiming.CaptureCompletedUtc,
            ExposureMillisecondsActual = exposureMilliseconds };
        if (UsesCatalogWcsTargetAuthority(context))
        {
            if (resolvedMode != Phd2SlitGuideMode.OffSlitGuideStar)
                return Phd2PlacementGuideChoice.Failed(seedField,
                    GateResult.Unknown("G3_CATALOG_GUIDE_MODE", "目录定位使用独立旁星导星，不把不可见目标当成直导星。"), resolvedMode, "catalogue-only guide mode");
            catalogReference = await CatalogReferenceAsync(context, seedField, cancellationToken).ConfigureAwait(false);
            var registered = await RegisterCatalogueFrameAsync(context, catalogReference, frame, image.Data.FlatArray,
                seedField.TargetIdentification.Target?.Centroid ?? seedField.TargetIdentification.PredictedPoint,
                seedField.SlitDetection.Geometry, preset, capture.Path, cancellationToken).ConfigureAwait(false);
            identification = TargetIdentification.FromCatalogWcs(registered.Target, frame.Width, frame.Height,
                "目录采样点保持不变；新选星帧通过独立参考星配准，不使用目标附近的亮峰。");
        }
        else if (seedField.BrightTargetAuthority is { } priorBrightAuthority)
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
            var predictedTarget = selectionTargetPrediction;
            var topology = SaturatedTargetGhostTopologyAnalyzer.Analyze(
                frame,
                predictedTarget,
                targetRecognitionRadius);
            // A long selection frame must not replace the independently checked
            // short-exposure centre with a clipped core/ghost blend. Fresh fine
            // residuals are still mandatory before any lock movement or science.
            identification = (resolvedMode == Phd2SlitGuideMode.DegradedDirectTargetGuiding ||
                string.IsNullOrWhiteSpace(seedField.TargetIdentification.BoundShortPositionEvidencePath)) &&
                topology.Gate.Disposition == GateDisposition.Passed && topology.Target is { } topologyTarget
                ? new TargetIdentification(
                    GateResult.Pass(
                        "PHD2_GUIDE_FRAME_CATALOG_TARGET_TOPOLOGY_REFINED",
                        $"Fresh {resolvedMode} retains mount-bound catalogue identity and refines its detector position from one filled saturated core; {topology.Ghosts.Count} hollow annular ghost(s) were excluded.",
                        topology.Gate.Metrics) with { Severity = topology.Gate.Severity },
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
            if (!Phd2FreshTargetFluxPolicy.HasMeasuredHandoffPosition(identification) &&
                !G3CatalogTargetPositionPolicy.NeedsShortPositionCheck(frame, predictedTarget, preset.TargetSearchRadiusPixels))
            {
                var stellar = SlitTargetIdentifier.Identify(frame, candidates, predictedTarget,
                    targetRecognitionRadius, preset.MinimumTargetSignalToNoise, preset.MinimumTargetUniquenessRatio);
                if (stellar.Gate.Disposition == GateDisposition.Passed) identification = stellar;
            }
            // Off-slit guiding can wait for fresh target evidence, but direct
            // selection must not pretend an old catalogue cursor is a new core.
            // A split/ambiguous source exhausts this local alternative; the
            // checked-stop target-quality route can continue the native night.
            if (!Phd2FreshTargetFluxPolicy.HasMeasuredHandoffPosition(identification) && trackedTarget is null)
                identification = await TryWideFieldStellarMeasurementAsync(context, selectionTrackProbe, frame, candidates,
                    selectionTargetPrediction, preset, cancellationToken).ConfigureAwait(false) ?? identification;
            if (resolvedMode == Phd2SlitGuideMode.DegradedDirectTargetGuiding &&
                !Phd2FreshTargetFluxPolicy.HasMeasuredHandoffPosition(identification))
            {
                var gate = GateResult.TargetQuality("PHD2_DIRECT_TARGET_POSITION_UNMEASURED",
                    "Fresh direct-target image has no unique measured core. The catalogue prediction was not submitted to PHD2 as a measured star; retain the optical evidence and follow the target's checked-stop policy.");
                var rejectedField = seedField with { Gate = gate, FramePath = capture.Path, Image = image, Frame = frame,
                    Candidates = candidates, TargetIdentification = identification, MountBinding = selectionMountBinding };
                return Phd2PlacementGuideChoice.Failed(rejectedField, gate, resolvedMode, gate.Message, capture);
            }
        }
        else
        {
            var predictedTarget = selectionTargetPrediction;
            identification = SlitTargetIdentifier.Identify(
                frame,
                candidates,
                predictedTarget,
                targetRecognitionRadius,
                preset.MinimumTargetSignalToNoise,
                preset.MinimumTargetUniquenessRatio);
            if (identification.Gate.Disposition != GateDisposition.Passed && trackedTarget is null)
                identification = await TryWideFieldStellarMeasurementAsync(context, selectionTrackProbe, frame, candidates,
                    selectionTargetPrediction, preset, cancellationToken).ConfigureAwait(false) ?? identification;
            if (identification.Gate.Disposition != GateDisposition.Passed || identification.Target is null)
            {
                var gate = GateResult.Unknown(
                    "PHD2_GUIDE_FRAME_TARGET_CONTINUITY_FAILED",
                    $"Fresh {resolvedMode} selection frame did not re-identify the target: {identification.Gate.Code}: {identification.Gate.Message}");
                var failedField = seedField with { Gate = gate, FramePath = capture.Path, Image = image, Frame = frame, Candidates = candidates, MainFocusMeasurement = focus, TargetIdentification = identification };
                return Phd2PlacementGuideChoice.Failed(failedField, gate, resolvedMode, gate.Message, capture);
            }
        }

        if (Phd2FreshTargetFluxPolicy.HasMeasuredHandoffPosition(identification) &&
            RejectStellarTargetTrackCandidate(trackedTarget, frame, identification.Target!.Centroid,
                image.MetaData.Image.ExposureTime, Math.Max(6, preset.MaximumResidualGrowthPixels), out _))
        {
            var gate = GateResult.TargetQuality("PHD2_GUIDE_FRAME_TARGET_CONTINUITY_FAILED",
                "Fresh selection disagrees with the last-trusted component after independent field registration; retained the trusted trajectory, not this tentative peak.");
            return Phd2PlacementGuideChoice.Failed(seedField with { Gate = gate, FramePath = capture.Path,
                Image = image, Frame = frame, TargetIdentification = identification }, gate, resolvedMode, gate.Message, capture);
        }
        if (Phd2FreshTargetFluxPolicy.HasMeasuredHandoffPosition(identification))
            await RememberStellarTargetTrackAsync(context, frame, raw, identification.Target!.Centroid,
                capture.Path, frameSha256, targetFrameTiming, image.MetaData.Image.ExposureTime,
                trackedTarget, cancellationToken).ConfigureAwait(false);

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
            CatalogRegistrationReference = catalogReference,
            ExposureTiming = targetFrameTiming,
            TargetTrackSource = null,
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
        Phd2SlitPlacementCommissioning preset,
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
        Phd2SlitPlacementCommissioning preset,
        string currentPierSide)
    {
        var commissionedSource = BuildPhd2SensorTopology(preset, preset.PierSide);
        return Phd2PierAdaptiveTopologyPolicy.Resolve(
            commissionedSource,
            preset.LockedTopologyFingerprintSha256,
            currentPierSide,
            preset.CalibrationPierSideEvidenceComplete,
            ownerManagedCalibration: true);
    }

    private Phd2CalibrationCandidateSelection SelectPhd2CalibrationQuality(
        Phd2CalibrationValidation calibration,
        Phd2SlitPlacementCommissioning preset,
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
        Phd2SlitPlacementCommissioning preset,
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
        Phd2SlitPlacementCommissioning preset,
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

    private Phd2LockShiftPendingState CreatePhd2PendingState(
        ObservationContext context,
        Phd2SlitPlacementCommissioning preset,
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
        var excludedExposureSeconds = phd2SettledHandoffBudget is { } inherited &&
            inherited.LineageId == ledger.LineageId ? inherited.ExcludedAtrExposureSeconds : 0;
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
            excludedExposureSeconds > 0 ? phd2SettledHandoffBudget!.StartedUtc : ledger.StartedUtc,
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
            originSlit.Y,
            excludedExposureSeconds,
            session.PrimaryStarSettleRecoveryAttempted ||
                pendingPhd2LockShift?.PrimaryStarSettleRecoveryAttempted == true ||
                phd2SettledHandoffBudget?.PrimaryStarSettleRecoveryAttempted == true,
            Math.Max(session.PrimaryStarSettleRecoveryStage,
                Math.Max(pendingPhd2LockShift?.PrimaryStarSettleRecoveryStage ?? 0,
                    phd2SettledHandoffBudget?.PrimaryStarSettleRecoveryStage ?? 0)));
    }

    private async Task PublishPhd2GuideSelectionEvidenceAsync(
        ObservationContext context,
        G3FieldState field,
        GuideStarSelection selection,
        Phd2Point requested,
        Phd2Point selected,
        Phd2SlitPlacementCommissioning preset,
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

    private Phd2SettleCriteria InitialNativeGuideCriteria(Phd2SettleCriteria configured) =>
        configuration.SequencePlan?.InitialGuideCriteria(configured) ?? configured;

    private Phd2SettleCriteria Phd2SettleCriteriaForSlitPlacement(
        Phd2SlitPlacementCommissioning preset)
    {
        var configured = Phd2SettleCriteriaFromConfiguration();
        if (preset.MaximumGuideLockWarningResidualPixels > preset.MaximumGuideLockResidualPixels)
            return Phd2SlitSettleTiming.ForBoundedWarningPlacement(configured,
                preset.EffectiveGuideLockResidualPixels, preset.MaximumStageSeconds);
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
        (HasSupervisedScienceOptIn() || session.ReadOnlyPostLockObservation?.MeasuredContinuation == true) &&
        session.FreshGuidingWindowReplacedSettle &&
        (session.ReadOnlyPostLockObservation is { } observation
            ? observation.HasAcceptedWindow(snapshot)
            : CanReplaceSettleWithFreshGuidingWindow(session.Settle, snapshot, session.ConnectionEpoch, session.GuideEpoch));

    private async Task<IReadOnlyList<Phd2GuidingResidualState>> CapturePhd2PlacementGuideWindowAsync(
        ObservationContext context,
        Phd2SlitPlacementCommissioning preset,
        Phd2SensorTopology topology,
        Phd2Point currentLock,
        Phd2Point expectedTarget,
        SlitGeometry runtimeSlitLocal,
        Phd2SlitGuideMode guideMode,
        int count,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken,
        IReadOnlyList<Phd2GuidingResidualState>? previousWindow = null,
        Phd2Point? referenceGuide = null,
        bool referenceTargetMeasured = true)
    {
        var baseline = phd2.Snapshot;
        var remaining = deadlineUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            throw new TargetQualityEvidenceException("PHD2_FRESH_GUIDE_WINDOW_DEADLINE",
                "No time remains for fresh position evidence; this is not a guide precision rejection.");
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(remaining);
        IReadOnlyList<Phd2GuidingResidualState> measurements;
        try
        {
            measurements = await Phd2PlacementGuideWindowPolicy.CaptureConsecutiveWindowAsync(
                count, previousWindow,
                requested => CapturePhd2GuidingMeasurementsAsync(
                    context, preset, topology, currentLock, expectedTarget,
                    runtimeSlitLocal, guideMode, requested, bounded.Token, rollingSample: requested == 1,
                    referenceGuide: referenceGuide, referenceTargetMeasured: referenceTargetMeasured),
                item => item.Measurement).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TargetQualityEvidenceException("PHD2_FRESH_GUIDE_WINDOW_DEADLINE",
                "The unchanged deadline expired before a complete fresh optical window; this is not a guide precision rejection.");
        }
        var snapshot = phd2.Snapshot;
        var sameEpochAndLock = snapshot.IsConnected && snapshot.AppState == Phd2AppState.Guiding &&
            snapshot.ConnectionEpoch == baseline.ConnectionEpoch && snapshot.GuideEpoch == baseline.GuideEpoch &&
            snapshot.LockPosition is { } actual && PointDistance(actual, currentLock) <= preset.LockVerificationTolerancePixels;
        var liveOutputConfirmed = !snapshot.AutomationPaused && !snapshot.Phd2Paused &&
            !snapshot.PendingSettleOperationId.HasValue && snapshot.GuideOutput is { Failed: false, ConsecutiveMissingOutputs: 0 };
        phd2.ThrowIfGuideOutputFailed();
        var residuals = measurements.Select(item => PointDistance(item.Measurement.GuideStar, currentLock)).ToArray();
        var accepted = Phd2PlacementGuideWindowPolicy.HasUsableMeasuredWindow(
            measurements.Select(item => item.Measurement).ToArray(), residuals, sameEpochAndLock, liveOutputConfirmed);
        var guideTrackingWarning = accepted &&
            Phd2PlacementGuideWindowPolicy.HasPrecisionWarning(residuals);
        await PublishRunJsonEvidenceAsync("phd2-placement-guide-window",
            "Single fresh measured window; guide residual above 3px is advisory only",
            new { accepted, guideTrackingWarning, residuals,
                warningThresholdPixels = Phd2PlacementGuideWindowPolicy.GuideResidualWarningPixels,
                precisionBlocks = false, sameEpochAndLock, liveOutputConfirmed,
                targetPositionAuthorities = measurements.Select(x => x.Measurement.TargetPositionAuthority.ToString()).ToArray(),
                measuredGuidePositions = measurements.Select(x => x.Measurement.GuidePositionMeasuredInFrame).ToArray(),
                deadlineUtc, lockMutation = false, guidingRestarted = false, budgetReset = false },
            measurements.LastOrDefault()?.Frame.Path, cancellationToken).ConfigureAwait(false);
        if (!sameEpochAndLock)
            throw new InvalidOperationException("PHD2_GUIDE_WINDOW_EPOCH_CHANGED: The guide epoch or lock changed during the measured window.");
        if (!accepted)
            throw new InvalidOperationException("PHD2_GUIDE_WINDOW_EVIDENCE_INVALID: Fresh measured positions or current guide output are missing; this is not a guide precision rejection.");
        placementGuidePrecisionWarningActive |= guideTrackingWarning;
        if (guideTrackingWarning)
            Report($"导星精度警告：实测偏差 {string.Join(", ", residuals.Select(value => value.ToString("F2", CultureInfo.InvariantCulture)))}px 超过 3px；仅记录警告，按同帧星位继续。目标入缝位置与运动完成情况独立核对。");
        return measurements;
    }

    private async Task VerifyWindSampledGuidingBeforeAtrAsync(
        ObservationContext context,
        CancellationToken cancellationToken,
        bool forceFreshWindow = false)
    {
        try
        {
            await VerifyWindSampledGuidingBeforeAtrCoreAsync(context, cancellationToken, forceFreshWindow).ConfigureAwait(false);
        }
        catch (Phd2GuidingFrameLostException ex)
        {
            // Only structured owner evidence enters the existing science
            // recovery policy. Transport silence must not acquire this route.
            await PublishRunJsonEvidenceAsync("phd2-pre-atr-star-lost",
                "PHD2 reported a lost guide star during a fresh optical window, not a camera timeout",
                new { ex.Snapshot.ConnectionEpoch, ex.Snapshot.GuideEpoch, ex.LostFrame,
                    cameraTimeoutInferred = false, staleFrameAccepted = false },
                lastG3Field?.FramePath, cancellationToken).ConfigureAwait(false);
            throw new PhysicalActionGateException(GateResult.Unknown("GUIDING_LOST", ex.Message));
        }
    }

    private async Task VerifyWindSampledGuidingBeforeAtrCoreAsync(
        ObservationContext context,
        CancellationToken cancellationToken,
        bool forceFreshWindow = false)
    {
        var session = phd2SlitPlacementSession;
        if (session is null || (!forceFreshWindow && !session.FreshGuidingWindowReplacedSettle &&
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
                cancellationToken, readoutGrace, referenceGuide: session.LastMeasurement.Measurement.GuideStar).ConfigureAwait(false);
            var residuals = measurements.Select(item => PointDistance(
                item.Measurement.TargetCentroid, item.Measurement.RecognizedSlitAcquisitionPoint)).ToArray();
            var snapshot = phd2.Snapshot;
            var sameEpoch = IsGuidingStable() &&
                snapshot.ConnectionEpoch == session.ConnectionEpoch && snapshot.GuideEpoch == session.GuideEpoch;
            var identityConfirmed = measurements.All(item => item.Measurement.TargetIdentityConfirmed);
            var precisionPassed = sameEpoch && identityConfirmed && double.IsFinite(tolerance) && tolerance > 0 &&
                residuals.All(value => double.IsFinite(value) && value <= tolerance);
            var precisionWarning = !precisionPassed && configuration.ScienceQuality.AllowsPreExposureWarning(
                sameEpoch, identityConfirmed &&
                measurements.All(item => item.Measurement.GuidePositionMeasuredInFrame &&
                    item.Measurement.TargetPositionAuthority != Phd2TargetPositionAuthority.CatalogWcsProjection) &&
                    HasSupervisedScienceOptIn(), residuals, preset.MaximumAcquisitionResidualPixels);
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
            if (!sameEpoch)
                throw new PhysicalActionGateException(GateResult.Unknown(
                    "PHD2_SCIENCE_GUIDE_EPOCH_CHANGED", "The guiding session changed during pre-exposure verification; no ATR exposure was started."));
            if (!identityConfirmed)
                throw new PhysicalActionGateException(GateResult.Unknown(
                    "PHD2_SCIENCE_TARGET_IDENTITY_UNCONFIRMED", "Target identity was not confirmed in the pre-exposure window; no ATR exposure was started."));
            if (windowAttempt == maximumWindows)
            {
                if (configuration.ScienceQuality.WarningProbeEnabled &&
                    await TryResumePreAtrPlacementAsync(context, session, preset, currentLock,
                        measurements, cancellationToken).ConfigureAwait(false)) return;
                throw new TargetQualityEvidenceException(
                    "PHD2_SCIENCE_FRESH_SLIT_WINDOW_REJECTED",
                    $"Fresh pre-exposure window {windowAttempt}/{maximumWindows} residuals ({string.Join(", ", residuals.Select(value => value.ToString("F2", CultureInfo.InvariantCulture)))}) did not meet the saved quality policy with valid target identity; no ATR exposure was started.");
            }
            Report($"ATR 曝光前遇到短时风扰：第 {windowAttempt}/{maximumWindows} 组新帧未全部满足 {tolerance:F2}px；保持 PHD2 导星，重采下一组，不移动锁点");
        }
    }

    private async Task<bool> TryResumePreAtrPlacementAsync(ObservationContext context,
        Phd2SlitPlacementSession session, Phd2SlitPlacementCommissioning preset, Phd2Point currentLock,
        IReadOnlyList<Phd2GuidingResidualState> measurements, CancellationToken token)
    {
        // Called once after this pre-exposure check's finite wait. Convergence
        // cannot call the ATR check recursively; it owns all further motion and
        // post-motion fresh-window acceptance under the original lineage.
        if (pendingPhd2LockShift is not null || measurements.Count < 3 ||
            measurements.Any(x => !x.Measurement.TargetIdentityConfirmed ||
                !x.Measurement.GuidePositionMeasuredInFrame ||
                !Phd2PlacementGuideWindowPolicy.HasMeasuredPositionAuthority(x.Measurement.TargetPositionAuthority)))
            return false;
        var path = Phd2LockShiftPendingPath(context.Plan.ObservationRunId);
        var loaded = await Phd2LockShiftPendingStore.LoadAsync(path, token).ConfigureAwait(false);
        if (loaded.Error is not null || loaded.State is not { } state) return false;
        var binding = ValidateCurrentPhd2LockLedgerBinding(context, preset, state);
        if (binding.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(binding);
        var actual = await phd2.GetLockPositionAsync(token).ConfigureAwait(false);
        if (!Phd2ScienceContinuationPolicy.CanObserveAtSettledLock(state, phd2.Snapshot,
            session.ConnectionEpoch, session.GuideEpoch, IsGuidingStable(), actual,
            preset.LockVerificationTolerancePixels) || actual is null ||
            PointDistance(actual, currentLock) > preset.LockVerificationTolerancePixels) return false;
        var ledger = Phd2ScienceContinuationPolicy.ResumeMotionLedger(state, DateTimeOffset.UtcNow);
        if (ledger is null) return false;
        var identity = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), token).ConfigureAwait(false);
        if (!identity.IsValid || ValidatePhdProfileBindingEvidence().Disposition != GateDisposition.Passed) return false;
        await RequireImmediatePhysicalActionGatesAsync(context, token).ConfigureAwait(false);
        phd2SettledHandoffBudget = state;
        session = session with { LastMeasurement = measurements[^1] };
        phd2SlitPlacementSession = session;
        lastG3Field = UpdateG3FieldFromGuidingResidual(lastG3Field!, measurements[^1], preset);
        await PublishRunJsonEvidenceAsync("phd2-pre-atr-resume-placement",
            "Fresh pre-exposure drift returned to the same bounded placement lineage",
            new { state.LineageId, state.StartedUtc, state.ExcludedAtrExposureSeconds,
                state.AttemptsUsed, state.CumulativeCommandedPixels, state.OriginLockX, state.OriginLockY,
                budgetReset = false, nativeGuideRestarted = false, exposureStarted = false },
            measurements[^1].Frame.Path, token).ConfigureAwait(false);
        Report("试拍前目标偏离：沿原导星会话和原精调预算继续修正；不重选星、不重校准、不重置计数。");
        var result = await phd2Convergence.ConvergeAsync(context,
            new(session, ledger, measurements, preset, identity, path, Metadata(commissioning!), 0, 0,
                AllowCoarseReacquisition: false), token)
            .ConfigureAwait(false);
        if (!result.CanAdvance) throw new PhysicalActionGateException(result.Gate);
        return true;
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

    internal static async Task<(bool RunIsTerminal, GateResult? Error)> ValidatePhd2LockManifestAsync(
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
        Phd2SlitPlacementCommissioning preset,
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
        Phd2SlitPlacementCommissioning preset,
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
        Phd2SlitPlacementCommissioning preset,
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

        var sourceManifest = await new ObservationRunJournalStore(Path.Combine(
            SlitPlacementObservationsRoot(), settledForeignState.ObservationRunId, "manifest.json"))
            .ReadAsync(cancellationToken).ConfigureAwait(false);
        var currentManifest = await new ObservationRunJournalStore(Path.Combine(
            SlitPlacementObservationsRoot(), context.Plan.ObservationRunId, "manifest.json"))
            .ReadAsync(cancellationToken).ConfigureAwait(false);
        if (sourceManifest is null || currentManifest is null ||
            sourceManifest.ObservationRunId != settledForeignState.ObservationRunId ||
            currentManifest.ObservationRunId != context.Plan.ObservationRunId)
            return GateResult.Unknown("PHD2_LOCK_HANDOFF_MANIFEST_MISSING",
                "Return verified, but source/current run history cannot establish the budget boundary. No new placement budget was created.");

        if (Phd2LockShiftBudgetHandoff.CanCloseCancelledSourceAsHistory(
                settledForeignState, sourceManifest.TerminalState, sourceManifest.UpdatedUtc,
                context.Plan.ObservationRunId, currentManifest.CreatedUtc, existing.State is not null))
        {
            var ownerGate = host.RealRunOwnershipGate();
            if (ownerGate.Disposition != GateDisposition.Passed) return ownerGate;
            // No current-run copy: the caller persists the returned source, then
            // checked-stops PHD2 and reacquires G3 before any new placement.
            // A crash before that persistence leaves the original debt open.
            await PublishRunJsonEvidenceAsync(
                "phd2-cancelled-source-return-closed",
                "Cancelled observation's verified return closes its historical budget, not a new run's placement window",
                new
                {
                    sourceRunId = settledForeignState.ObservationRunId,
                    currentRunId = context.Plan.ObservationRunId,
                    sourceManifest.TerminalState,
                    sourceEndedUtc = sourceManifest.UpdatedUtc,
                    currentRunCreatedUtc = currentManifest.CreatedUtc,
                    returnedSource = settledForeignState,
                    oldCountersAndClockPreserved = true,
                    currentRunBudgetCopyCreated = false,
                    checkedStopAndFreshAcquisitionStillRequired = true,
                    placementAuthorized = false,
                }, settledForeignState.LastFramePath, cancellationToken).ConfigureAwait(false);
            Report("已取消旧运行的回程已核验；旧次数、位移和计时保留在历史记录，不再转给本轮。确认停止并重建目标场后，本轮独立开始精调。");
            return GateResult.Pass("PHD2_LOCK_CANCELLED_SOURCE_RETURN_CLOSED",
                "The cancelled source's return is verified. Preserve its historical ledger without copying its expired clock into the later explicit observation.");
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

    private static Phd2Point ToPhd2Domain(PixelPoint local, Phd2SlitPlacementCommissioning preset) =>
        preset.CoordinateDomain == Phd2ImageCoordinateDomain.FullSensorCoordinates
            ? new Phd2Point(local.X + preset.RoiX, local.Y + preset.RoiY)
            : new Phd2Point(local.X, local.Y);

    private static PixelPoint ToFrameLocal(Phd2Point domain, Phd2SlitPlacementCommissioning preset) =>
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
        Phd2SlitPlacementCommissioning preset)
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

internal sealed class Phd2NativeGuideSelectionExhaustedException : Exception
{
    // Only called after the owned preview/guide operation has checked-stopped.
    // Native target termination still independently verifies all owners/ledgers.
    internal StageResult AfterCheckedStop() => new(GateResult.TargetQuality(
        "PHD2_OFF_SLIT_NATIVE_SELECTION_EXHAUSTED",
        $"Bounded PHD2-native off-slit selection exhausted and checked-stopped; no substitute guide/lock/mount command is authorized. {Message}"));

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
    Phd2PostLockGuidingObservation? ReadOnlyPostLockObservation = null,
    bool PrimaryStarSettleRecoveryAttempted = false,
    int PrimaryStarSettleRecoveryStage = 0);
