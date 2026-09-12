namespace UvexAdv.Observatory;

public sealed record G3ShortPositionMeasurement(
    GateResult Gate, TargetIdentification Identification,
    bool RequiresIndependentRepeat, double PositionSpreadPixels,
    IReadOnlyList<StarCandidate>? ResolvedCandidates = null);

public sealed record G3ShortPositionConfirmationDecision(
    GateResult Gate, GateResult? RepeatGate, bool Accepted, bool RetryAllowed,
    bool RetainPreviousMeasurement, double PositionSpreadPixels);

/// <summary>
/// Image-only COARSE position measurement, not focus or exact slit
/// placement. Small clipped cores need connected unsaturated contour support
/// and an independent frame. Large blobs, detached islands and rings remain
/// excluded by the shared topology classifier. Limits describe image shape,
/// not an enlarged mount/lock/science tolerance.
/// </summary>
public static partial class G3ShortPositionMeasurementPolicy
{
    public const int MaximumFrames = 3;
    public const double MaximumContourSpreadPixels = 3;
    public const double MaximumRepeatSeparationPixels = 4;
    public const string Version = "compact-short-contours-v6-component-lineage";

    public static G3ShortPositionMeasurement Measure(MonochromeFrame frame,
        PixelPoint prediction, double recognitionRadius, double minimumSnr = 8,
        double minimumUniqueness = 1.5, bool requireResolvedPsfCandidates = false)
    {
        var candidates = StarFieldDetector.Detect(frame);
        var identified = SlitTargetIdentifier.Identify(frame, candidates,
            prediction, recognitionRadius, minimumSnr, minimumUniqueness);
        if (!G3CatalogTargetPositionPolicy.NeedsShortPositionCheck(frame, prediction, recognitionRadius))
        {
            if (!requireResolvedPsfCandidates && G3CatalogTargetPositionPolicy.CanUseShortMeasurement(frame, identified))
                return new(identified.Gate, identified, false, 0);
            // An unsaturated image must not fall through to the clipped-core
            // requirement. Local halo maxima are not independent stellar PSFs.
            return MeasureUnsaturated(frame, candidates, identified, recognitionRadius, minimumSnr);
        }

        var metrics = new Dictionary<string, double>
        {
            ["maximumShortContourSpreadPixels"] = MaximumContourSpreadPixels,
            ["maximumShortCoreDiameterPixels"] = 16,
        };
        G3ShortPositionMeasurement Reject(string code, string reason,
            IReadOnlyDictionary<string, double>? extra = null)
        {
            if (extra is not null && !ReferenceEquals(extra, metrics)) foreach (var item in extra) metrics[item.Key] = item.Value;
            return new(GateResult.Unknown(code, reason, metrics), identified, true, 0);
        }
        var topology = SaturatedTargetGhostTopologyAnalyzer.Analyze(frame, prediction, recognitionRadius);
        metrics["shortSolidCoreCount"] = topology.Candidates.Count(c => c.Topology == SaturatedSourceTopology.SolidStellarCore);
        if (topology.Gate.Disposition != GateDisposition.Passed || topology.Target is not { } core ||
            topology.Candidates.Count(c => c.Topology == SaturatedSourceTopology.SolidStellarCore) != 1)
            return Reject("G3_SHORT_CORE_NOT_UNIQUE", $"A unique filled core is required: {topology.Gate.Code}.");
        var diameter = Math.Max(core.BoundingWidthPixels, core.BoundingHeightPixels);
        metrics["saturatedCorePixels"] = core.SaturatedPixels;
        metrics["shortCoreWidthPixels"] = core.BoundingWidthPixels;
        metrics["shortCoreHeightPixels"] = core.BoundingHeightPixels;
        if (diameter > 16 || core.SaturatedPixels > 256)
            return Reject("G3_SHORT_CORE_TOO_LARGE", "The clipped short-frame region is not a compact core.", core.Gate.Metrics);

        var radius = Math.Max(16, 3 * diameter);
        if (core.Source.EdgeDistancePixels <= radius || topology.Candidates.Any(other =>
                !ReferenceEquals(core, other) && Distance(core.Centroid, other.Centroid) <= radius + other.ExclusionRadiusPixels))
            return Reject("G3_SHORT_CORE_NOT_ISOLATED", "The short core is edge-truncated or overlaps another measured saturated feature.");
        var cx = (int)Math.Round(core.Centroid.X);
        var cy = (int)Math.Round(core.Centroid.Y);
        if (frame[cx, cy] < frame.SaturationLevel)
            return Reject("G3_SHORT_CORE_HOLLOW", "The core centre is not filled; a ring or fragmented feature cannot provide position.");

        var contours = new List<Contour>();
        foreach (var fraction in new[] { .35, .50, .70 })
        {
            var threshold = topology.BackgroundAdu + fraction * (frame.SaturationLevel - topology.BackgroundAdu);
            var points = ConnectedContour(frame, cx, cy, radius, threshold);
            if (points.Count == 0 || points.Any(p => Math.Abs(p.X - cx) == radius || Math.Abs(p.Y - cy) == radius))
                return Reject("G3_SHORT_CONTOUR_TRUNCATED", "The bright contour reaches its measurement boundary.");
            var center = new PixelPoint(points.Average(p => p.X), points.Average(p => p.Y));
            var width = points.Max(p => p.X) - points.Min(p => p.X) + 1;
            var height = points.Max(p => p.Y) - points.Min(p => p.Y) + 1;
            metrics["shortContourAspectRatio"] = (double)Math.Max(width, height) / Math.Min(width, height);
            if (Math.Max(width, height) > 2.5 * Math.Min(width, height))
                return Reject("G3_SHORT_CONTOUR_BLENDED", "The high-signal connected contour is elongated.");
            contours.Add(new(center, points, Math.Max(width, height)));
        }
        // The clipped plateau shrinks with flux/seeing and is not a PSF ruler.
        // Compare the outer contour with the measured inner contour instead;
        // both come from connected, above-background, unsaturated thresholds.
        var maximumExtent = 2.5 * contours[^1].Extent;
        metrics["shortOuterContourExtentPixels"] = contours[0].Extent;
        metrics["shortInnerContourExtentPixels"] = contours[^1].Extent;
        metrics["maximumShortOuterContourExtentPixels"] = maximumExtent;
        if (contours.Any(c => c.Extent > maximumExtent))
            return Reject("G3_SHORT_CONTOUR_BLENDED", "The outer connected contour is extended relative to the measured inner contour.");
        // Position is derived from connected contour geometry, never from the
        // clipped ADU values as if they were measured stellar flux. Several
        // levels expose asymmetric inner wings rather than trusting one core.
        var centers = contours.Select(c => c.Center).ToArray();
        var spread = centers.Max(a => centers.Max(b => Distance(a, b)));
        // Keep the plateau offset as a diagnostic, not a second, falsely precise
        // positional reference. Its flat top contains no unsaturated flux data.
        metrics["shortCoreContourOffsetPixels"] = centers.Max(c => Distance(c, core.Centroid));
        var wings = contours[0].Points.Where(p => frame[(int)p.X, (int)p.Y] < frame.SaturationLevel).ToArray();
        var sectors = wings.Select(p => Math.Clamp((int)((Math.Atan2(p.Y - core.Centroid.Y,
            p.X - core.Centroid.X) + Math.PI) / (2 * Math.PI) * 8), 0, 7)).Distinct().Count();
        var flux = wings.Sum(p => frame[(int)p.X, (int)p.Y] - topology.BackgroundAdu);
        var snr = flux / Math.Sqrt(Math.Max(1, flux + wings.Length * Math.Pow(topology.BackgroundSigmaAdu, 2)));
        metrics["shortWingPixels"] = wings.Length;
        metrics["shortContourSpreadPixels"] = spread;
        metrics["shortWingSectors"] = sectors;
        metrics["shortWingSnr"] = snr;
        if (wings.Length < 12 || sectors < 6 || !double.IsFinite(snr) || snr < minimumSnr)
            return Reject("G3_SHORT_WINGS_INCOMPLETE", "Insufficient unsaturated inner-wing coverage or signal supports the compact core.", metrics);
        if (spread > MaximumContourSpreadPixels)
            return Reject("G3_SHORT_CONTOURS_DISAGREE", "The unsaturated contour positions disagree; no single position was promoted.");
        var measuredCenter = new PixelPoint(contours.Average(c => c.Center.X), contours.Average(c => c.Center.Y));
        var positionSpread = spread + .5; // observed shape spread + pixel sampling, NOT a calibrated sigma
        if (Distance(measuredCenter, prediction) + positionSpread > recognitionRadius)
            return Reject("G3_SHORT_POSITION_OUTSIDE", "The measured position and its spread leave the commissioned recognition window.", metrics);
        metrics["shortPositionSpreadPixels"] = positionSpread;
        var gate = GateResult.Pass("G3_SHORT_COMPACT_POSITION_MEASURED",
            "Compact clipped core has unsaturated contour support; independent repeat is still required. Coarse position only, not exact placement or focus.", metrics);
        return new(gate, identified with
        {
            Gate = gate, Target = core.Source with { Centroid = measuredCenter, FluxAdu = flux, SignalToNoise = snr },
            Authority = TargetIdentificationAuthority.BrightWingCentroid,
            PredictionResidualPixels = Distance(measuredCenter, prediction),
        }, true, positionSpread);
    }

    public static GateResult ConfirmRepeat(G3ShortPositionMeasurement first, G3ShortPositionMeasurement second,
        string firstHash, string secondHash)
    {
        if (first.Gate.Disposition != GateDisposition.Passed || second.Gate.Disposition != GateDisposition.Passed ||
            first.Identification.Target is null || second.Identification.Target is null)
            return GateResult.Unknown("G3_SHORT_REPEAT_UNMEASURED", "Both independent short frames must have valid positions.");
        if (firstHash.Length != 64 || secondHash.Length != 64 || !firstHash.All(Uri.IsHexDigit) || !secondHash.All(Uri.IsHexDigit) ||
            string.Equals(firstHash, secondHash, StringComparison.OrdinalIgnoreCase))
            return GateResult.Unknown("G3_SHORT_FRAME_REUSED", "Repeated or invalid image hashes cannot establish independent confirmation.");
        var separation = Distance(first.Identification.Target.Centroid, second.Identification.Target.Centroid);
        var metrics = new Dictionary<string, double> { ["shortRepeatSeparationPixels"] = separation,
            ["maximumShortRepeatSeparationPixels"] = MaximumRepeatSeparationPixels };
        return double.IsFinite(separation) && separation <= MaximumRepeatSeparationPixels
            ? GateResult.Pass("G3_SHORT_REPEAT_CONFIRMED", "Independent compact positions agree for coarse handoff; fresh fine residual remains mandatory.", metrics)
            : GateResult.Unknown("G3_SHORT_REPEAT_DISAGREES", "Independent short-frame positions disagree; do not move from an uncertain centre.", metrics);
    }

    /// <summary>
    /// Shared by production and tests. A rejected image consumes a frame but is
    /// never retained as a position. Two good frames may straddle one rejected
    /// image, only while production revalidates the same immutable WCS binding.
    /// Device/identity/hash/safety failures are NOT retryable image conditions.
    /// </summary>
    public static G3ShortPositionConfirmationDecision EvaluateConfirmation(
        G3ShortPositionMeasurement? previous, G3ShortPositionMeasurement current,
        string? previousHash, string currentHash, int attempt, double recognitionRadius)
    {
        var measured = current.Gate.Disposition == GateDisposition.Passed && current.Identification.Target is not null;
        var repeat = previous is null ? null : ConfirmRepeat(previous, current, previousHash ?? "", currentHash);
        var spread = Math.Max(current.PositionSpreadPixels, previous?.PositionSpreadPixels ?? 0);
        if (repeat?.Metrics?.TryGetValue("shortRepeatSeparationPixels", out var separation) == true)
            spread = Math.Max(spread, separation);
        var gate = !measured ? current.Gate : repeat ?? (current.RequiresIndependentRepeat
            ? GateResult.Unknown("G3_SHORT_REPEAT_REQUIRED", "A second independent valid short position is required.")
            : current.Gate);
        if (attempt is < 1 or > MaximumFrames || currentHash.Length != 64 || !currentHash.All(Uri.IsHexDigit))
            gate = GateResult.Unknown("G3_SHORT_CONFIRMATION_INVALID", "Invalid frame index or hash; no further capture is authorized by this image policy.");
        else if (measured && (!double.IsFinite(spread) || spread < 0 ||
            !double.IsFinite(recognitionRadius) || recognitionRadius <= 0 ||
            !double.IsFinite(current.Identification.PredictionResidualPixels) ||
            current.Identification.PredictionResidualPixels + spread > recognitionRadius))
            gate = GateResult.Unknown("G3_SHORT_POSITION_OUTSIDE", "The measured centre plus its retained position spread leaves the commissioned recognition window.");
        var metrics = new Dictionary<string, double>();
        if (current.Gate.Metrics is not null) foreach (var item in current.Gate.Metrics) metrics[item.Key] = item.Value;
        if (repeat?.Metrics is not null) foreach (var item in repeat.Metrics) metrics[item.Key] = item.Value;
        metrics["shortPositionFrames"] = attempt;
        metrics["maximumShortPositionFrames"] = MaximumFrames;
        metrics["shortPositionSpreadPixels"] = spread;
        gate = gate with { Metrics = metrics };
        var accepted = measured && gate.Disposition == GateDisposition.Passed;
        var retry = !accepted && attempt is >= 1 and < MaximumFrames && gate.Code is
            "G3_SHORT_REPEAT_REQUIRED" or "G3_SHORT_REPEAT_DISAGREES" or
            "G3_SHORT_CONTOUR_TRUNCATED" or "G3_SHORT_CONTOUR_BLENDED" or
            "G3_SHORT_WINGS_INCOMPLETE" or "G3_SHORT_CONTOURS_DISAGREE" or "G3_SHORT_CORE_TOO_LARGE" or
            "G3_SHORT_UNSATURATED_UNMEASURED" or "G3_SHORT_CATALOG_PRIMARY_NOT_MEASURED" or
            "G3_SEP_UNMEASURED" or "G3_SEP_AMBIGUOUS" or "G3_SEP_CATALOG_PRIMARY_UNMEASURED";
        // A disagreeing frame must never replace a catalogue-established primary
        // with its companion. Keep the original anchor within this 3-frame window.
        var retainCatalogPrimary = previous is not null && G3ResolvedCompanionPositionPolicy.IsPrimaryBound(previous);
        return new(gate, repeat, accepted, retry, !measured || (!accepted && retainCatalogPrimary), spread);
    }

    private static List<PixelPoint> ConnectedContour(MonochromeFrame frame, int cx, int cy, int radius, double threshold)
    {
        var side = 2 * radius + 1;
        var seen = new bool[side * side];
        var pending = new Queue<(int X, int Y)>();
        pending.Enqueue((cx, cy));
        seen[radius * side + radius] = true;
        var points = new List<PixelPoint>();
        while (pending.TryDequeue(out var p))
        {
            if (frame[p.X, p.Y] < threshold) continue;
            points.Add(new(p.X, p.Y));
            for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
            {
                var x = p.X + dx; var y = p.Y + dy;
                if (Math.Abs(x - cx) > radius || Math.Abs(y - cy) > radius) continue;
                var index = (y - cy + radius) * side + x - cx + radius;
                if (seen[index]) continue;
                seen[index] = true;
                pending.Enqueue((x, y));
            }
        }
        return points;
    }

    private static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    private sealed record Contour(PixelPoint Center, List<PixelPoint> Points, double Extent);
}
