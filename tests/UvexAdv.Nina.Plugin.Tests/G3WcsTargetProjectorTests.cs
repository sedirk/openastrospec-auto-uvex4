using NINA.Astrometry;
using NINA.PlateSolving;
using UvexAdv.Observatory;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3WcsTargetProjectorTests
{
    [Fact]
    public void RecordedHd18552NeighbourArrivalCanProposeShortFramesWithinExistingEnvelope()
    {
        // Immutable run UVEX-20261010T170847Z: formal 00117 WCS scale/angle,
        // settled arrival hint in 00122/00125, and first 00120 FITS bright-core
        // centroid. This proves candidate-path reachability, not star identity
        // or actual short-frame success: those images were never acquired.
        var target = new Coordinates(45.04952545264, 38.13172151748999,
            Epoch.J2000, Coordinates.RAType.Degrees);
        var estimated = new PlateSolveResult
        {
            // Projector input only: this is a transported neighbour WCS,
            // explicitly not a successful target-field plate solve.
            Success = true,
            Coordinates = new Coordinates(45.068467793186834, 38.120332819937246,
                Epoch.J2000, Coordinates.RAType.Degrees),
            Pixscale = 0.38054519714166873,
            PositionAngle = 256.423428487602,
            Flipped = false,
        };
        var prediction = G3WcsTargetProjector.Project(target, estimated, 1920, 1080,
            "NINA.PlateSolving.Solvers.Platesolve3Solver, 3.2.0.9001");
        var slit = new PixelPoint(817.473, 426.867);
        var uncertainty = 2 / estimated.Pixscale + .1; // includes maximum inverse residual
        // Actual v6 preset: 25 px steps, 100 px cumulative, 8 attempts,
        // 300/30 s total/stage, degraded stage scale .5, growth allowance 2.
        var limits = new Phd2LockShiftLimits(25, 100, 8, TimeSpan.FromSeconds(300),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2),
            1, 1, 2, 100, 5, 5, 3, 4, 2, 10, 100000);
        var handoffRadius = Phd2CoarseHandoffPolicy.SelectHandoffRadius(true, 20, 100, limits, .5, 2);
        Assert.Equal(44, handoffRadius, 6);
        Assert.True(G3PostWcsPredictionPolicy.Evaluate(prediction, uncertainty, slit, 1920, 1080, 20).Authorized);
        var candidate = new TargetIdentification(
            GateResult.Pass("TARGET_IDENTIFIED_SATURATED_TOPOLOGY", "Candidate geometry only"),
            new StarCandidate(new PixelPoint(857.49, 423.39), 1000, 10000, 50, 3, 0, 1, 100),
            prediction, 40, 2, TargetIdentificationAuthority.BrightWingCentroid);
        Assert.False(G3PostWcsMeasuredHandoffPolicy.CanHandOff(candidate, prediction, uncertainty, slit, 1920, 1080, 20));
        Assert.True(G3PostWcsMeasuredHandoffPolicy.CanProposeBoundedShortConfirmation(
            candidate, prediction, uncertainty, slit, 1920, 1080, 20, 100, handoffRadius));
        Assert.False(G3PostWcsMeasuredHandoffPolicy.CanProposeBoundedShortConfirmation(
            candidate, prediction, uncertainty, slit, 1920, 1080, 20, 100, 39));
    }

    [Fact]
    public void NeighbourInversionSupportsOnlyItsBoundedExteriorEnvelopeWhileSlitInversionStaysInside()
    {
        var target = new Coordinates(310.35798, 45.28034, Epoch.J2000, Coordinates.RAType.Degrees);
        var solve = new PlateSolveResult
        {
            Success = true, Coordinates = new Coordinates(310, 45.1, Epoch.J2000, Coordinates.RAType.Degrees),
            Pixscale = 0.382, PositionAngle = 252.4,
        };
        var point = new UvexAdv.Observatory.PixelPoint(817,
            1080 + UvexAdv.Observatory.G3WcsApproachPolicy.GetNeighbourClearancePixels(1920, 1080));
        const string solver = "NINA.PlateSolving.Solvers.Platesolve3Solver";
        Assert.Throws<ArgumentOutOfRangeException>(() => G3WcsTargetProjector.SolveCenterForTargetAtPixel(
            target, solve, 1920, 1080, solver, point));
        var inverse = G3WcsTargetProjector.SolveCenterForNeighbourTargetAtPixel(
            target, solve, 1920, 1080, solver, point);
        Assert.True(inverse.InverseResidualPixels < 0.1);
        Assert.Equal(point, inverse.DesiredTargetPixel);
        Assert.Throws<ArgumentOutOfRangeException>(() => G3WcsTargetProjector.SolveCenterForNeighbourTargetAtPixel(
            target, solve, 1920, 1080, solver, point with { Y = point.Y + 1 }));
    }

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
