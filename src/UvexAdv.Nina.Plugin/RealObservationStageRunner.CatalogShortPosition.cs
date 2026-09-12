using NINA.Core.Enum;
using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private async Task<G3FieldState> RefineClippedCatalogPositionAsync(
        ObservationContext context, G3FieldState field, CancellationToken cancellationToken)
    {
        if (field.Gate.Disposition != GateDisposition.Passed || field.Frame is null ||
            field.Solve?.Result.Success != true || field.MountBinding is null ||
            field.TargetIdentification.Authority != TargetIdentificationAuthority.CatalogWcsProjection ||
            UsesCatalogWcsTargetAuthority(context) ||
            commissioning?.Value.Phd2SlitPlacement is not { } preset ||
            !G3CatalogTargetPositionPolicy.NeedsShortPositionCheck(field.Frame,
                field.TargetIdentification.PredictedPoint, preset.TargetSearchRadiusPixels))
            return field;

        G3FieldState Unconfirmed(string reason, IReadOnlyDictionary<string, double>? metrics = null) => field with
        {
            Gate = GateResult.Unknown("G3_CATALOG_SHORT_POSITION_UNCONFIRMED", reason, metrics),
        };
        var exposure = preset.DirectTargetGuidingExposureMilliseconds;
        if (exposure is not > 0)
            return Unconfirmed("The clipped formal-WCS frame requires the commissioned short target exposure; none is configured. No new motion was sent.");

        G3ShortPositionMeasurement? previous = null;
        string? previousHash = null, previousFramePath = null, previousReceipt = null;
        DateTimeOffset previousCompletedUtc = default;
        var frameHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var receipts = new List<string>();
        SepDetectionRuntime sepRuntime;
        string sepModulePath,sepWorkerSha256,sepModuleSha256;
        try
        {
            sepRuntime = SepStarDetectionClient.ReadRuntime(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UVEX-ADV", "star-detection", "runtime.json"));
            sepModulePath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(sepRuntime.WorkerPath)!, "sep_detector.py");
            sepWorkerSha256 = await ComputeFileSha256Async(sepRuntime.WorkerPath,cancellationToken).ConfigureAwait(false);
            sepModuleSha256 = await ComputeFileSha256Async(sepModulePath,cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Unconfirmed($"G3_SEP_RUNTIME_UNAVAILABLE: {ex.Message}. No short exposure or legacy fallback was started.");
        }
        var sepClient = new SepStarDetectionClient();
        // Read once and freeze an optional primary/companion reference. This is
        // catalogue metadata, not permission to move or a brightness heuristic.
        StellariumCompanionReference? companionReference = null;
        CatalogPrimaryAstrometry? primaryAstrometry = null;
        PixelPoint? astrometricPrimaryPrediction = null;
        PixelPoint? companionVector = null;
        var planetarium = profileService.ActiveProfile.PlanetariumSettings;
        if (string.Equals(planetarium.PreferredPlanetarium.ToString(), "STELLARIUM", StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = new UriBuilder(Uri.UriSchemeHttp,
                string.IsNullOrWhiteSpace(planetarium.StellariumHost) ? "localhost" : planetarium.StellariumHost.Trim(), planetarium.StellariumPort).Uri;
            companionReference = await StellariumCompanionReferenceReader.ReadAsync(endpoint, context.Plan.Target.CatalogId,
                context.Plan.Target.RightAscensionDegrees, context.Plan.Target.DeclinationDegrees, cancellationToken).ConfigureAwait(false);
            if (companionReference is not null)
            {
                primaryAstrometry = await CatalogPrimaryAstrometryReader.ReadAsync(context.Plan.Target.CatalogId,
                    context.Plan.Target.RightAscensionDegrees, context.Plan.Target.DeclinationDegrees, cancellationToken).ConfigureAwait(false);
                if (primaryAstrometry is null)
                    return Unconfirmed("G3_SEP_CATALOG_REFERENCE_UNAVAILABLE: The exact locked catalogue ID could not be verified in ICRS epoch-2000 coordinates with proper motion. No short exposure, component-pair guess or motion was started.");
                var primary = new NINA.Astrometry.Coordinates(primaryAstrometry.RightAscensionDegrees,
                    primaryAstrometry.DeclinationDegrees, NINA.Astrometry.Epoch.J2000, NINA.Astrometry.Coordinates.RAType.Degrees);
                var pa = companionReference.PositionAngleDegrees * Math.PI / 180;
                var companion = new NINA.Astrometry.Coordinates(
                    primary.RADegrees + companionReference.SeparationArcseconds * Math.Sin(pa) / (3600 * Math.Cos(primary.Dec * Math.PI / 180)),
                    primary.Dec + companionReference.SeparationArcseconds * Math.Cos(pa) / 3600,
                    primary.Epoch, NINA.Astrometry.Coordinates.RAType.Degrees);
                var projected = G3WcsTargetProjector.Project(companion, field.Solve.Result, field.Frame.Width, field.Frame.Height, field.Solve.SolverIdentity);
                var primaryProjected = G3WcsTargetProjector.Project(primary, field.Solve.Result, field.Frame.Width, field.Frame.Height, field.Solve.SolverIdentity);
                astrometricPrimaryPrediction = new(primaryProjected.X, primaryProjected.Y);
                companionVector = new(projected.X - primaryProjected.X, projected.Y - primaryProjected.Y);
            }
        }
        for (var attempt = 1; attempt <= G3ShortPositionMeasurementPolicy.MaximumFrames; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            var originalBinding = await ValidateG3FieldMountBindingForMotionAsync(context, field, cancellationToken).ConfigureAwait(false);
            if (originalBinding.Disposition != GateDisposition.Passed) return field with { Gate = originalBinding };
            if (!string.Equals(System.IO.Path.GetFullPath(field.Solve.SourcePath), System.IO.Path.GetFullPath(field.FramePath), StringComparison.OrdinalIgnoreCase) ||
                !SameHash(await ComputeFileSha256Async(field.Solve.EvidencePath, cancellationToken).ConfigureAwait(false), field.Solve.EvidenceSha256))
                return Unconfirmed("The formal WCS source or immutable solve evidence changed before the short-position check.");
            var identity = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), cancellationToken).ConfigureAwait(false);
            if (!identity.IsValid) throw new Phd2IdentityMismatchException(identity);
            var off = await EnsureSlitIlluminationOffAsync("catalogue short position confirmation", true, cancellationToken).ConfigureAwait(false);
            if (off.Issue is not null) throw new InvalidOperationException(off.Issue);

            const int shortGain = 0;
            Report(ObservationUiPresentation.Text(
                $"WCS 已确认目标在视场内；SEP 短帧定位 {attempt}/{G3ShortPositionMeasurementPolicy.MaximumFrames}：{exposure} ms、增益 {shortGain}%。核对完整星像、目录位置与独立新帧；不移动、不重复解算。",
                $"WCS places the target inside the field; SEP short position check {attempt}/{G3ShortPositionMeasurementPolicy.MaximumFrames}: {exposure} ms, gain {shortGain}%. Verify whole star regions, catalogue position and independent frames; no motion or repeat solve."));
            var before = CaptureG3FrameMountReadback();
            var capture = await CaptureG3NativeSingleFrameForAcquisitionAsync(
                new Phd2SingleFrameRequest(exposure.Value, configuration.G3.Binning, shortGain,
                    ReserveRunEvidencePath("g3-catalog-short-position", ".fit")), cancellationToken).ConfigureAwait(false);
            var after = CaptureG3FrameMountReadback();
            bool SamePointing(G3FrameMountReadback readback) =>
                string.Equals(readback.CoordinateEpoch, field.MountBinding.CoordinateEpoch, StringComparison.Ordinal) &&
                string.Equals(readback.PierSide, field.MountBinding.PierSide, StringComparison.OrdinalIgnoreCase) &&
                G3AcquisitionMotionPlanner.AngularSeparationArcseconds(field.MountBinding.RightAscensionDegrees,
                    field.MountBinding.DeclinationDegrees, readback.RightAscensionDegrees, readback.DeclinationDegrees) <= MountCommandArrivalToleranceArcseconds;
            var sha = await ComputeFileSha256Async(capture.Path, cancellationToken).ConfigureAwait(false);
            var binding = CreateG3FieldMountBinding(context, capture.Path, sha, capture.CompletedUtc, after);
            PublishEvidencePathOnce("g3-catalog-short-position-fits", capture.Path,
                new Dictionary<string, string>
                {
                    ["sourceWcsFrame"] = field.FramePath,
                    ["mountBindingSha256"] = binding.BindingSha256,
                    ["purpose"] = "no-motion-catalogue-position-refinement"
                }, sha);
            if (!frameHashes.Add(sha) || (previousCompletedUtc != default && capture.CompletedUtc <= previousCompletedUtc))
                return Unconfirmed("G3_SHORT_FRAME_REUSED: A repeated image or non-increasing capture completion cannot establish independent position evidence.");
            // Every capture consumes freshness, including a rejected image;
            // previous measurement below is only the last VALID position.
            previousCompletedUtc = capture.CompletedUtc;
            if (!SamePointing(before) || !SamePointing(after))
                return Unconfirmed("Pointing, epoch or pier side changed between the formal WCS and the short frame; no centroid offset was adopted.");
            var image = await imageDataFactory.CreateFromFile(capture.Path, 16, false,
                RawConverterEnum.FREEIMAGE, cancellationToken).ConfigureAwait(false);
            var imageGate = ValidateG3SolveProbeImage(capture, image, exposure.Value, nativeCaptureGainPercent: shortGain);
            if (imageGate.Disposition != GateDisposition.Passed) return field with { Gate = imageGate };
            var frame = G3FrameInputPolicy.Create(image.Properties.Width, image.Properties.Height,
                image.Data.FlatArray, configuration.G3);
            if (frame.Width != field.Frame.Width || frame.Height != field.Frame.Height)
                return Unconfirmed("Short-position frame changed the formal-WCS detector geometry.");
            var probe = new G3PlateSolveProbeState(GateResult.Unknown("G3_SHORT_CHECK_PENDING", "Not motion authority"),
                capture.Path, image, null, null, Array.Empty<G3PlateSolveAttemptEvidence>(),
                MountBinding: binding, BeforeExposureMountReadback: before);
            var shortBindingGate = await ValidateG3ProbeMountBindingForMotionAsync(context, probe, cancellationToken).ConfigureAwait(false);
            if (shortBindingGate.Disposition != GateDisposition.Passed) return field with { Gate = shortBindingGate };
            // Recheck the ORIGINAL frame after the short exposure, not just the new
            // frame. Both immutable hashes, run, owner, epoch, pier side and current
            // mount position must still agree. The long frame remains WCS authority.
            originalBinding = await ValidateG3FieldMountBindingForMotionAsync(context, field, cancellationToken).ConfigureAwait(false);
            if (originalBinding.Disposition != GateDisposition.Passed) return field with { Gate = originalBinding };
            SepImageMeasurements sepMeasurements;
            try
            {
                sepMeasurements = await sepClient.DetectAsync(sepRuntime,frame.Width,frame.Height,
                    image.Data.FlatArray,frame.SaturationLevel,cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                return Unconfirmed($"G3_SEP_WORKER_FAILED: {ex.Message}. No legacy detection fallback or motion was issued.");
            }
            if (!SameHash(sepWorkerSha256,await ComputeFileSha256Async(sepRuntime.WorkerPath,cancellationToken).ConfigureAwait(false))
                || !SameHash(sepModuleSha256,await ComputeFileSha256Async(sepModulePath,cancellationToken).ConfigureAwait(false))
                || !SameHash(sha,await ComputeFileSha256Async(capture.Path,cancellationToken).ConfigureAwait(false)))
                return Unconfirmed("G3_SEP_INPUT_CHANGED: Raw frame or frozen SEP worker changed during measurement.");
            // The image-only process has a bounded but nonzero latency. Recheck
            // both original WCS and short-frame bindings after it completes.
            originalBinding = await ValidateG3FieldMountBindingForMotionAsync(context,field,cancellationToken).ConfigureAwait(false);
            if (originalBinding.Disposition != GateDisposition.Passed) return field with { Gate=originalBinding };
            shortBindingGate = await ValidateG3ProbeMountBindingForMotionAsync(context,probe,cancellationToken).ConfigureAwait(false);
            if (shortBindingGate.Disposition != GateDisposition.Passed) return field with { Gate=shortBindingGate };
            var measurement = companionVector is not null && astrometricPrimaryPrediction is not null
                ? G3SepCatalogPrimaryPolicy.Measure(sepMeasurements, astrometricPrimaryPrediction, companionVector,
                    preset.TargetSearchRadiusPixels, preset.MinimumTargetSignalToNoise)
                : G3SepShortPositionPolicy.Measure(sepMeasurements, field.TargetIdentification.PredictedPoint,
                    preset.TargetSearchRadiusPixels, preset.MinimumTargetSignalToNoise, preset.MinimumTargetUniquenessRatio);
            var measured = measurement.Identification;
            if (previousFramePath is not null && !SameHash(previousHash!,
                    await ComputeFileSha256Async(previousFramePath, cancellationToken).ConfigureAwait(false)))
                return Unconfirmed("G3_SHORT_FRAME_CHANGED: The first confirmation frame changed; no position was adopted.");
            var confirmation = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(
                previous, measurement, previousHash, sha, attempt, preset.TargetSearchRadiusPixels);
            var repeatGate = confirmation.RepeatGate;
            var accepted = confirmation.Accepted;
            var positionSpread = confirmation.PositionSpreadPixels;
            var receipt = await PublishRunJsonEvidenceAsync("g3-catalog-short-position-confirmation",
                "Formal long-frame WCS and a separately bound short-exposure position",
                new
                {
                    accepted,
                    attempt,
                    policyVersion = primaryAstrometry is null ? G3SepShortPositionPolicy.Version : "sep-exact-id-parent-region-v1",
                    primaryAstrometry,
                    astrometricPrimaryPrediction,
                    originalPlanPrediction = field.TargetIdentification.PredictedPoint,
                    planCoordinatesChanged = false,
                    sepRuntime,
                    sepWorkerSha256,
                    sepModuleSha256,
                    sepMeasurements,
                    maximumFrames = G3ShortPositionMeasurementPolicy.MaximumFrames,
                    companionReference,
                    companionVector,
                    measurement,
                    confirmation,
                    repeatGate,
                    positionSpreadPixels = positionSpread,
                    previousFramePath,
                    previousHash,
                    previousReceipt,
                    sourceWcsFrame = field.FramePath,
                    sourceWcsEvidence = field.Solve.EvidencePath,
                    sourceWcsBinding = field.MountBinding,
                    shortFramePath = capture.Path,
                    shortFrameSha256 = sha,
                    shortBinding = binding,
                    before,
                    after,
                    originalBinding,
                    shortBindingGate,
                    imageGate,
                    measured,
                    exposureMilliseconds = exposure,
                    requestedGainPercent = shortGain,
                    capture.GainAndBinningApplied,
                    repeatSolve = false,
                    focusEvidence = false,
                    motionIssued = false,
                    budgetReset = false
                },
                capture.Path, cancellationToken).ConfigureAwait(false);
            receipts.Add(receipt);
            if (!accepted)
            {
                if (!confirmation.RetryAllowed)
                    return Unconfirmed($"Short position confirmation failed: {confirmation.Gate.Code}: {confirmation.Gate.Message}. Evidence: {receipt}", confirmation.Gate.Metrics);
                if (!confirmation.RetainPreviousMeasurement)
                {
                    previous = measurement;
                    previousHash = sha;
                    previousFramePath = capture.Path;
                    previousReceipt = receipt;
                }
                Report(ObservationUiPresentation.Text(
                    $"短曝光位置复核 {attempt}/{G3ShortPositionMeasurementPolicy.MaximumFrames} 尚未凑齐一致的新帧；保留本帧诊断，在原上限内补拍，不移动、不重置预算。",
                    $"Short position check {attempt}/{G3ShortPositionMeasurementPolicy.MaximumFrames} needs another consistent fresh position; retain diagnostics and retry within the original cap, without motion or budget reset."));
                continue;
            }

            var identification = measured with
            {
                Authority = TargetIdentificationAuthority.CatalogWcsProjection,
                CatalogPositionRefinedFromSameFrame = false,
                BoundShortPositionEvidencePath = receipt,
                CatalogPositionSpreadPixels = positionSpread,
                Gate = GateResult.Pass("TARGET_CATALOG_WCS_SHORT_REFINED",
                    "Formal WCS retains identity; separately hash-bound, unchanged-pointing short evidence supplies a measured coarse centre and its empirical spread. Not exact placement or focus.", measured.Gate.Metrics),
            };
            var residual = PixelDistance(identification.Target!.Centroid, field.SlitDetection.Geometry.AcquisitionPoint);
            PublishG3Preview(image, ObservationUiPresentation.Text(
                $"短曝光位置已复核：距本轮 LED 狭缝中点 {residual:F2}px，位置分散尺度 {positionSpread:F2}px；仅供粗定位/接管，精入缝仍须新帧实测。",
                $"Short position confirmed: {residual:F2}px from the run's LED slit midpoint, empirical position spread {positionSpread:F2}px. Coarse handoff only; fresh fine residual still required."),
                field.SlitDetection.Geometry, identification.Target.Centroid);
            return field with
            {
                TargetIdentification = identification,
                Gate = GateResult.Pass("G3_CATALOG_SHORT_POSITION_CONFIRMED", identification.Gate.Message,
                    new Dictionary<string, double>
                    {
                        ["targetToSlitResidualPixels"] = residual,
                        ["shortPositionSpreadPixels"] = positionSpread,
                        ["shortPositionFrames"] = attempt
                    })
            };
        }
        return Unconfirmed($"Bounded short position confirmation exhausted. Evidence: {string.Join(", ", receipts)}");
    }
}
