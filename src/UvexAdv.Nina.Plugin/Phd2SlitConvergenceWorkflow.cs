using System.Globalization;
using UvexAdv.Observatory;
using UvexAdv.Phd2;
using static UvexAdv.Nina.Plugin.ObservationWorkflowResults;

namespace UvexAdv.Nina.Plugin;

// Owns the convergence loop: complete-window decisions, same-lock resampling,
// durable precharge, bounded exact-lock stages and measured completion.
// Initial guiding/recalibration and cross-stage recovery are NOT retried here.
internal sealed class Phd2SlitConvergenceWorkflow(ScienceQualityPolicy qualityPolicy,
    IPhd2SlitPlacementPort port, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    internal async Task<StageResult> ConvergeAsync(ObservationContext context,
        Phd2SlitConvergenceRequest request, CancellationToken cancellationToken)
    {
        var session = request.Session;
        var ledger = request.Ledger;
        var preset = request.Preset;
        var topology = session.Topology;
        var calibration = session.Calibration;
        var identity = request.Identity;
        var pierSide = topology.PierSide;
        var priorResidual = PointDistance(session.LastMeasurement.Measurement.TargetCentroid, session.LastMeasurement.Measurement.RecognizedSlitAcquisitionPoint);
        IReadOnlyList<Phd2GuidingResidualState> targetCompletionWindow = request.InitialWindow;
        var completionWindowRetries = 0;
        var transientResidualGrowthWarning = false;
        var preferredProbeCorrectionUsed = false;
        var preferredProbeWindowUsed = false;
        while (true)
        {
            port.ThrowIfGuideOutputFailed();
            var safety = port.BuildPhd2LockShiftSafetySnapshot(context, preset, topology.PierSide);
            var plan = Phd2SlitLockShiftPlanner.PlanOutboundStage(
                session.Qualification,
                session.GuideMode,
                session.LastMeasurement.Measurement,
                ledger,
                safety,
                topology,
                preset.BuildMotionLimits(),
                clock.GetUtcNow());
            var completionTolerance = preset.BuildMotionLimits().TargetOnSlitTolerancePixels *
                session.Quality.RequiredResidualToleranceScale;
            var requiredCompletionFrames = Math.Max(3, session.Quality.RequiredFreshResidualsPerLockShiftStage);
            var budgetLimitedApproach = false;
            if (qualityPolicy.WarningProbeEnabled && ledger.AttemptsUsed > 0 &&
                plan.Code == "SLIT_LOCK_RETURN_CUMULATIVE_RESERVE")
            {
                var candidate = Phd2SlitLockShiftPlanner.PlanOutboundStage(session.Qualification,
                    session.GuideMode, session.LastMeasurement.Measurement, ledger, safety, topology,
                    preset.BuildMotionLimits(), clock.GetUtcNow(), allowBudgetLimitedStage: true);
                if (candidate.Stage is { } limited && IsUsefulBudgetLimitedApproach(
                    targetCompletionWindow, limited, preset, completionTolerance, requiredCompletionFrames))
                {
                    plan = candidate;
                    budgetLimitedApproach = true;
                }
            }
            var completionResiduals = targetCompletionWindow.Select(item => PointDistance(
                item.Measurement.TargetCentroid, item.Measurement.RecognizedSlitAcquisitionPoint)).ToArray();
            // A same-frame guide error can already explain the target error:
            // the existing lock is the desired destination. Wait for native
            // guiding instead of sending another shift or immediately tearing
            // down the guide session. This does not make the denied plan valid.
            var nativeCorrectionPending = !plan.IsAllowed &&
                string.Equals(plan.Code, "FRESH_G3_RESIDUAL_REQUIRED", StringComparison.Ordinal);
            var completionWindowUnstable = plan.IsAllowed && plan.IsComplete &&
                (targetCompletionWindow.Count < requiredCompletionFrames ||
                 !Phd2PlacementGuideWindowPolicy.AllWithinTolerance(completionResiduals, completionTolerance));
            var measuredSupervisedGeometry = qualityPolicy.AllowsMeasuredContinuation &&
                targetCompletionWindow.All(item => item.Measurement.GuidePositionMeasuredInFrame &&
                    item.Measurement.TargetIdentityConfirmed &&
                    item.Measurement.TargetPositionAuthority != Phd2TargetPositionAuthority.CatalogWcsProjection);
            var canWaitWithoutNewMotion = Phd2PlacementGuideWindowPolicy.CanWaitWithoutNewMotion(plan.IsAllowed, plan.Code);
            var correctionWindow = Phd2PlacementGuideWindowPolicy.EvaluateCorrectionWindow(
                targetCompletionWindow.Select(item => item.Measurement).ToArray(), ledger.CurrentLockPosition,
                session.GuideMode, measuredSupervisedGeometry, plan.IsAllowed, plan.IsComplete,
                preset.EffectiveGuideLockResidualPixels, completionTolerance, requiredCompletionFrames);
            // A supervised optical window may retain a tracking warning,
            // but that is not permission to chase an unfinished lock shift.
            // Wait under the SAME deadline/return reserve before budgeting
            // another motion (including when a transient frame vetoed it).
            // No local unfinished shift exists before initial placement (or
            // after a settled handoff). Initial guide error is advisory, not
            // a reason to burn the original budget waiting for a non-existent
            // motion receipt. Real outstanding shifts still require this wait.
            var existingLockNotReached = port.PendingMotion is { Phase: not Phd2LockShiftPendingPhase.SettledBudgetLedger } &&
                !correctionWindow.CanContinue && Phd2PlacementGuideWindowPolicy.MustWaitForExistingLock(
                targetCompletionWindow.Select(item => PointDistance(
                    item.Measurement.GuideStar, ledger.CurrentLockPosition)).ToArray(),
                preset.EffectiveGuideLockResidualPixels, measuredSupervisedGeometry,
                plan.IsAllowed, plan.Code);
            // Near the slit, do not spend another exact-lock action chasing
            // each wind/seeing sample. This envelope authorizes only waiting,
            // never science or additional movement. Acceptance stays exact.
            var nearSlitTrackingWarning = measuredSupervisedGeometry &&
                canWaitWithoutNewMotion && !plan.IsComplete &&
                Phd2PlacementGuideWindowPolicy.AllWithinTolerance(completionResiduals,
                    completionTolerance + preset.MaximumResidualGrowthPixels);
            var slitApertureResiduals = targetCompletionWindow.Select(item =>
                Phd2PlacementGuideWindowPolicy.ProjectMeasuredTarget(
                    item, ToFrameLocal(item.Measurement.TargetCentroid, preset))).ToArray();
            var warningProbe = qualityPolicy.EvaluateSlitWarning(
                measuredSupervisedGeometry, canWaitWithoutNewMotion, completionResiduals,
                slitApertureResiduals, completionTolerance, preset.MaximumResidualGrowthPixels,
                preset.MaximumAcquisitionResidualPixels);
            var alongSlitPrecisionWarning = warningProbe.AlongSlitWarning;
            var probeCorrectionBudget = warningProbe.CanProbe && !preferredProbeCorrectionUsed
                ? Phd2SlitLockShiftPlanner.EvaluateAcquisitionBudget(session.Qualification, session.GuideMode,
                    session.LastMeasurement.Measurement, ledger, safety, topology,
                    preset.BuildMotionLimits(), clock.GetUtcNow()) : null;
            var probeCorrectionAffordable = warningProbe.CanProbe && !preferredProbeCorrectionUsed &&
                plan.IsAllowed && !plan.IsComplete && plan.Stage is not null &&
                ScienceQualityPolicy.HasConsistentCrossSlitBias(slitApertureResiduals, completionTolerance) &&
                probeCorrectionBudget?.IsAllowed == true;
            var preferProbeCorrection = probeCorrectionAffordable && correctionWindow.CanContinue && !existingLockNotReached;
            // The last primary-star sample can be just outside its lock tolerance.
            // Give this optional improvement ONE fresh window, only if the entire
            // correction/return still fits after that window. If it does not help,
            // retain the warning probe rather than inventing a new quality stop.
            var probeBiasAwaitingWindow = probeCorrectionAffordable && !preferProbeCorrection && !preferredProbeWindowUsed &&
                correctionWindow.Code == "CORRECTION_WINDOW_LOCK_NOT_REACHED" &&
                correctionWindow.MaximumEndpointSpreadPixels <= completionTolerance &&
                probeCorrectionBudget!.TotalElapsedSeconds + preset.MaximumStageSeconds <= preset.MaximumElapsedSeconds;
            var supervisedSlitPrecisionWarning = warningProbe.CanProbe && !preferProbeCorrection && !probeBiasAwaitingWindow;
            // A rejected additional movement is not a rejected observation at
            // the current verified lock. Use the remaining read-only window,
            // even after the guide has caught up, before invoking the return.
            // The existing completion deadline still reserves the full return.
            var budgetLimitedReadOnlyWait = measuredSupervisedGeometry && ledger.AttemptsUsed > 0 &&
                !plan.IsAllowed && canWaitWithoutNewMotion;
            if (!supervisedSlitPrecisionWarning && (nativeCorrectionPending || completionWindowUnstable ||
                probeBiasAwaitingWindow || (nearSlitTrackingWarning && !preferProbeCorrection) || existingLockNotReached || budgetLimitedReadOnlyWait ||
                (transientResidualGrowthWarning && canWaitWithoutNewMotion)))
            {
                var completionLimits = preset.BuildMotionLimits();
                var recoveryDistanceUpper = PointDistance(ledger.OriginLockPosition, ledger.CurrentLockPosition) +
                    completionLimits.LockVerificationTolerancePixels;
                var reservedReturnAttempts = recoveryDistanceUpper <= completionLimits.LockVerificationTolerancePixels
                    ? 0
                    : (int)Math.Ceiling(recoveryDistanceUpper /
                        (completionLimits.MaximumStagePixels - completionLimits.LockVerificationTolerancePixels));
                var completionDeadline = ledger.StartedUtc + completionLimits.MaximumElapsed -
                    TimeSpan.FromTicks(completionLimits.MaximumStageDuration.Ticks * reservedReturnAttempts);
                if (clock.GetUtcNow() + completionLimits.MaximumStageDuration > completionDeadline)
                {
                    const string reason = "PHD2_COMPLETION_WINDOW_RETURN_RESERVE: A further read-only completion window would consume the original worst-case return time; no new exposure or lock mutation was dispatched.";
                    var completionFailureMetrics = new Dictionary<string, double>
                    {
                        ["slitTolerancePixels"] = completionTolerance,
                        ["minimumResidualPixels"] = completionResiduals.Min(),
                        ["maximumResidualPixels"] = completionResiduals.Max(),
                        ["slitQualityWarningEnabled"] = qualityPolicy.WarningProbeEnabled ? 1 : 0,
                        ["elapsedSeconds"] = Math.Max(0, (clock.GetUtcNow() - ledger.StartedUtc).TotalSeconds),
                        ["maximumElapsedSeconds"] = completionLimits.MaximumElapsed.TotalSeconds,
                        ["completionWindowRetries"] = completionWindowRetries,
                        ["waitingForExistingLock"] = existingLockNotReached ? 1 : 0,
                        ["latestGuideLockResidualPixels"] = PointDistance(session.LastMeasurement.Measurement.GuideStar, ledger.CurrentLockPosition),
                        ["guideLockTolerancePixels"] = preset.EffectiveGuideLockResidualPixels,
                    };
                    if (port.PendingMotion is { } returnReserved)
                        return await port.ReturnPhd2LockToOriginAsync(context, session, returnReserved, reason,
                            cancellationToken, completionFailureMetrics).ConfigureAwait(false);
                    return new StageResult(GateResult.TargetQuality("PHD2_SLIT_COMPLETION_WINDOW_EXHAUSTED",
                        reason, completionFailureMetrics));
                }
                // Keep sampling while the original deadline still reserves
                // the full return. Four windows are not a physical safety
                // boundary and must not force an early wind-induced return.
                completionWindowRetries++;
                preferredProbeWindowUsed |= probeBiasAwaitingWindow;
                port.Report(probeBiasAwaitingWindow
                    ? "新帧显示目标中心持续偏在缝的一侧：在原预算内多检查一次当前导星锁点；若可继续则精调一次，否则保留警告试拍。"
                    : budgetLimitedReadOnlyWait && !existingLockNotReached
                    ? "追加精调预算不足：保持当前锁点等待新帧，原试拍质量策略及回程预留不变；不立即放弃目标。"
                    : existingLockNotReached
                    ? "PHD2 尚未跟上当前锁点：保持原生导星等待新帧，不追加精调、不重置预算；原回程时间继续保留。"
                    : transientResidualGrowthWarning || nearSlitTrackingWarning
                    ? "导星扰动警告：保持当前锁点和原生导星，先补取整组新帧；不因短时残差增长立即回程，也不追加追逐扰动的移锁。"
                    : nativeCorrectionPending
                    ? "目标偏差与原生导星偏差相符，现有锁点无需重复移动；保持 PHD2 导星并有界补取新帧，等待实际纠偏。"
                    : "最后一帧已接近狭缝，但整组尚未稳定；保持导星补取新帧，必要时在原账本内继续精调，不提前宣布入缝完成。");
                var primaryRecovered = false;
                if (ledger.AttemptsUsed > 0 && session.PrimaryStarSettleRecoveryStage < ledger.AttemptsUsed &&
                    port.PendingMotion is { Phase: Phd2LockShiftPendingPhase.AwaitingFreshResidual } primaryPending &&
                    primaryPending.AttemptsUsed == ledger.AttemptsUsed &&
                    primaryPending.PrimaryStarSettleRecoveryStage < ledger.AttemptsUsed &&
                    NeedsPrimaryStarRecovery(targetCompletionWindow, ledger.CurrentLockPosition, session.GuideMode) &&
                    port.TryConsumePrimaryStarSettle(ledger.LineageId, ledger.AttemptsUsed))
                {
                    session = await RecoverPrimaryStarAsync(context, request, session, primaryPending,
                        ledger.CurrentLockPosition, completionDeadline, cancellationToken).ConfigureAwait(false);
                    primaryRecovered = true;
                }
                var rollingCorrectionWindow = !primaryRecovered && Phd2PlacementGuideWindowPolicy.CanRollCorrectionWindow(
                    measuredSupervisedGeometry, session.GuideMode, canWaitWithoutNewMotion, plan.IsComplete,
                    true,
                    targetCompletionWindow.Count, requiredCompletionFrames);
                var previousCompletionWindow = targetCompletionWindow;
                if (rollingCorrectionWindow)
                {
                    RequireSameCorrectionWindowEpoch(session, ledger.CurrentLockPosition, preset);
                    port.Report("PHD2 等待现锁点：每到一张新帧即检查最近完整窗口，不再每三帧才检查一次；原门限、运动账本和回程时间不变。");
                }
                var newCompletionMeasurements = await port.CapturePhd2PlacementGuideWindowAsync(
                    context, preset, topology, ledger.CurrentLockPosition,
                    session.LastMeasurement.Measurement.TargetCentroid,
                    session.LastMeasurement.RuntimeSlitLocal, session.GuideMode,
                    requiredCompletionFrames, completionDeadline,
                    cancellationToken, rollingCorrectionWindow ? previousCompletionWindow : null,
                    session.LastMeasurement.Measurement.GuideStar).ConfigureAwait(false);
                if (rollingCorrectionWindow)
                {
                    RequireSameCorrectionWindowEpoch(session, ledger.CurrentLockPosition, preset);
                }
                targetCompletionWindow = newCompletionMeasurements;
                if (primaryRecovered)
                    session = session with { ReadOnlyPostLockObservation = session.ReadOnlyPostLockObservation!.AcceptResiduals(
                        targetCompletionWindow.Select(item => item.Frame).ToArray(), port.GuideSnapshot) };
                var completionMeasurement = targetCompletionWindow[^1];
                session = session with { LastMeasurement = completionMeasurement };
                port.RecordSession(session);
                port.PromoteMeasurement(completionMeasurement, preset);
                priorResidual = PointDistance(completionMeasurement.Measurement.TargetCentroid,
                    completionMeasurement.Measurement.RecognizedSlitAcquisitionPoint);
                await port.PublishRunJsonEvidenceAsync("phd2-target-completion-window",
                    "Whole-window slit completion rechecked before committing the settled ledger",
                    new
                    {
                        completionWindowRetries, boundedByOriginalDeadline = true, nativeCorrectionPending,
                        nearSlitTrackingWarning, transientResidualGrowthWarning, existingLockNotReached, budgetLimitedReadOnlyWait,
                        probeBiasAwaitingWindow,
                        previousWindowDecision = correctionWindow,
                        previousWindowFrames = previousCompletionWindow.Select(item => item.Measurement.FrameSha256).ToArray(),
                        rollingCorrectionWindow,
                        newlyAcceptedFrames = rollingCorrectionWindow
                            ? newCompletionMeasurements.Count(item => !previousCompletionWindow.Any(prior =>
                                prior.Measurement.FrameSha256 == item.Measurement.FrameSha256))
                            : newCompletionMeasurements.Count,
                        currentWindowFrames = targetCompletionWindow.Select(item => item.Measurement.FrameSha256).ToArray(),
                        completionDeadline, reservedReturnAttempts,
                        plannerCode = plan.Code,
                        residuals = targetCompletionWindow.Select(item => PointDistance(
                            item.Measurement.TargetCentroid, item.Measurement.RecognizedSlitAcquisitionPoint)).ToArray(),
                        tolerance = completionTolerance, budgetReset = false,
                        newLockMotion = false, nextAction = "replan-from-last-real-fresh-frame",
                    }, completionMeasurement.Frame.Path, cancellationToken).ConfigureAwait(false);
                transientResidualGrowthWarning = false;
                continue;
            }
            if (!plan.IsAllowed && !supervisedSlitPrecisionWarning)
            {
                if (!request.AllowCoarseReacquisition && port.PendingMotion is null)
                    return new StageResult(Phd2HandoffRecoveryPolicy.FailureGate(plan.Code,
                        $"Same-lineage pre-exposure placement cannot continue: {plan.Code}: {plan.Message}"));
                return await port.HandleDeniedPhd2AcquisitionBudgetAsync(context, session, preset,
                    new Phd2LockShiftAcquisitionBudget(false, plan.Code, plan.Message, 0, 0, 0, 0,
                        ledger.CumulativeCommandedPixels, ledger.AttemptsUsed,
                        (clock.GetUtcNow() - ledger.StartedUtc).TotalSeconds),
                    request.PostCalibrationReacquisitionDepth, request.LostLockReacquisitionDepth, cancellationToken).ConfigureAwait(false);
            }
            if (plan.IsComplete || supervisedSlitPrecisionWarning)
            {
                session = session with { SlitPrecisionWarningActive = supervisedSlitPrecisionWarning };
                var settled = port.CreatePhd2PendingState(
                    context,
                    preset,
                    session,
                    ledger,
                    ledger.CurrentLockPosition,
                    Phd2LockShiftPendingPhase.SettledBudgetLedger,
                    intentEvidencePath: null,
                    supervisedSlitPrecisionWarning
                        ? "Known exact-lock endpoint and fresh optical identity retained for operator-authorized ATR probing with slit-precision warning; NOT exact slit completion or origin-return proof."
                        : "Target-on-slit completion was proven by fresh guiding-frame residual evidence.") with
                {
                    // Persistent settled state records the fresh frame that
                    // proved completion. The process-local motion ledger
                    // continues to record only frames consumed by motion.
                    LastAcceptedFrameSha256 = session.LastMeasurement.Measurement.FrameSha256,
                    LastFramePath = session.LastMeasurement.Frame.Path,
                };
                await port.PersistPendingAsync(request.PendingPath, settled, cancellationToken).ConfigureAwait(false);
                port.RecordPendingMotion(null);
                port.RecordSession(session);
                port.PromoteMeasurement(session.LastMeasurement, preset);
                if (supervisedSlitPrecisionWarning)
                {
                    var metrics = port.Phd2EffectiveQualityMetrics(session.Quality, session.GuideMode,
                        session.SelectedGuide, session.Settle, priorResidual);
                    metrics["slitPrecisionWarning"] = 1;
                    metrics["preciseSlitPlacementProven"] = 0;
                    metrics["measuredCoreOverlapProbe"] = slitApertureResiduals.Any(x => x?.TargetCoreRadiusPixels > 0) ? 1 : 0;
                    metrics["slitResidualWindowMaximumPixels"] = completionResiduals.Max();
                    metrics["phd2IsUnattendedScienceAuthority"] = 0;
                    await port.PublishRunJsonEvidenceAsync("phd2-supervised-slit-warning-probe-authorized",
                        "Operator-authorized ATR probing without claiming exact slit placement",
                        new { completionResiduals, tolerance = completionTolerance, operatorConsent = true,
                            alongSlitPrecisionWarning, slitApertureResiduals,
                            slitGeometrySource = "validated per-frame runtime physical slit",
                            exactSlitPlacementProven = false, originReturnProven = false,
                            ledger.AttemptsUsed, ledger.CumulativeCommandedPixels, ledger.StartedUtc,
                            lockPosition = ledger.CurrentLockPosition, budgetReset = false,
                            nextAuthority = "actual ATR spectral-trace clipping, contrast and SNR" },
                        session.LastMeasurement.Frame.Path, cancellationToken).ConfigureAwait(false);
                    return Warning("PHD2_SLIT_PRECISION_WARNING_PROBE_AUTHORIZED",
                        $"目标已在狭缝附近，但连续残差（{string.Join(", ", completionResiduals.Select(value => value.ToString("F2", CultureInfo.InvariantCulture)))} px）未全部满足 {completionTolerance:F2} px；按本次授权警告后 ATR 试拍，由实际光谱判断，不宣称精确入缝。",
                        metrics, request.CommissioningMetadata);
                }
                if (port.GuidePrecisionWarningActive)
                {
                    var metrics = port.Phd2EffectiveQualityMetrics(session.Quality, session.GuideMode,
                        session.SelectedGuide, session.Settle, priorResidual);
                    metrics["guidePrecisionWarning"] = 1;
                    metrics["guideResidualWarningPixels"] = Phd2PlacementGuideWindowPolicy.GuideResidualWarningPixels;
                    metrics["guideResidualWarningOnly"] = 1;
                    metrics["slitTolerancePixels"] = completionTolerance;
                    return Warning("PHD2_TARGET_AT_SLIT_MIDPOINT_GUIDE_PRECISION_WARNING",
                        $"目标已按原 {completionTolerance:F2}px 门限实测入缝；导星偏差曾超过 3px，仅记录精度警告，未因该数值阻断或重新校准。", metrics, request.CommissioningMetadata);
                }
                if (session.ReadOnlyPostLockObservation?.MeasuredContinuation == true && !session.Quality.RequiresOperatorSupervision)
                    return Warning("PHD2_TARGET_AT_SLIT_MIDPOINT_MEASURED_WINDOW",
                        $"目标已按原 {completionTolerance:F2}px 门限实测入缝；使用同一导星会话的三张新帧，未额外重启原生稳定等待，未伪造 SettleDone。",
                        port.Phd2EffectiveQualityMetrics(session.Quality, session.GuideMode, session.SelectedGuide, session.Settle, priorResidual), request.CommissioningMetadata);
                return session.FreshGuidingWindowReplacedSettle || session.Quality.RequiresOperatorSupervision
                    ? Warning(
                        "PHD2_TARGET_AT_SLIT_MIDPOINT_WIND_SAMPLED",
                        $"PHD2 placed the target at the runtime-recognized slit midpoint with residual {priorResidual:F2}px. Guiding/settle advisories remain; fresh same-epoch GuideStep/FITS measurements were accepted under explicit supervision, not as unattended authority.",
                        port.Phd2EffectiveQualityMetrics(session.Quality, session.GuideMode, session.SelectedGuide, session.Settle, priorResidual),
                        request.CommissioningMetadata)
                    : Passed(
                        "PHD2_TARGET_AT_SLIT_MIDPOINT",
                        $"PHD2 graded calibration placed the target at the runtime-recognized slit midpoint with residual {priorResidual:F2}px; guiding remains settled for StartGuiding.",
                        port.Phd2EffectiveQualityMetrics(session.Quality, session.GuideMode, session.SelectedGuide, session.Settle, priorResidual),
                        request.CommissioningMetadata);
            }

            // A complete read-only/explicit-warning endpoint was handled
            // above. Recheck the whole exact-midpoint trip. A deliberately
            // shorter, fully return-reserved warning-probe approach is useful
            // only when the complete measured window predicts the EXISTING
            // probe envelope. Prediction authorizes no exposure or completion.
            var nextAcquisitionBudget = Phd2SlitLockShiftPlanner.EvaluateAcquisitionBudget(
                session.Qualification, session.GuideMode, session.LastMeasurement.Measurement, ledger,
                port.BuildPhd2LockShiftSafetySnapshot(context, preset, topology.PierSide), topology,
                preset.BuildMotionLimits(), clock.GetUtcNow());
            if (!nextAcquisitionBudget.IsAllowed)
            {
                if (!budgetLimitedApproach || nextAcquisitionBudget.Code is not
                    ("SLIT_LOCK_RETURN_CUMULATIVE_RESERVE" or "SLIT_LOCK_ACQUISITION_CUMULATIVE_RESERVE"))
                {
                    if (!request.AllowCoarseReacquisition && port.PendingMotion is null)
                        return new StageResult(Phd2HandoffRecoveryPolicy.FailureGate(nextAcquisitionBudget.Code,
                            $"Same-lineage pre-exposure placement cannot continue: {nextAcquisitionBudget.Code}: {nextAcquisitionBudget.Message}"));
                    return await port.HandleDeniedPhd2AcquisitionBudgetAsync(context, session, preset,
                        nextAcquisitionBudget, request.PostCalibrationReacquisitionDepth, request.LostLockReacquisitionDepth,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            var stage = plan.Stage!;
            if (preferProbeCorrection)
            {
                preferredProbeCorrectionUsed = true;
                port.Report("三帧质心持续偏在缝的同一侧：优先完成一次原预算可承担的小步修正，再以新帧决定入缝或警告试拍。");
            }
            if (budgetLimitedApproach)
                port.Report($"精调预算接近上限：本步缩短为 {stage.StagePixels:F2}px，保留原回程预留；移锁后重新实测，不把预测当作入缝或试拍许可。");
            if (correctionWindow.CanContinue)
                port.Report($"PHD2 当前帧已回到现锁点附近（{correctionWindow.LatestGuideResidualPixels:F2}px）；整组新帧修正终点分散 {correctionWindow.MaximumEndpointSpreadPixels:F2}px，在原剩余预算内继续小步入缝。尚未确认入缝完成。");
            var preIntentFieldBinding = await port.ValidateCurrentFieldBindingAsync(context, cancellationToken).ConfigureAwait(false);
            if (preIntentFieldBinding.Disposition != GateDisposition.Passed)
            {
                port.ClearField();
                if (port.PendingMotion is { } outstanding)
                {
                    return await port.ReturnPhd2LockToOriginAsync(
                        context,
                        session,
                        outstanding with { Phase = Phd2LockShiftPendingPhase.ReturnRequired, LastReason = preIntentFieldBinding.Message },
                        $"G3 field mount binding failed before PHD2 lock intent: {preIntentFieldBinding.Code}: {preIntentFieldBinding.Message}",
                        cancellationToken).ConfigureAwait(false);
                }
                var relockStop = await port.EnsurePhdStoppedForAutomaticRebuildAsync(
                    ObservationStage.PlaceTargetOnSlit, preIntentFieldBinding.Code, cancellationToken).ConfigureAwait(false);
                if (relockStop.Disposition != GateDisposition.Passed) return new StageResult(relockStop);
                port.RecordSession(null);
                return new StageResult(preIntentFieldBinding, stage.SourceFrameSha256);
            }
            await port.RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            var intentPath = await port.PublishRunJsonEvidenceAsync(
                "phd2-lock-shift-stage-intent",
                $"PHD2 exact runtime-lock stage {ledger.AttemptsUsed + 1}",
                new
                {
                    authority = stage.Authority.ToString(),
                    guideMode = stage.GuideMode.ToString(),
                    formula = "desiredGuideLock = guide + (recognizedSlitAcquisitionPoint - targetCentroid)",
                    expectedCurrent = stage.ExpectedCurrentLockPosition,
                    requested = stage.RequestedLockPosition,
                    fullDesired = stage.FullDesiredLockPosition,
                    targetToSlitDelta = stage.TargetToSlitDelta,
                    stage.StagePixels,
                    stage.ReservedRecoveryMotionPixels,
                    stage.ReservedRecoveryAttempts,
                    stage.CalibrationQualityPolicyId,
                    calibrationQualityGrade = stage.CalibrationQualityGrade.ToString(),
                    stage.RequiresOperatorSupervision,
                    stage.IsUnattendedScienceAuthority,
                    stage.AppliedLockShiftScale,
                    stage.AppliedResidualToleranceScale,
                    stage.RequiredFreshResiduals,
                    planningCode = plan.Code,
                    preferProbeCorrection,
                    budgetLimitedApproach,
                    predictedProbeEnvelopeOnly = budgetLimitedApproach,
                    stage.CumulativeAfterStageUpperPixels,
                    ledger.CumulativeCommandedPixels,
                    preset.MaximumCumulativePixels,
                    correctionWindow,
                    sourceFrameSha256 = stage.SourceFrameSha256,
                    topologyFingerprintSha256 = stage.TopologyFingerprintSha256,
                    registryProfileMutationAllowed = false,
                    automaticRetryAllowed = false,
                },
                session.LastMeasurement.Frame.Path,
                cancellationToken).ConfigureAwait(false);
            var chargedLedger = ledger with
            {
                AttemptsUsed = ledger.AttemptsUsed + 1,
                CumulativeCommandedPixels = ledger.CumulativeCommandedPixels + stage.StagePixels,
                // The durable intent consumes the immutable residual that
                // authorized this exact-lock request. Do not charge the
                // fresh residual captured after the move: that is the
                // evidence from which the next stage must replan.
                LastAcceptedFrameSha256 = stage.SourceFrameSha256,
            };
            var pending = port.CreatePhd2PendingState(
                context,
                preset,
                session,
                chargedLedger,
                stage.RequestedLockPosition,
                Phd2LockShiftPendingPhase.StageIntent,
                intentPath,
                "Exact lock-position request has not yet produced operation-bound settle and fresh G3 residual evidence.");
            await port.PersistPendingAsync(request.PendingPath, pending, cancellationToken).ConfigureAwait(false);
            port.RecordPendingMotion(pending);

            Phd2ExactLockPositionResult exact;
            try
            {
                await port.RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
                var preDispatchFieldBinding = await port.ValidateCurrentFieldBindingAsync(context, cancellationToken).ConfigureAwait(false);
                if (preDispatchFieldBinding.Disposition != GateDisposition.Passed)
                {
                    port.ClearField();
                    return await port.ReturnPhd2LockToOriginAsync(
                        context,
                        session,
                        pending with { Phase = Phd2LockShiftPendingPhase.ReturnRequired, LastReason = preDispatchFieldBinding.Message },
                        $"G3 field mount binding failed after durable PHD2 intent and before dispatch: {preDispatchFieldBinding.Code}: {preDispatchFieldBinding.Message}",
                        cancellationToken).ConfigureAwait(false);
                }
                exact = await port.SetExactLockPositionAsync(
                    new Phd2ExactLockPositionRequest(
                        stage.ExpectedCurrentLockPosition,
                        stage.RequestedLockPosition,
                        preset.LockPreconditionTolerancePixels,
                        stage.StagePixels + 1e-9,
                        preset.LockVerificationTolerancePixels),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                pending = pending with
                {
                    Phase = Phd2LockShiftPendingPhase.ReturnRequired,
                    UpdatedUtc = clock.GetUtcNow(),
                    LastReason = "User cancellation occurred after the durable stage intent. No automatic lock-return command was sent.",
                };
                await port.PersistPendingAsync(request.PendingPath, pending, CancellationToken.None).ConfigureAwait(false);
                port.RecordPendingMotion(pending);
                throw;
            }
            catch (Exception ex) when (Phd2StageFailurePolicy.FindLostFrame(ex) is null)
            {
                return await port.ReturnPhd2LockToOriginAsync(
                    context,
                    session,
                    pending with { Phase = Phd2LockShiftPendingPhase.ReturnRequired, LastReason = ex.Message },
                    $"Outbound exact-lock stage failed or became ambiguous: {ex.Message}",
                    cancellationToken).ConfigureAwait(false);
            }
            ledger = chargedLedger with { CurrentLockPosition = exact.Verified };
            var postExactSnapshot = port.GuideSnapshot;
            if (!postExactSnapshot.IsConnected ||
                postExactSnapshot.AppState != Phd2AppState.Guiding ||
                postExactSnapshot.ConnectionEpoch != session.ConnectionEpoch)
            {
                return await port.ReturnPhd2LockToOriginAsync(
                    context,
                    session,
                    pending with { Phase = Phd2LockShiftPendingPhase.ReturnRequired, LastReason = "The local guide session changed after verified exact-lock readback." },
                    "The local guide session changed after verified exact-lock readback.",
                    cancellationToken).ConfigureAwait(false);
            }
            pending = pending.RebindAfterLocallyAttestedGuideEpoch(
                postExactSnapshot.ConnectionEpoch,
                postExactSnapshot.GuideEpoch,
                exact.Verified,
                clock.GetUtcNow(),
                $"Exact runtime lock readback passed with {exact.VerificationErrorPixels:F3}px error; the locally advanced guide epoch was rebound without resetting motion debt or budget.") with
            {
                Phase = Phd2LockShiftPendingPhase.AwaitingOperationBoundSettle,
            };
            session = session with { GuideEpoch = postExactSnapshot.GuideEpoch };
            await port.PersistPendingAsync(request.PendingPath, pending, CancellationToken.None).ConfigureAwait(false);
            port.RecordPendingMotion(pending);

            Phd2SettleResult stageSettle;
            Phd2StateSnapshot stageProof;
            var stageWindSampledSettle = false;
            Phd2PostLockGuidingObservation? stageReadOnlyObservation = null;
            try
            {
                var calibrationBeforeStageSettle = await port.ValidateCalibrationAsync(preset, cancellationToken).ConfigureAwait(false);
                if (calibrationBeforeStageSettle.Status != Phd2ValidationStatus.Valid)
                {
                    throw new InvalidOperationException(
                        $"Last-moment calibration readback rejected the operation-bound settle; no guide command was sent: {string.Join(" ", calibrationBeforeStageSettle.Failures.Concat(calibrationBeforeStageSettle.IndeterminateReasons))}");
                }
                // ADR-0029: exact lock mutation keeps PHD2 guiding. Do not
                // restart guide/settle for every microstep: the original
                // deadline is spent on three fresh measured optical frames.
                port.Report("PHD2_POST_LOCK_OBSERVING");
                stageReadOnlyObservation = port.BeginFreshResidualObservation(exact, session, preset.LockVerificationTolerancePixels,
                        qualityPolicy.AllowsMeasuredContinuation, cancellationToken);
                stageSettle = session.Settle; // Never fabricate native SettleDone.
                stageProof = port.GuideSnapshot;
                stageWindSampledSettle = true; // Unaccepted until optical window passes.
                pending = pending.RebindAfterLocallyAttestedGuideEpoch(
                    stageProof.ConnectionEpoch,
                    stageProof.GuideEpoch,
                    exact.Verified,
                    clock.GetUtcNow(),
                    "Post-lock guide continuity was checked; durable lineage and charged motion budget were preserved.");
                session = session with { GuideEpoch = stageProof.GuideEpoch };
                await port.PersistPendingAsync(request.PendingPath, pending, CancellationToken.None).ConfigureAwait(false);
                port.RecordPendingMotion(pending);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                pending = pending with
                {
                    Phase = Phd2LockShiftPendingPhase.ReturnRequired,
                    UpdatedUtc = clock.GetUtcNow(),
                    LastReason = "User cancellation occurred while awaiting the operation-bound settle. No automatic lock-return command was sent.",
                };
                await port.PersistPendingAsync(request.PendingPath, pending, CancellationToken.None).ConfigureAwait(false);
                port.RecordPendingMotion(pending);
                throw;
            }
            catch (Exception ex) when (Phd2StageFailurePolicy.FindLostFrame(ex) is null)
            {
                return await port.ReturnPhd2LockToOriginAsync(
                    context,
                    session,
                    pending with { Phase = Phd2LockShiftPendingPhase.ReturnRequired, LastReason = ex.Message },
                    $"Post-lock operation-bound settle failed: {ex.Message}",
                    cancellationToken).ConfigureAwait(false);
            }

            pending = pending with
            {
                Phase = Phd2LockShiftPendingPhase.AwaitingFreshResidual,
                UpdatedUtc = clock.GetUtcNow(),
                LastReason = stageReadOnlyObservation is null
                    ? "Operation-bound native settle passed; fresh immutable G3 residual evidence is required."
                    : "Native guiding was not restarted; read-only tracking observation ended and three fresh immutable G3 residuals are still required.",
            };
            await port.PersistPendingAsync(request.PendingPath, pending, CancellationToken.None).ConfigureAwait(false);
            port.RecordPendingMotion(pending);

            // Commanded lock displacement is not measured sky displacement.
            var expectedTargetAfter = session.LastMeasurement.Measurement.TargetCentroid;
            IReadOnlyList<Phd2GuidingResidualState> measurements;
            try
            {
                measurements = await port.CapturePhd2PlacementGuideWindowAsync(
                    context,
                    preset,
                    topology,
                    exact.Verified,
                    expectedTargetAfter,
                    session.LastMeasurement.RuntimeSlitLocal,
                    session.GuideMode,
                    stageWindSampledSettle
                        ? Math.Max(3, session.Quality.RequiredFreshResidualsPerLockShiftStage)
                        : session.Quality.RequiredFreshResidualsPerLockShiftStage,
                    exact.CompletedUtc.AddSeconds(preset.MaximumStageSeconds),
                    cancellationToken, referenceGuide: session.LastMeasurement.Measurement.GuideStar).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                pending = pending with
                {
                    Phase = Phd2LockShiftPendingPhase.ReturnRequired,
                    UpdatedUtc = clock.GetUtcNow(),
                    LastReason = "User cancellation occurred while acquiring fresh post-stage residual evidence. No automatic lock-return command was sent.",
                };
                await port.PersistPendingAsync(request.PendingPath, pending, CancellationToken.None).ConfigureAwait(false);
                port.RecordPendingMotion(pending);
                throw;
            }
            catch (Exception ex) when (Phd2StageFailurePolicy.FindLostFrame(ex) is null)
            {
                return await port.ReturnPhd2LockToOriginAsync(
                    context,
                    session,
                    pending with { Phase = Phd2LockShiftPendingPhase.ReturnRequired, LastReason = ex.Message },
                    $"Fresh post-stage G3 residual failed: {ex.Message}",
                    cancellationToken).ConfigureAwait(false);
            }
            var measured = measurements[^1];
            targetCompletionWindow = measurements;
            completionWindowRetries = 0;
            // Each completed lock stage produces a new immutable residual
            // and mount binding.  Promote it before any failure/return path
            // or next-stage pre-intent gate can inspect current G3 state.
            port.PromoteMeasurement(measured, preset);
            var residual = PointDistance(measured.Measurement.TargetCentroid, measured.Measurement.RecognizedSlitAcquisitionPoint);
            if (residual > priorResidual + preset.MaximumResidualGrowthPixels)
            {
                transientResidualGrowthWarning = qualityPolicy.AllowsMeasuredContinuation &&
                    measured.Measurement.GuidePositionMeasuredInFrame && measured.Measurement.TargetIdentityConfirmed &&
                    measured.Measurement.TargetPositionAuthority != Phd2TargetPositionAuthority.CatalogWcsProjection;
                if (!transientResidualGrowthWarning)
                    return await port.ReturnPhd2LockToOriginAsync(
                    context,
                    session,
                    pending with { Phase = Phd2LockShiftPendingPhase.ReturnRequired, LastReason = "Fresh target/slit residual worsened." },
                    $"Fresh target/slit residual worsened from {priorResidual:F3}px to {residual:F3}px.",
                    cancellationToken).ConfigureAwait(false);
                await port.PublishRunJsonEvidenceAsync("phd2-transient-residual-growth-warning",
                    "Measured residual growth retained as a supervised warning before same-lock resampling",
                    new { priorResidual, residual, preset.MaximumResidualGrowthPixels,
                        newLockMotion = false, budgetReset = false, scienceAcceptanceUnchanged = true },
                    measured.Frame.Path, cancellationToken).ConfigureAwait(false);
            }

            var stageProofAfterFrame = port.GuideSnapshot;
            if (stageReadOnlyObservation is not null)
            {
                stageReadOnlyObservation = stageReadOnlyObservation.AcceptResiduals(
                    measurements.Select(item => item.Frame).ToArray(), stageProofAfterFrame);
                await port.PublishRunJsonEvidenceAsync(
                    "phd2-post-lock-readonly-window",
                    "Measured exact-lock continuation without another native guide/settle request (ADR-0029)",
                    new { observation = stageReadOnlyObservation,
                        originalNativeSettle = stageSettle,
                        nativeGuideCommandSent = false, syntheticSettleDone = false,
                        preciseSlitPlacementProven = false, measuredContinuation = true,
                        budgetReset = false }, measured.Frame.Path, cancellationToken).ConfigureAwait(false);
            }
            var stageGuideResidual = PointDistance(measured.Measurement.GuideStar, exact.Verified);
            var stageQuality = port.SelectPhd2CalibrationQuality(
                calibration,
                preset,
                Phd2CalibrationEvaluationPhase.PostSettle,
                stageReadOnlyObservation?.ToCalibrationEvidence(stageSettle, stageProofAfterFrame) ??
                    port.CreateCalibrationSettleEvidence(stageSettle, stageProofAfterFrame, stageWindSampledSettle, measurements.Count),
                Phd2PlacementGuideWindowPolicy.CalibrationResidualEvidence(
                    measured, stageGuideResidual, topology, clock.GetUtcNow()),
                Phd2CalibrationSelectionPurpose.LockShift).Selected;
            if (stageQuality?.IsLockShiftAuthority != true)
            {
                return await port.ReturnPhd2LockToOriginAsync(
                    context,
                    session,
                    pending with { Phase = Phd2LockShiftPendingPhase.ReturnRequired, LastReason = "Calibration quality was revoked after the fresh stage residual." },
                    "The post-stage calibration grade no longer grants lock-shift authority.",
                    cancellationToken).ConfigureAwait(false);
            }
            var stageQualification = port.BuildPhd2LockShiftQualification(identity, calibration, topology, preset, stageQuality, pierSide);
            session = session with
            {
                Qualification = stageQualification,
                Quality = stageQuality,
                LastMeasurement = measured,
                Settle = stageSettle,
                FreshGuidingWindowReplacedSettle = session.FreshGuidingWindowReplacedSettle || stageWindSampledSettle,
                ReadOnlyPostLockObservation = stageReadOnlyObservation,
            };
            port.RecordSession(session);
            priorResidual = residual;
            pending = pending with
            {
                CurrentLockX = exact.Verified.X,
                CurrentLockY = exact.Verified.Y,
                LastAcceptedFrameSha256 = measured.Measurement.FrameSha256,
                LastFramePath = measured.Frame.Path,
                UpdatedUtc = clock.GetUtcNow(),
                LastReason = $"Fresh residual {residual:F3}px accepted; the next stage must replan from this actual lock/frame pair.",
            };
            await port.PersistPendingAsync(request.PendingPath, pending, CancellationToken.None).ConfigureAwait(false);
            port.RecordPendingMotion(pending);
        }
    }

    // Three matched native-primary measurements disagree with the effective
    // multi-star correction. Ordinary seeing, one outlier, direct-target mode
    // and absent/ambiguous crops cannot authorize this single recovery.
    internal static bool NeedsPrimaryStarRecovery(IReadOnlyList<Phd2GuidingResidualState> window,
        Phd2Point currentLock, Phd2SlitGuideMode mode) => mode == Phd2SlitGuideMode.OffSlitGuideStar &&
        window.Count >= 3 && window.All(item =>
            item.Measurement.GuidePositionMeasuredInFrame && item.Measurement.TargetIdentityConfirmed &&
            item.Frame.PrimaryStarImage?.Frame == item.Frame.TriggerGuideFrame &&
            item.Frame.NativeEffectiveGuidePosition is { } effective &&
            PointDistance(item.Measurement.GuideStar, currentLock) > 3 &&
            PointDistance(item.Measurement.GuideStar, effective) > 3);

    private async Task<Phd2SlitPlacementSession> RecoverPrimaryStarAsync(ObservationContext context,
        Phd2SlitConvergenceRequest request, Phd2SlitPlacementSession session,
        Phd2LockShiftPendingState pending, Phd2Point currentLock, DateTimeOffset completionDeadline,
        CancellationToken token)
    {
        RequireSameCorrectionWindowEpoch(session, currentLock, request.Preset);
        await port.RequireImmediatePhysicalActionGatesAsync(context, token).ConfigureAwait(false);
        var calibration = await port.ValidateCalibrationAsync(request.Preset, token).ConfigureAwait(false);
        if (calibration.Status != Phd2ValidationStatus.Valid)
            throw new Phd2Exception("PHD2_PRIMARY_RESET_CALIBRATION_CHANGED: Same-lock recovery cannot acquire a new calibration.");
        RequireSameCorrectionWindowEpoch(session, currentLock, request.Preset);
        // ADR-0040: precharge this already charged stage before dispatch. A
        // new outbound stage can qualify again; a retry/restart of this one cannot.
        pending = pending with { PrimaryStarSettleRecoveryAttempted = true,
            PrimaryStarSettleRecoveryStage = pending.AttemptsUsed,
            UpdatedUtc = clock.GetUtcNow(), LastReason = "ADR-0040 primary-star settle charged for this exact-lock stage; lock, attempts, distance and original clock unchanged." };
        await port.PersistPendingAsync(request.PendingPath, pending, token).ConfigureAwait(false);
        port.RecordPendingMotion(pending);
        session = session with { PrimaryStarSettleRecoveryAttempted = true,
            PrimaryStarSettleRecoveryStage = pending.AttemptsUsed };
        port.RecordSession(session);
        port.Report("多星综合偏差与主星实测位置不一致：保持锁点，本移锁阶段至多一次原生稳定恢复；不重校准、不追加位移，原预算和回程预留不变。");
        var remaining = completionDeadline - clock.GetUtcNow();
        if (remaining <= TimeSpan.Zero) throw new PhysicalActionGateException(GateResult.TargetQuality(
            "PHD2_PRIMARY_RESET_TIME_EXHAUSTED", "No time remains before the original return reserve; no settle sent."));
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(remaining < TimeSpan.FromSeconds(request.Preset.MaximumStageSeconds)
            ? remaining : TimeSpan.FromSeconds(request.Preset.MaximumStageSeconds));
        Phd2SettleResult result;
        try
        {
            await port.RequireImmediatePhysicalActionGatesAsync(context, bounded.Token).ConfigureAwait(false);
            result = await port.ResettleAtCurrentLockAsync(new(session.ConnectionEpoch, session.GuideEpoch,
                currentLock, request.Preset.LockVerificationTolerancePixels,
                new(3, 1, (int)Math.Max(1, Math.Min(remaining.TotalSeconds - 5,
                    request.Preset.MaximumStageSeconds - 5)))), bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && bounded.IsCancellationRequested)
        {
            throw new PhysicalActionGateException(GateResult.TargetQuality("PHD2_PRIMARY_RESET_TIME_EXHAUSTED",
                "Single native settle reached its original time allowance; retain debt for checked stop/skip. No extra lock shift authorized."));
        }
        catch (Phd2CommandTimeoutException) when (!token.IsCancellationRequested)
        {
            // The owner attempted stop, but a timeout is not a stop receipt.
            // The ordinary target boundary must still confirm every owner idle
            // before it may archive the debt and execute the next target.
            throw new PhysicalActionGateException(GateResult.TargetQuality("PHD2_PRIMARY_RESET_TIME_EXHAUSTED",
                "Single native settle did not receive its terminal receipt; checked stop/skip is required, not another guide or lock command."));
        }
        var snapshot = port.GuideSnapshot;
        var proof = new Phd2PostLockGuidingObservation("primary-recovery-" + Guid.NewGuid().ToString("N"),
            session.ConnectionEpoch, checked(session.GuideEpoch + 1), snapshot.EventSequence,
            currentLock, request.Preset.LockVerificationTolerancePixels, clock.GetUtcNow(),
            YieldedToFreshResiduals: true, MeasuredContinuation: true);
        if (result.NativeCalibrationOccurred || !proof.IsCurrent(snapshot))
            throw new Phd2Exception("PHD2_PRIMARY_RESET_OWNER_CHANGED: Recovery did not retain the same star/lock session.");
        pending = pending.RebindAfterLocallyAttestedGuideEpoch(snapshot.ConnectionEpoch, snapshot.GuideEpoch,
            currentLock, clock.GetUtcNow(), "Native single recovery ended; fresh primary/target/slit measurements still required.");
        await port.PersistPendingAsync(request.PendingPath, pending, CancellationToken.None).ConfigureAwait(false);
        port.RecordPendingMotion(pending);
        await port.PublishRunJsonEvidenceAsync("phd2-primary-star-settle-recovery", "Single same-lock native recovery, not slit acceptance",
            new { result, originalGuideEpoch = session.GuideEpoch, snapshot.GuideEpoch, currentLock,
                pending.LineageId, pending.StartedUtc, pending.AttemptsUsed, pending.CumulativeCommandedPixels,
                pending.PrimaryStarSettleRecoveryStage,
                completionDeadline, nativeGuideCalls = 1, lockChanged = false, recalibrationRequested = false,
                budgetReset = false, preciseSlitPlacementProven = false }, session.LastMeasurement.Frame.Path, token).ConfigureAwait(false);
        session = session with { GuideEpoch = snapshot.GuideEpoch, Settle = result,
            FreshGuidingWindowReplacedSettle = true, ReadOnlyPostLockObservation = proof };
        port.RecordSession(session);
        return session;
    }

    private bool IsUsefulBudgetLimitedApproach(IReadOnlyList<Phd2GuidingResidualState> window,
        Phd2LockShiftStagePlan stage, Phd2SlitPlacementCommissioning preset, double tolerance, int requiredFrames)
    {
        if (!qualityPolicy.WarningProbeEnabled || stage.GuideMode != Phd2SlitGuideMode.OffSlitGuideStar ||
            window.Count < requiredFrames || window.Any(item => !item.Measurement.GuidePositionMeasuredInFrame ||
                !item.Measurement.TargetIdentityConfirmed || !Phd2PlacementGuideWindowPolicy.HasMeasuredPositionAuthority(
                    item.Measurement.TargetPositionAuthority))) return false;
        if (!Phd2PlacementGuideWindowPolicy.EvaluateCorrectionWindow(
            window.Select(item => item.Measurement).ToArray(), stage.ExpectedCurrentLockPosition,
            stage.GuideMode, true, true, false, preset.EffectiveGuideLockResidualPixels,
            tolerance, requiredFrames).CanContinue) return false;
        // Same-frame guide + slit - target cancels the guide error. Evaluate
        // every frame; no favourable subset and no prediction-as-arrival.
        var predicted = window.Select(item => new Phd2Point(
            item.Measurement.TargetCentroid.X + stage.RequestedLockPosition.X - item.Measurement.GuideStar.X,
            item.Measurement.TargetCentroid.Y + stage.RequestedLockPosition.Y - item.Measurement.GuideStar.Y)).ToArray();
        var residuals = window.Select((item, i) => PointDistance(predicted[i],
            item.Measurement.RecognizedSlitAcquisitionPoint)).ToArray();
        var apertures = window.Select((item, i) => Phd2PlacementGuideWindowPolicy.ProjectOnMeasuredSlit(
            ToFrameLocal(predicted[i], preset), item.RuntimeSlitLocal)).ToArray();
        return Phd2PlacementGuideWindowPolicy.AllWithinTolerance(residuals, tolerance) ||
            qualityPolicy.EvaluateSlitWarning(true, true, residuals, apertures, tolerance,
                preset.MaximumResidualGrowthPixels, preset.MaximumAcquisitionResidualPixels).CanProbe;
    }

    private void RequireSameCorrectionWindowEpoch(Phd2SlitPlacementSession session,
        Phd2Point currentLock, Phd2SlitPlacementCommissioning preset)
    {
        var snapshot = port.GuideSnapshot;
        if (!snapshot.IsConnected || snapshot.AppState != Phd2AppState.Guiding ||
            snapshot.ConnectionEpoch != session.ConnectionEpoch || snapshot.GuideEpoch != session.GuideEpoch ||
            snapshot.LockPosition is not { } actual || PointDistance(actual, currentLock) > preset.LockVerificationTolerancePixels)
            throw new InvalidOperationException("PHD2_GUIDE_WINDOW_EPOCH_CHANGED: A rolling correction window cannot span a guide epoch or lock change.");
        port.ThrowIfGuideOutputFailed();
    }


    private static PixelPoint ToFrameLocal(Phd2Point domain, Phd2SlitPlacementCommissioning preset) =>
        preset.CoordinateDomain == Phd2ImageCoordinateDomain.FullSensorCoordinates
            ? new PixelPoint(domain.X - preset.RoiX, domain.Y - preset.RoiY)
            : new PixelPoint(domain.X, domain.Y);


    private static double PointDistance(Phd2Point a, Phd2Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }


    private static Phd2Point AddPoint(Phd2Point a, Phd2Point b) => new(a.X + b.X, a.Y + b.Y);

    private static Phd2Point SubtractPoint(Phd2Point a, Phd2Point b) => new(a.X - b.X, a.Y - b.Y);

}
