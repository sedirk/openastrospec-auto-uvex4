namespace UvexAdv.Observatory;

/// <summary>
/// Sign of the slit feature in an LED-on minus LED-off difference image.
/// Both signs are supported because camera/display polarity and illumination
/// geometry are not assumptions that may authorize slit placement.
/// </summary>
public enum SlitIlluminationPolarity
{
    Unknown,
    Bright,
    Dark
}

public sealed record SlitIlluminationPairOptions(
    double MaximumPerpendicularSearchPixels = 64,
    double MaximumAngleSearchDegrees = 8,
    double AngleStepDegrees = 0.5,
    int MaximumMeasuredWidthPixels = 24,
    double AlongSampleStepPixels = 2,
    int MinimumAlongSamples = 30,
    double MinimumContrastSigma = 5,
    double MinimumUniquenessRatio = 1.15,
    double MinimumValidFraction = 0.70,
    double MaximumSaturatedFraction = 0.02,
    double MaximumSidebandAsymmetry = 0.75,
    double MinimumAlongSignalFraction = 0.50,
    double MinimumMeasuredLengthPixels = 48,
    double MinimumLengthToWidthRatio = 8,
    double MaximumAlongGapPixels = 12,
    int MeasuredWidthStepPixels = 1);

/// <summary>
/// Result of a paired, differential slit-illumination measurement. Geometry is
/// always accompanied by Gate; callers must not use a geometry whose gate did
/// not pass to authorize mount motion or science exposure.
/// </summary>
public sealed record SlitIlluminationPairAnalysis(
    GateResult Gate,
    SlitGeometry Geometry,
    SlitIlluminationPolarity Polarity,
    double ContrastSigma,
    double PerpendicularOffsetPixels,
    double AngleOffsetDegrees,
    double MeasuredWidthPixels,
    double Confidence,
    double UniquenessRatio,
    double ValidFraction,
    double SaturatedFraction,
    double BadPixelFraction,
    double AlongSignalFraction = 0,
    double AlongSpanFraction = 0,
    double AlongStartOffsetPixels = 0,
    double AlongEndOffsetPixels = 0);

/// <summary>
/// Measures the illuminated slit from an immutable LED-off/LED-on G3 frame
/// pair. The supplied historical geometry is a bounded search seed only; a
/// fresh differential feature, its width, and its geometric uniqueness must
/// all pass before the returned geometry is trusted.
/// </summary>
public static class SlitIlluminationPairAnalyzer
{
    private const byte ValidPixel = 0;
    private const byte SaturatedPixel = 1;
    private const byte IsolatedBadPixel = 2;

    public static SlitIlluminationPairAnalysis Analyze(
        MonochromeFrame ledOff,
        MonochromeFrame ledOn,
        SlitGeometry seed,
        SlitIlluminationPairOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(ledOff);
        ArgumentNullException.ThrowIfNull(ledOn);
        ArgumentNullException.ThrowIfNull(seed);
        options ??= new SlitIlluminationPairOptions();
        ValidateInputs(ledOff, ledOn, seed, options);

        var statistics = MeasureDifferenceStatistics(ledOff, ledOn);
        var mask = BuildMask(ledOff, ledOn, statistics.Background, statistics.NoiseSigma);
        var corridor = MeasureSearchCorridor(ledOff, seed, options, mask);
        var baseMetrics = new Dictionary<string, double>
        {
            ["ledDifferenceBackgroundAdu"] = statistics.Background,
            ["ledDifferenceNoiseSigmaAdu"] = statistics.NoiseSigma,
            ["validFraction"] = corridor.ValidFraction,
            ["saturatedFraction"] = corridor.SaturatedFraction,
            ["badPixelFraction"] = corridor.BadPixelFraction
        };

        if (corridor.SaturatedFraction > options.MaximumSaturatedFraction)
        {
            return Failure(
                GateResult.Fail(
                    "SLIT_LED_PAIR_SATURATED",
                    $"{corridor.SaturatedFraction:P2} of the slit-search corridor is saturated; LED geometry is not measurable.",
                    baseMetrics),
                seed,
                corridor);
        }

        if (corridor.ValidFraction < options.MinimumValidFraction)
        {
            return Failure(
                GateResult.Fail(
                    "SLIT_LED_PAIR_INSUFFICIENT_VALID_PIXELS",
                    $"Only {corridor.ValidFraction:P1} of the slit-search corridor is valid after saturation and bad-pixel masking.",
                    baseMetrics),
                seed,
                corridor);
        }

        var candidates = new List<LineCandidate>();
        var profiles = new Dictionary<int, CrossProfile>();
        var angleCount = (int)Math.Floor(2 * options.MaximumAngleSearchDegrees / options.AngleStepDegrees + 1e-9) + 1;
        for (var angleIndex = 0; angleIndex < angleCount; angleIndex++)
        {
            var angleOffset = -options.MaximumAngleSearchDegrees + angleIndex * options.AngleStepDegrees;
            var profile = BuildCrossProfile(ledOff, ledOn, mask, statistics, seed, angleOffset, options);
            profiles[angleIndex] = profile;
            AddCandidates(profile, angleIndex, angleOffset, options, candidates);
        }

        if (candidates.Count == 0)
        {
            return Failure(
                GateResult.Unknown(
                    "SLIT_LED_PAIR_NO_DIFFERENTIAL_SIGNAL",
                    "The LED-on/off pair contains no measurable line-like differential feature near the seed.",
                    baseMetrics),
                seed,
                corridor);
        }

        candidates.Sort(static (left, right) => right.Score.CompareTo(left.Score));
        var best = candidates[0];
        var bestProfile = profiles[best.AngleIndex];
        var shape = EstimateShape(bestProfile, best, options);
        var measuredWidth = shape.WidthPixels;
        var reportedWidth = shape.IsValid ? measuredWidth : -1;
        var refinedOffset = shape.CenterOffsetPixels;
        var geometryClusterTolerance = Math.Max(3, (double.IsFinite(measuredWidth) ? measuredWidth : best.TrialWidthPixels) * 1.5);
        LineCandidate? secondDistinct = null;
        ShapeEstimate? secondDistinctShape = null;
        var secondDistinctMaximumLocusSeparation = 0d;
        foreach (var candidate in candidates)
        {
            if (ReferenceEquals(candidate, best)) continue;
            var candidateShape = EstimateShape(profiles[candidate.AngleIndex], candidate, options);
            var candidateCenterOffset = candidateShape.IsValid
                ? candidateShape.CenterOffsetPixels
                : candidate.CenterOffsetPixels;
            var maximumLocusSeparation = MaximumLineLocusSeparationPixels(
                refinedOffset,
                best.AngleOffsetDegrees,
                candidateCenterOffset,
                candidate.AngleOffsetDegrees,
                seed.LengthPixels);
            var samePolarity = Math.Sign(candidate.SignedContrastAdu) == Math.Sign(best.SignedContrastAdu);
            if (samePolarity && maximumLocusSeparation <= geometryClusterTolerance) continue;

            secondDistinct = candidate;
            secondDistinctShape = candidateShape;
            secondDistinctMaximumLocusSeparation = maximumLocusSeparation;
            break;
        }
        var uniqueness = secondDistinct is null
            ? 1000
            : best.Score / Math.Max(1e-9, secondDistinct.Score);
        var polarity = best.SignedContrastAdu >= 0
            ? SlitIlluminationPolarity.Bright
            : SlitIlluminationPolarity.Dark;
        var alongSignal = MeasureAlongSignal(
            ledOff,
            ledOn,
            mask,
            statistics,
            seed,
            best.AngleOffsetDegrees,
            refinedOffset,
            shape.IsValid ? measuredWidth : best.TrialWidthPixels,
            polarity,
            options);
        var confidence = CalculateConfidence(best, uniqueness, corridor.ValidFraction, options);
        var angle = seed.AngleDegrees + best.AngleOffsetDegrees;
        var angleRadians = angle * Math.PI / 180;
        var alongX = Math.Cos(angleRadians);
        var alongY = Math.Sin(angleRadians);
        var acrossX = -alongY;
        var acrossY = alongX;
        var geometryWidth = shape.IsValid ? measuredWidth : seed.WidthPixels;
        var measuredLength = alongSignal.MeasuredLengthPixels;
        var alongMidpoint = (alongSignal.StartOffsetPixels + alongSignal.EndOffsetPixels) / 2;
        var uncertainty = shape.IsValid
            ? Math.Max(0.25, geometryWidth / Math.Max(2, best.Score))
            : Math.Max(seed.UncertaintyPixels, options.MaximumPerpendicularSearchPixels);
        var geometry = seed with
        {
            AcquisitionPoint = new PixelPoint(
                seed.AcquisitionPoint.X + acrossX * refinedOffset + alongX * alongMidpoint,
                seed.AcquisitionPoint.Y + acrossY * refinedOffset + alongY * alongMidpoint),
            AngleDegrees = angle,
            LengthPixels = measuredLength > 0 ? measuredLength : seed.LengthPixels,
            WidthPixels = geometryWidth,
            UncertaintyPixels = uncertainty
        };

        var metrics = new Dictionary<string, double>(baseMetrics)
        {
            ["slitContrastSigma"] = best.Score,
            ["signedDifferentialContrastAdu"] = best.SignedContrastAdu,
            ["bestCandidateCenterOffsetPixels"] = best.CenterOffsetPixels,
            ["bestCandidateTrialWidthPixels"] = best.TrialWidthPixels,
            ["geometryClusterTolerancePixels"] = geometryClusterTolerance,
            ["perpendicularOffsetPixels"] = refinedOffset,
            ["angleOffsetDegrees"] = best.AngleOffsetDegrees,
            // Gate metrics are persisted as ordinary JSON numbers. Use a finite
            // sentinel when width is unresolved.
            ["measuredWidthPixels"] = reportedWidth,
            ["uniquenessRatio"] = uniqueness,
            ["confidence"] = confidence,
            ["profileValidFraction"] = best.ValidFraction,
            ["sidebandAsymmetry"] = best.SidebandAsymmetry,
            ["alongSignalFraction"] = alongSignal.SignalFraction,
            ["alongSpanFraction"] = alongSignal.SpanFraction,
            ["alongValidSamples"] = alongSignal.ValidSamples,
            ["alongSegmentValidSamples"] = alongSignal.SegmentValidSamples,
            ["alongSignalSamples"] = alongSignal.SignalSamples,
            ["measuredLengthPixels"] = measuredLength,
            ["alongStartOffsetPixels"] = alongSignal.StartOffsetPixels,
            ["alongEndOffsetPixels"] = alongSignal.EndOffsetPixels,
            ["measuredLengthToWidthRatio"] = measuredLength / Math.Max(0.5, geometryWidth),
            ["candidateCount"] = candidates.Count,
            ["polarity"] = polarity == SlitIlluminationPolarity.Bright ? 1 : -1
        };
        if (secondDistinct is not null)
        {
            var secondShape = secondDistinctShape!;
            metrics["secondDistinctScore"] = secondDistinct.Score;
            metrics["secondDistinctCenterOffsetPixels"] = secondDistinct.CenterOffsetPixels;
            metrics["secondDistinctRefinedCenterOffsetPixels"] = secondShape.CenterOffsetPixels;
            metrics["secondDistinctAngleOffsetDegrees"] = secondDistinct.AngleOffsetDegrees;
            metrics["secondDistinctTrialWidthPixels"] = secondDistinct.TrialWidthPixels;
            metrics["secondDistinctMeasuredWidthPixels"] =
                double.IsFinite(secondShape.WidthPixels) ? secondShape.WidthPixels : -1;
            metrics["secondDistinctMaximumLocusSeparationPixels"] = secondDistinctMaximumLocusSeparation;
        }

        GateResult gate;
        if (best.Score < options.MinimumContrastSigma)
        {
            gate = GateResult.Unknown(
                "SLIT_LED_PAIR_LOW_CONTRAST",
                $"The strongest paired LED feature is only {best.Score:F2}σ; the historical overlay remains an untrusted seed.",
                metrics);
        }
        else if (!shape.IsValid)
        {
            gate = GateResult.Unknown(
                "SLIT_LED_PAIR_WIDTH_UNRESOLVED",
                "A differential line was found, but its two half-maximum edges do not define a trustworthy width.",
                metrics);
        }
        else if (alongSignal.ValidSamples < options.MinimumAlongSamples)
        {
            gate = GateResult.Unknown(
                "SLIT_LED_PAIR_ALONG_COVERAGE_INSUFFICIENT",
                $"Only {alongSignal.ValidSamples} valid along-slit samples remain; a through-going feature cannot be established.",
                metrics);
        }
        else if (measuredLength < options.MinimumMeasuredLengthPixels ||
                 measuredLength / Math.Max(0.5, measuredWidth) < options.MinimumLengthToWidthRatio)
        {
            gate = GateResult.Unknown(
                "SLIT_LED_PAIR_NOT_THROUGHGOING",
                $"The dominant paired feature is only {measuredLength:F1} px long at width {measuredWidth:F1} px (length/width {measuredLength / Math.Max(0.5, measuredWidth):F1}); this is consistent with a compact positioning spot, not a through-going slit.",
                metrics);
        }
        else if (alongSignal.SignalFraction < options.MinimumAlongSignalFraction)
        {
            gate = GateResult.Unknown(
                "SLIT_LED_PAIR_NOT_THROUGHGOING",
                $"Only {alongSignal.SignalFraction:P1} of valid samples between the measured endpoints contain the paired feature; the candidate is not a continuous through-going slit.",
                metrics);
        }
        else if (best.SidebandAsymmetry > options.MaximumSidebandAsymmetry)
        {
            gate = GateResult.Unknown(
                "SLIT_LED_PAIR_BACKGROUND_ASYMMETRIC",
                $"The two slit sidebands disagree by {best.SidebandAsymmetry:F2} of the line contrast; local illumination is not geometrically reliable.",
                metrics);
        }
        else if (uniqueness < options.MinimumUniquenessRatio)
        {
            gate = GateResult.Unknown(
                "SLIT_LED_PAIR_GEOMETRY_AMBIGUOUS",
                $"Multiple line geometries fit the LED difference; uniqueness ratio {uniqueness:F2} is below {options.MinimumUniquenessRatio:F2}.",
                metrics);
        }
        else
        {
            gate = GateResult.Pass(
                "SLIT_LED_PAIR_GEOMETRY_MEASURED",
                $"{polarity.ToString().ToLowerInvariant()} slit illumination measured at {best.Score:F1}σ, offset {refinedOffset:+0.0;-0.0;0.0} px, angle correction {best.AngleOffsetDegrees:+0.0;-0.0;0.0}°, length {measuredLength:F1} px and width {measuredWidth:F2} px.",
                metrics);
        }

        return new SlitIlluminationPairAnalysis(
            gate,
            geometry,
            polarity,
            best.Score,
            refinedOffset,
            best.AngleOffsetDegrees,
            reportedWidth,
            confidence,
            uniqueness,
            corridor.ValidFraction,
            corridor.SaturatedFraction,
            corridor.BadPixelFraction,
            alongSignal.SignalFraction,
            alongSignal.SpanFraction,
            alongSignal.StartOffsetPixels,
            alongSignal.EndOffsetPixels);
    }

    /// <summary>
    /// Returns the largest perpendicular separation of two undirected line
    /// loci over a segment centered on the common, detector-fixed seed origin.
    /// Candidate band centers must be profile-refined before they are supplied;
    /// raw trial-window centers are not physical line loci.
    /// </summary>
    internal static double MaximumLineLocusSeparationPixels(
        double firstCenterOffsetPixels,
        double firstAngleOffsetDegrees,
        double secondCenterOffsetPixels,
        double secondAngleOffsetDegrees,
        double comparisonLengthPixels)
    {
        var angleSeparationDegrees = Math.Abs(secondAngleOffsetDegrees - firstAngleOffsetDegrees) % 180;
        if (angleSeparationDegrees > 90) angleSeparationDegrees = 180 - angleSeparationDegrees;
        var angleSeparationRadians = angleSeparationDegrees * Math.PI / 180;
        return Math.Abs(secondCenterOffsetPixels - firstCenterOffsetPixels) +
               Math.Abs(Math.Sin(angleSeparationRadians)) * comparisonLengthPixels / 2;
    }

    private static void ValidateInputs(
        MonochromeFrame ledOff,
        MonochromeFrame ledOn,
        SlitGeometry seed,
        SlitIlluminationPairOptions options)
    {
        if (ledOff.Width != ledOn.Width || ledOff.Height != ledOn.Height)
        {
            throw new ArgumentException("LED-off and LED-on frames must have identical dimensions.", nameof(ledOn));
        }
        if (!double.IsFinite(seed.AcquisitionPoint.X) || !double.IsFinite(seed.AcquisitionPoint.Y) ||
            !double.IsFinite(seed.AngleDegrees) || !double.IsFinite(seed.LengthPixels) || seed.LengthPixels <= 0 ||
            !double.IsFinite(seed.WidthPixels) || seed.WidthPixels <= 0)
        {
            throw new ArgumentException("The slit seed must contain finite, positive geometry.", nameof(seed));
        }
        if (options.MaximumPerpendicularSearchPixels <= 0 || options.MaximumAngleSearchDegrees < 0 ||
            options.AngleStepDegrees <= 0 || options.MaximumMeasuredWidthPixels < 1 ||
            options.AlongSampleStepPixels <= 0 || options.MinimumAlongSamples < 5 ||
            options.MinimumContrastSigma <= 0 || options.MinimumUniquenessRatio <= 1 ||
            options.MinimumValidFraction is <= 0 or > 1 ||
            options.MaximumSaturatedFraction is < 0 or >= 1 ||
            options.MaximumSidebandAsymmetry <= 0 ||
            options.MinimumAlongSignalFraction is <= 0 or > 1 ||
            options.MinimumMeasuredLengthPixels <= 0 ||
            options.MinimumLengthToWidthRatio <= 1 ||
            options.MaximumAlongGapPixels <= 0 ||
            options.MeasuredWidthStepPixels < 1 ||
            options.MeasuredWidthStepPixels > options.MaximumMeasuredWidthPixels)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Paired slit-illumination options are outside their valid range.");
        }
    }

    private static DifferenceStatistics MeasureDifferenceStatistics(MonochromeFrame ledOff, MonochromeFrame ledOn)
    {
        var pixelCount = checked(ledOff.Width * ledOff.Height);
        var stride = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(pixelCount / 100_000d)));
        var samples = new List<double>(Math.Min(pixelCount, 100_000));
        for (var y = 0; y < ledOff.Height; y += stride)
        for (var x = 0; x < ledOff.Width; x += stride)
        {
            if (IsSaturated(ledOff, ledOn, x, y)) continue;
            samples.Add((double)ledOn[x, y] - ledOff[x, y]);
        }
        samples.Sort();
        if (samples.Count == 0) return new DifferenceStatistics(0, 1);
        var background = MedianSorted(samples);
        var deviations = samples.Select(value => Math.Abs(value - background)).OrderBy(value => value).ToArray();
        var noise = Math.Max(0.25, MedianSorted(deviations) * 1.4826);
        return new DifferenceStatistics(background, noise);
    }

    private static byte[] BuildMask(
        MonochromeFrame ledOff,
        MonochromeFrame ledOn,
        double background,
        double noiseSigma)
    {
        var mask = new byte[checked(ledOff.Width * ledOff.Height)];
        for (var y = 0; y < ledOff.Height; y++)
        for (var x = 0; x < ledOff.Width; x++)
        {
            if (IsSaturated(ledOff, ledOn, x, y)) mask[y * ledOff.Width + x] = SaturatedPixel;
        }

        var impulseThreshold = Math.Max(32, noiseSigma * 10);
        for (var y = 1; y < ledOff.Height - 1; y++)
        for (var x = 1; x < ledOff.Width - 1; x++)
        {
            var index = y * ledOff.Width + x;
            if (mask[index] != ValidPixel) continue;
            var residual = (double)ledOn[x, y] - ledOff[x, y] - background;
            if (Math.Abs(residual) <= impulseThreshold) continue;
            var sign = Math.Sign(residual);
            var supportedNeighbors = 0;
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var neighborIndex = (y + dy) * ledOff.Width + x + dx;
                if (mask[neighborIndex] != ValidPixel) continue;
                var neighbor = (double)ledOn[x + dx, y + dy] - ledOff[x + dx, y + dy] - background;
                if (Math.Sign(neighbor) == sign && Math.Abs(neighbor) > impulseThreshold * 0.4) supportedNeighbors++;
            }
            if (supportedNeighbors < 2) mask[index] = IsolatedBadPixel;
        }
        return mask;
    }

    private static CorridorStatistics MeasureSearchCorridor(
        MonochromeFrame frame,
        SlitGeometry seed,
        SlitIlluminationPairOptions options,
        byte[] mask)
    {
        var angle = seed.AngleDegrees * Math.PI / 180;
        var alongX = Math.Cos(angle);
        var alongY = Math.Sin(angle);
        var acrossX = -alongY;
        var acrossY = alongX;
        var halfLength = seed.LengthPixels / 2 + 2;
        // The global saturation gate covers every possible slit core, not the much
        // wider candidate sidebands.  Sideband contamination is already rejected
        // per candidate through its valid-fraction and symmetry checks.  Including
        // all sidebands here lets an unrelated saturated feature hundreds of pixels
        // from the slit veto an otherwise measurable wide-slit scan.
        var halfWidth = options.MaximumPerpendicularSearchPixels + options.MaximumMeasuredWidthPixels / 2d + 8;
        long total = 0, saturated = 0, bad = 0;
        for (var y = 0; y < frame.Height; y++)
        for (var x = 0; x < frame.Width; x++)
        {
            var dx = x - seed.AcquisitionPoint.X;
            var dy = y - seed.AcquisitionPoint.Y;
            var along = alongX * dx + alongY * dy;
            var across = acrossX * dx + acrossY * dy;
            if (Math.Abs(along) > halfLength || Math.Abs(across) > halfWidth) continue;
            total++;
            switch (mask[y * frame.Width + x])
            {
                case SaturatedPixel: saturated++; break;
                case IsolatedBadPixel: bad++; break;
            }
        }
        if (total == 0) return new CorridorStatistics(0, 0, 0);
        return new CorridorStatistics(
            (total - saturated - bad) / (double)total,
            saturated / (double)total,
            bad / (double)total);
    }

    private static CrossProfile BuildCrossProfile(
        MonochromeFrame ledOff,
        MonochromeFrame ledOn,
        byte[] mask,
        DifferenceStatistics statistics,
        SlitGeometry seed,
        double angleOffset,
        SlitIlluminationPairOptions options)
    {
        var extent = (int)Math.Ceiling(options.MaximumPerpendicularSearchPixels + options.MaximumMeasuredWidthPixels * 2 + 8);
        var values = Enumerable.Repeat(double.NaN, extent * 2 + 1).ToArray();
        var validFractions = new double[values.Length];
        var angle = (seed.AngleDegrees + angleOffset) * Math.PI / 180;
        var alongX = Math.Cos(angle);
        var alongY = Math.Sin(angle);
        var acrossX = -alongY;
        var acrossY = alongX;
        var halfLength = seed.LengthPixels / 2;
        var alongStep = Math.Max(options.AlongSampleStepPixels, seed.LengthPixels / 500d);

        for (var across = -extent; across <= extent; across++)
        {
            var samples = new List<double>();
            var attempted = 0;
            for (var along = -halfLength; along <= halfLength + 1e-9; along += alongStep)
            {
                attempted++;
                var x = seed.AcquisitionPoint.X + alongX * along + acrossX * across;
                var y = seed.AcquisitionPoint.Y + alongY * along + acrossY * across;
                if (TrySampleDifference(ledOff, ledOn, mask, x, y, statistics.Background, out var value)) samples.Add(value);
            }
            if (samples.Count < options.MinimumAlongSamples) continue;
            samples.Sort();
            values[across + extent] = TrimmedMeanSorted(samples, 0.15);
            validFractions[across + extent] = attempted > 0 ? samples.Count / (double)attempted : 0;
        }

        var smoothed = (double[])values.Clone();
        for (var index = 1; index < values.Length - 1; index++)
        {
            if (!double.IsFinite(values[index - 1]) || !double.IsFinite(values[index]) || !double.IsFinite(values[index + 1])) continue;
            smoothed[index] = (values[index - 1] + 2 * values[index] + values[index + 1]) / 4;
        }
        var finite = smoothed.Where(double.IsFinite).OrderBy(value => value).ToArray();
        var baseline = finite.Length == 0 ? 0 : MedianSorted(finite);
        var profileDeviations = finite.Select(value => Math.Abs(value - baseline)).OrderBy(value => value).ToArray();
        var profileMadSigma = profileDeviations.Length == 0 ? 0 : MedianSorted(profileDeviations) * 1.4826;
        var typicalAlongSamples = Math.Max(1, validFractions.Where(value => value > 0).DefaultIfEmpty(1).Average() * Math.Min(500, seed.LengthPixels / alongStep + 1));
        var samplingSigma = statistics.NoiseSigma * 1.15 / Math.Sqrt(typicalAlongSamples);
        var profileSigma = Math.Max(0.25, Math.Max(profileMadSigma, samplingSigma));
        return new CrossProfile(extent, smoothed, validFractions, profileSigma);
    }

    private static void AddCandidates(
        CrossProfile profile,
        int angleIndex,
        double angleOffset,
        SlitIlluminationPairOptions options,
        List<LineCandidate> candidates)
    {
        var maximumCenter = (int)Math.Floor(options.MaximumPerpendicularSearchPixels);
        for (var center = -maximumCenter; center <= maximumCenter; center++)
        for (var trialWidth = 1; trialWidth <= options.MaximumMeasuredWidthPixels; trialWidth += options.MeasuredWidthStepPixels)
        {
            var inner = new List<double>();
            var left = new List<double>();
            var right = new List<double>();
            var validFractions = new List<double>();
            var halfWidth = trialWidth / 2d;
            var guard = Math.Max(2, trialWidth * 0.75);
            var sideWidth = Math.Max(2, trialWidth);
            for (var offset = -profile.Extent; offset <= profile.Extent; offset++)
            {
                var value = profile.Values[offset + profile.Extent];
                if (!double.IsFinite(value)) continue;
                var distance = offset - center;
                if (Math.Abs(distance) <= halfWidth)
                {
                    inner.Add(value);
                    validFractions.Add(profile.ValidFractions[offset + profile.Extent]);
                }
                else if (distance < -halfWidth - guard && distance >= -halfWidth - guard - sideWidth)
                {
                    left.Add(value);
                    validFractions.Add(profile.ValidFractions[offset + profile.Extent]);
                }
                else if (distance > halfWidth + guard && distance <= halfWidth + guard + sideWidth)
                {
                    right.Add(value);
                    validFractions.Add(profile.ValidFractions[offset + profile.Extent]);
                }
            }
            if (inner.Count == 0 || left.Count == 0 || right.Count == 0) continue;
            var innerMean = inner.Average();
            var leftMean = left.Average();
            var rightMean = right.Average();
            var referenceMean = (left.Sum() + right.Sum()) / (left.Count + right.Count);
            var contrast = innerMean - referenceMean;
            var standardError = profile.NoiseSigma * Math.Sqrt(1d / inner.Count + 1d / (left.Count + right.Count));
            var rawScore = Math.Abs(contrast) / Math.Max(0.25, standardError);
            var asymmetry = Math.Abs(leftMean - rightMean) / Math.Max(Math.Abs(contrast), profile.NoiseSigma);
            var score = rawScore / (1 + Math.Max(0, asymmetry - 0.25));
            candidates.Add(new LineCandidate(
                angleIndex,
                angleOffset,
                center,
                trialWidth,
                contrast,
                score,
                validFractions.Count == 0 ? 0 : validFractions.Average(),
                asymmetry,
                referenceMean));
        }
    }

    private static ShapeEstimate EstimateShape(
        CrossProfile profile,
        LineCandidate candidate,
        SlitIlluminationPairOptions options)
    {
        var sign = Math.Sign(candidate.SignedContrastAdu);
        if (sign == 0) return new ShapeEstimate(candidate.CenterOffsetPixels, double.NaN, false);
        var searchRadius = Math.Max(3, candidate.TrialWidthPixels);
        var first = Math.Max(-profile.Extent, (int)Math.Floor(candidate.CenterOffsetPixels - searchRadius));
        var last = Math.Min(profile.Extent, (int)Math.Ceiling(candidate.CenterOffsetPixels + searchRadius));
        var peakOffset = first;
        var peak = double.NegativeInfinity;
        for (var offset = first; offset <= last; offset++)
        {
            var value = profile.Values[offset + profile.Extent];
            if (!double.IsFinite(value)) continue;
            var signal = sign * (value - candidate.ReferenceAdu);
            if (signal <= peak) continue;
            peak = signal;
            peakOffset = offset;
        }
        if (!double.IsFinite(peak) || peak <= 0) return new ShapeEstimate(candidate.CenterOffsetPixels, double.NaN, false);

        var halfMaximum = peak / 2;
        var leftInside = peakOffset;
        while (leftInside > -profile.Extent)
        {
            var next = profile.Values[leftInside - 1 + profile.Extent];
            if (!double.IsFinite(next) || sign * (next - candidate.ReferenceAdu) < halfMaximum) break;
            leftInside--;
        }
        var rightInside = peakOffset;
        while (rightInside < profile.Extent)
        {
            var next = profile.Values[rightInside + 1 + profile.Extent];
            if (!double.IsFinite(next) || sign * (next - candidate.ReferenceAdu) < halfMaximum) break;
            rightInside++;
        }
        if (leftInside <= -profile.Extent || rightInside >= profile.Extent)
        {
            return new ShapeEstimate(candidate.CenterOffsetPixels, double.NaN, false);
        }

        var leftOutsideSignal = sign * (profile.Values[leftInside - 1 + profile.Extent] - candidate.ReferenceAdu);
        var leftInsideSignal = sign * (profile.Values[leftInside + profile.Extent] - candidate.ReferenceAdu);
        var rightInsideSignal = sign * (profile.Values[rightInside + profile.Extent] - candidate.ReferenceAdu);
        var rightOutsideSignal = sign * (profile.Values[rightInside + 1 + profile.Extent] - candidate.ReferenceAdu);
        if (!new[] { leftOutsideSignal, leftInsideSignal, rightInsideSignal, rightOutsideSignal }.All(double.IsFinite))
        {
            return new ShapeEstimate(candidate.CenterOffsetPixels, double.NaN, false);
        }
        var leftCrossing = InterpolateCrossing(leftInside - 1, leftOutsideSignal, leftInside, leftInsideSignal, halfMaximum);
        var rightCrossing = InterpolateCrossing(rightInside, rightInsideSignal, rightInside + 1, rightOutsideSignal, halfMaximum);
        var width = rightCrossing - leftCrossing;
        if (!double.IsFinite(width) || width < 0.5 ||
            width > Math.Max(100, options.MaximumMeasuredWidthPixels * 1.5))
        {
            return new ShapeEstimate(candidate.CenterOffsetPixels, width, false);
        }

        double weightedOffset = 0, weight = 0;
        for (var offset = leftInside; offset <= rightInside; offset++)
        {
            var signal = Math.Max(0, sign * (profile.Values[offset + profile.Extent] - candidate.ReferenceAdu));
            weightedOffset += offset * signal;
            weight += signal;
        }
        var center = weight > 0 ? weightedOffset / weight : (leftCrossing + rightCrossing) / 2;
        return new ShapeEstimate(center, width, true);
    }

    private static AlongSignalStatistics MeasureAlongSignal(
        MonochromeFrame ledOff,
        MonochromeFrame ledOn,
        byte[] mask,
        DifferenceStatistics statistics,
        SlitGeometry seed,
        double angleOffsetDegrees,
        double centerOffsetPixels,
        double measuredWidthPixels,
        SlitIlluminationPolarity polarity,
        SlitIlluminationPairOptions options)
    {
        var angle = (seed.AngleDegrees + angleOffsetDegrees) * Math.PI / 180;
        var alongX = Math.Cos(angle);
        var alongY = Math.Sin(angle);
        var acrossX = -alongY;
        var acrossY = alongX;
        var centerX = seed.AcquisitionPoint.X + acrossX * centerOffsetPixels;
        var centerY = seed.AcquisitionPoint.Y + acrossY * centerOffsetPixels;
        var halfLength = seed.LengthPixels / 2;
        var halfWidth = Math.Max(0.5, measuredWidthPixels / 2);
        var guard = Math.Max(2, measuredWidthPixels * 0.75);
        var sideWidth = Math.Max(2, measuredWidthPixels);
        var alongStep = Math.Max(options.AlongSampleStepPixels, seed.LengthPixels / 500d);
        var sign = polarity == SlitIlluminationPolarity.Bright ? 1d : -1d;
        var samples = new List<AlongSample>();

        for (var along = -halfLength; along <= halfLength + 1e-9; along += alongStep)
        {
            double innerTotal = 0, sideTotal = 0;
            var innerCount = 0;
            var sideCount = 0;
            for (var across = -halfWidth; across <= halfWidth + 1e-9; across += 1)
            {
                if (TrySampleDifference(
                    ledOff,
                    ledOn,
                    mask,
                    centerX + alongX * along + acrossX * across,
                    centerY + alongY * along + acrossY * across,
                    statistics.Background,
                    out var value))
                {
                    innerTotal += value;
                    innerCount++;
                }
            }
            for (var sideOffset = halfWidth + guard; sideOffset <= halfWidth + guard + sideWidth + 1e-9; sideOffset += 1)
            {
                for (var side = -1; side <= 1; side += 2)
                {
                    var across = side * sideOffset;
                    if (TrySampleDifference(
                        ledOff,
                        ledOn,
                        mask,
                        centerX + alongX * along + acrossX * across,
                        centerY + alongY * along + acrossY * across,
                        statistics.Background,
                        out var value))
                    {
                        sideTotal += value;
                        sideCount++;
                    }
                }
            }
            if (innerCount < 2 || sideCount < 2) continue;

            var contrast = sign * (innerTotal / innerCount - sideTotal / sideCount);
            var standardError = statistics.NoiseSigma * Math.Sqrt(1d / innerCount + 1d / sideCount);
            samples.Add(new AlongSample(
                along,
                contrast >= Math.Max(1, standardError * 2.5),
                contrast / Math.Max(0.25, standardError)));
        }

        var signalSamples = samples.Where(sample => sample.HasSignal).ToArray();
        if (samples.Count == 0 || signalSamples.Length == 0)
        {
            return new AlongSignalStatistics(samples.Count, 0, 0, 0, 0, 0, 0, 0);
        }

        // The historical slit length is only a bounded search extent. Find the
        // dominant connected run in detector coordinates so an old oversized
        // overlay cannot dilute a real, shorter physical slit. Small holes are
        // tolerated, but a distant noise hit cannot extend either endpoint.
        var components = new List<AlongSignalComponent>();
        var componentStart = 0;
        for (var index = 1; index <= signalSamples.Length; index++)
        {
            if (index < signalSamples.Length &&
                signalSamples[index].AlongPixels - signalSamples[index - 1].AlongPixels <= options.MaximumAlongGapPixels)
            {
                continue;
            }

            var componentSamples = signalSamples[componentStart..index];
            var first = componentSamples[0].AlongPixels;
            var last = componentSamples[^1].AlongPixels;
            var length = Math.Max(alongStep, last - first + alongStep);
            components.Add(new AlongSignalComponent(
                first,
                last,
                length,
                componentSamples.Length,
                componentSamples.Sum(sample => Math.Max(0, sample.Significance))));
            componentStart = index;
        }

        var dominant = components
            .OrderByDescending(component => component.LengthPixels)
            .ThenByDescending(component => component.SignalSamples)
            .ThenByDescending(component => component.TotalSignificance)
            .First();
        var start = Math.Max(-halfLength, dominant.FirstSignalPixels - alongStep / 2);
        var end = Math.Min(halfLength, dominant.LastSignalPixels + alongStep / 2);
        var measuredLength = Math.Max(0, end - start);
        var segmentSamples = samples
            .Where(sample => sample.AlongPixels >= start - 1e-9 && sample.AlongPixels <= end + 1e-9)
            .ToArray();
        var segmentSignalSamples = segmentSamples.Count(sample => sample.HasSignal);
        var signalFraction = segmentSamples.Length > 0
            ? segmentSignalSamples / (double)segmentSamples.Length
            : 0;
        var spanFraction = Math.Clamp(measuredLength / seed.LengthPixels, 0, 1);
        return new AlongSignalStatistics(
            samples.Count,
            segmentSamples.Length,
            segmentSignalSamples,
            signalFraction,
            spanFraction,
            start,
            end,
            measuredLength);
    }

    private static double CalculateConfidence(
        LineCandidate best,
        double uniqueness,
        double validFraction,
        SlitIlluminationPairOptions options)
    {
        var contrastQuality = 1 - Math.Exp(-best.Score / options.MinimumContrastSigma);
        var uniquenessQuality = uniqueness >= 1000
            ? 1
            : Math.Clamp((uniqueness - 1) / (options.MinimumUniquenessRatio - 1), 0, 1);
        var coverageQuality = Math.Clamp((validFraction - options.MinimumValidFraction) / (1 - options.MinimumValidFraction), 0, 1);
        var symmetryQuality = Math.Clamp(1 - best.SidebandAsymmetry / Math.Max(1, options.MaximumSidebandAsymmetry * 2), 0, 1);
        return Math.Clamp(contrastQuality * (0.5 + 0.5 * uniquenessQuality) * (0.75 + 0.25 * coverageQuality) * symmetryQuality, 0, 1);
    }

    private static SlitIlluminationPairAnalysis Failure(GateResult gate, SlitGeometry seed, CorridorStatistics corridor) =>
        new(
            gate,
            seed,
            SlitIlluminationPolarity.Unknown,
            0,
            0,
            0,
            -1,
            0,
            0,
            corridor.ValidFraction,
            corridor.SaturatedFraction,
            corridor.BadPixelFraction);

    private static bool TrySampleDifference(
        MonochromeFrame ledOff,
        MonochromeFrame ledOn,
        byte[] mask,
        double x,
        double y,
        double background,
        out double value)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        if (x0 < 0 || y0 < 0 || x0 + 1 >= ledOff.Width || y0 + 1 >= ledOff.Height)
        {
            value = 0;
            return false;
        }
        var xFraction = x - x0;
        var yFraction = y - y0;
        var topLeftIndex = y0 * ledOff.Width + x0;
        var topRightIndex = topLeftIndex + 1;
        var bottomLeftIndex = topLeftIndex + ledOff.Width;
        var bottomRightIndex = bottomLeftIndex + 1;
        if (mask[topLeftIndex] != ValidPixel ||
            mask[topRightIndex] != ValidPixel ||
            mask[bottomLeftIndex] != ValidPixel ||
            mask[bottomRightIndex] != ValidPixel)
        {
            value = 0;
            return false;
        }
        var topLeft = (double)ledOn[x0, y0] - ledOff[x0, y0] - background;
        var topRight = (double)ledOn[x0 + 1, y0] - ledOff[x0 + 1, y0] - background;
        var bottomLeft = (double)ledOn[x0, y0 + 1] - ledOff[x0, y0 + 1] - background;
        var bottomRight = (double)ledOn[x0 + 1, y0 + 1] - ledOff[x0 + 1, y0 + 1] - background;
        value =
            topLeft * (1 - xFraction) * (1 - yFraction) +
            topRight * xFraction * (1 - yFraction) +
            bottomLeft * (1 - xFraction) * yFraction +
            bottomRight * xFraction * yFraction;
        return true;
    }

    private static bool IsSaturated(MonochromeFrame ledOff, MonochromeFrame ledOn, int x, int y) =>
        ledOff[x, y] >= ledOff.SaturationLevel || ledOn[x, y] >= ledOn.SaturationLevel;

    private static double InterpolateCrossing(double x1, double y1, double x2, double y2, double target)
    {
        var difference = y2 - y1;
        if (Math.Abs(difference) < 1e-12) return (x1 + x2) / 2;
        return x1 + (target - y1) / difference * (x2 - x1);
    }

    private static double TrimmedMeanSorted(IReadOnlyList<double> values, double trimFraction)
    {
        if (values.Count == 0) return double.NaN;
        var trim = Math.Min(values.Count / 3, (int)Math.Floor(values.Count * trimFraction));
        double total = 0;
        for (var index = trim; index < values.Count - trim; index++) total += values[index];
        return total / Math.Max(1, values.Count - 2 * trim);
    }

    private static double MedianSorted(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return double.NaN;
        var center = values.Count / 2;
        return values.Count % 2 == 0 ? (values[center - 1] + values[center]) / 2 : values[center];
    }

    private sealed record DifferenceStatistics(double Background, double NoiseSigma);
    private sealed record CorridorStatistics(double ValidFraction, double SaturatedFraction, double BadPixelFraction);
    private sealed record CrossProfile(int Extent, double[] Values, double[] ValidFractions, double NoiseSigma);
    private sealed record LineCandidate(
        int AngleIndex,
        double AngleOffsetDegrees,
        double CenterOffsetPixels,
        int TrialWidthPixels,
        double SignedContrastAdu,
        double Score,
        double ValidFraction,
        double SidebandAsymmetry,
        double ReferenceAdu);
    private sealed record ShapeEstimate(double CenterOffsetPixels, double WidthPixels, bool IsValid);
    private sealed record AlongSample(double AlongPixels, bool HasSignal, double Significance);
    private sealed record AlongSignalComponent(
        double FirstSignalPixels,
        double LastSignalPixels,
        double LengthPixels,
        int SignalSamples,
        double TotalSignificance);
    private sealed record AlongSignalStatistics(
        int ValidSamples,
        int SegmentValidSamples,
        int SignalSamples,
        double SignalFraction,
        double SpanFraction,
        double StartOffsetPixels,
        double EndOffsetPixels,
        double MeasuredLengthPixels);
}
