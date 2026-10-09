using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal static class Phd2RecoveryCalibrationPresentation
{
    internal const string Code = "PHD2_LOCK_RECOVERY_PRE_GUIDE_CALIBRATION_INVALID";

    internal static GateResult Blocked(Phd2CalibrationValidation calibration, string sourceRunId,
        Phd2LockShiftPendingState pending)
    {
        var metrics = new Dictionary<string, double>
        {
            ["attemptsUsed"] = pending.AttemptsUsed,
            ["cumulativeCommandedPixels"] = pending.CumulativeCommandedPixels,
            ["guideOrRecalibrationCommandSent"] = 0,
        };
        Add("calibrationAgeDays", calibration.CalibrationAge?.TotalDays);
        Add("orthogonalityErrorDegrees", calibration.OrthogonalityErrorDegrees);
        Add("calibrationRaGuideRateArcsecondsPerSecond", calibration.NativeDetails?.RaGuideRateArcsecondsPerSecond);
        Add("calibrationDecGuideRateArcsecondsPerSecond", calibration.NativeDetails?.DecGuideRateArcsecondsPerSecond);
        Add("currentRaGuideRateArcsecondsPerSecond", calibration.CurrentRaGuideRateArcsecondsPerSecond);
        Add("currentDecGuideRateArcsecondsPerSecond", calibration.CurrentDecGuideRateArcsecondsPerSecond);
        return GateResult.Unknown(Code,
            $"Unfinished lock recovery from run {sourceRunId}, lineage {pending.LineageId}, was blocked BEFORE any guide/recalibration command. " +
            "This is not a settle-timeout or a judgement of current manual guiding. " +
            "The old endpoints and consumed budget remain unchanged. Explicit operator takeover at an idle boundary may archive a cancelled intent as incomplete; restarting alone cannot. " +
            string.Join(" ", calibration.Failures.Concat(calibration.IndeterminateReasons)), metrics);

        void Add(string key, double? value)
        {
            if (value is { } number && double.IsFinite(number)) metrics[key] = number;
        }
    }
}
