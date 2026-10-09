namespace UvexAdv.Phd2;

/// <summary>Same 5% change criterion used by native PHD2, independent of sky declination.</summary>
public static class Phd2GuideRateMatch
{
    public static void Validate(Phd2NativeCalibrationDetails? native, double? ra, double? dec,
        ICollection<string> failures, ICollection<string> unknown)
    {
        Check("RA", native?.RaGuideRateArcsecondsPerSecond, ra);
        Check("Dec", native?.DecGuideRateArcsecondsPerSecond, dec);
        void Check(string axis, double? calibrated, double? current)
        {
            if (current is not { } now || !double.IsFinite(now) || now <= 0)
            {
                unknown.Add($"PHD2_GUIDE_RATE_UNCONFIRMED: current {axis} mount guide speed is unavailable; calibration cannot be authorized.");
                return;
            }
            if (calibrated is not { } before || !double.IsFinite(before) || before <= 0)
            {
                // Missing OLD calibration metadata must not prohibit the first
                // bounded native calibration. It still cannot authorize guiding
                // with the old model, or pass post-calibration validation.
                unknown.Add($"PHD2_CALIBRATION_GUIDE_RATE_UNCONFIRMED: saved {axis} calibration guide speed is unavailable; a new calibration is required.");
                return;
            }
            if (Math.Abs(now / before - 1) > 0.05)
                failures.Add(FormattableString.Invariant($"PHD2_GUIDE_RATE_CHANGED: {axis} guide speed changed from {before:F4} to {now:F4} arcsec/sec; old calibration cannot authorize guiding."));
        }
    }
}
