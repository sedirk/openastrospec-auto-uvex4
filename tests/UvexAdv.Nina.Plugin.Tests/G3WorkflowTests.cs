using NINA.Astrometry;
using NINA.Core.Enum;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3WorkflowTests
{
    [Theory]
    [InlineData("interlock")] [InlineData("pending-return")] [InlineData("guide-return")]
    public async Task AcquisitionReconcilesExistingReturnBeforeAnyCapture(string blockedAt)
    {
        var port = StrictWorkflowPort.Create<IG3AcquisitionPort>(out var trace);
        var deny = GateResult.Unknown(blockedAt, "fixture");
        trace.Handlers["EvaluateInterlocksAsync"] = _ => Task.FromResult(blockedAt == "interlock" ? deny : Pass);
        trace.Handlers["EnsureFineAcquisitionClockStarted"] = _ => null;
        trace.Handlers["CompletePendingG3SearchReturnAsync"] = _ => Task.FromResult<StageResult?>(blockedAt == "pending-return" ? new(deny) : null);
        trace.Handlers["ReturnAfterOwnedGuidingBeforeReacquisitionAsync"] = _ => Task.FromResult(deny);
        var workflow = new G3AcquisitionWorkflow(Configuration, port);
        var result = await workflow.AcquireAsync(AtrWorkflowFixture.CreateContext(), default);
        Assert.Equal(blockedAt, result.Gate.Code);
        Assert.DoesNotContain("CaptureAndAnalyzeG3WithSolveLadderAsync", trace.Calls);
        if (blockedAt == "guide-return") Assert.Equal(new[] { "EvaluateInterlocksAsync", "EnsureFineAcquisitionClockStarted",
            "CompletePendingG3SearchReturnAsync", "ReturnAfterOwnedGuidingBeforeReacquisitionAsync" }, trace.Calls);
    }

    [Theory]
    [InlineData("TARGET_NOT_FOUND", true)] [InlineData("TARGET_AMBIGUOUS", true)]
    [InlineData(G3PostWcsMeasuredHandoffPolicy.OutsideRemainingCode, true)]
    [InlineData("G3_PLATE_SOLVE_FAILED", true)] [InlineData("CAMERA_OWNER_CHANGED", false)]
    public async Task FailedFieldUsesExistingNeighborBranchWithoutInventingBudget(string code, bool neighbor)
    {
        var port = StrictWorkflowPort.Create<IG3AcquisitionPort>(out var trace);
        var field = G3FieldState.Failed(GateResult.Unknown(code, "fixture"), "immutable.fits");
        trace.Handlers["EvaluateInterlocksAsync"] = _ => Task.FromResult(Pass);
        trace.Handlers["EnsureFineAcquisitionClockStarted"] = _ => null;
        trace.Handlers["CompletePendingG3SearchReturnAsync"] = _ => Task.FromResult<StageResult?>(null);
        trace.Handlers["ReturnAfterOwnedGuidingBeforeReacquisitionAsync"] = _ => Task.FromResult(Pass);
        trace.Handlers["EnsureWideToSlitTransferSkippedEvidenceAsync"] = _ => Task.FromResult("transfer.json");
        trace.Handlers["UsesCatalogPositionOnly"] = _ => false;
        trace.Handlers["PublishTargetBranchAsync"] = _ => Task.CompletedTask;
        trace.Handlers["CaptureAndAnalyzeG3WithSolveLadderAsync"] = _ => Task.FromResult(field);
        trace.Handlers["RememberField"] = a => { Assert.Same(field, a[0]); return null; };
        trace.Handlers["get_ActiveTargetBranch"] = _ => (TargetAcquisitionBranch?)null;
        trace.Handlers["RunBoundedG3LocalSearchAsync"] = a =>
        {
            Assert.Same(field, a[1]); Assert.Equal("transfer.json", a[2]); Assert.Equal(true, a[4]);
            return Task.FromResult(new StageResult(GateResult.Pass("neighbor", "same ledger")));
        };
        var result = await new G3AcquisitionWorkflow(Configuration, port).AcquireAsync(AtrWorkflowFixture.CreateContext(), default, true);
        Assert.Equal(neighbor ? "neighbor" : code, result.Gate.Code);
        Assert.Equal(neighbor ? 1 : 0, trace.Calls.Count(c => c == "RunBoundedG3LocalSearchAsync"));
        Assert.Single(trace.Calls, c => c == "CaptureAndAnalyzeG3WithSolveLadderAsync");
    }

    [Fact]
    public async Task ReturnPrechargesBeforeDispatchAndPreservesTheSameLineageAndClock()
    {
        var f = new ReturnFixture();
        var result = await f.Workflow.ReturnAsync(f.Context, f.State, "original.json", default);
        Assert.True(result.ReturnedToOrigin, result.Message);
        Assert.Equal(f.State.BudgetLineageId, result.State.BudgetLineageId);
        Assert.Equal(f.State.StartedUtc, result.State.StartedUtc);
        Assert.Equal(f.State.CorrectionAttempts + 1, result.State.CorrectionAttempts);
        Assert.Equal(f.State.CumulativeMotionArcseconds + f.State.MaximumSingleCorrectionArcseconds, result.State.CumulativeMotionArcseconds);
        Assert.Equal(G3AcquisitionMotionPhase.ReturnIntent, f.Persisted[0].Phase);
        Assert.Equal(G3AcquisitionMotionPhase.SettledBudgetLedger, f.Persisted[^1].Phase);
        Assert.True(f.Trace.Calls.IndexOf("PersistG3AcquisitionMotionAsync") < f.Trace.Calls.IndexOf("SlewToCoordinatesAsync"));
        Assert.Contains("WaitForG3PostSlewStabilityAsync", f.Trace.Calls);
    }

    [Theory]
    [InlineData("destination-before")] [InlineData("destination-after")]
    [InlineData("horizon")] [InlineData("cancel-after-intent")] [InlineData("slew-rejected")]
    public async Task InterruptedReturnNeverLiesAboutArrivalOrRefundsPrecharge(string mode)
    {
        var f = new ReturnFixture();
        var sideCalls = 0;
        var gateCalls = 0;
        f.Trace.Handlers["ValidateG3DestinationPierSide"] = _ =>
            ++sideCalls == (mode == "destination-before" ? 1 : mode == "destination-after" ? 2 : -1)
                ? GateResult.Fail("SIDE", "fixture") : Pass;
        if (mode == "horizon") f.Trace.Handlers["ValidateCommandCoordinateHorizon"] = _ => GateResult.Fail("HORIZON", "fixture");
        if (mode == "slew-rejected") f.Trace.Handlers["SlewToCoordinatesAsync"] = _ => Task.FromResult(false);
        f.Trace.Handlers["RequireImmediatePhysicalActionGatesAsync"] = _ =>
            ++gateCalls == 2 && mode == "cancel-after-intent" ? throw new OperationCanceledException() : Task.CompletedTask;
        if (mode == "cancel-after-intent")
            await Assert.ThrowsAsync<OperationCanceledException>(() => f.Workflow.ReturnAsync(f.Context, f.State, "original.json", default));
        else
            Assert.False((await f.Workflow.ReturnAsync(f.Context, f.State, "original.json", default)).ReturnedToOrigin);
        if (mode is "destination-after" or "cancel-after-intent" or "slew-rejected")
        {
            Assert.Equal(f.State.CorrectionAttempts + 1, Assert.Single(f.Persisted).CorrectionAttempts);
            Assert.Equal(G3AcquisitionMotionPhase.ReturnIntent, f.Persisted[0].Phase);
        }
        else Assert.Empty(f.Persisted);
        if (mode != "slew-rejected") Assert.DoesNotContain("SlewToCoordinatesAsync", f.Trace.Calls);
    }

    [Fact]
    public async Task ExpiredReturnCannotStartNewClockButAlreadyAtOriginCanBeRecordedWithoutMotion()
    {
        var f = new ReturnFixture();
        var old = f.State with { StartedUtc = DateTimeOffset.UtcNow.AddHours(-1) };
        Assert.False((await f.Workflow.ReturnAsync(f.Context, old, "original.json", default)).ReturnedToOrigin);
        Assert.DoesNotContain("SlewToCoordinatesAsync", f.Trace.Calls);
        f.Position = new(old.OriginRaDegrees, old.OriginDeclinationDegrees, Epoch.J2000, Coordinates.RAType.Degrees);
        var result = await f.Workflow.ReturnAsync(f.Context, old, "original.json", default);
        Assert.True(result.ReturnedToOrigin, result.Message);
        Assert.Equal(old.StartedUtc, result.State.StartedUtc);
        Assert.Equal(old.CorrectionAttempts, result.State.CorrectionAttempts);
    }

    [Theory]
    [InlineData("attempt", "skip")] [InlineData("attempt", "cleanup-failure")] [InlineData("attempt", "end-policy")]
    [InlineData("time", "skip")] [InlineData("time", "cleanup-failure")] [InlineData("time", "end-policy")]
    [InlineData("cumulative", "skip")] [InlineData("cumulative", "cleanup-failure")] [InlineData("cumulative", "end-policy")]
    [InlineData("precharge", "skip")] [InlineData("precharge", "cleanup-failure")] [InlineData("precharge", "end-policy")]
    [InlineData("clock", "unknown-gate")] [InlineData("invalid", "unknown-gate")]
    public async Task ReturnWorkflowPreservesBudgetFailureToTheActualNextNativeTarget(string cause, string boundary)
    {
        var f = new ReturnFixture();
        var state = cause switch
        {
            "attempt" => f.State with { CorrectionAttempts = f.State.MaximumCorrectionAttempts },
            "time" => f.State with { StartedUtc = f.State.StartedUtc.AddHours(-1) },
            "cumulative" => f.State with { CumulativeMotionArcseconds = 499 },
            "precharge" => f.State with { CumulativeMotionArcseconds = 475 },
            "clock" => f.State with { UpdatedUtc = DateTimeOffset.UtcNow.AddHours(1) },
            _ => f.State with { CumulativeMotionArcseconds = double.NaN },
        };
        var returned = await f.Workflow.ReturnAsync(f.Context, state, "original.json", default);
        Assert.False(returned.ReturnedToOrigin);
        Assert.NotNull(returned.FailureGate);
        Assert.Empty(f.Persisted);
        Assert.DoesNotContain("SlewToCoordinatesAsync", f.Trace.Calls);
        Assert.Equal(state.StartedUtc, returned.State.StartedUtc);
        Assert.Equal(state.CorrectionAttempts, returned.State.CorrectionAttempts);
        var gate = G3WcsRecoveryPolicy.UnreturnedCenteringGate(returned.FailureGate, returned.Message,
            new Dictionary<string, double> { ["returnedToOrigin"] = 0 });
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(boundary, gate.Code, gate);
    }

    private static GateResult Pass => GateResult.Pass("pass", "fixture");

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RecoveryReturnObservedFlipJoinsNativeSegmentWithoutOldSideMotionOrBudgetRefund(bool beforeCommand)
    {
        var f = new ReturnFixture();
        var session = new NativeMeridianSession(new(2, 4, 300), _ => throw new Exception("Observed change needs no timed flip"));
        using var boundary = new NativeMeridianStageBoundary(default);
        var currentSide = beforeCommand ? PierSide.pierWest : PierSide.pierEast;
        var commandCompleted = false;
        var joined = false;
        f.Trace.Handlers["ValidateG3SearchMountState"] = _ =>
        {
            if (currentSide != PierSide.pierEast &&
                session.RequestAtOperationBoundary(currentSide, savedSide: PierSide.pierEast, allowScheduled: false))
                boundary.Interrupt();
            return Pass;
        };
        f.Trace.Handlers["WaitForSlew"] = _ =>
        {
            commandCompleted = true;
            currentSide = PierSide.pierWest;
            return Task.CompletedTask;
        };
        var result = await boundary.ExecuteAsync(async token =>
        {
            try
            {
                var returned = await f.Workflow.ReturnAsync(f.Context, f.State, "original.json", token);
                return new StageResult(G3GuidingRecoveryReturnPolicy.FailureGate(returned.FailureGate, returned.Message));
            }
            finally { joined = true; }
        }, () =>
        {
            Assert.True(joined);
            return new StageResult(GateResult.Pass("NATIVE_MERIDIAN_SEGMENT_BOUNDARY", "joined"));
        });
        Assert.Equal("NATIVE_MERIDIAN_SEGMENT_BOUNDARY", result.Gate.Code);
        Assert.True(session.ObservedSideChange);
        Assert.Equal(!beforeCommand, commandCompleted);
        Assert.Equal(beforeCommand ? 0 : 1, f.Trace.Calls.Count(x => x == "SlewToCoordinatesAsync"));
        Assert.DoesNotContain("WaitForG3PostSlewStabilityAsync", f.Trace.Calls);
        Assert.DoesNotContain(f.Persisted, x => x.Phase == G3AcquisitionMotionPhase.SettledBudgetLedger);
        if (!beforeCommand)
        {
            var debit = Assert.Single(f.Persisted);
            Assert.Equal(f.State.CorrectionAttempts + 1, debit.CorrectionAttempts);
            Assert.Equal(f.State.CumulativeMotionArcseconds + f.State.MaximumSingleCorrectionArcseconds, debit.CumulativeMotionArcseconds);
            Assert.Equal(f.State.StartedUtc, debit.StartedUtc);
        }
        // Recorded HD 219937 counts: keep its accepted 300s probe and failed
        // second attempt; observed-side verification sends no second slew.
        session.RecordSegment("hd219937-before", 1, 2, 1, 300);
        var verified = await NativeMeridianMountOperation.VerifyObservedChangeAsync(PierSide.pierEast, PierSide.pierWest,
            () => new(true, false, true, false, false, currentSide), () => { }, _ => Task.CompletedTask, default);
        Assert.Equal(PierSide.pierWest, verified);
        session.ConfirmFlip();
        Assert.Equal(1, session.RemainingPlan().ScienceFrames);
        Assert.Equal(2, session.RemainingPlan().MaximumAttempts);
        Assert.Equal(300, session.AcceptedSeconds);
        Assert.Throws<InvalidOperationException>(() => session.RequestAtOperationBoundary(PierSide.pierEast));
    }

    [Theory]
    [InlineData("skip")] [InlineData("cleanup-failure")] [InlineData("end-policy")]
    [InlineData("late-cancel")] [InlineData("late-unsafe")] [InlineData("late-deadline")] [InlineData("late-dawn")]
    public async Task UnhandledRecoverySideChangeReachesActualNextTargetOnlyAfterStoppedBoundary(string mode)
    {
        var f = new ReturnFixture();
        f.Trace.Handlers["ValidateG3SearchMountState"] = _ =>
            GateResult.Fail("G3_SEARCH_PIER_SIDE_CHANGED", "known changed side; no native trigger");
        var returned = await f.Workflow.ReturnAsync(f.Context, f.State, "original.json", default);
        var gate = G3GuidingRecoveryReturnPolicy.FailureGate(returned.FailureGate, returned.Message);
        Assert.Equal("G3_GUIDING_RECOVERY_PIER_CHANGED", gate.Code);
        Assert.Equal(0, gate.Metrics!["returnedToOrigin"]);
        Assert.False(returned.ReturnedToOrigin);
        Assert.Empty(f.Persisted);
        Assert.DoesNotContain("SlewToCoordinatesAsync", f.Trace.Calls);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(mode, gate.Code, gate,
            ObservationStage.RunScienceBlock);
    }

    [Fact]
    public void G3ReadbackRequestsMeridianBeforeReturningFailureEvenInsideDependencyRebuild()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var start = source.IndexOf("private GateResult ValidateG3SearchMountState", StringComparison.Ordinal);
        var body = source[start..source.IndexOf("private GateResult ValidateG3SearchMoveAndReturnReserve", start, StringComparison.Ordinal)];
        var checkpoint = body.IndexOf("CheckNativeMeridianOperationBoundary", StringComparison.Ordinal);
        Assert.True(checkpoint > body.IndexOf("G3_SEARCH_PULSE_GUIDING_ACTIVE", StringComparison.Ordinal));
        Assert.True(checkpoint > body.IndexOf("G3_SEARCH_PIER_SIDE_UNKNOWN", StringComparison.Ordinal));
        Assert.True(checkpoint < body.IndexOf("\"G3_SEARCH_PIER_SIDE_CHANGED\"", StringComparison.Ordinal));
        Assert.Contains("savedSide: savedSide, allowScheduled: false", body);
    }

    [Theory]
    [InlineData("G3_SEARCH_PIER_SIDE_UNKNOWN")] [InlineData("G3_SEARCH_MOUNT_SLEWING")]
    [InlineData("G3_SEARCH_MOUNT_DISCONNECTED")] [InlineData("G3_SEARCH_PULSE_GUIDING_ACTIVE")]
    [InlineData("G3_SEARCH_TRACKING_DISABLED")] [InlineData("WEATHER_UNSAFE")]
    [InlineData("G3_GUIDING_RECOVERY_OWNER_CHANGED")]
    public void RecoverySideClassificationDoesNotDowngradeActualUncertaintyOrDanger(string code)
    {
        var original = GateResult.Unknown(code, "fixture");
        var gate = G3GuidingRecoveryReturnPolicy.FailureGate(original, original.Message);
        Assert.Equal(original.Code, gate.Code);
        Assert.Equal(original.Disposition, gate.Disposition);
        Assert.False(NativeSequencePlan.IsSkippableQualityFailure(gate));
        var textOnly = G3GuidingRecoveryReturnPolicy.FailureGate(null, "G3_SEARCH_PIER_SIDE_CHANGED");
        Assert.False(NativeSequencePlan.IsSkippableQualityFailure(textOnly));
        var uncertainSide = G3GuidingRecoveryReturnPolicy.FailureGate(
            GateResult.Unknown("G3_SEARCH_PIER_SIDE_CHANGED", "no confirmed side"), "fixture");
        Assert.False(NativeSequencePlan.IsSkippableQualityFailure(uncertainSide));
    }
    [Theory]
    [InlineData("G3_SEARCH_PIER_SIDE_CHANGED", true)]
    [InlineData("G3_SEARCH_PIER_SIDE_UNKNOWN", false)]
    [InlineData("G3_SEARCH_MOUNT_DISCONNECTED", false)]
    [InlineData("G3_SEARCH_MOUNT_SLEWING", false)]
    [InlineData("G3_SEARCH_PULSE_GUIDING_ACTIVE", false)]
    public async Task ChangedPierStopsOldReturnAndRetainsTypedCauseForTerminalSkip(string code, bool targetScoped)
    {
        var f = new ReturnFixture();
        var failure = GateResult.Fail(code, "reported pierWest -> pierEast");
        f.Trace.Handlers["ValidateG3SearchMountState"] = _ => failure;
        var result = await f.Workflow.ReturnAsync(f.Context, f.State, "original.json", default);
        Assert.False(result.ReturnedToOrigin);
        Assert.Same(failure, result.FailureGate);
        Assert.DoesNotContain("SlewToCoordinatesAsync", f.Trace.Calls);
        Assert.Empty(f.Persisted);
        Assert.Equal(f.State.BudgetLineageId, result.State.BudgetLineageId);
        Assert.Equal(f.State.CumulativeMotionArcseconds, result.State.CumulativeMotionArcseconds);
        var gate = G3WcsRecoveryPolicy.UnreturnedCenteringGate(result.FailureGate, result.Message,
            new Dictionary<string, double> { ["returnedToOrigin"] = 0 });
        Assert.Equal(targetScoped, NativeSequencePlan.IsSkippableQualityFailure(gate));
        Assert.Equal(0, gate.Metrics!["returnedToOrigin"]);
    }

    [Fact]
    public void UnstructuredReturnMessageDoesNotBecomeASkippableSideChange()
    {
        var gate = G3WcsRecoveryPolicy.UnreturnedCenteringGate(null,
            "G3_SEARCH_PIER_SIDE_CHANGED: arbitrary exception text", new Dictionary<string, double>());
        Assert.Equal("G3_WCS_CENTERING_RETURN_BLOCKED", gate.Code);
        Assert.False(NativeSequencePlan.IsSkippableQualityFailure(gate));
    }

    private static G3RunConfiguration Configuration => new(1000, 50, 1, 65535, 1000, 2.4, false, 5,
        null!, null!, 60, 120, 3, WideToSlitTransferMode.Skip, null!);

    private sealed class ReturnFixture
    {
        internal readonly StrictWorkflowPort Trace;
        internal readonly G3AcquisitionReturnWorkflow Workflow;
        internal readonly ObservationContext Context = AtrWorkflowFixture.CreateContext();
        internal readonly List<G3AcquisitionMotionState> Persisted = [];
        internal Coordinates Position = new(10, 20 + 20d / 3600, Epoch.J2000, Coordinates.RAType.Degrees);
        internal readonly G3AcquisitionMotionState State;
        internal ReturnFixture()
        {
            var now = DateTimeOffset.UtcNow;
            State = new(G3AcquisitionMotionState.CurrentSchemaVersion, G3AcquisitionMotionState.CurrentTangentProjectionId,
                "run", new('1', 32), new('A', 64), new('B', 64), new('C', 64), G3AcquisitionMotionKind.LocalSearch,
                G3AcquisitionMotionPhase.ReturnIntent, "pierEast", "J2000", 10, 20, 10, 20, 10, 20,
                0, 20, 20, 30, 100, 500, 20, 2, 10, 300, 50, 2, now, now, now, "evidence.json", "fixture");
            var port = StrictWorkflowPort.Create<IG3AcquisitionReturnPort>(out Trace);
            Trace.Handlers["GetCurrentPosition"] = _ => Position;
            Trace.Handlers["ValidateG3SearchMountState"] = _ => Pass;
            Trace.Handlers["ValidateG3DestinationPierSide"] = _ => Pass;
            Trace.Handlers["ValidateCommandCoordinateHorizon"] = _ => Pass;
            Trace.Handlers["RequireImmediatePhysicalActionGatesAsync"] = _ => Task.CompletedTask;
            Trace.Handlers["PersistG3AcquisitionMotionAsync"] = a => { Persisted.Add((G3AcquisitionMotionState)a[0]!); return Task.CompletedTask; };
            Trace.Handlers["SlewToCoordinatesAsync"] = a => { Position = (Coordinates)a[0]!; return Task.FromResult(true); };
            Trace.Handlers["WaitForSlew"] = _ => Task.CompletedTask;
            Trace.Handlers["WaitForG3PostSlewStabilityAsync"] = _ => Task.FromResult(new G3PostSlewStabilityResult(Pass, Position, now, now, 0, 0));
            Workflow = new(new(2, 3), port);
        }
    }
}
