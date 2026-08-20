namespace UvexAdv.Observatory;

public sealed record AltitudeSample(
    DateTimeOffset TimestampUtc,
    double AltitudeDegrees,
    double AzimuthDegrees,
    double RequiredAltitudeDegrees,
    bool Passed);

public sealed record HorizonEvaluation(
    bool Passed,
    double MinimumAltitudeDegrees,
    double MinimumClearanceDegrees,
    AltitudeSample? FirstFailure,
    IReadOnlyList<AltitudeSample> Samples)
{
    public GateResult ToGateResult() => Passed
        ? GateResult.Pass(
            "HORIZON_CLEAR",
            $"Target remains above the configured horizon; minimum clearance {MinimumClearanceDegrees:F2}°.",
            new Dictionary<string, double>
            {
                ["minimumAltitudeDegrees"] = MinimumAltitudeDegrees,
                ["minimumClearanceDegrees"] = MinimumClearanceDegrees
            })
        : GateResult.Fail(
            "HORIZON_BLOCKED",
            $"Target violates the configured horizon at {FirstFailure?.TimestampUtc:O}; altitude {FirstFailure?.AltitudeDegrees:F2}°, required {FirstFailure?.RequiredAltitudeDegrees:F2}°.",
            new Dictionary<string, double>
            {
                ["minimumAltitudeDegrees"] = MinimumAltitudeDegrees,
                ["minimumClearanceDegrees"] = MinimumClearanceDegrees
            });
}

public static class HorizonCalculator
{
    public static HorizonEvaluation Evaluate(ObservationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var samples = new List<AltitudeSample>();
        var end = plan.PlannedStartUtc + plan.PlannedDuration;
        var interval = plan.Horizon.EffectiveSampleInterval;

        for (var instant = plan.PlannedStartUtc; instant < end; instant += interval)
        {
            samples.Add(CreateSample(plan, instant, samples.Count == 0));
        }

        if (samples.Count == 0 || samples[^1].TimestampUtc != end)
        {
            samples.Add(CreateSample(plan, end, samples.Count == 0));
        }

        var firstFailure = samples.FirstOrDefault(sample => !sample.Passed);
        var minimumAltitude = samples.Min(sample => sample.AltitudeDegrees);
        var minimumClearance = samples.Min(sample => sample.AltitudeDegrees - sample.RequiredAltitudeDegrees);
        return new HorizonEvaluation(firstFailure is null, minimumAltitude, minimumClearance, firstFailure, samples.AsReadOnly());
    }

    public static (double AltitudeDegrees, double AzimuthDegrees) GetHorizontalCoordinates(
        EquatorialTarget target,
        ObservatorySite site,
        DateTimeOffset timestampUtc)
    {
        var jd = timestampUtc.ToUniversalTime().ToUnixTimeMilliseconds() / 86_400_000d + 2_440_587.5;
        var centuries = (jd - 2_451_545d) / 36_525d;
        var gmst = NormalizeDegrees(
            280.46061837
            + 360.98564736629 * (jd - 2_451_545d)
            + 0.000387933 * centuries * centuries
            - centuries * centuries * centuries / 38_710_000d);
        var localSidereal = NormalizeDegrees(gmst + site.LongitudeDegreesEast);
        var hourAngle = NormalizeSignedDegrees(localSidereal - target.RightAscensionDegrees);

        var latitude = DegreesToRadians(site.LatitudeDegrees);
        var declination = DegreesToRadians(target.DeclinationDegrees);
        var hourAngleRadians = DegreesToRadians(hourAngle);
        var sinAltitude =
            Math.Sin(latitude) * Math.Sin(declination)
            + Math.Cos(latitude) * Math.Cos(declination) * Math.Cos(hourAngleRadians);
        var altitude = Math.Asin(Math.Clamp(sinAltitude, -1, 1));

        var y = -Math.Sin(hourAngleRadians) * Math.Cos(declination);
        var x = Math.Sin(declination) * Math.Cos(latitude)
                - Math.Cos(declination) * Math.Sin(latitude) * Math.Cos(hourAngleRadians);
        var azimuth = NormalizeDegrees(RadiansToDegrees(Math.Atan2(y, x)));
        return (RadiansToDegrees(altitude), azimuth);
    }

    public static double InterpolateHorizonAltitude(HorizonPolicy policy, double azimuthDegrees)
    {
        var points = policy.AzimuthProfile?
            .OrderBy(point => NormalizeDegrees(point.AzimuthDegrees))
            .ToArray();
        if (points is null || points.Length == 0) return policy.BaseMinimumAltitudeDegrees;
        if (points.Length == 1) return points[0].AltitudeDegrees;

        var azimuth = NormalizeDegrees(azimuthDegrees);
        for (var i = 0; i < points.Length; i++)
        {
            var left = points[i];
            var right = points[(i + 1) % points.Length];
            var leftAz = NormalizeDegrees(left.AzimuthDegrees);
            var rightAz = NormalizeDegrees(right.AzimuthDegrees);
            if (i == points.Length - 1) rightAz += 360;
            var testAz = azimuth < leftAz ? azimuth + 360 : azimuth;
            if (testAz < leftAz || testAz > rightAz) continue;
            var fraction = (testAz - leftAz) / (rightAz - leftAz);
            return left.AltitudeDegrees + fraction * (right.AltitudeDegrees - left.AltitudeDegrees);
        }

        return policy.BaseMinimumAltitudeDegrees;
    }

    private static AltitudeSample CreateSample(ObservationPlan plan, DateTimeOffset instant, bool first)
    {
        var (altitude, azimuth) = GetHorizontalCoordinates(plan.Target, plan.Site, instant);
        var horizon = InterpolateHorizonAltitude(plan.Horizon, azimuth);
        var required = horizon + (first ? plan.Horizon.StartMarginDegrees : plan.Horizon.ContinueMarginDegrees);
        return new AltitudeSample(instant, altitude, azimuth, required, altitude >= required);
    }

    private static double NormalizeDegrees(double value) => ((value % 360) + 360) % 360;
    private static double NormalizeSignedDegrees(double value)
    {
        var normalized = NormalizeDegrees(value);
        return normalized > 180 ? normalized - 360 : normalized;
    }

    private static double DegreesToRadians(double value) => value * Math.PI / 180d;
    private static double RadiansToDegrees(double value) => value * 180d / Math.PI;
}
