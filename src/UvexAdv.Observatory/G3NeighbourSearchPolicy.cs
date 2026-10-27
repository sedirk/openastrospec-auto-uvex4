namespace UvexAdv.Observatory;

public sealed record G3NeighbourSearchVisit(G3LocalSearchWaypoint Waypoint,
    bool ReachesNominalDestination, bool PreservesOverlap, G3OverlapStepBound Overlap);

/// <summary>
/// Selects geometry only. The caller retains the original spiral cursor and
/// charges EVERY subdivision through the existing durable outbound/return gate.
/// Sparse data alone do not justify shrinking all blind search visits.
/// </summary>
public static class G3NeighbourSearchPolicy
{
    public static G3NeighbourSearchVisit PlanVisit(G3LocalSearchLimits limits,
        G3OverlapFootprint footprint, bool registeredSupportAvailable,
        G3LocalSearchWaypoint destination, double previousCommandEast,
        double previousCommandNorth, int actionNumber)
    {
        if (limits.Validate().Count != 0 || actionNumber < 1 || actionNumber > limits.MaximumAttempts ||
            !double.IsFinite(previousCommandEast) || !double.IsFinite(previousCommandNorth) ||
            !double.IsFinite(destination.RaTangentOffsetArcseconds) ||
            !double.IsFinite(destination.DeclinationOffsetArcseconds))
            throw new ArgumentException("Invalid original search limits, cursor or action number.");
        var east = destination.RaTangentOffsetArcseconds - previousCommandEast;
        var north = destination.DeclinationOffsetArcseconds - previousCommandNorth;
        var distance = Math.Sqrt(east * east + north * north);
        var destinationRadius = Math.Sqrt(Math.Pow(destination.RaTangentOffsetArcseconds, 2) +
            Math.Pow(destination.DeclinationOffsetArcseconds, 2));
        if (!double.IsFinite(distance) || distance <= 0 ||
            destinationRadius > limits.MaximumRadiusArcseconds + 1e-8 ||
            Math.Sqrt(previousCommandEast * previousCommandEast + previousCommandNorth * previousCommandNorth) > limits.MaximumRadiusArcseconds + 1e-8)
            throw new ArgumentException("Search cursor/destination must remain in the original radius.");
        var bound = G3FieldOverlapPolicy.Bound(footprint, east, north);
        // As before, invalid optional overlap geometry does not grant inferred
        // WCS. The existing blind search remains within its original limits.
        var preserve = registeredSupportAvailable && bound.Usable;
        var maximum = preserve ? Math.Min(limits.StepArcseconds, bound.MaximumStepArcseconds) : limits.StepArcseconds;
        var scale = Math.Min(1, maximum / distance);
        var x = previousCommandEast + east * scale;
        var y = previousCommandNorth + north * scale;
        return new(new(actionNumber, x, y, Math.Sqrt(x * x + y * y), distance * scale),
            scale == 1, preserve, bound);
    }
}
