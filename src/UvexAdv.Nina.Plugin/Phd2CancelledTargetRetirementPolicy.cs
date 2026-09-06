using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal static class Phd2CancelledTargetRetirementPolicy
{
    internal static bool CanRetireAfterVerifiedHome(
        ObservationRunState? terminalState, DateTimeOffset sourceUpdatedUtc,
        DateTimeOffset? homeVerifiedUtc, DateTimeOffset evaluatedUtc, bool sameTelescopeAndCurrentRun) =>
        terminalState == ObservationRunState.Cancelled && sameTelescopeAndCurrentRun &&
        homeVerifiedUtc is { } home && home > sourceUpdatedUtc && home <= evaluatedUtc;

    internal static bool CanRetire(ObservationRunState? terminalState, EquatorialTarget oldTarget, EquatorialTarget newTarget)
    {
        if (terminalState != ObservationRunState.Cancelled ||
            string.IsNullOrWhiteSpace(oldTarget.CatalogId) || string.IsNullOrWhiteSpace(newTarget.CatalogId) ||
            string.Equals(oldTarget.CatalogId.Trim(), newTarget.CatalogId.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        const double radians = Math.PI / 180;
        var dot = Math.Sin(oldTarget.DeclinationDegrees * radians) * Math.Sin(newTarget.DeclinationDegrees * radians) +
            Math.Cos(oldTarget.DeclinationDegrees * radians) * Math.Cos(newTarget.DeclinationDegrees * radians) *
            Math.Cos((oldTarget.RightAscensionDegrees - newTarget.RightAscensionDegrees) * radians);
        // Conservative disjoint-field test, not permission for any motion.
        // Merely renaming/re-importing the same field cannot reset its budget.
        return double.IsFinite(dot) && Math.Acos(Math.Clamp(dot, -1, 1)) / radians > 1;
    }
}
