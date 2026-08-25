using System.Text.Json.Serialization;

namespace UvexAdv.Qhy.Core;

public static class QhyControlProtocol
{
    public const string OwnerTokenHeaderName = "X-QHY-Owner-Token";
    public const string LeaseExpiresUtcHeaderName = "X-QHY-Lease-Expires-Utc";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QhyJobKind
{
    Acquisition,
    Photometry,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QhyJobState
{
    Queued,
    Running,
    Pausing,
    Paused,
    PausedNeedsAttention,
    Cancelling,
    Cancelled,
    Completed,
    Faulted,
    TakenOver,
}

public sealed record QhyCameraIdentity(
    string StableId,
    string Model,
    string Adapter,
    string? SdkVersion = null,
    string? DriverVersion = null,
    string? FirmwareVersion = null);

public sealed record QhyFilterWheelStatus(
    bool Configured,
    bool PositionKnown,
    int? Position,
    string? FilterName,
    string? Error,
    DateTimeOffset TimestampUtc);

public sealed record QhyCameraStatus(
    bool Connected,
    QhyCameraIdentity? Identity,
    double? TemperatureC,
    double? CoolerPowerPercent,
    string? LastError,
    DateTimeOffset TimestampUtc,
    QhyFilterWheelStatus? FilterWheel = null);

public sealed record QhyFrameSettings(
    double ExposureSeconds,
    int Gain,
    int Offset,
    int BinningX = 1,
    int BinningY = 1,
    int RoiX = 0,
    int RoiY = 0,
    int RoiWidth = 0,
    int RoiHeight = 0,
    int ReadoutMode = 1,
    int BitDepth = 16,
    int UsbTraffic = 0,
    string FilterName = "R",
    double? TargetTemperatureC = null);

public sealed record QhyFrame(
    int Width,
    int Height,
    ushort[] Pixels,
    DateTimeOffset ExposureStartedUtc,
    DateTimeOffset ExposureEndedUtc,
    QhyFrameSettings Settings,
    QhyCameraIdentity Identity)
{
    public DateTimeOffset MidpointUtc => ExposureStartedUtc + TimeSpan.FromSeconds(Settings.ExposureSeconds / 2);
}

public sealed record QhyQualityThresholds(
    int MinimumDetectedStars = 0,
    double MaximumSaturatedFraction = 0.002,
    double MinimumTransparency = 0,
    double SaturationAdu = 65_520,
    double DetectionSigma = 5.0);

public sealed record QhyFrameMetrics(
    double MinimumAdu,
    double MaximumAdu,
    double MeanAdu,
    double MedianAdu,
    double BackgroundSigmaAdu,
    double P90Adu,
    double P99Adu,
    double P999Adu,
    double ZeroFraction,
    double SaturatedFraction,
    int DetectedStars,
    double? MedianFwhmPixels,
    double? MedianEllipticity,
    double? MedianStarFlux,
    double? Transparency,
    IReadOnlyList<string> QualityFlags);

public sealed record QhyFrameRecord(
    Guid FrameId,
    int SequenceNumber,
    string Role,
    string FitsPath,
    string PreviewPath,
    string Sha256,
    DateTimeOffset ExposureStartedUtc,
    DateTimeOffset ExposureMidpointUtc,
    DateTimeOffset ExposureEndedUtc,
    QhyFrameSettings Settings,
    QhyFrameMetrics Metrics);

public sealed record QhyJobEvent(
    DateTimeOffset TimestampUtc,
    string Kind,
    string Message);

public sealed record QhyJobSnapshot(
    Guid Id,
    string ObservationRunId,
    QhyJobKind Kind,
    QhyJobState State,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    string RequestedTarget,
    string ExpectedCameraStableId,
    string? Error,
    string? AttentionReason,
    IReadOnlyList<QhyFrameRecord> Frames,
    IReadOnlyList<QhyJobEvent> Events,
    string ManifestPath,
    Guid? AcceptedFrameId = null,
    long Revision = 0,
    long TotalFrameCount = 0,
    long TotalAcceptedFrameCount = 0,
    string? FrameIndexPath = null,
    string? ClientRequestId = null,
    string? ClientRequestFingerprint = null,
    double? TargetRightAscensionDegrees = null,
    double? TargetDeclinationDegrees = null,
    string CoordinateEpoch = "ICRS",
    // Compatibility shim for older clients. Control credentials are never placed
    // in a public snapshot or persisted manifest; new clients use the owner-token
    // response header returned only by the idempotent start POST.
    [property: JsonIgnore] Guid? ControlLeaseId = null,
    DateTimeOffset? LeaseExpiresUtc = null,
    int ControlLeaseSeconds = 120,
    Guid? LastEvaluatedFrameId = null,
    bool? LastFramePassedQualityGate = null);

public sealed record AcquisitionJobRequest(
    string ObservationRunId,
    string RequestedTarget,
    IReadOnlyList<double> ExposureLadderSeconds,
    int Gain,
    int Offset,
    int MaximumAttempts = 4,
    int BinningX = 1,
    int BinningY = 1,
    int ReadoutMode = 1,
    string FilterName = "R",
    double? TargetTemperatureC = null,
    QhyQualityThresholds? QualityThresholds = null,
    int RoiX = 0,
    int RoiY = 0,
    int RoiWidth = 0,
    int RoiHeight = 0,
    int BitDepth = 16,
    int UsbTraffic = 0,
    string? ClientRequestId = null,
    double? TargetRightAscensionDegrees = null,
    double? TargetDeclinationDegrees = null,
    string CoordinateEpoch = "ICRS",
    int ControlLeaseSeconds = 120);

public sealed record PhotometryJobRequest(
    string ObservationRunId,
    string RequestedTarget,
    double ExposureSeconds,
    int Gain,
    int Offset,
    int FrameCount,
    double CadenceSeconds,
    int BinningX = 1,
    int BinningY = 1,
    int ReadoutMode = 1,
    string FilterName = "R",
    double? TargetTemperatureC = null,
    bool PauseOnQualityFailure = true,
    QhyQualityThresholds? QualityThresholds = null,
    int RoiX = 0,
    int RoiY = 0,
    int RoiWidth = 0,
    int RoiHeight = 0,
    int BitDepth = 16,
    int UsbTraffic = 0,
    string? ClientRequestId = null,
    double? TargetRightAscensionDegrees = null,
    double? TargetDeclinationDegrees = null,
    string CoordinateEpoch = "ICRS",
    int ControlLeaseSeconds = 120,
    IReadOnlyList<QhyPhotometryFilterStep>? FilterSequence = null);

/// <summary>
/// One element of a repeating QHY photometry/imaging cycle.  An empty request
/// sequence preserves the legacy single FilterName/ExposureSeconds behavior.
/// </summary>
public sealed record QhyPhotometryFilterStep(string FilterName, double ExposureSeconds);

public sealed record OperatorTakeoverRequest(bool Confirmed, string Operator, string Reason);

public sealed record QhyFilterSelectionRequest(string FilterName);

public sealed record QhyOwnerControlRequest(string OwnerToken, string Actor = "automation");

public sealed record QhyResumeRequest(string OwnerToken, int? LeaseSeconds = null, string Actor = "automation");

public sealed record QhyLeaseRenewalRequest(string OwnerToken, int? LeaseSeconds = null, string Actor = "automation")
{
    // Source-compatibility only. A legacy GUID obtained from an old public
    // snapshot is not a valid owner token under the hardened protocol.
    public QhyLeaseRenewalRequest(Guid legacyControlLeaseId, int? leaseSeconds = null)
        : this(legacyControlLeaseId.ToString("D"), leaseSeconds, "legacy-client")
    {
    }
}

public sealed record QhyJobControlResponse(
    QhyJobSnapshot Job,
    string OwnerToken,
    DateTimeOffset LeaseExpiresUtc,
    int LeaseSeconds);

public sealed record QhyPreview(
    Guid JobId,
    Guid FrameId,
    int Width,
    int Height,
    double DisplayMinimumAdu,
    double DisplayMaximumAdu,
    byte[] PngBytes,
    DateTimeOffset TimestampUtc);

public sealed record QhyCoordinatorOptions
{
    public string ExpectedStableId { get; init; } = string.Empty;
    public string ExpectedModel { get; init; } = "QHYminiCam8M";
    public string DataRoot { get; init; } = string.Empty;
    public QhyQualityThresholds DefaultQualityThresholds { get; init; } = new();
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
