using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal static class Phd2FreshTargetFluxPolicy
{
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
        TargetIdentification freshIdentification, bool hasFormalCatalogWcsChain) =>
        hasFormalCatalogWcsChain &&
        freshIdentification.Gate.Disposition == GateDisposition.Passed &&
        freshIdentification.Gate.Code == "TARGET_IDENTIFIED_SATURATED_TOPOLOGY" &&
        freshIdentification.Authority == TargetIdentificationAuthority.BrightWingCentroid &&
        freshIdentification.Target is { } target &&
        double.IsFinite(target.Centroid.X) && double.IsFinite(target.Centroid.Y) &&
        double.IsFinite(target.FluxAdu) && target.FluxAdu > 0;
}
