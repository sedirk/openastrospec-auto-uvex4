using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace UvexAdv.Nina.Plugin;

/// <summary>Detached, immutable display samples; never used for exposure acceptance.</summary>
internal sealed class SpectrumPreviewData(IReadOnlyList<double> flux, IReadOnlyList<double>? raw = null)
{
    public IReadOnlyList<double> Flux { get; } = Array.AsReadOnly(flux.ToArray());
    public IReadOnlyList<double>? Raw { get; } = raw is null ? null : Array.AsReadOnly(raw.ToArray());
}

/// <summary>Draw at the viewport's actual DIP size, without stretching text or smoothing samples.</summary>
public sealed class SpectrumPreviewPlot : FrameworkElement
{
    private SpectrumPreviewData? spectrum;
    internal SpectrumPreviewData? Spectrum
    {
        get => spectrum;
        set { spectrum = value; InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth < 120 || ActualHeight < 65) return;
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        drawing.DrawRoundedRectangle(Brush("#0B1625"), null, bounds, 4, 4);
        if (spectrum is null) return;
        if (spectrum.Flux.Count(double.IsFinite) < 2)
        {
            Label(drawing, ObservationUiPresentation.Text("没有可靠谱带 · 不生成伪光谱", "No reliable trace · no fabricated spectrum"),
                new Point(14, ActualHeight / 2 - 8), Brush("#FBBF24"));
            return;
        }
        var values = spectrum.Flux.Concat(spectrum.Raw ?? []).Where(double.IsFinite).ToArray();
        var (low, high, step) = AxisRange(values.Min(), values.Max());
        var area = new Rect(65, 20, ActualWidth - 80, ActualHeight - 46);
        var muted = Brush("#94A3B8");
        var grid = new Pen(Brush("#243448"), 1);
        var scale = Math.Max(Math.Abs(low), Math.Abs(high)) >= 10000 ? 1000d : 1d;
        Label(drawing, scale == 1000 ? "ADU / 1,000" : "ADU", new Point(8, 1), muted, 10);
        for (var tick = 0; tick <= 8; tick++)
        {
            var value = low + tick * step;
            if (value > high + step * 0.01) break;
            var y = area.Bottom - (value - low) / (high - low) * area.Height;
            drawing.DrawLine(grid, new Point(area.Left, y), new Point(area.Right, y));
            Label(drawing, (value / scale).ToString("0.###", CultureInfo.InvariantCulture),
                new Point(area.Left - 8, y - 7), muted, align: TextAlignment.Right);
        }
        var intervals = Math.Clamp((int)(area.Width / 140), 2, 6);
        for (var tick = 0; tick <= intervals; tick++)
        {
            var pixel = (int)Math.Round((spectrum.Flux.Count - 1d) * tick / intervals);
            var x = area.Left + area.Width * tick / intervals;
            drawing.DrawLine(grid, new Point(x, area.Top), new Point(x, area.Bottom));
            Label(drawing, pixel.ToString(CultureInfo.InvariantCulture), new Point(x, area.Bottom + 6), muted,
                align: tick == 0 ? TextAlignment.Left : tick == intervals ? TextAlignment.Right : TextAlignment.Center);
        }
        drawing.PushClip(new RectangleGeometry(area));
        if (spectrum.Raw is { } raw) DrawSeries(drawing, raw, area, low, high, new Pen(Brush("#64748B"), 1));
        DrawSeries(drawing, spectrum.Flux, area, low, high, new Pen(Brush("#2DD4BF"), 1.4));
        drawing.Pop();
    }

    internal static (double Low, double High, double Step) AxisRange(double minimum, double maximum)
    {
        var span = Math.Max(maximum - minimum, Math.Max(1, Math.Abs(maximum) * 0.05));
        var proposed = span * 1.08 / 4;
        var power = Math.Pow(10, Math.Floor(Math.Log10(proposed)));
        var fraction = proposed / power;
        var step = (fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10) * power;
        return (Math.Floor((minimum - span * .04) / step) * step,
            Math.Ceiling((maximum + span * .04) / step) * step, step);
    }

    internal static StreamGeometry SeriesGeometry(IReadOnlyList<double> samples, Rect area, double low, double high)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var started = false;
            for (var i = 0; i < samples.Count; i++)
            {
                if (!double.IsFinite(samples[i])) { started = false; continue; }
                var point = new Point(area.Left + i / (double)Math.Max(1, samples.Count - 1) * area.Width,
                    area.Bottom - (samples[i] - low) / (high - low) * area.Height);
                if (!started) { context.BeginFigure(point, false, false); started = true; }
                else context.LineTo(point, true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private static void DrawSeries(DrawingContext drawing, IReadOnlyList<double> samples, Rect area,
        double low, double high, Pen pen) => drawing.DrawGeometry(null, pen, SeriesGeometry(samples, area, low, high));

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));

    private void Label(DrawingContext drawing, string text, Point point, Brush brush, double size = 11,
        TextAlignment align = TextAlignment.Left)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var offset = align == TextAlignment.Right ? formatted.Width : align == TextAlignment.Center ? formatted.Width / 2 : 0;
        drawing.DrawText(formatted, new Point(point.X - offset, point.Y));
    }
}
