namespace UvexAdv.Nina.Plugin;

internal enum G3CompletedSolveResolutionResult
{
    Accepted,
    NoSolution,
    Implausible,
    ParityMismatch,
}

/// <summary>
/// Reuses only the already captured coarse-handoff frame. It neither owns a
/// capture/motion port nor creates a new deadline. The production delegates
/// retain their original return-time guard and live boundary validation.
/// </summary>
internal static class G3ExistingFrameResolutionRecovery
{
    internal static async Task<T> SolveAsync<T>(
        bool outsideFineHandoff, int actualSoftwareFactor, int? firstOverride,
        Func<int?, CancellationToken, Task<T>> solve,
        Func<T, G3CompletedSolveResolutionResult> classifyCompleted,
        Func<T, CancellationToken, Task> validateRecoveryBoundary,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var first = await solve(firstOverride, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!outsideFineHandoff || actualSoftwareFactor != 2 || firstOverride == 1)
            return first;
        var result = classifyCompleted(first);
        if (result is not (G3CompletedSolveResolutionResult.NoSolution or G3CompletedSolveResolutionResult.Implausible))
            return first;

        // No catch: failed processes, cancellation, changed binding/owner,
        // unsafe weather and exhausted return time cannot become optical retry.
        await validateRecoveryBoundary(first, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await solve(1, cancellationToken).ConfigureAwait(false);
    }
}
