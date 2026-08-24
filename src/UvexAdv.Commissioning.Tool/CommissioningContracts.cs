using UvexAdv.Observatory;

namespace UvexAdv.Commissioning.Tool;

public sealed record CommissioningPresetContract(
    int SchemaVersion,
    string PresetId,
    DateTimeOffset CreatedUtc,
    string Provenance,
    string NightSetupId,
    string NightSetupSha256,
    string Phd2ProfileEvidenceSha256,
    string Phd2CalibrationTimestampUtc,
    string TelescopeDeviceId,
    string G3CameraStableId,
    int G3Binning,
    int G3ExposureMilliseconds,
    int G3GainPercent,
    bool G3ExpectedWcsFlipped,
    SlitGeometryContract Slit,
    MountTransformContract? MountTransform,
    MotionLimitContract Motion,
    EnvironmentContract Environment,
    DateTimeOffset? ValidUntilUtc,
    HardwareFingerprintContract? HardwareFingerprint,
    int G3SaturationAdu,
    int FineMotionAuthority,
    Phd2SlitPlacementContract? Phd2SlitPlacement,
    GhostAssistanceContract? GhostAssistance = null,
    SlitWheelIdentityCalibration? SlitWheelIdentity = null)
{
    public const int CurrentSchemaVersion = 5;
}

public sealed record HardwareFingerprintContract(
    string AtrCameraStableId,
    string G3CameraStableId,
    string QhyCameraStableId,
    string TelescopeDeviceId,
    string NightSetupId,
    string NightSetupSha256,
    string Phd2ProfileEvidenceSha256,
    string Sha256);

public sealed record SlitGeometryContract(
    string CalibrationId,
    double AcquisitionX,
    double AcquisitionY,
    double AngleDegrees,
    double LengthPixels,
    double WidthPixels,
    double UncertaintyPixels);

public sealed record MountTransformContract(
    string CalibrationId,
    string PierSide,
    double RaArcsecondsPerPixelX,
    double RaArcsecondsPerPixelY,
    double DecArcsecondsPerPixelX,
    double DecArcsecondsPerPixelY,
    double RmsArcseconds);

public sealed record MotionLimitContract(
    double MaximumSingleCorrectionArcseconds,
    double MaximumCumulativeCorrectionArcseconds,
    int MaximumCorrectionAttempts,
    double MaximumAcquisitionMinutes);

public sealed record EnvironmentContract(
    bool RequireSafetyMonitor,
    bool RequireOpenDomeOrRoof,
    bool RequireWeatherData,
    double MaximumCloudCoverPercent,
    double MaximumHumidityPercent,
    double MaximumWindSpeedMetersPerSecond);

/// <summary>
/// Human-prepared measurement input. It is deliberately not a commissioned
/// preset: the builder must validate referenced slit evidence and fit a
/// non-singular pixel-to-mount transform whenever an independent-motion route
/// is selected, plus complete PHD2 fine-motion evidence and four independent
/// slit-width fingerprints before schema 5 can be emitted.  PHD2-only
/// commissioning deliberately omits the unused independent transform rather
/// than accepting invented calibration samples.
/// </summary>
public sealed record CommissioningMeasurementDefinition(
    int SchemaVersion,
    string PresetId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ValidUntilUtc,
    string Provenance,
    string Phd2CalibrationTimestampUtc,
    string TelescopeDeviceId,
    string G3CameraStableId,
    int G3Binning,
    int G3ExposureMilliseconds,
    int G3GainPercent,
    bool G3ExpectedWcsFlipped,
    SlitMeasurementDefinition Slit,
    MountMeasurementDefinition? MountTransform,
    MotionLimitContract Motion,
    EnvironmentContract Environment,
    int G3SaturationAdu,
    int FineMotionAuthority,
    Phd2SlitPlacementContract? Phd2SlitPlacement,
    int GhostAssistanceMode,
    GhostAssistanceContract? GhostAssistance,
    SlitWheelIdentityMeasurementDefinition? SlitWheelIdentity = null)
{
    public const int CurrentSchemaVersion = 4;
}

public sealed record SlitMeasurementDefinition(
    string CalibrationId,
    DateTimeOffset MeasuredUtc,
    string EvidencePath,
    string EvidenceSha256,
    double AcquisitionX,
    double AcquisitionY,
    double AngleDegrees,
    double LengthPixels,
    double WidthPixels,
    double UncertaintyPixels);

/// <summary>
/// Human-entered paths and measurements for all physical wheel slots. The
/// builder verifies every immutable evidence file and emits a self-hashed
/// SlitWheelIdentityCalibration; it never derives one slot from another.
/// </summary>
public sealed record SlitWheelIdentityMeasurementDefinition(
    string CalibrationId,
    string InstallationEpochId,
    int ImageWidthPixels,
    int ImageHeightPixels,
    double MaximumNormalizedResidual,
    double MinimumRunnerUpSeparationSigma,
    IReadOnlyList<SlitWidthFingerprintMeasurementDefinition> Fingerprints,
    string MeasurementModelId = SlitDarkApertureHdrAnalyzer.MeasurementModelId,
    int ShortExposureMilliseconds = 10,
    int LongExposureMilliseconds = 20,
    double EdgePsfAlphaPixels = 0.625,
    double EdgePsfBeta = 0.43);

public sealed record SlitWidthFingerprintMeasurementDefinition(
    int WheelPosition,
    string SlitLabel,
    double NominalWidthMicrometers,
    double MeasuredWidthPixels,
    double WidthUncertaintyPixels,
    DateTimeOffset MeasuredUtc,
    string EvidencePath,
    string EvidenceSha256,
    int Resolution = (int)SlitDarkApertureResolution.Unresolved,
    double ReflectiveEdgeToApertureCenterPixels = double.NaN,
    double SecondaryEdgeAmplitudeRatio = double.NaN,
    string ShortExposureEvidencePath = "",
    string ShortExposureEvidenceSha256 = "",
    string LongExposureEvidencePath = "",
    string LongExposureEvidenceSha256 = "");

public sealed record MountMeasurementDefinition(
    string CalibrationId,
    DateTimeOffset MeasuredUtc,
    string PierSide,
    double MaximumResidualArcseconds,
    double MaximumConditionEstimate,
    double MaximumSampleMotionArcseconds,
    IReadOnlyList<MountCalibrationSample> Samples);

public sealed record CommissioningBindings(
    string PresetPath,
    string PresetId,
    string PresetSha256,
    string HardwareFingerprintSha256,
    string MeasurementDefinitionPath,
    string MeasurementDefinitionSha256,
    string SlitEvidencePath,
    string SlitEvidenceSha256,
    string NightSetupPath,
    string NightSetupSha256,
    string Phd2EvidencePath,
    string Phd2ProfileEvidenceSha256,
    DateTimeOffset ValidUntilUtc,
    IReadOnlyDictionary<string, object?> NinaProfileValues);

public sealed record Phd2EvidenceBindings(
    string EvidencePath,
    string EvidenceFileSha256,
    string Phd2ProfileEvidenceSha256,
    DateTimeOffset CapturedUtc);

public sealed record NightSetupBindings(
    string NightSetupPath,
    string NightSetupId,
    string NightSetupSha256,
    DateTimeOffset LockedUtc);

public sealed record ValidationSummary(
    string Kind,
    string AbsolutePath,
    string FileSha256,
    bool Valid,
    IReadOnlyList<string> Issues,
    IReadOnlyDictionary<string, string> Bindings);
