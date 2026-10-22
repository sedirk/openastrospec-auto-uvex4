using NINA.Astrometry.Interfaces;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using UvexAdv.Nina.Plugin.SequenceItems;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed partial class NativeSequencePlanTests
{
    [Theory]
    [InlineData("G3_MAIN_FOCUS_UNVERIFIED", true, true)]
    [InlineData("G3_MAIN_FOCUS_UNVERIFIED", false, false)]
    [InlineData("G3_CATALOG_SHORT_POSITION_UNCONFIRMED", true, true)]
    [InlineData("G3_POST_WCS_SHORT_UNCONFIRMED", true, true)]
    [InlineData("G3_POST_WCS_TARGET_NOT_MEASURED", true, true)]
    [InlineData("G3_BOUNDED_SEARCH_EXHAUSTED_RETURNED", true, true)]
    [InlineData("ATR_SAVED_FRAME_TEMPERATURE_INVALID", true, false)]
    [InlineData("MOUNT_CLOCK_RECHECK_REQUIRED", false, false)]
    [InlineData("PHD2_ADAPTIVE_CAPTURE_OWNER_CHANGED", false, false)]
    public void FocusRecoveryEligibilityIsExplicitImageQualityNotAnExceptionOrScopeWildcard(string code, bool quality, bool allowed)
    {
        var gate = quality ? GateResult.TargetQuality(code, "measured quality") : GateResult.Unknown(code, "ownership/position not established");
        Assert.Equal(allowed, UvexNightSequenceContainer.IsSevereDefocusCandidate(ObservationStage.AcquireG3SlitField, gate));
        Assert.Equal(allowed, UvexNightSequenceContainer.IsSevereDefocusCandidate(ObservationStage.PlaceTargetOnSlit, gate));
        Assert.False(UvexNightSequenceContainer.IsSevereDefocusCandidate(ObservationStage.SelectAtrExposure, gate));
        Assert.False(UvexNightSequenceContainer.IsSevereDefocusCandidate(ObservationStage.RunScienceBlock, gate));
    }

    [Fact]
    public void FocusContinuationKeepsOriginalElapsedBudgetsAndLaterMeridianDebitsWithoutUsingClockRepair()
    {
        var start = DateTimeOffset.UtcNow;
        var session = new NativeTargetSession(new(2, 4, 0, DeadlineUtc: start.AddMinutes(8)))
        {
            TargetPlan = new("first", "night", new("target", "SIM", 0, 89), new(33, 120, 0), start, TimeSpan.FromMinutes(5),
                new(), new(), "atr", "g3", "qhy"),
        };
        var budget = new ClockContinuationBudget(new(2, 30, start, 8, 100, 180),
            new(1, 10, start, 8, 100, 150), 40, 100, start, 120, 2, .1, 1, 8, 1, 1);
        session.RecordSegment("prior", 1, 2, 1, 120);
        Assert.Equal(start.AddSeconds(120), session.RecoveryDeadline(budget));
        Assert.True(session.TryBeginFocusRecovery(budget, start.AddSeconds(1)));
        Assert.False(session.TryBeginFocusRecovery(budget, start.AddSeconds(2)));
        Assert.True(session.TryContinueAfterFocusRepair(budget, start.AddSeconds(20)));
        Assert.False(session.ClockContinuationUsed);
        Assert.False(session.TryContinueAfterFocusRepair(budget, start.AddSeconds(21)));
        var afterFlip = budget with { G3 = budget.G3! with { Attempts = 3, Distance = 40 } };
        session.RetainClockBudget(afterFlip);
        Assert.Same(afterFlip, session.ClockBudget);
        Assert.Equal(start.AddMinutes(5), session.RemainingPlan().TargetDeadlineUtc);
        Assert.Equal(start.AddSeconds(120), session.RecoveryDeadline(afterFlip));
        Assert.False(session.CanRecoverWithinOriginalBudget(afterFlip, start.AddSeconds(121)));
        Assert.False(session.CanRecoverWithinOriginalBudget(afterFlip with { FineMaximumSeconds = double.PositiveInfinity }, start.AddSeconds(25)));
        Assert.True(session.TryContinueAfterClockRepair(afterFlip, start.AddSeconds(30)));
        Assert.Equal(1, session.Accepted);
        Assert.Equal(2, session.Attempts);
    }

    [Theory]
    [InlineData("success", true, true)]
    [InlineData("catalog-short", true, true)]
    [InlineData("focus-unverified", true, true)]
    [InlineData("focus-position-changed", false, false)]
    [InlineData("optical-fail", true, true)]
    [InlineData("not-defocused", false, true)]
    [InlineData("diagnostic-quality", false, true)]
    [InlineData("local-diagnostic-timeout", false, true)]
    [InlineData("local-scan-timeout", false, true)]
    [InlineData("repair-quality", false, true)]
    [InlineData("repair-hard", false, false)]
    [InlineData("exception", false, false)]
    [InlineData("stop-before", false, false)]
    [InlineData("stop-after", false, false)]
    [InlineData("unsafe-before", false, false)]
    [InlineData("unsafe-during", false, false)]
    [InlineData("unsafe-diagnosis", false, false)]
    [InlineData("cancel-before", false, false)]
    [InlineData("cancel-during", false, false)]
    [InlineData("cancel-diagnosis", false, false)]
    [InlineData("end-before", false, false)]
    [InlineData("end-during", false, false)]
    [InlineData("end-diagnosis", false, false)]
    [InlineData("deadline-before", false, false)]
    [InlineData("deadline-during", false, false)]
    [InlineData("budget-empty", false, true)]
    [InlineData("budget-after", false, true)]
    [InlineData("target-time-empty", false, true)]
    [InlineData("wrong-stage", false, true)]
    [InlineData("wrong-code", false, false)]
    [InlineData("end-policy", false, false)]
    [InlineData("missing-reservation", false, false)]
    [InlineData("double-reservation", true, true)]
    public async Task NativeFocusRecoveryUsesAFreshSameCardSegmentThenActuallyRunsNextTarget(
        string mode, bool continued, bool nextRuns)
    {
        InitializeAstrometry();
        var settings = Settings();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? Proxy<IAstrometrySettings>() : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        var night = new UvexNightSequenceContainer(profiles, host, null!, settings) { StopAtDawn = false };
        var calls = new List<string>();
        var runners = new List<FocusBoundaryRunner>();
        var next = new TerminalGateRunner(null, false);
        for (var i = 0; i < 2; i++)
        {
            var isFirst = i == 0;
            night.Add(FocusTestTarget(profiles, host, settings, isFirst ? "Focus target" : "Next target", () =>
            {
                if (!isFirst) { calls.Add("next-target"); return next; }
                var runner = new FocusBoundaryRunner(mode, runners.Count, calls, night);
                runners.Add(runner); return runner;
            }, mode == "end-policy" ? SpectroscopyTargetFailurePolicy.EndNight : SpectroscopyTargetFailurePolicy.SkipQualityFailure));
        }
        var error = await Record.ExceptionAsync(() => night.Execute(new Progress<ApplicationStatus>(), default).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.IsNotType<TimeoutException>(error);
        Assert.Equal(continued ? 2 : 1, runners.Count);
        Assert.Equal(nextRuns, next.Stages.Count > 0);
        if (nextRuns) Assert.Equal(ObservationRunCoordinator.Stages, next.Stages);
        var diagnosticExpected = mode is not ("stop-before" or "unsafe-before" or "cancel-before" or "end-before" or
            "deadline-before" or "budget-empty" or "target-time-empty" or "wrong-stage" or "wrong-code" or "focus-position-changed" or "end-policy");
        Assert.Equal(diagnosticExpected ? 1 : 0, calls.Count(x => x == "diagnose"));
        if (diagnosticExpected)
        {
            Assert.True(calls.IndexOf("joined-segment-0") < calls.IndexOf("diagnose"));
            Assert.True(calls.LastIndexOf("stopped") > calls.IndexOf("diagnose"));
        }
        if (mode.EndsWith("-diagnosis", StringComparison.Ordinal)) Assert.DoesNotContain("scan", calls);
        if (continued)
        {
            var session = runners[1].Session!;
            Assert.Same(runners[0].Session, session);
            Assert.True(session.FocusAttempted);
            Assert.True(session.FocusContinuationUsed);
            Assert.False(session.ClockContinuationUsed);
            Assert.NotEqual(runners[0].RunId, runners[1].RunId);
            Assert.Equal(1, runners[1].Remaining!.ScienceFrames);
            Assert.Equal(2, runners[1].Remaining!.MaximumAttempts);
            Assert.False(runners[1].Remaining!.ForceGuideCalibration);
            Assert.Equal(session.TargetPlan!.PlannedStartUtc + TimeSpan.FromMinutes(5), runners[1].Remaining!.TargetDeadlineUtc);
            Assert.Equal(2, session.ClockBudget!.G3!.Attempts);
            Assert.Equal(30, session.ClockBudget.G3.Distance);
            Assert.Equal(runners[0].Started, session.ClockBudget.G3.StartedUtc);
            Assert.True(calls.IndexOf("scan") < calls.IndexOf("segment-1-ValidateNightSetup"));
            Assert.True(calls.IndexOf("segment-1-AcquireG3SlitField") < calls.IndexOf("next-target"));
            if (mode != "optical-fail")
            {
                Assert.Contains("segment-1-PlaceTargetOnSlit", calls);
                Assert.Contains("segment-1-StartGuiding", calls);
                Assert.Contains("segment-1-RunScienceBlock", calls);
                Assert.Equal(2, session.Accepted);
                Assert.Equal(3, session.Attempts);
                Assert.Equal(1, session.ReusedProbes);
                Assert.Equal(240, session.AcceptedSeconds);
            }
            else Assert.Equal(1, session.Accepted);
        }
        using var journal = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(night.JournalPath));
        var outcomes = journal.RootElement.GetProperty("outcomes").EnumerateArray().Where(x => x.TryGetProperty("target", out _)).ToArray();
        Assert.Equal(nextRuns ? 2 : 1, outcomes.Length);
        Assert.Equal("Focus target", outcomes[0].GetProperty("target").GetString());
        Assert.Equal(continued && mode != "optical-fail" ? "Completed" : nextRuns ? "SkippedQualityFailure" : "Stopped",
            outcomes[0].GetProperty("outcome").GetString());
        Assert.Equal(0, journal.RootElement.GetProperty("clockContinuations").GetArrayLength());
        if (host.SafetyStandby is { } guard) { night.EndNightCommand.Execute(null); await guard.Completion.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyAnActualFocusScanUsesTheNightAllowanceAndFollowingTargetsStillExecute(bool firstDiagnosisRejects)
    {
        InitializeAstrometry();
        var settings = Settings();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? Proxy<IAstrometrySettings>() : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        var night = new UvexNightSequenceContainer(profiles, host, null!, settings) { StopAtDawn = false };
        var calls = new List<string>();
        var segments = new int[2];
        for (var i = 0; i < 2; i++)
        {
            var index = i;
            night.Add(FocusTestTarget(profiles, host, settings, $"Focus target {i}", () =>
                new FocusBoundaryRunner(index == 0 && firstDiagnosisRejects ? "not-defocused" : "success", segments[index]++, calls, night)));
        }
        var next = new TerminalGateRunner(null, false);
        night.Add(FocusTestTarget(profiles, host, settings, "Third target", () => next));
        await night.Execute(new Progress<ApplicationStatus>(), default).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(firstDiagnosisRejects ? new[] { 1, 2 } : new[] { 2, 1 }, segments);
        Assert.Equal(firstDiagnosisRejects ? 2 : 1, calls.Count(x => x == "diagnose"));
        Assert.Single(calls, x => x == "scan");
        Assert.Equal(ObservationRunCoordinator.Stages, next.Stages);
        using var journal = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(night.JournalPath));
        Assert.True(journal.RootElement.GetProperty("focusScanReserved").GetBoolean());
        var outcomes = journal.RootElement.GetProperty("outcomes").EnumerateArray().Where(x => x.TryGetProperty("target", out _)).ToArray();
        Assert.Equal(firstDiagnosisRejects ? new[] { "SkippedQualityFailure", "Completed", "Completed" } :
            new[] { "Completed", "SkippedQualityFailure", "Completed" }, outcomes.Select(x => x.GetProperty("outcome").GetString()));
    }

    private static UvexTargetObservationContainer FocusTestTarget(IProfileService profiles, ObservationCoordinatorHost host,
        UvexPluginSettings settings, string name, Func<IObservationStageRunner> runner,
        SpectroscopyTargetFailurePolicy policy = SpectroscopyTargetFailurePolicy.SkipQualityFailure) =>
        new(profiles, Proxy<INighttimeCalculator>(), host, null!, settings, runner)
        {
            TargetName = name, CatalogId = "SIM-FOCUS", NightSetupId = "SIM-NIGHT", UseRealMode = false,
            SiteLatitudeDegrees = 33, SiteLongitudeDegreesEast = 120, RightAscensionDegrees = 0, DeclinationDegrees = 89,
            DurationMinutes = 5, HorizonMinimumDegrees = 0, HorizonStartMarginDegrees = 0, HorizonContinueMarginDegrees = 0,
            ExpectedAtrCameraId = "SIM-ATR", ExpectedG3ProfileName = "SIM-G3", ExpectedQhyCameraId = "SIM-QHY",
            ScienceFrames = 2, MaximumScienceAttempts = 4, ForceGuideCalibration = true, FailurePolicy = policy,
        };

    [Fact]
    public async Task UnjoinedFocusResponsibilityCannotBeWashedAwayByAFreshInstantaneousIdleRead()
    {
        InitializeAstrometry();
        var settings = Settings();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? Proxy<IAstrometrySettings>() : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        var night = new UvexNightSequenceContainer(profiles, host, null!, settings)
        { StopAtDawn = false, HoldOpenAfterTargets = true, EndAtIso8601 = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O") };
        var failed = new FocusBoundaryRunner("unjoined", 0, [], night);
        var next = new TerminalGateRunner(null, false);
        night.Add(FocusTestTarget(profiles, host, settings, "Unjoined focus", () => failed));
        night.Add(FocusTestTarget(profiles, host, settings, "Must not run", () => next));
        var error = await Record.ExceptionAsync(() => night.Execute(new Progress<ApplicationStatus>(), default).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.IsNotType<TimeoutException>(error);
        Assert.Empty(next.Stages);
        var guard = Assert.IsType<NightSafetyStandby>(host.SafetyStandby);
        failed.FreshInstantaneousIdle = true;
        Assert.Empty(await failed.ConfirmSequenceIdleAsync(default));
        Assert.False(await guard.RecheckIdleAsync(false, string.Empty, default));
        var committed = false;
        Assert.False(guard.TryHandOver(false, string.Empty, () => committed = true, freshIdleConfirmed: true));
        Assert.False(committed);
        using var journal = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(night.JournalPath));
        Assert.False(journal.RootElement.GetProperty("outcomes").EnumerateArray().Last().GetProperty("targetBoundariesSettled").GetBoolean());
        Assert.Contains("AUTO_FOCUS_OWNER_UNJOINED", await File.ReadAllTextAsync(night.JournalPath));
        night.EndNightCommand.Execute(null);
        await guard.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private sealed class FocusBoundaryRunner(string mode, int segment, List<string> calls, UvexNightSequenceContainer night)
        : ObservationStageRunnerBase, INativeTargetBoundary
    {
        public NativeTargetSession? Session;
        public NativeSequencePlan? Remaining;
        public string? RunId;
        public DateTimeOffset Started = DateTimeOffset.UtcNow.AddSeconds(-10);
        public bool FreshInstantaneousIdle;
        private bool diagnosed;
        private bool boundaryChanged;
        public void AttachNativeTarget(NativeTargetSession? session)
        {
            Session = session;
            // An earlier completed science segment (e.g. before a meridian
            // boundary) remains immutable; recovery must not count it again.
            if (segment == 0) session!.RecordSegment("prior-accepted-segment", 1, 2, 1, 120);
            Remaining = session!.RemainingPlan();
        }
        public void RecordMeridianSegment(NativeTargetSession session, string runId)
        {
            calls.Add($"joined-segment-{segment}");
            var accepted = segment > 0 && mode != "optical-fail" ? 1 : 0;
            session.RecordSegment(runId, accepted, accepted, 0, accepted * 120);
        }
        public ClockContinuationBudget CaptureClockContinuationBudget() => new(
            new(2, 30, Started, 8, 100, mode == "budget-empty" || mode == "budget-after" && diagnosed ? 1 : 600), null, 60, 120, null, 1200, 0, 0, 1, 8, 0, 0);
        public async Task<IReadOnlyList<string>> ConfirmSequenceIdleAsync(CancellationToken token, bool requireSettledMotion = true)
        {
            calls.Add("stopped");
            if (!boundaryChanged && mode.EndsWith("-before", StringComparison.Ordinal))
            { boundaryChanged = true; await ChangeBoundaryAsync(mode); }
            if (mode == "target-time-empty") Session!.TargetPlan = Session.TargetPlan! with { PlannedStartUtc = DateTimeOffset.UtcNow.AddMinutes(-10) };
            if (mode == "unjoined" && diagnosed && !FreshInstantaneousIdle) return ["AUTO_FOCUS_OWNER_UNJOINED: task not joined; snapshot is not responsibility proof."];
            return mode == "stop-before" || mode == "stop-after" && diagnosed ? ["Synthetic unknown physical stop"] : [];
        }
        public Task<GateResult> RecoverMountClockAtIdleBoundaryAsync(ObservationPlan failedPlan, CancellationToken token) =>
            throw new InvalidOperationException("Focus must not trigger clock repair.");
        public async Task<GateResult?> TryRecoverSevereDefocusAtIdleBoundaryAsync(ObservationPlan failedPlan,
            Func<CancellationToken, Task<bool>> tryReserveScan, CancellationToken token)
        {
            Assert.Contains("stopped", calls);
            calls.Add("diagnose"); diagnosed = true;
            if (mode is "local-diagnostic-timeout" or "local-scan-timeout")
                return await RunActualTimedFocusAsync(mode == "local-scan-timeout", tryReserveScan, calls);
            if (mode == "not-defocused") return null;
            if (mode == "diagnostic-quality") return GateResult.TargetQuality("FOCUS_NO_STABLE_FIELD", "No motion; stopped.");
            if (mode == "missing-reservation") return GateResult.Pass("FOCUS_OK", "Invalid test producer.");
            if (mode.EndsWith("-diagnosis", StringComparison.Ordinal)) await ChangeBoundaryAsync(mode);
            if (!await tryReserveScan(token)) return null;
            using var journal = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(night.JournalPath, token));
            Assert.Equal("FocusRecoveryIntent", journal.RootElement.GetProperty("phase").GetString());
            Assert.True(journal.RootElement.GetProperty("focusScanReserved").GetBoolean());
            calls.Add("scan");
            if (mode == "double-reservation") Assert.False(await tryReserveScan(token));
            if (mode.EndsWith("-during", StringComparison.Ordinal)) await ChangeBoundaryAsync(mode);
            if (mode == "exception") throw new InvalidOperationException("Ambiguous simulated focus transport.");
            return mode switch
            {
                "repair-quality" => GateResult.TargetQuality("FOCUS_NOT_IMPROVED", "Restored position and stopped."),
                "repair-hard" => GateResult.Unknown("FOCUS_POSITION_UNKNOWN", "Cannot attest restored position."),
                "unjoined" => GateResult.Unknown("AUTO_FOCUS_OWNER_UNJOINED", "Operation task has not joined."),
                _ => GateResult.Pass("FOCUS_VERIFIED", "Fresh validated focus; no hardware used in test."),
            };
        }
        private async Task ChangeBoundaryAsync(string reason)
        {
            if (reason.StartsWith("unsafe", StringComparison.Ordinal)) night.OnNightUnsafe();
            if (reason.StartsWith("cancel", StringComparison.Ordinal)) await night.Interrupt();
            if (reason.StartsWith("end", StringComparison.Ordinal)) night.EndNightCommand.Execute(null);
            if (reason.StartsWith("deadline", StringComparison.Ordinal))
            {
                var field = typeof(UvexNightSequenceContainer).GetField("standbyPlan", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                field.SetValue(night, ((NativeSequencePlan)field.GetValue(night)!) with { DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(-1) });
            }
        }
        public override Task<StageResult> ExecuteStageAsync(ObservationStage stage, ObservationContext context, CancellationToken token)
        {
            RunId = context.Plan.ObservationRunId;
            calls.Add($"segment-{segment}-{stage}");
            var failureStage = mode == "wrong-stage" ? ObservationStage.SelectAtrExposure : ObservationStage.AcquireG3SlitField;
            return Task.FromResult(new StageResult((segment == 0 || mode == "optical-fail") && stage == failureStage
                ? mode == "wrong-code" ? GateResult.Unknown("PHD2_CAPTURE_OWNER_CHANGED", "Not an optical failure.")
                    : mode == "catalog-short" ? RealObservationStageRunner.CreateCatalogShortPositionFailure("G3_SEP_UNMEASURED: recorded catalogue exhaustion.")
                    : mode == "focus-position-changed" ? GateResult.Unknown("G3_MAIN_FOCUS_UNVERIFIED", "External focus position changed, not a quality assertion.")
                    : mode == "focus-unverified" ? GateResult.TargetQuality("G3_MAIN_FOCUS_UNVERIFIED", "Broad measured stars.")
                    : GateResult.TargetQuality("G3_POST_WCS_SHORT_UNCONFIRMED", "Synthetic repeated split stellar images.")
                : GateResult.Pass("SIM_STAGE", "No hardware.")));
        }
    }

    // Exercise the real automatic owner guard, shared focus runner and production
    // classifier before feeding the gate back through the native target boundary.
    // Only the lowest device ports are fake; no focus/CCD hardware is opened.
    private static async Task<GateResult?> RunActualTimedFocusAsync(bool scan, Func<CancellationToken, Task<bool>> reserve,
        List<string> calls)
    {
        var port = new TimedFocusPort(scan);
        var options = new SepMainFocusOptions { MinimumPosition = 500, MaximumPosition = 1500,
            Step = 25, HalfPoints = 2, Frames = 2, ExposureMilliseconds = 500, MaximumSeconds = 60 };
        var limits = new FocusMotionLimits(500, 1500, 150, 1000, FocusApproachDirection.None, 0);
        var stops = 0;
        var boundary = new AutomaticSepFocusBoundary(port, limits, options, _ => Task.CompletedTask,
            _ => { stops++; return Task.FromResult<IReadOnlyList<string>>([]); }, async ct =>
            { var accepted = await reserve(ct); if (accepted) calls.Add("scan"); return accepted; },
            DateTimeOffset.UtcNow.AddSeconds(scan ? 30 : .15), default);
        SepMainFocusResult? result = null;
        GateResult? failure = null;
        if (scan)
        {
            result = await new SepMainFocusRunner(boundary).RunAsync(options,
                Path.Combine(Path.GetTempPath(), "UvexAdv-native-focus-tests", Guid.NewGuid().ToString("N")), null, default);
            Assert.Equal("Cancelled", result.Status);
            Assert.True(result.ReturnConfirmed);
            Assert.Equal(1000, result.FinalPosition);
            Assert.Equal(new[] { 950, 1000 }, port.Moves);
        }
        else
        {
            _ = await boundary.ReadReadyAsync(default);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => boundary.CaptureAsync("synthetic-no-file.fit", null, default));
            failure = GateResult.Unknown("AUTO_FOCUS_CANCELLED", "Original diagnostic clock expired.");
            Assert.Empty(port.Moves);
        }
        Assert.True(stops > 0);
        Assert.Equal(1000, port.Position);
        var gate = AutomaticSepFocusBoundary.ClassifyOutcome(result, failure, 1000, port.Position,
            positionConfirmed: true, originalDeadlineExpired: !scan, cancellationRequested: !scan, nightStop: null);
        Assert.Equal(GateFailureScope.TargetQuality, gate!.FailureScope);
        Assert.Equal(scan ? "AUTO_FOCUS_NOT_IMPROVED_RETURNED" : "AUTO_FOCUS_BUDGET_EXHAUSTED_RETURNED", gate.Code);
        return gate;
    }

    private sealed class TimedFocusPort(bool scan) : ISepMainFocusHardware
    {
        public int Position = 1000;
        public readonly List<int> Moves = [];
        public Task<SepFocusState> ReadReadyAsync(CancellationToken token) => Task.FromResult(new SepFocusState(Position, "SIM-C11", 0, 0));
        public Task MoveAsync(int position, CancellationToken token)
        { Position = position; Moves.Add(position); return Task.CompletedTask; }
        public async Task<SepFocusFrame> CaptureAsync(string path, SepFocusReference[]? reference, CancellationToken token)
        {
            if (!scan) await Task.Delay(Timeout.Infinite, token);
            if (Position != 1000)
            {
                using var scanWindow = CancellationTokenSource.CreateLinkedTokenSource(token);
                scanWindow.CancelAfter(TimeSpan.FromMilliseconds(10));
                await Task.Delay(Timeout.Infinite, scanWindow.Token);
            }
            var points = new[] { new SepFocusReference(200, 200), new SepFocusReference(400, 300), new SepFocusReference(600, 400) };
            return new(points, points.Select((p, i) => new SepFocusStar(i, p.X, p.Y, 5, 9, 1000, 100)).ToArray());
        }
        public Task<SepFocusFit> FitAsync(SepFocusPoint[] points, CancellationToken token) => throw new InvalidOperationException("Timeout should precede fitting.");
    }
}
