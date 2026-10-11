using NINA.Image.Interfaces;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

// The workflows cannot connect a camera, move the mount, rebuild guiding or close
// the roof. The run adapter retains the existing sole-owner capture/save path,
// including its fresh optical, temperature, cancellation and provenance checks.
internal interface IAtrObservationPort
{
    GateResult ValidateAtrCameraIdentity(string expectedDeviceId);
    void ThrowIfGuideOutputFailed();
    bool IsGuidingStable();
    Task<GateResult> WaitForAtrScienceTemperatureAsync(ObservationContext context, CancellationToken token);
    Task<GateResult> EnsureOpticalCoverOpenAsync(ObservationContext context, CancellationToken token);
    GateResult? SequenceStopGate();
    bool MeridianRequested { get; }
    bool RequestNativeMeridianBoundary(ObservationContext context, double exposure);
    StageResult MeridianSegmentBoundary();
    GateResult ValidateEnvironment(ObservationPlan plan);
    Task<GateResult> CheckPhotometryHealthAsync(ObservationContext context, CancellationToken token);
    bool PhotometryUnavailable { get; }
    bool PhotometryAvailable { get; }
    Task RequireImmediatePhysicalActionGatesAsync(ObservationContext context, CancellationToken token);
    Task<AtrCapture> CaptureAtrImageAsync(ObservationContext context, double exposure, string imageType,
        string title, CancellationToken token);
    Task<AtrSavedImage> SaveAtrImageAsync(AtrCapture capture, int attempt, bool accepted,
        GateDisposition disposition, string code, string message, CancellationToken token);
    Task<string> ComputeFileSha256Async(string path, CancellationToken token);
    void PublishScienceEvidence(string path, string sha, IReadOnlyDictionary<string, string> metadata);
    void PublishAtrPreview(IImageData image, string caption, double center, double halfWidth);
    void PublishFrameCounters();
    void PublishAcquisitionProgress(ObservationContext context, string phase = "Ready");
    Task WriteAuditBestEffortAsync(string kind, object payload);
    void Report(string message, double value = -1);
}

internal sealed record AtrSavedImage(GateResult TemperatureGate, string Path, Dictionary<string, string> Metadata);

internal interface IAtrFinalProbeConsumer
{
    Task CreditFinalProbeAsync(ObservationContext context, AtrCapture probe, AtrSavedImage saved,
        double selectedExposure, CancellationToken token);
    void UpdateRemainingScienceDuration(ObservationContext context, double exposureSeconds);
}
