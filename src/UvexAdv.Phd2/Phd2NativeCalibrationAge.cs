using System.Globalization;

namespace UvexAdv.Phd2;

/// <summary>Reads PHD2's native tab-separated export, not stale registry data.
/// Only orig_timestamp is age: timestamp is rewritten by SetCalibration on flips.
/// The native base geometry must match the separately read active RPC calibration.</summary>
public static class Phd2NativeCalibrationAge
{
    public static DateTimeOffset Read(string export, Phd2Profile profile, Phd2CalibrationData active,
        CultureInfo culture, TimeZoneInfo timeZone)
    {
        var lines = export.Split('\n');
        if (lines.Length == 0 || lines[0].TrimStart('\uFEFF').TrimEnd('\r') != "PHD Config 1")
            throw new InvalidDataException("Unsupported native PHD2 settings export.");
        var prefix = $"/profile/{profile.Id}/";
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            var parts = line.TrimEnd('\r').Split('\t', 3);
            if (parts.Length != 3 || !parts[0].StartsWith(prefix, StringComparison.Ordinal)) continue;
            var key = parts[0][prefix.Length..];
            if (key != "name" && !key.StartsWith("scope/calibration/", StringComparison.Ordinal)) continue;
            if (!values.TryAdd(key, parts[2]))
                throw new InvalidDataException("Duplicate native calibration export entry.");
        }
        string Value(string key) => values.TryGetValue(key, out var value) ? value
            : throw new InvalidDataException($"Native calibration export is missing {key}.");
        double Number(string key)
        {
            if (double.TryParse(Value("scope/calibration/" + key), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)) return value;
            throw new InvalidDataException($"Native calibration {key} is not finite.");
        }
        string Parity(string key) => Number(key) switch { 1 => "+", -1 => "-", _ => "?" };
        static bool Near(double? actual, double expected, double tolerance) =>
            actual is { } value && double.IsFinite(value) && Math.Abs(value - expected) <= tolerance;
        static bool AngleNear(double? actual, double radians) => actual is { } angle && double.IsFinite(angle) &&
            Math.Abs(Math.IEEERemainder(angle - radians * 180 / Math.PI, 360)) <= 0.051;

        // Export uses %g (6 significant figures); RPC uses degrees to 0.1,
        // rates to 0.001 px/s and declination to 0.0001 degrees. Tolerances
        // cover representation rounding only, not a different calibration.
        if (Value("name") != profile.Name || !active.Calibrated ||
            !AngleNear(active.RaAngleDegrees, Number("xAngle")) ||
            !AngleNear(active.DecAngleDegrees, Number("yAngle")) ||
            !Near(active.DecRatePixelsPerSecond, Number("yRate") * 1000, 0.0011) ||
            !Near(active.DeclinationDegrees, Number("declination") * 180 / Math.PI, 0.0002) ||
            Parity("raGuideParity") == "?" || Parity("decGuideParity") == "?" ||
            active.RaParity != Parity("raGuideParity") || active.DecParity != Parity("decGuideParity") ||
            Number("xRate") <= 0 || Number("yRate") <= 0 ||
            active.RaRatePixelsPerSecond is not { } raRate || !double.IsFinite(raRate) || raRate <= 0)
            throw new InvalidDataException("Native calibration export does not match the current profile/active base calibration.");
        // Active RA rate is declination-compensated and is deliberately not
        // equated with saved xRate. Normal validation still checks its quality.
        var timestamp = Value("scope/calibration/orig_timestamp");
        if (!DateTime.TryParseExact(timestamp, ["yyyy/M/d H:mm:ss", "yyyy-MM-dd HH:mm:ss"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var local) &&
            !DateTime.TryParse(timestamp, culture, DateTimeStyles.None, out local))
            throw new InvalidDataException("Native original calibration time is unparseable; it is not replaced with export time.");
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(local) || timeZone.IsAmbiguousTime(local))
            throw new InvalidDataException("Native original calibration local time is ambiguous or invalid.");
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone));
    }
}
