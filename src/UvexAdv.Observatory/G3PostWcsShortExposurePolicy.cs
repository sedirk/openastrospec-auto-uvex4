namespace UvexAdv.Observatory;

/// <summary>Bounded image-only search after a small WCS correction. Never changes guiding settings.</summary>
public sealed record G3PostWcsShortExposurePolicy(int ExposureMilliseconds, int CeilingMilliseconds,
    int Adjustments = 0, bool SaturationSeen = false)
{
    public const int MaximumFrames = 6;
    public static G3PostWcsShortExposurePolicy Start(int commissionedMilliseconds) =>
        commissionedMilliseconds > 0
            ? new(commissionedMilliseconds, Math.Max(100, commissionedMilliseconds))
            : throw new ArgumentOutOfRangeException(nameof(commissionedMilliseconds));

    public G3PostWcsShortExposurePolicy AfterMeasurement(string code, int attempt)
    {
        if (attempt < 1 || attempt > MaximumFrames) throw new ArgumentOutOfRangeException(nameof(attempt));
        // At most two adjustments; reserve two independent frames at the new setting.
        if (Adjustments >= 2 || attempt > MaximumFrames - 2) return this;
        if (code == G3ShortExposurePolicy.SaturatedCode && ExposureMilliseconds > 1)
            return this with { ExposureMilliseconds = Math.Max(1, ExposureMilliseconds / 2),
                Adjustments = Adjustments + 1, SaturationSeen = true };
        if (!SaturationSeen && code is ("G3_SEP_UNMEASURED" or "G3_SEP_AMBIGUOUS")
            && ExposureMilliseconds < CeilingMilliseconds)
            return this with { ExposureMilliseconds = (int)Math.Min(CeilingMilliseconds, 3L * ExposureMilliseconds),
                Adjustments = Adjustments + 1 };
        return this;
    }
}
