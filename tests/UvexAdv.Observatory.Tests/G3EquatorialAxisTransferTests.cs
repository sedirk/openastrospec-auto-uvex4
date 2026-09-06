using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3EquatorialAxisTransferTests
{
    [Fact]
    public void DeclinationReadbackBiasDoesNotRescaleTheRaAxisCorrection()
    {
        var actual = G3EquatorialAxisTransfer.Apply(308.7439, 44.8640, 310.3807, 45.2696, 312.6, 45.9);
        Assert.Equal(314.2368, actual.RaDegrees, 7);
        Assert.Equal(46.3056, actual.DecDegrees, 7);
        var tangent = G3AcquisitionMotionPlanner.SignedTangentOffsetArcseconds(308.7439, 44.8640, 310.3807, 45.2696);
        var old = G3AcquisitionMotionPlanner.ApplyTangentOffsetArcseconds(312.6, 45.9, tangent.RaArcseconds, tangent.DecArcseconds);
        Assert.True(G3AcquisitionMotionPlanner.AngularSeparationArcseconds(actual.RaDegrees, actual.DecDegrees, old.RaDegrees, old.DecDegrees) > 30);
    }

    [Fact]
    public void WrapAndArrivalReadbackUseTheSameAxisTransfer()
    {
        var wrapped = G3EquatorialAxisTransfer.Apply(359.9, 40, 0.1, 40.2, 359.95, 41);
        Assert.Equal(0.15, wrapped.RaDegrees, 8);
        Assert.Equal(41.2, wrapped.DecDegrees, 8);
        var arrival = G3EquatorialAxisTransfer.Apply(314.2, 46.3, 314.2001, 46.2998, 310.38, 45.27);
        Assert.Equal(310.3801, arrival.RaDegrees, 8);
        Assert.Equal(45.2698, arrival.DecDegrees, 8);
    }

    [Theory]
    [InlineData(double.NaN, 40, 40)]
    [InlineData(10, 90, 40)]
    [InlineData(10, 40, 90)]
    public void InvalidOrPolarAxesCannotCreateACommand(double ra, double sourceDec, double readbackDec)
    {
        Assert.True(double.IsNaN(G3EquatorialAxisTransfer.Apply(ra, sourceDec, 11, 41, 10, readbackDec).RaDegrees));
    }
}
