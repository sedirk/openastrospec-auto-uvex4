using NINA.Core.Enum;
using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private async Task<G3PlateSolveProbeState?> ConfirmSaturatedG3HandoffWithShortExposureAsync(
        ObservationContext context, G3WcsMotionPrediction prediction, TargetIdentification longTarget,
        string longFramePath, G3FieldMountBinding longMountBinding, int exposureMilliseconds, Phd2SlitPlacementCommissioningPreset preset,
        SlitGeometry slit, IReadOnlyList<G3PlateSolveAttemptEvidence> attempts, CancellationToken cancellationToken)
    {
        await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
        var longProbe = new G3PlateSolveProbeState(GateResult.Unknown("G3_SHORT_CHECK_PENDING", "Not a motion authority"),
            longFramePath, null, null, null, attempts, MountBinding: longMountBinding);
        var longGate = await ValidateG3ProbeMountBindingForMotionAsync(context, longProbe, cancellationToken).ConfigureAwait(false);
        if (longGate.Disposition != GateDisposition.Passed)
            throw new InvalidOperationException($"{longGate.Code}: {longGate.Message}");
        var identity = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), cancellationToken).ConfigureAwait(false);
        if (!identity.IsValid) throw new Phd2IdentityMismatchException(identity);
        var off = await EnsureSlitIlluminationOffAsync("short target-centre confirmation", true, cancellationToken).ConfigureAwait(false);
        if (off.Issue is not null) throw new InvalidOperationException(off.Issue);
        var before = CaptureG3FrameMountReadback();
        // The native owner applies/restores these single-frame parameters.
        // A short exposure at the high guiding gain can still clip a bright
        // target. Use the same supported minimum as LED geometry captures,
        // without changing the locked guiding profile or accepting clipping.
        const int shortConfirmationGainPercent = 0;
        Report($"饱和亮团不能单独证明星心；用 {exposureMilliseconds} ms、增益 {shortConfirmationGainPercent}% 的原生短曝光复核位置，不做短曝光 PL3、不移动设备，导星配置不变。");
        var captured = await CaptureG3NativeSingleFrameForAcquisitionAsync(
            new Phd2SingleFrameRequest(exposureMilliseconds, configuration.G3.Binning, shortConfirmationGainPercent,
                ReserveRunEvidencePath("g3-post-wcs-short-target-check", ".fit")), cancellationToken).ConfigureAwait(false);
        var after = CaptureG3FrameMountReadback();
        var sha = await ComputeFileSha256Async(captured.Path, cancellationToken).ConfigureAwait(false);
        var binding = CreateG3FieldMountBinding(context, captured.Path, sha, captured.CompletedUtc, after);
        PublishEvidencePathOnce("g3-post-wcs-short-target-check-fits", captured.Path,
            new Dictionary<string,string> { ["purpose"]="independent-short-exposure-target-centre",
                ["exposureMilliseconds"]=exposureMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["requestedGainPercent"]=shortConfirmationGainPercent.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["nativeParametersApplied"]=captured.GainAndBinningApplied.ToString(),
                ["mountBindingSha256"]=binding.BindingSha256, ["sourceLongFrame"]=longFramePath }, sha);
        var image = await imageDataFactory.CreateFromFile(captured.Path, 16, false,
            RawConverterEnum.FREEIMAGE, cancellationToken).ConfigureAwait(false);
        var imageGate = ValidateG3SolveProbeImage(captured, image, exposureMilliseconds,
            nativeCaptureGainPercent: shortConfirmationGainPercent);
        if (imageGate.Disposition != GateDisposition.Passed)
            throw new InvalidOperationException($"{imageGate.Code}: {imageGate.Message}");
        var frame = G3FrameInputPolicy.Create(image.Properties.Width, image.Properties.Height, image.Data.FlatArray, configuration.G3);
        var content = G3SolveProbeContentAnalyzer.Analyze(frame);
        var shortTarget = SlitTargetIdentifier.Identify(frame, content.StellarMeasurement.Stars,
            prediction.PredictedTargetPoint, preset.TargetSearchRadiusPixels,
            preset.MinimumTargetSignalToNoise, preset.MinimumTargetUniquenessRatio);
        var probe = new G3PlateSolveProbeState(
            GateResult.Unknown("G3_POST_WCS_MEASURED_TARGET_READY", "Long/short exposure target centres agree inside the original acquisition window; fresh PHD2 proof is still required."),
            captured.Path, image, null, content, attempts, MountBinding: binding,
            BeforeExposureMountReadback: before, MeasuredPostWcsTarget: shortTarget);
        var mountGate = await ValidateG3ProbeMountBindingForMotionAsync(context, probe, cancellationToken).ConfigureAwait(false);
        if (mountGate.Disposition != GateDisposition.Passed)
            throw new InvalidOperationException($"{mountGate.Code}: {mountGate.Message}");
        longGate = await ValidateG3ProbeMountBindingForMotionAsync(context, longProbe, cancellationToken).ConfigureAwait(false);
        if (longGate.Disposition != GateDisposition.Passed)
            throw new InvalidOperationException($"{longGate.Code}: {longGate.Message}");
        var accepted = G3PostWcsMeasuredHandoffPolicy.CanHandOff(longTarget, prediction.PredictedTargetPoint,
            prediction.MaximumUncertaintyPixels, slit.AcquisitionPoint, frame.Width, frame.Height,
            preset.MaximumAcquisitionResidualPixels, shortTarget);
        var receipt = await PublishRunJsonEvidenceAsync("g3-post-wcs-short-target-confirmation",
            "Independent short exposure checks the clipped long-exposure centroid",
            new { accepted, longFramePath, longTarget, shortFramePath=captured.Path, shortFrameSha256=sha,
                exposureMilliseconds, requestedGainPercent=shortConfirmationGainPercent,
                guidingProfileGainPercent=configuration.G3.GainPercent,
                captured.GainAndBinningApplied, imageGate,
                shortTarget, mountGate, binding, prediction.SourceSolveEvidencePath,
                targetFieldSolvePerformed=false, freshPhd2TargetAndSlitMeasurementStillRequired=true,
                scienceOrLockShiftAuthorized=false, budgetReset=false }, captured.Path, cancellationToken).ConfigureAwait(false);
        PublishG3Preview(image, accepted ? "长短曝光位置一致；已确认真实星心靠近狭缝，交给 PHD2 新帧精调。" :
            "短曝光未确认长曝光亮团的星心位置；不误交接，继续原有解算与有界定位。", slit, shortTarget.Target?.Centroid);
        return accepted ? probe with { SummaryEvidencePath = receipt } : null;
    }
}
