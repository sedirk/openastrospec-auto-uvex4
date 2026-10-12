using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

/// <summary>Soft SNR preference, not an admission gate or native Auto exposure.</summary>
internal static class Phd2GuideSnrExposurePolicy
{
    internal const double PreferredSnr = 30;
    internal const int MaximumExposureMilliseconds = 6000;
    internal const int MaximumIncreases = 2;

    // A new candidate lost while evaluating an OPTIONAL longer exposure can
    // restore the previous tier once, before guiding. Never catch owner,
    // configuration, calibration, pause or active-guide changes as low SNR.
    internal static bool CanRestoreAfterCandidateLoss(Phd2StateSnapshot baseline,
        Phd2StateSnapshot lost, Phd2StateSnapshot current, Phd2Point selected,
        bool hasPreviousTier, bool motionFree, bool guidingEverStarted) =>
        hasPreviousTier && CanRecoverCandidateLoss(baseline, lost, current, selected, motionFree, guidingEverStarted);

    internal static bool CanRecoverCandidateLoss(Phd2StateSnapshot baseline,
        Phd2StateSnapshot lost, Phd2StateSnapshot current, Phd2Point selected,
        bool motionFree, bool guidingEverStarted) =>
        motionFree && !guidingEverStarted &&
        baseline.AppState is Phd2AppState.Selected or Phd2AppState.Looping &&
        lost.AppState is Phd2AppState.LostLock or Phd2AppState.Looping && current.AppState == lost.AppState &&
        baseline.IsConnected && current.IsConnected && !current.AutomationPaused && !current.Phd2Paused &&
        current.ConnectionEpoch == baseline.ConnectionEpoch && lost.ConnectionEpoch == baseline.ConnectionEpoch &&
        current.GuideEpoch == lost.GuideEpoch && lost.GuideEpoch > baseline.GuideEpoch &&
        lost.GuideEpoch - baseline.GuideEpoch <= (lost.AppState == Phd2AppState.Looping ? 3 : 2) &&
        lost.EventSequence > baseline.EventSequence &&
        current.SelectedStar == selected && baseline.SelectedStar == selected &&
        current.CalibrationChangeSequence == baseline.CalibrationChangeSequence &&
        current.LastConfigurationChange?.EventSequence == baseline.LastConfigurationChange?.EventSequence &&
        !current.PendingSettleOperationId.HasValue && current.GuideOutput?.Failed != true;

    internal static double? MedianSnr(Phd2SelectedStarSnrObservation observation)
    {
        if (observation.TimedOut || observation.Samples.Count != 3 ||
            observation.Samples.Any(x => x.Snr is null || !double.IsFinite(x.Snr.Value) || x.Snr <= 0))
            return null;
        return observation.Samples.Select(x => x.Snr!.Value).Order().ElementAt(1);
    }

    internal static int? NextExposure(Phd2SelectedStarSnrObservation observation,
        IReadOnlyList<int> supported, int increases, bool motionFree, bool offSlit)
    {
        var snr = MedianSnr(observation);
        if (!motionFree || !offSlit || increases >= MaximumIncreases || snr is null || snr >= PreferredSnr ||
            observation.Samples.Any(x => x.ErrorCode is 1 or >= 3) ||
            observation.ExposureMilliseconds <= 0 || observation.ExposureMilliseconds >= MaximumExposureMilliseconds)
            return null;
        // Signal/noise roughly grows as sqrt(time). Native discrete choices,
        // bounded to 2x per adjustment and 6 s total; no auto-exposure mode.
        var desired = Math.Min(MaximumExposureMilliseconds,
            observation.ExposureMilliseconds * Math.Min(2, Math.Pow(PreferredSnr / snr.Value, 2)));
        var choices = supported.Where(x => x > observation.ExposureMilliseconds &&
            x <= MaximumExposureMilliseconds && x <= observation.ExposureMilliseconds * 2L).Distinct().Order().ToArray();
        if (choices.Length == 0) return null;
        return choices.FirstOrDefault(x => x >= desired, choices[^1]);
    }
}
