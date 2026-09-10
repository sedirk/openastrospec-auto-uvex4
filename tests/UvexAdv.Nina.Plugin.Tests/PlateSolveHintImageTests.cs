using NINA.Astrometry;
using NINA.Image.ImageData;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class PlateSolveHintImageTests
{
    [Fact]
    public void FreshQhyHintReplacesConflictingHeadersOnlyInIsolatedSolverMetadata()
    {
        var old = new Coordinates(14.59434, 60.86154, Epoch.J2000, Coordinates.RAType.Degrees);
        var source = new ImageMetaData
        {
            Target = new TargetParameter { Name = "Gamma Cas", Coordinates = old },
            Telescope = new TelescopeParameter { Coordinates = old },
            Image = new ImageParameter { ExposureTime = 10, ExposureStart = new DateTime(2026, 9, 7) },
        };
        source.GenericHeaders.Add(new DoubleMetaDataHeader("RA", old.RADegrees, "Original mount report"));
        var requested = new Coordinates(10.3411609, 59.9485990, Epoch.J2000, Coordinates.RAType.Degrees);
        var copy = PlateSolveHintImage.CreateMetadata(source, requested, 2150, 4, 1);

        Assert.Equal(requested.RADegrees, copy.Target.Coordinates.RADegrees, 9);
        Assert.Equal(requested.Dec, copy.Telescope.Coordinates.Dec, 9);
        Assert.Equal(Epoch.J2000, copy.Telescope.Coordinates.Epoch);
        Assert.Equal(14.59434, source.Target.Coordinates.RADegrees, 9);
        Assert.Equal(60.86154, source.Telescope.Coordinates.Dec, 9);
        Assert.Equal(10, copy.Image.ExposureTime);
        Assert.Equal(source.Image.ExposureStart, copy.Image.ExposureStart);
        Assert.Equal(2150, copy.Telescope.FocalLength);
        Assert.Equal(4, copy.Camera.PixelSize);
        Assert.NotSame(source.Image, copy.Image);
        Assert.NotSame(source.Camera, copy.Camera);
        Assert.NotSame(source.Target, copy.Target);
        Assert.NotSame(source.Telescope, copy.Telescope);
        Assert.NotSame(source.GenericHeaders, copy.GenericHeaders);
        Assert.NotSame(requested, copy.Target.Coordinates);
        Assert.NotSame(copy.Target.Coordinates, copy.Telescope.Coordinates);
        Assert.Null(copy.WorldCoordinateSystem);
        Assert.Single(source.GenericHeaders);
        Assert.Single(copy.GenericHeaders);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(359.99999, -89.9)]
    public void RequestedCoordinateIsNotReplacedByMissingOrUnrelatedSourceTarget(double ra, double dec)
    {
        var copy = PlateSolveHintImage.CreateMetadata(new ImageMetaData(),
            new Coordinates(ra, dec, Epoch.J2000, Coordinates.RAType.Degrees), 350, 1.45, 2);
        Assert.Equal(ra, copy.Target.Coordinates.RADegrees, 8);
        Assert.Equal(dec, copy.Telescope.Coordinates.Dec, 8);
        Assert.Equal(2, copy.Camera.BinX);
        Assert.Equal("2x2", copy.Image.Binning);
    }

    [Fact]
    public void SolverPixelEditsCannotChangeSixteenBitSource()
    {
        var original = new ImageArray(new ushort[] { 0, 40000, 65535 });
        var copy = PlateSolveHintImage.CopyPixels(original);
        Assert.Equal(original.FlatArray, copy.FlatArray);
        copy.FlatArray[1] = 10;
        Assert.Equal(40000, original.FlatArray[1]);
    }

    [Fact]
    public void WiderPixelsKeepTheirFullPrecisionAndAreNotShared()
    {
        var original = new ImageArrayInt(new[] { -10, 70000, 1234567 });
        var copy = PlateSolveHintImage.CopyPixels(original);
        Assert.Equal(original.FlatArrayInt, copy.FlatArrayInt);
        copy.FlatArrayInt[1] = 0;
        Assert.Equal(70000, original.FlatArrayInt[1]);
    }

    [Fact]
    public void ProductionSolverUsesTheIsolatedInputAndChecksOriginalHash()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Sources", "RealObservationStageRunner.cs"));
        Assert.Contains("imageSolver.Solve(solverImage, parameter", source);
        Assert.DoesNotContain("imageSolver.Solve(image, parameter", source);
        Assert.Contains("string.Equals(sourceSha256Before, sourceSha256After", source);
        Assert.Contains("appliedHeaderRaDegrees = parameter.Coordinates.RADegrees", source);
        Assert.Contains("sourceUnchanged = true", source);
    }
}
