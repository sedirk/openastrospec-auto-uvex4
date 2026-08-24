using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class BoundedAcquisitionSourceSafetyTests
{
    private static readonly string Source = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "Sources",
        "RealObservationStageRunner.cs"));

    [Fact]
    public void QhyCoarseCenteringUsesIndependentVersionedEnvelopeAndFreshSolves()
    {
        var body = MethodBody(
            "private async Task<StageResult> CoarseCenterAsync(",
            "private GateResult ValidateQhyCoarseMountState(");

        Assert.Contains("configuration.Qhy.CoarseCenteringLimits", body, StringComparison.Ordinal);
        Assert.Contains("ValidateQhyCoarseMoveAndReturnReserve", body, StringComparison.Ordinal);
        Assert.Contains("AcquireQhyWideFieldAsync", body, StringComparison.Ordinal);
        Assert.Contains("ReturnQhyCoarseToOriginAsync", body, StringComparison.Ordinal);
        Assert.Contains("qhy-coarse-centering-move-intent", body, StringComparison.Ordinal);
        Assert.DoesNotContain("commissioning!.MotionLimits", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ValidateCorrectionBudget", body, StringComparison.Ordinal);
        Assert.DoesNotContain("RegisterCorrection(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("MountTransform", body, StringComparison.Ordinal);
    }

    [Fact]
    public void G3RecoveryUsesBoundedPlannerFreshFramesAndSafeReturnWithoutOpticalOffset()
    {
        var body = MethodBody(
            "private async Task<StageResult> RunBoundedG3LocalSearchAsync(",
            "private GateResult ValidateG3SearchMountState(");

        Assert.Contains("G3LocalSearchPlanner.Build", body, StringComparison.Ordinal);
        Assert.Contains("ValidateG3SearchMoveAndReturnReserve", body, StringComparison.Ordinal);
        Assert.Contains("RequireImmediatePhysicalActionGatesAsync", body, StringComparison.Ordinal);
        Assert.Contains("CaptureAndAnalyzeG3WithSolveLadderAsync", body, StringComparison.Ordinal);
        Assert.Contains("ReturnDurableG3AcquisitionToOriginAsync", body, StringComparison.Ordinal);
        Assert.Contains("g3-bounded-search-attempt", body, StringComparison.Ordinal);
        Assert.DoesNotContain("MountTransform", body, StringComparison.Ordinal);
        Assert.DoesNotContain("QhyToG3", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnticipatedReturnStateIsPersistedBeforeEachAsynchronousOutboundSlew()
    {
        var coarse = MethodBody(
            "private async Task<StageResult> CoarseCenterAsync(",
            "private GateResult ValidateQhyCoarseMountState(");
        var coarsePending = coarse.IndexOf("pendingQhyCoarseReturn = state;", StringComparison.Ordinal);
        var coarseSlew = coarse.IndexOf("telescopeMediator.SlewToCoordinatesAsync", StringComparison.Ordinal);
        Assert.True(coarsePending >= 0 && coarsePending < coarseSlew);

        var search = MethodBody(
            "private async Task<StageResult> RunBoundedG3LocalSearchAsync(",
            "private GateResult ValidateG3SearchMountState(");
        var searchPending = search.IndexOf("pendingG3SearchReturn = search;", StringComparison.Ordinal);
        var searchSlew = search.IndexOf("telescopeMediator.SlewToCoordinatesAsync", StringComparison.Ordinal);
        Assert.True(searchPending >= 0 && searchPending < searchSlew);
    }

    [Fact]
    public void MissingWideToSlitSchemaProducesExplicitSkipProvenance()
    {
        var body = MethodBody(
            "private async Task<string> EnsureWideToSlitTransferSkippedEvidenceAsync(",
            "private static bool IsRecoverableG3SearchGate(");

        Assert.Contains("WideToSlitTransferMode.Skip", body, StringComparison.Ordinal);
        Assert.Contains("TransferSkipped", body, StringComparison.Ordinal);
        Assert.Contains("transferRecordId = (string?)null", body, StringComparison.Ordinal);
        Assert.Contains("mountTransformReused = false", body, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryLocalMotionFamilyChecksActualCommandCoordinateHorizon()
    {
        Assert.Contains(
            "ValidateCommandCoordinateHorizon(context, correctedCommanded, \"QHY coarse-centering outbound move\")",
            Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ValidateCommandCoordinateHorizon(context, commanded, \"QHY coarse-centering return move\")",
            Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ValidateCommandCoordinateHorizon(context, commanded, \"G3 bounded-search outbound move\")",
            Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ValidateCommandCoordinateHorizon(context, commanded, \"G3 bounded-search return move\")",
            Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ValidateCommandCoordinateHorizon(context, correctedCoordinates, \"segmented slit-placement move\")",
            Source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PendingReturnsAdoptReportedCoordinatesAndRejectUnknownPierSide()
    {
        Assert.Contains("ReanchorQhyCoarseStateFromReportedPosition", Source, StringComparison.Ordinal);
        Assert.Contains("ReanchorG3SearchStateFromReportedPosition", Source, StringComparison.Ordinal);
        Assert.Contains("QHY_COARSE_COMMAND_NOT_REACHED", Source, StringComparison.Ordinal);
        Assert.Contains("G3_SEARCH_COMMAND_NOT_REACHED", Source, StringComparison.Ordinal);
        Assert.Contains("QHY_COARSE_PIER_SIDE_UNKNOWN", Source, StringComparison.Ordinal);
        Assert.Contains("G3_SEARCH_PIER_SIDE_UNKNOWN", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidSparseG3ExposureRequiresAtLeastOneDetectedSkySourceBeforeBoundedSearch()
    {
        var recoverable = MethodBody(
            "private static bool IsRecoverableG3SearchGate(",
            "private async Task<StageResult> RunBoundedG3LocalSearchAsync(");

        Assert.Contains("G3_STAR_FIELD_SPARSE_VALID_EXPOSURE", recoverable, StringComparison.Ordinal);
        Assert.Contains("G3_FOCUS_STARS_INSUFFICIENT", recoverable, StringComparison.Ordinal);
        Assert.Contains("focusMeasurement.DetectedStarCount > 0", recoverable, StringComparison.Ordinal);
        Assert.DoesNotContain("G3_FOCUS_STARS_NOT_DETECTED", recoverable, StringComparison.Ordinal);
        Assert.Contains("pairAnalysis.Gate.Disposition == GateDisposition.Passed", recoverable, StringComparison.Ordinal);
        Assert.DoesNotContain("G3_FOCUS_STARS_TOO_BROAD", recoverable, StringComparison.Ordinal);
        Assert.DoesNotContain("G3_FOCUS_STARS_TOO_ELONGATED", recoverable, StringComparison.Ordinal);
    }

    [Fact]
    public void G3SolveProbeUsesActualPhd2ExposureAndLockedProfileParameterAuthority()
    {
        var validate = MethodBody(
            "private GateResult ValidateG3SolveProbeImage(",
            "private static bool IsRecoverableG3SearchGate(");

        Assert.Contains("G3SolveProbeCapturePolicy.Validate", validate, StringComparison.Ordinal);
        Assert.Contains("phdProfileEvidence", validate, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestedParametersApplied", validate, StringComparison.Ordinal);
        Assert.Contains("gainAndBinningAppliedByJsonRpc", Source, StringComparison.Ordinal);
        Assert.Contains("hash-locked-windows-phd2-profile+fits-headers-when-exposed", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void FeaturelessSolveLadderPausesWithoutSearchMotion()
    {
        var wrapper = MethodBody(
            "private async Task<G3FieldState> CaptureAndAnalyzeG3WithSolveLadderAsync(",
            "private async Task<G3PlateSolveProbeState> CaptureG3PlateSolveLadderAsync(");
        var recoverable = MethodBody(
            "private static bool IsRecoverableG3SearchGate(",
            "private string G3AcquisitionMotionPath(");

        Assert.Contains("G3_PLATE_SOLVE_LADDER_EXHAUSTED_STRUCTURED_FIELD", wrapper, StringComparison.Ordinal);
        Assert.Contains("G3_CLOUD_OR_TRANSPARENCY_INVALID", Source, StringComparison.Ordinal);
        Assert.DoesNotContain("G3_CLOUD_OR_TRANSPARENCY_INVALID", recoverable, StringComparison.Ordinal);
        Assert.Contains("mountMotionAuthorized = false", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void G3MotionWaitsCommissionedSettleAndReattestsDriftBeforeFreshCapture()
    {
        var settle = MethodBody(
            "private async Task<G3PostSlewStabilityResult> WaitForG3PostSlewStabilityAsync(",
            "private async Task<G3AcquisitionMotionReturnResult> ReturnDurableG3AcquisitionToOriginAsync(");
        Assert.Contains("MotionPostSlewSettleSeconds", settle, StringComparison.Ordinal);
        Assert.Contains("Task.Delay", settle, StringComparison.Ordinal);
        Assert.Contains("RequireImmediatePhysicalActionGatesAsync", settle, StringComparison.Ordinal);
        Assert.Contains("G3_POST_SLEW_POSITION_UNSTABLE", settle, StringComparison.Ordinal);

        var wcs = MethodBody(
            "private async Task<StageResult> RunG3WcsCenteringAsync(",
            "private async Task<StageResult> RunBoundedG3LocalSearchAsync(");
        Assert.True(
            wcs.IndexOf("WaitForG3PostSlewStabilityAsync", StringComparison.Ordinal) <
            wcs.IndexOf("CaptureAndAnalyzeG3WithSolveLadderAsync", StringComparison.Ordinal));
        var search = MethodBody(
            "private async Task<StageResult> RunBoundedG3LocalSearchAsync(",
            "private GateResult ValidateG3SearchMountState(");
        Assert.True(
            search.IndexOf("WaitForG3PostSlewStabilityAsync", StringComparison.Ordinal) <
            search.IndexOf("CaptureAndAnalyzeG3WithSolveLadderAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void DurableG3RecoveryValidatesEveryManifestAndContinuesOneSettledLineage()
    {
        var begin = MethodBody(
            "private async Task<G3AcquisitionMotionState> BeginG3AcquisitionMotionAsync(",
            "private async Task PersistG3AcquisitionMotionAsync(");
        Assert.Contains("ContinueSettledLedger", begin, StringComparison.Ordinal);
        var freshLedger = begin.IndexOf("var state = new G3AcquisitionMotionState", StringComparison.Ordinal);
        Assert.True(freshLedger > 0);
        Assert.DoesNotContain("Guid.NewGuid", begin[..freshLedger], StringComparison.Ordinal);
        Assert.Contains("ObservationRunJournalStore(manifestPath)", Source, StringComparison.Ordinal);

        var recovery = MethodBody(
            "private async Task<StageResult?> RecoverDurableG3AcquisitionBeforeStageAsync(",
            "private async Task<StageResult> RunG3WcsCenteringAsync(");
        Assert.Contains("ValidateG3AcquisitionMotionManifestAsync(item", recovery, StringComparison.Ordinal);
        Assert.Contains("G3_MOTION_MULTIPLE_ACTIVE_LINEAGES", recovery, StringComparison.Ordinal);
        Assert.Contains("G3_MOTION_MULTIPLE_OUTSTANDING", recovery, StringComparison.Ordinal);
        Assert.Contains("manifest.RunIsTerminal", recovery, StringComparison.Ordinal);
        Assert.Contains("terminalIdentity", recovery, StringComparison.Ordinal);
        Assert.Contains("active.Add(item)", recovery, StringComparison.Ordinal);
        Assert.DoesNotContain("G3_MOTION_TERMINAL_RUN_OUTSTANDING", recovery, StringComparison.Ordinal);
        Assert.Contains("lineageCopies.Max(copy => copy.CumulativeMotionArcseconds)", recovery, StringComparison.Ordinal);
        Assert.Contains("lineageCopies.Max(copy => copy.CorrectionAttempts)", recovery, StringComparison.Ordinal);
        Assert.Contains("lineageCopies.Min(copy => copy.StartedUtc)", recovery, StringComparison.Ordinal);
        Assert.Contains("lineageCopies.Min(copy => copy.MaximumSingleCorrectionArcseconds)", recovery, StringComparison.Ordinal);
        Assert.Contains("lineageCopies.Min(copy => copy.MaximumRadiusArcseconds)", recovery, StringComparison.Ordinal);
        Assert.Contains("lineageCopies.Min(copy => copy.MaximumCumulativeMotionArcseconds)", recovery, StringComparison.Ordinal);
        Assert.Contains("lineageCopies.Min(copy => copy.MaximumCorrectionAttempts)", recovery, StringComparison.Ordinal);
        Assert.Contains("lineageCopies.Min(copy => copy.MaximumElapsedSeconds)", recovery, StringComparison.Ordinal);
    }

    [Fact]
    public void G3WcsAndLocalSearchConsumeCaptureMountBindingsAtEveryMotionBoundary()
    {
        var wrapper = MethodBody(
            "private async Task<G3FieldState> CaptureAndAnalyzeG3WithSolveLadderAsync(",
            "private async Task<G3PlateSolveProbeState> CaptureG3PlateSolveLadderAsync(");
        var wcs = MethodBody(
            "private async Task<StageResult> RunG3WcsCenteringAsync(",
            "private async Task<StageResult> RunBoundedG3LocalSearchAsync(");
        var search = MethodBody(
            "private async Task<StageResult> RunBoundedG3LocalSearchAsync(",
            "private GateResult ValidateG3SearchMountState(");

        Assert.Contains("ValidateG3ProbeMountBindingForMotionAsync", wrapper, StringComparison.Ordinal);
        Assert.True(CountOccurrences(wcs, "ValidateG3FieldMountBindingForMotionAsync") >= 3);
        Assert.True(CountOccurrences(search, "ValidateG3FieldMountBindingForMotionAsync") >= 3);
        var returned = wcs.IndexOf("if (!returned.ReturnedToOrigin)", StringComparison.Ordinal);
        var freshOrigin = wcs.IndexOf("var originField = await CaptureAndAnalyzeG3WithSolveLadderAsync", StringComparison.Ordinal);
        var local = wcs.IndexOf("return await RunBoundedG3LocalSearchAsync", StringComparison.Ordinal);
        Assert.True(returned >= 0 && freshOrigin > returned && local > freshOrigin);
    }

    [Fact]
    public void LocalSearchPublishesLegacyPendingMarkerOnlyAfterDurableLedgerExists()
    {
        var search = MethodBody(
            "private async Task<StageResult> RunBoundedG3LocalSearchAsync(",
            "private GateResult ValidateG3SearchMountState(");
        var begin = search.IndexOf("BeginG3AcquisitionMotionAsync", StringComparison.Ordinal);
        var durablePersist = search.IndexOf("PersistG3AcquisitionMotionAsync(durableSearch", begin, StringComparison.Ordinal);
        var legacyMarker = search.IndexOf("pendingG3SearchReturn = search;", StringComparison.Ordinal);

        Assert.True(begin >= 0 && durablePersist > begin && legacyMarker > durablePersist);
        Assert.DoesNotContain("pendingG3SearchReturn = search;", search[..begin], StringComparison.Ordinal);
    }

    [Fact]
    public void QhyAcceptedFrameIsDualBoundAndRecheckedBeforeIntentAndDispatch()
    {
        var acquisition = MethodBody(
            "private async Task<StageResult> AcquireQhyWideFieldAsync(",
            "private async Task<QhyJobSnapshot> AcquireOrContinueQhyAcquisitionAsync(");
        var job = MethodBody(
            "private async Task<QhyJobSnapshot> AcquireOrContinueQhyAcquisitionAsync(",
            "private async Task<StageResult> CoarseCenterAsync(");
        var coarse = MethodBody(
            "private async Task<StageResult> CoarseCenterAsync(",
            "private GateResult ValidateQhyCoarseMountState(");

        Assert.Contains("CreateQhyAcceptedFrameMountBinding", acquisition, StringComparison.Ordinal);
        Assert.Contains("afterAcceptedFrameMountReadback", acquisition, StringComparison.Ordinal);
        Assert.True(job.IndexOf("CaptureG3FrameMountReadback", StringComparison.Ordinal) <
            job.IndexOf("StartOrAdoptAcquisitionAsync", StringComparison.Ordinal));
        Assert.True(CountOccurrences(coarse, "ValidateQhyAcceptedFrameMountBindingForMotionAsync") >= 3);
        Assert.Contains("reportedBeforeDispatch", coarse, StringComparison.Ordinal);
        Assert.Contains("QHY_COARSE_SOURCE_POSITION_CHANGED", coarse, StringComparison.Ordinal);
    }

    [Fact]
    public void G3FormalMotionUsesOneSphericalGeometryAndKeepsFailedFreshFramesOutstanding()
    {
        var durableReturn = MethodBody(
            "private async Task<G3AcquisitionMotionReturnResult> ReturnDurableG3AcquisitionToOriginAsync(",
            "private async Task<(bool RunIsTerminal, GateResult? Error)> ValidateG3AcquisitionMotionManifestAsync(");
        var wcs = MethodBody(
            "private async Task<StageResult> RunG3WcsCenteringAsync(",
            "private async Task<StageResult> RunBoundedG3LocalSearchAsync(");
        var search = MethodBody(
            "private async Task<StageResult> RunBoundedG3LocalSearchAsync(",
            "private GateResult ValidateG3SearchMountState(");

        Assert.True(CountOccurrences(durableReturn, "ValidateSphericalCommand") >= 2);
        Assert.True(CountOccurrences(wcs, "ValidateSphericalCommand") >= 2);
        Assert.True(CountOccurrences(search, "ValidateSphericalCommand") >= 2);
        Assert.DoesNotContain("ApplySkyCorrection(", wcs, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplySkyCorrection(", search, StringComparison.Ordinal);
        Assert.Contains("ApplyTangentOffsetArcseconds", wcs, StringComparison.Ordinal);
        Assert.Contains("ApplyTangentOffsetArcseconds", search, StringComparison.Ordinal);

        var improvement = wcs.IndexOf("Fresh G3 WCS improved", StringComparison.Ordinal);
        Assert.True(improvement > 0);
        Assert.Contains(
            "Phase = G3AcquisitionMotionPhase.AwaitingFreshSolve",
            wcs[Math.Max(0, improvement - 500)..Math.Min(wcs.Length, improvement + 500)],
            StringComparison.Ordinal);

        var nonSuccess = search.IndexOf("the durable return obligation remains outstanding", StringComparison.Ordinal);
        var passed = search.IndexOf("if (lastG3Field.Gate.Disposition == GateDisposition.Passed)", StringComparison.Ordinal);
        Assert.True(nonSuccess > 0 && passed > nonSuccess);
        Assert.DoesNotContain(
            "SettledBudgetLedger",
            search[nonSuccess..passed],
            StringComparison.Ordinal);
        Assert.Contains("ReturnDurableG3AcquisitionToOriginAsync", search, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(Source, "ReturnG3SearchToOriginAsync("));
        Assert.Contains(
            "[Obsolete(\"Unjournaled G3 return is prohibited; use ReturnDurableG3AcquisitionToOriginAsync.\", error: true)]",
            Source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SlitSegmentPersistsDurableIntentBeforeSlewAndRecoversBeforeResumeWork()
    {
        var placement = MethodBody(
            "private async Task<StageResult> PlaceTargetOnSlitLockedAsync(",
            "private async Task<StageResult> StartGuidingAsync(");
        var durableWrite = placement.IndexOf("SlitPlacementPendingStore.WriteAtomicAsync", StringComparison.Ordinal);
        var outboundSlew = placement.IndexOf("telescopeMediator.SlewToCoordinatesAsync", StringComparison.Ordinal);

        Assert.True(durableWrite >= 0 && durableWrite < outboundSlew);
        Assert.Contains("ValidateOutboundAndReturnReserve", placement, StringComparison.Ordinal);
        Assert.Contains("ReturnPendingSlitPlacementLockedAsync", placement, StringComparison.Ordinal);
        Assert.Contains("SlitPlacementPendingPhase.AwaitingFreshField", placement, StringComparison.Ordinal);

        var execute = MethodBody(
            "public override async Task<StageResult> ExecuteStageAsync(",
            "public override async Task<GateResult> RevalidateAsync(");
        Assert.True(
            execute.IndexOf("Interlocked.Exchange(ref resumeRecoveryRequired, 0)", StringComparison.Ordinal) <
            execute.IndexOf("RecoverDurableSlitPlacementBeforeStageAsync", StringComparison.Ordinal));
        Assert.True(
            execute.IndexOf("RecoverDurableSlitPlacementBeforeStageAsync", StringComparison.Ordinal) <
            execute.IndexOf("RecoverInterruptedStageAsync", StringComparison.Ordinal));
        Assert.Contains("SlitPlacementPendingStore.DiscoverAsync", Source, StringComparison.Ordinal);
        Assert.Contains("SlitPlacementBudgetLineageResolver.Resolve", Source, StringComparison.Ordinal);
        Assert.Contains("ObservationRunJournalStore(manifestPath)", Source, StringComparison.Ordinal);
        Assert.Contains("PersistCurrentRunSlitBudgetHandoffAsync", Source, StringComparison.Ordinal);
        Assert.Contains("manifest.TerminalState is not null", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void ForeignRunRecoveryCannotResetFineMotionBudget()
    {
        var recovery = MethodBody(
            "private async Task<StageResult> ReturnPendingSlitPlacementLockedAsync(",
            "private async Task<StageResult> PlaceTargetOnSlitAsync(");

        Assert.Contains("AdoptDurableSlitBudget(state);", recovery, StringComparison.Ordinal);
        Assert.DoesNotContain("if (!allowForeignRunRecovery) AdoptDurableSlitBudget", recovery, StringComparison.Ordinal);
        Assert.DoesNotContain("if (!allowForeignRunRecovery)\r\n            {\r\n                cumulativeCorrectionDegrees", recovery, StringComparison.Ordinal);

        var handoff = MethodBody(
            "private async Task<(SlitPlacementPendingState? State, GateResult? Error)> PersistCurrentRunSlitBudgetHandoffAsync(",
            "private async Task<GateResult?> PersistSettledForeignSlitBudgetAfterReturnAsync(");
        Assert.Contains("ObservationRunId = context.Plan.ObservationRunId", handoff, StringComparison.Ordinal);
        Assert.Contains("SlitPlacementPendingStore.WriteAtomicAsync", handoff, StringComparison.Ordinal);
        Assert.Contains("FineAcquisitionStartedUtc", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void SlitReturnPrechargesBudgetAndRechecksReportedPositionBeforeCommand()
    {
        var recovery = MethodBody(
            "private async Task<StageResult> ReturnPendingSlitPlacementLockedAsync(",
            "private async Task<StageResult> PlaceTargetOnSlitAsync(");
        var precharge = recovery.IndexOf(
            "CumulativeCorrectionDegrees = state.CumulativeCorrectionDegrees + step.CommandMagnitudeDegrees",
            StringComparison.Ordinal);
        var finalPositionRead = recovery.IndexOf("var immediatelyBeforeReturn = telescopeMediator.GetCurrentPosition();", StringComparison.Ordinal);
        var slew = recovery.IndexOf("telescopeMediator.SlewToCoordinatesAsync", StringComparison.Ordinal);
        Assert.True(precharge >= 0 && precharge < slew);
        Assert.True(finalPositionRead >= 0 && finalPositionRead < slew);
        Assert.Contains("SLIT_RETURN_PRECOMMAND_POSITION_CHANGED", recovery, StringComparison.Ordinal);
        Assert.Contains("SLIT_RETURN_POSTCOMMAND_EPOCH_CHANGED", recovery, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandCoordinateHorizonTransformsMountEpochToJ2000()
    {
        var horizon = MethodBody(
            "private static GateResult ValidateCommandCoordinateHorizon(",
            "private async Task<StageResult> SlewToCatalogTargetAsync(");
        Assert.Contains("commanded.Transform(Epoch.J2000)", horizon, StringComparison.Ordinal);
        Assert.Contains("RightAscensionDegrees = commandedJ2000.RADegrees", horizon, StringComparison.Ordinal);
        Assert.Contains("COMMAND_COORDINATE_EPOCH_CONVERSION_FAILED", horizon, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogSlewEnablesAndVerifiesTrackingThroughNinaMediator()
    {
        var source = MethodBody(
            "private async Task<StageResult> SlewToCatalogTargetAsync(",
            "private async Task<StageResult> AcquireQhyWideFieldAsync(");
        var tracking = source.IndexOf("EnsureMountTrackingEnabledAsync", StringComparison.Ordinal);
        var slew = source.IndexOf("telescopeMediator.SlewToCoordinatesAsync", StringComparison.Ordinal);

        Assert.True(tracking >= 0 && tracking < slew);
        Assert.Contains("telescopeMediator.SetTrackingEnabled(true)", source, StringComparison.Ordinal);
        Assert.Contains("verified.TrackingEnabled", source, StringComparison.Ordinal);
        Assert.Contains("TELESCOPE_TRACKING_ENABLE_FAILED", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PauseStartsNoReturnMotionAndResumeReconcilesPendingBeforeDevicesResume()
    {
        var pause = MethodBody(
            "public override async Task OnPausedAsync(",
            "public override async Task OnResumingAsync(");
        Assert.Contains("MarkResumeRecoveryRequired", pause, StringComparison.Ordinal);
        Assert.Contains("automaticMountOrLockReturnAttempted = false", pause, StringComparison.Ordinal);
        Assert.Contains("recoveryDeferredUntilExplicitResume = true", pause, StringComparison.Ordinal);
        Assert.DoesNotContain("ReturnDurableSlitPlacementForLifecycleAsync", pause, StringComparison.Ordinal);
        Assert.DoesNotContain("ReturnPhd2LockToOriginAsync", pause, StringComparison.Ordinal);
        Assert.DoesNotContain("SlewToCoordinatesAsync", pause, StringComparison.Ordinal);

        Assert.Contains("ReturnDurableSlitPlacementForLifecycleAsync", MethodBody(
            "public override async Task OnTakeoverAsync(",
            "public override async Task OnCancelledAsync("), StringComparison.Ordinal);
        Assert.DoesNotContain("ReturnDurableSlitPlacementForLifecycleAsync", MethodBody(
            "public override async Task OnCancelledAsync(",
            "public override async Task OnFaultedAsync("), StringComparison.Ordinal);

        var resume = MethodBody(
            "public override async Task OnResumingAsync(",
            "public override async Task OnTakeoverAsync(");
        var pendingRecovery = resume.IndexOf("ReturnDurableSlitPlacementForLifecycleAsync", StringComparison.Ordinal);
        var qhyResume = resume.IndexOf("qhy.ResumeAsync", StringComparison.Ordinal);
        var phdResume = resume.IndexOf("phd2.ResumeAutomation", StringComparison.Ordinal);
        Assert.True(pendingRecovery >= 0 && pendingRecovery < qhyResume && pendingRecovery < phdResume);
    }

    [Fact]
    public void CooperativePauseRecoveryIsReentrantAndDiscardsTheStalePlacementStack()
    {
        var placementWrapper = MethodBody(
            "private async Task<StageResult> PlaceTargetOnSlitAsync(",
            "private async Task<StageResult> PlaceTargetOnSlitLockedAsync(");
        Assert.Contains("slitPlacementRecoveryDepth.Value == 0", placementWrapper, StringComparison.Ordinal);
        Assert.Contains("slitPlacementRecoveryDepth.Value++", placementWrapper, StringComparison.Ordinal);

        var lifecycleRecovery = MethodBody(
            "private async Task<StageResult?> ReturnDurableSlitPlacementForLifecycleAsync(",
            "private GateResult ValidateSlitPendingIdentity(");
        Assert.Contains("slitPlacementRecoveryDepth.Value == 0", lifecycleRecovery, StringComparison.Ordinal);
        Assert.Contains("if (ownsRecoveryLock) slitPlacementRecoveryLock.Release();", lifecycleRecovery, StringComparison.Ordinal);

        var placement = MethodBody(
            "private async Task<StageResult> PlaceTargetOnSlitLockedAsync(",
            "private async Task<StageResult> StartGuidingAsync(");
        var staleStackCatch = placement.IndexOf("catch (ResumeStageRestartException)", StringComparison.Ordinal);
        var genericCatch = placement.IndexOf("catch (Exception ex)", staleStackCatch, StringComparison.Ordinal);
        Assert.True(staleStackCatch >= 0 && staleStackCatch < genericCatch);
        Assert.Contains("throw;", placement[staleStackCatch..genericCatch], StringComparison.Ordinal);
    }

    [Fact]
    public void OperatorCancellationDoesNotInitiateG3AcquisitionReturnMotion()
    {
        var cancellation = MethodBody(
            "public override async Task OnCancelledAsync(",
            "public override async Task OnFaultedAsync(");
        var cleanup = MethodBody(
            "private async Task<IReadOnlyList<string>> CleanupAfterFailureAsync(",
            "private async Task StopPhdAndWaitAsync(");

        Assert.DoesNotContain("ReturnDurableG3AcquisitionToOriginAsync", cancellation, StringComparison.Ordinal);
        Assert.DoesNotContain("SlewToCoordinatesAsync", cancellation, StringComparison.Ordinal);
        Assert.DoesNotContain("ReturnDurableSlitPlacementForLifecycleAsync", cancellation, StringComparison.Ordinal);
        Assert.Contains("real-run-cancellation-no-motion", cancellation, StringComparison.Ordinal);
        Assert.DoesNotContain("ReturnDurableG3AcquisitionToOriginAsync", cleanup, StringComparison.Ordinal);
        Assert.DoesNotContain("SlewToCoordinatesAsync", cleanup, StringComparison.Ordinal);
    }

    [Fact]
    public void CancellationAndDisposeAreStopOnlyWhileLiveFaultCleanupMayCloseCoverOnce()
    {
        var execute = MethodBody(
            "public override async Task<StageResult> ExecuteStageAsync(",
            "public override async Task<GateResult> RevalidateAsync(");
        var cancellation = MethodBody(
            "public override async Task OnCancelledAsync(",
            "public override async Task OnFaultedAsync(");
        var fault = MethodBody(
            "public override async Task OnFaultedAsync(",
            "public async ValueTask DisposeAsync()");
        var dispose = MethodBody(
            "public async ValueTask DisposeAsync()",
            "private async Task<StageResult> ValidateNightSetupAsync(");
        var cleanup = MethodBody(
            "private async Task<IReadOnlyList<string>> CleanupAfterFailureAsync(",
            "private async Task StopPhdAndWaitAsync(");
        var closeCover = MethodBody(
            "private async Task<string?> CloseOpticalCoverAsync(",
            "private async Task<QhyCameraStatus> ConnectQhyAtCheckpointAsync(");

        Assert.Contains("allowMechanicalActions: false", cancellation, StringComparison.Ordinal);
        Assert.DoesNotContain("allowMechanicalActions: true", cancellation, StringComparison.Ordinal);
        Assert.Contains("allowMechanicalActions: false", dispose, StringComparison.Ordinal);
        Assert.DoesNotContain("allowMechanicalActions: true", dispose, StringComparison.Ordinal);

        Assert.Equal(1, CountOccurrences(fault, "allowMechanicalActions: true"));
        Assert.Contains("cancellationToken", fault, StringComparison.Ordinal);
        Assert.DoesNotContain("CleanupAfterFailureAsync($\"Coordinator fault: {cause.Message}\", CancellationToken.None", fault, StringComparison.Ordinal);

        var cancelledNonOce = execute.IndexOf(
            "catch (Exception ex) when (cancellationToken.IsCancellationRequested)",
            StringComparison.Ordinal);
        var ordinaryFault = execute.IndexOf("catch (Exception ex)", cancelledNonOce + 1, StringComparison.Ordinal);
        Assert.True(cancelledNonOce >= 0 && ordinaryFault > cancelledNonOce);
        Assert.Contains("throw new OperationCanceledException", execute[cancelledNonOce..ordinaryFault], StringComparison.Ordinal);
        Assert.Contains("allowMechanicalActions: true", execute[ordinaryFault..], StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationToken.None", execute[ordinaryFault..], StringComparison.Ordinal);

        Assert.Contains("if (allowMechanicalActions && configuration.Environment.CloseOpticalCoverOnFailure)", cleanup, StringComparison.Ordinal);
        Assert.Contains("cancellationToken.ThrowIfCancellationRequested()", cleanup, StringComparison.Ordinal);
        Assert.Contains("CreateLinkedTokenSource(cancellationToken)", cleanup, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(cleanup, "CloseOpticalCoverAsync(reason"));
        Assert.Contains("catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)", closeCover, StringComparison.Ordinal);
        Assert.Contains("throw;", closeCover, StringComparison.Ordinal);
    }

    [Fact]
    public void FaultReturnUsesRunCancellationAndPropagatesCancellationBeforeCleanupContinues()
    {
        var fault = MethodBody(
            "public override async Task OnFaultedAsync(",
            "public async ValueTask DisposeAsync()");
        var returnCall = fault.IndexOf("ReturnDurableSlitPlacementForLifecycleAsync", StringComparison.Ordinal);
        var cancellationCatch = fault.IndexOf(
            "catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)",
            StringComparison.Ordinal);
        var cleanup = fault.IndexOf("CleanupAfterFailureAsync", StringComparison.Ordinal);

        Assert.True(returnCall >= 0 && returnCall < cancellationCatch && cancellationCatch < cleanup);
        Assert.Contains("cancellationToken).ConfigureAwait(false)", fault[returnCall..cancellationCatch], StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationToken.None", fault[returnCall..cancellationCatch], StringComparison.Ordinal);
        Assert.Contains("real-run-fault-recovery-cancelled-no-further-motion", fault[cancellationCatch..cleanup], StringComparison.Ordinal);
        Assert.Contains("throw;", fault[cancellationCatch..cleanup], StringComparison.Ordinal);
    }

    [Fact]
    public void IndependentSlitFailureRetainsReturnDebtButCancellationStartsNoMountReturn()
    {
        var placement = MethodBody(
            "private async Task<StageResult> PlaceTargetOnSlitLockedAsync(",
            "private async Task<StageResult> StartGuidingAsync(");
        var cancelledNonOce = placement.LastIndexOf(
            "catch (Exception ex) when (cancellationToken.IsCancellationRequested)",
            StringComparison.Ordinal);
        var ordinaryFault = placement.IndexOf("catch (Exception ex)", cancelledNonOce + 1, StringComparison.Ordinal);
        Assert.True(cancelledNonOce >= 0 && ordinaryFault > cancelledNonOce);

        var cancellationBlock = placement[cancelledNonOce..ordinaryFault];
        Assert.Contains("Phase = SlitPlacementPendingPhase.ReturnRequired", cancellationBlock, StringComparison.Ordinal);
        Assert.Contains("SlitPlacementPendingStore.WriteAtomicAsync", cancellationBlock, StringComparison.Ordinal);
        Assert.Contains("throw new OperationCanceledException", cancellationBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("ReturnPendingSlitPlacementLockedAsync", cancellationBlock, StringComparison.Ordinal);

        var returnCall = placement.IndexOf("ReturnPendingSlitPlacementLockedAsync", ordinaryFault, StringComparison.Ordinal);
        var returnEnd = placement.IndexOf(").ConfigureAwait(false);", returnCall, StringComparison.Ordinal);
        Assert.True(returnCall > ordinaryFault && returnEnd > returnCall);
        var ordinaryReturn = placement[returnCall..returnEnd];
        Assert.Contains("cancellationToken", ordinaryReturn, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationToken.None", ordinaryReturn, StringComparison.Ordinal);

        var recovery = MethodBody(
            "private async Task<StageResult> ReturnPendingSlitPlacementLockedAsync(",
            "private async Task<StageResult> PlaceTargetOnSlitAsync(");
        var durable = recovery.IndexOf("SlitPlacementPendingStore.WriteAtomicAsync(path, state, CancellationToken.None)", StringComparison.Ordinal);
        var entryCancellation = recovery.IndexOf("cancellationToken.ThrowIfCancellationRequested()", durable, StringComparison.Ordinal);
        var loop = recovery.IndexOf("for (var move", entryCancellation, StringComparison.Ordinal);
        var slew = recovery.IndexOf("telescopeMediator.SlewToCoordinatesAsync", loop, StringComparison.Ordinal);
        var preSlewCancellation = recovery.LastIndexOf("cancellationToken.ThrowIfCancellationRequested()", slew, StringComparison.Ordinal);
        Assert.True(durable >= 0 && entryCancellation > durable && loop > entryCancellation);
        Assert.True(preSlewCancellation > loop && preSlewCancellation < slew);
    }

    [Fact]
    public void IndependentPlacementCannotBypassHashBoundGradedGuidingAuthority()
    {
        var start = MethodBody(
            "private async Task<StageResult> StartGuidingAsync(",
            "private async Task<StageResult> StartQhyPhotometryAsync(");
        var stability = MethodBody(
            "private bool IsGuidingStable()",
            "private bool IsDegradedSupervisedScience()");
        var atrSave = MethodBody(
            "private async Task<string> SaveAtrImageAsync(",
            "private SpectralProbeMetrics MeasureSpectralProbe(");

        Assert.Contains("RealSlitPlacementAuthority.IndependentMountTransform", start, StringComparison.Ordinal);
        Assert.Contains("StartGradedPhd2GuidingAfterIndependentPlacementAsync", start, StringComparison.Ordinal);
        Assert.Contains("phd2SlitPlacementSession is not null", stability, StringComparison.Ordinal);
        Assert.Contains("IsUnattendedPhd2ScienceAuthority", atrSave, StringComparison.Ordinal);
        Assert.DoesNotContain("?? true", atrSave, StringComparison.Ordinal);
    }

    private static string MethodBody(string startMarker, string endMarker)
    {
        var start = Source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = Source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"Could not locate source section {startMarker} -> {endMarker}.");
        return Source[start..end];
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
        {
            count++;
        }
        return count;
    }
}
