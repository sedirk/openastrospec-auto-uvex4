using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class CommissioningAlgorithmsTests
{
    [Fact]
    public void MountTransformIsRecoveredFromBoundedCalibrationMoves()
    {
        var samples = new[]
        {
            new MountCalibrationSample(10, 0, 0, 5),
            new MountCalibrationSample(0, 10, -5, 0),
            new MountCalibrationSample(-10, 0, 0, -5),
            new MountCalibrationSample(0, -10, 5, 0)
        };

        var result = MountTransformCalibrator.Fit("test", "East", samples);

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.NotNull(result.Transform);
        Assert.Equal(-2, result.Transform.DecArcsecondsPerPixelX, 6);
        Assert.Equal(2, result.Transform.RaArcsecondsPerPixelY, 6);
    }

    [Fact]
    public void SlitThroughputFitFindsPeak()
    {
        var samples = new List<SlitThroughputSample>();
        foreach (var x in new[] { -6d, -3, 0, 3, 6 })
        {
            var flux = 10000 - 150 * (x - 1.2) * (x - 1.2);
            samples.Add(new SlitThroughputSample(x, flux, 50, 0, x.ToString()));
        }

        var result = SlitThroughputOptimizer.Fit(samples, 8);

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal(1.2, result.BestOffsetArcseconds, 6);
    }
}
