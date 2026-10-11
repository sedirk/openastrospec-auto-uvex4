using NINA.Equipment.Model;
using UvexAdv.Observatory;
using UvexAdv.Spectroscopy;
using static UvexAdv.Nina.Plugin.ObservationWorkflowResults;

namespace UvexAdv.Nina.Plugin;

// Owns probe-tier visits and selection only. A selected tier is not a science
// acceptance; only AtrScienceWorkflow may credit an immutable final probe.
internal sealed class AtrProbeWorkflow(AtrRunConfiguration configuration, IAtrObservationPort port)
{
    private double? selectedAtrExposureSeconds;
    private int attemptedAtrProbeFrames;
    private int retainedAtrProbeFrames;
    private int acceptedAtrProbeFrames;
    internal double? SelectedExposureSeconds => selectedAtrExposureSeconds;
    internal int AttemptedFrames => Volatile.Read(ref attemptedAtrProbeFrames);
    internal int RetainedFrames => Volatile.Read(ref retainedAtrProbeFrames);
    internal int AcceptedFrames => Volatile.Read(ref acceptedAtrProbeFrames);
    internal bool RequiresReprobe { get; private set; }

    internal void Invalidate()
    {
        selectedAtrExposureSeconds = null;
        RequiresReprobe = true;
        // Counters belong to this logical run, not the next probe visit.
    }


    internal async Task<StageResult> SelectAsync(ObservationContext context, IAtrFinalProbeConsumer science, CancellationToken cancellationToken)
    {
        var identity = port.ValidateAtrCameraIdentity(context.Plan.ExpectedAtrCameraId);
        if (identity.Disposition != GateDisposition.Passed) return new StageResult(identity);
        port.ThrowIfGuideOutputFailed();
        if (!port.IsGuidingStable()) return Attention(ObservationStage.SelectAtrExposure, "GUIDING_UNSTABLE", "PHD2 is not in the settled-guiding state required for a probe exposure.");
        var ladder = configuration.ExposureLadderSeconds;
        if (ladder.Count == 0) return Attention(ObservationStage.SelectAtrExposure, "ATR_EXPOSURE_LADDER_EMPTY", "ATR exposure ladder contains no positive tier.");
        if (!ladder.Any(value => Math.Abs(value - configuration.ProbeExposureSeconds) < 1e-9))
        {
            return Attention(ObservationStage.SelectAtrExposure, "ATR_PROBE_NOT_IN_LADDER", "ATR probe exposure must be one of the configured tiers.");
        }

        // Everything before this point (slew, solve, slit placement, guiding
        // and QHY photometry) intentionally ran while ATR pre-cooled.  This is
        // the first point at which ATR science readiness is actually required.
        var temperatureGate = await port.WaitForAtrScienceTemperatureAsync(context, cancellationToken).ConfigureAwait(false);
        if (temperatureGate.Disposition != GateDisposition.Passed) return new StageResult(temperatureGate);

        var coverGate = await port.EnsureOpticalCoverOpenAsync(context, cancellationToken).ConfigureAwait(false);
        if (coverGate.Disposition != GateDisposition.Passed) return new StageResult(coverGate);

        // A clipped probe supplies only a lower bound on the true peak. Never
        // infer that a lower tier is safe and immediately open a science frame:
        // capture that tier and run the same trace-local gate again. Rejected
        // higher tiers remain excluded to prevent a 1s -> 3s -> 1s cycle.
        var availableTiers = ladder
            .Where(static value => double.IsFinite(value) && value > 0)
            .Distinct()
            .OrderBy(static value => value)
            .ToList();
        var probeExposure = configuration.ProbeExposureSeconds;
        var visitBudget = new AtrProbeVisitBudget(availableTiers);
        var followsSaturationBackoff = false;
        while (visitBudget.AttemptCount < visitBudget.MaximumAttempts)
        {
            if (port.SequenceStopGate() is { } sequenceStop) return new StageResult(sequenceStop);
            if (port.RequestNativeMeridianBoundary(context, probeExposure)) return port.MeridianSegmentBoundary();
            var backoffReprobesBefore = visitBudget.BackoffReprobes;
            if (!visitBudget.TryBeginProbe(probeExposure, followsSaturationBackoff))
            {
                return Attention(ObservationStage.SelectAtrExposure, "ATR_PROBE_TIER_CYCLE", "ATR exposure selection revisited a tier without a new saturation-backoff reason, or exhausted its single fresh backoff reprobe.");
            }
            followsSaturationBackoff = false;
            if (visitBudget.BackoffReprobes > backoffReprobesBefore)
            {
                port.Report($"较长光谱试拍档过亮；回退到 {probeExposure:G4}s 复拍验证（本次选档最多一次），不复用旧试拍帧。");
                await port.WriteAuditBestEffortAsync("atr-bounded-backoff-reprobe", new
                {
                    context.Plan.ObservationRunId,
                    exposureSeconds = probeExposure,
                    visitBudget.AttemptCount,
                    visitBudget.MaximumAttempts,
                    visitBudget.BackoffReprobes,
                    availableTiers,
                    freshCaptureRequired = true,
                    scienceAuthorized = false,
                }).ConfigureAwait(false);
            }

            await port.RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            attemptedAtrProbeFrames++;
            port.PublishFrameCounters();
            var probe = await port.CaptureAtrImageAsync(
                context,
                probeExposure,
                CaptureSequence.ImageTypes.SNAPSHOT,
                $"UVEX-ADV spectral exposure probe {attemptedAtrProbeFrames}",
                cancellationToken).ConfigureAwait(false);
            port.PublishAtrPreview(probe.Image, ObservationUiPresentation.AtrFrameCaption(false, 0, 0, attemptedAtrProbeFrames,
                probeExposure, probe.Metrics.HighPercentileAdu, probe.Metrics.ClippedDispersionColumnFraction,
                probe.Metrics.LineSnrPerResolutionElement, ObservationStaticTextLocalization.EffectiveCulture),
                probe.Metrics.TraceSpatialCenterPixel, probe.Metrics.TraceSpatialHalfWidthPixels);
            var exposureOptions = new ExposureTierOptions(
                availableTiers,
                MaximumSaturatedFraction: configuration.MaximumSaturatedFraction,
                MaximumTraceSaturatedFraction: configuration.MaximumTraceSaturatedFraction,
                MaximumClippedDispersionColumnFraction: configuration.MaximumClippedDispersionColumnFraction,
                MaximumConsecutiveClippedDispersionColumns: configuration.MaximumConsecutiveClippedDispersionColumns,
                MinimumTargetToSkyContrast: configuration.MinimumTargetToSkyContrast,
                MinimumContinuumSnr: configuration.MinimumContinuumSnr,
                MinimumLineSnr: configuration.MinimumLineSnr);
            var decision = ExposureTierSelector.Select(probe.Metrics, exposureOptions);
            var selectedTierValidatedByThisFrame = decision.Accepted &&
                Math.Abs(decision.SelectedExposureSeconds - probeExposure) < 1e-9;
            var evidenceCode = selectedTierValidatedByThisFrame ? "ATR_PROBE_TIER_VALIDATED" : decision.Code;
            var evidenceReason = selectedTierValidatedByThisFrame
                ? $"Fresh {probeExposure:G4}s probe passed the spectral-trace clipping, contrast and SNR gates."
                : decision.Reason;
            var savedProbe = await port.SaveAtrImageAsync(
                probe,
                attemptedAtrProbeFrames,
                selectedTierValidatedByThisFrame,
                selectedTierValidatedByThisFrame ? GateDisposition.Passed : GateDisposition.Indeterminate,
                evidenceCode,
                evidenceReason,
                cancellationToken).ConfigureAwait(false);
            retainedAtrProbeFrames++;
            if (savedProbe.TemperatureGate.Disposition != GateDisposition.Passed)
            {
                port.PublishFrameCounters();
                return new StageResult(savedProbe.TemperatureGate);
            }
            if (selectedTierValidatedByThisFrame) acceptedAtrProbeFrames++;
            port.PublishFrameCounters();
            if (!decision.Accepted)
            {
                // The two successful unattended on-sky loops did not treat an
                // underexposed first probe as proof that a faint target was
                // absent.  If the current immutable frame is demonstrably not
                // clipped, advance exactly one configured tier and measure
                // again.  Invalid telemetry, unstable guiding and exhausted
                // ladders remain hard stops; this is not permission to infer a
                // science exposure from a low-signal frame.
                if (decision.Code is "TARGET_SKY_CONTRAST_LOW" or "NO_POSITIVE_SPECTRAL_SIGNAL" &&
                    IsSafeSignalLimitedAtrProbe(probe.Metrics, exposureOptions))
                {
                    var nextLonger = availableTiers
                        .Where(value => value > probeExposure)
                        .OrderBy(value => value)
                        .Cast<double?>()
                        .FirstOrDefault();
                    if (nextLonger is { } nextExposure)
                    {
                        await port.WriteAuditBestEffortAsync("atr-signal-limited-advance", new
                        {
                            context.Plan.ObservationRunId,
                            attemptedAtrProbeFrames,
                            fromExposureSeconds = probeExposure,
                            toExposureSeconds = nextExposure,
                            decision.Code,
                            decision.Reason,
                            currentFrameClipped = false,
                            policy = "advance-one-configured-tier-and-remeasure",
                        }).ConfigureAwait(false);
                        port.Report($"ATR {probeExposure:G4}s 探针信号不足但未削顶；沿安全阶梯重拍 {nextExposure:G4}s，不要求人工恢复");
                        probeExposure = nextExposure;
                        continue;
                    }

                    selectedAtrExposureSeconds = probeExposure;
                    RequiresReprobe = false;
                    context.Set("atrSelectedExposureSeconds", probeExposure);
                    await science.CreditFinalProbeAsync(context, probe, savedProbe, probeExposure, cancellationToken).ConfigureAwait(false);
                    science.UpdateRemainingScienceDuration(context, probeExposure);
                    await port.WriteAuditBestEffortAsync("atr-signal-limited-longest-safe-tier", new
                    {
                        context.Plan.ObservationRunId,
                        attemptedAtrProbeFrames,
                        selectedExposureSeconds = probeExposure,
                        decision.Code,
                        decision.Reason,
                        currentFrameClipped = false,
                        policy = "continue-and-stack-at-longest-safe-tier",
                    }).ConfigureAwait(false);
                    return Warning(
                        "ATR_SIGNAL_LIMITED_LONGEST_SAFE_TIER",
                        $"The target remains signal-limited at the longest configured safe, unclipped tier ({probeExposure:G4}s). This is expected for faint targets: continue science acquisition and improve SNR by stacking instead of blocking the run.",
                        decision.Metrics);
                }
                return Attention(ObservationStage.SelectAtrExposure, decision.Code, decision.Reason, decision.Metrics);
            }
            if (selectedTierValidatedByThisFrame)
            {
                selectedAtrExposureSeconds = probeExposure;
                RequiresReprobe = false;
                context.Set("atrSelectedExposureSeconds", probeExposure);
                await science.CreditFinalProbeAsync(context, probe, savedProbe, probeExposure, cancellationToken).ConfigureAwait(false);
                science.UpdateRemainingScienceDuration(context, probeExposure);
                return Passed("ATR_PROBE_TIER_VALIDATED", evidenceReason, decision.Metrics);
            }

            if (decision.Code == "SATURATION_BACKOFF")
            {
                availableTiers.RemoveAll(value => value >= probeExposure);
                followsSaturationBackoff = true;
            }
            if (!availableTiers.Any(value => Math.Abs(value - decision.SelectedExposureSeconds) < 1e-9))
            {
                return Attention(ObservationStage.SelectAtrExposure, "ATR_REPROBE_TIER_UNAVAILABLE", $"Selected reprobe tier {decision.SelectedExposureSeconds:G4}s is no longer available after clipping backoff.", decision.Metrics);
            }
            probeExposure = decision.SelectedExposureSeconds;
        }

        return Attention(ObservationStage.SelectAtrExposure, "ATR_PROBE_ATTEMPT_LIMIT", "ATR exposure ladder was exhausted without a freshly validated spectral-trace probe.");
    }

    private static bool IsSafeSignalLimitedAtrProbe(
        SpectralProbeMetrics metrics,
        ExposureTierOptions options)
    {
        if (!double.IsFinite(metrics.BiasLevelAdu) ||
            !double.IsFinite(metrics.HighPercentileAdu) ||
            !double.IsFinite(metrics.FullScaleAdu) ||
            metrics.FullScaleAdu <= metrics.BiasLevelAdu ||
            !double.IsFinite(metrics.SaturatedFraction) ||
            !double.IsFinite(metrics.TraceSaturatedFraction) ||
            !double.IsFinite(metrics.ClippedDispersionColumnFraction))
        {
            return false;
        }

        var usableRange = metrics.FullScaleAdu - metrics.BiasLevelAdu;
        var observedFraction = Math.Max(0, metrics.HighPercentileAdu - metrics.BiasLevelAdu) / usableRange;
        return metrics.SaturatedFraction <= options.MaximumSaturatedFraction &&
            metrics.TraceSaturatedFraction <= options.MaximumTraceSaturatedFraction &&
            metrics.ClippedDispersionColumnFraction <= options.MaximumClippedDispersionColumnFraction &&
            metrics.LongestClippedDispersionColumnRun <= options.MaximumConsecutiveClippedDispersionColumns &&
            observedFraction < options.MaximumFullScaleFraction;
    }
}
