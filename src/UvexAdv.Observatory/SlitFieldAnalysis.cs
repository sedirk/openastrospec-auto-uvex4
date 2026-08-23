namespace UvexAdv.Observatory;

public sealed class MonochromeFrame
{
    private readonly ReadOnlyMemory<ushort> pixels;

    public MonochromeFrame(int width, int height, ReadOnlyMemory<ushort> pixels, ushort saturationLevel = ushort.MaxValue)
    {
        if (width < 3 || height < 3 || pixels.Length != checked(width * height)) throw new ArgumentException("Frame dimensions do not match the pixel buffer.");
        Width = width;
        Height = height;
        this.pixels = pixels;
        SaturationLevel = saturationLevel;
    }

    public int Width { get; }
    public int Height { get; }
    public ushort SaturationLevel { get; }
    public ushort this[int x, int y] => pixels.Span[y * Width + x];
}

public sealed record PixelPoint(double X, double Y);

public sealed record StarCandidate(
    PixelPoint Centroid,
    double PeakAdu,
    double FluxAdu,
    double SignalToNoise,
    double FwhmPixels,
    double Ellipticity,
    double SaturatedFraction,
    double EdgeDistancePixels);

public sealed record StarDetectionOptions(
    double DetectionSigma = 5,
    int CentroidRadiusPixels = 4,
    int EdgeMarginPixels = 12,
    int MaximumCandidates = 500,
    double MaximumEllipticity = 0.65,
    double MaximumSaturatedFraction = 0.02);

public static class StarFieldDetector
{
    public static IReadOnlyList<StarCandidate> Detect(MonochromeFrame frame, StarDetectionOptions? options = null)
    {
        options ??= new StarDetectionOptions();
        var sample = new List<double>(Math.Max(1024, frame.Width * frame.Height / 64));
        for (var y = 0; y < frame.Height; y += 8)
        for (var x = 0; x < frame.Width; x += 8)
            sample.Add(frame[x, y]);
        sample.Sort();
        var background = PercentileSorted(sample, 0.5);
        var deviations = sample.Select(value => Math.Abs(value - background)).OrderBy(value => value).ToArray();
        var sigma = Math.Max(1, PercentileSorted(deviations, 0.5) * 1.4826);
        var threshold = background + options.DetectionSigma * sigma;
        var radius = options.CentroidRadiusPixels;
        var candidates = new List<StarCandidate>();

        for (var y = Math.Max(radius, options.EdgeMarginPixels); y < frame.Height - Math.Max(radius, options.EdgeMarginPixels); y++)
        for (var x = Math.Max(radius, options.EdgeMarginPixels); x < frame.Width - Math.Max(radius, options.EdgeMarginPixels); x++)
        {
            var peak = frame[x, y];
            if (peak < threshold || !IsStrictLocalMaximum(frame, x, y, peak)) continue;
            var candidate = Measure(frame, x, y, radius, background, sigma);
            if (candidate.FluxAdu <= 0 || candidate.Ellipticity > options.MaximumEllipticity || candidate.SaturatedFraction > options.MaximumSaturatedFraction) continue;
            candidates.Add(candidate);
        }

        return candidates
            .OrderByDescending(candidate => candidate.SignalToNoise)
            .Take(options.MaximumCandidates)
            .ToArray();
    }

    private static bool IsStrictLocalMaximum(MonochromeFrame frame, int x, int y, ushort peak)
    {
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            if (dx == 0 && dy == 0) continue;
            var neighbor = frame[x + dx, y + dy];
            if (neighbor > peak || (neighbor == peak && (dy < 0 || (dy == 0 && dx < 0)))) return false;
        }
        return true;
    }

    private static StarCandidate Measure(MonochromeFrame frame, int centerX, int centerY, int radius, double background, double sigma)
    {
        double flux = 0, weightedX = 0, weightedY = 0, peak = 0;
        var saturated = 0;
        var count = 0;
        for (var y = centerY - radius; y <= centerY + radius; y++)
        for (var x = centerX - radius; x <= centerX + radius; x++)
        {
            var raw = frame[x, y];
            var signal = Math.Max(0, raw - background);
            flux += signal;
            weightedX += signal * x;
            weightedY += signal * y;
            peak = Math.Max(peak, raw);
            if (raw >= frame.SaturationLevel) saturated++;
            count++;
        }

        var centroidX = flux > 0 ? weightedX / flux : centerX;
        var centroidY = flux > 0 ? weightedY / flux : centerY;
        double mxx = 0, myy = 0, mxy = 0;
        for (var y = centerY - radius; y <= centerY + radius; y++)
        for (var x = centerX - radius; x <= centerX + radius; x++)
        {
            var signal = Math.Max(0, frame[x, y] - background);
            var dx = x - centroidX;
            var dy = y - centroidY;
            mxx += signal * dx * dx;
            myy += signal * dy * dy;
            mxy += signal * dx * dy;
        }
        if (flux > 0) { mxx /= flux; myy /= flux; mxy /= flux; }
        var trace = mxx + myy;
        var determinant = Math.Max(0, mxx * myy - mxy * mxy);
        var discriminant = Math.Sqrt(Math.Max(0, trace * trace / 4 - determinant));
        var majorVariance = Math.Max(0, trace / 2 + discriminant);
        var minorVariance = Math.Max(0, trace / 2 - discriminant);
        var majorSigma = Math.Sqrt(majorVariance);
        var minorSigma = Math.Sqrt(minorVariance);
        var ellipticity = majorSigma > 0 ? 1 - minorSigma / majorSigma : 1;
        var fwhm = 2.354820045 * Math.Sqrt(Math.Max(0, (majorVariance + minorVariance) / 2));
        var noise = Math.Sqrt(Math.Max(1, flux + count * sigma * sigma));
        var edge = Math.Min(Math.Min(centroidX, frame.Width - 1 - centroidX), Math.Min(centroidY, frame.Height - 1 - centroidY));
        return new StarCandidate(new PixelPoint(centroidX, centroidY), peak, flux, flux / noise, fwhm, ellipticity, saturated / (double)count, edge);
    }

    private static double PercentileSorted(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        var index = percentile * (values.Count - 1);
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper) return values[lower];
        return values[lower] + (index - lower) * (values[upper] - values[lower]);
    }
}

public sealed record SlitGeometry(
    string CalibrationId,
    PixelPoint AcquisitionPoint,
    double AngleDegrees,
    double LengthPixels,
    double WidthPixels,
    double UncertaintyPixels,
    string CameraIdentity,
    int BinningX,
    int BinningY);

public sealed record SlitLocusDetection(
    GateResult Gate,
    SlitGeometry Geometry,
    double ContrastSigma,
    double PerpendicularOffsetPixels,
    double AngleOffsetDegrees);

public static class SlitLocusDetector
{
    public static SlitLocusDetection DetectDarkSlit(
        MonochromeFrame frame,
        SlitGeometry seed,
        double maximumPerpendicularSearchPixels = 50,
        double maximumAngleSearchDegrees = 5,
        double minimumContrastSigma = 2.5)
    {
        var bestScore = double.NegativeInfinity;
        var bestOffset = 0d;
        var bestAngleOffset = 0d;
        for (var angleOffset = -maximumAngleSearchDegrees; angleOffset <= maximumAngleSearchDegrees + 1e-9; angleOffset += 1)
        for (var offset = -maximumPerpendicularSearchPixels; offset <= maximumPerpendicularSearchPixels + 1e-9; offset += 1)
        {
            var score = ScoreLine(frame, seed, offset, angleOffset);
            if (score <= bestScore) continue;
            bestScore = score;
            bestOffset = offset;
            bestAngleOffset = angleOffset;
        }

        var angle = (seed.AngleDegrees + bestAngleOffset) * Math.PI / 180;
        var moved = seed with
        {
            AcquisitionPoint = new PixelPoint(
                seed.AcquisitionPoint.X - Math.Sin(angle) * bestOffset,
                seed.AcquisitionPoint.Y + Math.Cos(angle) * bestOffset),
            AngleDegrees = seed.AngleDegrees + bestAngleOffset,
            UncertaintyPixels = Math.Max(seed.UncertaintyPixels, 1)
        };
        var metrics = new Dictionary<string, double>
        {
            ["slitContrastSigma"] = bestScore,
            ["perpendicularOffsetPixels"] = bestOffset,
            ["angleOffsetDegrees"] = bestAngleOffset
        };
        var gate = bestScore >= minimumContrastSigma
            ? GateResult.Pass("SLIT_LOCUS_DETECTED", $"Dark slit locus detected at {bestScore:F2}σ, offset {bestOffset:F1} px and angle adjustment {bestAngleOffset:F1}°.", metrics)
            : GateResult.Unknown("SLIT_LOCUS_LOW_CONFIDENCE", $"Best dark-line contrast is only {bestScore:F2}σ; the slit locus is not trusted.", metrics);
        return new SlitLocusDetection(gate, moved, bestScore, bestOffset, bestAngleOffset);
    }

    private static double ScoreLine(MonochromeFrame frame, SlitGeometry seed, double perpendicularOffset, double angleOffset)
    {
        var angle = (seed.AngleDegrees + angleOffset) * Math.PI / 180;
        var alongX = Math.Cos(angle);
        var alongY = Math.Sin(angle);
        var perpendicularX = -alongY;
        var perpendicularY = alongX;
        var centerX = seed.AcquisitionPoint.X + perpendicularX * perpendicularOffset;
        var centerY = seed.AcquisitionPoint.Y + perpendicularY * perpendicularOffset;
        var halfLength = Math.Min(seed.LengthPixels / 2, Math.Sqrt(frame.Width * frame.Width + frame.Height * frame.Height));
        var on = new List<double>();
        var reference = new List<double>();
        var halfWidth = Math.Max(1, seed.WidthPixels / 2);
        var guard = Math.Max(8, seed.WidthPixels * 3);
        for (var along = -halfLength; along <= halfLength; along += 3)
        {
            for (var across = -halfWidth; across <= halfWidth; across += 1)
            {
                AddPixel(frame, centerX + alongX * along + perpendicularX * across, centerY + alongY * along + perpendicularY * across, on);
            }
            AddPixel(frame, centerX + alongX * along + perpendicularX * guard, centerY + alongY * along + perpendicularY * guard, reference);
            AddPixel(frame, centerX + alongX * along - perpendicularX * guard, centerY + alongY * along - perpendicularY * guard, reference);
        }
        if (on.Count < 20 || reference.Count < 20) return double.NegativeInfinity;
        on.Sort();
        reference.Sort();
        var onMedian = MedianSorted(on);
        var referenceMedian = MedianSorted(reference);
        var absoluteDeviations = reference.Select(value => Math.Abs(value - referenceMedian)).OrderBy(value => value).ToArray();
        var sigma = Math.Max(0.25, MedianSorted(absoluteDeviations) * 1.4826 / Math.Sqrt(Math.Max(1, on.Count / 4d)));
        return (referenceMedian - onMedian) / sigma;
    }

    private static void AddPixel(MonochromeFrame frame, double x, double y, List<double> values)
    {
        var ix = (int)Math.Round(x);
        var iy = (int)Math.Round(y);
        if (ix >= 0 && ix < frame.Width && iy >= 0 && iy < frame.Height) values.Add(frame[ix, iy]);
    }

    private static double MedianSorted(IReadOnlyList<double> values)
    {
        var center = values.Count / 2;
        return values.Count % 2 == 0 ? (values[center - 1] + values[center]) / 2 : values[center];
    }
}

public sealed record TargetIdentification(
    GateResult Gate,
    StarCandidate? Target,
    PixelPoint PredictedPoint,
    double PredictionResidualPixels,
    double UniquenessRatio);

public static class SlitTargetIdentifier
{
    public static TargetIdentification Identify(
        IReadOnlyList<StarCandidate> candidates,
        PixelPoint predictedPoint,
        double maximumPredictionResidualPixels,
        double minimumSignalToNoise = 8,
        double minimumUniquenessRatio = 1.5)
    {
        var ranked = candidates
            .Where(candidate => candidate.SignalToNoise >= minimumSignalToNoise)
            .Select(candidate => (Candidate: candidate, Distance: Distance(candidate.Centroid, predictedPoint)))
            .Where(item => item.Distance <= maximumPredictionResidualPixels)
            .OrderBy(item => item.Distance)
            .ToArray();
        if (ranked.Length == 0)
        {
            return new TargetIdentification(
                GateResult.Fail("TARGET_NOT_FOUND", $"No suitable star was found within {maximumPredictionResidualPixels:F1} px of the WCS prediction."),
                null, predictedPoint, double.PositiveInfinity, 0);
        }

        var best = ranked[0];
        var uniqueness = ranked.Length == 1 ? double.PositiveInfinity : ranked[1].Distance / Math.Max(0.1, best.Distance);
        if (uniqueness < minimumUniquenessRatio)
        {
            return new TargetIdentification(
                GateResult.Unknown("TARGET_AMBIGUOUS", $"Two candidates are too similar near the predicted target; uniqueness ratio {uniqueness:F2}."),
                best.Candidate, predictedPoint, best.Distance, uniqueness);
        }

        return new TargetIdentification(
            GateResult.Pass(
                "TARGET_IDENTIFIED",
                $"Target matched within {best.Distance:F2} px of the WCS prediction.",
                new Dictionary<string, double>
                {
                    ["predictionResidualPixels"] = best.Distance,
                    ["targetSnr"] = best.Candidate.SignalToNoise,
                    ["uniquenessRatio"] = uniqueness
                }),
            best.Candidate, predictedPoint, best.Distance, uniqueness);
    }

    private static double Distance(PixelPoint a, PixelPoint b) => Length(a.X - b.X, a.Y - b.Y);
    private static double Length(double x, double y) => Math.Sqrt(x * x + y * y);
}

public sealed record GuideStarSelection(GateResult Gate, StarCandidate? Star, double Score);

public sealed record GuideStarSelectionPolicy(
    double SlitGuardPixels = 12,
    double TargetGuardPixels = 20,
    double BrightTargetHaloGuardPixels = 120,
    double BrightTargetSignalToNoiseThreshold = 100,
    double BrightTargetSaturatedFractionThreshold = 0.005,
    double MinimumSignalToNoise = 10,
    double MinimumEdgeDistancePixels = 20,
    double MaximumFwhmPixels = 7,
    double MaximumEllipticity = 0.45,
    double MaximumSaturatedFraction = 0.005);

public static class GuideStarSelector
{
    public static GuideStarSelection Select(
        IReadOnlyList<StarCandidate> candidates,
        SlitGeometry slit,
        PixelPoint targetPoint,
        double slitGuardPixels = 12,
        double targetGuardPixels = 20,
        double minimumSnr = 10)
    {
        return SelectCore(
            candidates,
            slit,
            targetPoint,
            target: null,
            new GuideStarSelectionPolicy(
                SlitGuardPixels: slitGuardPixels,
                TargetGuardPixels: targetGuardPixels,
                MinimumSignalToNoise: minimumSnr));
    }

    public static GuideStarSelection Select(
        IReadOnlyList<StarCandidate> candidates,
        SlitGeometry slit,
        StarCandidate target,
        GuideStarSelectionPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        return SelectCore(candidates, slit, target.Centroid, target, policy ?? new GuideStarSelectionPolicy());
    }

    private static GuideStarSelection SelectCore(
        IReadOnlyList<StarCandidate> candidates,
        SlitGeometry slit,
        PixelPoint targetPoint,
        StarCandidate? target,
        GuideStarSelectionPolicy policy)
    {
        var targetIsUltraBright = target is not null &&
            (target.FwhmPixels <= 0 ||
             target.SignalToNoise >= policy.BrightTargetSignalToNoiseThreshold ||
             target.SaturatedFraction >= policy.BrightTargetSaturatedFractionThreshold);
        var effectiveTargetGuardPixels = targetIsUltraBright
            ? Math.Max(policy.TargetGuardPixels, policy.BrightTargetHaloGuardPixels)
            : policy.TargetGuardPixels;
        var eligible = candidates
            .Where(candidate => double.IsFinite(candidate.SignalToNoise) && candidate.SignalToNoise >= policy.MinimumSignalToNoise)
            .Where(candidate => double.IsFinite(candidate.FwhmPixels) && candidate.FwhmPixels > 0 && candidate.FwhmPixels <= policy.MaximumFwhmPixels)
            .Where(candidate => double.IsFinite(candidate.Ellipticity) && candidate.Ellipticity <= policy.MaximumEllipticity)
            .Where(candidate => double.IsFinite(candidate.SaturatedFraction) && candidate.SaturatedFraction <= policy.MaximumSaturatedFraction)
            .Where(candidate => candidate.EdgeDistancePixels >= policy.MinimumEdgeDistancePixels)
            .Where(candidate => Distance(candidate.Centroid, targetPoint) >= effectiveTargetGuardPixels)
            .Where(candidate => DistanceToSlit(candidate.Centroid, slit) >= slit.WidthPixels / 2 + policy.SlitGuardPixels)
            .Select(candidate => (
                Candidate: candidate,
                Score: candidate.SignalToNoise /
                    (1 + candidate.Ellipticity * 5 + candidate.SaturatedFraction * 100 + Math.Max(0, candidate.FwhmPixels - 2) * 0.5)))
            .OrderByDescending(item => item.Score)
            .ToArray();
        return eligible.Length == 0
            ? new GuideStarSelection(
                GateResult.Fail(
                    "GUIDE_STAR_NOT_FOUND",
                    $"No off-slit guide star passed the SNR, compact-FWHM, ellipticity, saturation, edge and {effectiveTargetGuardPixels:F0}px target/halo guard gates."),
                null,
                0)
            : new GuideStarSelection(
                GateResult.Pass(
                    "GUIDE_STAR_SELECTED",
                    $"Selected compact guide star at ({eligible[0].Candidate.Centroid.X:F1}, {eligible[0].Candidate.Centroid.Y:F1}); target/halo guard {effectiveTargetGuardPixels:F0}px."),
                eligible[0].Candidate,
                eligible[0].Score);
    }

    public static double DistanceToSlit(PixelPoint point, SlitGeometry slit)
    {
        var angle = slit.AngleDegrees * Math.PI / 180;
        var dx = point.X - slit.AcquisitionPoint.X;
        var dy = point.Y - slit.AcquisitionPoint.Y;
        var perpendicular = -Math.Sin(angle) * dx + Math.Cos(angle) * dy;
        var along = Math.Cos(angle) * dx + Math.Sin(angle) * dy;
        if (Math.Abs(along) <= slit.LengthPixels / 2) return Math.Abs(perpendicular);
        var endpointAlong = Math.CopySign(slit.LengthPixels / 2, along);
        return Math.Sqrt(perpendicular * perpendicular + (along - endpointAlong) * (along - endpointAlong));
    }

    private static double Distance(PixelPoint a, PixelPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

public sealed record PixelToMountTransform(
    string CalibrationId,
    double RaArcsecondsPerPixelX,
    double RaArcsecondsPerPixelY,
    double DecArcsecondsPerPixelX,
    double DecArcsecondsPerPixelY,
    string PierSide,
    double RmsArcseconds,
    DateTimeOffset CalibratedUtc);

public sealed record MountCorrection(
    GateResult Gate,
    double DeltaRaArcseconds,
    double DeltaDecArcseconds,
    double MagnitudeDegrees,
    double RequestedMagnitudeDegrees = double.NaN,
    int ReservedSegmentCount = 0)
{
    public bool IsSegmented =>
        double.IsFinite(RequestedMagnitudeDegrees) &&
        RequestedMagnitudeDegrees > MagnitudeDegrees + 1e-12;
}

public static class SlitCorrectionCalculator
{
    public static MountCorrection Calculate(
        PixelPoint target,
        SlitGeometry slit,
        PixelToMountTransform transform,
        MotionLimits limits,
        double cumulativeCorrectionDegrees,
        int completedCorrectionAttempts = 0)
    {
        var dx = slit.AcquisitionPoint.X - target.X;
        var dy = slit.AcquisitionPoint.Y - target.Y;
        var ra = transform.RaArcsecondsPerPixelX * dx + transform.RaArcsecondsPerPixelY * dy;
        var dec = transform.DecArcsecondsPerPixelX * dx + transform.DecArcsecondsPerPixelY * dy;
        var requestedMagnitude = Math.Sqrt(ra * ra + dec * dec) / 3600;
        if (!double.IsFinite(requestedMagnitude) || requestedMagnitude <= 0 ||
            !double.IsFinite(limits.MaximumSingleCorrectionDegrees) || limits.MaximumSingleCorrectionDegrees <= 0)
        {
            return new MountCorrection(
                GateResult.Fail("CORRECTION_INVALID", "The requested pixel-to-mount correction or the single-action limit is not positive and finite."),
                ra,
                dec,
                requestedMagnitude,
                requestedMagnitude);
        }

        var requiredSegmentsValue = Math.Ceiling(
            requestedMagnitude / limits.MaximumSingleCorrectionDegrees - 1e-12);
        if (!double.IsFinite(requiredSegmentsValue) || requiredSegmentsValue > int.MaxValue)
        {
            return new MountCorrection(
                GateResult.Fail("CORRECTION_INVALID", "The requested correction cannot be represented as a bounded number of segments."),
                ra,
                dec,
                requestedMagnitude,
                requestedMagnitude);
        }
        var requiredSegments = (int)requiredSegmentsValue;
        if (completedCorrectionAttempts < 0 ||
            requiredSegments > limits.MaximumCorrectionAttempts - completedCorrectionAttempts)
        {
            return new MountCorrection(
                GateResult.Fail(
                    "CORRECTION_ATTEMPT_RESERVE",
                    $"The full measured slit correction requires {requiredSegments} bounded segment(s), but only {Math.Max(0, limits.MaximumCorrectionAttempts - completedCorrectionAttempts)} attempt(s) remain."),
                ra,
                dec,
                requestedMagnitude,
                requestedMagnitude,
                requiredSegments);
        }
        if (!double.IsFinite(cumulativeCorrectionDegrees) || cumulativeCorrectionDegrees < 0 ||
            !double.IsFinite(limits.MaximumCumulativeCorrectionDegrees) ||
            cumulativeCorrectionDegrees + requestedMagnitude > limits.MaximumCumulativeCorrectionDegrees + 1e-12)
        {
            return new MountCorrection(
                GateResult.Fail(
                    "MOTION_CUMULATIVE_LIMIT",
                    $"Completing the full measured slit correction would exceed the remaining cumulative limit {Math.Max(0, limits.MaximumCumulativeCorrectionDegrees - cumulativeCorrectionDegrees) * 3600:F2} arcsec."),
                ra,
                dec,
                requestedMagnitude,
                requestedMagnitude,
                requiredSegments);
        }

        // Only the next bounded segment is returned.  Production must capture
        // and solve a fresh G3 field before asking for another segment; this is
        // deliberately not an open-loop list of slews.
        var segmentMagnitude = Math.Min(requestedMagnitude, limits.MaximumSingleCorrectionDegrees);
        var scale = segmentMagnitude / requestedMagnitude;
        var segmentRa = ra * scale;
        var segmentDec = dec * scale;
        return new MountCorrection(
            GateResult.Pass(
                requiredSegments > 1 ? "MOTION_SEGMENT_BOUNDED" : "MOTION_BOUNDED",
                requiredSegments > 1
                    ? $"The {requestedMagnitude * 3600:F2} arcsec measured correction was capped to one {segmentMagnitude * 3600:F2} arcsec segment; a fresh G3 measurement is required before the next segment."
                    : $"Bounded slit correction is {segmentRa:F2} arcsec RA, {segmentDec:F2} arcsec Dec."),
            segmentRa,
            segmentDec,
            segmentMagnitude,
            requestedMagnitude,
            requiredSegments);
    }
}
