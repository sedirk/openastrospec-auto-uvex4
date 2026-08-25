using System.IO;
using NINA.Profile;
using NINA.Profile.Interfaces;
using UvexAdv.Observatory;
using UvexAdv.Phd2;
using UvexAdv.Qhy.Core;
using UvexAdv.Spectroscopy;

namespace UvexAdv.Nina.Plugin;

internal sealed class UvexPluginSettings
{
    public static readonly Guid PluginGuid = Guid.Parse("A4183531-55BD-4FD0-B04A-97ED7EDC15DA");
    public const int Atr585mDefaultOffset = 256;
    public const int G3M2210mDefaultSaturationAdu = 4095;
    private readonly IProfileService profileService;
    private readonly IPluginOptionsAccessor values;

    public UvexPluginSettings(IProfileService profileService)
        : this(profileService, new PluginOptionsAccessor(profileService, PluginGuid))
    {
    }

    internal UvexPluginSettings(IProfileService profileService, IPluginOptionsAccessor values)
    {
        this.profileService = profileService;
        this.values = values;
    }

    public string ServiceUrl { get => GetString(nameof(ServiceUrl), "http://127.0.0.1:47844"); set => values.SetValueString(nameof(ServiceUrl), value); }
    public string ExpectedCameraName { get => GetString(nameof(ExpectedCameraName), "ATR585M"); set => values.SetValueString(nameof(ExpectedCameraName), value); }
    public string BoundCameraId { get => GetString(nameof(BoundCameraId), string.Empty); set => values.SetValueString(nameof(BoundCameraId), value); }
    // Retained only so older profiles remain readable.  New code uses the two
    // independently commissioned motion authorities below; a single switch must
    // never imply that both M2 focus and grating response were commissioned.
    public bool Commissioned { get => values.GetValueBoolean(nameof(Commissioned), false); set => values.SetValueBoolean(nameof(Commissioned), value); }
    public bool SpectralAutofocusCommissioned { get => values.GetValueBoolean(nameof(SpectralAutofocusCommissioned), false); set => values.SetValueBoolean(nameof(SpectralAutofocusCommissioned), value); }
    public bool WavelengthLockCommissioned { get => values.GetValueBoolean(nameof(WavelengthLockCommissioned), false); set => values.SetValueBoolean(nameof(WavelengthLockCommissioned), value); }
    public double ExposureSeconds { get => values.GetValueDouble(nameof(ExposureSeconds), 5); set => values.SetValueDouble(nameof(ExposureSeconds), value); }
    public int Gain { get => values.GetValueInt32(nameof(Gain), -1); set => values.SetValueInt32(nameof(Gain), value); }
    public int Offset { get => values.GetValueInt32(nameof(Offset), Atr585mDefaultOffset); set => values.SetValueInt32(nameof(Offset), value); }
    public short Binning { get => values.GetValueInt16(nameof(Binning), 1); set => values.SetValueInt16(nameof(Binning), value); }
    public int RoiX { get => values.GetValueInt32(nameof(RoiX), 0); set => values.SetValueInt32(nameof(RoiX), value); }
    public int RoiY { get => values.GetValueInt32(nameof(RoiY), 0); set => values.SetValueInt32(nameof(RoiY), value); }
    public int RoiWidth { get => values.GetValueInt32(nameof(RoiWidth), 3840); set => values.SetValueInt32(nameof(RoiWidth), value); }
    public int RoiHeight { get => values.GetValueInt32(nameof(RoiHeight), 2160); set => values.SetValueInt32(nameof(RoiHeight), value); }
    public int ApertureStart { get => values.GetValueInt32(nameof(ApertureStart), 0); set => values.SetValueInt32(nameof(ApertureStart), value); }
    public int ApertureLength { get => values.GetValueInt32(nameof(ApertureLength), 2160); set => values.SetValueInt32(nameof(ApertureLength), value); }
    public DispersionAxis DispersionAxis { get => values.GetValueEnum(nameof(DispersionAxis), DispersionAxis.Horizontal); set => values.SetValueEnum(nameof(DispersionAxis), value); }
    public bool AutoRepairAtr585mSdkWrap { get => values.GetValueBoolean(nameof(AutoRepairAtr585mSdkWrap), true); set => values.SetValueBoolean(nameof(AutoRepairAtr585mSdkWrap), value); }
    public int SdkWrapShiftPixels { get => values.GetValueInt32(nameof(SdkWrapShiftPixels), 64); set => values.SetValueInt32(nameof(SdkWrapShiftPixels), value); }
    public double SdkWrapSeamSigma { get => values.GetValueDouble(nameof(SdkWrapSeamSigma), 4); set => values.SetValueDouble(nameof(SdkWrapSeamSigma), value); }
    public string FocusLinePixelsCsv { get => GetString(nameof(FocusLinePixelsCsv), string.Empty); set => values.SetValueString(nameof(FocusLinePixelsCsv), value); }
    public int FocusStepSize { get => values.GetValueInt32(nameof(FocusStepSize), 50); set => values.SetValueInt32(nameof(FocusStepSize), value); }
    public int FocusMinimum { get => values.GetValueInt32(nameof(FocusMinimum), -20_000); set => values.SetValueInt32(nameof(FocusMinimum), value); }
    public int FocusMaximum { get => values.GetValueInt32(nameof(FocusMaximum), 20_000); set => values.SetValueInt32(nameof(FocusMaximum), value); }
    public int FocusBacklash { get => values.GetValueInt32(nameof(FocusBacklash), 0); set => values.SetValueInt32(nameof(FocusBacklash), value); }
    public int ManualM2StepSize { get => values.GetValueInt32(nameof(ManualM2StepSize), 50); set => values.SetValueInt32(nameof(ManualM2StepSize), value); }
    public string ManualUvexSelectedDevice { get => GetString(nameof(ManualUvexSelectedDevice), "UVEX4 / COM5"); set => values.SetValueString(nameof(ManualUvexSelectedDevice), value); }
    public double WavelengthReferencePixel { get => values.GetValueDouble(nameof(WavelengthReferencePixel), double.NaN); set => values.SetValueDouble(nameof(WavelengthReferencePixel), value); }
    public double WavelengthTargetPixel { get => values.GetValueDouble(nameof(WavelengthTargetPixel), double.NaN); set => values.SetValueDouble(nameof(WavelengthTargetPixel), value); }
    public double GratingStepsPerPixel { get => values.GetValueDouble(nameof(GratingStepsPerPixel), 0); set => values.SetValueDouble(nameof(GratingStepsPerPixel), value); }
    public string CalibrationLibraryRoot
    {
        get => GetString(nameof(CalibrationLibraryRoot), Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "UVEX-ADV Calibration Library"));
        set => values.SetValueString(nameof(CalibrationLibraryRoot), value);
    }
    public int CalibrationGain { get => values.GetValueInt32(nameof(CalibrationGain), 100); set => values.SetValueInt32(nameof(CalibrationGain), value); }
    public int CalibrationOffset { get => values.GetValueInt32(nameof(CalibrationOffset), Atr585mDefaultOffset); set => values.SetValueInt32(nameof(CalibrationOffset), value); }
    public short CalibrationBinning { get => values.GetValueInt16(nameof(CalibrationBinning), 1); set => values.SetValueInt16(nameof(CalibrationBinning), value); }
    public double CalibrationTemperatureC { get => values.GetValueDouble(nameof(CalibrationTemperatureC), -10); set => values.SetValueDouble(nameof(CalibrationTemperatureC), value); }
    public double CalibrationTemperatureToleranceC { get => values.GetValueDouble(nameof(CalibrationTemperatureToleranceC), 0.5); set => values.SetValueDouble(nameof(CalibrationTemperatureToleranceC), value); }
    public double BiasExposureSeconds { get => values.GetValueDouble(nameof(BiasExposureSeconds), 0.000276); set => values.SetValueDouble(nameof(BiasExposureSeconds), value); }
    public int CalibrationWarmupFrameCount { get => values.GetValueInt32(nameof(CalibrationWarmupFrameCount), 2); set => values.SetValueInt32(nameof(CalibrationWarmupFrameCount), value); }
    public int BiasFrameCount { get => values.GetValueInt32(nameof(BiasFrameCount), 32); set => values.SetValueInt32(nameof(BiasFrameCount), value); }
    public string DarkExposureSecondsCsv { get => GetString(nameof(DarkExposureSecondsCsv), "300,600"); set => values.SetValueString(nameof(DarkExposureSecondsCsv), value); }
    public int DarkFrameCountEach { get => values.GetValueInt32(nameof(DarkFrameCountEach), 5); set => values.SetValueInt32(nameof(DarkFrameCountEach), value); }
    public bool BuildCalibrationMasters { get => values.GetValueBoolean(nameof(BuildCalibrationMasters), true); set => values.SetValueBoolean(nameof(BuildCalibrationMasters), value); }

    public string ObservationTargetName { get => GetString(nameof(ObservationTargetName), "Deneb / 天津四"); set => values.SetValueString(nameof(ObservationTargetName), value); }
    public string ObservationCatalogId { get => GetString(nameof(ObservationCatalogId), "HIP 102098"); set => values.SetValueString(nameof(ObservationCatalogId), value); }
    public double ObservationRightAscensionDegrees { get => values.GetValueDouble(nameof(ObservationRightAscensionDegrees), 310.35798); set => values.SetValueDouble(nameof(ObservationRightAscensionDegrees), value); }
    public double ObservationDeclinationDegrees { get => values.GetValueDouble(nameof(ObservationDeclinationDegrees), 45.28034); set => values.SetValueDouble(nameof(ObservationDeclinationDegrees), value); }
    public string ObservationCoordinateEpoch { get => GetString(nameof(ObservationCoordinateEpoch), "J2000"); set => values.SetValueString(nameof(ObservationCoordinateEpoch), value); }
    public string ObservationTargetImportSource { get => GetString(nameof(ObservationTargetImportSource), "手工输入"); set => values.SetValueString(nameof(ObservationTargetImportSource), value); }
    public string ObservationTargetImportedUtc { get => GetString(nameof(ObservationTargetImportedUtc), string.Empty); set => values.SetValueString(nameof(ObservationTargetImportedUtc), value); }
    public string ObservationTargetImportDetails { get => GetString(nameof(ObservationTargetImportDetails), "尚未从构图助手或第三方星图导入。当前目标字段可手工编辑。"); set => values.SetValueString(nameof(ObservationTargetImportDetails), value); }
    public double ObservationTargetPositionAngleDegrees { get => values.GetValueDouble(nameof(ObservationTargetPositionAngleDegrees), double.NaN); set => values.SetValueDouble(nameof(ObservationTargetPositionAngleDegrees), value); }
    public TargetObservabilityClass ObservationTargetObservability
    {
        get
        {
            var stored = values.GetValueInt32(nameof(ObservationTargetObservability), (int)TargetObservabilityClass.DirectStellar);
            return Enum.IsDefined(typeof(TargetObservabilityClass), stored)
                ? (TargetObservabilityClass)stored
                : TargetObservabilityClass.DirectStellar;
        }
        set => values.SetValueInt32(nameof(ObservationTargetObservability), (int)value);
    }
    public double ObservationDurationMinutes { get => values.GetValueDouble(nameof(ObservationDurationMinutes), 10); set => values.SetValueDouble(nameof(ObservationDurationMinutes), value); }
    public string ObservationNightSetupId { get => GetString(nameof(ObservationNightSetupId), "SIM-NIGHT-SETUP"); set => values.SetValueString(nameof(ObservationNightSetupId), value); }
    public double ObservatoryLatitudeDegrees { get => values.GetValueDouble(nameof(ObservatoryLatitudeDegrees), profileService.ActiveProfile.AstrometrySettings.Latitude); set => values.SetValueDouble(nameof(ObservatoryLatitudeDegrees), value); }
    public double ObservatoryLongitudeDegreesEast { get => values.GetValueDouble(nameof(ObservatoryLongitudeDegreesEast), profileService.ActiveProfile.AstrometrySettings.Longitude); set => values.SetValueDouble(nameof(ObservatoryLongitudeDegreesEast), value); }
    public double ObservatoryElevationMeters { get => values.GetValueDouble(nameof(ObservatoryElevationMeters), profileService.ActiveProfile.AstrometrySettings.Elevation); set => values.SetValueDouble(nameof(ObservatoryElevationMeters), value); }
    public double HorizonMinimumDegrees { get => values.GetValueDouble(nameof(HorizonMinimumDegrees), 40); set => values.SetValueDouble(nameof(HorizonMinimumDegrees), value); }
    public double HorizonStartMarginDegrees { get => values.GetValueDouble(nameof(HorizonStartMarginDegrees), 5); set => values.SetValueDouble(nameof(HorizonStartMarginDegrees), value); }
    public double HorizonContinueMarginDegrees { get => values.GetValueDouble(nameof(HorizonContinueMarginDegrees), 2); set => values.SetValueDouble(nameof(HorizonContinueMarginDegrees), value); }
    public string ObservationExpectedAtrCameraId { get => GetString(nameof(ObservationExpectedAtrCameraId), "SIM-ATR585M"); set => values.SetValueString(nameof(ObservationExpectedAtrCameraId), value); }
    public string ObservationExpectedG3ProfileName { get => GetString(nameof(ObservationExpectedG3ProfileName), "SIM-PHD2-G3M2210M"); set => values.SetValueString(nameof(ObservationExpectedG3ProfileName), value); }
    public string ObservationExpectedQhyCameraId { get => GetString(nameof(ObservationExpectedQhyCameraId), "SIM-QHYMINICAM8M"); set => values.SetValueString(nameof(ObservationExpectedQhyCameraId), value); }
    public int ObservationSimulationStageMilliseconds { get => values.GetValueInt32(nameof(ObservationSimulationStageMilliseconds), 1200); set => values.SetValueInt32(nameof(ObservationSimulationStageMilliseconds), value); }
    public bool ObservationUseRealMode { get => values.GetValueBoolean(nameof(ObservationUseRealMode), false); set => values.SetValueBoolean(nameof(ObservationUseRealMode), value); }

    // Real target-observation mode is deliberately opt-in. Empty calibration/identity values are
    // interpreted as uncommissioned and cause a NeedsAttention gate instead of guessed motion.
    public bool RealModeCommissioned { get => values.GetValueBoolean(nameof(RealModeCommissioned), false); set => values.SetValueBoolean(nameof(RealModeCommissioned), value); }
    public string CommissioningPresetPath { get => GetString(nameof(CommissioningPresetPath), string.Empty); set => values.SetValueString(nameof(CommissioningPresetPath), value); }
    public string CommissioningPresetId { get => GetString(nameof(CommissioningPresetId), string.Empty); set => values.SetValueString(nameof(CommissioningPresetId), value); }
    public string CommissioningPresetSha256 { get => GetString(nameof(CommissioningPresetSha256), string.Empty); set => values.SetValueString(nameof(CommissioningPresetSha256), value); }
    public string CommissioningHardwareFingerprintSha256 { get => GetString(nameof(CommissioningHardwareFingerprintSha256), string.Empty); set => values.SetValueString(nameof(CommissioningHardwareFingerprintSha256), value); }
    public string NightSetupSnapshotPath { get => GetString(nameof(NightSetupSnapshotPath), string.Empty); set => values.SetValueString(nameof(NightSetupSnapshotPath), value); }
    public string NightSetupSnapshotSha256 { get => GetString(nameof(NightSetupSnapshotSha256), string.Empty); set => values.SetValueString(nameof(NightSetupSnapshotSha256), value); }
    public string ExpectedTelescopeId { get => GetString(nameof(ExpectedTelescopeId), string.Empty); set => values.SetValueString(nameof(ExpectedTelescopeId), value); }
    public double AtrTargetTemperatureC { get => values.GetValueDouble(nameof(AtrTargetTemperatureC), -10); set => values.SetValueDouble(nameof(AtrTargetTemperatureC), value); }
    public short AtrReadoutModeIndex { get => values.GetValueInt16(nameof(AtrReadoutModeIndex), 1); set => values.SetValueInt16(nameof(AtrReadoutModeIndex), value); }
    public string QhyServiceUrl { get => GetString(nameof(QhyServiceUrl), "http://127.0.0.1:47845"); set => values.SetValueString(nameof(QhyServiceUrl), value); }
    public string QhyAcquisitionExposureLadderCsv { get => GetString(nameof(QhyAcquisitionExposureLadderCsv), "0.5,1,2,5"); set => values.SetValueString(nameof(QhyAcquisitionExposureLadderCsv), value); }
    public int QhyGain { get => values.GetValueInt32(nameof(QhyGain), 20); set => values.SetValueInt32(nameof(QhyGain), value); }
    public int QhyOffset { get => values.GetValueInt32(nameof(QhyOffset), 20); set => values.SetValueInt32(nameof(QhyOffset), value); }
    public int QhyBinning { get => values.GetValueInt32(nameof(QhyBinning), 1); set => values.SetValueInt32(nameof(QhyBinning), value); }
    public int QhyReadoutMode { get => values.GetValueInt32(nameof(QhyReadoutMode), 1); set => values.SetValueInt32(nameof(QhyReadoutMode), value); }
    public string QhyFilterName { get => GetString(nameof(QhyFilterName), "R"); set => values.SetValueString(nameof(QhyFilterName), value); }
    public int QhyRoiX { get => values.GetValueInt32(nameof(QhyRoiX), 0); set => values.SetValueInt32(nameof(QhyRoiX), value); }
    public int QhyRoiY { get => values.GetValueInt32(nameof(QhyRoiY), 0); set => values.SetValueInt32(nameof(QhyRoiY), value); }
    public int QhyRoiWidth { get => values.GetValueInt32(nameof(QhyRoiWidth), 0); set => values.SetValueInt32(nameof(QhyRoiWidth), value); }
    public int QhyRoiHeight { get => values.GetValueInt32(nameof(QhyRoiHeight), 0); set => values.SetValueInt32(nameof(QhyRoiHeight), value); }
    public double QhyTargetTemperatureC { get => values.GetValueDouble(nameof(QhyTargetTemperatureC), double.NaN); set => values.SetValueDouble(nameof(QhyTargetTemperatureC), value); }
    public double QhyFocalLengthMillimeters { get => values.GetValueDouble(nameof(QhyFocalLengthMillimeters), 0); set => values.SetValueDouble(nameof(QhyFocalLengthMillimeters), value); }
    public double QhyPixelSizeMicrometers { get => values.GetValueDouble(nameof(QhyPixelSizeMicrometers), 0); set => values.SetValueDouble(nameof(QhyPixelSizeMicrometers), value); }
    public double QhyCenteringToleranceArcseconds { get => values.GetValueDouble(nameof(QhyCenteringToleranceArcseconds), 20); set => values.SetValueDouble(nameof(QhyCenteringToleranceArcseconds), value); }
    public double QhyPhotometryExposureSeconds { get => values.GetValueDouble(nameof(QhyPhotometryExposureSeconds), 5); set => values.SetValueDouble(nameof(QhyPhotometryExposureSeconds), value); }
    public double QhyPhotometryCadenceSeconds { get => values.GetValueDouble(nameof(QhyPhotometryCadenceSeconds), 8); set => values.SetValueDouble(nameof(QhyPhotometryCadenceSeconds), value); }
    public string QhyParallelFilterSequenceCsv { get => GetString(nameof(QhyParallelFilterSequenceCsv), string.Empty); set => values.SetValueString(nameof(QhyParallelFilterSequenceCsv), value); }
    public int QhyMinimumDetectedStars { get => values.GetValueInt32(nameof(QhyMinimumDetectedStars), 0); set => values.SetValueInt32(nameof(QhyMinimumDetectedStars), value); }
    public double QhyMinimumTransparency { get => values.GetValueDouble(nameof(QhyMinimumTransparency), 0); set => values.SetValueDouble(nameof(QhyMinimumTransparency), value); }
    public double QhyMaximumSaturatedFraction { get => values.GetValueDouble(nameof(QhyMaximumSaturatedFraction), 0.002); set => values.SetValueDouble(nameof(QhyMaximumSaturatedFraction), value); }

    public string Phd2Host { get => GetString(nameof(Phd2Host), "127.0.0.1"); set => values.SetValueString(nameof(Phd2Host), value); }
    public int Phd2Port { get => values.GetValueInt32(nameof(Phd2Port), 4400); set => values.SetValueInt32(nameof(Phd2Port), value); }
    public bool AllowDegradedSupervisedScience { get => values.GetValueBoolean(nameof(AllowDegradedSupervisedScience), false); set => values.SetValueBoolean(nameof(AllowDegradedSupervisedScience), value); }
    public int Phd2ProfileId { get => values.GetValueInt32(nameof(Phd2ProfileId), -1); set => values.SetValueInt32(nameof(Phd2ProfileId), value); }
    public string Phd2ProfileName { get => GetString(nameof(Phd2ProfileName), string.Empty); set => values.SetValueString(nameof(Phd2ProfileName), value); }
    public string Phd2CameraName { get => GetString(nameof(Phd2CameraName), string.Empty); set => values.SetValueString(nameof(Phd2CameraName), value); }
    public string Phd2CameraStableId { get => GetString(nameof(Phd2CameraStableId), string.Empty); set => values.SetValueString(nameof(Phd2CameraStableId), value); }
    public string Phd2MountName { get => GetString(nameof(Phd2MountName), string.Empty); set => values.SetValueString(nameof(Phd2MountName), value); }
    public string Phd2RuntimeCameraName { get => GetString(nameof(Phd2RuntimeCameraName), Phd2RuntimeEquipmentConventions.G3CameraName); set => values.SetValueString(nameof(Phd2RuntimeCameraName), value); }
    public string Phd2RuntimeMountName { get => GetString(nameof(Phd2RuntimeMountName), Phd2RuntimeEquipmentConventions.OnStepMountName); set => values.SetValueString(nameof(Phd2RuntimeMountName), value); }
    public string Phd2CalibrationTimestampUtc { get => GetString(nameof(Phd2CalibrationTimestampUtc), string.Empty); set => values.SetValueString(nameof(Phd2CalibrationTimestampUtc), value); }
    public string Phd2ProfileEvidenceSha256 { get => GetString(nameof(Phd2ProfileEvidenceSha256), string.Empty); set => values.SetValueString(nameof(Phd2ProfileEvidenceSha256), value); }
    public double Phd2CalibrationMaximumAgeHours { get => values.GetValueDouble(nameof(Phd2CalibrationMaximumAgeHours), 24 * 30); set => values.SetValueDouble(nameof(Phd2CalibrationMaximumAgeHours), value); }
    public int G3ExposureMilliseconds { get => values.GetValueInt32(nameof(G3ExposureMilliseconds), 10_000); set => values.SetValueInt32(nameof(G3ExposureMilliseconds), value); }
    public int G3GainPercent { get => values.GetValueInt32(nameof(G3GainPercent), 100); set => values.SetValueInt32(nameof(G3GainPercent), value); }
    public int G3CameraRecoveryDelayMilliseconds { get => values.GetValueInt32(nameof(G3CameraRecoveryDelayMilliseconds), 3_000); set => values.SetValueInt32(nameof(G3CameraRecoveryDelayMilliseconds), value); }
    public int G3Binning { get => values.GetValueInt32(nameof(G3Binning), 1); set => values.SetValueInt32(nameof(G3Binning), value); }
    public int G3SaturationAdu { get => values.GetValueInt32(nameof(G3SaturationAdu), G3M2210mDefaultSaturationAdu); set => values.SetValueInt32(nameof(G3SaturationAdu), value); }
    public double G3FocalLengthMillimeters { get => values.GetValueDouble(nameof(G3FocalLengthMillimeters), 0); set => values.SetValueDouble(nameof(G3FocalLengthMillimeters), value); }
    public double G3PixelSizeMicrometers { get => values.GetValueDouble(nameof(G3PixelSizeMicrometers), 0); set => values.SetValueDouble(nameof(G3PixelSizeMicrometers), value); }
    public bool G3ExpectedWcsFlipped { get => values.GetValueBoolean(nameof(G3ExpectedWcsFlipped), false); set => values.SetValueBoolean(nameof(G3ExpectedWcsFlipped), value); }
    public double G3MaximumPlateSolveHintOffsetDegrees { get => values.GetValueDouble(nameof(G3MaximumPlateSolveHintOffsetDegrees), 5); set => values.SetValueDouble(nameof(G3MaximumPlateSolveHintOffsetDegrees), value); }
    public int G3PlateSolveExposurePresetSchemaVersion { get => values.GetValueInt32(nameof(G3PlateSolveExposurePresetSchemaVersion), G3PlateSolveExposurePreset.CurrentSchemaVersion); set => values.SetValueInt32(nameof(G3PlateSolveExposurePresetSchemaVersion), value); }
    public string G3PlateSolveExposurePresetId { get => GetString(nameof(G3PlateSolveExposurePresetId), string.Empty); set => values.SetValueString(nameof(G3PlateSolveExposurePresetId), value); }
    public string G3PlateSolveExposureMillisecondsCsv { get => GetString(nameof(G3PlateSolveExposureMillisecondsCsv), string.Empty); set => values.SetValueString(nameof(G3PlateSolveExposureMillisecondsCsv), value); }
    public int G3WcsCenteringSchemaVersion { get => values.GetValueInt32(nameof(G3WcsCenteringSchemaVersion), G3WcsCenteringLimits.CurrentSchemaVersion); set => values.SetValueInt32(nameof(G3WcsCenteringSchemaVersion), value); }
    public double G3WcsMaximumSingleCorrectionArcseconds { get => values.GetValueDouble(nameof(G3WcsMaximumSingleCorrectionArcseconds), 0); set => values.SetValueDouble(nameof(G3WcsMaximumSingleCorrectionArcseconds), value); }
    public double G3WcsMaximumRadiusArcseconds { get => values.GetValueDouble(nameof(G3WcsMaximumRadiusArcseconds), 0); set => values.SetValueDouble(nameof(G3WcsMaximumRadiusArcseconds), value); }
    public double G3WcsMaximumCumulativeMotionArcseconds { get => values.GetValueDouble(nameof(G3WcsMaximumCumulativeMotionArcseconds), 0); set => values.SetValueDouble(nameof(G3WcsMaximumCumulativeMotionArcseconds), value); }
    public int G3WcsMaximumCorrectionAttempts { get => values.GetValueInt32(nameof(G3WcsMaximumCorrectionAttempts), 0); set => values.SetValueInt32(nameof(G3WcsMaximumCorrectionAttempts), value); }
    public double G3WcsMaximumCenteringMinutes { get => values.GetValueDouble(nameof(G3WcsMaximumCenteringMinutes), 0); set => values.SetValueDouble(nameof(G3WcsMaximumCenteringMinutes), value); }
    public double G3TargetInsideFieldMarginPixels { get => values.GetValueDouble(nameof(G3TargetInsideFieldMarginPixels), 0); set => values.SetValueDouble(nameof(G3TargetInsideFieldMarginPixels), value); }
    public double G3MotionWorstCaseActionSeconds { get => values.GetValueDouble(nameof(G3MotionWorstCaseActionSeconds), 0); set => values.SetValueDouble(nameof(G3MotionWorstCaseActionSeconds), value); }
    public double G3MotionPostSlewSettleSeconds { get => values.GetValueDouble(nameof(G3MotionPostSlewSettleSeconds), 0); set => values.SetValueDouble(nameof(G3MotionPostSlewSettleSeconds), value); }
    public bool BrightTargetWingCentroidEnabled { get => values.GetValueBoolean(nameof(BrightTargetWingCentroidEnabled), false); set => values.SetValueBoolean(nameof(BrightTargetWingCentroidEnabled), value); }
    public int BrightTargetMinimumG3ExposureMilliseconds { get => values.GetValueInt32(nameof(BrightTargetMinimumG3ExposureMilliseconds), 0); set => values.SetValueInt32(nameof(BrightTargetMinimumG3ExposureMilliseconds), value); }
    public double BrightTargetMaximumQhyWcsAgeMinutes { get => values.GetValueDouble(nameof(BrightTargetMaximumQhyWcsAgeMinutes), 0); set => values.SetValueDouble(nameof(BrightTargetMaximumQhyWcsAgeMinutes), value); }
    public double BrightTargetMaximumG3FrameAgeMinutes { get => values.GetValueDouble(nameof(BrightTargetMaximumG3FrameAgeMinutes), 0); set => values.SetValueDouble(nameof(BrightTargetMaximumG3FrameAgeMinutes), value); }
    public double BrightTargetMaximumQhyResidualArcseconds { get => values.GetValueDouble(nameof(BrightTargetMaximumQhyResidualArcseconds), 0); set => values.SetValueDouble(nameof(BrightTargetMaximumQhyResidualArcseconds), value); }
    public double BrightTargetMaximumCatalogMismatchArcseconds { get => values.GetValueDouble(nameof(BrightTargetMaximumCatalogMismatchArcseconds), 1); set => values.SetValueDouble(nameof(BrightTargetMaximumCatalogMismatchArcseconds), value); }
    public double BrightTargetMinimumC11FocusConfidence { get => values.GetValueDouble(nameof(BrightTargetMinimumC11FocusConfidence), 0.7); set => values.SetValueDouble(nameof(BrightTargetMinimumC11FocusConfidence), value); }
    public int BrightTargetMinimumSaturatedCorePixels { get => values.GetValueInt32(nameof(BrightTargetMinimumSaturatedCorePixels), 3); set => values.SetValueInt32(nameof(BrightTargetMinimumSaturatedCorePixels), value); }
    public int BrightTargetMaximumSaturatedCorePixels { get => values.GetValueInt32(nameof(BrightTargetMaximumSaturatedCorePixels), 20_000); set => values.SetValueInt32(nameof(BrightTargetMaximumSaturatedCorePixels), value); }
    public int BrightTargetWingRadiusPixels { get => values.GetValueInt32(nameof(BrightTargetWingRadiusPixels), 24); set => values.SetValueInt32(nameof(BrightTargetWingRadiusPixels), value); }
    public double BrightTargetMinimumWingProminenceSigma { get => values.GetValueDouble(nameof(BrightTargetMinimumWingProminenceSigma), 6); set => values.SetValueDouble(nameof(BrightTargetMinimumWingProminenceSigma), value); }
    public double BrightTargetMaximumWingLevelFraction { get => values.GetValueDouble(nameof(BrightTargetMaximumWingLevelFraction), 0.92); set => values.SetValueDouble(nameof(BrightTargetMaximumWingLevelFraction), value); }
    public int BrightTargetMinimumWingPixels { get => values.GetValueInt32(nameof(BrightTargetMinimumWingPixels), 48); set => values.SetValueInt32(nameof(BrightTargetMinimumWingPixels), value); }
    public double BrightTargetMinimumWingSignalToNoise { get => values.GetValueDouble(nameof(BrightTargetMinimumWingSignalToNoise), 20); set => values.SetValueDouble(nameof(BrightTargetMinimumWingSignalToNoise), value); }
    public double BrightTargetMinimumAngularCoverageFraction { get => values.GetValueDouble(nameof(BrightTargetMinimumAngularCoverageFraction), 0.75); set => values.SetValueDouble(nameof(BrightTargetMinimumAngularCoverageFraction), value); }
    public double BrightTargetMinimumOpposedWingBalance { get => values.GetValueDouble(nameof(BrightTargetMinimumOpposedWingBalance), 0.35); set => values.SetValueDouble(nameof(BrightTargetMinimumOpposedWingBalance), value); }
    public double BrightTargetMaximumWingCentroidDisagreementPixels { get => values.GetValueDouble(nameof(BrightTargetMaximumWingCentroidDisagreementPixels), 1.5); set => values.SetValueDouble(nameof(BrightTargetMaximumWingCentroidDisagreementPixels), value); }
    public int BrightTargetEdgeMarginPixels { get => values.GetValueInt32(nameof(BrightTargetEdgeMarginPixels), 30); set => values.SetValueInt32(nameof(BrightTargetEdgeMarginPixels), value); }
    public double BrightTargetNearbySaturatedCoreRadiusPixels { get => values.GetValueDouble(nameof(BrightTargetNearbySaturatedCoreRadiusPixels), 48); set => values.SetValueDouble(nameof(BrightTargetNearbySaturatedCoreRadiusPixels), value); }
    public double BrightTargetMinimumUniquenessRatio { get => values.GetValueDouble(nameof(BrightTargetMinimumUniquenessRatio), 1.8); set => values.SetValueDouble(nameof(BrightTargetMinimumUniquenessRatio), value); }
    public double BrightTargetMaximumSecondaryPeakRatio { get => values.GetValueDouble(nameof(BrightTargetMaximumSecondaryPeakRatio), 0.35); set => values.SetValueDouble(nameof(BrightTargetMaximumSecondaryPeakRatio), value); }
    public GhostAssistanceMode GhostAssistanceMode { get => values.GetValueEnum(nameof(GhostAssistanceMode), GhostAssistanceMode.Skip); set => values.SetValueEnum(nameof(GhostAssistanceMode), value); }
    public WideToSlitTransferMode WideToSlitTransferMode { get => values.GetValueEnum(nameof(WideToSlitTransferMode), WideToSlitTransferMode.Skip); set => values.SetValueEnum(nameof(WideToSlitTransferMode), value); }
    public bool QhyG3FastPairEnabled { get => values.GetValueBoolean(nameof(QhyG3FastPairEnabled), false); set => values.SetValueBoolean(nameof(QhyG3FastPairEnabled), value); }
    public int QhyG3FastPairSchemaVersion { get => values.GetValueInt32(nameof(QhyG3FastPairSchemaVersion), QhyG3FastPairPolicy.CurrentSchemaVersion); set => values.SetValueInt32(nameof(QhyG3FastPairSchemaVersion), value); }
    public string QhyG3FastPairPolicyId { get => GetString(nameof(QhyG3FastPairPolicyId), "qhy-g3-fast-pair-v1"); set => values.SetValueString(nameof(QhyG3FastPairPolicyId), value); }
    public double QhyG3FastPairExposureSeconds { get => values.GetValueDouble(nameof(QhyG3FastPairExposureSeconds), 2); set => values.SetValueDouble(nameof(QhyG3FastPairExposureSeconds), value); }
    public double QhyG3FastPairMaximumCachedAgeSeconds { get => values.GetValueDouble(nameof(QhyG3FastPairMaximumCachedAgeSeconds), 15); set => values.SetValueDouble(nameof(QhyG3FastPairMaximumCachedAgeSeconds), value); }
    public double QhyG3FastPairMaximumMidpointSeparationSeconds { get => values.GetValueDouble(nameof(QhyG3FastPairMaximumMidpointSeparationSeconds), 20); set => values.SetValueDouble(nameof(QhyG3FastPairMaximumMidpointSeparationSeconds), value); }
    public double QhyG3FastPairMaximumWallClockSeconds { get => values.GetValueDouble(nameof(QhyG3FastPairMaximumWallClockSeconds), 30); set => values.SetValueDouble(nameof(QhyG3FastPairMaximumWallClockSeconds), value); }
    public double QhyG3FastPairMaximumMountSpanArcseconds { get => values.GetValueDouble(nameof(QhyG3FastPairMaximumMountSpanArcseconds), 2); set => values.SetValueDouble(nameof(QhyG3FastPairMaximumMountSpanArcseconds), value); }
    public double QhyG3FastPairCandidateValidityHours { get => values.GetValueDouble(nameof(QhyG3FastPairCandidateValidityHours), 24); set => values.SetValueDouble(nameof(QhyG3FastPairCandidateValidityHours), value); }
    public double QhyG3FastPairMaximumCandidateUncertaintyArcseconds { get => values.GetValueDouble(nameof(QhyG3FastPairMaximumCandidateUncertaintyArcseconds), 20); set => values.SetValueDouble(nameof(QhyG3FastPairMaximumCandidateUncertaintyArcseconds), value); }
    public G3LocalSearchPattern G3SearchPattern { get => values.GetValueEnum(nameof(G3SearchPattern), G3LocalSearchPattern.SquareSpiral); set => values.SetValueEnum(nameof(G3SearchPattern), value); }
    public double G3SearchStepArcseconds { get => values.GetValueDouble(nameof(G3SearchStepArcseconds), 0); set => values.SetValueDouble(nameof(G3SearchStepArcseconds), value); }
    public double G3SearchMaximumRadiusArcseconds { get => values.GetValueDouble(nameof(G3SearchMaximumRadiusArcseconds), 0); set => values.SetValueDouble(nameof(G3SearchMaximumRadiusArcseconds), value); }
    public double G3SearchMaximumCumulativeArcseconds { get => values.GetValueDouble(nameof(G3SearchMaximumCumulativeArcseconds), 0); set => values.SetValueDouble(nameof(G3SearchMaximumCumulativeArcseconds), value); }
    public int G3SearchMaximumAttempts { get => values.GetValueInt32(nameof(G3SearchMaximumAttempts), 0); set => values.SetValueInt32(nameof(G3SearchMaximumAttempts), value); }
    public double G3SearchMaximumMinutes { get => values.GetValueDouble(nameof(G3SearchMaximumMinutes), 0); set => values.SetValueDouble(nameof(G3SearchMaximumMinutes), value); }
    public int QhyCoarseCenteringSchemaVersion { get => values.GetValueInt32(nameof(QhyCoarseCenteringSchemaVersion), QhyCoarseCenteringLimits.CurrentSchemaVersion); set => values.SetValueInt32(nameof(QhyCoarseCenteringSchemaVersion), value); }
    public double QhyCoarseMaximumSingleCorrectionArcseconds { get => values.GetValueDouble(nameof(QhyCoarseMaximumSingleCorrectionArcseconds), 0); set => values.SetValueDouble(nameof(QhyCoarseMaximumSingleCorrectionArcseconds), value); }
    public double QhyCoarseMaximumCumulativeCorrectionArcseconds { get => values.GetValueDouble(nameof(QhyCoarseMaximumCumulativeCorrectionArcseconds), 0); set => values.SetValueDouble(nameof(QhyCoarseMaximumCumulativeCorrectionArcseconds), value); }
    public int QhyCoarseMaximumCorrectionAttempts { get => values.GetValueInt32(nameof(QhyCoarseMaximumCorrectionAttempts), 0); set => values.SetValueInt32(nameof(QhyCoarseMaximumCorrectionAttempts), value); }
    public double QhyCoarseMaximumCenteringMinutes { get => values.GetValueDouble(nameof(QhyCoarseMaximumCenteringMinutes), 0); set => values.SetValueDouble(nameof(QhyCoarseMaximumCenteringMinutes), value); }
    public double Phd2SettlePixels { get => values.GetValueDouble(nameof(Phd2SettlePixels), 1.5); set => values.SetValueDouble(nameof(Phd2SettlePixels), value); }
    public int Phd2SettleStableSeconds { get => values.GetValueInt32(nameof(Phd2SettleStableSeconds), 10); set => values.SetValueInt32(nameof(Phd2SettleStableSeconds), value); }
    public int Phd2SettleTimeoutSeconds { get => values.GetValueInt32(nameof(Phd2SettleTimeoutSeconds), 120); set => values.SetValueInt32(nameof(Phd2SettleTimeoutSeconds), value); }

    public bool SlitGeometryCommissioned { get => values.GetValueBoolean(nameof(SlitGeometryCommissioned), false); set => values.SetValueBoolean(nameof(SlitGeometryCommissioned), value); }
    public string SlitGeometryCalibrationId { get => GetString(nameof(SlitGeometryCalibrationId), string.Empty); set => values.SetValueString(nameof(SlitGeometryCalibrationId), value); }
    public double SlitSeedX { get => values.GetValueDouble(nameof(SlitSeedX), 0); set => values.SetValueDouble(nameof(SlitSeedX), value); }
    public double SlitSeedY { get => values.GetValueDouble(nameof(SlitSeedY), 0); set => values.SetValueDouble(nameof(SlitSeedY), value); }
    public double SlitAngleDegrees { get => values.GetValueDouble(nameof(SlitAngleDegrees), 0); set => values.SetValueDouble(nameof(SlitAngleDegrees), value); }
    public double SlitLengthPixels { get => values.GetValueDouble(nameof(SlitLengthPixels), 0); set => values.SetValueDouble(nameof(SlitLengthPixels), value); }
    public double SlitWidthPixels { get => values.GetValueDouble(nameof(SlitWidthPixels), 0); set => values.SetValueDouble(nameof(SlitWidthPixels), value); }
    public double SlitUncertaintyPixels { get => values.GetValueDouble(nameof(SlitUncertaintyPixels), 0); set => values.SetValueDouble(nameof(SlitUncertaintyPixels), value); }
    public double SlitTargetPredictionTolerancePixels { get => values.GetValueDouble(nameof(SlitTargetPredictionTolerancePixels), 40); set => values.SetValueDouble(nameof(SlitTargetPredictionTolerancePixels), value); }
    public double SlitPlacementTolerancePixels { get => values.GetValueDouble(nameof(SlitPlacementTolerancePixels), 2); set => values.SetValueDouble(nameof(SlitPlacementTolerancePixels), value); }

    public bool MountTransformCommissioned { get => values.GetValueBoolean(nameof(MountTransformCommissioned), false); set => values.SetValueBoolean(nameof(MountTransformCommissioned), value); }
    public string MountTransformCalibrationId { get => GetString(nameof(MountTransformCalibrationId), string.Empty); set => values.SetValueString(nameof(MountTransformCalibrationId), value); }
    public string MountTransformPierSide { get => GetString(nameof(MountTransformPierSide), string.Empty); set => values.SetValueString(nameof(MountTransformPierSide), value); }
    public double MountRaArcsecondsPerPixelX { get => values.GetValueDouble(nameof(MountRaArcsecondsPerPixelX), 0); set => values.SetValueDouble(nameof(MountRaArcsecondsPerPixelX), value); }
    public double MountRaArcsecondsPerPixelY { get => values.GetValueDouble(nameof(MountRaArcsecondsPerPixelY), 0); set => values.SetValueDouble(nameof(MountRaArcsecondsPerPixelY), value); }
    public double MountDecArcsecondsPerPixelX { get => values.GetValueDouble(nameof(MountDecArcsecondsPerPixelX), 0); set => values.SetValueDouble(nameof(MountDecArcsecondsPerPixelX), value); }
    public double MountDecArcsecondsPerPixelY { get => values.GetValueDouble(nameof(MountDecArcsecondsPerPixelY), 0); set => values.SetValueDouble(nameof(MountDecArcsecondsPerPixelY), value); }
    public double MountTransformRmsArcseconds { get => values.GetValueDouble(nameof(MountTransformRmsArcseconds), 0); set => values.SetValueDouble(nameof(MountTransformRmsArcseconds), value); }
    public double MaximumSingleCorrectionArcseconds { get => values.GetValueDouble(nameof(MaximumSingleCorrectionArcseconds), 30); set => values.SetValueDouble(nameof(MaximumSingleCorrectionArcseconds), value); }
    public double MaximumCumulativeCorrectionArcseconds { get => values.GetValueDouble(nameof(MaximumCumulativeCorrectionArcseconds), 120); set => values.SetValueDouble(nameof(MaximumCumulativeCorrectionArcseconds), value); }
    public int MaximumCorrectionAttempts { get => values.GetValueInt32(nameof(MaximumCorrectionAttempts), 4); set => values.SetValueInt32(nameof(MaximumCorrectionAttempts), value); }
    public double MaximumAcquisitionMinutes { get => values.GetValueDouble(nameof(MaximumAcquisitionMinutes), 12); set => values.SetValueDouble(nameof(MaximumAcquisitionMinutes), value); }

    public int ExpectedUvexSlitPosition { get => values.GetValueInt32(nameof(ExpectedUvexSlitPosition), -1); set => values.SetValueInt32(nameof(ExpectedUvexSlitPosition), value); }
    public int ExpectedUvexGratingPositionSteps { get => values.GetValueInt32(nameof(ExpectedUvexGratingPositionSteps), int.MinValue); set => values.SetValueInt32(nameof(ExpectedUvexGratingPositionSteps), value); }
    public int ExpectedUvexM2PositionSteps { get => values.GetValueInt32(nameof(ExpectedUvexM2PositionSteps), int.MinValue); set => values.SetValueInt32(nameof(ExpectedUvexM2PositionSteps), value); }
    public int UvexPositionToleranceSteps { get => values.GetValueInt32(nameof(UvexPositionToleranceSteps), 2); set => values.SetValueInt32(nameof(UvexPositionToleranceSteps), value); }

    public string AtrExposureLadderSecondsCsv { get => GetString(nameof(AtrExposureLadderSecondsCsv), "0.01,0.03,0.1,0.3,1,3,10,15,30,60,120,300,600"); set => values.SetValueString(nameof(AtrExposureLadderSecondsCsv), value); }
    public double AtrProbeExposureSeconds { get => values.GetValueDouble(nameof(AtrProbeExposureSeconds), 0.1); set => values.SetValueDouble(nameof(AtrProbeExposureSeconds), value); }
    public int AtrScienceFrameCount { get => values.GetValueInt32(nameof(AtrScienceFrameCount), 3); set => values.SetValueInt32(nameof(AtrScienceFrameCount), value); }
    public int AtrScienceMaximumAttempts { get => values.GetValueInt32(nameof(AtrScienceMaximumAttempts), 6); set => values.SetValueInt32(nameof(AtrScienceMaximumAttempts), value); }

    public bool RequireSafetyMonitor { get => values.GetValueBoolean(nameof(RequireSafetyMonitor), true); set => values.SetValueBoolean(nameof(RequireSafetyMonitor), value); }
    public bool RequireOpenDomeOrRoof { get => values.GetValueBoolean(nameof(RequireOpenDomeOrRoof), true); set => values.SetValueBoolean(nameof(RequireOpenDomeOrRoof), value); }
    public bool RequireWeatherData { get => values.GetValueBoolean(nameof(RequireWeatherData), true); set => values.SetValueBoolean(nameof(RequireWeatherData), value); }
    public bool RequireOpenOpticalCover { get => values.GetValueBoolean(nameof(RequireOpenOpticalCover), true); set => values.SetValueBoolean(nameof(RequireOpenOpticalCover), value); }
    public bool CloseOpticalCoverOnFinalize { get => values.GetValueBoolean(nameof(CloseOpticalCoverOnFinalize), true); set => values.SetValueBoolean(nameof(CloseOpticalCoverOnFinalize), value); }
    public bool CloseOpticalCoverOnFailure { get => values.GetValueBoolean(nameof(CloseOpticalCoverOnFailure), true); set => values.SetValueBoolean(nameof(CloseOpticalCoverOnFailure), value); }
    public int OpticalCoverTransitionTimeoutSeconds { get => values.GetValueInt32(nameof(OpticalCoverTransitionTimeoutSeconds), 45); set => values.SetValueInt32(nameof(OpticalCoverTransitionTimeoutSeconds), value); }
    public double MountClockMaximumOffsetSeconds { get => values.GetValueDouble(nameof(MountClockMaximumOffsetSeconds), 60); set => values.SetValueDouble(nameof(MountClockMaximumOffsetSeconds), value); }
    public double MaximumCloudCoverPercent { get => values.GetValueDouble(nameof(MaximumCloudCoverPercent), 40); set => values.SetValueDouble(nameof(MaximumCloudCoverPercent), value); }
    public double MaximumHumidityPercent { get => values.GetValueDouble(nameof(MaximumHumidityPercent), 90); set => values.SetValueDouble(nameof(MaximumHumidityPercent), value); }
    public double MaximumWindSpeedMetersPerSecond { get => values.GetValueDouble(nameof(MaximumWindSpeedMetersPerSecond), 12); set => values.SetValueDouble(nameof(MaximumWindSpeedMetersPerSecond), value); }

    public ImageRoi Roi => new(RoiX, RoiY, RoiWidth, RoiHeight);

    public IReadOnlyList<double> ParseQhyExposureLadder() => ParsePositiveDoubles(QhyAcquisitionExposureLadderCsv);
    public IReadOnlyList<QhyPhotometryFilterStep> ParseQhyParallelFilterSequence()
    {
        if (string.IsNullOrWhiteSpace(QhyParallelFilterSequenceCsv)) return Array.Empty<QhyPhotometryFilterStep>();
        return QhyParallelFilterSequenceCsv
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Split(':', StringSplitOptions.TrimEntries))
            .Select(parts => parts.Length == 2
                ? new QhyPhotometryFilterStep(
                    parts[0],
                    double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture))
                : throw new FormatException("QHY parallel filter sequence must use Filter:Seconds entries, for example H:60,O:60,S:60."))
            .ToArray();
    }
    public IReadOnlyList<double> ParseAtrExposureLadder() => ParsePositiveDoubles(AtrExposureLadderSecondsCsv);
    public IReadOnlyList<int> ParseG3PlateSolveExposureLadder() => G3PlateSolveExposureMillisecondsCsv
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(item => int.Parse(item, System.Globalization.CultureInfo.InvariantCulture))
        .Where(item => item > 0)
        .Distinct()
        .OrderBy(item => item)
        .ToArray();

    public IReadOnlyList<SpectralLineWindow> ParseFocusLines() => FocusLinePixelsCsv
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
        .Select(pixel => new SpectralLineWindow(pixel))
        .ToArray();

    private string GetString(string name, string defaultValue) => values.GetValueString(name, defaultValue) ?? defaultValue;

    private static IReadOnlyList<double> ParsePositiveDoubles(string value) => value
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(item => double.Parse(item, System.Globalization.CultureInfo.InvariantCulture))
        .Where(item => double.IsFinite(item) && item > 0)
        .Distinct()
        .OrderBy(item => item)
        .ToArray();
}
