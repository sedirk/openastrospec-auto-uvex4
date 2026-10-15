using System.Globalization;
using UvexAdv.Nina.Plugin;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3WcsRecoveryPolicyTests
{
    [Theory]
    [InlineData(11548.37, 0, 0, 0, 100, 2, 21600, true)]
    [InlineData(10770.19, 0, 0, 0, 100, 2, 21600, false)]
    [InlineData(10902, 0, 0, 0, 100, 2, 21600, false)]
    [InlineData(11548, 1, 0, 0, 100, 2, 21600, false)]
    [InlineData(11548, 0, 1, 0, 100, 2, 21600, false)]
    [InlineData(11548, 0, 0, 3, 100, 2, 21600, false)]
    [InlineData(double.NaN, 0, 0, 0, 100, 2, 21600, false)]
    [InlineData(11548, 0, 0, 0, double.NaN, 2, 21600, false)]
    [InlineData(11548, 0, 0, 0, 100, 2, double.NaN, false)]
    public void GrossSolvedOffsetChecksWholeRouteWithoutRejectingFeasibleOrPartialRoutes(
        double required, int actions, double cumulative, double radius, double handoff, double tolerance,
        double ceiling, bool impossible)
    {
        var gate = G3WcsRecoveryPolicy.UnreachableRoundTripAtOrigin(actions, cumulative, radius, required, handoff, tolerance, ceiling);
        Assert.Equal(impossible, gate is not null);
        if (gate is null) return;
        Assert.Equal(GateFailureScope.TargetQuality, gate.FailureScope);
        Assert.True(G3WcsRecoveryPolicy.IsReserveFailure(gate));
        Assert.Equal(0, gate.Metrics!["outboundCommandSent"]);
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("cleanup-failure")]
    [InlineData("late-unsafe")]
    [InlineData("late-cancel")]
    public async Task UnreachableSolvedOriginSkipsOnlyAfterIndependentNativeStopBoundary(string mode)
    {
        var reserve = G3WcsRecoveryPolicy.UnreachableRoundTripAtOrigin(0, 0, 0, 11579.84559, 38.26784, 2, 21600)!;
        var terminal = G3WcsRecoveryPolicy.ReturnedBudgetGate(reserve, true,
            new Dictionary<string, double> { ["chargedActions"] = 0 });
        Assert.Equal(0, terminal.Metrics!["outboundCommandSent"]);
        Assert.Contains("stayed", terminal.Message);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("typed-" + mode,
            G3WcsRecoveryPolicy.UnreachableAtOriginCode, terminal);
    }

    [Theory]
    [InlineData(1, 0, true, 1128.4555079707877, 1200, 2, true)]
    [InlineData(0, 0, true, 1128, 1200, 2, false)]
    [InlineData(1, 1, true, 1128, 1200, 2, false)]
    [InlineData(1, 0, false, 1128, 1200, 2, false)]
    [InlineData(1, 0, true, 1199, 1200, 2, false)]
    [InlineData(1, 0, true, double.NaN, 1200, 2, false)]
    [InlineData(1, 0, true, 1128, double.PositiveInfinity, 2, false)]
    [InlineData(1, 0, true, 1128, 1200, -1, false)]
    public void FailedExteriorFieldCanTryActualTargetOnceWithinUnchangedSingleStep(
        int failed, int attempts, bool solved, double distance, double limit, double tolerance, bool expected) =>
        Assert.Equal(expected, G3WcsRecoveryPolicy.CanTryDirectAfterFailedNeighbour(failed, attempts, solved,
            distance, limit, tolerance));

    [Fact]
    public void DirectAfterFailedNeighbourPersistsUseBeforeMoveAndKeepsFullPreflight()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var start = source.IndexOf("var directAfterFailedNeighbour =", StringComparison.Ordinal);
        var charge = source.IndexOf("DirectApproachAfterNeighbourFailureAttempts = state.", start, StringComparison.Ordinal);
        var persist = source.IndexOf("await PersistG3AcquisitionMotionAsync", charge, StringComparison.Ordinal);
        var move = source.IndexOf("SlewToCoordinatesAsync", persist, StringComparison.Ordinal);
        Assert.True(start > 0 && charge > start && persist > charge && move > persist);
        var preflight = source[start..charge];
        Assert.Contains("ValidateOutboundAndReturnReserve", preflight);
        Assert.Contains("ValidateCommandCoordinateHorizon", preflight);
        Assert.Contains("ValidateG3FieldMountBindingForMotionAsync", preflight);
        Assert.Contains("ValidateG3DestinationPierSide", preflight);
        Assert.Contains("ValidateSphericalCommand", preflight);
        Assert.Contains("AllowUnsolvedTargetHandoff: !isSolvedNeighbourApproach", source[move..]);
        Assert.Contains("DirectApproachAfterNeighbourFailureAttempts = lineageCopies.Max", source);
    }

    [Theory]
    [InlineData(35.4359007393, 37.1583296727, 2.49, 3.4679939326, .3826551718)]
    [InlineData(30.5223701788, 35.2387409441, 1.4248683292, 1.5234874450, .3819118812)]
    public void RecordedOctoberSmallResponseCanReplanOnceWithoutClaimingArrival(
        double before, double after, double priorSpread, double freshSpread, double pixelScale)
    {
        Assert.True(G3WcsRecoveryPolicy.CanRetryNearTargetResponse(true, true, true, true,
            before, after, priorSpread, freshSpread, pixelScale, 2, 100, 0));
        Assert.False(G3WcsRecoveryPolicy.CanRetryNearTargetResponse(true, true, true, true,
            before, after, priorSpread, freshSpread, pixelScale, 2, 100, 1));
        Assert.False(G3WcsRecoveryPolicy.RecheckExistingCoarseHandoffBeforeImprovement(
            GateResult.Pass("G3_CATALOG_SHORT_POSITION_CONFIRMED", "measured"), true, after + freshSpread, 20));
    }

    [Theory]
    [InlineData(false, true, true, true, 35, 37, 1, 1, .38, 2, 100, 0)]
    [InlineData(true, false, true, true, 35, 37, 1, 1, .38, 2, 100, 0)]
    [InlineData(true, true, false, true, 35, 37, 1, 1, .38, 2, 100, 0)]
    [InlineData(true, true, true, false, 35, 37, 1, 1, .38, 2, 100, 0)]
    [InlineData(true, true, true, true, 35, 60, 1, 1, .38, 2, 100, 0)]
    [InlineData(true, true, true, true, 101, 99, 1, 1, .38, 2, 100, 0)]
    [InlineData(true, true, true, true, 35, 100, 1, 1, .38, 2, 100, 0)]
    [InlineData(true, true, true, true, 35, double.NaN, 1, 1, .38, 2, 100, 0)]
    [InlineData(true, true, true, true, 35, 37, -1, 1, .38, 2, 100, 0)]
    [InlineData(true, true, true, true, 35, 37, 1, double.PositiveInfinity, .38, 2, 100, 0)]
    [InlineData(true, true, true, true, 35, 37, 1, 1, 0, 2, 100, 0)]
    [InlineData(true, true, true, true, 35, 37, 1, 1, .38, 0, 100, 0)]
    [InlineData(true, true, true, true, 35, 37, 1, 1, .38, 2, 100, -1)]
    public void UncertainIdentityDivergenceInvalidDataOrSpentRetryCannotReplan(
        bool prior, bool fresh, bool solve, bool direct, double before, double after,
        double priorSpread, double freshSpread, double scale, double tolerance, double radius, int used) =>
        Assert.False(G3WcsRecoveryPolicy.CanRetryNearTargetResponse(prior, fresh, solve, direct,
            before, after, priorSpread, freshSpread, scale, tolerance, radius, used));

    [Fact]
    public void ProductionRetryRetainsLedgerAndReentersAllMotionGates()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var start = source.IndexOf("if (G3WcsRecoveryPolicy.CanRetryNearTargetResponse", StringComparison.Ordinal);
        var end = source.IndexOf("// A positively identified", start, StringComparison.Ordinal);
        var branch = source[start..end];
        Assert.Contains("NearTargetResponseRetries = state.NearTargetResponseRetries + 1", branch);
        Assert.Contains("PersistG3AcquisitionMotionAsync", branch);
        Assert.Contains("continue;", branch);
        Assert.DoesNotContain("SlewToCoordinatesAsync", branch);
        Assert.DoesNotContain("CorrectionAttempts =", branch);
        Assert.DoesNotContain("StartedUtc =", branch);
        Assert.DoesNotContain("SettledBudgetLedger", branch);
        Assert.Contains("NearTargetResponseRetries = lineageCopies.Max(copy => copy.NearTargetResponseRetries)", source);
        var stop = source.IndexOf("if (nearTargetResponseStopGate is not null)", end, StringComparison.Ordinal);
        var search = source.IndexOf("if (localSearchBudgetExhausted", stop, StringComparison.Ordinal);
        Assert.True(stop > end && search > stop);
        Assert.Contains("return new StageResult(nearTargetResponseStopGate", source[stop..search]);
    }

    [Theory]
    [InlineData(2483.907, 723.311, 1109.418, 650.0697)]
    [InlineData(2496.0975, 281.3333, 1059.4627, 642.0132)]
    public void RecordedAlmachNeighbourSuccessAdvancesRatherThanChasingOutsideAnchor(
        double x, double y, double prior, double fresh)
    {
        var slit = new PixelPoint(817.473, 426.867);
        var target = new PixelPoint(x, y);
        var oldChoice = G3WcsApproachPolicy.ChooseTargetPixel(target, slit, 1920, 1080,
            arrivalTolerancePixels: 2 / .38);
        Assert.Equal(2460, oldChoice.X, 6);
        var completed = G3WcsRecoveryPolicy.CompletedSolvedNeighbourApproach(true, 1, true, prior, fresh, 2);
        Assert.True(completed);
        Assert.Equal(slit, G3WcsApproachPolicy.ChooseTargetPixel(target, slit, 1920, 1080,
            arrivalTolerancePixels: 2 / .38, completedSolvedNeighbourApproach: completed));
    }

    [Fact]
    public void RecordedHd21455CappedStepFinishesTowardSlitWhenFreshRemainingTravelFitsOneStep()
    {
        // 2026-10-12: new formal WCS after a capped 5306 arcsec move.
        // Another exterior staging field failed and consumed the return budget.
        var slit = new PixelPoint(817.473, 426.867);
        var target = new PixelPoint(8415.245, 12175.212);
        var completed = G3WcsRecoveryPolicy.CompletedSolvedNeighbourApproach(
            true, .5, true, 10770.19038, 5336.51546, 2, 5400);
        Assert.True(completed);
        Assert.Equal(slit, G3WcsApproachPolicy.ChooseTargetPixel(target, slit, 1920, 1080,
            arrivalTolerancePixels: 2 / .380921849, completedSolvedNeighbourApproach: completed));
        Assert.NotEqual(slit, G3WcsApproachPolicy.ChooseTargetPixel(target, slit, 1920, 1080,
            arrivalTolerancePixels: 2 / .380921849));
    }

    [Theory]
    [InlineData(.5, true, 5398, 5400, true)]
    [InlineData(.5, true, 5398.01, 5400, false)]
    [InlineData(.5, false, 5336, 5400, false)]
    [InlineData(.5, true, 10770, 18000, false)]
    [InlineData(.5, true, 5336, double.NaN, false)]
    [InlineData(.5, true, 5336, double.PositiveInfinity, false)]
    [InlineData(.5, true, double.NaN, 5400, false)]
    [InlineData(0, true, 5336, 5400, false)]
    [InlineData(1.1, true, 5336, 5400, false)]
    public void CappedStagingRequiresNewSolveProgressAndUnchangedSingleStepRoom(
        double scale, bool solved, double fresh, double maximum, bool expected) =>
        Assert.Equal(expected, G3WcsRecoveryPolicy.CompletedSolvedNeighbourApproach(
            true, scale, solved, 10770.19038, fresh, 2, maximum));

    [Theory]
    [InlineData(false, 1, true, 642)]
    [InlineData(true, .5, true, 642)]
    [InlineData(true, double.NaN, true, 642)]
    [InlineData(true, 1, false, 642)]
    [InlineData(true, 1, true, 1058)]
    [InlineData(true, 1, true, double.NaN)]
    public void PartialFailedOrNonImprovingLegDoesNotCompleteStaging(bool neighbour, double scale, bool solved, double fresh) =>
        Assert.False(G3WcsRecoveryPolicy.CompletedSolvedNeighbourApproach(neighbour, scale, solved, 1059.4627, fresh, 2));

    [Theory]
    [InlineData(700, 600, 2, true)] // A 100 arcsec neighbour step still leaves 600 arcsec.
    [InlineData(700, 699, 2, false)]
    [InlineData(700, 698, 2, false)]
    [InlineData(700, 701, 2, false)]
    [InlineData(700, double.NaN, 2, false)]
    [InlineData(double.NaN, 600, 2, false)]
    [InlineData(700, -1, 2, false)]
    [InlineData(700, 600, -1, false)]
    public void NeighbourProgressComparesTotalRemainingTravelNotTheLastShortStep(
        double prior, double fresh, double tolerance, bool expected) =>
        Assert.Equal(expected, G3WcsRecoveryPolicy.HasMeasuredApproachProgress(prior, fresh, tolerance));

    [Fact]
    public void TotalRemainingMotionIsCapturedBeforeTheInverseIsReplacedByANeighbour()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var prior = source.IndexOf("var priorDirectCorrection =", StringComparison.Ordinal);
        var neighbour = source.IndexOf("var approachTargetPixel =", prior, StringComparison.Ordinal);
        Assert.True(prior > 0 && neighbour > prior);
        Assert.Contains("inverse.DesiredG3Center", source[prior..neighbour]);
        Assert.Contains("priorRequiredMotionArcseconds, nextRequiredMotionArcseconds, state.ArrivalToleranceArcseconds", source);
        Assert.DoesNotContain("nextRequiredMotionArcseconds < fullMagnitude", source);
    }

    [Theory]
    [InlineData(17.2, true, true)]
    [InlineData(20, true, true)]
    [InlineData(20.01, true, false)]
    [InlineData(17.2, false, false)]
    [InlineData(double.NaN, true, false)]
    [InlineData(-1, true, false)]
    public void ExactExistingCoarseWcsArrivalIsCheckedBeforeImprovement(
        double residual, bool hasSolve, bool expected)
    {
        var gate = GateResult.Pass("G3_FIELD_ANALYZED_RUN_SLIT_GEOMETRY", "Fresh field");
        Assert.Equal(expected, G3WcsRecoveryPolicy.RecheckExistingCoarseHandoffBeforeImprovement(
            gate, hasSolve, residual, 20));
        Assert.False(G3WcsRecoveryPolicy.RecheckExistingCoarseHandoffBeforeImprovement(
            GateResult.Unknown("TARGET_AMBIGUOUS", "No authority"), true, residual, 20));
    }

    [Fact]
    public void FreshCoarseArrivalReentersNormalGateWithoutAnotherMoveOrBudgetReset()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var start = source.IndexOf("if (G3WcsRecoveryPolicy.RecheckExistingCoarseHandoffBeforeImprovement", StringComparison.Ordinal);
        var end = source.IndexOf("if (double.IsFinite(nextRequiredMotionArcseconds)", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        var branch = source[start..end];
        Assert.Contains("continue;", branch);
        Assert.Contains("finePlacementAccepted = false, budgetReset = false", branch);
        Assert.DoesNotContain("Slew", branch);
        Assert.DoesNotContain("Phase =", branch);
    }

    [Theory]
    [InlineData("G3_MOTION_RETURN_CUMULATIVE_RESERVE_LIMIT")]
    [InlineData("G3_MOTION_RETURN_ATTEMPT_RESERVE_LIMIT")]
    [InlineData("G3_MOTION_RETURN_TIME_RESERVE_LIMIT")]
    public void StructuredReserveFailureIsRecognizedWithoutParsingMessage(string code)
    {
        Assert.True(G3WcsRecoveryPolicy.IsReserveFailure(GateResult.Unknown(code, "original reason")));
    }

    [Theory]
    [InlineData("G3_SOLVED_TARGET_OUTSIDE")]
    [InlineData("G3_PLATE_SOLVE_FAILED")]
    [InlineData("G3_FIELD_MOUNT_BINDING_STALE")]
    public void OtherFailuresAreNotReclassifiedByTheirMessage(string code)
    {
        Assert.False(G3WcsRecoveryPolicy.IsReserveFailure(
            GateResult.Unknown(code, "G3_MOTION_RETURN_CUMULATIVE_RESERVE_LIMIT")));
        Assert.False(G3WcsRecoveryPolicy.IsReserveFailure(null));
    }

    [Fact]
    public void GuidanceNamesTheSpentBudgetAndDoesNotRecommendRetryingSearch()
    {
        var gate = GateResult.Unknown(G3WcsRecoveryPolicy.ExhaustedReturnedCode, "original reason");
        var zh = ObservationOperatorGuidance.For(ObservationStage.AcquireG3SlitField, gate, CultureInfo.GetCultureInfo("zh-CN"));
        var en = ObservationOperatorGuidance.For(ObservationStage.AcquireG3SlitField, gate, CultureInfo.GetCultureInfo("en-US"));
        Assert.Contains("回程预留", zh.Recommendation);
        Assert.Contains("cannot replenish", en.Recommendation);
    }

    [Fact]
    public void PresentationKeepsTheWcsFailureAndItsOriginalReserveReason()
    {
        var gate = GateResult.Unknown(G3WcsRecoveryPolicy.ExhaustedReturnedCode,
            "G3_MOTION_RETURN_CUMULATIVE_RESERVE_LIMIT: outbound plus return exceeds the ledger.");
        var presentation = ObservationUiPresentation.Present(ObservationStage.AcquireG3SlitField,
            gate, CultureInfo.GetCultureInfo("zh-CN"));
        Assert.Contains("没有继续邻场搜索", presentation.Summary);
        Assert.Contains("G3_MOTION_RETURN_CUMULATIVE_RESERVE_LIMIT", presentation.TechnicalDetails);
    }

    [Theory]
    [InlineData(21467.8309, 4, 600, true)]
    [InlineData(20500, 4, 600, false)]
    [InlineData(1000, 11, 600, true)]
    [InlineData(1000, 10, 600, false)]
    [InlineData(1000, 4, 1100, true)]
    [InlineData(1000, 4, 900, false)]
    public void SmallFallbackIsNotBlockedMerelyBecauseTheLargeWcsStepCouldNotFit(
        double consumedMotion, int consumedActions, double elapsedSeconds, bool expected)
    {
        var started = DateTimeOffset.Parse("2026-09-07T14:00:00Z");
        var state = new G3AcquisitionMotionState(
            2, G3AcquisitionMotionState.CurrentTangentProjectionId, "run", "lineage",
            new string('A', 64), new string('B', 64), new string('C', 64),
            G3AcquisitionMotionKind.WcsCentering, G3AcquisitionMotionPhase.SettledBudgetLedger,
            "pierWest", "JNOW", 340, 39, 340, 39, 340, 39, 0, 0, 0,
            5400, 18000, 21600, 12, 2, 90, 1200,
            consumedMotion, consumedActions, started, started, started, "evidence.json");
        var search = new G3LocalSearchLimits(G3LocalSearchPattern.SquareSpiral,
            300, 900, 3600, 12, TimeSpan.FromMinutes(20));
        var commissioned = new MotionLimits(1.5, 6, 12, TimeSpan.FromMinutes(20));
        Assert.Equal(expected, G3WcsRecoveryPolicy.HasNoGlobalSearchRoundTripBudget(
            state, search, commissioned, started.AddSeconds(elapsedSeconds)));
        Assert.Equal(consumedMotion, state.CumulativeMotionArcseconds);
        Assert.Equal(consumedActions, state.CorrectionAttempts);
        Assert.Equal(started, state.StartedUtc);
    }
}
