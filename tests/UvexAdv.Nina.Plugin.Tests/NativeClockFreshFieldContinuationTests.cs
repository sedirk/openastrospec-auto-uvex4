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
    [Fact]
    public async Task ActualUnjoinedNativeClockReconnectCannotIssueFreshFieldAuthorityOrHandOffToNextTarget()
    {
        InitializeAstrometry();
        var settings = Settings();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? Proxy<IAstrometrySettings>() : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        using var fixture = new ClockHandoffFixture();
        using var cancel = new CancellationTokenSource();
        var night = new UvexNightSequenceContainer(profiles, host, null!, settings)
        { StopAtDawn = false, HoldOpenAfterTargets = true, EndAtIso8601 = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O") };
        var trace = new List<string>();
        var failed = new ClockFreshFieldNativeRunner("reconnect-unjoined", 0, fixture, host, night, cancel, trace);
        var next = new TerminalGateRunner(null, false);
        var created = 0;
        night.Add(FocusTestTarget(profiles, host, settings, "HD 218393", () => { created++; return failed; }));
        night.Add(FocusTestTarget(profiles, host, settings, "Must not run", () => next));
        try
        {
            var error = await Record.ExceptionAsync(() => night.Execute(new Progress<ApplicationStatus>(), cancel.Token)
                .WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.IsNotType<TimeoutException>(error);
            Assert.Equal(1, created);
            Assert.Empty(next.Stages);
            Assert.Null(failed.Session!.ClockPositionCheck);
            Assert.False(failed.Session.ClockContinuationUsed);
            Assert.Equal(1, failed.Session.Accepted);
            Assert.Equal(600, failed.Session.AcceptedSeconds);
            Assert.True(failed.ReconnectBoundary.HasUnjoinedOperation);
            Assert.Same(failed.ReconnectOwner.Task, failed.ReconnectBoundary.PendingOperation);
            Assert.Equal("MOUNT_CLOCK_OWNER_OPERATION_UNCONFIRMED", failed.ReconnectBoundary.BoundaryGate!.Code);
            Assert.Contains(await failed.ConfirmSequenceIdleAsync(default), x => x.Contains("MOUNT_CLOCK_OWNER_OPERATION_UNCONFIRMED", StringComparison.Ordinal));
            var guard = Assert.IsType<NightSafetyStandby>(host.SafetyStandby);
            failed.ReconnectOwner.TrySetResult(true);
            Assert.False(failed.ReconnectBoundary.HasUnjoinedOperation);
            Assert.NotNull(failed.ReconnectBoundary.BoundaryGate); // A late completion does not erase uncertainty.
            failed.ForceInstantaneousIdle = true;
            Assert.Empty(await failed.ConfirmSequenceIdleAsync(default));
            Assert.False(await guard.RecheckIdleAsync(false, string.Empty, default));
            var handedOff = false;
            Assert.False(guard.TryHandOver(false, string.Empty, () => handedOff = true, freshIdleConfirmed: true));
            Assert.False(handedOff);
            using var journal = JsonDocument.Parse(await File.ReadAllTextAsync(night.JournalPath));
            Assert.Empty(journal.RootElement.GetProperty("clockContinuations").EnumerateArray());
            Assert.Contains("MOUNT_CLOCK_OWNER_OPERATION_UNCONFIRMED", await File.ReadAllTextAsync(night.JournalPath));
            night.EndNightCommand.Execute(null);
            await guard.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { failed.ReconnectOwner.TrySetResult(true); }
    }

    [Theory]
    [InlineData("fresh-success", 1, 0, true, true)]
    [InlineData("optical-miss", 1, 1, true, true)]
    [InlineData("fallback-quality-failure", 1, 1, false, true)]
    [InlineData("missing-receipt", 0, 1, true, true)]
    [InlineData("used-receipt", 0, 1, true, true)]
    [InlineData("expired-receipt", 0, 1, true, true)]
    [InlineData("pointing-changed", 0, 1, true, true)]
    [InlineData("pier-changed", 0, 1, true, true)]
    [InlineData("owner-changed", 0, 0, false, false)]
    [InlineData("stop-unconfirmed", 0, 0, false, false)]
    [InlineData("unsafe", 0, 0, false, false)]
    [InlineData("cancel", 0, 0, false, false)]
    [InlineData("night-deadline", 0, 0, false, false)]
    [InlineData("target-deadline", 0, 0, false, true)]
    [InlineData("persist-failed", 0, 0, false, false)]
    [InlineData("capture-owner-changed", 1, 0, false, false)]
    [InlineData("capture-stop-unconfirmed", 1, 0, false, false)]
    [InlineData("frame-tampered", 1, 0, false, false)]
    [InlineData("pointing-changed-after-frame", 1, 0, false, false)]
    [InlineData("fallback-stop-unconfirmed", 1, 1, false, false)]
    public async Task ClockFreshFieldWorkflowRunsInsideActualNativeContinuationAndRetainsItsOriginalBudget(
        string mode, int probes, int canonicalAcquisitions, bool completed, bool nextRuns)
    {
        InitializeAstrometry();
        var settings = Settings();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? Proxy<IAstrometrySettings>() : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        using var fixture = new ClockHandoffFixture();
        using var cancel = new CancellationTokenSource();
        var night = new UvexNightSequenceContainer(profiles, host, null!, settings) { StopAtDawn = false };
        var trace = new List<string>();
        var runners = new List<ClockFreshFieldNativeRunner>();
        var next = new TerminalGateRunner(null, false);
        var target = FocusTestTarget(profiles, host, settings, "HD 218393", () =>
        {
            var runner = new ClockFreshFieldNativeRunner(mode, runners.Count, fixture, host, night, cancel, trace);
            runners.Add(runner);
            return runner;
        });
        // The shared UI fixture sets coordinates after its catalogue label;
        // the real editor correctly clears that stale label. Bind it only
        // after coordinates are final, as an actual catalogue selection does.
        target.CatalogId = "HD 218393";
        target.DurationMinutes = 35;
        night.Add(target);
        night.Add(FocusTestTarget(profiles, host, settings, "HD 224559", () =>
        {
            trace.Add("next-target");
            return next;
        }));

        var error = await Record.ExceptionAsync(() => night.Execute(new Progress<ApplicationStatus>(), cancel.Token)
            .WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.IsNotType<TimeoutException>(error);
        Assert.Equal(2, runners.Count);
        var continued = runners[1];
        var session = runners[0].Session!;
        Assert.Same(session, continued.Session);
        Assert.True(session.ClockContinuationUsed);
        Assert.False(session.FocusContinuationUsed);
        Assert.NotEqual(runners[0].RunId, continued.RunId);
        Assert.Equal(probes, trace.Count(x => x == "1-fresh-g3"));
        Assert.Equal(canonicalAcquisitions, trace.Count(x => x == "1-catalog-goto"));
        Assert.Equal(canonicalAcquisitions, trace.Count(x => x == "1-qhy-wide-field"));
        Assert.Equal(canonicalAcquisitions, trace.Count(x => x == "1-canonical-g3"));
        Assert.Equal(1, trace.Count(x => x == "clock-repair"));
        Assert.Equal(nextRuns, next.Stages.Count > 0);
        if (nextRuns) Assert.Equal(ObservationRunCoordinator.Stages, next.Stages);

        // A successful shortcut replaces only coarse prerequisites. The real
        // native markers and later fine/guiding/ATR stages still execute once.
        if (completed)
        {
            Assert.Equal(ObservationRunCoordinator.Stages, continued.Stages);
            foreach (var stage in new[] { ObservationStage.PlaceTargetOnSlit, ObservationStage.StartGuiding,
                ObservationStage.SelectAtrExposure, ObservationStage.RunScienceBlock, ObservationStage.FinalizeObservation })
                Assert.Single(trace, x => x == $"1-canonical-{stage}");
            Assert.NotNull(continued.AcceptedFreshFramePath);
            Assert.Contains(continued.RunId, continued.AcceptedFreshFramePath);
        }
        if (mode == "fresh-success")
        {
            Assert.Equal(1, trace.Count(x => x == "1-led-geometry"));
            Assert.True(trace.IndexOf("1-attempt-durable") < trace.IndexOf("1-fresh-g3"));
            Assert.Equal(continued.RunId, session.ClockPositionCheck!.ConsumedRunId);
            Assert.DoesNotContain("1-canonical-CoarseCenter", trace);
        }
        if (mode is "optical-miss" or "fallback-quality-failure" or "fallback-stop-unconfirmed")
        {
            Assert.True(trace.IndexOf("1-fresh-g3") < trace.IndexOf("1-catalog-goto"));
            Assert.Single(trace, x => x == "1-attempt-durable");
            Assert.True(session.ClockPositionCheck!.Attempted);
        }
        Assert.Equal(1, continued.Remaining!.ScienceFrames);
        Assert.Equal(3, continued.Remaining.MaximumAttempts);
        Assert.False(continued.Remaining.ForceGuideCalibration);
        Assert.Equal(session.TargetPlan!.PlannedStartUtc.AddMinutes(35), continued.Remaining.TargetDeadlineUtc);
        Assert.Same(fixture.Budget, session.ClockBudget);
        Assert.Equal(fixture.Credited.MotionClockStartedUtc, session.ClockBudget!.Phd2!.StartedUtc);
        Assert.Equal(614.4, fixture.Credited.ExcludedAtrExposureSeconds, 6);
        Assert.Equal(1, session.ClockBudget.Phd2.Attempts);
        Assert.Equal(21.925484400443796, session.ClockBudget.Phd2.Distance);
        Assert.Equal(300, session.ClockBudget.Phd2.MaximumSeconds);
        Assert.Equal(2, session.ClockBudget.G3!.Attempts);
        Assert.Equal(1675.6780413616566, session.ClockBudget.G3.Distance);
        Assert.Equal(6, session.ClockBudget.ProbeAttempts);
        Assert.Equal(14, session.ClockBudget.MaximumProbeAttempts);
        Assert.Equal(completed ? 2 : 1, session.Accepted);
        Assert.Equal(completed ? 2 : 1, session.Attempts);
        Assert.Equal(1, session.ReusedProbes);
        Assert.Equal(completed ? 1200 : 600, session.AcceptedSeconds);
        Assert.True(trace.IndexOf("0-retired") < trace.IndexOf("clock-repair"));
        Assert.True(trace.IndexOf("clock-repair") < trace.IndexOf("1-ValidateNightSetup"));
        if (nextRuns) Assert.True(trace.IndexOf("1-retired") < trace.IndexOf("next-target"));
        if (mode == "fallback-stop-unconfirmed")
        {
            Assert.True(File.Exists(continued.LedgerPath));
            Assert.DoesNotContain("1-retired", trace);
        }
        using var journal = JsonDocument.Parse(await File.ReadAllTextAsync(night.JournalPath));
        Assert.Single(journal.RootElement.GetProperty("clockContinuations").EnumerateArray());
        var outcomes = journal.RootElement.GetProperty("outcomes").EnumerateArray()
            .Where(x => x.TryGetProperty("target", out _)).ToArray();
        Assert.Equal(nextRuns ? 2 : 1, outcomes.Length);
        Assert.Equal(completed ? "Completed" : nextRuns ? "SkippedQualityFailure" : "Stopped",
            outcomes[0].GetProperty("outcome").GetString());
        var retained = outcomes[0].GetProperty("retainedScience");
        Assert.Equal(completed ? 2 : 1, retained.GetProperty("acceptedFrames").GetInt32());
        Assert.Equal(completed ? 1200 : 600, retained.GetProperty("acceptedExposureSeconds").GetDouble());
        Assert.Equal(1, retained.GetProperty("reusedProbeFrames").GetInt32());
        if (nextRuns) Assert.Equal("Completed", outcomes[1].GetProperty("outcome").GetString());
        if (host.SafetyStandby is { } guard)
        {
            night.EndNightCommand.Execute(null);
            await guard.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    // Actual native containers + shared production shortcut dispatcher + real
    // immutable mount-binding/terminal-retirement policies. Only finite device
    // ports are substituted; no hardware client, stored sequence or raw FITS.
    private sealed class ClockFreshFieldNativeRunner(string mode, int segment, ClockHandoffFixture fixture,
        ObservationCoordinatorHost host, UvexNightSequenceContainer night, CancellationTokenSource cancel, List<string> trace)
        : ObservationStageRunnerBase, INativeTargetBoundary, IClockRecoveryFieldPort,
            IObservationRunProvenanceSource, IObservationTerminalGuideCleanup
    {
        internal NativeTargetSession? Session;
        internal NativeSequencePlan? Remaining;
        internal string RunId = "";
        internal string? AcceptedFreshFramePath;
        internal List<ObservationStage> Stages { get; } = [];
        internal MountClockReconnectBoundary ReconnectBoundary { get; } = new(TimeSpan.FromMilliseconds(20));
        internal TaskCompletionSource<bool> ReconnectOwner { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool ForceInstantaneousIdle;
        internal string LedgerPath => Path.Combine(fixture.Root, RunId, "control",
            segment == 0 ? "phd2-lock-shift-pending.json" : "g3-acquisition-motion.json");
        private ClockRecoveryFieldWorkflow? workflow;
        private ClockRecoveryPositionIdentity? identity;
        private G3FieldMountBinding? freshBinding;
        private byte[] ledgerBytes = [];
        private bool retired;
        private bool scienceCompleted;
        public ObservationRunLockedMetadata LockedMetadata { get; } = new(
            CommissioningPresetSha256: ClockHandoffFixture.PresetHash,
            AdditionalHashes: new Dictionary<string, string> { ["actionConfigurationSha256"] = ClockHandoffFixture.ActionHash },
            Labels: new Dictionary<string, string> { ["adapter"] = "simulator", ["telescopeId"] = "mount", ["g3CameraId"] = "guide" });

        public void AttachNativeTarget(NativeTargetSession? session)
        {
            Session = session;
            Remaining = session!.RemainingPlan();
            session.SharedActionConfigurationSha256 ??= ClockHandoffFixture.ActionHash;
            identity = new(session.TargetPlan!.Target, session.SharedActionConfigurationSha256, ClockHandoffFixture.PresetHash,
                "mount", Guid.Parse("09d640de-75f2-43ba-8ca2-5b93ef805a4b"), "finite-phd2-owner", 1, "guide-profile", "guide");
            workflow = new(segment == 1 ? session.ClockPositionCheck : null, this);
        }

        public void RecordMeridianSegment(NativeTargetSession session, string runId) =>
            session.RecordSegment(runId, segment == 0 || scienceCompleted ? 1 : 0,
                segment == 0 || scienceCompleted ? 1 : 0, segment == 0 ? 1 : 0, segment == 0 || scienceCompleted ? 600 : 0);
        public ClockContinuationBudget CaptureClockContinuationBudget() => fixture.Budget;
        public bool CanRecheckSequenceIdle => retired && ReconnectBoundary.BoundaryGate is null;
        public Task<IReadOnlyList<string>> ConfirmSequenceIdleAsync(CancellationToken token, bool requireSettledMotion = true)
        {
            token.ThrowIfCancellationRequested();
            if (ForceInstantaneousIdle) return Task.FromResult<IReadOnlyList<string>>([]);
            if (ReconnectBoundary.BoundaryGate is { } pending)
                return Task.FromResult<IReadOnlyList<string>>([$"{pending.Code}: {pending.Message}"]);
            return Task.FromResult<IReadOnlyList<string>>(retired ? [] : ["Finite owner stop/retirement remains unresolved."]);
        }

        public async Task<GateResult> RecoverMountClockAtIdleBoundaryAsync(ObservationPlan failedPlan, CancellationToken token)
        {
            Assert.True(retired);
            Assert.Contains("ClockRecoveryIntent", await File.ReadAllTextAsync(night.JournalPath, token));
            trace.Add("clock-repair");
            if (mode == "reconnect-unjoined")
            {
                await Assert.ThrowsAsync<TimeoutException>(() => ReconnectBoundary.RunAsync("Connect", () => ReconnectOwner.Task,
                    TimeSpan.FromMilliseconds(20), token));
                return ReconnectBoundary.BoundaryGate!;
            }
            var repair = GateResult.Pass("MOUNT_CLOCK_RECOVERED_BY_NINA_RECONNECT", "Finite consecutive repaired UTC readbacks.");
            var now = DateTimeOffset.UtcNow;
            var issued = mode == "expired-receipt" ? now.AddSeconds(-10) : now;
            var before = Snapshot(issued.AddSeconds(-1));
            var after = Snapshot(issued);
            Session!.ClockPositionCheck = mode == "missing-receipt" ? null : ClockRecoveryPositionCheck.TryCreate(
                RunId, repair, before, after, issued,
                mode == "expired-receipt" ? now.AddSeconds(-1) : Session.RecoveryDeadline(fixture.Budget), 2, out _);
            if (mode != "missing-receipt") Assert.NotNull(Session.ClockPositionCheck);
            if (mode == "used-receipt") Assert.True(Session.ClockPositionCheck!.TryConsume("already-attempted-segment", Snapshot(now), now, out _));
            return repair;
        }

        public override async Task<StageResult> ExecuteStageAsync(ObservationStage stage, ObservationContext context, CancellationToken token)
        {
            Stages.Add(stage);
            trace.Add($"{segment}-{stage}");
            if (stage == ObservationStage.ValidateNightSetup)
            {
                RunId = context.Plan.ObservationRunId;
                await PrepareLedgerAsync(token);
            }
            var substituted = await workflow!.TryExecuteAsync(stage, context, token);
            if (substituted is not null) return substituted;
            if (segment == 1 && stage == ObservationStage.SlewToCatalogTarget)
            {
                // Receipt rejection cannot launder a failed ordinary safety
                // boundary into permission to GOTO. Normal route checks again.
                var gate = await ValidateStoppedBoundaryAsync(context, token);
                if (gate.Disposition != GateDisposition.Passed) return new(gate);
            }
            trace.Add($"{segment}-canonical-{stage}");
            switch (stage)
            {
                case ObservationStage.SlewToCatalogTarget: trace.Add($"{segment}-catalog-goto"); break;
                case ObservationStage.AcquireQhyWideField: trace.Add($"{segment}-qhy-wide-field"); break;
                case ObservationStage.AcquireG3SlitField:
                    trace.Add($"{segment}-canonical-g3");
                    if (segment == 1 && mode is ("fallback-quality-failure" or "fallback-stop-unconfirmed"))
                        return new(G3WcsRecoveryPolicy.ResolvedResponseFailure(18.35, 15.50, 1, 2, 2));
                    await NewSyntheticFieldAsync(context, token);
                    break;
                case ObservationStage.PlaceTargetOnSlit:
                    Assert.NotNull(AcceptedFreshFramePath);
                    Assert.Contains(RunId, AcceptedFreshFramePath);
                    break;
                case ObservationStage.RunScienceBlock:
                    if (segment == 0) return new(GateResult.Unknown("MOUNT_CLOCK_RECHECK_REQUIRED", "Recorded failure after one accepted 600-second frame."));
                    scienceCompleted = true;
                    break;
            }
            return new(GateResult.Pass("FINITE_CANONICAL_STAGE", "Shared canonical marker executed through finite owner ports."));
        }

        public ClockRecoveryPositionSnapshot ReadSnapshot()
        {
            var value = Snapshot(DateTimeOffset.UtcNow);
            if (mode == "pointing-changed") value = value with { Mount = value.Mount with { RightAscensionDegrees = 1 } };
            if (mode == "pier-changed") value = value with { Mount = value.Mount with { PierSide = "pierWest" } };
            if (mode == "owner-changed") value = value with { Identity = value.Identity with { G3StableCameraId = "replacement-guide" } };
            return value;
        }

        private ClockRecoveryPositionSnapshot Snapshot(DateTimeOffset at) => new(identity!, new(0, 89, "J2000", "pierEast", at), true, true, true, true);

        public Task<GateResult> ValidateStoppedBoundaryAsync(ObservationContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            trace.Add($"{segment}-live-boundary");
            if (segment == 1 && mode == "cancel") { cancel.Cancel(); token.ThrowIfCancellationRequested(); }
            if (segment == 1 && mode == "unsafe") night.OnNightUnsafe();
            if (segment == 1 && mode == "night-deadline")
            {
                var field = typeof(UvexNightSequenceContainer).GetField("standbyPlan", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var plan = (NativeSequencePlan)field.GetValue(night)!;
                field.SetValue(night, plan with { DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(-1) });
            }
            if (segment == 1 && mode == "target-deadline")
                return Task.FromResult((Remaining! with { TargetDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(-1) }).StopGateNow()!);
            var code = segment != 1 ? null : mode switch
            {
                "owner-changed" => "PHD2_ADAPTIVE_CAPTURE_OWNER_CHANGED",
                "stop-unconfirmed" => "PHD2_STOP_UNCONFIRMED",
                "unsafe" => "SAFETY_UNSAFE",
                "night-deadline" => "NIGHT_DEADLINE_REACHED",
                _ => null,
            };
            return Task.FromResult(code is null ? GateResult.Pass("FINITE_STOPPED", "All finite owners joined and stopped.") : GateResult.Unknown(code, "Finite hard boundary failure."));
        }

        public async Task PersistAttemptAsync(ClockRecoveryPositionCheck receipt, ObservationContext context, CancellationToken token)
        {
            Assert.True(receipt.Attempted);
            Assert.Equal(context.Plan.ObservationRunId, receipt.ConsumedRunId);
            if (mode == "persist-failed") throw new IOException("Finite durable intent write failed.");
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, RunId, "clock-current-field-attempt.json"),
                JsonSerializer.Serialize(new { receipt.SourceRunId, receipt.ConsumedRunId, receipt.OriginalRecoveryDeadlineUtc }), token);
            trace.Add($"{segment}-attempt-durable");
        }

        public async Task<ClockRecoveryFieldProbeResult> CaptureFreshAsync(ObservationContext context, CancellationToken token)
        {
            Assert.True(File.Exists(Path.Combine(fixture.Root, RunId, "clock-current-field-attempt.json")));
            trace.Add($"{segment}-fresh-g3");
            if (mode is "capture-owner-changed" or "capture-stop-unconfirmed")
                return new(new(GateResult.Unknown(mode == "capture-owner-changed" ? "PHD2_ADAPTIVE_CAPTURE_OWNER_CHANGED" : "PHD2_STOP_UNCONFIRMED", "Finite capture did not establish a safe completed owner.")), false);
            await NewSyntheticFieldAsync(context, token);
            if (mode is "optical-miss" or "fallback-quality-failure" or "fallback-stop-unconfirmed")
                return new(new(GateResult.TargetQuality("G3_PLATE_SOLVE_FAILED", "Finite optical miss; original route may try once."), AcceptedFreshFramePath), true);
            return new(new(GateResult.Pass("G3_CURRENT_FIELD_CONFIRMED", "Synthetic new-segment target/slit and immutable mount binding."), AcceptedFreshFramePath), false);
        }

        public async Task<GateResult> ValidateFreshFieldAsync(ObservationContext context, CancellationToken token)
        {
            Assert.NotNull(freshBinding);
            var frame = AcceptedFreshFramePath!;
            if (mode == "frame-tampered") await File.AppendAllTextAsync(frame, "mutated test-only input", token);
            var current = Snapshot(DateTimeOffset.UtcNow).Mount;
            if (mode == "pointing-changed-after-frame") current = current with { RightAscensionDegrees = 1 };
            trace.Add($"{segment}-validate-fresh-field");
            return G3FieldMountBindingPolicy.ValidateForMotion(freshBinding, RunId, ClockHandoffFixture.ActionHash,
                ClockHandoffFixture.PresetHash, frame, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(frame, token))),
                current.RightAscensionDegrees, current.DeclinationDegrees, current.CoordinateEpoch, current.PierSide, 2);
        }

        public Task RecordDecisionAsync(string decision, StageResult? result, ObservationContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            trace.Add($"{segment}-decision-{decision}");
            return Task.CompletedTask;
        }

        private async Task NewSyntheticFieldAsync(ObservationContext context, CancellationToken token)
        {
            // Deliberately not FITS: these generated bytes represent a finite
            // owner response, never historical observations or actual stars.
            AcceptedFreshFramePath = Path.Combine(fixture.Root, RunId, Guid.NewGuid().ToString("N") + ".synthetic-frame");
            await File.WriteAllTextAsync(AcceptedFreshFramePath, "new segment " + context.Plan.ObservationRunId, token);
            var completedAt = DateTimeOffset.UtcNow;
            freshBinding = G3FieldMountBinding.Create(RunId, ClockHandoffFixture.ActionHash, ClockHandoffFixture.PresetHash,
                AcceptedFreshFramePath, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(AcceptedFreshFramePath, token))),
                completedAt, segment == 1 ? ReadSnapshot().Mount : Snapshot(DateTimeOffset.UtcNow).Mount);
            trace.Add($"{segment}-led-geometry");
        }

        private async Task PrepareLedgerAsync(CancellationToken token)
        {
            if (segment == 0)
                await Phd2LockShiftPendingStore.WriteAtomicAsync(LedgerPath, fixture.Credited with
                { ObservationRunId = RunId, LineageId = Guid.NewGuid().ToString("N") }, token);
            else
            {
                var now = DateTimeOffset.UtcNow;
                var fresh = new G3AcquisitionMotionState(2, G3AcquisitionMotionState.CurrentTangentProjectionId,
                    RunId, Guid.NewGuid().ToString("N"), ClockHandoffFixture.ActionHash, ClockHandoffFixture.ContextHash,
                    ClockHandoffFixture.PresetHash, G3AcquisitionMotionKind.WcsCentering, G3AcquisitionMotionPhase.SettledBudgetLedger,
                    "pierEast", "JNOW", 346.8, 50.2, 346.8, 50.2, 346.8, 50.2, 0, 0, 0,
                    5400, 18000, 21600, 8, 2, 90, 1200, 0, 0, now, now, now, "new-segment-wcs.json");
                await G3AcquisitionMotionStore.WriteAtomicAsync(LedgerPath, Session!.ClockBudget!.Charge(fresh), token);
            }
            ledgerBytes = await File.ReadAllBytesAsync(LedgerPath, token);
        }

        public async Task CleanupTerminalGuideStateAsync(string runId, CancellationToken token)
        {
            var source = host.Dashboard.ManifestPath!;
            var sourceBytes = await File.ReadAllBytesAsync(source, token);
            var sourceHash = SHA256.HashData(sourceBytes);
            var manifest = await new ObservationRunJournalStore(source).ReadAsync(token);
            Assert.Equal(runId, manifest!.ObservationRunId);
            Assert.True(manifest.TerminalState is ObservationRunState.Completed or ObservationRunState.Faulted or ObservationRunState.Cancelled);
            await File.WriteAllBytesAsync(Path.Combine(fixture.Root, runId, "manifest.json"), sourceBytes, token);
            var archives = await GuideStateBoundaryRetirement.RetireAsync(fixture.Root, runId, _ =>
            {
                var stopped = segment != 1 || mode != "fallback-stop-unconfirmed";
                trace.Add($"{segment}-independent-stop");
                return Task.FromResult(new GuideBoundaryWitness(stopped, true, stopped, stopped, "mount", "guide",
                    stopped ? Phd2AppState.Stopped : Phd2AppState.Guiding, true, DateTimeOffset.UtcNow));
            }, token);
            var archive = Assert.Single(archives);
            Assert.Equal(ledgerBytes, await File.ReadAllBytesAsync(archive, token));
            Assert.Equal(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(source, token)));
            using var audit = JsonDocument.Parse(await File.ReadAllTextAsync(archive + ".audit.json", token));
            foreach (var key in new[] { "returnVerified", "targetOnSlitVerified", "motionSent", "countersReset", "calibrationChanged" })
                Assert.False(audit.RootElement.GetProperty(key).GetBoolean());
            retired = true;
            trace.Add($"{segment}-retired");
        }
    }
}
