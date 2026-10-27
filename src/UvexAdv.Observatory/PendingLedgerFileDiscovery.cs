using System.Collections.Concurrent;

namespace UvexAdv.Observatory;

/// <summary>Complete, uncached inventory; no read error is treated as an empty subtree.</summary>
public static class PendingLedgerFileDiscovery
{
    public static async Task<IReadOnlyList<string>> FindAsync(string root, string fileName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var paths = new ConcurrentBag<string>();
        foreach (var path in Directory.EnumerateFiles(root, fileName, SearchOption.TopDirectoryOnly))
            paths.Add(path);
        // A small fixed concurrency bound reduces metadata latency, not the
        // search surface. Await ALL subtrees before granting any result; a
        // cancellation/unreadable subtree must fail the entire inventory.
        await Parallel.ForEachAsync(Directory.EnumerateDirectories(root),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
            (directory, token) =>
            {
                foreach (var path in Directory.EnumerateFiles(directory, fileName, SearchOption.AllDirectories))
                {
                    token.ThrowIfCancellationRequested();
                    paths.Add(path);
                }
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
