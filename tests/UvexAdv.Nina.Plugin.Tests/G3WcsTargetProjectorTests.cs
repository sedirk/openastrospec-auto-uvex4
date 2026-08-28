using NINA.Astrometry;
using NINA.PlateSolving;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3WcsTargetProjectorTests
{
    [Fact]
    public void PlateSolve3ProjectionComplementsLiveDenebPositionAngle()
    {
        // Immutable values from
        // g3-deneb-returned-20260824T123340Z_PS3.txt. The raw 252.08 degree
        // angle predicts (1738.9, 600.0), on the wrong side of the detector;
        // PS3 theta and the live target are at the complemented orientation.
        var center = new Coordinates(
            5.41628646820643 * 180d / Math.PI,
            0.788879350738306 * 180d / Math.PI,
            Epoch.J2000,
            Coordinates.RAType.Degrees);
        var target = new Coordinates(310.35798, 45.28034, Epoch.J2000, Coordinates.RAType.Degrees);
        var solve = new PlateSolveResult
        {
            Success = true,
            Coordinates = center,
            Pixscale = 206264.8 / 538021.58660572,
            PositionAngle = 252.083619547881,
            Flipped = false,
        };

        var projected = G3WcsTargetProjector.Project(
            target,
            solve,
            1920,
            1080,
            "NINA.PlateSolving.Solvers.Platesolve3Solver, 3.2.0.9001");

        Assert.Equal(293.443395272793, projected.X, 6);
        Assert.Equal(947.330970670695, projected.Y, 6);
        Assert.Equal(107.916380452119, G3WcsTargetProjector.ProjectionRotationDegrees(
            solve.PositionAngle,
            "NINA.PlateSolving.Solvers.Platesolve3Solver"), 9);
    }

    [Fact]
    public void OtherNinaSolversKeepTheirNormalizedPositionAngle()
    {
        Assert.Equal(
            252.083619547881,
            G3WcsTargetProjector.ProjectionRotationDegrees(
                252.083619547881,
                "NINA.PlateSolving.Solvers.ASTAPSolver"),
            9);
    }

    [Fact]
    public void InverseProjectionPlacesLiveDenebCoordinateAtCommissionedSlit()
    {
        var center = new Coordinates(
            310.33035526204696,
            45.199457342325516,
            Epoch.J2000,
            Coordinates.RAType.Degrees);
        var target = new Coordinates(310.35798, 45.28034, Epoch.J2000, Coordinates.RAType.Degrees);
        var solve = new PlateSolveResult
        {
            Success = true,
            Coordinates = center,
            Pixscale = 0.383376439,
            PositionAngle = 252.0836195,
            Flipped = false,
        };
        var slit = new UvexAdv.Observatory.PixelPoint(817.473, 426.867);

        var inverse = G3WcsTargetProjector.SolveCenterForTargetAtPixel(
            target,
            solve,
            1920,
            1080,
            "NINA.PlateSolving.Solvers.Platesolve3Solver, 3.2.0.9001",
            slit);

        Assert.Equal(310.3809078580967, inverse.DesiredG3Center.RADegrees, 6);
        Assert.Equal(45.269606431258076, inverse.DesiredG3Center.Dec, 6);
        Assert.True(inverse.InverseResidualPixels < 0.1);
        Assert.InRange(inverse.Iterations, 1, 4);

        var verificationSolve = new PlateSolveResult
        {
            Success = true,
            Coordinates = inverse.DesiredG3Center,
            Pixscale = solve.Pixscale,
            PositionAngle = solve.PositionAngle,
            Flipped = solve.Flipped,
        };
        var projected = G3WcsTargetProjector.Project(
            target,
            verificationSolve,
            1920,
            1080,
            "NINA.PlateSolving.Solvers.Platesolve3Solver, 3.2.0.9001");
        Assert.Equal(slit.X, projected.X, 6);
        Assert.Equal(slit.Y, projected.Y, 6);
    }
}
