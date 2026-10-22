using UvexAdv.Observatory;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3GuidingRecoveryReturnPolicyTests
{
    [Fact]
    public async Task RecordedOctober21Hd219937ReturnRetainsItsAcceptedFrameAndDebitsThenExecutesNextTarget()
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return; // Opt-in, immutable local replay; CI has no raw observations.
        var directory = Path.Combine(root, "UVEX-20261021T124816Z-703ba90f12da429");
        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        var frames = manifest.RootElement.GetProperty("evidence").EnumerateArray()
            .Where(x => x.GetProperty("kind").GetString() == "atr-science-fits").ToArray();
        Assert.Equal(2, frames.Length);
        Assert.Equal("True", frames[0].GetProperty("metadata").GetProperty("qualityAccepted").GetString());
        Assert.Equal("300", frames[0].GetProperty("metadata").GetProperty("exposureSeconds").GetString());
        Assert.Equal("GUIDING_UNSTABLE", frames[1].GetProperty("metadata").GetProperty("qualityCode").GetString());
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        using var reserved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(
            Path.Combine(directory, "evidence"), "*-g3-guiding-recovery-return-reserved.json").Single()));
        using var returned = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(
            Path.Combine(directory, "evidence"), "*-g3-guiding-recovery-return-result.json").Single()));
        var before = System.Text.Json.JsonSerializer.Deserialize<G3AcquisitionMotionState>(
            reserved.RootElement.GetProperty("payload").GetProperty("charged"), options)!;
        var result = returned.RootElement.GetProperty("payload");
        var after = System.Text.Json.JsonSerializer.Deserialize<G3AcquisitionMotionState>(result.GetProperty("state"), options)!;
        Assert.False(result.GetProperty("returnedToOrigin").GetBoolean());
        Assert.Equal(before.StartedUtc, after.StartedUtc);
        Assert.Equal(before.ExcludedAtrExposureSeconds, after.ExcludedAtrExposureSeconds);
        Assert.Equal(before.CorrectionAttempts + 1, after.CorrectionAttempts);
        Assert.Equal(before.CumulativeMotionArcseconds + before.MaximumSingleCorrectionArcseconds, after.CumulativeMotionArcseconds, 8);
        Assert.Equal(before.MaximumElapsedSeconds, after.MaximumElapsedSeconds);
        var recorded = manifest.RootElement.GetProperty("gates").GetProperty("RunScienceBlock");
        Assert.Equal("G3_SEARCH_PIER_SIDE_CHANGED", recorded.GetProperty("code").GetString());
        var classified = G3GuidingRecoveryReturnPolicy.FailureGate(
            GateResult.Fail(recorded.GetProperty("code").GetString()!, recorded.GetProperty("message").GetString()!),
            result.GetProperty("message").GetString()!);
        // Use the production native containers, not just a classifier assertion.
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("skip", classified.Code, classified,
            ObservationStage.RunScienceBlock);
    }

    [Fact]
    public void Hd18552Completed600SecondProbeDoesNotConsumeTheOriginalReturnClock()
    {
        var started = DateTimeOffset.Parse("2026-10-13T16:03:04.5499177Z");
        var state = State() with { ObservationRunId = "hd18552", OriginRaDegrees = 45.48297375,
            OriginDeclinationDegrees = 38.23936944444444, PriorReportedRaDegrees = 45.37885,
            PriorReportedDeclinationDegrees = 38.283476944444445, CommandedRaDegrees = 45.37798891313855,
            CommandedDeclinationDegrees = 38.283198736924284, CurrentRaTangentOffsetArcseconds = -294.23710079019367,
            CurrentDeclinationOffsetArcseconds = 158.9526743722364, CumulativeMotionArcseconds = 338.61698411637,
            CorrectionAttempts = 1, StartedUtc = started, CreatedUtc = started, UpdatedUtc = started };
        var now = started.AddSeconds(1136.1120301);
        G3GuidingRecoveryReturnPlan Recover(G3AcquisitionMotionState s) => G3GuidingRecoveryReturnPolicy.Plan(s,
            "hd18552", 45.43549458333331, 38.24236194444445, "JNOW", "pierWest", true, 5, 10, now);
        Assert.Equal("G3_GUIDING_RECOVERY_RETURN_RESERVE_LIMIT", Recover(state).Gate.Code);
        var credited = Assert.IsType<G3AcquisitionMotionState>(G3SettledScienceContinuationPolicy.CreditExposure(
            state, state, started.AddSeconds(450), started.AddSeconds(1055), 600, 605));
        Assert.Equal(600, credited.ExcludedAtrExposureSeconds); // No five-second readout credit.
        Assert.Equal(state.StartedUtc, credited.StartedUtc);
        Assert.Equal(state.MaximumElapsedSeconds, credited.MaximumElapsedSeconds);
        Assert.Equal(state.CorrectionAttempts, credited.CorrectionAttempts);
        Assert.Equal(state.CumulativeMotionArcseconds, credited.CumulativeMotionArcseconds);
        var plan = Recover(credited);
        Assert.Equal(GateDisposition.Passed, plan.Gate.Disposition);
        Assert.Equal(1, plan.ReservedReturnActions);
        var step = G3AcquisitionMotionPlanner.PlanNextReturnStep(plan.ChargedLedger!,
            45.43549458333331, 38.24236194444445, 10, now);
        Assert.Equal(GateDisposition.Passed, step.Gate.Disposition);
        Assert.Equal(state.OriginRaDegrees, step.CommandedRaDegrees, 9);
        // Changed ledger, outstanding motion and duplicate receipt grant no credit.
        Assert.Null(G3SettledScienceContinuationPolicy.CreditExposure(state, credited, started, now, 600, 600));
        var pending = state with { Phase = G3AcquisitionMotionPhase.AwaitingFreshSolve };
        Assert.Null(G3SettledScienceContinuationPolicy.CreditExposure(pending, pending, started, now, 600, 600));
    }

    [Theory]
    [InlineData(-1, 600)] [InlineData(600, double.NaN)] [InlineData(double.PositiveInfinity, 600)]
    public void InvalidExposureClockCannotMintRecoveryTime(double requested, double elapsed)
    {
        var state = State();
        Assert.Null(G3SettledScienceContinuationPolicy.CreditExposure(state, state, Started, Now, requested, elapsed));
        Assert.NotEmpty((state with { ExcludedAtrExposureSeconds = 1 }).Validate());
    }

    [Fact]
    public void PassiveCreditRoundTripsWithoutChangingTheOriginOrMintingAnotherReceipt()
    {
        var state = State();
        var credited = Assert.IsType<G3AcquisitionMotionState>(G3SettledScienceContinuationPolicy.CreditExposure(
            state, state, Started.AddSeconds(10), Started.AddSeconds(615), 600, 605));
        var restored = System.Text.Json.JsonSerializer.Deserialize<G3AcquisitionMotionState>(
            System.Text.Json.JsonSerializer.Serialize(credited))!;
        Assert.Equal(credited, restored);
        Assert.Empty(restored.Validate());
        Assert.Equal(state.StartedUtc, restored.StartedUtc);
        Assert.Equal(state.OriginRaDegrees, restored.OriginRaDegrees);
        Assert.Equal(state.StartedUtc.AddSeconds(600), restored.MotionClockStartedUtc);
        Assert.Null(G3SettledScienceContinuationPolicy.CreditExposure(restored, restored,
            Started.AddSeconds(10), Started.AddSeconds(615), 600, 605));
    }

    private static readonly DateTimeOffset Started = DateTimeOffset.Parse("2026-09-08T14:24:52Z");
    private static readonly DateTimeOffset Now = Started.AddMinutes(10);

    // Sanitized numeric replay of the failed 10 Lac handoff, not a live ledger.
    private static G3AcquisitionMotionState State() => new(
        2, G3AcquisitionMotionState.CurrentTangentProjectionId, "run-replay",
        "11111111111111111111111111111111", new('A', 64), new('B', 64), new('C', 64),
        G3AcquisitionMotionKind.WcsCentering, G3AcquisitionMotionPhase.SettledBudgetLedger,
        "pierWest", "JNOW", 340.1229079166667, 39.19229333333333,
        340.0379683333333, 39.20103972222223, 340.038496805121, 39.20067658984426,
        -236.96102836057932, 31.598015173556057, 0,
        5400, 18000, 21600, 8, 2, 90, 1200, 271.6072434372361, 2,
        Started, Started, Started, "replay-declaration.json");

    private static G3GuidingRecoveryReturnPlan Plan(G3AcquisitionMotionState? state = null, bool stop = true) =>
        G3GuidingRecoveryReturnPolicy.Plan(state ?? State(), "run-replay", 340.06963, 39.19148166666666,
            "JNOW", "pierWest", stop, 5, 10, Now);

    [Fact]
    public void Measured95ArcsecondHandoffReservesARealReturnWithoutAdoptingPositionOrResettingBudget()
    {
        var before = State();
        var plan = Plan(before);
        Assert.Equal(GateDisposition.Passed, plan.Gate.Disposition);
        Assert.InRange(plan.ContinuityDeltaArcseconds, 94.7, 94.9);
        Assert.Equal(1, plan.ReservedReturnActions);
        var charged = Assert.IsType<G3AcquisitionMotionState>(plan.ChargedLedger);
        Assert.Equal(before.CumulativeMotionArcseconds + plan.ContinuityDeltaArcseconds, charged.CumulativeMotionArcseconds);
        Assert.Equal(before.CorrectionAttempts + 1, charged.CorrectionAttempts);
        Assert.Equal(before, charged with { CumulativeMotionArcseconds = before.CumulativeMotionArcseconds,
            CorrectionAttempts = before.CorrectionAttempts, UpdatedUtc = before.UpdatedUtc, LastReason = before.LastReason });
        var step = G3AcquisitionMotionPlanner.PlanNextReturnStep(charged, 340.06963, 39.19148166666666, 10, Now);
        Assert.Equal(GateDisposition.Passed, step.Gate.Disposition);
        Assert.False(step.AlreadyAtOrigin);
        Assert.Equal(before.OriginRaDegrees, step.CommandedRaDegrees, 8);
        Assert.Equal(before.OriginDeclinationDegrees, step.CommandedDeclinationDegrees, 8);
        // Exercise the same return precharge and origin settlement accounting.
        var returned = charged with { CumulativeMotionArcseconds = charged.CumulativeMotionArcseconds + charged.MaximumSingleCorrectionArcseconds,
            CorrectionAttempts = charged.CorrectionAttempts + 1 };
        Assert.True(G3AcquisitionMotionPlanner.PlanNextReturnStep(returned,
            step.CommandedRaDegrees, step.CommandedDeclinationDegrees, 10, Now.AddSeconds(10)).AlreadyAtOrigin);
        Assert.Equal(before.StartedUtc, returned.StartedUtc);
        Assert.Empty(returned.Validate());
    }

    [Fact]
    public void OrdinaryReadbackVariationDoesNotCauseAnExtraReturnOrCharge()
    {
        var s = State();
        var plan = G3GuidingRecoveryReturnPolicy.Plan(s, "run-replay", s.PriorReportedRaDegrees,
            s.PriorReportedDeclinationDegrees, "JNOW", "pierWest", false, 5, 10, Now);
        Assert.Equal(GateDisposition.Passed, plan.Gate.Disposition);
        Assert.Null(plan.ChargedLedger);
    }

    [Theory]
    [InlineData("attempt")]
    [InlineData("cumulative")]
    [InlineData("elapsed")]
    [InlineData("radius")]
    [InlineData("single")]
    public void AllOriginalBoundsRemainHardLimits(string bound)
    {
        var s = State();
        s = bound switch
        {
            "attempt" => s with { CorrectionAttempts = 7 },
            "cumulative" => s with { CumulativeMotionArcseconds = 21500 },
            "elapsed" => s with { MaximumElapsedSeconds = 650 },
            "radius" => s with { MaximumSingleCorrectionArcseconds = 100, MaximumRadiusArcseconds = 100 },
            _ => s with { MaximumSingleCorrectionArcseconds = 90 },
        };
        var plan = Plan(s);
        Assert.NotEqual(GateDisposition.Passed, plan.Gate.Disposition);
        Assert.Null(plan.ChargedLedger);
        Assert.Equal(bound is "attempt" or "cumulative" or "elapsed" ? GateFailureScope.TargetQuality : GateFailureScope.Run,
            plan.Gate.FailureScope);
    }

    public static IEnumerable<object[]> BudgetBoundaries() =>
        from budget in new[] { "attempt", "cumulative", "elapsed" }
        from boundary in new[] { "skip", "cleanup-failure", "end-policy" }
        select new object[] { budget, boundary };

    [Theory]
    [MemberData(nameof(BudgetBoundaries))]
    public async Task RecoveryBudgetFailureDuringScienceExecutesNextNativeTargetOnlyAfterCheckedStop(string budget, string boundary)
    {
        var original = budget switch
        {
            "attempt" => State() with { CorrectionAttempts = 7 },
            "cumulative" => State() with { CumulativeMotionArcseconds = 21500 },
            _ => State() with { MaximumElapsedSeconds = 650 },
        };
        var before = original with { };
        var plan = Plan(original);
        Assert.Equal("G3_GUIDING_RECOVERY_RETURN_RESERVE_LIMIT", plan.Gate.Code);
        Assert.Equal(GateFailureScope.TargetQuality, plan.Gate.FailureScope);
        Assert.Equal(before, original);
        Assert.Null(plan.ChargedLedger); // No new motion authority or persisted charge on refusal.
        Assert.True(NativeSequencePlan.IsSkippableQualityFailure(plan.Gate));
        Assert.False(NativeSequencePlan.IsSkippableQualityFailure(GateResult.Unknown(plan.Gate.Code, "Unverified context")));
        Assert.NotNull(plan.Gate.Metrics);
        Assert.Equal(600, plan.Gate.Metrics["elapsedSeconds"]);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(boundary, plan.Gate.Code,
            plan.Gate, ObservationStage.RunScienceBlock);
    }

    [Fact]
    public void Hd218393ElapsedBudgetRetainsOriginalClockAndStopsBeforeAnyReturn()
    {
        // Recorded numeric ledger from 2026-10-12, with synthetic identity and
        // a displaced post-guide position inside the commissioned geometry.
        // No raw data or machine-local identity is embedded or rewritten.
        var start = DateTimeOffset.Parse("2026-10-12T14:30:54.3681872Z");
        var end = DateTimeOffset.Parse("2026-10-12T14:58:27.566688Z");
        var state = State() with
        {
            PierSide = "pierEast", OriginRaDegrees = 347.0837191666667, OriginDeclinationDegrees = 50.34157916666667,
            PriorReportedRaDegrees = 347.2377775, PriorReportedDeclinationDegrees = 50.19137222222222,
            CommandedRaDegrees = 347.2380841751832, CommandedDeclinationDegrees = 50.191428752994895,
            CurrentRaTangentOffsetArcseconds = 355.0767163308616, CurrentDeclinationOffsetArcseconds = -540.3795281525065,
            CumulativeMotionArcseconds = 682.5347126193966, CorrectionAttempts = 3,
            StartedUtc = start, UpdatedUtc = end, CreatedUtc = start,
        };
        var plan = G3GuidingRecoveryReturnPolicy.Plan(state, "run-replay", 347.25, 50.20,
            "JNOW", "pierEast", true, 5, 10, end);
        Assert.Equal(GateFailureScope.TargetQuality, plan.Gate.FailureScope);
        Assert.Null(plan.ChargedLedger);
        var metrics = Assert.IsAssignableFrom<IReadOnlyDictionary<string, double>>(plan.Gate.Metrics);
        Assert.InRange(metrics["elapsedSeconds"], 1653, 1654);
        Assert.Equal(1200, metrics["maximumElapsedSeconds"]);
        Assert.True(metrics["requiredSeconds"] > metrics["maximumElapsedSeconds"]);
        Assert.True(metrics["requiredActions"] <= metrics["maximumActions"]);
        Assert.True(metrics["requiredMotionArcseconds"] <= metrics["maximumMotionArcseconds"]);
        Assert.Equal(start, state.StartedUtc);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("run")]
    [InlineData("epoch")]
    [InlineData("pier")]
    [InlineData("pending")]
    [InlineData("position")]
    [InlineData("nonfinite")]
    public async Task ExhaustedClockCannotHideInvalidRecoveryContextOrStartNextTarget(string failure)
    {
        var state = State() with { MaximumElapsedSeconds = 650 };
        state = failure switch
        {
            "run" => state with { ObservationRunId = "another-run" },
            "epoch" => state with { CoordinateEpoch = "J2000" },
            "pier" => state with { PierSide = "pierEast" },
            "pending" => state with { Phase = G3AcquisitionMotionPhase.OutboundIntent },
            "position" => state with { MaximumSingleCorrectionArcseconds = 90 },
            _ => state,
        };
        var plan = G3GuidingRecoveryReturnPolicy.Plan(state, "run-replay",
            failure == "nonfinite" ? double.NaN : 340.06963, 39.19148166666666,
            "JNOW", "pierWest", failure != "stop", 5, 10, Now);
        Assert.Equal(GateFailureScope.Run, plan.Gate.FailureScope);
        Assert.Null(plan.ChargedLedger);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("unknown-gate", plan.Gate.Code,
            plan.Gate, ObservationStage.RunScienceBlock);
    }

    [Fact]
    public void MissingStopWrongRunEpochPierPendingIntentAndNonfiniteCoordinatesRefuseReturn()
    {
        Assert.NotEqual(GateDisposition.Passed, Plan(stop: false).Gate.Disposition);
        foreach (var s in new[] { State() with { ObservationRunId = "another-run" },
            State() with { CoordinateEpoch = "J2000" }, State() with { PierSide = "pierEast" },
            State() with { Phase = G3AcquisitionMotionPhase.OutboundIntent },
            State() with { UpdatedUtc = Now.AddMinutes(1) } })
            Assert.NotEqual(GateDisposition.Passed, Plan(s).Gate.Disposition);
        Assert.NotEqual(GateDisposition.Passed, G3GuidingRecoveryReturnPolicy.Plan(State(), "run-replay",
            double.NaN, 39, "JNOW", "pierWest", true, 5, 10, Now).Gate.Disposition);
    }

    [Fact]
    public void StopProofIsInvalidatedByRestartReconnectPauseOrAnyNewCapture()
    {
        var proof = new Phd2DependencyRebuildStopProof(1, 102, "stop.json");
        var stopped = Phd2StateSnapshot.Disconnected with { IsConnected = true,
            AppState = Phd2AppState.Stopped, ConnectionEpoch = 1, GuideEpoch = 102 };
        Assert.True(proof.IsCurrent(stopped));
        Assert.False((proof with { EvidencePath = "" }).IsCurrent(stopped));
        foreach (var s in new[] { stopped with { ConnectionEpoch = 2 }, stopped with { GuideEpoch = 103 },
            stopped with { AutomationPaused = true }, stopped with { Phd2Paused = true },
            stopped with { IsConnected = false }, stopped with { AppState = Phd2AppState.Guiding },
            stopped with { AppState = Phd2AppState.Looping }, stopped with { PendingSettleOperationId = 1 } })
            Assert.False(proof.IsCurrent(s));
    }

    [Fact]
    public void ProductionGatesReturnBeforeCaptureAndPersistsAccountingBeforeNativeMotion()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var entry = WorkflowSource.Acquisition;
        Assert.True(entry.IndexOf("ReturnAfterOwnedGuidingBeforeReacquisitionAsync", StringComparison.Ordinal) <
            entry.IndexOf("CaptureAndAnalyzeG3WithSolveLadderAsync", StringComparison.Ordinal));
        Assert.Contains("if (guidingReturn.Disposition != GateDisposition.Passed)", entry, StringComparison.Ordinal);
        var stopStart = source.IndexOf("private async Task<GateResult> EnsurePhdStoppedForAutomaticRebuildAsync(", StringComparison.Ordinal);
        var stop = source[stopStart..source.IndexOf("public override async Task<GateResult> RevalidateAsync(", stopStart, StringComparison.Ordinal)];
        Assert.True(stop.IndexOf("automaticRebuildStopProof?.IsCurrent", StringComparison.Ordinal) <
            stop.IndexOf("automaticRebuildStopProof = null", StringComparison.Ordinal));
        var helper = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.GuidingRecovery.cs"));
        Assert.True(helper.IndexOf("PersistG3AcquisitionMotionAsync(plan.ChargedLedger", StringComparison.Ordinal) <
            helper.IndexOf("ReturnDurableG3AcquisitionToOriginAsync", StringComparison.Ordinal));
        Assert.Contains("loaded.State != state", helper, StringComparison.Ordinal);
        Assert.Contains("ValidateIdentityAsync", helper, StringComparison.Ordinal);
        Assert.Contains("returned.ReturnedToOrigin", helper, StringComparison.Ordinal);
        Assert.Contains("g3-guiding-recovery-return-declined", helper, StringComparison.Ordinal);
        Assert.Contains("returnCommandSent = false", helper, StringComparison.Ordinal);
        Assert.Contains("Math.Max(cumulativeCorrectionDegrees, returned.State.CumulativeMotionArcseconds / 3600d)", helper, StringComparison.Ordinal);
        Assert.Contains("Math.Max(correctionAttempts, returned.State.CorrectionAttempts)", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("SlewToCoordinatesAsync", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("CaptureFullFrame", helper, StringComparison.Ordinal);
        Assert.Contains("G3_GUIDING_RECOVERY_STOP_CHANGED: PHD2 changed before return dispatch", WorkflowSource.Return, StringComparison.Ordinal);
    }

    [Fact]
    public void InternalReturnCalibrationAndStructuredLossRetainCheckedStopAuthorityBeforeCommonReacquisition()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.Phd2SlitPlacement.cs"));
        var returnStart = source.IndexOf("private async Task<StageResult> ReturnPhd2LockToOriginCoreAsync(", StringComparison.Ordinal);
        var returning = source[returnStart..];
        Assert.True(returning.IndexOf("StopPhdAfterOriginReachedWithRetryAsync", StringComparison.Ordinal) <
            returning.IndexOf("automaticRebuildStopProof = returnProof", StringComparison.Ordinal));
        Assert.Contains("returnStopSnapshot.GuideEpoch != state.GuideEpoch", returning, StringComparison.Ordinal);
        Assert.Contains("!returnProof.IsCurrent(phd2.Snapshot)", returning, StringComparison.Ordinal);
        Assert.Contains("phd2-lock-origin-stop-confirmed", returning, StringComparison.Ordinal);
        Assert.Contains("automaticRebuildStopProof = calibrationStop", source, StringComparison.Ordinal);
        Assert.Contains("if (relockStop.Disposition != GateDisposition.Passed) return new StageResult(relockStop)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StructuredLossCatchChecksStopBeforeClearingItsOwnerOrCapturingAgain()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.Phd2SlitPlacement.cs"));
        var adaptiveStart = source.IndexOf("if (AdaptiveGuideRecoveryEnabled &&", StringComparison.Ordinal);
        var adaptiveStop = source.IndexOf("var recoveryStop = await EnsurePhdStoppedForAutomaticRebuildAsync(", adaptiveStart, StringComparison.Ordinal);
        var adaptiveReturn = source.IndexOf("return Attention(ObservationStage.PlaceTargetOnSlit, \"PHD2_STRUCTURED_GUIDE_SESSION_LOSS\"", adaptiveStop, StringComparison.Ordinal);
        Assert.True(adaptiveStart >= 0 && adaptiveStop > adaptiveStart && adaptiveReturn > adaptiveStop);
        Assert.Contains("if (recoveryStop.Disposition != GateDisposition.Passed)", source[adaptiveStop..adaptiveReturn]);
        Assert.Contains("return new StageResult(recoveryStop with", source[adaptiveStop..adaptiveReturn]);
        Assert.Contains("Original failure: {ex.Message}", source[adaptiveStop..adaptiveReturn]);
        Assert.Contains("triggerEvidence", source[adaptiveStop..adaptiveReturn]);
        var start = source.IndexOf("if (!AdaptiveGuideRecoveryEnabled && IsStructuredPhd2GuideSessionLoss(ex)", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("return await PlaceTargetOnSlitWithPhd2Async(", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var recovery = source[start..end];
        var stop = recovery.IndexOf("EnsurePhdStoppedForAutomaticRebuildAsync(", StringComparison.Ordinal);
        var check = recovery.IndexOf("if (relockStop.Disposition != GateDisposition.Passed) return new StageResult(relockStop)", StringComparison.Ordinal);
        var clear = recovery.IndexOf("phd2SlitPlacementSession = null", StringComparison.Ordinal);
        var capture = recovery.IndexOf("AcquireG3SlitFieldAsync(", StringComparison.Ordinal);
        Assert.True(stop >= 0 && check > stop && clear > check && capture > clear);
        Assert.DoesNotContain("catch { }", recovery, StringComparison.Ordinal);
        Assert.Contains("lostLockReacquisitionDepth == 0", recovery, StringComparison.Ordinal);
        Assert.Contains("Phase == Phd2LockShiftPendingPhase.SettledBudgetLedger", recovery, StringComparison.Ordinal);
    }
}
