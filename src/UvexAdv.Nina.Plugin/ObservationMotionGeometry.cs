using NINA.Astrometry;
using UvexAdv.Observatory;
namespace UvexAdv.Nina.Plugin;

internal static class ObservationMotionGeometry
{

    internal static G3AcquisitionMotionState ReanchorG3AcquisitionMotionFromReportedPosition(
        G3AcquisitionMotionState state,
        Coordinates reported)
    {
        if (!string.Equals(state.CoordinateEpoch, reported.Epoch.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Durable G3 motion uses epoch '{state.CoordinateEpoch}', but the mount reports '{reported.Epoch}'.");
        }
        var (ra, dec) = G3AcquisitionMotionPlanner.SignedTangentOffsetArcseconds(
            state.OriginRaDegrees,
            state.OriginDeclinationDegrees,
            NormalizeDegrees(reported.RADegrees),
            reported.Dec);
        if (!double.IsFinite(ra) || !double.IsFinite(dec))
        {
            throw new InvalidOperationException("The mount's reported position cannot be expressed in the durable G3 tangent plane.");
        }
        return state with
        {
            PriorReportedRaDegrees = NormalizeDegrees(reported.RADegrees),
            PriorReportedDeclinationDegrees = reported.Dec,
            CurrentRaTangentOffsetArcseconds = ra,
            CurrentDeclinationOffsetArcseconds = dec,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
    }


    internal static double AngularSeparationArcseconds(Coordinates a, Coordinates b)
    {
        // Coordinates represent a physical direction, not just two raw RA/Dec numbers.
        // N.I.N.A. accepts J2000 catalogue/WCS coordinates and converts them to the
        // mount's reported epoch (normally JNOW). Comparing those raw values directly
        // turns ordinary precession into a false pointing residual (about 1187 arcsec
        // for the 2026-08-29 QHY recovery incident). Always compare in one epoch.
        var bInAEpoch = b.Epoch == a.Epoch ? b : b.Transform(a.Epoch);
        var ra1 = a.RADegrees * Math.PI / 180d;
        var ra2 = bInAEpoch.RADegrees * Math.PI / 180d;
        var dec1 = a.Dec * Math.PI / 180d;
        var dec2 = bInAEpoch.Dec * Math.PI / 180d;
        var cosine = Math.Sin(dec1) * Math.Sin(dec2) + Math.Cos(dec1) * Math.Cos(dec2) * Math.Cos(ra1 - ra2);
        return Math.Acos(Math.Clamp(cosine, -1, 1)) * 180d / Math.PI * 3600d;
    }


    internal static double NormalizeDegrees(double value) => ((value % 360) + 360) % 360;
}
