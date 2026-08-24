namespace UvexAdv.Spectroscopy;

/// <summary>
/// Quality measurements taken from the spatially compact spectral trace rather
/// than from the full detector. A narrow trace can clip thousands of wavelength
/// columns while contributing a deceptively small fraction of all image pixels.
/// </summary>
public sealed record SpectralTraceQuality(
    double BiasLevelAdu,
    double HighPercentileAdu,
    double FullFrameSaturatedFraction,
    double TraceSaturatedFraction,
    double ClippedDispersionColumnFraction,
    int LongestClippedDispersionColumnRun,
    int TraceSpatialCenterPixel,
    int TraceSpatialHalfWidthPixels,
    double ContinuumSnrPerResolutionElement,
    double LineSnrPerResolutionElement,
    double TargetToSkyContrast);

public static class SpectralTraceQualityAnalyzer
{
    private const int MaximumGlobalSamples = 250_000;
    private const int MaximumRowSamples = 1_024;
    private const int MinimumTraceHalfWidthPixels = 4;
    private const int MaximumTraceHalfWidthPixels = 24;

    public static SpectralTraceQuality Analyze(
        SpectralImage image,
        ImageRoi roi,
        DispersionAxis axis,
        int apertureStart,
        int apertureLength)
    {
        ArgumentNullException.ThrowIfNull(image);
        roi.Validate(image.Width, image.Height);
        var crossLength = axis == DispersionAxis.Horizontal ? roi.Height : roi.Width;
        var dispersionLength = axis == DispersionAxis.Horizontal ? roi.Width : roi.Height;
        var effectiveApertureLength = apertureLength <= 0 ? crossLength : apertureLength;
        if (apertureStart < 0 || effectiveApertureLength < 1 || apertureStart + effectiveApertureLength > crossLength)
        {
            throw new ArgumentOutOfRangeException(nameof(apertureLength), "Trace-search aperture is outside the ROI.");
        }

        var globalSample = new List<double>(Math.Min(MaximumGlobalSamples, checked(roi.Width * roi.Height)));
        var globalStride = Math.Max(1, checked(roi.Width * roi.Height) / MaximumGlobalSamples);
        var globalCounter = 0;
        var fullFrameClipped = 0L;
        var fullFrameTotal = 0L;
        var clipThreshold = image.SaturationLevel * 0.999;
        for (var y = roi.Y; y < roi.Y + roi.Height; y++)
        {
            for (var x = roi.X; x < roi.X + roi.Width; x++)
            {
                var value = image[x, y];
                if (value >= clipThreshold) fullFrameClipped++;
                if (globalCounter++ % globalStride == 0) globalSample.Add(value);
                fullFrameTotal++;
            }
        }

        globalSample.Sort();
        var bias = Percentile(globalSample, 0.5);
        var globalDeviations = globalSample
            .Select(value => Math.Abs(value - bias))
            .OrderBy(static value => value)
            .ToArray();
        var noiseSigma = Math.Max(1, Percentile(globalDeviations, 0.5) * 1.4826);

        // A row/column score uses the 90th percentile along dispersion. This
        // rejects isolated hot pixels while retaining ordinary stellar continua
        // and emission spectra that occupy a meaningful part of the detector.
        var spatialScores = new double[effectiveApertureLength];
        var dispersionStride = Math.Max(1, dispersionLength / MaximumRowSamples);
        for (var localCross = 0; localCross < effectiveApertureLength; localCross++)
        {
            var cross = apertureStart + localCross;
            var row = new List<double>((dispersionLength + dispersionStride - 1) / dispersionStride);
            for (var dispersion = 0; dispersion < dispersionLength; dispersion += dispersionStride)
            {
                row.Add(GetPixel(image, roi, axis, dispersion, cross));
            }
            row.Sort();
            spatialScores[localCross] = Percentile(row, 0.9);
        }

        var centerLocal = Array.IndexOf(spatialScores, spatialScores.Max());
        var sortedScores = spatialScores.OrderBy(static value => value).ToArray();
        var scoreBaseline = Percentile(sortedScores, 0.5);
        var peakProminence = Math.Max(0, spatialScores[centerLocal] - scoreBaseline);
        var traceThreshold = scoreBaseline + (peakProminence * 0.10);
        var lower = centerLocal;
        var upper = centerLocal;
        while (lower > 0 && spatialScores[lower - 1] >= traceThreshold) lower--;
        while (upper + 1 < spatialScores.Length && spatialScores[upper + 1] >= traceThreshold) upper++;
        lower = Math.Max(0, lower - 2);
        upper = Math.Min(spatialScores.Length - 1, upper + 2);
        var inferredHalfWidth = Math.Max(centerLocal - lower, upper - centerLocal);
        var halfWidth = Math.Clamp(inferredHalfWidth, MinimumTraceHalfWidthPixels, MaximumTraceHalfWidthPixels);
        lower = Math.Max(0, centerLocal - halfWidth);
        upper = Math.Min(spatialScores.Length - 1, centerLocal + halfWidth);

        var traceSample = new List<double>(checked((upper - lower + 1) * dispersionLength));
        var traceClipped = 0L;
        var clippedColumns = 0;
        var longestClippedRun = 0;
        var currentClippedRun = 0;
        for (var dispersion = 0; dispersion < dispersionLength; dispersion++)
        {
            var columnClipped = false;
            for (var localCross = lower; localCross <= upper; localCross++)
            {
                var value = GetPixel(image, roi, axis, dispersion, apertureStart + localCross);
                traceSample.Add(value);
                if (value < clipThreshold) continue;
                traceClipped++;
                columnClipped = true;
            }

            if (columnClipped)
            {
                clippedColumns++;
                currentClippedRun++;
                longestClippedRun = Math.Max(longestClippedRun, currentClippedRun);
            }
            else
            {
                currentClippedRun = 0;
            }
        }

        traceSample.Sort();
        var p90 = Percentile(traceSample, 0.9);
        var p999 = Percentile(traceSample, 0.999);
        var continuumSnr = Math.Max(0, (p90 - bias) / noiseSigma);
        var lineSnr = Math.Max(0, (p999 - bias) / noiseSigma);
        var contrast = bias > 0 ? p90 / bias : 0;
        return new SpectralTraceQuality(
            bias,
            p999,
            fullFrameClipped / (double)Math.Max(1, fullFrameTotal),
            traceClipped / (double)Math.Max(1, traceSample.Count),
            clippedColumns / (double)Math.Max(1, dispersionLength),
            longestClippedRun,
            apertureStart + centerLocal,
            halfWidth,
            continuumSnr,
            lineSnr,
            contrast);
    }

    private static double GetPixel(
        SpectralImage image,
        ImageRoi roi,
        DispersionAxis axis,
        int dispersion,
        int cross) =>
        axis == DispersionAxis.Horizontal
            ? image[roi.X + dispersion, roi.Y + cross]
            : image[roi.X + cross, roi.Y + dispersion];

    private static double Percentile(IReadOnlyList<double> sorted, double fraction)
    {
        if (sorted.Count == 0) return 0;
        var position = Math.Clamp(fraction, 0, 1) * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(sorted.Count - 1, lower + 1);
        var weight = position - lower;
        return (sorted[lower] * (1 - weight)) + (sorted[upper] * weight);
    }
}
