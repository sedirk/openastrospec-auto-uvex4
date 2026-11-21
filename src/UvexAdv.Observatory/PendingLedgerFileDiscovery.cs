using System.Collections.Concurrent;

namespace UvexAdv.Observatory;

/// <summary>Complete, uncached inventory; no read error is treated as an empty subtree.</summary>
public static class PendingLedgerFileDiscovery
{
    // G3 and independent-slit stores have a different contract from PHD2:
    // exactly <each immediate run>/control/<name>, not arbitrary siblings.
    // Probe every canonical path on every call, but do not serialize 100k
    // negative metadata reads while an unguided field is drifting.
    internal static async Task<IReadOnlyList<string>> FindRunControlFilesAsync(string root, string name,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if ((int)new DirectoryInfo(root).Attributes == -1) return [];
        var paths = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(Directory.EnumerateDirectories(root),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token },
            (runDirectory, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var file = new FileInfo(Path.Combine(runDirectory, "control", name));
                // FileInfo.Attributes distinguishes a missing entry (-1)
                // from access/I/O failures; File.Exists silently hides both.
                if ((int)file.Attributes != -1) paths.Add(file.FullName);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return paths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

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
        // These are synchronous filesystem enumerators. With >130k run folders,
        // an async enumerator handshake per folder dominated the handoff. Partition
        // one list across bounded workers, outside the UI thread, then join them all.
        await Task.Run(() =>
        {
            var directories = Directory.EnumerateDirectories(root).Select(d =>
            { cancellationToken.ThrowIfCancellationRequested(); return d; }).ToArray();
            try
            {
                Parallel.ForEach(directories,
                    new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken }, directory =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var path in Directory.EnumerateFiles(directory, fileName, SearchOption.AllDirectories))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        paths.Add(path);
                    }
                });
            }
            catch (AggregateException ex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new IOException("A ledger subtree could not be completely enumerated; no complete inventory is available.", ex);
            }
        }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
