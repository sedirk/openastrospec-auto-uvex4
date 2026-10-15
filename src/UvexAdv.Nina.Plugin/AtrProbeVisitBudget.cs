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
    public int TemperatureReprobes { get; private set; }
    public bool CanRepeatAfterTemperatureFailure => AttemptCount < MaximumAttempts && BackoffReprobes + TemperatureReprobes == 0;

    public bool TryBeginProbe(double exposure, bool followsSaturationBackoff, bool followsTemperatureRecovery = false)
    {
        if (!configuredTiers.Contains(exposure) || AttemptCount >= MaximumAttempts) return false;
        if (visited.Contains(exposure))
        {
            if (BackoffReprobes + TemperatureReprobes != 0) return false;
            if (followsTemperatureRecovery && !followsSaturationBackoff && exposure == previousExposure)
                TemperatureReprobes++;
            else if (followsSaturationBackoff && !followsTemperatureRecovery && exposure < previousExposure)
                BackoffReprobes++;
            else return false;
        }
        visited.Add(exposure);
        previousExposure = exposure;
        AttemptCount++;
        return true;
    }
}
