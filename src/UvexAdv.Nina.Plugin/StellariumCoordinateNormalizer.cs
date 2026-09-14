using System.Text.Json;
using NINA.Astrometry;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// RemoteControl raJ2000/decJ2000 are directions in J2000 axes, NOT necessarily
/// catalogue astrometry. Stellarium adds v/c and normalizes (StelCore and
/// StarWrapper/CustomObject). Invert that operation, not NINA's JNOW transform.
/// NINA still performs its own apparent-place conversion at the mount boundary.
/// </summary>
internal static class StellariumCoordinateNormalizer
{
    internal const string Convention = "AstrometricJ2000";
    internal sealed record State(double JulianDay, double DeltaTDays, string Planet,
        bool AberrationEnabled, double AberrationFactor, bool ParallaxEnabled, double ParallaxFactor);
    internal sealed record Velocity(double X, double Y, double Z);

    internal static State ReadState(JsonElement status, JsonElement properties)
    {
        var time = status.GetProperty("time");
        double Number(JsonElement root, string name) => root.GetProperty(name).GetDouble();
        JsonElement Property(string name) => properties.GetProperty("StelCore." + name).GetProperty("value");
        var state = new State(Number(time, "jday"), Number(time, "deltaT"),
            status.GetProperty("location").GetProperty("planet").GetString() ?? string.Empty,
            Property("flagUseAberration").GetBoolean(), Property("aberrationFactor").GetDouble(),
            Property("flagUseParallax").GetBoolean(), Property("parallaxFactor").GetDouble());
        if (state.Planet != "Earth" || !double.IsFinite(state.JulianDay) ||
            state.JulianDay is < 2415020.5 or > 2488069.5 ||
            !double.IsFinite(state.DeltaTDays) || Math.Abs(state.DeltaTDays) > 0.1 ||
            !double.IsFinite(state.AberrationFactor) || state.AberrationFactor is < 0 or > 1 ||
            !double.IsFinite(state.ParallaxFactor) || state.ParallaxFactor is < 0 or > 1)
            throw new InvalidOperationException("Only Earth, 1900–2100, and physical (not exaggerated) aberration/parallax settings are supported.");
        return state;
    }

    internal static bool SameState(State before, State after) =>
        before with { JulianDay = after.JulianDay, DeltaTDays = after.DeltaTDays } == after &&
        Math.Abs(before.JulianDay - after.JulianDay) * 86400 <= 10 &&
        Math.Abs(before.DeltaTDays - after.DeltaTDays) * 86400 < 0.01;

    internal static Velocity EarthVelocity(double julianEphemerisDay)
    {
        // Reuse the ephemeris already shipped/initialized by NINA. Units are
        // AU/day, equatorial ICRS; no new ephemeris, SDK or device owner.
        var v = NOVAS.BodyPositionAndVelocity(julianEphemerisDay, NOVAS.Body.Earth,
            NOVAS.SolarSystemOrigin.Barycenter).Velocity;
        const double auPerLightDay = 149597870700d / (299792458d * 86400d);
        return new(v.X * auPerLightDay, v.Y * auPerLightDay, v.Z * auPerLightDay);
    }

    internal static (ObservationTargetCoordinates Coordinates, TargetCoordinateProvenance Provenance) Normalize(
        ObservationTargetCoordinates raw, State state, string objectType, string? subtype,
        Func<double, Velocity>? velocityProvider = null)
    {
        // Marker points and solar-system objects have different astrometric
        // semantics. A SIMBAD CustomObject is a fixed catalogue direction;
        // arbitrary markers must not inherit that assumption.
        var supported = objectType.ToLowerInvariant() is "star" or "nebula" or "quasar" or "pulsar" or
            "supernova" or "nova" or "exoplanet" ||
            (objectType == "CustomObject" && subtype?.StartsWith("SIMBAD;", StringComparison.Ordinal) == true);
        // Nebula::getInfoMap localizes "type"; "object-type" remains the
        // machine-readable DSO type. Never infer an object from its name.
        var classification = TargetCatalogClassifier.Classify(new("selected", null,
            raw.RightAscensionDegrees, raw.DeclinationDegrees, "Stellarium", objectType, null,
            DateTimeOffset.UtcNow, subtype));
        supported |= classification.HasKnownObjectType && !classification.IsUnsupportedObjectType;
        if (classification.IsUnsupportedObjectType) supported = false;
        if (!supported) throw new InvalidOperationException("Selected object has no supported fixed-catalogue coordinate contract: " + objectType);
        if (!double.IsFinite(raw.RightAscensionDegrees) || !double.IsFinite(raw.DeclinationDegrees) ||
            Math.Abs(raw.DeclinationDegrees) > 90) throw new InvalidOperationException("Invalid selected coordinates.");
        var ra = raw.RightAscensionDegrees * Math.PI / 180;
        var dec = raw.DeclinationDegrees * Math.PI / 180;
        var x = Math.Cos(dec) * Math.Cos(ra);
        var y = Math.Cos(dec) * Math.Sin(ra);
        var z = Math.Sin(dec);
        var beta = state.AberrationEnabled && state.AberrationFactor != 0
            ? (velocityProvider ?? EarthVelocity)(state.JulianDay + state.DeltaTDays)
            : new Velocity(0, 0, 0);
        beta = new(beta.X * state.AberrationFactor, beta.Y * state.AberrationFactor, beta.Z * state.AberrationFactor);
        var b2 = beta.X * beta.X + beta.Y * beta.Y + beta.Z * beta.Z;
        if (!double.IsFinite(b2) || b2 > 0.000001) throw new InvalidOperationException("Earth velocity unavailable or invalid.");
        // q = normalize(p+b), |p|=1. Solve |kq-b|=1 exactly; simply
        // subtracting RA/Dec or subtracting b then normalizing is not its inverse.
        var dot = x * beta.X + y * beta.Y + z * beta.Z;
        var k = dot + Math.Sqrt(1 - b2 + dot * dot);
        x = k * x - beta.X; y = k * y - beta.Y; z = k * z - beta.Z;
        var corrected = new ObservationTargetCoordinates(
            (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360,
            Math.Atan2(z, Math.Sqrt(x * x + y * y)) * 180 / Math.PI);
        var offset = G3AcquisitionMotionPlanner.AngularSeparationArcseconds(
            ((raw.RightAscensionDegrees % 360) + 360) % 360, raw.DeclinationDegrees,
            corrected.RightAscensionDegrees, corrected.DeclinationDegrees);
        return (corrected, new TargetCoordinateProvenance(Convention,
            "Stellarium RemoteControl / raJ2000,decJ2000; inverse normalize(p+v/c); NINA NOVAS Earth ephemeris",
            raw.RightAscensionDegrees, raw.DeclinationDegrees, state.JulianDay + state.DeltaTDays,
            state.AberrationEnabled, offset,
            "J2000 axes; source-epoch proper motion/parallax retained when supplied by Stellarium; not JNOW or catalogue epoch reset"));
    }
}
