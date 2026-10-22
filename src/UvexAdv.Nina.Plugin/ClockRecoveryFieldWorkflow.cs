using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal interface IClockRecoveryFieldPort
{
    ClockRecoveryPositionSnapshot ReadSnapshot();
    Task<GateResult> ValidateStoppedBoundaryAsync(ObservationContext context, CancellationToken token);
    Task PersistAttemptAsync(ClockRecoveryPositionCheck receipt, ObservationContext context, CancellationToken token);
    Task<ClockRecoveryFieldProbeResult> CaptureFreshAsync(ObservationContext context, CancellationToken token);
    Task<GateResult> ValidateFreshFieldAsync(ObservationContext context, CancellationToken token);
    Task RecordDecisionAsync(string decision, StageResult? result, ObservationContext context, CancellationToken token);
}

internal sealed record ClockRecoveryFieldProbeResult(StageResult Result, bool CanFallback);

// An optional prerequisite substitution inside the canonical stage loop, not a
// second observing runner. All downstream placement, guiding and ATR work stays
// with its existing owner, native marker and budget. Null means ordinary route.
internal sealed class ClockRecoveryFieldWorkflow(ClockRecoveryPositionCheck? receipt, IClockRecoveryFieldPort port)
{
    private bool attempted;
    private StageResult? accepted;

    internal async Task<StageResult?> TryExecuteAsync(ObservationStage stage, ObservationContext context, CancellationToken token)
    {
        if (receipt is null || stage is not (ObservationStage.SlewToCatalogTarget or
            ObservationStage.AcquireQhyWideField or ObservationStage.CoarseCenter or ObservationStage.AcquireG3SlitField)) return null;
        // A later focus/meridian segment must not inherit a consumed optional
        // receipt's stopped-only prerequisites. Its canonical gates own it.
        if (accepted is null && receipt.Attempted) return null;
        token.ThrowIfCancellationRequested();
        if (accepted is null && (stage != ObservationStage.SlewToCatalogTarget || attempted)) return null;
        var boundary = await port.ValidateStoppedBoundaryAsync(context, token).ConfigureAwait(false);
        if (boundary.Disposition != GateDisposition.Passed) return new(boundary);
        if (accepted is null)
        {
            attempted = true;
            if (!receipt.TryConsume(context.Plan.ObservationRunId, port.ReadSnapshot(), DateTimeOffset.UtcNow, out var reason))
            {
                await port.RecordDecisionAsync("ineligible: " + reason, null, context, token).ConfigureAwait(false);
                return null;
            }
            // Not best effort: a failed/ambiguous durable intent must not start
            // capture or silently enter movement via fallback.
            await port.PersistAttemptAsync(receipt, context, token).ConfigureAwait(false);
            var probe = await port.CaptureFreshAsync(context, token).ConfigureAwait(false);
            if (!probe.Result.CanAdvance)
            {
                await port.RecordDecisionAsync(probe.CanFallback ? "optical-fallback" : "hard-boundary", probe.Result, context, token).ConfigureAwait(false);
                if (!probe.CanFallback) return probe.Result;
                boundary = await port.ValidateStoppedBoundaryAsync(context, token).ConfigureAwait(false);
                return boundary.Disposition == GateDisposition.Passed ? null : new(boundary);
            }
            accepted = probe.Result;
        }
        var fresh = await port.ValidateFreshFieldAsync(context, token).ConfigureAwait(false);
        if (fresh.Disposition != GateDisposition.Passed) return new(fresh);
        var result = new StageResult(GateResult.Pass("CLOCK_CURRENT_FIELD_PREREQUISITE_REVALIDATED",
            $"{stage}: substituted by this segment's fresh G3 target/slit evidence; no catalogue GOTO, QHY acquisition or coarse motion was dispatched. Fresh placement/guiding and original budgets remain mandatory."), accepted.EvidencePath,
            accepted.Metadata);
        await port.RecordDecisionAsync("fresh-field-substitution", result, context, token).ConfigureAwait(false);
        return result;
    }
}
