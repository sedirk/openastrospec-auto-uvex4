using UvexAdv.Observatory;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3GuidingRecoveryReturnPolicyTests
{
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
        var start = source.IndexOf("private async Task<StageResult> AcquireG3SlitFieldAsync(", StringComparison.Ordinal);
        var entry = source[start..source.IndexOf("private StageResult G3FieldPassed(", start, StringComparison.Ordinal)];
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
        Assert.Contains("Math.Max(cumulativeCorrectionDegrees, returned.State.CumulativeMotionArcseconds / 3600d)", helper, StringComparison.Ordinal);
        Assert.Contains("Math.Max(correctionAttempts, returned.State.CorrectionAttempts)", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("SlewToCoordinatesAsync", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("CaptureFullFrame", helper, StringComparison.Ordinal);
        Assert.Contains("G3_GUIDING_RECOVERY_STOP_CHANGED: PHD2 changed before return dispatch", source, StringComparison.Ordinal);
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
        var start = source.IndexOf("if (IsStructuredPhd2GuideSessionLoss(ex)", StringComparison.Ordinal);
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
