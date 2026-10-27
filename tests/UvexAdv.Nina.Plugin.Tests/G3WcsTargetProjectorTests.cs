using NINA.Astrometry;
using NINA.PlateSolving;
using UvexAdv.Observatory;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3WcsTargetProjectorTests
{
    [Theory]
    [InlineData("FormalSolver", true, true)]
    [InlineData("FormalSolver", false, false)]
    [InlineData("RegisteredMosaicWcs", true, false)]
    [InlineData("LocalDualOpticsPrediction", true, false)]
    [InlineData("Unknown", true, false)]
    public void OnlyCurrentFormalSolveRemovesOverlapCapWithoutIncreasingOriginalMotionLimit(
        string authority, bool success, bool direct)
    {
        var coordinates = new Coordinates(346.77, 50.19, Epoch.J2000, Coordinates.RAType.Degrees);
        var evidence = new PlateSolveEvidence(new PlateSolveResult { Success = success, Coordinates = coordinates,
            Pixscale = .381, PositionAngle = 77.7 }, coordinates, 0, "PL3", "current.fit", "solve.json",
            new('A', 64), 1920, 1080, 1, DateTimeOffset.UtcNow) { Authority = authority };
        var footprint = new G3OverlapFootprint(1920, 1080, .381, 77.7, 5);
        Assert.Equal(direct, G3SparseRecoveryWorkflow.CanApproachDirectly(evidence));
        // Approximately the recorded mid0 initial offset. Before ADR-0045 even
        // a formal solution was divided into tiny overlap segments.
        var plan = G3SparseRecoveryWorkflow.PlanCorrection(evidence, footprint, 1800, 600, 5398);
        if (direct) Assert.Equal(1, plan.AppliedScale);
        else Assert.InRange(plan.AppliedScale, .001, .3);
        var capped = G3SparseRecoveryWorkflow.PlanCorrection(evidence, footprint, 1800, 600, 100);
        Assert.InRange(Math.Sqrt(Math.Pow(capped.RaTangentArcseconds, 2) +
            Math.Pow(capped.DeclinationArcseconds, 2)), 0, 100.000001);
        Assert.False(G3SparseRecoveryWorkflow.CanApproachDirectly(null));
    }

    [Theory]
    [InlineData(0)] [InlineData(90)] [InlineData(180)] [InlineData(256.45384026037)] [InlineData(359.9)]
    public void KnownAxisOverlapMatchesActualProjectorAndRetainsOriginalLimits(double angle)
    {
        var solve = new PlateSolveResult { Success = true, Coordinates = new Coordinates(349.9082927951558,
            23.763221766882612, Epoch.J2000, Coordinates.RAType.Degrees), Pixscale = .3812849948092597,
            PositionAngle = angle, Flipped = false };
        const string solver = "NINA.PlateSolving.Solvers.Platesolve3Solver";
        var axis = G3WcsTargetProjector.ImageXAxisEastToNorthDegrees(solve, solver);
        Assert.NotNull(axis);
        var footprint = new G3OverlapFootprint(1920,1080,solve.Pixscale,axis,5,OrientationUncertaintyDegrees:1);
        // October 25 recorded command, before the old unknown-angle cap.
        var east = 96.764007 / .90164256; var north = 158.378631 / .90164256;
        var plan = G3FieldOverlapPolicy.CapMove(footprint,east,north,5398);
        var p = G3AcquisitionMotionPlanner.ApplyTangentOffsetArcseconds(solve.Coordinates.RADegrees,
            solve.Coordinates.Dec,plan.RaTangentArcseconds,plan.DeclinationArcseconds);
        var projected = G3WcsTargetProjector.Project(new Coordinates(p.RaDegrees,p.DecDegrees,
            Epoch.J2000,Coordinates.RAType.Degrees),solve,1920,1080,solver);
        Assert.True(Math.Abs(projected.X-960) < 1920*.5);
        Assert.True(Math.Abs(projected.Y-540) < 1080*.5);
        Assert.InRange(plan.AppliedScale,0,1);
        if (angle == 256.45384026037) Assert.Equal(1,plan.AppliedScale);
        solve.Success = false;
        Assert.Null(G3WcsTargetProjector.ImageXAxisEastToNorthDegrees(solve,solver));
    }

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
