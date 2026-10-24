namespace UvexAdv.Nina.Plugin;

/// <summary>The shared production fallback order. Ports carry the original return clock;
/// this dispatcher owns neither equipment nor a retry/motion budget.</summary>
internal static class G3SparseRecoveryWorkflow
{
    internal const string Version = "local-dual-overlap-v2";

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
