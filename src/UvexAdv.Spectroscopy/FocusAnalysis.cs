namespace UvexAdv.Spectroscopy;

public sealed record FocusMetric(double FwhmPixels, int ValidLineCount, double Confidence, IReadOnlyList<SpectralLineMeasurement> Lines);

public sealed record FocusSample(int PositionSteps, FocusMetric Metric);

public sealed record FocusFit(
    double OptimumPositionSteps,
    double Curvature,
    double RSquared,
    bool IsValid,
    string? FailureReason = null);

public static class FocusMetricCalculator
{
    public static FocusMetric Calculate(Spectrum1D spectrum, IEnumerable<SpectralLineWindow> windows, int minimumLines = 3)
    {
        var lines = windows.Select(window => SpectralLineMeasurer.Measure(spectrum, window)).ToArray();
        var valid = lines.Where(static line => line.IsValid).ToArray();
        if (valid.Length < minimumLines)
        {
            return new FocusMetric(double.NaN, valid.Length, 0, lines);
        }

        var metric = RobustStatistics.WeightedMedian(valid.Select(line => (line.FwhmPixels, Math.Min(line.SignalToNoise, 100))));
        var confidence = Math.Clamp(valid.Length / (double)Math.Max(minimumLines, lines.Length), 0, 1);
        return new FocusMetric(metric, valid.Length, confidence, lines);
    }
}

public static class FocusCurveFitter
{
    public static FocusFit Fit(IReadOnlyList<FocusSample> samples)
    {
        var valid = samples.Where(sample => double.IsFinite(sample.Metric.FwhmPixels)).ToArray();
        if (valid.Length < 5)
        {
            return new FocusFit(double.NaN, double.NaN, 0, false, "At least five valid focus samples are required.");
        }

        var origin = valid.Average(static sample => sample.PositionSteps);
        var x = valid.Select(sample => sample.PositionSteps - origin).ToArray();
        var y = valid.Select(static sample => sample.Metric.FwhmPixels).ToArray();
        var coefficients = LeastSquares.Quadratic(x, y);
        var (c, b, a) = (coefficients[0], coefficients[1], coefficients[2]);
        if (a <= 0 || !double.IsFinite(a))
        {
            return new FocusFit(double.NaN, a, 0, false, "Focus curve does not have a positive minimum.");
        }

        var optimum = origin - (b / (2 * a));
        var minimum = valid.Min(static sample => sample.PositionSteps);
        var maximum = valid.Max(static sample => sample.PositionSteps);
        if (optimum < minimum || optimum > maximum)
        {
            return new FocusFit(optimum, a, 0, false, "Fitted optimum lies outside the sampled range.");
        }

        var mean = y.Average();
        var residual = x.Select((value, index) => y[index] - (c + (b * value) + (a * value * value))).ToArray();
        var ssResidual = residual.Sum(static value => value * value);
        var ssTotal = y.Sum(value => (value - mean) * (value - mean));
        var rSquared = ssTotal <= 1e-12 ? 0 : 1 - (ssResidual / ssTotal);
        var validFit = rSquared >= 0.8;
        return new FocusFit(optimum, a, rSquared, validFit, validFit ? null : "Focus fit R-squared is below 0.8.");
    }
}

public static class FocusSamplingPlan
{
    public static IReadOnlyList<int> Symmetric(int centerSteps, int stepSize, int sampleCount = 7)
    {
        if (stepSize <= 0 || sampleCount < 5 || sampleCount % 2 == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount), "Use an odd sample count of at least five and a positive step size.");
        }

        var half = sampleCount / 2;
        return Enumerable.Range(-half, sampleCount).Select(offset => checked(centerSteps + (offset * stepSize))).ToArray();
    }
}
