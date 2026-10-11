using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    // Run-local decision, not a rewrite of the immutable Profile/preset. The
    // selected fixed duration is used by every subsequent FITS exposure check.
    private int? selectedOffSlitExposureMilliseconds;
    private bool guideSnrPreparationConsumed;

    private int GuideExposureFor(Phd2SlitPlacementCommissioning preset, Phd2SlitGuideMode mode) =>
        mode == Phd2SlitGuideMode.OffSlitGuideStar && selectedOffSlitExposureMilliseconds is { } selected
            ? selected : preset.ExposureFor(mode);

    private async Task<Phd2PreparedGuideSelection> PrepareSnrOptimizedGuideSelectionAsync(
        ObservationContext context, Phd2PlacementGuideChoice choice,
        Phd2SlitPlacementCommissioning preset, Phd2LoopingStartResult loop, CancellationToken token)
    {
        var selection = await SelectFreshPhd2GuideAsync(choice, preset, token).ConfigureAwait(false);
        var motionFree = pendingPhd2LockShift is null &&
            (pendingSlitPlacement is null or { Phase: SlitPlacementPendingPhase.SettledBudgetLedger });
        if (guideSnrPreparationConsumed || !motionFree || choice.Mode != Phd2SlitGuideMode.OffSlitGuideStar ||
            Volatile.Read(ref phd2GuidingEverStarted) != 0)
            return new(choice, loop, selection.Selection, selection.Requested, selection.Selected);
        guideSnrPreparationConsumed = true;
        var supported = await phd2.GetExposureDurationsAsync(token).ConfigureAwait(false);
        int? previousExposureMilliseconds = null;
        for (var increases = 0; increases <= Phd2GuideSnrExposurePolicy.MaximumIncreases; increases++)
        {
            await RequireImmediatePhysicalActionGatesAsync(context, token).ConfigureAwait(false);
            Phd2SelectedStarSnrObservation? observation = null;
            Phd2GuidingFrameLostException? candidateLoss = null;
            try
            {
                observation = await phd2.ObserveSelectedStarSnrAsync(selection.Selected,
                    choice.ExposureMilliseconds, TimeSpan.FromSeconds(10 + 4d * choice.ExposureMilliseconds / 1000),
                    token).ConfigureAwait(false);
            }
            catch (Phd2GuidingFrameLostException lost) when (lost.SelectedStarSnrBaseline is { } sampleOwner &&
                Phd2GuideSnrExposurePolicy.CanRestoreAfterCandidateLoss(
                sampleOwner, lost.Snapshot, phd2.Snapshot, selection.Selected, previousExposureMilliseconds.HasValue,
                pendingPhd2LockShift is null && (pendingSlitPlacement is null or { Phase: SlitPlacementPendingPhase.SettledBudgetLedger }),
                Volatile.Read(ref phd2GuidingEverStarted) != 0))
            {
                candidateLoss = lost;
            }
            var next = candidateLoss is not null ? previousExposureMilliseconds :
                Phd2GuideSnrExposurePolicy.NextExposure(observation!, supported, increases, motionFree, true);
            var median = observation is null ? null : Phd2GuideSnrExposurePolicy.MedianSnr(observation);
            await PublishRunJsonEvidenceAsync("phd2-guide-snr-preflight",
                "Native selected-star SNR preference before guiding; never a new quality blocker",
                new { observation, medianSnr = median, preferredSnr = Phd2GuideSnrExposurePolicy.PreferredSnr,
                    nextExposureMilliseconds = next, increases, maximumIncreases = Phd2GuideSnrExposurePolicy.MaximumIncreases,
                    maximumExposureMilliseconds = Phd2GuideSnrExposurePolicy.MaximumExposureMilliseconds,
                    candidateLost = candidateLoss is not null, lostFrame = candidateLoss?.LostFrame,
                    restoringPreviousTier = candidateLoss is not null,
                    candidateRankingByCoordinator = false, automaticExposureMode = false, qualityGate = false },
                choice.Field.FramePath, token).ConfigureAwait(false);
            if (next is null)
            {
                Report(median is >= Phd2GuideSnrExposurePolicy.PreferredSnr
                    ? $"PHD2 原生选星 SNR 中位数 {median:F1}；本轮固定导星曝光 {choice.ExposureMilliseconds} ms。"
                    : $"warning：PHD2 选星 SNR {(median?.ToString("F1") ?? "未提供完整读回")}；保留 {choice.ExposureMilliseconds} ms，不因 SNR 偏好值阻断。后续仍检查真实导星及入缝。");
                break;
            }
            Report(candidateLoss is not null
                ? $"warning：延长曝光后的候选星丢失，停止本轮预览后用新帧恢复 {next} ms、由 PHD2 重选一次；不消耗整目标重校准恢复机会。"
                : $"PHD2 选星 SNR {median:F1} 偏低：导星前将曝光从 {choice.ExposureMilliseconds} ms 调至 {next} ms，再由 PHD2 原生重选；不修改校准或运动预算。");
            // This selection loop is locally owned; stop it before any exposure
            // change. No active guiding/science/return lineage uses this path.
            await StopPhdAndWaitAsync(token).ConfigureAwait(false);
            await RequireImmediatePhysicalActionGatesAsync(context, token).ConfigureAwait(false);
            var brighter = await CaptureAndSelectPhd2GuideAtExposureAsync(context, choice.Field, preset,
                choice.Mode, token, next).ConfigureAwait(false);
            var restored = candidateLoss is not null;
            if (brighter.Selection.Gate.Disposition != GateDisposition.Passed)
            {
                if (restored) throw new PhysicalActionGateException(brighter.Selection.Gate);
                // Longer exposure can clip the target even if it helps the
                // off-slit star. Do not turn this optional optimization into a
                // new veto; once, restore the previous fixed tier with a FRESH
                // frame and the same continuity checks, never the old image.
                await PublishRunJsonEvidenceAsync("phd2-guide-snr-restore-tier",
                    "Longer selection frame lost target continuity; bounded restoration of previous tier",
                    new { attemptedExposure = next, restoreExposure = choice.ExposureMilliseconds,
                        brighter.Selection.Gate, staleFrameReused = false }, brighter.Field.FramePath, token).ConfigureAwait(false);
                brighter = await CaptureAndSelectPhd2GuideAtExposureAsync(context, choice.Field, preset,
                    choice.Mode, token, choice.ExposureMilliseconds).ConfigureAwait(false);
                if (brighter.Selection.Gate.Disposition != GateDisposition.Passed)
                    throw new PhysicalActionGateException(brighter.Selection.Gate);
                restored = true;
            }
            previousExposureMilliseconds = choice.ExposureMilliseconds;
            selectedOffSlitExposureMilliseconds = brighter.ExposureMilliseconds;
            choice = brighter;
            var binding = await ValidateG3FieldMountBindingForMotionAsync(context, choice.Field, token).ConfigureAwait(false);
            if (binding.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(binding);
            loop = await phd2.StartLoopingAndWaitForFreshFrameAsync(
                new(TimeSpan.FromSeconds(preset.FreshLoopFrameTimeoutSeconds)), token).ConfigureAwait(false);
            if (!loop.LeavesLoopingForGuideTakeover || loop.StopCommandSent || loop.ExposureChanged)
                throw new InvalidOperationException("PHD2 SNR selection loop did not preserve the fixed-exposure takeover contract.");
            selection = await SelectFreshPhd2GuideAsync(choice, preset, token).ConfigureAwait(false);
            if (restored)
            {
                Report($"warning：长曝光候选未保留，已用新帧恢复 {choice.ExposureMilliseconds} ms 并由 PHD2 重选；不再延长，后续仍须实测导星和入缝。");
                break;
            }
        }
        return new(choice, loop, selection.Selection, selection.Requested, selection.Selected);
    }
}
