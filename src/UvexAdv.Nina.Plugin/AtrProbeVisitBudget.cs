namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Bounds exposure selection, while permitting one fresh measurement of a
/// previously tried lower tier after the last probe required saturation backoff.
/// This grants a capture attempt only, never quality or science authority.
/// </summary>
internal sealed class AtrProbeVisitBudget
{
    private readonly HashSet<double> configuredTiers;
    private readonly HashSet<double> visited = [];
    private double previousExposure;

    public AtrProbeVisitBudget(IEnumerable<double> tiers)
    {
        configuredTiers = tiers.Where(value => double.IsFinite(value) && value > 0).ToHashSet();
        MaximumAttempts = configuredTiers.Count + 1;
    }

    public int AttemptCount { get; private set; }
    public int MaximumAttempts { get; }
    public int BackoffReprobes { get; private set; }

    public bool TryBeginProbe(double exposure, bool followsSaturationBackoff)
    {
        if (!configuredTiers.Contains(exposure) || AttemptCount >= MaximumAttempts) return false;
        if (visited.Contains(exposure))
        {
            if (!followsSaturationBackoff || exposure >= previousExposure || BackoffReprobes != 0)
                return false;
            BackoffReprobes++;
        }
        visited.Add(exposure);
        previousExposure = exposure;
        AttemptCount++;
        return true;
    }
}
