using System.Text.RegularExpressions;

namespace UvexAdv.Phd2;

/// <summary>
/// Ordered from unusable to strongest authority.  A degraded calibration may
/// be used only by an explicitly supervised workflow; it is never equivalent
/// to qualified unattended-science authority.
/// </summary>
public enum Phd2CalibrationQualityGrade
{
    Rejected = 0,
    DegradedSupervised = 1,
    Qualified = 2,
    Excellent = 3,
}

public enum Phd2CalibrationEvaluationPhase
{
    PreGuide = 0,
    PostSettle = 1,
}

public enum Phd2CalibrationSelectionPurpose
{
    ValidationGuide = 0,
    LockShift = 1,
    UnattendedScience = 2,
}

/// <summary>
/// Versioned multi-dimensional grading policy.  Orthogonality is one quality
/// dimension; the qualified threshold is deliberately not a binary stop line.
/// </summary>
public sealed record Phd2CalibrationQualityPolicy(
    string PolicyId,
    TimeSpan ExcellentMaximumAge,
    TimeSpan QualifiedMaximumAge,
    TimeSpan DegradedMaximumAge,
    double ExcellentMaximumOrthogonalityErrorDegrees,
    double QualifiedMaximumOrthogonalityErrorDegrees,
    double DegradedMaximumOrthogonalityErrorDegrees,
    double ExcellentMaximumBidirectionalRateRatio,
    double QualifiedMaximumBidirectionalRateRatio,
    double DegradedMaximumBidirectionalRateRatio,
    double ExcellentMaximumCrossAxisRateRatio,
    double QualifiedMaximumCrossAxisRateRatio,
    double DegradedMaximumCrossAxisRateRatio,
    double ExcellentMaximumDroppedFrameFraction,
    double QualifiedMaximumDroppedFrameFraction,
    double DegradedMaximumDroppedFrameFraction,
    TimeSpan MaximumSettleEvidenceAge,
    TimeSpan MaximumResidualEvidenceAge,
    double QualifiedMaximumLockShiftScale,
    double DegradedMaximumLockShiftScale,
    double QualifiedResidualToleranceScale,
    double DegradedResidualToleranceScale,
    int RequiredFreshResidualsPerLockShiftStage)
{
    public static Phd2CalibrationQualityPolicy Default { get; } = new(
        PolicyId: "phd2-calibration-quality-v1",
        ExcellentMaximumAge: TimeSpan.FromHours(24),
        QualifiedMaximumAge: TimeSpan.FromDays(7),
        DegradedMaximumAge: TimeSpan.FromDays(30),
        ExcellentMaximumOrthogonalityErrorDegrees: 5,
        QualifiedMaximumOrthogonalityErrorDegrees: 10,
        DegradedMaximumOrthogonalityErrorDegrees: 30,
        ExcellentMaximumBidirectionalRateRatio: 1.20,
        QualifiedMaximumBidirectionalRateRatio: 1.75,
        DegradedMaximumBidirectionalRateRatio: 3.00,
        ExcellentMaximumCrossAxisRateRatio: 1.50,
        QualifiedMaximumCrossAxisRateRatio: 2.50,
        DegradedMaximumCrossAxisRateRatio: 5.00,
        ExcellentMaximumDroppedFrameFraction: 0.05,
        QualifiedMaximumDroppedFrameFraction: 0.20,
        DegradedMaximumDroppedFrameFraction: 0.50,
        MaximumSettleEvidenceAge: TimeSpan.FromMinutes(5),
        MaximumResidualEvidenceAge: TimeSpan.FromMinutes(5),
        QualifiedMaximumLockShiftScale: 1.00,
        DegradedMaximumLockShiftScale: 0.50,
        QualifiedResidualToleranceScale: 1.00,
        DegradedResidualToleranceScale: 0.75,
        RequiredFreshResidualsPerLockShiftStage: 1);

    /// <summary>
    /// Applies only the policy's hard rejection ceilings to the legacy PHD2
    /// structural validator.  In particular, the qualified 10° boundary must
    /// not be passed as its binary MaximumOrthogonalityErrorDegrees value,
    /// otherwise an 11.7° candidate is rejected before graded evaluation.
    /// </summary>
    public Phd2CalibrationRequirement ApplyHardRejectionCeilings(Phd2CalibrationRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        return requirement with
        {
            MaximumAge = DegradedMaximumAge,
            MaximumOrthogonalityErrorDegrees = DegradedMaximumOrthogonalityErrorDegrees,
        };
    }
}

/// <summary>
/// Attestation for one real guide/settle operation.  All epoch booleans must
/// originate from the same locally issued guide RPC and its SettleBegin /
/// SettleDone event sequence; an unsolicited SettleDone is not evidence.
/// </summary>
public sealed record Phd2CalibrationSettleEvidence(
    string EvidenceId,
    Phd2SettleResult Result,
    bool GuideCommandAccepted,
    bool SettleBeginObserved,
    bool SameConnectionEpoch,
    bool SameGuideEpoch,
    DateTimeOffset EvaluatedUtc);

/// <summary>
/// A fresh, immutable target/slit measurement taken after the last calibration,
/// exact-lock shift, or other acquisition motion that could invalidate it.
/// Ordinary guide corrections are judged by the accompanying settle evidence.
/// </summary>
public sealed record Phd2CalibrationResidualEvidence(
    string FrameSha256,
    DateTimeOffset CapturedUtc,
    double ResidualPixels,
    double MaximumResidualPixels,
    bool TargetIdentityConfirmed,
    bool TopologyMatched,
    bool NoUnvalidatedCalibrationOrLockShiftAfterMeasurement,
    DateTimeOffset EvaluatedUtc);

/// <summary>
/// One selectable calibration candidate and all evidence used to grade it.
/// Nullable topology/pier matches mean the relationship was not attested; a
/// definite false is a hard mismatch.
/// </summary>
public sealed record Phd2CalibrationQualityCandidate(
    string CandidateId,
    Phd2CalibrationValidation Validation,
    Phd2CalibrationEvaluationPhase Phase,
    bool ProfileEvidenceMatched,
    bool EquipmentIdentityMatched,
    bool? CalibrationTopologyMatched,
    bool? CalibrationPierSideMatched,
    bool CalibrationProcessEvidenceComplete,
    double? RaBidirectionalRateRatio,
    double? DecBidirectionalRateRatio,
    Phd2CalibrationSettleEvidence? Settle,
    Phd2CalibrationResidualEvidence? FreshResidual);

public sealed record Phd2CalibrationQualityAssessment(
    string CandidateId,
    string PolicyId,
    Phd2CalibrationQualityGrade Grade,
    double Score,
    double? CrossAxisRateRatio,
    bool CanAttemptValidationGuide,
    bool IsUsableForSupervisedGuiding,
    bool IsLockShiftAuthority,
    bool IsUnattendedScienceAuthority,
    bool RequiresOperatorSupervision,
    double MaximumLockShiftScale,
    double RequiredResidualToleranceScale,
    int RequiredFreshResidualsPerLockShiftStage,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> HardFailures,
    DateTimeOffset EvaluatedUtc)
{
    public string Summary =>
        $"{Grade}: {string.Join(" ", HardFailures.Count > 0 ? HardFailures : Reasons)}";
}

public sealed record Phd2CalibrationCandidateSelection(
    Phd2CalibrationSelectionPurpose Purpose,
    Phd2CalibrationQualityAssessment? Selected,
    IReadOnlyList<Phd2CalibrationQualityAssessment> Assessments)
{
    public bool HasUsableCandidate => Selected is not null;
}

public static partial class Phd2CalibrationQualityEvaluator
{
    public static Phd2CalibrationQualityAssessment Evaluate(
        Phd2CalibrationQualityCandidate candidate,
        Phd2CalibrationQualityPolicy policy,
        DateTimeOffset evaluatedUtc)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(candidate.Validation);
        ArgumentNullException.ThrowIfNull(policy);
        ValidatePolicy(policy);

        var reasons = new List<string>();
        var failures = new List<string>();
        var grade = Phd2CalibrationQualityGrade.Excellent;
        var validation = candidate.Validation;
        var calibration = validation.Calibration;

        if (string.IsNullOrWhiteSpace(candidate.CandidateId))
        {
            failures.Add("candidate id is missing");
        }
        if (!candidate.ProfileEvidenceMatched)
        {
            failures.Add("current profile does not match the calibration/profile evidence");
        }
        if (!candidate.EquipmentIdentityMatched)
        {
            failures.Add("current PHD2 camera or mount identity does not match");
        }
        if (candidate.CalibrationTopologyMatched == false)
        {
            failures.Add("camera/profile/ROI/binning/topology differs from the calibration evidence");
        }
        else if (!candidate.CalibrationTopologyMatched.HasValue)
        {
            Cap(ref grade, Phd2CalibrationQualityGrade.DegradedSupervised);
            reasons.Add("calibration-to-current topology provenance is unavailable; supervised use only");
        }
        if (candidate.CalibrationPierSideMatched == false)
        {
            failures.Add("current pier side differs from the calibration evidence");
        }
        else if (!candidate.CalibrationPierSideMatched.HasValue)
        {
            Cap(ref grade, Phd2CalibrationQualityGrade.DegradedSupervised);
            reasons.Add("calibration pier-side provenance is unavailable; supervised use only");
        }
        if (!candidate.CalibrationProcessEvidenceComplete)
        {
            Cap(ref grade, Phd2CalibrationQualityGrade.DegradedSupervised);
            reasons.Add("complete calibration-process evidence is unavailable; supervised use only");
        }

        if (!calibration.Calibrated)
        {
            failures.Add("PHD2 reports no mount calibration");
        }
        if (validation.Failures.Count > 0)
        {
            failures.AddRange(validation.Failures.Select(reason => $"calibration validation: {reason}"));
        }
        if (validation.IndeterminateReasons.Count > 0)
        {
            failures.AddRange(validation.IndeterminateReasons.Select(reason => $"calibration validation is indeterminate: {reason}"));
        }

        GradeAge(validation.CalibrationAge, policy, ref grade, reasons, failures);
        GradeOrthogonality(validation.OrthogonalityErrorDegrees, policy, ref grade, reasons, failures);
        ValidateAxisDirection("RA", calibration.RaAngleDegrees, calibration.RaRatePixelsPerSecond, calibration.RaParity, failures);
        ValidateAxisDirection("Dec", calibration.DecAngleDegrees, calibration.DecRatePixelsPerSecond, calibration.DecParity, failures);
        GradeSymmetry("RA", candidate.RaBidirectionalRateRatio, policy, ref grade, reasons, failures);
        GradeSymmetry("Dec", candidate.DecBidirectionalRateRatio, policy, ref grade, reasons, failures);
        var crossAxisRateRatio = GradeCrossAxisRateRatio(calibration, policy, ref grade, reasons, failures);

        if (candidate.Phase == Phd2CalibrationEvaluationPhase.PostSettle)
        {
            GradeSettle(candidate.Settle, policy, evaluatedUtc, ref grade, reasons, failures);
            GradeResidual(candidate.FreshResidual, policy, evaluatedUtc, ref grade, reasons, failures);
        }
        else
        {
            reasons.Add("pre-guide assessment only; actual settle and fresh residual are still required");
        }

        if (failures.Count > 0)
        {
            grade = Phd2CalibrationQualityGrade.Rejected;
        }
        else if (reasons.Count == 0)
        {
            reasons.Add("all supplied calibration, identity, settle, and residual evidence is excellent");
        }

        var canAttemptValidationGuide = grade != Phd2CalibrationQualityGrade.Rejected;
        var operationalEvidenceComplete = candidate.Phase == Phd2CalibrationEvaluationPhase.PostSettle &&
            candidate.Settle is not null &&
            candidate.FreshResidual is not null;
        var lockShiftAuthority = grade != Phd2CalibrationQualityGrade.Rejected && operationalEvidenceComplete;
        var usableSupervised = lockShiftAuthority;
        var unattended = grade is Phd2CalibrationQualityGrade.Excellent or Phd2CalibrationQualityGrade.Qualified &&
            usableSupervised &&
            candidate.CalibrationProcessEvidenceComplete &&
            candidate.CalibrationTopologyMatched == true &&
            candidate.CalibrationPierSideMatched == true;
        var maximumLockShiftScale = grade switch
        {
            Phd2CalibrationQualityGrade.Excellent or Phd2CalibrationQualityGrade.Qualified => policy.QualifiedMaximumLockShiftScale,
            Phd2CalibrationQualityGrade.DegradedSupervised => policy.DegradedMaximumLockShiftScale,
            _ => 0,
        };
        var residualToleranceScale = grade switch
        {
            Phd2CalibrationQualityGrade.Excellent or Phd2CalibrationQualityGrade.Qualified => policy.QualifiedResidualToleranceScale,
            Phd2CalibrationQualityGrade.DegradedSupervised => policy.DegradedResidualToleranceScale,
            _ => 0,
        };
        var score = Score(candidate, grade, policy);

        return new Phd2CalibrationQualityAssessment(
            candidate.CandidateId,
            policy.PolicyId,
            grade,
            score,
            crossAxisRateRatio,
            canAttemptValidationGuide,
            usableSupervised,
            lockShiftAuthority,
            unattended,
            grade == Phd2CalibrationQualityGrade.DegradedSupervised,
            maximumLockShiftScale,
            residualToleranceScale,
            policy.RequiredFreshResidualsPerLockShiftStage,
            reasons.AsReadOnly(),
            failures.AsReadOnly(),
            evaluatedUtc);
    }

    /// <summary>
    /// Deterministically selects the strongest currently usable candidate.
    /// Rejected and pre-guide-only assessments are retained for the UI/evidence
    /// list but cannot win selection.
    /// </summary>
    public static Phd2CalibrationCandidateSelection SelectBest(
        IEnumerable<Phd2CalibrationQualityCandidate> candidates,
        Phd2CalibrationQualityPolicy policy,
        DateTimeOffset evaluatedUtc,
        Phd2CalibrationSelectionPurpose purpose = Phd2CalibrationSelectionPurpose.LockShift)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var assessments = candidates
            .Select(candidate => Evaluate(candidate, policy, evaluatedUtc))
            .OrderByDescending(assessment => assessment.Grade)
            .ThenByDescending(assessment => assessment.Score)
            .ThenBy(assessment => assessment.CandidateId, StringComparer.Ordinal)
            .ToArray();
        var selected = assessments.FirstOrDefault(assessment => IsEligible(assessment, purpose));
        return new Phd2CalibrationCandidateSelection(purpose, selected, Array.AsReadOnly(assessments));
    }

    private static bool IsEligible(
        Phd2CalibrationQualityAssessment assessment,
        Phd2CalibrationSelectionPurpose purpose) => purpose switch
        {
            Phd2CalibrationSelectionPurpose.ValidationGuide => assessment.CanAttemptValidationGuide,
            Phd2CalibrationSelectionPurpose.LockShift => assessment.IsLockShiftAuthority,
            Phd2CalibrationSelectionPurpose.UnattendedScience => assessment.IsUnattendedScienceAuthority,
            _ => false,
        };

    private static void GradeAge(
        TimeSpan? age,
        Phd2CalibrationQualityPolicy policy,
        ref Phd2CalibrationQualityGrade grade,
        ICollection<string> reasons,
        ICollection<string> failures)
    {
        if (!age.HasValue || age.Value < TimeSpan.Zero)
        {
            failures.Add("calibration age is missing, negative, or from the future");
        }
        else if (age.Value > policy.DegradedMaximumAge)
        {
            failures.Add($"calibration age {age.Value} exceeds the degraded-use limit {policy.DegradedMaximumAge}");
        }
        else if (age.Value > policy.QualifiedMaximumAge)
        {
            Cap(ref grade, Phd2CalibrationQualityGrade.DegradedSupervised);
            reasons.Add($"calibration age {age.Value} is usable only in the degraded supervised band");
        }
        else if (age.Value > policy.ExcellentMaximumAge)
        {
            Cap(ref grade, Phd2CalibrationQualityGrade.Qualified);
            reasons.Add($"calibration age {age.Value} is in the qualified band");
        }
    }

    private static void GradeOrthogonality(
        double? error,
        Phd2CalibrationQualityPolicy policy,
        ref Phd2CalibrationQualityGrade grade,
        ICollection<string> reasons,
        ICollection<string> failures)
    {
        if (!error.HasValue || !double.IsFinite(error.Value) || error.Value < 0)
        {
            failures.Add("orthogonality error is missing, non-finite, or negative");
        }
        else if (error.Value > policy.DegradedMaximumOrthogonalityErrorDegrees)
        {
            failures.Add($"orthogonality error {error.Value:F1}° exceeds the degraded-use limit {policy.DegradedMaximumOrthogonalityErrorDegrees:F1}°");
        }
        else if (error.Value > policy.QualifiedMaximumOrthogonalityErrorDegrees)
        {
            Cap(ref grade, Phd2CalibrationQualityGrade.DegradedSupervised);
            reasons.Add($"orthogonality error {error.Value:F1}° is above the qualified {policy.QualifiedMaximumOrthogonalityErrorDegrees:F1}° band but remains supervised-usable");
        }
        else if (error.Value > policy.ExcellentMaximumOrthogonalityErrorDegrees)
        {
            Cap(ref grade, Phd2CalibrationQualityGrade.Qualified);
            reasons.Add($"orthogonality error {error.Value:F1}° is in the qualified band");
        }
    }

    private static void ValidateAxisDirection(
        string axis,
        double? angle,
        double? rate,
        string? parity,
        ICollection<string> failures)
    {
        if (!IsFinite(angle)) failures.Add($"{axis} direction angle is missing or non-finite");
        if (!IsFinite(rate) || rate <= 0) failures.Add($"{axis} calibration rate is missing, non-finite, or non-positive");
        if (parity is not "+" and not "-") failures.Add($"{axis} direction parity is not the PHD2 '+' or '-' protocol value");
    }

    private static void GradeSymmetry(
        string axis,
        double? ratio,
        Phd2CalibrationQualityPolicy policy,
        ref Phd2CalibrationQualityGrade grade,
        ICollection<string> reasons,
        ICollection<string> failures)
    {
        if (!ratio.HasValue)
        {
            Cap(ref grade, Phd2CalibrationQualityGrade.Qualified);
            reasons.Add($"{axis} bidirectional rate symmetry is not exposed by the event-server API; quality cannot be excellent");
        }
        else if (!double.IsFinite(ratio.Value) || ratio.Value < 1)
        {
            failures.Add($"{axis} bidirectional rate ratio is invalid");
        }
        else if (ratio.Value > policy.DegradedMaximumBidirectionalRateRatio)
        {
            failures.Add($"{axis} bidirectional rate ratio {ratio.Value:F2} exceeds the degraded-use limit");
        }
        else if (ratio.Value > policy.QualifiedMaximumBidirectionalRateRatio)
        {
            Cap(ref grade, Phd2CalibrationQualityGrade.DegradedSupervised);
            reasons.Add($"{axis} bidirectional rate ratio {ratio.Value:F2} is degraded");
        }
        else if (ratio.Value > policy.ExcellentMaximumBidirectionalRateRatio)
        {
            Cap(ref grade, Phd2CalibrationQualityGrade.Qualified);
            reasons.Add($"{axis} bidirectional rate ratio {ratio.Value:F2} is qualified");
        }
    }

    private static double? GradeCrossAxisRateRatio(
        Phd2CalibrationData calibration,
        Phd2CalibrationQualityPolicy policy,
        ref Phd2CalibrationQualityGrade grade,
        ICollection<string> reasons,
        ICollection<string> failures)
    {
        if (!IsFinite(calibration.RaRatePixelsPerSecond) || calibration.RaRatePixelsPerSecond <= 0 ||
            !IsFinite(calibration.DecRatePixelsPerSecond) || calibration.DecRatePixelsPerSecond <= 0)
        {
            return null;
        }
        var ratio = Math.Max(calibration.RaRatePixelsPerSecond!.Value, calibration.DecRatePixelsPerSecond!.Value) /
            Math.Min(calibration.RaRatePixelsPerSecond.Value, calibration.DecRatePixelsPerSecond.Value);
        if (ratio > policy.DegradedMaximumCrossAxisRateRatio)
        {
            failures.Add($"RA/Dec cross-axis rate ratio {ratio:F2} exceeds the degraded-use limit");
        }
        else if (ratio > policy.QualifiedMaximumCrossAxisRateRatio)
        {
            Cap(ref grade, Phd2CalibrationQualityGrade.DegradedSupervised);
            reasons.Add($"RA/Dec cross-axis rate ratio {ratio:F2} is degraded; equal RA/Dec rates are not assumed");
        }
        else if (ratio > policy.ExcellentMaximumCrossAxisRateRatio)
        {
            Cap(ref grade, Phd2CalibrationQualityGrade.Qualified);
            reasons.Add($"RA/Dec cross-axis rate ratio {ratio:F2} is qualified; equal RA/Dec rates are not required");
        }
        return ratio;
    }

    private static void GradeSettle(
        Phd2CalibrationSettleEvidence? settle,
        Phd2CalibrationQualityPolicy policy,
        DateTimeOffset evaluatedUtc,
        ref Phd2CalibrationQualityGrade grade,
        ICollection<string> reasons,
        ICollection<string> failures)
    {
        if (settle is null)
        {
            failures.Add("actual guide/settle evidence is missing");
            return;
        }
        var age = evaluatedUtc - settle.Result.CompletedUtc;
        if (string.IsNullOrWhiteSpace(settle.EvidenceId) || !settle.Result.Succeeded ||
            !settle.GuideCommandAccepted || !settle.SettleBeginObserved ||
            !settle.SameConnectionEpoch || !settle.SameGuideEpoch)
        {
            failures.Add("settle is unsuccessful, unsolicited, or from a different connection/guide epoch");
        }
        if (age < TimeSpan.Zero || age > policy.MaximumSettleEvidenceAge || settle.EvaluatedUtc < settle.Result.CompletedUtc)
        {
            failures.Add("settle evidence is stale or temporally inconsistent");
        }
        if (settle.Result.TotalFrames <= 0 || settle.Result.DroppedFrames < 0 || settle.Result.DroppedFrames > settle.Result.TotalFrames)
        {
            failures.Add("settle frame counters are invalid");
            return;
        }
        var dropped = settle.Result.DroppedFrames / (double)settle.Result.TotalFrames;
        if (dropped > policy.DegradedMaximumDroppedFrameFraction)
        {
            failures.Add($"settle dropped-frame fraction {dropped:P1} exceeds the degraded-use limit");
        }
        else if (dropped > policy.QualifiedMaximumDroppedFrameFraction)
        {
            Cap(ref grade, Phd2CalibrationQualityGrade.DegradedSupervised);
            reasons.Add($"settle dropped-frame fraction {dropped:P1} is degraded");
        }
        else if (dropped > policy.ExcellentMaximumDroppedFrameFraction)
        {
            Cap(ref grade, Phd2CalibrationQualityGrade.Qualified);
            reasons.Add($"settle dropped-frame fraction {dropped:P1} is qualified");
        }
    }

    private static void GradeResidual(
        Phd2CalibrationResidualEvidence? residual,
        Phd2CalibrationQualityPolicy policy,
        DateTimeOffset evaluatedUtc,
        ref Phd2CalibrationQualityGrade grade,
        ICollection<string> reasons,
        ICollection<string> failures)
    {
        if (residual is null)
        {
            failures.Add("fresh target/slit residual evidence is missing");
            return;
        }
        var age = evaluatedUtc - residual.CapturedUtc;
        if (!Sha256Pattern().IsMatch(residual.FrameSha256 ?? string.Empty)) failures.Add("fresh residual frame SHA-256 is invalid");
        if (age < TimeSpan.Zero || age > policy.MaximumResidualEvidenceAge || residual.EvaluatedUtc < residual.CapturedUtc)
            failures.Add("fresh residual evidence is stale or temporally inconsistent");
        if (!residual.TargetIdentityConfirmed) failures.Add("fresh residual does not retain confirmed target identity");
        if (!residual.TopologyMatched) failures.Add("fresh residual frame topology differs from the calibration topology");
        if (!residual.NoUnvalidatedCalibrationOrLockShiftAfterMeasurement)
            failures.Add("a calibration or exact-lock shift occurred after the fresh residual measurement");
        if (!double.IsFinite(residual.ResidualPixels) || residual.ResidualPixels < 0 ||
            !double.IsFinite(residual.MaximumResidualPixels) || residual.MaximumResidualPixels <= 0)
        {
            failures.Add("fresh residual value or tolerance is invalid");
        }
        else if (residual.ResidualPixels > residual.MaximumResidualPixels)
        {
            failures.Add($"fresh target/slit residual {residual.ResidualPixels:F2}px exceeds {residual.MaximumResidualPixels:F2}px");
        }
        else
        {
            var fraction = residual.ResidualPixels / residual.MaximumResidualPixels;
            if (fraction > 0.8)
            {
                Cap(ref grade, Phd2CalibrationQualityGrade.Qualified);
                reasons.Add($"fresh target/slit residual {residual.ResidualPixels:F2}px is close to the {residual.MaximumResidualPixels:F2}px limit");
            }
        }
    }

    private static double Score(
        Phd2CalibrationQualityCandidate candidate,
        Phd2CalibrationQualityGrade grade,
        Phd2CalibrationQualityPolicy policy)
    {
        var score = (int)grade * 1000d;
        score -= Math.Min(candidate.Validation.OrthogonalityErrorDegrees ?? policy.DegradedMaximumOrthogonalityErrorDegrees, 90) * 10;
        score -= Math.Min(candidate.Validation.CalibrationAge?.TotalHours ?? policy.DegradedMaximumAge.TotalHours, 100_000) / 1000d;
        if (candidate.Settle?.Result is { TotalFrames: > 0 } settle)
            score -= 100d * settle.DroppedFrames / settle.TotalFrames;
        if (candidate.FreshResidual is { MaximumResidualPixels: > 0 } residual)
            score -= 50d * residual.ResidualPixels / residual.MaximumResidualPixels;
        return score;
    }

    private static void ValidatePolicy(Phd2CalibrationQualityPolicy policy)
    {
        static bool Fraction(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
        if (string.IsNullOrWhiteSpace(policy.PolicyId) ||
            policy.ExcellentMaximumAge <= TimeSpan.Zero ||
            policy.QualifiedMaximumAge < policy.ExcellentMaximumAge ||
            policy.DegradedMaximumAge < policy.QualifiedMaximumAge ||
            !Ordered(policy.ExcellentMaximumOrthogonalityErrorDegrees, policy.QualifiedMaximumOrthogonalityErrorDegrees, policy.DegradedMaximumOrthogonalityErrorDegrees, 90) ||
            !Ordered(policy.ExcellentMaximumBidirectionalRateRatio, policy.QualifiedMaximumBidirectionalRateRatio, policy.DegradedMaximumBidirectionalRateRatio, double.PositiveInfinity) ||
            !Ordered(policy.ExcellentMaximumCrossAxisRateRatio, policy.QualifiedMaximumCrossAxisRateRatio, policy.DegradedMaximumCrossAxisRateRatio, double.PositiveInfinity) ||
            !Fraction(policy.ExcellentMaximumDroppedFrameFraction) ||
            !Fraction(policy.QualifiedMaximumDroppedFrameFraction) ||
            !Fraction(policy.DegradedMaximumDroppedFrameFraction) ||
            policy.QualifiedMaximumDroppedFrameFraction < policy.ExcellentMaximumDroppedFrameFraction ||
            policy.DegradedMaximumDroppedFrameFraction < policy.QualifiedMaximumDroppedFrameFraction ||
            policy.MaximumSettleEvidenceAge <= TimeSpan.Zero ||
            policy.MaximumResidualEvidenceAge <= TimeSpan.Zero ||
            !UnitIntervalExclusiveZero(policy.QualifiedMaximumLockShiftScale) ||
            !UnitIntervalExclusiveZero(policy.DegradedMaximumLockShiftScale) ||
            policy.DegradedMaximumLockShiftScale > policy.QualifiedMaximumLockShiftScale ||
            !UnitIntervalExclusiveZero(policy.QualifiedResidualToleranceScale) ||
            !UnitIntervalExclusiveZero(policy.DegradedResidualToleranceScale) ||
            policy.DegradedResidualToleranceScale > policy.QualifiedResidualToleranceScale ||
            policy.RequiredFreshResidualsPerLockShiftStage <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "PHD2 calibration quality policy thresholds must be finite, positive, and monotonically ordered.");
        }
    }

    private static bool Ordered(double excellent, double qualified, double degraded, double upperExclusive) =>
        double.IsFinite(excellent) && double.IsFinite(qualified) && double.IsFinite(degraded) &&
        excellent >= 0 && qualified >= excellent && degraded >= qualified && degraded < upperExclusive;

    private static bool UnitIntervalExclusiveZero(double value) => double.IsFinite(value) && value is > 0 and <= 1;

    private static void Cap(ref Phd2CalibrationQualityGrade grade, Phd2CalibrationQualityGrade maximum)
    {
        if (grade > maximum) grade = maximum;
    }

    private static bool IsFinite(double? value) => value.HasValue && double.IsFinite(value.Value);

    [GeneratedRegex("^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
