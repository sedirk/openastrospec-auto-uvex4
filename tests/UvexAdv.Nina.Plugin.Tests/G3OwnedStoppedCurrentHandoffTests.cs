using System.Security.Cryptography;
using System.Text.Json;
using UvexAdv.Observatory;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3OwnedStoppedCurrentHandoffTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-23T12:58:17Z");
    private static readonly DateTimeOffset Now = Start.AddSeconds(198);
    private static readonly GateResult Pass = GateResult.Pass("fixture", "fixture");
    private const double CurrentRa = 53;
    private const double CurrentDec = 47 + 20d / 3600;

    private static G3AcquisitionMotionState State() => G3MeasuredHandoffPolicyTests.State() with
    {
        StartedUtc = Start, CreatedUtc = Start, UpdatedUtc = Start,
    };

    private static Phd2StateSnapshot Stopped() => Phd2StateSnapshot.Disconnected with
    {
        IsConnected = true, AppState = Phd2AppState.Stopped, ConnectionEpoch = 1, GuideEpoch = 12,
    };

    private static G3GuidingRecoveryReturnPlan Plan(G3AcquisitionMotionState? state = null, bool stop = true,
        DateTimeOffset? now = null, bool prefer = true) => G3GuidingRecoveryReturnPolicy.Plan(state ?? State(),
            "replay", CurrentRa, CurrentDec, "JNOW", "pierEast", stop, 5, 10, now ?? Now, prefer);

    [Fact]
    public async Task AboveNineArcsecondsDefersDebitUntilFreshCaptureInsteadOfDispatchingReturn()
    {
        var original = State();
        var plan = Plan(original);
        Assert.True(plan.CaptureCurrentPositionFirst);
        Assert.Equal("G3_GUIDING_RECOVERY_CURRENT_CAPTURE_RESERVED", plan.Gate.Code);
        Assert.True(plan.ContinuityDeltaArcseconds > 9);
        Assert.Null(plan.ChargedLedger); // A reserve preview is not position adoption.
        var calls = new List<string>();
        var result = await G3GuidingRecoveryReturnPolicy.DispatchAsync(plan,
            () => { calls.Add("remember-current-stop"); return Task.FromResult(plan.Gate); },
            _ => { calls.Add("return-motion"); return Task.FromResult(Pass); });
        Assert.Equal(plan.Gate, result);
        Assert.Equal(new[] { "remember-current-stop" }, calls);
        Assert.Equal(original, State());
        Assert.Equal(1, plan.Gate.Metrics!["reservedReturnActions"]);
    }

    [Fact]
    public async Task OriginalChargedReturnRemainsTheFallbackWhenCurrentCaptureReserveDoesNotFit()
    {
        // The fresh handoff's cleanup reserve no longer fits; the original
        // return-only route still fits exactly within its original 1200 s.
        var plan = Plan(now: Start.AddSeconds(1020));
        Assert.False(plan.CaptureCurrentPositionFirst);
        Assert.Equal("G3_GUIDING_RECOVERY_RETURN_RESERVED", plan.Gate.Code);
        Assert.NotNull(plan.ChargedLedger);
        var calls = new List<string>();
        await G3GuidingRecoveryReturnPolicy.DispatchAsync(plan,
            () => { calls.Add("capture"); return Task.FromResult(Pass); },
            charged =>
            {
                calls.Add("persist-then-original-return");
                Assert.Equal(State().CorrectionAttempts + 1, charged.CorrectionAttempts);
                Assert.Equal(State().StartedUtc, charged.StartedUtc);
                return Task.FromResult(Pass);
            });
        Assert.Equal(new[] { "persist-then-original-return" }, calls);
    }

    [Theory]
    [InlineData("stop")] [InlineData("run")] [InlineData("pier")]
    [InlineData("epoch")] [InlineData("pending")] [InlineData("budget")]
    public async Task InvalidContextCannotReachEitherCaptureOrReturn(string failure)
    {
        var state = failure switch
        {
            "run" => State() with { ObservationRunId = "other" },
            "pier" => State() with { PierSide = "pierWest" },
            "epoch" => State() with { CoordinateEpoch = "J2000" },
            "pending" => State() with { Phase = G3AcquisitionMotionPhase.OutboundIntent },
            "budget" => State() with { CorrectionAttempts = 12 },
            _ => State(),
        };
        var plan = Plan(state, stop: failure != "stop");
        Assert.NotEqual(GateDisposition.Passed, plan.Gate.Disposition);
        Assert.Null(plan.ChargedLedger);
        var result = await G3GuidingRecoveryReturnPolicy.DispatchAsync(plan,
            () => throw new InvalidOperationException("capture must not execute"),
            _ => throw new InvalidOperationException("return must not execute"));
        Assert.Same(plan.Gate, result);
    }

    [Theory]
    [InlineData("normal")] [InlineData("missing-image")] [InlineData("late-pier")]
    [InlineData("late-owner")] [InlineData("late-budget")]
    public async Task ActualAcquisitionWorkflowCapturesBeforeBoundHandoffAndNeverMovesEarly(string mode)
    {
        var state = State();
        var plan = Plan(state);
        var snapshot = Stopped();
        var proof = new Phd2DependencyRebuildStopProof(1, 12, "owned-stop.json");
        var calls = new List<string>();
        var framePath = Path.Combine(Path.GetTempPath(), "synthetic-fresh-g3.fit");
        var frameHash = new string('D', 64); // Synthetic immutable-frame adapter, not a physical FITS claim.
        var binding = G3FieldMountBinding.Create(state.ObservationRunId, state.ActionConfigurationSha256,
            state.CommissioningPresetSha256, framePath, frameHash, Now,
            new(CurrentRa, CurrentDec, "JNOW", "pierEast", Now));
        var field = G3FieldState.Failed(GateResult.Unknown("TARGET_NOT_FOUND", "fresh field needs existing neighbour route"),
            framePath, mountBinding: mode == "missing-image" ? null : binding);
        var port = StrictWorkflowPort.Create<IG3AcquisitionPort>(out var trace);
        trace.Handlers["EvaluateInterlocksAsync"] = _ => Task.FromResult(Pass);
        trace.Handlers["EnsureFineAcquisitionClockStarted"] = _ => null;
        trace.Handlers["CompletePendingG3SearchReturnAsync"] = _ => Task.FromResult<StageResult?>(null);
        trace.Handlers["ReturnAfterOwnedGuidingBeforeReacquisitionAsync"] = _ =>
            G3GuidingRecoveryReturnPolicy.DispatchAsync(plan,
                () => { calls.Add("remember-stop"); return Task.FromResult(plan.Gate); },
                _ => throw new InvalidOperationException("The preferred path must not return first."));
        trace.Handlers["EnsureWideToSlitTransferSkippedEvidenceAsync"] = _ => Task.FromResult("transfer.json");
        trace.Handlers["UsesCatalogPositionOnly"] = _ => false;
        trace.Handlers["PublishTargetBranchAsync"] = _ => Task.CompletedTask;
        trace.Handlers["CaptureAndAnalyzeG3WithSolveLadderAsync"] = _ => CaptureFieldAsync();
        async Task<G3FieldState> CaptureFieldAsync()
        {
            Assert.Equal(new[] { "remember-stop" }, calls);
            Assert.Equal(State(), state); // Neither motion nor debit before the fresh frame.
            return await proof.CaptureAsync(() => snapshot, _ =>
            {
                calls.Add("native-fresh-capture");
                return Task.FromResult(field);
            }, default);
        }
        trace.Handlers["RememberField"] = _ => null;
        trace.Handlers["get_ActiveTargetBranch"] = _ => (TargetAcquisitionBranch?)null;
        trace.Handlers["RunBoundedG3LocalSearchAsync"] = args =>
        {
            Assert.True((bool)args[4]!);
            Assert.Same(field, args[1]);
            Assert.Null(field.Solve); // Do not invent formal WCS for the new field.
            if (mode == "late-owner") snapshot = snapshot with { GuideEpoch = 13 };
            if (!proof.IsCurrent(snapshot))
                return Task.FromResult(new StageResult(GateResult.Unknown("G3_GUIDING_RECOVERY_STOP_CHANGED", "fixture")));
            var gate = G3FieldMountBindingPolicy.ValidateForMotion(field.MountBinding, state.ObservationRunId,
                state.ActionConfigurationSha256, state.CommissioningPresetSha256, framePath, frameHash,
                CurrentRa, CurrentDec, "JNOW", mode == "late-pier" ? "pierWest" : "pierEast", 2);
            if (gate.Disposition != GateDisposition.Passed) return Task.FromResult(new StageResult(gate));
            calls.Add("fresh-binding-validated");
            var currentLedger = mode == "late-budget" ? state with { CumulativeMotionArcseconds = 20000 } : state;
            var radius = G3AcquisitionMotionPlanner.AngularSeparationArcseconds(
                state.OriginRaDegrees, state.OriginDeclinationDegrees, CurrentRa, CurrentDec);
            var debit = G3MeasuredHandoffPolicy.Charge(currentLedger, plan.ContinuityDeltaArcseconds, radius, Now.AddSeconds(1));
            if (debit.Charged is not null) { state = debit.Charged; calls.Add("persist-conservative-debit"); }
            return Task.FromResult(new StageResult(debit.Gate));
        };
        var configuration = new G3RunConfiguration(1000, 50, 1, 65535, 1000, 2.4, false, 5,
            null!, null!, 60, 120, 3, WideToSlitTransferMode.Skip, null!);
        var result = await new G3AcquisitionWorkflow(configuration, port)
            .AcquireAsync(AtrWorkflowFixture.CreateContext(), default, allowChargedCurrentPositionHandoff: true);
        Assert.Equal(new[] { "remember-stop", "native-fresh-capture" }, calls.Take(2));
        if (mode == "normal")
        {
            Assert.Equal("G3_FRESH_HANDOFF_DEBITED", result.Gate.Code);
            Assert.Equal(new[] { "remember-stop", "native-fresh-capture", "fresh-binding-validated", "persist-conservative-debit" }, calls);
            Assert.Equal(State().CumulativeMotionArcseconds + plan.ContinuityDeltaArcseconds, state.CumulativeMotionArcseconds);
            Assert.Equal(State().CorrectionAttempts + 1, state.CorrectionAttempts);
            Assert.Equal(State().StartedUtc, state.StartedUtc);
            Assert.Equal(State().OriginRaDegrees, state.OriginRaDegrees);
        }
        else
        {
            Assert.NotEqual(GateDisposition.Passed, result.Gate.Disposition);
            Assert.DoesNotContain("persist-conservative-debit", calls);
            Assert.Equal(State(), state);
        }
        Assert.DoesNotContain("SlewToCoordinatesAsync", trace.Calls);
    }

    [Fact]
    public async Task DirectAcceptedFieldFinalizesPendingReceiptBeforeAnotherGuideEpochWithoutCoarseMotion()
    {
        var state = State();
        var plan = Plan(state);
        var proof = new Phd2DependencyRebuildStopProof(1, 12, "owned-stop.json");
        var snapshot = Stopped();
        var pending = false;
        var calls = new List<string>();
        var framePath = Path.Combine(Path.GetTempPath(), "synthetic-direct-fresh-g3.fit");
        var hash = new string('D', 64);
        var binding = G3FieldMountBinding.Create(state.ObservationRunId, state.ActionConfigurationSha256,
            state.CommissioningPresetSha256, framePath, hash, Now,
            new(CurrentRa, CurrentDec, "JNOW", "pierEast", Now));
        var field = G3FieldState.Failed(Pass, framePath, mountBinding: binding);
        var port = StrictWorkflowPort.Create<IG3AcquisitionPort>(out var trace);
        trace.Handlers["EvaluateInterlocksAsync"] = _ => Task.FromResult(Pass);
        trace.Handlers["EnsureFineAcquisitionClockStarted"] = _ => null;
        trace.Handlers["CompletePendingG3SearchReturnAsync"] = _ => Task.FromResult<StageResult?>(null);
        trace.Handlers["ReturnAfterOwnedGuidingBeforeReacquisitionAsync"] = _ =>
            G3GuidingRecoveryReturnPolicy.DispatchAsync(plan,
                () => { pending = true; calls.Add("remember-stop"); return Task.FromResult(plan.Gate); },
                _ => throw new InvalidOperationException("no return on current-field path"));
        trace.Handlers["EnsureWideToSlitTransferSkippedEvidenceAsync"] = _ => Task.FromResult("transfer.json");
        trace.Handlers["UsesCatalogPositionOnly"] = _ => false;
        trace.Handlers["PublishTargetBranchAsync"] = _ => Task.CompletedTask;
        trace.Handlers["CaptureAndAnalyzeG3WithSolveLadderAsync"] = _ => proof.CaptureAsync(() => snapshot,
            _ => { calls.Add("fresh-frame"); return Task.FromResult(field); }, default);
        trace.Handlers["RememberField"] = _ => null;
        trace.Handlers["get_PlacementPreset"] = _ => null;
        var accepted = new StageResult(GateResult.Pass("accepted-field", "not exact slit placement"));
        trace.Handlers["AcceptField"] = _ => { calls.Add("accept-field"); return accepted; };
        var configuration = new G3RunConfiguration(1000, 50, 1, 65535, 1000, 2.4, false, 5,
            null!, null!, 60, 120, 3, WideToSlitTransferMode.Skip, null!);
        var acquired = await new G3AcquisitionWorkflow(configuration, port)
            .AcquireAsync(AtrWorkflowFixture.CreateContext(), default, true);
        Assert.True(pending);
        Assert.Equal(State(), state); // Direct acceptance did not magically pay the pending delta.
        var result = await G3GuidingRecoveryReturnPolicy.FinalizeAcceptedFieldAsync(acquired, pending, () =>
        {
            Assert.True(proof.IsCurrent(snapshot));
            var gate = G3FieldMountBindingPolicy.ValidateForMotion(field.MountBinding, state.ObservationRunId,
                state.ActionConfigurationSha256, state.CommissioningPresetSha256, framePath, hash,
                CurrentRa, CurrentDec, "JNOW", "pierEast", 2);
            Assert.Equal(GateDisposition.Passed, gate.Disposition);
            calls.Add("independent-fresh-binding");
            var debit = G3MeasuredHandoffPolicy.Charge(state, plan.ContinuityDeltaArcseconds, 20, Now.AddSeconds(1));
            state = Assert.IsType<G3AcquisitionMotionState>(debit.Charged);
            calls.Add("persist-debit");
            pending = false;
            calls.Add("consume-stop-receipt");
            return Task.FromResult(debit.Gate);
        });
        Assert.Same(accepted, result);
        Assert.False(pending);
        Assert.Equal(new[] { "remember-stop", "fresh-frame", "accept-field", "independent-fresh-binding", "persist-debit", "consume-stop-receipt" }, calls);
        Assert.DoesNotContain("RunG3WcsCenteringAsync", trace.Calls);
        Assert.DoesNotContain("RunBoundedG3LocalSearchAsync", trace.Calls);
        Assert.Equal(State().CorrectionAttempts + 1, state.CorrectionAttempts);
        // A subsequent normal guide may legitimately create a new epoch. The
        // old stop receipt is consumed, not allowed to poison later selection.
        snapshot = snapshot with { AppState = Phd2AppState.Guiding, GuideEpoch = 13 };
        Assert.False(proof.IsCurrent(snapshot));
        var finalizedAgain = 0;
        Assert.Same(result, await G3GuidingRecoveryReturnPolicy.FinalizeAcceptedFieldAsync(result, pending,
            () => { ++finalizedAgain; return Task.FromResult(Pass); }));
        Assert.Equal(0, finalizedAgain);
    }

    [Theory]
    [InlineData("G3_FIELD_MOUNT_BINDING_FRAME_MISSING")]
    [InlineData("G3_GUIDING_RECOVERY_OWNER_CHANGED")]
    [InlineData("G3_GUIDING_RECOVERY_STOP_CHANGED")]
    [InlineData("G3_FIELD_MOUNT_BINDING_PIER_CHANGED")]
    [InlineData("G3_FRESH_HANDOFF_BUDGET_EXHAUSTED")]
    public async Task FailedSuccessfulExitFinalizationCannotReleaseItsAcceptedGateToPhd2(string code)
    {
        var original = new StageResult(Pass);
        var failure = GateResult.Unknown(code, "fixture");
        var calls = 0;
        var result = await G3GuidingRecoveryReturnPolicy.FinalizeAcceptedFieldAsync(original, true,
            () => { ++calls; return Task.FromResult(failure); });
        Assert.False(result.CanAdvance);
        Assert.Same(failure, result.Gate);
        Assert.Equal(1, calls);
        var failedAcquisition = new StageResult(failure);
        Assert.Same(failedAcquisition, await G3GuidingRecoveryReturnPolicy.FinalizeAcceptedFieldAsync(failedAcquisition, true,
            () => throw new InvalidOperationException("Failure must not fabricate successful finalization.")));
    }

    [Fact]
    public async Task SuccessfulExitFinalizerDoesNotSwallowCancellationOrUnknownOwnerException()
    {
        var original = new StageResult(Pass);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => G3GuidingRecoveryReturnPolicy.FinalizeAcceptedFieldAsync(original, true,
            () => Task.FromException<GateResult>(new OperationCanceledException())));
        var expected = new PhysicalActionGateException(GateResult.Unknown("OWNER_UNKNOWN", "fixture"));
        var actual = await Assert.ThrowsAsync<PhysicalActionGateException>(() => G3GuidingRecoveryReturnPolicy.FinalizeAcceptedFieldAsync(original, true,
            () => Task.FromException<GateResult>(expected)));
        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task BoundedFreshRetryRetainsTheExactReceiptUntilSuccessfulDebitWithoutRefreshingBudget()
    {
        var state = State();
        var original = state;
        var proof = new Phd2DependencyRebuildStopProof(1, 12, "owned-stop.json");
        (string Run, string Lineage, Phd2DependencyRebuildStopProof Proof)? pending = null;
        var captures = 0;
        var debits = 0;
        for (var attempt = 0; attempt < 2; ++attempt)
        {
            var selection = G3GuidingRecoveryReturnPolicy.SelectOwnedStopProof(attempt == 0 ? proof : null,
                pending, state.ObservationRunId, state, Stopped());
            Assert.Equal(GateDisposition.Passed, selection.Gate.Disposition);
            Assert.Same(proof, selection.Proof);
            Assert.Equal(original, state);
            var plan = Plan(state, now: Now.AddSeconds(attempt));
            await G3GuidingRecoveryReturnPolicy.DispatchAsync(plan, () =>
            {
                pending = (state.ObservationRunId, state.BudgetLineageId, selection.Proof!);
                return Task.FromResult(plan.Gate);
            }, _ => throw new InvalidOperationException("A read-only retry must not return before capture."));
            await selection.Proof!.CaptureAsync(Stopped, _ => Task.FromResult(++captures), default);
            var result = new StageResult(attempt == 0
                ? GateResult.Unknown("G3_PLATE_SOLVE_LADDER_TRANSIENT_EXHAUSTED", "bounded read-only retry") : Pass);
            await G3GuidingRecoveryReturnPolicy.FinalizeAcceptedFieldAsync(result, pending is not null, () =>
            {
                var debit = G3MeasuredHandoffPolicy.Charge(state, plan.ContinuityDeltaArcseconds, 20, Now.AddSeconds(2));
                state = Assert.IsType<G3AcquisitionMotionState>(debit.Charged);
                ++debits;
                pending = null;
                return Task.FromResult(debit.Gate);
            });
            if (attempt == 0)
            {
                Assert.NotNull(pending);
                Assert.Same(proof, pending.GetValueOrDefault().Proof);
                Assert.Equal(0, debits);
                Assert.Equal(original, state);
            }
        }
        Assert.Null(pending);
        Assert.Equal(2, captures);
        Assert.Equal(1, debits);
        Assert.Equal(original.CorrectionAttempts + 1, state.CorrectionAttempts);
        Assert.Equal(original.StartedUtc, state.StartedUtc);
        Assert.Equal(original.MaximumElapsedSeconds, state.MaximumElapsedSeconds);
        Assert.Equal(original.MaximumCumulativeMotionArcseconds, state.MaximumCumulativeMotionArcseconds);
    }

    [Theory]
    [InlineData("new-receipt")] [InlineData("run")] [InlineData("lineage")]
    [InlineData("pending-motion")] [InlineData("guide-epoch")] [InlineData("owner")]
    public void FreshRetryCannotReplaceOrForgetAnIncompatiblePendingReceipt(string failure)
    {
        var state = State();
        var proof = new Phd2DependencyRebuildStopProof(1, 12, "owned-stop.json");
        var pending = (Run: state.ObservationRunId, Lineage: state.BudgetLineageId, Proof: proof);
        var snapshot = Stopped();
        if (failure == "lineage") state = state with { BudgetLineageId = new string('2', 32) };
        if (failure == "pending-motion") state = state with { Phase = G3AcquisitionMotionPhase.OutboundIntent };
        if (failure == "guide-epoch") snapshot = snapshot with { GuideEpoch = 13 };
        if (failure == "owner") snapshot = snapshot with { ConnectionEpoch = 2 };
        var decision = G3GuidingRecoveryReturnPolicy.SelectOwnedStopProof(
            failure == "new-receipt" ? proof with { } : null, pending,
            failure == "run" ? "other" : state.ObservationRunId, state, snapshot);
        Assert.NotEqual(GateDisposition.Passed, decision.Gate.Disposition);
        Assert.Equal(GateFailureScope.Run, decision.Gate.FailureScope);
        Assert.Null(decision.Proof);
        Assert.Same(proof, pending.Proof); // The original responsibility was not mutated or renewed.
    }

    [Theory]
    [InlineData("loop")] [InlineData("guiding")] [InlineData("epoch")]
    [InlineData("connection")] [InlineData("disconnect")] [InlineData("pause")]
    [InlineData("pending")]
    public async Task OriginalStopReceiptRejectsExternalContextChangeAcrossOneNativeCapture(string change)
    {
        var proof = new Phd2DependencyRebuildStopProof(1, 12, "owned-stop.json");
        var snapshot = Stopped();
        var calls = 0;
        var ex = await Assert.ThrowsAsync<PhysicalActionGateException>(() => proof.CaptureAsync(() => snapshot, _ =>
        {
            ++calls;
            snapshot = change switch
            {
                "loop" => snapshot with { AppState = Phd2AppState.Looping },
                "guiding" => snapshot with { AppState = Phd2AppState.Guiding },
                "epoch" => snapshot with { GuideEpoch = 13 },
                "connection" => snapshot with { ConnectionEpoch = 2 },
                "disconnect" => snapshot with { IsConnected = false },
                "pause" => snapshot with { AutomationPaused = true },
                _ => snapshot with { PendingSettleOperationId = 9 },
            };
            return Task.FromResult(42);
        }, default));
        Assert.Equal("G3_GUIDING_RECOVERY_STOP_CHANGED", ex.Gate.Code);
        Assert.Equal(1, calls);
        Assert.Equal(12, proof.GuideEpoch); // No renewed stop receipt was fabricated.
    }

    [Fact]
    public async Task NativeCapturePreservesSameStoppedReceiptAndNeverRetriesExceptionsOrCancellation()
    {
        var proof = new Phd2DependencyRebuildStopProof(1, 12, "owned-stop.json");
        var snapshot = Stopped();
        var calls = 0;
        Assert.Equal(42, await proof.CaptureAsync(() => snapshot, _ => { ++calls; return Task.FromResult(42); }, default));
        Assert.Equal(1, calls);
        Assert.True(proof.IsCurrent(snapshot));
        calls = 0;
        var expected = new IOException("native capture failed");
        var actual = await Assert.ThrowsAsync<IOException>(() => proof.CaptureAsync(() => snapshot,
            _ => { ++calls; return Task.FromException<int>(expected); }, default));
        Assert.Same(expected, actual);
        Assert.Equal(1, calls);
        using var cancellation = new CancellationTokenSource();
        calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => proof.CaptureAsync(() => snapshot,
            _ => { ++calls; cancellation.Cancel(); return Task.FromResult(42); }, cancellation.Token));
        Assert.Equal(1, calls);
        calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => proof.CaptureAsync(() => snapshot,
            _ => { ++calls; return Task.FromResult(42); }, cancellation.Token));
        Assert.Equal(0, calls);
        var wrong = snapshot with { GuideEpoch = 13 };
        await Assert.ThrowsAsync<PhysicalActionGateException>(() => proof.CaptureAsync(() => wrong,
            _ => { ++calls; return Task.FromResult(42); }, default));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task NativeCaptureTimeoutCannotBecomeAnOrdinaryNextExposureTier()
    {
        var proof = new Phd2DependencyRebuildStopProof(1, 12, "owned-stop.json");
        var calls = 0;
        var failure = await Assert.ThrowsAsync<PhysicalActionGateException>(() => proof.CaptureAsync(() => Stopped(),
            _ => { calls++; return Task.FromException<int>(new Phd2CommandTimeoutException(
                "native single-frame capture", TimeSpan.FromSeconds(20))); }, default));
        Assert.Equal(1, calls);
        Assert.Equal(Phd2StageFailurePolicy.NativeFrameTimeout, failure.Gate.Code);
        Assert.Equal(GateFailureScope.Run, failure.Gate.FailureScope);
        Assert.Null(RealObservationStageRunner.ClassifyG3PlateSolveTransientFailure(
            G3PlateSolveTransientOperation.Capture, failure));
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(
            "unknown-gate", failure.Gate.Code, failure.Gate);
    }

    [Fact]
    public void Hd221662RecordedHandoffAvoidsOne5400ArcsecondReturnPrechargeButDoesNotRefundAnything()
    {
        // Sanitized numeric comparison against the 2026-10-23 21:01:35 record.
        // Recorded delta is conservative max(spherical,TAN); radius below is
        // triangle-inequality upper bound, not a manufactured mount readback.
        var state = State() with { CumulativeMotionArcseconds = 1363.9907779610762, CorrectionAttempts = 2 };
        const double delta = 11.820672735979116;
        const double radiusUpperBound = 1359.3111156298007 + delta;
        var debit = G3MeasuredHandoffPolicy.Charge(state, delta, radiusUpperBound, Now);
        var after = Assert.IsType<G3AcquisitionMotionState>(debit.Charged);
        Assert.Equal(1375.8114506970553, after.CumulativeMotionArcseconds, 8);
        Assert.Equal(3, after.CorrectionAttempts);
        Assert.Equal(1, debit.Gate.Metrics!["reservedReturnActions"]);
        Assert.Equal(6775.811450697055, after.CumulativeMotionArcseconds + state.MaximumSingleCorrectionArcseconds, 8);
        Assert.Equal(4, after.CorrectionAttempts + 1); // Historical return's extra conservative precharge.
        Assert.Equal(state.StartedUtc, after.StartedUtc);
        Assert.Equal(state.MaximumCumulativeMotionArcseconds, after.MaximumCumulativeMotionArcseconds);
        Assert.Equal(state.MaximumCorrectionAttempts, after.MaximumCorrectionAttempts);
        Assert.Equal(state.MaximumElapsedSeconds, after.MaximumElapsedSeconds);
    }

    [Fact]
    public void ProductionWiringRetainsAllPreconditionsAndUsesNativeReceiptInsteadOfLoopTakeover()
    {
        var helper = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.GuidingRecovery.cs"));
        var dispatch = helper.IndexOf("G3GuidingRecoveryReturnPolicy.DispatchAsync", StringComparison.Ordinal);
        Assert.True(dispatch > helper.IndexOf("loaded.State != state", StringComparison.Ordinal));
        Assert.True(dispatch > helper.IndexOf("ValidateIdentityAsync", StringComparison.Ordinal));
        Assert.True(dispatch > helper.IndexOf("RequireImmediatePhysicalActionGatesAsync", StringComparison.Ordinal));
        Assert.True(dispatch > helper.IndexOf("ValidateG3SearchMountState", StringComparison.Ordinal));
        Assert.Contains("preferFreshCurrentPositionHandoff: true", helper, StringComparison.Ordinal);
        Assert.Contains("freshMountBoundImageRequired = true", helper, StringComparison.Ordinal);
        Assert.Contains("formalWcsEstablished = false", helper, StringComparison.Ordinal);
        Assert.Contains("actualHandoffDebitPending = true", helper, StringComparison.Ordinal);
        Assert.True(helper.IndexOf("SelectOwnedStopProof", StringComparison.Ordinal) <
            helper.IndexOf("g3StoppedHandoff = null", StringComparison.Ordinal));
        Assert.Contains("if (selected.Gate.Disposition != GateDisposition.Passed) return selected.Gate", helper, StringComparison.Ordinal);
        var finalizer = helper[helper.IndexOf("private async Task<GateResult> FinalizeOwnedStoppedG3HandoffAsync(", StringComparison.Ordinal)..];
        Assert.True(finalizer.IndexOf("ValidateG3FieldMountBindingForMotionAsync", StringComparison.Ordinal) <
            finalizer.IndexOf("BeginG3AcquisitionMotionAsync", StringComparison.Ordinal));
        Assert.Contains("loaded.State != ledger", finalizer, StringComparison.Ordinal);
        Assert.Contains("ledger.MaximumCumulativeMotionArcseconds, ledger.MaximumCorrectionAttempts", finalizer, StringComparison.Ordinal);
        Assert.DoesNotContain("continuationFamilyAdditional", finalizer, StringComparison.Ordinal);
        Assert.DoesNotContain("attestedLineageMaximum", finalizer, StringComparison.Ordinal);
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var wrapper = source[source.IndexOf("private async Task<StageResult> AcquireG3SlitFieldAsync(", StringComparison.Ordinal)..];
        wrapper = wrapper[..wrapper.IndexOf("private StageResult G3FieldPassed(", StringComparison.Ordinal)];
        Assert.True(wrapper.IndexOf("g3Acquisition.AcquireAsync", StringComparison.Ordinal) <
            wrapper.IndexOf("FinalizeAcceptedFieldAsync", StringComparison.Ordinal));
        Assert.Contains("FinalizeOwnedStoppedG3HandoffAsync(context, token)", wrapper, StringComparison.Ordinal);
        var start = source.IndexOf("private async Task<Phd2SingleFrameResult> CaptureG3FullFrameCoreAsync(", StringComparison.Ordinal);
        var end = source.IndexOf("// PHD2's generic full-frame primitive", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var native = source[start..end];
        Assert.Contains("G3AcquisitionMotionPhase.SettledBudgetLedger", native, StringComparison.Ordinal);
        Assert.Contains("handoff.Run != ledger.ObservationRunId", native, StringComparison.Ordinal);
        Assert.Contains("handoff.Lineage != ledger.BudgetLineageId", native, StringComparison.Ordinal);
        Assert.Contains("handoff.Proof.CaptureAsync", native, StringComparison.Ordinal);
        Assert.Contains("phd2.CaptureSingleFrameWithParametersAsync", native, StringComparison.Ordinal);
        Assert.DoesNotContain("StopCaptureAndConfirmAsync", native, StringComparison.Ordinal);
        Assert.DoesNotContain("CaptureFullFrameAsync", native, StringComparison.Ordinal);
        Assert.DoesNotContain("catch", native, StringComparison.Ordinal);
        var begin = source[source.IndexOf("private async Task<G3AcquisitionMotionState> BeginG3AcquisitionMotionAsync(", StringComparison.Ordinal)..];
        Assert.Contains("handoff.Proof.IsCurrent(phd2.Snapshot)", begin, StringComparison.Ordinal);
        Assert.Contains("hasFreshMountBoundHandoffAuthority", begin, StringComparison.Ordinal);
        Assert.Contains("G3MeasuredHandoffPolicy.Charge(continued", begin, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordedHd221662StopAndReturnEvidenceHashesBindTheNumericRegression()
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var folder = Path.Combine(root, "UVEX-20261023T125614Z-cdae286312eb469");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
        foreach (var kind in new[] { "g3-guiding-recovery-return-reserved", "g3-guiding-recovery-return-result", "phd2-dependency-rebuild-stop-confirmed" })
        {
            var item = manifest.RootElement.GetProperty("evidence").EnumerateArray()
                .Single(x => x.GetProperty("kind").GetString() == kind);
            var bytes = File.ReadAllBytes(item.GetProperty("absolutePath").GetString()!);
            Assert.Equal(item.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes)), ignoreCase: true);
            using var evidence = JsonDocument.Parse(bytes);
            var payload = evidence.RootElement.GetProperty("payload");
            if (kind == "g3-guiding-recovery-return-reserved")
            {
                Assert.Equal(11.820672735979116, payload.GetProperty("continuityDeltaArcseconds").GetDouble(), 8);
                Assert.Equal(1363.9907779610762, payload.GetProperty("before").GetProperty("cumulativeMotionArcseconds").GetDouble(), 8);
            }
            if (kind == "g3-guiding-recovery-return-result")
            {
                Assert.True(payload.GetProperty("returnedToOrigin").GetBoolean());
                Assert.Equal(6775.811450697055, payload.GetProperty("state").GetProperty("cumulativeMotionArcseconds").GetDouble(), 8);
            }
            if (kind == "phd2-dependency-rebuild-stop-confirmed")
                Assert.True(payload.GetProperty("confirmedIdle").GetBoolean());
        }
    }
}
