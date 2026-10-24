namespace UvexAdv.Observatory;

/// <summary>Actual optical sky differences, not an encoder-derived zero-drift assumption.</summary>
public sealed record QhyG3SkyDriftWitness(double EastArcsecondsPerSecond, double NorthArcsecondsPerSecond,
    double DriftRateUncertaintyArcsecondsPerSecond, double BaselineSeconds,
    DateTimeOffset FirstMidpointUtc, DateTimeOffset SecondMidpointUtc, DateTimeOffset G3MidpointUtc,
    double G3MidpointQhyRightAscensionDegrees, double G3MidpointQhyDeclinationDegrees,
    double AdditionalUncertaintyArcseconds, DateTimeOffset ValidUntilUtc,
    string FirstFrameSha256, string SecondFrameSha256)
{
    public bool MountStableMeansPixelStable => false;
    public bool G3StarsRequired => false;
    public double FirstQhyExposureHalfSupportSeconds { get; init; }
    public double SecondQhyExposureHalfSupportSeconds { get; init; }
    public double G3ExposureHalfDurationSeconds { get; init; }
    public double NativeG3MidpointIntervalHalfWidthSeconds { get; init; }
    public double FirstQhyIntegrationUncertaintyArcseconds { get; init; }
    public double SecondQhyIntegrationUncertaintyArcseconds { get; init; }
}

public sealed record QhyG3SkyDriftResult(GateResult Gate, QhyG3SkyDriftWitness? Witness);

public static class QhyG3SkyDriftPolicy
{
    public static QhyG3SkyDriftResult Build(QhyG3SolvedFrame first, QhyG3SolvedFrame second,
        double firstUncertaintyArcseconds, double secondUncertaintyArcseconds,
        DateTimeOffset g3StartedUtc, DateTimeOffset g3EndedUtc, int actualG3ExposureMilliseconds,
        QhyG3FastPairPolicy policy, DateTimeOffset now, double availableUncertaintyArcseconds)
    {
        QhyG3SkyDriftResult Reject(string code, string message) => new(GateResult.Warn(code, message), null);
        if (!ValidOpticalFrame(first) || !ValidOpticalFrame(second) ||
            actualG3ExposureMilliseconds <= 0 || g3StartedUtc == default || g3EndedUtc < g3StartedUtc ||
            (g3EndedUtc - g3StartedUtc).TotalMilliseconds + 1 < actualG3ExposureMilliseconds ||
            !double.IsFinite(firstUncertaintyArcseconds) || firstUncertaintyArcseconds <= 0 ||
            !double.IsFinite(secondUncertaintyArcseconds) || secondUncertaintyArcseconds <= 0 ||
            !double.IsFinite(availableUncertaintyArcseconds) || availableUncertaintyArcseconds <= 0 ||
            !QhyG3LocalModelPolicy.SameGeometry(first, second) ||
            QhyG3LocalModelPolicy.SameHash(first.FrameSha256, second.FrameSha256))
            return Reject("QHY_G3_SKY_DRIFT_INPUT_INVALID", "Distinct formally solved optical frames and actual G3 shutter timing are required.");
        var baseline = (second.ExposureMidpointUtc - first.ExposureMidpointUtc).TotalSeconds;
        var corner = Math.Sqrt(Math.Pow(first.FrameWidthPixels, 2) + Math.Pow(first.FrameHeightPixels, 2)) / 2;
        var shapeDifference = corner * (Math.Abs(first.PixelScaleArcseconds - second.PixelScaleArcseconds) +
            2 * second.PixelScaleArcseconds * Math.Abs(Math.Sin(Math.IEEERemainder(first.PositionAngleDegrees - second.PositionAngleDegrees, 360) * Math.PI / 360)));
        if (!double.IsFinite(shapeDifference) || Math.Abs(first.PixelScaleArcseconds / second.PixelScaleArcseconds - 1) > .01 ||
            Math.Abs(Math.IEEERemainder(first.PositionAngleDegrees - second.PositionAngleDegrees, 360)) > 1)
            return Reject("QHY_G3_SKY_DRIFT_GEOMETRY_CHANGED", "QHY scale/rotation changed beyond a local polar-drift witness.");
        if (baseline <= 0 || baseline > policy.MaximumPairWallClock.TotalSeconds ||
            first.ExposureEndedUtc > second.ExposureStartedUtc || first.SolveCompletedUtc > now || second.SolveCompletedUtc > now ||
            !QhyG3LocalModelPolicy.CurrentExposuresAligned(g3EndedUtc, actualG3ExposureMilliseconds, second, policy, now, out _) ||
            now - second.ExposureEndedUtc > TimeSpan.FromSeconds(10))
            return Reject("QHY_G3_SKY_DRIFT_TIME_INVALID", "Optical baseline, shutter midpoint or processing delay exceeds the original local witness window.");
        var earliestG3Midpoint = g3StartedUtc + TimeSpan.FromMilliseconds(actualG3ExposureMilliseconds / 2d);
        var latestG3Midpoint = g3EndedUtc - TimeSpan.FromMilliseconds(actualG3ExposureMilliseconds / 2d);
        var shutterIntervalHalfWidth = Math.Max(0, (latestG3Midpoint - earliestG3Midpoint).TotalSeconds / 2);
        var g3Midpoint = earliestG3Midpoint + TimeSpan.FromSeconds(shutterIntervalHalfWidth);
        if (new[] { first.ExposureMidpointUtc, second.ExposureMidpointUtc }.Any(qhyMidpoint =>
            Math.Max(Math.Abs((earliestG3Midpoint - qhyMidpoint).TotalSeconds), Math.Abs((latestG3Midpoint - qhyMidpoint).TotalSeconds)) >
            policy.MaximumPairMidpointSeparation.TotalSeconds))
            return Reject("QHY_G3_SKY_DRIFT_TIME_INVALID", "G3 midpoint is outside the small optical interpolation/extrapolation envelope.");
        var measured = G3AcquisitionMotionPlanner.SignedTangentOffsetArcseconds(first.CenterRightAscensionDegrees,
            first.CenterDeclinationDegrees, second.CenterRightAscensionDegrees, second.CenterDeclinationDegrees);
        var eastRate = measured.RaArcseconds / baseline;
        var northRate = measured.DecArcseconds / baseline;
        var speed = Math.Sqrt(eastRate * eastRate + northRate * northRate);
        // Small real field rotation (e.g. polar alignment error) is not zero:
        // include its full-field corner displacement in the differential rate bound.
        var initialRateUncertainty = (firstUncertaintyArcseconds + secondUncertaintyArcseconds + shapeDifference) / baseline;
        // A WCS centroid integrates a shutter interval; an exact midpoint does
        // not turn it into an instantaneous optical encoder. Retain the full
        // furthest support from the recorded midpoint, including asymmetric
        // native exposure brackets, and propagate both witness smear terms
        // into the differential-rate error. These are empirical conservative
        // allowances, not a claim of a measured native solver covariance.
        var firstHalfSupport = Math.Max((first.ExposureMidpointUtc - first.ExposureStartedUtc).TotalSeconds,
            (first.ExposureEndedUtc - first.ExposureMidpointUtc).TotalSeconds);
        var secondHalfSupport = Math.Max((second.ExposureMidpointUtc - second.ExposureStartedUtc).TotalSeconds,
            (second.ExposureEndedUtc - second.ExposureMidpointUtc).TotalSeconds);
        var integratedSpeedAllowance = speed + initialRateUncertainty;
        var firstIntegrationUncertainty = integratedSpeedAllowance * firstHalfSupport;
        var secondIntegrationUncertainty = integratedSpeedAllowance * secondHalfSupport;
        var rateUncertainty = (firstUncertaintyArcseconds + secondUncertaintyArcseconds + shapeDifference +
            firstIntegrationUncertainty + secondIntegrationUncertainty) / baseline;
        var g3HalfExposure = actualG3ExposureMilliseconds / 2000d;
        var g3TemporalHalfSupport = shutterIntervalHalfWidth + g3HalfExposure;
        var toG3Seconds = (g3Midpoint - second.ExposureMidpointUtc).TotalSeconds;
        var nowFromSecond = Math.Abs((now - second.ExposureMidpointUtc).TotalSeconds);
        // Count the full actual drift signal since the G3 shutter as stale-image
        // uncertainty. Do not pretend the old image is a new measurement at now.
        var uncertainty = secondUncertaintyArcseconds + secondIntegrationUncertainty +
            rateUncertainty * (Math.Max(Math.Abs(toG3Seconds), nowFromSecond) + g3TemporalHalfSupport) +
            speed * (Math.Abs((now - g3Midpoint).TotalSeconds) + g3TemporalHalfSupport);
        if (!double.IsFinite(uncertainty) || uncertainty >= availableUncertaintyArcseconds)
            return Reject("QHY_G3_SKY_DRIFT_UNCERTAINTY_EXCEEDED", "Observed drift and differential-WCS uncertainty consume the original target-recognition envelope.");
        var growthPerSecond = rateUncertainty + speed;
        var lifetime = Math.Min(10, (availableUncertaintyArcseconds - uncertainty) / growthPerSecond);
        var expiry = now + TimeSpan.FromSeconds(lifetime);
        expiry = Earlier(expiry, second.ExposureEndedUtc + TimeSpan.FromSeconds(10));
        expiry = Earlier(expiry, second.ExposureMidpointUtc + policy.MaximumCachedQhyAge);
        expiry = Earlier(expiry, g3EndedUtc + policy.MaximumPairWallClock);
        if (expiry <= now) return Reject("QHY_G3_SKY_DRIFT_EXPIRED", "The optical drift witness has no remaining uncertainty/time allowance.");
        var center = G3AcquisitionMotionPlanner.ApplyTangentOffsetArcseconds(second.CenterRightAscensionDegrees,
            second.CenterDeclinationDegrees, eastRate * toG3Seconds, northRate * toG3Seconds);
        var witness = new QhyG3SkyDriftWitness(eastRate, northRate, rateUncertainty, baseline,
            first.ExposureMidpointUtc, second.ExposureMidpointUtc, g3Midpoint, center.RaDegrees, center.DecDegrees,
            uncertainty, expiry, first.FrameSha256, second.FrameSha256)
        {
            FirstQhyExposureHalfSupportSeconds = firstHalfSupport,
            SecondQhyExposureHalfSupportSeconds = secondHalfSupport,
            G3ExposureHalfDurationSeconds = g3HalfExposure,
            NativeG3MidpointIntervalHalfWidthSeconds = shutterIntervalHalfWidth,
            FirstQhyIntegrationUncertaintyArcseconds = firstIntegrationUncertainty,
            SecondQhyIntegrationUncertaintyArcseconds = secondIntegrationUncertainty,
        };
        return new(GateResult.Warn("QHY_G3_MEASURED_SKY_DRIFT_WITNESS",
            "Two actual wide-field WCS frames measure sky drift; differential uncertainty and shutter/processing latency remain charged."), witness);
    }

    private static DateTimeOffset Earlier(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private static bool ValidOpticalFrame(QhyG3SolvedFrame f) => QhyG3LocalModelPolicy.IsSha(f.FrameSha256) &&
        QhyG3LocalModelPolicy.IsSha(f.SolveEvidenceSha256) && f.ExposureStartedUtc != default &&
        f.ExposureStartedUtc <= f.ExposureMidpointUtc && f.ExposureMidpointUtc <= f.ExposureEndedUtc &&
        f.ExposureEndedUtc <= f.SolveCompletedUtc && double.IsFinite(f.CenterRightAscensionDegrees) &&
        f.CenterRightAscensionDegrees is >= 0 and < 360 && double.IsFinite(f.CenterDeclinationDegrees) &&
        f.CenterDeclinationDegrees is > -90 and < 90 && double.IsFinite(f.PixelScaleArcseconds) && f.PixelScaleArcseconds > 0 &&
        double.IsFinite(f.PositionAngleDegrees);
}
