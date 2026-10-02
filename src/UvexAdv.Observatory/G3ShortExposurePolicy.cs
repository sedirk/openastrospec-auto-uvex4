namespace UvexAdv.Observatory;

/// <summary>Image-only exposure search, never a change to commissioned guiding
/// settings or motion budgets. The caller must validate actual capture readback.</summary>
public sealed record G3ShortExposurePolicy(int ExposureMilliseconds, int MaximumFrames = 3, int Reductions = 0)
{
    public int BlendExposureIncreases { get; init; }
    public int SignalExposureIncreases { get; init; }
    public const int MaximumBlendExposureMilliseconds = 200;
    public const int MaximumSignalExposureMilliseconds = 5_000;
    public const int MaximumSignalExposureIncreases = 3;
    public const int AbsoluteMaximumFrames = 6;
    public const int MaximumReductions = 3;
    public const int MinimumExposureMilliseconds = 1;
    public const string SaturatedCode = "G3_SEP_SATURATED";
    public const string Version = "sep-short-exposure-v3-signal-reprobe";

    /// <summary>No measurement is not proof that the source is absent. Search
    /// exposure only, below both the attested solve exposure and a fixed cap.
    /// Identity/ambiguity/coverage failures cannot acquire this retry path.</summary>
    public G3ShortExposurePolicy AfterUnmeasured(G3ShortPositionMeasurement measurement, int attempt,
        double rawRoiPeak, double saturationLevel, double sourceExposureMilliseconds)
    {
        _ = AfterMeasurement("validate-only", attempt);
        if (SignalExposureIncreases is < 0 or > MaximumSignalExposureIncreases)
            throw new ArgumentOutOfRangeException(nameof(SignalExposureIncreases));
        if (measurement.Gate.Code != "G3_SEP_UNMEASURED"
            || measurement.Gate.Disposition != GateDisposition.Indeterminate
            || measurement.Identification.Target is not null
            || measurement.Gate.Metrics is not { } metrics
            || !metrics.TryGetValue("sepValidRoiParents", out var parents) || parents != 0
            || !metrics.TryGetValue("sepSaturatedRoiComponents", out var saturated) || saturated != 0
            || Reductions != 0 || BlendExposureIncreases != 0
            || SignalExposureIncreases == MaximumSignalExposureIncreases || attempt > AbsoluteMaximumFrames - 2
            || !double.IsFinite(rawRoiPeak) || rawRoiPeak <= 0
            || !double.IsFinite(saturationLevel) || saturationLevel <= rawRoiPeak
            || !double.IsFinite(sourceExposureMilliseconds) || sourceExposureMilliseconds <= ExposureMilliseconds)
            return this;
        // Conservative headroom includes bias and ALL raw pixels in the WCS
        // window, not only SEP-detected sources (there may be none). Never
        // extrapolate between the solve-frame gain and short-frame gain.
        var factor = Math.Min(10, .8 * saturationLevel / rawRoiPeak);
        var next = (int)Math.Floor(Math.Min(Math.Min(MaximumSignalExposureMilliseconds,
            sourceExposureMilliseconds), ExposureMilliseconds * factor));
        if (next < 2L * ExposureMilliseconds) return this;
        return this with { ExposureMilliseconds = next, SignalExposureIncreases = SignalExposureIncreases + 1,
            MaximumFrames = Math.Min(AbsoluteMaximumFrames, Math.Max(MaximumFrames, attempt + 3)) };
    }

    public static double RecognitionPeak(MonochromeFrame frame, PixelPoint prediction, double radius)
    {
        // Missing coverage cannot be treated as a dark image.
        if (!double.IsFinite(radius) || radius <= 0 || !double.IsFinite(prediction.X) || !double.IsFinite(prediction.Y)
            || prediction.X - radius < 0 || prediction.Y - radius < 0
            || prediction.X + radius > frame.Width - 1 || prediction.Y + radius > frame.Height - 1)
            return double.NaN;
        var peak = 0d;
        for (var y = (int)Math.Floor(prediction.Y - radius); y <= Math.Ceiling(prediction.Y + radius); y++)
        for (var x = (int)Math.Floor(prediction.X - radius); x <= Math.Ceiling(prediction.X + radius); x++)
            if (Math.Pow(x - prediction.X, 2) + Math.Pow(y - prediction.Y, 2) <= radius * radius)
                peak = Math.Max(peak, frame[x, y]);
        return peak;
    }

    public G3ShortExposurePolicy AfterMeasurement(string code, int attempt)
    {
        if (ExposureMilliseconds < MinimumExposureMilliseconds || MaximumFrames is < 3 or > AbsoluteMaximumFrames
            || Reductions is < 0 or > MaximumReductions || attempt < 1 || attempt > MaximumFrames)
            throw new ArgumentOutOfRangeException(nameof(attempt));
        if (code != SaturatedCode || ExposureMilliseconds == MinimumExposureMilliseconds
            || Reductions == MaximumReductions || attempt > AbsoluteMaximumFrames - 2)
            return this;
        // Reserve two fresh matching frames, with one missing-image allowance.
        // Saturation extends the ordinary window; the separate blend re-probe
        // also shares this absolute cap rather than resetting it.
        return this with { ExposureMilliseconds = Math.Max(MinimumExposureMilliseconds, ExposureMilliseconds / 2),
            MaximumFrames = Math.Min(AbsoluteMaximumFrames, Math.Max(MaximumFrames, attempt + 3)), Reductions = Reductions + 1 };
    }

    /// <summary>Persistent sub-peaks in very short seeing/noise-limited frames
    /// deserve one longer image-only probe, not a blend veto bypass. The new
    /// exposure needs a NEW pair and the unchanged unsplit/identity proof.
    /// No increase after saturation, no oscillation and no unbounded frames.</summary>
    public G3ShortExposurePolicy AfterConfirmation(G3ShortPositionConfirmationDecision confirmation,
        G3ShortPositionMeasurement measurement, int attempt, double saturationLevel)
    {
        // Apply the same argument bounds even when this branch is inapplicable.
        _ = AfterMeasurement("validate-only", attempt);
        if (BlendExposureIncreases is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(BlendExposureIncreases));
        if (confirmation.Accepted || confirmation.Gate.Code != "G3_SEP_PARENT_BLEND_UNRESOLVED"
            || measurement.Gate.Disposition != GateDisposition.Passed || measurement.Identification.Target is not { } target
            || !G3SepShortPositionPolicy.IsSplitParent(measurement)
            || Reductions != 0 || BlendExposureIncreases != 0 || attempt < 2 || attempt > AbsoluteMaximumFrames - 2
            || ExposureMilliseconds >= MaximumBlendExposureMilliseconds
            || !double.IsFinite(saturationLevel) || saturationLevel <= 0
            || !double.IsFinite(target.PeakAdu) || target.PeakAdu <= 0 || target.SaturatedFraction != 0
            || !double.IsFinite(confirmation.PositionSpreadPixels)
            || confirmation.PositionSpreadPixels > G3ShortPositionMeasurementPolicy.MaximumRepeatSeparationPixels)
            return this;
        var next = Math.Min(MaximumBlendExposureMilliseconds, ExposureMilliseconds * 4);
        // Conservative headroom: scale the whole raw peak INCLUDING bias. Do
        // not extrapolate a guessed background or silently clip the next image.
        if (target.PeakAdu * ((double)next / ExposureMilliseconds) > .8 * saturationLevel) return this;
        return this with { ExposureMilliseconds = next, BlendExposureIncreases = 1,
            MaximumFrames = Math.Min(AbsoluteMaximumFrames, Math.Max(MaximumFrames, attempt + 3)) };
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
