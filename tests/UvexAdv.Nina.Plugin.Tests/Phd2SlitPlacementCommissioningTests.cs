using System.Text;
using UvexAdv.Observatory;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2SlitPlacementCommissioningTests
{
    [Fact]
    public void FreshResidualReferencesRunLedGeometryWithoutSearchingForDarknessInStarlight()
    {
        var body = Section(
            "private async Task<IReadOnlyList<Phd2GuidingResidualState>> CapturePhd2GuidingMeasurementsAsync(",
            "private async Task<Phd2PlacementGuideChoice> AcquireFreshPhd2PlacementGuideAsync(");
        Assert.Contains("G3RunLedSlitGeometryPolicy.Evaluate", body, StringComparison.Ordinal);
        Assert.Contains("RUN_LED_OFF_ON_OFF", body, StringComparison.Ordinal);
        Assert.Contains("slitIdentityEvidenceSha256", body, StringComparison.Ordinal);
        Assert.Contains("SlitIlluminationLedState == UvexOutputState.Off", body, StringComparison.Ordinal);
        Assert.DoesNotContain("DetectDarkSlit", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SlitLocalBackgroundDetector.Detect", body, StringComparison.Ordinal);
        Assert.Contains("immutableRunSlitAnchorUsed", body, StringComparison.Ordinal);
    }

    [Fact]
    public void SlitCompletionNeedsWholeFreshWindowAndCanReplanWithinOriginalLedger()
    {
        Assert.Contains("targetCompletionWindow = firstMeasurements", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("targetCompletionWindow = measurements", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("AllWithinTolerance(completionResiduals, completionTolerance)", RunnerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("completionWindowRetries >= 4", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("boundedByOriginalDeadline = true", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("completionDeadline = ledger.StartedUtc + completionLimits.MaximumElapsed -", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("completionLimits.MaximumStageDuration.Ticks * reservedReturnAttempts", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("nextAction = \"replan-from-last-real-fresh-frame\"", RunnerSource, StringComparison.Ordinal);
        Assert.True(RunnerSource.IndexOf("targetCompletionWindow.Count < requiredCompletionFrames", StringComparison.Ordinal) <
            RunnerSource.IndexOf("Target-on-slit completion was proven", StringComparison.Ordinal));
    }

    [Fact]
    public void PreAtrWindRecoveryUsesBoundedNewWindowsWithoutMovingOrRelaxingTolerance()
    {
        var start = RunnerSource.IndexOf("private async Task VerifyWindSampledGuidingBeforeAtrAsync(", StringComparison.Ordinal);
        var end = RunnerSource.IndexOf("private static Dictionary<string, double> Phd2QualityMetrics(", start, StringComparison.Ordinal);
        var body = RunnerSource[start..end];
        Assert.Contains("const int maximumWindows = 4", body, StringComparison.Ordinal);
        Assert.Contains("CapturePhd2GuidingMeasurementsAsync", body, StringComparison.Ordinal);
        Assert.Contains("residuals.All(value => double.IsFinite(value) && value <= tolerance)", body, StringComparison.Ordinal);
        Assert.Contains("!sameEpoch || !identityConfirmed || windowAttempt == maximumWindows", body, StringComparison.Ordinal);
        Assert.Contains("phd2-supervised-pre-atr-window", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SetLockPosition", body, StringComparison.Ordinal);
        Assert.DoesNotContain("GuideAndSettle", body, StringComparison.Ordinal);
        Assert.DoesNotContain("StopPhd", body, StringComparison.Ordinal);
    }

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
        Assert.Contains("PHD2 native full-frame/geometric-ROI find_star; coordinator validates the exact returned point and never ranks a substitute", RunnerSource, StringComparison.Ordinal);
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
        var freshFrame = nativeSelection.IndexOf("await RefreshNativeSelectionGeometryAsync", exactCatch, StringComparison.Ordinal);
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
        var refresh = Section("async Task RefreshNativeSelectionGeometryAsync(string evidenceRole)",
            "var targetIsUltraBright = target.FwhmPixels <= 0");
        Assert.Contains("await phd2.SaveNextLoopingFrameAsync", refresh, StringComparison.Ordinal);
        Assert.Contains("fresh.VerifiedExposureMilliseconds != preset.ExposureFor(choice.Mode)", refresh, StringComparison.Ordinal);
        Assert.Contains("Phd2NativeGuideSaturatedRegions.Measure(exclusionFrame", refresh, StringComparison.Ordinal);
        Assert.Contains("!insideSaturatedStructure", nativeSelection, StringComparison.Ordinal);
        Assert.Contains("exclusionFramePath, cancellationToken", nativeSelection, StringComparison.Ordinal);
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
    public void DurableLockRecoveryReusesThePassedCurrentFieldBeforeFallingBackToFreshPl3()
    {
        var recovery = Section(
            "private async Task<StageResult> RecoverPersistedPhd2LockToOriginAsync(",
            "private async Task<StageResult> PlaceTargetOnSlitWithPhd2Async(");

        var reusableField = recovery.IndexOf("var reusableField = lastG3Field", StringComparison.Ordinal);
        var bindingValidation = recovery.IndexOf("ValidateG3FieldMountBindingForMotionAsync(", reusableField, StringComparison.Ordinal);
        var reuseEvidence = recovery.IndexOf("phd2-lock-recovery-current-field-reused", bindingValidation, StringComparison.Ordinal);
        var solveFallback = recovery.IndexOf("CaptureAndAnalyzeG3WithSolveLadderAsync(", reuseEvidence, StringComparison.Ordinal);

        Assert.True(reusableField >= 0 && bindingValidation > reusableField && reuseEvidence > bindingValidation && solveFallback > reuseEvidence);
        Assert.Contains("reusableFieldBinding.Disposition == GateDisposition.Passed", recovery, StringComparison.Ordinal);
        Assert.Contains("PHD2_LOCK_RECOVERY_CURRENT_FIELD_UNAVAILABLE", recovery, StringComparison.Ordinal);
        Assert.DoesNotContain("lastG3Field = await CaptureAndAnalyzeG3Async(", recovery, StringComparison.Ordinal);
        Assert.Contains("PHD2_LOCK_RECOVERY_FRESH_FIELD_REQUIRED", recovery, StringComparison.Ordinal);
    }

    [Fact]
    public void ForeignRecoveryTranslatesOnlyAProvenPostDispatchEndpointAndNeverComparesOldAbsoluteTargetCoordinates()
    {
        var recovery = Section(
            "private async Task<StageResult> RecoverPersistedPhd2LockToOriginAsync(",
            "private async Task<StageResult> PlaceTargetOnSlitWithPhd2Async(");
        var normalized = recovery.Replace("\r\n", "\n", StringComparison.Ordinal);
        var foreignBranch = normalized.IndexOf("if (foreignRun)", StringComparison.Ordinal);
        var sameRunBranch = normalized.IndexOf("else\n        {\n            if (PointDistance(observedSlit, storedOriginSlit)", StringComparison.Ordinal);

        Assert.True(foreignBranch >= 0 && sameRunBranch > foreignBranch);
        var foreignProof = normalized[foreignBranch..sameRunBranch];
        Assert.Contains("Phd2ForeignRecoveryEndpointPolicy.Evaluate(state, proofTolerance)", recovery, StringComparison.Ordinal);
        Assert.Contains("provenOldEndpoint = foreignEndpointProof!.ProvenEndpoint!", foreignProof, StringComparison.Ordinal);
        Assert.DoesNotContain("storedOriginTarget", foreignProof, StringComparison.Ordinal);
        Assert.DoesNotContain("storedOriginSlit", foreignProof, StringComparison.Ordinal);
        Assert.Contains("return new StageResult(foreignEndpointProof!.Gate", foreignProof, StringComparison.Ordinal);
        Assert.Contains("sameEpochSlitStabilityError", recovery, StringComparison.Ordinal);
        Assert.Contains("Phd2RecoveryFieldTranslationPolicy.Evaluate(", recovery, StringComparison.Ordinal);
        Assert.Contains("Phd2RecoveryReturnVerificationPolicy.Evaluate(", recovery, StringComparison.Ordinal);
        Assert.Contains("oldAbsoluteTargetAndSlitUsedForForeignProof = false", recovery, StringComparison.Ordinal);
        Assert.Contains("motionBudgetReset = false", recovery, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((int)Phd2LockShiftPendingPhase.AwaitingOperationBoundSettle)]
    [InlineData((int)Phd2LockShiftPendingPhase.AwaitingFreshResidual)]
    [InlineData((int)Phd2LockShiftPendingPhase.ReturnRequired)]
    public void ForeignEndpointPolicyAcceptsOnlyReadbackVerifiedPostDispatchEndpoints(int phaseValue)
    {
        var phase = (Phd2LockShiftPendingPhase)phaseValue;
        var state = CreateState("old-run", phase) with
        {
            CurrentLockX = 603.14,
            CurrentLockY = 559.29,
            RequestedLockX = 603.14,
            RequestedLockY = 559.29,
        };

        var proof = Phd2ForeignRecoveryEndpointPolicy.Evaluate(state, 5);

        Assert.Equal(GateDisposition.Passed, proof.Gate.Disposition);
        Assert.Equal("PHD2_LOCK_RECOVERY_FOREIGN_ENDPOINT_PROVEN", proof.Gate.Code);
        Assert.True(proof.EndpointPhaseProven);
        Assert.Equal(new Phd2Point(603.14, 559.29), proof.ProvenEndpoint);
        Assert.Equal(0, proof.CurrentRequestedLockErrorPixels, 9);
    }

    [Theory]
    [InlineData((int)Phd2LockShiftPendingPhase.StageIntent)]
    [InlineData((int)Phd2LockShiftPendingPhase.SettledBudgetLedger)]
    public void ForeignEndpointPolicyRejectsPhasesWithoutPostDispatchReadbackProof(int phaseValue)
    {
        var phase = (Phd2LockShiftPendingPhase)phaseValue;
        var state = CreateState("old-run", phase) with
        {
            RequestedLockX = 105,
            RequestedLockY = 100,
        };

        var proof = Phd2ForeignRecoveryEndpointPolicy.Evaluate(state, 5);

        Assert.Equal(GateDisposition.Indeterminate, proof.Gate.Disposition);
        Assert.Equal("PHD2_LOCK_RECOVERY_FOREIGN_ENDPOINT_UNPROVEN", proof.Gate.Code);
        Assert.False(proof.EndpointPhaseProven);
        Assert.Null(proof.ProvenEndpoint);
    }

    [Fact]
    public void ForeignEndpointPolicyRejectsARequestedEndpointThatWasNotTheVerifiedCurrentReadback()
    {
        var state = CreateState("old-run", Phd2LockShiftPendingPhase.AwaitingFreshResidual) with
        {
            CurrentLockX = 603.14,
            CurrentLockY = 559.29,
            RequestedLockX = 613.14,
            RequestedLockY = 559.29,
        };

        var proof = Phd2ForeignRecoveryEndpointPolicy.Evaluate(state, 5);

        Assert.Equal(GateDisposition.Indeterminate, proof.Gate.Disposition);
        Assert.True(proof.EndpointPhaseProven);
        Assert.Null(proof.ProvenEndpoint);
        Assert.Equal(10, proof.CurrentRequestedLockErrorPixels, 9);
    }

    [Fact]
    public void ReturnRequiredEndpointStillRejectsAnAmbiguousCommandWindow()
    {
        var state = CreateState("old-run", Phd2LockShiftPendingPhase.ReturnRequired) with
        {
            CurrentLockX = 304.75,
            CurrentLockY = 960.92,
            RequestedLockX = 314.75,
            RequestedLockY = 960.92,
        };

        var proof = Phd2ForeignRecoveryEndpointPolicy.Evaluate(state, 5);

        Assert.Equal(GateDisposition.Indeterminate, proof.Gate.Disposition);
        Assert.True(proof.EndpointPhaseProven);
        Assert.Null(proof.ProvenEndpoint);
        Assert.Equal(10, proof.CurrentRequestedLockErrorPixels, 9);
    }

    [Fact]
    public void RecoveryEpisodePlannerLedgerRefreshesOnlyActiveTimeAndPreservesDurableDebt()
    {
        var state = CreateState("old-run", Phd2LockShiftPendingPhase.AwaitingFreshResidual) with
        {
            AttemptsUsed = 3,
            CumulativeCommandedPixels = 37.5,
            StartedUtc = DateTimeOffset.UtcNow - TimeSpan.FromHours(2),
        };
        var episodeStartedUtc = DateTimeOffset.UtcNow;

        var planner = state.ToRecoveryEpisodePlannerLedger(episodeStartedUtc);

        Assert.Equal(episodeStartedUtc, planner.StartedUtc);
        Assert.Equal(state.LineageId, planner.LineageId);
        Assert.Equal(state.AttemptsUsed, planner.AttemptsUsed);
        Assert.Equal(state.CumulativeCommandedPixels, planner.CumulativeCommandedPixels);
        Assert.Equal(new Phd2Point(state.OriginLockX, state.OriginLockY), planner.OriginLockPosition);
        Assert.Equal(new Phd2Point(state.CurrentLockX, state.CurrentLockY), planner.CurrentLockPosition);
        Assert.NotEqual(planner.StartedUtc, state.StartedUtc);
    }

    [Fact]
    public void RecoveryFieldTranslationUsesThreeOrdinaryStarsInsteadOfSaturatedTargetCentroid()
    {
        var expected = new Phd2Point(-2.22, 12.30);
        var beforeTarget = new Phd2Point(865.30, 511.15);
        var afterTarget = new Phd2Point(870.86, 524.81); // Field evidence from the reported Deneb failure.
        var beforeSlit = new Phd2Point(817.44, 425.97);
        var afterSlit = new Phd2Point(817.48, 426.97);
        var before = new[]
        {
            Candidate(100, 100),
            Candidate(250, 160),
            Candidate(420, 700),
            Candidate(700, 800),
        };
        var after = before
            .Select(star => Candidate(
                star.Centroid.X + expected.X + 0.10,
                star.Centroid.Y + expected.Y - 0.08))
            .Append(Candidate(afterTarget.X, afterTarget.Y))
            .ToArray();

        var proof = Phd2RecoveryFieldTranslationPolicy.Evaluate(
            before,
            after,
            beforeTarget,
            afterTarget,
            beforeSlit,
            afterSlit,
            expected,
            proofTolerancePixels: 2);

        Assert.Equal(GateDisposition.Passed, proof.Gate.Disposition);
        Assert.Equal("PHD2_LOCK_RETURN_FIELD_TRANSLATION_VERIFIED", proof.Gate.Code);
        Assert.Equal(4, proof.MatchedStars);
        Assert.NotNull(proof.ObservedTranslation);
        Assert.Equal(expected.X + 0.10, proof.ObservedTranslation!.X, 6);
        Assert.Equal(expected.Y - 0.08, proof.ObservedTranslation.Y, 6);
        Assert.True(proof.ExpectedTranslationErrorPixels < 0.2);
    }

    [Fact]
    public void RecoveryFieldTranslationNeverFabricatesProofFromFewerThanThreeStars()
    {
        var expected = new Phd2Point(5, -4);
        var before = new[] { Candidate(100, 100), Candidate(300, 300) };
        var after = before
            .Select(star => Candidate(star.Centroid.X + expected.X, star.Centroid.Y + expected.Y))
            .ToArray();

        var proof = Phd2RecoveryFieldTranslationPolicy.Evaluate(
            before,
            after,
            new Phd2Point(800, 500),
            new Phd2Point(805, 496),
            new Phd2Point(817, 427),
            new Phd2Point(817, 427),
            expected,
            proofTolerancePixels: 2);

        Assert.Equal(GateDisposition.Indeterminate, proof.Gate.Disposition);
        Assert.Equal("PHD2_LOCK_RETURN_FIELD_TRANSLATION_INSUFFICIENT", proof.Gate.Code);
        Assert.Equal(2, proof.MatchedStars);
    }

    [Fact]
    public void RecoveryAlreadyAtOriginClosesFromFreshIdentityAndStableSlitWithoutDemandingFakeMotion()
    {
        var unavailableField = Phd2RecoveryFieldTranslationPolicy.Evaluate(
            Array.Empty<StarCandidate>(),
            Array.Empty<StarCandidate>(),
            new Phd2Point(800, 500),
            new Phd2Point(808, 507),
            new Phd2Point(817, 427),
            new Phd2Point(817.5, 427.5),
            new Phd2Point(0, 0),
            proofTolerancePixels: 2);

        var result = Phd2RecoveryReturnVerificationPolicy.Evaluate(
            foreignRun: true,
            returnDelta: new Phd2Point(0, 0),
            measuredTargetDelta: new Phd2Point(8, 7),
            exactOriginLockErrorPixels: 0,
            originSlitErrorPixels: 999,
            sameEpochSlitStabilityErrorPixels: 0.71,
            targetIdentityConfirmed: true,
            unavailableField,
            proofTolerancePixels: 2);

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.True(result.NoMotionReturn);
        Assert.True(result.ExactOriginLockVerified);
        Assert.True(result.SlitVerified);
        Assert.False(result.TargetVectorVerified);
        Assert.False(result.FieldTranslationVerified);
    }

    [Fact]
    public void RecoveryAcceptsOrdinaryStarTranslationWhenSaturatedTargetCentroidWandersCrossAxis()
    {
        var expected = new Phd2Point(-2.22, 12.30);
        var field = new Phd2RecoveryFieldTranslationProof(
            GateResult.Pass("PHD2_LOCK_RETURN_FIELD_TRANSLATION_VERIFIED", "verified"),
            new Phd2Point(-2.12, 12.22),
            MatchedStars: 8,
            ResidualRmsPixels: 0.4,
            ExpectedTranslationErrorPixels: 0.13);

        var result = Phd2RecoveryReturnVerificationPolicy.Evaluate(
            foreignRun: true,
            expected,
            measuredTargetDelta: new Phd2Point(5.56, 13.66),
            exactOriginLockErrorPixels: 0,
            originSlitErrorPixels: 999,
            sameEpochSlitStabilityErrorPixels: 1,
            targetIdentityConfirmed: true,
            field,
            proofTolerancePixels: 2);

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.False(result.NoMotionReturn);
        Assert.False(result.TargetVectorVerified);
        Assert.True(result.FieldTranslationVerified);
        Assert.False(result.FreshSlitReacquisitionRequired);
    }

    [Fact]
    public void ForeignReturnClosesFromOrdinaryStarTranslationWhileQuarantiningAnUnstableSlitFit()
    {
        var expected = new Phd2Point(4.71, -24.49);
        var field = new Phd2RecoveryFieldTranslationProof(
            GateResult.Pass("PHD2_LOCK_RETURN_FIELD_TRANSLATION_VERIFIED", "33 stars verified"),
            new Phd2Point(3.848, -24.488),
            MatchedStars: 33,
            ResidualRmsPixels: 1.221,
            ExpectedTranslationErrorPixels: 0.862);

        var result = Phd2RecoveryReturnVerificationPolicy.Evaluate(
            foreignRun: true,
            expected,
            measuredTargetDelta: new Phd2Point(0.0, -25.904),
            exactOriginLockErrorPixels: 0,
            originSlitErrorPixels: 999,
            sameEpochSlitStabilityErrorPixels: 4,
            targetIdentityConfirmed: true,
            field,
            proofTolerancePixels: 2);

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal("PHD2_LOCK_RESTART_RETURN_FRESHLY_VERIFIED", result.Gate.Code);
        Assert.True(result.FieldTranslationVerified);
        Assert.False(result.SlitVerified);
        Assert.True(result.FreshSlitReacquisitionRequired);
        Assert.Equal(1, result.Gate.Metrics!["freshSlitReacquisitionRequired"]);
    }

    [Fact]
    public void SameRunReturnStillRejectsAnUnstableSlitEvenWhenFieldTranslationPasses()
    {
        var field = new Phd2RecoveryFieldTranslationProof(
            GateResult.Pass("PHD2_LOCK_RETURN_FIELD_TRANSLATION_VERIFIED", "verified"),
            new Phd2Point(5, -4),
            MatchedStars: 8,
            ResidualRmsPixels: 0.4,
            ExpectedTranslationErrorPixels: 0.1);

        var result = Phd2RecoveryReturnVerificationPolicy.Evaluate(
            foreignRun: false,
            returnDelta: new Phd2Point(5, -4),
            measuredTargetDelta: new Phd2Point(5, -4),
            exactOriginLockErrorPixels: 0,
            originSlitErrorPixels: 4,
            sameEpochSlitStabilityErrorPixels: 0,
            targetIdentityConfirmed: true,
            field,
            proofTolerancePixels: 2);

        Assert.Equal(GateDisposition.Indeterminate, result.Gate.Disposition);
        Assert.False(result.SlitVerified);
        Assert.False(result.FreshSlitReacquisitionRequired);
    }

    [Fact]
    public void RecoveryStillBlocksWhenNeitherTargetNorOrdinaryFieldProvesANonzeroReturn()
    {
        var field = new Phd2RecoveryFieldTranslationProof(
            GateResult.Unknown("PHD2_LOCK_RETURN_FIELD_TRANSLATION_MISMATCH", "mismatch"),
            new Phd2Point(7, 7),
            MatchedStars: 4,
            ResidualRmsPixels: 0.5,
            ExpectedTranslationErrorPixels: 9);

        var result = Phd2RecoveryReturnVerificationPolicy.Evaluate(
            foreignRun: true,
            returnDelta: new Phd2Point(-2.22, 12.30),
            measuredTargetDelta: new Phd2Point(20, -10),
            exactOriginLockErrorPixels: 0,
            originSlitErrorPixels: 999,
            sameEpochSlitStabilityErrorPixels: 1,
            targetIdentityConfirmed: true,
            field,
            proofTolerancePixels: 2);

        Assert.Equal(GateDisposition.Indeterminate, result.Gate.Disposition);
        Assert.Equal("PHD2_LOCK_RESTART_RETURN_RESIDUAL_MISMATCH", result.Gate.Code);
        Assert.False(result.TargetVectorVerified);
        Assert.False(result.FieldTranslationVerified);
    }

    [Fact]
    public void PersistedRecoveryUsesEpisodeClockOnlyForReturnWhileImmediateFailureKeepsLineageClock()
    {
        var persisted = Section(
            "private async Task<StageResult> RecoverPersistedPhd2LockToOriginAsync(",
            "private async Task<StageResult> PlaceTargetOnSlitWithPhd2Async(");
        var immediateWrapper = Section(
            "private Task<StageResult> ReturnPhd2LockToOriginAsync(",
            "private async Task<StageResult> ReturnPhd2LockToOriginCoreAsync(");
        var core = Section(
            "private async Task<StageResult> ReturnPhd2LockToOriginCoreAsync(",
            "private async Task<IReadOnlyList<Phd2GuidingResidualState>> CapturePhd2GuidingMeasurementsAsync(");

        Assert.Contains("recoveryEpisodeStartedUtc: DateTimeOffset.UtcNow", persisted, StringComparison.Ordinal);
        Assert.Contains("recoveryEpisodeStartedUtc: null", immediateWrapper, StringComparison.Ordinal);
        Assert.Contains("state.ToRecoveryEpisodePlannerLedger(activeRecoveryStartedUtc)", core, StringComparison.Ordinal);
        Assert.Contains("durableLineageClockRewritten = false", core, StringComparison.Ordinal);
        Assert.Contains("attemptBudgetReset = false", core, StringComparison.Ordinal);
        Assert.Contains("cumulativeMotionBudgetReset = false", core, StringComparison.Ordinal);
        Assert.Contains("outboundPlacementAuthorizedByRecoveryClock = false", core, StringComparison.Ordinal);
        Assert.DoesNotContain("StartedUtc = activeRecoveryStartedUtc", core, StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessfulDurableReturnAutomaticallyRebuildsFreshG3BeforePlacementContinues()
    {
        var discovery = Section(
            "private async Task<StageResult?> RecoverOutstandingPhd2LockBeforePlacementAsync(",
            "private async Task<StageResult> RecoverPersistedPhd2LockToOriginAsync(");

        Assert.Contains("PHD2_LOCK_FAILURE_RETURNED", discovery, StringComparison.Ordinal);
        Assert.Contains("AcquireG3SlitFieldAsync(", discovery, StringComparison.Ordinal);
        Assert.Contains("allowChargedCurrentPositionHandoff: true", discovery, StringComparison.Ordinal);
        Assert.Contains("phd2-lock-return-auto-g3-reacquisition", discovery, StringComparison.Ordinal);
        Assert.Contains("return null;", discovery, StringComparison.Ordinal);
    }

    [Fact]
    public void ReturnRequiredAtOriginStillTakesFreshVerifiedRecoveryInsteadOfCoordinateOnlySettlement()
    {
        var discovery = Section(
            "private async Task<StageResult?> RecoverOutstandingPhd2LockBeforePlacementAsync(",
            "private async Task<StageResult> RecoverPersistedPhd2LockToOriginAsync(");

        Assert.DoesNotContain("Phd2ZeroMotionSettlementPolicy", discovery, StringComparison.Ordinal);
        Assert.DoesNotContain("PHD2_LOCK_ZERO_MOTION_SETTLEMENT_ALLOWED", discovery, StringComparison.Ordinal);
        Assert.Contains("RecoverPersistedPhd2LockToOriginAsync(", discovery, StringComparison.Ordinal);
        Assert.Contains("A durable endpoint tuple equal to the recorded origin", discovery, StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroVectorCanOnlyEnterFreshNoMotionVerificationAcrossAPierOnlyChange()
    {
        var east = new Phd2SensorTopology("install", 2, "profile", "G3", "camera", "mount", new string('A', 64),
            1920, 1080, 1, new Phd2Rectangle(0, 0, 1920, 1080), Phd2ImageCoordinateDomain.FullSensorCoordinates,
            0, Phd2SensorRotationAuthority.QualifiedPhd2Calibration, "pierEast");
        var west = east with { PierSide = "pierWest" };
        var state = CreateState("old-run", Phd2LockShiftPendingPhase.ReturnRequired) with
        {
            TopologyFingerprintSha256 = east.ComputeFingerprintSha256(),
            CurrentLockX = 100, CurrentLockY = 100, RequestedLockX = 100, RequestedLockY = 100,
        };
        Assert.True(Phd2ZeroVectorPierRecoveryPolicy.CanVerifyWithoutMotion(state, west));
        Assert.False(Phd2ZeroVectorPierRecoveryPolicy.CanVerifyWithoutMotion(state with { CurrentLockX = 100.000001 }, west));
        Assert.False(Phd2ZeroVectorPierRecoveryPolicy.CanVerifyWithoutMotion(state with { RequestedLockY = 101 }, west));
        Assert.False(Phd2ZeroVectorPierRecoveryPolicy.CanVerifyWithoutMotion(state with { Phase = Phd2LockShiftPendingPhase.StageIntent }, west));
        Assert.False(Phd2ZeroVectorPierRecoveryPolicy.CanVerifyWithoutMotion(state, west with { CameraStableId = "changed" }));
        Assert.False(Phd2ZeroVectorPierRecoveryPolicy.CanVerifyWithoutMotion(state, west with { Binning = 2 }));
        Assert.False(Phd2ZeroVectorPierRecoveryPolicy.CanVerifyWithoutMotion(state, west with { PierSide = "Unknown" }));
        Assert.Equal(Phd2LockShiftPendingPhase.ReturnRequired, state.Phase);
        Assert.Contains("prohibitReturnMotion && !plan.IsComplete", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("prohibitReturnMotion: zeroVectorAcrossPier", RunnerSource, StringComparison.Ordinal);
    }

    private static StarCandidate Candidate(double x, double y) => new(
        new PixelPoint(x, y),
        PeakAdu: 12000,
        FluxAdu: 50000,
        SignalToNoise: 25,
        FwhmPixels: 4,
        Ellipticity: 0.1,
        SaturatedFraction: 0,
        EdgeDistancePixels: 100);

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
        Assert.Contains("selectedIsForeign", discovery, StringComparison.Ordinal);
        Assert.Contains("ValidateForeignPhd2LockRecoveryBinding", discovery, StringComparison.Ordinal);
        Assert.Contains("ValidateCurrentPhd2LockLedgerBinding", discovery, StringComparison.Ordinal);
        Assert.DoesNotContain("PHD2_LOCK_FOREIGN_RUN_NOT_TERMINAL", discovery, StringComparison.Ordinal);
    }

    [Fact]
    public void ForeignReturnDebtDoesNotRequireTheNewObservationToReuseTheOldTargetContext()
    {
        var discovery = Section(
            "private async Task<StageResult?> RecoverOutstandingPhd2LockBeforePlacementAsync(",
            "private async Task<StageResult> RecoverPersistedPhd2LockToOriginAsync(");
        var foreignBinding = Section(
            "private GateResult ValidateForeignPhd2LockRecoveryBinding(",
            "private GateResult ValidatePhd2LockLedgerOperationalBinding(");
        var operationalBinding = Section(
            "private GateResult ValidatePhd2LockLedgerOperationalBinding(",
            "private async Task<GateResult> PersistCurrentRunPhd2BudgetHandoffAsync(");

        Assert.Contains(
            "selectedIsForeign\n            ? ValidateForeignPhd2LockRecoveryBinding",
            discovery.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.DoesNotContain("ComputeSlitRecoveryContextSha256", foreignBinding, StringComparison.Ordinal);
        Assert.DoesNotContain("ComputeSlitRecoveryContextSha256", operationalBinding, StringComparison.Ordinal);
        Assert.Contains("ActionConfigurationSha256", operationalBinding, StringComparison.Ordinal);
        Assert.Contains("CommissioningPresetSha256", operationalBinding, StringComparison.Ordinal);
        Assert.Contains("CalibrationQualityPolicySha256", operationalBinding, StringComparison.Ordinal);
        Assert.Contains("TopologyFingerprintSha256", operationalBinding, StringComparison.Ordinal);
        Assert.Contains("MaximumCumulativePixels", operationalBinding, StringComparison.Ordinal);
    }

    [Fact]
    public void SameRunOutboundContinuationStillRequiresTheExactObservationContext()
    {
        var currentBinding = Section(
            "private GateResult ValidateCurrentPhd2LockLedgerBinding(",
            "private GateResult ValidateForeignPhd2LockRecoveryBinding(");

        Assert.Contains("ComputeSlitRecoveryContextSha256(context)", currentBinding, StringComparison.Ordinal);
        Assert.Contains("cannot continue outbound placement", currentBinding, StringComparison.Ordinal);
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

    [Theory]
    [InlineData(Phd2ImageCoordinateDomain.FullSensorCoordinates)]
    [InlineData(Phd2ImageCoordinateDomain.RoiLocalCoordinates)]
    public void NonzeroRoiApertureResidualUsesTheSameLocalDomainAsTheMeasuredSlit(Phd2ImageCoordinateDomain domain)
    {
        var preset = CreatePreset() with { CoordinateDomain = domain, RoiX = 500, RoiY = 200, RoiWidth = 300, RoiHeight = 300 };
        var slit = new UvexAdv.Observatory.SlitGeometry("frame-local", new(100, 100), 0, 100, 3, 0.5, "guide", 1, 1);
        var domainTarget = domain == Phd2ImageCoordinateDomain.FullSensorCoordinates
            ? new Phd2Point(610, 301) : new Phd2Point(110, 101);
        var conversion = typeof(RealObservationStageRunner).GetMethod("ToFrameLocal",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var local = Assert.IsType<UvexAdv.Observatory.PixelPoint>(conversion.Invoke(null, [domainTarget, preset]));
        var residual = Phd2PlacementGuideWindowPolicy.ProjectOnMeasuredSlit(local, slit)!;
        Assert.Equal(10, residual.AlongSlitPixels, 8);
        Assert.Equal(1, residual.CrossSlitPixels, 8);
        Assert.True(Phd2PlacementGuideWindowPolicy.CanProbeAlongSlitWithPrecisionWarning(
            true, true, [residual, residual, residual], 2, 2, 75));
        var offSlitDomainTarget = domainTarget with { Y = domainTarget.Y + 8 };
        var offSlitLocal = Assert.IsType<UvexAdv.Observatory.PixelPoint>(conversion.Invoke(null, [offSlitDomainTarget, preset]));
        var offSlit = Phd2PlacementGuideWindowPolicy.ProjectOnMeasuredSlit(offSlitLocal, slit)!;
        Assert.False(Phd2PlacementGuideWindowPolicy.CanProbeAlongSlitWithPrecisionWarning(
            true, true, [offSlit, offSlit, offSlit], 2, 2, 75));
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
