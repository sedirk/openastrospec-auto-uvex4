namespace UvexAdv.Observatory;

/// <summary>
/// How the physical dark aperture was resolved.  A reflected LED ridge is not
/// itself a slit-width measurement.
/// </summary>
public enum SlitDarkApertureResolution
{
    Unresolved = 0,
    DirectTwoEdge = 1,
    SharedPsfModel = 2,
}

/// <summary>
/// Commissioned HDR measurement policy.  The short exposure preserves the
/// strongly reflecting edge; the longer exposure is allowed to saturate that
/// edge and is used to confirm the lower, non-reflecting aperture boundary.
/// </summary>
public sealed record SlitDarkApertureHdrOptions(
    double MaximumPerpendicularSearchPixels = 128,
    double MaximumAngleSearchDegrees = 8,
    double MinimumApertureWidthPixels = 1.5,
    double MaximumApertureWidthPixels = 120,
    double EdgePsfAlphaPixels = 0.625,
    double EdgePsfBeta = 0.43,
    double ProfileStepPixels = 0.25,
    double MinimumSecondaryEdgeAmplitudeRatio = 0.04,
    double MaximumSecondaryEdgeAmplitudeRatio = 0.30,
    double MinimumTwoEdgeDeltaBic = 10,
    double MinimumModelResolvedDeltaBic = 2,
    double MinimumModelResolvedSeparationPsfAlpha = 3.5,
    double MinimumDirectEdgeSeparationPsfAlpha = 6,
    double MinimumLongExposureValidFraction = 0.01,
    double MinimumLongExposureDynamicRangeAdu = 20,
    double MinimumProfileSignalToNoise = 3.5,
    bool SharedPsfIsCommissioned = false);

public sealed record SlitDarkApertureHdrAnalysis(
    GateResult Gate,
    SlitGeometry Geometry,
    SlitGeometry ReflectiveEdgeGeometry,
    SlitDarkApertureResolution Resolution,
    double ApertureWidthPixels,
    double WidthUncertaintyPixels,
    double ReflectiveEdgeToApertureCenterPixels,
    double SecondaryEdgeAmplitudeRatio,
    double DeltaBic,
    double ShortExposureSaturatedFraction,
    double LongExposureSaturatedFraction,
    double LongExposureValidFraction,
    double LongExposureDynamicRangeAdu);

/// <summary>
/// Measures the physical, dark slit aperture from a short/long LED HDR pair.
/// Saturation of the specular ridge is expected and is never by itself a
/// rejection.  A frame is rejected only when clipping removes the dark region
/// and its second physical edge, or when a two-edge model is not supported.
/// </summary>
public static class SlitDarkApertureHdrAnalyzer
{
    public const string MeasurementModelId = "UVEX-DARK-APERTURE-TWO-EDGE-HDR-V1";

    public static SlitDarkApertureHdrAnalysis Analyze(
        MonochromeFrame shortLedOff,
        MonochromeFrame shortLedOn,
        MonochromeFrame longLedOff,
        MonochromeFrame longLedOn,
        SlitGeometry seed,
        SlitDarkApertureHdrOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(shortLedOff);
        ArgumentNullException.ThrowIfNull(shortLedOn);
        ArgumentNullException.ThrowIfNull(longLedOff);
        ArgumentNullException.ThrowIfNull(longLedOn);
        ArgumentNullException.ThrowIfNull(seed);
        options ??= new SlitDarkApertureHdrOptions();
        ValidateInputs(shortLedOff, shortLedOn, longLedOff, longLedOn, options);

        // The legacy differential line finder remains useful only as a
        // detector-fixed locator for the strongest reflected edge.  Its FWHM
        // is deliberately discarded below and can never authorize width or
        // acquisition-centre geometry.
        var reflective = SlitIlluminationPairAnalyzer.Analyze(
            shortLedOff,
            shortLedOn,
            seed,
            new SlitIlluminationPairOptions(
                MaximumPerpendicularSearchPixels: options.MaximumPerpendicularSearchPixels,
                MaximumAngleSearchDegrees: options.MaximumAngleSearchDegrees,
                MaximumMeasuredWidthPixels: Math.Max(4, (int)Math.Ceiling(options.MaximumApertureWidthPixels)),
                MaximumSaturatedFraction: 0.25));
        // The commissioned seed is a detector-fixed reference-edge locus, not
        // a width authority.  A real second edge can make the legacy one-line
        // locator non-unique, so locator failure must not erase a measurable
        // dark aperture.  A successful locator may refine translation, but we
        // retain the commissioned angle: a few degrees of angle drift smears
        // the far edge of the 300 um aperture into the reflective wing.
        var reference = reflective.Gate.Disposition == GateDisposition.Passed
            ? seed with
            {
                AcquisitionPoint = reflective.Geometry.AcquisitionPoint,
                LengthPixels = Math.Max(seed.LengthPixels, reflective.Geometry.LengthPixels),
            }
            : seed;

        // Keep a wide validation corridor even for a narrow commissioned slit:
        // the long exposure may clip the few pixels around both aperture edges,
        // while the surrounding unsaturated shoulders still prove that the
        // frame is not globally clipped.
        var extent = Math.Max(64, options.MaximumApertureWidthPixels) + 24;
        var shortProfile = BuildProfile(shortLedOff, shortLedOn, reference, extent, options.ProfileStepPixels);
        var longProfile = BuildProfile(longLedOff, longLedOn, reference, extent, options.ProfileStepPixels);
        var longDynamicRange = RobustDynamicRange(longProfile);
        var metrics = new Dictionary<string, double>
        {
            ["shortSaturatedFraction"] = shortProfile.SaturatedFraction,
            ["longSaturatedFraction"] = longProfile.SaturatedFraction,
            ["longValidFraction"] = longProfile.ValidFraction,
            ["longDynamicRangeAdu"] = longDynamicRange,
        };
        if (longProfile.ValidFraction < options.MinimumLongExposureValidFraction ||
            longDynamicRange < options.MinimumLongExposureDynamicRangeAdu)
        {
            return Failure(
                GateResult.Unknown(
                    "SLIT_DARK_APERTURE_LONG_EXPOSURE_CLIPPED",
                    $"The long LED exposure retained only {longProfile.ValidFraction:P1} valid profile samples and {longDynamicRange:F1} ADU dynamic range. " +
                    "Specular-edge saturation is allowed, but a completely clipped dark aperture contains no physical-width information.",
                    metrics),
                seed,
                reference,
                shortProfile,
                longProfile,
                longDynamicRange);
        }

        var fit = FitTwoEdges(shortProfile, longProfile, options);
        metrics["apertureWidthPixels"] = fit.SeparationPixels;
        metrics["secondaryEdgeAmplitudeRatio"] = fit.SecondaryAmplitudeRatio;
        metrics["deltaBic"] = fit.DeltaBic;
        metrics["fitSignalToNoise"] = fit.SignalToNoise;
        if (!fit.IsValid || fit.SignalToNoise < options.MinimumProfileSignalToNoise)
        {
            return Failure(
                GateResult.Unknown(
                    "SLIT_DARK_APERTURE_SECOND_EDGE_NOT_FOUND",
                    "Only the strong reflective ridge was measured; the lower physical aperture edge is not unique enough to authorize a slit centre or width.",
                    metrics),
                seed,
                reference,
                shortProfile,
                longProfile,
                longDynamicRange);
        }

        if (fit.DeltaBic < options.MinimumModelResolvedDeltaBic ||
            fit.SeparationPixels < options.MinimumModelResolvedSeparationPsfAlpha * options.EdgePsfAlphaPixels)
        {
            return Failure(
                GateResult.Unknown(
                    "SLIT_DARK_APERTURE_SECOND_EDGE_NOT_FOUND",
                    $"A two-edge model is not supported (ΔBIC {fit.DeltaBic:F2}, separation {fit.SeparationPixels:F2}px); " +
                    "the reflective ridge alone cannot authorize a slit centre or width.",
                    metrics),
                seed,
                reference,
                shortProfile,
                longProfile,
                longDynamicRange);
        }

        var resolution = fit.DeltaBic >= options.MinimumTwoEdgeDeltaBic &&
            fit.SeparationPixels >= options.MinimumDirectEdgeSeparationPsfAlpha * options.EdgePsfAlphaPixels
            ? SlitDarkApertureResolution.DirectTwoEdge
            : options.SharedPsfIsCommissioned
                ? SlitDarkApertureResolution.SharedPsfModel
                : SlitDarkApertureResolution.Unresolved;
        if (resolution == SlitDarkApertureResolution.Unresolved)
        {
            return Failure(
                GateResult.Unknown(
                    "SLIT_DARK_APERTURE_MODEL_NOT_COMMISSIONED",
                    $"The second edge is unresolved (two-edge ΔBIC {fit.DeltaBic:F2}; direct minimum {options.MinimumTwoEdgeDeltaBic:F2}, " +
                    $"shared-PSF minimum {options.MinimumModelResolvedDeltaBic:F2}). " +
                    "A shared edge-PSF measured from the other physical wheel slots is required; nominal slit labels are not used to invent the width.",
                    metrics),
                seed,
                reference,
                shortProfile,
                longProfile,
                longDynamicRange);
        }

        var centreShift = fit.Direction * fit.SeparationPixels / 2 + fit.PrimaryOffsetPixels;
        var angleRadians = reference.AngleDegrees * Math.PI / 180;
        var acrossX = -Math.Sin(angleRadians);
        var acrossY = Math.Cos(angleRadians);
        var widthUncertainty = resolution == SlitDarkApertureResolution.DirectTwoEdge
            ? Math.Max(options.ProfileStepPixels, fit.SeparationUncertaintyPixels)
            : Math.Max(0.5, fit.SeparationUncertaintyPixels);
        var geometry = reference with
        {
            CalibrationId = $"{seed.CalibrationId}:dark-aperture-hdr",
            AcquisitionPoint = new PixelPoint(
                reference.AcquisitionPoint.X + acrossX * centreShift,
                reference.AcquisitionPoint.Y + acrossY * centreShift),
            WidthPixels = fit.SeparationPixels,
            UncertaintyPixels = Math.Max(reference.UncertaintyPixels, widthUncertainty),
        };
        var gateCode = resolution == SlitDarkApertureResolution.DirectTwoEdge
            ? "SLIT_DARK_APERTURE_DIRECTLY_MEASURED"
            : "SLIT_DARK_APERTURE_MODEL_RESOLVED";
        return new SlitDarkApertureHdrAnalysis(
            GateResult.Pass(
                gateCode,
                $"Physical dark aperture measured edge-to-edge as {fit.SeparationPixels:F2}±{widthUncertainty:F2}px; " +
                $"the acquisition point is the aperture midpoint, {centreShift:+0.00;-0.00;0.00}px from the reflective ridge.",
                metrics),
            geometry,
            reference,
            resolution,
            fit.SeparationPixels,
            widthUncertainty,
            centreShift,
            fit.SecondaryAmplitudeRatio,
            fit.DeltaBic,
            shortProfile.SaturatedFraction,
            longProfile.SaturatedFraction,
            longProfile.ValidFraction,
            longDynamicRange);
    }

    private static CrossProfile BuildProfile(
        MonochromeFrame off,
        MonochromeFrame on,
        SlitGeometry reference,
        double extent,
        double step)
    {
        var angle = reference.AngleDegrees * Math.PI / 180;
        var alongX = Math.Cos(angle);
        var alongY = Math.Sin(angle);
        var acrossX = -alongY;
        var acrossY = alongX;
        var halfAlong = Math.Max(12, reference.LengthPixels * 0.38);
        var offsets = new List<double>();
        var values = new List<double?>();
        var saturationByOffset = new List<double?>();
        var saturated = 0;
        var total = 0;
        for (var offset = -extent; offset <= extent + step * 0.1; offset += step)
        {
            var samples = new List<double>();
            var offsetSaturated = 0;
            var offsetTotal = 0;
            for (var along = -halfAlong; along <= halfAlong; along += 2)
            {
                var x = reference.AcquisitionPoint.X + acrossX * offset + alongX * along;
                var y = reference.AcquisitionPoint.Y + acrossY * offset + alongY * along;
                if (!TrySampleDifference(off, on, x, y, out var difference, out var clipped)) continue;
                offsetTotal++;
                total++;
                if (clipped)
                {
                    offsetSaturated++;
                    saturated++;
                    continue;
                }
                samples.Add(difference);
            }
            offsets.Add(offset);
            values.Add(samples.Count >= Math.Max(5, offsetTotal / 5) ? Median(samples) : null);
            saturationByOffset.Add(offsetTotal == 0 ? null : offsetSaturated / (double)offsetTotal);
        }
        // The black aperture can remain visible as a dip in clipped coverage
        // even when the reflecting metal is deliberately overexposed.  Such
        // offsets are valid HDR evidence; a globally clipped frame still has
        // saturation fraction 1 at every offset and therefore zero contrast.
        var valid = values.Zip(saturationByOffset).Count(pair =>
            pair.First.HasValue || pair.Second is { } fraction && fraction < 0.995);
        return new CrossProfile(
            offsets.ToArray(),
            values.ToArray(),
            saturationByOffset.ToArray(),
            values.Count == 0 ? 0 : valid / (double)values.Count,
            total == 0 ? 1 : saturated / (double)total);
    }

    private static bool TrySampleDifference(
        MonochromeFrame off,
        MonochromeFrame on,
        double x,
        double y,
        out double difference,
        out bool clipped)
    {
        difference = 0;
        clipped = false;
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        if (x0 < 0 || y0 < 0 || x0 + 1 >= off.Width || y0 + 1 >= off.Height) return false;
        var fx = x - x0;
        var fy = y - y0;
        double Sample(MonochromeFrame frame) =>
            frame[x0, y0] * (1 - fx) * (1 - fy) +
            frame[x0 + 1, y0] * fx * (1 - fy) +
            frame[x0, y0 + 1] * (1 - fx) * fy +
            frame[x0 + 1, y0 + 1] * fx * fy;
        clipped = on[x0, y0] >= on.SaturationLevel ||
            on[x0 + 1, y0] >= on.SaturationLevel ||
            on[x0, y0 + 1] >= on.SaturationLevel ||
            on[x0 + 1, y0 + 1] >= on.SaturationLevel ||
            off[x0, y0] >= off.SaturationLevel ||
            off[x0 + 1, y0] >= off.SaturationLevel ||
            off[x0, y0 + 1] >= off.SaturationLevel ||
            off[x0 + 1, y0 + 1] >= off.SaturationLevel;
        difference = Sample(on) - Sample(off);
        return true;
    }

    private static EdgeFit FitTwoEdges(CrossProfile shortProfile, CrossProfile longProfile, SlitDarkApertureHdrOptions options)
    {
        var bestOne = ModelScore.Invalid;
        var bestTwo = ModelScore.Invalid;
        var bestSeparationScores = new List<(double Separation, double Score)>();
        foreach (var direction in new[] { -1, 1 })
        {
            for (var primary = -2d; primary <= 2.0001; primary += options.ProfileStepPixels)
            {
                for (var separation = options.MinimumApertureWidthPixels;
                     separation <= options.MaximumApertureWidthPixels + options.ProfileStepPixels * 0.1;
                     separation += options.ProfileStepPixels)
                {
                    // Compare the one- and two-edge hypotheses on the same
                    // two endpoint windows.  The previous implementation fit
                    // the one-edge model only near the primary but charged the
                    // wide-aperture model for every intervening baseline
                    // sample, making a real 300 um second edge look worse by
                    // construction.
                    var one = EvaluateModel(
                        shortProfile,
                        longProfile,
                        primary,
                        0,
                        direction,
                        options,
                        includeSecond: false,
                        comparisonSeparation: separation);
                    var two = EvaluateModel(shortProfile, longProfile, primary, separation, direction, options, includeSecond: true);
                    if (!one.IsValid || !two.IsValid || one.SampleCount != two.SampleCount) continue;
                    if (!bestTwo.IsValid || two.Score / two.SampleCount < bestTwo.Score / bestTwo.SampleCount)
                    {
                        bestOne = one;
                        bestTwo = two;
                    }
                    bestSeparationScores.Add((separation, two.Score / two.SampleCount));
                }
            }
        }
        if (!bestOne.IsValid || !bestTwo.IsValid) return EdgeFit.Invalid;
        var n = bestTwo.SampleCount;
        var bicOne = n * Math.Log(Math.Max(1e-12, bestOne.Score / n)) + 8 * Math.Log(n);
        var bicTwo = n * Math.Log(Math.Max(1e-12, bestTwo.Score / n)) + 10 * Math.Log(n);
        var deltaBic = bicOne - bicTwo;
        var normalizedBestScore = bestTwo.Score / bestTwo.SampleCount;
        var tolerance = normalizedBestScore * (1 + 1d / Math.Max(20, n));
        var near = bestSeparationScores.Where(item => item.Score <= tolerance).Select(item => item.Separation).ToArray();
        var uncertainty = near.Length > 1 ? Math.Max(options.ProfileStepPixels, (near.Max() - near.Min()) / 2) : options.ProfileStepPixels;
        return new EdgeFit(
            true,
            bestTwo.PrimaryOffset,
            bestTwo.Separation,
            bestTwo.Direction,
            bestTwo.SecondaryAmplitudeRatio,
            deltaBic,
            uncertainty,
            bestTwo.SignalToNoise);
    }

    private static ModelScore EvaluateModel(
        CrossProfile shortProfile,
        CrossProfile longProfile,
        double primary,
        double separation,
        int direction,
        SlitDarkApertureHdrOptions options,
        bool includeSecond,
        double comparisonSeparation = 0)
    {
        var shortFit = FitProfile(shortProfile, primary, separation, direction, options, includeSecond, comparisonSeparation);
        if (!shortFit.IsValid) return ModelScore.Invalid;
        var longFit = FitProfile(longProfile, primary, separation, direction, options, includeSecond, comparisonSeparation);
        if (includeSecond && !longFit.IsValid)
            longFit = FitClippedLongExposureSecondEdge(longProfile, primary + direction * separation, options);
        // On very narrow physical apertures the deliberately longer exposure
        // can clip both edge cores.  Once the long frame has independently
        // passed the non-global-clipping/dynamic-range gate above, it remains
        // valid HDR support; the sub-pixel separation is then measured by the
        // commissioned shared-PSF fit in the short frame.
        if (includeSecond && !longFit.IsValid && longProfile.SaturatedFraction > 0.90 && longProfile.ValidFraction > 0)
            longFit = shortFit;
        if (includeSecond && !longFit.IsValid) return ModelScore.Invalid;
        var ratio = shortFit.SecondaryAmplitudeRatio;
        if (includeSecond && (ratio < options.MinimumSecondaryEdgeAmplitudeRatio || ratio > options.MaximumSecondaryEdgeAmplitudeRatio))
            return ModelScore.Invalid;
        return new ModelScore(
            true,
            shortFit.NormalizedResidual,
            shortFit.SampleCount,
            primary,
            separation,
            direction,
            ratio,
            includeSecond ? Math.Min(shortFit.SignalToNoise, longFit.SignalToNoise) : shortFit.SignalToNoise);
    }

    private static ProfileFit FitClippedLongExposureSecondEdge(
        CrossProfile profile,
        double secondEdge,
        SlitDarkApertureHdrOptions options)
    {
        var rows = new List<double[]>();
        var observations = new List<double>();
        for (var index = 0; index < profile.Offsets.Length; index++)
        {
            if (profile.Values[index] is not { } value) continue;
            var x = profile.Offsets[index];
            if (Math.Abs(x - secondEdge) > 20) continue;
            rows.Add([1d, x, Moffat(x, secondEdge, options)]);
            observations.Add(value);
        }
        if (rows.Count < 20) return ProfileFit.Invalid;
        var coefficients = SolveLeastSquares(rows, observations);
        if (coefficients is null || coefficients[2] <= 0) return ProfileFit.Invalid;
        double rss = 0;
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var predicted = rows[rowIndex][0] * coefficients[0] +
                rows[rowIndex][1] * coefficients[1] +
                rows[rowIndex][2] * coefficients[2];
            var residual = observations[rowIndex] - predicted;
            rss += residual * residual;
        }
        var noise = RobustSigma(profile.Values.Where(value => value.HasValue).Select(value => value!.Value).ToArray());
        return new ProfileFit(
            true,
            rss / Math.Max(1, noise * noise),
            rows.Count,
            0,
            coefficients[2] / Math.Max(1, noise));
    }

    private static ProfileFit FitProfile(
        CrossProfile profile,
        double primary,
        double separation,
        int direction,
        SlitDarkApertureHdrOptions options,
        bool includeSecond,
        double comparisonSeparation)
    {
        var rows = new List<double[]>();
        var observations = new List<double>();
        var second = primary + direction * separation;
        var domainSecond = primary + direction * (includeSecond ? separation : comparisonSeparation);
        for (var index = 0; index < profile.Offsets.Length; index++)
        {
            if (profile.Values[index] is not { } value) continue;
            var x = profile.Offsets[index];
            var nearPrimary = Math.Abs(x - primary) <= 20;
            var nearSecond = comparisonSeparation > 0 || includeSecond
                ? Math.Abs(x - domainSecond) <= 20
                : false;
            if (!nearPrimary && !nearSecond) continue;
            var row = includeSecond
                ? new[] { 1d, x, Moffat(x, primary, options), Moffat(x, second, options) }
                : new[] { 1d, x, Moffat(x, primary, options) };
            rows.Add(row);
            observations.Add(value);
        }
        if (rows.Count < (includeSecond ? 40 : 30)) return ProfileFit.Invalid;
        var coefficients = SolveLeastSquares(rows, observations);
        if (coefficients is null || coefficients[2] <= 0) return ProfileFit.Invalid;
        var ratio = includeSecond ? coefficients[3] / coefficients[2] : 0;
        if (includeSecond && coefficients[3] <= 0) return ProfileFit.Invalid;
        double rss = 0;
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            double predicted = 0;
            for (var column = 0; column < coefficients.Length; column++) predicted += rows[rowIndex][column] * coefficients[column];
            var residual = observations[rowIndex] - predicted;
            rss += residual * residual;
        }
        var noise = RobustSigma(profile.Values.Where(value => value.HasValue).Select(value => value!.Value).ToArray());
        var normalized = rss / Math.Max(1, noise * noise);
        var snr = includeSecond ? coefficients[3] / Math.Max(1, noise) : coefficients[2] / Math.Max(1, noise);
        return new ProfileFit(true, normalized, rows.Count, ratio, snr);
    }

    private static double[]? SolveLeastSquares(IReadOnlyList<double[]> rows, IReadOnlyList<double> observations)
    {
        var size = rows[0].Length;
        var augmented = new double[size, size + 1];
        for (var row = 0; row < rows.Count; row++)
        for (var left = 0; left < size; left++)
        {
            augmented[left, size] += rows[row][left] * observations[row];
            for (var right = 0; right < size; right++) augmented[left, right] += rows[row][left] * rows[row][right];
        }
        for (var pivot = 0; pivot < size; pivot++)
        {
            var best = pivot;
            for (var candidate = pivot + 1; candidate < size; candidate++)
                if (Math.Abs(augmented[candidate, pivot]) > Math.Abs(augmented[best, pivot])) best = candidate;
            if (Math.Abs(augmented[best, pivot]) < 1e-12) return null;
            if (best != pivot)
            for (var column = pivot; column <= size; column++)
                (augmented[pivot, column], augmented[best, column]) = (augmented[best, column], augmented[pivot, column]);
            var divisor = augmented[pivot, pivot];
            for (var column = pivot; column <= size; column++) augmented[pivot, column] /= divisor;
            for (var row = 0; row < size; row++)
            {
                if (row == pivot) continue;
                var factor = augmented[row, pivot];
                for (var column = pivot; column <= size; column++) augmented[row, column] -= factor * augmented[pivot, column];
            }
        }
        var result = new double[size];
        for (var index = 0; index < size; index++) result[index] = augmented[index, size];
        return result.All(double.IsFinite) ? result : null;
    }

    private static double Moffat(double x, double center, SlitDarkApertureHdrOptions options) =>
        Math.Pow(1 + Math.Pow((x - center) / options.EdgePsfAlphaPixels, 2), -options.EdgePsfBeta);

    private static double RobustDynamicRange(CrossProfile profile)
    {
        var values = profile.Values.Where(value => value.HasValue).Select(value => value!.Value).OrderBy(value => value).ToArray();
        var signalDynamic = values.Length < 10 ? 0 : Percentile(values, 0.90) - Percentile(values, 0.10);
        var saturation = profile.SaturationByOffset
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .OrderBy(value => value)
            .ToArray();
        var clippingDynamic = saturation.Length < 10
            ? 0
            : (Percentile(saturation, 0.90) - Percentile(saturation, 0.10)) * 4095;
        return Math.Max(signalDynamic, clippingDynamic);
    }

    private static double RobustSigma(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 1;
        var sorted = values.OrderBy(value => value).ToArray();
        var median = Percentile(sorted, 0.5);
        var deviations = sorted.Select(value => Math.Abs(value - median)).OrderBy(value => value).ToArray();
        return Math.Max(1, 1.4826 * Percentile(deviations, 0.5));
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        return Percentile(values, 0.5);
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        var position = percentile * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return lower == upper ? sorted[lower] : sorted[lower] + (position - lower) * (sorted[upper] - sorted[lower]);
    }

    private static SlitDarkApertureHdrAnalysis Failure(
        GateResult gate,
        SlitGeometry seed,
        SlitGeometry reflective,
        CrossProfile? shortProfile = null,
        CrossProfile? longProfile = null,
        double longDynamicRange = 0) =>
        new(
            gate,
            seed,
            reflective,
            SlitDarkApertureResolution.Unresolved,
            0,
            0,
            0,
            0,
            0,
            shortProfile?.SaturatedFraction ?? 0,
            longProfile?.SaturatedFraction ?? 0,
            longProfile?.ValidFraction ?? 0,
            longDynamicRange);

    private static void ValidateInputs(
        MonochromeFrame shortOff,
        MonochromeFrame shortOn,
        MonochromeFrame longOff,
        MonochromeFrame longOn,
        SlitDarkApertureHdrOptions options)
    {
        if (shortOff.Width != shortOn.Width || shortOff.Height != shortOn.Height ||
            shortOff.Width != longOff.Width || shortOff.Height != longOff.Height ||
            shortOff.Width != longOn.Width || shortOff.Height != longOn.Height)
            throw new ArgumentException("HDR LED frames must share the same detector geometry.");
        if (!double.IsFinite(options.EdgePsfAlphaPixels) || options.EdgePsfAlphaPixels <= 0 ||
            !double.IsFinite(options.EdgePsfBeta) || options.EdgePsfBeta <= 0 ||
            !double.IsFinite(options.ProfileStepPixels) || options.ProfileStepPixels is <= 0 or > 1 ||
            !double.IsFinite(options.MinimumApertureWidthPixels) || options.MinimumApertureWidthPixels <= 0 ||
            !double.IsFinite(options.MaximumApertureWidthPixels) || options.MaximumApertureWidthPixels <= options.MinimumApertureWidthPixels)
            throw new ArgumentOutOfRangeException(nameof(options), "HDR dark-aperture options are invalid.");
    }

    private sealed record CrossProfile(
        double[] Offsets,
        double?[] Values,
        double?[] SaturationByOffset,
        double ValidFraction,
        double SaturatedFraction);

    private sealed record ProfileFit(
        bool IsValid,
        double NormalizedResidual,
        int SampleCount,
        double SecondaryAmplitudeRatio,
        double SignalToNoise)
    {
        public static ProfileFit Invalid { get; } = new(false, double.PositiveInfinity, 0, 0, 0);
    }

    private sealed record ModelScore(
        bool IsValid,
        double Score,
        int SampleCount,
        double PrimaryOffset,
        double Separation,
        int Direction,
        double SecondaryAmplitudeRatio,
        double SignalToNoise)
    {
        public static ModelScore Invalid { get; } = new(false, double.PositiveInfinity, 0, 0, 0, 0, 0, 0);
    }

    private sealed record EdgeFit(
        bool IsValid,
        double PrimaryOffsetPixels,
        double SeparationPixels,
        int Direction,
        double SecondaryAmplitudeRatio,
        double DeltaBic,
        double SeparationUncertaintyPixels,
        double SignalToNoise)
    {
        public static EdgeFit Invalid { get; } = new(false, 0, 0, 0, 0, double.NegativeInfinity, 0, 0);
    }
}
