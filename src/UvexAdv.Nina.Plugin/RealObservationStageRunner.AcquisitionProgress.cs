using System.Globalization;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private int reusedAtrProbeFrames => atrScience.ReusedProbeFrames;
    private double acceptedAtrExposureSeconds => atrScience.AcceptedExposureSeconds;

    private void PublishAcquisitionProgress(ObservationContext context, string phase = "Ready", string role = "",
        string? captureId = null, double exposureSeconds = 0) =>
        PublishNativeSequenceProgress(new(context.Plan.ObservationRunId, configuration.Atr.ScienceFrameCount,
            savedAtrFrames, attemptedAtrFrames, configuration.Atr.MaximumScienceAttempts,
            reusedAtrProbeFrames, acceptedAtrExposureSeconds, selectedAtrExposureSeconds,
            phase, role, captureId, phase == "Exposing" ? DateTimeOffset.UtcNow : null, exposureSeconds));

    private void PublishNativeSequenceProgress(ObservationAcquisitionProgress local) =>
        host.PublishAcquisitionProgress(NativeTarget?.Aggregate(local) ?? local);

}
