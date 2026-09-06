namespace UvexAdv.Observatory;

/// <summary>Use an intermediate solved neighbour before a long final WCS transfer.</summary>
public static class G3WcsApproachPolicy
{
    public const double NeighbourClearancePixels = 120;
    public static PixelPoint ChooseTargetPixel(PixelPoint currentTarget, PixelPoint slit, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(currentTarget);
        ArgumentNullException.ThrowIfNull(slit);
        if (width <= 0 || height <= 0 ||
            !double.IsFinite(currentTarget.X) || !double.IsFinite(currentTarget.Y) ||
            !double.IsFinite(slit.X) || !double.IsFinite(slit.Y) ||
            slit.X < 0 || slit.X >= width || slit.Y < 0 || slit.Y >= height)
            throw new ArgumentException("A WCS approach needs finite target geometry and an in-frame slit.");
        var dx = currentTarget.X - slit.X;
        var dy = currentTarget.Y - slit.Y;
        if (Math.Sqrt(dx * dx + dy * dy) <= 2 * Math.Max(width, height)) return slit;

        // Keep the very bright target beyond the frame for a neighbour solve.
        // This is one point on the existing outbound path, not another search
        // budget. The next formally solved frame can make the final short move.
        const double clearancePixels = NeighbourClearancePixels;
        var tx = dx > 0 ? (width + clearancePixels - slit.X) / dx :
            dx < 0 ? (-clearancePixels - slit.X) / dx : double.PositiveInfinity;
        var ty = dy > 0 ? (height + clearancePixels - slit.Y) / dy :
            dy < 0 ? (-clearancePixels - slit.Y) / dy : double.PositiveInfinity;
        var fraction = Math.Min(tx, ty);
        return new PixelPoint(slit.X + dx * fraction, slit.Y + dy * fraction);
    }
}
