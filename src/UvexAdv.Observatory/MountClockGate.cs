namespace UvexAdv.Observatory;

/// <summary>
/// Evaluates the UTC clock exposed by a telescope driver before mount motion.
/// ASCOM defines UTCDate as UTC even when a driver returns a DateTime whose
/// Kind is Unspecified, so Unspecified values are deliberately interpreted as
/// UTC rather than as the workstation's local time.
/// </summary>
public static class MountClockGate
{
    public static GateResult Evaluate(
        DateTime? mountUtcDate,
        DateTimeOffset systemUtc,
        TimeSpan maximumAbsoluteOffset)
    {
        if (maximumAbsoluteOffset <= TimeSpan.Zero ||
            maximumAbsoluteOffset > TimeSpan.FromMinutes(5))
        {
            return GateResult.Unknown(
                "MOUNT_CLOCK_POLICY_INVALID",
                $"Mount-clock maximum offset must be greater than zero and no more than 300 seconds; configured {maximumAbsoluteOffset.TotalSeconds:R} seconds.");
        }

        if (mountUtcDate is null ||
            mountUtcDate == DateTime.MinValue ||
            mountUtcDate == DateTime.MaxValue)
        {
            return GateResult.Unknown(
                "MOUNT_CLOCK_UNAVAILABLE",
                "The connected telescope did not provide a usable UTCDate; mount motion is prohibited.");
        }

        DateTimeOffset reportedUtc;
        try
        {
            var normalized = mountUtcDate.Value.Kind switch
            {
                DateTimeKind.Utc => mountUtcDate.Value,
                DateTimeKind.Local => mountUtcDate.Value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(mountUtcDate.Value, DateTimeKind.Utc),
            };
            reportedUtc = new DateTimeOffset(normalized).ToUniversalTime();
        }
        catch (ArgumentException)
        {
            return GateResult.Unknown(
                "MOUNT_CLOCK_UNAVAILABLE",
                "The connected telescope returned an invalid UTCDate; mount motion is prohibited.");
        }

        systemUtc = systemUtc.ToUniversalTime();
        var signedOffset = (reportedUtc - systemUtc).TotalSeconds;
        if (!double.IsFinite(signedOffset))
        {
            return GateResult.Unknown(
                "MOUNT_CLOCK_UNAVAILABLE",
                "The connected telescope UTCDate could not be compared with the workstation UTC clock; mount motion is prohibited.");
        }

        var metrics = new Dictionary<string, double>
        {
            ["mountClockOffsetSeconds"] = signedOffset,
            ["mountClockAbsoluteOffsetSeconds"] = Math.Abs(signedOffset),
            ["mountClockMaximumOffsetSeconds"] = maximumAbsoluteOffset.TotalSeconds,
        };
        if (Math.Abs(signedOffset) > maximumAbsoluteOffset.TotalSeconds)
        {
            return GateResult.Fail(
                "MOUNT_CLOCK_OFFSET_EXCEEDED",
                $"Telescope UTCDate {reportedUtc:O} differs from workstation UTC {systemUtc:O} by {signedOffset:+0.000;-0.000;0.000} seconds; the hard limit is +/-{maximumAbsoluteOffset.TotalSeconds:0.###} seconds. No unpark or slew is permitted.",
                metrics);
        }

        return GateResult.Pass(
            "MOUNT_CLOCK_VALID",
            $"Telescope UTCDate is within {maximumAbsoluteOffset.TotalSeconds:0.###} seconds of workstation UTC (offset {signedOffset:+0.000;-0.000;0.000} seconds).",
            metrics);
    }
}
