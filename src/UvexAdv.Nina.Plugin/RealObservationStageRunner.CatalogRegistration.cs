using System.IO;
using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed record G3CatalogRegistrationReference(string FramePath, string FrameSha256,
    string RunId, long ConnectionEpoch, string PierSide, int Width, int Height,
    PixelPoint CataloguePoint, IReadOnlyList<StarCandidate> Stars);

internal static class G3CatalogReferenceHandoff
{
    internal static G3FieldState Preserve(G3FieldState arrival, G3CatalogRegistrationReference? reference, bool catalogOnly)
    {
        if (!catalogOnly || arrival.Gate.Disposition != GateDisposition.Passed) return arrival;
        if (arrival.CatalogRegistrationReference is not null) return arrival;
        if (reference is null)
            return arrival with { Gate = GateResult.Unknown("G3_CATALOG_REFERENCE_MISSING",
                "目录定位的原始 WCS 参考星场未传递到接管阶段；未把运动预测或目标附近亮峰当成目录参考。") };
        // Preserve the original point, pixels' hash and independent stars, not
        // the arrival prediction. Registration rechecks run/owner/pier/detector
        // and immutable pixels before measuring any fresh fine residual.
        return arrival with { CatalogRegistrationReference = reference };
    }
}

internal sealed partial class RealObservationStageRunner
{
    private async Task<IReadOnlyList<StarCandidate>> CatalogRegistrationStarsAsync(
        MonochromeFrame frame, ushort[] pixels, CancellationToken token, string? frameSha = null)
    {
        if (frameSha is not null && await TryGetSparseRegistrationStarsAsync(frameSha, token).ConfigureAwait(false) is { } cached)
            return cached;
        var runtime = SepStarDetectionClient.ReadRuntime(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UVEX-ADV", "star-detection", "runtime.json"));
        var module = Path.Combine(Path.GetDirectoryName(runtime.WorkerPath)!, "sep_detector.py");
        var workerSha = await ComputeFileSha256Async(runtime.WorkerPath, token).ConfigureAwait(false);
        var moduleSha = await ComputeFileSha256Async(module, token).ConfigureAwait(false);
        if ((sparseSepWorkerSha is not null && !SameHash(sparseSepWorkerSha, workerSha)) ||
            (sparseSepModuleSha is not null && !SameHash(sparseSepModuleSha, moduleSha)))
            throw new PhysicalActionGateException(GateResult.Unknown("G3_SPARSE_SOURCE_CHANGED", "The frozen image extractor changed within this runner."));
        sparseSepWorkerSha = workerSha; sparseSepModuleSha = moduleSha;
        var measurements = await new SepStarDetectionClient().DetectAsync(runtime, frame.Width, frame.Height,
            pixels, frame.SaturationLevel, token).ConfigureAwait(false);
        if (!SameHash(workerSha, await ComputeFileSha256Async(runtime.WorkerPath, token).ConfigureAwait(false)) ||
            !SameHash(moduleSha, await ComputeFileSha256Async(module, token).ConfigureAwait(false)))
            throw new PhysicalActionGateException(GateResult.Unknown("G3_SPARSE_SOURCE_CHANGED", "The image extractor changed during registration measurement."));
        return CatalogFieldRegistration.ReferenceStars(measurements);
    }

    private async Task<G3CatalogRegistrationReference> CatalogReferenceAsync(ObservationContext context, G3FieldState field, CancellationToken token)
    {
        if (field.CatalogRegistrationReference is { } existing) return existing;
        if (field.Solve is not { Result.Success: true } solve || field.Frame is null || field.Image is null || field.MountBinding is null ||
            !string.Equals(Path.GetFullPath(solve.SourcePath), Path.GetFullPath(field.FramePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("G3_CATALOG_REFERENCE_MISSING: Catalogue placement requires the original solved field, not a commanded target position.");
        return new(field.FramePath, field.MountBinding.FrameSha256, context.Plan.ObservationRunId,
            phd2.Snapshot.ConnectionEpoch, field.MountBinding.PierSide, field.Frame.Width, field.Frame.Height,
            field.TargetIdentification.PredictedPoint,
            await CatalogRegistrationStarsAsync(field.Frame, field.Image.Data.FlatArray, token, field.MountBinding.FrameSha256).ConfigureAwait(false));
    }

    private async Task<CatalogFieldRegistrationResult> RegisterCatalogueFrameAsync(
        ObservationContext context, G3CatalogRegistrationReference reference, MonochromeFrame frame,
        ushort[] pixels, PixelPoint expectedTarget, SlitGeometry slit,
        Phd2SlitPlacementCommissioning preset, string freshFramePath, CancellationToken token)
    {
        if (reference.RunId != context.Plan.ObservationRunId || reference.ConnectionEpoch != phd2.Snapshot.ConnectionEpoch ||
            reference.PierSide != telescopeMediator.GetInfo().SideOfPier.ToString() ||
            reference.Width != frame.Width || reference.Height != frame.Height ||
            !SameHash(await ComputeFileSha256Async(reference.FramePath, token).ConfigureAwait(false), reference.FrameSha256))
            throw new InvalidOperationException("G3_CATALOG_REFERENCE_CHANGED: Catalogue registration reference, owner, pier side or detector changed.");
        var stars = await CatalogRegistrationStarsAsync(frame, pixels, token,
            await ComputeFileSha256Async(freshFramePath, token).ConfigureAwait(false)).ConfigureAwait(false);
        var independent = G3TargetTrackPolicy.Register(reference.Stars, stars, reference.CataloguePoint,
            expectedTarget, slit, frame.Width, frame.Height, preset.TargetSearchRadiusPixels, token);
        var target = independent.Transform?.Apply(reference.CataloguePoint) ?? reference.CataloguePoint;
        var registration = new CatalogFieldRegistrationResult(
            independent.Accepted && PixelDistance(target, expectedTarget) <= preset.GuideSearchRadiusPixels
                ? GateResult.Pass("G3_CATALOG_FIELD_REGISTERED", "Catalogue point transported by independent fit and held-out stars; not a detected target.")
                : GateResult.TargetQuality("G3_CATALOG_FIELD_REGISTRATION_UNCONFIRMED", independent.Reason),
            target, new(target.X - reference.CataloguePoint.X, target.Y - reference.CataloguePoint.Y),
            independent.MatchedStars, independent.MaximumHoldoutResidualPixels);
        await PublishRunJsonEvidenceAsync("g3-catalog-field-registration", "目录采样点由新帧参考星配准，不吸附到目标附近亮峰",
            new { reference.FramePath, reference.FrameSha256, reference.CataloguePoint, registration, independent,
                extraction = SepStarDetectionClient.Algorithm, referenceStars = reference.Stars.Count, freshStars = stars.Count,
                targetCentroidMeasured = false, targetFluxApplicable = false, commandedOffsetUsedAsMeasurement = false },
            freshFramePath, token).ConfigureAwait(false);
        if (registration.Gate.Disposition != GateDisposition.Passed)
            throw new PhysicalActionGateException(registration.Gate);
        return registration;
    }
}
