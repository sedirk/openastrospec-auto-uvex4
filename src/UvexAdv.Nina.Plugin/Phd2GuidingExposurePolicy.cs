using System.Globalization;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>Checks immutable guide-frame metadata against the locked run, without changing PHD2.</summary>
internal static class Phd2GuidingExposurePolicy
{
    internal const string MismatchCode = "PHD2_GUIDING_EXPOSURE_MISMATCH";

    internal static GateResult Evaluate(double exposureSeconds, int expectedMilliseconds, string guideMode)
    {
        var actual = Math.Round(exposureSeconds * 1000);
        var valid = double.IsFinite(actual) && actual > 0 && actual <= int.MaxValue;
        var tolerance = Math.Max(1, expectedMilliseconds * 0.02);
        var metrics = new Dictionary<string, double>
        {
            ["expectedGuideExposureMilliseconds"] = expectedMilliseconds,
            ["guideExposureToleranceMilliseconds"] = tolerance,
            ["guideExposureMetadataValid"] = valid ? 1 : 0,
        };
        if (valid) metrics["actualGuideExposureMilliseconds"] = actual;
        if (valid && expectedMilliseconds > 0 && Math.Abs(actual - expectedMilliseconds) <= tolerance)
            return GateResult.Pass("PHD2_GUIDING_EXPOSURE_MATCHED", "Fresh guiding exposure matches the locked run.", metrics);

        var measured = valid ? actual.ToString("0", CultureInfo.InvariantCulture) + " ms" : "invalid/missing";
        return GateResult.Fail(MismatchCode,
            $"Fresh PHD2 guiding FITS exposure is {measured}; the locked run requires {expectedMilliseconds} ms for {guideMode}. " +
            "No further spectroscopy exposure is authorized. Preserve the FITS and inspect PHD2 exposure selection/auto-exposure history. " +
            "Do not overwrite live owner settings, adopt a different exposure, reset motion budgets or infer cloud/cooling failure.", metrics);
    }
}
