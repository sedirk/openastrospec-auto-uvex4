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
    private const double InverseJacobianStepDegrees = 1d / 3600d;
    private const double MaximumInverseStepDegrees = 0.25d;
    private const int MaximumInverseIterations = 40;
    private const double MaximumInverseResidualPixels = 0.1d;

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

    /// <summary>
    /// Solves the inverse acquisition problem: which G3 WCS centre will place
    /// the declared catalogue coordinate at a detector-fixed destination such
    /// as the measured slit midpoint.  The result is a G3 optical-axis centre,
    /// not a mount coordinate; callers must apply the centre delta to the fresh
    /// mount readback so the guide/main optical-axis offset is retained.
    /// </summary>
    internal static G3WcsInverseSolution SolveCenterForTargetAtPixel(
        Coordinates target,
        PlateSolveResult solve,
        int imageWidth,
        int imageHeight,
        string solverIdentity,
        PixelPoint desiredTargetPixel)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(solve);
        if (!solve.Success || solve.Coordinates is null)
            throw new ArgumentException("A successful solve with center coordinates is required.", nameof(solve));
        if (imageWidth <= 0 || imageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "Image dimensions must be positive.");
        if (!double.IsFinite(desiredTargetPixel.X) || !double.IsFinite(desiredTargetPixel.Y) ||
            desiredTargetPixel.X < 0 || desiredTargetPixel.X >= imageWidth ||
            desiredTargetPixel.Y < 0 || desiredTargetPixel.Y >= imageHeight)
            throw new ArgumentOutOfRangeException(nameof(desiredTargetPixel), "The requested detector destination must be finite and inside the image.");

        var currentProjection = Project(target, solve, imageWidth, imageHeight, solverIdentity);
        var centerRaDegrees = NormalizeDegrees(solve.Coordinates.RADegrees);
        var centerDecDegrees = solve.Coordinates.Dec;
        var inverseResidualPixels = double.PositiveInfinity;
        var iterations = 0;

        PixelPoint ProjectAtCenter(double raDegrees, double decDegrees)
        {
            var hypothetical = new PlateSolveResult
            {
                Success = true,
                Coordinates = new Coordinates(
                    NormalizeDegrees(raDegrees),
                    Math.Clamp(decDegrees, -89.9d, 89.9d),
                    solve.Coordinates.Epoch,
                    Coordinates.RAType.Degrees),
                Pixscale = solve.Pixscale,
                PositionAngle = solve.PositionAngle,
                Flipped = solve.Flipped,
            };
            return Project(target, hypothetical, imageWidth, imageHeight, solverIdentity);
        }

        for (; iterations < MaximumInverseIterations; iterations++)
        {
            var candidate = ProjectAtCenter(centerRaDegrees, centerDecDegrees);
            var errorX = candidate.X - desiredTargetPixel.X;
            var errorY = candidate.Y - desiredTargetPixel.Y;
            inverseResidualPixels = Math.Sqrt(errorX * errorX + errorY * errorY);
            if (inverseResidualPixels <= 0.02d) break;

            var plusRa = ProjectAtCenter(centerRaDegrees + InverseJacobianStepDegrees, centerDecDegrees);
            var plusDec = ProjectAtCenter(centerRaDegrees, centerDecDegrees + InverseJacobianStepDegrees);
            var j11 = (plusRa.X - candidate.X) / InverseJacobianStepDegrees;
            var j21 = (plusRa.Y - candidate.Y) / InverseJacobianStepDegrees;
            var j12 = (plusDec.X - candidate.X) / InverseJacobianStepDegrees;
            var j22 = (plusDec.Y - candidate.Y) / InverseJacobianStepDegrees;
            var determinant = j11 * j22 - j12 * j21;
            if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-9d)
                throw new InvalidOperationException("The local G3 detector-to-sky Jacobian is singular.");

            var deltaRa = (-errorX * j22 + j12 * errorY) / determinant;
            var deltaDec = (j21 * errorX - j11 * errorY) / determinant;
            var stepLength = Math.Sqrt(deltaRa * deltaRa + deltaDec * deltaDec);
            if (!double.IsFinite(stepLength))
                throw new InvalidOperationException("The G3 detector-to-sky inverse step is not finite.");
            if (stepLength > MaximumInverseStepDegrees)
            {
                var scale = MaximumInverseStepDegrees / stepLength;
                deltaRa *= scale;
                deltaDec *= scale;
            }

            centerRaDegrees = NormalizeDegrees(centerRaDegrees + deltaRa);
            centerDecDegrees = Math.Clamp(centerDecDegrees + deltaDec, -89.9d, 89.9d);
        }

        var finalProjection = ProjectAtCenter(centerRaDegrees, centerDecDegrees);
        inverseResidualPixels = Math.Sqrt(
            Math.Pow(finalProjection.X - desiredTargetPixel.X, 2) +
            Math.Pow(finalProjection.Y - desiredTargetPixel.Y, 2));
        if (!double.IsFinite(inverseResidualPixels) || inverseResidualPixels > MaximumInverseResidualPixels)
            throw new InvalidOperationException($"G3 detector-to-sky inversion did not converge: {inverseResidualPixels:F3} px.");

        return new G3WcsInverseSolution(
            currentProjection,
            new Coordinates(
                centerRaDegrees,
                centerDecDegrees,
                solve.Coordinates.Epoch,
                Coordinates.RAType.Degrees),
            desiredTargetPixel,
            inverseResidualPixels,
            iterations);
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

internal sealed record G3WcsInverseSolution(
    PixelPoint CurrentTargetPixel,
    Coordinates DesiredG3Center,
    PixelPoint DesiredTargetPixel,
    double InverseResidualPixels,
    int Iterations);
