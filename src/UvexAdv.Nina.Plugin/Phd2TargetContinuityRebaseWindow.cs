using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

// The old target/primary translation can stay fixed while a saturated or
// defocused target centroid settles at a nearby, repeatable detector position.
// Three independent fresh measurements may replace only that search baseline.
// They do not prove motion, create movement credit, or consume a new budget.
internal sealed class Phd2TargetContinuityRebaseWindow
{
    internal const int RequiredSamples = 3;
    internal const double MaximumPairwiseSpreadPixels = 3;

    private readonly List<PixelPoint> samples = new(RequiredSamples);

    internal Phd2TargetContinuityRebaseWindow(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A continuity-rebase window key is required.", nameof(key));
        Key = key;
    }

    internal string Key { get; }
    internal int SampleCount => samples.Count;
    internal bool Consumed { get; private set; }

    internal bool Observe(PixelPoint sample, out PixelPoint rebased,
        out double maximumPairwiseSpreadPixels)
    {
        rebased = new PixelPoint(double.NaN, double.NaN);
        maximumPairwiseSpreadPixels = double.NaN;
        if (Consumed) return false;
        if (!double.IsFinite(sample.X) || !double.IsFinite(sample.Y))
        {
            samples.Clear();
            return false;
        }

        samples.Add(sample);
        if (samples.Count < RequiredSamples) return false;

        maximumPairwiseSpreadPixels = MaximumPairwiseDistance(samples);
        if (!double.IsFinite(maximumPairwiseSpreadPixels) ||
            maximumPairwiseSpreadPixels > MaximumPairwiseSpreadPixels)
        {
            samples.Clear();
            return false;
        }

        rebased = new PixelPoint(
            Median(samples.Select(point => point.X)),
            Median(samples.Select(point => point.Y)));
        Consumed = true;
        return true;
    }

    private static double MaximumPairwiseDistance(IReadOnlyList<PixelPoint> points)
    {
        var maximum = 0d;
        for (var i = 0; i < points.Count; i++)
        for (var j = i + 1; j < points.Count; j++)
            maximum = Math.Max(maximum, Distance(points[i], points[j]));
        return maximum;
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        return ordered.Length % 2 == 1
            ? ordered[ordered.Length / 2]
            : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2;
    }

    private static double Distance(PixelPoint a, PixelPoint b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
