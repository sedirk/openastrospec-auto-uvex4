using System.Collections.Concurrent;
using System.Text.Json;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class ObservationRunJournalStoreTests
{
    [Fact]
    public async Task ReinitializationRejectsChangedLockedMotionOrSafetyPlan()
    {
        await using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "run-manifest.json");
        var store = new ObservationRunJournalStore(path);
        var plan = CreatePlan();
        await store.InitializeAsync(plan, ObservationRunLockedMetadata.Empty);

        var changedMotion = plan with
        {
            Motion = plan.Motion with { MaximumSingleCorrectionDegrees = plan.Motion.MaximumSingleCorrectionDegrees / 2 },
        };
        var changedSafety = plan with { RequireSafetyMonitor = !plan.RequireSafetyMonitor };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.InitializeAsync(changedMotion, ObservationRunLockedMetadata.Empty));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.InitializeAsync(changedSafety, ObservationRunLockedMetadata.Empty));

        var reloaded = await store.ReadAsync();
        Assert.NotNull(reloaded);
        Assert.Equal(plan.Motion, reloaded.Plan.Motion);
        Assert.Equal(plan.RequireSafetyMonitor, reloaded.Plan.RequireSafetyMonitor);
    }

    [Fact]
    public async Task RoundTripPersistsLockedInputsLatestStateCountersEvidenceAndReasons()
    {
        await using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "run-manifest.json");
        var evidencePath = Path.Combine(temporary.Path, "guide-evidence.json");
        await File.WriteAllTextAsync(evidencePath, "{\"locked\":true}");

        var plan = CreatePlan();
        var locked = new ObservationRunLockedMetadata(
            NightSetupSha256: Hash(1),
            CommissioningPresetSha256: Hash(2),
            Phd2ProfileEvidenceSha256: Hash(3),
            QhyConfigurationSha256: Hash(4),
            AdditionalHashes: new Dictionary<string, string> { ["slit-placement"] = Hash(5) },
            Labels: new Dictionary<string, string> { ["nightSetupId"] = plan.NightSetupId });
        var counters = new ObservationRunCounters(
            AtrAttemptedFrames: 8,
            AtrAcceptedFrames: 7,
            QhyAttemptedFrames: 12,
            QhyAcceptedFrames: 11,
            Additional: new Dictionary<string, long> { ["guidingSamples"] = 42 });
        var store = new ObservationRunJournalStore(path);

        var initialized = await store.InitializeAsync(plan, locked);
        var pausedAt = initialized.UpdatedUtc.AddSeconds(1);
        var paused = Snapshot(
            plan,
            ObservationRunState.PausedNeedsAttention,
            ObservationStage.PlaceTargetOnSlit,
            "Slit placement confidence is below the automatic gate.",
            "Target centroid was lost at the slit edge.",
            pausedAt);
        var afterPause = await store.PublishSnapshotAsync(paused, counters, initialized.Revision);
        Assert.Equal("Target centroid was lost at the slit edge.", afterPause.PauseReason);

        var gate = GateResult.Fail("SLIT_TARGET_LOST", "Target centroid could not be confirmed.");
        var afterGate = await store.PublishGateAsync(
            ObservationStage.PlaceTargetOnSlit,
            gate,
            afterPause.Revision);
        var evidence = await ObservationManifestWriter.DescribeEvidenceAsync("slit-analysis", evidencePath);
        var afterEvidence = await store.PublishEvidenceAsync(evidence, afterGate.Revision);

        var completedAt = afterEvidence.UpdatedUtc.AddSeconds(1);
        var completed = Snapshot(
            plan,
            ObservationRunState.Completed,
            ObservationStage.FinalizeObservation,
            "Observation completed and final evidence was committed.",
            pauseReason: null,
            completedAt);
        var committed = await store.PublishSnapshotAsync(completed, counters, afterEvidence.Revision);
        var reloaded = await new ObservationRunJournalStore(path).ReadAsync();

        Assert.NotNull(reloaded);
        Assert.Equal(committed.Revision, reloaded.Revision);
        Assert.Equal(committed.PlanSha256, reloaded.PlanSha256);
        Assert.Equal(committed.LockedMetadataSha256, reloaded.LockedMetadataSha256);
        Assert.Equal(committed.Snapshot.State, reloaded.Snapshot.State);
        Assert.Equal(committed.Snapshot.StatusMessage, reloaded.Snapshot.StatusMessage);
        Assert.Equal(committed.Snapshot.UpdatedUtc, reloaded.Snapshot.UpdatedUtc);
        Assert.Equal(plan, reloaded.Plan);
        Assert.Equal(Hash(1), reloaded.LockedMetadata.NightSetupSha256);
        Assert.Equal(Hash(5), reloaded.LockedMetadata.AdditionalHashes!["slit-placement"]);
        Assert.Equal(counters.AtrAttemptedFrames, reloaded.Counters.AtrAttemptedFrames);
        Assert.Equal(counters.AtrAcceptedFrames, reloaded.Counters.AtrAcceptedFrames);
        Assert.Equal(counters.QhyAttemptedFrames, reloaded.Counters.QhyAttemptedFrames);
        Assert.Equal(counters.QhyAcceptedFrames, reloaded.Counters.QhyAcceptedFrames);
        Assert.Equal(42, reloaded.Counters.Additional!["guidingSamples"]);
        Assert.Equal(ObservationRunState.Completed, reloaded.TerminalState);
        Assert.Equal("Observation completed and final evidence was committed.", reloaded.TerminalReason);
        Assert.Null(reloaded.PauseReason);
        Assert.Single(reloaded.Evidence);
        Assert.Equal(evidence.AbsolutePath, reloaded.Evidence[0].AbsolutePath);
        Assert.Equal(evidence.Sha256, reloaded.Evidence[0].Sha256, ignoreCase: true);
        Assert.Equal(gate, reloaded.Gates[ObservationStage.PlaceTargetOnSlit]);
        Assert.Equal(reloaded.Revision, reloaded.Journal.Count);
        Assert.Equal(
            "Target centroid was lost at the slit edge.",
            reloaded.Journal.Single(entry => entry.Kind == ObservationRunJournalEntryKind.Snapshot &&
                                             entry.State == ObservationRunState.PausedNeedsAttention).PauseReason);
        Assert.True(reloaded.PlanSha256.Length == 64);
        Assert.True(reloaded.LockedMetadataSha256.Length == 64);
    }

    [Fact]
    public async Task ConcurrentStoreInstancesCommitEveryMutationWithoutLostUpdates()
    {
        await using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "run-manifest.json");
        var plan = CreatePlan();
        var first = new ObservationRunJournalStore(path);
        var second = new ObservationRunJournalStore(path);
        await first.InitializeAsync(plan, ObservationRunLockedMetadata.Empty);

        const int writerCount = 32;
        var writes = Enumerable.Range(0, writerCount).Select(index =>
        {
            var evidence = new EvidenceReference(
                "concurrency-test",
                Path.Combine(temporary.Path, $"evidence-{index:D2}.json"),
                Hash(index + 1),
                DateTimeOffset.UtcNow.AddMilliseconds(index));
            return (index & 1) == 0
                ? first.PublishEvidenceAsync(evidence)
                : second.PublishEvidenceAsync(evidence);
        });

        await Task.WhenAll(writes);
        var manifest = await first.ReadAsync();

        Assert.NotNull(manifest);
        Assert.Equal(writerCount, manifest.Evidence.Count);
        Assert.Equal(writerCount + 1, manifest.Revision);
        Assert.Equal(
            Enumerable.Range(1, writerCount + 1).Select(value => (long)value),
            manifest.Journal.Select(entry => entry.Revision));
        Assert.Equal(writerCount, manifest.Evidence.Select(item => item.AbsolutePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task AtomicReplacementNeverExposesEmptyOrPartialJson()
    {
        await using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "run-manifest.json");
        var store = new ObservationRunJournalStore(path);
        await store.InitializeAsync(CreatePlan(), ObservationRunLockedMetadata.Empty);
        var readFailures = new ConcurrentQueue<Exception>();
        using var stop = new CancellationTokenSource();

        var reader = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        4096,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: stop.Token);
                    if (document.RootElement.GetProperty("revision").GetInt64() < 1)
                    {
                        readFailures.Enqueue(new InvalidDataException("Reader observed an invalid revision."));
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return;
                }
                catch (IOException)
                {
                    // ReplaceFile may briefly deny an unlocked raw open on Windows. Store
                    // readers participate in the lock protocol; raw readers retry and every
                    // successful open must be a complete old-or-new JSON document.
                }
                catch (Exception exception)
                {
                    readFailures.Enqueue(exception);
                }

                await Task.Yield();
            }
        });

        try
        {
            for (var index = 0; index < 50; index++)
            {
                await store.PublishGateAsync(
                    ObservationStage.ValidateNightSetup,
                    GateResult.Pass("NIGHT_SETUP_VALID", $"Atomic commit {index}."));
            }
        }
        finally
        {
            stop.Cancel();
            await reader;
        }

        Assert.Empty(readFailures);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.tmp-*"));
        var manifest = await store.ReadAsync();
        Assert.NotNull(manifest);
        Assert.Equal(51, manifest.Revision);
    }

    [Fact]
    public async Task StaleExpectedRevisionIsRejectedBeforeDiskStateCanChange()
    {
        await using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "run-manifest.json");
        var store = new ObservationRunJournalStore(path);
        var initialized = await store.InitializeAsync(CreatePlan(), ObservationRunLockedMetadata.Empty);
        var firstGate = GateResult.Pass("FIRST", "First writer committed.");
        var committed = await store.PublishGateAsync(
            ObservationStage.ValidateNightSetup,
            firstGate,
            initialized.Revision);

        var conflict = await Assert.ThrowsAsync<ObservationRunRevisionConflictException>(() =>
            store.PublishGateAsync(
                ObservationStage.ValidateNightSetup,
                GateResult.Fail("STALE", "This stale writer must not commit."),
                initialized.Revision));

        Assert.Equal(initialized.Revision, conflict.ExpectedRevision);
        Assert.Equal(committed.Revision, conflict.ActualRevision);
        var reloaded = await store.ReadAsync();
        Assert.NotNull(reloaded);
        Assert.Equal(committed.Revision, reloaded.Revision);
        Assert.Equal(firstGate, reloaded.Gates[ObservationStage.ValidateNightSetup]);
        Assert.DoesNotContain(reloaded.Journal, entry => entry.Code == "STALE");
    }

    [Fact]
    public async Task CountersCannotRegressAndEvidencePathCannotChangeHash()
    {
        await using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "run-manifest.json");
        var store = new ObservationRunJournalStore(path);
        await store.InitializeAsync(CreatePlan(), ObservationRunLockedMetadata.Empty);
        var counted = await store.PublishCountersAsync(new ObservationRunCounters(5, 4, 7, 6));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.PublishCountersAsync(new ObservationRunCounters(4, 4, 7, 6)));

        var evidencePath = Path.Combine(temporary.Path, "immutable-evidence.json");
        var first = new EvidenceReference("test", evidencePath, Hash(100), DateTimeOffset.UtcNow);
        var published = await store.PublishEvidenceAsync(first);
        var duplicate = await store.PublishEvidenceAsync(first with { TimestampUtc = first.TimestampUtc.AddSeconds(1) });
        Assert.Equal(published.Revision, duplicate.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.PublishEvidenceAsync(first with { Sha256 = Hash(101) }));

        var reloaded = await store.ReadAsync();
        Assert.NotNull(reloaded);
        Assert.Equal(published.Revision, reloaded.Revision);
        Assert.Equal(counted.Counters, reloaded.Counters);
        Assert.Single(reloaded.Evidence);
    }

    [Fact]
    public async Task CompletionCommitAtomicallyCapturesFinalGatesCountersAndPriorEvidence()
    {
        await using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "run-manifest.json");
        var evidencePath = Path.Combine(temporary.Path, "final-guide-evidence.json");
        await File.WriteAllTextAsync(evidencePath, "{\"settled\":true}");
        var plan = CreatePlan();
        var store = new ObservationRunJournalStore(path);
        await store.InitializeAsync(plan, ObservationRunLockedMetadata.Empty);
        var evidence = await ObservationManifestWriter.DescribeEvidenceAsync(
            "guide-settle",
            evidencePath);
        var beforeCompletion = await store.PublishEvidenceAsync(evidence);
        var counters = new ObservationRunCounters(9, 8, 42, 40);
        var gates = new Dictionary<ObservationStage, GateResult>
        {
            [ObservationStage.ValidateNightSetup] = GateResult.Pass("SETUP_LOCKED", "locked"),
            [ObservationStage.FinalizeObservation] = GateResult.Pass("QHY_RECONCILED", "reconciled"),
        };
        var completedAt = beforeCompletion.UpdatedUtc.AddSeconds(1);
        var completed = Snapshot(
            plan,
            ObservationRunState.Completed,
            ObservationStage.FinalizeObservation,
            "All evidence and counters were durably committed.",
            pauseReason: null,
            completedAt);

        var committed = await store.CommitCompletionAsync(completed, counters, gates);
        var reloaded = await store.ReadAsync();

        Assert.NotNull(reloaded);
        Assert.Equal(beforeCompletion.Revision + 1, committed.Revision);
        Assert.Equal(committed.Revision, reloaded.Revision);
        Assert.Equal(ObservationRunState.Completed, reloaded.Snapshot.State);
        Assert.Equal(ObservationRunState.Completed, reloaded.TerminalState);
        Assert.Equal(counters, reloaded.Counters);
        Assert.Equal(gates.Count, reloaded.Gates.Count);
        Assert.Equal(gates[ObservationStage.FinalizeObservation], reloaded.Gates[ObservationStage.FinalizeObservation]);
        Assert.Single(reloaded.Evidence);
        Assert.Equal(evidence.Sha256, reloaded.Evidence[0].Sha256, ignoreCase: true);
        Assert.Equal(ObservationRunJournalEntryKind.Snapshot, reloaded.Journal[^1].Kind);
        Assert.Equal(ObservationRunState.Completed, reloaded.Journal[^1].State);
    }

    private static ObservationSnapshot Snapshot(
        ObservationPlan plan,
        ObservationRunState state,
        ObservationStage stage,
        string message,
        string? pauseReason,
        DateTimeOffset updatedUtc) =>
        new(
            plan.ObservationRunId,
            state,
            stage,
            stage,
            message,
            pauseReason,
            state == ObservationRunState.Completed ? ObservationRunCoordinator.Stages.Count : 5,
            ObservationRunCoordinator.Stages.Count,
            updatedUtc,
            new[] { new ObservationEvent(updatedUtc, state, stage, "SNAPSHOT_TEST", message) });

    private static ObservationPlan CreatePlan() =>
        new(
            "run-journal-test",
            "night-setup-test",
            new EquatorialTarget("Vega", "HIP 91262", 279.23473479, 38.78368896),
            new ObservatorySite(31.2, 121.5, 20),
            DateTimeOffset.Parse("2026-08-16T14:00:00Z"),
            TimeSpan.FromHours(2),
            new HorizonPolicy(),
            new MotionLimits(),
            "ATR585M",
            "G3M2210M spectroscopy",
            "QHYminiCam8M");

    private static string Hash(int value) => value.ToString("X64");

    private sealed class TemporaryDirectory : IAsyncDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uvex-observation-journal-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
