using Microsoft.Extensions.Logging.Abstractions;
using UvexAdv.Phd2;
using UvexAdv.Phd2.Watchdog;

namespace UvexAdv.Phd2.Watchdog.Tests;

public sealed class Phd2WatchdogServiceTests
{
    [Fact]
    public async Task MissingLeaseWritesHealthyIdleStatusWithoutCallingPhd2()
    {
        using var directory = new TemporaryDirectory();
        var fakePhd2 = new FakePhd2SafetyStopper();
        var context = CreateContext(directory.Path, fakePhd2);

        var status = await context.Cycle.EvaluateOnceAsync(CancellationToken.None);

        Assert.Equal("LEASE_MISSING", status.Code);
        Assert.Equal(Phd2WatchdogHealth.Healthy, status.Health);
        Assert.False(status.StopAttemptedThisCycle);
        Assert.Equal(0, fakePhd2.CallCount);
        Assert.Equal(status, AtomicPhd2WatchdogStatusStore.Read(context.Paths.StatusPath));
    }

    [Fact]
    public async Task HostedWorkerPublishesIdleHealthWithoutOpeningFakePhd2()
    {
        using var directory = new TemporaryDirectory();
        var options = new Phd2WatchdogOptions { PollIntervalMilliseconds = 100 };
        var paths = Phd2WatchdogPaths.FromDataRoot(directory.Path, options);
        var fakePhd2 = new FakePhd2SafetyStopper();
        var leaseStore = new Phd2SafetyLeaseStore(paths.LeasePath);
        var watchdog = new Phd2SafetyWatchdog(
            leaseStore,
            new PinnedPhd2SafetyStopper(fakePhd2));
        var statusStore = new CapturingStatusStore();
        var cycle = new Phd2WatchdogCycle(
            watchdog,
            leaseStore,
            statusStore,
            paths,
            new LoadedPhd2WatchdogConfiguration(options, new string('B', 64)),
            TimeProvider.System,
            NullLogger<Phd2WatchdogCycle>.Instance);
        var worker = new Phd2WatchdogWorker(
            cycle,
            options,
            TimeProvider.System,
            NullLogger<Phd2WatchdogWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        var first = await statusStore.FirstStatus.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);
        worker.Dispose();

        Assert.Equal("LEASE_MISSING", first.Code);
        Assert.Equal(Phd2WatchdogHealth.Healthy, first.Health);
        Assert.Equal(0, fakePhd2.CallCount);
        Assert.Contains(statusStore.Statuses, status => status.Health == Phd2WatchdogHealth.Stopping);
    }

    [Fact]
    public async Task HealthyLeaseIsArmedButPerformsNoPhd2Action()
    {
        using var directory = new TemporaryDirectory();
        var fakePhd2 = new FakePhd2SafetyStopper();
        var context = CreateContext(directory.Path, fakePhd2);
        var now = DateTimeOffset.UtcNow;
        await AcquireLeaseAsync(
            context.LeaseStore,
            host: "127.0.0.1",
            issuedUtc: now,
            expiresUtc: now.AddSeconds(30));

        var status = await context.Cycle.EvaluateOnceAsync(CancellationToken.None);

        Assert.Equal("LEASE_HEALTHY", status.Code);
        Assert.Equal(Phd2WatchdogHealth.Healthy, status.Health);
        Assert.Equal(0, fakePhd2.CallCount);
    }

    [Fact]
    public async Task ExpiredLeaseUsesFakePhd2AndPublishesConfirmedStop()
    {
        using var directory = new TemporaryDirectory();
        var fakePhd2 = new FakePhd2SafetyStopper
        {
            Result = new Phd2StopCaptureResult(
                Phd2AppState.Guiding,
                Phd2AppState.Stopped,
                StopCommandSent: true,
                ConfirmedIdle: true,
                DateTimeOffset.UtcNow),
        };
        var context = CreateContext(directory.Path, fakePhd2);
        var now = DateTimeOffset.UtcNow;
        await AcquireLeaseAsync(
            context.LeaseStore,
            host: "127.0.0.1",
            issuedUtc: now.AddMinutes(-1),
            expiresUtc: now.AddSeconds(-1));

        var status = await context.Cycle.EvaluateOnceAsync(CancellationToken.None);

        Assert.Equal("EXPIRED_LEASE_STOP_CONFIRMED", status.Code);
        Assert.Equal(Phd2WatchdogHealth.Healthy, status.Health);
        Assert.True(status.StopAttemptedThisCycle);
        Assert.True(status.StopConfirmedThisCycle);
        Assert.Equal(1, fakePhd2.CallCount);
        Assert.Equal("127.0.0.1", fakePhd2.LastHost);
        Assert.Equal(4400, fakePhd2.LastPort);
    }

    [Fact]
    public async Task MalformedEndpointLeaseCannotReachEvenFakePhd2Transport()
    {
        using var directory = new TemporaryDirectory();
        var fakeTransport = new FakePhd2SafetyStopper();
        var context = CreateContext(directory.Path, fakeTransport);
        var now = DateTimeOffset.UtcNow;
        await AcquireLeaseAsync(
            context.LeaseStore,
            host: "localhost",
            issuedUtc: now.AddMinutes(-1),
            expiresUtc: now.AddSeconds(-1));

        var status = await context.Cycle.EvaluateOnceAsync(CancellationToken.None);

        Assert.Equal("LEASE_ENDPOINT_INVALID", status.Code);
        Assert.Equal(Phd2WatchdogHealth.Unhealthy, status.Health);
        Assert.Equal(0, fakeTransport.CallCount);
        Assert.Contains("not the pinned", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailedFakePhd2StopIsBackedOffBeforeRetry()
    {
        using var directory = new TemporaryDirectory();
        var fakePhd2 = new FakePhd2SafetyStopper
        {
            Exception = new IOException("synthetic PHD2 outage"),
        };
        var context = CreateContext(directory.Path, fakePhd2);
        var now = DateTimeOffset.UtcNow;
        await AcquireLeaseAsync(
            context.LeaseStore,
            host: "127.0.0.1",
            issuedUtc: now.AddMinutes(-1),
            expiresUtc: now.AddSeconds(-1));

        var failed = await context.Cycle.EvaluateOnceAsync(CancellationToken.None);
        var backedOff = await context.Cycle.EvaluateOnceAsync(CancellationToken.None);

        Assert.Equal("EXPIRED_LEASE_STOP_FAILED", failed.Code);
        Assert.Equal("STOP_RETRY_BACKOFF", backedOff.Code);
        Assert.Equal(1, fakePhd2.CallCount);
    }

    [Fact]
    public async Task CorruptLeaseIsUnhealthyButNeverCallsPhd2()
    {
        using var directory = new TemporaryDirectory();
        var fakePhd2 = new FakePhd2SafetyStopper();
        var context = CreateContext(directory.Path, fakePhd2);
        await File.WriteAllTextAsync(context.Paths.LeasePath, "{this is not json");

        var status = await context.Cycle.EvaluateOnceAsync(CancellationToken.None);

        Assert.Equal("WATCHDOG_EVALUATION_FAILED", status.Code);
        Assert.Equal(Phd2WatchdogHealth.Unhealthy, status.Health);
        Assert.Equal(0, fakePhd2.CallCount);
    }

    [Fact]
    public async Task PinnedStopperDelegatesOnlyExactLoopbackEndpoint()
    {
        var fakePhd2 = new FakePhd2SafetyStopper();
        var pinned = new PinnedPhd2SafetyStopper(fakePhd2);

        _ = await pinned.StopCaptureAndConfirmAsync("127.0.0.1", 4400, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pinned.StopCaptureAndConfirmAsync("localhost", 4400, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pinned.StopCaptureAndConfirmAsync("127.0.0.1", 4401, CancellationToken.None));

        Assert.Equal(1, fakePhd2.CallCount);
    }

    [Fact]
    public async Task BoundedStopperTurnsHungFakePhd2IntoTimeout()
    {
        var hung = new HungPhd2SafetyStopper();
        var bounded = new BoundedPhd2SafetyStopper(hung, TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            bounded.StopCaptureAndConfirmAsync("127.0.0.1", 4400, CancellationToken.None));
    }

    private static WatchdogTestContext CreateContext(
        string dataRoot,
        FakePhd2SafetyStopper fakePhd2)
    {
        var options = new Phd2WatchdogOptions();
        var paths = Phd2WatchdogPaths.FromDataRoot(dataRoot, options);
        var leaseStore = new Phd2SafetyLeaseStore(paths.LeasePath);
        var watchdog = new Phd2SafetyWatchdog(
            leaseStore,
            new PinnedPhd2SafetyStopper(fakePhd2));
        var statusStore = new AtomicPhd2WatchdogStatusStore(paths.StatusPath);
        var configuration = new LoadedPhd2WatchdogConfiguration(options, new string('A', 64));
        var cycle = new Phd2WatchdogCycle(
            watchdog,
            leaseStore,
            statusStore,
            paths,
            configuration,
            TimeProvider.System,
            NullLogger<Phd2WatchdogCycle>.Instance);
        return new(cycle, leaseStore, paths);
    }

    private static async Task AcquireLeaseAsync(
        Phd2SafetyLeaseStore store,
        string host,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc)
    {
        var lease = new Phd2SafetyLease(
            Phd2SafetyLease.CurrentSchemaVersion,
            Guid.NewGuid(),
            "watchdog-service-test",
            Environment.ProcessId,
            host,
            4400,
            issuedUtc,
            expiresUtc,
            Revision: 1,
            Phd2SafetyLeaseState.Active);
        Assert.True(await store.TryAcquireAsync(lease, issuedUtc, CancellationToken.None));
    }

    private sealed record WatchdogTestContext(
        Phd2WatchdogCycle Cycle,
        Phd2SafetyLeaseStore LeaseStore,
        Phd2WatchdogPaths Paths);

    private sealed class FakePhd2SafetyStopper : IPhd2SafetyStopper
    {
        public int CallCount { get; private set; }

        public string? LastHost { get; private set; }

        public int? LastPort { get; private set; }

        public Phd2StopCaptureResult Result { get; init; } = new(
            Phd2AppState.Stopped,
            Phd2AppState.Stopped,
            StopCommandSent: false,
            ConfirmedIdle: true,
            DateTimeOffset.UtcNow);

        public Exception? Exception { get; init; }

        public Task<Phd2StopCaptureResult> StopCaptureAndConfirmAsync(
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastHost = host;
            LastPort = port;
            if (Exception is not null)
            {
                return Task.FromException<Phd2StopCaptureResult>(Exception);
            }
            return Task.FromResult(Result);
        }
    }

    private sealed class HungPhd2SafetyStopper : IPhd2SafetyStopper
    {
        public async Task<Phd2StopCaptureResult> StopCaptureAndConfirmAsync(
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable fake-PHD2 continuation.");
        }
    }

    private sealed class CapturingStatusStore : IPhd2WatchdogStatusStore
    {
        private readonly object gate = new();

        public TaskCompletionSource<Phd2WatchdogStatus> FirstStatus { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<Phd2WatchdogStatus> Statuses { get; } = [];

        public Task WriteAsync(Phd2WatchdogStatus status, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                Statuses.Add(status);
            }
            FirstStatus.TrySetResult(status);
            return Task.CompletedTask;
        }
    }
}
