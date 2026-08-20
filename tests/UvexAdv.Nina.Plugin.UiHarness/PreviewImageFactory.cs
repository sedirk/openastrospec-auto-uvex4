using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UvexAdv.Nina.Plugin.UiHarness;

internal static class PreviewImageFactory
{
    private const int Width = 960;
    private const int Height = 420;

    public static ImageSource CreateQhyField()
    {
        var visual = CreateStarField(seed: 1208, defocused: false, out var context);
        using (context)
        {
            var cyan = new Pen(new SolidColorBrush(Color.FromRgb(34, 211, 238)), 2);
            context.DrawLine(cyan, new Point(Width / 2d - 18, Height / 2d), new Point(Width / 2d + 18, Height / 2d));
            context.DrawLine(cyan, new Point(Width / 2d, Height / 2d - 18), new Point(Width / 2d, Height / 2d + 18));
            context.DrawEllipse(null, cyan, new Point(Width / 2d, Height / 2d), 32, 32);
            DrawLabel(context, "QHY · WCS solved · target", 18, 18, Color.FromRgb(125, 211, 252));
        }

        return Render(visual);
    }

    public static ImageSource CreateG3SlitField(bool defocused)
    {
        var visual = CreateStarField(seed: 3503, defocused, out var context);
        using (context)
        {
            var slit = new Pen(new SolidColorBrush(Color.FromArgb(220, 240, 240, 230)), 3);
            context.DrawLine(slit, new Point(Width * 0.56, 36), new Point(Width * 0.56, Height - 36));

            var targetColor = defocused ? Color.FromRgb(248, 113, 113) : Color.FromRgb(45, 212, 191);
            var targetPen = new Pen(new SolidColorBrush(targetColor), 3);
            context.DrawEllipse(null, targetPen, new Point(Width * 0.53, Height * 0.49), defocused ? 32 : 18, defocused ? 32 : 18);
            context.DrawLine(targetPen, new Point(Width * 0.53 - 42, Height * 0.49), new Point(Width * 0.53 + 42, Height * 0.49));
            context.DrawLine(targetPen, new Point(Width * 0.53, Height * 0.49 - 42), new Point(Width * 0.53, Height * 0.49 + 42));
            DrawLabel(context, defocused ? "G3 · focus gate failed" : "G3 · slit centering", 18, 18, targetColor);
        }

        return Render(visual);
    }

    public static ImageSource CreateAtrSpectrum()
    {
        var visual = new DrawingVisual();
        using var context = visual.RenderOpen();
        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(3, 9, 18)), null, new Rect(0, 0, Width, Height));

        var bandBrush = new LinearGradientBrush(
            Color.FromArgb(30, 56, 189, 248),
            Color.FromArgb(95, 45, 212, 191),
            0);
        context.DrawRectangle(bandBrush, null, new Rect(32, Height * 0.42, Width - 64, Height * 0.16));

        var spectrum = new StreamGeometry();
        using (var geometry = spectrum.Open())
        {
            geometry.BeginFigure(new Point(32, Height * 0.72), false, false);
            for (var x = 33; x < Width - 32; x += 3)
            {
                var baseline = Height * 0.72;
                var peak1 = 115 * Math.Exp(-Math.Pow((x - 265) / 14d, 2));
                var peak2 = 82 * Math.Exp(-Math.Pow((x - 530) / 21d, 2));
                var peak3 = 135 * Math.Exp(-Math.Pow((x - 744) / 10d, 2));
                geometry.LineTo(new Point(x, baseline - peak1 - peak2 - peak3), true, false);
            }
        }

        spectrum.Freeze();
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(56, 189, 248)), 2), spectrum);
        DrawLabel(context, "ATR · 2D ROI + live 1D extraction", 18, 18, Color.FromRgb(125, 211, 252));
        return Render(visual);
    }

    private static DrawingVisual CreateStarField(int seed, bool defocused, out DrawingContext context)
    {
        var visual = new DrawingVisual();
        context = visual.RenderOpen();
        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(2, 8, 16)), null, new Rect(0, 0, Width, Height));

        var random = new Random(seed);
        for (var index = 0; index < 125; index++)
        {
            var x = random.NextDouble() * Width;
            var y = random.NextDouble() * Height;
            var brightness = (byte)random.Next(110, 245);
            var radius = defocused ? random.NextDouble() * 4.5 + 3.5 : random.NextDouble() * 2.4 + 0.7;
            var brush = new RadialGradientBrush(
                Color.FromArgb((byte)Math.Min(255, brightness + 10), 220, 239, 255),
                Color.FromArgb(0, 100, 180, 255));
            context.DrawEllipse(brush, null, new Point(x, y), radius * 2.1, radius * 2.1);
        }

        return visual;
    }

    private static void DrawLabel(DrawingContext context, string text, double x, double y, Color color)
    {
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            20,
            new SolidColorBrush(color),
            1.0);
        context.DrawText(formatted, new Point(x, y));
    }

    private static BitmapSource Render(DrawingVisual visual)
    {
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
