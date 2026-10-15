using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class PartiallyClippedCoreTests
{
    [Theory]
    [InlineData(58560, true)]
    [InlineData(20000, false)]
    [InlineData(4000, false)]
    public void BrightSignalInsideClippedAnnulusIsNotAnEmptyHole(ushort center, bool measured)
    {
        var pixels = Enumerable.Repeat((ushort)4000, 100 * 100).ToArray();
        for (var y=35; y<=65; y++) for (var x=35; x<=65; x++)
        {
            var r = Math.Sqrt((x-50)*(x-50)+(y-50)*(y-50));
            if (r <= 13) pixels[y*100+x] = r < 4 && !(x >= 50 && y >= 50) ? center : (ushort)65520;
        }
        var result = SaturatedTargetGhostTopologyAnalyzer.Analyze(new(100,100,pixels,65520),new(50,50),30);
        Assert.Equal(measured, result.Gate.Disposition == GateDisposition.Passed);
        if (measured)
        {
            Assert.Equal(GateSeverity.Warning, result.Gate.Severity);
            Assert.Equal(1, result.Target!.Gate.Metrics!["partialClippingSignalSupport"]);
            // The clipped quadrant makes this fixture slightly asymmetric; the
            // measured centroid should remain at the core, not snap to its seed.
            Assert.InRange(result.Target.Centroid.X, 49.9, 50.1);
            Assert.InRange(result.Target.Centroid.Y, 49.9, 50.1);
        }
        else Assert.Null(result.Target);
    }
}
