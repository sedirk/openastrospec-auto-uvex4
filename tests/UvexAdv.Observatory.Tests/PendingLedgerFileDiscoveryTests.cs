using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class PendingLedgerFileDiscoveryTests
{
    [Theory]
    [InlineData("g3-acquisition-motion.json")]
    [InlineData("slit-placement-pending.json")]
    public async Task CanonicalChecksAreCompleteFreshAndStayWithinTheOriginalSearchSurface(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "uvex-canonical-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Empty(await PendingLedgerFileDiscovery.FindRunControlFilesAsync(root, name, default));
            foreach (var dir in new[] { "empty", "a/control", "b/control", "a/misplaced/control" })
                Directory.CreateDirectory(Path.Combine(root, dir));
            foreach (var dir in new[] { "", "a/control", "a/misplaced/control" })
                await File.WriteAllTextAsync(Path.Combine(root, dir, name), "fixture");
            var a = Path.Combine(root, "a", "control", name);
            var b = Path.Combine(root, "b", "control", name);
            Assert.Equal([a], await PendingLedgerFileDiscovery.FindRunControlFilesAsync(root, name, default));
            await File.WriteAllTextAsync(b, "new ledger since last call");
            Assert.Equal([a, b], await PendingLedgerFileDiscovery.FindRunControlFilesAsync(root, name, default));
            // Enumeration failure is not "no outstanding motion".
            await Assert.ThrowsAnyAsync<IOException>(() => PendingLedgerFileDiscovery.FindRunControlFilesAsync(a, name, default));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                PendingLedgerFileDiscovery.FindRunControlFilesAsync(root, name, new CancellationToken(true)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ParallelInventoryIncludesRootCanonicalMisplacedAndDuplicateCopies()
    {
        var root = Path.Combine(Path.GetTempPath(), "uvex-ledger-discovery-" + Guid.NewGuid().ToString("N"));
        const string name = "phd2-lock-shift-pending.json";
        try
        {
            string[] relative = [name, Path.Combine("run-a", "control", name),
                Path.Combine("run-a", "misplaced", "nested", name), Path.Combine("not-a-run", name)];
            foreach (var path in relative)
            {
                var full = Path.Combine(root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllTextAsync(full, "fixture");
            }
            var expected = Directory.GetFiles(root, name, SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase);
            Assert.Equal(expected, await PendingLedgerFileDiscovery.FindAsync(root, name, default));
            await File.WriteAllTextAsync(Path.Combine(root, "run-a", name), "later debt");
            Assert.Equal(5, (await PendingLedgerFileDiscovery.FindAsync(root, name, default)).Count);
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PendingLedgerFileDiscovery.FindAsync(root, name, cancel.Token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MissingRootIsNotReportedAsACompleteEmptyInventory()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => PendingLedgerFileDiscovery.FindAsync(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), "pending.json", default));
    }
}
