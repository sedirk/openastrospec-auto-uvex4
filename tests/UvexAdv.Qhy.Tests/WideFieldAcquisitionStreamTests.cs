using System.Security.Cryptography;
using System.Text.Json;
using UvexAdv.Qhy.Core;

namespace UvexAdv.Qhy.Tests;

public sealed class WideFieldAcquisitionStreamTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "UVEX-ADV-QHY.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RealCoordinatorRetainsAllAcquisitionFramesBeyondFirstAcceptedAndJoinsSameOwner()
    {
        await using var coordinator = Create(new RecordedOrSyntheticAdapter());
        var control = coordinator.StartAcquisition(Request(4));
        var completed = await Joined(coordinator, control.Job.Id);
        Assert.Equal(QhyJobState.Completed, completed.State);
        Assert.Equal(4, completed.TotalFrameCount);
        Assert.Equal(4, completed.Frames.Count);
        Assert.All(completed.Frames, f => Assert.Equal("ACQUISITION-WIDE-STREAM", f.Role));
        Assert.All(completed.Frames, f => Assert.NotNull(f.Metrics.AstrometricStars));
        Assert.Equal(4, File.ReadLines(completed.FrameIndexPath!).Count());
        Assert.True(coordinator.IsIdle);
        // A role transition is a new job only after the old worker actually joined.
        var science = coordinator.StartPhotometry(new("science-after-stream", "field", .01, 10, 20, 1, 0,
            PauseOnQualityFailure: false));
        Assert.Equal(QhyJobState.Completed, (await Joined(coordinator, science.Job.Id)).State);
    }

    [Fact]
    public async Task QualityMissKeepsRawFramesAndDoesNotPauseContinuousProducer()
    {
        await using var coordinator = Create(new RecordedOrSyntheticAdapter());
        var control = coordinator.StartAcquisition(Request(3) with { QualityThresholds = new(MinimumDetectedStars: 100_000) });
        var completed = await Joined(coordinator, control.Job.Id);
        Assert.Equal(3, completed.TotalFrameCount);
        Assert.Equal(QhyJobState.Completed, completed.State);
        Assert.Null(completed.AcceptedFrameId);
        Assert.All(completed.Frames, f => Assert.Contains("LOW_STAR_COUNT", f.Metrics.QualityFlags));
    }

    [Fact]
    public async Task CancellationJoinsCaptureBeforeAnyRoleTransitionAndDoesNotRestart()
    {
        var adapter = new RecordedOrSyntheticAdapter(TimeSpan.FromMilliseconds(50));
        await using var coordinator = Create(adapter);
        var control = coordinator.StartAcquisition(Request(100));
        while (adapter.Captures < 1) await Task.Delay(5);
        await coordinator.CancelAsync(control.Job.Id, new(control.OwnerToken), CancellationToken.None);
        var stopped = await Joined(coordinator, control.Job.Id);
        var calls = adapter.Captures;
        await Task.Delay(70);
        Assert.Equal(calls, adapter.Captures);
        Assert.Equal(QhyJobState.Cancelled, stopped.State);
        Assert.True(stopped.ExecutionJoined);
    }

    [Fact]
    public async Task OriginalAbsoluteDeadlineStopsProducerWithoutAllocatingMoreFrames()
    {
        var adapter = new RecordedOrSyntheticAdapter(TimeSpan.FromMilliseconds(60));
        await using var coordinator = Create(adapter);
        var control = coordinator.StartAcquisition(Request(100) with {
            WideFieldStream = new(DateTimeOffset.UtcNow.AddMilliseconds(140), 100) });
        var done = await Joined(coordinator, control.Job.Id);
        Assert.Equal(QhyJobState.Completed, done.State);
        Assert.InRange(done.TotalFrameCount, 0, 3);
        Assert.True(done.ExecutionJoined);
    }

    [Theory]
    [InlineData(1)] [InlineData(4097)]
    public async Task InvalidSubordinateFrameCountDoesNotDispatch(int count)
    {
        var adapter = new RecordedOrSyntheticAdapter();
        await using var coordinator = Create(adapter);
        Assert.Throws<ArgumentOutOfRangeException>(() => coordinator.StartAcquisition(Request(count)));
        Assert.Equal(0, adapter.Captures);
    }

    [Fact]
    public async Task HistoricalNativePersistenceThroughRealCoordinatorAndProductionRegistration()
    {
        var root = Environment.GetEnvironmentVariable("UVEX_QHY_STREAM_REPLAY_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return;
        var jobs = new[] { ("UVEX-20261021T155003Z-90031e12848644f", "qhy-75828de777e841418319561e2d08c127"),
            ("UVEX-20261021T161417Z-b4d1814cc23441c", "qhy-6aeb145cdf0241329c6353593f5c4b77") };
        var summaries = new List<object>(); var references = new List<QhyFrameRecord>();
        foreach (var (run, job) in jobs)
        {
            var index = Path.Combine(root, "runs", run, job, "frames.jsonl");
            var source = File.ReadLines(index).Skip(4).Take(20).Select(line => JsonSerializer.Deserialize<QhyFrameRecord>(line,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!).ToArray();
            Assert.Equal(20, source.Length);
            var hashes = source.ToDictionary(f => f.FitsPath, f => Digest(f.FitsPath));
            Assert.All(source, f => Assert.Equal(f.Sha256, hashes[f.FitsPath], ignoreCase: true));
            await using var coordinator = Create(new RecordedOrSyntheticAdapter(source));
            var control = coordinator.StartAcquisition(Request(20) with { ObservationRunId = "offline-replay-" + run,
                ExposureLadderSeconds = [5], ControlLeaseSeconds = 120,
                WideFieldStream = new(DateTimeOffset.UtcNow.AddMinutes(2), 20) });
            var completed = await Joined(coordinator, control.Job.Id);
            Assert.Equal(20, completed.TotalFrameCount);
            var first = completed.Frames[0]; references.Add(first);
            var measured = completed.Frames.Skip(1).Select((f, i) => QhyWideFieldRelativeRegistration.Register(completed.Frames[i], f)).ToArray();
            Assert.All(measured, result => Assert.True(result.Accepted, result.Code));
            Assert.All(source, f => Assert.Equal(hashes[f.FitsPath], Digest(f.FitsPath)));
            summaries.Add(new { run, sourceFrames = 20, validatedNonSelf = measured.Count(m => m.Accepted),
                maxHoldoutP95Pixels = measured.Max(m => m.HoldoutP95Pixels), minimumRecovery = measured.Min(m => m.HoldoutRecoveryFraction),
                maximumCornerUncertaintyPixels = measured.Max(m => m.CornerUncertaintyPixels),
                registrationMode = "adjacent independent raw images, not fabricated periodic formal WCS", rawSourcesUnchanged = true });
        }
        var wrong = QhyWideFieldRelativeRegistration.Register(references[0], references[1] with {
            ExposureStartedUtc = references[0].ExposureEndedUtc.AddSeconds(1),
            ExposureMidpointUtc = references[0].ExposureEndedUtc.AddSeconds(3), ExposureEndedUtc = references[0].ExposureEndedUtc.AddSeconds(6) });
        Assert.False(wrong.Accepted);
        var report = Environment.GetEnvironmentVariable("UVEX_QHY_STREAM_REPLAY_REPORT");
        if (!string.IsNullOrWhiteSpace(report)) File.WriteAllText(report, JsonSerializer.Serialize(new {
            productionEntry = "QhyJobCoordinator.StartAcquisition -> existing owner Capture/NativePersistence -> QhyFrameAnalyzer -> QhyWideFieldRelativeRegistration",
            summaries, wrongFieldControl = new { wrong.Accepted, wrong.Code }, noHardware = true,
            noAbsoluteG3AccuracyClaim = true }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private QhyJobCoordinator Create(IQhyCameraAdapter adapter) => new(adapter, new() {
        ExpectedStableId = "QHYminiCam8M-recorded-offline", ExpectedModel = "QHYminiCam8M", DataRoot = directory });
    private static AcquisitionJobRequest Request(int count) => new("offline-stream", "field", [.01], 10, 20,
        MaximumAttempts: 1, ClientRequestId: "stream-" + count, ControlLeaseSeconds: 15,
        WideFieldStream: new(DateTimeOffset.UtcNow.AddSeconds(30), count));
    private static async Task<QhyJobSnapshot> Joined(QhyJobCoordinator coordinator, Guid id)
    {
        for (var i = 0; i < 12000; i++)
        {
            var state = coordinator.GetJob(id)!;
            if (state.ExecutionJoined) return state;
            if (state.State is QhyJobState.Paused or QhyJobState.PausedNeedsAttention)
                throw new InvalidOperationException("Recorded owner paused: " + state.AttentionReason);
            await Task.Delay(10);
        }
        throw new TimeoutException("Recorded/simulated acquisition worker did not join.");
    }
    private static string Digest(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    private sealed class RecordedOrSyntheticAdapter : IQhyCameraAdapter, IQhyNativeFramePersistence
    {
        private readonly QhyFrameRecord[]? records;
        private readonly TimeSpan delay;
        private readonly QhyCameraIdentity identity = new("QHYminiCam8M-recorded-offline", "QHYminiCam8M", "recorded-offline");
        private int captures;
        private readonly Dictionary<DateTimeOffset, string> paths = new();
        public int Captures => Volatile.Read(ref captures);
        public string AdapterName => "recorded-offline";
        public QhyCameraStatus Status { get; private set; } = new(false, null, null, null, null, DateTimeOffset.UtcNow);
        public RecordedOrSyntheticAdapter(TimeSpan delay = default) { this.delay = delay; }
        public RecordedOrSyntheticAdapter(QhyFrameRecord[] records) { this.records = records; }
        public Task<QhyCameraIdentity> ConnectExactAsync(string stableId, string model, CancellationToken token)
        { Status = new(true, identity, -10, 10, null, DateTimeOffset.UtcNow); return Task.FromResult(identity); }
        public Task DisconnectAsync(CancellationToken token) { Status = Status with { Connected = false }; return Task.CompletedTask; }
        public Task<QhyFilterWheelStatus> ReadFilterWheelStatusAsync(CancellationToken token) =>
            Task.FromResult(new QhyFilterWheelStatus(true, true, 5, "R", null, DateTimeOffset.UtcNow));
        public Task<QhyFilterWheelStatus> SelectFilterAsync(string filter, CancellationToken token) => ReadFilterWheelStatusAsync(token);
        public async Task<QhyFrame> CaptureSingleFrameAsync(QhyFrameSettings settings, CancellationToken token)
        {
            var ordinal = Interlocked.Increment(ref captures) - 1;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            if (records is null) return QhyCodecAndAnalysisTests.CreateStarFrame() with { Settings = settings, Identity = identity };
            var r = records[ordinal]; var pixels = QhyFitsCodec.Read(r.FitsPath);
            paths[r.ExposureStartedUtc] = r.FitsPath;
            return new(pixels.Width, pixels.Height, pixels.Pixels, r.ExposureStartedUtc, r.ExposureEndedUtc, settings, identity,
                "immutable recorded service timestamp (offline replay)");
        }
        public async Task<string> SaveNativeFrameAsync(QhyJobSnapshot job, QhyFrame frame, Guid id, int sequence,
            string role, CancellationToken token)
        {
            if (records is not null) return paths[frame.ExposureStartedUtc]; // Never rewrite original FITS.
            var path = Path.Combine(Path.GetDirectoryName(job.ManifestPath)!, "synthetic-" + id + ".fits");
            await QhyFitsCodec.WriteAsync(path, frame, job.Id, job.ObservationRunId, id, sequence, role, job.RequestedTarget,
                job.TargetRightAscensionDegrees, job.TargetDeclinationDegrees, job.CoordinateEpoch, token);
            return path;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
