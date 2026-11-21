using NINA.Core.Enum;
using NINA.Image.Interfaces;
using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private readonly AdaptiveGuidingRecovery localGuidingRecovery = new();
    private AdaptiveGuidingRecovery GuideRecovery => NativeTarget?.GuidingRecovery ?? localGuidingRecovery;
    private bool adaptiveCalibrationPending;
    private bool AdaptiveGuideRecoveryEnabled => configuration.SequencePlan?.AutomaticGuideRecovery ?? true;
    private bool HasNoOutstandingGuideRecoveryMotion => pendingG3SearchReturn is null &&
        (durableG3AcquisitionMotion is null or { Phase: G3AcquisitionMotionPhase.SettledBudgetLedger }) &&
        (pendingSlitPlacement is null or { Phase: SlitPlacementPendingPhase.SettledBudgetLedger }) &&
        (pendingPhd2LockShift is null or { Phase: Phd2LockShiftPendingPhase.SettledBudgetLedger });

    private async Task<(bool Retry, StageResult Result)> PrepareAdaptiveGuidingRecoveryAsync(
        ObservationStage stage, StageResult original, ObservationContext context, CancellationToken token)
    {
        if (stage == ObservationStage.RunScienceBlock &&
            AtrScienceFrameQualityPolicy.RemainingAttemptGate(savedAtrFrames, attemptedAtrFrames, configuration.Atr) is { } attemptGate)
            return (false, new StageResult(attemptGate));
        // These are prerequisites, not precision thresholds. In particular a
        // lost star during unreturned lock motion cannot authorize a new guide.
        if (!HasNoOutstandingGuideRecoveryMotion || !phd2.IsConnected)
            return (false, original);
        // Retain the existing no-motion science continuation before stopping a
        // guide that can still pass a fresh same-epoch slit check.
        if (stage is ObservationStage.SelectAtrExposure or ObservationStage.RunScienceBlock &&
            Phd2ScienceContinuationPolicy.MayVerifySettledGuideInPlace(original.Gate.Code) &&
            GuideRecovery.TryConsumeInPlaceCheck())
        {
            try
            {
                if (await TryVerifyScienceInPlaceAsync(context, original.Gate.Code, token).ConfigureAwait(false)) return (true, original);
            }
            catch (PhysicalActionGateException ex) when (ex.Gate.Code == "GUIDING_LOST") { }
        }
        if (stage is ObservationStage.SelectAtrExposure or ObservationStage.RunScienceBlock &&
            Phd2ScienceContinuationPolicy.MayVerifySettledGuideInPlace(original.Gate.Code) &&
            await WaitForSameFieldGuideSelfRecoveryAsync(context, original.Gate.Code, token).ConfigureAwait(false))
        {
            // Only the just-attested optical window can issue this one-shot
            // receipt. The shutter path still checks its age, hash and owner.
            if (preExposureReceipt is not null && IsGuidingStable()) return (true, original);
            try
            {
                if (await TryVerifyScienceInPlaceAsync(context, original.Gate.Code, token).ConfigureAwait(false))
                    return (true, original);
            }
            catch (PhysicalActionGateException ex) when (ex.Gate.Code == "GUIDING_LOST") { }
        }
        var stopped = await EnsurePhdStoppedForAutomaticRebuildAsync(stage,
            original.Gate.Code, token).ConfigureAwait(false);
        if (stopped.Disposition != GateDisposition.Passed) return (false, new StageResult(stopped));
        if (automaticRebuildStopProof?.IsCurrent(phd2.Snapshot) != true)
            return (false, new StageResult(GateResult.Unknown("PHD2_ADAPTIVE_STOP_RECEIPT_REQUIRED",
                "导星恢复没有本轮有效的停止回执；未接管外部会话、等待取帧或重校准。")));

        GuideRecovery.BeginCheck(DateTimeOffset.UtcNow);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(configuration.SequencePlan?.GuideStarWaitSeconds ?? 300);
        var origin = CaptureG3FrameMountReadback();
        var quiet = await WaitForPostCalibrationMountIdleAsync(automaticRebuildStopProof!, origin.PierSide,
            token, phaseDescription: "本轮导星已确认停止").ConfigureAwait(false);
        if (!quiet.CanAdvance) return (false, quiet);
        var off = await EnsureSlitIlluminationOffAsync("guide recovery star detection", true, token).ConfigureAwait(false);
        if (off.Issue is not null)
            return (false, new StageResult(GateResult.Unknown("PHD2_RECOVERY_LED_OFF_UNCONFIRMED", off.Issue)));
        var evidence = original.EvidencePath;
        // The count fence remains effective even if the wall clock moves back.
        for (var sample = 0; sample < 42; sample++)
        {
            token.ThrowIfCancellationRequested();
            if (SequenceStopGate() is { } terminal) return (false, new StageResult(terminal, evidence));
            await RequireImmediatePhysicalActionGatesAsync(context, token).ConfigureAwait(false);
            if (!HasNoOutstandingGuideRecoveryMotion ||
                automaticRebuildStopProof?.IsCurrent(phd2.Snapshot) != true)
                return (false, new StageResult(GateResult.Unknown("PHD2_ADAPTIVE_OWNER_CHANGED",
                    "等待期间所有权或运动责任发生变化；不重新选星或校准。"), evidence));
            var mount = ValidateG3SearchMountState(origin.PierSide);
            if (mount.Disposition != GateDisposition.Passed) return (false, new StageResult(mount, evidence));
            if (DateTimeOffset.UtcNow >= deadline || GuideRecovery.Expired(DateTimeOffset.UtcNow)) break;
            var identity = await phd2.ValidateIdentityAsync(PhdIdentityRequirement(), token).ConfigureAwait(false);
            if (!identity.IsValid) throw new Phd2IdentityMismatchException(identity);
            var path = ReserveRunEvidencePath("phd2-recovery-star-check", ".fit");
            // Same atomic native capture used by G3 acquisition. A preview loop
            // can reselect a star; it is not an inert star-detection operation.
            var capture = await Phd2AdaptiveStarCapture.CaptureAsync(phd2, new Phd2SingleFrameRequest(
                2000, configuration.G3.Binning, configuration.G3.GainPercent, path), token).ConfigureAwait(false);
            var captured = capture.Frame;
            var after = capture.After;
            if (capture.Rejection is { } rejection)
            {
                evidence = await PublishRunJsonEvidenceAsync("phd2-adaptive-capture-rejected",
                    "Native star-check ownership readback rejected; original guiding failure retained",
                    new { sourceCode = original.Gate.Code, rejection, capture.Before, capture.After,
                        captured, motionIssued = false, budgetReset = false }, path, token).ConfigureAwait(false);
                return (false, new StageResult(GateResult.Unknown("PHD2_ADAPTIVE_CAPTURE_OWNER_CHANGED",
                    $"原生星点检测后的停止/设备证明不一致（{rejection}）；原导星问题 {original.Gate.Code}，未请求导星。"), evidence));
            }
            var sha = await ComputeFileSha256Async(path, token).ConfigureAwait(false);
            var readback = CaptureG3FrameMountReadback();
            var binding = CreateG3FieldMountBinding(context, path, sha, captured.CompletedUtc, readback);
            var unchangedPointing = G3FieldMountBindingPolicy.ValidateForMotion(binding,
                context.Plan.ObservationRunId, configuration.ActionConfigurationSha256, commissioning!.Sha256,
                path, sha, origin.RightAscensionDegrees, origin.DeclinationDegrees,
                origin.CoordinateEpoch, origin.PierSide, MountCommandArrivalToleranceArcseconds);
            if (unchangedPointing.Disposition != GateDisposition.Passed)
                return (false, new StageResult(unchangedPointing, path));
            var image = await imageDataFactory.CreateFromFile(path, 16, false, RawConverterEnum.FREEIMAGE, token).ConfigureAwait(false);
            var imageGate = ValidateG3SolveProbeImage(captured, image, 2000);
            if (imageGate.Disposition != GateDisposition.Passed) return (false, new StageResult(imageGate, path));
            var bindingGate = await ValidateG3FieldMountBindingForMotionAsync(context,
                G3FieldState.Failed(imageGate, path, image, mountBinding: binding), token).ConfigureAwait(false);
            if (bindingGate.Disposition != GateDisposition.Passed) return (false, new StageResult(bindingGate, path));
            var frame = G3FrameInputPolicy.Create(image.Properties.Width, image.Properties.Height,
                image.Data.FlatArray, configuration.G3);
            var content = G3SolveProbeContentAnalyzer.Analyze(frame);
            var action = DateTimeOffset.UtcNow >= deadline ? GuideRecoveryAction.Exhausted :
                GuideRecovery.Observe(DateTimeOffset.UtcNow, captured.CompletedUtc, true,
                    content.StellarMeasurement.StarCount, content.StellarMeasurement.MedianSignalToNoise,
                    content.SaturatedPixelFraction);
            var searchRadius = commissioning?.Value.Phd2SlitPlacement?.TargetSearchRadiusPixels ??
                configuration.Slit.TargetPredictionTolerancePixels;
            var recoverySeedPrediction = TryRegisterSameFieldRecoverySeed(
                lastG3Field,
                content.StellarMeasurement.Stars,
                frame.Width, frame.Height, searchRadius, token);
            var registrationSource = "focus-detector";
            if (recoverySeedPrediction is null && action == GuideRecoveryAction.Reselect &&
                stage is ObservationStage.SelectAtrExposure or ObservationStage.RunScienceBlock &&
                lastG3Field is { Frame: { } oldFrame, Image: { } oldImage, MountBinding: { } oldBinding } old &&
                oldBinding.ObservationRunId == binding.ObservationRunId &&
                oldBinding.CoordinateEpoch == binding.CoordinateEpoch && oldBinding.PierSide == binding.PierSide &&
                SameHash(oldBinding.ActionConfigurationSha256, binding.ActionConfigurationSha256) &&
                SameHash(oldBinding.CommissioningPresetSha256, binding.CommissioningPresetSha256) &&
                SameHash(oldBinding.ComputeBindingSha256(), oldBinding.BindingSha256) &&
                G3TargetTrackPolicy.MatchesImmutablePixels(oldFrame, oldImage.Data.FlatArray) &&
                string.Equals(old.FramePath, oldBinding.FramePath, StringComparison.OrdinalIgnoreCase) &&
                SameHash(await ComputeFileSha256Async(old.FramePath, token).ConfigureAwait(false), oldBinding.FrameSha256))
            {
                // Use matched extraction on both immutable originals. A registration is a search
                // hint only; it cannot replace the stopped-owner, fresh slit or new guide proof.
                var a = await OptionalTargetTrackStarsAsync(oldFrame, oldImage.Data.FlatArray,
                    oldBinding.FrameSha256, token).ConfigureAwait(false);
                var b = await OptionalTargetTrackStarsAsync(frame, image.Data.FlatArray, sha, token).ConfigureAwait(false);
                if (DateTimeOffset.UtcNow < deadline &&
                    SameHash(await ComputeFileSha256Async(path, token).ConfigureAwait(false), sha))
                    recoverySeedPrediction = TryRegisterSameFieldRecoverySeed(old with { Candidates = a },
                        b, frame.Width, frame.Height, searchRadius, token);
                registrationSource = "independent-field-extraction";
            }
            var recoverySeedField = recoverySeedPrediction is null
                ? null
                : CreateSameFieldRecoverySeed(
                    lastG3Field!,
                    path,
                    image,
                    frame,
                    content.StellarMeasurement,
                    binding,
                    recoverySeedPrediction);
            evidence = await PublishRunJsonEvidenceAsync("phd2-adaptive-star-check",
                "Fresh field triage, not proof of weather, target identity or slit arrival",
                new { sourceCode = original.Gate.Code, action = action.ToString(), sample,
                    captured.CompletedUtc, content, sha256 = sha, mountBinding = binding, registrationSource,
                    recoverySeed = recoverySeedPrediction is null ? null : new
                    {
                        recoverySeedPrediction.Target,
                        recoverySeedPrediction.Slit.AcquisitionPoint,
                        recoverySeedPrediction.Registration.Code,
                        recoverySeedPrediction.Registration.MatchedStars,
                        recoverySeedPrediction.Registration.MaximumHoldoutResidualPixels,
                        targetMeasured = false,
                    },
                    deadline, GuideRecovery.Reselections, GuideRecovery.CalibrationConsumed,
                    weatherCauseConfirmed = false, motionIssued = false, budgetReset = false }, path, token).ConfigureAwait(false);
            // Atomic capture preserves the stopped epoch. Never manufacture a
            // new stop receipt for a preview loop or an externally restarted guide.
            if (!automaticRebuildStopProof.IsCurrent(phd2.Snapshot))
                return (false, new StageResult(GateResult.Unknown("PHD2_ADAPTIVE_OWNER_CHANGED",
                    "星点检测后停止回执已失效；不继续恢复。"), evidence));
            PublishG3Preview(image, $"导星恢复星点检测 {sample + 1}：{content.StellarMeasurement.StarCount} 星；{action}");
            if (action == GuideRecoveryAction.Exhausted || DateTimeOffset.UtcNow >= deadline)
                return (false, AdaptiveRecoveryExhausted(AdaptiveGuidingRecovery.ExhaustedCode, evidence));
            if (action is GuideRecoveryAction.Reselect or GuideRecoveryAction.ForceCalibration)
            {
                if (stage is ObservationStage.SelectAtrExposure or ObservationStage.RunScienceBlock &&
                    action == GuideRecoveryAction.Reselect &&
                    recoverySeedField is not null)
                {
                    // A long science/probe exposure can outlive the old frame's
                    // moving-mount binding even though the stopped detector
                    // field never moved. Re-register the fresh stopped frame
                    // against the last trusted field, then let PHD2 reselect
                    // and require a new target/slit measurement before resume.
                    lastG3Field = recoverySeedField;
                }
                if (stage is ObservationStage.SelectAtrExposure or ObservationStage.RunScienceBlock &&
                    action == GuideRecoveryAction.Reselect &&
                    await TryReacquireSameFieldGuideWithoutMotionAsync(
                        context, original.Gate.Code, evidence, token).ConfigureAwait(false))
                    return (true, original);
                // The observations above are inert. MarkResumeRecoveryRequired
                // below schedules full G3 acquisition AND slit placement, and
                // ForceCalibration emits pulses. Neither is a no-motion check.
                // If that chain cannot be funded, retain the original cause and
                // skip after checked stop instead of wasting another solve/search.
                if (stage is ObservationStage.SelectAtrExposure or ObservationStage.RunScienceBlock)
                {
                    var budget = await CheckScienceRebuildBudgetAsync(context, original.Gate, token).ConfigureAwait(false);
                    if (budget is not null) return (false, new StageResult(budget, evidence));
                }
                adaptiveCalibrationPending = action == GuideRecoveryAction.ForceCalibration;
                // The old run-wide "guiding ever started" shortcut silently
                // skipped SNR preparation on the very recovery that needed it.
                // Admit once from the current checked stop; all exposure raises
                // remain shared with the original selection and bounded to 6s.
                guideSnrPreparation.AdmitRecovery(automaticRebuildStopProof, phd2.Snapshot);
                Report(adaptiveCalibrationPending
                    ? "warning：重新选星后仍导星异常，连续新帧星场可用；预留本目标唯一一次 PHD2 强制校准，完成后重新定位和入缝。"
                    : "warning：连续新帧星场恢复可用；重新定位并由 PHD2 重新选星，原运动和曝光预算不变。");
                MarkResumeRecoveryRequired();
                return (true, original);
            }
            Report(AdaptiveGuidingRecovery.HasUsableStars(content.StellarMeasurement.StarCount,
                content.StellarMeasurement.MedianSignalToNoise, content.SaturatedPixelFraction)
                ? "导星恢复：已取得一张可用星场，等待下一张独立新帧确认；尚未重新选星或校准。"
                : $"warning：疑似云雾或星场信号不足，等待星点恢复；已检测 {sample + 1} 帧，不改锁点、不发校准脉冲。截止 {deadline:HH:mm:ss} UTC");
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            var delay = GuideRecovery.NextStarCheckDelay;
            await Task.Delay(remaining < delay ? remaining : delay, token).ConfigureAwait(false);
        }
        return (false, AdaptiveRecoveryExhausted(AdaptiveGuidingRecovery.StarsTimeoutCode, evidence));
    }

    private async Task<bool> WaitForSameFieldGuideSelfRecoveryAsync(
        ObservationContext context,
        string sourceCode,
        CancellationToken token)
    {
        preExposureReceipt = null;
        if (phd2SlitPlacementSession is not { } session) return false;
        var preset = commissioning?.Value.Phd2SlitPlacement;
        if (preset is null) return false;
        var tolerance = preset.BuildMotionLimits().LockVerificationTolerancePixels;
        var maximumSeconds = Math.Clamp(configuration.SequencePlan?.GuideStarWaitSeconds ?? 60, 15, 60);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(maximumSeconds);
        Phd2StateSnapshot? previous = null;
        var gatePoll = 0;

        Report($"PHD2 掉星后先在原锁点等待自身恢复最多 {maximumSeconds} 秒；不换场、不移动、不重新选星。");
        while (DateTimeOffset.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (SequenceStopGate() is not null) return false;
            if (gatePoll++ % 5 == 0)
                await RequireImmediatePhysicalActionGatesAsync(context, token).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);

            var current = phd2.Snapshot;
            if (current.ConnectionEpoch != session.ConnectionEpoch ||
                current.AutomationPaused || current.Phd2Paused ||
                current.GuideOutput?.Failed == true ||
                current.PendingSettleOperationId is not null)
                return false;

            var active = Phd2ScienceContinuationPolicy.IsSameFieldGuideActiveAfterLoss(
                current, session.ConnectionEpoch, session.SelectedGuide, session.OriginLock, tolerance);
            if (!active)
            {
                previous = null;
                continue;
            }

            if (previous is not null &&
                previous.GuideEpoch == current.GuideEpoch &&
                previous.SelectedStar == current.SelectedStar &&
                previous.LockPosition == current.LockPosition)
            {
                // Two cached polls are not new guide frames. They only nominate
                // this field for optical revalidation; never edit an epoch on
                // the session alone and leave the durable ledger behind.
                if (current.GuideEpoch != session.GuideEpoch &&
                    !await TryAttestRecoveredFieldAsync(context, session, preset, deadline, token).ConfigureAwait(false))
                    return false;
                await PublishRunJsonEvidenceAsync(
                    "phd2-same-field-guide-self-recovered",
                    "PHD2 recovered the same selected guide field after loss; no selection, motion, calibration or exposure was issued",
                    new
                    {
                        sourceCode,
                        session.ConnectionEpoch,
                        previousGuideEpoch = session.GuideEpoch,
                        current.GuideEpoch,
                        selectedGuide = session.SelectedGuide,
                        settledLock = session.OriginLock,
                        waitSeconds = maximumSeconds,
                        stablePolls = 2,
                        reselectionIssued = false,
                        calibrationIssued = false,
                        mountMotionIssued = false,
                        exposureStarted = false,
                        budgetReset = false,
                    },
                    session.LastMeasurement.Frame.Path,
                    token).ConfigureAwait(false);
                return true;
            }
            previous = current;
        }
        return false;
    }

    private async Task<bool> TryReacquireSameFieldGuideWithoutMotionAsync(
        ObservationContext context,
        string sourceCode,
        string? failedEvidence,
        CancellationToken token)
    {
        var previousSession = phd2SlitPlacementSession;
        var seedField = lastG3Field;
        var unavailable = previousSession is null
            ? "guide-session-not-retained"
            : seedField is null || !HasRecoveryTargetPrediction(seedField)
                ? "target-field-prediction-not-retained"
                : automaticRebuildStopProof?.IsCurrent(phd2.Snapshot) != true
                    ? "stopped-owner-receipt-stale"
                    : !HasNoOutstandingGuideRecoveryMotion
                        ? "unsettled-motion-responsibility"
                        : phd2.Snapshot.GuideOutput?.Failed == true
                            ? "guide-output-fault-latched"
                            : previousSession.ConnectionEpoch != phd2.Snapshot.ConnectionEpoch
                                ? "connection-epoch-changed"
                                : null;
        if (unavailable is not null)
        {
            await PublishRunJsonEvidenceAsync(
                "phd2-same-field-guide-reacquire-unavailable",
                "No-motion local guide recovery was withheld at a named prerequisite",
                new
                {
                    sourceCode,
                    unavailable,
                    connectionEpoch = phd2.Snapshot.ConnectionEpoch,
                    guideEpoch = phd2.Snapshot.GuideEpoch,
                    appState = phd2.Snapshot.AppState.ToString(),
                    hasSession = phd2SlitPlacementSession is not null,
                    hasFieldPrediction = lastG3Field is { } field && HasRecoveryTargetPrediction(field),
                    stopReceiptCurrent = automaticRebuildStopProof?.IsCurrent(phd2.Snapshot) == true,
                    hasNoOutstandingMotion = HasNoOutstandingGuideRecoveryMotion,
                    guideOutputFailed = phd2.Snapshot.GuideOutput?.Failed == true,
                    motionIssued = false,
                    lockMutationIssued = false,
                    budgetReset = false,
                },
                failedEvidence,
                token).ConfigureAwait(false);
            return false;
        }
        ArgumentNullException.ThrowIfNull(previousSession);
        ArgumentNullException.ThrowIfNull(seedField);

        var preset = commissioning?.Value.Phd2SlitPlacement;
        if (preset is null || preset.Validate().Count != 0) return false;
        var ledgerLoad = await Phd2LockShiftPendingStore.LoadAsync(
            Phd2LockShiftPendingPath(context.Plan.ObservationRunId), token).ConfigureAwait(false);
        if (ledgerLoad.Error is not null ||
            ledgerLoad.State is not { Phase: Phd2LockShiftPendingPhase.SettledBudgetLedger } previousLedger)
            return false;

        var currentLedgerBinding = ValidateCurrentPhd2LockLedgerBinding(context, preset, previousLedger);
        if (currentLedgerBinding.Disposition != GateDisposition.Passed)
        {
            await PublishRunJsonEvidenceAsync(
                "phd2-same-field-guide-reacquire-ledger-rejected",
                "Current PHD2 owner/lock no longer proves the durable motion lineage",
                new { sourceCode, currentLedgerBinding, previousLedger.LineageId,
                    previousLedger.ConnectionEpoch, previousLedger.GuideEpoch,
                    motionIssued = false, lockMutationIssued = false, budgetReset = false },
                failedEvidence,
                token).ConfigureAwait(false);
            return false;
        }
        var originalBinding = await ValidateG3FieldMountBindingForMotionAsync(
            context, seedField, token).ConfigureAwait(false);
        if (originalBinding.Disposition != GateDisposition.Passed)
        {
            await WriteAuditBestEffortAsync("phd2-same-field-guide-reacquire-binding-rejected", new
            {
                context.Plan.ObservationRunId, sourceCode, originalBinding,
                seedField.FramePath, seedField.MountBinding, adaptiveStarEvidence = failedEvidence,
                motionIssued = false, lockMutationIssued = false, budgetReset = false,
            }).ConfigureAwait(false);
            return false;
        }

        Report("薄云/短时掉星后已取得连续可用星场：尝试在当前离线场原地重新选择导星，不移动赤道仪、不重跑 WCS/邻场搜索。");
        Phd2PlacementGuideChoice choice;
        try
        {
            choice = await AcquireFreshPhd2PlacementGuideAsync(
                context, seedField, preset, token).ConfigureAwait(false);
        }
        catch (Phd2Exception ex) when (ex is Phd2NoGuideStarException or
            Phd2GuideSelectionUnavailableException or Phd2GuidingFrameLostException)
        {
            await PublishRunJsonEvidenceAsync(
                "phd2-same-field-guide-reacquire-rejected",
                "Fresh local guide selection was transiently unavailable; no mount motion or budget reset",
                new { sourceCode, error = ex.Message, retryable = true,
                    motionIssued = false, lockMutationIssued = false,
                    budgetReset = false, fullG3WcsRebuildIssued = false },
                failedEvidence, token).ConfigureAwait(false);
            return false;
        }
        catch (Phd2CommandTimeoutException ex)
        {
            await PublishRunJsonEvidenceAsync(
                "phd2-same-field-guide-reacquire-timed-out",
                "Fresh local guide selection timed out; no mount motion or budget reset",
                new { sourceCode, error = ex.Message, timeoutSeconds = ex.Timeout.TotalSeconds,
                    motionIssued = false, lockMutationIssued = false,
                    budgetReset = false, fullG3WcsRebuildIssued = false },
                failedEvidence, token).ConfigureAwait(false);
            return false;
        }
        if (choice.Selection.Gate.Disposition != GateDisposition.Passed ||
            choice.Field.TargetIdentification.Target is null)
        {
            await PublishRunJsonEvidenceAsync(
                "phd2-same-field-guide-reacquire-rejected",
                "Fresh local target/slit continuity was insufficient; no guide, lock or mount command was issued",
                new { sourceCode, choice.Selection.Gate, choice.SelectionReason,
                    previousConnectionEpoch = previousSession.ConnectionEpoch,
                    previousGuideEpoch = previousSession.GuideEpoch,
                    motionIssued = false, lockMutationIssued = false,
                    reselectionIssued = false, budgetReset = false },
                choice.Field.FramePath ?? failedEvidence, token).ConfigureAwait(false);
            return false;
        }
        lastG3Field = choice.Field;

        var calibration = await phd2.ValidateCalibrationAsync(
            preset.CalibrationQualityPolicy.ApplyHardRejectionCeilings(PhdCalibrationRequirement()),
            token).ConfigureAwait(false);
        if (calibration.Status != Phd2ValidationStatus.Valid ||
            previousSession.Quality.IsLockShiftAuthority != true)
            return false;

        await RequireImmediatePhysicalActionGatesAsync(context, token).ConfigureAwait(false);
        var loop = await phd2.StartLoopingAndWaitForFreshFrameAsync(
            new Phd2LoopingStartRequest(TimeSpan.FromSeconds(preset.FreshLoopFrameTimeoutSeconds)),
            token).ConfigureAwait(false);
        if (!loop.LeavesLoopingForGuideTakeover || loop.StopCommandSent || loop.ExposureChanged)
            return false;

        var preSelectionBinding = await ValidateG3FieldMountBindingForMotionAsync(
            context, lastG3Field, token).ConfigureAwait(false);
        if (preSelectionBinding.Disposition != GateDisposition.Passed)
        {
            await StopPhdAndWaitAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        Phd2PreparedGuideSelection prepared;
        try
        {
            prepared = await PrepareSnrOptimizedGuideSelectionAsync(
                context, choice, preset, loop, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Phd2NativeGuideSelectionExhaustedException or
            Phd2NoGuideStarException or Phd2GuideSelectionUnavailableException or
            Phd2GuidingFrameLostException or Phd2CommandTimeoutException)
        {
            await StopPhdAndWaitAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }
        if (prepared.Selection.Gate.Disposition != GateDisposition.Passed ||
            prepared.Selection.Star is null)
        {
            await StopPhdAndWaitAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        choice = prepared.Choice;
        lastG3Field = choice.Field;
        var guideSelectionRoi = BuildPhd2GuideSelectionRoi(
            prepared.Selected, preset.SensorWidthPixels, preset.SensorHeightPixels);
        await PublishPhd2GuideSelectionEvidenceAsync(
            context, choice.Field, prepared.Selection, prepared.Requested, prepared.Selected,
            preset, choice.Mode, choice.Capture, prepared.Loop, guideSelectionRoi,
            token).ConfigureAwait(false);

        await RequireImmediatePhysicalActionGatesAsync(context, token).ConfigureAwait(false);
        var preGuideBinding = await ValidateG3FieldMountBindingForMotionAsync(
            context, lastG3Field, token).ConfigureAwait(false);
        if (preGuideBinding.Disposition != GateDisposition.Passed)
        {
            await StopPhdAndWaitAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        Volatile.Write(ref phd2GuidingEverStarted, 1);
        var settle = await phd2.GuideAndSettleAsync(
            Phd2SettleCriteriaForSlitPlacement(preset),
            forceRecalibration: false,
            guideSelectionRoi,
            preserveSameEpochGuidingOnSettleTimeout: HasSupervisedScienceOptIn(),
            token).ConfigureAwait(false);
        var snapshot = phd2.Snapshot;
        var windSampledSettle = CanReplaceSettleWithFreshGuidingWindow(settle, snapshot);
        if ((!settle.Succeeded && !windSampledSettle) ||
            (settle.Succeeded && !snapshot.HasCurrentSuccessfulSettle))
        {
            await StopPhdAndWaitAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        var lockReadback = await phd2.GetLockPositionWithSameEpochRetryAsync(
            snapshot.ConnectionEpoch, snapshot.GuideEpoch, maximumAttempts: 3, token).ConfigureAwait(false);
        if (!lockReadback.SameGuideEpoch || lockReadback.Position is not { } freshLock)
        {
            await StopPhdAndWaitAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        var requiredResiduals = windSampledSettle
            ? Math.Max(3, preset.CalibrationQualityPolicy.RequiredFreshResidualsPerLockShiftStage)
            : preset.CalibrationQualityPolicy.RequiredFreshResidualsPerLockShiftStage;
        IReadOnlyList<Phd2GuidingResidualState> freshMeasurements;
        try
        {
            freshMeasurements = await CapturePhd2GuidingMeasurementsAsync(
                context,
                preset,
                previousSession.Topology,
                freshLock,
                ToPhd2Domain(lastG3Field.TargetIdentification.Target!.Centroid, preset),
                lastG3Field.SlitDetection.Geometry,
                choice.Mode,
                requiredResiduals,
                token,
                referenceGuide: prepared.Selected,
                referenceTargetMeasured: Phd2FreshTargetFluxPolicy.HasMeasuredHandoffPosition(
                    lastG3Field.TargetIdentification)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Phd2GuidingFrameLostException or
            Phd2CommandTimeoutException)
        {
            await StopPhdAndWaitAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }
        var initial = freshMeasurements[^1];
        var targetSlitResidual = PointDistance(
            initial.Measurement.TargetCentroid,
            initial.Measurement.RecognizedSlitAcquisitionPoint);
        var precisionTolerance = preset.BuildMotionLimits().TargetOnSlitTolerancePixels *
            previousSession.Quality.RequiredResidualToleranceScale;
        var maximumResidual = precisionTolerance + preset.MaximumResidualGrowthPixels;
        if (!double.IsFinite(targetSlitResidual) || targetSlitResidual > maximumResidual)
        {
            await StopPhdAndWaitAsync(CancellationToken.None).ConfigureAwait(false);
            await PublishRunJsonEvidenceAsync(
                "phd2-same-field-guide-residual-rejected",
                "Fresh guiding returned, but target/slit growth exceeded the unchanged same-field envelope",
                new { sourceCode, targetSlitResidual, precisionTolerance, maximumResidual,
                    previousResidual = PointDistance(previousSession.LastMeasurement.Measurement.TargetCentroid,
                        previousSession.LastMeasurement.Measurement.RecognizedSlitAcquisitionPoint),
                    motionIssuedByCoordinator = false, budgetReset = false },
                initial.Frame.Path, token).ConfigureAwait(false);
            return false;
        }

        lastG3Field = UpdateG3FieldFromGuidingResidual(lastG3Field, initial, preset);
        var rebasedLedger = previousLedger.RebaseAfterSameFieldGuideEpoch(
            snapshot.ConnectionEpoch,
            snapshot.GuideEpoch,
            freshLock,
            preset.LockVerificationTolerancePixels,
            DateTimeOffset.UtcNow,
            initial.Frame.Path,
            initial.Measurement.FrameSha256,
            "PHD2 reacquired the same visible field after a short star loss. Only guide coordinates/epoch were translated; coordinator-issued mount motion, attempts, cumulative pixels and lineage time remain unchanged.");
        await Phd2LockShiftPendingStore.WriteAtomicAsync(
            Phd2LockShiftPendingPath(context.Plan.ObservationRunId),
            rebasedLedger,
            token).ConfigureAwait(false);
        phd2SettledHandoffBudget = rebasedLedger;
        pendingPhd2LockShift = rebasedLedger;

        var warning = targetSlitResidual > precisionTolerance;
        phd2SlitPlacementSession = previousSession with
        {
            SelectedGuide = prepared.Selected,
            OriginLock = freshLock,
            InitialTarget = initial.Measurement.TargetCentroid,
            InitialRuntimeSlitLocal = initial.RuntimeSlitLocal,
            LastMeasurement = initial,
            Settle = settle,
            ConnectionEpoch = snapshot.ConnectionEpoch,
            GuideEpoch = snapshot.GuideEpoch,
            FreshGuidingWindowReplacedSettle = windSampledSettle,
            SlitPrecisionWarningActive = previousSession.SlitPrecisionWarningActive || warning,
            ReadOnlyPostLockObservation = null,
        };
        validatedG3GuideConnectionEpoch = snapshot.ConnectionEpoch;
        validatedG3GuideEpoch = snapshot.GuideEpoch;
        preExposureReceipt = new Phd2PreExposureReceipt(
            context.Plan.ObservationRunId,
            initial.Measurement.FrameSha256,
            initial.Frame.CompletedUtc,
            phd2.Snapshot);
        await PublishRunJsonEvidenceAsync(
            "phd2-same-field-guide-reacquired",
            "PHD2 guide recovered on the unchanged current target/slit field without coordinator-issued mount motion",
            new
            {
                sourceCode,
                previousConnectionEpoch = previousSession.ConnectionEpoch,
                previousGuideEpoch = previousSession.GuideEpoch,
                newConnectionEpoch = snapshot.ConnectionEpoch,
                newGuideEpoch = snapshot.GuideEpoch,
                previousLock = previousSession.OriginLock,
                newLock = freshLock,
                targetSlitResidual,
                guideTolerance = preset.LockVerificationTolerancePixels,
                precisionTolerance,
                warning,
                preservedAttempts = rebasedLedger.AttemptsUsed,
                preservedCumulativePixels = rebasedLedger.CumulativeCommandedPixels,
                preservedExcludedAtrSeconds = rebasedLedger.ExcludedAtrExposureSeconds,
                coordinatorMountMotionIssued = false,
                lockDisplacementIssued = false,
                calibrationIssued = false,
                budgetReset = false,
                fullG3WcsRebuildIssued = false,
            },
            initial.Frame.Path,
            token).ConfigureAwait(false);
        Report(warning
            ? $"warning：同场导星已恢复，但目标距缝 {targetSlitResidual:F2}px；按警告后试拍策略继续，不宣称精确入缝。"
            : $"同场导星已在原地恢复，目标距缝 {targetSlitResidual:F2}px；保留原曝光档和科学帧预算继续。");
        return true;
    }

    private static bool HasRecoveryTargetPrediction(G3FieldState field) =>
        field.TargetIdentification.Target is not null ||
        double.IsFinite(field.TargetIdentification.PredictedPoint.X) &&
        double.IsFinite(field.TargetIdentification.PredictedPoint.Y);

    internal static G3SameFieldRecoverySeed? TryRegisterSameFieldRecoverySeed(
        G3FieldState? seedField,
        IReadOnlyList<StarCandidate> currentStars,
        int width,
        int height,
        double targetSearchRadiusPixels,
        CancellationToken token)
    {
        if (seedField is null || seedField.Frame is null || seedField.BrightTargetAuthority is not null ||
            !HasRecoveryTargetPrediction(seedField) ||
            seedField.Frame.Width != width || seedField.Frame.Height != height ||
            seedField.Candidates.Count < 5 || currentStars.Count < 5 ||
            !double.IsFinite(targetSearchRadiusPixels) || targetSearchRadiusPixels <= 0)
            return null;

        var target = seedField.TargetIdentification.Target?.Centroid ??
            seedField.TargetIdentification.PredictedPoint;
        var registered = G3TargetTrackPolicy.Register(
            seedField.Candidates,
            currentStars,
            target,
            target,
            seedField.SlitDetection.Geometry,
            width,
            height,
            targetSearchRadiusPixels,
            token);
        if (!registered.Accepted || registered.Transform is not { } transform ||
            !double.IsFinite(transform.Scale) || transform.Scale is < 0.97 or > 1.03 ||
            Math.Abs(transform.RotationDegrees) > 5)
            return null;

        var recoveryTarget = transform.Apply(target);
        if (!double.IsFinite(recoveryTarget.X) || !double.IsFinite(recoveryTarget.Y) ||
            recoveryTarget.X < 0 || recoveryTarget.X >= width ||
            recoveryTarget.Y < 0 || recoveryTarget.Y >= height)
            return null;
        if (PixelDistance(recoveryTarget, target) > Math.Max(6, targetSearchRadiusPixels))
            return null;

        // Stars move across the detector. Its physical entrance slit does not.
        // Transforming both would hide the very drift the new frame must measure.
        return new G3SameFieldRecoverySeed(
            recoveryTarget,
            seedField.SlitDetection.Geometry,
            registered);
    }

    private static G3FieldState CreateSameFieldRecoverySeed(
        G3FieldState seedField,
        string framePath,
        IImageData image,
        MonochromeFrame frame,
        G3StellarFocusMeasurement focus,
        G3FieldMountBinding binding,
        G3SameFieldRecoverySeed registered) =>
        seedField with
        {
            Gate = GateResult.Pass(
                "PHD2_SAME_FIELD_RECOVERY_SEED_REGISTERED",
                "The fresh stopped frame is independently registered to the last trusted field; its target and slit coordinates are predictions until PHD2 supplies a new measured frame.",
                new Dictionary<string, double>
                {
                    ["matchedStars"] = registered.Registration.MatchedStars,
                    ["maximumHoldoutResidualPixels"] =
                        registered.Registration.MaximumHoldoutResidualPixels,
                    ["predictedTargetShiftPixels"] =
                        PixelDistance(seedField.TargetIdentification.PredictedPoint,
                            registered.Target),
                }),
            FramePath = framePath,
            Image = image,
            // Do not attach the old whole-frame WCS or sparse source chain to
            // the new stopped frame. Only the independently registered local
            // geometry survives, and all live gates are revalidated by the caller.
            Solve = null,
            BrightTargetAnalysis = null,
            BrightTargetAuthority = null,
            BrightTargetEvidencePath = null,
            GhostAssistance = null,
            Frame = frame,
            Candidates = focus.Stars,
            SlitDetection = seedField.SlitDetection with
            {
                Gate = GateResult.Warn(
                    "PHD2_SAME_FIELD_RECOVERY_SLIT_PREDICTED",
                    "The last physical slit geometry is retained in detector coordinates. A fresh target/slit measurement is still mandatory."),
                Geometry = registered.Slit,
            },
            TargetIdentification = seedField.TargetIdentification with
            {
                Gate = GateResult.Warn(
                    "PHD2_SAME_FIELD_RECOVERY_TARGET_PREDICTED",
                    "The retained target is a fresh registration prediction, not a new target centroid."),
                Target = null,
                CatalogPositionRefinedFromSameFrame = false,
                BoundShortPositionEvidencePath = null,
                CatalogPositionSpreadPixels = 0,
                UniquenessRatio = 0,
                PredictedPoint = registered.Target,
                PredictionResidualPixels = PixelDistance(
                    seedField.TargetIdentification.Target?.Centroid ??
                        seedField.TargetIdentification.PredictedPoint,
                    registered.Target),
            },
            MainFocusMeasurement = focus,
            MountBinding = binding,
            SparseInferenceConfirmed = false,
            SparseMeasuredContinuity = null,
            ExposureTiming = null,
            TargetTrackSource = null,
        };

    private StageResult AdaptiveRecoveryExhausted(string code, string? evidence) =>
        new(GateResult.TargetQuality(code,
            "本目标有界导星恢复或星点等待已用尽，已停止；整夜序列独立复核空闲与原运动账本后，按本目标失败策略跳过。未降低入缝门限，未单独关顶。",
            new Dictionary<string, double> { ["stopConfirmed"] = 1,
                ["reselectionAttempts"] = GuideRecovery.Reselections,
                ["calibrationReserved"] = GuideRecovery.CalibrationConsumed ? 1 : 0 }), evidence);
}

internal sealed record G3SameFieldRecoverySeed(
    PixelPoint Target,
    SlitGeometry Slit,
    G3OverlapRegistrationResult Registration);
