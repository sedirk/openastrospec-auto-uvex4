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
        bool motionFree, bool guidingEverStarted, bool ownedRecoveryPreparation = false) =>
        motionFree && (!guidingEverStarted || ownedRecoveryPreparation) &&
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

    // A native error 2 is explicit low-SNR evidence, unlike silence or an
    // ownership change. Before falling back to direct-target guiding, try one
    // supported longer tier in the existing preparation allowance. This is a
    // proposal only: checked stop, a NEW frame and native reselection follow.
    internal static int? NextExposureAfterCandidateLoss(Phd2GuideStep? lost,
        int exposureMilliseconds, IReadOnlyList<int> supported, int increases) =>
        lost?.ErrorCode == 2 && increases is >= 0 and < MaximumIncreases && exposureMilliseconds > 0
            ? supported.Where(x => x > exposureMilliseconds && x <= MaximumExposureMilliseconds &&
                x <= exposureMilliseconds * 2L).Distinct().OrderDescending().Select(x => (int?)x).FirstOrDefault()
            : null;

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

// One initial preparation plus the EXISTING checked-stop adaptive recovery
// admissions. Exposure increases are shared across them, not replenished by a
// new selection. This grants no guide, motion, calibration or science authority.
internal sealed class Phd2GuideSnrPreparationBudget
{
    private bool initialConsumed;
    private long? recoveryConnection;
    private string? lastRecoveryReceipt;
    private int recoveryAdmissions;
    internal int Increases { get; private set; }

    internal bool AdmitRecovery(Phd2DependencyRebuildStopProof? proof, Phd2StateSnapshot state)
    {
        if (proof is null || !proof.IsCurrent(state) || state.GuideOutput?.Failed == true || string.IsNullOrWhiteSpace(proof.EvidencePath) ||
            proof.EvidencePath == lastRecoveryReceipt || recoveryAdmissions >= 2) return false;
        recoveryAdmissions++;
        lastRecoveryReceipt = proof.EvidencePath;
        recoveryConnection = proof.ConnectionEpoch;
        return true;
    }

    internal bool TryBegin(Phd2StateSnapshot state, bool motionFree, bool offSlit,
        bool guidingEverStarted, out bool recovery)
    {
        recovery = false;
        var admittedConnection = recoveryConnection;
        recoveryConnection = null; // Single use, including rejection/cancellation.
        if (!motionFree || !offSlit || !state.IsConnected || state.AutomationPaused || state.Phd2Paused ||
            state.AppState is not (Phd2AppState.Looping or Phd2AppState.Selected) ||
            state.PendingSettleOperationId.HasValue || state.GuideOutput?.Failed == true) return false;
        if (admittedConnection == state.ConnectionEpoch) return recovery = true;
        if (initialConsumed || guidingEverStarted) return false;
        initialConsumed = true;
        return true;
    }

    internal bool TryChargeIncrease(int before, int after)
    {
        if (before <= 0 || after <= before || after > before * 2L ||
            after > Phd2GuideSnrExposurePolicy.MaximumExposureMilliseconds ||
            Increases >= Phd2GuideSnrExposurePolicy.MaximumIncreases) return false;
        Increases++;
        return true;
    }
}
