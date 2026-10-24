using System.IO;
using UvexAdv.Observatory;
using UvexAdv.Phd2;
using NINA.Image.Interfaces;

namespace UvexAdv.Nina.Plugin;

// A measured short-frame source may accompany an older formal WCS field.
// Its own pixels/hash/time must not be relabelled as the long frame's time.
internal sealed record G3MeasuredTargetSource(string Path, string Sha, IImageData Image,
    MonochromeFrame Frame, G3FieldMountBinding Binding, G3SparseExposureTiming Timing, PixelPoint Target);

internal sealed partial class RealObservationStageRunner
{
    // One runner-local last-trusted state for every measured stellar target.
    // Rejected candidates never overwrite it. A new focus/pier/owner segment
    // creates a new runner, and live gates remain outside this image-only helper.
    private StellarTargetTrackReference? stellarTargetTrack;

    private sealed record StellarTargetTrackReference(string RunId, long ConnectionEpoch, string PierSide,
        string Path, string Sha, DateTimeOffset CompletedUtc, double ExposureSeconds,
        MonochromeFrame Frame, PixelPoint Target, IReadOnlyList<StarCandidate> Stars,
        G3SparseExposureTiming? Timing);
    private sealed record StellarTargetTrackPrediction(StellarTargetTrackReference Reference,
        PixelPoint Point, IReadOnlyList<StarCandidate> Stars, G3OverlapRegistrationResult Registration);

    private async Task<IReadOnlyList<StarCandidate>> OptionalTargetTrackStarsAsync(
        MonochromeFrame frame, ushort[] pixels, string sha, CancellationToken token)
    {
        try { return await CatalogRegistrationStarsAsync(frame, pixels, token, sha).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or
            System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException)
        {
            // This is an optional independent measurement route. Integrity,
            // physical gates, cancellation and return-budget exceptions escape.
            await WriteAuditBestEffortAsync("g3-target-track-extraction-fallback",
                new { reason = ex.Message, fallback = "existing stellar detector; no new exposure" }).ConfigureAwait(false);
            return StarFieldDetector.Detect(frame);
        }
    }

    private async Task<StellarTargetTrackPrediction?> PredictStellarTargetTrackAsync(
        ObservationContext context, G3FieldState? seed, MonochromeFrame frame, ushort[] pixels,
        PixelPoint expectedTarget, SlitGeometry slit, Phd2SlitPlacementCommissioning preset,
        string path, string sha, G3SparseExposureTiming timing, CancellationToken token)
    {
        if (UsesCatalogWcsTargetAuthority(context) || seed?.BrightTargetAuthority is not null) return null;
        var source = seed?.TargetTrackSource;
        var original = source?.Frame ?? seed?.Frame;
        var originalImage = source?.Image ?? seed?.Image;
        var binding = source?.Binding ?? seed?.MountBinding;
        var sourcePath = source?.Path ?? seed?.FramePath;
        if (stellarTargetTrack is null && original is not null && originalImage is not null && binding is not null &&
            seed is not null && Phd2FreshTargetFluxPolicy.HasMeasuredHandoffPosition(seed.TargetIdentification) &&
            G3TargetTrackPolicy.MatchesImmutablePixels(original,originalImage.Data.FlatArray))
        {
            if (binding.ObservationRunId != context.Plan.ObservationRunId ||
                !SameHash(binding.ActionConfigurationSha256,configuration.ActionConfigurationSha256) ||
                !SameHash(binding.ComputeBindingSha256(),binding.BindingSha256) ||
                !string.Equals(Path.GetFullPath(sourcePath!),Path.GetFullPath(binding.FramePath),StringComparison.OrdinalIgnoreCase) ||
                (source is not null && !SameHash(source.Sha,binding.FrameSha256)))
                throw new PhysicalActionGateException(GateResult.Unknown("G3_TARGET_TRACK_CONTEXT_CHANGED", "Measured target source no longer matches the immutable run and frame binding."));
            if (!SameHash(await ComputeFileSha256Async(sourcePath!, token).ConfigureAwait(false), binding.FrameSha256))
                throw new PhysicalActionGateException(GateResult.Unknown("G3_TARGET_TRACK_SOURCE_CHANGED", "Immutable last-trusted target pixels changed."));
            stellarTargetTrack = new(context.Plan.ObservationRunId, phd2.Snapshot.ConnectionEpoch, binding.PierSide,
                sourcePath!, binding.FrameSha256, binding.FrameCompletedUtc, originalImage.MetaData.Image.ExposureTime,
                original, source?.Target ?? seed.TargetIdentification.Target!.Centroid,
                await OptionalTargetTrackStarsAsync(original, originalImage.Data.FlatArray, binding.FrameSha256, token).ConfigureAwait(false),
                source?.Timing ?? seed.ExposureTiming ?? (seed.Solve?.ExposureTiming is { } sourceTiming &&
                    string.Equals(seed.Solve.SourcePath, seed.FramePath, StringComparison.OrdinalIgnoreCase) ? sourceTiming : null));
        }
        if (stellarTargetTrack is not { } reference) return null;
        if (reference.RunId != context.Plan.ObservationRunId || reference.ConnectionEpoch != phd2.Snapshot.ConnectionEpoch ||
            reference.PierSide != telescopeMediator.GetInfo().SideOfPier.ToString() ||
            reference.Frame.Width != frame.Width || reference.Frame.Height != frame.Height)
            throw new PhysicalActionGateException(GateResult.Unknown("G3_TARGET_TRACK_CONTEXT_CHANGED", "Target trajectory owner, pier side or detector changed."));
        if (!SameHash(await ComputeFileSha256Async(reference.Path, token).ConfigureAwait(false), reference.Sha))
            throw new PhysicalActionGateException(GateResult.Unknown("G3_TARGET_TRACK_SOURCE_CHANGED", "Immutable last-trusted target pixels changed."));
        var completedUtc = timing.CaptureCompletedUtc;
        if (reference.Timing is null || !G3TargetTrackPolicy.IsFresh(reference.CompletedUtc, completedUtc, DateTimeOffset.UtcNow, reference.Sha, sha)) return null;
        var stars = await OptionalTargetTrackStarsAsync(frame, pixels, sha, token).ConfigureAwait(false);
        var registered = G3TargetTrackPolicy.Register(reference.Stars, stars, reference.Target, expectedTarget,
            slit, frame.Width, frame.Height, preset.TargetSearchRadiusPixels, token);
        var temporal = G3SparseFrameTimingPolicy.Evaluate(timing with { SolveCompletedUtc = DateTimeOffset.UtcNow },
            DateTimeOffset.UtcNow, G3SparseTimingPurpose.RegisteredCurrentFrame,
            registered.Accepted ? registered.MaximumHoldoutResidualPixels : 0, preset.TargetSearchRadiusPixels,
            new[] { reference.Timing });
        var captureTimeUnknown = timing.CaptureRequestedUtc == default || reference.Timing.CaptureRequestedUtc == default;
        var point = registered.Transform?.Apply(reference.Target);
        // Current-image registration can guide a SEARCH even when native guide
        // frames have no proven shutter interval. It never becomes a WCS/temporal
        // inference or a motion position: both callers must freshly remeasure
        // the target and retain native-primary continuity before any action.
        var available = (temporal.Usable || captureTimeUnknown) && registered.Accepted && point is not null && point.X >= 0 && point.X < frame.Width &&
            point.Y >= 0 && point.Y < frame.Height && PixelDistance(point, expectedTarget) <= preset.TargetSearchRadiusPixels;
        await PublishRunJsonEvidenceAsync("g3-target-track-registration", "Last-trusted target transported by independent field pixels; prediction only",
            new { reference.Path, reference.Sha, reference.Target, reference.CompletedUtc,
                currentPath = path, currentSha = sha, completedUtc, registered, temporal, point, available, captureTimeUnknown,
                temporalAuthority = false, searchOnly = true, motionAuthority = false,
                targetRegionExcluded = true, targetPositionMeasured = false, budgetReset = false }, path, token).ConfigureAwait(false);
        return available ? new(reference, point!, stars, registered) : null;
    }

    private async Task RememberStellarTargetTrackAsync(ObservationContext context, MonochromeFrame frame,
        ushort[] pixels, PixelPoint measured, string path, string sha, G3SparseExposureTiming timing,
        double exposureSeconds, StellarTargetTrackPrediction? prediction, CancellationToken token)
    {
        if (UsesCatalogWcsTargetAuthority(context) || lastG3Field?.BrightTargetAuthority is not null) return;
        // Only callers after fresh target/guide/slit acceptance may commit.
        // A registration point, forced aperture or rejected candidate cannot call here.
        stellarTargetTrack = new(context.Plan.ObservationRunId, phd2.Snapshot.ConnectionEpoch,
            telescopeMediator.GetInfo().SideOfPier.ToString(), path, sha, timing.CaptureCompletedUtc, exposureSeconds,
            frame, measured, prediction?.Stars ?? await OptionalTargetTrackStarsAsync(frame, pixels, sha, token).ConfigureAwait(false),
            timing with { SolveCompletedUtc = DateTimeOffset.UtcNow });
    }

    private static bool RejectStellarTargetTrackCandidate(StellarTargetTrackPrediction? prediction,
        MonochromeFrame frame, PixelPoint measured, double exposureSeconds, double allowance, out double correlation)
    {
        correlation = double.NaN;
        if (prediction is null) return false;
        var reference = prediction.Reference;
        var comparable = reference.ExposureSeconds > 0 &&
            Math.Abs(reference.ExposureSeconds - exposureSeconds) <= Math.Max(.001, exposureSeconds * .02);
        return !G3TargetTrackPolicy.AcceptMeasuredCandidate(reference.Frame, reference.Target,
            frame, measured, prediction.Point, allowance, comparable, out correlation);
    }

    private async Task<TargetIdentification?> TryWideFieldStellarMeasurementAsync(ObservationContext context,
        G3PlateSolveProbeState probe, MonochromeFrame frame, IReadOnlyList<StarCandidate> candidates,
        PixelPoint originalPrediction, Phd2SlitPlacementCommissioning preset, CancellationToken token)
    {
        if(UsesCatalogWcsTargetAuthority(context)) return null;
        var hint=await TryWideFieldTargetSearchHintAsync(context,probe,token).ConfigureAwait(false);
        if(hint is null || !double.IsFinite(hint.PredictedTarget.X) || !double.IsFinite(hint.PredictedTarget.Y) ||
            PixelDistance(hint.PredictedTarget,originalPrediction)>preset.TargetSearchRadiusPixels) return null;
        // Unknown wide/narrow time uncertainty is explicitly allowed ONLY to
        // propose another search on these same pixels. It cannot widen the
        // original recognition circle, satisfy continuity or authorize movement.
        var measured=G3TargetTrackPolicy.MeasureSearchHint(frame,candidates,hint.PredictedTarget,originalPrediction,
            preset.TargetSearchRadiusPixels,preset.MinimumTargetSignalToNoise,preset.MinimumTargetUniquenessRatio);
        var accepted=measured is not null;
        await PublishRunJsonEvidenceAsync("g3-wide-hint-fresh-target-measurement",
            "Wide-field search hint tested against this actual new G3 image; no model point accepted as a measurement",
            new { hint, measured, originalPrediction, accepted, preset.TargetSearchRadiusPixels,
                newExposure=false, nativePrimaryContinuityStillRequired=true, budgetReset=false },probe.FramePath,token).ConfigureAwait(false);
        return accepted ? measured! with { Gate=measured!.Gate with { Severity=GateSeverity.Warning,
            Message="Fresh G3 pixels remeasured the candidate inside the original recognition circle. The wide-field prior supplied search only, not a formal solve of this image or new catalogue identity. " + hint.EvidencePath } } : null;
    }
}
