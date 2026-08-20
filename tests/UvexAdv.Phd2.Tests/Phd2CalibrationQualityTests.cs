using UvexAdv.Phd2;

namespace UvexAdv.Phd2.Tests;

public sealed class Phd2CalibrationQualityTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ElevenPointSevenDegreesIsDegradedAndUsableUnderExplicitSupervision()
    {
        var candidate = Candidate(orthogonalityDegrees: 11.7);

        var assessment = Phd2CalibrationQualityEvaluator.Evaluate(
            candidate,
            Phd2CalibrationQualityPolicy.Default,
            Now);

        Assert.Equal(Phd2CalibrationQualityGrade.DegradedSupervised, assessment.Grade);
        Assert.True(assessment.IsUsableForSupervisedGuiding);
        Assert.True(assessment.CanAttemptValidationGuide);
        Assert.True(assessment.IsLockShiftAuthority);
        Assert.False(assessment.IsUnattendedScienceAuthority);
        Assert.True(assessment.RequiresOperatorSupervision);
        Assert.Equal(0.5, assessment.MaximumLockShiftScale);
        Assert.Equal(0.75, assessment.RequiredResidualToleranceScale);
        Assert.Equal(1, assessment.RequiredFreshResidualsPerLockShiftStage);
        Assert.Contains(assessment.Reasons, reason => reason.Contains("11.7", StringComparison.Ordinal));
        Assert.Empty(assessment.HardFailures);
    }

    [Fact]
    public void LargeOrthogonalityErrorIsRejectedEvenWhenSettleAndResidualPass()
    {
        var candidate = Candidate(orthogonalityDegrees: 40.6);

        var assessment = Phd2CalibrationQualityEvaluator.Evaluate(
            candidate,
            Phd2CalibrationQualityPolicy.Default,
            Now);

        Assert.Equal(Phd2CalibrationQualityGrade.Rejected, assessment.Grade);
        Assert.False(assessment.IsUsableForSupervisedGuiding);
        Assert.Contains(assessment.HardFailures, reason => reason.Contains("40.6", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownPierProvenanceCapsCandidateAtDegradedButMismatchRejects()
    {
        var unknown = Phd2CalibrationQualityEvaluator.Evaluate(
            Candidate(orthogonalityDegrees: 4) with { CalibrationPierSideMatched = null },
            Phd2CalibrationQualityPolicy.Default,
            Now);
        var mismatch = Phd2CalibrationQualityEvaluator.Evaluate(
            Candidate(orthogonalityDegrees: 4) with { CalibrationPierSideMatched = false },
            Phd2CalibrationQualityPolicy.Default,
            Now);

        Assert.Equal(Phd2CalibrationQualityGrade.DegradedSupervised, unknown.Grade);
        Assert.True(unknown.IsUsableForSupervisedGuiding);
        Assert.False(unknown.IsUnattendedScienceAuthority);
        Assert.Equal(Phd2CalibrationQualityGrade.Rejected, mismatch.Grade);
        Assert.Contains(mismatch.HardFailures, reason => reason.Contains("pier side", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnsolicitedSettleCannotBecomeCalibrationAuthority()
    {
        var forged = Candidate(orthogonalityDegrees: 4) with
        {
            Settle = Settle() with { GuideCommandAccepted = false, SettleBeginObserved = false },
        };

        var assessment = Phd2CalibrationQualityEvaluator.Evaluate(
            forged,
            Phd2CalibrationQualityPolicy.Default,
            Now);

        Assert.Equal(Phd2CalibrationQualityGrade.Rejected, assessment.Grade);
        Assert.Contains(assessment.HardFailures, reason => reason.Contains("unsolicited", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StaleOrPostMutationResidualIsRejected()
    {
        var stale = Candidate(orthogonalityDegrees: 4) with
        {
            FreshResidual = Residual() with { CapturedUtc = Now.AddMinutes(-6) },
        };
        var moved = Candidate(orthogonalityDegrees: 4) with
        {
            FreshResidual = Residual() with { NoUnvalidatedCalibrationOrLockShiftAfterMeasurement = false },
        };

        var staleAssessment = Phd2CalibrationQualityEvaluator.Evaluate(stale, Phd2CalibrationQualityPolicy.Default, Now);
        var movedAssessment = Phd2CalibrationQualityEvaluator.Evaluate(moved, Phd2CalibrationQualityPolicy.Default, Now);

        Assert.Equal(Phd2CalibrationQualityGrade.Rejected, staleAssessment.Grade);
        Assert.Equal(Phd2CalibrationQualityGrade.Rejected, movedAssessment.Grade);
    }

    [Fact]
    public void BestUsableCandidateWinsAndRejectedCandidatesRemainVisible()
    {
        var degraded = Candidate(orthogonalityDegrees: 11.7) with { CandidateId = "degraded" };
        var qualified = Candidate(orthogonalityDegrees: 7) with { CandidateId = "qualified" };
        var rejected = Candidate(orthogonalityDegrees: 40) with { CandidateId = "rejected" };

        var selection = Phd2CalibrationQualityEvaluator.SelectBest(
            [degraded, rejected, qualified],
            Phd2CalibrationQualityPolicy.Default,
            Now);

        Assert.True(selection.HasUsableCandidate);
        Assert.Equal("qualified", selection.Selected!.CandidateId);
        Assert.Equal(3, selection.Assessments.Count);
        Assert.Contains(selection.Assessments, assessment => assessment.CandidateId == "rejected" && assessment.Grade == Phd2CalibrationQualityGrade.Rejected);
    }

    [Fact]
    public void DegradedCandidateNeverSilentlyBecomesUnattendedAuthority()
    {
        var candidate = Candidate(orthogonalityDegrees: 4) with
        {
            CalibrationProcessEvidenceComplete = false,
        };

        var assessment = Phd2CalibrationQualityEvaluator.Evaluate(candidate, Phd2CalibrationQualityPolicy.Default, Now);

        Assert.Equal(Phd2CalibrationQualityGrade.DegradedSupervised, assessment.Grade);
        Assert.True(assessment.IsUsableForSupervisedGuiding);
        Assert.False(assessment.IsUnattendedScienceAuthority);
    }

    [Fact]
    public void PreGuideAssessmentCannotBeSelectedAsOperationallyUsable()
    {
        var candidate = Candidate(orthogonalityDegrees: 4) with
        {
            Phase = Phd2CalibrationEvaluationPhase.PreGuide,
            Settle = null,
            FreshResidual = null,
        };

        var selection = Phd2CalibrationQualityEvaluator.SelectBest(
            [candidate],
            Phd2CalibrationQualityPolicy.Default,
            Now);

        Assert.False(selection.HasUsableCandidate);
        Assert.Null(selection.Selected);
        Assert.Contains(selection.Assessments[0].Reasons, reason => reason.Contains("pre-guide", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PreGuideCandidateCanBeSelectedOnlyToAttemptValidationGuide()
    {
        var candidate = Candidate(orthogonalityDegrees: 11.7) with
        {
            Phase = Phd2CalibrationEvaluationPhase.PreGuide,
            Settle = null,
            FreshResidual = null,
        };

        var preGuide = Phd2CalibrationQualityEvaluator.SelectBest(
            [candidate],
            Phd2CalibrationQualityPolicy.Default,
            Now,
            Phd2CalibrationSelectionPurpose.ValidationGuide);
        var lockShift = Phd2CalibrationQualityEvaluator.SelectBest(
            [candidate],
            Phd2CalibrationQualityPolicy.Default,
            Now,
            Phd2CalibrationSelectionPurpose.LockShift);

        Assert.True(preGuide.HasUsableCandidate);
        Assert.True(preGuide.Selected!.CanAttemptValidationGuide);
        Assert.False(preGuide.Selected.IsLockShiftAuthority);
        Assert.False(preGuide.Selected.IsUnattendedScienceAuthority);
        Assert.False(lockShift.HasUsableCandidate);
    }

    [Fact]
    public void LegacyValidatorReceivesDegradedHardCeilingRatherThanQualifiedBoundary()
    {
        var legacy = new Phd2CalibrationRequirement(
            2,
            "c11+ccdt67+slit+2210",
            Now,
            TimeSpan.FromDays(7),
            MaximumOrthogonalityErrorDegrees: 10);

        var graded = Phd2CalibrationQualityPolicy.Default.ApplyHardRejectionCeilings(legacy);

        Assert.Equal(TimeSpan.FromDays(30), graded.MaximumAge);
        Assert.Equal(30, graded.MaximumOrthogonalityErrorDegrees);
    }

    [Fact]
    public void CrossAxisRateRatioIsGradedWithoutAssumingUnity()
    {
        var source = Candidate(orthogonalityDegrees: 4);
        var calibration = source.Validation.Calibration with
        {
            RaRatePixelsPerSecond = 20,
            DecRatePixelsPerSecond = 40,
        };
        var candidate = source with
        {
            Validation = source.Validation with { Calibration = calibration },
        };

        var assessment = Phd2CalibrationQualityEvaluator.Evaluate(
            candidate,
            Phd2CalibrationQualityPolicy.Default,
            Now);

        Assert.Equal(2, assessment.CrossAxisRateRatio);
        Assert.Equal(Phd2CalibrationQualityGrade.Qualified, assessment.Grade);
        Assert.True(assessment.IsLockShiftAuthority);
        Assert.Contains(assessment.Reasons, reason => reason.Contains("cross-axis", StringComparison.OrdinalIgnoreCase));
    }

    private static Phd2CalibrationQualityCandidate Candidate(double orthogonalityDegrees)
    {
        var validation = new Phd2CalibrationValidation(
            new Phd2Profile(2, "c11+ccdt67+slit+2210"),
            new Phd2CalibrationData(
                Calibrated: true,
                RaAngleDegrees: 65.4,
                RaRatePixelsPerSecond: 23.1,
                RaParity: "+",
                DecAngleDegrees: 155.4 + orthogonalityDegrees,
                DecRatePixelsPerSecond: 27.888,
                DecParity: "+",
                DeclinationDegrees: 15.2),
            EvaluatedUtc: Now.AddSeconds(-20),
            CalibrationAge: TimeSpan.FromHours(1),
            OrthogonalityErrorDegrees: orthogonalityDegrees,
            Failures: [],
            IndeterminateReasons: []);
        return new Phd2CalibrationQualityCandidate(
            CandidateId: "current",
            Validation: validation,
            Phase: Phd2CalibrationEvaluationPhase.PostSettle,
            ProfileEvidenceMatched: true,
            EquipmentIdentityMatched: true,
            CalibrationTopologyMatched: true,
            CalibrationPierSideMatched: true,
            CalibrationProcessEvidenceComplete: true,
            RaBidirectionalRateRatio: 1.1,
            DecBidirectionalRateRatio: 1.15,
            Settle: Settle(),
            FreshResidual: Residual());
    }

    private static Phd2CalibrationSettleEvidence Settle() => new(
        EvidenceId: "guide-operation-42",
        Result: new Phd2SettleResult(true, null, 20, 0, Now.AddSeconds(-5)),
        GuideCommandAccepted: true,
        SettleBeginObserved: true,
        SameConnectionEpoch: true,
        SameGuideEpoch: true,
        EvaluatedUtc: Now);

    private static Phd2CalibrationResidualEvidence Residual() => new(
        FrameSha256: new string('a', 64),
        CapturedUtc: Now.AddSeconds(-30),
        ResidualPixels: 0.5,
        MaximumResidualPixels: 2,
        TargetIdentityConfirmed: true,
        TopologyMatched: true,
        NoUnvalidatedCalibrationOrLockShiftAfterMeasurement: true,
        EvaluatedUtc: Now);
}
