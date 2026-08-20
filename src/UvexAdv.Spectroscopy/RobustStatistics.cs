namespace UvexAdv.Spectroscopy;

internal static class RobustStatistics
{
    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(double.IsFinite).Order().ToArray();
        if (sorted.Length == 0)
        {
            return double.NaN;
        }

        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }

    public static double MedianAbsoluteDeviation(IEnumerable<double> values, double median)
    {
        return Median(values.Select(value => Math.Abs(value - median)));
    }

    public static double WeightedMedian(IEnumerable<(double Value, double Weight)> values)
    {
        var sorted = values.Where(static item => double.IsFinite(item.Value) && item.Weight > 0)
            .OrderBy(static item => item.Value)
            .ToArray();
        var total = sorted.Sum(static item => item.Weight);
        if (total <= 0)
        {
            return double.NaN;
        }

        var cumulative = 0d;
        foreach (var item in sorted)
        {
            cumulative += item.Weight;
            if (cumulative >= total / 2)
            {
                return item.Value;
            }
        }

        return sorted[^1].Value;
    }
}
