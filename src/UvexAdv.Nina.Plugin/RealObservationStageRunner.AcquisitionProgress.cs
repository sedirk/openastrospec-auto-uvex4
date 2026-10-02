using System.Globalization;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private readonly AtrProbeScienceCredit probeScienceCredit = new();
    private int reusedAtrProbeFrames;
    private double acceptedAtrExposureSeconds;

    private void PublishAcquisitionProgress(ObservationContext context, string phase = "Ready", string role = "",
        string? captureId = null, double exposureSeconds = 0) =>
        PublishNativeSequenceProgress(new(context.Plan.ObservationRunId, configuration.Atr.ScienceFrameCount,
            savedAtrFrames, attemptedAtrFrames, configuration.Atr.MaximumScienceAttempts,
            reusedAtrProbeFrames, acceptedAtrExposureSeconds, selectedAtrExposureSeconds,
            phase, role, captureId, phase == "Exposing" ? DateTimeOffset.UtcNow : null, exposureSeconds));

    private void PublishNativeSequenceProgress(ObservationAcquisitionProgress local) =>
        host.PublishAcquisitionProgress(NativeMeridian?.Aggregate(local) ?? local);

    private async Task CreditFinalProbeAsync(ObservationContext context, AtrCapture probe, AtrSavedImage saved,
        double selectedExposure, CancellationToken token)
    {
        var quality = ValidateAtrScienceMetrics(probe.Metrics);
        var sha = await ComputeFileSha256Async(saved.Path, token).ConfigureAwait(false);
        if (!probeScienceCredit.TryCredit(probe.CaptureToken, probe.Provenance.ObservationRunId,
            context.Plan.ObservationRunId, probe.Metrics.ExposureSeconds, selectedExposure, saved.TemperatureGate,
            quality, saved.Metadata.GetValueOrDefault("fitsProvenanceVerified") == "True",
            savedAtrFrames, configuration.Atr.ScienceFrameCount, attemptedAtrFrames, configuration.Atr.MaximumScienceAttempts)) return;
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
        host.PublishEvidence("atr-science-fits", saved.Path, sha, metadata);
        attemptedAtrFrames++;
        savedAtrFrames++;
        reusedAtrProbeFrames++;
        acceptedAtrExposureSeconds += probe.Metrics.ExposureSeconds;
        if (quality.Severity == GateSeverity.Warning) atrWarningFrames++;
        context.Set("atrAttemptedFrames", attemptedAtrFrames);
        context.Set("atrSavedFrames", savedAtrFrames);
        context.Set("atrAcceptedFrames", savedAtrFrames);
        PublishFrameCounters();
        PublishAcquisitionProgress(context);
        Report($"最终 {selectedExposure:G4}s 试拍已计入科学帧 {savedAtrFrames}/{configuration.Atr.ScienceFrameCount}；原始文件和试拍来源保留。");
    }

    private sealed record AtrSavedImage(GateResult TemperatureGate, string Path, Dictionary<string, string> Metadata);
}
