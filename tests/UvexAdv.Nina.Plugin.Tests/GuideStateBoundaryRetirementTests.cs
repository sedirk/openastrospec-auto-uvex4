using System.Text.Json;
using UvexAdv.Nina.Plugin;
using UvexAdv.Observatory;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class GuideStateBoundaryRetirementTests
{
    private static GuideBoundaryWitness Witness() => new(true, true, true, true,
        "mount", "guide-camera", Phd2AppState.Stopped, true, DateTimeOffset.UtcNow);

    [Fact]
    public async Task SameRunOrMeridianContinuationCannotRetirePriorBudget()
    {
        await WithState(ObservationRunState.Completed, async (root, path, store) =>
        {
            var original = (await store.ReadAsync(default))!;
            Assert.False(GuideStateBoundaryRetirement.IsDifferentLogicalTarget(original, original.ObservationRunId, original.LockedMetadata));
            var metadata = original.LockedMetadata with { Labels = new Dictionary<string,string> { ["nativeTargetRootRunId"] = "target-root" } };
            var segment = original with { LockedMetadata = metadata };
            Assert.False(GuideStateBoundaryRetirement.IsDifferentLogicalTarget(segment, "second-segment", metadata));
            Assert.True(GuideStateBoundaryRetirement.IsDifferentLogicalTarget(segment, "different-target", original.LockedMetadata));
        });
    }

    [Fact]
    public async Task G3AndPhdAreBothValidatedBeforeAnyHandleIsRetired()
    {
        await WithState(ObservationRunState.Cancelled, async (root, path, store) =>
        {
            var phd = (await Phd2LockShiftPendingStore.LoadAsync(path, default)).State!;
            var now = DateTimeOffset.UtcNow;
            var g3 = new G3AcquisitionMotionState(2, G3AcquisitionMotionState.CurrentTangentProjectionId,
                "old-run", Guid.NewGuid().ToString("N"), phd.ActionConfigurationSha256, new string('B', 64),
                phd.CommissioningPresetSha256, G3AcquisitionMotionKind.LocalSearch, G3AcquisitionMotionPhase.ReturnIntent,
                "pierEast", "J2000", 10, 20, 10, 20, 10, 20, 0, 0, 1, 30, 100, 500, 20, 2, 10, 300, 40, 3,
                now, now, now, "evidence/test.json");
            var g3Path = Path.Combine(root, "old-run", "control", "g3-acquisition-motion.json");
            await G3AcquisitionMotionStore.WriteAtomicAsync(g3Path, g3, default);
            var original = await File.ReadAllBytesAsync(g3Path);
            await File.AppendAllTextAsync(g3Path, "corrupt");
            await Assert.ThrowsAsync<InvalidOperationException>(() => GuideStateBoundaryRetirement.RetireAsync(root, "old-run", _ => Task.FromResult(Witness()), default));
            Assert.True(File.Exists(path));
            Assert.Empty(Directory.GetFiles(root, "*.archive", SearchOption.AllDirectories));
            await File.WriteAllBytesAsync(g3Path, original);
            var archives = await GuideStateBoundaryRetirement.RetireAsync(root, "old-run", _ => Task.FromResult(Witness()), default);
            Assert.Equal(2, archives.Count);
            Assert.Equal(original, await File.ReadAllBytesAsync(archives.Single(x => x.Contains("g3-acquisition"))));
            Assert.Empty(await G3AcquisitionMotionStore.DiscoverAsync(root, default));
        });
    }

    [Fact]
    public async Task HostCleansAfterTerminalCommitWhileLeaseIsStillHeld()
    {
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        var root = Path.Combine(Path.GetTempPath(), "uvex-boundary-host", Guid.NewGuid().ToString("N"));
        try
        {
            var runner = new BoundaryRunner(host, Path.Combine(root, "owner.lock"));
            var plan = new ObservationPlan("terminal-test-" + Guid.NewGuid().ToString("N"), "night",
                new("synthetic", "test", 0, 89), new(31, 121, 20), DateTimeOffset.UtcNow,
                TimeSpan.FromMinutes(1), new(0, 0, 0), new(), "ATR", "G3", "QHY");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await host.RunAsync(plan, runner, timeout.Token);
            Assert.Equal(1, runner.Calls);
            using var lease = RealObservationRunOwnershipLease.TryAcquire(runner.RealObservationOwnershipLockPath).Lease;
            Assert.NotNull(lease);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class BoundaryRunner(ObservationCoordinatorHost host, string path) : ObservationStageRunnerBase,
        IRealObservationRunOwnershipSource, IObservationTerminalGuideCleanup
    {
        public int Calls;
        public string RealObservationOwnershipLockPath => path;
        public override Task<StageResult> ExecuteStageAsync(ObservationStage stage, ObservationContext context, CancellationToken token) =>
            Task.FromResult(new StageResult(GateResult.Pass("TEST", "synthetic")));
        public async Task CleanupTerminalGuideStateAsync(string runId, CancellationToken token)
        {
            Calls++;
            Assert.Equal(GateDisposition.Passed, host.RealRunOwnershipGate().Disposition);
            Assert.False(RealObservationRunOwnershipLease.TryAcquire(path).Acquired);
            var manifest = await new ObservationRunJournalStore(host.Dashboard.ManifestPath!).ReadAsync(token);
            Assert.Equal(runId, manifest!.ObservationRunId);
            Assert.Equal(ObservationRunState.Completed, manifest.TerminalState);
        }
    }

    private sealed class SilentNotifier : IObservationAttentionNotifier
    {
        public void Notify(ObservationAttentionNotification notification) { }
        public void ClearActiveIndicator() { }
        public void Dispose() { }
    }

    [Theory]
    [InlineData(ObservationRunState.Completed)]
    [InlineData(ObservationRunState.Faulted)]
    [InlineData(ObservationRunState.Cancelled)]
    public async Task TerminalTargetArchivePreservesOriginalBytesAndBudgetAndIsIdempotent(ObservationRunState terminal)
    {
        await WithState(terminal, async (root, path, store) =>
        {
            var bytes = await File.ReadAllBytesAsync(path);
            var manifest = await File.ReadAllBytesAsync(store.ManifestPath);
            var archives = await GuideStateBoundaryRetirement.RetireAsync(root, "old-run", _ => Task.FromResult(Witness()), default);
            Assert.Single(archives);
            Assert.False(File.Exists(path));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(archives[0]));
            Assert.Equal(manifest, await File.ReadAllBytesAsync(store.ManifestPath));
            using var audit = JsonDocument.Parse(await File.ReadAllTextAsync(archives[0] + ".audit.json"));
            foreach (var key in new[] { "returnVerified", "targetOnSlitVerified", "motionSent", "countersReset", "calibrationChanged" })
                Assert.False(audit.RootElement.GetProperty(key).GetBoolean());
            Assert.Empty(await Phd2LockShiftPendingStore.DiscoverAsync(root, default));
            Assert.Empty(await GuideStateBoundaryRetirement.RetireAsync(root, "old-run", _ => Task.FromResult(Witness()), default));
        });
    }

    [Theory]
    [InlineData("owners")][InlineData("lease")][InlineData("camera")][InlineData("motion")]
    [InlineData("mount")][InlineData("guideCamera")][InlineData("guiding")][InlineData("identity")]
    [InlineData("stale")][InlineData("future")][InlineData("active")][InlineData("corrupt")]
    [InlineData("binding")][InlineData("changedAfterProof")]
    public async Task UncertainOrChangedStatePreservesDiscoveryHandle(string condition)
    {
        await WithState(condition == "active" ? ObservationRunState.RunningAuto : ObservationRunState.Faulted,
            async (root, path, store) =>
        {
            var witness = condition switch
            {
                "owners" => Witness() with { OwnersIdle = false },
                "lease" => Witness() with { ExclusiveRunOwner = false },
                "camera" => Witness() with { CameraIdle = false },
                "motion" => Witness() with { MountStationary = false },
                "mount" => Witness() with { TelescopeId = "other" },
                "guideCamera" => Witness() with { GuideCameraId = "other" },
                "guiding" => Witness() with { Phd2State = Phd2AppState.Guiding },
                "identity" => Witness() with { Phd2IdentityMatches = false },
                "stale" => Witness() with { CapturedUtc = DateTimeOffset.UtcNow.AddSeconds(-30) },
                "future" => Witness() with { CapturedUtc = DateTimeOffset.UtcNow.AddMinutes(1) },
                _ => Witness(),
            };
            if (condition == "corrupt") await File.AppendAllTextAsync(path, "corrupt");
            if (condition == "binding")
            {
                var state = (await Phd2LockShiftPendingStore.LoadAsync(path, default)).State!;
                await Phd2LockShiftPendingStore.WriteAtomicAsync(path, state with { ActionConfigurationSha256 = new string('F', 64) }, default);
            }
            var before = await File.ReadAllBytesAsync(path);
            var reads = 0;
            await Assert.ThrowsAsync<InvalidOperationException>(() => GuideStateBoundaryRetirement.RetireAsync(root, "old-run", async _ =>
            {
                if (++reads == 2 && condition == "changedAfterProof") await File.AppendAllTextAsync(path, "changed");
                return witness;
            }, default));
            Assert.True(File.Exists(path));
            if (condition != "changedAfterProof") Assert.Equal(before, await File.ReadAllBytesAsync(path));
            Assert.Empty(Directory.GetFiles(root, "*.archive", SearchOption.AllDirectories));
        });
    }

    [Fact]
    public void GuideTargetFailuresAreSkippableButIdentityAndSafetyAreNot()
    {
        foreach (var code in new[] { "PHD2_LOCK_RETURN_EPOCH_CHANGED", "PHD2_LOCK_RETURN_SETTLE_FAILED", "PHD2_PLACEMENT_GUIDE_STAR_LOST" })
            Assert.True(NativeSequencePlan.IsSkippableQualityFailure(code));
        foreach (var code in new[] { "PHD2_LOCK_LEDGER_BINDING_CHANGED", "PHD2_IDENTITY_CHANGED", "SAFETY_UNSAFE", "STAGE_EXCEPTION" })
            Assert.False(NativeSequencePlan.IsSkippableQualityFailure(code));
    }

    private static async Task WithState(ObservationRunState terminal,
        Func<string, string, ObservationRunJournalStore, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "uvex-terminal-guide-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var state = Phd2SlitPlacementCommissioningTests.CreateState("old-run", Phd2LockShiftPendingPhase.ReturnRequired);
            var path = Path.Combine(root, "old-run", "control", "phd2-lock-shift-pending.json");
            var store = new ObservationRunJournalStore(Path.Combine(root, "old-run", "manifest.json"));
            var plan = new ObservationPlan("old-run", "night", new("target", "catalog", 89, 46),
                new(31, 121, 20), DateTimeOffset.UtcNow, TimeSpan.FromHours(2), new(), new(), "ATR", "G3", "QHY");
            var manifest = await store.InitializeAsync(plan, new(CommissioningPresetSha256: state.CommissioningPresetSha256,
                AdditionalHashes: new Dictionary<string,string> { ["actionConfigurationSha256"] = state.ActionConfigurationSha256 },
                Labels: new Dictionary<string,string> { ["telescopeId"] = "mount", ["g3CameraId"] = "guide-camera" }));
            await store.PublishSnapshotAsync(manifest.Snapshot with { State = terminal, UpdatedUtc = DateTimeOffset.UtcNow }, manifest.Counters, manifest.Revision);
            await Phd2LockShiftPendingStore.WriteAtomicAsync(path, state, default);
            await test(root, path, store);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
