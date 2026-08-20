namespace UvexAdv.Observatory;

/// <summary>
/// Independently versioned motion envelope for QHY/GS350 wide-field
/// centering.  It is intentionally separate from the G3/slit fine-motion
/// envelope because a normal post-slew wide-field residual can be hundreds of
/// arcseconds while slit corrections must remain much smaller.
/// </summary>
public sealed record QhyCoarseCenteringLimits(
    int SchemaVersion,
    double MaximumSingleCorrectionArcseconds,
    double MaximumCumulativeCorrectionArcseconds,
    int MaximumCorrectionAttempts,
    TimeSpan MaximumElapsedTime)
{
    public const int CurrentSchemaVersion = 1;

    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion)
        {
            issues.Add($"QHY coarse-centering limit schema must be {CurrentSchemaVersion}.");
        }
        if (!double.IsFinite(MaximumSingleCorrectionArcseconds) || MaximumSingleCorrectionArcseconds <= 0)
        {
            issues.Add("QHY coarse single-correction limit must be positive and finite.");
        }
        if (!double.IsFinite(MaximumCumulativeCorrectionArcseconds) ||
            MaximumCumulativeCorrectionArcseconds < MaximumSingleCorrectionArcseconds * 2)
        {
            issues.Add("QHY coarse cumulative limit must reserve at least one maximum outward correction and its safe return.");
        }
        if (MaximumCorrectionAttempts < 2)
        {
            issues.Add("QHY coarse attempt limit must reserve at least one outward correction and one return move.");
        }
        if (MaximumElapsedTime <= TimeSpan.Zero)
        {
            issues.Add("QHY coarse elapsed-time limit must be positive.");
        }
        return issues.AsReadOnly();
    }
}
