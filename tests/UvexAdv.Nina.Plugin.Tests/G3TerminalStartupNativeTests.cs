using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using NINA.Astrometry.Interfaces;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using UvexAdv.Nina.Plugin.SequenceItems;
using UvexAdv.Observatory;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed partial class NativeSequencePlanTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("fresh-quality-failure")]
    [InlineData("same-logical-target")]
    [InlineData("active")]
    [InlineData("paused")]
    [InlineData("paused-attention")]
    [InlineData("ambiguous-g3")]
    [InlineData("ambiguous-phd")]
    [InlineData("mechanical-debt")]
    [InlineData("meridian-debt")]
    [InlineData("corrupt")]
    [InlineData("original-binding-changed")]
    [InlineData("original-context-changed")]
    [InlineData("stop-unknown")]
    [InlineData("camera-busy")]
    [InlineData("mount-moving")]
    [InlineData("owner-changed")]
    [InlineData("remote-owner-configuration-changed")]
    [InlineData("remote-camera-changed")]
    [InlineData("stale-stop")]
    [InlineData("capture-failed")]
    [InlineData("unsafe-before")]
    [InlineData("unsafe-after-frame")]
    [InlineData("cancel-after-frame")]
    [InlineData("deadline-after-frame")]
    public async Task TerminalG3StartupRetirementRunsFreshNativeTargetsWithoutReplayingOldMotion(string mode)
    {
        await using var fixture = await TerminalStartupFixture.CreateAsync(mode);
        await AssertTerminalStartupNightAsync(fixture, mode);
    }

    [Fact]
    public async Task RecordedCancelled10CasLedgerCanStartHd218393WithNewPresetWithoutChangingOriginalEvidence()
    {
        var observations = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(observations)) return;
        const string run = "UVEX-20261021T141104Z-634da089beae440";
        var sourceDirectory = Path.Combine(observations, run);
        var sourceManifest = Path.Combine(sourceDirectory, "manifest.json");
        var sourceLedger = Path.Combine(sourceDirectory, "control", "g3-acquisition-motion.json");
        // An operator may since have retired this exact discovery handle. Find
        // the immutable archive too; never change the real observation tree.
        if (!File.Exists(sourceLedger)) sourceLedger = Assert.Single(
            Directory.GetFiles(Path.Combine(sourceDirectory, "control"), "g3-acquisition-motion.json.terminal-*.archive"));
        var originalManifest = await File.ReadAllBytesAsync(sourceManifest);
        var originalLedger = await File.ReadAllBytesAsync(sourceLedger);
        await using var fixture = await TerminalStartupFixture.CopyRecordedAsync(run, originalManifest, originalLedger);
        Assert.Equal("10 Cas", fixture.Manifest.Plan.Target.Name);
        Assert.Equal(ObservationRunState.Cancelled, fixture.Manifest.TerminalState);
        Assert.Equal(G3AcquisitionMotionPhase.OutboundIntent, fixture.State.Phase);
        Assert.Equal(2, fixture.State.CorrectionAttempts);
        Assert.Equal(1474.403043112041, fixture.State.CumulativeMotionArcseconds);
        await AssertTerminalStartupNightAsync(fixture, "success");
        Assert.Equal(SHA256.HashData(originalLedger), SHA256.HashData(await File.ReadAllBytesAsync(sourceLedger)));
        Assert.Equal(SHA256.HashData(originalManifest), SHA256.HashData(await File.ReadAllBytesAsync(sourceManifest)));
    }

    private static async Task AssertTerminalStartupNightAsync(TerminalStartupFixture fixture, string mode)
    {
        InitializeAstrometry();
        var settings = Settings();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? Proxy<IAstrometrySettings>() : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        using var cancel = new CancellationTokenSource();
        var night = new UvexNightSequenceContainer(profiles, host, null!, settings) { StopAtDawn = false };
        var first = new TerminalStartupRunner(fixture, mode, night, cancel);
        var second = new TerminalGateRunner(null, false);
        night.Add(FocusTestTarget(profiles, host, settings, "HD 218393", () => first));
        night.Add(FocusTestTarget(profiles, host, settings, "HD 224559", () => second));
        var error = await Record.ExceptionAsync(() => night.Execute(new Progress<ApplicationStatus>(), cancel.Token)
            .WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.IsNotType<TimeoutException>(error);
        var allowed = mode is "success" or "fresh-quality-failure";
        Assert.Equal(allowed, second.Stages.Count > 0);
        if (allowed)
        {
            Assert.Equal(ObservationRunCoordinator.Stages, second.Stages);
            Assert.True(first.Calls.IndexOf("fresh-stationary-frame") < first.Calls.IndexOf("archive-verified"));
            Assert.True(first.Calls.IndexOf("archive-verified") < first.Calls.IndexOf("new-target-catalog-slew"));
            var archive = Assert.Single(Directory.GetFiles(fixture.Root, "g3-acquisition-motion.json.terminal-*.archive", SearchOption.AllDirectories));
            Assert.Equal(fixture.OriginalLedger, await File.ReadAllBytesAsync(archive));
            Assert.Equal(fixture.State, (await G3AcquisitionMotionStore.LoadAsync(archive, default)).State);
            using var audit = JsonDocument.Parse(await File.ReadAllTextAsync(archive + ".audit.json"));
            foreach (var key in new[] { "returnVerified", "targetOnSlitVerified", "motionSent", "countersReset" })
                Assert.False(audit.RootElement.GetProperty(key).GetBoolean());
            Assert.Empty(await G3AcquisitionMotionStore.DiscoverAsync(fixture.Root, default));
        }
        else
        {
            Assert.DoesNotContain("new-target-catalog-slew", first.Calls);
            Assert.True(File.Exists(fixture.LedgerPath));
            Assert.Equal(fixture.OriginalLedger, await File.ReadAllBytesAsync(fixture.LedgerPath));
            Assert.Empty(Directory.GetFiles(fixture.Root, "*.archive", SearchOption.AllDirectories));
        }
        Assert.Equal(fixture.OriginalManifest, await File.ReadAllBytesAsync(fixture.ManifestPath));
        Assert.DoesNotContain("old-return", first.Calls);
        Assert.DoesNotContain("focus-move", first.Calls);
        using var journal = JsonDocument.Parse(await File.ReadAllTextAsync(night.JournalPath));
        var outcomes = journal.RootElement.GetProperty("outcomes").EnumerateArray().Where(x => x.TryGetProperty("target", out _)).ToArray();
        Assert.Equal(allowed ? 2 : 1, outcomes.Length);
        Assert.Equal(mode == "success" ? "Completed" : mode == "fresh-quality-failure" ? "SkippedQualityFailure" : "Stopped",
            outcomes[0].GetProperty("outcome").GetString());
        if (allowed) Assert.Equal("Completed", outcomes[1].GetProperty("outcome").GetString());
        if (host.SafetyStandby is { } guard)
        {
            night.EndNightCommand.Execute(null);
            await guard.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private sealed class TerminalStartupRunner(TerminalStartupFixture fixture, string mode,
        UvexNightSequenceContainer night, CancellationTokenSource cancel) : ObservationStageRunnerBase
    {
        internal List<string> Calls { get; } = [];
        private bool frameCaptured;
        private bool archived;

        public override async Task<StageResult> ExecuteStageAsync(ObservationStage stage, ObservationContext context, CancellationToken token)
        {
            if (stage == ObservationStage.ValidateNightSetup)
            {
                var metadata = fixture.Manifest.LockedMetadata with
                {
                    CommissioningPresetSha256 = new string('E', 64),
                    QhyConfigurationSha256 = mode == "remote-owner-configuration-changed"
                        ? new string('E', 64) : fixture.Manifest.LockedMetadata.QhyConfigurationSha256,
                    AdditionalHashes = new Dictionary<string, string> { ["actionConfigurationSha256"] = new string('F', 64) },
                    Labels = new Dictionary<string, string>(fixture.Manifest.LockedMetadata.Labels!)
                    {
                        ["nativeTargetRootRunId"] = mode == "same-logical-target" ? fixture.RunId : context.Plan.ObservationRunId,
                        ["qhyCameraId"] = mode == "remote-camera-changed" ? "replacement-camera" : fixture.Manifest.LockedMetadata.Labels!["qhyCameraId"],
                    },
                };
                try
                {
                    var result = await TerminalGuideStartupRetirement.TryRetireAsync(fixture.Root,
                        context.Plan.ObservationRunId, metadata,
                        t => { t.ThrowIfCancellationRequested(); Calls.Add("prepare-owners-no-motion"); return Task.CompletedTask; },
                        t =>
                        {
                            t.ThrowIfCancellationRequested();
                            Assert.True(File.Exists(fixture.LedgerPath));
                            Assert.DoesNotContain("new-target-catalog-slew", Calls);
                            if (mode == "capture-failed") throw new InvalidOperationException("Fresh stationary frame unavailable.");
                            Calls.Add("fresh-stationary-frame"); frameCaptured = true;
                            if (mode == "cancel-after-frame") cancel.Cancel();
                            if (mode == "unsafe-after-frame") night.OnNightUnsafe();
                            if (mode == "deadline-after-frame")
                            {
                                var field = typeof(UvexNightSequenceContainer).GetField("standbyPlan", BindingFlags.Instance | BindingFlags.NonPublic)!;
                                var plan = (NativeSequencePlan)field.GetValue(night)!;
                                field.SetValue(night, plan with { DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(-1) });
                            }
                            return Task.CompletedTask;
                        },
                        t =>
                        {
                            t.ThrowIfCancellationRequested(); Calls.Add("read-stop");
                            return Task.FromResult(fixture.Witness() with
                            {
                                OwnersIdle = mode != "stop-unknown", CameraIdle = mode != "camera-busy",
                                MountStationary = mode != "mount-moving", Phd2IdentityMatches = mode != "owner-changed",
                                CapturedUtc = mode == "stale-stop" ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow,
                            });
                        },
                        t =>
                        {
                            t.ThrowIfCancellationRequested(); Calls.Add("check-safety");
                            if (mode == "unsafe-before" || mode == "unsafe-after-frame" && frameCaptured)
                                throw new InvalidOperationException("SAFETY_UNSAFE");
                            if (mode == "deadline-after-frame" && frameCaptured)
                            {
                                var gate = new NativeSequencePlan(1, 2, 0, DeadlineUtc: DateTimeOffset.UtcNow.AddSeconds(-1)).StopGateNow();
                                Assert.Equal("NIGHT_DEADLINE_REACHED", gate!.Code);
                                throw new ObservationCheckpointGateException(gate, stage);
                            }
                            return Task.CompletedTask;
                        }, token);
                    // Null leaves the production's existing exact-context
                    // recovery path responsible; it is not permission to move.
                    if (result is null || result.Count == 0)
                        return new(GateResult.Unknown("G3_MOTION_RECOVERY_CONTEXT_CHANGED", "Old strict recovery remains required."));
                    Assert.True(frameCaptured);
                    Assert.False(File.Exists(fixture.LedgerPath));
                    archived = true; Calls.Add("archive-verified");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    return new(GateResult.Unknown("G3_TERMINAL_STARTUP_RETIREMENT_REJECTED", ex.Message));
                }
            }
            if (stage == ObservationStage.SlewToCatalogTarget)
            {
                Assert.True(archived); Calls.Add("new-target-catalog-slew");
            }
            if (mode == "fresh-quality-failure" && stage == ObservationStage.AcquireG3SlitField)
                return new(RealObservationStageRunner.CreateCatalogShortPositionFailure("New target short pair exhausted; old ledger is already archived."));
            return new(GateResult.Pass("SIM_FRESH_STAGE", "Finite device ports; no equipment."));
        }
    }

    private sealed class TerminalStartupFixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "uvex-terminal-startup-tests", Guid.NewGuid().ToString("N"));
        internal string RunId { get; private set; } = "old-10cas";
        internal string ManifestPath => Path.Combine(Root, RunId, "manifest.json");
        internal string LedgerPath => Path.Combine(Root, RunId, "control", "g3-acquisition-motion.json");
        internal ObservationRunManifest Manifest { get; private set; } = null!;
        internal G3AcquisitionMotionState State { get; private set; } = null!;
        internal byte[] OriginalManifest { get; private set; } = [];
        internal byte[] OriginalLedger { get; private set; } = [];

        internal GuideBoundaryWitness Witness() => new(true, true, true, true,
            Manifest.LockedMetadata.Labels!["telescopeId"], Manifest.LockedMetadata.Labels["g3CameraId"],
            Phd2AppState.Stopped, true, DateTimeOffset.UtcNow);

        internal static async Task<TerminalStartupFixture> CreateAsync(string mode)
        {
            var value = new TerminalStartupFixture();
            var terminal = mode switch
            {
                "active" => ObservationRunState.RunningAuto,
                "paused" => ObservationRunState.Paused,
                "paused-attention" => ObservationRunState.PausedNeedsAttention,
                _ => ObservationRunState.Cancelled,
            };
            await value.AddRunAsync(value.RunId, terminal, true);
            if (mode == "ambiguous-g3") await value.AddRunAsync("second-old-target", ObservationRunState.Faulted, true);
            if (mode == "ambiguous-phd") await value.AddRunAsync("second-old-target", ObservationRunState.Faulted, false);
            value.Manifest = (await new ObservationRunJournalStore(value.ManifestPath).ReadAsync(default))!;
            value.State = (await G3AcquisitionMotionStore.LoadAsync(value.LedgerPath, default)).State!;
            if (mode == "corrupt") await File.AppendAllTextAsync(value.LedgerPath, "corrupt");
            if (mode == "original-binding-changed")
                await G3AcquisitionMotionStore.WriteAtomicAsync(value.LedgerPath,
                    value.State with { CommissioningPresetSha256 = new string('D', 64) }, default);
            if (mode == "original-context-changed")
                await G3AcquisitionMotionStore.WriteAtomicAsync(value.LedgerPath,
                    value.State with { RecoveryContextSha256 = new string('D', 64) }, default);
            if (mode == "meridian-debt")
                await NativeMeridianJournalStore.SaveAsync(Path.Combine(value.Root, "control", "native-meridian-flip.json"),
                    new(value.RunId, "FlipRequested", "pierEast", "", DateTimeOffset.UtcNow, [value.RunId]), default);
            if (mode == "mechanical-debt")
            {
                var now = DateTimeOffset.UtcNow;
                var pending = new SlitPlacementPendingState(SlitPlacementPendingState.CurrentSchemaVersion, value.RunId,
                    Guid.NewGuid().ToString("N"), value.State.ActionConfigurationSha256, value.State.RecoveryContextSha256,
                    value.State.CommissioningPresetSha256, "original-transform", "pierEast", "J2000",
                    1, 64, 1, 64, 1.01, 64, .01, 10, .1, 1, 10, 1200, .01, 1,
                    now, now, now, SlitPlacementPendingPhase.MoveIntent, "evidence/original-move.json");
                await SlitPlacementPendingStore.WriteAtomicAsync(Path.Combine(value.Root, value.RunId, "control", "slit-placement-pending.json"), pending, default);
            }
            value.OriginalManifest = await File.ReadAllBytesAsync(value.ManifestPath);
            value.OriginalLedger = await File.ReadAllBytesAsync(value.LedgerPath);
            return value;
        }

        private async Task AddRunAsync(string run, ObservationRunState terminal, bool g3)
        {
            var now = DateTimeOffset.UtcNow;
            var plan = new ObservationPlan(run, "old-night", new("10 Cas", "* 10 Cas", 1.61057219282, 64.19615796995),
                new(33, 120, 0), now.AddHours(-2), TimeSpan.FromMinutes(35), new(), new(), "ATR", "G3", "QHY");
            var action = new string('A', 64); var preset = new string('B', 64);
            var store = new ObservationRunJournalStore(Path.Combine(Root, run, "manifest.json"));
            var manifest = await store.InitializeAsync(plan, new(CommissioningPresetSha256: preset, QhyConfigurationSha256: new string('D', 64),
                AdditionalHashes: new Dictionary<string, string> { ["actionConfigurationSha256"] = action },
                Labels: new Dictionary<string, string>
                { ["telescopeId"] = "mount", ["g3CameraId"] = "guide-camera", ["qhyCameraId"] = "qhy-camera", ["nativeTargetRootRunId"] = run }));
            await store.PublishSnapshotAsync(manifest.Snapshot with { State = terminal, UpdatedUtc = DateTimeOffset.UtcNow }, manifest.Counters, manifest.Revision);
            if (!g3)
            {
                var phd = Phd2SlitPlacementCommissioningTests.CreateState(run, Phd2LockShiftPendingPhase.ReturnRequired)
                    with { ActionConfigurationSha256 = action, CommissioningPresetSha256 = preset };
                await Phd2LockShiftPendingStore.WriteAtomicAsync(Path.Combine(Root, run, "control", "phd2-lock-shift-pending.json"), phd, default);
                return;
            }
            var state = new G3AcquisitionMotionState(2, G3AcquisitionMotionState.CurrentTangentProjectionId,
                run, Guid.NewGuid().ToString("N"), action, RealObservationStageRunner.ComputeSlitRecoveryContextSha256(plan, "mount"), preset,
                G3AcquisitionMotionKind.WcsCentering, G3AcquisitionMotionPhase.OutboundIntent, "pierEast", "J2000",
                1.97, 64.35, 1.33, 64.28, 1.07, 64.24, -1413.24, -400.37, 427.51,
                5400, 18000, 21600, 8, 2, 90, 1200, 1474.403043, 2,
                now.AddHours(-1), now.AddHours(-1), now, "evidence/old-motion.json");
            await G3AcquisitionMotionStore.WriteAtomicAsync(Path.Combine(Root, run, "control", "g3-acquisition-motion.json"), state, default);
        }

        internal static async Task<TerminalStartupFixture> CopyRecordedAsync(string run, byte[] manifest, byte[] ledger)
        {
            var value = new TerminalStartupFixture { RunId = run, OriginalManifest = manifest, OriginalLedger = ledger };
            Directory.CreateDirectory(Path.GetDirectoryName(value.LedgerPath)!);
            await File.WriteAllBytesAsync(value.ManifestPath, manifest);
            await File.WriteAllBytesAsync(value.LedgerPath, ledger);
            value.Manifest = (await new ObservationRunJournalStore(value.ManifestPath).ReadAsync(default))!;
            value.State = (await G3AcquisitionMotionStore.LoadAsync(value.LedgerPath, default)).State!;
            return value;
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            return ValueTask.CompletedTask;
        }
    }
}
