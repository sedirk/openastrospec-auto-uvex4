using UvexAdv.Core;

namespace UvexAdv.Core.Tests;

public sealed class ControlLeaseManagerTests
{
    [Fact]
    public void LeaseIsExclusiveAndTokenProtected()
    {
        var manager = new ControlLeaseManager();
        var lease = manager.Acquire("NINA", TimeSpan.FromSeconds(30));

        Assert.Throws<InvalidOperationException>(() => manager.Acquire("Admin", TimeSpan.FromSeconds(30)));
        Assert.Throws<UnauthorizedAccessException>(() => manager.Require("wrong-token"));
        manager.Require(lease.Token);
        manager.Release(lease.Token);

        Assert.Null(manager.Current);
    }

    [Fact]
    public void TtlIsClampedToSafetyLimits()
    {
        var before = DateTimeOffset.UtcNow;
        var manager = new ControlLeaseManager();
        var lease = manager.Acquire("NINA", TimeSpan.FromHours(1));

        Assert.InRange(lease.ExpiresUtc - before, TimeSpan.FromSeconds(119), TimeSpan.FromSeconds(121));
    }

    [Fact]
    public void ProductionMotionRequiresVerifiedCom5Identity()
    {
        var options = new UvexSafetyOptions { Simulator = false, HardwareIdentityVerified = false };

        Assert.Throws<InvalidOperationException>(options.ValidateForMotion);

        options.HardwareIdentityVerified = true;
        options.ValidateForMotion();
    }
}
