namespace UvexAdv.Observatory;

/// <summary>Use an intermediate solved neighbour before a long final WCS transfer.</summary>
public static class G3WcsApproachPolicy
{
    public const double NeighbourClearancePixels = 120;

    public static double GetNeighbourClearancePixels(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        // The 2026-09-09 Scheat arrival still included the saturated target
        // and its halo with only 120 px clearance. Stop EARLIER along the
        // same path, half the shorter detector dimension outside the frame.
        // This is a solve-staging point, not an increased motion/identity
        // allowance; the next leg still needs a new successful formal WCS.
        return Math.Max(NeighbourClearancePixels, Math.Min(width, height) / 2d);
    }
    public static PixelPoint ChooseTargetPixel(PixelPoint currentTarget, PixelPoint slit, int width, int height,
        int failedNeighbourApproaches = 0)
    {
        ArgumentNullException.ThrowIfNull(currentTarget);
        ArgumentNullException.ThrowIfNull(slit);
        if (width <= 0 || height <= 0 || failedNeighbourApproaches < 0 ||
            !double.IsFinite(currentTarget.X) || !double.IsFinite(currentTarget.Y) ||
            !double.IsFinite(slit.X) || !double.IsFinite(slit.Y) ||
            slit.X < 0 || slit.X >= width || slit.Y < 0 || slit.Y >= height)
            throw new ArgumentException("A WCS approach needs finite target geometry and an in-frame slit.");
        var dx = currentTarget.X - slit.X;
        var dy = currentTarget.Y - slit.Y;
        // Keep the very bright target beyond the frame for a neighbour solve.
        // This is one point on the existing outbound path, not another search
        // budget. The next formally solved frame can make the final short move.
        var clearancePixels = GetNeighbourClearancePixels(width, height);
        var tx = dx > 0 ? (width + clearancePixels - slit.X) / dx :
            dx < 0 ? (-clearancePixels - slit.X) / dx : double.PositiveInfinity;
        var ty = dy > 0 ? (height + clearancePixels - slit.Y) / dy :
            dy < 0 ? (-clearancePixels - slit.Y) / dy : double.PositiveInfinity;
        var fraction = Math.Min(tx, ty);
        // A radial 2 * max(width,height) shortcut classified the 2026-09-09
        // 10 Lac projection (-1065,-2097) as "nearby" on a 1920x1080 sensor.
        // Its 20 arcmin direct transfer then left the measured star far from
        // the mount-readback prediction. Use the actual expanded rectangle:
        // only a target already inside/on it takes the final leg. The epsilon
        // avoids an identical waypoint caused by roundoff at the boundary.
        if (fraction >= 1 - 1e-9) return slit;
        var first = new PixelPoint(slit.X + dx * fraction, slit.Y + dy * fraction);
        if (failedNeighbourApproaches == 0) return first;

        // Scheat's recovery twice solved the outer search field, then returned
        // to the same unsolvable intermediate field. Alternate along the SAME
        // expanded rectangle, retaining the halo clearance. This only selects
        // a waypoint: formal WCS, arrival checks and the durable motion/return
        // budget still authorize and charge its actual (possibly longer) path.
        var left = -clearancePixels;
        var top = -clearancePixels;
        var right = width + clearancePixels;
        var bottom = height + clearancePixels;
        var w = right - left;
        var h = bottom - top;
        var perimeter = 2 * (w + h);
        var origin = Math.Abs(first.Y - top) < 1e-6 ? first.X - left :
            Math.Abs(first.X - right) < 1e-6 ? w + first.Y - top :
            Math.Abs(first.Y - bottom) < 1e-6 ? w + h + right - first.X :
            2 * w + h + bottom - first.Y;
        var offset = Math.Ceiling(failedNeighbourApproaches / 2d) * clearancePixels *
            (failedNeighbourApproaches % 2 == 1 ? 1 : -1);
        var position = ((origin + offset) % perimeter + perimeter) % perimeter;
        if (position <= w) return new PixelPoint(left + position, top);
        if (position <= w + h) return new PixelPoint(right, top + position - w);
        if (position <= 2 * w + h) return new PixelPoint(right - (position - w - h), bottom);
        return new PixelPoint(left, bottom - (position - 2 * w - h));
    }
}
