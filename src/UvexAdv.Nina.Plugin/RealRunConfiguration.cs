using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;
using UvexAdv.Spectroscopy;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Immutable copy of every Profile value that can change a real physical
/// action, exposure, solve, or quality gate. Stages use only this object. The
/// mutable Profile is re-snapshotted solely to detect drift.
/// </summary>
internal sealed record RealRunConfiguration(
    DateTimeOffset CapturedUtc,
    string ActionConfigurationSha256,
    bool RealModeAuthorized,
    string UvexServiceUrl,
    string QhyServiceUrl,
    string Phd2Host,
    int Phd2Port,
    bool AllowDegradedSupervisedScience,
    string NinaImageFilePattern,
    string ExpectedTelescopeId,
    int ExpectedUvexSlitPosition,
    int ExpectedUvexGratingPositionSteps,
    int ExpectedUvexM2PositionSteps,
    int UvexPositionToleranceSteps,
    AtrRunConfiguration Atr,
    QhyRunConfiguration Qhy,
    G3RunConfiguration G3,
    Phd2RunConfiguration Phd2,
    EnvironmentRunConfiguration Environment,
    SlitRunConfiguration Slit,
    PlateSolverRunConfiguration PlateSolver,
    CommissioningRunBinding Commissioning,
    NightSetupRunBinding NightSetup)
{
    public static RealRunConfiguration Capture(
        UvexPluginSettings settings,
        PlateSolverRunConfiguration plateSolver,
        string ninaImageFilePattern = "")
    {
        var payload = new ActionPayload(
            settings.ObservationUseRealMode,
            settings.ServiceUrl,
            settings.QhyServiceUrl,
            settings.Phd2Host,
            settings.Phd2Port,
            settings.AllowDegradedSupervisedScience,
            ninaImageFilePattern ?? string.Empty,
            settings.ExpectedTelescopeId,
            settings.ExpectedUvexSlitPosition,
            settings.ExpectedUvexGratingPositionSteps,
            settings.ExpectedUvexM2PositionSteps,
            settings.UvexPositionToleranceSteps,
            new AtrRunConfiguration(
                settings.Gain,
                settings.Offset,
                settings.Binning,
                settings.AtrTargetTemperatureC,
                settings.AtrReadoutModeIndex,
                new ImageRoi(settings.RoiX, settings.RoiY, settings.RoiWidth, settings.RoiHeight),
                settings.DispersionAxis,
                settings.ApertureStart,
                settings.ApertureLength,
                Array.AsReadOnly(settings.ParseAtrExposureLadder().ToArray()),
                settings.AtrProbeExposureSeconds,
                settings.AtrScienceFrameCount,
                settings.AtrScienceMaximumAttempts,
                0.001,
                0.001,
                0.002,
                3,
                1.15,
                5,
                3),
            new QhyRunConfiguration(
                Array.AsReadOnly(settings.ParseQhyExposureLadder().ToArray()),
                settings.QhyGain,
                settings.QhyOffset,
                settings.QhyBinning,
                settings.QhyReadoutMode,
                settings.QhyFilterName.Trim(),
                settings.QhyRoiX,
                settings.QhyRoiY,
                settings.QhyRoiWidth,
                settings.QhyRoiHeight,
                double.IsFinite(settings.QhyTargetTemperatureC) ? settings.QhyTargetTemperatureC : null,
                settings.QhyFocalLengthMillimeters,
                settings.QhyPixelSizeMicrometers,
                settings.QhyCenteringToleranceArcseconds,
                settings.QhyPhotometryExposureSeconds,
                settings.QhyPhotometryCadenceSeconds,
                Array.AsReadOnly(settings.ParseQhyParallelFilterSequence().ToArray()),
                new QhyQualityThresholds(
                    settings.QhyMinimumDetectedStars,
                    settings.QhyMaximumSaturatedFraction,
                    settings.QhyMinimumTransparency),
                new QhyCoarseCenteringLimits(
                    settings.QhyCoarseCenteringSchemaVersion,
                    settings.QhyCoarseMaximumSingleCorrectionArcseconds,
                    settings.QhyCoarseMaximumCumulativeCorrectionArcseconds,
                    settings.QhyCoarseMaximumCorrectionAttempts,
                    TimeSpan.FromMinutes(double.IsFinite(settings.QhyCoarseMaximumCenteringMinutes)
                        ? settings.QhyCoarseMaximumCenteringMinutes
                        : 0))),
            new G3RunConfiguration(
                settings.G3ExposureMilliseconds,
                settings.G3GainPercent,
                settings.G3Binning,
                settings.G3SaturationAdu,
                settings.G3FocalLengthMillimeters,
                settings.G3PixelSizeMicrometers,
                settings.G3ExpectedWcsFlipped,
                settings.G3MaximumPlateSolveHintOffsetDegrees,
                new G3PlateSolveExposurePreset(
                    settings.G3PlateSolveExposurePresetSchemaVersion,
                    settings.G3PlateSolveExposurePresetId.Trim(),
                    Array.AsReadOnly(settings.ParseG3PlateSolveExposureLadder().ToArray())),
                new G3WcsCenteringLimits(
                    settings.G3WcsCenteringSchemaVersion,
                    settings.G3WcsMaximumSingleCorrectionArcseconds,
                    settings.G3WcsMaximumRadiusArcseconds,
                    settings.G3WcsMaximumCumulativeMotionArcseconds,
                    settings.G3WcsMaximumCorrectionAttempts,
                    TimeSpan.FromMinutes(double.IsFinite(settings.G3WcsMaximumCenteringMinutes)
                        ? settings.G3WcsMaximumCenteringMinutes
                        : 0),
                    settings.G3TargetInsideFieldMarginPixels),
                settings.G3WcsFreshSolveAuthorizationResidualArcseconds,
                settings.G3MotionWorstCaseActionSeconds,
                settings.G3MotionPostSlewSettleSeconds,
                settings.WideToSlitTransferMode,
                new G3LocalSearchLimits(
                    settings.G3SearchPattern,
                    settings.G3SearchStepArcseconds,
                    settings.G3SearchMaximumRadiusArcseconds,
                    settings.G3SearchMaximumCumulativeArcseconds,
                    settings.G3SearchMaximumAttempts,
                    TimeSpan.FromMinutes(double.IsFinite(settings.G3SearchMaximumMinutes)
                        ? settings.G3SearchMaximumMinutes
                        : 0)),
                 new BrightTargetRunConfiguration(
                    settings.BrightTargetWingCentroidEnabled,
                    settings.BrightTargetMinimumG3ExposureMilliseconds,
                    TimeSpan.FromMinutes(double.IsFinite(settings.BrightTargetMaximumQhyWcsAgeMinutes)
                        ? settings.BrightTargetMaximumQhyWcsAgeMinutes
                        : 0),
                    TimeSpan.FromMinutes(double.IsFinite(settings.BrightTargetMaximumG3FrameAgeMinutes)
                        ? settings.BrightTargetMaximumG3FrameAgeMinutes
                        : 0),
                    settings.BrightTargetMaximumQhyResidualArcseconds,
                    settings.BrightTargetMaximumCatalogMismatchArcseconds,
                    settings.BrightTargetMinimumC11FocusConfidence,
                    new BrightTargetCentroidOptions(
                        settings.BrightTargetMinimumSaturatedCorePixels,
                        settings.BrightTargetMaximumSaturatedCorePixels,
                        settings.BrightTargetWingRadiusPixels,
                        settings.BrightTargetMinimumWingProminenceSigma,
                        settings.BrightTargetMaximumWingLevelFraction,
                        settings.BrightTargetMinimumWingPixels,
                        settings.BrightTargetMinimumWingSignalToNoise,
                        settings.BrightTargetMinimumAngularCoverageFraction,
                        settings.BrightTargetMinimumOpposedWingBalance,
                        settings.BrightTargetMaximumWingCentroidDisagreementPixels,
                         settings.BrightTargetEdgeMarginPixels,
                         settings.BrightTargetNearbySaturatedCoreRadiusPixels,
                         settings.BrightTargetMinimumUniquenessRatio,
                         settings.BrightTargetMaximumSecondaryPeakRatio)),
                 settings.GhostAssistanceMode,
                 new QhyG3FastPairPolicy(
                     settings.QhyG3FastPairSchemaVersion,
                     settings.QhyG3FastPairPolicyId.Trim(),
                     settings.QhyG3FastPairEnabled,
                     settings.QhyG3FastPairExposureSeconds,
                     QhyG3FastPairPolicy.ValidationTimeSpanFromSeconds(settings.QhyG3FastPairMaximumCachedAgeSeconds),
                     QhyG3FastPairPolicy.ValidationTimeSpanFromSeconds(settings.QhyG3FastPairMaximumMidpointSeparationSeconds),
                     QhyG3FastPairPolicy.ValidationTimeSpanFromSeconds(settings.QhyG3FastPairMaximumWallClockSeconds),
                     settings.QhyG3FastPairMaximumMountSpanArcseconds,
                     QhyG3FastPairPolicy.ValidationTimeSpanFromHours(settings.QhyG3FastPairCandidateValidityHours),
                     settings.QhyG3FastPairMaximumCandidateUncertaintyArcseconds),
                 settings.G3CameraRecoveryDelayMilliseconds),
            new Phd2RunConfiguration(
                settings.Phd2ProfileId,
                settings.Phd2ProfileName,
                settings.Phd2CameraName,
                settings.Phd2CameraStableId,
                settings.Phd2MountName,
                settings.Phd2RuntimeCameraName,
                settings.Phd2RuntimeMountName,
                settings.Phd2CalibrationTimestampUtc,
                settings.Phd2ProfileEvidenceSha256,
                settings.Phd2CalibrationMaximumAgeHours,
                settings.Phd2SettlePixels,
                settings.Phd2SettleStableSeconds,
                settings.Phd2SettleTimeoutSeconds),
            new EnvironmentRunConfiguration(
                settings.RequireSafetyMonitor,
                settings.RequireOpenDomeOrRoof,
                settings.RequireWeatherData,
                settings.RequireOpenOpticalCover,
                settings.WeakSupervisionEnabled,
                settings.CloseOpticalCoverOnFinalize,
                settings.CloseOpticalCoverOnFailure,
                settings.OpticalCoverTransitionTimeoutSeconds,
                settings.MountClockMaximumOffsetSeconds,
                settings.MaximumCloudCoverPercent,
                settings.MaximumHumidityPercent,
                settings.MaximumWindSpeedMetersPerSecond),
            new SlitRunConfiguration(
                settings.SlitTargetPredictionTolerancePixels,
                settings.SlitPlacementTolerancePixels),
            plateSolver,
            new CommissioningRunBinding(
                settings.RealModeCommissioned,
                settings.CommissioningPresetPath,
                settings.CommissioningPresetId,
                settings.CommissioningPresetSha256,
                settings.CommissioningHardwareFingerprintSha256,
                settings.SlitGeometryCommissioned,
                settings.SlitGeometryCalibrationId,
                settings.SlitSeedX,
                settings.SlitSeedY,
                settings.SlitAngleDegrees,
                settings.SlitLengthPixels,
                settings.SlitWidthPixels,
                settings.SlitUncertaintyPixels,
                settings.MountTransformCommissioned,
                settings.MountTransformCalibrationId,
                settings.MountTransformPierSide,
                settings.MountRaArcsecondsPerPixelX,
                settings.MountRaArcsecondsPerPixelY,
                settings.MountDecArcsecondsPerPixelX,
                settings.MountDecArcsecondsPerPixelY,
                settings.MountTransformRmsArcseconds,
                settings.MaximumSingleCorrectionArcseconds,
                settings.MaximumCumulativeCorrectionArcseconds,
                settings.MaximumCorrectionAttempts,
                settings.MaximumAcquisitionMinutes),
            new NightSetupRunBinding(
                settings.NightSetupSnapshotPath,
                settings.NightSetupSnapshotSha256,
                settings.ObservationNightSetupId,
                settings.ObservationExpectedAtrCameraId,
                settings.ObservationExpectedG3ProfileName,
                settings.ObservationExpectedQhyCameraId));
        var json = JsonSerializer.Serialize(payload);
        var sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        return new RealRunConfiguration(
            DateTimeOffset.UtcNow,
            sha256,
            payload.RealModeAuthorized,
            payload.UvexServiceUrl,
            payload.QhyServiceUrl,
            payload.Phd2Host,
            payload.Phd2Port,
            payload.AllowDegradedSupervisedScience,
            payload.NinaImageFilePattern,
            payload.ExpectedTelescopeId,
            payload.ExpectedUvexSlitPosition,
            payload.ExpectedUvexGratingPositionSteps,
            payload.ExpectedUvexM2PositionSteps,
            payload.UvexPositionToleranceSteps,
            payload.Atr,
            payload.Qhy,
            payload.G3,
            payload.Phd2,
            payload.Environment,
            payload.Slit,
            payload.PlateSolver,
            payload.Commissioning,
            payload.NightSetup);
    }

    public bool MatchesCurrentProfile(
        UvexPluginSettings settings,
        PlateSolverRunConfiguration currentPlateSolver,
        out string actualSha256)
        => MatchesCurrentProfile(settings, currentPlateSolver, NinaImageFilePattern, out actualSha256);

    public bool MatchesCurrentProfile(
        UvexPluginSettings settings,
        PlateSolverRunConfiguration currentPlateSolver,
        string currentNinaImageFilePattern,
        out string actualSha256)
    {
        try { actualSha256 = Capture(settings, currentPlateSolver, currentNinaImageFilePattern).ActionConfigurationSha256; }
        catch { actualSha256 = string.Empty; return false; }
        return string.Equals(ActionConfigurationSha256, actualSha256, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ActionPayload(
        bool RealModeAuthorized,
        string UvexServiceUrl,
        string QhyServiceUrl,
        string Phd2Host,
        int Phd2Port,
        bool AllowDegradedSupervisedScience,
        string NinaImageFilePattern,
        string ExpectedTelescopeId,
        int ExpectedUvexSlitPosition,
        int ExpectedUvexGratingPositionSteps,
        int ExpectedUvexM2PositionSteps,
        int UvexPositionToleranceSteps,
        AtrRunConfiguration Atr,
        QhyRunConfiguration Qhy,
        G3RunConfiguration G3,
        Phd2RunConfiguration Phd2,
        EnvironmentRunConfiguration Environment,
        SlitRunConfiguration Slit,
        PlateSolverRunConfiguration PlateSolver,
        CommissioningRunBinding Commissioning,
        NightSetupRunBinding NightSetup);
}

/// <summary>
/// Immutable copy of every N.I.N.A. plate-solver value that can change a WCS
/// decision or mount correction. Secret API keys are represented by SHA-256,
/// never by their plaintext value in manifests or diagnostics.
/// </summary>
internal sealed record PlateSolverRunConfiguration(
    string PrimarySolverSelection,
    string BlindSolverSelection,
    string PrimarySolverImplementation,
    string BlindSolverImplementation,
    double SearchRadiusDegrees,
    int Regions,
    int DownSampleFactor,
    int MaximumObjects,
    bool BlindFailoverEnabled,
    double DetectionThreshold,
    double RotationToleranceDegrees,
    int NumberOfAttempts,
    string PlateSolve2Path,
    string PlateSolve3Path,
    string AstapPath,
    string AllSkyPlateSolverPath,
    string AstrometryUrl,
    string AstrometryApiKeySha256,
    string PinPointApiHost,
    string PinPointApiKeySha256)
{
    public static PlateSolverRunConfiguration Capture(
        IPlateSolveSettings settings,
        IPlateSolver primary,
        IPlateSolver blind) => new(
            settings.PlateSolverType.ToString(),
            settings.BlindSolverType.ToString(),
            primary.GetType().AssemblyQualifiedName ?? primary.GetType().FullName ?? primary.GetType().Name,
            blind.GetType().AssemblyQualifiedName ?? blind.GetType().FullName ?? blind.GetType().Name,
            settings.SearchRadius,
            settings.Regions,
            settings.DownSampleFactor,
            settings.MaxObjects,
            settings.BlindFailoverEnabled,
            settings.Threshold,
            settings.RotationTolerance,
            settings.NumberOfAttempts,
            NormalizePath(settings.PS2Location),
            NormalizePath(settings.PS3Location),
            NormalizePath(settings.ASTAPLocation),
            NormalizePath(settings.AspsLocation),
            settings.AstrometryURL?.Trim() ?? string.Empty,
            SecretHash(settings.AstrometryAPIKey),
            settings.PinPointAllSkyApiHost?.Trim() ?? string.Empty,
            SecretHash(settings.PinPointAllSkyApiKey));

    public static PlateSolverRunConfiguration CaptureCurrent(
        IPlateSolveSettings settings,
        PlateSolverRunConfiguration locked) => new(
            settings.PlateSolverType.ToString(),
            settings.BlindSolverType.ToString(),
            locked.PrimarySolverImplementation,
            locked.BlindSolverImplementation,
            settings.SearchRadius,
            settings.Regions,
            settings.DownSampleFactor,
            settings.MaxObjects,
            settings.BlindFailoverEnabled,
            settings.Threshold,
            settings.RotationTolerance,
            settings.NumberOfAttempts,
            NormalizePath(settings.PS2Location),
            NormalizePath(settings.PS3Location),
            NormalizePath(settings.ASTAPLocation),
            NormalizePath(settings.AspsLocation),
            settings.AstrometryURL?.Trim() ?? string.Empty,
            SecretHash(settings.AstrometryAPIKey),
            settings.PinPointAllSkyApiHost?.Trim() ?? string.Empty,
            SecretHash(settings.PinPointAllSkyApiKey));

    private static string NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try { return Path.GetFullPath(value.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return value.Trim(); }
    }

    private static string SecretHash(string? value) => string.IsNullOrEmpty(value)
        ? string.Empty
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

internal sealed record AtrRunConfiguration(
    int Gain,
    int Offset,
    short Binning,
    double TargetTemperatureC,
    short ReadoutModeIndex,
    ImageRoi Roi,
    DispersionAxis DispersionAxis,
    int ApertureStart,
    int ApertureLength,
    IReadOnlyList<double> ExposureLadderSeconds,
    double ProbeExposureSeconds,
    int ScienceFrameCount,
    int MaximumScienceAttempts,
    double MaximumSaturatedFraction,
    double MaximumTraceSaturatedFraction,
    double MaximumClippedDispersionColumnFraction,
    int MaximumConsecutiveClippedDispersionColumns,
    double MinimumTargetToSkyContrast,
    double MinimumLineSnr,
    double MinimumContinuumSnr);

internal sealed record QhyRunConfiguration(
    IReadOnlyList<double> AcquisitionExposureLadderSeconds,
    int Gain,
    int Offset,
    int Binning,
    int ReadoutMode,
    string FilterName,
    int RoiX,
    int RoiY,
    int RoiWidth,
    int RoiHeight,
    double? TargetTemperatureC,
    double FocalLengthMillimeters,
    double PixelSizeMicrometers,
    double CenteringToleranceArcseconds,
    double PhotometryExposureSeconds,
    double PhotometryCadenceSeconds,
    IReadOnlyList<QhyPhotometryFilterStep> ParallelFilterSequence,
    QhyQualityThresholds QualityThresholds,
    QhyCoarseCenteringLimits CoarseCenteringLimits);

internal sealed record G3RunConfiguration(
    int ExposureMilliseconds,
    int GainPercent,
    int Binning,
    int SaturationAdu,
    double FocalLengthMillimeters,
    double PixelSizeMicrometers,
    bool ExpectedWcsFlipped,
    double MaximumPlateSolveHintOffsetDegrees,
    G3PlateSolveExposurePreset PlateSolveExposurePreset,
    G3WcsCenteringLimits WcsCentering,
    double WcsFreshSolveAuthorizationResidualArcseconds,
    double MotionWorstCaseActionSeconds,
    double MotionPostSlewSettleSeconds,
    WideToSlitTransferMode WideToSlitTransferMode,
    G3LocalSearchLimits Search,
    BrightTargetRunConfiguration? BrightTarget = null,
    GhostAssistanceMode GhostAssistanceMode = GhostAssistanceMode.Skip,
    QhyG3FastPairPolicy? FastSolvePair = null,
    int CameraRecoveryDelayMilliseconds = 3_000)
{
    public BrightTargetRunConfiguration EffectiveBrightTarget => BrightTarget ?? BrightTargetRunConfiguration.Disabled;
    public QhyG3FastPairPolicy EffectiveFastSolvePair => FastSolvePair ?? QhyG3FastPairPolicy.Disabled;
}

internal sealed record BrightTargetRunConfiguration(
    bool Enabled,
    int MinimumG3ExposureMilliseconds,
    TimeSpan MaximumQhyWcsAge,
    TimeSpan MaximumG3FrameAge,
    double MaximumQhyTargetResidualArcseconds,
    double MaximumCatalogCoordinateMismatchArcseconds,
    double MinimumC11FocusConfidence,
    BrightTargetCentroidOptions CentroidOptions)
{
    public static BrightTargetRunConfiguration Disabled { get; } = new(
        false,
        0,
        TimeSpan.Zero,
        TimeSpan.Zero,
        0,
        0,
        0,
        new BrightTargetCentroidOptions());

    public BrightTargetAuthorityOptions AuthorityOptions => new(
        MaximumQhyWcsAge,
        MaximumG3FrameAge,
        MaximumQhyTargetResidualArcseconds,
        MaximumCatalogCoordinateMismatchArcseconds,
        MinimumC11FocusConfidence);

    public IReadOnlyList<string> Validate(int normalG3ExposureMilliseconds)
    {
        if (!Enabled) return Array.Empty<string>();
        var issues = new List<string>();
        if (MinimumG3ExposureMilliseconds <= 0 || MinimumG3ExposureMilliseconds > normalG3ExposureMilliseconds)
            issues.Add("Bright-target minimum G3 exposure must be positive and no longer than the normal G3 exposure.");
        if (MaximumQhyWcsAge <= TimeSpan.Zero) issues.Add("Bright-target maximum QHY WCS age must be positive.");
        if (MaximumG3FrameAge <= TimeSpan.Zero) issues.Add("Bright-target maximum G3 frame age must be positive.");
        if (!double.IsFinite(MaximumQhyTargetResidualArcseconds) || MaximumQhyTargetResidualArcseconds <= 0)
            issues.Add("Bright-target maximum QHY target residual must be positive and finite.");
        if (!double.IsFinite(MaximumCatalogCoordinateMismatchArcseconds) || MaximumCatalogCoordinateMismatchArcseconds <= 0)
            issues.Add("Bright-target catalog-coordinate mismatch must be positive and finite.");
        if (!double.IsFinite(MinimumC11FocusConfidence) || MinimumC11FocusConfidence is <= 0 or > 1)
            issues.Add("Bright-target minimum C11 focus confidence must be in (0, 1].");
        if (CentroidOptions.MinimumSaturatedCorePixels < 1 ||
            CentroidOptions.MaximumSaturatedCorePixels < CentroidOptions.MinimumSaturatedCorePixels)
            issues.Add("Bright-target saturated-core pixel limits are invalid.");
        if (CentroidOptions.WingRadiusPixels < 4 || CentroidOptions.EdgeMarginPixels < CentroidOptions.WingRadiusPixels)
            issues.Add("Bright-target wing radius/edge margin is invalid.");
        if (!Positive(CentroidOptions.MinimumWingProminenceSigma) ||
            !Fraction(CentroidOptions.MaximumWingLevelFraction) ||
            CentroidOptions.MinimumWingPixels < 8 ||
            !Positive(CentroidOptions.MinimumWingSignalToNoise) ||
            !Fraction(CentroidOptions.MinimumAngularCoverageFraction) ||
            !Fraction(CentroidOptions.MinimumOpposedWingBalance) ||
            !Positive(CentroidOptions.MaximumWingCentroidDisagreementPixels) ||
            !Positive(CentroidOptions.NearbySaturatedCoreRadiusPixels) ||
            !double.IsFinite(CentroidOptions.MinimumUniquenessRatio) || CentroidOptions.MinimumUniquenessRatio <= 1 ||
            !Fraction(CentroidOptions.MaximumSecondaryPeakRatio))
            issues.Add("One or more bright-target wing morphology thresholds are invalid.");
        return issues.AsReadOnly();
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
    private static bool Fraction(double value) => double.IsFinite(value) && value > 0 && value < 1;
}

internal sealed record Phd2RunConfiguration(
    int ProfileId,
    string ProfileName,
    string CameraName,
    string CameraStableId,
    string MountName,
    string RuntimeCameraName,
    string RuntimeMountName,
    string CalibrationTimestampUtc,
    string ProfileEvidenceSha256,
    double CalibrationMaximumAgeHours,
    double SettlePixels,
    int SettleStableSeconds,
    int SettleTimeoutSeconds);

internal sealed record EnvironmentRunConfiguration(
    bool RequireSafetyMonitor,
    bool RequireOpenDomeOrRoof,
    bool RequireWeatherData,
    bool RequireOpenOpticalCover,
    bool WeakSupervisionEnabled,
    bool CloseOpticalCoverOnFinalize,
    bool CloseOpticalCoverOnFailure,
    int OpticalCoverTransitionTimeoutSeconds,
    double MountClockMaximumOffsetSeconds,
    double MaximumCloudCoverPercent,
    double MaximumHumidityPercent,
    double MaximumWindSpeedMetersPerSecond);

internal sealed record SlitRunConfiguration(
    double TargetPredictionTolerancePixels,
    double PlacementTolerancePixels);

internal sealed record CommissioningRunBinding(
    bool RealModeCommissioned,
    string PresetPath,
    string PresetId,
    string PresetSha256,
    string HardwareFingerprintSha256,
    bool SlitGeometryCommissioned,
    string SlitGeometryCalibrationId,
    double SlitSeedX,
    double SlitSeedY,
    double SlitAngleDegrees,
    double SlitLengthPixels,
    double SlitWidthPixels,
    double SlitUncertaintyPixels,
    bool MountTransformCommissioned,
    string MountTransformCalibrationId,
    string MountTransformPierSide,
    double MountRaArcsecondsPerPixelX,
    double MountRaArcsecondsPerPixelY,
    double MountDecArcsecondsPerPixelX,
    double MountDecArcsecondsPerPixelY,
    double MountTransformRmsArcseconds,
    double MaximumSingleCorrectionArcseconds,
    double MaximumCumulativeCorrectionArcseconds,
    int MaximumCorrectionAttempts,
    double MaximumAcquisitionMinutes);

internal sealed record NightSetupRunBinding(
    string SnapshotPath,
    string SnapshotSha256,
    string NightSetupId,
    string AtrStableId,
    string Phd2ProfileBinding,
    string QhyStableId);
