using UvexAdv.Qhy.Core;

namespace UvexAdv.Qhy.Tests;

public sealed class QhyCoordinatorTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "UVEX-ADV-QHY.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AcquisitionCompletesAndPersistsFitsPreviewAndManifest()
    {
        await using var coordinator = CreateCoordinator(new ScriptedAdapter());
        var started = coordinator.StartAcquisition(new AcquisitionJobRequest(
            "run-acquire",
            "field",
            [0.01],
            10,
            256,
            QualityThresholds: PassingThresholds())).Job;

        var completed = await WaitForStateAsync(coordinator, started.Id, QhyJobState.Completed);

        var frame = Assert.Single(completed.Frames);
        Assert.Equal(frame.FrameId, completed.AcceptedFrameId);
        Assert.True(File.Exists(frame.FitsPath));
        Assert.True(File.Exists(frame.PreviewPath));
        Assert.True(File.Exists(completed.ManifestPath));
        Assert.True(File.Exists(completed.FrameIndexPath));
        Assert.Equal(1, completed.TotalFrameCount);
        Assert.Equal(1, completed.TotalAcceptedFrameCount);
        Assert.Equal(frame.FrameId, completed.LastEvaluatedFrameId);
        Assert.True(completed.LastFramePassedQualityGate);
        Assert.Equal("QHYminiCam8M-test-id", QhyFitsCodec.Read(frame.FitsPath).Header["CAMERAID"]);
        Assert.Contains(completed.Events, item => item.Kind == "job.completed");
    }

    [Fact]
    public async Task CumulativeAcceptedCountAndFrameIndexRemainCompleteBeyondRecentFrameWindow()
    {
        await using var coordinator = CreateCoordinator(new ScriptedAdapter());
        var started = coordinator.StartPhotometry(new PhotometryJobRequest(
            "run-long-photometry",
            "target",
            0.01,
            10,
            256,
            40,
            0,
            PauseOnQualityFailure: false,
            QualityThresholds: PassingThresholds())).Job;

        var completed = await WaitForStateAsync(coordinator, started.Id, QhyJobState.Completed);

        Assert.Equal(40, completed.TotalFrameCount);
        Assert.Equal(40, completed.TotalAcceptedFrameCount);
        Assert.Equal(32, completed.Frames.Count);
        Assert.Equal(9, completed.Frames[0].SequenceNumber);
        Assert.Equal(40, completed.Frames[^1].SequenceNumber);
        Assert.Equal(40, File.ReadLines(Assert.IsType<string>(completed.FrameIndexPath)).Count());

        var manifest = System.Text.Json.JsonSerializer.Deserialize<QhyJobSnapshot>(
            await File.ReadAllTextAsync(completed.ManifestPath),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.NotNull(manifest);
        Assert.Equal(40, manifest.TotalFrameCount);
        Assert.Equal(40, manifest.TotalAcceptedFrameCount);
        Assert.Equal(32, manifest.Frames.Count);
    }

    [Fact]
    public async Task PhotometryCyclesConfiguredFiltersWithIndependentExposureTimes()
    {
        await using var coordinator = CreateCoordinator(new ScriptedAdapter());
        var started = coordinator.StartPhotometry(new PhotometryJobRequest(
            "run-sho-cycle",
            "M76",
            5,
            10,
            256,
            4,
            0,
            PauseOnQualityFailure: false,
            QualityThresholds: PassingThresholds(),
            FilterSequence:
            [
                new QhyPhotometryFilterStep("H", 60),
                new QhyPhotometryFilterStep("O", 45),
                new QhyPhotometryFilterStep("S", 90),
            ])).Job;

        var completed = await WaitForStateAsync(coordinator, started.Id, QhyJobState.Completed);

        Assert.Equal(["H", "O", "S", "H"], completed.Frames.Select(frame => frame.Settings.FilterName));
        Assert.Equal([60d, 45d, 90d, 60d], completed.Frames.Select(frame => frame.Settings.ExposureSeconds));
        Assert.Equal(["PHOTOMETRY-H", "PHOTOMETRY-O", "PHOTOMETRY-S", "PHOTOMETRY-H"], completed.Frames.Select(frame => frame.Role));
    }

    [Fact]
    public async Task QualityRejectedFramesDoNotIncrementAcceptedCount()
    {
        await using var coordinator = CreateCoordinator(new ScriptedAdapter());
        var control = coordinator.StartAcquisition(new AcquisitionJobRequest(
            "run-rejected-acquisition",
            "field",
            [0.01],
            10,
            256,
            MaximumAttempts: 2,
            QualityThresholds: PassingThresholds() with { MinimumDetectedStars = 100_000 }));
        var started = control.Job;

        var paused = await WaitForStateAsync(coordinator, started.Id, QhyJobState.PausedNeedsAttention);
        Assert.Equal(2, paused.TotalFrameCount);
        Assert.Equal(0, paused.TotalAcceptedFrameCount);
        Assert.Equal(2, paused.Frames.Count);
        Assert.False(paused.LastFramePassedQualityGate);

        await coordinator.CancelAsync(started.Id, new QhyOwnerControlRequest(control.OwnerToken), CancellationToken.None);
        await WaitForStateAsync(coordinator, started.Id, QhyJobState.Cancelled);
    }

    [Fact]
    public async Task CaptureFailureAutoPausesAndResumeContinuesWithoutConfirmationGate()
    {
        var adapter = new ScriptedAdapter(failuresBeforeSuccess: 1);
        await using var coordinator = CreateCoordinator(adapter);
        var control = coordinator.StartPhotometry(new PhotometryJobRequest(
            "run-recover",
            "photometry-target",
            0.01,
            10,
            256,
            1,
            0,
            PauseOnQualityFailure: false,
            QualityThresholds: PassingThresholds()));
        var started = control.Job;

        var paused = await WaitForStateAsync(coordinator, started.Id, QhyJobState.PausedNeedsAttention);
        Assert.Contains("synthetic capture fault", paused.AttentionReason, StringComparison.OrdinalIgnoreCase);

        await coordinator.ResumeAsync(started.Id, new QhyResumeRequest(control.OwnerToken), CancellationToken.None);
        var completed = await WaitForStateAsync(coordinator, started.Id, QhyJobState.Completed);
        Assert.Single(completed.Frames);
        Assert.Equal(2, adapter.CaptureCalls);
    }

    [Fact]
    public async Task ConnectFailureAutoPausesAndResumeRevalidatesIdentityGate()
    {
        var adapter = new ScriptedAdapter(connectFailuresBeforeSuccess: 1);
        await using var coordinator = CreateCoordinator(adapter);
        var control = coordinator.StartAcquisition(new AcquisitionJobRequest(
            "run-connect-retry",
            "field",
            [0.01],
            10,
            256,
            QualityThresholds: PassingThresholds()));
        var started = control.Job;

        var paused = await WaitForStateAsync(coordinator, started.Id, QhyJobState.PausedNeedsAttention);
        Assert.Contains("identity/connect gate", paused.AttentionReason, StringComparison.OrdinalIgnoreCase);

        await coordinator.ResumeAsync(started.Id, new QhyResumeRequest(control.OwnerToken), CancellationToken.None);
        var completed = await WaitForStateAsync(coordinator, started.Id, QhyJobState.Completed);
        Assert.Single(completed.Frames);
    }

    [Fact]
    public async Task PauseIsCooperativeAtFrameBoundaryThenResumeCompletes()
    {
        var adapter = new ScriptedAdapter(captureDelay: TimeSpan.FromMilliseconds(100));
        await using var coordinator = CreateCoordinator(adapter);
        var control = coordinator.StartPhotometry(new PhotometryJobRequest(
            "run-pause",
            "target",
            0.01,
            10,
            256,
            3,
            0.2,
            PauseOnQualityFailure: false,
            QualityThresholds: PassingThresholds()));
        var started = control.Job;

        await WaitUntilAsync(() => coordinator.GetJob(started.Id)?.State == QhyJobState.Running);
        var pausing = await coordinator.PauseAsync(
            started.Id,
            new QhyOwnerControlRequest(control.OwnerToken),
            CancellationToken.None);
        Assert.Equal(QhyJobState.Pausing, pausing.State);
        var paused = await WaitForStateAsync(coordinator, started.Id, QhyJobState.Paused);
        Assert.InRange(paused.Frames.Count, 0, 1);

        await coordinator.ResumeAsync(started.Id, new QhyResumeRequest(control.OwnerToken), CancellationToken.None);
        var completed = await WaitForStateAsync(coordinator, started.Id, QhyJobState.Completed);
        Assert.Equal(3, completed.Frames.Count);
        Assert.Contains(completed.Events, item => item.Kind == "owner.pause");
        Assert.Contains(completed.Events, item => item.Kind == "owner.resume-and-renew");
    }

    [Fact]
    public async Task CancelReleasesBlockedAttentionJobAndPreservesRecordedFailure()
    {
        await using var coordinator = CreateCoordinator(new ScriptedAdapter(failuresBeforeSuccess: int.MaxValue));
        var control = coordinator.StartPhotometry(new PhotometryJobRequest(
            "run-cancel",
            "target",
            0.01,
            10,
            256,
            1,
            0,
            PauseOnQualityFailure: false,
            QualityThresholds: PassingThresholds()));
        var started = control.Job;
        await WaitForStateAsync(coordinator, started.Id, QhyJobState.PausedNeedsAttention);

        await coordinator.CancelAsync(started.Id, new QhyOwnerControlRequest(control.OwnerToken), CancellationToken.None);
        var cancelled = await WaitForStateAsync(coordinator, started.Id, QhyJobState.Cancelled);

        Assert.Contains(cancelled.Events, item => item.Kind == "job.needs-attention");
        Assert.Contains(cancelled.Events, item => item.Kind == "job.cancelled");
        Assert.Contains(cancelled.Events, item => item.Kind == "owner.cancel");
    }

    [Fact]
    public async Task ClientRequestIdMakesJobCreationIdempotentAndRejectsParameterDrift()
    {
        var adapter = new ScriptedAdapter(captureDelay: TimeSpan.FromMilliseconds(100));
        await using var coordinator = CreateCoordinator(adapter);
        var request = new PhotometryJobRequest(
            "run-idempotent",
            "target",
            0.01,
            10,
            256,
            1,
            0,
            PauseOnQualityFailure: false,
            QualityThresholds: PassingThresholds(),
            ClientRequestId: "photometry-v1");

        var first = coordinator.StartPhotometry(request);
        var retry = coordinator.StartPhotometry(request);

        Assert.Equal(first.Job.Id, retry.Job.Id);
        Assert.Equal(first.OwnerToken, retry.OwnerToken);
        Assert.Equal(first.Job.Id, coordinator.FindByClientRequest("run-idempotent", QhyJobKind.Photometry, "photometry-v1")?.Id);
        var changed = request with { ExposureSeconds = 0.02 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.FromResult(coordinator.StartPhotometry(changed)));
        await WaitForStateAsync(coordinator, first.Job.Id, QhyJobState.Completed);
        Assert.Equal(1, adapter.CaptureCalls);
    }

    [Fact]
    public async Task ControlLeaseRequiresExactIdentityAndCanBeRenewed()
    {
        var adapter = new ScriptedAdapter(captureDelay: TimeSpan.FromMilliseconds(100));
        await using var coordinator = CreateCoordinator(adapter);
        var control = coordinator.StartPhotometry(new PhotometryJobRequest(
            "run-lease",
            "target",
            0.01,
            10,
            256,
            2,
            0.2,
            PauseOnQualityFailure: false,
            QualityThresholds: PassingThresholds()));
        var started = control.Job;
        Assert.Null(started.ControlLeaseId);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            coordinator.RenewLeaseAsync(started.Id, new QhyLeaseRenewalRequest("wrong-owner-token"), CancellationToken.None));
        var renewed = await coordinator.RenewLeaseAsync(
            started.Id,
            new QhyLeaseRenewalRequest(control.OwnerToken, 180),
            CancellationToken.None);

        Assert.Equal(180, renewed.ControlLeaseSeconds);
        Assert.True(renewed.LeaseExpiresUtc > started.LeaseExpiresUtc);
        await coordinator.CancelAsync(started.Id, new QhyOwnerControlRequest(control.OwnerToken), CancellationToken.None);
        await WaitForStateAsync(coordinator, started.Id, QhyJobState.Cancelled);
    }

    [Fact]
    public async Task ExpiredLeasePausesAtCheckpointAndAnonymousResumeCannotWakeIt()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 17, 1, 0, 0, TimeSpan.Zero));
        var adapter = new ScriptedAdapter(blockFirstCapture: true);
        await using var coordinator = CreateCoordinator(adapter, clock);
        var control = coordinator.StartPhotometry(new PhotometryJobRequest(
            "run-expired-lease",
            "target",
            0.01,
            10,
            256,
            2,
            0,
            PauseOnQualityFailure: false,
            QualityThresholds: PassingThresholds(),
            ControlLeaseSeconds: 15));

        await adapter.FirstCaptureStarted.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(16));
        adapter.ReleaseFirstCapture();
        var paused = await WaitForStateAsync(coordinator, control.Job.Id, QhyJobState.PausedNeedsAttention);
        Assert.Equal(1, adapter.CaptureCalls);
        Assert.Contains(paused.Events, item => item.Kind == "lease.expired");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => coordinator.ResumeAsync(
            control.Job.Id,
            new QhyResumeRequest(string.Empty, 30, "anonymous"),
            CancellationToken.None));
        Assert.Equal(QhyJobState.PausedNeedsAttention, coordinator.GetJob(control.Job.Id)?.State);
        Assert.Equal(1, adapter.CaptureCalls);

        var resumed = await coordinator.ResumeAsync(
            control.Job.Id,
            new QhyResumeRequest(control.OwnerToken, 30, "test-owner"),
            CancellationToken.None);
        Assert.Equal(QhyJobState.Running, resumed.State);
        Assert.Equal(clock.GetUtcNow().AddSeconds(30), resumed.LeaseExpiresUtc);
        var completed = await WaitForStateAsync(coordinator, control.Job.Id, QhyJobState.Completed);
        Assert.Equal(2, completed.TotalFrameCount);
    }

    [Fact]
    public async Task CancellationWinsDeterministicRaceBeforeCompletedTransition()
    {
        await using var coordinator = CreateCoordinator(new ScriptedAdapter());
        var acceptedReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseAccepted = new ManualResetEventSlim(false);
        coordinator.JobChanged += snapshot =>
        {
            if (snapshot.Events.LastOrDefault()?.Kind != "acquisition.frame-accepted") return;
            acceptedReached.TrySetResult();
            Assert.True(releaseAccepted.Wait(TimeSpan.FromSeconds(5)), "Timed out releasing accepted-frame terminal race.");
        };
        var control = coordinator.StartAcquisition(new AcquisitionJobRequest(
            "run-cancel-complete-race",
            "field",
            [0.01],
            10,
            256,
            QualityThresholds: PassingThresholds()));

        await acceptedReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var cancelling = await coordinator.CancelAsync(
            control.Job.Id,
            new QhyOwnerControlRequest(control.OwnerToken, "race-test"),
            CancellationToken.None);
        Assert.Equal(QhyJobState.Cancelling, cancelling.State);
        releaseAccepted.Set();

        var cancelled = await WaitForStateAsync(coordinator, control.Job.Id, QhyJobState.Cancelled);
        Assert.DoesNotContain(cancelled.Events, item => item.Kind == "job.completed");
        Assert.Contains(cancelled.Events, item => item.Kind == "job.cancelled-after-completion-race");
    }

    [Fact]
    public async Task ConfirmedOperatorTakeoverIsDistinctFromOwnerControlAndAudited()
    {
        var adapter = new ScriptedAdapter(captureDelay: TimeSpan.FromSeconds(1));
        await using var coordinator = CreateCoordinator(adapter);
        var control = coordinator.StartPhotometry(new PhotometryJobRequest(
            "run-operator-takeover",
            "target",
            0.01,
            10,
            256,
            2,
            0,
            PauseOnQualityFailure: false,
            QualityThresholds: PassingThresholds()));
        await WaitForStateAsync(coordinator, control.Job.Id, QhyJobState.Running);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.TakeOverAsync(
            control.Job.Id,
            new OperatorTakeoverRequest(false, "operator", "test"),
            CancellationToken.None));
        var takenOver = await coordinator.TakeOverAsync(
            control.Job.Id,
            new OperatorTakeoverRequest(true, "operator", "manual hardware access"),
            CancellationToken.None);

        Assert.Equal(QhyJobState.TakenOver, takenOver.State);
        Assert.Contains(takenOver.Events, item => item.Kind == "operator.takeover-denied");
        Assert.Contains(takenOver.Events, item => item.Kind == "operator.takeover-requested");
        Assert.Contains(takenOver.Events, item => item.Kind == "operator.takeover-completed");
        Assert.False(adapter.Status.Connected);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private QhyJobCoordinator CreateCoordinator(IQhyCameraAdapter adapter, TimeProvider? timeProvider = null) => new(
        adapter,
        new QhyCoordinatorOptions
        {
            ExpectedStableId = "QHYminiCam8M-test-id",
            ExpectedModel = "QHYminiCam8M",
            DataRoot = directory,
            TimeProvider = timeProvider ?? TimeProvider.System,
        });

    private static QhyQualityThresholds PassingThresholds() => new(
        MinimumDetectedStars: 2,
        MaximumSaturatedFraction: 0.1,
        MinimumTransparency: 0,
        DetectionSigma: 4);

    private static async Task<QhyJobSnapshot> WaitForStateAsync(QhyJobCoordinator coordinator, Guid id, QhyJobState expected)
    {
        QhyJobSnapshot? latest = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            latest = coordinator.GetJob(id);
            if (latest?.State == expected) return latest;
            if (latest?.State is QhyJobState.Faulted && expected != QhyJobState.Faulted)
            {
                throw new Xunit.Sdk.XunitException($"Job faulted while waiting for {expected}: {latest.Error}");
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Job did not reach {expected}; latest={latest?.State}, error={latest?.Error}.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        throw new TimeoutException("Condition was not reached.");
    }

    private sealed class ScriptedAdapter(
        int failuresBeforeSuccess = 0,
        TimeSpan? captureDelay = null,
        int connectFailuresBeforeSuccess = 0,
        bool blockFirstCapture = false) : IQhyCameraAdapter
    {
        private readonly QhyCameraIdentity identity = new("QHYminiCam8M-test-id", "QHYminiCam8M", "test");
        private readonly TaskCompletionSource firstCaptureStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseFirstCapture = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int remainingFailures = failuresBeforeSuccess;
        private int remainingConnectFailures = connectFailuresBeforeSuccess;

        public string AdapterName => "test";
        public int CaptureCalls { get; private set; }
        public Task FirstCaptureStarted => firstCaptureStarted.Task;
        public QhyCameraStatus Status { get; private set; } = new(false, null, null, null, null, DateTimeOffset.UtcNow);

        public void ReleaseFirstCapture() => releaseFirstCapture.TrySetResult();

        public Task<QhyCameraIdentity> ConnectExactAsync(string expectedStableId, string expectedModel, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remainingConnectFailures > 0)
            {
                remainingConnectFailures--;
                throw new QhyAdapterException("synthetic connect fault");
            }

            if (expectedStableId != identity.StableId || !expectedModel.Equals(identity.Model, StringComparison.OrdinalIgnoreCase))
            {
                throw new QhyAdapterException("identity mismatch");
            }

            Status = new QhyCameraStatus(true, identity, -10, 10, null, DateTimeOffset.UtcNow);
            return Task.FromResult(identity);
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Status = Status with { Connected = false, TimestampUtc = DateTimeOffset.UtcNow };
            return Task.CompletedTask;
        }

        public Task<QhyFilterWheelStatus> ReadFilterWheelStatusAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new QhyFilterWheelStatus(true, true, 5, "R", null, DateTimeOffset.UtcNow));
        }

        public Task<QhyFilterWheelStatus> SelectFilterAsync(string filterName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(filterName, "R", StringComparison.OrdinalIgnoreCase))
            {
                throw new QhyAdapterException("unknown test filter");
            }
            return ReadFilterWheelStatusAsync(cancellationToken);
        }

        public async Task<QhyFrame> CaptureSingleFrameAsync(QhyFrameSettings settings, CancellationToken cancellationToken)
        {
            CaptureCalls++;
            if (blockFirstCapture && CaptureCalls == 1)
            {
                firstCaptureStarted.TrySetResult();
                await releaseFirstCapture.Task.WaitAsync(cancellationToken);
            }
            if (captureDelay is { } delay) await Task.Delay(delay, cancellationToken);
            if (remainingFailures > 0)
            {
                remainingFailures--;
                throw new QhyAdapterException("synthetic capture fault");
            }

            var source = QhyCodecAndAnalysisTests.CreateStarFrame();
            return source with { Settings = settings, Identity = identity };
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private long utcTicks = initialUtc.UtcTicks;

        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);

        public void Advance(TimeSpan delta) => Interlocked.Add(ref utcTicks, delta.Ticks);
    }
}
