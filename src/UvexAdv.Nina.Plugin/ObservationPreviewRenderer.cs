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
        return RenderG3Bitmap(image.RenderBitmapSource(), slit, target, guideStar);
    }

    internal static BitmapSource RenderG3Bitmap(BitmapSource bitmap, SlitGeometry? slit = null,
        PixelPoint? target = null, PixelPoint? guideStar = null)
    {
        var source = bitmap.Clone();
        var overlay = new DrawingGroup
        {
            ClipGeometry = new RectangleGeometry(new Rect(0, 0, source.PixelWidth, source.PixelHeight)),
        };
        using (var drawing = overlay.Open())
        {
            // Fix the overlay coordinate bounds without baking it into pixels.
            drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            if (slit is not null)
            {
                var angle = slit.AngleDegrees * Math.PI / 180d;
                var dx = Math.Cos(angle) * slit.LengthPixels / 2d;
                var dy = Math.Sin(angle) * slit.LengthPixels / 2d;
                var center = new Point(slit.AcquisitionPoint.X, slit.AcquisitionPoint.Y);
                if (slit.IlluminationExtent is { IsValid: true } extent)
                {
                    var visiblePen = new Pen(Brushes.Cyan, 2) { DashStyle = DashStyles.Dash };
                    Point Endpoint(double along) => new(center.X + Math.Cos(angle) * along,
                        center.Y + Math.Sin(angle) * along);
                    drawing.DrawLine(visiblePen, Endpoint(extent.StartOffsetPixels), Endpoint(extent.EndOffsetPixels));
                    foreach (var bound in new[] { extent.StartOffsetPixels, extent.EndOffsetPixels })
                    {
                        var end = Endpoint(bound);
                        drawing.DrawLine(visiblePen, end + new Vector(-Math.Sin(angle) * 8, Math.Cos(angle) * 8),
                            end - new Vector(-Math.Sin(angle) * 8, Math.Cos(angle) * 8));
                    }
                    DrawLabel(drawing,
                        $"虚线：LED可见段 {extent.LengthPixels:F0}px（非物理全长）；实线：标定中央段 {slit.LengthPixels:F0}px；十字：入缝锚点",
                        new Point(12, source.PixelHeight - 30), Brushes.Cyan);
                }
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
        return ObservationPreviewLayers.Attach(source, overlay: new DrawingImage(overlay));
    }

    internal static ushort[] CopyAtrRoi(IImageData image, ImageRoi roi, DispersionAxis axis)
    {
        roi.Validate(image.Properties.Width, image.Properties.Height);
        var raw = image.Data.FlatArray;
        var width = axis == DispersionAxis.Horizontal ? roi.Width : roi.Height;
        var height = axis == DispersionAxis.Horizontal ? roi.Height : roi.Width;
        var copy = new ushort[checked(width * height)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            copy[y * width + x] = axis == DispersionAxis.Horizontal
                ? raw[(roi.Y + y) * image.Properties.Width + roi.X + x]
                : raw[(roi.Y + x) * image.Properties.Width + roi.X + y];
        return copy;
    }

    public static BitmapSource RenderAtr(IImageData image, ImageRoi roi, double traceCenter = double.NaN,
        double traceHalfWidth = double.NaN, DispersionAxis axis = DispersionAxis.Horizontal)
    {
        var source = image.RenderBitmapSource();
        roi.Validate(source.PixelWidth, source.PixelHeight);
        var cropped = new CroppedBitmap(
            source,
            new Int32Rect(roi.X, roi.Y, roi.Width, roi.Height));
        // The scientific pixels remain a camera-sized bitmap. The 1D curve is
        // a separate vector view, not part of the contrast/zoom input bitmap.
        var values = CopyAtrRoi(image, roi, axis);
        var spectrum = SpectrumQuickLook.Extract(values,
            axis == DispersionAxis.Horizontal ? roi.Width : roi.Height,
            axis == DispersionAxis.Horizontal ? roi.Height : roi.Width,
            traceCenter, traceHalfWidth, Math.Pow(2, Math.Clamp(image.Properties.BitDepth, 1, 16)) - 1);
        var plot = RenderSpectrum(spectrum, null, string.Empty);
        Rect? focusRegion = null;
        if (double.IsFinite(traceCenter) && double.IsFinite(traceHalfWidth) && traceHalfWidth > 0 &&
            axis == DispersionAxis.Horizontal && traceCenter > 0 && traceCenter < roi.Height)
        {
            var padding = Math.Max(64, traceHalfWidth * 3);
            var top = Math.Max(0, traceCenter - padding);
            var bottom = Math.Min(roi.Height, traceCenter + padding);
            focusRegion = new Rect(0, top, roi.Width, bottom - top);
        }
        // Only a suggested viewport; the original ROI bitmap and science data
        // remain intact and the operator can switch back to the full frame.
        return ObservationPreviewLayers.Attach(cropped, spectrum: plot, focusRegion: focusRegion,
            spectrumDescription: "基础快览：谱带求和 − 两侧天空；未清理 · ADU / 孔径 · 未标定",
            englishSpectrumDescription: "Quick look: aperture sum minus sky; uncleaned · ADU / aperture · uncalibrated");
    }

    internal static BitmapSource WithReducedSpectrum(BitmapSource original, ReductionPreviewResult result)
    {
        var layers = ObservationPreviewLayers.For(original);
        var plot = RenderSpectrum(result.Flux.Select(v => v ?? double.NaN).ToArray(),
            result.RawFlux.Select(v => v ?? double.NaN).ToArray(),
            string.Empty);
        return ObservationPreviewLayers.Attach(original.Clone(), layers?.Overlay, plot, layers?.FocusRegion,
            $"青：后期提取（{result.Backend}） · 灰：未清理孔径 · ADU / 孔径；无光谱平滑",
            $"Cyan: reduced ({result.Backend}) · Gray: uncleaned aperture · ADU / aperture; no spectral smoothing");
    }

    internal static DrawingImage RenderSpectrum(IReadOnlyList<double> spectrum, IReadOnlyList<double>? raw, string label)
    {
        var plot = new DrawingGroup();
        using var drawing = plot.Open();
        drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(8, 19, 34)), new Pen(Brushes.SlateGray, 1), new Rect(0, 0, 1200, 210));
        DrawLabel(drawing, label, new Point(12, 6), Brushes.LightGray, 24);
        var finite = spectrum.Where(double.IsFinite).OrderBy(value => value).ToArray();
        if (finite.Length < 2)
        {
            DrawLabel(drawing, "本帧没有可靠谱带或有效天空区；不生成伪光谱。", new Point(50, 80), Brushes.Orange, 28);
            return new DrawingImage(plot);
        }
        var low = finite[0];
        var high = finite[^1];
        if (!(high > low)) high = low + 1;
        var rect = new Rect(110, 36, 1060, 142);
        // The vector plot is displayed at 90 DIPs tall. Keep axis labels legible
        // after that vertical scaling instead of reducing them to 5-pixel text.
        DrawLabel(drawing, high.ToString("G4", CultureInfo.InvariantCulture), new Point(4, 27), Brushes.LightSlateGray, 22);
        DrawLabel(drawing, low.ToString("G4", CultureInfo.InvariantCulture), new Point(4, 158), Brushes.LightSlateGray, 22);
        DrawLabel(drawing, "0", new Point(110, 182), Brushes.LightSlateGray, 22);
        DrawLabel(drawing, (spectrum.Count - 1).ToString(CultureInfo.InvariantCulture) + " px", new Point(1050, 182), Brushes.LightSlateGray, 22);
        drawing.PushClip(new RectangleGeometry(rect));
        if (raw is not null) DrawSpectrum(drawing, raw, rect, low, high, new Pen(Brushes.SlateGray, .8));
        DrawSpectrum(drawing, spectrum, rect, low, high, new Pen(Brushes.Turquoise, 1.2));
        drawing.Pop();
        return new DrawingImage(plot);
    }

    private static void DrawSpectrum(DrawingContext drawing, IReadOnlyList<double> spectrum, Rect rect, double low, double high, Pen pen)
    {
        if (spectrum.Count < 2) return;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var started = false;
            for (var index = 0; index < spectrum.Count; index++)
            {
                if (!double.IsFinite(spectrum[index])) { started = false; continue; }
                var x = rect.Left + index / (double)(spectrum.Count - 1) * rect.Width;
                var normalized = (spectrum[index] - low) / (high - low);
                var point = new Point(x, rect.Bottom - normalized * rect.Height);
                if (!started) { context.BeginFigure(point, false, false); started = true; }
                else context.LineTo(point, true, false);
            }
        }
        geometry.Freeze();
        drawing.DrawGeometry(null, pen, geometry);
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

}
