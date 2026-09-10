using NINA.Astrometry;
using NINA.Image.ImageData;
using NINA.PlateSolving;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class PlateSolveSoftwareBinningTests
{
    private const string Pl3 = "NINA.PlateSolving.Solvers.Platesolve3Solver, 3.2.0.9001";

    [Theory]
    [InlineData("PHD2/G3", Pl3, 2, 1920, 1080, false, 2)]
    [InlineData("PHD2/G3", Pl3, 1, 1920, 1080, false, 1)]
    [InlineData("QHY", Pl3, 2, 1920, 1080, false, 1)]
    [InlineData("PHD2/G3", "NINA.PlateSolving.Solvers.ASTAPSolver, 3.2", 2, 1920, 1080, false, 1)]
    [InlineData("PHD2/G3", Pl3, 2, 1919, 1080, false, 1)]
    [InlineData("PHD2/G3", Pl3, 2, 1920, 1080, true, 1)]
    [InlineData("PHD2/G3", Pl3, 2, 32, 32, false, 1)]
    public void OnlyCompatibleMonoG3Pl3InputsAreResampled(string role, string solver, int requested,
        int width, int height, bool bayer, int expected) =>
        Assert.Equal(expected, PlateSolveSoftwareBinning.SelectFactor(role, solver, requested, width, height, bayer));

    [Fact]
    public void WholeBlocksKeepOrientationAndSourcePixelsUnchanged()
    {
        var pixels = new ushort[] { 0, 2, 10, 12, 4, 6, 14, 16, 20, 22, 30, 32, 24, 26, 34, 36 };
        var source = new ImageArray(pixels);
        var binned = PlateSolveSoftwareBinning.CopyBlockMeans(source, 4, 4, 2);
        Assert.Equal(new ushort[] { 3, 13, 23, 33 }, binned.FlatArray);
        binned.FlatArray[0] = 999;
        Assert.Equal(0, source.FlatArray[0]);
        Assert.Equal(36, source.FlatArray[15]);
    }

    [Fact]
    public void WidePixelMeanDoesNotOverflowOrLoseSign()
    {
        var source = new ImageArrayInt(new[] { int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue });
        Assert.Equal(int.MaxValue, PlateSolveSoftwareBinning.CopyBlockMeans(source, 2, 2, 2).FlatArrayInt[0]);
        var negative = new ImageArrayInt(new[] { int.MinValue, int.MinValue, int.MinValue, int.MinValue });
        Assert.Equal(int.MinValue, PlateSolveSoftwareBinning.CopyBlockMeans(negative, 2, 2, 2).FlatArrayInt[0]);
    }

    [Fact]
    public void PartialBlockOrWrongPixelCountCannotSilentlyCropTheField()
    {
        Assert.Throws<ArgumentException>(() => PlateSolveSoftwareBinning.CopyBlockMeans(new ImageArray(new ushort[6]), 3, 2, 2));
        Assert.Throws<ArgumentException>(() => PlateSolveSoftwareBinning.CopyBlockMeans(new ImageArray(new ushort[5]), 2, 2, 2));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void RestoredWcsUsesOriginalPixelUnitsAndSameFullFieldSkyGeometry(int factor)
    {
        var centre = new Coordinates(345.9, 28.08, Epoch.J2000, Coordinates.RAType.Degrees);
        var input = new PlateSolveResult { Success = true, Coordinates = centre,
            Pixscale = 0.384 * factor, PositionAngle = 147, Radius = 0.12, Flipped = true };
        var result = PlateSolveSoftwareBinning.RestoreDetectorScale(input, factor);
        Assert.Equal(0.384, result.Pixscale, 10);
        Assert.Equal(input.Coordinates.RADegrees, result.Coordinates.RADegrees, 10);
        Assert.Equal(input.Coordinates.Dec, result.Coordinates.Dec, 10);
        Assert.Equal(input.Radius, result.Radius);
        Assert.Equal(input.PositionAngle, result.PositionAngle);
        Assert.Equal(input.Flipped, result.Flipped);
        Assert.Equal(input.SolveTime, result.SolveTime);
        Assert.Equal(0.384 * factor, input.Pixscale, 10);
        // Full-field angular extent is invariant, and a pixel-edge coordinate
        // rescales about the same field centre without a crop-induced translation.
        Assert.Equal(1920 * result.Pixscale, 1920 / factor * input.Pixscale, 10);
        Assert.Equal((817.4 - 1920 / 2.0) * result.Pixscale,
            (817.4 / factor - 1920 / factor / 2.0) * input.Pixscale, 10);
    }

    [Fact]
    public void ProductionRestoresScaleBeforeEvidenceAndUsesOriginalDimensions()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        Assert.Contains("parameter.PixelSize *= actualSoftwareFactor", source);
        Assert.Contains("parameter.DownSampleFactor = 1", source);
        Assert.Contains("result = PlateSolveSoftwareBinning.RestoreDetectorScale(result, actualSoftwareFactor)", source);
        Assert.Contains("image.Properties.Width,\n            image.Properties.Height,\n            binning,", source.Replace("\r\n", "\n"));
        Assert.True(source.IndexOf("RestoreDetectorScale(result", StringComparison.Ordinal) <
            source.IndexOf("solvedRaDegrees = result.Coordinates", StringComparison.Ordinal));
    }
}
