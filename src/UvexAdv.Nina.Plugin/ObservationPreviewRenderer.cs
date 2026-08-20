using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NINA.Image.Interfaces;
using UvexAdv.Observatory;
using UvexAdv.Spectroscopy;

namespace UvexAdv.Nina.Plugin;

internal static class ObservationPreviewRenderer
{
    public static BitmapSource RenderG3(
        IImageData image,
        SlitGeometry? slit = null,
        PixelPoint? target = null,
        PixelPoint? guideStar = null)
    {
        var source = image.RenderBitmapSource();
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            if (slit is not null)
            {
                var angle = slit.AngleDegrees * Math.PI / 180d;
                var dx = Math.Cos(angle) * slit.LengthPixels / 2d;
                var dy = Math.Sin(angle) * slit.LengthPixels / 2d;
                var center = new Point(slit.AcquisitionPoint.X, slit.AcquisitionPoint.Y);
                drawing.DrawLine(
                    new Pen(Brushes.DeepSkyBlue, Math.Max(2, slit.WidthPixels)),
                    new Point(center.X - dx, center.Y - dy),
                    new Point(center.X + dx, center.Y + dy));
                DrawCrosshair(drawing, center, Brushes.Cyan, 18);
            }
            if (target is not null)
            {
                var point = new Point(target.X, target.Y);
                drawing.DrawEllipse(null, new Pen(Brushes.OrangeRed, 4), point, 18, 18);
                DrawLabel(drawing, "TARGET", point + new Vector(22, -22), Brushes.OrangeRed);
            }
            if (guideStar is not null)
            {
                var point = new Point(guideStar.X, guideStar.Y);
                drawing.DrawEllipse(null, new Pen(Brushes.LimeGreen, 3), point, 15, 15);
                DrawLabel(drawing, "GUIDE", point + new Vector(19, 15), Brushes.LimeGreen);
            }
        }
        return Render(visual, source.PixelWidth, source.PixelHeight, source.DpiX, source.DpiY);
    }

    public static BitmapSource RenderAtr(IImageData image, ImageRoi roi)
    {
        var source = image.RenderBitmapSource();
        roi.Validate(source.PixelWidth, source.PixelHeight);
        var cropped = new CroppedBitmap(
            source,
            new Int32Rect(roi.X, roi.Y, roi.Width, roi.Height));
        const int canvasWidth = 1200;
        const int canvasHeight = 720;
        const double imageTop = 34;
        const double imageHeight = 450;
        const double plotTop = 515;
        const double plotHeight = 175;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(3, 10, 20)), null, new Rect(0, 0, canvasWidth, canvasHeight));
            DrawLabel(drawing, $"ATR585M spectral ROI {roi.X},{roi.Y} {roi.Width}×{roi.Height}", new Point(16, 8), Brushes.WhiteSmoke, 16);
            var imageRect = FitRect(cropped.PixelWidth, cropped.PixelHeight, new Rect(12, imageTop, canvasWidth - 24, imageHeight));
            drawing.DrawImage(cropped, imageRect);
            drawing.DrawRectangle(null, new Pen(Brushes.SlateGray, 1), imageRect);

            var spectrum = ExtractMeanSpectrum(image, roi);
            var plotRect = new Rect(46, plotTop, canvasWidth - 66, plotHeight);
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(8, 19, 34)), new Pen(Brushes.SlateGray, 1), plotRect);
            DrawSpectrum(drawing, spectrum, plotRect);
            DrawLabel(drawing, "即时 1D（沿空间方向稳健抽样平均；仅诊断）", new Point(48, plotTop - 25), Brushes.LightSkyBlue, 14);
            DrawLabel(drawing, "蓝端/红端方向以 Night Setup 为准", new Point(850, plotTop - 25), Brushes.Goldenrod, 13);
        }
        return Render(visual, canvasWidth, canvasHeight, 96, 96);
    }

    private static double[] ExtractMeanSpectrum(IImageData image, ImageRoi roi)
    {
        var values = image.Data.FlatArray;
        var imageWidth = image.Properties.Width;
        var rowStride = Math.Max(1, roi.Height / 240);
        var spectrum = new double[roi.Width];
        var rows = 0;
        for (var y = roi.Y; y < roi.Y + roi.Height; y += rowStride)
        {
            for (var x = 0; x < roi.Width; x++) spectrum[x] += values[y * imageWidth + roi.X + x];
            rows++;
        }
        if (rows > 0)
        {
            for (var x = 0; x < spectrum.Length; x++) spectrum[x] /= rows;
        }
        return spectrum;
    }

    private static void DrawSpectrum(DrawingContext drawing, IReadOnlyList<double> spectrum, Rect rect)
    {
        if (spectrum.Count < 2) return;
        var finite = spectrum.Where(double.IsFinite).OrderBy(value => value).ToArray();
        if (finite.Length == 0) return;
        var low = Percentile(finite, 0.02);
        var high = Percentile(finite, 0.995);
        if (!(high > low)) high = low + 1;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var step = Math.Max(1, spectrum.Count / (int)Math.Max(1, rect.Width));
            var started = false;
            for (var index = 0; index < spectrum.Count; index += step)
            {
                var x = rect.Left + index / (double)(spectrum.Count - 1) * rect.Width;
                var normalized = Math.Clamp((spectrum[index] - low) / (high - low), 0, 1);
                var point = new Point(x, rect.Bottom - normalized * rect.Height);
                if (!started) { context.BeginFigure(point, false, false); started = true; }
                else context.LineTo(point, true, false);
            }
        }
        geometry.Freeze();
        drawing.DrawGeometry(null, new Pen(Brushes.Turquoise, 1.5), geometry);
    }

    private static Rect FitRect(double width, double height, Rect bounds)
    {
        var scale = Math.Min(bounds.Width / width, bounds.Height / height);
        var fittedWidth = width * scale;
        var fittedHeight = height * scale;
        return new Rect(
            bounds.Left + (bounds.Width - fittedWidth) / 2,
            bounds.Top + (bounds.Height - fittedHeight) / 2,
            fittedWidth,
            fittedHeight);
    }

    private static BitmapSource Render(DrawingVisual visual, int width, int height, double dpiX, double dpiY)
    {
        var bitmap = new RenderTargetBitmap(width, height, dpiX > 0 ? dpiX : 96, dpiY > 0 ? dpiY : 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DrawCrosshair(DrawingContext drawing, Point center, Brush brush, double radius)
    {
        var pen = new Pen(brush, 3);
        drawing.DrawLine(pen, new Point(center.X - radius, center.Y), new Point(center.X + radius, center.Y));
        drawing.DrawLine(pen, new Point(center.X, center.Y - radius), new Point(center.X, center.Y + radius));
    }

    private static void DrawLabel(
        DrawingContext drawing,
        string text,
        Point point,
        Brush brush,
        double size = 15)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            size,
            brush,
            1);
        drawing.DrawText(formatted, point);
    }

    private static double Percentile(IReadOnlyList<double> sorted, double fraction)
    {
        var index = Math.Clamp(fraction, 0, 1) * (sorted.Count - 1);
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        return lower == upper ? sorted[lower] : sorted[lower] + (index - lower) * (sorted[upper] - sorted[lower]);
    }
}
