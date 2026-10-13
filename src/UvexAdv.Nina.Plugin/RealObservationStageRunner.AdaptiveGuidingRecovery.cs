using NINA.Core.Enum;
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
        if (!HasNoOutstandingGuideRecoveryMotion || !phd2.IsConnected ||
            phd2.Snapshot.GuideOutput?.Failed == true)
            return (false, original);
        // Retain the existing no-motion science continuation before stopping a
        // guide that can still pass a fresh same-epoch slit check.
        if (stage is ObservationStage.SelectAtrExposure or ObservationStage.RunScienceBlock &&
            original.Gate.Code is "GUIDING_UNSTABLE" or "GUIDING_NOT_STABLE" &&
            GuideRecovery.TryConsumeInPlaceCheck())
        {
            try
            {
                if (await TryVerifyScienceInPlaceAsync(context, token).ConfigureAwait(false)) return (true, original);
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
            evidence = await PublishRunJsonEvidenceAsync("phd2-adaptive-star-check",
                "Fresh field triage, not proof of weather, target identity or slit arrival",
                new { sourceCode = original.Gate.Code, action = action.ToString(), sample,
                    captured.CompletedUtc, content, sha256 = sha, mountBinding = binding,
                    deadline, GuideRecovery.Reselections, GuideRecovery.CalibrationConsumed,
                    weatherCauseConfirmed = false, motionIssued = false, budgetReset = false }, path, token).ConfigureAwait(false);
            // Atomic capture preserves the stopped epoch. Never manufacture a
            // new stop receipt for a preview loop or an externally restarted guide.
            if (!automaticRebuildStopProof.IsCurrent(phd2.Snapshot))
                return (false, new StageResult(GateResult.Unknown("PHD2_ADAPTIVE_OWNER_CHANGED",
                    "星点检测后停止回执已失效；不继续恢复。"), evidence));
            PublishG3Preview(image, $"导星恢复星点检测 {sample + 1}：{content.StellarMeasurement.StarCount} 星；{action}");
            if (action == GuideRecoveryAction.Exhausted)
                return (false, AdaptiveRecoveryExhausted(AdaptiveGuidingRecovery.ExhaustedCode, evidence));
            if (action is GuideRecoveryAction.Reselect or GuideRecoveryAction.ForceCalibration)
            {
                if (stage is ObservationStage.SelectAtrExposure or ObservationStage.RunScienceBlock)
                {
                    var budget = await CheckScienceRebuildBudgetAsync(context, original.Gate, token).ConfigureAwait(false);
                    if (budget is not null)
                        return (false, new StageResult(budget, evidence));
                }
                adaptiveCalibrationPending = action == GuideRecoveryAction.ForceCalibration;
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
            await Task.Delay(remaining < TimeSpan.FromSeconds(15) ? remaining : TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        }
        return (false, AdaptiveRecoveryExhausted(AdaptiveGuidingRecovery.StarsTimeoutCode, evidence));
    }

    private StageResult AdaptiveRecoveryExhausted(string code, string? evidence) =>
        new(GateResult.TargetQuality(code,
            "本目标有界导星恢复或星点等待已用尽，已停止；整夜序列独立复核空闲与原运动账本后，按本目标失败策略跳过。未降低入缝门限，未单独关顶。",
            new Dictionary<string, double> { ["stopConfirmed"] = 1,
                ["reselectionAttempts"] = GuideRecovery.Reselections,
                ["calibrationReserved"] = GuideRecovery.CalibrationConsumed ? 1 : 0 }), evidence);
}
