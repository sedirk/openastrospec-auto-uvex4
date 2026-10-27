using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class PendingLedgerFileDiscoveryTests
{
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
