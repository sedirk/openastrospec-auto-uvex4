using System.IO;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed record G3CatalogRegistrationReference(string FramePath, string FrameSha256,
    string RunId, long ConnectionEpoch, string PierSide, int Width, int Height,
    PixelPoint CataloguePoint, IReadOnlyList<StarCandidate> Stars);

internal sealed partial class RealObservationStageRunner
{
    private static async Task<IReadOnlyList<StarCandidate>> CatalogRegistrationStarsAsync(
        MonochromeFrame frame, ushort[] pixels, CancellationToken token)
    {
        var runtime = SepStarDetectionClient.ReadRuntime(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UVEX-ADV", "star-detection", "runtime.json"));
        var measurements = await new SepStarDetectionClient().DetectAsync(runtime, frame.Width, frame.Height,
            pixels, frame.SaturationLevel, token).ConfigureAwait(false);
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
            await CatalogRegistrationStarsAsync(field.Frame, field.Image.Data.FlatArray, token).ConfigureAwait(false));
    }

    private async Task<CatalogFieldRegistrationResult> RegisterCatalogueFrameAsync(
        ObservationContext context, G3CatalogRegistrationReference reference, MonochromeFrame frame,
        ushort[] pixels, PixelPoint expectedTarget, SlitGeometry slit,
        Phd2SlitPlacementCommissioningPreset preset, string freshFramePath, CancellationToken token)
    {
        if (reference.RunId != context.Plan.ObservationRunId || reference.ConnectionEpoch != phd2.Snapshot.ConnectionEpoch ||
            reference.PierSide != telescopeMediator.GetInfo().SideOfPier.ToString() ||
            reference.Width != frame.Width || reference.Height != frame.Height ||
            !SameHash(await ComputeFileSha256Async(reference.FramePath, token).ConfigureAwait(false), reference.FrameSha256))
            throw new InvalidOperationException("G3_CATALOG_REFERENCE_CHANGED: Catalogue registration reference, owner, pier side or detector changed.");
        var stars = await CatalogRegistrationStarsAsync(frame, pixels, token).ConfigureAwait(false);
        var registration = CatalogFieldRegistration.Measure(reference.Stars, stars, reference.CataloguePoint,
            new(expectedTarget.X - reference.CataloguePoint.X, expectedTarget.Y - reference.CataloguePoint.Y),
            preset.GuideSearchRadiusPixels, Math.Min(2, preset.BuildMotionLimits().TargetOnSlitTolerancePixels),
            frame.Width, frame.Height, slit);
        await PublishRunJsonEvidenceAsync("g3-catalog-field-registration", "目录采样点由新帧参考星配准，不吸附到目标附近亮峰",
            new { reference.FramePath, reference.FrameSha256, reference.CataloguePoint, registration,
                extraction = SepStarDetectionClient.Algorithm, referenceStars = reference.Stars.Count, freshStars = stars.Count,
                targetCentroidMeasured = false, targetFluxApplicable = false, commandedOffsetUsedAsMeasurement = false },
            freshFramePath, token).ConfigureAwait(false);
        if (registration.Gate.Disposition != GateDisposition.Passed)
            throw new InvalidOperationException(registration.Gate.Code + ": " + registration.Gate.Message);
        return registration;
    }
}
