using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal static class Phd2SlitSettleTiming
{
    internal static Phd2SettleCriteria ForBoundedWarningPlacement(
        Phd2SettleCriteria configured, double opticalCeiling, double maximumStageSeconds)
    {
        // Native settle must still succeed. Leave time within the SAME stage
        // for the independent full optical window; no fresh 120-second wait.
        var timeout = Math.Min(configured.TimeoutSeconds,
            Math.Max(1, (int)Math.Floor(maximumStageSeconds * 0.6)));
        return new(Math.Min(3, Math.Max(configured.Pixels, opticalCeiling)),
            Math.Min(configured.StableTimeSeconds, Math.Min(5, timeout)), timeout);
    }

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
