using Xunit;

namespace UvexAdv.Phd2.Tests;

public sealed class Phd2SafetyWatchdogTests
{
    [Fact]
    public async Task HealthyLeaseDoesNotStopCapture()
    {
        using var directory = new TemporaryDirectory();
        var store = new Phd2SafetyLeaseStore(Path.Combine(directory.Path, "phd2-safety.json"));
        var now = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);
        var lease = CreateLease(now, now.AddSeconds(30));
        Assert.True(await store.TryAcquireAsync(lease, now, CancellationToken.None));
        var stopper = new RecordingStopper();
        var watchdog = new Phd2SafetyWatchdog(store, stopper);

        var result = await watchdog.EvaluateOnceAsync(now.AddSeconds(10), CancellationToken.None);

        Assert.Equal("LEASE_HEALTHY", result.Code);
        Assert.False(result.StopAttempted);
        Assert.Equal(0, stopper.CallCount);
        Assert.Equal(Phd2SafetyLeaseState.Active, (await store.ReadAsync(CancellationToken.None))!.State);
    }

    [Fact]
    public async Task ExpiredLeaseIsClaimedAndOnlyConfirmedIdleCompletesIt()
    {
        using var directory = new TemporaryDirectory();
        var store = new Phd2SafetyLeaseStore(Path.Combine(directory.Path, "phd2-safety.json"));
        var issued = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);
        var lease = CreateLease(issued, issued.AddSeconds(15));
        Assert.True(await store.TryAcquireAsync(lease, issued, CancellationToken.None));
        var stopper = new RecordingStopper();
        var watchdog = new Phd2SafetyWatchdog(store, stopper);

        var result = await watchdog.EvaluateOnceAsync(issued.AddSeconds(16), CancellationToken.None);

        Assert.Equal("EXPIRED_LEASE_STOP_CONFIRMED", result.Code);
        Assert.True(result.StopAttempted);
        Assert.True(result.StopConfirmed);
        Assert.Equal(1, stopper.CallCount);
        var stored = await store.ReadAsync(CancellationToken.None);
        Assert.Equal(Phd2SafetyLeaseState.StopConfirmed, stored!.State);
        Assert.True(stored.Revision >= 3);
    }

    [Fact]
    public async Task RenewalCasMakesOldExpiryHarmless()
    {
        using var directory = new TemporaryDirectory();
        var store = new Phd2SafetyLeaseStore(Path.Combine(directory.Path, "phd2-safety.json"));
        var issued = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);
        var original = CreateLease(issued, issued.AddSeconds(15));
        Assert.True(await store.TryAcquireAsync(original, issued, CancellationToken.None));
        var renewed = original with
        {
            Revision = original.Revision + 1,
            IssuedUtc = issued.AddSeconds(10),
            ExpiresUtc = issued.AddSeconds(40),
        };
        Assert.True(await store.TryReplaceAsync(
            original.LeaseId,
            original.Revision,
            renewed,
            CancellationToken.None));
        var stopper = new RecordingStopper();
        var watchdog = new Phd2SafetyWatchdog(store, stopper);

        var result = await watchdog.EvaluateOnceAsync(issued.AddSeconds(16), CancellationToken.None);

        Assert.Equal("LEASE_HEALTHY", result.Code);
        Assert.Equal(0, stopper.CallCount);
    }

    [Fact]
    public async Task FailedStopRemainsNonTerminalAndIsRetriedNextPoll()
    {
        using var directory = new TemporaryDirectory();
        var store = new Phd2SafetyLeaseStore(Path.Combine(directory.Path, "phd2-safety.json"));
        var issued = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);
        var lease = CreateLease(issued, issued.AddSeconds(15));
        Assert.True(await store.TryAcquireAsync(lease, issued, CancellationToken.None));
        var stopper = new RecordingStopper { Failure = new IOException("PHD2 unavailable") };
        var watchdog = new Phd2SafetyWatchdog(store, stopper);

        var first = await watchdog.EvaluateOnceAsync(issued.AddSeconds(16), CancellationToken.None);
        Assert.Equal("EXPIRED_LEASE_STOP_FAILED", first.Code);
        Assert.Equal(Phd2SafetyLeaseState.StopFailed, (await store.ReadAsync(CancellationToken.None))!.State);

        stopper.Failure = null;
        var second = await watchdog.EvaluateOnceAsync(issued.AddSeconds(17), CancellationToken.None);
        Assert.Equal("EXPIRED_LEASE_STOP_CONFIRMED", second.Code);
        Assert.Equal(2, stopper.CallCount);
        Assert.Equal(Phd2SafetyLeaseState.StopConfirmed, (await store.ReadAsync(CancellationToken.None))!.State);
    }

    [Fact]
    public async Task UnreconciledExpiredLeaseCannotBeOverwrittenByRestartedOwner()
    {
        using var directory = new TemporaryDirectory();
        var store = new Phd2SafetyLeaseStore(Path.Combine(directory.Path, "phd2-safety.json"));
        var issued = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);
        var crashed = CreateLease(issued, issued.AddSeconds(15));
        Assert.True(await store.TryAcquireAsync(crashed, issued, CancellationToken.None));
        var restarted = CreateLease(issued.AddSeconds(20), issued.AddSeconds(40));

        var acquired = await store.TryAcquireAsync(
            restarted,
            issued.AddSeconds(20),
            CancellationToken.None);

        Assert.False(acquired);
        Assert.Equal(crashed.LeaseId, (await store.ReadAsync(CancellationToken.None))!.LeaseId);
    }

    [Fact]
    public async Task HeartbeatRenewsRevisionAndCleanDisposeReleasesLease()
    {
        using var directory = new TemporaryDirectory();
        var store = new Phd2SafetyLeaseStore(Path.Combine(directory.Path, "phd2-safety.json"));
        var heartbeat = new Phd2SafetyLeaseHeartbeat(
            store,
            "127.0.0.1",
            4400,
            leaseDuration: TimeSpan.FromSeconds(2),
            heartbeatInterval: TimeSpan.FromMilliseconds(50),
            ownerInstanceId: "heartbeat-test");
        await heartbeat.StartAsync(CancellationToken.None);
        var initial = heartbeat.Current!;

        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        Phd2SafetyLease? renewed = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            renewed = await store.ReadAsync(CancellationToken.None);
            if (renewed!.Revision > initial.Revision) break;
            await Task.Delay(20);
        }

        Assert.NotNull(renewed);
        Assert.True(renewed!.Revision > initial.Revision);
        Assert.Equal(Phd2SafetyLeaseState.Active, renewed.State);
        Assert.Null(heartbeat.Failure);

        await heartbeat.DisposeAsync();
        Assert.Equal(
            Phd2SafetyLeaseState.Released,
            (await store.ReadAsync(CancellationToken.None))!.State);
    }

    private static Phd2SafetyLease CreateLease(DateTimeOffset issued, DateTimeOffset expires) => new(
        Phd2SafetyLease.CurrentSchemaVersion,
        Guid.NewGuid(),
        "test-owner",
        OwnerProcessId: 1234,
        Phd2Host: "127.0.0.1",
        Phd2Port: 4400,
        IssuedUtc: issued,
        ExpiresUtc: expires,
        Revision: 1,
        Phd2SafetyLeaseState.Active);

    private sealed class RecordingStopper : IPhd2SafetyStopper
    {
        public int CallCount { get; private set; }

        public Exception? Failure { get; set; }

        public Task<Phd2StopCaptureResult> StopCaptureAndConfirmAsync(
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (Failure is not null) throw Failure;
            return Task.FromResult(new Phd2StopCaptureResult(
                Phd2AppState.Guiding,
                Phd2AppState.Stopped,
                StopCommandSent: true,
                ConfirmedIdle: true,
                DateTimeOffset.UtcNow));
        }
    }
}
