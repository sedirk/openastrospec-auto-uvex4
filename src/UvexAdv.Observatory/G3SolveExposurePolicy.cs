namespace UvexAdv.Observatory;

/// <summary>Use an existing remaining ladder slot for a fresh short witness.
/// Never adds a frame, changes gain or turns an earlier image into current authority.</summary>
public static class G3SolveExposurePolicy
{
    public static int? ShortRecheckMilliseconds(int currentMilliseconds, int nextConfiguredMilliseconds,
        G3SolveProbeContentAssessment current, double saturationAdu,
        int? earlierUsefulMilliseconds, G3SolveProbeContentAssessment? earlierUseful)
    {
        if (currentMilliseconds <= 0 || nextConfiguredMilliseconds <= currentMilliseconds ||
            !double.IsFinite(saturationAdu) || saturationAdu <= 0 ||
            current.Gate.Code == G3SolveProbeContentAnalyzer.OverexposedGateCode)
            return null; // Explicit clipping retains its original stop policy.
        if ((!current.HasCoherentSource || current.StellarMeasurement.DetectedStarCount == 0 &&
                earlierUseful?.StellarMeasurement.DetectedStarCount > 0) && earlierUseful?.HasCoherentSource == true &&
            earlierUsefulMilliseconds is > 0 && earlierUsefulMilliseconds < currentMilliseconds &&
            current.BackgroundMedianAdu >= earlierUseful.BackgroundMedianAdu)
            return earlierUsefulMilliseconds;
        // Upper background, not the brightest star: a compact saturated target
        // is not a reason to truncate a useful dark-sky ladder. Counts include
        // bias, so this is deliberately conservative, not a radiometric model.
        var projected = current.UpperBackgroundAdu * nextConfiguredMilliseconds / currentMilliseconds;
        // Exposure headroom is a detector measurement, independent of source
        // detection. Requiring stars here drove bright sparse fields 5->10->15s
        // until the detector clipped. A fresh same-duration slot is not motion
        // authority; its own raw content and formal solve must still be checked.
        return current.UpperBackgroundAdu > 0 &&
            double.IsFinite(projected) && projected >= 0.8 * saturationAdu
            ? currentMilliseconds : null;
    }
}
