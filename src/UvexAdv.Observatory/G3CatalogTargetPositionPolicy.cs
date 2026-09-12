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
    public static bool CanUseShortMeasurement(MonochromeFrame frame, TargetIdentification measured) =>
        measured.Gate.Disposition == GateDisposition.Passed && measured.Target is { } star &&
        measured.Authority == TargetIdentificationAuthority.StellarCentroid &&
        double.IsFinite(star.FwhmPixels) && star.FwhmPixels > 0 &&
        double.IsFinite(star.SignalToNoise) && star.SignalToNoise > 0 &&
        double.IsFinite(star.SaturatedFraction) && star.SaturatedFraction == 0 &&
        !NeedsShortPositionCheck(frame, star.Centroid, Math.Max(3, 2 * star.FwhmPixels));

    // A long solve image can resolve the field but merge the bright target and
    // its ghosts. Never let a clipped island refine that formal WCS position.
    public static bool NeedsShortPositionCheck(MonochromeFrame frame, PixelPoint prediction, double radius)
    {
        if (!double.IsFinite(radius) || radius <= 0 ||
            !double.IsFinite(prediction.X) || !double.IsFinite(prediction.Y))
            throw new ArgumentOutOfRangeException(nameof(radius));
        var minX = (int)Math.Clamp(Math.Floor(prediction.X - radius), 0, frame.Width - 1);
        var maxX = (int)Math.Clamp(Math.Ceiling(prediction.X + radius), 0, frame.Width - 1);
        var minY = (int)Math.Clamp(Math.Floor(prediction.Y - radius), 0, frame.Height - 1);
        var maxY = (int)Math.Clamp(Math.Ceiling(prediction.Y + radius), 0, frame.Height - 1);
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
            if ((x-prediction.X)*(x-prediction.X)+(y-prediction.Y)*(y-prediction.Y) <= radius*radius &&
                frame[x,y] >= frame.SaturationLevel) return true;
        return false;
    }

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

        if (NeedsShortPositionCheck(frame, catalogProjection, recognitionRadiusPixels))
            return projected with { Gate = projected.Gate with
            {
                Message = "Formal WCS retains catalogue identity; the clipped recognition region requires a bound short-exposure position check before coarse handoff.",
            } };

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
        if (!identification.HasCatalogPositionRefinement)
            return physicalDestination;
        if (identification.Gate.Disposition != GateDisposition.Passed || identification.Target is null ||
            identification.Authority != TargetIdentificationAuthority.CatalogWcsProjection ||
            !double.IsFinite(recognitionRadiusPixels) || recognitionRadiusPixels <= 0)
            throw new InvalidOperationException("Same-frame catalogue position refinement is incomplete.");
        var dx = identification.PredictedPoint.X - identification.Target.Centroid.X;
        var dy = identification.PredictedPoint.Y - identification.Target.Centroid.Y;
        if (!double.IsFinite(dx) || !double.IsFinite(dy) ||
            !double.IsFinite(identification.CatalogPositionSpreadPixels) || identification.CatalogPositionSpreadPixels < 0 ||
            Math.Sqrt(dx * dx + dy * dy) + identification.CatalogPositionSpreadPixels > recognitionRadiusPixels)
            throw new InvalidOperationException("Measured catalogue offset exceeds its commissioned identity window.");
        // Move the observed star to the slit using the fresh WCS differential
        // mapping. Never persist this offset as a camera/optical-axis constant.
        return new PixelPoint(physicalDestination.X + dx, physicalDestination.Y + dy);
    }
}
