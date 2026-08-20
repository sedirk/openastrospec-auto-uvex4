namespace UvexAdv.Observatory;

/// <summary>
/// Pure image-analysis policy for the G3 slit-field camera.  The resulting metric belongs to the
/// C11/Gemini main-focus domain only; this type deliberately has no device or motion dependency.
/// </summary>
public sealed record G3StellarFocusAnalysisOptions(
    double DetectionSigma = 3.5,
    int CentroidRadiusPixels = 9,
    int EdgeMarginPixels = 14,
    int MaximumCandidates = 300,
    int MinimumStars = 3,
    int PreferredStars = 8,
    double MinimumSignalToNoise = 6,
    double MaximumPerStarSaturatedFraction = 0.04,
    double MaximumSaturatedStarFraction = 0.25,
    // G3 is sampled at about 0.384 arcsec/pixel on the current C11 train. A
    // 10 px ceiling is already deliberately tolerant of roughly 3.8 arcsec
    // seeing/tracking blur, while rejecting the 12.3-12.9 px annular/comatic
    // stars measured in the 2026-08-17 commissioning frames.
    double MaximumMedianFwhmPixels = 10,
    double MaximumMedianEllipticity = 0.78,
    double MaximumRelativeFwhmMad = 0.45,
    double MinimumConfidence = 0.42,
    double MinimumSeparationPixels = 7,
    // A local maximum in read noise can accumulate a deceptively high aperture SNR when every
    // positive noise sample in the 19x19 measurement box is summed.  A stellar source must also
    // have a spatially coherent core: the median of its central 3x3 pixels must stand above the
    // robust frame background by this many sigma.
    double MinimumCoreMedianProminenceSigma = 4.0);

public sealed record G3StellarFocusMeasurement(
    GateResult Gate,
    double MedianFwhmPixels,
    double MedianEllipticity,
    int StarCount,
    int DetectedStarCount,
    double SaturatedStarFraction,
    double MedianSignalToNoise,
    double RelativeFwhmMad,
    double Confidence,
    IReadOnlyList<StarCandidate> Stars);

public static class G3StellarFocusAnalyzer
{
    public static G3StellarFocusMeasurement Analyze(
        MonochromeFrame frame,
        G3StellarFocusAnalysisOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        options ??= new G3StellarFocusAnalysisOptions();
        Validate(options);

        // Detection is intentionally permissive about shape and saturation.  The aggregate gate below
        // must see and report poor stars instead of allowing the detector to silently discard them.
        var rawDetected = StarFieldDetector.Detect(
                frame,
                new StarDetectionOptions(
                    options.DetectionSigma,
                    options.CentroidRadiusPixels,
                    options.EdgeMarginPixels,
                    options.MaximumCandidates,
                    MaximumEllipticity: 0.98,
                    MaximumSaturatedFraction: 1))
            .Where(IsFiniteCandidate)
            .OrderByDescending(star => star.SignalToNoise)
            .ToArray();

        var (background, sigma) = EstimateBackgroundAndSigma(frame);
        var detected = rawDetected
            .Where(star => HasCoherentCore(
                frame,
                star,
                background,
                sigma,
                options.MinimumCoreMedianProminenceSigma))
            .ToArray();

        var separated = SuppressDuplicatePeaks(detected, options.MinimumSeparationPixels);
        if (separated.Count == 0)
        {
            return Empty(
                GateResult.Unknown(
                    "G3_FOCUS_STARS_NOT_DETECTED",
                    rawDetected.Length == 0
                        ? "No finite stellar candidates were detected in the G3 frame."
                        : $"The G3 frame contained {rawDetected.Length} local maxima, but none had a spatially coherent stellar core."));
        }

        var saturatedStarFraction = separated.Count(star => star.SaturatedFraction > 0) / (double)separated.Count;
        var usable = separated
            .Where(star => star.SignalToNoise >= options.MinimumSignalToNoise
                           && star.FwhmPixels > 0
                           && star.SaturatedFraction <= options.MaximumPerStarSaturatedFraction)
            .ToArray();

        if (usable.Length == 0)
        {
            return Empty(
                GateResult.Unknown(
                    "G3_FOCUS_STARS_UNUSABLE",
                    "G3 candidates were detected, but none has finite unsaturated shape and SNR suitable for focus analysis.",
                    Metrics(0, 0, 0, separated.Count, saturatedStarFraction, 0, 0, 0)),
                separated.Count,
                saturatedStarFraction);
        }

        // Median/MAD aggregation keeps one blend, hot-pixel remnant, or cosmic-ray-shaped candidate from
        // steering the telescope focus metric.  The clipping floor is deliberately generous for the broad,
        // asymmetric stars currently seen on the C11 focal plane.
        var initialFwhm = Median(usable.Select(star => star.FwhmPixels));
        var initialMad = Median(usable.Select(star => Math.Abs(star.FwhmPixels - initialFwhm)));
        var clippingHalfWidth = Math.Max(initialFwhm * 0.60, 4.5 * 1.4826 * initialMad);
        var robust = usable
            .Where(star => Math.Abs(star.FwhmPixels - initialFwhm) <= clippingHalfWidth)
            .ToArray();
        if (robust.Length == 0) robust = usable;

        var medianFwhm = Median(robust.Select(star => star.FwhmPixels));
        var medianEllipticity = Median(robust.Select(star => star.Ellipticity));
        var medianSnr = Median(robust.Select(star => star.SignalToNoise));
        var fwhmMad = Median(robust.Select(star => Math.Abs(star.FwhmPixels - medianFwhm)));
        var relativeFwhmMad = medianFwhm > 0 ? 1.4826 * fwhmMad / medianFwhm : 1;
        var confidence = CalculateConfidence(
            robust.Length,
            medianSnr,
            medianFwhm,
            medianEllipticity,
            relativeFwhmMad,
            saturatedStarFraction,
            options);

        var metrics = Metrics(
            medianFwhm,
            medianEllipticity,
            robust.Length,
            separated.Count,
            saturatedStarFraction,
            medianSnr,
            relativeFwhmMad,
            confidence);
        GateResult gate;
        if (saturatedStarFraction > options.MaximumSaturatedStarFraction)
        {
            gate = GateResult.Fail(
                "G3_FOCUS_SATURATED",
                $"{saturatedStarFraction:P1} of detected G3 stars contain saturated pixels; focus motion must not use this frame.",
                metrics);
        }
        else if (robust.Length < options.MinimumStars)
        {
            gate = GateResult.Unknown(
                "G3_FOCUS_STARS_INSUFFICIENT",
                $"Only {robust.Length} usable G3 stars remain; at least {options.MinimumStars} are required.",
                metrics);
        }
        else if (medianFwhm > options.MaximumMedianFwhmPixels)
        {
            gate = GateResult.Unknown(
                "G3_FOCUS_STARS_TOO_BROAD",
                $"Median G3 FWHM {medianFwhm:F2} px exceeds the commissioned analysis ceiling {options.MaximumMedianFwhmPixels:F2} px.",
                metrics);
        }
        else if (medianEllipticity > options.MaximumMedianEllipticity)
        {
            gate = GateResult.Unknown(
                "G3_FOCUS_STARS_TOO_ELONGATED",
                $"Median G3 ellipticity {medianEllipticity:F3} exceeds {options.MaximumMedianEllipticity:F3}.",
                metrics);
        }
        else if (relativeFwhmMad > options.MaximumRelativeFwhmMad)
        {
            gate = GateResult.Unknown(
                "G3_FOCUS_SHAPE_INCONSISTENT",
                $"Robust relative FWHM scatter {relativeFwhmMad:P1} exceeds {options.MaximumRelativeFwhmMad:P1}.",
                metrics);
        }
        else if (confidence < options.MinimumConfidence)
        {
            gate = GateResult.Unknown(
                "G3_FOCUS_CONFIDENCE_LOW",
                $"G3 stellar-focus confidence {confidence:F3} is below {options.MinimumConfidence:F3}.",
                metrics);
        }
        else
        {
            gate = GateResult.Pass(
                "G3_FOCUS_METRIC_VALID",
                $"G3 main-focus metric uses {robust.Length} stars: median FWHM {medianFwhm:F2} px, ellipticity {medianEllipticity:F3}.",
                metrics);
        }

        return new G3StellarFocusMeasurement(
            gate,
            medianFwhm,
            medianEllipticity,
            robust.Length,
            separated.Count,
            saturatedStarFraction,
            medianSnr,
            relativeFwhmMad,
            confidence,
            robust);
    }

    private static IReadOnlyList<StarCandidate> SuppressDuplicatePeaks(
        IReadOnlyList<StarCandidate> ordered,
        double minimumSeparationPixels)
    {
        var result = new List<StarCandidate>(ordered.Count);
        var minimumDistanceSquared = minimumSeparationPixels * minimumSeparationPixels;
        foreach (var candidate in ordered)
        {
            var duplicate = result.Any(existing =>
            {
                var dx = existing.Centroid.X - candidate.Centroid.X;
                var dy = existing.Centroid.Y - candidate.Centroid.Y;
                return dx * dx + dy * dy < minimumDistanceSquared;
            });
            if (!duplicate) result.Add(candidate);
        }
        return result;
    }

    private static bool IsFiniteCandidate(StarCandidate star) =>
        double.IsFinite(star.Centroid.X)
        && double.IsFinite(star.Centroid.Y)
        && double.IsFinite(star.PeakAdu)
        && double.IsFinite(star.FluxAdu)
        && double.IsFinite(star.SignalToNoise)
        && double.IsFinite(star.FwhmPixels)
        && double.IsFinite(star.Ellipticity)
        && double.IsFinite(star.SaturatedFraction)
        && double.IsFinite(star.EdgeDistancePixels);

    private static bool HasCoherentCore(
        MonochromeFrame frame,
        StarCandidate star,
        double background,
        double sigma,
        double minimumProminenceSigma)
    {
        var centerX = (int)Math.Round(star.Centroid.X, MidpointRounding.AwayFromZero);
        var centerY = (int)Math.Round(star.Centroid.Y, MidpointRounding.AwayFromZero);
        if (centerX < 1 || centerY < 1 || centerX >= frame.Width - 1 || centerY >= frame.Height - 1)
            return false;

        Span<ushort> core = stackalloc ushort[9];
        var index = 0;
        for (var y = centerY - 1; y <= centerY + 1; y++)
        for (var x = centerX - 1; x <= centerX + 1; x++)
            core[index++] = frame[x, y];
        core.Sort();
        var coreMedian = core[core.Length / 2];
        return coreMedian >= background + minimumProminenceSigma * sigma;
    }

    private static (double Background, double Sigma) EstimateBackgroundAndSigma(MonochromeFrame frame)
    {
        var sample = new List<double>(Math.Max(1024, frame.Width * frame.Height / 64));
        for (var y = 0; y < frame.Height; y += 8)
        for (var x = 0; x < frame.Width; x += 8)
            sample.Add(frame[x, y]);
        sample.Sort();
        var background = PercentileSorted(sample, 0.5);
        var deviations = sample
            .Select(value => Math.Abs(value - background))
            .OrderBy(value => value)
            .ToArray();
        var sigma = Math.Max(1, PercentileSorted(deviations, 0.5) * 1.4826);
        return (background, sigma);
    }

    private static double PercentileSorted(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        var position = percentile * (values.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return values[lower];
        return values[lower] + (position - lower) * (values[upper] - values[lower]);
    }

    private static double CalculateConfidence(
        int starCount,
        double medianSnr,
        double medianFwhm,
        double medianEllipticity,
        double relativeFwhmMad,
        double saturatedStarFraction,
        G3StellarFocusAnalysisOptions options)
    {
        var countScore = Clamp01(starCount / (double)options.PreferredStars);
        var snrScore = Clamp01((medianSnr / options.MinimumSignalToNoise - 1) / 2);
        var shapeScore = Clamp01(1 - medianEllipticity / Math.Max(0.01, options.MaximumMedianEllipticity * 1.2));
        var widthScore = Clamp01(1 - Math.Max(0, medianFwhm - 2) / Math.Max(1, options.MaximumMedianFwhmPixels - 2));
        var consistencyScore = Clamp01(1 - relativeFwhmMad / options.MaximumRelativeFwhmMad);
        var saturationScore = Clamp01(1 - saturatedStarFraction / options.MaximumSaturatedStarFraction);
        return Clamp01(
            0.30 * countScore
            + 0.25 * snrScore
            + 0.15 * shapeScore
            + 0.10 * widthScore
            + 0.10 * consistencyScore
            + 0.10 * saturationScore);
    }

    private static Dictionary<string, double> Metrics(
        double medianFwhm,
        double medianEllipticity,
        int starCount,
        int detectedStarCount,
        double saturatedStarFraction,
        double medianSnr,
        double relativeFwhmMad,
        double confidence) => new()
    {
        ["medianFwhmPixels"] = FiniteOrZero(medianFwhm),
        ["medianEllipticity"] = FiniteOrZero(medianEllipticity),
        ["starCount"] = starCount,
        ["detectedStarCount"] = detectedStarCount,
        ["saturatedStarFraction"] = FiniteOrZero(saturatedStarFraction),
        ["medianSignalToNoise"] = FiniteOrZero(medianSnr),
        ["relativeFwhmMad"] = FiniteOrZero(relativeFwhmMad),
        ["confidence"] = FiniteOrZero(confidence)
    };

    private static G3StellarFocusMeasurement Empty(
        GateResult gate,
        int detectedStarCount = 0,
        double saturatedStarFraction = 0) => new(
        gate,
        0,
        0,
        0,
        detectedStarCount,
        FiniteOrZero(saturatedStarFraction),
        0,
        0,
        0,
        Array.Empty<StarCandidate>());

    private static void Validate(G3StellarFocusAnalysisOptions options)
    {
        RequireFinitePositive(options.DetectionSigma, nameof(options.DetectionSigma));
        if (options.CentroidRadiusPixels < 2) throw new ArgumentOutOfRangeException(nameof(options.CentroidRadiusPixels));
        if (options.EdgeMarginPixels < options.CentroidRadiusPixels) throw new ArgumentOutOfRangeException(nameof(options.EdgeMarginPixels));
        if (options.MaximumCandidates < 1) throw new ArgumentOutOfRangeException(nameof(options.MaximumCandidates));
        if (options.MinimumStars < 1) throw new ArgumentOutOfRangeException(nameof(options.MinimumStars));
        if (options.PreferredStars < options.MinimumStars) throw new ArgumentOutOfRangeException(nameof(options.PreferredStars));
        RequireFinitePositive(options.MinimumSignalToNoise, nameof(options.MinimumSignalToNoise));
        RequireFraction(options.MaximumPerStarSaturatedFraction, nameof(options.MaximumPerStarSaturatedFraction), allowZero: true);
        RequireFraction(options.MaximumSaturatedStarFraction, nameof(options.MaximumSaturatedStarFraction));
        RequireFinitePositive(options.MaximumMedianFwhmPixels, nameof(options.MaximumMedianFwhmPixels));
        RequireFraction(options.MaximumMedianEllipticity, nameof(options.MaximumMedianEllipticity));
        RequireFinitePositive(options.MaximumRelativeFwhmMad, nameof(options.MaximumRelativeFwhmMad));
        RequireFraction(options.MinimumConfidence, nameof(options.MinimumConfidence));
        RequireFinitePositive(options.MinimumSeparationPixels, nameof(options.MinimumSeparationPixels));
        RequireFinitePositive(options.MinimumCoreMedianProminenceSigma, nameof(options.MinimumCoreMedianProminenceSigma));
    }

    private static void RequireFinitePositive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(name);
    }

    private static void RequireFraction(double value, string name, bool allowZero = false)
    {
        if (!double.IsFinite(value) || value > 1 || (allowZero ? value < 0 : value <= 0))
            throw new ArgumentOutOfRangeException(name);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(double.IsFinite).OrderBy(value => value).ToArray();
        if (sorted.Length == 0) return 0;
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static double Clamp01(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
    private static double FiniteOrZero(double value) => double.IsFinite(value) ? value : 0;
}

public sealed record G3StellarFocusScanSample(
    int PositionSteps,
    G3StellarFocusMeasurement Measurement);

public sealed record G3StellarFocusFitOptions(
    int MinimumUniquePositions = 5,
    int MinimumPositionSpanSteps = 40,
    double MinimumInteriorMarginFraction = 0.08,
    int MinimumSamplesPerSide = 2,
    double MinimumPositionCoverageFraction = 0.45,
    double MinimumEdgeRiseFraction = 0.025,
    double MaximumNormalizedRmsResidual = 0.10,
    double HuberTuningConstant = 1.5,
    int MaximumRobustIterations = 12,
    double MaximumVerificationWorseningFraction = 0.02);

public sealed record G3StellarFocusPlan(
    GateResult Gate,
    int InitialPositionSteps,
    int FallbackPositionSteps,
    int? RecommendedPositionSteps,
    double PredictedOptimumPositionSteps,
    double PredictedMinimumFwhmPixels,
    double CurvaturePerStepSquared,
    double NormalizedRmsResidual,
    double PositionCoverageFraction,
    double InitialFwhmPixels,
    int UsedSampleCount,
    int RobustOutlierCount)
{
    public bool IsActionable => Gate.Disposition == GateDisposition.Passed && RecommendedPositionSteps.HasValue;
}

public sealed record G3StellarFocusVerificationDecision(
    GateResult Gate,
    int SelectedPositionSteps,
    int FallbackPositionSteps,
    bool MustReturnToFallback,
    double VerifiedFwhmPixels,
    double ChangeFromInitialFraction);

public static class G3StellarFocusPlanner
{
    public static G3StellarFocusPlan Fit(
        IReadOnlyList<G3StellarFocusScanSample> samples,
        int initialPositionSteps,
        G3StellarFocusFitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        options ??= new G3StellarFocusFitOptions();
        Validate(options);

        var finitePassed = samples
            .Where(sample => sample.Measurement is not null
                             && sample.Measurement.Gate.Disposition == GateDisposition.Passed
                             && double.IsFinite(sample.Measurement.MedianFwhmPixels)
                             && sample.Measurement.MedianFwhmPixels > 0
                             && double.IsFinite(sample.Measurement.Confidence)
                             && sample.Measurement.Confidence > 0)
            .GroupBy(sample => sample.PositionSteps)
            .Select(group => new FitPoint(
                group.Key,
                Median(group.Select(sample => sample.Measurement.MedianFwhmPixels)),
                Math.Clamp(Median(group.Select(sample => sample.Measurement.Confidence)), 0.05, 1)))
            .OrderBy(point => point.Position)
            .ToArray();

        if (finitePassed.Length < options.MinimumUniquePositions)
        {
            return Failure(
                "G3_FOCUS_SCAN_INSUFFICIENT",
                $"Only {finitePassed.Length} unique finite passing focus positions are available; at least {options.MinimumUniquePositions} are required.",
                initialPositionSteps,
                finitePassed.Length);
        }

        var initial = finitePassed.SingleOrDefault(point => point.Position == initialPositionSteps);
        if (initial is null)
        {
            return Failure(
                "G3_FOCUS_INITIAL_SAMPLE_MISSING",
                "The bounded scan must include a passing measurement at the initial focuser position so verification has a rollback baseline.",
                initialPositionSteps,
                finitePassed.Length);
        }

        var minimumPosition = finitePassed[0].Position;
        var maximumPosition = finitePassed[^1].Position;
        var span = (double)maximumPosition - minimumPosition;
        if (span < options.MinimumPositionSpanSteps)
        {
            return Failure(
                "G3_FOCUS_SCAN_SPAN_TOO_SMALL",
                $"Focus scan span {span:F0} steps is below the configured minimum {options.MinimumPositionSpanSteps} steps.",
                initialPositionSteps,
                finitePassed.Length,
                initial.Value);
        }

        var center = (minimumPosition + maximumPosition) / 2d;
        var halfSpan = span / 2;
        var x = finitePassed.Select(point => (point.Position - center) / halfSpan).ToArray();
        var y = finitePassed.Select(point => point.Value).ToArray();
        var baseWeights = finitePassed.Select(point => point.Confidence * point.Confidence).ToArray();
        var weights = baseWeights.ToArray();
        double[] coefficients = [0, 0, 0];
        double robustScale = 0;
        double[] residuals = [];

        for (var iteration = 0; iteration < options.MaximumRobustIterations; iteration++)
        {
            if (!TryFitQuadratic(x, y, weights, out coefficients))
            {
                return Failure(
                    "G3_FOCUS_SCAN_SINGULAR",
                    "Focus positions do not support a stable quadratic fit.",
                    initialPositionSteps,
                    finitePassed.Length,
                    initial.Value);
            }

            residuals = x.Select((value, index) => y[index] - Evaluate(coefficients, value)).ToArray();
            var residualCenter = Median(residuals);
            robustScale = 1.4826 * Median(residuals.Select(value => Math.Abs(value - residualCenter)));
            robustScale = Math.Max(robustScale, Median(y) * 1e-6);
            var changed = 0d;
            for (var index = 0; index < weights.Length; index++)
            {
                var standardized = Math.Abs(residuals[index] - residualCenter) / robustScale;
                var huber = standardized <= options.HuberTuningConstant
                    ? 1
                    : options.HuberTuningConstant / standardized;
                var next = baseWeights[index] * huber;
                changed = Math.Max(changed, Math.Abs(next - weights[index]));
                weights[index] = next;
            }
            if (changed < 1e-8) break;
        }

        if (coefficients.Any(value => !double.IsFinite(value)) || residuals.Any(value => !double.IsFinite(value)))
        {
            return Failure(
                "G3_FOCUS_FIT_NONFINITE",
                "Quadratic focus fit produced a non-finite coefficient or residual.",
                initialPositionSteps,
                finitePassed.Length,
                initial.Value);
        }

        var a = coefficients[0];
        var b = coefficients[1];
        if (a <= 0)
        {
            return Failure(
                "G3_FOCUS_CURVATURE_INVALID",
                "G3 FWHM scan is not convex; no bounded focus minimum is demonstrated.",
                initialPositionSteps,
                finitePassed.Length,
                initial.Value,
                GateDisposition.Indeterminate);
        }

        var optimumNormalized = -b / (2 * a);
        var optimum = center + halfSpan * optimumNormalized;
        var predictedMinimum = Evaluate(coefficients, optimumNormalized);
        var curvature = a / (halfSpan * halfSpan);
        if (!double.IsFinite(optimum)
            || !double.IsFinite(predictedMinimum)
            || predictedMinimum <= 0
            || !double.IsFinite(curvature)
            || curvature <= 0)
        {
            return Failure(
                "G3_FOCUS_FIT_NONFINITE",
                "Quadratic focus minimum is non-finite or physically invalid.",
                initialPositionSteps,
                finitePassed.Length,
                initial.Value);
        }

        var margin = span * options.MinimumInteriorMarginFraction;
        if (optimum < minimumPosition + margin || optimum > maximumPosition - margin)
        {
            return Failure(
                "G3_FOCUS_MINIMUM_AT_BOUNDARY",
                $"Predicted focus minimum {optimum:F1} lies at or beyond the commissioned interior of [{minimumPosition}, {maximumPosition}] steps.",
                initialPositionSteps,
                finitePassed.Length,
                initial.Value,
                GateDisposition.Indeterminate,
                optimum,
                predictedMinimum,
                curvature);
        }

        var samplesLeft = finitePassed.Count(point => point.Position < optimum);
        var samplesRight = finitePassed.Count(point => point.Position > optimum);
        if (samplesLeft < options.MinimumSamplesPerSide || samplesRight < options.MinimumSamplesPerSide)
        {
            return Failure(
                "G3_FOCUS_MINIMUM_NOT_BRACKETED",
                $"Predicted minimum has only {samplesLeft} samples to the left and {samplesRight} to the right.",
                initialPositionSteps,
                finitePassed.Length,
                initial.Value,
                GateDisposition.Indeterminate,
                optimum,
                predictedMinimum,
                curvature);
        }

        var maximumGap = finitePassed.Zip(finitePassed.Skip(1), (left, right) => right.Position - left.Position).Max();
        var coverage = Math.Clamp(1 - maximumGap / span, 0, 1);
        if (coverage < options.MinimumPositionCoverageFraction)
        {
            return Failure(
                "G3_FOCUS_POSITION_COVERAGE_LOW",
                $"Focus scan position coverage {coverage:P1} is below {options.MinimumPositionCoverageFraction:P1}.",
                initialPositionSteps,
                finitePassed.Length,
                initial.Value,
                GateDisposition.Indeterminate,
                optimum,
                predictedMinimum,
                curvature,
                positionCoverage: coverage);
        }

        var predictedLeft = Evaluate(coefficients, -1);
        var predictedRight = Evaluate(coefficients, 1);
        var edgeRise = (Math.Min(predictedLeft, predictedRight) - predictedMinimum) / predictedMinimum;
        if (!double.IsFinite(edgeRise) || edgeRise < options.MinimumEdgeRiseFraction)
        {
            return Failure(
                "G3_FOCUS_CURVATURE_TOO_FLAT",
                $"The weaker fitted edge rises only {FiniteOrZero(edgeRise):P1} above the predicted minimum; at least {options.MinimumEdgeRiseFraction:P1} is required.",
                initialPositionSteps,
                finitePassed.Length,
                initial.Value,
                GateDisposition.Indeterminate,
                optimum,
                predictedMinimum,
                curvature,
                positionCoverage: coverage);
        }

        var totalWeight = weights.Sum();
        var weightedResidualSquare = residuals.Select((value, index) => weights[index] * value * value).Sum();
        var rms = totalWeight > 0 ? Math.Sqrt(weightedResidualSquare / totalWeight) : double.MaxValue;
        var normalizedRms = rms / predictedMinimum;
        if (!double.IsFinite(normalizedRms) || normalizedRms > options.MaximumNormalizedRmsResidual)
        {
            return Failure(
                "G3_FOCUS_RESIDUAL_HIGH",
                $"Robust normalized fit RMS {FiniteOrZero(normalizedRms):P1} exceeds {options.MaximumNormalizedRmsResidual:P1}.",
                initialPositionSteps,
                finitePassed.Length,
                initial.Value,
                GateDisposition.Indeterminate,
                optimum,
                predictedMinimum,
                curvature,
                FiniteOrZero(normalizedRms),
                coverage);
        }

        var outlierCount = residuals.Count(value => Math.Abs(value - Median(residuals)) > 3 * robustScale);
        var recommended = checked((int)Math.Round(optimum, MidpointRounding.AwayFromZero));
        var metrics = PlanMetrics(
            optimum,
            predictedMinimum,
            curvature,
            normalizedRms,
            coverage,
            initial.Value,
            finitePassed.Length,
            outlierCount);
        return new G3StellarFocusPlan(
            GateResult.Pass(
                "G3_FOCUS_MINIMUM_FITTED",
                $"Bounded robust fit predicts the C11/Gemini focus minimum at {optimum:F1} steps; a verification frame is still required.",
                metrics),
            initialPositionSteps,
            initialPositionSteps,
            recommended,
            optimum,
            predictedMinimum,
            curvature,
            normalizedRms,
            coverage,
            initial.Value,
            finitePassed.Length,
            outlierCount);
    }

    public static G3StellarFocusVerificationDecision Verify(
        G3StellarFocusPlan plan,
        G3StellarFocusMeasurement verification,
        G3StellarFocusFitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(verification);
        options ??= new G3StellarFocusFitOptions();
        Validate(options);

        if (!plan.IsActionable
            || !double.IsFinite(plan.InitialFwhmPixels)
            || plan.InitialFwhmPixels <= 0
            || !double.IsFinite(plan.PredictedOptimumPositionSteps)
            || !double.IsFinite(plan.PredictedMinimumFwhmPixels)
            || plan.PredictedMinimumFwhmPixels <= 0
            || !double.IsFinite(plan.CurvaturePerStepSquared)
            || plan.CurvaturePerStepSquared <= 0)
        {
            return new G3StellarFocusVerificationDecision(
                GateResult.Fail("G3_FOCUS_PLAN_NOT_ACTIONABLE", "The focus fit did not authorize a finite physical candidate position; retain or return to the initial position."),
                plan.FallbackPositionSteps,
                plan.FallbackPositionSteps,
                true,
                0,
                0);
        }

        if (verification.Gate.Disposition != GateDisposition.Passed
            || !double.IsFinite(verification.MedianFwhmPixels)
            || verification.MedianFwhmPixels <= 0)
        {
            return new G3StellarFocusVerificationDecision(
                GateResult.Unknown(
                    "G3_FOCUS_VERIFICATION_INVALID",
                    "The candidate-position G3 verification frame did not pass its stellar-shape gate; return to the initial position."),
                plan.FallbackPositionSteps,
                plan.FallbackPositionSteps,
                true,
                0,
                0);
        }

        var change = verification.MedianFwhmPixels / plan.InitialFwhmPixels - 1;
        var metrics = new Dictionary<string, double>
        {
            ["verifiedFwhmPixels"] = verification.MedianFwhmPixels,
            ["initialFwhmPixels"] = plan.InitialFwhmPixels,
            ["changeFromInitialFraction"] = change,
            ["maximumWorseningFraction"] = options.MaximumVerificationWorseningFraction,
            ["candidatePositionSteps"] = plan.RecommendedPositionSteps!.Value,
            ["fallbackPositionSteps"] = plan.FallbackPositionSteps
        };
        if (!double.IsFinite(change) || change > options.MaximumVerificationWorseningFraction)
        {
            return new G3StellarFocusVerificationDecision(
                GateResult.Fail(
                    "G3_FOCUS_VERIFICATION_WORSE",
                    $"Verified FWHM is {FiniteOrZero(change):P1} worse than the initial frame, beyond the allowed {options.MaximumVerificationWorseningFraction:P1}; return to the initial position.",
                    metrics),
                plan.FallbackPositionSteps,
                plan.FallbackPositionSteps,
                true,
                verification.MedianFwhmPixels,
                FiniteOrZero(change));
        }

        return new G3StellarFocusVerificationDecision(
            GateResult.Pass(
                "G3_FOCUS_VERIFIED",
                $"Candidate C11/Gemini focus at {plan.RecommendedPositionSteps.Value} steps passed verification ({change:+0.0%;-0.0%;0.0%} versus initial).",
                metrics),
            plan.RecommendedPositionSteps.Value,
            plan.FallbackPositionSteps,
            false,
            verification.MedianFwhmPixels,
            change);
    }

    private static G3StellarFocusPlan Failure(
        string code,
        string message,
        int initialPosition,
        int usedSampleCount,
        double initialFwhm = 0,
        GateDisposition disposition = GateDisposition.Failed,
        double optimum = 0,
        double predictedMinimum = 0,
        double curvature = 0,
        double normalizedRms = 0,
        double positionCoverage = 0) => new(
        new GateResult(
            code,
            disposition,
            message,
            PlanMetrics(optimum, predictedMinimum, curvature, normalizedRms, positionCoverage, initialFwhm, usedSampleCount, 0)),
        initialPosition,
        initialPosition,
        null,
        FiniteOrZero(optimum),
        FiniteOrZero(predictedMinimum),
        FiniteOrZero(curvature),
        FiniteOrZero(normalizedRms),
        FiniteOrZero(positionCoverage),
        FiniteOrZero(initialFwhm),
        usedSampleCount,
        0);

    private static Dictionary<string, double> PlanMetrics(
        double optimum,
        double predictedMinimum,
        double curvature,
        double normalizedRms,
        double positionCoverage,
        double initialFwhm,
        int usedSamples,
        int outliers) => new()
    {
        ["predictedOptimumPositionSteps"] = FiniteOrZero(optimum),
        ["predictedMinimumFwhmPixels"] = FiniteOrZero(predictedMinimum),
        ["curvaturePerStepSquared"] = FiniteOrZero(curvature),
        ["normalizedRmsResidual"] = FiniteOrZero(normalizedRms),
        ["positionCoverageFraction"] = FiniteOrZero(positionCoverage),
        ["initialFwhmPixels"] = FiniteOrZero(initialFwhm),
        ["usedSampleCount"] = usedSamples,
        ["robustOutlierCount"] = outliers
    };

    private static bool TryFitQuadratic(
        IReadOnlyList<double> x,
        IReadOnlyList<double> y,
        IReadOnlyList<double> weights,
        out double[] coefficients)
    {
        var matrix = new double[3, 4];
        for (var index = 0; index < x.Count; index++)
        {
            if (!double.IsFinite(x[index]) || !double.IsFinite(y[index]) || !double.IsFinite(weights[index]) || weights[index] <= 0)
            {
                coefficients = [0, 0, 0];
                return false;
            }
            var basis = new[] { x[index] * x[index], x[index], 1d };
            for (var row = 0; row < 3; row++)
            {
                for (var column = 0; column < 3; column++)
                    matrix[row, column] += weights[index] * basis[row] * basis[column];
                matrix[row, 3] += weights[index] * basis[row] * y[index];
            }
        }
        return Solve3x3(matrix, out coefficients);
    }

    private static bool Solve3x3(double[,] matrix, out double[] result)
    {
        result = [0, 0, 0];
        for (var pivot = 0; pivot < 3; pivot++)
        {
            var best = pivot;
            for (var row = pivot + 1; row < 3; row++)
                if (Math.Abs(matrix[row, pivot]) > Math.Abs(matrix[best, pivot])) best = row;
            if (!double.IsFinite(matrix[best, pivot]) || Math.Abs(matrix[best, pivot]) < 1e-12) return false;
            if (best != pivot)
            {
                for (var column = pivot; column < 4; column++)
                    (matrix[pivot, column], matrix[best, column]) = (matrix[best, column], matrix[pivot, column]);
            }
            var divisor = matrix[pivot, pivot];
            for (var column = pivot; column < 4; column++) matrix[pivot, column] /= divisor;
            for (var row = 0; row < 3; row++)
            {
                if (row == pivot) continue;
                var factor = matrix[row, pivot];
                for (var column = pivot; column < 4; column++) matrix[row, column] -= factor * matrix[pivot, column];
            }
        }
        for (var row = 0; row < 3; row++) result[row] = matrix[row, 3];
        return result.All(double.IsFinite);
    }

    private static double Evaluate(IReadOnlyList<double> coefficients, double x) =>
        coefficients[0] * x * x + coefficients[1] * x + coefficients[2];

    private static void Validate(G3StellarFocusFitOptions options)
    {
        if (options.MinimumUniquePositions < 5) throw new ArgumentOutOfRangeException(nameof(options.MinimumUniquePositions));
        if (options.MinimumPositionSpanSteps < 1) throw new ArgumentOutOfRangeException(nameof(options.MinimumPositionSpanSteps));
        RequireFraction(options.MinimumInteriorMarginFraction, nameof(options.MinimumInteriorMarginFraction));
        if (options.MinimumInteriorMarginFraction >= 0.5) throw new ArgumentOutOfRangeException(nameof(options.MinimumInteriorMarginFraction));
        if (options.MinimumSamplesPerSide < 1) throw new ArgumentOutOfRangeException(nameof(options.MinimumSamplesPerSide));
        RequireFraction(options.MinimumPositionCoverageFraction, nameof(options.MinimumPositionCoverageFraction));
        RequireFinitePositive(options.MinimumEdgeRiseFraction, nameof(options.MinimumEdgeRiseFraction));
        RequireFinitePositive(options.MaximumNormalizedRmsResidual, nameof(options.MaximumNormalizedRmsResidual));
        RequireFinitePositive(options.HuberTuningConstant, nameof(options.HuberTuningConstant));
        if (options.MaximumRobustIterations < 1) throw new ArgumentOutOfRangeException(nameof(options.MaximumRobustIterations));
        RequireFraction(options.MaximumVerificationWorseningFraction, nameof(options.MaximumVerificationWorseningFraction), allowZero: true);
    }

    private static void RequireFinitePositive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(name);
    }

    private static void RequireFraction(double value, string name, bool allowZero = false)
    {
        if (!double.IsFinite(value) || value >= 1 || (allowZero ? value < 0 : value <= 0))
            throw new ArgumentOutOfRangeException(name);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(double.IsFinite).OrderBy(value => value).ToArray();
        if (sorted.Length == 0) return 0;
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static double FiniteOrZero(double value) => double.IsFinite(value) ? value : 0;

    private sealed record FitPoint(int Position, double Value, double Confidence);
}
