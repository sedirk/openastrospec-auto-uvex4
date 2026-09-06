namespace UvexAdv.Nina.Plugin;

internal static class PlateSolveDownSamplePolicy
{
    public const int G3SolveFactor = 2;

    // Diversify one existing exposure tier after a completed failed/implausible
    // solve, rather than discarding sparse sources at the same resolution again.
    // This is software-only; the camera binning and immutable FITS do not change.
    public static int? G3RecoveryTierOverride(int oneBasedTier, bool priorCompletedSolveRejected) =>
        oneBasedTier == 2 && priorCompletedSolveRejected ? 1 : null;

    public static int EffectiveForRole(int configuredFactor, string role)
    {
        var normalized = Math.Max(0, configuredFactor);
        return IsG3Role(role) ? Math.Max(G3SolveFactor, normalized) : normalized;
    }

    private static bool IsG3Role(string role) =>
        !string.IsNullOrWhiteSpace(role) &&
        role.StartsWith("PHD2/G3", StringComparison.OrdinalIgnoreCase);
}
