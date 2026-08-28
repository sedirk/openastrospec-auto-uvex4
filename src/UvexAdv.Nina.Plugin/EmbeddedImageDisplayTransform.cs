using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UvexAdv.Nina.Plugin;

internal readonly record struct EmbeddedImageDisplayLevels(
    byte BlackPoint,
    byte WhitePoint,
    double Gamma,
    bool Automatic);

/// <summary>
/// Display-only stretch for the already immutable preview bitmap.  It never
/// changes a FITS file, acquisition evidence, plate-solving input or quality
/// measurement.  N.I.N.A.'s renderer remains the source of ATR/G3 previews;
/// this layer only gives the operator the missing black/white/gamma controls.
/// </summary>
internal static class EmbeddedImageDisplayTransform
{
    private const double AutoLowFraction = 0.005;
    private const double AutoHighFraction = 0.9975;

    public static BitmapSource Apply(
        BitmapSource source,
        bool automatic,
        double requestedBlackPoint,
        double requestedWhitePoint,
        double requestedGamma,
        out EmbeddedImageDisplayLevels levels)
    {
        ArgumentNullException.ThrowIfNull(source);

        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        converted.CopyPixels(pixels, stride, 0);

        byte black;
        byte white;
        if (automatic)
        {
            (black, white) = CalculateAutomaticLevels(pixels);
        }
        else
        {
            black = (byte)Math.Clamp(Math.Round(requestedBlackPoint), 0, 254);
            white = (byte)Math.Clamp(Math.Round(requestedWhitePoint), black + 1, 255);
        }

        var gamma = double.IsFinite(requestedGamma)
            ? Math.Clamp(requestedGamma, 0.2, 5.0)
            : 1.0;
        var lut = CreateLookupTable(black, white, gamma);
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = lut[pixels[index]];
            pixels[index + 1] = lut[pixels[index + 1]];
            pixels[index + 2] = lut[pixels[index + 2]];
            // Alpha is deliberately left unchanged.
        }

        var result = BitmapSource.Create(
            width,
            height,
            source.DpiX > 0 ? source.DpiX : 96,
            source.DpiY > 0 ? source.DpiY : 96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        result.Freeze();
        levels = new EmbeddedImageDisplayLevels(black, white, gamma, automatic);
        return result;
    }

    private static (byte Black, byte White) CalculateAutomaticLevels(byte[] pixels)
    {
        var histogram = new int[256];
        var count = 0;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index + 3] == 0) continue;
            var luminance = ((19 * pixels[index]) + (183 * pixels[index + 1]) + (54 * pixels[index + 2])) >> 8;
            histogram[luminance]++;
            count++;
        }

        if (count == 0) return (0, 255);
        var black = Quantile(histogram, count, AutoLowFraction);
        var white = Quantile(histogram, count, AutoHighFraction);
        if (white <= black)
        {
            black = (byte)Math.Max(0, black - 1);
            white = (byte)Math.Min(255, black + 2);
        }
        return (black, white);
    }

    private static byte Quantile(IReadOnlyList<int> histogram, int count, double fraction)
    {
        var target = Math.Clamp((int)Math.Ceiling(count * fraction), 1, count);
        var cumulative = 0;
        for (var value = 0; value < histogram.Count; value++)
        {
            cumulative += histogram[value];
            if (cumulative >= target) return (byte)value;
        }
        return 255;
    }

    private static byte[] CreateLookupTable(byte black, byte white, double gamma)
    {
        var result = new byte[256];
        var range = white - black;
        var inverseGamma = 1.0 / gamma;
        for (var value = 0; value < result.Length; value++)
        {
            var normalized = Math.Clamp((value - black) / (double)range, 0, 1);
            result[value] = (byte)Math.Clamp(
                Math.Round(Math.Pow(normalized, inverseGamma) * 255),
                0,
                255);
        }
        return result;
    }
}
