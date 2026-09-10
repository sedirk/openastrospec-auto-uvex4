using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed record Phd2FreshSlitStackSample(
    Phd2FreshSlitTemporalSample Evidence,
    MonochromeFrame Frame);

/// <summary>
/// A detector-fixed dark-aperture measurement from three current guide frames.
/// Never supplies a target centroid, guide position, WCS, or extra capture budget.
/// </summary>
internal static class Phd2FreshSlitStackPolicy
{
    internal const int RequiredFrames = 3;

    internal static SlitLocusDetection Evaluate(
        IReadOnlyList<Phd2FreshSlitStackSample> samples,
        SlitGeometry seed,
        double minimumContrastSigma,
        double maximumSearchPixels,
        double maximumSearchDegrees,
        double maximumMountSpanArcseconds,
        TimeSpan maximumAge,
        TimeSpan maximumWindowSpan,
        DateTimeOffset now,
        bool sameRunSlitSeedAuthorized,
        bool guideEpochAndLockStayedFixed)
    {
        SlitLocusDetection Reject(string suffix, string message) => new(
            GateResult.Unknown("PHD2_FRESH_SLIT_STACK_" + suffix, message), seed, 0, 0, 0);
        if (!sameRunSlitSeedAuthorized || !guideEpochAndLockStayedFixed)
            return Reject("CONTEXT_INVALID", "Fresh slit stacking requires this run's LED identity and an unchanged guide epoch/lock.");
        if (samples.Count != RequiredFrames)
            return Reject("FRAME_COUNT", "Exactly three recent independent guiding frames are required for a detector-fixed slit stack.");
        if (maximumAge <= TimeSpan.Zero || maximumWindowSpan <= TimeSpan.Zero ||
            !double.IsFinite(maximumMountSpanArcseconds) || maximumMountSpanArcseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumAge));

        var last = samples[^1];
        var binding = last.Evidence.MountBinding;
        // The live target/guide measurement is from the newest frame. Earlier
        // members describe only the detector-fixed slit over a bounded window;
        // applying a single-frame age to all three makes a 2 s cadence impossible.
        if (now < last.Evidence.GuideStepUtc || now - last.Evidence.GuideStepUtc > maximumAge ||
            now < binding.FrameCompletedUtc || now - binding.FrameCompletedUtc > maximumAge)
            return Reject("LATEST_FRAME_STALE", "The newest individual target/guide frame exceeds its unchanged measurement age limit.");
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            var evidence = sample.Evidence;
            if (string.IsNullOrWhiteSpace(evidence.FrameSha256) || !hashes.Add(evidence.FrameSha256) ||
                evidence.GuidingWasInterrupted || evidence.ExposureChanged || evidence.CaptureLoopStarted ||
                evidence.Detection.Gate.Code is not ("SLIT_LOCUS_LOW_CONFIDENCE" or
                    "SLIT_LOCUS_DETECTED" or "SLIT_LOCUS_LOCAL_BACKGROUND_DETECTED") ||
                sample.Frame.Width != last.Frame.Width || sample.Frame.Height != last.Frame.Height ||
                sample.Frame.SaturationLevel != last.Frame.SaturationLevel)
                return Reject("MEMBER_INVALID", "A slit-stack frame was reused or its capture/detector/recognition context changed.");
            if (now < evidence.GuideStepUtc ||
                last.Evidence.GuideStepUtc - evidence.GuideStepUtc > maximumWindowSpan ||
                now < evidence.MountBinding.FrameCompletedUtc ||
                binding.FrameCompletedUtc - evidence.MountBinding.FrameCompletedUtc > maximumWindowSpan ||
                i > 0 && (evidence.TriggerGuideFrame <= samples[i - 1].Evidence.TriggerGuideFrame ||
                    evidence.EventSequence <= samples[i - 1].Evidence.EventSequence ||
                    evidence.GuideStepUtc <= samples[i - 1].Evidence.GuideStepUtc))
                return Reject("STALE_OR_UNORDERED", "A slit-stack member is stale, future-dated, or not a new ordered guide frame.");
            var gate = G3FieldMountBindingPolicy.ValidateForMotion(
                evidence.MountBinding, binding.ObservationRunId, binding.ActionConfigurationSha256,
                binding.CommissioningPresetSha256, evidence.MountBinding.FramePath, evidence.FrameSha256,
                binding.RightAscensionDegrees, binding.DeclinationDegrees, binding.CoordinateEpoch,
                binding.PierSide, maximumMountSpanArcseconds);
            if (gate.Disposition != GateDisposition.Passed)
                return Reject("BINDING_INVALID", $"A fresh slit-stack mount binding failed: {gate.Code}: {gate.Message}");
        }
        for (var i = 0; i < samples.Count; i++)
        for (var j = i + 1; j < samples.Count; j++)
        {
            var a = samples[i].Evidence.MountBinding;
            var b = samples[j].Evidence.MountBinding;
            var span = G3AcquisitionMotionPlanner.AngularSeparationArcseconds(
                a.RightAscensionDegrees, a.DeclinationDegrees, b.RightAscensionDegrees, b.DeclinationDegrees);
            if (!double.IsFinite(span) || span > maximumMountSpanArcseconds)
                return Reject("MOUNT_MOVED", "Mount position changed beyond the existing fresh-frame binding envelope.");
        }

        var pixels = new ushort[checked(last.Frame.Width * last.Frame.Height)];
        for (var y = 0; y < last.Frame.Height; y++)
        for (var x = 0; x < last.Frame.Width; x++)
        {
            var sum = 0;
            var saturated = false;
            foreach (var sample in samples)
            {
                var value = sample.Frame[x, y];
                sum += value;
                saturated |= value >= sample.Frame.SaturationLevel;
            }
            // A clipped member cannot become an apparently valid flank by averaging.
            pixels[y * last.Frame.Width + x] = saturated ? last.Frame.SaturationLevel
                : (ushort)((sum + RequiredFrames / 2) / RequiredFrames);
        }
        var stack = new MonochromeFrame(last.Frame.Width, last.Frame.Height, pixels, last.Frame.SaturationLevel);
        var detection = SlitLocalBackgroundDetector.Detect(stack, seed,
            maximumSearchPixels, maximumSearchDegrees, minimumContrastSigma);
        if (detection.Gate.Disposition != GateDisposition.Passed)
            return detection with { Gate = GateResult.Unknown("PHD2_FRESH_SLIT_STACK_LOW_CONFIDENCE",
                "Three fresh frames still do not establish the physical dark aperture at the unchanged contrast threshold.",
                detection.Gate.Metrics) };
        return detection with { Gate = GateResult.Pass("PHD2_FRESH_SLIT_STACK_DETECTED",
            $"Three fresh detector-aligned frames prove the same-run LED-authorized dark slit at {detection.ContrastSigma:F2}σ; target/guide positions still come only from the newest individual frame.",
            detection.Gate.Metrics) };
    }
}
