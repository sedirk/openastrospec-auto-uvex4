using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal static class Phd2SlitSettleTiming
{
    internal static Phd2SettleCriteria ForSupervisedPlacement(
        Phd2SettleCriteria configured, double maximumStageSeconds)
    {
        var timeout = Math.Min(configured.TimeoutSeconds,
            Math.Max(1, (int)Math.Floor(maximumStageSeconds * 0.6)));
        return configured with
        {
            StableTimeSeconds = Math.Min(configured.StableTimeSeconds, Math.Min(3, timeout)),
            TimeoutSeconds = timeout,
        };
    }
}
