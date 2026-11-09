namespace UvexAdv.Observatory;

/// <summary>Bounded image-only search after a small WCS correction. Never changes guiding settings.</summary>
public sealed record G3PostWcsShortExposurePolicy(int ExposureMilliseconds, int CeilingMilliseconds,
    int Adjustments = 0, bool SaturationSeen = false)
{
    public const int MaximumFrames = 6;
    public const int MaximumFramesWithoutSaturation = 4;
    public const int AbsoluteMaximumCeilingMilliseconds = 5_000;
    public const double PreferredMeasuredSignalToNoise = 30;
    public bool ExtendedSignalRange { get; init; }

    public static G3PostWcsShortExposurePolicy Start(int commissionedMilliseconds,
        int ceilingMilliseconds = 0)
    {
        if (commissionedMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(commissionedMilliseconds));
        if (ceilingMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(ceilingMilliseconds));
        var ceiling = Math.Min(AbsoluteMaximumCeilingMilliseconds,
            Math.Max(commissionedMilliseconds, Math.Max(100, ceilingMilliseconds)));
        return new(commissionedMilliseconds, ceiling)
        {
            ExtendedSignalRange = ceiling > Math.Max(100, commissionedMilliseconds),
        };
    }

    public G3PostWcsShortExposurePolicy AfterMeasurement(string code, int attempt)
    {
        if (attempt < 1 || attempt > MaximumFrames) throw new ArgumentOutOfRangeException(nameof(attempt));
        // Low-signal augmentation remains bounded but may traverse the
        // already commissioned short-to-off-slit exposure range. Saturation
        // reductions retain their stricter path below.
        var maximumAdjustments = ExtendedSignalRange ? MaximumFramesWithoutSaturation : 2;
        if (Adjustments >= maximumAdjustments || attempt > MaximumFrames - 2) return this;
        if (code == G3ShortExposurePolicy.SaturatedCode && ExposureMilliseconds > 1)
            return this with { ExposureMilliseconds = Math.Max(1, ExposureMilliseconds / 2),
                Adjustments = Adjustments + 1, SaturationSeen = true };
        if (!SaturationSeen && code is ("G3_SEP_UNMEASURED" or "G3_SEP_AMBIGUOUS")
            && ExposureMilliseconds < CeilingMilliseconds)
        {
            // Cover the declared range before the last independent pair. A
            // constant x3 stopped at 810ms for a 2000ms ceiling, despite claiming
            // to search the whole range. No extra frame or ceiling is granted.
            var remainingAdjustments = Math.Min(maximumAdjustments - Adjustments, MaximumFrames - 1 - attempt);
            var next = ExtendedSignalRange
                ? Math.Max(3d * ExposureMilliseconds, Math.Ceiling(ExposureMilliseconds *
                    Math.Pow((double)CeilingMilliseconds / ExposureMilliseconds, 1d / remainingAdjustments)))
                : 3d * ExposureMilliseconds;
            return this with { ExposureMilliseconds = (int)Math.Min(CeilingMilliseconds, next), Adjustments = Adjustments + 1 };
        }
        return this;
    }

    public G3PostWcsShortExposurePolicy AfterConfirmation(string measurementCode,
        G3ShortPositionConfirmationDecision confirmation, int attempt,
        double? targetSignalToNoise = null)
    {
        if (confirmation.Accepted || !confirmation.RetryAllowed) return this;
        // A disagreement between two valid centroids is not a signal deficit.
        // Repeating the same exposure gives an independent position without
        // driving a bright target straight into saturation.
        if (confirmation.Gate.Code == "G3_SHORT_REPEAT_DISAGREES") return this;
        // A valid but weak position cannot prove a repeat. Use the remaining
        // frames at a stronger exposure instead of spending both on a known
        // marginal setting. The original ceiling, adjustment count and total
        // six-frame bound are unchanged.
        if (measurementCode == "G3_SEP_POSITION_MEASURED" &&
            confirmation.Gate.Code == "G3_SHORT_REPEAT_REQUIRED" &&
            targetSignalToNoise is > 0 and < PreferredMeasuredSignalToNoise)
            return AfterMeasurement("G3_SEP_UNMEASURED", attempt);
        // A centroid in each frame is not a confirmed position. HD 21455 had
        // two unsaturated centroids but split/disagreeing profiles: looking only
        // at POSITION_MEASURED repeated the weak setting until all six frames
        // were spent. Seek a stronger independent pair within the same caps;
        // never merge components or choose a brighter companion to pass.
        var code = measurementCode == "G3_SEP_POSITION_MEASURED" &&
            confirmation.Gate.Code is "G3_SHORT_REPEAT_DISAGREES" or "G3_SEP_PARENT_BLEND_UNRESOLVED"
                ? "G3_SEP_UNMEASURED" : measurementCode;
        return AfterMeasurement(code, attempt);
    }
}
