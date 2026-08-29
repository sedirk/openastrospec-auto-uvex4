namespace UvexAdv.Observatory;

/// <summary>
/// Image-domain policy for separating a filled saturated stellar core from an
/// annular optical ghost.  Catalogue/WCS evidence remains the identity
/// authority; this analyzer only refines detector position and labels topology.
/// </summary>
public sealed record SaturatedTargetGhostTopologyOptions(
    int MinimumComponentPixels = 3,
    int MaximumComponentPixels = 50_000,
    double MinimumSolidCentralSaturationFraction = 0.55,
    double MinimumSolidBoundingBoxFillFraction = 0.12,
    double MaximumGhostCentralSaturationFraction = 0.15,
    double MinimumGhostAnnularSaturationFraction = 0.35,
    double MaximumGhostCentralToAnnularSignalRatio = 0.80,
    double MinimumTargetUniquenessRatio = 1.20);

public enum SaturatedSourceTopology
{
    SolidStellarCore,
    AnnularGhost,
    Indeterminate,
}

public sealed record SaturatedSourceTopologyCandidate(
    GateResult Gate,
    SaturatedSourceTopology Topology,
    PixelPoint Centroid,
    StarCandidate Source,
    int SaturatedPixels,
    int BoundingWidthPixels,
    int BoundingHeightPixels,
    double BoundingBoxFillFraction,
    double CentralSaturationFraction,
    double AnnularSaturationFraction,
    double CentralToAnnularSignalRatio,
    double DistanceToPredictionPixels,
    double SelectionScore,
    double ExclusionRadiusPixels);

public sealed record SaturatedTargetGhostTopologyAnalysis(
    GateResult Gate,
    SaturatedSourceTopologyCandidate? Target,
    IReadOnlyList<SaturatedSourceTopologyCandidate> Ghosts,
    IReadOnlyList<SaturatedSourceTopologyCandidate> Candidates,
    double UniquenessRatio,
    double BackgroundAdu,
    double BackgroundSigmaAdu);

public static class SaturatedTargetGhostTopologyAnalyzer
{
    public static SaturatedTargetGhostTopologyAnalysis Analyze(
        MonochromeFrame frame,
        PixelPoint predictedPoint,
        double maximumPredictionResidualPixels,
        SaturatedTargetGhostTopologyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(predictedPoint);
        options ??= new SaturatedTargetGhostTopologyOptions();
        Validate(predictedPoint, maximumPredictionResidualPixels, options);

        var (background, sigma) = EstimateBackground(frame);
        var components = FindSaturatedComponents(frame);
        var measured = components
            .Where(component => component.Pixels.Count >= options.MinimumComponentPixels &&
                                component.Pixels.Count <= options.MaximumComponentPixels)
            .Select(component => Measure(
                frame,
                component,
                predictedPoint,
                maximumPredictionResidualPixels,
                background,
                sigma,
                options))
            .Where(candidate => candidate.DistanceToPredictionPixels <= maximumPredictionResidualPixels)
            .ToArray();
        var ghosts = measured
            .Where(candidate => candidate.Topology == SaturatedSourceTopology.AnnularGhost)
            .OrderBy(candidate => candidate.DistanceToPredictionPixels)
            .ToArray();
        var solid = measured
            .Where(candidate => candidate.Topology == SaturatedSourceTopology.SolidStellarCore)
            .OrderByDescending(candidate => candidate.SelectionScore)
            .ToArray();
        var uniqueness = solid.Length < 2
            ? double.PositiveInfinity
            : solid[0].SelectionScore / Math.Max(1e-9, solid[1].SelectionScore);

        var metrics = SummaryMetrics(measured, ghosts, solid, uniqueness, background, sigma);
        if (solid.Length == 0)
        {
            var code = ghosts.Length > 0
                ? "SATURATED_TARGET_ONLY_ANNULAR_GHOSTS"
                : "SATURATED_TARGET_TOPOLOGY_NOT_APPLICABLE";
            var message = ghosts.Length > 0
                ? $"Found {ghosts.Length} hollow annular saturated feature(s), but no filled stellar core within {maximumPredictionResidualPixels:F1}px of the WCS prediction."
                : $"No filled saturated stellar core was found within {maximumPredictionResidualPixels:F1}px of the WCS prediction.";
            return new SaturatedTargetGhostTopologyAnalysis(
                GateResult.Unknown(code, message, metrics),
                null,
                ghosts,
                measured,
                0,
                background,
                sigma);
        }

        var target = solid[0];
        if (solid.Length > 1 && uniqueness < options.MinimumTargetUniquenessRatio)
        {
            return new SaturatedTargetGhostTopologyAnalysis(
                GateResult.Unknown(
                    "SATURATED_TARGET_SOLID_CORES_AMBIGUOUS",
                    $"Two filled saturated stellar cores remain plausible after rejecting {ghosts.Length} annular ghost(s); score uniqueness {uniqueness:F2} is below {options.MinimumTargetUniquenessRatio:F2}.",
                    metrics),
                target,
                ghosts,
                measured,
                uniqueness,
                background,
                sigma);
        }

        return new SaturatedTargetGhostTopologyAnalysis(
            GateResult.Pass(
                "SATURATED_TARGET_SOLID_CORE_IDENTIFIED",
                $"A filled saturated stellar core was identified at ({target.Centroid.X:F2}, {target.Centroid.Y:F2}) px after rejecting {ghosts.Length} hollow annular ghost(s); WCS residual {target.DistanceToPredictionPixels:F2}px.",
                metrics),
            target,
            ghosts,
            measured,
            uniqueness,
            background,
            sigma);
    }

    private static SaturatedSourceTopologyCandidate Measure(
        MonochromeFrame frame,
        SaturatedComponent component,
        PixelPoint predictedPoint,
        double maximumPredictionResidualPixels,
        double background,
        double sigma,
        SaturatedTargetGhostTopologyOptions options)
    {
        var center = component.Center;
        var width = component.MaximumX - component.MinimumX + 1;
        var height = component.MaximumY - component.MinimumY + 1;
        var minorExtent = Math.Min(width, height);
        var majorExtent = Math.Max(width, height);
        var centralRadius = Math.Max(2, minorExtent * 0.10);
        var annulusInner = Math.Max(centralRadius * 1.5, majorExtent * 0.22);
        var annulusOuter = Math.Max(annulusInner + 1, majorExtent * 0.48);
        var centerValues = new List<double>();
        var annulusValues = new List<double>();
        var centerSaturated = 0;
        var annulusSaturated = 0;
        var minimumX = Math.Max(0, (int)Math.Floor(center.X - annulusOuter));
        var maximumX = Math.Min(frame.Width - 1, (int)Math.Ceiling(center.X + annulusOuter));
        var minimumY = Math.Max(0, (int)Math.Floor(center.Y - annulusOuter));
        var maximumY = Math.Min(frame.Height - 1, (int)Math.Ceiling(center.Y + annulusOuter));
        for (var y = minimumY; y <= maximumY; y++)
        for (var x = minimumX; x <= maximumX; x++)
        {
            var distance = Distance(new PixelPoint(x, y), center);
            var value = frame[x, y];
            if (distance <= centralRadius)
            {
                centerValues.Add(value);
                if (value >= frame.SaturationLevel) centerSaturated++;
            }
            else if (distance >= annulusInner && distance <= annulusOuter)
            {
                annulusValues.Add(value);
                if (value >= frame.SaturationLevel) annulusSaturated++;
            }
        }

        centerValues.Sort();
        annulusValues.Sort();
        var centerSaturationFraction = centerSaturated / (double)Math.Max(1, centerValues.Count);
        var annularSaturationFraction = annulusSaturated / (double)Math.Max(1, annulusValues.Count);
        var centerSignal = Math.Max(0, Percentile(centerValues, 0.5) - background);
        var annularSignal = Math.Max(1, Percentile(annulusValues, 0.9) - background);
        var centerToAnnularSignalRatio = centerSignal / annularSignal;
        var fillFraction = component.Pixels.Count / (double)Math.Max(1, width * height);
        var distanceToPrediction = Distance(center, predictedPoint);
        var isGhost = centerSaturationFraction <= options.MaximumGhostCentralSaturationFraction &&
                      annularSaturationFraction >= options.MinimumGhostAnnularSaturationFraction &&
                      centerToAnnularSignalRatio <= options.MaximumGhostCentralToAnnularSignalRatio;
        var isSolid = !isGhost &&
                      centerSaturationFraction >= options.MinimumSolidCentralSaturationFraction &&
                      fillFraction >= options.MinimumSolidBoundingBoxFillFraction;
        var topology = isGhost
            ? SaturatedSourceTopology.AnnularGhost
            : isSolid
                ? SaturatedSourceTopology.SolidStellarCore
                : SaturatedSourceTopology.Indeterminate;
        var positionalWeight = 1 / (1 + distanceToPrediction / Math.Max(1, maximumPredictionResidualPixels * 0.25));
        var topologyQuality = 0.50 * centerSaturationFraction +
                              0.30 * Math.Clamp(fillFraction, 0, 1) +
                              0.20 * Math.Clamp(centerToAnnularSignalRatio, 0, 1);
        var selectionScore = topology == SaturatedSourceTopology.SolidStellarCore
            ? positionalWeight * topologyQuality
            : 0;
        var edgeDistance = Math.Min(
            Math.Min(center.X, frame.Width - 1 - center.X),
            Math.Min(center.Y, frame.Height - 1 - center.Y));
        var flux = component.Pixels.Count * Math.Max(1, frame.SaturationLevel - background);
        var snr = flux / Math.Sqrt(Math.Max(1, flux + component.Pixels.Count * sigma * sigma));
        var source = new StarCandidate(
            center,
            frame.SaturationLevel,
            flux,
            snr,
            Math.Max(1, 0.5 * (width + height)),
            majorExtent > 0 ? 1 - minorExtent / (double)majorExtent : 0,
            fillFraction,
            edgeDistance);
        var candidateMetrics = new Dictionary<string, double>
        {
            ["saturatedPixels"] = component.Pixels.Count,
            ["boundingWidthPixels"] = width,
            ["boundingHeightPixels"] = height,
            ["boundingBoxFillFraction"] = fillFraction,
            ["centralSaturationFraction"] = centerSaturationFraction,
            ["annularSaturationFraction"] = annularSaturationFraction,
            ["centralToAnnularSignalRatio"] = centerToAnnularSignalRatio,
            ["predictionResidualPixels"] = distanceToPrediction,
            ["selectionScore"] = selectionScore,
        };
        var gate = topology switch
        {
            SaturatedSourceTopology.SolidStellarCore => GateResult.Pass(
                "SATURATED_SOURCE_SOLID_STELLAR_CORE",
                "The connected saturated source has a filled central core rather than a hollow annular topology.",
                candidateMetrics),
            SaturatedSourceTopology.AnnularGhost => GateResult.Warn(
                "SATURATED_SOURCE_ANNULAR_GHOST",
                "The connected saturated source has a dark center and bright annulus and is excluded from target-centroid competition.",
                candidateMetrics),
            _ => GateResult.Unknown(
                "SATURATED_SOURCE_TOPOLOGY_INDETERMINATE",
                "The saturated component is neither a proven filled stellar core nor a proven hollow annular ghost.",
                candidateMetrics),
        };
        return new SaturatedSourceTopologyCandidate(
            gate,
            topology,
            center,
            source,
            component.Pixels.Count,
            width,
            height,
            fillFraction,
            centerSaturationFraction,
            annularSaturationFraction,
            centerToAnnularSignalRatio,
            distanceToPrediction,
            selectionScore,
            Math.Max(width, height) * 0.60);
    }

    private static IReadOnlyDictionary<string, double> SummaryMetrics(
        IReadOnlyList<SaturatedSourceTopologyCandidate> measured,
        IReadOnlyList<SaturatedSourceTopologyCandidate> ghosts,
        IReadOnlyList<SaturatedSourceTopologyCandidate> solid,
        double uniqueness,
        double background,
        double sigma) => new Dictionary<string, double>
    {
        ["saturatedComponents"] = measured.Count,
        ["solidStellarCoreCandidates"] = solid.Count,
        ["annularGhostCandidates"] = ghosts.Count,
        ["targetUniquenessRatio"] = double.IsFinite(uniqueness) ? uniqueness : double.MaxValue,
        ["backgroundAdu"] = background,
        ["backgroundSigmaAdu"] = sigma,
        ["targetX"] = solid.Count > 0 ? solid[0].Centroid.X : 0,
        ["targetY"] = solid.Count > 0 ? solid[0].Centroid.Y : 0,
        ["targetPredictionResidualPixels"] = solid.Count > 0 ? solid[0].DistanceToPredictionPixels : 0,
    };

    private static IReadOnlyList<SaturatedComponent> FindSaturatedComponents(MonochromeFrame frame)
    {
        var visited = new bool[checked(frame.Width * frame.Height)];
        var result = new List<SaturatedComponent>();
        for (var y = 0; y < frame.Height; y++)
        for (var x = 0; x < frame.Width; x++)
        {
            var offset = y * frame.Width + x;
            if (visited[offset] || frame[x, y] < frame.SaturationLevel) continue;
            var queue = new Queue<(int X, int Y)>();
            var pixels = new List<(int X, int Y)>();
            visited[offset] = true;
            queue.Enqueue((x, y));
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                pixels.Add(current);
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var nextX = current.X + dx;
                    var nextY = current.Y + dy;
                    if (nextX < 0 || nextY < 0 || nextX >= frame.Width || nextY >= frame.Height) continue;
                    var nextOffset = nextY * frame.Width + nextX;
                    if (visited[nextOffset] || frame[nextX, nextY] < frame.SaturationLevel) continue;
                    visited[nextOffset] = true;
                    queue.Enqueue((nextX, nextY));
                }
            }

            result.Add(new SaturatedComponent(
                pixels,
                new PixelPoint(pixels.Average(pixel => pixel.X), pixels.Average(pixel => pixel.Y)),
                pixels.Min(pixel => pixel.X),
                pixels.Max(pixel => pixel.X),
                pixels.Min(pixel => pixel.Y),
                pixels.Max(pixel => pixel.Y)));
        }
        return result;
    }

    private static (double Background, double Sigma) EstimateBackground(MonochromeFrame frame)
    {
        var sample = new List<double>(Math.Max(1024, frame.Width * frame.Height / 64));
        for (var y = 0; y < frame.Height; y += 8)
        for (var x = 0; x < frame.Width; x += 8)
            if (frame[x, y] < frame.SaturationLevel) sample.Add(frame[x, y]);
        sample.Sort();
        if (sample.Count == 0) return (0, 1);
        var background = Percentile(sample, 0.5);
        var deviations = sample.Select(value => Math.Abs(value - background)).OrderBy(value => value).ToArray();
        return (background, Math.Max(1, Percentile(deviations, 0.5) * 1.4826));
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0;
        var position = Math.Clamp(percentile, 0, 1) * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return lower == upper
            ? sorted[lower]
            : sorted[lower] + (position - lower) * (sorted[upper] - sorted[lower]);
    }

    private static void Validate(
        PixelPoint predictedPoint,
        double maximumPredictionResidualPixels,
        SaturatedTargetGhostTopologyOptions options)
    {
        if (!double.IsFinite(predictedPoint.X) || !double.IsFinite(predictedPoint.Y)) throw new ArgumentOutOfRangeException(nameof(predictedPoint));
        if (!double.IsFinite(maximumPredictionResidualPixels) || maximumPredictionResidualPixels <= 0) throw new ArgumentOutOfRangeException(nameof(maximumPredictionResidualPixels));
        if (options.MinimumComponentPixels < 1) throw new ArgumentOutOfRangeException(nameof(options.MinimumComponentPixels));
        if (options.MaximumComponentPixels < options.MinimumComponentPixels) throw new ArgumentOutOfRangeException(nameof(options.MaximumComponentPixels));
        RequireFraction(options.MinimumSolidCentralSaturationFraction, nameof(options.MinimumSolidCentralSaturationFraction));
        RequireFraction(options.MinimumSolidBoundingBoxFillFraction, nameof(options.MinimumSolidBoundingBoxFillFraction));
        RequireFraction(options.MaximumGhostCentralSaturationFraction, nameof(options.MaximumGhostCentralSaturationFraction));
        RequireFraction(options.MinimumGhostAnnularSaturationFraction, nameof(options.MinimumGhostAnnularSaturationFraction));
        RequireFraction(options.MaximumGhostCentralToAnnularSignalRatio, nameof(options.MaximumGhostCentralToAnnularSignalRatio));
        if (!double.IsFinite(options.MinimumTargetUniquenessRatio) || options.MinimumTargetUniquenessRatio <= 1) throw new ArgumentOutOfRangeException(nameof(options.MinimumTargetUniquenessRatio));
    }

    private static void RequireFraction(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0 || value > 1) throw new ArgumentOutOfRangeException(name);
    }

    private static double Distance(PixelPoint left, PixelPoint right)
    {
        var deltaX = left.X - right.X;
        var deltaY = left.Y - right.Y;
        return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
    }

    private sealed record SaturatedComponent(
        IReadOnlyList<(int X, int Y)> Pixels,
        PixelPoint Center,
        int MinimumX,
        int MaximumX,
        int MinimumY,
        int MaximumY);
}
