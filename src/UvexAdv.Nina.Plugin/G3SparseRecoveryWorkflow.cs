using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>The shared production fallback order. Ports carry the original return clock;
/// this dispatcher owns neither equipment nor a retry/motion budget.</summary>
internal static class G3SparseRecoveryWorkflow
{
    internal const string Version = "formal-direct-inferred-overlap-v3";

    internal static bool CanApproachDirectly(PlateSolveEvidence? solve) =>
        solve is { IsFormal: true, Result.Success: true };

    // ADR-0045: only a formal solve of this image removes the optical-overlap
    // cap. This helper does not validate/execute motion; the caller retains the
    // original single/cumulative/action/return, source and safety gates.
    internal static G3OverlapMovePlan PlanCorrection(PlateSolveEvidence? solve,
        G3OverlapFootprint footprint, double east, double north, double maximumCommand)
    {
        var overlap = G3FieldOverlapPolicy.CapMove(footprint, east, north, maximumCommand);
        if (!CanApproachDirectly(solve)) return overlap;
        var scale = Math.Min(1, maximumCommand / Math.Sqrt(east * east + north * north));
        return overlap with { RaTangentArcseconds = east * scale,
            DeclinationArcseconds = north * scale, AppliedScale = scale };
    }

    internal static Task<T?> WithoutContinuousOwnerAsync<T>(bool continuousOwnerPresent,
        Func<CancellationToken, Task<T?>> capture, CancellationToken token) where T : class
    {
        token.ThrowIfCancellationRequested();
        return continuousOwnerPresent ? Task.FromResult<T?>(null) : capture(token);
    }

    internal static bool TryBeginMosaic(IReadOnlyList<string> sourceHashes, ISet<string> attempted)
    {
        if (sourceHashes.Count is < 2 or > 16 ||
            sourceHashes.Any(h => h.Length != 64 || !h.All(Uri.IsHexDigit)) ||
            sourceHashes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sourceHashes.Count)
            return false;
        var key = string.Join("|", sourceHashes.Select(h => h.ToUpperInvariant()).Order(StringComparer.Ordinal));
        return attempted.Add(key);
    }

    // The same finite ports are available before another native solve when
    // real retained pixels or an independently qualified model already exist.
    // No capture, retry, movement or new deadline is owned by this dispatcher.
    internal static async Task<T?> TryAvailableBeforeSolveAsync<T>(
        Func<CancellationToken, Task> validate, Func<CancellationToken, Task> retainFrame,
        Func<CancellationToken, Task<T?>> registeredAnchor,
        Func<CancellationToken, Task<T?>> localDualModel,
        Func<CancellationToken, Task<T?>> solveMosaic, CancellationToken token) where T : class
    {
        token.ThrowIfCancellationRequested();
        await validate(token).ConfigureAwait(false);
        await retainFrame(token).ConfigureAwait(false);
        await validate(token).ConfigureAwait(false);
        foreach (var fallback in new[] { registeredAnchor, solveMosaic, localDualModel })
        {
            token.ThrowIfCancellationRequested();
            await validate(token).ConfigureAwait(false);
            var result = await fallback(token).ConfigureAwait(false);
            await validate(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (result is not null) return result;
        }
        return null;
    }
    internal static async Task<T> ResolveAsync<T>(T direct, bool directAccepted, bool opticalMiss,
        Func<CancellationToken, Task> validate,
        Func<CancellationToken, Task> retainFrame,
        Func<CancellationToken, Task<T?>> registeredAnchor,
        Func<CancellationToken, Task<T?>> localDualModel,
        Func<CancellationToken, Task<T?>> solveMosaic,
        CancellationToken token) where T : class
    {
        token.ThrowIfCancellationRequested();
        if (!directAccepted && !opticalMiss) return direct;
        await validate(token).ConfigureAwait(false);
        await retainFrame(token).ConfigureAwait(false);
        await validate(token).ConfigureAwait(false);
        if (directAccepted) return direct;
        foreach (var fallback in new[] { registeredAnchor, solveMosaic, localDualModel })
        {
            token.ThrowIfCancellationRequested();
            await validate(token).ConfigureAwait(false);
            var result = await fallback(token).ConfigureAwait(false);
            // No catch: failed process/owner, cancellation and source corruption
            // cannot silently become permission to try another path.
            await validate(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (result is not null) return result;
        }
        return direct;
    }
}
