using System.Text.Json;

namespace UvexAdv.Phd2;

/// <summary>
/// Exact names reported by PHD2's JSON-RPC <c>get_current_equipment</c> for the
/// commissioned G3/On-Step runtime.  These are deliberately distinct from the
/// menu/registry names stored in the PHD2 profile evidence.
/// </summary>
public static class Phd2RuntimeEquipmentConventions
{
    public const string G3CameraName = "G3M2210M";
    public const string OnStepMountName = "On-Step (ASCOM)";
}

public enum Phd2AppState
{
    Unknown,
    Stopped,
    Selected,
    Calibrating,
    Guiding,
    LostLock,
    Paused,
    Looping,
}

public enum Phd2ValidationStatus
{
    Valid,
    Invalid,
    Indeterminate,
}

public sealed record Phd2Point(double X, double Y);

public sealed record Phd2Rectangle(int X, int Y, int Width, int Height);

public sealed record Phd2Profile(int Id, string Name);

public sealed record Phd2EquipmentDevice(string Name, bool Connected, string? StableId = null);

public sealed record Phd2Equipment(
    Phd2EquipmentDevice? Camera,
    Phd2EquipmentDevice? Mount,
    Phd2EquipmentDevice? AuxMount,
    Phd2EquipmentDevice? AdaptiveOptics,
    Phd2EquipmentDevice? Rotator);

public sealed record Phd2IdentityRequirement(
    int ProfileId,
    string ProfileName,
    string CameraName,
    string MountName,
    bool RequireConnected = true,
    string? StableCameraId = null);

public sealed record Phd2IdentityValidation(
    Phd2Profile Profile,
    Phd2Equipment Equipment,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> IndeterminateReasons)
{
    public Phd2ValidationStatus Status => Failures.Count > 0
        ? Phd2ValidationStatus.Invalid
        : IndeterminateReasons.Count > 0
            ? Phd2ValidationStatus.Indeterminate
            : Phd2ValidationStatus.Valid;

    public bool IsValid => Status == Phd2ValidationStatus.Valid;
}

public sealed record Phd2CalibrationData(
    bool Calibrated,
    double? RaAngleDegrees,
    double? RaRatePixelsPerSecond,
    string? RaParity,
    double? DecAngleDegrees,
    double? DecRatePixelsPerSecond,
    string? DecParity,
    double? DeclinationDegrees);

public sealed record Phd2CalibrationRequirement(
    int ProfileId,
    string ProfileName,
    DateTimeOffset? CalibrationTimestampUtc,
    TimeSpan MaximumAge,
    double MaximumOrthogonalityErrorDegrees = 15,
    double MinimumAxisRatePixelsPerSecond = 0.001,
    double MaximumAxisRatePixelsPerSecond = 1000,
    bool RequireKnownAge = true);

public sealed record Phd2CalibrationValidation(
    Phd2Profile Profile,
    Phd2CalibrationData Calibration,
    DateTimeOffset EvaluatedUtc,
    TimeSpan? CalibrationAge,
    double? OrthogonalityErrorDegrees,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> IndeterminateReasons)
{
    public Phd2ValidationStatus Status => Failures.Count > 0
        ? Phd2ValidationStatus.Invalid
        : IndeterminateReasons.Count > 0
            ? Phd2ValidationStatus.Indeterminate
            : Phd2ValidationStatus.Valid;

    public bool IsValid => Status == Phd2ValidationStatus.Valid;
}

// CaptureFullFrameAsync retains these settings for caller/manifest
// compatibility when it uses the legacy loop/save route. The explicit native
// single-frame route applies all three through PHD2 capture_single_frame.
public sealed record Phd2SingleFrameRequest(
    int ExposureMs,
    int Binning,
    int GainPercent,
    string DestinationPath);

// UsedLoopSaveFallback distinguishes the legacy loop/stop/save_image route
// from native capture_single_frame. RequestedParametersApplied is true only
// when PHD2 accepted exposure, binning and gain in the atomic request.
public sealed record Phd2SingleFrameResult(
    string Path,
    bool UsedLoopSaveFallback,
    bool RequestedParametersApplied,
    DateTimeOffset CompletedUtc,
    int? VerifiedExposureMilliseconds = null,
    bool AutomaticRetryAllowed = false)
{
    /// <summary>The supported capture path applies ExposureMs through set_exposure before loop.</summary>
    public bool ExposureApplied => true;

    /// <summary>True only for a successful native single-frame request carrying gain/binning.</summary>
    public bool GainAndBinningApplied => RequestedParametersApplied;
}

public sealed record Phd2SettleCriteria(double Pixels, int StableTimeSeconds, int TimeoutSeconds);

public sealed record Phd2SettleProgress(
    double DistancePixels,
    double ElapsedSeconds,
    double StableSeconds,
    bool StarLocked);

public sealed record Phd2SettleResult(
    bool Succeeded,
    string? Error,
    int TotalFrames,
    int DroppedFrames,
    DateTimeOffset CompletedUtc);

public sealed record Phd2StopCaptureResult(
    Phd2AppState InitialState,
    Phd2AppState FinalState,
    bool StopCommandSent,
    bool ConfirmedIdle,
    DateTimeOffset CompletedUtc);

public sealed record Phd2GuideStep(
    long? Frame,
    double? DxPixels,
    double? DyPixels,
    double? Snr,
    double? HfdPixels,
    double? AverageDistancePixels,
    int? ErrorCode);

public sealed record Phd2EventMessage(
    string Name,
    long Sequence,
    DateTimeOffset ReceivedUtc,
    JsonElement Payload);

public sealed record Phd2StateSnapshot(
    bool IsConnected,
    bool AutomationPaused,
    bool Phd2Paused,
    string? PhdVersion,
    Phd2AppState AppState,
    Phd2Profile? Profile,
    Phd2Equipment? Equipment,
    Phd2CalibrationValidation? CalibrationValidation,
    Phd2Point? LockPosition,
    Phd2Point? SelectedStar,
    Phd2SettleProgress? SettleProgress,
    Phd2SettleResult? LastSettle,
    Phd2SingleFrameResult? LastSingleFrame,
    Phd2GuideStep? LastGuideStep,
    string? LastAlert,
    string? LastProtocolError,
    long ConnectionEpoch,
    long GuideEpoch,
    long? PendingSettleOperationId,
    long? PendingSettleConnectionEpoch,
    long? PendingSettleGuideEpoch,
    long? PendingSettleArmedAfterSequence,
    long? PendingSettleBeginSequence,
    bool PendingSettleCommandAccepted,
    bool PendingTakeoverLoopStopAllowed,
    bool PendingLateLoopFrameAllowed,
    bool PendingForceRecalibration,
    long? PendingCalibrationStartSequence,
    long? PendingCalibrationTerminalSequence,
    long? LastSettleOperationId,
    bool LastSettleCommandAccepted,
    long? LastSettleConnectionEpoch,
    long? LastSettleGuideEpoch,
    long EventSequence,
    DateTimeOffset? LastEventUtc)
{
    public bool HasCurrentSuccessfulSettle =>
        IsConnected &&
        !AutomationPaused &&
        !Phd2Paused &&
        AppState == Phd2AppState.Guiding &&
        LastSettle?.Succeeded == true &&
        LastSettleOperationId.HasValue &&
        LastSettleCommandAccepted &&
        LastSettleConnectionEpoch == ConnectionEpoch &&
        LastSettleGuideEpoch == GuideEpoch;

    public static Phd2StateSnapshot Disconnected { get; } = new(
        IsConnected: false,
        AutomationPaused: false,
        Phd2Paused: false,
        PhdVersion: null,
        AppState: Phd2AppState.Unknown,
        Profile: null,
        Equipment: null,
        CalibrationValidation: null,
        LockPosition: null,
        SelectedStar: null,
        SettleProgress: null,
        LastSettle: null,
        LastSingleFrame: null,
        LastGuideStep: null,
        LastAlert: null,
        LastProtocolError: null,
        ConnectionEpoch: 0,
        GuideEpoch: 0,
        PendingSettleOperationId: null,
        PendingSettleConnectionEpoch: null,
        PendingSettleGuideEpoch: null,
        PendingSettleArmedAfterSequence: null,
        PendingSettleBeginSequence: null,
        PendingSettleCommandAccepted: false,
        PendingTakeoverLoopStopAllowed: false,
        PendingLateLoopFrameAllowed: false,
        PendingForceRecalibration: false,
        PendingCalibrationStartSequence: null,
        PendingCalibrationTerminalSequence: null,
        LastSettleOperationId: null,
        LastSettleCommandAccepted: false,
        LastSettleConnectionEpoch: null,
        LastSettleGuideEpoch: null,
        EventSequence: 0,
        LastEventUtc: null);
}
