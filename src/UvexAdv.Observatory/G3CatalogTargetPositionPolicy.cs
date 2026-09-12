namespace UvexAdv.Observatory;

/// <summary>
/// Formal WCS owns catalogue identity; a uniquely matched position in that SAME
/// immutable detector frame can refine the coarse destination. Use the same
/// commissioned recognition window as guide takeover, not a second narrower
/// window which silently swaps a measured star for its catalogue projection.
/// Invisible/extended targets remain catalogue positions, without invented flux.
/// </summary>
public static class G3CatalogTargetPositionPolicy
{
    public static TargetIdentification Identify(
        MonochromeFrame frame, IReadOnlyList<StarCandidate> candidates,
        PixelPoint catalogProjection, bool targetMayBeInvisible,
        double recognitionRadiusPixels, double minimumSignalToNoise = 8,
        double minimumUniquenessRatio = 1.5)
    {
        var projected = TargetIdentification.FromCatalogWcs(
            catalogProjection, frame.Width, frame.Height,
            "Formal same-frame WCS establishes catalogue identity; no target flux is inferred.");
        if (targetMayBeInvisible || projected.Gate.Disposition != GateDisposition.Passed)
            return projected;

        var measured = SlitTargetIdentifier.Identify(frame, candidates, catalogProjection,
            recognitionRadiusPixels, minimumSignalToNoise, minimumUniquenessRatio);
        if (measured.Gate.Disposition != GateDisposition.Passed || measured.Target is null)
            return projected with { Gate = projected.Gate with
            {
                Message = $"{projected.Gate.Message} Local position diagnostic: {measured.Gate.Code}; no measured offset was applied.",
            } };

        return measured with
        {
            Gate = GateResult.Pass("TARGET_CATALOG_WCS_REFINED",
                $"Same-frame WCS owns identity; {measured.Gate.Code} refines detector position by {measured.PredictionResidualPixels:F2}px within the commissioned recognition window.",
                measured.Gate.Metrics),
            Authority = TargetIdentificationAuthority.CatalogWcsProjection,
            CatalogPositionRefinedFromSameFrame = true,
        };
    }

    public static PixelPoint ProjectionDestination(
        TargetIdentification identification, PixelPoint physicalDestination,
        double recognitionRadiusPixels)
    {
        if (!identification.CatalogPositionRefinedFromSameFrame)
            return physicalDestination;
        if (identification.Gate.Disposition != GateDisposition.Passed || identification.Target is null ||
            identification.Authority != TargetIdentificationAuthority.CatalogWcsProjection ||
            !double.IsFinite(recognitionRadiusPixels) || recognitionRadiusPixels <= 0)
            throw new InvalidOperationException("Same-frame catalogue position refinement is incomplete.");
        var dx = identification.PredictedPoint.X - identification.Target.Centroid.X;
        var dy = identification.PredictedPoint.Y - identification.Target.Centroid.Y;
        if (!double.IsFinite(dx) || !double.IsFinite(dy) ||
            Math.Sqrt(dx * dx + dy * dy) > recognitionRadiusPixels)
            throw new InvalidOperationException("Measured catalogue offset exceeds its commissioned identity window.");
        // Move the observed star to the slit using the fresh WCS differential
        // mapping. Never persist this offset as a camera/optical-axis constant.
        return new PixelPoint(physicalDestination.X + dx, physicalDestination.Y + dy);
    }
}
