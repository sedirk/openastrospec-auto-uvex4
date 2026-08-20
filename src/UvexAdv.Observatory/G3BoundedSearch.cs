namespace UvexAdv.Observatory;

/// <summary>
/// Policy for the optional GS350/QHY to C11/G3 pre-positioning move.  The
/// production runner can collect independently versioned paired-WCS candidates,
/// but has no verified-record importer/activator yet, so real runs are required
/// to lock this motion value to <see cref="Skip"/>.
/// </summary>
public enum WideToSlitTransferMode
{
    AutoIfValidElseSkip,
    Skip,
    RequireValid,
}

public enum G3LocalSearchPattern
{
    SquareSpiral,
}

/// <summary>
/// Immutable, run-scoped limits for the direct-G3 recovery search.  Values are
/// tangent-plane arcseconds and are additionally capped by the independently
/// commissioned mount-motion envelope.
/// </summary>
public sealed record G3LocalSearchLimits(
    G3LocalSearchPattern Pattern,
    double StepArcseconds,
    double MaximumRadiusArcseconds,
    double MaximumCumulativeMotionArcseconds,
    int MaximumAttempts,
    TimeSpan MaximumElapsedTime)
{
    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        if (!double.IsFinite(StepArcseconds) || StepArcseconds <= 0)
        {
            issues.Add("G3 search step must be positive and finite.");
        }
        if (!double.IsFinite(MaximumRadiusArcseconds) || MaximumRadiusArcseconds <= 0)
        {
            issues.Add("G3 search radius must be positive and finite.");
        }
        if (double.IsFinite(StepArcseconds) && double.IsFinite(MaximumRadiusArcseconds) &&
            StepArcseconds > MaximumRadiusArcseconds)
        {
            issues.Add("G3 search step cannot exceed the search radius.");
        }
        if (!double.IsFinite(MaximumCumulativeMotionArcseconds) || MaximumCumulativeMotionArcseconds <= 0)
        {
            issues.Add("G3 search cumulative-motion limit must be positive and finite.");
        }
        if (double.IsFinite(MaximumCumulativeMotionArcseconds) &&
            double.IsFinite(StepArcseconds) &&
            MaximumCumulativeMotionArcseconds < StepArcseconds * 2)
        {
            issues.Add("G3 search cumulative-motion limit must reserve at least one outward step and one safe return step.");
        }
        if (MaximumAttempts <= 0)
        {
            issues.Add("G3 search attempt limit must be positive.");
        }
        if (MaximumElapsedTime <= TimeSpan.Zero)
        {
            issues.Add("G3 search elapsed-time limit must be positive.");
        }
        return issues.AsReadOnly();
    }
}

/// <summary>
/// One absolute waypoint relative to the saved search origin.  Consecutive
/// waypoints are exactly one configured grid step apart; no optical-axis
/// offset or G3 pixel-to-mount transform participates in their construction.
/// </summary>
public sealed record G3LocalSearchWaypoint(
    int Attempt,
    double RaTangentOffsetArcseconds,
    double DeclinationOffsetArcseconds,
    double RadiusArcseconds,
    double MoveFromPreviousArcseconds);

public static class G3LocalSearchPlanner
{
    /// <summary>
    /// Builds a deterministic square-spiral visit order beginning one step east
    /// of the origin. When the nominal square spiral crosses the circular
    /// boundary, cardinal-grid bridge steps (including the origin when needed)
    /// keep every physical move exactly one step and inside the radius. This is
    /// why a small-radius plan may revisit a coordinate.
    /// </summary>
    public static IReadOnlyList<G3LocalSearchWaypoint> Build(G3LocalSearchLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var issues = limits.Validate();
        if (issues.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", issues), nameof(limits));
        }
        if (limits.Pattern != G3LocalSearchPattern.SquareSpiral)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), limits.Pattern, "Unsupported G3 local-search pattern.");
        }

        var desired = BuildDesiredSpiralCells(limits);
        var points = new List<G3LocalSearchWaypoint>(limits.MaximumAttempts);
        var visited = new HashSet<(int X, int Y)> { (0, 0) };
        var current = (X: 0, Y: 0);
        foreach (var target in desired)
        {
            if (points.Count >= limits.MaximumAttempts) break;
            if (visited.Contains(target)) continue;
            foreach (var cell in FindCardinalPathInsideRadius(current, target, limits))
            {
                if (points.Count >= limits.MaximumAttempts) break;
                var raOffset = cell.X * limits.StepArcseconds;
                var decOffset = cell.Y * limits.StepArcseconds;
                points.Add(new G3LocalSearchWaypoint(
                    points.Count + 1,
                    raOffset,
                    decOffset,
                    Math.Sqrt(raOffset * raOffset + decOffset * decOffset),
                    limits.StepArcseconds));
                current = cell;
                visited.Add(cell);
            }
        }

        return points.AsReadOnly();
    }

    private static IReadOnlyList<(int X, int Y)> BuildDesiredSpiralCells(G3LocalSearchLimits limits)
    {
        var cells = new List<(int X, int Y)>();
        var gridRadius = (int)Math.Floor(limits.MaximumRadiusArcseconds / limits.StepArcseconds + 1e-12);
        var maximumNominalSteps = checked((2 * gridRadius + 1) * (2 * gridRadius + 1) - 1);
        var x = 0;
        var y = 0;
        var directionX = 1;
        var directionY = 0;
        var legLength = 1;
        var legProgress = 0;
        var completedLegsAtLength = 0;

        for (var nominalStep = 0; nominalStep < maximumNominalSteps; nominalStep++)
        {
            var nextX = x + directionX;
            var nextY = y + directionY;
            var raOffset = nextX * limits.StepArcseconds;
            var decOffset = nextY * limits.StepArcseconds;
            var radius = Math.Sqrt(raOffset * raOffset + decOffset * decOffset);
            x = nextX;
            y = nextY;
            if (radius <= limits.MaximumRadiusArcseconds + 1e-9) cells.Add((x, y));

            legProgress++;
            if (legProgress != legLength) continue;

            legProgress = 0;
            (directionX, directionY) = (-directionY, directionX);
            completedLegsAtLength++;
            if (completedLegsAtLength != 2) continue;

            completedLegsAtLength = 0;
            legLength++;
        }

        return cells.AsReadOnly();
    }

    private static IReadOnlyList<(int X, int Y)> FindCardinalPathInsideRadius(
        (int X, int Y) start,
        (int X, int Y) target,
        G3LocalSearchLimits limits)
    {
        var queue = new Queue<(int X, int Y)>();
        var previous = new Dictionary<(int X, int Y), (int X, int Y)>();
        queue.Enqueue(start);
        previous[start] = start;
        ReadOnlySpan<(int X, int Y)> directions = [(1, 0), (0, 1), (-1, 0), (0, -1)];
        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            if (cell == target) break;
            foreach (var direction in directions)
            {
                var next = (cell.X + direction.X, cell.Y + direction.Y);
                if (previous.ContainsKey(next)) continue;
                var radius = Math.Sqrt(next.Item1 * next.Item1 + next.Item2 * next.Item2) * limits.StepArcseconds;
                if (radius > limits.MaximumRadiusArcseconds + 1e-9) continue;
                previous[next] = cell;
                queue.Enqueue(next);
            }
        }
        if (!previous.ContainsKey(target))
        {
            throw new InvalidOperationException("No continuous cardinal path exists inside the declared G3 search radius.");
        }
        var reversed = new List<(int X, int Y)>();
        for (var cell = target; cell != start; cell = previous[cell]) reversed.Add(cell);
        reversed.Reverse();
        return reversed.AsReadOnly();
    }

    /// <summary>
    /// Returns the number of no-larger-than-step motions needed to travel from
    /// a waypoint to the saved origin on a straight tangent-plane path.
    /// </summary>
    public static int RequiredReturnMoves(double radiusArcseconds, double maximumStepArcseconds)
    {
        if (!double.IsFinite(radiusArcseconds) || radiusArcseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radiusArcseconds));
        }
        if (!double.IsFinite(maximumStepArcseconds) || maximumStepArcseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumStepArcseconds));
        }
        return radiusArcseconds == 0
            ? 0
            : checked((int)Math.Ceiling(radiusArcseconds / maximumStepArcseconds));
    }
}
