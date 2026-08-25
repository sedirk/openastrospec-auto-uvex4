using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace UvexAdv.Phd2;

/// <summary>
/// Selects which measured relationship is allowed to command final slit
/// placement. Plate-solve rotation is deliberately absent: it may seed a
/// candidate topology, but it is never motion authority.
/// </summary>
public enum SlitPlacementMappingAuthority
{
    GradedPhd2CalibrationLockShift = 0,
    [Obsolete("Use GradedPhd2CalibrationLockShift; authority is granted by the explicit post-settle quality assessment, not by a binary qualified label.")]
    QualifiedPhd2CalibrationLockShift = GradedPhd2CalibrationLockShift,
    IndependentFourDirectionTransformDiagnostic = 1,
}

public enum Phd2SlitGuideMode
{
    OffSlitGuideStar = 0,
    DegradedDirectTargetGuiding = 1,
    AutoPreferOffSlitThenDirectTarget = 2,
    AutoPreferDirectTargetThenOffSlit = 3,
}

public enum Phd2SensorRotationAuthority
{
    PlateSolveSeedOnly = 0,
    QualifiedPhd2Calibration = 1,
}

/// <summary>
/// Declares the coordinate system used by PHD2 lock points and G3 centroids.
/// A cropped PHD2 frame may report either full-sensor coordinates or
/// coordinates local to the active ROI; those domains must never be mixed.
/// </summary>
public enum Phd2ImageCoordinateDomain
{
    FullSensorCoordinates = 0,
    RoiLocalCoordinates = 1,
}

public enum Phd2LockShiftStageKind
{
    Outbound = 0,
    Recovery = 1,
}

/// <summary>
/// Detector topology whose canonical fingerprint invalidates the lock-shift
/// authority after a profile, camera, ROI, binning, rotation, installation
/// epoch, mount, or pier-side change.
/// </summary>
public sealed record Phd2SensorTopology(
    string InstallationEpochId,
    int ProfileId,
    string ProfileName,
    string CameraName,
    string CameraStableId,
    string MountName,
    string RegistryEvidenceSha256,
    int SensorWidthPixels,
    int SensorHeightPixels,
    int Binning,
    Phd2Rectangle Roi,
    Phd2ImageCoordinateDomain CoordinateDomain,
    double SensorRotationDegrees,
    Phd2SensorRotationAuthority RotationAuthority,
    string PierSide)
{
    public string ComputeFingerprintSha256()
    {
        var fields = new[]
        {
            InstallationEpochId,
            ProfileId.ToString(CultureInfo.InvariantCulture),
            ProfileName,
            CameraName,
            CameraStableId,
            MountName,
            RegistryEvidenceSha256,
            SensorWidthPixels.ToString(CultureInfo.InvariantCulture),
            SensorHeightPixels.ToString(CultureInfo.InvariantCulture),
            Binning.ToString(CultureInfo.InvariantCulture),
            Roi.X.ToString(CultureInfo.InvariantCulture),
            Roi.Y.ToString(CultureInfo.InvariantCulture),
            Roi.Width.ToString(CultureInfo.InvariantCulture),
            Roi.Height.ToString(CultureInfo.InvariantCulture),
            CoordinateDomain.ToString(),
            SensorRotationDegrees.ToString("R", CultureInfo.InvariantCulture),
            RotationAuthority.ToString(),
            PierSide,
        };
        var canonical = string.Concat(fields.Select(value => $"{value.Length}:{value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public sealed record Phd2LockShiftQualificationLimits(
    TimeSpan MaximumCalibrationAge,
    TimeSpan MaximumValidationAge,
    double MaximumOrthogonalityErrorDegrees,
    double MinimumAxisRatePixelsPerSecond,
    double MaximumAxisRatePixelsPerSecond);

public sealed record Phd2LockShiftQualificationRequest(
    SlitPlacementMappingAuthority Authority,
    Phd2IdentityValidation Identity,
    Phd2CalibrationValidation Calibration,
    Phd2SensorTopology CurrentTopology,
    string LockedTopologyFingerprintSha256,
    string CurrentPierSide,
    DateTimeOffset EvaluatedUtc,
    double? PlateSolveRotationSeedDegrees,
    Phd2LockShiftQualificationLimits Limits,
    Phd2CalibrationQualityAssessment CalibrationQuality);

public sealed record Phd2LockShiftQualification(
    bool IsQualified,
    string Code,
    IReadOnlyList<string> Failures,
    string CurrentTopologyFingerprintSha256,
    bool PlateSolveRotationUsedForMotion,
    string CalibrationQualityPolicyId,
    Phd2CalibrationQualityGrade CalibrationQualityGrade,
    bool RequiresOperatorSupervision,
    bool IsUnattendedScienceAuthority,
    double MaximumLockShiftScale,
    double RequiredResidualToleranceScale,
    int RequiredFreshResidualsPerLockShiftStage,
    DateTimeOffset EvaluatedUtc);

/// <summary>
/// A newly captured G3 measurement. RecognizedSlitAcquisitionPoint is runtime
/// evidence only; the planner has no registry/profile write path.
/// </summary>
public sealed record Phd2SlitFieldMeasurement(
    string FrameSha256,
    DateTimeOffset CapturedUtc,
    string TopologyFingerprintSha256,
    Phd2Point GuideStar,
    Phd2Point TargetCentroid,
    Phd2Point RecognizedSlitAcquisitionPoint,
    double GuideStarDistanceFromSlitPixels,
    bool TargetIdentityConfirmed,
    int ExposureMilliseconds,
    bool CommissionedMinimumExposureApplied,
    string TargetIdentityEvidenceId,
    string FluxEvidenceLabel,
    double FluxMetric,
    string ResidualEvidenceLabel,
    Phd2TargetPositionAuthority TargetPositionAuthority = Phd2TargetPositionAuthority.DetectedTargetCentroid);

public enum Phd2TargetPositionAuthority
{
    DetectedTargetCentroid,
    CatalogWcsProjection,
}

public sealed record Phd2LockShiftSafetySnapshot(
    bool SafetyGatePassed,
    double CurrentAltitudeDegrees,
    double PredictedMinimumAltitudeDegrees,
    double MinimumAltitudeDegrees,
    string PierSide,
    DateTimeOffset EvaluatedUtc);

public sealed record Phd2LockShiftLimits(
    double MaximumStagePixels,
    double MaximumCumulativePixels,
    int MaximumAttempts,
    TimeSpan MaximumElapsed,
    TimeSpan MaximumStageDuration,
    TimeSpan MaximumMeasurementAge,
    TimeSpan MaximumSafetySnapshotAge,
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
    double MaximumFluxMetric);

public sealed record Phd2LockShiftLedger(
    string LineageId,
    Phd2Point OriginLockPosition,
    Phd2Point CurrentLockPosition,
    int AttemptsUsed,
    double CumulativeCommandedPixels,
    DateTimeOffset StartedUtc,
    string? LastAcceptedFrameSha256);

public sealed record Phd2LockShiftStagePlan(
    Phd2LockShiftStageKind Kind,
    SlitPlacementMappingAuthority Authority,
    Phd2SlitGuideMode GuideMode,
    Phd2Point ExpectedCurrentLockPosition,
    Phd2Point RequestedLockPosition,
    Phd2Point FullDesiredLockPosition,
    Phd2Point TargetToSlitDelta,
    double StagePixels,
    double ResidualBeforePixels,
    double ReservedRecoveryDistanceUpperPixels,
    double ReservedRecoveryMotionPixels,
    int ReservedRecoveryAttempts,
    double CumulativeAfterStageUpperPixels,
    int AttemptsAfterStage,
    string TopologyFingerprintSha256,
    string SourceFrameSha256,
    string TargetIdentityEvidenceId,
    string FluxEvidenceLabel,
    string ResidualEvidenceLabel,
    string CalibrationQualityPolicyId,
    Phd2CalibrationQualityGrade CalibrationQualityGrade,
    bool RequiresOperatorSupervision,
    bool IsUnattendedScienceAuthority,
    double AppliedLockShiftScale,
    double AppliedResidualToleranceScale,
    int RequiredFreshResiduals,
    bool Degraded,
    bool RequiresFreshG3ResidualAfter,
    bool RequiresFreshLockVerificationAfter,
    bool RegistryProfileMutationAllowed,
    bool AutomaticRetryAllowed);

public sealed record Phd2LockShiftPlanResult(
    bool IsAllowed,
    bool IsComplete,
    string Code,
    string Message,
    Phd2LockShiftStagePlan? Stage);

public static class Phd2SlitLockShiftPlanner
{
    private static readonly Regex Sha256Pattern = new("^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant);

    public static Phd2LockShiftQualification Qualify(Phd2LockShiftQualificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Identity);
        ArgumentNullException.ThrowIfNull(request.Calibration);
        ArgumentNullException.ThrowIfNull(request.CurrentTopology);
        ArgumentNullException.ThrowIfNull(request.Limits);
        var failures = new List<string>();
        var topology = request.CurrentTopology;
        var fingerprint = topology.ComputeFingerprintSha256();

        if (request.Authority != SlitPlacementMappingAuthority.GradedPhd2CalibrationLockShift)
        {
            failures.Add("independent four-direction transform is an explicit diagnostic/fallback, not default PHD2 lock-shift authority");
        }
        if (request.CalibrationQuality is null)
        {
            failures.Add("a post-settle PHD2 calibration-quality assessment is required");
        }
        else
        {
            var quality = request.CalibrationQuality;
            if (!quality.IsLockShiftAuthority || quality.Grade == Phd2CalibrationQualityGrade.Rejected)
                failures.Add($"the post-settle calibration-quality assessment does not grant lock-shift authority: {quality.Summary}");
            if (string.IsNullOrWhiteSpace(quality.PolicyId))
                failures.Add("the calibration-quality policy id is missing");
            if (!double.IsFinite(quality.MaximumLockShiftScale) || quality.MaximumLockShiftScale is <= 0 or > 1)
                failures.Add("the calibration-quality lock-shift scale is invalid");
            if (!double.IsFinite(quality.RequiredResidualToleranceScale) || quality.RequiredResidualToleranceScale is <= 0 or > 1)
                failures.Add("the calibration-quality residual-tolerance scale is invalid");
            if (quality.RequiredFreshResidualsPerLockShiftStage <= 0)
                failures.Add("the calibration-quality fresh-residual count is invalid");
            var qualityAge = request.EvaluatedUtc - quality.EvaluatedUtc;
            if (qualityAge < TimeSpan.Zero || qualityAge > request.Limits.MaximumValidationAge)
                failures.Add("the post-settle calibration-quality assessment is stale or from the future");
        }
        if (!request.Identity.IsValid)
        {
            failures.AddRange(request.Identity.Failures.Concat(request.Identity.IndeterminateReasons)
                .Select(reason => $"identity: {reason}"));
        }
        if (!request.Calibration.IsValid)
        {
            failures.AddRange(request.Calibration.Failures.Concat(request.Calibration.IndeterminateReasons)
                .Select(reason => $"calibration: {reason}"));
        }
        if (!ValidateTopology(topology, failures))
        {
            failures.Add("sensor topology is incomplete or invalid");
        }
        if (!Sha256Pattern.IsMatch(request.LockedTopologyFingerprintSha256 ?? string.Empty) ||
            !string.Equals(fingerprint, request.LockedTopologyFingerprintSha256, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("current sensor topology fingerprint differs from the locked commissioned fingerprint");
        }
        if (topology.RotationAuthority != Phd2SensorRotationAuthority.QualifiedPhd2Calibration)
        {
            failures.Add("plate-solve rotation is seed-only and cannot authorize lock-position motion");
        }
        if (!string.Equals(topology.PierSide, request.CurrentPierSide, StringComparison.Ordinal))
        {
            failures.Add($"pier side is '{request.CurrentPierSide}', expected topology side '{topology.PierSide}'");
        }
        if (request.Identity.Profile.Id != topology.ProfileId ||
            !string.Equals(request.Identity.Profile.Name, topology.ProfileName, StringComparison.Ordinal) ||
            request.Calibration.Profile.Id != topology.ProfileId ||
            !string.Equals(request.Calibration.Profile.Name, topology.ProfileName, StringComparison.Ordinal))
        {
            failures.Add("identity/calibration profile does not match the current sensor topology");
        }
        if (!string.Equals(request.Identity.Equipment.Camera?.Name, topology.CameraName, StringComparison.Ordinal) ||
            request.Identity.Equipment.Camera?.Connected != true ||
            !string.Equals(request.Identity.Equipment.Mount?.Name, topology.MountName, StringComparison.Ordinal) ||
            request.Identity.Equipment.Mount?.Connected != true)
        {
            failures.Add("runtime PHD2 camera/mount identity or connection differs from the topology");
        }

        ValidateQualificationLimits(request.Limits, failures);
        var validationAge = request.EvaluatedUtc - request.Calibration.EvaluatedUtc;
        if (validationAge < TimeSpan.Zero || validationAge > request.Limits.MaximumValidationAge)
        {
            failures.Add("calibration validation snapshot is stale or from the future");
        }
        if (!request.Calibration.CalibrationAge.HasValue ||
            request.Calibration.CalibrationAge.Value < TimeSpan.Zero ||
            request.Calibration.CalibrationAge.Value > request.Limits.MaximumCalibrationAge)
        {
            failures.Add("calibration age is unknown, negative, or expired");
        }

        var calibration = request.Calibration.Calibration;
        if (!calibration.Calibrated)
        {
            failures.Add("PHD2 mount calibration is absent");
        }
        ValidateAxis("RA", calibration.RaAngleDegrees, calibration.RaRatePixelsPerSecond, calibration.RaParity, request.Limits, failures);
        ValidateAxis("Dec", calibration.DecAngleDegrees, calibration.DecRatePixelsPerSecond, calibration.DecParity, request.Limits, failures);
        if (!IsFinite(calibration.DeclinationDegrees) || calibration.DeclinationDegrees!.Value is < -90 or > 90)
        {
            failures.Add("calibration declination is absent, non-finite, or outside [-90, 90] degrees");
        }
        if (IsFinite(calibration.RaAngleDegrees) && IsFinite(calibration.DecAngleDegrees))
        {
            var orthogonality = OrthogonalityError(calibration.RaAngleDegrees!.Value, calibration.DecAngleDegrees!.Value);
            if (orthogonality > request.Limits.MaximumOrthogonalityErrorDegrees)
            {
                failures.Add($"calibration orthogonality error {orthogonality:F3}° exceeds the limit");
            }
        }

        var qualified = failures.Count == 0;
        return new Phd2LockShiftQualification(
            qualified,
            qualified ? "PHD2_LOCK_SHIFT_QUALIFIED" : "PHD2_LOCK_SHIFT_UNQUALIFIED",
            failures,
            fingerprint,
            PlateSolveRotationUsedForMotion: false,
            request.CalibrationQuality?.PolicyId ?? string.Empty,
            request.CalibrationQuality?.Grade ?? Phd2CalibrationQualityGrade.Rejected,
            request.CalibrationQuality?.RequiresOperatorSupervision ?? true,
            request.CalibrationQuality?.IsUnattendedScienceAuthority ?? false,
            request.CalibrationQuality?.MaximumLockShiftScale ?? 0,
            request.CalibrationQuality?.RequiredResidualToleranceScale ?? 0,
            request.CalibrationQuality?.RequiredFreshResidualsPerLockShiftStage ?? 0,
            request.EvaluatedUtc);
    }

    public static Phd2LockShiftPlanResult PlanOutboundStage(
        Phd2LockShiftQualification qualification,
        Phd2SlitGuideMode guideMode,
        Phd2SlitFieldMeasurement measurement,
        Phd2LockShiftLedger ledger,
        Phd2LockShiftSafetySnapshot safety,
        Phd2SensorTopology topology,
        Phd2LockShiftLimits limits,
        DateTimeOffset now)
    {
        var common = ValidateCommon(qualification, ledger, safety, topology, limits, now);
        if (common is not null) return common;
        var measurementFailure = ValidateMeasurement(guideMode, measurement, ledger, topology, limits, now);
        if (measurementFailure is not null) return measurementFailure;

        var delta = Subtract(measurement.RecognizedSlitAcquisitionPoint, measurement.TargetCentroid);
        var residual = Norm(delta);
        var effectiveTargetTolerance = limits.TargetOnSlitTolerancePixels * qualification.RequiredResidualToleranceScale;
        if (residual <= effectiveTargetTolerance)
        {
            return new Phd2LockShiftPlanResult(
                true,
                true,
                "TARGET_AT_SLIT_MIDPOINT",
                $"Fresh G3 target/slit-midpoint residual {residual:F3} pixels is within graded tolerance {effectiveTargetTolerance:F3}.",
                null);
        }
        if (residual > limits.MaximumAcquisitionResidualPixels)
        {
            return Denied("SLIT_RESIDUAL_SEARCH_WINDOW", $"Fresh target/slit residual {residual:F3} pixels exceeds the acquisition window.");
        }

        var fullDesired = Add(measurement.GuideStar, delta);
        if (!InsideCoordinateDomain(fullDesired, topology))
        {
            return Denied("DESIRED_LOCK_OUTSIDE_DOMAIN", "The target-to-slit delta would place the desired lock position outside the commissioned PHD2 image coordinate domain.");
        }
        var remainingVector = Subtract(fullDesired, ledger.CurrentLockPosition);
        var remaining = Norm(remainingVector);
        if (remaining <= limits.LockVerificationTolerancePixels)
        {
            return Denied(
                "FRESH_G3_RESIDUAL_REQUIRED",
                "The lock is already at the calculated destination but the target remains off slit; another lock mutation is prohibited until a fresh G3 residual explains the mismatch.");
        }

        var stagePixels = Math.Min(limits.MaximumStagePixels * qualification.MaximumLockShiftScale, remaining);
        var fraction = stagePixels / remaining;
        var requested = Add(ledger.CurrentLockPosition, Scale(remainingVector, fraction));
        return BuildStage(
            Phd2LockShiftStageKind.Outbound,
            qualification,
            guideMode,
            measurement,
            ledger,
            limits,
            now,
            requested,
            fullDesired,
            delta,
            stagePixels,
            residual,
            requiresFreshG3: true);
    }

    public static Phd2LockShiftPlanResult PlanRecoveryStage(
        Phd2LockShiftQualification qualification,
        Phd2SlitGuideMode guideMode,
        Phd2LockShiftLedger ledger,
        Phd2LockShiftSafetySnapshot safety,
        Phd2SensorTopology topology,
        Phd2LockShiftLimits limits,
        DateTimeOffset now,
        string sourceFrameSha256,
        string targetIdentityEvidenceId)
    {
        var common = ValidateCommon(qualification, ledger, safety, topology, limits, now);
        if (common is not null) return common;
        var remainingVector = Subtract(ledger.OriginLockPosition, ledger.CurrentLockPosition);
        var remaining = Norm(remainingVector);
        if (remaining <= limits.LockVerificationTolerancePixels)
        {
            return new Phd2LockShiftPlanResult(true, true, "LOCK_ORIGIN_RECOVERED", "The runtime lock position is back at the staged-shift origin.", null);
        }
        var stagePixels = Math.Min(limits.MaximumStagePixels, remaining);
        var requested = Add(ledger.CurrentLockPosition, Scale(remainingVector, stagePixels / remaining));
        var evidence = new Phd2SlitFieldMeasurement(
            sourceFrameSha256,
            now,
            qualification.CurrentTopologyFingerprintSha256,
            ledger.CurrentLockPosition,
            ledger.CurrentLockPosition,
            ledger.OriginLockPosition,
            double.PositiveInfinity,
            true,
            0,
            true,
            targetIdentityEvidenceId,
            "RECOVERY_NO_FLUX_CLAIM",
            0,
            "RECOVERY_LOCK_RESIDUAL");
        return BuildStage(
            Phd2LockShiftStageKind.Recovery,
            qualification,
            guideMode,
            evidence,
            ledger,
            limits,
            now,
            requested,
            ledger.OriginLockPosition,
            new Phd2Point(0, 0),
            stagePixels,
            remaining,
            requiresFreshG3: false);
    }

    private static Phd2LockShiftPlanResult BuildStage(
        Phd2LockShiftStageKind kind,
        Phd2LockShiftQualification qualification,
        Phd2SlitGuideMode guideMode,
        Phd2SlitFieldMeasurement measurement,
        Phd2LockShiftLedger ledger,
        Phd2LockShiftLimits limits,
        DateTimeOffset now,
        Phd2Point requested,
        Phd2Point fullDesired,
        Phd2Point delta,
        double stagePixels,
        double residual,
        bool requiresFreshG3)
    {
        var recoveryDistanceUpper = Distance(ledger.OriginLockPosition, requested) + limits.LockVerificationTolerancePixels;
        var minimumRecoveryProgress = limits.MaximumStagePixels - limits.LockVerificationTolerancePixels;
        if (minimumRecoveryProgress <= 0)
        {
            return Denied("LOCK_RECOVERY_PROGRESS_INVALID", "Lock verification tolerance leaves no guaranteed recovery progress.");
        }
        var recoveryAttempts = recoveryDistanceUpper <= limits.LockVerificationTolerancePixels
            ? 0
            : (int)Math.Ceiling(recoveryDistanceUpper / minimumRecoveryProgress);
        var recoveryMotion = recoveryDistanceUpper +
            (2 * limits.LockVerificationTolerancePixels * recoveryAttempts);
        var attemptsAfter = checked(ledger.AttemptsUsed + 1);
        if (attemptsAfter + recoveryAttempts > limits.MaximumAttempts)
        {
            return Denied("SLIT_LOCK_RETURN_ATTEMPT_RESERVE", "This stage cannot reserve an attempt-bounded exact-lock return to the run origin.");
        }
        var cumulativeAfterUpper = ledger.CumulativeCommandedPixels + stagePixels + recoveryMotion;
        if (cumulativeAfterUpper > limits.MaximumCumulativePixels)
        {
            return Denied("SLIT_LOCK_RETURN_CUMULATIVE_RESERVE", "This stage cannot reserve a cumulative-pixel-bounded exact-lock return to the run origin.");
        }
        var elapsed = now - ledger.StartedUtc;
        var timeAfterUpper = elapsed + TimeSpan.FromTicks(limits.MaximumStageDuration.Ticks * (1L + recoveryAttempts));
        if (elapsed < TimeSpan.Zero || timeAfterUpper > limits.MaximumElapsed)
        {
            return Denied("SLIT_LOCK_RETURN_TIME_RESERVE", "This stage cannot reserve a time-bounded exact-lock return to the run origin.");
        }

        var degraded = guideMode == Phd2SlitGuideMode.DegradedDirectTargetGuiding ||
            qualification.RequiresOperatorSupervision ||
            qualification.CalibrationQualityGrade == Phd2CalibrationQualityGrade.DegradedSupervised;
        var degradedPrefix = guideMode == Phd2SlitGuideMode.DegradedDirectTargetGuiding
            ? "DEGRADED_DIRECT_TARGET_GUIDING:"
            : degraded
                ? "DEGRADED_SUPERVISED_CALIBRATION:"
                : string.Empty;
        var stage = new Phd2LockShiftStagePlan(
            kind,
            SlitPlacementMappingAuthority.GradedPhd2CalibrationLockShift,
            guideMode,
            ledger.CurrentLockPosition,
            requested,
            fullDesired,
            delta,
            stagePixels,
            residual,
            recoveryDistanceUpper,
            recoveryMotion,
            recoveryAttempts,
            cumulativeAfterUpper,
            attemptsAfter,
            qualification.CurrentTopologyFingerprintSha256,
            measurement.FrameSha256,
            measurement.TargetIdentityEvidenceId,
            degradedPrefix + measurement.FluxEvidenceLabel,
            degradedPrefix + measurement.ResidualEvidenceLabel,
            qualification.CalibrationQualityPolicyId,
            qualification.CalibrationQualityGrade,
            qualification.RequiresOperatorSupervision || guideMode == Phd2SlitGuideMode.DegradedDirectTargetGuiding,
            qualification.IsUnattendedScienceAuthority && guideMode != Phd2SlitGuideMode.DegradedDirectTargetGuiding,
            qualification.MaximumLockShiftScale,
            qualification.RequiredResidualToleranceScale,
            qualification.RequiredFreshResidualsPerLockShiftStage,
            degraded,
            requiresFreshG3,
            RequiresFreshLockVerificationAfter: true,
            RegistryProfileMutationAllowed: false,
            AutomaticRetryAllowed: false);
        return new Phd2LockShiftPlanResult(
            true,
            false,
            kind == Phd2LockShiftStageKind.Outbound ? "SLIT_LOCK_STAGE_ALLOWED" : "SLIT_LOCK_RECOVERY_STAGE_ALLOWED",
            $"One {stagePixels:F3}-pixel exact runtime lock-position stage is allowed with {recoveryAttempts} recovery attempt(s) reserved.",
            stage);
    }

    private static Phd2LockShiftPlanResult? ValidateCommon(
        Phd2LockShiftQualification qualification,
        Phd2LockShiftLedger ledger,
        Phd2LockShiftSafetySnapshot safety,
        Phd2SensorTopology topology,
        Phd2LockShiftLimits limits,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(qualification);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(safety);
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(limits);
        var limitFailure = ValidateMotionLimits(limits);
        if (limitFailure is not null) return Denied("SLIT_LOCK_LIMITS_INVALID", limitFailure);
        if (!qualification.IsQualified) return Denied("PHD2_LOCK_SHIFT_UNQUALIFIED", "PHD2 identity/calibration/topology authority is not qualified.");
        if (!string.Equals(topology.ComputeFingerprintSha256(), qualification.CurrentTopologyFingerprintSha256, StringComparison.OrdinalIgnoreCase))
            return Denied("G3_TOPOLOGY_FINGERPRINT_CHANGED", "G3 sensor topology changed after PHD2 lock-shift qualification.");
        if (!safety.SafetyGatePassed ||
            safety.CurrentAltitudeDegrees < safety.MinimumAltitudeDegrees ||
            safety.PredictedMinimumAltitudeDegrees < safety.MinimumAltitudeDegrees)
            return Denied("SLIT_LOCK_ALTITUDE_SAFETY", "Current/predicted altitude or the external safety gate does not authorize a lock shift.");
        if (!string.Equals(safety.PierSide, topology.PierSide, StringComparison.Ordinal))
            return Denied("SLIT_LOCK_PIER_CHANGED", "Pier side changed after topology qualification.");
        var safetyAge = now - safety.EvaluatedUtc;
        if (safetyAge < TimeSpan.Zero || safetyAge > limits.MaximumSafetySnapshotAge)
            return Denied("SLIT_LOCK_SAFETY_STALE", "The safety/altitude snapshot is stale or from the future.");
        if (ledger.AttemptsUsed < 0 || ledger.CumulativeCommandedPixels < 0 ||
            ledger.AttemptsUsed > limits.MaximumAttempts || ledger.CumulativeCommandedPixels > limits.MaximumCumulativePixels ||
            string.IsNullOrWhiteSpace(ledger.LineageId))
            return Denied("SLIT_LOCK_LEDGER_INVALID", "The staged lock-shift ledger is invalid or already exhausted.");
        if (!InsideCoordinateDomain(ledger.OriginLockPosition, topology) || !InsideCoordinateDomain(ledger.CurrentLockPosition, topology))
            return Denied("SLIT_LOCK_POSITION_OUTSIDE_SENSOR", "Origin/current runtime lock position is outside the current G3 sensor.");
        return null;
    }

    private static Phd2LockShiftPlanResult? ValidateMeasurement(
        Phd2SlitGuideMode guideMode,
        Phd2SlitFieldMeasurement measurement,
        Phd2LockShiftLedger ledger,
        Phd2SensorTopology topology,
        Phd2LockShiftLimits limits,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        if (!Sha256Pattern.IsMatch(measurement.FrameSha256 ?? string.Empty))
            return Denied("G3_FRAME_HASH_INVALID", "Fresh G3 residual evidence requires a SHA-256 frame hash.");
        if (string.Equals(measurement.FrameSha256, ledger.LastAcceptedFrameSha256, StringComparison.OrdinalIgnoreCase))
            return Denied("G3_FRAME_REUSED", "Each staged shift requires a new immutable G3 residual frame.");
        var age = now - measurement.CapturedUtc;
        if (age < TimeSpan.Zero || age > limits.MaximumMeasurementAge)
            return Denied("G3_RESIDUAL_STALE", "The G3 target/slit residual frame is stale or from the future.");
        if (!string.Equals(measurement.TopologyFingerprintSha256, topology.ComputeFingerprintSha256(), StringComparison.OrdinalIgnoreCase))
            return Denied("G3_FRAME_TOPOLOGY_MISMATCH", "The G3 frame topology fingerprint differs from the qualified topology.");
        if (!InsideCoordinateDomain(measurement.GuideStar, topology) || !InsideCoordinateDomain(measurement.TargetCentroid, topology) ||
            !InsideCoordinateDomain(measurement.RecognizedSlitAcquisitionPoint, topology))
            return Denied("G3_MEASUREMENT_OUTSIDE_DOMAIN", "Guide, target, or runtime slit acquisition point is outside the commissioned PHD2 image coordinate domain.");
        if (!measurement.TargetIdentityConfirmed || string.IsNullOrWhiteSpace(measurement.TargetIdentityEvidenceId))
            return Denied("TARGET_IDENTITY_UNCONFIRMED", "Lock shifting requires confirmed target identity evidence.");
        if (string.IsNullOrWhiteSpace(measurement.FluxEvidenceLabel) || string.IsNullOrWhiteSpace(measurement.ResidualEvidenceLabel))
            return Denied("G3_FLUX_RESIDUAL_EVIDENCE_INVALID", "Flux and residual evidence labels must be present.");
        if (measurement.TargetPositionAuthority == Phd2TargetPositionAuthority.CatalogWcsProjection)
        {
            if (measurement.FluxMetric != 0 ||
                !string.Equals(measurement.FluxEvidenceLabel, "CATALOG_WCS_TARGET_FLUX_NOT_APPLICABLE", StringComparison.Ordinal))
                return Denied("G3_CATALOG_WCS_AUTHORITY_INVALID", "Catalogue-WCS target geometry must explicitly mark target flux as not applicable and must not fabricate a flux metric.");
        }
        else if (!double.IsFinite(measurement.FluxMetric) ||
                 measurement.FluxMetric < limits.MinimumFluxMetric ||
                 measurement.FluxMetric > limits.MaximumFluxMetric)
        {
            return Denied("G3_FLUX_RESIDUAL_EVIDENCE_INVALID", "Detected-target flux must pass the commissioned hard envelope.");
        }

        var guideLockResidual = Distance(measurement.GuideStar, ledger.CurrentLockPosition);
        if (guideMode == Phd2SlitGuideMode.OffSlitGuideStar)
        {
            if (measurement.GuideStarDistanceFromSlitPixels < limits.MinimumOffSlitGuideDistancePixels)
                return Denied("OFF_SLIT_GUIDE_GUARD", "The selected ordinary guide star is inside the slit guard region.");
            if (Distance(measurement.GuideStar, measurement.TargetCentroid) < limits.MinimumOffSlitGuideTargetSeparationPixels)
                return Denied("OFF_SLIT_GUIDE_NOT_DISTINCT", "The ordinary guide star is not distinct from the science target.");
            if (guideLockResidual > limits.MaximumGuideLockResidualPixels)
                return Denied("GUIDE_LOCK_RESIDUAL_HIGH", "The ordinary guide star is not sufficiently settled at the fresh runtime lock position.");
        }
        else if (guideMode == Phd2SlitGuideMode.DegradedDirectTargetGuiding)
        {
            if (!measurement.CommissionedMinimumExposureApplied || measurement.ExposureMilliseconds <= 0)
                return Denied("DIRECT_TARGET_MINIMUM_EXPOSURE_UNCOMMISSIONED", "Degraded direct-target guiding requires the commissioned shortest G3 exposure.");
            if (Distance(measurement.GuideStar, measurement.TargetCentroid) > limits.MaximumDirectTargetCentroidSeparationPixels)
                return Denied("DIRECT_TARGET_GUIDE_IDENTITY_MISMATCH", "The PHD2 guide star is not the confirmed ultra-bright science target.");
            if (guideLockResidual > limits.MaximumDegradedDirectTargetGuideLockResidualPixels)
                return Denied("DIRECT_TARGET_GUIDE_LOCK_RESIDUAL_HIGH", "The ultra-bright target is outside the degraded guide/lock residual hard gate.");
        }
        else
        {
            return Denied("GUIDE_MODE_UNKNOWN", "Unknown slit-placement guide mode.");
        }
        return null;
    }

    private static bool ValidateTopology(Phd2SensorTopology topology, List<string> failures)
    {
        var valid = true;
        if (string.IsNullOrWhiteSpace(topology.InstallationEpochId) || topology.ProfileId < 0 ||
            string.IsNullOrWhiteSpace(topology.ProfileName) || string.IsNullOrWhiteSpace(topology.CameraName) ||
            string.IsNullOrWhiteSpace(topology.CameraStableId) || string.IsNullOrWhiteSpace(topology.MountName) ||
            !Sha256Pattern.IsMatch(topology.RegistryEvidenceSha256 ?? string.Empty) ||
            topology.SensorWidthPixels <= 0 || topology.SensorHeightPixels <= 0 || topology.Binning <= 0 ||
            topology.Roi.Width <= 0 || topology.Roi.Height <= 0 || topology.Roi.X < 0 || topology.Roi.Y < 0 ||
            topology.Roi.X + topology.Roi.Width > topology.SensorWidthPixels ||
            topology.Roi.Y + topology.Roi.Height > topology.SensorHeightPixels ||
            !Enum.IsDefined(topology.CoordinateDomain) ||
            !double.IsFinite(topology.SensorRotationDegrees) || string.IsNullOrWhiteSpace(topology.PierSide))
        {
            valid = false;
        }
        return valid;
    }

    private static void ValidateQualificationLimits(Phd2LockShiftQualificationLimits limits, List<string> failures)
    {
        if (limits.MaximumCalibrationAge <= TimeSpan.Zero || limits.MaximumValidationAge <= TimeSpan.Zero ||
            !double.IsFinite(limits.MaximumOrthogonalityErrorDegrees) || limits.MaximumOrthogonalityErrorDegrees < 0 || limits.MaximumOrthogonalityErrorDegrees >= 90 ||
            !double.IsFinite(limits.MinimumAxisRatePixelsPerSecond) || limits.MinimumAxisRatePixelsPerSecond <= 0 ||
            !double.IsFinite(limits.MaximumAxisRatePixelsPerSecond) || limits.MaximumAxisRatePixelsPerSecond <= limits.MinimumAxisRatePixelsPerSecond)
        {
            failures.Add("PHD2 lock-shift qualification limits are invalid");
        }
    }

    private static void ValidateAxis(
        string name,
        double? angle,
        double? rate,
        string? parity,
        Phd2LockShiftQualificationLimits limits,
        List<string> failures)
    {
        if (!IsFinite(angle)) failures.Add($"{name} calibration angle is absent/non-finite");
        if (!IsFinite(rate) || rate!.Value < limits.MinimumAxisRatePixelsPerSecond || rate.Value > limits.MaximumAxisRatePixelsPerSecond)
            failures.Add($"{name} calibration rate is absent/non-finite/out of range");
        if (parity is not "+" and not "-") failures.Add($"{name} calibration parity must be the PHD2 protocol value '+' or '-'");
    }

    private static string? ValidateMotionLimits(Phd2LockShiftLimits limits)
    {
        if (!double.IsFinite(limits.MaximumStagePixels) || limits.MaximumStagePixels <= 0 ||
            !double.IsFinite(limits.MaximumCumulativePixels) || limits.MaximumCumulativePixels <= 0 ||
            limits.MaximumAttempts <= 0 || limits.MaximumElapsed <= TimeSpan.Zero || limits.MaximumStageDuration <= TimeSpan.Zero ||
            limits.MaximumMeasurementAge <= TimeSpan.Zero || limits.MaximumSafetySnapshotAge <= TimeSpan.Zero ||
            !double.IsFinite(limits.LockPreconditionTolerancePixels) || limits.LockPreconditionTolerancePixels < 0 ||
            !double.IsFinite(limits.LockVerificationTolerancePixels) || limits.LockVerificationTolerancePixels < 0 ||
            limits.LockVerificationTolerancePixels >= limits.MaximumStagePixels ||
            !double.IsFinite(limits.TargetOnSlitTolerancePixels) || limits.TargetOnSlitTolerancePixels < 0 ||
            !double.IsFinite(limits.MaximumAcquisitionResidualPixels) || limits.MaximumAcquisitionResidualPixels <= limits.TargetOnSlitTolerancePixels ||
            !double.IsFinite(limits.MinimumOffSlitGuideDistancePixels) || limits.MinimumOffSlitGuideDistancePixels < 0 ||
            !double.IsFinite(limits.MinimumOffSlitGuideTargetSeparationPixels) || limits.MinimumOffSlitGuideTargetSeparationPixels < 0 ||
            !double.IsFinite(limits.MaximumGuideLockResidualPixels) || limits.MaximumGuideLockResidualPixels < 0 ||
            !double.IsFinite(limits.MaximumDegradedDirectTargetGuideLockResidualPixels) || limits.MaximumDegradedDirectTargetGuideLockResidualPixels < 0 ||
            !double.IsFinite(limits.MaximumDirectTargetCentroidSeparationPixels) || limits.MaximumDirectTargetCentroidSeparationPixels < 0 ||
            !double.IsFinite(limits.MinimumFluxMetric) || !double.IsFinite(limits.MaximumFluxMetric) || limits.MaximumFluxMetric <= limits.MinimumFluxMetric)
            return "One or more pixel, cumulative, attempt, time, freshness, residual, or flux limits are invalid.";
        return null;
    }

    private static bool InsideCoordinateDomain(Phd2Point point, Phd2SensorTopology topology)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
        {
            return false;
        }

        return topology.CoordinateDomain switch
        {
            Phd2ImageCoordinateDomain.FullSensorCoordinates =>
                point.X >= topology.Roi.X && point.Y >= topology.Roi.Y &&
                point.X < topology.Roi.X + topology.Roi.Width &&
                point.Y < topology.Roi.Y + topology.Roi.Height,
            Phd2ImageCoordinateDomain.RoiLocalCoordinates =>
                point.X >= 0 && point.Y >= 0 &&
                point.X < topology.Roi.Width && point.Y < topology.Roi.Height,
            _ => false,
        };
    }

    private static double OrthogonalityError(double first, double second)
    {
        var difference = Math.Abs(NormalizeSignedDegrees(second - first));
        return Math.Abs(90 - Math.Min(difference, 180 - difference));
    }

    private static double NormalizeSignedDegrees(double value)
    {
        var normalized = ((value % 360) + 360) % 360;
        return normalized > 180 ? normalized - 360 : normalized;
    }

    private static bool IsFinite(double? value) => value.HasValue && double.IsFinite(value.Value);
    private static Phd2Point Add(Phd2Point a, Phd2Point b) => new(a.X + b.X, a.Y + b.Y);
    private static Phd2Point Subtract(Phd2Point a, Phd2Point b) => new(a.X - b.X, a.Y - b.Y);
    private static Phd2Point Scale(Phd2Point value, double factor) => new(value.X * factor, value.Y * factor);
    private static double Norm(Phd2Point value) => Math.Sqrt((value.X * value.X) + (value.Y * value.Y));
    private static double Distance(Phd2Point a, Phd2Point b) => Norm(Subtract(a, b));
    private static Phd2LockShiftPlanResult Denied(string code, string message) => new(false, false, code, message, null);
}
