namespace UvexAdv.Observatory;

/// <summary>
/// A conservative footprint, not an optical solution or motion authority. The
/// position angle is the image +X axis measured counter-clockwise from tangent
/// east towards north. Callers must convert the solver's angle convention; when
/// that convention is unknown, leave the angle null and use the short-side bound.
/// All uncertainties describe one endpoint and are charged for BOTH fields.
/// </summary>
public sealed record G3OverlapFootprint(
    int WidthPixels,
    int HeightPixels,
    double PixelScaleArcseconds,
    double? ImageXAxisEastToNorthDegrees,
    double EndpointPositionUncertaintyArcseconds,
    double FractionalScaleUncertainty = 0.05,
    double OrientationUncertaintyDegrees = 0,
    double MinimumAxisOverlapFraction = 0.5);

public sealed record G3OverlapStepBound(
    bool Usable,
    string Reason,
    double MaximumStepArcseconds,
    double MinimumWidthArcseconds,
    double MinimumHeightArcseconds,
    double MinimumAxisOverlapFraction,
    double EndpointMarginArcseconds,
    bool UsedUnknownOrientationBound);

public sealed record G3OverlapSearchPlan(
    G3OverlapStepBound Overlap,
    G3LocalSearchLimits EffectiveLimits,
    IReadOnlyList<G3LocalSearchWaypoint> Waypoints);

public sealed record G3OverlapMovePlan(
    G3OverlapStepBound Overlap,
    double RaTangentArcseconds,
    double DeclinationArcseconds,
    double AppliedScale);

/// <summary>
/// Caps, but never enlarges, existing acquisition commands so neighbouring raw
/// fields retain a conservative common rectangle. Registration still has to
/// prove actual overlap from independent stars. This policy grants neither a
/// new search allowance nor WCS, ownership, safety or return authority.
/// </summary>
public static class G3FieldOverlapPolicy
{
    public static G3OverlapStepBound Bound(
        G3OverlapFootprint footprint,
        double raTangentDirection,
        double declinationDirection)
    {
        ArgumentNullException.ThrowIfNull(footprint);
        var directionLength = Math.Sqrt(
            raTangentDirection * raTangentDirection +
            declinationDirection * declinationDirection);
        if (footprint.WidthPixels <= 0 || footprint.HeightPixels <= 0 ||
            !PositiveFinite(footprint.PixelScaleArcseconds) ||
            !double.IsFinite(footprint.EndpointPositionUncertaintyArcseconds) ||
            footprint.EndpointPositionUncertaintyArcseconds < 0 ||
            !double.IsFinite(footprint.FractionalScaleUncertainty) ||
            footprint.FractionalScaleUncertainty is < 0 or >= 1 ||
            !double.IsFinite(footprint.OrientationUncertaintyDegrees) ||
            footprint.OrientationUncertaintyDegrees is < 0 or > 180 ||
            !double.IsFinite(footprint.MinimumAxisOverlapFraction) ||
            footprint.MinimumAxisOverlapFraction is <= 0 or >= 1 ||
            footprint.ImageXAxisEastToNorthDegrees is { } angle && !double.IsFinite(angle) ||
            !PositiveFinite(directionLength))
            return Unavailable("G3 overlap geometry or uncertainty is invalid.");

        var scale = footprint.PixelScaleArcseconds * (1 - footprint.FractionalScaleUncertainty);
        var width = footprint.WidthPixels * scale;
        var height = footprint.HeightPixels * scale;
        if (!PositiveFinite(width) || !PositiveFinite(height))
            return Unavailable("G3 conservative field dimensions are not finite.");

        // Every point of a rectangle rotates by at most 2 R sin(delta / 2).
        // Crop that distance from each edge, obtaining an axis-aligned core
        // contained in each possible rotated footprint. Add each endpoint's
        // position error; the common area pays the loss at both endpoints.
        var radius = Math.Sqrt(width * width + height * height) / 2;
        var rotationMargin = 2 * radius * Math.Sin(footprint.OrientationUncertaintyDegrees * Math.PI / 360);
        var endpointMargin = footprint.EndpointPositionUncertaintyArcseconds + rotationMargin;
        var availableX = width * (1 - footprint.MinimumAxisOverlapFraction) - 2 * endpointMargin;
        var availableY = height * (1 - footprint.MinimumAxisOverlapFraction) - 2 * endpointMargin;
        if (!PositiveFinite(availableX) || !PositiveFinite(availableY))
            return Unavailable("G3 footprint uncertainty leaves no guaranteed common rectangle.");

        var unknownOrientation = footprint.ImageXAxisEastToNorthDegrees is null;
        double maximumStep;
        if (unknownOrientation)
        {
            // Arbitrary angle/parity must not accidentally turn the long side
            // into an assumed usable north/south displacement.
            maximumStep = Math.Min(availableX, availableY);
        }
        else
        {
            var radians = footprint.ImageXAxisEastToNorthDegrees!.Value * Math.PI / 180;
            var east = raTangentDirection / directionLength;
            var north = declinationDirection / directionLength;
            var imageX = Math.Abs(east * Math.Cos(radians) + north * Math.Sin(radians));
            var imageY = Math.Abs(-east * Math.Sin(radians) + north * Math.Cos(radians));
            maximumStep = Math.Min(
                imageX > 1e-12 ? availableX / imageX : double.PositiveInfinity,
                imageY > 1e-12 ? availableY / imageY : double.PositiveInfinity);
        }

        if (!PositiveFinite(maximumStep))
            return Unavailable("G3 directional overlap step is not finite.");
        return new(true,
            "Overlap cap only; fresh stellar registration and original safety/motion/return checks remain required.",
            maximumStep, width, height, footprint.MinimumAxisOverlapFraction,
            endpointMargin, unknownOrientation);
    }

    /// <summary>
    /// Uses one capped cardinal grid step. Every other limit is byte-for-byte
    /// unchanged, including action count and elapsed time. Denser visits consume
    /// the original budget; the runtime ledger may stop before this list ends.
    /// </summary>
    public static G3OverlapSearchPlan PlanSearch(
        G3LocalSearchLimits limits, G3OverlapFootprint footprint)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var east = Bound(footprint, 1, 0);
        var north = Bound(footprint, 0, 1);
        var bound = !east.Usable ? east : !north.Usable ? north
            : east.MaximumStepArcseconds <= north.MaximumStepArcseconds ? east : north;
        // Unavailable geometry disables the overlap route, not the pre-existing
        // bounded direct/neighbor search. Never label those visits as a mosaic.
        var effective = bound.Usable
            ? limits with { StepArcseconds = Math.Min(limits.StepArcseconds, bound.MaximumStepArcseconds) }
            : limits;
        return new(bound, effective, G3LocalSearchPlanner.Build(effective));
    }

    /// <summary>
    /// Caps an existing WCS-authorized vector without changing its direction.
    /// The caller retains its original single-motion ceiling, reserve checks,
    /// durable precharge, target deadline, and next fresh optical measurement.
    /// </summary>
    public static G3OverlapMovePlan CapMove(
        G3OverlapFootprint footprint,
        double raTangentArcseconds,
        double declinationArcseconds,
        double originalMaximumCommandArcseconds)
    {
        if (!PositiveFinite(originalMaximumCommandArcseconds))
            throw new ArgumentOutOfRangeException(nameof(originalMaximumCommandArcseconds));
        var bound = Bound(footprint, raTangentArcseconds, declinationArcseconds);
        var magnitude = Math.Sqrt(raTangentArcseconds * raTangentArcseconds + declinationArcseconds * declinationArcseconds);
        if (!PositiveFinite(magnitude))
            throw new ArgumentOutOfRangeException(nameof(raTangentArcseconds), "The commanded vector must be finite and nonzero.");
        var maximum = bound.Usable
            ? Math.Min(originalMaximumCommandArcseconds, bound.MaximumStepArcseconds)
            : originalMaximumCommandArcseconds;
        var factor = Math.Min(1, maximum / magnitude);
        return new(bound, raTangentArcseconds * factor, declinationArcseconds * factor, factor);
    }

    private static bool PositiveFinite(double value) => double.IsFinite(value) && value > 0;

    private static G3OverlapStepBound Unavailable(string reason) =>
        new(false, reason, 0, 0, 0, 0, 0, true);
}
