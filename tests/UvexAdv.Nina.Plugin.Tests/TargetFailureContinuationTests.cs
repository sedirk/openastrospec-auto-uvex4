using UvexAdv.Observatory;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class TargetFailureContinuationTests
{
    [Theory]
    [InlineData("skip")]
    [InlineData("cleanup-failure")]
    [InlineData("end-policy")]
    public async Task PreGuideFreshFrameCandidateLossUsesTheSameCheckedNextTargetBoundary(string boundary)
    {
        var failure = new TargetQualityEvidenceException("PHD2_PRE_GUIDE_SELECTION_UNAVAILABLE",
            "Fresh existing-loop frame lost the native pre-guide candidate; no guide/lock/mount request issued.");
        var gate = failure.AfterStop(true).Gate;
        Assert.False(NativeSequencePlan.IsSkippableQualityFailure(failure.AfterStop(false).Gate));
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(boundary, gate.Code, gate,
            ObservationStage.PlaceTargetOnSlit);
    }

    [Theory]
    [InlineData("PHD2_DIRECT_TARGET_POSITION_UNMEASURED", ObservationStage.PlaceTargetOnSlit)]
    [InlineData("PHD2_SLIT_COMPLETION_WINDOW_EXHAUSTED", ObservationStage.SelectAtrExposure)]
    [InlineData("G3_BOUNDED_SEARCH_EXHAUSTED_RETURNED", ObservationStage.AcquireG3SlitField)]
    [InlineData("PHD2_SCIENCE_RECOVERY_BUDGET_UNAVAILABLE", ObservationStage.SelectAtrExposure)]
    [InlineData("G3_SEARCH_NOT_STARTED_RETURNED", ObservationStage.AcquireG3SlitField)]
    [InlineData(G3PostWcsMeasuredHandoffPolicy.OutsideRemainingCode, ObservationStage.AcquireG3SlitField)]
    public async Task October14Front1FailuresActuallyExecuteFollowingNativeTarget(string code, ObservationStage stage)
    {
        var gate=GateResult.TargetQuality(code,"Recorded front1 failure; no fabricated science or new motion budget.");
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("skip",code,gate,stage);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("cleanup-failure",code,gate,stage);
    }

    [Theory]
    [InlineData(0, 40, 100, "G3_MOTION_SINGLE_LIMIT")]
    [InlineData(135, 145, 100, "G3_MOTION_RADIUS_LIMIT")]
    public async Task November19OutboundEnvelopeFailuresActuallyExecuteFollowingNativeTarget(
        double currentRaArcseconds, double proposedRaArcseconds, double maximumRadiusArcseconds, string code)
    {
        var started = DateTimeOffset.UtcNow.AddMinutes(-1);
        var state = new G3AcquisitionMotionState(
            SchemaVersion: G3AcquisitionMotionState.CurrentSchemaVersion,
            TangentProjectionId: G3AcquisitionMotionState.CurrentTangentProjectionId,
            ObservationRunId: "recorded-2026-11-19",
            BudgetLineageId: Guid.Parse("8a3fa3af-cb05-4ef4-93af-70ec1598c20d").ToString("N"),
            ActionConfigurationSha256: new string('A', 64),
            RecoveryContextSha256: new string('B', 64),
            CommissioningPresetSha256: new string('C', 64),
            Kind: G3AcquisitionMotionKind.WcsCentering,
            Phase: G3AcquisitionMotionPhase.SettledBudgetLedger,
            PierSide: "pierEast",
            CoordinateEpoch: "JNOW",
            OriginRaDegrees: 356.85564850923345,
            OriginDeclinationDegrees: 25.59169767504804,
            PriorReportedRaDegrees: 356.85564850923345,
            PriorReportedDeclinationDegrees: 25.59169767504804,
            CommandedRaDegrees: 356.85564850923345,
            CommandedDeclinationDegrees: 25.59169767504804,
            CurrentRaTangentOffsetArcseconds: currentRaArcseconds,
            CurrentDeclinationOffsetArcseconds: 0,
            CommandMagnitudeArcseconds: 0,
            MaximumSingleCorrectionArcseconds: 30,
            MaximumRadiusArcseconds: maximumRadiusArcseconds,
            MaximumCumulativeMotionArcseconds: 21600,
            MaximumCorrectionAttempts: 8,
            ArrivalToleranceArcseconds: 2,
            WorstCaseActionSeconds: 90,
            MaximumElapsedSeconds: 1200,
            CumulativeMotionArcseconds: 0,
            CorrectionAttempts: 0,
            StartedUtc: started,
            CreatedUtc: started,
            UpdatedUtc: started,
            DeclaredEvidencePath: "recorded-g3-wcs-centering.json",
            LastReason: "recorded 2026-11-19 outbound envelope failure");
        var gate = G3AcquisitionMotionPlanner.ValidateOutboundAndReturnReserve(
            state, proposedRaArcseconds, 0, started.AddSeconds(1)).Gate;

        Assert.Equal(code, gate.Code);
        Assert.Equal(GateFailureScope.TargetQuality, gate.FailureScope);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(
            "skip", gate.Code, gate, ObservationStage.AcquireG3SlitField);
    }

    [Theory]
    [InlineData(40, 0, 100, "G3_MOTION_SPHERICAL_SINGLE_LIMIT")]
    [InlineData(135, 145, 100, "G3_MOTION_SPHERICAL_RADIUS_LIMIT")]
    public async Task November19FreshReadbackEnvelopeFailuresActuallyExecuteFollowingNativeTarget(
        double currentRaArcseconds, double commandedRaArcseconds, double maximumRadiusArcseconds, string code)
    {
        var started = DateTimeOffset.UtcNow.AddMinutes(-1);
        var state = new G3AcquisitionMotionState(
            SchemaVersion: G3AcquisitionMotionState.CurrentSchemaVersion,
            TangentProjectionId: G3AcquisitionMotionState.CurrentTangentProjectionId,
            ObservationRunId: "recorded-2026-11-19-fresh-readback",
            BudgetLineageId: Guid.Parse("4f3f944d-e35e-4b28-a1a8-3f212ac0d959").ToString("N"),
            ActionConfigurationSha256: new string('A', 64),
            RecoveryContextSha256: new string('B', 64),
            CommissioningPresetSha256: new string('C', 64),
            Kind: G3AcquisitionMotionKind.WcsCentering,
            Phase: G3AcquisitionMotionPhase.SettledBudgetLedger,
            PierSide: "pierEast",
            CoordinateEpoch: "JNOW",
            OriginRaDegrees: 356.85564850923345,
            OriginDeclinationDegrees: 25.59169767504804,
            PriorReportedRaDegrees: 356.85564850923345,
            PriorReportedDeclinationDegrees: 25.59169767504804,
            CommandedRaDegrees: 356.85564850923345,
            CommandedDeclinationDegrees: 25.59169767504804,
            CurrentRaTangentOffsetArcseconds: currentRaArcseconds,
            CurrentDeclinationOffsetArcseconds: 0,
            CommandMagnitudeArcseconds: 0,
            MaximumSingleCorrectionArcseconds: 30,
            MaximumRadiusArcseconds: maximumRadiusArcseconds,
            MaximumCumulativeMotionArcseconds: 21600,
            MaximumCorrectionAttempts: 8,
            ArrivalToleranceArcseconds: 2,
            WorstCaseActionSeconds: 90,
            MaximumElapsedSeconds: 1200,
            CumulativeMotionArcseconds: 0,
            CorrectionAttempts: 0,
            StartedUtc: started,
            CreatedUtc: started,
            UpdatedUtc: started,
            DeclaredEvidencePath: "recorded-g3-wcs-centering-fresh-readback.json",
            LastReason: "recorded 2026-11-19 fresh-readback envelope failure");
        var reported = G3AcquisitionMotionPlanner.ApplyTangentOffsetArcseconds(
            state.OriginRaDegrees, state.OriginDeclinationDegrees, currentRaArcseconds, 0);
        var commanded = G3AcquisitionMotionPlanner.ApplyTangentOffsetArcseconds(
            state.OriginRaDegrees, state.OriginDeclinationDegrees, commandedRaArcseconds, 0);
        var gate = G3AcquisitionMotionPlanner.ValidateSphericalCommand(
            state, reported.RaDegrees, reported.DecDegrees,
            commanded.RaDegrees, commanded.DecDegrees, 10).Gate;

        Assert.Equal(code, gate.Code);
        Assert.Equal(GateFailureScope.TargetQuality, gate.FailureScope);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(
            "skip", gate.Code, gate, ObservationStage.AcquireG3SlitField);
    }

    [Fact]
    public async Task ExhaustedMeridianSegmentCapacitySkipsTheTargetAndActuallyExecutesTheNextOne()
    {
        var gate = GateResult.TargetQuality(
            "NATIVE_MERIDIAN_SEGMENT_BUDGET_EXHAUSTED",
            "A clock continuation already used the target's only additional segment; no third segment or flip intent may be created.");

        Assert.True(NativeSequencePlan.IsSkippableQualityFailure(gate));
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(
            "skip", gate.Code, gate, ObservationStage.AcquireG3SlitField);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(
            "cleanup-failure", gate.Code, gate, ObservationStage.AcquireG3SlitField);
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("cleanup-failure")]
    public async Task RecordedHd18552WindowRejectionReallyExecutesNextNativeTarget(string boundary)
    {
        var gate = GateResult.TargetQuality(
            HorizonEvaluation.WindowExhaustedCode,
            "HD 18552 exhausted its original target window after retaining one accepted frame.");
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(
            boundary,
            gate.Code,
            gate,
            ObservationStage.RunScienceBlock);
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("cleanup-failure")]
    [InlineData("end-policy")]
    public async Task AmbiguousDirectFallbackCannotGuidePredictionOrEndNightAfterCheckedStop(string boundary)
    {
        var failure = new TargetQualityEvidenceException("PHD2_DIRECT_TARGET_POSITION_UNMEASURED",
            "X Per: the old catalogue cursor is not a unique measured star in the fresh 10ms frame.");
        var gate = failure.AfterStop(true).Gate;
        Assert.False(NativeSequencePlan.IsSkippableQualityFailure(failure.AfterStop(false).Gate));
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(boundary, gate.Code, gate,
            ObservationStage.PlaceTargetOnSlit);
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("cleanup-failure")]
    [InlineData("end-policy")]
    public async Task FinalSnrSampleLossUsesBoundedReselectionThenCheckedNativeTargetContinuation(string boundary)
    {
        var star = new Phd2Point(708.29, 130.89);
        var baseline = Phd2StateSnapshot.Disconnected with { IsConnected = true,
            AppState = Phd2AppState.Selected, ConnectionEpoch = 1, GuideEpoch = 20,
            EventSequence = 100, SelectedStar = star, LockPosition = star };
        // Selected -> loop, then same-frame LockPositionLost + StarLost.
        var lost = baseline with { AppState = Phd2AppState.Looping, LockPosition = null,
            GuideEpoch = 23, EventSequence = 107 };
        Assert.True(Phd2GuideSnrExposurePolicy.CanRecoverCandidateLoss(baseline, lost, lost, star, true, false));
        Assert.False(Phd2GuideSnrExposurePolicy.CanRecoverCandidateLoss(baseline, lost, lost, star, false, false));
        Assert.False(Phd2GuideSnrExposurePolicy.CanRecoverCandidateLoss(baseline, lost, lost, star, true, true));
        var gate = new Phd2NativeGuideSelectionExhaustedException(4,
            ["Pre-guide candidate lost after the final SNR sample; bounded native reselection exhausted."])
            .AfterCheckedStop().Gate;
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(boundary, gate.Code, gate,
            ObservationStage.PlaceTargetOnSlit);
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("cleanup-failure")]
    public async Task NativeNoStarRpcReselectionExhaustionActuallyExecutesFollowingNativeTarget(string boundary)
    {
        var gate = new Phd2NativeGuideSelectionExhaustedException(4,
            [
                $"attempt 1: {Phd2NoGuideStarException.FailureCode}",
                $"attempt 2: {Phd2NoGuideStarException.FailureCode}",
                $"attempt 3: {Phd2NoGuideStarException.FailureCode}",
                $"attempt 4: {Phd2NoGuideStarException.FailureCode}",
            ]).AfterCheckedStop().Gate;

        Assert.Equal("PHD2_OFF_SLIT_NATIVE_SELECTION_EXHAUSTED", gate.Code);
        Assert.Equal(GateFailureScope.TargetQuality, gate.FailureScope);
        Assert.True(NativeSequencePlan.IsSkippableQualityFailure(gate));
        Assert.NotEqual("PHD2_SLIT_PLACEMENT_FAILED_SAFE", gate.Code);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(boundary, gate.Code, gate,
            ObservationStage.PlaceTargetOnSlit);
    }

    public static IEnumerable<object[]> NativeCases()
    {
        foreach (var failure in new[] { "return-reacquire", "relock-reacquire", "atr-clipping", "off-slit", "empty-g3" })
        foreach (var boundary in new[] { "skip", "cleanup-failure", "end-policy" })
            yield return new object[] { failure, boundary };
    }

    [Theory]
    [MemberData(nameof(NativeCases))]
    public async Task ProductionFailureActuallyRunsNextNativeTargetOnlyAfterCheckedBoundary(string failure, string boundary)
    {
        GateResult gate;
        var stage = ObservationStage.PlaceTargetOnSlit;
        switch (failure)
        {
            case "return-reacquire":
            case "relock-reacquire":
                // Exercise both typed producer results and historical known-quality results.
                var original = failure == "return-reacquire"
                    ? GateResult.TargetQuality("G3_POST_WCS_SHORT_UNCONFIRMED", "Fresh optical samples exhausted.")
                    : GateResult.Unknown("G3_BOUNDED_SEARCH_EXHAUSTED_RETURNED", "Legacy returned acquisition failure.");
                gate = ObservationWorkflowResults.WithFailureContext(new(original), failure).Gate;
                break;
            case "atr-clipping":
                var fixture = new AtrWorkflowFixture(1, [30]);
                fixture.Port.NextMetrics = m => m with { HighPercentileAdu = 65000 };
                gate = (await fixture.Science.SelectExposureAsync(fixture.Context, default)).Gate;
                Assert.Equal("NO_SAFE_SATURATION_BACKOFF", gate.Code);
                Assert.Single(fixture.Port.Saved);
                Assert.Equal(0, fixture.Science.AcceptedFrames);
                stage = ObservationStage.SelectAtrExposure;
                break;
            case "off-slit":
                gate = new Phd2NativeGuideSelectionExhaustedException(4, ["Native candidates vetoed."])
                    .AfterCheckedStop().Gate;
                break;
            default:
                gate = RealObservationStageRunner.ClassifyG3EmptyLadder(EmptyAttempts(), 2);
                var retries = new ObservationAutomaticRecoverySession();
                for (var i = 0; i < 3; i++)
                    Assert.True(retries.Evaluate(ObservationStage.AcquireG3SlitField, gate).ShouldRetry);
                Assert.True(retries.Evaluate(ObservationStage.AcquireG3SlitField, gate).Exhausted);
                stage = ObservationStage.AcquireG3SlitField;
                break;
        }
        Assert.True(NativeSequencePlan.IsSkippableQualityFailure(gate));
        // Executes TWO N.I.N.A. containers and their canonical coordinator, including
        // manifest-join race and cleanup. Asserts second FinalizeObservation actually ran.
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(boundary, gate.Code, gate, stage);
    }

    [Theory]
    [InlineData("PHD2_STOP_UNCONFIRMED")]
    [InlineData("PHD2_IDENTITY_STALE")]
    [InlineData("NIGHT_UNSAFE")]
    [InlineData("UNKNOWN_NEW_FAILURE")]
    [InlineData("PHD2_GUIDE_SNR_CONTEXT_CHANGED")]
    public async Task ReacquisitionContextCannotConvertSafetyOrUnknownFailureIntoTargetQuality(string code)
    {
        var original = new StageResult(GateResult.Unknown(code, "Original failure", new Dictionary<string, double> { ["measurement"] = 7 }),
            "original.json", new Dictionary<string, string> { ["owner"] = "original" });
        var result = ObservationWorkflowResults.WithFailureContext(original, "Reacquisition failed.");
        Assert.Equal(code, result.Gate.Code);
        Assert.Equal(original.Gate.FailureScope, result.Gate.FailureScope);
        Assert.Same(original.Gate.Metrics, result.Gate.Metrics);
        Assert.Same(original.Metadata, result.Metadata);
        Assert.Equal(original.EvidencePath, result.EvidencePath);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("unknown-gate", code, result.Gate);
    }

    [Theory]
    [InlineData("invalid-buffer")]
    [InlineData("capture-transient")]
    [InlineData("solver-transient")]
    [InlineData("missing-content")]
    [InlineData("missing-binding")]
    [InlineData("missing-tier")]
    public async Task EmptyImageCannotHideInvalidCaptureEvidenceAndRunNextTarget(string invalid)
    {
        var attempts = EmptyAttempts();
        attempts[1] = invalid switch
        {
            "invalid-buffer" => attempts[1] with { GateCode = "G3_SOLVE_PROBE_PIXEL_BUFFER_UNSUPPORTED" },
            "capture-transient" => attempts[1] with { GateCode = "G3_PLATE_SOLVE_CAPTURE_TRANSIENT_TIER_SKIPPED" },
            "solver-transient" => attempts[1] with { GateCode = "G3_PLATE_SOLVE_SOLVER_TRANSIENT_TIER_SKIPPED" },
            "missing-content" => attempts[1] with { ContentGateCode = null },
            "missing-binding" => attempts[1] with { MountBinding = null },
            _ => attempts[1],
        };
        var gate = RealObservationStageRunner.ClassifyG3EmptyLadder(invalid == "missing-tier" ? attempts[..1] : attempts, 2);
        Assert.Equal(GateFailureScope.Run, gate.FailureScope);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("unknown-gate", gate.Code, gate);
    }

    [Fact]
    public async Task AtrInvalidMetricsAreNotClippingQualityFailure()
    {
        var f = new AtrWorkflowFixture();
        f.Port.NextMetrics = m => m with { HighPercentileAdu = double.NaN };
        var gate = (await f.Science.SelectExposureAsync(f.Context, default)).Gate;
        Assert.Equal(GateFailureScope.Run, gate.FailureScope);
        Assert.False(NativeSequencePlan.IsSkippableQualityFailure(gate));
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("unknown-gate", gate.Code, gate, ObservationStage.SelectAtrExposure);
    }

    private static G3PlateSolveAttemptEvidence[] EmptyAttempts() => Enumerable.Range(1, 2).Select(i =>
    {
        var path = $"synthetic-{i}.fits";
        var binding = new G3FieldMountBinding(1, "test-run", "test-action", "test-preset", path,
            "test-sha", DateTimeOffset.UtcNow, 45, 40, "J2000", "East", DateTimeOffset.UtcNow, "test-binding");
        return new G3PlateSolveAttemptEvidence(i, i * 1000, "G3_PLATE_SOLVE_TIER_FAILED",
            GateDisposition.Indeterminate, false, path, $"attempt-{i}.json", "G3_CLOUD_OR_TRANSPARENCY_INVALID", 0, binding);
    }).ToArray();
}
