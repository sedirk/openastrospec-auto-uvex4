using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal static class Phd2FreshTargetFluxPolicy
{
    internal static string? CatalogIdentityEvidence(G3FieldState? field) =>
        field is null || field.Gate.Disposition != GateDisposition.Passed ||
        field.TargetIdentification.Gate.Disposition != GateDisposition.Passed ? null :
        field.Solve is { Result.Success: true, Result.Coordinates: not null } solved &&
        !string.IsNullOrWhiteSpace(solved.EvidencePath) ? solved.EvidencePath :
        field.NeighbourCatalogIdentityEvidencePath;

    internal static TargetIdentification PreserveMeasuredHandoff(TargetIdentification prediction,
        TargetIdentification? measured, bool catalogOnly) =>
        !catalogOnly && measured is not null && HasMeasuredHandoffPosition(measured)
            // Preserve centroid, flux, authority and short-frame provenance, not
            // just a coordinate copied onto FromCatalogWcs's zero-flux placeholder.
            ? measured with
            {
                Gate = new GateResult("TARGET_CATALOG_IDENTITY_MEASURED_HANDOFF", GateDisposition.Passed,
                    "Original catalogue/WCS identity retained with the fresh measured coarse position; not a prediction or completed slit placement.",
                    measured.Gate.Metrics, measured.Gate.Severity),
            }
            : prediction;

    // Routing only: a measured stellar/core centre must not fall back to the
    // catalogue-only 20px envelope just because it is not a SEP refinement.
    // The caller still owns identity, mount binding and the live return ledger.
    internal static bool HasMeasuredHandoffPosition(TargetIdentification id) =>
        id.Gate.Disposition == GateDisposition.Passed && id.Target is { } target &&
        double.IsFinite(target.Centroid.X) && double.IsFinite(target.Centroid.Y) &&
        (id.HasCatalogPositionRefinement ||
         (double.IsFinite(target.FluxAdu) && target.FluxAdu > 0 &&
          (id.Authority is TargetIdentificationAuthority.StellarCentroid or TargetIdentificationAuthority.BrightWingCentroid ||
           id.Gate.Code == "PHD2_GUIDE_FRAME_CATALOG_TARGET_TOPOLOGY_REFINED")));

    internal static bool CanRefineCatalogPositionWithStellarCentroid(
        TargetIdentification identification, double maximumPredictionResidualPixels) =>
        identification.Gate.Disposition == GateDisposition.Passed &&
        identification.Authority == TargetIdentificationAuthority.StellarCentroid &&
        identification.Target is { } target &&
        double.IsFinite(target.Centroid.X) && double.IsFinite(target.Centroid.Y) &&
        double.IsFinite(target.FluxAdu) && target.FluxAdu > 0 &&
        double.IsFinite(identification.PredictionResidualPixels) &&
        identification.PredictionResidualPixels >= 0 &&
        double.IsFinite(maximumPredictionResidualPixels) && maximumPredictionResidualPixels > 0 &&
        identification.PredictionResidualPixels <= maximumPredictionResidualPixels;

    internal static bool UsesSaturatedTopologyFluxNotApplicable(
        TargetIdentification freshIdentification, bool hasCatalogIdentityChain) =>
        hasCatalogIdentityChain &&
        freshIdentification.Gate.Disposition == GateDisposition.Passed &&
        freshIdentification.Gate.Code == "TARGET_IDENTIFIED_SATURATED_TOPOLOGY" &&
        freshIdentification.Authority == TargetIdentificationAuthority.BrightWingCentroid &&
        freshIdentification.Target is { } target &&
        double.IsFinite(target.Centroid.X) && double.IsFinite(target.Centroid.Y) &&
        double.IsFinite(target.FluxAdu) && target.FluxAdu > 0;
}
