using NINA.Astrometry;
using NINA.PlateSolving;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3RegisteredWcsTests
{
    [Theory]
    [InlineData("NINA.PlateSolving.Solvers.Platesolve3Solver", 256.4)]
    [InlineData("NINA.PlateSolving.Solvers.ASTAPSolver", 103.6)]
    public void ComposedWcsProjectsFreshFrameThroughMeasuredTransform(string solver, double angle)
    {
        var anchor = new PlateSolveResult { Success = true,
            Coordinates = new Coordinates(345.7, 49.2, Epoch.J2000, Coordinates.RAType.Degrees),
            Pixscale = .382, PositionAngle = angle };
        var transform = new G3SimilarityTransform(Math.Cos(.02), Math.Sin(.02), -250, 160);
        var restored = G3RegisteredWcs.RestoreCurrentResult(anchor, 1920, 1080, solver, transform, 1920, 1080);
        foreach (var target in new[] { new Coordinates(345.71,49.21,Epoch.J2000,Coordinates.RAType.Degrees),
            new Coordinates(345.66,49.18,Epoch.J2000,Coordinates.RAType.Degrees) })
        {
            var expected = transform.Apply(G3WcsTargetProjector.Project(target, anchor, 1920, 1080, solver));
            var actual = G3WcsTargetProjector.Project(target, restored, 1920, 1080, solver);
            Assert.InRange(Math.Sqrt(Math.Pow(expected.X-actual.X,2)+Math.Pow(expected.Y-actual.Y,2)), 0, .3);
        }
        Assert.Equal(angle, anchor.PositionAngle);
        Assert.NotSame(anchor, restored);
    }

    [Fact]
    public void FormalMosaicCenterOffsetIsRemovedBeforeFreshDetectorProjection()
    {
        var solve = new PlateSolveResult { Success = true,
            Coordinates = new Coordinates(20, 30, Epoch.J2000, Coordinates.RAType.Degrees), Pixscale = .38, PositionAngle = 260 };
        var mosaic = new G3MosaicComposition(2400, 1500, Array.Empty<ushort>(), new(-200,-150), 1,
            new Dictionary<string,int>(), new[] { new string('A',64), new string('B',64) });
        var current = new G3SimilarityTransform(1,0,-100,100);
        var result = G3RegisteredWcs.RestoreFromMosaic(solve,mosaic,"Platesolve3Solver",current,1920,1080);
        var target = new Coordinates(20.005,30.002,Epoch.J2000,Coordinates.RAType.Degrees);
        var mp = G3WcsTargetProjector.Project(target,solve,mosaic.Width,mosaic.Height,"Platesolve3Solver");
        var expected = current.Apply(mosaic.MosaicToReference(mp));
        var actual = G3WcsTargetProjector.Project(target,result,1920,1080,"Platesolve3Solver");
        Assert.InRange(Math.Sqrt(Math.Pow(actual.X-expected.X,2)+Math.Pow(actual.Y-expected.Y,2)),0,.1);
    }
}
