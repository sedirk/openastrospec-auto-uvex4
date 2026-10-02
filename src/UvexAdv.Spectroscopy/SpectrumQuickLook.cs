namespace UvexAdv.Spectroscopy;

/// <summary>Uncleaned trace aperture minus adjacent sky. Display only; no gate or loop uses this.</summary>
public static class SpectrumQuickLook
{
    public static double[] Extract(ushort[] pixels, int width, int height, double center, double halfWidth, double saturation)
    {
        if (pixels.Length != checked(width * height)) throw new ArgumentException("Invalid pixel buffer.");
        var flux = Enumerable.Repeat(double.NaN, width).ToArray();
        if (!double.IsFinite(center) || !double.IsFinite(halfWidth) || halfWidth < 2 || halfWidth > 128) return flux;
        var lower = (int)Math.Round(center) - (int)Math.Ceiling(halfWidth);
        var upper = (int)Math.Round(center) + (int)Math.Ceiling(halfWidth);
        if (lower < 0 || upper >= height) return flux;
        for (var x = 0; x < width; x++)
        {
            var sky = new List<double>(28);
            foreach (var start in new[] { lower - 8 - 14, upper + 1 + 8 })
                for (var y = Math.Max(0, start); y < Math.Min(height, start + 14); y++)
                    if (pixels[y * width + x] < saturation * .999) sky.Add(pixels[y * width + x]);
            if (sky.Count < 7) continue;
            var background = RobustStatistics.Median(sky);
            var sum = 0d;
            for (var y = lower; y <= upper; y++)
            {
                var value = pixels[y * width + x];
                if (value >= saturation * .999) { sum = double.NaN; break; }
                sum += value - background;
            }
            flux[x] = sum;
        }
        return flux;
    }
}
