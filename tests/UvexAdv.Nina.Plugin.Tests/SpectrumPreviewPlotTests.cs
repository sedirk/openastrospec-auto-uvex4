using System.Windows;
using System.Windows.Media;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class SpectrumPreviewPlotTests
{
    [Fact]
    public void PreviewCopiesValuesWithoutSmoothingOrReplacingMissingSamples()
    {
        double[] flux = [4, 428000, double.NaN, -2, 10];
        double[] raw = [8, 450000, double.NaN, 0, 12];
        var preview = new SpectrumPreviewData(flux, raw);
        flux[1] = raw[1] = 0;
        Assert.Equal(428000, preview.Flux[1]);
        Assert.Equal(450000, preview.Raw![1]);
        Assert.True(double.IsNaN(preview.Flux[2]));
        Assert.Equal(-2, preview.Flux[3]);
    }

    [Theory]
    [InlineData(6620, 428000)]
    [InlineData(-120, 24)]
    [InlineData(17, 17)]
    [InlineData(0, 0)]
    public void AxisHasHeadroomAndFiniteTicks(double minimum, double maximum)
    {
        var (low, high, step) = SpectrumPreviewPlot.AxisRange(minimum, maximum);
        Assert.True(double.IsFinite(low + high + step));
        Assert.True(low < minimum && high > maximum && step > 0);
        Assert.InRange((high - low) / step, 1, 8);
    }

    [Fact]
    public void GapsSplitThePolylineAndPeaksAreNotResampled()
    {
        var geometry = SpectrumPreviewPlot.SeriesGeometry([0, 10, double.NaN, 5, 0], new Rect(0, 0, 400, 100), 0, 10);
        var figures = PathGeometry.CreateFromGeometry(geometry).Figures;
        Assert.Equal(2, figures.Count);
        Assert.Equal(new Point(0, 100), figures[0].StartPoint);
        Assert.Equal(new Point(300, 50), figures[1].StartPoint);
        Assert.Contains(new Point(100, 0), figures[0].Segments.OfType<PolyLineSegment>().SelectMany(x => x.Points)
            .Concat(figures[0].Segments.OfType<LineSegment>().Select(x => x.Point)));
    }
}
