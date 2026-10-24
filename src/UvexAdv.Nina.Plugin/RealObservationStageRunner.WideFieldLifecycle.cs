using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private static G3SparseExposureTiming? ProbeExposureTiming(G3PlateSolveProbeState probe) =>
        probe.ExposureStartedUtc != default && probe.ExposureEndedUtc >= probe.ExposureStartedUtc &&
        probe.ExposureMillisecondsActual > 0
            ? new(probe.ExposureStartedUtc, probe.ExposureEndedUtc,
                probe.ExposureMillisecondsActual / 1000d, probe.Solve?.CompletedUtc ?? probe.ExposureEndedUtc)
            : probe.Solve?.ExposureTiming;

    private DateTimeOffset WideFieldAcquisitionDeadline(ObservationContext context)
    {
        // Starting a witness does not start a second target or motion clock.
        var original = NativeTarget?.TargetPlan ?? context.Plan;
        var deadline = original.PlannedStartUtc + original.PlannedDuration;
        if (configuration.SequencePlan?.TargetDeadlineUtc is { } targetEnd && targetEnd < deadline) deadline = targetEnd;
        if (configuration.SequencePlan?.DeadlineUtc is { } nightEnd && nightEnd < deadline) deadline = nightEnd;
        var debit = CaptureClockContinuationBudget();
        foreach (var clock in new[] { debit.G3, debit.Phd2 })
            if (clock is not null && clock.StartedUtc.AddSeconds(clock.MaximumSeconds) < deadline)
                deadline = clock.StartedUtc.AddSeconds(clock.MaximumSeconds);
        if (debit.FineStartedUtc is { } fine && fine.AddSeconds(debit.FineMaximumSeconds) < deadline)
            deadline = fine.AddSeconds(debit.FineMaximumSeconds);
        return deadline;
    }

    private async Task RequireWideFieldAcquisitionStoppedAsync(CancellationToken token)
    {
        var stopped = await StopWideFieldAcquisitionStreamAsync(token).ConfigureAwait(false);
        if (stopped.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(stopped);
    }

    private async Task RetireWideFieldForBoundaryAsync(List<string> failures, CancellationToken token)
    {
        try
        {
            var stopped = await StopWideFieldAcquisitionStreamAsync(token).ConfigureAwait(false);
            if (stopped.Disposition != GateDisposition.Passed) failures.Add($"{stopped.Code}: {stopped.Message}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { failures.Add($"QHY_STREAM_STOP_UNCONFIRMED: {ex.Message}"); }
        // Caller continues stopping the other owners; an optional channel must
        // never bypass their cleanup. Unjoined capture still prohibits reuse.
    }
}
