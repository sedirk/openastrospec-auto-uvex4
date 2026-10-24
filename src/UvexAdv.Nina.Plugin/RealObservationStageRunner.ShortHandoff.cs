using NINA.Core.Enum;
using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private async Task<G3PlateSolveProbeState?> ConfirmSaturatedG3HandoffWithShortExposureAsync(
        ObservationContext context, G3WcsMotionPrediction prediction, TargetIdentification longTarget,
        string longFramePath, G3FieldMountBinding longMountBinding, int exposureMilliseconds, Phd2SlitPlacementCommissioning preset,
        SlitGeometry slit, IReadOnlyList<G3PlateSolveAttemptEvidence> attempts, CancellationToken cancellationToken)
    {
        var longProbe = new G3PlateSolveProbeState(GateResult.Unknown("G3_SHORT_CHECK_PENDING", "Not a motion authority"),
            longFramePath, null, null, null, attempts, MountBinding: longMountBinding);
        var runtime = SepStarDetectionClient.ReadRuntime(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UVEX-ADV", "star-detection", "runtime.json"));
        var modulePath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(runtime.WorkerPath)!, "sep_detector.py");
        var workerHash = await ComputeFileSha256Async(runtime.WorkerPath, cancellationToken).ConfigureAwait(false);
        var moduleHash = await ComputeFileSha256Async(modulePath, cancellationToken).ConfigureAwait(false);
        var client = new SepStarDetectionClient();
        var policy = G3PostWcsShortExposurePolicy.Start(exposureMilliseconds);
        G3ShortPositionMeasurement? previous = null;
        string? previousHash = null, previousPath = null;
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lastCompleted = DateTimeOffset.MinValue;
        G3PlateSolveProbeState? latest = null;
        const int shortConfirmationGainPercent = 0;
        var boundaryRecheckUsed = false;
        var maximumFrames = G3PostWcsShortExposurePolicy.MaximumFrames;

        for (var attempt = 1; attempt <= maximumFrames; attempt++)
        {
            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            var longGate = await ValidateG3ProbeMountBindingForMotionAsync(context, longProbe, cancellationToken).ConfigureAwait(false);
            if (longGate.Disposition != GateDisposition.Passed)
                throw new InvalidOperationException($"{longGate.Code}: {longGate.Message}");
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
                throw new InvalidOperationException($"{imageGate.Code}: {imageGate.Message}");
            var frame = G3FrameInputPolicy.Create(image.Properties.Width, image.Properties.Height, image.Data.FlatArray, configuration.G3);
            var content = G3SolveProbeContentAnalyzer.Analyze(frame); // diagnostics only, NOT target extraction
            var sep = await client.DetectAsync(runtime, frame.Width, frame.Height, image.Data.FlatArray,
                frame.SaturationLevel, cancellationToken).ConfigureAwait(false);
            if (!SameHash(workerHash, await ComputeFileSha256Async(runtime.WorkerPath, cancellationToken).ConfigureAwait(false)) ||
                !SameHash(moduleHash, await ComputeFileSha256Async(modulePath, cancellationToken).ConfigureAwait(false)) ||
                !SameHash(sha, await ComputeFileSha256Async(captured.Path, cancellationToken).ConfigureAwait(false)) ||
                (previousPath is not null && !SameHash(previousHash!, await ComputeFileSha256Async(previousPath, cancellationToken).ConfigureAwait(false))))
                throw new InvalidOperationException("G3_SEP_INPUT_CHANGED: Frozen SEP/input changed during confirmation.");
            var measurement = G3SepShortPositionPolicy.Measure(sep, prediction.PredictedTargetPoint,
                preset.TargetSearchRadiusPixels, preset.MinimumTargetSignalToNoise, preset.MinimumTargetUniquenessRatio,
                previousSameSetting: previous);
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
                throw new InvalidOperationException($"{mountGate.Code}: {mountGate.Message}");
            longGate = await ValidateG3ProbeMountBindingForMotionAsync(context, longProbe, cancellationToken).ConfigureAwait(false);
            if (longGate.Disposition != GateDisposition.Passed)
                throw new InvalidOperationException($"{longGate.Code}: {longGate.Message}");
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
            var next = boundaryRecheckUsed ? policy : policy.AfterConfirmation(measurement.Gate.Code, confirmation, attempt);
            var receipt = await PublishRunJsonEvidenceAsync("g3-post-wcs-short-target-confirmation",
                "SEP continuity check after a small WCS correction (no repeat solve)",
                new { accepted, attempt, maximumFrames,
                    longFramePath, longTarget, shortFramePath = captured.Path, shortFrameSha256 = sha,
                    exposureMilliseconds, requestedGainPercent = shortConfirmationGainPercent,
                    guidingProfileGainPercent = configuration.G3.GainPercent,
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
            if (accepted) return latest;
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
        // Do not spend another 5/10/15-second PL3 ladder and then hand a stale
        // prediction to guiding. Preserve the failed measured evidence instead.
        Report(ObservationUiPresentation.Present(ObservationStage.AcquireG3SlitField, latest!.Gate,
            ObservationStaticTextLocalization.EffectiveCulture).Summary);
        return latest with { MeasuredPostWcsTarget = null };
    }
}
