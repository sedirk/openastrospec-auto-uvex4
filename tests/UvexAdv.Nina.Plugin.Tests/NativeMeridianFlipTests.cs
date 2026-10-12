using System.Reflection;
using NINA.Core.Enum;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.Trigger.MeridianFlip;
using UvexAdv.Nina.Plugin.SequenceItems;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class NativeMeridianFlipTests
{
    [Fact]
    public void SmallSameSideCorrectionsContinueUntilNativeTimingOrDestinationRequestsHandoff()
    {
        var durations = new List<double>();
        var session = new NativeMeridianSession(new(2, 4, 120), s => { durations.Add(s); return false; });
        // HA changing sign is deliberately NOT a criterion or a supplied input.
        Assert.False(session.RequestAtOperationBoundary(PierSide.pierWest, 5));
        Assert.False(session.RequestAtOperationBoundary(PierSide.pierWest, 10));
        Assert.False(session.Requested);
        Assert.True(session.RequestAtOperationBoundary(PierSide.pierWest, 10, destinationWouldChangeSide: true));
        Assert.False(session.ObservedSideChange);
        Assert.Equal(PierSide.pierWest, session.BeforeSide);
        Assert.Equal(new double[] { 5, 10 }, durations);
    }

    [Fact]
    public void ObservedSideChangeUsesSavedGeometryWithoutRequiringAnotherTimingTrigger()
    {
        var session = new NativeMeridianSession(new(2, 4, 120), _ => throw new Exception("already changed side"));
        Assert.True(session.RequestAtOperationBoundary(PierSide.pierEast, savedSide: PierSide.pierWest));
        Assert.True(session.ObservedSideChange);
        Assert.Equal(PierSide.pierWest, session.BeforeSide);
        Assert.Equal(PierSide.pierEast, session.AfterSide);
        session.RecordSegment("first", 1, 2, 1, 120);
        session.ConfirmFlip();
        Assert.Equal(1, session.RemainingPlan().ScienceFrames);
        Assert.Equal(2, session.RemainingPlan().MaximumAttempts);
        Assert.Throws<InvalidOperationException>(() => session.RequestAtOperationBoundary(PierSide.pierWest));
    }

    [Fact]
    public void OrdinaryFailureCannotManufactureScheduledHandoffButActualChangeStillInvalidatesOldVectors()
    {
        var calls = 0;
        var session = new NativeMeridianSession(new(2, 4, 120), _ => { calls++; return true; });
        Assert.False(session.RequestAtOperationBoundary(PierSide.pierWest, allowScheduled: false));
        Assert.Equal(0, calls);
        Assert.True(session.RequestAtOperationBoundary(PierSide.pierEast, allowScheduled: false));
        Assert.True(session.ObservedSideChange);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(ObservationStage.AcquireG3SlitField)]
    [InlineData(ObservationStage.PlaceTargetOnSlit)]
    public async Task NativeMarkersCompleteSegmentAfterInternalBoundaryAndDoNotStartScience(ObservationStage interruptAt)
    {
        using var host = new ObservationCoordinatorHost(new QuietNotifier());
        var session = new NativeMeridianSession(new(2, 4, 120), _ => true);
        var runner = new StageFixture(session, interruptAt);
        using var bridge = new SequencerStageBridge(runner) { EndRunOnGateFailure = true };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var plan = new ObservationPlan("meridian-fixture-" + Guid.NewGuid().ToString("N"), "test",
            new("fixture", "fixture", 0, 89), new(31, 121, 20), DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(1), new(0, 0, 0), new(), "ATR", "G3", "QHY");
        var run = host.RunAsync(plan, bridge, timeout.Token);
        foreach (var stage in Enum.GetValues<ObservationStage>())
            await bridge.ExecuteMarkerAsync(stage, new Progress<NINA.Core.Model.ApplicationStatus>(), timeout.Token);
        await run;
        Assert.Equal(ObservationRunState.Completed, host.Dashboard.Run.State);
        Assert.True(bridge.HasCompletedFinalMarker);
        Assert.False(bridge.HasTerminalGateResult);
        Assert.True(runner.OwnerJoined);
        Assert.True(runner.Finalized);
        Assert.DoesNotContain(ObservationStage.RunScienceBlock, runner.Actions);
        Assert.DoesNotContain(ObservationStage.SelectAtrExposure, runner.Actions);
        Assert.True(session.Requested);
        session.RecordSegment(plan.ObservationRunId, 0, 0, 0, 0);
        session.ConfirmFlip(); // synthetic readback fixture, no device dispatch
        Assert.Equal(2, session.RemainingPlan().ScienceFrames);
        Assert.Equal(4, session.RemainingPlan().MaximumAttempts);
    }

    private sealed class StageFixture(NativeMeridianSession session, ObservationStage interruptAt) : ObservationStageRunnerBase
    {
        internal List<ObservationStage> Actions { get; } = [];
        internal bool OwnerJoined, Finalized;
        public override async Task<StageResult> ExecuteStageAsync(ObservationStage stage, ObservationContext context, CancellationToken token)
        {
            StageResult Boundary() => new(GateResult.Pass("NATIVE_MERIDIAN_SEGMENT_BOUNDARY", "fixture"));
            if (stage == ObservationStage.FinalizeObservation) { Finalized = true; return Boundary(); }
            if (session.Requested) return Boundary();
            using var boundary = new NativeMeridianStageBoundary(token);
            return await boundary.ExecuteAsync(async _ =>
            {
                Actions.Add(stage);
                if (stage == interruptAt)
                {
                    try
                    {
                        await Task.Yield();
                        session.RequestAtOperationBoundary(PierSide.pierWest);
                        boundary.Interrupt();
                        throw new Exception("stale stage continued");
                    }
                    finally { OwnerJoined = true; }
                }
                return new StageResult(GateResult.Pass("FIXTURE_STAGE", ""));
            }, Boundary);
        }
    }

    private sealed class QuietNotifier : IObservationAttentionNotifier
    {
        public void Notify(ObservationAttentionNotification notification) { }
        public void ClearActiveIndicator() { }
        public void Dispose() { }
    }

    [Theory]
    [InlineData(PierSide.pierUnknown, 1)]
    [InlineData(PierSide.pierEast, -1)]
    [InlineData(PierSide.pierEast, double.NaN)]
    public void UnknownSideOrDurationDoesNotRequestBoundary(PierSide side, double seconds)
    {
        var session = new NativeMeridianSession(new(2, 4, 120), _ => true);
        Assert.Throws<InvalidOperationException>(() => session.RequestAtOperationBoundary(side, seconds));
        Assert.False(session.Requested);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task InternalStageInterruptionJoinsOwnerAndDoesNotCancelLogicalTarget(bool adapterReturnsFailure)
    {
        using var parent = new CancellationTokenSource();
        using var boundary = new NativeMeridianStageBoundary(parent.Token);
        var events = new List<string>();
        var result = await boundary.ExecuteAsync(async token =>
        {
            try
            {
                await Task.Yield();
                boundary.Interrupt();
                events.Add("stale-move");
            }
            catch (OperationCanceledException) when (adapterReturnsFailure && token.IsCancellationRequested)
            {
                return new StageResult(GateResult.Unknown("OWNER_STOPPED", "adapter converted cancellation"));
            }
            finally { events.Add("owner-joined"); }
            return new StageResult(GateResult.Pass("UNREACHABLE", ""));
        }, () => { events.Add("handoff"); return new(GateResult.Pass("MERIDIAN", "")); });
        Assert.Equal("MERIDIAN", result.Gate.Code);
        Assert.Equal(new[] { "owner-joined", "handoff" }, events);
        Assert.False(parent.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualCancellationWinsOverInternalHandoff(bool simultaneous)
    {
        using var parent = new CancellationTokenSource();
        using var boundary = new NativeMeridianStageBoundary(parent.Token);
        var handoffs = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => boundary.ExecuteAsync(_ =>
        {
            if (!simultaneous) parent.Cancel();
            try { boundary.Interrupt(); }
            finally { parent.Cancel(); }
            return Task.FromResult(new StageResult(GateResult.Pass("UNREACHABLE", "")));
        }, () => { handoffs++; return new(GateResult.Pass("MERIDIAN", "")); }));
        Assert.Equal(0, handoffs);
    }

    [Fact]
    public async Task UnrequestedCancellationAndRealErrorsAreNeverReclassifiedAsMeridian()
    {
        using var boundary = new NativeMeridianStageBoundary(default);
        StageResult Handoff() => throw new Exception("must not hand off");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => boundary.ExecuteAsync(
            _ => throw new OperationCanceledException("owner timeout"), Handoff));
        await Assert.ThrowsAsync<InvalidOperationException>(() => boundary.ExecuteAsync(
            _ => throw new InvalidOperationException("identity changed"), Handoff));
    }

    [Fact]
    public async Task ObservedOrientationIsVerifiedWithoutAcceptingAnyMotionDispatchPort()
    {
        var readbacks = 0;
        var result = await NativeMeridianMountOperation.VerifyObservedChangeAsync(PierSide.pierWest,
            PierSide.pierEast, () => { readbacks++; return Ready(PierSide.pierEast); },
            () => { }, _ => Task.CompletedTask, default);
        Assert.Equal(PierSide.pierEast, result);
        Assert.Equal(2, readbacks);
    }

    [Theory]
    [InlineData("flapping")] [InlineData("slewing")] [InlineData("disconnect")]
    [InlineData("tracking-off")] [InlineData("unknown")]
    public async Task ObservedSideMustRemainIdleAndStableAcrossSettle(string failure)
    {
        var state = Ready(PierSide.pierEast);
        await Assert.ThrowsAsync<InvalidOperationException>(() => NativeMeridianMountOperation.VerifyObservedChangeAsync(
            PierSide.pierWest, PierSide.pierEast, () => state, () => { }, _ =>
            {
                state = failure switch
                {
                    "flapping" => state with { Side = PierSide.pierWest },
                    "slewing" => state with { Slewing = true },
                    "disconnect" => state with { Connected = false },
                    "tracking-off" => state with { Tracking = false },
                    _ => state with { Side = PierSide.pierUnknown },
                };
                return Task.CompletedTask;
            }, default));
    }

    [Theory]
    [InlineData("ObservedSideChange", "pierWest", false)]
    [InlineData("Scheduled", "pierWest", true)]
    [InlineData("ObservedSideChange", "pierEast", true)]
    public async Task ObservedJournalRequiresExplicitReasonAndChangedSide(string reason, string after, bool blocked)
    {
        var path = Path.Combine(Path.GetTempPath(), "uvex-observed-side-" + Guid.NewGuid().ToString("N"), "journal.json");
        await NativeMeridianJournalStore.SaveAsync(path, new("run", "SideChangeVerified", "pierEast", after,
            DateTimeOffset.UtcNow, ["run"], reason), default);
        Assert.Equal(blocked, NativeMeridianJournalStore.PendingGate(path) is not null);
    }

    [Fact]
    public void MeridianFinalizationBypassesOldSideReturnsAndPostShutterPathCannotInterruptSave()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var start = source.IndexOf("private async Task<StageResult> ExecuteStageCoreAsync", StringComparison.Ordinal);
        var body = source[start..source.IndexOf("var automaticRecovery = await", start, StringComparison.Ordinal)];
        Assert.True(body.IndexOf("await FinalizeObservationAsync", StringComparison.Ordinal) <
            body.IndexOf("await RecoverDurableG3AcquisitionBeforeStageAsync", StringComparison.Ordinal));
        var capture = source.IndexOf("await imagingMediator.CaptureImage", StringComparison.Ordinal);
        var savedEnd = source.IndexOf("private SpectralProbeMetrics MeasureSpectralProbe", capture, StringComparison.Ordinal);
        Assert.DoesNotContain("CheckNativeMeridian", source[capture..savedEnd]);
        Assert.DoesNotContain("CheckpointAndRejectStaleStageStackAsync", source[capture..savedEnd]);
        Assert.DoesNotContain("RequireImmediatePhysicalActionGatesAsync", source[capture..savedEnd]);
    }

    [Fact]
    public void IndependentTransformLedgerIsReadBeforeMeridianRetirementOrAnyMountDispatch()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.NativeMeridianFlip.cs"));
        var durable = source.IndexOf("await ResolveSlitPlacementPendingAsync", StringComparison.Ordinal);
        var journal = source.IndexOf("await Journal(\"OwnersIdle\")", StringComparison.Ordinal);
        Assert.True(durable > 0 && journal > durable);
        Assert.Contains("independent.Error is { } independentError", source[durable..journal]);
        Assert.Contains("independent.Resolution?.State is { Phase: not SlitPlacementPendingPhase.SettledBudgetLedger }", source[durable..journal]);
        Assert.True(source.IndexOf("await GuideStateBoundaryRetirement.RetireMeridianSegmentAsync", StringComparison.Ordinal) > journal);
        Assert.True(source.IndexOf("telescopeMediator.SetTrackingEnabled(false)", StringComparison.Ordinal) > journal);
    }

    [Fact]
    public void TargetBudgetAndIntegrationCarryAcrossTwoSegmentsWithoutRecreditingFiles()
    {
        var due = false;
        var session = new NativeMeridianSession(new(5, 8, 600), _ => due);
        Assert.False(session.RequestAtFrameBoundary(600));
        session.RecordSegment("before", 2, 3, 1, 1200);
        due = true;
        Assert.True(session.RequestAtFrameBoundary(600));
        var remaining = session.RemainingPlan();
        Assert.Equal(3, remaining.ScienceFrames);
        Assert.Equal(5, remaining.MaximumAttempts);
        Assert.True(remaining.DeferObservatoryCloseout);
        session.ConfirmFlip();
        due = false;
        Assert.False(session.RequestAtFrameBoundary(600));
        var total = session.Aggregate(new("after", 3, 1, 2, 5, 0, 600, 600));
        Assert.Equal(3, total.AcceptedFrames);
        Assert.Equal(5, total.AttemptedFrames);
        Assert.Equal(5, total.RequestedFrames);
        Assert.Equal(8, total.MaximumAttempts);
        Assert.Equal(1800, total.AcceptedExposureSeconds);
        Assert.Equal(1, total.ReusedProbeFrames);
        session.RecordSegment("after", 3, 4, 1, 1800);
        Assert.Equal(5, session.Accepted);
        Assert.Equal(7, session.Attempts);
        Assert.Equal(3000, session.AcceptedSeconds);
        Assert.Throws<InvalidOperationException>(() => session.RecordSegment("after", 0, 0, 0, 0));
        Assert.Throws<InvalidOperationException>(() => session.RemainingPlan());
    }

    [Fact]
    public void ExhaustedAttemptBudgetAndSecondFlipCannotManufactureMoreAuthority()
    {
        var session = new NativeMeridianSession(new(3, 4, 600), _ => true);
        session.RecordSegment("exhausted", 1, 4, 0, 600);
        Assert.Throws<InvalidOperationException>(() => session.RemainingPlan());
        Assert.True(session.RequestAtFrameBoundary(600));
        session.ConfirmFlip();
        Assert.Throws<InvalidOperationException>(() => session.RequestAtFrameBoundary(600));
        Assert.Throws<InvalidOperationException>(() => session.ConfirmFlip());
    }

    [Theory]
    [InlineData("OwnersIdle", "pierEast", true)]
    [InlineData("WaitingForMeridian", "pierEast", true)]
    [InlineData("CommandPending", "pierWest", true)]
    [InlineData("MountVerified", "pierEast", true)]
    [InlineData("MountVerified", "pierUnknown", true)]
    [InlineData("MountVerified", "pierWest", false)]
    public async Task CrashJournalCannotAuthorizeAFreshRunUntilOppositeSideIsVerified(string phase, string after, bool blocked)
    {
        var path = Path.Combine(Path.GetTempPath(), "uvex-flip-test-" + Guid.NewGuid().ToString("N"), "journal.json");
        Assert.Null(NativeMeridianJournalStore.PendingGate(path));
        await NativeMeridianJournalStore.SaveAsync(path, new("run", phase, "pierEast", after,
            DateTimeOffset.UtcNow, ["run"]), CancellationToken.None);
        Assert.Equal(blocked, NativeMeridianJournalStore.PendingGate(path) is not null);
        await File.WriteAllTextAsync(path, "{corrupt}");
        Assert.Equal("NATIVE_FLIP_JOURNAL_UNREADABLE", NativeMeridianJournalStore.PendingGate(path)?.Code);
    }

    [Fact]
    public async Task MountProtocolDispatchesOnceAndWaitsForVerifiedSideBeforeReturning()
    {
        var events = new List<string>();
        var state = Ready(PierSide.pierEast);
        var after = await NativeMeridianMountOperation.ExecuteAsync(PierSide.pierEast, () => state,
            _ => { events.Add("native-flip"); state = Ready(PierSide.pierWest); return Task.FromResult(true); },
            () => events.Add("stop"), () => events.Add("gate"),
            _ => { events.Add("settle"); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(PierSide.pierWest, after);
        Assert.Equal(new[] { "gate", "native-flip", "settle", "gate" }, events);
    }

    [Theory]
    [InlineData("wrong-side")]
    [InlineData("unknown-side")]
    [InlineData("slewing")]
    [InlineData("disconnected")]
    [InlineData("not-tracking")]
    [InlineData("parked")]
    [InlineData("home")]
    [InlineData("native-false")]
    public async Task CommandCompletionWithoutValidReadbackCannotResume(string failure)
    {
        var calls = 0;
        var state = Ready(PierSide.pierEast);
        await Assert.ThrowsAsync<InvalidOperationException>(() => NativeMeridianMountOperation.ExecuteAsync(
            PierSide.pierEast, () => state, _ =>
            {
                calls++;
                state = failure switch
                {
                    "wrong-side" => Ready(PierSide.pierEast),
                    "unknown-side" => Ready(PierSide.pierUnknown),
                    "slewing" => Ready(PierSide.pierWest) with { Slewing = true },
                    "disconnected" => Ready(PierSide.pierWest) with { Connected = false },
                    "not-tracking" => Ready(PierSide.pierWest) with { Tracking = false },
                    "parked" => Ready(PierSide.pierWest) with { Parked = true },
                    "home" => Ready(PierSide.pierWest) with { Home = true },
                    _ => Ready(PierSide.pierWest),
                };
                return Task.FromResult(failure != "native-false");
            }, () => { }, () => { }, _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task BusyOwnerOrUnsafeGatePreventsDispatch()
    {
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => NativeMeridianMountOperation.ExecuteAsync(
            PierSide.pierEast, () => Ready(PierSide.pierEast), _ => { calls++; return Task.FromResult(true); },
            () => { }, () => throw new InvalidOperationException("worker still exposing or safety unsafe"),
            _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SynchronousOwnerDispatchFailureAlsoStopsWithoutRetrying()
    {
        var calls = 0;
        var stopped = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => NativeMeridianMountOperation.ExecuteAsync(
            PierSide.pierEast, () => Ready(PierSide.pierEast), _ =>
            { calls++; throw new InvalidOperationException("owner dispatch failed"); },
            () => stopped = true, () => { }, _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(1, calls);
        Assert.True(stopped);
    }

    [Fact]
    public void ScienceFlipCheckPrecedesNextAttemptAndCannotFollowFinalAcceptedFrame()
    {
        var science = WorkflowSource.Science;
        var boundary = science.IndexOf("RequestNativeMeridianBoundary", StringComparison.Ordinal);
        var attempt = science.IndexOf("attemptedAtrFrames++", StringComparison.Ordinal);
        var accepted = science.IndexOf("savedAtrFrames++", StringComparison.Ordinal);
        Assert.True(boundary > 0 && boundary < attempt && attempt < accepted);
        Assert.DoesNotContain("RequestNativeMeridianBoundary", science[(boundary + "RequestNativeMeridianBoundary".Length)..]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancelOrSafetyTripJoinsTheOriginalOwnerAndNeverRetries(bool safety)
    {
        var calls = 0;
        var stopped = false;
        var joined = false;
        using var cancel = new CancellationTokenSource();
        var run = NativeMeridianMountOperation.ExecuteAsync(PierSide.pierEast, () => Ready(PierSide.pierEast),
            async ct =>
            {
                calls++;
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return true; }
                finally { joined = true; }
            }, () => stopped = true,
            () => { if (safety && calls > 0) throw new InvalidOperationException("unsafe"); },
            _ => Task.CompletedTask, cancel.Token);
        if (safety) await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(3)));
        else
        {
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        Assert.Equal(1, calls);
        Assert.True(stopped);
        Assert.True(joined);
    }

    [Fact]
    public void TriggerUsesNativeTimingButCannotDispatchAtOrdinaryStageMarkers()
    {
        var trigger = Trigger();
        Assert.IsAssignableFrom<MeridianFlipTrigger>(trigger);
        Assert.False(trigger.ShouldTrigger(null!, null!));
        Assert.True(trigger.Validate());
        var clone = Assert.IsType<SpectroscopyMeridianFlipTrigger>(trigger.Clone());
        Assert.Equal(trigger.BoundaryReserveSeconds, clone.BoundaryReserveSeconds);
        trigger.BoundaryReserveSeconds = double.NaN;
        Assert.False(trigger.Validate());
        Assert.Throws<InvalidOperationException>(() => trigger.IsDue(600));
    }

    [Fact]
    public void ExactNativeTimingIncludesUpcomingExposureAndDoesNotRepeatOnNewSide()
    {
        var info = new TelescopeInfo
        {
            Connected = true, TrackingEnabled = true, SideOfPier = PierSide.pierWest,
            Coordinates = new NINA.Astrometry.Coordinates(10, 30, NINA.Astrometry.Epoch.JNOW, NINA.Astrometry.Coordinates.RAType.Hours),
            SiderealTime = 9.98, TimeToMeridianFlip = 11.2 / 60,
        };
        var trigger = Trigger(info);
        Assert.False(trigger.IsDue(120)); // 240 s still fit before the native latest limit.
        Assert.True(trigger.IsDue(600)); // 600 + 120 s no longer fit.
        Assert.Equal(PierSide.pierWest, trigger.RequestedSide);
        Assert.InRange(trigger.WaitBeforeFlip.TotalSeconds, 371, 373);
        info.SideOfPier = PierSide.pierEast;
        info.SiderealTime = 10.1;
        info.TimeToMeridianFlip = 12 + 4d / 60;
        Assert.False(trigger.IsDue(600));
        info.SideOfPier = PierSide.pierUnknown;
        Assert.Throws<InvalidOperationException>(() => trigger.IsDue(600));
    }

    [Fact]
    public async Task DuplicateGlobalAndLocalTriggersAreRejectedAndSerializationNeverRestoresRuntimeAuthority()
    {
        var root = new SequentialContainer();
        var group = new SequentialContainer();
        root.Add(group);
        var trigger = Trigger();
        root.Triggers.Add(trigger); trigger.AttachNewParent(root);
        Assert.Empty(SpectroscopyMeridianFlipTrigger.ScopeIssues(group));
        var duplicate = Assert.IsType<SpectroscopyMeridianFlipTrigger>(trigger.Clone());
        group.Triggers.Add(duplicate); duplicate.AttachNewParent(group);
        Assert.Contains(SpectroscopyMeridianFlipTrigger.ScopeIssues(group), x => x.Contains("重复"));
        duplicate.Status = SequenceEntityStatus.DISABLED;
        Assert.Empty(SpectroscopyMeridianFlipTrigger.ScopeIssues(group));
        await Assert.ThrowsAsync<InvalidOperationException>(() => trigger.Execute(root, null!, CancellationToken.None));
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(trigger, new Newtonsoft.Json.JsonSerializerSettings
        { PreserveReferencesHandling = Newtonsoft.Json.PreserveReferencesHandling.All, TypeNameHandling = Newtonsoft.Json.TypeNameHandling.All });
        Assert.Contains("BoundaryReserveSeconds", json);
        Assert.DoesNotContain("RequestedSide", json);
        Assert.DoesNotContain("FlipStatus", json);
        Assert.DoesNotContain("ConfigurationKey", json);
        trigger.BoundaryReserveSeconds = 180;
        trigger.SetStatus("runtime status must not restore");
        var factory = Proxy<NINA.Sequencer.ISequencerFactory>((m, _) => m.Name switch
        {
            "GetContainer" => new SequentialContainer(),
            "GetTrigger" => Trigger(),
            _ => Default(m.ReturnType),
        });
        var converter = new NINA.Sequencer.Serialization.SequenceJsonConverter(factory);
        var restored = Assert.IsType<SequentialContainer>(converter.Deserialize(converter.Serialize(root)));
        var restoredTrigger = Assert.IsType<SpectroscopyMeridianFlipTrigger>(Assert.Single(restored.Triggers));
        Assert.Equal(180, restoredTrigger.BoundaryReserveSeconds);
        Assert.Null(restoredTrigger.RequestedSide);
        Assert.DoesNotContain("runtime status", restoredTrigger.FlipStatus);
    }

    private static NativeMeridianMountReadback Ready(PierSide side) => new(true, false, true, false, false, side);
    private static SpectroscopyMeridianFlipTrigger Trigger(TelescopeInfo? telescopeInfo = null)
    {
        var flip = Proxy<IMeridianFlipSettings>((m, _) => m.Name switch
        {
            "get_UseSideOfPier" => true,
            "get_MinutesAfterMeridian" => 5d,
            "get_MaxMinutesAfterMeridian" => 10d,
            "get_PauseTimeBeforeMeridian" => 0d,
            _ => Default(m.ReturnType),
        });
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_MeridianFlipSettings" ? flip : Default(m.ReturnType));
        return new(Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType)),
            null!, Proxy<ITelescopeMediator>((m, _) => m.Name == "GetInfo" ? telescopeInfo : Default(m.ReturnType)), null!, null!, null!);
    }
    private static object? Default(Type type) => type == typeof(void) ? null : type.IsValueType ? Activator.CreateInstance(type) : null;
    private static T Proxy<T>(Func<MethodInfo, object?[], object?>? handler = null) where T : class
    {
        var proxy = DispatchProxy.Create<T, InterfaceProxy>();
        ((InterfaceProxy)(object)proxy).Handler = handler ?? ((m, _) => Default(m.ReturnType));
        return proxy;
    }
    private class InterfaceProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args ?? []);
    }
}
