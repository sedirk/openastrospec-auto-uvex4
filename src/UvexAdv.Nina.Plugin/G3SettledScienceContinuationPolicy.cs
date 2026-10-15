using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal static class G3SettledScienceContinuationPolicy
{
    // Same completed-shutter receipt as the fine lock ledger, not elapsed
    // wall time inferred from FITS or a later recovery. No outstanding return,
    // intervening ledger change, pause/readout credit or budget reset.
    internal static G3AcquisitionMotionState? CreditExposure(G3AcquisitionMotionState before,
        G3AcquisitionMotionState after, DateTimeOffset openedUtc, DateTimeOffset completedUtc,
        double requestedSeconds, double monotonicElapsedSeconds)
    {
        if (before != after || before.Validate().Count != 0 ||
            before.Phase != G3AcquisitionMotionPhase.SettledBudgetLedger || openedUtc < before.UpdatedUtc)
            return null;
        var credit = Phd2ScienceContinuationPolicy.CompletedExposureCredit(openedUtc, completedUtc,
            requestedSeconds, monotonicElapsedSeconds);
        return credit <= 0 ? null : before with
        {
            ExcludedAtrExposureSeconds = before.ExcludedAtrExposureSeconds + credit,
            UpdatedUtc = completedUtc,
        };
    }

    // Called only AFTER durable-copy validation established SettledBudgetLedger.
    // This permits retaining accounting for non-acquisition stages, not spending
    // more motion authority. No clock, consumed counter or limit is changed.
    internal static bool CanRetainExpiredAccounting(ObservationStage stage, string ledgerRunId, string currentRunId) =>
        !string.IsNullOrWhiteSpace(currentRunId) &&
        string.Equals(ledgerRunId, currentRunId, StringComparison.Ordinal) &&
        stage is ObservationStage.SelectAtrExposure or ObservationStage.RunScienceBlock or ObservationStage.FinalizeObservation;
}
