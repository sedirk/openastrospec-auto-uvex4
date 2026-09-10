namespace UvexAdv.Observatory;

public sealed record G3AcquisitionWorkTimePlan(GateResult Gate, TimeSpan? OperationTimeout);

/// <summary>Non-motion work may not spend the durable segmented-return reserve.</summary>
public static class G3AcquisitionReturnTimePolicy
{
    public const string ReserveCode = "G3_PLATE_SOLVE_RETURN_TIME_RESERVED";
    public const double CleanupReserveSeconds = 30;

    public static G3AcquisitionWorkTimePlan Plan(G3AcquisitionMotionState? state, DateTimeOffset now)
    {
        if (state is null || state.Phase == G3AcquisitionMotionPhase.SettledBudgetLedger)
            return new(GateResult.Pass("G3_WORK_NO_PENDING_RETURN", "No outstanding acquisition return is being deferred."), null);
        var issues = state.Validate();
        if (issues.Count > 0 || now < state.StartedUtc || state.Phase != G3AcquisitionMotionPhase.AwaitingFreshSolve)
            return new(GateResult.Unknown("G3_WORK_RETURN_STATE_INVALID",
                "Acquisition work requires a valid, arrived acquisition ledger and a non-reversed clock. " + string.Join(" ", issues)), null);

        // Use the same worst-case endpoint allowance as the segmented return.
        // Include one extra endpoint tolerance before rounding; never assume
        // that the most recent reported coordinate is an exact physical point.
        var progress = state.MaximumSingleCorrectionArcseconds - 2 * state.ArrivalToleranceArcseconds;
        var segments = Math.Max(1, Math.Ceiling((state.CurrentRadiusArcseconds + 2 * state.ArrivalToleranceArcseconds) / progress));
        var returnSeconds = segments * state.WorstCaseActionSeconds;
        var remainingSeconds = state.MaximumElapsedSeconds - (now - state.StartedUtc).TotalSeconds;
        var requiredSeconds = state.WorstCaseActionSeconds + returnSeconds + CleanupReserveSeconds;
        var values = new Dictionary<string, double>
        {
            ["remainingSeconds"] = remainingSeconds,
            ["operationTimeoutSeconds"] = state.WorstCaseActionSeconds,
            ["returnSegments"] = segments,
            ["returnReserveSeconds"] = returnSeconds,
            ["cleanupReserveSeconds"] = CleanupReserveSeconds,
        };
        if (!double.IsFinite(requiredSeconds) || remainingSeconds < requiredSeconds)
            return new(GateResult.Unknown(ReserveCode,
                "No further capture or plate solve was started: its bounded duration would spend the original segmented-return and cleanup reserve.", values), null);
        return new(GateResult.Pass("G3_WORK_RETURN_TIME_PROTECTED",
            "This non-motion operation fits before the unchanged durable return reserve.", values), TimeSpan.FromSeconds(state.WorstCaseActionSeconds));
    }

    public static async Task<T> ExecuteAsync<T>(G3AcquisitionWorkTimePlan plan,
        Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.Gate.Disposition != GateDisposition.Passed)
            throw new G3ReturnTimeReserveException(plan.Gate);
        if (plan.OperationTimeout is not { } timeout)
            return await operation(cancellationToken).ConfigureAwait(false);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        try
        {
            // Await the owner's cancellation/cleanup; never abandon a running
            // camera or solver task and start a return concurrently with it.
            var result = await operation(bounded.Token).ConfigureAwait(false);
            bounded.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && bounded.IsCancellationRequested)
        {
            throw new G3ReturnTimeReserveException(GateResult.Unknown(ReserveCode,
                "Capture/plate solve reached its bounded duration. Stop the ladder and preserve the original return reserve.", plan.Gate.Metrics), operationStarted: true);
        }
    }
}

public sealed class G3ReturnTimeReserveException(GateResult gate, bool operationStarted = false) : Exception($"{gate.Code}: {gate.Message}")
{
    public GateResult Gate { get; } = gate;
    public bool OperationStarted { get; } = operationStarted;
}
