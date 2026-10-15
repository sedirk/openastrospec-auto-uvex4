using System.Globalization;
using NINA.Equipment.Model;
using UvexAdv.Observatory;
using UvexAdv.Spectroscopy;
using static UvexAdv.Nina.Plugin.ObservationWorkflowResults;

namespace UvexAdv.Nina.Plugin;

// Sole writer of science attempts, accepted frames, integration and probe credit.
// Recovery can invalidate the exposure selection, but cannot reset these totals.
internal sealed class AtrScienceWorkflow(AtrRunConfiguration configuration, AtrProbeWorkflow probe,
    IAtrObservationPort port) : IAtrFinalProbeConsumer
{
    private readonly AtrProbeScienceCredit probeScienceCredit = new();
    private int savedAtrFrames;
    private int attemptedAtrFrames;
    private int retainedAtrScienceFrames;
    private int atrWarningFrames;
    private int reusedAtrProbeFrames;
    private double acceptedAtrExposureSeconds;

    internal int AcceptedFrames => Volatile.Read(ref savedAtrFrames);
    internal int AttemptedFrames => Volatile.Read(ref attemptedAtrFrames);
    internal int RetainedFrames => Volatile.Read(ref retainedAtrScienceFrames);
    internal int WarningFrames => Volatile.Read(ref atrWarningFrames);
    internal int ReusedProbeFrames => Volatile.Read(ref reusedAtrProbeFrames);
    internal double AcceptedExposureSeconds => Volatile.Read(ref acceptedAtrExposureSeconds);
    internal GateResult? RemainingAttemptGate =>
        AtrScienceFrameQualityPolicy.RemainingAttemptGate(savedAtrFrames, attemptedAtrFrames, configuration);

    internal Task<StageResult> SelectExposureAsync(ObservationContext context, CancellationToken token) =>
        probe.SelectAsync(context, this, token);
    private GateResult ValidateAtrScienceMetrics(SpectralProbeMetrics metrics) =>
        AtrScienceFrameQualityPolicy.Evaluate(metrics, configuration);


    internal async Task<StageResult> RunAsync(ObservationContext context, CancellationToken cancellationToken)
    {
        if (port.MeridianRequested) return port.MeridianSegmentBoundary();
        // A terminal science budget must be discovered before even a reprobe
        // or guide rebuild; resuming cannot manufacture another attempt.
        var entryAttemptGate = AtrScienceFrameQualityPolicy.RemainingAttemptGate(
            savedAtrFrames, attemptedAtrFrames, configuration);
        if (entryAttemptGate is not null) return new StageResult(entryAttemptGate);
        if (probe.RequiresReprobe)
        {
            var reprobe = await SelectExposureAsync(context, cancellationToken).ConfigureAwait(false);
            if (!reprobe.CanAdvance) return reprobe;
            if (port.MeridianRequested) return port.MeridianSegmentBoundary();
        }
        if (probe.SelectedExposureSeconds is not { } exposure)
        {
            return Attention(ObservationStage.RunScienceBlock, "ATR_TIER_NOT_SELECTED", "No quality-gated ATR exposure tier is available.");
        }
        if (configuration.ScienceFrameCount <= 0)
        {
            return Attention(ObservationStage.RunScienceBlock, "ATR_FRAME_COUNT_INVALID", "ATR science frame count must be positive.");
        }

        while (savedAtrFrames < configuration.ScienceFrameCount)
        {
            // A normal deadline never aborts a long exposure. Check at this
            // saved-frame boundary before authorizing another acquisition.
            if (port.SequenceStopGate() is { } sequenceStop) return new StageResult(sequenceStop);
            var remainingAttemptGate = AtrScienceFrameQualityPolicy.RemainingAttemptGate(
                savedAtrFrames, attemptedAtrFrames, configuration);
            if (remainingAttemptGate is not null) return new StageResult(remainingAttemptGate);
            if (probe.RequiresReprobe)
            {
                var reprobe = await SelectExposureAsync(context, cancellationToken).ConfigureAwait(false);
                if (!reprobe.CanAdvance) return reprobe;
                if (port.MeridianRequested) return port.MeridianSegmentBoundary();
                if (probe.SelectedExposureSeconds is not { } reselectedExposure)
                {
                    return Attention(ObservationStage.RunScienceBlock, "ATR_TIER_NOT_SELECTED", "Automatic reprobe advanced without selecting an ATR exposure tier.");
                }
                exposure = reselectedExposure;
                if (savedAtrFrames >= configuration.ScienceFrameCount) break;
            }
            UpdateRemainingScienceDuration(context, exposure);
            if (port.RequestNativeMeridianBoundary(context, exposure)) return port.MeridianSegmentBoundary();
            var protectedPlan = context.Plan with { PlannedDuration = context.RemainingWorstCaseDuration ?? context.Plan.PlannedDuration };
            var environment = port.ValidateEnvironment(protectedPlan);
            if (environment.Disposition != GateDisposition.Passed) return new StageResult(environment);
            var coverGate = await port.EnsureOpticalCoverOpenAsync(context, cancellationToken).ConfigureAwait(false);
            if (coverGate.Disposition != GateDisposition.Passed) return new StageResult(coverGate);
            port.ThrowIfGuideOutputFailed();
            if (!port.IsGuidingStable()) return Attention(ObservationStage.RunScienceBlock, "GUIDING_LOST", "PHD2 no longer reports a settled guiding state; no new ATR exposure was started.");
            var qhyGate = await port.CheckPhotometryHealthAsync(context, cancellationToken).ConfigureAwait(false);
            if (qhyGate.Disposition != GateDisposition.Passed) return new StageResult(qhyGate);
            if (qhyGate.Severity == GateSeverity.Warning)
            {
                await port.WriteAuditBestEffortAsync("qhy-photometry-optional-warning", new
                {
                    context.Plan.ObservationRunId,
                    qhyGate.Code,
                    qhyGate.Message,
                    atrAttempt = attemptedAtrFrames + 1,
                    spectroscopyContinues = true,
                }).ConfigureAwait(false);
            }

            // One fresh fail-closed checkpoint immediately before every individual ATR frame.
            await port.RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            attemptedAtrFrames++;
            // An exposure attempt is provenance even when capture, conversion, or save
            // subsequently fails. Persist it before opening the shutter.
            port.PublishFrameCounters();
            port.PublishAcquisitionProgress(context);
            var acceptedIndex = savedAtrFrames + 1;
            port.Report($"准备科学帧 {acceptedIndex}/{configuration.ScienceFrameCount} · 已验收 {savedAtrFrames} 张 · 尝试 {attemptedAtrFrames}/{configuration.MaximumScienceAttempts} · {exposure:G4}s", savedAtrFrames / (double)configuration.ScienceFrameCount);
            var captured = await port.CaptureAtrImageAsync(
                context,
                exposure,
                CaptureSequence.ImageTypes.LIGHT,
                $"UVEX-ADV science accepted-candidate {acceptedIndex}/{configuration.ScienceFrameCount} attempt {attemptedAtrFrames}",
                cancellationToken).ConfigureAwait(false);
            port.PublishAtrPreview(captured.Image, ObservationUiPresentation.AtrFrameCaption(true, acceptedIndex,
                configuration.ScienceFrameCount, attemptedAtrFrames, exposure, captured.Metrics.HighPercentileAdu,
                captured.Metrics.ClippedDispersionColumnFraction, captured.Metrics.LineSnrPerResolutionElement,
                ObservationStaticTextLocalization.EffectiveCulture),
                captured.Metrics.TraceSpatialCenterPixel, captured.Metrics.TraceSpatialHalfWidthPixels);
            var quality = ValidateAtrScienceMetrics(captured.Metrics);
            var savedScience = await port.SaveAtrImageAsync(
                captured,
                attemptedAtrFrames,
                quality.Disposition == GateDisposition.Passed,
                quality.Disposition,
                quality.Code,
                quality.Message,
                cancellationToken).ConfigureAwait(false);
            retainedAtrScienceFrames++;
            if (savedScience.TemperatureGate.Disposition != GateDisposition.Passed)
            {
                probe.Invalidate();
                port.PublishFrameCounters();
                port.PublishAcquisitionProgress(context);
                if (RemainingAttemptGate is null)
                {
                    var recovered = await probe.TryRecoverSavedTemperatureAsync(context, savedScience.TemperatureGate, cancellationToken).ConfigureAwait(false);
                    if (recovered is { Disposition: GateDisposition.Passed }) continue;
                    if (recovered is not null) return new StageResult(recovered);
                }
                return new StageResult(savedScience.TemperatureGate);
            }
            if (quality.Severity == GateSeverity.Warning) atrWarningFrames++;
            context.Set("atrAttemptedFrames", attemptedAtrFrames);
            if (quality.Disposition != GateDisposition.Passed)
            {
                port.PublishFrameCounters();
                probe.Invalidate();
                port.PublishAcquisitionProgress(context);
                await port.WriteAuditBestEffortAsync("atr-science-frame-rejected", new
                {
                    context.Plan.ObservationRunId,
                    attemptedAtrFrames,
                    acceptedFrames = savedAtrFrames,
                    quality.Code,
                    quality.Message,
                    immutableFrameRetained = true,
                }).ConfigureAwait(false);
                var exhaustedAttempts = AtrScienceFrameQualityPolicy.RemainingAttemptGate(
                    savedAtrFrames, attemptedAtrFrames, configuration);
                if (exhaustedAttempts is not null)
                    return new StageResult(exhaustedAttempts with
                    {
                        Message = $"{exhaustedAttempts.Message} Last retained frame: {quality.Code}: {quality.Message}",
                    });
                if (quality.Code == "GUIDING_UNSTABLE")
                {
                    // Save the failed exposure first, but never count it as
                    // accepted or retry through the clipping-only loop. The
                    // stage supervisor owns the single bounded same-lock check
                    // or dependency rebuild, with all counters preserved.
                    return new StageResult(quality);
                }
                // Clipping is recoverable: retain and flag the rejected FITS,
                // re-run the bounded exposure selector, and continue in the same
                // unattended stage.  The maximum-attempt limit still bounds this
                // loop and prevents an endless probe/capture cycle.
                continue;
            }
            savedAtrFrames++;
            acceptedAtrExposureSeconds += captured.Metrics.ExposureSeconds;
            context.Set("atrSavedFrames", savedAtrFrames);
            context.Set("atrAcceptedFrames", savedAtrFrames);
            port.PublishFrameCounters();
            port.PublishAcquisitionProgress(context);
            UpdateRemainingScienceDuration(context, exposure);
        }

        port.Report("ATR 科学曝光块完成", 1);
        port.PublishAcquisitionProgress(context, "Complete");
        var scienceMetrics = new Dictionary<string, double>
        {
            ["acceptedFrames"] = savedAtrFrames,
            ["retainedFrames"] = retainedAtrScienceFrames + reusedAtrProbeFrames,
            ["reusedProbeFrames"] = reusedAtrProbeFrames,
            ["acceptedExposureSeconds"] = acceptedAtrExposureSeconds,
            ["attemptedFrames"] = attemptedAtrFrames,
            ["warningFrames"] = atrWarningFrames,
            ["exposureSeconds"] = exposure,
        };
        return atrWarningFrames > 0 || port.PhotometryUnavailable
            ? Warning(
                "ATR_SCIENCE_BLOCK_COMPLETE_WITH_WARNINGS",
                $"Accepted {savedAtrFrames}/{retainedAtrScienceFrames + reusedAtrProbeFrames} retained science FITS, including {reusedAtrProbeFrames} final probes; accepted integration {acceptedAtrExposureSeconds:G6}s. {atrWarningFrames} frame(s) have low-signal warnings; synchronized photometry was {(port.PhotometryAvailable ? "available" : "unavailable")}.",
                scienceMetrics)
            : Passed(
                "ATR_SCIENCE_BLOCK_COMPLETE",
                $"Accepted {savedAtrFrames}/{retainedAtrScienceFrames + reusedAtrProbeFrames} retained science FITS through N.I.N.A. ImageSaveMediator, including {reusedAtrProbeFrames} final probes; accepted integration {acceptedAtrExposureSeconds:G6}s.",
                scienceMetrics);
    }

    public async Task CreditFinalProbeAsync(ObservationContext context, AtrCapture probe, AtrSavedImage saved,
        double selectedExposure, CancellationToken token)
    {
        var quality = ValidateAtrScienceMetrics(probe.Metrics);
        var sha = await port.ComputeFileSha256Async(saved.Path, token).ConfigureAwait(false);
        if (!probeScienceCredit.TryCredit(probe.CaptureToken, probe.Provenance.ObservationRunId,
            context.Plan.ObservationRunId, probe.Metrics.ExposureSeconds, selectedExposure, saved.TemperatureGate,
            quality, saved.Metadata.GetValueOrDefault("fitsProvenanceVerified") == "True",
            savedAtrFrames, configuration.ScienceFrameCount, attemptedAtrFrames, configuration.MaximumScienceAttempts)) return;
        // A second reference, never a copied/renamed/rewritten FITS; keep source identity and all warnings.
        var metadata = new Dictionary<string, string>(saved.Metadata, StringComparer.Ordinal)
        {
            ["scienceReusedFromProbe"] = "True", ["effectiveStageRole"] = "SCIENCE",
            ["sourceCaptureRole"] = "PROBE", ["qualityAccepted"] = "True",
            ["qualityDisposition"] = quality.Disposition.ToString(), ["qualityCode"] = quality.Code,
            ["qualityMessage"] = quality.Message,
            ["scienceAcceptedIndex"] = (savedAtrFrames + 1).ToString(CultureInfo.InvariantCulture),
            ["scienceAttemptNumber"] = (attemptedAtrFrames + 1).ToString(CultureInfo.InvariantCulture),
        };
        port.PublishScienceEvidence(saved.Path, sha, metadata);
        attemptedAtrFrames++;
        savedAtrFrames++;
        reusedAtrProbeFrames++;
        acceptedAtrExposureSeconds += probe.Metrics.ExposureSeconds;
        if (quality.Severity == GateSeverity.Warning) atrWarningFrames++;
        context.Set("atrAttemptedFrames", attemptedAtrFrames);
        context.Set("atrSavedFrames", savedAtrFrames);
        context.Set("atrAcceptedFrames", savedAtrFrames);
        port.PublishFrameCounters();
        port.PublishAcquisitionProgress(context);
        port.Report($"最终 {selectedExposure:G4}s 试拍已计入科学帧 {savedAtrFrames}/{configuration.ScienceFrameCount}；原始文件和试拍来源保留。");
    }

    public void UpdateRemainingScienceDuration(ObservationContext context, double exposureSeconds)
    {
        // Covers every still-available bounded attempt, N.I.N.A. save/metadata
        // latency, guide/slit revalidation margin, and checked finalization.
        var remainingAttempts = Math.Max(1, configuration.MaximumScienceAttempts - attemptedAtrFrames);
        var seconds = remainingAttempts * (exposureSeconds + 45d) + 120d;
        context.RemainingWorstCaseDuration = TimeSpan.FromSeconds(seconds);
    }
}
