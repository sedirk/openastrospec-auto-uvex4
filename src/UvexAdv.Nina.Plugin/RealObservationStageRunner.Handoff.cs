using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private int phd2CoarseHandoffRecoveryAttempts;
    private bool centreInsideOneGuideStage;
    private Phd2LockShiftPendingState? phd2SettledHandoffBudget;

    private async Task<(Phd2PlacementGuideChoice Choice, string? ReacquisitionReason)>
        RefreshPhd2TakeoverFieldIfStaleAsync(ObservationContext context, Phd2PlacementGuideChoice choice,
            Phd2Point selectedGuide, Phd2SlitPlacementCommissioning preset, CancellationToken token, bool forceFresh = false)
    {
        // Catalogue-only objects use their independent registered-field
        // residual route; absence of a stellar centroid is not a new veto.
        if (UsesCatalogWcsTargetAuthority(context)) return (choice, null);
        var now = DateTimeOffset.UtcNow;
        // Saving/copying a long exposure does not make its photons younger.
        // Use the native request bound when available, never the file copy
        // completion as the sole proof that a measured stellar position is fresh.
        var measurementBound = choice.Capture.NativeRequestStartedUtc ?? choice.Capture.LoopingFrameObservedUtc;
        if (!forceFresh && !(choice.Field.SparseGuideAncestry is not null &&
            !Phd2FreshTargetFluxPolicy.HasMeasuredHandoffPosition(choice.Field.TargetIdentification)) &&
            !Phd2HandoffRecoveryPolicy.NeedsPreGuideRefresh(measurementBound.GetValueOrDefault(), now,
            preset.MaximumMeasurementAgeSeconds))
            return (choice, Phd2HandoffRecoveryPolicy.FreshTargetExceedsHandoff(choice.Field.TargetIdentification,
                choice.Field.SlitDetection.Geometry, Phd2HandoffResidualPixels(choice.Field, preset))
                ? "本轮选星准备的新实测目标已超出原剩余精调/回程容量；在 guide/settle 前交回原粗定位账本。" : null);
        await RequireImmediatePhysicalActionGatesAsync(context, token).ConfigureAwait(false);
        var before = phd2.Snapshot;
        var mountBefore = CaptureG3FrameMountReadback();
        // The loop is already owned by the native selection operation. This
        // method neither changes exposure nor starts/stops/selects a guide.
        PreGuidePreviewReadback Readback(Phd2StateSnapshot state, long? frameSequence = null) =>
            Phd2HandoffRecoveryPolicy.ClassifyPreviewReadback(before, state, selectedGuide, frameSequence,
                HasNoOutstandingGuideRecoveryMotion && pendingPhd2LockShift is null);
        Phd2SingleFrameResult capture;
        try
        {
            capture = await phd2.SaveNextLoopingFrameAsync(new(choice.ExposureMilliseconds,
                configuration.G3.Binning, configuration.G3.GainPercent,
                ReserveRunEvidencePath("g3-phd2-pre-guide-target-refresh", ".fit")), token).ConfigureAwait(false);
        }
        catch (Phd2CaptureException ex) when (ex.LoopReadbackConflict is { } atConflict &&
            Readback(atConflict) == PreGuidePreviewReadback.CandidateLost &&
            Readback(phd2.Snapshot) == PreGuidePreviewReadback.CandidateLost)
        {
            token.ThrowIfCancellationRequested();
            throw new TargetQualityEvidenceException("PHD2_PRE_GUIDE_SELECTION_UNAVAILABLE",
                "PHD2 reported native preview candidate loss before the fresh frame could be verified; no guide or lock movement was started.")
                { PreviewLossWitness = atConflict };
        }
        var mountAfter = CaptureG3FrameMountReadback();
        async Task VerifySelectionAsync(Phd2StateSnapshot after)
        {
            token.ThrowIfCancellationRequested();
            var readback = Readback(after, capture.LoopingFrameEventSequence);
            if (readback == PreGuidePreviewReadback.SameSelection) return;
            await PublishRunJsonEvidenceAsync("phd2-pre-guide-readback",
                "Native preview evidence before guide takeover; no motion or science",
                new { readback = readback.ToString(), selectedGuide, capture.LoopingFrameEventSequence,
                    capture.LoopingStarSample, beforeEpoch = before.GuideEpoch, afterEpoch = after.GuideEpoch,
                    after.LastPreviewStarMissSequence, transitions = after.RecentGuideEpochChanges.Where(c => c.Epoch > before.GuideEpoch),
                    guideCommandSent = false }, capture.Path, token).ConfigureAwait(false);
            if (readback == PreGuidePreviewReadback.CandidateLost)
                throw new TargetQualityEvidenceException("PHD2_PRE_GUIDE_SELECTION_UNAVAILABLE",
                    "PHD2 reported native preview candidate loss during the fresh pre-guide image; no guide or lock movement was started.")
                    { PreviewLossWitness = after };
            throw new InvalidOperationException("PHD2_PRE_GUIDE_REFRESH_OWNER_CHANGED: Native preview ownership/selection could not be verified while checking the target; no guide was started.");
        }
        await VerifySelectionAsync(phd2.Snapshot).ConfigureAwait(false);
        var refreshed = await AnalyzePhd2PlacementGuideFrameAsync(context, choice.Field, preset, choice.Mode,
            choice.ExposureMilliseconds, capture, null, mountBefore, mountAfter, before.ConnectionEpoch, token).ConfigureAwait(false);
        // StarLost/reselection can arrive while the immutable FITS is being analysed.
        var after = phd2.Snapshot;
        await VerifySelectionAsync(after).ConfigureAwait(false);
        var measured = Phd2FreshTargetFluxPolicy.HasMeasuredHandoffPosition(refreshed.Field.TargetIdentification);
        double? residual = measured ? PixelDistance(refreshed.Field.TargetIdentification.Target!.Centroid,
            refreshed.Field.SlitDetection.Geometry.AcquisitionPoint) : null;
        var handoffRadius = Phd2HandoffResidualPixels(refreshed.Field, preset);
        string? reason = refreshed.Selection.Gate.Disposition != GateDisposition.Passed || !measured
            ? $"选星等待后的新帧未确认唯一实测目标（{refreshed.Field.TargetIdentification.Gate.Code}）；不沿用旧目标像素启动导星。"
            : Phd2HandoffRecoveryPolicy.FreshTargetExceedsHandoff(refreshed.Field.TargetIdentification,
                refreshed.Field.SlitDetection.Geometry, handoffRadius)
            ? $"选星等待后的新帧目标距缝 {residual:F2}px，超过原剩余精调/回程容量 {handoffRadius:F2}px；在 guide/settle 前交回原粗定位账本。"
            : null;
        if (reason is null && choice.Mode == Phd2SlitGuideMode.OffSlitGuideStar)
        {
            var native = ToFrameLocal(selectedGuide, preset);
            var guidePolicy = new GuideStarSelectionPolicy(MinimumSignalToNoise: preset.MinimumGuideSignalToNoise);
            var target = refreshed.Field.TargetIdentification.Target!;
            var geometry = GuideStarSelector.ValidateNativeSelection(refreshed.Field.Candidates,
                refreshed.Field.SlitDetection.Geometry, target, native, preset.GuideSearchRadiusPixels, guidePolicy);
            var edgeAfterPlacement = Phd2NativeGuideSearchRegions.PlannedEdgeDistance(preset.RoiWidth,
                preset.RoiHeight, native, target.Centroid, refreshed.Field.SlitDetection.Geometry.AcquisitionPoint);
            if (geometry.Gate.Disposition != GateDisposition.Passed || edgeAfterPlacement < guidePolicy.MinimumEdgeDistancePixels ||
                Phd2NativeGuideSaturatedRegions.Measure(refreshed.Field.Frame!, guidePolicy).Any(item =>
                    Phd2NativeGuideSaturatedRegions.Contains(item.Region, native)))
                reason = "选星等待后的新帧改变了原生候选的目标/狭缝/边缘几何；保留原生候选，不由协调器替换或在旧像素上开始导星。";
        }
        else if (reason is null && PixelDistance(ToFrameLocal(selectedGuide, preset),
            refreshed.Field.TargetIdentification.Target!.Centroid) > preset.MaximumDirectTargetCentroidSeparationPixels)
            reason = "直导目标在选星等待后离开原生已选位置；不把旧锁点当成当前实测星位，先按原有边界重新取场。";
        await PublishRunJsonEvidenceAsync("phd2-pre-guide-target-refresh",
            "One current-loop target measurement before native guide takeover; no solve or setting change",
            new { previousFrame = choice.Field.FramePath, currentFrame = capture.Path,
                previousCompletedUtc = choice.Capture.CompletedUtc, capture.CompletedUtc,
                capture.LoopingFrameEventSequence, capture.LoopingFrameObservedUtc, capture.LoopingStarSample,
                nativeReadback = nameof(PreGuidePreviewReadback.SameSelection), beforeEpoch = before.GuideEpoch, afterEpoch = after.GuideEpoch,
                previousMeasurementBoundUtc = measurementBound,
                previousAgeSeconds = measurementBound is { } time ? (double?)(now - time).TotalSeconds : null,
                exposureMilliseconds = choice.ExposureMilliseconds, preset.MaximumMeasurementAgeSeconds,
                measured, residual, handoffRadius, selectedGuide, reason,
                captureTimingKnown = false, guideCommandSent = false, guidingRestarted = false,
                selectionChanged = false, g3PlateSolveRequested = false, motionIssued = false, budgetReset = false },
            capture.Path, token).ConfigureAwait(false);
        if (reason is not null) Report(reason);
        return (refreshed, reason);
    }

    private async Task<StageResult> WaitForPostCalibrationMountIdleAsync(
        Phd2DependencyRebuildStopProof stopProof, string expectedPierSide,
        CancellationToken cancellationToken, string phaseDescription = "PHD2 校准已通过并停止采集")
    {
        Report($"{phaseDescription}；等待赤道仪导星脉冲结束及连续静止读回，再重新获取目标（最多 15 秒）");
        var samples = new List<object>();
        var gate = await Phd2MountIdleWait.WaitAsync(async token =>
        {
            await phd2.GetAppStateAsync(token).ConfigureAwait(false);
            if (!stopProof.IsCurrent(phd2.Snapshot))
                return GateResult.Unknown("PHD2_REBUILD_STOP_UNCONFIRMED",
                    "The stopped PHD2 connection/guide epoch changed during the mount handoff; no capture or motion was issued.");
            var mountGate = ValidateG3SearchMountState(expectedPierSide);
            samples.Add(new { timestampUtc = DateTimeOffset.UtcNow, mountGate.Code,
                connectionEpoch = phd2.Snapshot.ConnectionEpoch, guideEpoch = phd2.Snapshot.GuideEpoch });
            return mountGate;
        }, cancellationToken).ConfigureAwait(false);
        var evidence = await PublishRunJsonEvidenceAsync("phd2-post-calibration-mount-idle",
            "Read-only mount pulse drain after confirmed PHD2 stop",
            new { gate, samples, phaseDescription, expectedPierSide, stopProof.ConnectionEpoch, stopProof.GuideEpoch,
                maximumWaitSeconds = Phd2MountIdleWait.MaximumWait.TotalSeconds,
                requiredQuietSeconds = Phd2MountIdleWait.QuietPeriod.TotalSeconds,
                originalBudgetsPreserved = true, motionOrCaptureIssued = false },
            stopProof.EvidencePath, cancellationToken).ConfigureAwait(false);
        return new StageResult(gate, evidence);
    }

    private double Phd2HandoffResidualPixels(G3FieldState field,
        Phd2SlitPlacementCommissioning preset)
    {
        return RemainingPhd2HandoffRadius(preset,
            field.Gate.Disposition == GateDisposition.Passed &&
            Phd2FreshTargetFluxPolicy.HasMeasuredHandoffPosition(field.TargetIdentification));
    }

    private double RemainingPhd2HandoffRadius(Phd2SlitPlacementCommissioning preset,
        bool measuredTargetConfirmed)
    {
        var initial = preset.HandoffResidualPixels(measuredTargetConfirmed);
        if (centreInsideOneGuideStage) initial = Math.Min(initial, Math.Min(preset.MaximumStagePixels, preset.MaximumAcquisitionResidualPixels));
        if (phd2SettledHandoffBudget is not { } spent)
            return NativeTarget?.ClockBudget?.Phd2 is not { } debit ? initial :
                Phd2CoarseHandoffPolicy.LimitToRemainingLedger(initial, preset.BuildMotionLimits(),
                    preset.CalibrationQualityPolicy.DegradedMaximumLockShiftScale, preset.MaximumResidualGrowthPixels,
                    debit.Attempts, debit.Distance, DateTimeOffset.UtcNow - debit.StartedUtc);
        return
            Phd2CoarseHandoffPolicy.LimitToRemainingLedger(initial, preset.BuildMotionLimits(),
                preset.CalibrationQualityPolicy.DegradedMaximumLockShiftScale, preset.MaximumResidualGrowthPixels,
                spent.AttemptsUsed, spent.CumulativeCommandedPixels, DateTimeOffset.UtcNow - spent.MotionClockStartedUtc);
    }

    private bool NeedsG3CoarseCentering(G3FieldState field) =>
        commissioning?.Value.Phd2SlitPlacement is { } preset &&
        field.TargetIdentification.Target is { } target &&
        field.SlitDetection.Gate.Disposition == GateDisposition.Passed &&
        G3WcsRecoveryPolicy.NeedsCoarseCentering(field.Gate,
            field.Solve?.Result.Success == true && field.Solve.Result.Coordinates is not null,
            PixelDistance(target.Centroid, field.SlitDetection.Geometry.AcquisitionPoint) +
            field.TargetIdentification.CatalogPositionSpreadPixels, Phd2HandoffResidualPixels(field, preset));

    private async Task<StageResult> HandleDeniedPhd2AcquisitionBudgetAsync(
        ObservationContext context, Phd2SlitPlacementSession session,
        Phd2SlitPlacementCommissioning preset, Phd2LockShiftAcquisitionBudget budget,
        int postCalibrationReacquisitionDepth, int lostLockReacquisitionDepth,
        CancellationToken cancellationToken)
    {
        // This gate runs only after existing read-only completion and explicit
        // quality-warning paths. A denial cannot dispatch another exact lock.
        // A non-settled lineage still owns its original return before any
        // coarse handoff, while an accepted historical endpoint is not debt.
        if (pendingPhd2LockShift is { Phase: not Phd2LockShiftPendingPhase.SettledBudgetLedger } outstanding)
            return await ReturnPhd2LockToOriginAsync(context, session,
                outstanding with { Phase = Phd2LockShiftPendingPhase.ReturnRequired },
                $"{budget.Code}: {budget.Message}", cancellationToken).ConfigureAwait(false);
        var residual = PointDistance(session.LastMeasurement.Measurement.TargetCentroid,
            session.LastMeasurement.Measurement.RecognizedSlitAcquisitionPoint);
        if (Phd2HandoffRecoveryPolicy.ShouldReacquire(budget, residual,
            preset.BuildMotionLimits().TargetOnSlitTolerancePixels * session.Quality.RequiredResidualToleranceScale +
            preset.MaximumResidualGrowthPixels))
            return await ReacquireG3ForPhd2HandoffAsync(context, budget.Code, budget.Message,
                postCalibrationReacquisitionDepth, lostLockReacquisitionDepth, cancellationToken).ConfigureAwait(false);
        var stopped = await EnsurePhdStoppedForAutomaticRebuildAsync(
            ObservationStage.PlaceTargetOnSlit, budget.Code, cancellationToken).ConfigureAwait(false);
        if (stopped.Disposition != GateDisposition.Passed) return new StageResult(stopped, session.LastMeasurement.Frame.Path);
        return new StageResult(Phd2HandoffRecoveryPolicy.FailureGate(budget.Code,
            $"{budget.Message} No new exact-lock command was issued; the existing ledger and limits were retained."),
            session.LastMeasurement.Frame.Path);
    }

    private async Task<StageResult> ReacquireG3ForPhd2HandoffAsync(
        ObservationContext context, string sourceCode, string reason,
        int postCalibrationReacquisitionDepth, int lostLockReacquisitionDepth,
        CancellationToken cancellationToken)
    {
        // This is a pre-motion handoff repair, not a relabelled LostLock and
        // never a budget replenishment. An outstanding exact-lock command must
        // follow its own verified return path instead.
        if (pendingPhd2LockShift is { Phase: not Phd2LockShiftPendingPhase.SettledBudgetLedger })
            return new StageResult(GateResult.Unknown("PHD2_COARSE_HANDOFF_RETURN_REQUIRED",
                $"{sourceCode}: {reason} 尚有未结精调动作；必须先按原账本核验回程。"), lastG3Field?.FramePath);
        if (phd2CoarseHandoffRecoveryAttempts >= 1)
        {
            var stop = await EnsurePhdStoppedForAutomaticRebuildAsync(
                ObservationStage.PlaceTargetOnSlit, sourceCode, cancellationToken).ConfigureAwait(false);
            if (stop.Disposition != GateDisposition.Passed) return new StageResult(stop);
            return new StageResult(GateResult.TargetQuality("PHD2_COARSE_HANDOFF_RECOVERY_EXHAUSTED",
                $"{sourceCode}: {reason} 本轮已完成一次有界 WCS／精调交接重建，仍未满足条件；保留原预算与证据，未发出新的精调。"), lastG3Field?.FramePath);
        }
        phd2CoarseHandoffRecoveryAttempts++;
        var evidence = await PublishRunJsonEvidenceAsync("phd2-coarse-handoff-rebuild",
            "Pre-motion guide/WCS handoff reconciliation within the existing run",
            new { sourceCode, reason, attempts = phd2CoarseHandoffRecoveryAttempts, maximumAttempts = 1,
                exactLockCommandIssued = false, durableMotionBudgetsReset = false,
                freshFormalWcsRequiredForCoarseMotion = true }, lastG3Field?.FramePath, cancellationToken).ConfigureAwait(false);
        var stopped = await EnsurePhdStoppedForAutomaticRebuildAsync(
            ObservationStage.PlaceTargetOnSlit, sourceCode, cancellationToken).ConfigureAwait(false);
        if (stopped.Disposition != GateDisposition.Passed) return new StageResult(stopped, evidence);
        if (automaticRebuildStopProof is { } stopProof)
        {
            var quiet = await WaitForPostCalibrationMountIdleAsync(stopProof,
                telescopeMediator.GetInfo().SideOfPier.ToString(), cancellationToken, "PHD2 粗精交接已确认停止").ConfigureAwait(false);
            if (!quiet.CanAdvance) return quiet;
        }
        phd2SlitPlacementSession = null;
        lastG3Field = null;
        Report($"WCS／导星交接需重建：{reason} 先重拍并按原账本完成粗定位，不让精调走到半途耗尽回程预算。");
        StageResult acquired;
        try
        {
            centreInsideOneGuideStage = Phd2HandoffRecoveryPolicy.CentreInsideOneStageOnRebuild(sourceCode);
            acquired = await AcquireG3SlitFieldAsync(context, cancellationToken,
                allowChargedCurrentPositionHandoff: true).ConfigureAwait(false);
        }
        finally { centreInsideOneGuideStage = false; }
        if (!acquired.CanAdvance) return acquired;
        return await PlaceTargetOnSlitWithPhd2Async(context, cancellationToken,
            postCalibrationReacquisitionDepth, lostLockReacquisitionDepth).ConfigureAwait(false);
    }
}
