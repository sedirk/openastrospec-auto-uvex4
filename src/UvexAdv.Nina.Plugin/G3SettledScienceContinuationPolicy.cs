using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal static class G3SettledScienceContinuationPolicy
{
    // Called only AFTER durable-copy validation established SettledBudgetLedger.
    // This permits retaining accounting for non-acquisition stages, not spending
    // more motion authority. No clock, consumed counter or limit is changed.
    internal static bool CanRetainExpiredAccounting(ObservationStage stage, string ledgerRunId, string currentRunId) =>
        !string.IsNullOrWhiteSpace(currentRunId) &&
        string.Equals(ledgerRunId, currentRunId, StringComparison.Ordinal) &&
        stage is ObservationStage.SelectAtrExposure or ObservationStage.RunScienceBlock or ObservationStage.FinalizeObservation;
}
