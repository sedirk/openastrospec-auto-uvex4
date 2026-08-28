namespace UvexAdv.Nina.Plugin;

internal static class PlateSolveDownSamplePolicy
{
    public const int G3SolveFactor = 2;

    public static int EffectiveForRole(int configuredFactor, string role)
    {
        var normalized = Math.Max(0, configuredFactor);
        return IsG3Role(role) ? Math.Max(G3SolveFactor, normalized) : normalized;
    }

    private static bool IsG3Role(string role) =>
        !string.IsNullOrWhiteSpace(role) &&
        role.StartsWith("PHD2/G3", StringComparison.OrdinalIgnoreCase);
}
