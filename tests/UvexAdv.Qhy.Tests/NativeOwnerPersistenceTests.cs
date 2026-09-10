using System.Security.Cryptography;
using UvexAdv.Qhy.Core;

namespace UvexAdv.Qhy.Tests;

public sealed class NativeOwnerPersistenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "UVEX-ADV-QHY.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task NativeOwnerSavesExactlyOneImmutableRawFileAndStoreOnlyIndexesIt()
    {
        var adapter = new NativeFixture(directory);
        await using var coordinator = Create(adapter);
        var started = coordinator.StartAcquisition(Request("native-save"));
        var complete = await Wait(coordinator, started.Job.Id, j => j.State == QhyJobState.Completed);
        var record = Assert.Single(complete.Frames);
        Assert.Equal(1, adapter.SaveCount);
        Assert.Equal(adapter.NativePath, record.FitsPath);
        Assert.Equal("night-native", complete.NightSetupId);
        Assert.Equal("fixture-clock", record.TimingSource);
        Assert.Equal(0.1, record.TimingUncertaintySeconds);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(record.FitsPath))), record.Sha256);
        Assert.Single(Directory.GetFiles(directory, "*.fits", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(complete.ManifestPath)!, "raw")));
        Assert.True(File.Exists(complete.FrameIndexPath));
    }

    [Fact]
    public async Task ManualPauseDuringFilterPreparationPreventsNextExposure()
    {
        var adapter = new NativeFixture(directory) { BlockPreparation = true };
        await using var coordinator = Create(adapter);
        var started = coordinator.StartAcquisition(Request("pause-preparation"));
        await adapter.PreparationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.PauseAsync(started.Job.Id, new(started.OwnerToken), CancellationToken.None);
        adapter.ContinuePreparation.TrySetResult();
        await Wait(coordinator, started.Job.Id, j => j.State == QhyJobState.Paused);
        Assert.Equal(0, adapter.CaptureCount);
        await coordinator.CancelAsync(started.Job.Id, new(started.OwnerToken), CancellationToken.None);
        await Wait(coordinator, started.Job.Id, j => j.State == QhyJobState.Cancelled);
        Assert.Equal(0, adapter.SaveCount);
    }

    [Fact]
    public async Task PauseWhileQueuedNativeConnectionCannotBeOverwrittenByInitialization()
    {
        var adapter = new NativeFixture(directory) { BlockConnection = true };
        await using var coordinator = Create(adapter);
        var started = coordinator.StartAcquisition(Request("pause-queued"));
        await adapter.ConnectionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(QhyJobState.Queued, coordinator.GetJob(started.Job.Id)!.State);
        await coordinator.PauseAsync(started.Job.Id, new(started.OwnerToken), CancellationToken.None);
        adapter.ContinueConnection.TrySetResult();
        await Wait(coordinator, started.Job.Id, j => j.State == QhyJobState.Paused);
        Assert.Equal(0, adapter.CaptureCount);
        await coordinator.CancelAsync(started.Job.Id, new(started.OwnerToken), CancellationToken.None);
        await Wait(coordinator, started.Job.Id, j => j.State == QhyJobState.Cancelled);
    }

    [Fact]
    public async Task ExpiredLeaseAfterPreparationCannotOpenShutter()
    {
        var clock = new FixtureClock();
        var adapter = new NativeFixture(directory) { BlockPreparation = true };
        await using var coordinator = Create(adapter, clock);
        var started = coordinator.StartAcquisition(Request("expire-preparation") with { ControlLeaseSeconds = 15 });
        await adapter.PreparationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(16));
        adapter.ContinuePreparation.TrySetResult();
        await Wait(coordinator, started.Job.Id, j => j.State == QhyJobState.PausedNeedsAttention);
        Assert.Equal(0, adapter.CaptureCount);
        await coordinator.CancelAsync(started.Job.Id, new(started.OwnerToken), CancellationToken.None);
    }

    [Fact]
    public async Task CancellationWhileSavingStillPublishesTheCapturedRawFile()
    {
        var adapter = new NativeFixture(directory) { BlockSaving = true };
        await using var coordinator = Create(adapter);
        var started = coordinator.StartAcquisition(Request("stop-saving"));
        await adapter.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancelling = coordinator.CancelAsync(started.Job.Id, new(started.OwnerToken), CancellationToken.None);
        adapter.ContinueSaving.TrySetResult();
        await cancelling.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = await Wait(coordinator, started.Job.Id, j => j.State == QhyJobState.Cancelled);
        Assert.True(File.Exists(Assert.Single(stopped.Frames).FitsPath));
        Assert.True(File.Exists(stopped.FrameIndexPath));
    }

    [Fact]
    public async Task PriorityYieldWaitsForNativeSaveAndPersistsItsReasonBeforeNewAcquisition()
    {
        var adapter = new NativeFixture(directory) { BlockSaving = true };
        await using var coordinator = Create(adapter);
        var started = coordinator.StartPhotometry(new("fixture-run", "fixture-target", 0.01, 10, 0, 100, 1,
            PauseOnQualityFailure: false, ClientRequestId: "photometry", NightSetupId: "night-native"));
        await adapter.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pausing = await coordinator.PauseAsync(started.Job.Id, new(started.OwnerToken), CancellationToken.None);
        Assert.Equal(QhyJobState.Pausing, pausing.State);
        var priority = new QhyOwnerControlRequest(started.OwnerToken, YieldToAcquisitionRequestId: "witness");
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CancelAsync(started.Job.Id, priority, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => coordinator.StartAcquisition(Request("witness")));
        adapter.ContinueSaving.TrySetResult();
        await Wait(coordinator, started.Job.Id, j => j.State == QhyJobState.Paused);
        await coordinator.CancelAsync(started.Job.Id, priority, CancellationToken.None);
        var retired = await Wait(coordinator, started.Job.Id, j => j.State == QhyJobState.Cancelled);
        Assert.Equal("witness", retired.YieldedToAcquisitionRequestId);
        Assert.Single(retired.Frames);
        Assert.True(File.Exists(retired.Frames[0].FitsPath));
        Assert.Contains(retired.Events, e => e.Kind == "owner.yield-to-acquisition");
        adapter.BlockPreparation = true;
        var witness = coordinator.StartAcquisition(Request("witness"));
        await coordinator.CancelAsync(witness.Job.Id, new(witness.OwnerToken), CancellationToken.None);
        await Wait(coordinator, witness.Job.Id, j => j.State == QhyJobState.Cancelled);
        Assert.Equal(1, adapter.CaptureCount);
    }

    private QhyJobCoordinator Create(NativeFixture adapter, TimeProvider? clock = null) => new(adapter,
        new QhyCoordinatorOptions { DataRoot = directory, ExpectedStableId = "fixture-camera", ExpectedModel = "fixture-model", TimeProvider = clock ?? TimeProvider.System });
    private static AcquisitionJobRequest Request(string key) => new("fixture-run", "fixture-target", [0.01], 10, 0,
        ClientRequestId: key, NightSetupId: "night-native");
    private static async Task<QhyJobSnapshot> Wait(QhyJobCoordinator coordinator, Guid id, Func<QhyJobSnapshot, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var snapshot = coordinator.GetJob(id)!;
            Assert.NotEqual(QhyJobState.Faulted, snapshot.State);
            if (predicate(snapshot)) return snapshot;
            await Task.Delay(10, timeout.Token);
        }
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }

    private sealed class FixtureClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }
    private sealed class NativeFixture(string root) : IQhyCheckpointCameraAdapter, IQhyNativeFramePersistence
    {
        public string AdapterName => "fixture-native-owner";
        public QhyCameraStatus Status { get; private set; } = new(false, null, null, null, null, DateTimeOffset.UtcNow);
        public int CaptureCount, SaveCount;
        public bool BlockPreparation, BlockSaving, BlockConnection;
        public string NativePath => Path.Combine(root, "native", "immutable.fits");
        public TaskCompletionSource PreparationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinuePreparation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SaveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueSaving = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ConnectionEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueConnection = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<QhyCameraIdentity> ConnectExactAsync(string id, string model, CancellationToken token)
        {
            ConnectionEntered.TrySetResult();
            if (BlockConnection) await ContinueConnection.Task.WaitAsync(token);
            var identity = new QhyCameraIdentity(id, model, AdapterName);
            Status = new(true, identity, 0, 0, null, DateTimeOffset.UtcNow);
            return identity;
        }
        public Task DisconnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task<QhyFilterWheelStatus> ReadFilterWheelStatusAsync(CancellationToken token) => Task.FromResult(new QhyFilterWheelStatus(true, true, 0, "R", null, DateTimeOffset.UtcNow));
        public Task<QhyFilterWheelStatus> SelectFilterAsync(string name, CancellationToken token) => ReadFilterWheelStatusAsync(token);
        public Task<QhyFrame> CaptureSingleFrameAsync(QhyFrameSettings settings, CancellationToken token) => throw new InvalidOperationException("The unchecked route must not be called.");
        public async Task<QhyFrame> CaptureWithCheckpointAsync(QhyFrameSettings settings, Func<CancellationToken, Task> checkpoint, CancellationToken token)
        {
            PreparationEntered.TrySetResult();
            if (BlockPreparation) await ContinuePreparation.Task.WaitAsync(token);
            await checkpoint(token);
            CaptureCount++;
            var now = DateTimeOffset.UtcNow;
            return new(8, 8, Enumerable.Repeat((ushort)100, 64).ToArray(), now, now.AddSeconds(settings.ExposureSeconds), settings, Status.Identity!, "fixture-clock", 0.1);
        }
        public async Task<string> SaveNativeFrameAsync(QhyJobSnapshot job, QhyFrame frame, Guid frameId, int number, string role, CancellationToken token)
        {
            SaveEntered.TrySetResult();
            if (BlockSaving) await ContinueSaving.Task.WaitAsync(token);
            SaveCount++;
            await QhyFitsCodec.WriteAsync(NativePath, frame, job.Id, job.ObservationRunId, frameId, number,
                role, job.RequestedTarget, null, null, "ICRS", token);
            return NativePath;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
