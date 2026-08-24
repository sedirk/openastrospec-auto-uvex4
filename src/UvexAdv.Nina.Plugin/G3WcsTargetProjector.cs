using System.Windows;
using NINA.Astrometry;
using NINA.PlateSolving;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Converts a N.I.N.A. plate-solve result into a detector coordinate for the
/// catalog target. PlateSolve3 in N.I.N.A. 3.2 is a special case: its adapter
/// copies PS3's raw position angle directly, while N.I.N.A.'s projection API
/// expects the complemented detector rotation used by its other adapters.
/// </summary>
internal static class G3WcsTargetProjector
{
    private const string PlateSolve3TypeName = "Platesolve3Solver";

    internal static PixelPoint Project(
        Coordinates target,
        PlateSolveResult solve,
        int imageWidth,
        int imageHeight,
        string solverIdentity)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(solve);
        if (!solve.Success || solve.Coordinates is null)
            throw new ArgumentException("A successful solve with center coordinates is required.", nameof(solve));
        if (imageWidth <= 0 || imageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "Image dimensions must be positive.");
        if (!double.IsFinite(solve.Pixscale) || solve.Pixscale <= 0)
            throw new ArgumentOutOfRangeException(nameof(solve), "Plate scale must be positive and finite.");

        var rotation = ProjectionRotationDegrees(solve.PositionAngle, solverIdentity);
        var projected = target.XYProjection(
            solve.Coordinates,
            new Point(imageWidth / 2d, imageHeight / 2d),
            solve.Pixscale,
            solve.Pixscale,
            rotation);
        if (!double.IsFinite(projected.X) || !double.IsFinite(projected.Y))
            throw new InvalidOperationException("The WCS target projection is not finite.");
        return new PixelPoint(projected.X, projected.Y);
    }

    internal static double ProjectionRotationDegrees(double positionAngleDegrees, string solverIdentity)
    {
        if (!double.IsFinite(positionAngleDegrees))
            throw new ArgumentOutOfRangeException(nameof(positionAngleDegrees), "Position angle must be finite.");

        // N.I.N.A. 3.2 Platesolve3Solver.ReadResult stores the second PS3
        // result-field verbatim and never populates Flipped. Live Deneb data
        // on 2026-08-24 showed PS3 theta = 360 - PositionAngle; applying the
        // raw value mirrored the catalog prediction across the detector.
        var rotation = IsPlateSolve3(solverIdentity)
            ? 360d - positionAngleDegrees
            : positionAngleDegrees;
        return NormalizeDegrees(rotation);
    }

    internal static bool IsPlateSolve3(string solverIdentity) =>
        !string.IsNullOrWhiteSpace(solverIdentity) &&
        solverIdentity.Contains(PlateSolve3TypeName, StringComparison.OrdinalIgnoreCase);

    private static double NormalizeDegrees(double degrees)
    {
        var normalized = degrees % 360d;
        return normalized < 0 ? normalized + 360d : normalized;
    }
}
