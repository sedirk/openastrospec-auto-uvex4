using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2FreshResidualPromotionSafetyTests
{
    private static readonly string Source = File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "src",
        "UvexAdv.Nina.Plugin",
        "RealObservationStageRunner.Phd2SlitPlacement.cs"));

    [Fact]
    public void WholeTripDenialCannotFallThroughToAnIndividuallyAffordableDispatch()
    {
        var body = Slice(Source,
            "private async Task<StageResult> PlaceTargetOnSlitWithPhd2Async(",
            "private Task<StageResult> ReturnPhd2LockToOriginAsync(");
        AssertOrdered(body,
            "if (plan.IsComplete || supervisedSlitPrecisionWarning)",
            "var nextAcquisitionBudget = Phd2SlitLockShiftPlanner.EvaluateAcquisitionBudget(",
            "session.Qualification, session.GuideMode, session.LastMeasurement.Measurement, ledger,",
            "if (!nextAcquisitionBudget.IsAllowed)",
            "return await HandleDeniedPhd2AcquisitionBudgetAsync(",
            "var stage = plan.Stage!;",
            "var chargedLedger = ledger with",
            "exact = await phd2.SetExactLockPositionAsync(");
        var initialDiagnostic = body[body.IndexOf("var acquisitionBudget =", StringComparison.Ordinal)..
            body.IndexOf("while (true)", body.IndexOf("var acquisitionBudget =", StringComparison.Ordinal), StringComparison.Ordinal)];
        Assert.DoesNotContain("ReacquireG3ForPhd2HandoffAsync", initialDiagnostic, StringComparison.Ordinal);

        var handoff = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "UvexAdv.Nina.Plugin",
            "RealObservationStageRunner.Handoff.cs"));
        AssertOrdered(handoff,
            "private async Task<StageResult> HandleDeniedPhd2AcquisitionBudgetAsync(",
            "Phase: not Phd2LockShiftPendingPhase.SettledBudgetLedger",
            "return await ReturnPhd2LockToOriginAsync(",
            "Phd2HandoffRecoveryPolicy.ShouldReacquire(",
            "return await ReacquireG3ForPhd2HandoffAsync(",
            "GateResult.Unknown(budget.Code,");
    }

    [Fact]
    public void SlitApertureWarningConvertsTheTargetToTheRuntimeSlitsFrameLocalDomain()
    {
        AssertOrdered(Source,
            "var slitApertureResiduals = targetCompletionWindow.Select(item =>",
            "Phd2PlacementGuideWindowPolicy.ProjectOnMeasuredSlit(",
            "ToFrameLocal(item.Measurement.TargetCentroid, preset),",
            "item.RuntimeSlitLocal)");
    }

    [Fact]
    public void PendingNativeCorrectionWaitsForNewGeometryBeforeFailureRecovery()
    {
        var body = Slice(Source,
            "private async Task<StageResult> PlaceTargetOnSlitWithPhd2Async(",
            "private Task<StageResult> ReturnPhd2LockToOriginAsync(");
        AssertOrdered(body,
            "var nativeCorrectionPending = !plan.IsAllowed &&",
            "string.Equals(plan.Code, \"FRESH_G3_RESIDUAL_REQUIRED\", StringComparison.Ordinal)",
            "if (!supervisedSlitPrecisionWarning && (nativeCorrectionPending || completionWindowUnstable ||",
            "DateTimeOffset.UtcNow + completionLimits.MaximumStageDuration > completionDeadline",
            "targetCompletionWindow = await CapturePhd2PlacementGuideWindowAsync(",
            "requiredCompletionFrames, completionDeadline,",
            "session = session with { LastMeasurement = completionMeasurement };",
            "newLockMotion = false",
            "continue;",
            "if (!plan.IsAllowed && !supervisedSlitPrecisionWarning)");
    }

    [Fact]
    public void CompletionTimeoutReturnPreservesRootCauseWithoutAutomaticFullRebuild()
    {
        AssertOrdered(Source,
            "PHD2_COMPLETION_WINDOW_RETURN_RESERVE:",
            "var completionFailureMetrics = new Dictionary<string, double>",
            "cancellationToken, completionFailureMetrics)");
        AssertOrdered(Source,
            "var stopFailure = await StopPhdAfterOriginReachedWithRetryAsync()",
            "if (completionFailureMetrics is not null)",
            "\"PHD2_SLIT_COMPLETION_WINDOW_EXHAUSTED_RETURNED\"",
            "\"PHD2_LOCK_FAILURE_RETURNED\"");
        var plan = UvexAdv.Observatory.ObservationAutomaticRecoveryPolicy.For(
            UvexAdv.Observatory.ObservationStage.PlaceTargetOnSlit,
            UvexAdv.Observatory.GateResult.Unknown("PHD2_SLIT_COMPLETION_WINDOW_EXHAUSTED_RETURNED", "timeout"));
        Assert.False(plan.IsRecoverable);
    }

    [Fact]
    public void ReadOnlyCompletionWindowsRetainWorstCaseReturnTime()
    {
        AssertOrdered(Source,
            "if (!supervisedSlitPrecisionWarning && (nativeCorrectionPending || completionWindowUnstable ||",
            "var reservedReturnAttempts =",
            "var completionDeadline = ledger.StartedUtc + completionLimits.MaximumElapsed -",
            "PHD2_COMPLETION_WINDOW_RETURN_RESERVE:",
            "targetCompletionWindow = await CapturePhd2PlacementGuideWindowAsync(",
            "requiredCompletionFrames, completionDeadline,");
    }

    [Fact]
    public void UnsolvedMotionForecastMustBeReplacedBeforeGuideResidualConstruction()
    {
        AssertOrdered(Source,
            "if (lastG3Field?.Solve?.Result.Success != true &&",
            "targetPositionAuthority == Phd2TargetPositionAuthority.CatalogWcsProjection)",
            "G3_POST_WCS_TARGET_NOT_MEASURED:",
            "PixelPoint guideLocal;");
    }

    [Fact]
    public void NativeRoiEscapeIsRejectedWithinTheExistingSelectionBudget()
    {
        Assert.Contains("const int maximumNativeSelectionAttempts = 4;", Source, StringComparison.Ordinal);
        Assert.Contains("catch (Phd2GuideStarOutsideRoiException outsideRoi)", Source, StringComparison.Ordinal);
        Assert.Contains("var geometryAccepted = insideFrame && selectedInsideRequestedRoi &&", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalPlacementPromotesInitialResidualBeforeFirstMotionBindingGate()
    {
        var body = Slice(
            Source,
            "private async Task<StageResult> PlaceTargetOnSlitWithPhd2Async(",
            "private Task<StageResult> ReturnPhd2LockToOriginAsync(");

        AssertOrdered(
            body,
            "var first = firstMeasurements[^1];",
            "lastG3Field = UpdateG3FieldFromGuidingResidual(lastG3Field, first, preset);",
            "Phd2SlitLockShiftPlanner.PlanOutboundStage(",
            "var preIntentFieldBinding = await ValidateG3FieldMountBindingForMotionAsync(");
    }

    [Fact]
    public void EveryAcceptedPostStageResidualBecomesTheNextMotionAndReturnBindingWithoutBeingPreConsumed()
    {
        var body = Slice(
            Source,
            "private async Task<StageResult> PlaceTargetOnSlitWithPhd2Async(",
            "private Task<StageResult> ReturnPhd2LockToOriginAsync(");

        AssertOrdered(
            body,
            "var measured = measurements[^1];",
            "lastG3Field = UpdateG3FieldFromGuidingResidual(lastG3Field, measured, preset);",
            "var residual = PointDistance(measured.Measurement.TargetCentroid",
            "LastAcceptedFrameSha256 = measured.Measurement.FrameSha256,");

        Assert.DoesNotContain(
            "ledger = ledger with { LastAcceptedFrameSha256 = measured.Measurement.FrameSha256 };",
            body,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DurableStageIntentConsumesItsSourceFrameRatherThanTheFuturePostStageFrame()
    {
        var body = Slice(
            Source,
            "private async Task<StageResult> PlaceTargetOnSlitWithPhd2Async(",
            "private Task<StageResult> ReturnPhd2LockToOriginAsync(");

        AssertOrdered(
            body,
            "var stage = plan.Stage!;",
            "var chargedLedger = ledger with",
            "LastAcceptedFrameSha256 = stage.SourceFrameSha256,",
            "CreatePhd2PendingState(",
            "exact = await phd2.SetExactLockPositionAsync(",
            "var measured = measurements[^1];");
    }

    [Fact]
    public void SettledBudgetPersistsTheFreshFrameThatActuallyProvedCompletion()
    {
        var body = Slice(
            Source,
            "private async Task<StageResult> PlaceTargetOnSlitWithPhd2Async(",
            "private Task<StageResult> ReturnPhd2LockToOriginAsync(");

        AssertOrdered(
            body,
            "if (plan.IsComplete || supervisedSlitPrecisionWarning)",
            "Phd2LockShiftPendingPhase.SettledBudgetLedger",
            "LastAcceptedFrameSha256 = session.LastMeasurement.Measurement.FrameSha256,",
            "Phd2LockShiftPendingStore.WriteAtomicAsync(pendingPath, settled");
    }

    [Fact]
    public void DurableRecoveryPromotesFreshResidualBeforeReturnAuthorization()
    {
        var body = Slice(
            Source,
            "private async Task<StageResult> RecoverPersistedPhd2LockToOriginAsync(",
            "private async Task<StageResult> PlaceTargetOnSlitWithPhd2Async(");

        AssertOrdered(
            body,
            "var initial = freshMeasurements[^1];",
            "lastG3Field = UpdateG3FieldFromGuidingResidual(lastG3Field, initial, preset);",
            "var qualitySelection = SelectPhd2CalibrationQuality(",
            "return await ReturnPhd2LockToOriginCoreAsync(");
    }

    [Fact]
    public void PromotionCarriesTheAcceptedResidualMountBindingAndImmutableFrame()
    {
        var body = Slice(
            Source,
            "private static G3FieldState UpdateG3FieldFromGuidingResidual(",
            "internal sealed record Phd2GuidingResidualState(");

        Assert.Contains("FramePath = residual.Frame.Path", body, StringComparison.Ordinal);
        Assert.Contains("Frame = residual.MonochromeFrame", body, StringComparison.Ordinal);
        Assert.Contains("MountBinding = residual.MountBinding", body, StringComparison.Ordinal);
        Assert.Contains("Geometry = residual.RuntimeSlitLocal", body, StringComparison.Ordinal);
    }

    private static void AssertOrdered(string source, params string[] markers)
    {
        var previous = -1;
        foreach (var marker in markers)
        {
            var index = source.IndexOf(marker, previous + 1, StringComparison.Ordinal);
            Assert.True(index > previous, $"Expected '{marker}' after index {previous}.");
            previous = index;
        }
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Start marker not found: {start}");
        var endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"End marker not found after start: {end}");
        return source[startIndex..endIndex];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src")) &&
                File.Exists(Path.Combine(directory.FullName, "UVEX-ADV.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
