using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NINA.Astrometry;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Deterministic, commissioning-bound optical-ghost assistance for G3.  This
/// partial contains no acquisition or motion command: it consumes immutable
/// OFF frames already captured by the normal PHD2-owned slit sequence.
/// </summary>
internal sealed partial class RealObservationStageRunner
{
    private GhostRunnerAssistanceEvidence? lastGhostAssistanceEvidence;
    private GhostQhySolveMountBinding? lastQhySolveMountBinding;

    private GhostQhySolveMountBinding CaptureGhostQhySolveMountBinding(
        ObservationContext context,
        QhyFrameRecord accepted,
        PlateSolveEvidence solve,
        QhyAcceptedFrameMountBinding captureBinding)
    {
        return new GhostQhySolveMountBinding(
            context.Plan.ObservationRunId,
            solve.EvidenceSha256,
            solve.EvidencePath,
            accepted.FrameId,
            accepted.Sha256,
            accepted.ExposureEndedUtc,
            captureBinding.BindingSha256,
            captureBinding.AfterAcceptedFrame.RightAscensionDegrees,
            captureBinding.AfterAcceptedFrame.DeclinationDegrees,
            captureBinding.AfterAcceptedFrame.CoordinateEpoch,
            captureBinding.AfterAcceptedFrame.PierSide,
            captureBinding.AfterAcceptedFrame.ReportedUtc);
    }

    private async Task<G3FieldState?> TryAcquireTargetFromGhostAsync(
        ObservationContext context,
        G3SlitIlluminationSequence sequence,
        IReadOnlyList<G3LoadedIlluminationFrame> loadedFrames,
        G3LoadedIlluminationFrame reference,
        MonochromeFrame offComposite,
        SlitIlluminationPairAnalysis pairAnalysis,
        SlitGeometry slitSeed,
        C11MainFocusOwnerSnapshot focusOwnerBefore,
        C11MainFocusOwnerSnapshot focusOwnerAfter,
        G3StellarFocusMeasurement currentFocusMeasurement,
        PlateSolveEvidence? g3Solve,
        CancellationToken cancellationToken)
    {
        var mode = configuration.G3.GhostAssistanceMode;
        if (mode == GhostAssistanceMode.Skip)
        {
            var skipped = GhostUnavailableResult(
                mode,
                GateResult.Unknown(
                    "GHOST_TEMPLATE_SKIPPED",
                    "Ghost assistance is explicitly set to Skip. The existing fresh long-exposure WCS/N.I.N.A. centering/bounded-search path remains selected."));
            await PublishGhostAssistanceEvidenceAsync(
                context,
                sequence,
                reference,
                pairAnalysis,
                focus: null,
                externalIdentity: null,
                runtime: null,
                extractions: Array.Empty<GhostSourceExtractionResult>(),
                skipped,
                cancellationToken,
                rehashReferenceSource: false).ConfigureAwait(false);
            return null;
        }

        var ghost = commissioning?.Value.GhostAssistance;
        if (ghost is null)
        {
            return await CompleteUnavailableGhostAttemptAsync(
                context,
                sequence,
                reference,
                offComposite,
                pairAnalysis,
                currentFocusMeasurement,
                g3Solve,
                mode,
                GateResult.Unknown(
                    "GHOST_COMMISSIONING_BINDING_MISSING",
                    "The selected ghost-assistance mode has no complete hash-bound calibration/policy binding in the loaded commissioning preset."),
                cancellationToken).ConfigureAwait(false);
        }

        var commissioningIssues = ghost.Validate();
        if (commissioningIssues.Count > 0)
        {
            return await CompleteUnavailableGhostAttemptAsync(
                context,
                sequence,
                reference,
                offComposite,
                pairAnalysis,
                currentFocusMeasurement,
                g3Solve,
                mode,
                GateResult.Unknown(
                    "GHOST_COMMISSIONING_BINDING_INVALID",
                    $"The hash-bound ghost commissioning payload is invalid: {string.Join("; ", commissioningIssues)}."),
                cancellationToken).ConfigureAwait(false);
        }

        var slitGate = ValidateGhostSlitAuthority(pairAnalysis, slitSeed);
        if (slitGate.Disposition != GateDisposition.Passed)
        {
            return await CompleteUnavailableGhostAttemptAsync(
                context,
                sequence,
                reference,
                offComposite,
                pairAnalysis,
                currentFocusMeasurement,
                g3Solve,
                mode,
                slitGate,
                cancellationToken).ConfigureAwait(false);
        }

        var focus = ValidateGhostIndependentFocus(
            ghost,
            focusOwnerBefore,
            focusOwnerAfter,
            DateTimeOffset.UtcNow);
        if (focus.Gate.Disposition != GateDisposition.Passed || focus.Binding is null)
        {
            return await CompleteUnavailableGhostAttemptAsync(
                context,
                sequence,
                reference,
                offComposite,
                pairAnalysis,
                currentFocusMeasurement,
                g3Solve,
                mode,
                focus.Gate,
                cancellationToken,
                focus.Binding).ConfigureAwait(false);
        }

        var evaluatedUtc = DateTimeOffset.UtcNow;
        var externalIdentity = await ResolveGhostExternalIdentityAsync(
            context,
            reference,
            g3Solve,
            ghost,
            evaluatedUtc,
            cancellationToken).ConfigureAwait(false);

        var preparation = await IsolateGhostFramePreparationAsync(
            async token =>
            {
                var requiredFrameCount = Math.Max(2, ghost.MatchPolicy.MinimumFrameCount);
                var selected = SelectFreshSameExposureGhostFrames(
                    loadedFrames,
                    requiredFrameCount,
                    ghost.MatchPolicy.MaximumFrameAge,
                    evaluatedUtc);
                var preparedExtractions = new List<GhostSourceExtractionResult>(selected.Count);
                foreach (var frame in selected)
                {
                    var currentSha256 = await ComputeFileSha256Async(
                        frame.Captured.Capture.Path,
                        token).ConfigureAwait(false);
                    if (!SameHash(currentSha256, frame.Captured.Sha256))
                    {
                        preparedExtractions.Add(new GhostSourceExtractionResult(
                            GateResult.Fail(
                                "GHOST_SOURCE_FRAME_HASH_MISMATCH",
                                $"Immutable OFF frame {frame.Captured.Role} changed after the slit sequence hash was recorded."),
                            null,
                            string.Empty,
                            Array.Empty<GhostSourceOverlay>()));
                        continue;
                    }

                    var exposureMilliseconds = GhostFrameExposureMilliseconds(frame);
                    var gain = GhostFrameGain(frame);
                    var metadata = new GhostFrameCaptureMetadata(
                        $"{sequence.SequenceId}:{frame.Captured.Role}",
                        frame.Captured.Sha256,
                        frame.Captured.Capture.CompletedUtc,
                        exposureMilliseconds,
                        gain);
                    preparedExtractions.Add(GhostFrameObservationFactory.FromMonochromeFrame(
                        frame.Frame,
                        metadata,
                        ghost.ExtractionPolicy));
                }

                var properties = reference.Image.Properties;
                var binX = reference.Image.MetaData.Camera.BinX;
                var binY = reference.Image.MetaData.Camera.BinY;
                var detector = binX > 0 && binY > 0
                    ? new GhostDetectorGeometry(0, 0, properties.Width * binX, properties.Height * binY, binX, binY)
                    : new GhostDetectorGeometry(0, 0, properties.Width, properties.Height, 0, 0);
                var runtimeBinding = new GhostRuntimeBinding(
                    context.Plan.ObservationRunId,
                    context.Plan.Target.CatalogId,
                    evaluatedUtc,
                    ghost.RuntimeFingerprint.InstallationEpochId,
                    configuration.Phd2.CameraStableId,
                    configuration.Phd2.ProfileId.ToString(CultureInfo.InvariantCulture),
                    ghost.ExtractionPolicy.ExtractorKind,
                    ghost.ExtractionPolicy.ExtractorVersion,
                    ghost.ExtractionPolicy.PolicyId,
                    ghost.ExtractionPolicySha256,
                    ghost.RuntimeFingerprint.OpticalTopologySha256,
                    detector,
                    ghost.RuntimeFingerprint.OrientationFingerprintSha256,
                    g3Solve?.Result.Success == true && double.IsFinite(g3Solve.Result.PositionAngle)
                        ? g3Solve.Result.PositionAngle
                        : ghost.RuntimeFingerprint.OrientationDegrees,
                    telescopeMediator.GetInfo().SideOfPier.ToString(),
                    externalIdentity.Evidence);
                var preparedObservations = preparedExtractions
                    .Where(item => item.Observation is not null)
                    .Select(item => item.Observation!)
                    .ToArray();
                return new GhostPreparedEvaluationInput(
                    preparedExtractions.AsReadOnly(),
                    runtimeBinding,
                    preparedObservations);
            },
            cancellationToken).ConfigureAwait(false);
        if (!preparation.Succeeded)
        {
            return await CompleteUnavailableGhostAttemptAsync(
                context,
                sequence,
                reference,
                offComposite,
                pairAnalysis,
                currentFocusMeasurement,
                g3Solve,
                mode,
                preparation.Failure!,
                cancellationToken,
                focus.Binding,
                externalIdentity,
                rehashReferenceSource: false).ConfigureAwait(false);
        }

        var prepared = preparation.Value!;
        var extractions = prepared.Extractions;
        var runtime = prepared.Runtime;
        var result = GhostTemplateAssistance.Evaluate(
            mode,
            ghost.Calibration,
            ghost.MatchPolicy,
            runtime,
            prepared.Observations);
        var evidencePath = await PublishGhostAssistanceEvidenceAsync(
            context,
            sequence,
            reference,
            pairAnalysis,
            focus.Binding,
            externalIdentity,
            runtime,
            extractions,
            result,
            cancellationToken).ConfigureAwait(false);
        var evidence = new GhostRunnerAssistanceEvidence(
            mode,
            result,
            evidencePath,
            externalIdentity.Evidence,
            focus.Binding,
            ghost.Calibration.CalibrationId,
            ghost.Calibration.CalibrationSha256,
            ghost.MatchPolicy.PolicyId,
            ghost.MatchPolicySha256,
            extractions,
            reference.Captured.MountBinding,
            externalIdentity.MountBinding);
        lastGhostAssistanceEvidence = evidence;

        if (result.Decision == GhostAssistanceDecision.ContinueLongExposureWcsFallback)
            return null;

        var slitDetection = new SlitLocusDetection(
            pairAnalysis.Gate,
            pairAnalysis.Geometry,
            pairAnalysis.ContrastSigma,
            pairAnalysis.PerpendicularOffsetPixels,
            pairAnalysis.AngleOffsetDegrees);
        if (result.Decision == GhostAssistanceDecision.PauseNeedsAttention ||
            result.EstimatedTargetCentroid is null ||
            result.EstimatedTargetCovariancePixelsSquared is null)
        {
            PublishG3Preview(
                reference.Image,
                $"鬼影辅助未通过：{result.Gate.Code} · {result.Gate.Message}",
                pairAnalysis.Geometry);
            return new G3FieldState(
                result.Gate,
                reference.Captured.Capture.Path,
                reference.Image,
                g3Solve,
                offComposite,
                currentFocusMeasurement.Stars,
                slitDetection,
                EmptyTargetIdentification(),
                currentFocusMeasurement,
                GhostAssistance: evidence);
        }

        var centroid = result.EstimatedTargetCentroid;
        var edgeDistance = Math.Min(
            Math.Min(centroid.X, reference.Frame.Width - 1d - centroid.X),
            Math.Min(centroid.Y, reference.Frame.Height - 1d - centroid.Y));
        // Deliberately not a guide-star candidate: the auxiliary template
        // supplies a target centroid/covariance only, never stellar morphology,
        // catalogue identity, guide-star selection, or mount authority.
        var targetCandidate = new StarCandidate(
            centroid,
            PeakAdu: 0,
            FluxAdu: 0,
            SignalToNoise: 0,
            FwhmPixels: 0,
            Ellipticity: 0,
            SaturatedFraction: 0,
            EdgeDistancePixels: edgeDistance);
        var identityGate = GateResult.Pass(
            "G3_TARGET_LOCATED_WITH_GHOST_AUXILIARY",
            $"Fresh external {externalIdentity.Evidence.Authority} evidence retains catalogue identity; calibrated ghost assistance supplied only target centroid ({centroid.X:F2}, {centroid.Y:F2}) px and covariance. Fresh slit/PHD2 residual authority is still mandatory.",
            new Dictionary<string, double>
            {
                ["targetX"] = centroid.X,
                ["targetY"] = centroid.Y,
                ["targetUncertaintyPixels"] = result.TargetUncertaintyPixels,
                ["ghostCanEstablishIdentity"] = result.CanEstablishTargetIdentity ? 1 : 0,
                ["ghostCanAuthorizeMotion"] = 0,
            });
        var identification = new TargetIdentification(
            identityGate,
            targetCandidate,
            centroid,
            PredictionResidualPixels: 0,
            result.UniquenessLikelihoodRatio);
        PublishG3Preview(
            reference.Image,
            $"鬼影辅助质心 ({centroid.X:F1},{centroid.Y:F1})，1σ={result.TargetUncertaintyPixels:F2}px；身份来自新鲜 {externalIdentity.Evidence.Authority}，仍需狭缝/PHD2新鲜残差。",
            pairAnalysis.Geometry,
            centroid);
        return new G3FieldState(
            GateResult.Pass(
                "G3_GHOST_AUXILIARY_FIELD_LOCATED",
                "A hash-bound ghost template supplied an auxiliary target centroid after fresh external identity, independent C11 focus and paired-slit gates passed; no ghost-derived motion authority was granted."),
            reference.Captured.Capture.Path,
            reference.Image,
            g3Solve,
            offComposite,
            currentFocusMeasurement.Stars,
            slitDetection,
            identification,
            currentFocusMeasurement,
            GhostAssistance: evidence,
            MountBinding: reference.Captured.MountBinding);
    }

    private async Task<G3FieldState?> CompleteUnavailableGhostAttemptAsync(
        ObservationContext context,
        G3SlitIlluminationSequence sequence,
        G3LoadedIlluminationFrame reference,
        MonochromeFrame offComposite,
        SlitIlluminationPairAnalysis pairAnalysis,
        G3StellarFocusMeasurement currentFocusMeasurement,
        PlateSolveEvidence? g3Solve,
        GhostAssistanceMode mode,
        GateResult unavailableGate,
        CancellationToken cancellationToken,
        FocusDomainBinding? focus = null,
        GhostExternalIdentityResolution? externalIdentity = null,
        bool rehashReferenceSource = true)
    {
        var result = GhostUnavailableResult(mode, unavailableGate);
        var path = await PublishGhostAssistanceEvidenceAsync(
            context,
            sequence,
            reference,
            pairAnalysis,
            focus,
            externalIdentity,
            runtime: null,
            extractions: Array.Empty<GhostSourceExtractionResult>(),
            result,
            cancellationToken,
            rehashReferenceSource).ConfigureAwait(false);
        var ghost = commissioning?.Value.GhostAssistance;
        var evidence = new GhostRunnerAssistanceEvidence(
            mode,
            result,
            path,
            externalIdentity?.Evidence,
            focus,
            ghost?.Calibration.CalibrationId,
            ghost?.Calibration.CalibrationSha256,
            ghost?.MatchPolicy.PolicyId,
            ghost?.MatchPolicySha256,
            Array.Empty<GhostSourceExtractionResult>(),
            reference.Captured.MountBinding,
            externalIdentity?.MountBinding);
        lastGhostAssistanceEvidence = evidence;
        if (result.Decision != GhostAssistanceDecision.PauseNeedsAttention) return null;
        return new G3FieldState(
            result.Gate,
            reference.Captured.Capture.Path,
            reference.Image,
            g3Solve,
            offComposite,
            currentFocusMeasurement.Stars,
            new SlitLocusDetection(
                pairAnalysis.Gate,
                pairAnalysis.Geometry,
                pairAnalysis.ContrastSigma,
                pairAnalysis.PerpendicularOffsetPixels,
                pairAnalysis.AngleOffsetDegrees),
            EmptyTargetIdentification(),
            currentFocusMeasurement,
            GhostAssistance: evidence);
    }

    internal static GhostAssistanceResult GhostUnavailableResult(
        GhostAssistanceMode mode,
        GateResult templateGate)
    {
        if (mode == GhostAssistanceMode.RequireValid)
        {
            return new GhostAssistanceResult(
                GhostAssistanceDecision.PauseNeedsAttention,
                GateResult.Unknown(
                    "GHOST_REQUIRED_ASSISTANCE_UNAVAILABLE",
                    $"Ghost assistance was explicitly required but could not be used. {templateGate.Message}"),
                templateGate,
                GhostLocatorAuthority.None,
                null,
                null,
                double.PositiveInfinity,
                0,
                Array.Empty<GhostFrameMatch>());
        }
        return new GhostAssistanceResult(
            GhostAssistanceDecision.ContinueLongExposureWcsFallback,
            GateResult.Pass(
                mode == GhostAssistanceMode.Skip
                    ? "GHOST_ASSISTANCE_SKIPPED_FALLBACK"
                    : "GHOST_ASSISTANCE_INVALID_FALLBACK",
                $"{templateGate.Message} Continue the existing fresh long-exposure WCS/N.I.N.A. centering/bounded-search route; no ghost-derived motion is authorized."),
            templateGate,
            GhostLocatorAuthority.None,
            null,
            null,
            double.PositiveInfinity,
            0,
            Array.Empty<GhostFrameMatch>());
    }

    private GateResult ValidateGhostSlitAuthority(
        SlitIlluminationPairAnalysis pairAnalysis,
        SlitGeometry slitSeed)
    {
        if (pairAnalysis.Gate.Disposition != GateDisposition.Passed)
        {
            return GateResult.Unknown(
                "GHOST_SLIT_AUTHORITY_UNAVAILABLE",
                $"Ghost assistance cannot bypass the paired OFF/ON/OFF slit gate: {pairAnalysis.Gate.Code}: {pairAnalysis.Gate.Message}");
        }
        var maximumCommissionedSlitOffset = Math.Max(
            slitSeed.UncertaintyPixels * 3,
            configuration.Slit.PlacementTolerancePixels * 2);
        if (Math.Abs(pairAnalysis.PerpendicularOffsetPixels) > maximumCommissionedSlitOffset ||
            Math.Abs(pairAnalysis.AngleOffsetDegrees) > 3)
        {
            return GateResult.Unknown(
                "GHOST_SLIT_RESIDUAL_OUTSIDE_COMMISSIONING",
                $"Fresh paired-slit residual {pairAnalysis.PerpendicularOffsetPixels:F1}px/{pairAnalysis.AngleOffsetDegrees:F1}° is outside the locked {maximumCommissionedSlitOffset:F1}px/3.0° envelope; a ghost match cannot replace slit authority.");
        }
        return GateResult.Pass(
            "GHOST_FRESH_SLIT_AUTHORITY_VALID",
            "Fresh detector-fixed paired OFF/ON/OFF evidence independently retained the slit locus.");
    }

    private GhostFocusResolution ValidateGhostIndependentFocus(
        GhostAssistanceCommissioningPreset ghost,
        C11MainFocusOwnerSnapshot ownerBefore,
        C11MainFocusOwnerSnapshot ownerAfter,
        DateTimeOffset evaluatedUtc)
    {
        if (nightSetup is null)
            return GhostFocusResolution.Failed("The locked Night Setup is unavailable.");
        var bindings = nightSetup.Value.FocusDomains?
            .Where(binding => binding.Role == FocusDomainRole.C11Main)
            .ToArray() ?? [];
        if (bindings.Length != 1)
            return GhostFocusResolution.Failed($"The locked Night Setup contains {bindings.Length} independent C11 focus bindings; exactly one is required.");
        var focus = bindings[0];
        var ownerBeforeGate = C11MainFocusPolicy.ValidateLockedPosition(ownerBefore, nightSetup.Value);
        var ownerAfterGate = C11MainFocusPolicy.ValidateLockedPosition(ownerAfter, nightSetup.Value);
        var failures = new List<string>();
        if (ownerBeforeGate.Disposition != GateDisposition.Passed) failures.Add(ownerBeforeGate.Message);
        if (ownerAfterGate.Disposition != GateDisposition.Passed) failures.Add(ownerAfterGate.Message);
        if (ownerBefore.PositionSteps != ownerAfter.PositionSteps) failures.Add("Star Focuser Pro moved during the paired slit sequence");
        if (focus.ValidUntilUtc <= evaluatedUtc || focus.VerifiedUtc > evaluatedUtc.AddMinutes(5)) failures.Add("independent C11 focus evidence is stale or future-dated");
        if (focus.Confidence < ghost.MinimumC11FocusConfidence) failures.Add($"independent C11 focus confidence {focus.Confidence:F3} is below {ghost.MinimumC11FocusConfidence:F3}");
        if (focus.Metric.Kind != FocusMetricKind.G3StellarShape) failures.Add("independent focus evidence belongs to a different metric domain");
        if (!string.Equals(focus.Metric.SourceCameraStableDeviceId, configuration.Phd2.CameraStableId, StringComparison.OrdinalIgnoreCase))
            failures.Add("independent C11 focus evidence is not bound to the current G3 stable identity");
        if (!IsSha256Value(focus.Metric.EvidenceSha256)) failures.Add("independent C11 focus evidence SHA-256 is invalid");
        return failures.Count == 0
            ? new GhostFocusResolution(
                GateResult.Pass(
                    "GHOST_INDEPENDENT_C11_FOCUS_VALID",
                    $"Independent C11/G3 focus evidence remains valid at locked Star Focuser Pro position {ownerAfter.PositionSteps}, confidence {focus.Confidence:F3}."),
                focus)
            : new GhostFocusResolution(
                GateResult.Unknown(
                    "GHOST_INDEPENDENT_C11_FOCUS_UNAVAILABLE",
                    $"Ghost assistance cannot bypass independent C11 focus authority: {string.Join("; ", failures)}."),
                focus);
    }

    private async Task<GhostExternalIdentityResolution> ResolveGhostExternalIdentityAsync(
        ObservationContext context,
        G3LoadedIlluminationFrame reference,
        PlateSolveEvidence? g3Solve,
        GhostAssistanceCommissioningPreset ghost,
        DateTimeOffset evaluatedUtc,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(context.Plan.Target.CatalogId))
            failures.Add("the observation plan has no catalogue ID");
        var target = TargetCoordinates(context.Plan);

        if (g3Solve?.Result.Success == true && g3Solve.Result.Coordinates is not null)
        {
            var g3Failure = await ValidateGhostG3IdentitySourceAsync(
                context,
                target,
                reference,
                g3Solve,
                ghost,
                evaluatedUtc,
                cancellationToken).ConfigureAwait(false);
            if (g3Failure is null && !string.IsNullOrWhiteSpace(context.Plan.Target.CatalogId))
            {
                var evidenceSha256 = ComputeGhostBindingSha256(new
                {
                    authority = GhostExternalIdentityAuthority.CatalogBoundG3Wcs.ToString(),
                    observationRunId = context.Plan.ObservationRunId,
                    context.Plan.Target.CatalogId,
                    reference.Captured.Sha256,
                    g3Solve.EvidenceSha256,
                    g3Solve.ResidualArcseconds,
                    reference.Captured.Capture.CompletedUtc,
                    mountBinding = reference.Captured.MountBinding,
                });
                var evidence = new GhostExternalIdentityEvidence(
                    context.Plan.ObservationRunId,
                    context.Plan.Target.CatalogId,
                    GhostExternalIdentityAuthority.CatalogBoundG3Wcs,
                    GateResult.Pass(
                        "GHOST_EXTERNAL_G3_WCS_IDENTITY_VALID",
                        "The current OFF frame, its current-run catalogue request and its WCS evidence were re-hashed and passed the explicit residual/age gates."),
                    evidenceSha256,
                    reference.Captured.Capture.CompletedUtc,
                    reference.Captured.Capture.CompletedUtc + ghost.MaximumExternalIdentityAge);
                return new GhostExternalIdentityResolution(
                    evidence,
                    reference.Captured.Capture.Path,
                    reference.Captured.Sha256,
                    g3Solve.EvidencePath,
                    g3Solve.EvidenceSha256,
                    MountBinding: null);
            }
            if (g3Failure is not null) failures.Add($"G3 WCS: {g3Failure}");
        }
        else
        {
            failures.Add("G3 WCS: no successful current OFF-frame solution");
        }

        if (lastQhyAcquisition?.AcceptedFrameId is { } acceptedFrameId && lastQhySolve is not null)
        {
            var accepted = lastQhyAcquisition.Frames.SingleOrDefault(frame => frame.FrameId == acceptedFrameId);
            var qhyFailure = await ValidateGhostQhyIdentitySourceAsync(
                context,
                target,
                accepted,
                lastQhySolve,
                ghost,
                evaluatedUtc,
                cancellationToken).ConfigureAwait(false);
            if (qhyFailure is null && accepted is not null && !string.IsNullOrWhiteSpace(context.Plan.Target.CatalogId))
            {
                var evidenceSha256 = ComputeGhostBindingSha256(new
                {
                    authority = GhostExternalIdentityAuthority.CatalogBoundQhyWcs.ToString(),
                    observationRunId = context.Plan.ObservationRunId,
                    context.Plan.Target.CatalogId,
                    accepted.FrameId,
                    accepted.Sha256,
                    lastQhySolve.EvidenceSha256,
                    lastQhySolve.ResidualArcseconds,
                    accepted.ExposureEndedUtc,
                    mountBinding = lastQhySolveMountBinding,
                });
                var evidence = new GhostExternalIdentityEvidence(
                    context.Plan.ObservationRunId,
                    context.Plan.Target.CatalogId,
                    GhostExternalIdentityAuthority.CatalogBoundQhyWcs,
                    GateResult.Pass(
                        "GHOST_EXTERNAL_QHY_WCS_IDENTITY_VALID",
                        "The current-run accepted QHY frame, catalogue coordinates and WCS evidence were re-hashed and passed the explicit residual/age gates."),
                    evidenceSha256,
                    accepted.ExposureEndedUtc,
                    accepted.ExposureEndedUtc + ghost.MaximumExternalIdentityAge);
                return new GhostExternalIdentityResolution(
                    evidence,
                    accepted.FitsPath,
                    accepted.Sha256,
                    lastQhySolve.EvidencePath,
                    lastQhySolve.EvidenceSha256,
                    lastQhySolveMountBinding);
            }
            if (qhyFailure is not null) failures.Add($"QHY WCS: {qhyFailure}");
        }
        else
        {
            failures.Add("QHY WCS: no accepted current-run frame/solve pair");
        }

        var failureMessage = string.Join("; ", failures);
        return new GhostExternalIdentityResolution(
            new GhostExternalIdentityEvidence(
                context.Plan.ObservationRunId,
                context.Plan.Target.CatalogId,
                GhostExternalIdentityAuthority.CatalogBoundQhyWcs,
                GateResult.Unknown(
                    "GHOST_EXTERNAL_IDENTITY_UNAVAILABLE",
                    $"No fresh run-bound external catalogue/WCS identity passed: {failureMessage}."),
                ComputeGhostBindingSha256(new { context.Plan.ObservationRunId, context.Plan.Target.CatalogId, failureMessage }),
                evaluatedUtc,
                evaluatedUtc + ghost.MaximumExternalIdentityAge),
            null,
            null,
            null,
            null,
            null);
    }

    private async Task<string?> ValidateGhostG3IdentitySourceAsync(
        ObservationContext context,
        Coordinates target,
        G3LoadedIlluminationFrame reference,
        PlateSolveEvidence solve,
        GhostAssistanceCommissioningPreset ghost,
        DateTimeOffset evaluatedUtc,
        CancellationToken cancellationToken)
    {
        if (commissioning is null) return "commissioning is unavailable for the G3 capture mount binding";
        var mountBinding = reference.Captured.MountBinding;
        if (mountBinding is null)
            return "the selected OFF frame has no capture-time mount binding";
        var bindingIntegrity = G3FieldMountBindingPolicy.ValidateForMotion(
            mountBinding,
            context.Plan.ObservationRunId,
            configuration.ActionConfigurationSha256,
            commissioning.Sha256,
            reference.Captured.Capture.Path,
            reference.Captured.Sha256,
            mountBinding.RightAscensionDegrees,
            mountBinding.DeclinationDegrees,
            mountBinding.CoordinateEpoch,
            mountBinding.PierSide,
            MountCommandArrivalToleranceArcseconds);
        if (bindingIntegrity.Disposition != GateDisposition.Passed)
            return $"the OFF-frame mount binding failed integrity/context validation: {bindingIntegrity.Code}: {bindingIntegrity.Message}";
        if (!string.Equals(Path.GetFullPath(reference.Captured.Capture.Path), Path.GetFullPath(solve.SourcePath), StringComparison.OrdinalIgnoreCase))
            return "the WCS source is not the selected immutable OFF frame";
        if (solve.Result.Flipped != configuration.G3.ExpectedWcsFlipped)
            return "WCS parity does not match commissioning";
        if (!double.IsFinite(solve.ResidualArcseconds) || solve.ResidualArcseconds > ghost.MaximumCatalogCoordinateMismatchArcseconds)
            return $"target residual {solve.ResidualArcseconds:F1} arcsec exceeds {ghost.MaximumCatalogCoordinateMismatchArcseconds:F1} arcsec";
        if (AngularSeparationArcseconds(target, solve.Requested) > ghost.MaximumCatalogCoordinateMismatchArcseconds)
            return "the solver request is not bound to the planned catalogue coordinates";
        if (reference.Captured.Capture.CompletedUtc == default ||
            reference.Captured.Capture.CompletedUtc > evaluatedUtc.AddSeconds(5) ||
            evaluatedUtc - reference.Captured.Capture.CompletedUtc > ghost.MaximumExternalIdentityAge)
            return "the current OFF-frame WCS identity is stale or future-dated";
        try
        {
            var frameHash = await ComputeFileSha256Async(reference.Captured.Capture.Path, cancellationToken).ConfigureAwait(false);
            var solveHash = await ComputeFileSha256Async(solve.EvidencePath, cancellationToken).ConfigureAwait(false);
            if (!SameHash(frameHash, reference.Captured.Sha256) || !SameHash(solveHash, solve.EvidenceSha256))
                return "the OFF FITS or WCS evidence changed after hashing";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"the OFF FITS or WCS evidence could not be re-hashed: {ex.Message}";
        }
        return null;
    }

    private async Task<string?> ValidateGhostQhyIdentitySourceAsync(
        ObservationContext context,
        Coordinates target,
        QhyFrameRecord? accepted,
        PlateSolveEvidence solve,
        GhostAssistanceCommissioningPreset ghost,
        DateTimeOffset evaluatedUtc,
        CancellationToken cancellationToken)
    {
        var job = lastQhyAcquisition!;
        if (commissioning is null) return "commissioning is unavailable for the QHY capture mount binding";
        if (accepted is null) return "AcceptedFrameId is absent from the immutable job manifest";
        if (!string.Equals(job.ObservationRunId, context.Plan.ObservationRunId, StringComparison.Ordinal))
            return "the accepted QHY frame belongs to another observation run";
        if (!string.Equals(job.RequestedTarget, context.Plan.Target.Name, StringComparison.Ordinal))
            return "the QHY request target name differs from the current plan";
        if (!string.Equals(job.ExpectedCameraStableId, configuration.NightSetup.QhyStableId, StringComparison.Ordinal))
            return "the QHY job is bound to another stable camera identity";
        if (job.TargetRightAscensionDegrees is not { } requestedRa || job.TargetDeclinationDegrees is not { } requestedDec)
            return "the QHY request lacks run-bound catalogue coordinates";
        var qhyRequested = new Coordinates(requestedRa, requestedDec, Epoch.J2000, Coordinates.RAType.Degrees);
        if (AngularSeparationArcseconds(target, qhyRequested) > ghost.MaximumCatalogCoordinateMismatchArcseconds ||
            AngularSeparationArcseconds(target, solve.Requested) > ghost.MaximumCatalogCoordinateMismatchArcseconds)
            return "the QHY job/solver coordinates do not match the planned catalogue target";
        if (!solve.Result.Success || solve.Result.Coordinates is null)
            return "the accepted QHY FITS has no successful WCS";
        if (!double.IsFinite(solve.ResidualArcseconds) || solve.ResidualArcseconds > ghost.MaximumQhyTargetResidualArcseconds)
            return $"target residual {solve.ResidualArcseconds:F1} arcsec exceeds {ghost.MaximumQhyTargetResidualArcseconds:F1} arcsec";
        if (!string.Equals(Path.GetFullPath(accepted.FitsPath), Path.GetFullPath(solve.SourcePath), StringComparison.OrdinalIgnoreCase))
            return "the QHY WCS source is not the accepted immutable FITS";
        var acceptedFrameMountBinding = lastQhyAcceptedFrameMountBinding;
        if (acceptedFrameMountBinding is null)
            return "the accepted QHY frame has no dual-ended capture mount binding";
        var acceptedFrameMountGate = acceptedFrameMountBinding.Validate(
            context.Plan.ObservationRunId,
            configuration.ActionConfigurationSha256,
            commissioning.Sha256,
            job.Id,
            accepted.FrameId,
            accepted.Sha256,
            MountCommandArrivalToleranceArcseconds);
        if (acceptedFrameMountGate.Disposition != GateDisposition.Passed)
            return $"the accepted QHY frame mount binding failed current run/action/preset/job/frame/hash validation: {acceptedFrameMountGate.Code}: {acceptedFrameMountGate.Message}";

        var mountBinding = lastQhySolveMountBinding;
        if (mountBinding is null ||
            !string.Equals(mountBinding.ObservationRunId, context.Plan.ObservationRunId, StringComparison.Ordinal) ||
            !SameHash(mountBinding.SolveEvidenceSha256, solve.EvidenceSha256) ||
            !string.Equals(Path.GetFullPath(mountBinding.SolveEvidencePath), Path.GetFullPath(solve.EvidencePath), StringComparison.OrdinalIgnoreCase) ||
            mountBinding.FrameId != accepted.FrameId ||
            !SameHash(mountBinding.FrameSha256, accepted.Sha256) ||
            mountBinding.ExposureEndedUtc != accepted.ExposureEndedUtc ||
            !SameHash(mountBinding.CaptureBindingSha256, acceptedFrameMountBinding.BindingSha256))
            return "the QHY WCS is not bound to the current-run accepted frame, dual-ended capture attestation and solve evidence";
        var currentReported = telescopeMediator.GetCurrentPosition();
        try { EnsureFiniteReportedCoordinates(currentReported); }
        catch (InvalidOperationException ex) { return $"fresh mount position is invalid: {ex.Message}"; }
        var currentPierSide = telescopeMediator.GetInfo().SideOfPier.ToString();
        if (!IsKnownPierSide(mountBinding.PierSide) ||
            !IsKnownPierSide(currentPierSide) ||
            !string.Equals(mountBinding.PierSide, currentPierSide, StringComparison.OrdinalIgnoreCase))
            return "mount pier side changed or is unknown after the QHY solve";
        if (!string.Equals(mountBinding.CoordinateEpoch, currentReported.Epoch.ToString(), StringComparison.Ordinal))
            return "mount coordinate epoch changed after the QHY solve";
        var solvedMountPosition = new Coordinates(
            mountBinding.RightAscensionDegrees,
            mountBinding.DeclinationDegrees,
            currentReported.Epoch,
            Coordinates.RAType.Degrees);
        var mountDriftArcseconds = AngularSeparationArcseconds(solvedMountPosition, currentReported);
        if (!double.IsFinite(mountDriftArcseconds) || mountDriftArcseconds > MountCommandArrivalToleranceArcseconds)
            return $"mount moved {mountDriftArcseconds:F2} arcsec after the accepted QHY solve (limit {MountCommandArrivalToleranceArcseconds:F2} arcsec)";
        if (accepted.ExposureEndedUtc == default || accepted.ExposureEndedUtc > evaluatedUtc.AddSeconds(5) ||
            evaluatedUtc - accepted.ExposureEndedUtc > ghost.MaximumExternalIdentityAge)
            return "the accepted QHY identity is stale or future-dated";
        try
        {
            var frameHash = await ComputeFileSha256Async(accepted.FitsPath, cancellationToken).ConfigureAwait(false);
            var solveHash = await ComputeFileSha256Async(solve.EvidencePath, cancellationToken).ConfigureAwait(false);
            if (!SameHash(frameHash, accepted.Sha256) || !SameHash(solveHash, solve.EvidenceSha256))
                return "the accepted QHY FITS or WCS evidence changed after hashing";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"the accepted QHY FITS or WCS evidence could not be re-hashed: {ex.Message}";
        }
        return null;
    }

    private static IReadOnlyList<G3LoadedIlluminationFrame> SelectFreshSameExposureGhostFrames(
        IReadOnlyList<G3LoadedIlluminationFrame> loadedFrames,
        int requiredFrameCount,
        TimeSpan maximumFrameAge,
        DateTimeOffset evaluatedUtc)
    {
        var off = loadedFrames
            .Where(frame => frame.Captured.Phase is G3SlitIlluminationPhase.OffBefore or G3SlitIlluminationPhase.OffAfter)
            .Where(frame => frame.Captured.Capture.CompletedUtc != default &&
                            frame.Captured.Capture.CompletedUtc <= evaluatedUtc.AddSeconds(5) &&
                            evaluatedUtc - frame.Captured.Capture.CompletedUtc <= maximumFrameAge)
            .GroupBy(frame => (ExposureMilliseconds: GhostFrameExposureMilliseconds(frame), Gain: GhostFrameGain(frame)))
            .Where(group => group.Key.ExposureMilliseconds > 0 && group.Key.Gain >= 0)
            .OrderByDescending(group => group.Max(frame => frame.Captured.Capture.CompletedUtc))
            .FirstOrDefault(group => group.Count() >= requiredFrameCount);
        if (off is null) return Array.Empty<G3LoadedIlluminationFrame>();
        return off
            .OrderByDescending(frame => frame.Captured.Capture.CompletedUtc)
            .Take(requiredFrameCount)
            .OrderBy(frame => frame.Captured.Capture.CompletedUtc)
            .ToArray();
    }

    private static int GhostFrameExposureMilliseconds(G3LoadedIlluminationFrame frame) =>
        frame.Captured.Capture.VerifiedExposureMilliseconds is { } verified && verified > 0
            ? verified
            : (int)Math.Round(frame.Image.MetaData.Image.ExposureTime * 1000);

    private static int GhostFrameGain(G3LoadedIlluminationFrame frame) =>
        ConvertGhostFrameGain(frame.Image.MetaData.Camera.Gain);

    internal static int ConvertGhostFrameGain(object? gain) =>
        Convert.ToInt32(gain, CultureInfo.InvariantCulture);

    internal static async Task<GhostFramePreparationIsolation<T>> IsolateGhostFramePreparationAsync<T>(
        Func<CancellationToken, Task<T>> prepare,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(prepare);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = await prepare(cancellationToken).ConfigureAwait(false);
            if (value is null)
                throw new InvalidOperationException("Ghost frame preparation returned no evaluation input.");
            return GhostFramePreparationIsolation<T>.Completed(value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverableGhostFramePreparationException(ex))
        {
            return GhostFramePreparationIsolation<T>.Unavailable(
                GateResult.Unknown(
                    "GHOST_FRAME_PREPARATION_FAILED",
                    $"Ghost frame selection, FITS metadata conversion, immutable re-hash or deterministic source extraction failed before template evaluation ({ex.GetType().Name}): {ex.Message}"));
        }
    }

    private static bool IsRecoverableGhostFramePreparationException(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;

    private async Task<string> PublishGhostAssistanceEvidenceAsync(
        ObservationContext context,
        G3SlitIlluminationSequence sequence,
        G3LoadedIlluminationFrame reference,
        SlitIlluminationPairAnalysis slit,
        FocusDomainBinding? focus,
        GhostExternalIdentityResolution? externalIdentity,
        GhostRuntimeBinding? runtime,
        IReadOnlyList<GhostSourceExtractionResult> extractions,
        GhostAssistanceResult result,
        CancellationToken cancellationToken,
        bool rehashReferenceSource = true)
    {
        var ghost = commissioning?.Value.GhostAssistance;
        var applicability = result.TemplateGate.Code;
        var metadata = new Dictionary<string, string>
        {
            ["mode"] = configuration.G3.GhostAssistanceMode.ToString(),
            ["calibrationId"] = ghost?.Calibration.CalibrationId ?? "none",
            ["calibrationSha256"] = ghost?.Calibration.CalibrationSha256 ?? string.Empty,
            ["matchPolicyId"] = ghost?.MatchPolicy.PolicyId ?? "none",
            ["matchPolicySha256"] = ghost?.MatchPolicySha256 ?? string.Empty,
            ["templateApplicability"] = applicability,
            ["decision"] = result.Decision.ToString(),
            ["decisionGate"] = result.Gate.Code,
            ["sourceFrameCount"] = extractions.Count.ToString(CultureInfo.InvariantCulture),
            ["externalIdentityAuthority"] = externalIdentity?.Evidence.Authority.ToString() ?? "none",
            ["targetIdentityAuthority"] = "external-only",
            ["ghostAuthority"] = result.Authority.ToString(),
            ["ghostCanAuthorizeMotion"] = bool.FalseString,
            ["g3MountBindingSha256"] = reference.Captured.MountBinding?.BindingSha256 ?? string.Empty,
            ["qhyMountBindingPresent"] = (externalIdentity?.MountBinding is not null).ToString(),
        };
        if (double.IsFinite(result.TargetUncertaintyPixels))
            metadata["targetUncertaintyPixels"] = result.TargetUncertaintyPixels.ToString("R", CultureInfo.InvariantCulture);
        var path = await PublishRunJsonEvidenceAsync(
            "g3-ghost-assistance",
            "Deterministic calibrated ghost-assistance decision",
            new
            {
                mode = configuration.G3.GhostAssistanceMode.ToString(),
                requestedTarget = context.Plan.Target,
                sequence.SequenceId,
                policy = new
                {
                    defaultMode = GhostAssistanceMode.Skip.ToString(),
                    calibrationId = ghost?.Calibration.CalibrationId,
                    calibrationSha256 = ghost?.Calibration.CalibrationSha256,
                    matchPolicyId = ghost?.MatchPolicy.PolicyId,
                    matchPolicySha256 = ghost?.MatchPolicySha256,
                    extractionPolicyId = ghost?.ExtractionPolicy.PolicyId,
                    extractionPolicySha256 = ghost?.ExtractionPolicySha256,
                    commissioningPresetSha256 = commissioning?.Sha256,
                    targetSpecificConstant = (string?)null,
                    opticalAxisOffset = (string?)null,
                    canEstablishTargetIdentity = result.CanEstablishTargetIdentity,
                    canAuthorizeMotion = false,
                },
                slit = new
                {
                    disposition = slit.Gate.Disposition.ToString(),
                    slit.Gate.Code,
                    slit.Gate.Message,
                    slit.Geometry.CalibrationId,
                    slit.Geometry.AcquisitionPoint,
                    slit.Confidence,
                    slit.ContrastSigma,
                    slit.PerpendicularOffsetPixels,
                    slit.AngleOffsetDegrees,
                },
                independentC11Focus = focus is null ? null : new
                {
                    focus.Role,
                    focus.Owner,
                    focus.LogicalDeviceId,
                    focus.StartPositionSteps,
                    focus.Metric,
                    focus.VerifiedUtc,
                    focus.ValidUntilUtc,
                    focus.Confidence,
                },
                externalIdentity,
                g3FieldMountBinding = reference.Captured.MountBinding,
                qhySolveMountBinding = externalIdentity?.MountBinding,
                runtime,
                sourceFrames = extractions.Select((extraction, index) => new
                {
                    index = index + 1,
                    gate = new
                    {
                        disposition = extraction.Gate.Disposition.ToString(),
                        extraction.Gate.Code,
                        extraction.Gate.Message,
                    },
                    frame = extraction.Observation is null ? null : new
                    {
                        extraction.Observation.FrameId,
                        extraction.Observation.FrameSha256,
                        extraction.Observation.CompletedUtc,
                        extraction.Observation.ExposureMilliseconds,
                        extraction.Observation.Gain,
                        extraction.Observation.SourceExtractionEvidenceSha256,
                    },
                    extraction.EvidenceSha256,
                    overlays = extraction.OverlaySources,
                }).ToArray(),
                referenceFrame = new
                {
                    reference.Captured.Role,
                    reference.Captured.Capture.Path,
                    reference.Captured.Sha256,
                    reference.Captured.Capture.CompletedUtc,
                },
                result = new
                {
                    decision = result.Decision.ToString(),
                    authority = result.Authority.ToString(),
                    gate = new
                    {
                        disposition = result.Gate.Disposition.ToString(),
                        result.Gate.Code,
                        result.Gate.Message,
                        result.Gate.Metrics,
                    },
                    applicability = new
                    {
                        disposition = result.TemplateGate.Disposition.ToString(),
                        result.TemplateGate.Code,
                        result.TemplateGate.Message,
                        result.TemplateGate.Metrics,
                    },
                    result.EstimatedTargetCentroid,
                    result.EstimatedTargetCovariancePixelsSquared,
                    targetUncertaintyPixels = double.IsFinite(result.TargetUncertaintyPixels)
                        ? result.TargetUncertaintyPixels
                        : (double?)null,
                    uniquenessLikelihoodRatio = double.IsFinite(result.UniquenessLikelihoodRatio)
                        ? result.UniquenessLikelihoodRatio
                        : (double?)null,
                    result.FrameMatches,
                    result.CanEstablishTargetIdentity,
                    canAuthorizeMotion = false,
                },
            },
            rehashReferenceSource ? reference.Captured.Capture.Path : null,
            cancellationToken,
            metadata).ConfigureAwait(false);
        return path;
    }

    private static string ComputeGhostBindingSha256(object value)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static bool IsSha256Value(string? value)
    {
        var normalized = (value ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).Trim();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit);
    }
}

internal sealed record GhostFocusResolution(GateResult Gate, FocusDomainBinding? Binding)
{
    public static GhostFocusResolution Failed(string message) => new(
        GateResult.Unknown("GHOST_INDEPENDENT_C11_FOCUS_UNAVAILABLE", message),
        null);
}

internal sealed record GhostExternalIdentityResolution(
    GhostExternalIdentityEvidence Evidence,
    string? SourceFramePath,
    string? SourceFrameSha256,
    string? WcsEvidencePath,
    string? WcsEvidenceSha256,
    GhostQhySolveMountBinding? MountBinding);

internal sealed record GhostQhySolveMountBinding(
    string ObservationRunId,
    string SolveEvidenceSha256,
    string SolveEvidencePath,
    Guid FrameId,
    string FrameSha256,
    DateTimeOffset ExposureEndedUtc,
    string CaptureBindingSha256,
    double RightAscensionDegrees,
    double DeclinationDegrees,
    string CoordinateEpoch,
    string PierSide,
    DateTimeOffset CapturedUtc);

internal sealed record GhostPreparedEvaluationInput(
    IReadOnlyList<GhostSourceExtractionResult> Extractions,
    GhostRuntimeBinding Runtime,
    IReadOnlyList<GhostFrameObservation> Observations);

internal sealed record GhostFramePreparationIsolation<T>(
    T? Value,
    GateResult? Failure)
    where T : class
{
    public bool Succeeded => Value is not null && Failure is null;

    public static GhostFramePreparationIsolation<T> Completed(T value) => new(value, null);

    public static GhostFramePreparationIsolation<T> Unavailable(GateResult failure) => new(null, failure);
}

internal sealed record GhostRunnerAssistanceEvidence(
    GhostAssistanceMode Mode,
    GhostAssistanceResult Result,
    string EvidencePath,
    GhostExternalIdentityEvidence? ExternalIdentity,
    FocusDomainBinding? C11Focus,
    string? CalibrationId,
    string? CalibrationSha256,
    string? MatchPolicyId,
    string? MatchPolicySha256,
    IReadOnlyList<GhostSourceExtractionResult> Extractions,
    G3FieldMountBinding? G3MountBinding,
    GhostQhySolveMountBinding? QhyMountBinding);
