using NINA.Core.Enum;
using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private async Task<G3PlateSolveProbeState?> ConfirmSaturatedG3HandoffWithShortExposureAsync(
        ObservationContext context, G3WcsMotionPrediction prediction, TargetIdentification longTarget,
        string longFramePath, G3FieldMountBinding longMountBinding, int exposureMilliseconds, Phd2SlitPlacementCommissioning preset,
        SlitGeometry slit, IReadOnlyList<G3PlateSolveAttemptEvidence> attempts, CancellationToken cancellationToken,
        IReadOnlyList<StarCandidate> longFieldStars, Func<CancellationToken, Task<IReadOnlyList<StarCandidate>>> extractLongStars,
        G3SparseExposureTiming longTiming,
        bool allowMeasuredCoarse = false)
    {
        var longProbe = new G3PlateSolveProbeState(GateResult.Unknown("G3_SHORT_CHECK_PENDING", "Not a motion authority"),
            longFramePath, null, null, null, attempts, MountBinding: longMountBinding);
        var runtime = SepStarDetectionClient.ReadRuntime(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UVEX-ADV", "star-detection", "runtime.json"));
        var modulePath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(runtime.WorkerPath)!, "sep_detector.py");
        var workerHash = await ComputeFileSha256Async(runtime.WorkerPath, cancellationToken).ConfigureAwait(false);
        var moduleHash = await ComputeFileSha256Async(modulePath, cancellationToken).ConfigureAwait(false);
        var client = new SepStarDetectionClient();
        var commissionedShortExposureMilliseconds = exposureMilliseconds;
        var commissionedTargetGuidingCeilingMilliseconds =
            Math.Max(commissionedShortExposureMilliseconds,
                preset.OffSlitGuidingExposureMilliseconds ?? commissionedShortExposureMilliseconds);
        var policy = G3PostWcsShortExposurePolicy.Start(commissionedShortExposureMilliseconds,
            commissionedTargetGuidingCeilingMilliseconds);
        G3ShortPositionMeasurement? previous = null;
        IReadOnlyList<StarCandidate>? independentLongStars = null;
        string? previousHash = null, previousPath = null;
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lastCompleted = DateTimeOffset.MinValue;
        G3PlateSolveProbeState? latest = null;
        const int shortConfirmationGainPercent = 0;
        var boundaryRecheckUsed = false;
        var maximumFrames = G3PostWcsShortExposurePolicy.MaximumFrames;
        var longTargetAnchor = longTarget.Target?.Centroid
            ?? throw new InvalidOperationException("G3_SHORT_LONG_ANCHOR_MISSING: The saturated handoff candidate has no measured long-frame centroid.");
        var shortAssociationRadius = Math.Max(6,
            prediction.MaximumUncertaintyPixels + G3ShortPositionMeasurementPolicy.MaximumTrackedRepeatSeparationPixels);

        for (var attempt = 1; attempt <= maximumFrames; attempt++)
        {
            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            var longGate = await ValidateG3ProbeMountBindingForMotionAsync(context, longProbe, cancellationToken).ConfigureAwait(false);
            if (longGate.Disposition != GateDisposition.Passed)
                throw new PhysicalActionGateException(longGate);
            var identity = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), cancellationToken).ConfigureAwait(false);
            if (!identity.IsValid) throw new Phd2IdentityMismatchException(identity);
            var off = await EnsureSlitIlluminationOffAsync("short target-centre confirmation", true, cancellationToken).ConfigureAwait(false);
            if (off.Issue is not null) throw new InvalidOperationException(off.Issue);
            exposureMilliseconds = policy.ExposureMilliseconds;
            Report($"小范围 WCS 修正后 SEP 连续复核 {attempt}/{maximumFrames}：{exposureMilliseconds} ms、增益 {shortConfirmationGainPercent}%；需要两张独立一致星像，不移动、不重复解算。");
            var before = CaptureG3FrameMountReadback();
            var captured = await CaptureG3NativeSingleFrameForAcquisitionAsync(
                new Phd2SingleFrameRequest(exposureMilliseconds, configuration.G3.Binning, shortConfirmationGainPercent,
                    ReserveRunEvidencePath("g3-post-wcs-short-target-check", ".fit")), cancellationToken).ConfigureAwait(false);
            var after = CaptureG3FrameMountReadback();
            bool SamePointing(G3FrameMountReadback readback) =>
                readback.CoordinateEpoch == longMountBinding.CoordinateEpoch &&
                string.Equals(readback.PierSide, longMountBinding.PierSide, StringComparison.OrdinalIgnoreCase) &&
                G3AcquisitionMotionPlanner.AngularSeparationArcseconds(longMountBinding.RightAscensionDegrees,
                    longMountBinding.DeclinationDegrees, readback.RightAscensionDegrees, readback.DeclinationDegrees) <= MountCommandArrivalToleranceArcseconds;
            if (!SamePointing(before) || !SamePointing(after))
                throw new InvalidOperationException("G3_SHORT_POINTING_CHANGED: Pointing/pier/epoch changed during independent confirmation.");
            var sha = await ComputeFileSha256Async(captured.Path, cancellationToken).ConfigureAwait(false);
            var binding = CreateG3FieldMountBinding(context, captured.Path, sha, captured.CompletedUtc, after);
            PublishEvidencePathOnce("g3-post-wcs-short-target-check-fits", captured.Path,
                new Dictionary<string, string> { ["purpose"] = "independent-sep-short-exposure-target-centre",
                    ["exposureMilliseconds"] = exposureMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["requestedGainPercent"] = shortConfirmationGainPercent.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["nativeParametersApplied"] = captured.GainAndBinningApplied.ToString(),
                    ["mountBindingSha256"] = binding.BindingSha256, ["sourceLongFrame"] = longFramePath }, sha);
            if (!hashes.Add(sha) || captured.CompletedUtc <= lastCompleted)
                throw new InvalidOperationException("G3_SHORT_FRAME_REUSED: Independent fresh frames are required.");
            lastCompleted = captured.CompletedUtc;
            var image = await imageDataFactory.CreateFromFile(captured.Path, 16, false,
                RawConverterEnum.FREEIMAGE, cancellationToken).ConfigureAwait(false);
            var imageGate = ValidateG3SolveProbeImage(captured, image, exposureMilliseconds,
                nativeCaptureGainPercent: shortConfirmationGainPercent);
            if (imageGate.Disposition != GateDisposition.Passed)
                throw new PhysicalActionGateException(imageGate);
            var frame = G3FrameInputPolicy.Create(image.Properties.Width, image.Properties.Height, image.Data.FlatArray, configuration.G3);
            var content = G3SolveProbeContentAnalyzer.Analyze(frame); // diagnostics only, NOT target extraction
            var sep = await client.DetectAsync(runtime, frame.Width, frame.Height, image.Data.FlatArray,
                frame.SaturationLevel, cancellationToken).ConfigureAwait(false);
            if (!SameHash(workerHash, await ComputeFileSha256Async(runtime.WorkerPath, cancellationToken).ConfigureAwait(false)) ||
                !SameHash(moduleHash, await ComputeFileSha256Async(modulePath, cancellationToken).ConfigureAwait(false)) ||
                !SameHash(sha, await ComputeFileSha256Async(captured.Path, cancellationToken).ConfigureAwait(false)) ||
                (previousPath is not null && !SameHash(previousHash!, await ComputeFileSha256Async(previousPath, cancellationToken).ConfigureAwait(false))))
                throw new InvalidOperationException("G3_SEP_INPUT_CHANGED: Frozen SEP/input changed during confirmation.");
            // The long frame already established the saturated target core.
            // A fresh short frame is bound to that measured point, not to the
            // wider stale prediction map: otherwise a brighter nearby star can
            // be confirmed twice and wrongly replace the catalogue target.
            var measurement = G3SepShortPositionPolicy.Measure(sep, longTargetAnchor,
                shortAssociationRadius, preset.MinimumTargetSignalToNoise, preset.MinimumTargetUniquenessRatio,
                previousSameSetting: previous);
            G3OverlapRegistrationResult? fieldRegistration = null;
            PixelPoint? registeredHint = null;
            if (measurement.Gate.Code == "G3_SEP_UNMEASURED" && longTiming.CaptureRequestedUtc != default &&
                captured.NativeRequestStartedUtc is { } started && started > longTiming.CaptureCompletedUtc)
            {
                // Do not enlarge a missed ROI around a bright blob. Only an independent
                // field transform (target/halo excluded, separate holdouts) may translate
                // the SAME search window on this original. It never supplies the centroid.
                fieldRegistration = await RunWithG3PendingReturnTimeAsync("short-field-registration", async t =>
                {
                    var freshStars = CatalogFieldRegistration.ReferenceStars(sep);
                    G3OverlapRegistrationResult Register(IReadOnlyList<StarCandidate> stars) =>
                        G3TargetTrackPolicy.Register(stars, freshStars, longTargetAnchor, longTargetAnchor,
                            slit, frame.Width, frame.Height, preset.TargetSearchRadiusPixels, t);
                    var first = Register(longFieldStars);
                    if (first.Accepted || freshStars.Count < 5) return first;
                    independentLongStars ??= await extractLongStars(t).ConfigureAwait(false);
                    return Register(independentLongStars);
                }, cancellationToken).ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow;
                registeredHint = G3TargetTrackPolicy.RegisteredShortSearchHint(fieldRegistration, longTargetAnchor,
                    shortAssociationRadius, preset.TargetSearchRadiusPixels, frame.Width, frame.Height, longTiming,
                    new(started, captured.CompletedUtc, exposureMilliseconds / 1000d, now), now);
                if (registeredHint is { } hint)
                {
                    measurement = G3SepShortPositionPolicy.Measure(sep, hint, shortAssociationRadius,
                        preset.MinimumTargetSignalToNoise, preset.MinimumTargetUniquenessRatio, previousSameSetting: previous);
                }
            }
            var shortTarget = measurement.Identification;
            var probe = new G3PlateSolveProbeState(
                GateResult.Unknown("G3_POST_WCS_MEASURED_TARGET_READY", "Independent SEP positions agree; fresh PHD2 proof is still required."),
                captured.Path, image, null, content, attempts, MountBinding: binding,
                BeforeExposureMountReadback: before, MeasuredPostWcsTarget: shortTarget)
            {
                ExposureStartedUtc = before.ReportedUtc,
                ExposureEndedUtc = captured.CompletedUtc,
                ExposureMillisecondsActual = exposureMilliseconds,
            };
            var mountGate = await ValidateG3ProbeMountBindingForMotionAsync(context, probe, cancellationToken).ConfigureAwait(false);
            if (mountGate.Disposition != GateDisposition.Passed)
                throw new PhysicalActionGateException(mountGate);
            longGate = await ValidateG3ProbeMountBindingForMotionAsync(context, longProbe, cancellationToken).ConfigureAwait(false);
            if (longGate.Disposition != GateDisposition.Passed)
                throw new PhysicalActionGateException(longGate);
            var confirmation = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(previous, measurement,
                previousHash, sha, attempt, preset.TargetSearchRadiusPixels, maximumFrames);
            var handoffGeometry = G3PostWcsMeasuredHandoffPolicy.CanHandOff(longTarget, prediction.PredictedTargetPoint,
                prediction.MaximumUncertaintyPixels, slit.AcquisitionPoint, frame.Width, frame.Height,
                preset.MaximumAcquisitionResidualPixels, shortTarget);
            var decision = G3PostWcsShortHandoffPolicy.Evaluate(longTarget, prediction.PredictedTargetPoint,
                prediction.MaximumUncertaintyPixels, slit.AcquisitionPoint, frame.Width, frame.Height,
                preset.MaximumAcquisitionResidualPixels, previous, measurement, confirmation, attempt, boundaryRecheckUsed,
                RemainingPhd2HandoffRadius(preset, measuredTargetConfirmed: true), preset.TargetSearchRadiusPixels);
            var accepted = decision.Accepted;
            var coarsePair = !accepted && allowMeasuredCoarse && MeasuredCoarseAnchorUsable(prediction) &&
                G3MeasuredCoarseRecoveryPolicy.PairUsable(longTarget, prediction.PredictedTargetPoint,
                    prediction.MaximumUncertaintyPixels, slit.AcquisitionPoint, frame.Width, frame.Height,
                    preset.MaximumAcquisitionResidualPixels, preset.TargetSearchRadiusPixels,
                    prediction.FormalAnchor!.Result.Pixscale,
                    RemainingPhd2HandoffRadius(preset, measuredTargetConfirmed: true), previous, measurement, confirmation);
            var next = boundaryRecheckUsed ? policy : policy.AfterConfirmation(measurement.Gate.Code, confirmation, attempt,
                measurement.Identification.Target?.SignalToNoise);
            var receipt = await PublishRunJsonEvidenceAsync("g3-post-wcs-short-target-confirmation",
                "SEP continuity check after a small WCS correction (no repeat solve)",
                new { accepted, coarsePair, attempt, maximumFrames,
                    longFramePath, longTarget, shortFramePath = captured.Path, shortFrameSha256 = sha,
                    longTargetAnchor, shortAssociationRadius, fieldRegistration, registeredHint,
                    exposureMilliseconds, requestedGainPercent = shortConfirmationGainPercent,
                    guidingProfileGainPercent = configuration.G3.GainPercent,
                    commissionedShortStartMilliseconds = commissionedShortExposureMilliseconds,
                    commissionedSearchCeilingMilliseconds = commissionedTargetGuidingCeilingMilliseconds,
                    captured.GainAndBinningApplied, imageGate, shortTarget, measurement, sep, confirmation, handoffGeometry,
                    decision, boundaryRecheckUsed,
                    policy, nextExposurePolicy = next, workerHash, moduleHash, previousPath, previousHash,
                    mountGate, binding, prediction.SourceSolveEvidencePath,
                    targetFieldSolvePerformed = false, freshPhd2TargetAndSlitMeasurementStillRequired = true,
                    scienceOrLockShiftAuthorized = false, budgetReset = false }, captured.Path, cancellationToken).ConfigureAwait(false);
            latest = probe with { SummaryEvidencePath = receipt, Gate = accepted ? probe.Gate : decision.Gate,
                MeasuredPostWcsTarget = accepted ? shortTarget with
                {
                    BoundShortPositionEvidencePath = receipt,
                    CatalogPositionSpreadPixels = confirmation.PositionSpreadPixels,
                    Gate = decision.Gate,
                } : null };
            PublishG3Preview(image, accepted ? "SEP 两张独立短帧已确认目标位置；在原剩余运动预算内交给 PHD2 新帧精调，不代表已经入缝。" :
                ObservationUiPresentation.Present(ObservationStage.AcquireG3SlitField, decision.Gate,
                    ObservationStaticTextLocalization.EffectiveCulture).Summary, slit, shortTarget.Target?.Centroid);
            if (accepted) return latest!;
            if (coarsePair)
            {
                var coarse = await CreateMeasuredCoarseProbeAsync(context, prediction, longProbe,
                    probe with { MeasuredPostWcsTarget = shortTarget with
                    {
                        BoundShortPositionEvidencePath = receipt,
                        CatalogPositionSpreadPixels = confirmation.PositionSpreadPixels,
                    } }, previousPath!, previousHash!, receipt, cancellationToken).ConfigureAwait(false);
                if (coarse is not null) return coarse;
            }
            if (decision.RecheckPair)
            {
                boundaryRecheckUsed = true;
                maximumFrames = attempt + 2;
                previous = null;
                previousHash = previousPath = null; // a wholly new pair, never cherry-pick from the old one
                Report(ObservationUiPresentation.Present(ObservationStage.AcquireG3SlitField, decision.Gate,
                    ObservationStaticTextLocalization.EffectiveCulture).Summary);
                continue;
            }
            if (!confirmation.RetryAllowed || (confirmation.Accepted && !handoffGeometry)) break;
            if (next.ExposureMilliseconds != policy.ExposureMilliseconds)
            {
                previous = null;
                previousHash = previousPath = null; // never pair across settings
            }
            else if (!confirmation.RetainPreviousMeasurement)
            {
                previous = measurement;
                previousHash = sha;
                previousPath = captured.Path;
            }
            policy = next;
        }
        // Preserve the failed evidence. The caller may formally solve its
        // existing long frame for a typed optical failure, never repeat the
        // whole capture ladder or hand a stale prediction to guiding.
        Report(ObservationUiPresentation.Present(ObservationStage.AcquireG3SlitField, latest!.Gate,
            ObservationStaticTextLocalization.EffectiveCulture).Summary);
        return latest with { MeasuredPostWcsTarget = null };
    }
}
