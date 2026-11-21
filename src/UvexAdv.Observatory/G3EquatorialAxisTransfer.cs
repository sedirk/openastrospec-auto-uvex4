namespace UvexAdv.Observatory;

/// <summary>
/// Transfers a measured equatorial-coordinate change to another readback of
/// the same axes. All coordinates must have the same epoch and pier side.
/// This is an incremental pointing correction, not a plate solve or a sync.
/// The caller must still reserve the actual spherical command and its return.
/// </summary>
public static class G3EquatorialAxisTransfer
{
    // Optical and encoder/readback declinations can differ after unverified
    // Sync. Cap the projected command, not only the vector in the optical TAN
    // plane. Each probe here is arithmetic only. The durable reserve is final.
    public static double FitCommandScale(double proposed, double maximum, Func<double, double> mountDistance)
    {
        if (!(proposed > 0 && proposed <= 1 && double.IsFinite(maximum) && maximum > 0)) return double.NaN;
        var full = mountDistance(proposed);
        if (!double.IsFinite(full) || full < 0) return double.NaN;
        if (full < maximum - .01) return proposed;
        var lo = 0d;
        var hi = proposed;
        for (var i = 0; i < 24; i++)
        {
            var mid = (lo + hi) * .5;
            var actual = mountDistance(mid);
            if (!double.IsFinite(actual) || actual < 0) return double.NaN;
            if (actual <= maximum - .01) lo = mid; else hi = mid;
        }
        return lo > 0 ? lo : double.NaN;
    }

    public static (double RaDegrees, double DecDegrees) Apply(
        double sourceRaDegrees, double sourceDecDegrees,
        double destinationRaDegrees, double destinationDecDegrees,
        double readbackRaDegrees, double readbackDecDegrees)
    {
        if (!Valid(sourceRaDegrees, sourceDecDegrees) ||
            !Valid(destinationRaDegrees, destinationDecDegrees) ||
            !Valid(readbackRaDegrees, readbackDecDegrees))
            return (double.NaN, double.NaN);

        // A tangent-plane eastward distance depends on cos(dec). Reapplying
        // it at a different readback declination changes the RA-axis command,
        // even though the measured correction was to the same physical axis.
        var deltaRa = (destinationRaDegrees - sourceRaDegrees + 540) % 360 - 180;
        var dec = readbackDecDegrees + destinationDecDegrees - sourceDecDegrees;
        if (Math.Abs(deltaRa) >= 90 || Math.Abs(dec) >= 89.999999)
            return (double.NaN, double.NaN);
        return ((readbackRaDegrees + deltaRa + 360) % 360, dec);
    }

    private static bool Valid(double ra, double dec) =>
        double.IsFinite(ra) && ra >= 0 && ra < 360 &&
        double.IsFinite(dec) && Math.Abs(dec) < 89.999999;
}
