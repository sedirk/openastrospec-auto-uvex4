namespace UvexAdv.Observatory;

/// <summary>Resolve a catalogue-declared primary by its SIGNED companion vector,
/// not by brightness or a nearest-star guess. Caller binds the catalogue record
/// to the locked target and projects it with the same formal WCS.</summary>
public static class G3ResolvedCompanionPositionPolicy
{
    public static G3ShortPositionMeasurement Resolve(G3ShortPositionMeasurement measurement,
        PixelPoint companionMinusPrimary, double recognitionRadius, G3ShortPositionMeasurement? establishedPrimary = null)
    {
        G3ShortPositionMeasurement Unbound() => measurement with
        {
            Gate = GateResult.Unknown("G3_SHORT_CATALOG_PRIMARY_NOT_MEASURED",
                "A catalogue companion is present; this frame has not measured the same primary. Nearest-WCS selection cannot substitute its companion.", measurement.Gate.Metrics),
            Identification = measurement.Identification with { Target = null }, RequiresIndependentRepeat = true,
        };
        // SEP deblend children are not independently identified stars: a PSF
        // wing plus noise can accidentally reproduce a signed binary vector.
        // Production SEP uses exact-ID astrometry and whole parent regions.
        if (measurement.Gate.Code.StartsWith("G3_SEP_", StringComparison.Ordinal)) return Unbound();
        var expectedLength = Length(companionMinusPrimary);
        if (!double.IsFinite(expectedLength) || expectedLength < 8 || expectedLength > recognitionRadius
            || !double.IsFinite(recognitionRadius) || recognitionRadius <= 0)
            return measurement.Gate.Disposition == GateDisposition.Passed ? Unbound() : measurement;
        var candidates = measurement.ResolvedCandidates;
        var sepCandidates = measurement.Gate.Code == "G3_SEP_AMBIGUOUS" && candidates is { Count: >= 2 and <= 2000 };
        if (candidates is not { Count: 2 } && !sepCandidates)
        {
            // Only a newly measured compact PSF within the ORIGINAL repeat limit
            // of a signed-pair-established primary can bridge a faint companion.
            // A one-candidate first frame, clipped contour, or legacy shortcut has
            // no component identity. Never infer primary position by subtracting
            // the companion vector from a lone secondary measurement.
            if (measurement.Gate.Code is not ("G3_SHORT_UNSATURATED_POSITION_MEASURED" or "G3_SEP_POSITION_MEASURED")
                || measurement.ResolvedCandidates is not { Count: 1 }
                || establishedPrimary is null || !IsPrimaryBound(establishedPrimary)
                || establishedPrimary.Identification.Target is null || measurement.Identification.Target is null)
                return Unbound();
            var error = Length(new(measurement.Identification.Target.Centroid.X - establishedPrimary.Identification.Target.Centroid.X,
                measurement.Identification.Target.Centroid.Y - establishedPrimary.Identification.Target.Centroid.Y));
            if (!double.IsFinite(error) || error > G3ShortPositionMeasurementPolicy.MaximumRepeatSeparationPixels)
                return Unbound();
            var trackedGate = GateResult.Pass("G3_SHORT_CATALOG_COMPANION_PRIMARY_TRACKED",
                "A new compact PSF measures the same signed-pair-established primary within the unchanged repeat limit; original WCS and fresh hashes remain required.",measurement.Gate.Metrics);
            return measurement with { Gate=trackedGate, Identification=measurement.Identification with { Gate=trackedGate }, RequiresIndependentRepeat=true };
        }
        if (measurement.Gate.Code is not ("G3_SHORT_UNSATURATED_AMBIGUOUS" or "G3_SEP_AMBIGUOUS")) return Unbound();
        const double maximumVectorResidual = 3;
        var matches = new List<(StarCandidate Primary, double Error)>();
        for (var i = 0; i < candidates!.Count; i++)
        for (var j = 0; j < candidates.Count; j++)
        {
            if (i == j) continue;
            var primary = candidates[i]; var secondary = candidates[j];
            var error = Length(new(secondary.Centroid.X - primary.Centroid.X - companionMinusPrimary.X,
                secondary.Centroid.Y - primary.Centroid.Y - companionMinusPrimary.Y));
            if (double.IsFinite(error) && error <= maximumVectorResidual) matches.Add((primary, error));
        }
        if (matches.Count != 1) return sepCandidates ? Unbound() : measurement;
        var selected = matches[0];
        // Retain the full allowed contour spread, since the ambiguous result
        // intentionally did not carry one candidate's individual uncertainty.
        var spread = G3ShortPositionMeasurementPolicy.MaximumContourSpreadPixels + .5;
        var residual = Length(new(selected.Primary.Centroid.X - measurement.Identification.PredictedPoint.X,
            selected.Primary.Centroid.Y - measurement.Identification.PredictedPoint.Y));
        if (residual + spread > recognitionRadius) return measurement;
        var metrics = measurement.Gate.Metrics?.ToDictionary(p => p.Key, p => p.Value) ?? new();
        metrics["catalogCompanionExpectedSeparationPixels"] = expectedLength;
        metrics["catalogCompanionVectorResidualPixels"] = selected.Error;
        metrics["maximumCatalogCompanionVectorResidualPixels"] = maximumVectorResidual;
        metrics["shortPositionSpreadPixels"] = spread;
        var gate = GateResult.Pass("G3_SHORT_CATALOG_COMPANION_PRIMARY_MEASURED",
            "Exactly one ordered pair of measured components matches the signed catalogue primary-to-companion vector. Brightness was not used; independent confirmation remains mandatory.", metrics);
        return measurement with
        {
            Gate = gate, RequiresIndependentRepeat = true, PositionSpreadPixels = spread,
            Identification = measurement.Identification with
            {
                Gate = gate, Target = selected.Primary, PredictionResidualPixels = residual,
                Authority = TargetIdentificationAuthority.StellarCentroid, UniquenessRatio = double.PositiveInfinity,
            },
        };
    }

    public static bool IsPrimaryBound(G3ShortPositionMeasurement measurement) =>
        measurement.Gate.Disposition == GateDisposition.Passed && measurement.Gate.Code is
            "G3_SHORT_CATALOG_COMPANION_PRIMARY_MEASURED" or "G3_SHORT_CATALOG_COMPANION_PRIMARY_TRACKED";

    private static double Length(PixelPoint p) => Math.Sqrt(p.X * p.X + p.Y * p.Y);
}
