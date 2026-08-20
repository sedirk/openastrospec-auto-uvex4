using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class ObservationRunOwnershipLeaseTests
{
    [Fact]
    public void SameMachineLockAllowsOnlyOneLiveOwnerAndAllowsTakeoverAfterRelease()
    {
        var root = Path.Combine(Path.GetTempPath(), "uvex-owner-lease-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "real-observation-owner.lock");
        try
        {
            var first = RealObservationRunOwnershipLease.TryAcquire(path);
            Assert.True(first.Acquired, first.Failure);

            var competingHost = RealObservationRunOwnershipLease.TryAcquire(path);
            Assert.False(competingHost.Acquired);
            Assert.Null(competingHost.Lease);
            Assert.NotNull(competingHost.Failure);

            first.Lease!.Dispose();
            var explicitLaterRun = RealObservationRunOwnershipLease.TryAcquire(path);
            Assert.True(explicitLaterRun.Acquired, explicitLaterRun.Failure);
            explicitLaterRun.Lease!.Dispose();
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
