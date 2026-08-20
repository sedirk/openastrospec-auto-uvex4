namespace UvexAdv.Qhy.Core;

public static class QhyFrameAnalyzer
{
    private const int MaximumStatisticsSamples = 250_000;
    private const int MaximumStarCandidates = 500;
    private const int CandidateSelectionMultiplier = 4;
    private const int MeasurementRadius = 4;
    private const int BackgroundAnnulusInnerRadius = 5;
    private const int BackgroundAnnulusOuterRadius = 7;
    // Plate-solving quality needs more than a threshold-crossing central pixel.
    // A 30-sigma aperture sum keeps low-read-noise tails and fixed pattern from
    // masquerading as a solve-capable field while retaining ordinary stellar PSFs.
    private const double MinimumIntegratedSignalToNoise = 30;
    private const double MinimumFwhmPixels = 1.2;
    private const double MaximumFwhmPixels = 20;
    private const double MinimumCoreFluxFraction = 0.12;
    private const double MaximumCoreFluxFraction = 0.90;
    private const double MaximumEllipticity = 0.80;
    private const string StarDetectionCappedFlag = "STAR_DETECTION_CAPPED";

    public static QhyFrameMetrics Analyze(
        QhyFrame frame,
        QhyQualityThresholds thresholds,
        double? baselineStarFlux = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(thresholds);
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Pixels.Length != checked(frame.Width * frame.Height))
        {
            throw new ArgumentException("Frame dimensions do not match the pixel buffer.", nameof(frame));
        }

        var sampleStride = Math.Max(1, frame.Pixels.Length / MaximumStatisticsSamples);
        var sample = new double[(frame.Pixels.Length + sampleStride - 1) / sampleStride];
        var sampleIndex = 0;
        double sum = 0;
        var zeroCount = 0;
        var saturatedCount = 0;
        ushort minimum = ushort.MaxValue;
        ushort maximum = ushort.MinValue;

        for (var index = 0; index < frame.Pixels.Length; index++)
        {
            var value = frame.Pixels[index];
            sum += value;
            if (value == 0) zeroCount++;
            if (value >= thresholds.SaturationAdu) saturatedCount++;
            if (value < minimum) minimum = value;
            if (value > maximum) maximum = value;
            if (index % sampleStride == 0) sample[sampleIndex++] = value;
        }

        if (sampleIndex != sample.Length) Array.Resize(ref sample, sampleIndex);
        Array.Sort(sample);
        var median = PercentileSorted(sample, 0.5);
        var deviations = new double[sample.Length];
        for (var index = 0; index < sample.Length; index++) deviations[index] = Math.Abs(sample[index] - median);
        Array.Sort(deviations);
        var backgroundSigma = Math.Max(1.0, 1.4826 * PercentileSorted(deviations, 0.5));

        var stars = DetectStars(frame, median, backgroundSigma, thresholds.DetectionSigma);
        var medianFlux = MedianNullable(stars.Select(static star => star.Flux));
        var transparency = baselineStarFlux is > 0 && medianFlux is > 0 ? medianFlux / baselineStarFlux : null;
        var saturatedFraction = saturatedCount / (double)frame.Pixels.Length;
        var flags = new List<string>();
        if (stars.Count < thresholds.MinimumDetectedStars) flags.Add("LOW_STAR_COUNT");
        if (stars.Count >= MaximumStarCandidates) flags.Add(StarDetectionCappedFlag);
        if (saturatedFraction > thresholds.MaximumSaturatedFraction) flags.Add("SATURATION_HIGH");
        if (transparency is not null && transparency < thresholds.MinimumTransparency) flags.Add("TRANSPARENCY_LOW");
        if (zeroCount > frame.Pixels.Length * 0.001) flags.Add("ZERO_CLIPPING");

        return new QhyFrameMetrics(
            minimum,
            maximum,
            sum / frame.Pixels.Length,
            median,
            backgroundSigma,
            PercentileSorted(sample, 0.9),
            PercentileSorted(sample, 0.99),
            PercentileSorted(sample, 0.999),
            zeroCount / (double)frame.Pixels.Length,
            saturatedFraction,
            stars.Count,
            MedianNullable(stars.Select(static star => star.Fwhm)),
            MedianNullable(stars.Select(static star => star.Ellipticity)),
            medianFlux,
            transparency,
            flags);
    }

    public static bool PassesAcquisitionGate(QhyFrameMetrics metrics, QhyQualityThresholds thresholds) =>
        metrics.DetectedStars >= thresholds.MinimumDetectedStars &&
        !metrics.QualityFlags.Contains(StarDetectionCappedFlag, StringComparer.Ordinal) &&
        metrics.SaturatedFraction <= thresholds.MaximumSaturatedFraction &&
        metrics.ZeroFraction <= 0.001;

    private static List<StarMeasurement> DetectStars(
        QhyFrame frame,
        double background,
        double sigma,
        double detectionSigma)
    {
        var threshold = background + (Math.Max(3, detectionSigma) * sigma);
        var candidates = new List<(int X, int Y, ushort Value)>();
        var pixels = frame.Pixels;
        var width = frame.Width;
        var height = frame.Height;
        var scanStep = frame.Pixels.Length > 2_000_000 ? 2 : 1;
        var scanMargin = BackgroundAnnulusOuterRadius + 1;

        for (var y = scanMargin; y < height - scanMargin; y += scanStep)
        {
            for (var x = scanMargin; x < width - scanMargin; x += scanStep)
            {
                var candidate = FindBrightestInScanCell(pixels, width, height, x, y, scanStep);
                if (candidate.Value <= threshold) continue;
                if (!IsLocalMaximum(pixels, width, candidate.X, candidate.Y, radius: 2)) continue;
                candidates.Add(candidate);
            }
        }

        var selected = candidates
            .OrderByDescending(static candidate => candidate.Value)
            .Take(MaximumStarCandidates * CandidateSelectionMultiplier)
            .ToArray();
        var acceptedPositions = new List<(int X, int Y)>();
        var measurements = new List<StarMeasurement>();
        foreach (var candidate in selected)
        {
            if (acceptedPositions.Any(existing =>
                    Math.Abs(existing.X - candidate.X) <= 7 && Math.Abs(existing.Y - candidate.Y) <= 7))
            {
                continue;
            }

            var measurement = MeasureStar(frame, candidate.X, candidate.Y, sigma, detectionSigma);
            if (measurement is null) continue;
            acceptedPositions.Add((candidate.X, candidate.Y));
            measurements.Add(measurement);
            if (measurements.Count >= MaximumStarCandidates) break;
        }

        return measurements;
    }

    private static (int X, int Y, ushort Value) FindBrightestInScanCell(
        IReadOnlyList<ushort> pixels,
        int width,
        int height,
        int startX,
        int startY,
        int scanStep)
    {
        var bestX = startX;
        var bestY = startY;
        var bestValue = pixels[(startY * width) + startX];
        for (var offsetY = 0; offsetY < scanStep && startY + offsetY < height; offsetY++)
        {
            var row = (startY + offsetY) * width;
            for (var offsetX = 0; offsetX < scanStep && startX + offsetX < width; offsetX++)
            {
                var value = pixels[row + startX + offsetX];
                if (value <= bestValue) continue;
                bestX = startX + offsetX;
                bestY = startY + offsetY;
                bestValue = value;
            }
        }

        return (bestX, bestY, bestValue);
    }

    private static bool IsLocalMaximum(
        IReadOnlyList<ushort> pixels,
        int width,
        int centerX,
        int centerY,
        int radius)
    {
        var centerValue = pixels[(centerY * width) + centerX];
        for (var y = centerY - radius; y <= centerY + radius; y++)
        {
            var row = y * width;
            for (var x = centerX - radius; x <= centerX + radius; x++)
            {
                if (x == centerX && y == centerY) continue;
                var value = pixels[row + x];
                if (value > centerValue) return false;
                if (value == centerValue && (y < centerY || (y == centerY && x < centerX))) return false;
            }
        }

        return true;
    }

    private static StarMeasurement? MeasureStar(
        QhyFrame frame,
        int centerX,
        int centerY,
        double globalSigma,
        double detectionSigma)
    {
        var localBackgroundSamples = new double[
            ((BackgroundAnnulusOuterRadius * 2) + 1) * ((BackgroundAnnulusOuterRadius * 2) + 1)];
        var localBackgroundCount = 0;
        var innerRadiusSquared = BackgroundAnnulusInnerRadius * BackgroundAnnulusInnerRadius;
        var outerRadiusSquared = BackgroundAnnulusOuterRadius * BackgroundAnnulusOuterRadius;
        for (var offsetY = -BackgroundAnnulusOuterRadius; offsetY <= BackgroundAnnulusOuterRadius; offsetY++)
        {
            var row = (centerY + offsetY) * frame.Width;
            for (var offsetX = -BackgroundAnnulusOuterRadius; offsetX <= BackgroundAnnulusOuterRadius; offsetX++)
            {
                var radiusSquared = (offsetX * offsetX) + (offsetY * offsetY);
                if (radiusSquared < innerRadiusSquared || radiusSquared > outerRadiusSquared) continue;
                localBackgroundSamples[localBackgroundCount++] = frame.Pixels[row + centerX + offsetX];
            }
        }

        Array.Sort(localBackgroundSamples, 0, localBackgroundCount);
        var localBackground = PercentileSorted(localBackgroundSamples.AsSpan(0, localBackgroundCount), 0.5);
        var deviations = new double[localBackgroundCount];
        for (var index = 0; index < localBackgroundCount; index++)
        {
            deviations[index] = Math.Abs(localBackgroundSamples[index] - localBackground);
        }

        Array.Sort(deviations);
        var localSigma = Math.Max(1.0, 1.4826 * PercentileSorted(deviations, 0.5));
        var noiseSigma = Math.Max(globalSigma, localSigma);
        var requiredPeak = Math.Max(3, detectionSigma) * noiseSigma;
        var peak = frame.Pixels[(centerY * frame.Width) + centerX] - localBackground;
        if (peak < requiredPeak) return null;

        double signedFlux = 0;
        double positiveFlux = 0;
        double coreFlux = 0;
        double weightedX = 0;
        double weightedY = 0;
        var aperturePixelCount = 0;
        var coreRadiusSquared = 4;
        var apertureRadiusSquared = MeasurementRadius * MeasurementRadius;
        for (var offsetY = -MeasurementRadius; offsetY <= MeasurementRadius; offsetY++)
        {
            var row = (centerY + offsetY) * frame.Width;
            for (var offsetX = -MeasurementRadius; offsetX <= MeasurementRadius; offsetX++)
            {
                var radiusSquared = (offsetX * offsetX) + (offsetY * offsetY);
                if (radiusSquared > apertureRadiusSquared) continue;
                aperturePixelCount++;
                var excess = frame.Pixels[row + centerX + offsetX] - localBackground;
                signedFlux += excess;
                var weight = Math.Max(0, excess);
                positiveFlux += weight;
                if (radiusSquared <= coreRadiusSquared) coreFlux += weight;
                weightedX += weight * offsetX;
                weightedY += weight * offsetY;
            }
        }

        var integratedSignalToNoise = signedFlux / (noiseSigma * Math.Sqrt(aperturePixelCount));
        if (!double.IsFinite(integratedSignalToNoise) ||
            integratedSignalToNoise < Math.Max(MinimumIntegratedSignalToNoise, detectionSigma * 2))
        {
            return null;
        }

        if (!HasPsfConnectivity(frame, centerX, centerY, localBackground, noiseSigma, detectionSigma)) return null;
        if (positiveFlux <= 0) return null;
        var coreFluxFraction = coreFlux / positiveFlux;
        if (coreFluxFraction < MinimumCoreFluxFraction || coreFluxFraction > MaximumCoreFluxFraction) return null;

        var centroidX = weightedX / positiveFlux;
        var centroidY = weightedY / positiveFlux;
        double xx = 0;
        double yy = 0;
        double xy = 0;
        for (var offsetY = -MeasurementRadius; offsetY <= MeasurementRadius; offsetY++)
        {
            var row = (centerY + offsetY) * frame.Width;
            for (var offsetX = -MeasurementRadius; offsetX <= MeasurementRadius; offsetX++)
            {
                if ((offsetX * offsetX) + (offsetY * offsetY) > apertureRadiusSquared) continue;
                var weight = Math.Max(0, frame.Pixels[row + centerX + offsetX] - localBackground);
                var dx = offsetX - centroidX;
                var dy = offsetY - centroidY;
                xx += weight * dx * dx;
                yy += weight * dy * dy;
                xy += weight * dx * dy;
            }
        }

        xx /= positiveFlux;
        yy /= positiveFlux;
        xy /= positiveFlux;
        var trace = xx + yy;
        var discriminant = Math.Sqrt(Math.Max(0, ((xx - yy) * (xx - yy)) + (4 * xy * xy)));
        var major = Math.Max(0, (trace + discriminant) / 2);
        var minor = Math.Max(0, (trace - discriminant) / 2);
        if (major <= 0) return null;
        var fwhm = 2.35482 * Math.Sqrt(Math.Max(0.01, trace / 2));
        var ellipticity = 1 - Math.Sqrt(minor / major);
        if (!double.IsFinite(fwhm) || fwhm < MinimumFwhmPixels || fwhm > MaximumFwhmPixels) return null;
        if (!double.IsFinite(ellipticity) || ellipticity > MaximumEllipticity) return null;
        return new StarMeasurement(signedFlux, fwhm, Math.Clamp(ellipticity, 0, 1));
    }

    private static bool HasPsfConnectivity(
        QhyFrame frame,
        int centerX,
        int centerY,
        double background,
        double noiseSigma,
        double detectionSigma)
    {
        var diameter = (MeasurementRadius * 2) + 1;
        var significant = new bool[diameter * diameter];
        var connectivityThreshold = background + (Math.Max(2.5, detectionSigma * 0.5) * noiseSigma);
        for (var offsetY = -MeasurementRadius; offsetY <= MeasurementRadius; offsetY++)
        {
            var row = (centerY + offsetY) * frame.Width;
            for (var offsetX = -MeasurementRadius; offsetX <= MeasurementRadius; offsetX++)
            {
                if ((offsetX * offsetX) + (offsetY * offsetY) > MeasurementRadius * MeasurementRadius) continue;
                var localX = offsetX + MeasurementRadius;
                var localY = offsetY + MeasurementRadius;
                significant[(localY * diameter) + localX] =
                    frame.Pixels[row + centerX + offsetX] > connectivityThreshold;
            }
        }

        var centerIndex = (MeasurementRadius * diameter) + MeasurementRadius;
        if (!significant[centerIndex]) return false;
        var visited = new bool[significant.Length];
        var queue = new Queue<int>();
        queue.Enqueue(centerIndex);
        visited[centerIndex] = true;
        var connectedCount = 0;
        var minimumX = MeasurementRadius;
        var maximumX = MeasurementRadius;
        var minimumY = MeasurementRadius;
        var maximumY = MeasurementRadius;
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var currentX = current % diameter;
            var currentY = current / diameter;
            connectedCount++;
            minimumX = Math.Min(minimumX, currentX);
            maximumX = Math.Max(maximumX, currentX);
            minimumY = Math.Min(minimumY, currentY);
            maximumY = Math.Max(maximumY, currentY);

            for (var deltaY = -1; deltaY <= 1; deltaY++)
            {
                for (var deltaX = -1; deltaX <= 1; deltaX++)
                {
                    if (deltaX == 0 && deltaY == 0) continue;
                    var nextX = currentX + deltaX;
                    var nextY = currentY + deltaY;
                    if (nextX < 0 || nextX >= diameter || nextY < 0 || nextY >= diameter) continue;
                    var next = (nextY * diameter) + nextX;
                    if (visited[next] || !significant[next]) continue;
                    visited[next] = true;
                    queue.Enqueue(next);
                }
            }
        }

        return connectedCount >= 4 && maximumX > minimumX && maximumY > minimumY;
    }

    private static double PercentileSorted(ReadOnlySpan<double> sorted, double fraction)
    {
        if (sorted.Length == 0) return double.NaN;
        var position = Math.Clamp(fraction, 0, 1) * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        var weight = position - lower;
        return (sorted[lower] * (1 - weight)) + (sorted[upper] * weight);
    }

    private static double? MedianNullable(IEnumerable<double> source)
    {
        var values = source.Where(double.IsFinite).Order().ToArray();
        if (values.Length == 0) return null;
        var middle = values.Length / 2;
        return values.Length % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
    }

    private sealed record StarMeasurement(double Flux, double Fwhm, double Ellipticity);
}
