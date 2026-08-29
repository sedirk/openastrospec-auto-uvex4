namespace UvexAdv.Observatory;

/// <summary>
/// Configuration for locating one deliberately saturated target from its
/// unsaturated PSF wings. These are image-domain quality limits; no sky
/// coordinate, target name, optical-axis displacement, or camera exposure is
/// embedded in the algorithm.
/// </summary>
public sealed record BrightTargetCentroidOptions(
    int MinimumSaturatedCorePixels = 3,
    int MaximumSaturatedCorePixels = 20_000,
    int WingRadiusPixels = 24,
    double MinimumWingProminenceSigma = 6,
    double MaximumWingLevelFraction = 0.92,
    int MinimumWingPixels = 48,
    double MinimumWingSignalToNoise = 20,
    double MinimumAngularCoverageFraction = 0.75,
    double MinimumOpposedWingBalance = 0.35,
    double MaximumWingCentroidDisagreementPixels = 1.5,
    int EdgeMarginPixels = 30,
    double NearbySaturatedCoreRadiusPixels = 48,
    double MinimumUniquenessRatio = 1.8,
    double MaximumSecondaryPeakRatio = 0.35);

public sealed record BrightTargetWingCandidate(
    GateResult Gate,
    PixelPoint Centroid,
    PixelPoint SaturatedCoreCenter,
    int SaturatedCorePixels,
    int WingPixels,
    double WingFluxAdu,
    double WingSignalToNoise,
    double AngularCoverageFraction,
    double OpposedWingBalance,
    double WingCentroidDisagreementPixels,
    double EdgeDistancePixels,
    double NearestOtherSaturatedCorePixels,
    double SecondaryPeakRatio,
    SaturatedSourceTopology SaturatedTopology,
    double CentralSaturationFraction,
    double AnnularSaturationFraction,
    double CentralToAnnularSignalRatio,
    double SaturatedBoundingBoxFillFraction);

/// <summary>
/// The result is intentionally ineligible for focus. A saturated-core frame
/// cannot establish FWHM and must never replace independent C11/Gemini focus
/// evidence, even when its wing centroid is precise enough for slit placement.
/// </summary>
public sealed record BrightTargetCentroidAnalysis(
    GateResult Gate,
    BrightTargetWingCandidate? Target,
    IReadOnlyList<BrightTargetWingCandidate> Candidates,
    double BackgroundAdu,
    double BackgroundSigmaAdu,
    double UniquenessRatio,
    bool FocusEligible = false);

public static class BrightTargetWingCentroidAnalyzer
{
    private const int AngularSectors = 8;

    public static BrightTargetCentroidAnalysis Analyze(
        MonochromeFrame frame,
        BrightTargetCentroidOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        options ??= new BrightTargetCentroidOptions();
        Validate(options);

        var (background, sigma) = EstimateBackground(frame);
        // The wing estimator must not call every connected saturated feature a
        // stellar core.  In particular, a hollow optical ghost can have bright,
        // symmetric unsaturated wings and otherwise win the flux ranking.  Run
        // the existing topology classifier over the complete detector first;
        // catalogue/QHY evidence remains the separate identity authority.
        var detectorCenter = new PixelPoint((frame.Width - 1) / 2d, (frame.Height - 1) / 2d);
        var detectorRadius = Math.Sqrt(frame.Width * (double)frame.Width + frame.Height * (double)frame.Height);
        var topology = SaturatedTargetGhostTopologyAnalyzer.Analyze(
            frame,
            detectorCenter,
            detectorRadius,
            new SaturatedTargetGhostTopologyOptions(
                MinimumComponentPixels: 1,
                MaximumComponentPixels: checked(frame.Width * frame.Height)));
        var cores = topology.Candidates
            .Select(candidate => new SaturatedCore(candidate.SaturatedPixels, candidate.Centroid, candidate))
            .ToArray();
        if (cores.Length == 0)
        {
            return Failure(
                "BRIGHT_TARGET_SATURATED_CORE_NOT_FOUND",
                "The explicit bright-target branch requires one saturated core; none was detected.",
                background,
                sigma);
        }

        var candidates = cores
            .Select((core, index) => MeasureCandidate(frame, core, index, cores, background, sigma, options))
            .ToArray();
        var eligible = candidates
            .Where(candidate => candidate.Gate.Disposition == GateDisposition.Passed)
            .OrderByDescending(candidate => candidate.WingFluxAdu)
            .ToArray();
        if (eligible.Length == 0)
        {
            var reasons = string.Join(" ", candidates.Select(candidate =>
                $"{candidate.Gate.Code}: {candidate.Gate.Message}"));
            var onlyAnnularGhosts = candidates.All(candidate =>
                candidate.SaturatedTopology == SaturatedSourceTopology.AnnularGhost);
            var noProvenSolidCore = candidates.All(candidate =>
                candidate.SaturatedTopology != SaturatedSourceTopology.SolidStellarCore);
            return new BrightTargetCentroidAnalysis(
                GateResult.Unknown(
                    onlyAnnularGhosts
                        ? "BRIGHT_TARGET_ONLY_ANNULAR_GHOSTS"
                        : noProvenSolidCore
                            ? "BRIGHT_TARGET_TOPOLOGY_UNPROVEN"
                            : "BRIGHT_TARGET_WINGS_UNUSABLE",
                    onlyAnnularGhosts
                        ? $"Every saturated feature is a hollow annular ghost; none may enter bright-target wing ranking. {reasons}"
                        : noProvenSolidCore
                            ? $"No saturated feature has proven filled-stellar-core topology; an indeterminate feature cannot become the automatic target. {reasons}"
                            : $"Saturated cores were present, but no candidate had complete, isolated unsaturated wings. {reasons}",
                    Metrics(candidates.Length, 0, 0, background, sigma)),
                null,
                candidates,
                background,
                sigma,
                0,
                FocusEligible: false);
        }

        var best = eligible[0];
        var uniqueness = eligible.Length == 1
            ? double.PositiveInfinity
            : best.WingFluxAdu / Math.Max(1, eligible[1].WingFluxAdu);
        if (uniqueness < options.MinimumUniquenessRatio)
        {
            return new BrightTargetCentroidAnalysis(
                GateResult.Unknown(
                    "BRIGHT_TARGET_AMBIGUOUS",
                    $"Two isolated saturated sources have similar wing flux; uniqueness {uniqueness:F2} is below {options.MinimumUniquenessRatio:F2}.",
                    Metrics(candidates.Length, eligible.Length, uniqueness, background, sigma)),
                best,
                candidates,
                background,
                sigma,
                uniqueness,
                FocusEligible: false);
        }

        return new BrightTargetCentroidAnalysis(
            GateResult.Pass(
                "BRIGHT_TARGET_WING_CENTROID_VALID",
                $"One unique saturated target has a quality-gated unsaturated-wing centroid at ({best.Centroid.X:F2}, {best.Centroid.Y:F2}) px. This frame is excluded from focus analysis.",
                Metrics(candidates.Length, eligible.Length, uniqueness, background, sigma, best)),
            best,
            candidates,
            background,
            sigma,
            uniqueness,
            FocusEligible: false);
    }

    public static IReadOnlyList<string> ValidateOptions(BrightTargetCentroidOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            Validate(options);
            return Array.Empty<string>();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return new[] { $"{ex.ParamName ?? "bright-target option"} is outside its valid range." };
        }
    }

    private static BrightTargetWingCandidate MeasureCandidate(
        MonochromeFrame frame,
        SaturatedCore core,
        int coreIndex,
        IReadOnlyList<SaturatedCore> cores,
        double background,
        double sigma,
        BrightTargetCentroidOptions options)
    {
        var coreCenter = core.Center;
        var edgeDistance = Math.Min(
            Math.Min(coreCenter.X, frame.Width - 1 - coreCenter.X),
            Math.Min(coreCenter.Y, frame.Height - 1 - coreCenter.Y));
        var nearestCore = cores
            .Where((_, index) => index != coreIndex)
            .Select(other => Distance(coreCenter, other.Center))
            .DefaultIfEmpty(double.PositiveInfinity)
            .Min();

        var minimumWing = background + options.MinimumWingProminenceSigma * sigma;
        var maximumWing = frame.SaturationLevel * options.MaximumWingLevelFraction;
        var radiusSquared = options.WingRadiusPixels * options.WingRadiusPixels;
        var innerRadiusSquared = radiusSquared * 0.36;
        var centerX = (int)Math.Round(coreCenter.X);
        var centerY = (int)Math.Round(coreCenter.Y);
        var pixels = new List<WingPixel>();
        var sectorFlux = new double[AngularSectors];
        for (var y = Math.Max(0, centerY - options.WingRadiusPixels);
             y <= Math.Min(frame.Height - 1, centerY + options.WingRadiusPixels);
             y++)
        for (var x = Math.Max(0, centerX - options.WingRadiusPixels);
             x <= Math.Min(frame.Width - 1, centerX + options.WingRadiusPixels);
             x++)
        {
            var dx = x - coreCenter.X;
            var dy = y - coreCenter.Y;
            var distanceSquared = dx * dx + dy * dy;
            if (distanceSquared > radiusSquared) continue;
            var raw = frame[x, y];
            if (raw >= frame.SaturationLevel || raw < minimumWing || raw > maximumWing) continue;
            var flux = raw - background;
            if (flux <= 0) continue;
            var angle = Math.Atan2(dy, dx);
            var sector = (int)Math.Floor((angle + Math.PI) / (2 * Math.PI) * AngularSectors);
            sector = Math.Clamp(sector, 0, AngularSectors - 1);
            pixels.Add(new WingPixel(x, y, flux, distanceSquared <= innerRadiusSquared));
            sectorFlux[sector] += flux;
        }

        var wingFlux = pixels.Sum(pixel => pixel.Flux);
        var wingSnr = wingFlux / Math.Sqrt(Math.Max(1, wingFlux + pixels.Count * sigma * sigma));
        var centroid = WeightedCentroid(pixels, coreCenter);
        var innerCentroid = WeightedCentroid(pixels.Where(pixel => pixel.Inner), centroid);
        var outerCentroid = WeightedCentroid(pixels.Where(pixel => !pixel.Inner), centroid);
        var disagreement = Distance(innerCentroid, outerCentroid);
        var occupied = sectorFlux.Count(flux => flux > 0);
        var coverage = occupied / (double)AngularSectors;
        var opposedNumerator = 0d;
        var opposedDenominator = 0d;
        for (var sector = 0; sector < AngularSectors / 2; sector++)
        {
            var opposite = sector + AngularSectors / 2;
            opposedNumerator += Math.Min(sectorFlux[sector], sectorFlux[opposite]);
            opposedDenominator += Math.Max(sectorFlux[sector], sectorFlux[opposite]);
        }
        var opposedBalance = opposedDenominator > 0 ? opposedNumerator / opposedDenominator : 0;
        var secondaryPeakRatio = FindSecondaryPeakRatio(
            frame,
            centroid,
            options.WingRadiusPixels,
            background,
            sigma);

        GateResult gate;
        var metrics = CandidateMetrics(
            core.PixelCount,
            pixels.Count,
            wingFlux,
            wingSnr,
            coverage,
            opposedBalance,
            disagreement,
            edgeDistance,
            nearestCore,
            secondaryPeakRatio);
        if (core.Topology.Topology == SaturatedSourceTopology.AnnularGhost)
        {
            gate = GateResult.Fail(
                "BRIGHT_TARGET_ANNULAR_GHOST_REJECTED",
                "The connected saturated feature has a hollow annular topology and is excluded before wing-flux ranking.",
                metrics);
        }
        else if (core.Topology.Topology != SaturatedSourceTopology.SolidStellarCore)
        {
            gate = GateResult.Unknown(
                "BRIGHT_TARGET_TOPOLOGY_INDETERMINATE",
                "The connected saturated feature is neither a proven filled stellar core nor a proven annular ghost; it cannot become the automatic bright target.",
                metrics);
        }
        else if (core.PixelCount < options.MinimumSaturatedCorePixels ||
            core.PixelCount > options.MaximumSaturatedCorePixels)
        {
            gate = GateResult.Fail(
                "BRIGHT_TARGET_CORE_SIZE_INVALID",
                $"Saturated core size {core.PixelCount} px is outside [{options.MinimumSaturatedCorePixels}, {options.MaximumSaturatedCorePixels}] px.",
                metrics);
        }
        else if (edgeDistance < options.EdgeMarginPixels)
        {
            gate = GateResult.Fail(
                "BRIGHT_TARGET_EDGE_TRUNCATED",
                $"Candidate is only {edgeDistance:F1} px from an edge; complete wings cannot be established.",
                metrics);
        }
        else if (nearestCore < options.NearbySaturatedCoreRadiusPixels)
        {
            gate = GateResult.Fail(
                "BRIGHT_TARGET_SATURATED_NEIGHBOR",
                $"Another saturated core is only {nearestCore:F1} px away; the target centroid is blended or ambiguous.",
                metrics);
        }
        else if (pixels.Count < options.MinimumWingPixels || wingSnr < options.MinimumWingSignalToNoise)
        {
            gate = GateResult.Unknown(
                "BRIGHT_TARGET_WINGS_TOO_WEAK",
                $"Only {pixels.Count} wing pixels at SNR {wingSnr:F1} are usable.",
                metrics);
        }
        else if (coverage < options.MinimumAngularCoverageFraction ||
                 opposedBalance < options.MinimumOpposedWingBalance)
        {
            gate = GateResult.Unknown(
                "BRIGHT_TARGET_WINGS_INCOMPLETE",
                $"Wing angular coverage {coverage:P0} / opposed balance {opposedBalance:F2} does not prove an isolated symmetric source.",
                metrics);
        }
        else if (disagreement > options.MaximumWingCentroidDisagreementPixels)
        {
            gate = GateResult.Unknown(
                "BRIGHT_TARGET_WING_CENTROIDS_DISAGREE",
                $"Inner and outer unsaturated-wing centroids differ by {disagreement:F2} px.",
                metrics);
        }
        else if (secondaryPeakRatio > options.MaximumSecondaryPeakRatio)
        {
            gate = GateResult.Unknown(
                "BRIGHT_TARGET_HIGH_SIGNAL_RIVAL",
                $"A separate coherent peak reaches {secondaryPeakRatio:P1} of saturation, above the configured {options.MaximumSecondaryPeakRatio:P1} rival/ghost limit.",
                metrics);
        }
        else
        {
            gate = GateResult.Pass(
                "BRIGHT_TARGET_CANDIDATE_VALID",
                "The saturated core is isolated and its unsaturated wings pass coverage, symmetry, SNR, edge, neighbor, and rival-source gates.",
                metrics);
        }

        return new BrightTargetWingCandidate(
            gate,
            centroid,
            coreCenter,
            core.PixelCount,
            pixels.Count,
            wingFlux,
            wingSnr,
            coverage,
            opposedBalance,
            disagreement,
            edgeDistance,
            nearestCore,
            secondaryPeakRatio,
            core.Topology.Topology,
            core.Topology.CentralSaturationFraction,
            core.Topology.AnnularSaturationFraction,
            core.Topology.CentralToAnnularSignalRatio,
            core.Topology.BoundingBoxFillFraction);
    }

    private static double FindSecondaryPeakRatio(
        MonochromeFrame frame,
        PixelPoint primary,
        double exclusionRadius,
        double background,
        double sigma)
    {
        var exclusionSquared = exclusionRadius * exclusionRadius;
        var minimumCoreMedian = background + 6 * sigma;
        ushort best = 0;
        for (var y = 1; y < frame.Height - 1; y++)
        for (var x = 1; x < frame.Width - 1; x++)
        {
            var dx = x - primary.X;
            var dy = y - primary.Y;
            if (dx * dx + dy * dy <= exclusionSquared) continue;
            var peak = frame[x, y];
            if (peak <= best || peak >= frame.SaturationLevel || !IsLocalMaximum(frame, x, y, peak)) continue;
            var core = new ushort[9];
            var index = 0;
            for (var iy = y - 1; iy <= y + 1; iy++)
            for (var ix = x - 1; ix <= x + 1; ix++)
                core[index++] = frame[ix, iy];
            Array.Sort(core);
            if (core[4] >= minimumCoreMedian) best = peak;
        }
        var denominator = Math.Max(1, frame.SaturationLevel - background);
        return Math.Clamp((best - background) / denominator, 0, 1);
    }

    private static bool IsLocalMaximum(MonochromeFrame frame, int x, int y, ushort value)
    {
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            if (dx == 0 && dy == 0) continue;
            if (frame[x + dx, y + dy] > value) return false;
        }
        return true;
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

    private static PixelPoint WeightedCentroid(IEnumerable<WingPixel> source, PixelPoint fallback)
    {
        var pixels = source.ToArray();
        var total = pixels.Sum(pixel => pixel.Flux);
        return total > 0
            ? new PixelPoint(
                pixels.Sum(pixel => pixel.X * pixel.Flux) / total,
                pixels.Sum(pixel => pixel.Y * pixel.Flux) / total)
            : fallback;
    }

    private static IReadOnlyDictionary<string, double> CandidateMetrics(
        int corePixels,
        int wingPixels,
        double wingFlux,
        double wingSnr,
        double coverage,
        double opposedBalance,
        double disagreement,
        double edgeDistance,
        double nearestCore,
        double secondaryPeakRatio) => new Dictionary<string, double>
    {
        ["saturatedCorePixels"] = corePixels,
        ["wingPixels"] = wingPixels,
        ["wingFluxAdu"] = Finite(wingFlux),
        ["wingSignalToNoise"] = Finite(wingSnr),
        ["angularCoverageFraction"] = Finite(coverage),
        ["opposedWingBalance"] = Finite(opposedBalance),
        ["wingCentroidDisagreementPixels"] = Finite(disagreement),
        ["edgeDistancePixels"] = Finite(edgeDistance),
        ["nearestOtherSaturatedCorePixels"] = FiniteOrLarge(nearestCore),
        ["secondaryPeakRatio"] = Finite(secondaryPeakRatio),
    };

    private static IReadOnlyDictionary<string, double> Metrics(
        int coreCandidates,
        int eligibleCandidates,
        double uniqueness,
        double background,
        double sigma,
        BrightTargetWingCandidate? best = null) => new Dictionary<string, double>
    {
        ["saturatedCoreCandidates"] = coreCandidates,
        ["eligibleCandidates"] = eligibleCandidates,
        ["uniquenessRatio"] = FiniteOrLarge(uniqueness),
        ["backgroundAdu"] = Finite(background),
        ["backgroundSigmaAdu"] = Finite(sigma),
        ["centroidX"] = best is null ? 0 : Finite(best.Centroid.X),
        ["centroidY"] = best is null ? 0 : Finite(best.Centroid.Y),
        ["wingSignalToNoise"] = best is null ? 0 : Finite(best.WingSignalToNoise),
    };

    private static BrightTargetCentroidAnalysis Failure(
        string code,
        string message,
        double background,
        double sigma) => new(
        GateResult.Fail(code, message, Metrics(0, 0, 0, background, sigma)),
        null,
        Array.Empty<BrightTargetWingCandidate>(),
        background,
        sigma,
        0,
        FocusEligible: false);

    private static void Validate(BrightTargetCentroidOptions options)
    {
        if (options.MinimumSaturatedCorePixels < 1) throw new ArgumentOutOfRangeException(nameof(options.MinimumSaturatedCorePixels));
        if (options.MaximumSaturatedCorePixels < options.MinimumSaturatedCorePixels) throw new ArgumentOutOfRangeException(nameof(options.MaximumSaturatedCorePixels));
        if (options.WingRadiusPixels < 4) throw new ArgumentOutOfRangeException(nameof(options.WingRadiusPixels));
        RequirePositive(options.MinimumWingProminenceSigma, nameof(options.MinimumWingProminenceSigma));
        RequireFraction(options.MaximumWingLevelFraction, nameof(options.MaximumWingLevelFraction));
        if (options.MinimumWingPixels < 8) throw new ArgumentOutOfRangeException(nameof(options.MinimumWingPixels));
        RequirePositive(options.MinimumWingSignalToNoise, nameof(options.MinimumWingSignalToNoise));
        RequireFraction(options.MinimumAngularCoverageFraction, nameof(options.MinimumAngularCoverageFraction));
        RequireFraction(options.MinimumOpposedWingBalance, nameof(options.MinimumOpposedWingBalance));
        RequirePositive(options.MaximumWingCentroidDisagreementPixels, nameof(options.MaximumWingCentroidDisagreementPixels));
        if (options.EdgeMarginPixels < options.WingRadiusPixels) throw new ArgumentOutOfRangeException(nameof(options.EdgeMarginPixels));
        RequirePositive(options.NearbySaturatedCoreRadiusPixels, nameof(options.NearbySaturatedCoreRadiusPixels));
        if (!double.IsFinite(options.MinimumUniquenessRatio) || options.MinimumUniquenessRatio <= 1) throw new ArgumentOutOfRangeException(nameof(options.MinimumUniquenessRatio));
        RequireFraction(options.MaximumSecondaryPeakRatio, nameof(options.MaximumSecondaryPeakRatio));
    }

    private static void RequirePositive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(name);
    }

    private static void RequireFraction(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0 || value >= 1) throw new ArgumentOutOfRangeException(name);
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        var position = Math.Clamp(percentile, 0, 1) * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return lower == upper ? sorted[lower] : sorted[lower] + (position - lower) * (sorted[upper] - sorted[lower]);
    }

    private static double Distance(PixelPoint left, PixelPoint right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;
    private static double FiniteOrLarge(double value) => double.IsFinite(value) ? value : double.MaxValue;

    private sealed record SaturatedCore(
        int PixelCount,
        PixelPoint Center,
        SaturatedSourceTopologyCandidate Topology);
    private sealed record WingPixel(int X, int Y, double Flux, bool Inner);
}

public sealed record BrightTargetAuthorityOptions(
    TimeSpan MaximumQhyWcsAge,
    TimeSpan MaximumG3FrameAge,
    double MaximumQhyTargetResidualArcseconds,
    double MaximumCatalogCoordinateMismatchArcseconds,
    double MinimumC11FocusConfidence);

public sealed record BrightTargetAuthorityEvidence(
    bool Enabled,
    string ObservationRunId,
    EquatorialTarget CatalogTarget,
    string QhyObservationRunId,
    string QhyRequestedTarget,
    double? QhyRequestedRightAscensionDegrees,
    double? QhyRequestedDeclinationDegrees,
    string QhyCoordinateEpoch,
    string QhyAcceptedFrameSha256,
    DateTimeOffset QhyFrameCompletedUtc,
    bool QhyWcsSucceeded,
    double QhyWcsRequestedRightAscensionDegrees,
    double QhyWcsRequestedDeclinationDegrees,
    double QhyWcsResidualArcseconds,
    string QhyWcsEvidenceSha256,
    string C11FocusEvidenceSha256,
    FocusMetricKind C11FocusMetricKind,
    string C11FocusSourceCameraStableId,
    string ExpectedG3SourceCameraStableId,
    double C11FocusMetricValue,
    DateTimeOffset C11FocusVerifiedUtc,
    DateTimeOffset? C11FocusValidUntilUtc,
    double C11FocusConfidence,
    int C11LockedPositionSteps,
    int C11CurrentPositionSteps,
    string G3FrameSha256,
    DateTimeOffset G3FrameCompletedUtc,
    int G3ExposureMilliseconds,
    int ConfiguredMinimumG3ExposureMilliseconds,
    bool G3FrameUsedForFocus,
    DateTimeOffset EvaluatedUtc);

/// <summary>
/// Pure fail-closed evidence gate for the exceptional bright-target branch.
/// Image morphology alone never establishes target identity.
/// </summary>
public static class BrightTargetAuthorityGate
{
    public static GateResult Evaluate(
        BrightTargetAuthorityEvidence evidence,
        BrightTargetAuthorityOptions options)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        var failures = new List<string>();

        if (!evidence.Enabled) failures.Add("the branch is not explicitly enabled");
        if (string.IsNullOrWhiteSpace(evidence.ObservationRunId)) failures.Add("observation run ID is missing");
        if (!string.Equals(evidence.ObservationRunId, evidence.QhyObservationRunId, StringComparison.Ordinal)) failures.Add("QHY evidence belongs to another run");
        if (string.IsNullOrWhiteSpace(evidence.CatalogTarget.Name) || string.IsNullOrWhiteSpace(evidence.CatalogTarget.CatalogId)) failures.Add("the catalog target name/ID is incomplete");
        if (!string.Equals(evidence.QhyRequestedTarget, evidence.CatalogTarget.Name, StringComparison.Ordinal)) failures.Add("QHY requested-target name differs from the catalog target");
        if (!string.Equals(evidence.QhyCoordinateEpoch, "ICRS", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(evidence.QhyCoordinateEpoch, "J2000", StringComparison.OrdinalIgnoreCase)) failures.Add("QHY target coordinate epoch is not ICRS/J2000");
        if (evidence.QhyRequestedRightAscensionDegrees is not { } qhyRa ||
            evidence.QhyRequestedDeclinationDegrees is not { } qhyDec ||
            !CoordinatesValid(qhyRa, qhyDec) ||
            AngularSeparationArcseconds(qhyRa, qhyDec, evidence.CatalogTarget.RightAscensionDegrees, evidence.CatalogTarget.DeclinationDegrees) > options.MaximumCatalogCoordinateMismatchArcseconds)
            failures.Add("QHY requested coordinates do not match the catalog target");
        if (!CoordinatesValid(evidence.QhyWcsRequestedRightAscensionDegrees, evidence.QhyWcsRequestedDeclinationDegrees) ||
            AngularSeparationArcseconds(
                evidence.QhyWcsRequestedRightAscensionDegrees,
                evidence.QhyWcsRequestedDeclinationDegrees,
                evidence.CatalogTarget.RightAscensionDegrees,
                evidence.CatalogTarget.DeclinationDegrees) > options.MaximumCatalogCoordinateMismatchArcseconds)
            failures.Add("the WCS request was not bound to the catalog target");
        if (!evidence.QhyWcsSucceeded || !double.IsFinite(evidence.QhyWcsResidualArcseconds) || evidence.QhyWcsResidualArcseconds < 0 ||
            evidence.QhyWcsResidualArcseconds > options.MaximumQhyTargetResidualArcseconds)
            failures.Add("QHY WCS did not pass its configured target-residual limit");
        if (!IsSha256(evidence.QhyAcceptedFrameSha256)) failures.Add("accepted QHY FITS SHA-256 is invalid");
        if (!IsSha256(evidence.QhyWcsEvidenceSha256)) failures.Add("QHY WCS evidence SHA-256 is invalid");
        if (!Fresh(evidence.QhyFrameCompletedUtc, evidence.EvaluatedUtc, options.MaximumQhyWcsAge)) failures.Add("QHY WCS/frame evidence is stale or future-dated");

        if (!IsSha256(evidence.C11FocusEvidenceSha256)) failures.Add("independent C11 focus evidence SHA-256 is invalid");
        if (evidence.C11FocusMetricKind != FocusMetricKind.G3StellarShape) failures.Add("independent C11 focus evidence is not a G3 stellar-shape metric");
        if (string.IsNullOrWhiteSpace(evidence.C11FocusSourceCameraStableId) ||
            !string.Equals(evidence.C11FocusSourceCameraStableId, evidence.ExpectedG3SourceCameraStableId, StringComparison.OrdinalIgnoreCase))
            failures.Add("C11 focus source-camera identity does not match the locked G3 camera");
        if (!double.IsFinite(evidence.C11FocusMetricValue) || evidence.C11FocusMetricValue <= 0)
            failures.Add("independent C11 focus metric value is not finite and positive");
        if (evidence.EvaluatedUtc < evidence.C11FocusVerifiedUtc)
            failures.Add("independent C11 focus evidence is future-dated");
        if (!double.IsFinite(evidence.C11FocusConfidence) || evidence.C11FocusConfidence < options.MinimumC11FocusConfidence || evidence.C11FocusConfidence > 1)
            failures.Add("independent C11 focus confidence is below the configured limit");
        if (evidence.C11LockedPositionSteps < 0 || evidence.C11CurrentPositionSteps != evidence.C11LockedPositionSteps)
            failures.Add("current Star Focuser Pro position differs from the independently verified C11 position");

        if (!IsSha256(evidence.G3FrameSha256)) failures.Add("short-exposure G3 FITS SHA-256 is invalid");
        if (!Fresh(evidence.G3FrameCompletedUtc, evidence.EvaluatedUtc, options.MaximumG3FrameAge)) failures.Add("short-exposure G3 frame is stale or future-dated");
        if (evidence.ConfiguredMinimumG3ExposureMilliseconds <= 0 || evidence.G3ExposureMilliseconds != evidence.ConfiguredMinimumG3ExposureMilliseconds)
            failures.Add("G3 frame was not captured at the explicitly configured minimum exposure");
        if (evidence.G3FrameUsedForFocus) failures.Add("the saturated G3 frame was marked as focus evidence");

        var metrics = new Dictionary<string, double>
        {
            ["qhyWcsAgeSeconds"] = Math.Max(0, (evidence.EvaluatedUtc - evidence.QhyFrameCompletedUtc).TotalSeconds),
            ["qhyWcsResidualArcseconds"] = double.IsFinite(evidence.QhyWcsResidualArcseconds) ? evidence.QhyWcsResidualArcseconds : 0,
            ["c11FocusConfidence"] = double.IsFinite(evidence.C11FocusConfidence) ? evidence.C11FocusConfidence : 0,
            ["g3FrameAgeSeconds"] = Math.Max(0, (evidence.EvaluatedUtc - evidence.G3FrameCompletedUtc).TotalSeconds),
            ["g3ExposureMilliseconds"] = evidence.G3ExposureMilliseconds,
            ["focusEligible"] = evidence.G3FrameUsedForFocus ? 1 : 0,
        };
        return failures.Count == 0
            ? GateResult.Pass(
                "BRIGHT_TARGET_AUTHORITY_VALID",
                "Fresh run-bound QHY WCS, catalog target, independent C11/Gemini focus evidence, and exact minimum-exposure G3 evidence authorize wing-centroid slit placement. The G3 frame remains excluded from focus.",
                metrics)
            : GateResult.Unknown(
                "BRIGHT_TARGET_AUTHORITY_WITHHELD",
                $"Bright-target wing centroiding is not authorized: {string.Join("; ", failures)}.",
                metrics);
    }

    private static void Validate(BrightTargetAuthorityOptions options)
    {
        if (options.MaximumQhyWcsAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options.MaximumQhyWcsAge));
        if (options.MaximumG3FrameAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options.MaximumG3FrameAge));
        if (!double.IsFinite(options.MaximumQhyTargetResidualArcseconds) || options.MaximumQhyTargetResidualArcseconds <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaximumQhyTargetResidualArcseconds));
        if (!double.IsFinite(options.MaximumCatalogCoordinateMismatchArcseconds) || options.MaximumCatalogCoordinateMismatchArcseconds <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaximumCatalogCoordinateMismatchArcseconds));
        if (!double.IsFinite(options.MinimumC11FocusConfidence) || options.MinimumC11FocusConfidence <= 0 || options.MinimumC11FocusConfidence > 1) throw new ArgumentOutOfRangeException(nameof(options.MinimumC11FocusConfidence));
    }

    private static bool Fresh(DateTimeOffset timestamp, DateTimeOffset evaluatedUtc, TimeSpan maximumAge) =>
        timestamp != default && timestamp <= evaluatedUtc.AddSeconds(5) && evaluatedUtc - timestamp <= maximumAge;

    private static bool IsSha256(string value)
    {
        var normalized = (value ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).Trim();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit);
    }

    private static bool CoordinatesValid(double ra, double dec) =>
        double.IsFinite(ra) && ra >= 0 && ra < 360 && double.IsFinite(dec) && dec >= -90 && dec <= 90;

    private static double AngularSeparationArcseconds(double ra1, double dec1, double ra2, double dec2)
    {
        if (!CoordinatesValid(ra1, dec1) || !CoordinatesValid(ra2, dec2)) return double.PositiveInfinity;
        var ra1Radians = ra1 * Math.PI / 180;
        var ra2Radians = ra2 * Math.PI / 180;
        var dec1Radians = dec1 * Math.PI / 180;
        var dec2Radians = dec2 * Math.PI / 180;
        var cosine = Math.Sin(dec1Radians) * Math.Sin(dec2Radians) +
                     Math.Cos(dec1Radians) * Math.Cos(dec2Radians) * Math.Cos(ra1Radians - ra2Radians);
        return Math.Acos(Math.Clamp(cosine, -1, 1)) * 180 / Math.PI * 3600;
    }
}
