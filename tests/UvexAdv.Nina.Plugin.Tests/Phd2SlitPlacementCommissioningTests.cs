using System.Text;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2SlitPlacementCommissioningTests
{
    private static readonly string RunnerSource = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "Sources",
        "RealObservationStageRunner.Phd2SlitPlacement.cs"));
    private static readonly string LegacyRunnerSource = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "Sources",
        "RealObservationStageRunner.cs"));

    [Fact]
    public void EveryFineMotionAuthorityTargetsFreshMeasuredSlitMidpoint()
    {
        Assert.Contains(
            "ToPhd2Domain(slitDetection.Geometry.AcquisitionPoint, preset)",
            RunnerSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ClosestPointOnSlit(\r\n                targetLocal",
            RunnerSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ClosestPointOnSlit(\n                targetLocal",
            RunnerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Distance(target.Centroid, slit.AcquisitionPoint)",
            LegacyRunnerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "SlitCorrectionCalculator.Calculate(",
            LegacyRunnerSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CompletePolicyAndTopologyPresetPassesButNumericPolicyDriftBreaksHash()
    {
        var valid = CreatePreset();

        Assert.Empty(valid.Validate());

        var changedPolicy = valid.CalibrationQualityPolicy with
        {
            DegradedMaximumOrthogonalityErrorDegrees = 29,
        };
        var changed = valid with { CalibrationQualityPolicy = changedPolicy };
        Assert.Contains(changed.Validate(), issue => issue.Contains("policy SHA-256", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DurableStoreRoundTripsAndRejectsTamperedEnvelope()
    {
        var directory = Path.Combine(Path.GetTempPath(), "uvex-phd2-lock-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "phd2-lock-shift-pending.json");
        try
        {
            var state = CreateState("run-a", Phd2LockShiftPendingPhase.StageIntent);
            await Phd2LockShiftPendingStore.WriteAtomicAsync(path, state, CancellationToken.None);
            var loaded = await Phd2LockShiftPendingStore.LoadAsync(path, CancellationToken.None);

            Assert.Null(loaded.Error);
            Assert.Equal(state, loaded.State);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp-*"));

            var bytes = await File.ReadAllBytesAsync(path);
            var text = Encoding.UTF8.GetString(bytes).Replace("\"attemptsUsed\": 1", "\"attemptsUsed\": 2", StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, text);
            var tampered = await Phd2LockShiftPendingStore.LoadAsync(path, CancellationToken.None);

            Assert.Null(tampered.State);
            Assert.Contains("SHA-256 mismatch", tampered.Error);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DiscoveryKeepsForeignOutstandingLineageVisible()
    {
        var root = Path.Combine(Path.GetTempPath(), "uvex-phd2-lock-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var first = Path.Combine(root, "run-a", "control", "phd2-lock-shift-pending.json");
            var second = Path.Combine(root, "run-b", "control", "phd2-lock-shift-pending.json");
            await Phd2LockShiftPendingStore.WriteAtomicAsync(first, CreateState("run-a", Phd2LockShiftPendingPhase.ReturnRequired), CancellationToken.None);
            await Phd2LockShiftPendingStore.WriteAtomicAsync(second, CreateState("run-b", Phd2LockShiftPendingPhase.SettledBudgetLedger), CancellationToken.None);

            var discovered = await Phd2LockShiftPendingStore.DiscoverAsync(root, CancellationToken.None);

            Assert.Equal(2, discovered.Count);
            Assert.Single(discovered, item => item.State?.Phase != Phd2LockShiftPendingPhase.SettledBudgetLedger);
            Assert.Contains(discovered, item => item.State?.ObservationRunId == "run-a");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AutoAuthorityIsDistinctFromBothIndividualAuthorities()
    {
        Assert.NotEqual(RealSlitPlacementAuthority.Phd2CalibrationLockShift, RealSlitPlacementAuthority.AutoPreferPhd2ThenIndependent);
        Assert.NotEqual(RealSlitPlacementAuthority.IndependentMountTransform, RealSlitPlacementAuthority.AutoPreferPhd2ThenIndependent);
    }

    [Fact]
    public void ProductionRunnerSelectsOnlyFromFreshCommissionedExposureFrame()
    {
        var capture = RunnerSource.IndexOf("CaptureAndSelectPhd2GuideAtExposureAsync", StringComparison.Ordinal);
        var fullFrame = RunnerSource.IndexOf("CaptureG3FullFrameForAcquisitionAsync", capture, StringComparison.Ordinal);
        var analyze = RunnerSource.IndexOf("G3FrameInputPolicy.Create", fullFrame, StringComparison.Ordinal);
        var select = RunnerSource.IndexOf("GuideStarSelector.Select", analyze, StringComparison.Ordinal);

        Assert.True(capture >= 0 && fullFrame > capture && analyze > fullFrame && select > analyze);
        Assert.Contains("capture.VerifiedExposureMilliseconds != exposureMilliseconds", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("selectionMustUseThisFrame", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("AutoPreferOffSlitThenDirectTarget", RunnerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryNewGuideEpochDelegatesOrdinarySelectionToPhd2AndRetainsBoundedRecoveryFence()
    {
        Assert.Equal(3, Count("BuildPhd2GuideSelectionRoi(") - 1); // Three commissioned new-guide paths plus the helper declaration.
        Assert.Equal(4, Count("PublishPhd2GuideSelectionEvidenceAsync(")); // Three call sites plus the evidence helper declaration.
        Assert.Contains("phd2.FindGuideStarAsync", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("GuideStarSelector.ValidateNativeSelection", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("PHD2 native full-frame find_star; coordinator validates the exact returned point and never ranks a substitute", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("candidateRankingByCoordinator = false", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("phd2.GetPixelScaleAsync", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("const int commissionedSizePixels = 80", RunnerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AutoSelectGuideStarAsync", RunnerSource, StringComparison.Ordinal);

        Assert.Equal(2, CountIn(LegacyRunnerSource, "BuildPhd2GuideSelectionRoi("));
        Assert.Equal(3, CountIn(LegacyRunnerSource, "PublishGuideSelectionEvidenceAsync(")); // Two call sites plus the evidence helper declaration.
        Assert.Equal(1, CountIn(LegacyRunnerSource, "guideSelectionAuthority = \"same-frame compact-source morphology; PHD2 fallback confined to ROI\""));
        Assert.Contains("bright-target/halo guard, compact FWHM", LegacyRunnerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeOffSlitExhaustionNeverUsesCoordinatorRankedSubstituteAndDegradesExplicitly()
    {
        var nativeSelection = Section(
            "private async Task<(GuideStarSelection Selection, Phd2Point Requested, Phd2Point Selected)> SelectFreshPhd2GuideAsync(",
            "private async Task<Phd2PreparedGuideSelection> PrepareDirectTargetFallbackAfterNativeExhaustionAsync(");
        Assert.Contains("throw new Phd2NativeGuideSelectionExhaustedException", nativeSelection, StringComparison.Ordinal);
        Assert.DoesNotContain("requestedAlternate", nativeSelection, StringComparison.Ordinal);
        Assert.DoesNotContain("PHD2_GUIDE_RESELECTED_AFTER_GEOMETRY_REJECTIONS", nativeSelection, StringComparison.Ordinal);

        var fallback = Section(
            "private async Task<Phd2PreparedGuideSelection> PrepareDirectTargetFallbackAfterNativeExhaustionAsync(",
            "private async Task<Phd2PlacementGuideChoice> CaptureAndSelectPhd2GuideAtExposureAsync(");
        Assert.Contains("PHD2_OFF_SLIT_NATIVE_EXHAUSTED_DIRECT_TARGET_FALLBACK", fallback, StringComparison.Ordinal);
        Assert.Contains("Phd2SlitGuideMode.DegradedDirectTargetGuiding", fallback, StringComparison.Ordinal);
        Assert.Contains("exactLockOrMountMutationIssued = false", fallback, StringComparison.Ordinal);
        Assert.Contains("coordinatorRankedSubstituteUsed = false", fallback, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeNoCandidateUsesOnlyTheBoundedFreshFrameReselectionPath()
    {
        var nativeSelection = Section(
            "const int maximumNativeSelectionAttempts = 4;",
            "private async Task<Phd2PreparedGuideSelection> PrepareDirectTargetFallbackAfterNativeExhaustionAsync(");
        var find = nativeSelection.IndexOf("phd2.FindGuideStarAsync", StringComparison.Ordinal);
        var exactCatch = nativeSelection.IndexOf("catch (Phd2NoGuideStarException noGuideStar)", StringComparison.Ordinal);
        var freshFrame = nativeSelection.IndexOf("phd2.SaveNextLoopingFrameAsync", exactCatch, StringComparison.Ordinal);
        var continueSelection = nativeSelection.IndexOf("continue;", freshFrame, StringComparison.Ordinal);
        var exhaustion = nativeSelection.LastIndexOf("throw new Phd2NativeGuideSelectionExhaustedException", StringComparison.Ordinal);

        Assert.True(find >= 0 && exactCatch > find && freshFrame > exactCatch && continueSelection > freshFrame && exhaustion > continueSelection);
        Assert.Contains("g3-phd2-native-no-candidate-{attempt}", nativeSelection, StringComparison.Ordinal);
        Assert.Contains("attempt < maximumNativeSelectionAttempts", nativeSelection, StringComparison.Ordinal);
        Assert.Contains("rejected.Add(reason)", nativeSelection, StringComparison.Ordinal);
        Assert.DoesNotContain("catch (Phd2Exception", nativeSelection, StringComparison.Ordinal);
        Assert.DoesNotContain("catch (Exception", nativeSelection, StringComparison.Ordinal);
        Assert.DoesNotContain("phd2.GuideAsync", nativeSelection, StringComparison.Ordinal);
        Assert.DoesNotContain("SetExactLockPosition", nativeSelection, StringComparison.Ordinal);
        Assert.DoesNotContain("mount.", nativeSelection, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeSelectionExhaustionRetainsFourNoCandidateRejectionsForCheckedStopEvidence()
    {
        var rejections = Enumerable.Range(1, 4)
            .Select(attempt => $"attempt {attempt}: {Phd2NoGuideStarException.FailureCode}")
            .ToArray();

        var exception = new Phd2NativeGuideSelectionExhaustedException(4, rejections);

        Assert.Equal(4, exception.Attempts);
        Assert.Equal(rejections, exception.Rejections);
        Assert.Contains(Phd2NativeGuideSelectionExhaustedException.FailureCode, exception.Message, StringComparison.Ordinal);
        Assert.All(rejections, rejection => Assert.Contains(rejection, exception.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void FullFieldRelockIsLimitedToStructuredLostLockOrDisconnectEvidence()
    {
        var placement = Section(
            "private async Task<StageResult> PlaceTargetOnSlitWithPhd2Async(",
            "private Task<StageResult> ReturnPhd2LockToOriginAsync(");
        var classifier = placement.IndexOf("IsStructuredPhd2GuideSessionLoss(ex)", StringComparison.Ordinal);
        var fullReacquisition = placement.IndexOf("AcquireG3SlitFieldAsync(", classifier, StringComparison.Ordinal);

        Assert.True(classifier >= 0 && fullReacquisition > classifier);
        Assert.Contains("PHD2_SLIT_PLACEMENT_FAILED_SAFE", placement, StringComparison.Ordinal);
        Assert.Contains("not reclassified by message text", placement, StringComparison.Ordinal);
        Assert.DoesNotContain("ex.Message.Contains", placement, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectTargetFirstAutoModeRequiresBothCommissionedExposures()
    {
        var preset = CreatePreset() with
        {
            GuideMode = Phd2SlitGuideMode.AutoPreferDirectTargetThenOffSlit,
            DirectTargetGuidingExposureMilliseconds = 10,
            OffSlitGuidingExposureMilliseconds = 2000,
        };

        Assert.Empty(preset.Validate());
        Assert.Equal(10, preset.ExposureFor(Phd2SlitGuideMode.DegradedDirectTargetGuiding));
        Assert.Equal(2000, preset.ExposureFor(Phd2SlitGuideMode.OffSlitGuideStar));
        Assert.Contains("if (preset.GuideMode == Phd2SlitGuideMode.AutoPreferDirectTargetThenOffSlit)", RunnerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void DurableOriginSlitUsesFiniteNearestPointRatherThanHistoricalMidpoint()
    {
        var pendingState = Section(
            "private Phd2LockShiftPendingState CreatePhd2PendingState(",
            "private async Task PublishPhd2GuideSelectionEvidenceAsync(");

        Assert.Contains("GuideStarSelector.ClosestPointOnSlit", pendingState, StringComparison.Ordinal);
        Assert.Contains("originSlit.X", pendingState, StringComparison.Ordinal);
        Assert.Contains("originSlit.Y", pendingState, StringComparison.Ordinal);
        Assert.DoesNotContain("InitialRuntimeSlitLocal.AcquisitionPoint, preset).X", pendingState, StringComparison.Ordinal);
    }

    [Fact]
    public void UserCancellationPersistsReturnRequiredButNeverStartsAutomaticReturn()
    {
        Assert.True(Count("catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)") >= 5);
        Assert.Contains("No automatic lock-return command was sent", RunnerSource, StringComparison.Ordinal);
        var firstCancellation = RunnerSource.IndexOf("User cancellation occurred after the durable stage intent", StringComparison.Ordinal);
        var nextGenericCatch = RunnerSource.IndexOf("catch (Exception ex)", firstCancellation, StringComparison.Ordinal);
        var cancellationBlock = RunnerSource[firstCancellation..nextGenericCatch];
        Assert.DoesNotContain("ReturnPhd2LockToOriginAsync", cancellationBlock, StringComparison.Ordinal);
        Assert.Contains("throw;", cancellationBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPhd2FailureReturnRetainsTheLiveCancellationToken()
    {
        const string marker = "return await ReturnPhd2LockToOriginAsync(";
        var count = 0;
        for (var start = 0; (start = RunnerSource.IndexOf(marker, start, StringComparison.Ordinal)) >= 0;)
        {
            var end = RunnerSource.IndexOf(").ConfigureAwait(false);", start, StringComparison.Ordinal);
            Assert.True(end > start, "Could not locate the end of a PHD2 lock-return call.");
            var call = RunnerSource[start..(end + ").ConfigureAwait(false);".Length)];
            Assert.Contains("cancellationToken", call, StringComparison.Ordinal);
            Assert.DoesNotContain("CancellationToken.None", call, StringComparison.Ordinal);
            count++;
            start = end + 1;
        }
        Assert.True(count >= 8, $"Expected every production PHD2 return path; found {count} calls.");

        var placement = Section(
            "private async Task<StageResult> PlaceTargetOnSlitWithPhd2Async(",
            "private Task<StageResult> ReturnPhd2LockToOriginAsync(");
        Assert.Contains("catch (Exception ex) when (cancellationToken.IsCancellationRequested)", placement, StringComparison.Ordinal);
        Assert.Contains("Phase = Phd2LockShiftPendingPhase.ReturnRequired", placement, StringComparison.Ordinal);
        Assert.Contains("without issuing a recovery command", placement, StringComparison.Ordinal);
    }

    [Fact]
    public void Phd2ReturnPersistsObligationThenHonorsCancellationBeforeEveryCommandStage()
    {
        var recovery = Section(
            "private async Task<StageResult> ReturnPhd2LockToOriginCoreAsync(",
            "private async Task<IReadOnlyList<Phd2GuidingResidualState>> CapturePhd2GuidingMeasurementsAsync(");
        var returnRequired = recovery.IndexOf("Phase = Phd2LockShiftPendingPhase.ReturnRequired", StringComparison.Ordinal);
        var durableWrite = recovery.IndexOf("WriteAtomicAsync(path, state, CancellationToken.None)", returnRequired, StringComparison.Ordinal);
        var entryCancellation = recovery.IndexOf("cancellationToken.ThrowIfCancellationRequested()", durableWrite, StringComparison.Ordinal);
        var loop = recovery.IndexOf("for (var recovery", entryCancellation, StringComparison.Ordinal);
        var loopCancellation = recovery.IndexOf("cancellationToken.ThrowIfCancellationRequested()", loop, StringComparison.Ordinal);
        var exactSet = recovery.IndexOf("phd2.SetExactLockPositionAsync", loopCancellation, StringComparison.Ordinal);
        var preExactCancellation = recovery.LastIndexOf("cancellationToken.ThrowIfCancellationRequested()", exactSet, StringComparison.Ordinal);

        Assert.True(returnRequired >= 0 && durableWrite > returnRequired && entryCancellation > durableWrite);
        Assert.True(loop > entryCancellation && loopCancellation > loop);
        Assert.True(preExactCancellation > loopCancellation && preExactCancellation < exactSet);

        var frame = recovery.IndexOf("phd2.SaveCurrentGuidingFrameAsync", exactSet, StringComparison.Ordinal);
        var cancellationCatch = recovery.IndexOf(
            "catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)",
            frame,
            StringComparison.Ordinal);
        var genericCatch = recovery.IndexOf("catch (Exception ex)", cancellationCatch, StringComparison.Ordinal);
        Assert.True(frame >= 0 && cancellationCatch > frame && genericCatch > cancellationCatch);
        Assert.Contains("throw;", recovery[cancellationCatch..genericCatch], StringComparison.Ordinal);
    }

    [Fact]
    public void IndependentFallbackStillUsesHashBoundGradedGuiding()
    {
        Assert.Contains("StartGradedPhd2GuidingAfterIndependentPlacementAsync", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("policy.ApplyHardRejectionCeilings", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("Phd2CalibrationEvaluationPhase.PostSettle", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("CapturePhd2GuidingMeasurementsAsync", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("PHD2_DEGRADED_SUPERVISION_OPT_IN_REQUIRED", RunnerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void DurableLedgerCannotResetCurrentRunBudgetOnReentry()
    {
        Assert.Contains("ValidatePhd2LockManifestAsync", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("ValidateCurrentPhd2LockLedgerBinding", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("inheritedSettledBudget?.LineageId ?? Guid.NewGuid()", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("inheritedSettledBudget?.AttemptsUsed ?? 0", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("inheritedSettledBudget?.CumulativeCommandedPixels ?? 0", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("inheritedSettledBudget?.StartedUtc ?? DateTimeOffset.UtcNow", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("PHD2_LOCK_INHERITED_BUDGET_EXHAUSTED", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("PHD2_LOCK_LEDGER_ALREADY_SETTLED", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("RecoveryContextSha256", RunnerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void LocallyAttestedGuideEpochRebindPreservesEveryDurableBudgetDimension()
    {
        var state = CreateState("run-a", Phd2LockShiftPendingPhase.ReturnRequired) with
        {
            AttemptsUsed = 7,
            CumulativeCommandedPixels = 42.5,
        };
        var rebound = state.RebindAfterLocallyAttestedGuideEpoch(
            state.ConnectionEpoch,
            state.GuideEpoch + 2,
            new Phd2Point(107.5, 101.25),
            state.UpdatedUtc.AddMilliseconds(1),
            "fault-injected locally attested guide epoch");

        Assert.Equal(state.ConnectionEpoch, rebound.ConnectionEpoch);
        Assert.Equal(state.GuideEpoch + 2, rebound.GuideEpoch);
        Assert.Equal(107.5, rebound.CurrentLockX);
        Assert.Equal(101.25, rebound.CurrentLockY);
        Assert.Equal(state.LineageId, rebound.LineageId);
        Assert.Equal(state.AttemptsUsed, rebound.AttemptsUsed);
        Assert.Equal(state.CumulativeCommandedPixels, rebound.CumulativeCommandedPixels);
        Assert.Equal(state.MaximumAttempts, rebound.MaximumAttempts);
        Assert.Equal(state.MaximumCumulativePixels, rebound.MaximumCumulativePixels);
        Assert.Equal(state.MaximumElapsedSeconds, rebound.MaximumElapsedSeconds);
        Assert.Equal(state.StartedUtc, rebound.StartedUtc);
        Assert.Equal(state.OriginLockX, rebound.OriginLockX);
        Assert.Equal(state.OriginLockY, rebound.OriginLockY);
    }

    [Fact]
    public void DurableReturnReplansPreDispatchDriftWithoutResendOrBudgetRollback()
    {
        var recovery = Section(
            "private async Task<StageResult> ReturnPhd2LockToOriginCoreAsync(",
            "private async Task<IReadOnlyList<Phd2GuidingResidualState>> CapturePhd2GuidingMeasurementsAsync(");
        var precharge = recovery.IndexOf("AttemptsUsed = state.AttemptsUsed + 1", StringComparison.Ordinal);
        var drift = recovery.IndexOf("Fresh runtime lock changed before dispatch", precharge, StringComparison.Ordinal);
        var continuation = recovery.IndexOf("continue;", drift, StringComparison.Ordinal);
        var exact = recovery.IndexOf("phd2.SetExactLockPositionAsync", drift, StringComparison.Ordinal);

        Assert.True(precharge >= 0 && drift > precharge && continuation > drift && exact > continuation);
        var driftBranch = recovery[drift..continuation];
        Assert.DoesNotContain("AttemptsUsed =", driftBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("CumulativeCommandedPixels =", driftBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("SetExactLockPositionAsync", driftBranch, StringComparison.Ordinal);
        Assert.Contains("pendingPhd2LockShift = state", driftBranch, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryNewOrResettleGuideRetainsLastMomentCalibrationReadback()
    {
        var returnPath = Section(
            "private async Task<StageResult> ReturnPhd2LockToOriginCoreAsync(",
            "private async Task<IReadOnlyList<Phd2GuidingResidualState>> CapturePhd2GuidingMeasurementsAsync(");
        var returnValidation = returnPath.IndexOf("calibrationBeforeReturnSettle", StringComparison.Ordinal);
        var returnGuide = returnPath.IndexOf("phd2.GuideAndSettleAsync", returnValidation, StringComparison.Ordinal);

        Assert.True(returnValidation >= 0 && returnGuide > returnValidation);
        Assert.Contains("PHD2_LOCK_RECOVERY_LAST_MOMENT_CALIBRATION_INVALID", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("calibrationBeforeStageSettle", RunnerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ForeignReturnCreatesCurrentRunSameLineageHandoffBeforeClosingHistoricalCopy()
    {
        var recovery = Section(
            "private async Task<StageResult> ReturnPhd2LockToOriginCoreAsync(",
            "private async Task<IReadOnlyList<Phd2GuidingResidualState>> CapturePhd2GuidingMeasurementsAsync(");
        var handoff = recovery.IndexOf("PersistCurrentRunPhd2BudgetHandoffAsync", StringComparison.Ordinal);
        var historicalClose = recovery.IndexOf("WriteAtomicAsync(path, settledState", StringComparison.Ordinal);

        Assert.True(handoff >= 0 && historicalClose > handoff);
        Assert.Contains("The foreign source remains ReturnRequired on disk", recovery, StringComparison.Ordinal);
        Assert.Contains("PHD2_LOCK_HANDOFF_CURRENT_RUN_FORK", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("PHD2_LOCK_HANDOFF_COPY_INCONSISTENT", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("PHD2_LOCK_HANDOFF_CRASH_WINDOW_RECONCILED", RunnerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void SettledBudgetHandoffPreservesLineageConsumptionLimitsAndClock()
    {
        var source = CreateState("old-run", Phd2LockShiftPendingPhase.SettledBudgetLedger) with
        {
            CurrentLockX = 100,
            CurrentLockY = 100,
            RequestedLockX = 100,
            RequestedLockY = 100,
            AttemptsUsed = 5,
            CumulativeCommandedPixels = 23.5,
        };
        var now = source.UpdatedUtc.AddSeconds(1);
        var handoff = Phd2LockShiftBudgetHandoff.CreateCurrentRunSettledCopy(
            source,
            "current-run",
            source.RecoveryContextSha256,
            now);

        Assert.Equal("current-run", handoff.ObservationRunId);
        Assert.Equal(source.LineageId, handoff.LineageId);
        Assert.Equal(source.AttemptsUsed, handoff.AttemptsUsed);
        Assert.Equal(source.CumulativeCommandedPixels, handoff.CumulativeCommandedPixels);
        Assert.Equal(source.StartedUtc, handoff.StartedUtc);
        Assert.Equal(source.MaximumStagePixels, handoff.MaximumStagePixels);
        Assert.Equal(source.MaximumCumulativePixels, handoff.MaximumCumulativePixels);
        Assert.Equal(source.MaximumAttempts, handoff.MaximumAttempts);
        Assert.Equal(source.MaximumElapsedSeconds, handoff.MaximumElapsedSeconds);
        Assert.Empty(Phd2LockShiftBudgetHandoff.ValidateCompletedHandoff(
            source,
            handoff,
            "current-run",
            source.RecoveryContextSha256));

        var resetFork = handoff with
        {
            LineageId = Guid.NewGuid().ToString("N"),
            AttemptsUsed = 0,
            CumulativeCommandedPixels = 0,
            StartedUtc = now,
        };
        Assert.NotEmpty(Phd2LockShiftBudgetHandoff.ValidateCompletedHandoff(
            source,
            resetFork,
            "current-run",
            source.RecoveryContextSha256));
    }

    [Fact]
    public void EveryGuidePathRechecksFieldBindingAndRecoveryGradesCalibrationBeforeGuide()
    {
        Assert.True(Count("preSelectBinding") >= 3);
        Assert.True(Count("preGuideBinding") >= 3);
        var recovery = Section(
            "private async Task<StageResult> RecoverPersistedPhd2LockToOriginAsync(",
            "private GateResult ValidateCurrentPhd2LockLedgerBinding(");
        var preCalibration = recovery.IndexOf("activeCalibrationBeforeGuide", StringComparison.Ordinal);
        var preGrade = recovery.IndexOf("Phd2CalibrationEvaluationPhase.PreGuide", preCalibration, StringComparison.Ordinal);
        var guide = recovery.IndexOf("phd2.GuideAndSettleAsync", StringComparison.Ordinal);
        Assert.True(preCalibration >= 0 && preGrade > preCalibration && guide > preGrade);
        Assert.Contains("PHD2_LOCK_RECOVERY_PRE_GUIDE_REJECTED", recovery, StringComparison.Ordinal);
        Assert.Contains("PHD2_LOCK_RECOVERY_SUPERVISION_OPT_IN_REQUIRED", recovery, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectTargetModeIsAlwaysSupervisedAndNeverUnattendedEvenWithNominalCalibration()
    {
        Assert.Contains("quality.RequiresOperatorSupervision ||", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("guideMode == Phd2SlitGuideMode.DegradedDirectTargetGuiding", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("quality.IsUnattendedScienceAuthority &&", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("guideMode != Phd2SlitGuideMode.DegradedDirectTargetGuiding", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("PHD2_DIRECT_TARGET_SUPERVISION_OPT_IN_REQUIRED", RunnerSource, StringComparison.Ordinal);
        var normal = Section(
            "private async Task<StageResult> PlaceTargetOnSlitWithPhd2Async(",
            "private async Task<IReadOnlyList<Phd2GuidingResidualState>> CapturePhd2GuidingMeasurementsAsync(");
        var directGate = normal.IndexOf("PHD2_DIRECT_TARGET_SUPERVISION_OPT_IN_REQUIRED", StringComparison.Ordinal);
        var guide = normal.IndexOf("phd2.GuideAndSettleAsync", StringComparison.Ordinal);
        Assert.True(directGate >= 0 && guide > directGate);
    }

    [Fact]
    public void OperatorWeakSupervisionIsTheExplicitSupervisedScienceOptIn()
    {
        Assert.Contains("HasSupervisedScienceOptIn()", RunnerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("!configuration.AllowDegradedSupervisedScience", RunnerSource, StringComparison.Ordinal);
        Assert.Contains(
            "configuration.AllowDegradedSupervisedScience ||\n        configuration.Environment.WeakSupervisionEnabled",
            LegacyRunnerSource.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ForeignTerminalSettledLedgerIsNeverRevivedAsReturnDebt()
    {
        var discovery = Section(
            "private async Task<StageResult?> RecoverOutstandingPhd2LockBeforePlacementAsync(",
            "private async Task<StageResult> RecoverPersistedPhd2LockToOriginAsync(");
        Assert.Contains(
            "var requiresRecovery = state.Phase != Phd2LockShiftPendingPhase.SettledBudgetLedger",
            discovery,
            StringComparison.Ordinal);
        Assert.DoesNotContain("!isAtOrigin", discovery, StringComparison.Ordinal);
    }

    [Fact]
    public void ForeignHardCrashRecoveryRequiresLiveMachineOwnerLeaseAndKeepsUniqueLineageGate()
    {
        var discovery = Section(
            "private async Task<StageResult?> RecoverOutstandingPhd2LockBeforePlacementAsync(",
            "private async Task<StageResult> RecoverPersistedPhd2LockToOriginAsync(");

        Assert.Contains("host.RealRunOwnershipGate()", discovery, StringComparison.Ordinal);
        Assert.Contains("!isCurrentRun && !manifest.RunIsTerminal", discovery, StringComparison.Ordinal);
        Assert.Contains("outstanding.Count != 1", discovery, StringComparison.Ordinal);
        Assert.Contains("lineages.Length != 1", discovery, StringComparison.Ordinal);
        Assert.Contains("ValidateCurrentPhd2LockLedgerBinding", discovery, StringComparison.Ordinal);
        Assert.DoesNotContain("PHD2_LOCK_FOREIGN_RUN_NOT_TERMINAL", discovery, StringComparison.Ordinal);
    }

    private static int Count(string value)
        => CountIn(RunnerSource, value);

    private static int CountIn(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static string Section(string startMarker, string endMarker)
    {
        var start = RunnerSource.IndexOf(startMarker, StringComparison.Ordinal);
        var end = RunnerSource.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"Could not locate {startMarker} -> {endMarker}.");
        return RunnerSource[start..end];
    }

    private static Phd2SlitPlacementCommissioningPreset CreatePreset()
    {
        var policy = Phd2CalibrationQualityPolicy.Default;
        return new Phd2SlitPlacementCommissioningPreset(
            "install-20260819",
            new string('A', 64),
            Phd2ImageCoordinateDomain.FullSensorCoordinates,
            1920,
            1080,
            0,
            0,
            1920,
            1080,
            12.5,
            Phd2SensorRotationAuthority.QualifiedPhd2Calibration,
            "pierEast",
            Phd2SlitGuideMode.OffSlitGuideStar,
            1000,
            10,
            100,
            20,
            300,
            20,
            10,
            5,
            0.25,
            0.25,
            1,
            100,
            10,
            20,
            1,
            3,
            1,
            10,
            1_000_000_000,
            45,
            0.001,
            1000,
            null,
            null,
            true,
            true,
            true,
            15,
            15,
            20,
            20,
            8,
            10,
            1.5,
            20,
            5,
            2.5,
            0.5,
            policy,
            Phd2SlitPlacementCommissioningPreset.ComputePolicySha256(policy));
    }

    private static Phd2LockShiftPendingState CreateState(string runId, Phd2LockShiftPendingPhase phase)
    {
        var now = DateTimeOffset.UtcNow;
        return new Phd2LockShiftPendingState(
            Phd2LockShiftPendingState.CurrentSchemaVersion,
            runId,
            Guid.NewGuid().ToString("N"),
            new string('A', 64),
            new string('B', 64),
            new string('F', 64),
            "policy-v1",
            new string('C', 64),
            new string('D', 64),
            Phd2SlitGuideMode.OffSlitGuideStar,
            1,
            1,
            100,
            100,
            105,
            100,
            110,
            100,
            10,
            100,
            20,
            300,
            5,
            1,
            now - TimeSpan.FromSeconds(2),
            now - TimeSpan.FromSeconds(1),
            now,
            phase,
            new string('E', 64),
            "frame.fit",
            "intent.json",
            "test",
            OriginTargetX: 500,
            OriginTargetY: 400,
            OriginSlitX: 500,
            OriginSlitY: 405);
    }
}
