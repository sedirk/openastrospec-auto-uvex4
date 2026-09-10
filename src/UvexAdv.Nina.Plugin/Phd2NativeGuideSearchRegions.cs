using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

/// <summary>Geometric exclusion only; never detects, scores or substitutes a star.</summary>
internal static class Phd2NativeGuideSearchRegions
{
    public static IReadOnlyList<Phd2Rectangle> Build(
        int width, int height, PixelPoint target, double targetGuard,
        SlitGeometry slit, double edgeGuard, double slitGuard,
        IReadOnlyList<PixelPoint> rejectedPoints, IReadOnlyList<Phd2Rectangle> searchedRegions,
        IReadOnlyList<Phd2Rectangle>? saturatedStructureExclusions = null)
    {
        if (width <= 0 || height <= 0 ||
            new[] { target.X, target.Y, targetGuard, edgeGuard, slitGuard, slit.AcquisitionPoint.X,
                slit.AcquisitionPoint.Y, slit.AngleDegrees, slit.LengthPixels, slit.WidthPixels }.Any(v => !double.IsFinite(v)) ||
            targetGuard < 0 || edgeGuard < 0 || slitGuard < 0 || slit.LengthPixels <= 0 || slit.WidthPixels <= 0)
            return [];
        if (target.X < 0 || target.X >= width || target.Y < 0 || target.Y >= height ||
            targetGuard > 2d * Math.Max(width, height) || edgeGuard > Math.Max(width, height) ||
            slit.LengthPixels > 4d * Math.Max(width, height) || slit.WidthPixels > Math.Max(width, height))
            return [];
        var edge = (int)Math.Ceiling(edgeGuard) + 1;
        if (width <= 2 * edge || height <= 2 * edge) return [];
        var regions = new List<Phd2Rectangle> { new(edge, edge, width - 2 * edge, height - 2 * edge) };
        var angle = slit.AngleDegrees * Math.PI / 180;
        var halfX = Math.Abs(Math.Cos(angle) * slit.LengthPixels / 2) + slit.WidthPixels / 2 + slitGuard + 1;
        var halfY = Math.Abs(Math.Sin(angle) * slit.LengthPixels / 2) + slit.WidthPixels / 2 + slitGuard + 1;
        var exclusions = new List<Phd2Rectangle>
        {
            Box(target, targetGuard + 1, targetGuard + 1),
            Box(slit.AcquisitionPoint, halfX, halfY),
        };
        exclusions.AddRange(rejectedPoints.Select(p => Box(p, Math.Max(edgeGuard, 20), Math.Max(edgeGuard, 20))));
        if (saturatedStructureExclusions is not null) exclusions.AddRange(saturatedStructureExclusions);
        exclusions.AddRange(searchedRegions);
        foreach (var exclusion in exclusions)
            regions = regions.SelectMany(region => Subtract(region, exclusion)).ToList();
        return regions.Where(r => r.Width >= 40 && r.Height >= 40)
            .OrderByDescending(r => (long)r.Width * r.Height).ThenBy(r => r.Y).ThenBy(r => r.X).ToArray();
    }

    private static Phd2Rectangle Box(PixelPoint center, double halfX, double halfY)
    {
        var x = (int)Math.Floor(center.X - halfX);
        var y = (int)Math.Floor(center.Y - halfY);
        return new(x, y, (int)Math.Ceiling(center.X + halfX) - x + 1, (int)Math.Ceiling(center.Y + halfY) - y + 1);
    }

    private static IEnumerable<Phd2Rectangle> Subtract(Phd2Rectangle r, Phd2Rectangle blocked)
    {
        var left = Math.Max(r.X, blocked.X);
        var top = Math.Max(r.Y, blocked.Y);
        var right = Math.Min(r.X + r.Width, blocked.X + blocked.Width);
        var bottom = Math.Min(r.Y + r.Height, blocked.Y + blocked.Height);
        if (left >= right || top >= bottom) { yield return r; yield break; }
        if (top > r.Y) yield return new(r.X, r.Y, r.Width, top - r.Y);
        if (bottom < r.Y + r.Height) yield return new(r.X, bottom, r.Width, r.Y + r.Height - bottom);
        if (left > r.X) yield return new(r.X, top, left - r.X, bottom - top);
        if (right < r.X + r.Width) yield return new(right, top, r.X + r.Width - right, bottom - top);
    }
}
