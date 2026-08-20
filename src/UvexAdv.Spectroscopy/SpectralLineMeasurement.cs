namespace UvexAdv.Spectroscopy;

public enum SpectralLinePolarity
{
    Auto,
    Emission,
    Absorption,
}

public sealed record SpectralLineWindow(
    double ExpectedPixel,
    int HalfWidth = 12,
    SpectralLinePolarity Polarity = SpectralLinePolarity.Auto);

public sealed record SpectralLineMeasurement(
    double CentroidPixel,
    double FwhmPixels,
    double SignalToNoise,
    double Peak,
    double IntegratedFlux,
    bool IsValid,
    string? FailureReason = null,
    SpectralLinePolarity Polarity = SpectralLinePolarity.Auto);

public static class SpectralLineMeasurer
{
    public static SpectralLineMeasurement Measure(Spectrum1D spectrum, SpectralLineWindow window, double minimumSnr = 10)
    {
        var center = (int)Math.Round(window.ExpectedPixel);
        var start = Math.Max(0, center - window.HalfWidth);
        var end = Math.Min(spectrum.Flux.Length - 1, center + window.HalfWidth);
        if (end - start < 6)
        {
            return Invalid("Line window is outside the spectrum.");
        }

        var edge = Math.Max(2, (end - start + 1) / 5);
        var leftIndices = Enumerable.Range(start, edge).ToArray();
        var rightIndices = Enumerable.Range(end - edge + 1, edge).ToArray();
        var leftBaseline = RobustStatistics.Median(leftIndices.Select(index => spectrum.Flux[index]));
        var rightBaseline = RobustStatistics.Median(rightIndices.Select(index => spectrum.Flux[index]));
        var leftX = leftIndices.Average();
        var rightX = rightIndices.Average();
        double BaselineAt(int index) => leftBaseline +
            ((rightBaseline - leftBaseline) * (index - leftX) / Math.Max(1, rightX - leftX));

        var baselineResiduals = leftIndices.Concat(rightIndices)
            .Select(index => spectrum.Flux[index] - BaselineAt(index))
            .ToArray();
        var residualCenter = RobustStatistics.Median(baselineResiduals);
        var noiseMad = RobustStatistics.MedianAbsoluteDeviation(baselineResiduals, residualCenter);
        var noise = Math.Max(1e-9, noiseMad * 1.4826);

        var residuals = Enumerable.Range(start, end - start + 1)
            .Select(index => (Index: index, Residual: spectrum.Flux[index] - BaselineAt(index)))
            .ToArray();
        var emissionPeak = residuals.Max(static sample => sample.Residual);
        var absorptionPeak = -residuals.Min(static sample => sample.Residual);
        var polarity = window.Polarity == SpectralLinePolarity.Auto
            ? (absorptionPeak > emissionPeak ? SpectralLinePolarity.Absorption : SpectralLinePolarity.Emission)
            : window.Polarity;
        var samples = residuals
            .Select(sample => (
                sample.Index,
                Value: Math.Max(0, polarity == SpectralLinePolarity.Absorption ? -sample.Residual : sample.Residual)))
            .ToArray();
        var integrated = samples.Sum(static sample => sample.Value);
        if (integrated <= 0)
        {
            return Invalid("No line signal was found for the selected polarity.", polarity: polarity);
        }

        var centroid = samples.Sum(sample => sample.Index * sample.Value) / integrated;
        var peak = samples.Max(static sample => sample.Value);
        var snr = peak / noise;
        if (snr < minimumSnr)
        {
            return Invalid($"Line SNR {snr:F2} is below {minimumSnr:F2}.", centroid, snr, peak, integrated, polarity);
        }

        var half = peak / 2;
        var peakIndex = Array.FindIndex(samples, sample => sample.Value == peak);
        var left = FindCrossing(samples, peakIndex, -1, half);
        var right = FindCrossing(samples, peakIndex, 1, half);
        if (!double.IsFinite(left) || !double.IsFinite(right) || right <= left)
        {
            return Invalid("FWHM crossings were not found.", centroid, snr, peak, integrated, polarity);
        }

        return new SpectralLineMeasurement(centroid, right - left, snr, peak, integrated, true, Polarity: polarity);
    }

    private static double FindCrossing((int Index, double Value)[] samples, int peakIndex, int direction, double half)
    {
        var i = peakIndex;
        while (i + direction >= 0 && i + direction < samples.Length)
        {
            var next = i + direction;
            if (samples[next].Value <= half)
            {
                var a = samples[i];
                var b = samples[next];
                var denominator = b.Value - a.Value;
                if (Math.Abs(denominator) < 1e-12)
                {
                    return (a.Index + b.Index) / 2d;
                }

                return a.Index + ((half - a.Value) * (b.Index - a.Index) / denominator);
            }

            i = next;
        }

        return double.NaN;
    }

    private static SpectralLineMeasurement Invalid(
        string reason,
        double centroid = double.NaN,
        double snr = 0,
        double peak = 0,
        double integrated = 0,
        SpectralLinePolarity polarity = SpectralLinePolarity.Auto) =>
        new(centroid, double.NaN, snr, peak, integrated, false, reason, polarity);
}
