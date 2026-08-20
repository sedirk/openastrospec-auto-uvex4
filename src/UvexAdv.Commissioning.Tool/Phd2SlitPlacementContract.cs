using System.Security.Cryptography;
using System.Text.Json;
using UvexAdv.Phd2;

namespace UvexAdv.Commissioning.Tool;

/// <summary>
/// JSON contract for the N.I.N.A. plugin's schema-4 PHD2 slit-placement
/// commissioning record. Enum-shaped properties intentionally remain integers:
/// the production loader uses the default System.Text.Json enum representation,
/// while the evidence tool also uses a string-enum converter for unrelated
/// Night Setup records.
/// </summary>
public sealed record Phd2SlitPlacementContract(
    string InstallationEpochId,
    string LockedTopologyFingerprintSha256,
    int CoordinateDomain,
    int SensorWidthPixels,
    int SensorHeightPixels,
    int RoiX,
    int RoiY,
    int RoiWidth,
    int RoiHeight,
    double SensorRotationDegrees,
    int RotationAuthority,
    string PierSide,
    int GuideMode,
    int ExpectedGuidingExposureMilliseconds,
    double MaximumStagePixels,
    double MaximumCumulativePixels,
    int MaximumAttempts,
    double MaximumElapsedSeconds,
    double MaximumStageSeconds,
    double MaximumMeasurementAgeSeconds,
    double MaximumSafetySnapshotAgeSeconds,
    double LockPreconditionTolerancePixels,
    double LockVerificationTolerancePixels,
    double TargetOnSlitTolerancePixels,
    double MaximumAcquisitionResidualPixels,
    double MinimumOffSlitGuideDistancePixels,
    double MinimumOffSlitGuideTargetSeparationPixels,
    double MaximumGuideLockResidualPixels,
    double MaximumDegradedDirectTargetGuideLockResidualPixels,
    double MaximumDirectTargetCentroidSeparationPixels,
    double MinimumFluxMetric,
    double MaximumFluxMetric,
    double MinimumAltitudeDegrees,
    double MinimumAxisRatePixelsPerSecond,
    double MaximumAxisRatePixelsPerSecond,
    double? RaBidirectionalRateRatio,
    double? DecBidirectionalRateRatio,
    bool CalibrationProcessEvidenceComplete,
    bool CalibrationTopologyEvidenceComplete,
    bool CalibrationPierSideEvidenceComplete,
    double FreshLoopFrameTimeoutSeconds,
    double FreshGuidingFrameTimeoutSeconds,
    double TargetSearchRadiusPixels,
    double GuideSearchRadiusPixels,
    double MinimumTargetSignalToNoise,
    double MinimumGuideSignalToNoise,
    double MinimumTargetUniquenessRatio,
    double SlitMaximumPerpendicularSearchPixels,
    double SlitMaximumAngleSearchDegrees,
    double SlitMinimumContrastSigma,
    double MaximumResidualGrowthPixels,
    Phd2CalibrationQualityPolicy? CalibrationQualityPolicy,
    string CalibrationQualityPolicySha256,
    int? OffSlitGuidingExposureMilliseconds,
    int? DirectTargetGuidingExposureMilliseconds)
{
    /// <summary>
    /// Mirrors Phd2SlitPlacementCommissioningPreset.Validate in the production
    /// plugin. Generator-only completeness requirements are applied separately
    /// so this contract cannot drift from the loader's acceptance rules.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(InstallationEpochId)) issues.Add("PHD2 installation epoch id is missing.");
        if (!IsSha256(LockedTopologyFingerprintSha256)) issues.Add("PHD2 locked topology fingerprint is not a SHA-256 value.");
        if (!Enum.IsDefined(typeof(Phd2ImageCoordinateDomain), CoordinateDomain)) issues.Add("PHD2 image coordinate domain is invalid.");
        if (SensorWidthPixels <= 0 || SensorHeightPixels <= 0) issues.Add("PHD2 sensor dimensions are invalid.");
        if (RoiWidth <= 0 || RoiHeight <= 0 || RoiX < 0 || RoiY < 0 ||
            RoiX + RoiWidth > SensorWidthPixels || RoiY + RoiHeight > SensorHeightPixels)
            issues.Add("PHD2 ROI is invalid or outside the commissioned sensor.");
        if (!double.IsFinite(SensorRotationDegrees)) issues.Add("PHD2 sensor rotation is not finite.");
        if (RotationAuthority != (int)Phd2SensorRotationAuthority.QualifiedPhd2Calibration)
            issues.Add("Plate-solve rotation cannot be commissioned as lock-shift motion authority.");
        if (string.IsNullOrWhiteSpace(PierSide) || PierSide.Contains("unknown", StringComparison.OrdinalIgnoreCase))
            issues.Add("PHD2 lock-shift commissioning requires an exact known pier side.");
        if (!Enum.IsDefined(typeof(Phd2SlitGuideMode), GuideMode)) issues.Add("PHD2 slit guide mode is invalid.");
        if (GuideMode == (int)Phd2SlitGuideMode.AutoPreferOffSlitThenDirectTarget)
        {
            if (OffSlitGuidingExposureMilliseconds is not > 0) issues.Add("Auto guide selection requires a commissioned ordinary off-slit guiding exposure.");
            if (DirectTargetGuidingExposureMilliseconds is not > 0) issues.Add("Auto guide selection requires a separately commissioned shortest direct-target exposure.");
        }
        else if (ExpectedGuidingExposureMilliseconds <= 0)
        {
            issues.Add("Expected PHD2 guiding exposure is missing or invalid.");
        }

        Positive(issues, MaximumStagePixels, nameof(MaximumStagePixels));
        Positive(issues, MaximumCumulativePixels, nameof(MaximumCumulativePixels));
        if (MaximumCumulativePixels < MaximumStagePixels) issues.Add("Maximum cumulative lock shift is smaller than one stage.");
        if (MaximumAttempts <= 0) issues.Add("Maximum lock-shift attempts is invalid.");
        Positive(issues, MaximumElapsedSeconds, nameof(MaximumElapsedSeconds));
        Positive(issues, MaximumStageSeconds, nameof(MaximumStageSeconds));
        Positive(issues, MaximumMeasurementAgeSeconds, nameof(MaximumMeasurementAgeSeconds));
        Positive(issues, MaximumSafetySnapshotAgeSeconds, nameof(MaximumSafetySnapshotAgeSeconds));
        NonNegative(issues, LockPreconditionTolerancePixels, nameof(LockPreconditionTolerancePixels));
        NonNegative(issues, LockVerificationTolerancePixels, nameof(LockVerificationTolerancePixels));
        if (LockVerificationTolerancePixels >= MaximumStagePixels) issues.Add("Lock verification tolerance must be smaller than one stage.");
        NonNegative(issues, TargetOnSlitTolerancePixels, nameof(TargetOnSlitTolerancePixels));
        Positive(issues, MaximumAcquisitionResidualPixels, nameof(MaximumAcquisitionResidualPixels));
        if (MaximumAcquisitionResidualPixels <= TargetOnSlitTolerancePixels) issues.Add("Acquisition residual window must exceed target-on-slit tolerance.");
        NonNegative(issues, MinimumOffSlitGuideDistancePixels, nameof(MinimumOffSlitGuideDistancePixels));
        NonNegative(issues, MinimumOffSlitGuideTargetSeparationPixels, nameof(MinimumOffSlitGuideTargetSeparationPixels));
        NonNegative(issues, MaximumGuideLockResidualPixels, nameof(MaximumGuideLockResidualPixels));
        NonNegative(issues, MaximumDegradedDirectTargetGuideLockResidualPixels, nameof(MaximumDegradedDirectTargetGuideLockResidualPixels));
        NonNegative(issues, MaximumDirectTargetCentroidSeparationPixels, nameof(MaximumDirectTargetCentroidSeparationPixels));
        if (!double.IsFinite(MinimumFluxMetric) || !double.IsFinite(MaximumFluxMetric) || MaximumFluxMetric <= MinimumFluxMetric)
            issues.Add("PHD2 target flux envelope is invalid.");
        if (!double.IsFinite(MinimumAltitudeDegrees) || MinimumAltitudeDegrees is < 0 or > 90)
            issues.Add("PHD2 minimum altitude is invalid.");
        Positive(issues, MinimumAxisRatePixelsPerSecond, nameof(MinimumAxisRatePixelsPerSecond));
        Positive(issues, MaximumAxisRatePixelsPerSecond, nameof(MaximumAxisRatePixelsPerSecond));
        if (MaximumAxisRatePixelsPerSecond <= MinimumAxisRatePixelsPerSecond) issues.Add("PHD2 maximum axis rate must exceed the minimum.");
        ValidateOptionalRatio(issues, RaBidirectionalRateRatio, "RA bidirectional rate ratio");
        ValidateOptionalRatio(issues, DecBidirectionalRateRatio, "Dec bidirectional rate ratio");
        Positive(issues, FreshLoopFrameTimeoutSeconds, nameof(FreshLoopFrameTimeoutSeconds));
        Positive(issues, FreshGuidingFrameTimeoutSeconds, nameof(FreshGuidingFrameTimeoutSeconds));
        Positive(issues, TargetSearchRadiusPixels, nameof(TargetSearchRadiusPixels));
        Positive(issues, GuideSearchRadiusPixels, nameof(GuideSearchRadiusPixels));
        Positive(issues, MinimumTargetSignalToNoise, nameof(MinimumTargetSignalToNoise));
        Positive(issues, MinimumGuideSignalToNoise, nameof(MinimumGuideSignalToNoise));
        if (!double.IsFinite(MinimumTargetUniquenessRatio) || MinimumTargetUniquenessRatio <= 1)
            issues.Add("Target uniqueness ratio must be finite and greater than one.");
        Positive(issues, SlitMaximumPerpendicularSearchPixels, nameof(SlitMaximumPerpendicularSearchPixels));
        Positive(issues, SlitMaximumAngleSearchDegrees, nameof(SlitMaximumAngleSearchDegrees));
        Positive(issues, SlitMinimumContrastSigma, nameof(SlitMinimumContrastSigma));
        NonNegative(issues, MaximumResidualGrowthPixels, nameof(MaximumResidualGrowthPixels));
        ValidatePolicy(CalibrationQualityPolicy, CalibrationQualityPolicySha256, issues);
        return issues.AsReadOnly();
    }

    public string ComputeTopologyFingerprintSha256(
        Phd2ProfileBindingSnapshot profileEvidence,
        string cameraStableId,
        int binning)
    {
        ArgumentNullException.ThrowIfNull(profileEvidence);
        if (!Enum.IsDefined(typeof(Phd2ImageCoordinateDomain), CoordinateDomain) ||
            !Enum.IsDefined(typeof(Phd2SensorRotationAuthority), RotationAuthority))
        {
            throw new InvalidDataException("PHD2 topology enum values are invalid.");
        }

        return new Phd2SensorTopology(
            InstallationEpochId,
            profileEvidence.ProfileId,
            profileEvidence.ProfileName,
            Phd2RuntimeEquipmentConventions.G3CameraName,
            cameraStableId,
            Phd2RuntimeEquipmentConventions.OnStepMountName,
            profileEvidence.Sha256,
            SensorWidthPixels,
            SensorHeightPixels,
            binning,
            new Phd2Rectangle(RoiX, RoiY, RoiWidth, RoiHeight),
            (Phd2ImageCoordinateDomain)CoordinateDomain,
            SensorRotationDegrees,
            (Phd2SensorRotationAuthority)RotationAuthority,
            PierSide).ComputeFingerprintSha256();
    }

    public static string ComputePolicySha256(Phd2CalibrationQualityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(policy)));
    }

    private static void ValidatePolicy(
        Phd2CalibrationQualityPolicy? policy,
        string expectedSha256,
        ICollection<string> issues)
    {
        if (policy is null)
        {
            issues.Add("A complete versioned PHD2 calibration-quality policy is required.");
            return;
        }
        if (string.IsNullOrWhiteSpace(policy.PolicyId)) issues.Add("PHD2 calibration-quality policy id is missing.");
        if (policy.ExcellentMaximumAge <= TimeSpan.Zero ||
            policy.QualifiedMaximumAge < policy.ExcellentMaximumAge ||
            policy.DegradedMaximumAge < policy.QualifiedMaximumAge)
            issues.Add("PHD2 calibration-quality age bands are invalid.");
        if (!OrderedNonNegative(
                policy.ExcellentMaximumOrthogonalityErrorDegrees,
                policy.QualifiedMaximumOrthogonalityErrorDegrees,
                policy.DegradedMaximumOrthogonalityErrorDegrees) ||
            policy.DegradedMaximumOrthogonalityErrorDegrees >= 90)
            issues.Add("PHD2 calibration-quality orthogonality bands are invalid.");
        if (!OrderedAtLeastOne(policy.ExcellentMaximumBidirectionalRateRatio, policy.QualifiedMaximumBidirectionalRateRatio, policy.DegradedMaximumBidirectionalRateRatio) ||
            !OrderedAtLeastOne(policy.ExcellentMaximumCrossAxisRateRatio, policy.QualifiedMaximumCrossAxisRateRatio, policy.DegradedMaximumCrossAxisRateRatio))
            issues.Add("PHD2 calibration-quality rate-ratio bands are invalid.");
        if (!OrderedFraction(policy.ExcellentMaximumDroppedFrameFraction, policy.QualifiedMaximumDroppedFrameFraction, policy.DegradedMaximumDroppedFrameFraction))
            issues.Add("PHD2 calibration-quality dropped-frame bands are invalid.");
        if (policy.MaximumSettleEvidenceAge <= TimeSpan.Zero || policy.MaximumResidualEvidenceAge <= TimeSpan.Zero)
            issues.Add("PHD2 calibration-quality evidence ages are invalid.");
        if (!UnitScale(policy.QualifiedMaximumLockShiftScale) || !UnitScale(policy.DegradedMaximumLockShiftScale) ||
            policy.DegradedMaximumLockShiftScale > policy.QualifiedMaximumLockShiftScale ||
            !UnitScale(policy.QualifiedResidualToleranceScale) || !UnitScale(policy.DegradedResidualToleranceScale) ||
            policy.DegradedResidualToleranceScale > policy.QualifiedResidualToleranceScale)
            issues.Add("PHD2 calibration-quality motion/residual scales are invalid.");
        if (policy.RequiredFreshResidualsPerLockShiftStage <= 0)
            issues.Add("PHD2 calibration-quality fresh-residual count is invalid.");
        if (!IsSha256(expectedSha256) ||
            !string.Equals(ComputePolicySha256(policy), expectedSha256, StringComparison.OrdinalIgnoreCase))
            issues.Add("PHD2 calibration-quality policy SHA-256 is missing or does not match all serialized numeric bands.");
    }

    private static bool OrderedNonNegative(double a, double b, double c) =>
        double.IsFinite(a) && double.IsFinite(b) && double.IsFinite(c) && a >= 0 && b >= a && c >= b;

    private static bool OrderedAtLeastOne(double a, double b, double c) =>
        double.IsFinite(a) && double.IsFinite(b) && double.IsFinite(c) && a >= 1 && b >= a && c >= b;

    private static bool OrderedFraction(double a, double b, double c) =>
        double.IsFinite(a) && double.IsFinite(b) && double.IsFinite(c) && a >= 0 && b >= a && c >= b && c <= 1;

    private static bool UnitScale(double value) => double.IsFinite(value) && value is > 0 and <= 1;
    private static bool IsSha256(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);

    private static void Positive(ICollection<string> issues, double value, string label)
    {
        if (!double.IsFinite(value) || value <= 0) issues.Add($"{label} must be positive and finite.");
    }

    private static void NonNegative(ICollection<string> issues, double value, string label)
    {
        if (!double.IsFinite(value) || value < 0) issues.Add($"{label} must be non-negative and finite.");
    }

    private static void ValidateOptionalRatio(ICollection<string> issues, double? value, string label)
    {
        if (value.HasValue && (!double.IsFinite(value.Value) || value.Value < 1)) issues.Add($"{label} is invalid.");
    }
}
