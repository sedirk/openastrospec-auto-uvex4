using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
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
    [InlineData("capacity-exhausted", true, true)]
    [InlineData("already-on-slit", true, true)]
    [InlineData("clock-not-repaired", false, false)]
    [InlineData("first-stop-unknown", false, false)]
    [InlineData("continuation-stop-unknown", true, false)]
    [InlineData("return-unconfirmed", true, false)]
    [InlineData("retirement-corrupt", true, false)]
    [InlineData("night-deadline", true, false)]
    [InlineData("unsafe", true, false)]
    [InlineData("cancel", true, false)]
    public async Task ClockContinuationWithRecordedExposureCreditRetiresFailedHandoffThenActuallyExecutesNextTarget(
        string mode, bool continued, bool nextRuns)
    {
        InitializeAstrometry();
        var settings = Settings();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? Proxy<IAstrometrySettings>() : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        using var fixture = new ClockHandoffFixture();
        using var cancellation = new CancellationTokenSource();
        var night = new UvexNightSequenceContainer(profiles, host, null!, settings) { StopAtDawn = false };
        var nightStates = new System.Collections.Concurrent.ConcurrentQueue<string>();
        night.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(night.NightState)) nightStates.Enqueue(night.NightState); };
        var runners = new List<ClockHandoffBoundaryRunner>();
        var calls = new List<string>();
        var next = new TerminalGateRunner(null, false);
        var target = FocusTestTarget(profiles, host, settings, "HD 218393", () =>
        {
            var runner = new ClockHandoffBoundaryRunner(mode, runners.Count, fixture, calls, host, night, cancellation);
            runners.Add(runner);
            return runner;
        });
        target.DurationMinutes = 35;
        night.Add(target);
        night.Add(FocusTestTarget(profiles, host, settings, "HD 224559", () =>
        {
            calls.Add("next-target");
            return next;
        }));

        var error = await Record.ExceptionAsync(() => night.Execute(new Progress<ApplicationStatus>(), cancellation.Token)
            .WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.IsNotType<TimeoutException>(error);
        Assert.Equal(continued ? 2 : 1, runners.Count);
        Assert.Equal(nextRuns, next.Stages.Count > 0);
        if (nextRuns) Assert.Equal(ObservationRunCoordinator.Stages, next.Stages);
        Assert.Equal(mode == "first-stop-unknown" ? 0 : 1, calls.Count(x => x == "clock-repair"));
        Assert.DoesNotContain("coarse-slew-after-response-exhausted", calls);
        Assert.DoesNotContain("exact-lock-command", calls);

        var session = runners[0].Session!;
        // A first-segment cleanup exception never reaches RecordSegment. It
        // must stop; it cannot pretend to have joined and accounted the card.
        var expectedAccepted = mode == "first-stop-unknown" ? 0 : mode == "already-on-slit" ? 2 : 1;
        Assert.Equal(expectedAccepted, session.Accepted);
        Assert.Equal(expectedAccepted, session.Attempts);
        Assert.Equal(expectedAccepted == 0 ? 0 : 1, session.ReusedProbes);
        Assert.Equal(expectedAccepted * 600, session.AcceptedSeconds);
        Assert.False(session.FocusAttempted);
        if (continued)
        {
            Assert.Same(session, runners[1].Session);
            Assert.True(session.ClockContinuationUsed);
            Assert.NotEqual(runners[0].RunId, runners[1].RunId);
            Assert.Equal(1, runners[1].Remaining!.ScienceFrames);
            Assert.Equal(3, runners[1].Remaining!.MaximumAttempts);
            Assert.False(runners[1].Remaining!.ForceGuideCalibration);
            Assert.Equal(session.TargetPlan!.PlannedStartUtc + TimeSpan.FromMinutes(35), runners[1].Remaining!.TargetDeadlineUtc);
            Assert.Same(fixture.Budget, session.ClockBudget);
            Assert.Equal(fixture.Credited.MotionClockStartedUtc, session.ClockBudget!.Phd2!.StartedUtc);
            Assert.Equal(1, session.ClockBudget.Phd2.Attempts);
            Assert.Equal(21.925484400443796, session.ClockBudget.Phd2.Distance);
            Assert.Equal(300, session.ClockBudget.Phd2.MaximumSeconds);
            Assert.Equal(2, session.ClockBudget.G3!.Attempts);
            Assert.Equal(1675.6780413616566, session.ClockBudget.G3.Distance);
            Assert.True(calls.IndexOf("segment-0-retired") < calls.IndexOf("clock-repair"));
            Assert.True(calls.IndexOf("clock-repair") < calls.IndexOf("segment-1-ValidateNightSetup"));
            if (mode == "already-on-slit")
            {
                Assert.Null(runners[1].CapacityGate);
                Assert.Contains("same-target-readonly-verification", calls);
                Assert.Contains("segment-1-RunScienceBlock", calls);
            }
            else
            {
                Assert.Equal(mode == "return-unconfirmed" ? "G3_MOTION_RETURN_STOP_UNCONFIRMED" :
                    G3WcsRecoveryPolicy.ResponseNotConvergedCode, runners[1].CapacityGate!.Code);
                Assert.Equal(mode == "return-unconfirmed" ? GateFailureScope.Run : GateFailureScope.TargetQuality,
                    runners[1].CapacityGate!.FailureScope);
                if (mode != "return-unconfirmed") Assert.Equal(1, runners[1].CapacityGate!.Metrics!["fineBudgetUnavailable"]);
                Assert.Contains("existing-near-response-retry-exhausted", calls);
                Assert.DoesNotContain("segment-1-PlaceTargetOnSlit", calls);
                Assert.DoesNotContain("segment-1-RunScienceBlock", calls);
            }
            if (nextRuns)
                Assert.True(calls.IndexOf("segment-1-retired") < calls.IndexOf("next-target"));
        }

        using var journal = JsonDocument.Parse(await File.ReadAllTextAsync(night.JournalPath));
        Assert.Equal(continued ? 1 : 0, journal.RootElement.GetProperty("clockContinuations").GetArrayLength());
        var outcomes = journal.RootElement.GetProperty("outcomes").EnumerateArray()
            .Where(x => x.TryGetProperty("target", out _)).ToArray();
        Assert.Equal(nextRuns ? 2 : 1, outcomes.Length);
        Assert.Equal(mode == "already-on-slit" ? "Completed" : nextRuns ? "SkippedQualityFailure" : "Stopped",
            outcomes[0].GetProperty("outcome").GetString());
        var retained = outcomes[0].GetProperty("retainedScience");
        Assert.Equal(2, retained.GetProperty("requestedFrames").GetInt32());
        Assert.Equal(expectedAccepted, retained.GetProperty("acceptedFrames").GetInt32());
        Assert.Equal(expectedAccepted, retained.GetProperty("attemptedFrames").GetInt32());
        Assert.Equal(expectedAccepted == 0 ? 0 : 1, retained.GetProperty("reusedProbeFrames").GetInt32());
        Assert.Equal(expectedAccepted * 600, retained.GetProperty("acceptedExposureSeconds").GetDouble());
        Assert.Equal(session.SegmentRunIds.Count, retained.GetProperty("segmentRunIds").GetArrayLength());
        if (mode is "capacity-exhausted" or "clock-not-repaired")
        {
            Assert.Contains("已保留 1/2 张，累计 600 秒；剩余未完成", target.LastOutcome);
            Assert.Contains(nightStates, x => x.Contains("已保留 1/2 张，累计 600 秒；剩余未完成", StringComparison.Ordinal));
            Assert.Equal(JsonValueKind.Null, outcomes[0].GetProperty("AcquisitionProgress").ValueKind);
        }
        if (mode == "already-on-slit") Assert.Equal("完成", target.LastOutcome);
        if (nextRuns) Assert.Equal("Completed", outcomes[1].GetProperty("outcome").GetString());
        if (mode is "first-stop-unknown" or "continuation-stop-unknown" or "retirement-corrupt")
            Assert.True(File.Exists(runners[^1].LedgerPath));
        if (host.SafetyStandby is { } standby)
        {
            night.EndNightCommand.Execute(null);
            await standby.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public void RecordedHd218393CreditSurvivesFreshLedgerWithoutFundingAnotherSixtySecondRoundTrip()
    {
        using var fixture = new ClockHandoffFixture();
        var budget = fixture.Budget;
        Assert.Equal(614.4, fixture.Credited.ExcludedAtrExposureSeconds, 6);
        Assert.Equal(fixture.Credited.StartedUtc.AddSeconds(614.4), budget.Phd2!.StartedUtc);
        Assert.True(budget.HasRemainingBudget(fixture.Now));
        // The real 00:11:56 fresh field left 52.55 seconds: one outward and
        // one return action require at least 60 seconds under the old limits.
        // Useful G3 correction remains available; this does not justify an
        // early target failure before its own existing bounded response retry.
        var afterFreshField = budget.Phd2.StartedUtc.AddSeconds(247.4474);
        var radius = Phd2CoarseHandoffPolicy.LimitToRemainingLedger(44, ClockHandoffLimits(), .5, 2,
            budget.Phd2.Attempts, budget.Phd2.Distance, afterFreshField - budget.Phd2.StartedUtc);
        Assert.Equal(2, radius);
        var fresh = new Phd2LockShiftLedger("fresh-optical-lineage", new(400, 300), new(400, 300), 0, 0, afterFreshField, null);
        var charged = budget.Charge(fresh);
        Assert.Equal(budget.Phd2.StartedUtc, charged.StartedUtc);
        Assert.Equal(fresh.OriginLockPosition, charged.OriginLockPosition);
        Assert.Equal(fresh.LineageId, charged.LineageId);
        Assert.Null(charged.LastAcceptedFrameSha256);
        Assert.Equal(1, charged.AttemptsUsed);
        Assert.Equal(21.925484400443796, charged.CumulativeCommandedPixels);
        Assert.False(budget.HasRemainingBudget(budget.Phd2.StartedUtc.AddSeconds(300)));
    }

    [Fact]
    public async Task RecordedHd218393ImmutableLedgerRetainsExposureCreditAndExplainsExhaustedResponse()
    {
        var observations = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(observations)) return;
        var run = Path.Combine(observations, "UVEX-20261021T155003Z-90031e12848644f");
        var path = Assert.Single(Directory.GetFiles(Path.Combine(run, "control"), "phd2-lock-shift-pending.json.terminal-*.archive"));
        var original = await File.ReadAllBytesAsync(path);
        var loaded = await Phd2LockShiftPendingStore.LoadAsync(path, default);
        Assert.Null(loaded.Error);
        var state = loaded.State!;
        Assert.Equal(614.4, state.ExcludedAtrExposureSeconds, 6);
        Assert.Equal(1, state.AttemptsUsed);
        Assert.Equal(21.925484400443796, state.CumulativeCommandedPixels);
        Assert.Equal(DateTimeOffset.Parse("2026-10-21T16:07:48.6574658Z"), state.MotionClockStartedUtc);
        var atFirstFreshField = DateTimeOffset.Parse("2026-10-21T16:11:56.1048656Z");
        var radius = Phd2CoarseHandoffPolicy.LimitToRemainingLedger(44, ClockHandoffLimits(), .5, 2,
            state.AttemptsUsed, state.CumulativeCommandedPixels, atFirstFreshField - state.MotionClockStartedUtc);
        Assert.Equal(2, radius);
        // The existing original near-response retry remains available before
        // it is consumed; do not use fine capacity as an early G3 veto.
        Assert.True(G3WcsRecoveryPolicy.CanRetryNearTargetResponse(true, true, true, true,
            12.306604471080362, 18.350953572757913, 1.845979091810387, 2.657638010605546, .383, 2, 100, 0));
        Assert.False(G3WcsRecoveryPolicy.CanRetryNearTargetResponse(true, true, true, true,
            18.350953572757913, 15.497077265677957, 2.657638010605546, 1.7728290026522975, .383, 2, 100, 1));
        var gate = G3WcsRecoveryPolicy.ResolvedResponseFailure(18.350953572757913, 15.497077265677957, 1, radius, 2);
        Assert.Equal(G3WcsRecoveryPolicy.ResponseNotConvergedCode, gate.Code);
        Assert.Equal(GateFailureScope.TargetQuality, gate.FailureScope);
        Assert.Equal(1, gate.Metrics!["fineBudgetUnavailable"]);
        Assert.Equal(SHA256.HashData(original), SHA256.HashData(await File.ReadAllBytesAsync(path)));
    }

    private static Phd2LockShiftLimits ClockHandoffLimits() => new(
        25, 100, 8, TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), 1, 1, 2, 100, 5, 5, 3, 4, 2, 10, 100000);

    private sealed class ClockHandoffFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "uvex-clock-handoff-tests", Guid.NewGuid().ToString("N"));
        internal DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
        internal Phd2LockShiftPendingState Credited { get; }
        internal ClockContinuationBudget Budget { get; }
        internal const string ActionHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        internal const string PresetHash = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        internal const string ContextHash = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";

        internal ClockHandoffFixture()
        {
            var start = Now.AddSeconds(-742.4);
            var ledger = new Phd2LockShiftPendingState(3, "first-segment", new('a', 32), ActionHash, PresetHash, ContextHash,
                "policy", new('D', 64), new('E', 64), Phd2SlitGuideMode.OffSlitGuideStar, 1, 75,
                1492.65, 927.53, 1494.52, 949.38, 1494.52, 949.38, 25, 100, 8, 300,
                21.925484400443796, 1, start, start, start.AddSeconds(13),
                Phd2LockShiftPendingPhase.SettledBudgetLedger, new('F', 64), "fresh-residual.fit", null, "warning-probe",
                814.35, 408.13, 815.15, 431.01);
            var opened = start.AddSeconds(50);
            foreach (var seconds in new[] { .1, .3, 1, 3, 10, 600 })
            {
                ledger = Phd2ScienceContinuationPolicy.CreditExposure(ledger, ledger, opened,
                    opened.AddSeconds(seconds), seconds, seconds)!;
                opened = opened.AddSeconds(seconds);
            }
            Credited = ledger;
            Budget = new(new(2, 1675.6780413616566, Now.AddSeconds(-405), 8, 21600, 1200),
                new(ledger.AttemptsUsed, ledger.CumulativeCommandedPixels, ledger.MotionClockStartedUtc, 8, 100, 300),
                5400, 18000, Now.AddSeconds(-447), 1200, 2, .46546612260046016, 6, 14, 0, 0);
            Directory.CreateDirectory(Root);
        }

        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }

    // Real native containers, production response failure and production
    // terminal retirement; only device reads and elapsed reacquisition time
    // are finite test ports. No hardware, stored user sequence or raw FITS.
    private sealed class ClockHandoffBoundaryRunner(string mode, int segment, ClockHandoffFixture fixture,
        List<string> calls, ObservationCoordinatorHost host, UvexNightSequenceContainer night,
        CancellationTokenSource cancellation) : ObservationStageRunnerBase, INativeTargetBoundary,
        IObservationRunProvenanceSource, IObservationTerminalGuideCleanup
    {
        internal NativeTargetSession? Session;
        internal NativeSequencePlan? Remaining;
        internal string RunId = "";
        internal GateResult? CapacityGate;
        internal string LedgerPath => Path.Combine(fixture.Root, RunId, "control",
            segment == 0 ? "phd2-lock-shift-pending.json" : "g3-acquisition-motion.json");
        private byte[] originalLedger = [];
        private bool retired;
        public ObservationRunLockedMetadata LockedMetadata { get; } = new(
            CommissioningPresetSha256: ClockHandoffFixture.PresetHash,
            AdditionalHashes: new Dictionary<string, string> { ["actionConfigurationSha256"] = ClockHandoffFixture.ActionHash },
            Labels: new Dictionary<string, string> { ["adapter"] = "simulator", ["telescopeId"] = "mount", ["g3CameraId"] = "guide" });
        public void AttachNativeTarget(NativeTargetSession? session) { Session = session; Remaining = session!.RemainingPlan(); }
        public void RecordMeridianSegment(NativeTargetSession session, string runId)
        {
            calls.Add($"segment-{segment}-joined");
            session.RecordSegment(runId, segment == 0 || mode == "already-on-slit" ? 1 : 0,
                segment == 0 || mode == "already-on-slit" ? 1 : 0, segment == 0 ? 1 : 0,
                segment == 0 || mode == "already-on-slit" ? 600 : 0);
        }
        public ClockContinuationBudget CaptureClockContinuationBudget() => fixture.Budget;
        public Task<IReadOnlyList<string>> ConfirmSequenceIdleAsync(CancellationToken token, bool requireSettledMotion = true)
        {
            token.ThrowIfCancellationRequested();
            calls.Add($"segment-{segment}-stop-checked");
            return Task.FromResult<IReadOnlyList<string>>(retired ? [] : ["Test device owner or guide retirement remains unconfirmed."]);
        }
        public async Task<GateResult> RecoverMountClockAtIdleBoundaryAsync(ObservationPlan failedPlan, CancellationToken token)
        {
            Assert.True(retired);
            Assert.Contains("ClockRecoveryIntent", await File.ReadAllTextAsync(night.JournalPath, token));
            calls.Add("clock-repair");
            return mode == "clock-not-repaired"
                ? GateResult.Unknown("MOUNT_CLOCK_RECOVERY_POSTCHECK_FAILED", "Synthetic UTC still differs by two days.")
                : GateResult.Pass("MOUNT_CLOCK_RECOVERED_BY_NINA_RECONNECT", "Consecutive finite UTC checks passed; no hardware.");
        }
        public override async Task<StageResult> ExecuteStageAsync(ObservationStage stage, ObservationContext context, CancellationToken token)
        {
            calls.Add($"segment-{segment}-{stage}");
            if (stage == ObservationStage.ValidateNightSetup)
            {
                RunId = context.Plan.ObservationRunId;
                if (segment == 0)
                {
                    var state = fixture.Credited with { ObservationRunId = RunId, LineageId = Guid.NewGuid().ToString("N") };
                    await Phd2LockShiftPendingStore.WriteAtomicAsync(LedgerPath, state, token);
                }
                else
                {
                    // A new pointing/frame lineage carries only numeric debt.
                    // The old exact lock and its source frame are not imported.
                    var now = DateTimeOffset.UtcNow;
                    var fresh = new G3AcquisitionMotionState(2, G3AcquisitionMotionState.CurrentTangentProjectionId,
                        RunId, Guid.NewGuid().ToString("N"), ClockHandoffFixture.ActionHash, ClockHandoffFixture.ContextHash,
                        ClockHandoffFixture.PresetHash, G3AcquisitionMotionKind.WcsCentering, G3AcquisitionMotionPhase.SettledBudgetLedger,
                        "pierEast", "JNOW", 346.8, 50.2, 346.8, 50.2, 346.8, 50.2, 0, 0, 0,
                        5400, 18000, 21600, 8, 2, 90, 1200, 0, 0, now, now, now, "fresh-segment-wcs.json");
                    await G3AcquisitionMotionStore.WriteAtomicAsync(LedgerPath, Session!.ClockBudget!.Charge(fresh), token);
                }
                originalLedger = await File.ReadAllBytesAsync(LedgerPath, token);
            }
            if (segment == 0 && stage == ObservationStage.RunScienceBlock)
                return new(GateResult.Unknown("MOUNT_CLOCK_RECHECK_REQUIRED", "Recorded two-day failure after one retained 600-second frame."));
            if (segment == 1 && stage == ObservationStage.AcquireG3SlitField)
            {
                var debit = Session!.ClockBudget!.Phd2!;
                var radius = Phd2CoarseHandoffPolicy.LimitToRemainingLedger(44, ClockHandoffLimits(), .5, 2,
                    debit.Attempts, debit.Distance, TimeSpan.FromSeconds(247.4474));
                var measuredResidual = mode == "already-on-slit" ? 1.2 : 15.497077265677957 + 1.7728290026522975;
                if (G3WcsRecoveryPolicy.RecheckExistingCoarseHandoffBeforeImprovement(
                    GateResult.Pass("G3_CATALOG_SHORT_POSITION_CONFIRMED", "Fresh independently measured pair."), true, measuredResidual, radius))
                    calls.Add("same-target-readonly-verification");
                else
                {
                    Assert.False(G3WcsRecoveryPolicy.HasMeasuredApproachProgress(7.029115285382087, 15.497077265677957 * .383, 2));
                    Assert.False(G3WcsRecoveryPolicy.CanRetryNearTargetResponse(true, true, true, true,
                        18.350953572757913, 15.497077265677957, 2.657638010605546, 1.7728290026522975, .383, 2, 100, 1));
                    calls.Add("existing-near-response-retry-exhausted");
                    if (mode == "return-unconfirmed")
                        CapacityGate = G3WcsRecoveryPolicy.UnreturnedCenteringGate(
                            GateResult.Unknown("G3_MOTION_RETURN_STOP_UNCONFIRMED", "Finite mount port did not confirm the return."),
                            "Return unresolved; no target-quality bypass.", new Dictionary<string, double>());
                    else
                    {
                        calls.Add("g3-return-verified");
                        CapacityGate = G3WcsRecoveryPolicy.ResolvedResponseFailure(
                            18.350953572757913, 15.497077265677957, 1, radius, 2);
                    }
                }
                if (mode == "night-deadline")
                {
                    var field = typeof(UvexNightSequenceContainer).GetField("standbyPlan", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    var plan = (NativeSequencePlan)field.GetValue(night)!;
                    field.SetValue(night, plan with { DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(-1) });
                }
                if (mode == "unsafe") night.OnNightUnsafe();
                if (mode == "cancel") cancellation.Cancel();
                if (CapacityGate is not null) return new(CapacityGate);
            }
            return new(GateResult.Pass("SIM_FRESH_STAGE", "Finite read-only test ports; no equipment commands."));
        }
        public async Task CleanupTerminalGuideStateAsync(string runId, CancellationToken token)
        {
            calls.Add($"segment-{segment}-terminal-committed");
            var source = host.Dashboard.ManifestPath!;
            var sourceBytes = await File.ReadAllBytesAsync(source, token);
            var sourceHash = SHA256.HashData(sourceBytes);
            var manifest = await new ObservationRunJournalStore(source).ReadAsync(token);
            Assert.Equal(runId, manifest!.ObservationRunId);
            Assert.True(manifest.TerminalState is ObservationRunState.Completed or ObservationRunState.Faulted or ObservationRunState.Cancelled);
            var copy = Path.Combine(fixture.Root, runId, "manifest.json");
            await File.WriteAllBytesAsync(copy, sourceBytes, token);
            if (segment == 1 && mode == "retirement-corrupt") await File.AppendAllTextAsync(LedgerPath, "corrupt", token);
            var archives = await GuideStateBoundaryRetirement.RetireAsync(fixture.Root, runId, _ =>
            {
                calls.Add($"segment-{segment}-independent-owner-stop");
                var stopped = !(segment == 0 && mode == "first-stop-unknown" || segment == 1 && mode == "continuation-stop-unknown");
                return Task.FromResult(new GuideBoundaryWitness(stopped, true, stopped, stopped,
                    "mount", "guide", stopped ? Phd2AppState.Stopped : Phd2AppState.Guiding, true, DateTimeOffset.UtcNow));
            }, token);
            var archive = Assert.Single(archives);
            Assert.Equal(originalLedger, await File.ReadAllBytesAsync(archive, token));
            Assert.False(File.Exists(LedgerPath));
            Assert.Equal(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(source, token)));
            using var audit = JsonDocument.Parse(await File.ReadAllTextAsync(archive + ".audit.json", token));
            foreach (var property in new[] { "returnVerified", "targetOnSlitVerified", "motionSent", "countersReset", "calibrationChanged" })
                Assert.False(audit.RootElement.GetProperty(property).GetBoolean());
            retired = true;
            calls.Add($"segment-{segment}-retired");
        }
    }
}
