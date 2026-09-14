namespace UvexAdv.Observatory;

/// <summary>Image-only exposure search, never a change to commissioned guiding
/// settings or motion budgets. The caller must validate actual capture readback.</summary>
public sealed record G3ShortExposurePolicy(int ExposureMilliseconds, int MaximumFrames = 3, int Reductions = 0)
{
    public const int AbsoluteMaximumFrames = 6;
    public const int MaximumReductions = 3;
    public const int MinimumExposureMilliseconds = 1;
    public const string SaturatedCode = "G3_SEP_SATURATED";
    public const string Version = "sep-short-exposure-v1";

    public G3ShortExposurePolicy AfterMeasurement(string code, int attempt)
    {
        if (ExposureMilliseconds < MinimumExposureMilliseconds || MaximumFrames is < 3 or > AbsoluteMaximumFrames
            || Reductions is < 0 or > MaximumReductions || attempt < 1 || attempt > MaximumFrames)
            throw new ArgumentOutOfRangeException(nameof(attempt));
        if (code != SaturatedCode || ExposureMilliseconds == MinimumExposureMilliseconds
            || Reductions == MaximumReductions || attempt > AbsoluteMaximumFrames - 2)
            return this;
        // Reserve two fresh matching frames, with one missing-image allowance.
        // Only measured saturation extends the ordinary three-frame window.
        return new(Math.Max(MinimumExposureMilliseconds, ExposureMilliseconds / 2),
            Math.Min(AbsoluteMaximumFrames, Math.Max(MaximumFrames, attempt + 3)), Reductions + 1);
    }

    public static bool RecordSaturation(IEnumerable<SepPositionComponent> components, PixelPoint prediction,
        double radius, double minimumSnr, IDictionary<string, double> metrics)
    {
        // Retain rejected strong regions as association vetoes. Filtering them
        // away must not make an unrelated weak component look uniquely valid.
        var clipped = components.Where(c => c.SaturatedFraction > 0 && c.Flux > 0 && c.Snr >= minimumSnr
            && c.Npix >= 9 && c.RawSupportPixels >= 3
            && Math.Pow(c.X - prediction.X, 2) + Math.Pow(c.Y - prediction.Y, 2) <= radius * radius).ToArray();
        metrics["sepSaturatedRoiComponents"] = clipped.Length;
        metrics["sepMaximumRoiSaturationFraction"] = clipped.Select(c => c.SaturatedFraction).DefaultIfEmpty().Max();
        metrics["sepSaturatedRoiPeakAdu"] = clipped.Select(c => c.Peak).DefaultIfEmpty().Max();
        metrics["sepSaturatedRoiMaximumSnr"] = clipped.Select(c => c.Snr).DefaultIfEmpty().Max();
        return clipped.Length > 0;
    }
}
