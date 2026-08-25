using UvexAdv.Phd2;

namespace UvexAdv.Phd2.Tests;

public sealed class Phd2SlitLockShiftPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OffSlitFormulaUsesGuidePlusTargetToSlitDeltaAndSegments()
    {
        var fixture = CreateFixture();
        var measurement = Measurement(
            guide: new Phd2Point(100, 100),
            target: new Phd2Point(200, 200),
            slit: new Phd2Point(215, 200));

        var result = Phd2SlitLockShiftPlanner.PlanOutboundStage(
            fixture.Qualification,
            Phd2SlitGuideMode.OffSlitGuideStar,
            measurement,
            fixture.Ledger,
            fixture.Safety,
            fixture.Topology,
            fixture.MotionLimits,
            Now);

        Assert.True(result.IsAllowed, $"{result.Code}: {result.Message}; qualification={string.Join(" | ", fixture.Qualification.Failures)}");
        Assert.False(result.IsComplete);
        Assert.Equal(new Phd2Point(215, 200), measurement.RecognizedSlitAcquisitionPoint);
        Assert.Equal(new Phd2Point(15, 0), result.Stage!.TargetToSlitDelta);
        Assert.Equal(new Phd2Point(115, 100), result.Stage.FullDesiredLockPosition);
        Assert.Equal(new Phd2Point(110, 100), result.Stage.RequestedLockPosition);
        Assert.Equal(10, result.Stage.StagePixels, 9);
        Assert.True(result.Stage.RequiresFreshG3ResidualAfter);
        Assert.True(result.Stage.RequiresFreshLockVerificationAfter);
        Assert.False(result.Stage.RegistryProfileMutationAllowed);
        Assert.False(result.Stage.AutomaticRetryAllowed);
    }

    [Fact]
    public void CatalogWcsGeometryCanPlanOffSlitShiftWithoutFabricatedTargetFlux()
    {
        var fixture = CreateFixture();
        var measurement = Measurement(
            guide: new Phd2Point(100, 100),
            target: new Phd2Point(200, 200),
            slit: new Phd2Point(215, 200),
            fluxLabel: "CATALOG_WCS_TARGET_FLUX_NOT_APPLICABLE",
            fluxMetric: 0,
            targetPositionAuthority: Phd2TargetPositionAuthority.CatalogWcsProjection);

        var result = Phd2SlitLockShiftPlanner.PlanOutboundStage(
            fixture.Qualification,
            Phd2SlitGuideMode.OffSlitGuideStar,
            measurement,
            fixture.Ledger,
            fixture.Safety,
            fixture.Topology,
            fixture.MotionLimits,
            Now);

        Assert.True(result.IsAllowed, $"{result.Code}: {result.Message}");
        Assert.Equal(new Phd2Point(15, 0), result.Stage!.TargetToSlitDelta);
    }

    [Fact]
    public void CatalogWcsGeometryRejectsFabricatedFluxEvidence()
    {
        var fixture = CreateFixture();
        var measurement = Measurement(
            guide: new Phd2Point(100, 100),
            target: new Phd2Point(200, 200),
            slit: new Phd2Point(215, 200),
            targetPositionAuthority: Phd2TargetPositionAuthority.CatalogWcsProjection);

        var result = Phd2SlitLockShiftPlanner.PlanOutboundStage(
            fixture.Qualification,
            Phd2SlitGuideMode.OffSlitGuideStar,
            measurement,
            fixture.Ledger,
            fixture.Safety,
            fixture.Topology,
            fixture.MotionLimits,
            Now);

        Assert.False(result.IsAllowed);
        Assert.Equal("G3_CATALOG_WCS_AUTHORITY_INVALID", result.Code);
    }

    [Fact]
    public void TargetAlreadyOnSlitEdgeStillMovesToRecognizedMidpoint()
    {
        var fixture = CreateFixture();
        var measurement = Measurement(
            guide: new Phd2Point(100, 100),
            target: new Phd2Point(165, 200),
            slit: new Phd2Point(215, 200));

        var result = Phd2SlitLockShiftPlanner.PlanOutboundStage(
            fixture.Qualification,
            Phd2SlitGuideMode.OffSlitGuideStar,
            measurement,
            fixture.Ledger,
            fixture.Safety,
            fixture.Topology,
            fixture.MotionLimits,
            Now);

        Assert.True(result.IsAllowed);
        Assert.False(result.IsComplete);
        Assert.Equal(new Phd2Point(50, 0), result.Stage!.TargetToSlitDelta);
        Assert.Equal(new Phd2Point(150, 100), result.Stage.FullDesiredLockPosition);
    }

    [Fact]
    public void DegradedDirectTargetGuidingWorksWithoutAnOffSlitStarAndIsLabeled()
    {
        var fixture = CreateFixture(currentLock: new Phd2Point(200, 200), originLock: new Phd2Point(200, 200));
        var measurement = Measurement(
            guide: new Phd2Point(200, 200),
            target: new Phd2Point(200, 200),
            slit: new Phd2Point(215, 200),
            guideDistanceFromSlit: 0,
            minimumExposureApplied: true,
            exposureMilliseconds: 10,
            fluxLabel: "ULTRABRIGHT_WING_FLUX_PASS");

        var result = Phd2SlitLockShiftPlanner.PlanOutboundStage(
            fixture.Qualification,
            Phd2SlitGuideMode.DegradedDirectTargetGuiding,
            measurement,
            fixture.Ledger,
            fixture.Safety,
            fixture.Topology,
            fixture.MotionLimits,
            Now);

        Assert.True(result.IsAllowed);
        Assert.True(result.Stage!.Degraded);
        Assert.Equal(new Phd2Point(215, 200), result.Stage.FullDesiredLockPosition);
        Assert.Equal(new Phd2Point(210, 200), result.Stage.RequestedLockPosition);
        Assert.StartsWith("DEGRADED_DIRECT_TARGET_GUIDING:", result.Stage.FluxEvidenceLabel);
        Assert.StartsWith("DEGRADED_DIRECT_TARGET_GUIDING:", result.Stage.ResidualEvidenceLabel);
    }

    [Fact]
    public void DegradedDirectTargetGuidingRejectsUncommissionedShortestExposure()
    {
        var fixture = CreateFixture(currentLock: new Phd2Point(200, 200), originLock: new Phd2Point(200, 200));
        var measurement = Measurement(
            guide: new Phd2Point(200, 200),
            target: new Phd2Point(200, 200),
            slit: new Phd2Point(215, 200),
            guideDistanceFromSlit: 0,
            minimumExposureApplied: false,
            exposureMilliseconds: 10);

        var result = Phd2SlitLockShiftPlanner.PlanOutboundStage(
            fixture.Qualification,
            Phd2SlitGuideMode.DegradedDirectTargetGuiding,
            measurement,
            fixture.Ledger,
            fixture.Safety,
            fixture.Topology,
            fixture.MotionLimits,
            Now);

        Assert.False(result.IsAllowed);
        Assert.Equal("DIRECT_TARGET_MINIMUM_EXPOSURE_UNCOMMISSIONED", result.Code);
    }

    [Fact]
    public void QualificationRejectsPlateSolveSeedAsMotionAuthority()
    {
        var fixture = CreateFixture(rotationAuthority: Phd2SensorRotationAuthority.PlateSolveSeedOnly);

        Assert.False(fixture.Qualification.IsQualified);
        Assert.Contains(fixture.Qualification.Failures, failure => failure.Contains("seed-only", StringComparison.Ordinal));
        Assert.False(fixture.Qualification.PlateSolveRotationUsedForMotion);
    }

    [Fact]
    public void RotationOrTopologyChangeInvalidatesFingerprint()
    {
        var original = Topology(rotation: 12.5);
        var changed = original with { SensorRotationDegrees = 12.6 };
        var fixture = CreateFixture(topology: changed, lockedFingerprint: original.ComputeFingerprintSha256());

        Assert.NotEqual(original.ComputeFingerprintSha256(), changed.ComputeFingerprintSha256());
        Assert.False(fixture.Qualification.IsQualified);
        Assert.Contains(fixture.Qualification.Failures, failure => failure.Contains("fingerprint", StringComparison.Ordinal));
    }

    [Fact]
    public void CroppedFullSensorDomainRejectsRoiLocalCoordinates()
    {
        var topology = Topology() with
        {
            Roi = new Phd2Rectangle(500, 200, 200, 100),
            CoordinateDomain = Phd2ImageCoordinateDomain.FullSensorCoordinates,
        };
        var fixture = CreateFixture(
            topology: topology,
            currentLock: new Phd2Point(50, 50),
            originLock: new Phd2Point(50, 50));
        var result = Phd2SlitLockShiftPlanner.PlanOutboundStage(
            fixture.Qualification,
            Phd2SlitGuideMode.OffSlitGuideStar,
            Measurement(
                new Phd2Point(50, 50),
                new Phd2Point(100, 50),
                new Phd2Point(110, 50),
                topology: topology),
            fixture.Ledger,
            fixture.Safety,
            topology,
            fixture.MotionLimits,
            Now);

        Assert.False(result.IsAllowed);
        Assert.Equal("SLIT_LOCK_POSITION_OUTSIDE_SENSOR", result.Code);
    }

    [Fact]
    public void CroppedRoiLocalDomainAcceptsOnlyRoiLocalBoundsAndChangesFingerprint()
    {
        var fullSensorDomain = Topology() with
        {
            Roi = new Phd2Rectangle(500, 200, 200, 100),
            CoordinateDomain = Phd2ImageCoordinateDomain.FullSensorCoordinates,
        };
        var roiLocalDomain = fullSensorDomain with
        {
            CoordinateDomain = Phd2ImageCoordinateDomain.RoiLocalCoordinates,
        };
        var fixture = CreateFixture(
            topology: roiLocalDomain,
            currentLock: new Phd2Point(50, 50),
            originLock: new Phd2Point(50, 50));
        var result = Phd2SlitLockShiftPlanner.PlanOutboundStage(
            fixture.Qualification,
            Phd2SlitGuideMode.OffSlitGuideStar,
            Measurement(
                new Phd2Point(50, 50),
                new Phd2Point(100, 50),
                new Phd2Point(110, 50),
                topology: roiLocalDomain),
            fixture.Ledger,
            fixture.Safety,
            roiLocalDomain,
            fixture.MotionLimits,
            Now);

        Assert.True(result.IsAllowed);
        Assert.NotEqual(fullSensorDomain.ComputeFingerprintSha256(), roiLocalDomain.ComputeFingerprintSha256());
    }

    [Fact]
    public void QualificationRejectsNonProtocolParityAndInvalidDeclination()
    {
        var fixture = CreateFixture(calibrationData: new Phd2CalibrationData(
            true,
            0,
            10,
            "Even",
            90,
            9,
            "Odd",
            91));

        Assert.False(fixture.Qualification.IsQualified);
        Assert.Contains(fixture.Qualification.Failures, failure => failure.Contains("protocol value", StringComparison.Ordinal));
        Assert.Contains(fixture.Qualification.Failures, failure => failure.Contains("declination", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitIndependentTransformModeDoesNotSilentlyBecomeDefaultAuthority()
    {
        var fixture = CreateFixture(authority: SlitPlacementMappingAuthority.IndependentFourDirectionTransformDiagnostic);

        Assert.False(fixture.Qualification.IsQualified);
        Assert.Contains(fixture.Qualification.Failures, failure => failure.Contains("diagnostic/fallback", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryOutboundStageMustReserveAttemptsAndCumulativeRecovery()
    {
        var fixture = CreateFixture();
        var tight = fixture.MotionLimits with { MaximumAttempts = 2 };
        var result = Phd2SlitLockShiftPlanner.PlanOutboundStage(
            fixture.Qualification,
            Phd2SlitGuideMode.OffSlitGuideStar,
            Measurement(new Phd2Point(100, 100), new Phd2Point(200, 200), new Phd2Point(215, 200)),
            fixture.Ledger,
            fixture.Safety,
            fixture.Topology,
            tight,
            Now);

        Assert.False(result.IsAllowed);
        Assert.Equal("SLIT_LOCK_RETURN_ATTEMPT_RESERVE", result.Code);
    }

    [Fact]
    public void FreshFrameCannotBeReusedAcrossStages()
    {
        var fixture = CreateFixture(lastFrameSha: Hash('A'));
        var measurement = Measurement(
            new Phd2Point(100, 100),
            new Phd2Point(200, 200),
            new Phd2Point(205, 200),
            frameSha: Hash('A'));

        var result = Phd2SlitLockShiftPlanner.PlanOutboundStage(
            fixture.Qualification,
            Phd2SlitGuideMode.OffSlitGuideStar,
            measurement,
            fixture.Ledger,
            fixture.Safety,
            fixture.Topology,
            fixture.MotionLimits,
            Now);

        Assert.False(result.IsAllowed);
        Assert.Equal("G3_FRAME_REUSED", result.Code);
    }

    [Fact]
    public void RecoveryUsesFreshActualLockAndReturnsInBoundedStage()
    {
        var fixture = CreateFixture(
            currentLock: new Phd2Point(110, 100),
            originLock: new Phd2Point(100, 100),
            attempts: 1,
            cumulative: 10);
        var result = Phd2SlitLockShiftPlanner.PlanRecoveryStage(
            fixture.Qualification,
            Phd2SlitGuideMode.OffSlitGuideStar,
            fixture.Ledger,
            fixture.Safety,
            fixture.Topology,
            fixture.MotionLimits,
            Now,
            Hash('B'),
            "target-proof");

        Assert.True(result.IsAllowed);
        Assert.Equal(Phd2LockShiftStageKind.Recovery, result.Stage!.Kind);
        Assert.Equal(new Phd2Point(100, 100), result.Stage.RequestedLockPosition);
        Assert.False(result.Stage.RequiresFreshG3ResidualAfter);
        Assert.True(result.Stage.RequiresFreshLockVerificationAfter);
    }

    [Fact]
    public void AltitudeAndPierRemainHardGatesInDegradedMode()
    {
        var fixture = CreateFixture(currentLock: new Phd2Point(200, 200), originLock: new Phd2Point(200, 200));
        var unsafeSnapshot = fixture.Safety with { PredictedMinimumAltitudeDegrees = 39 };
        var result = Phd2SlitLockShiftPlanner.PlanOutboundStage(
            fixture.Qualification,
            Phd2SlitGuideMode.DegradedDirectTargetGuiding,
            Measurement(new Phd2Point(200, 200), new Phd2Point(200, 200), new Phd2Point(205, 200), 0, true, 10),
            fixture.Ledger,
            unsafeSnapshot,
            fixture.Topology,
            fixture.MotionLimits,
            Now);

        Assert.False(result.IsAllowed);
        Assert.Equal("SLIT_LOCK_ALTITUDE_SAFETY", result.Code);
    }

    [Fact]
    public void ElevenPointSevenDegreeCalibrationIsSupervisedAndStageScaledAfterPostSettleEvidence()
    {
        var fixture = CreateFixture(calibrationData: new Phd2CalibrationData(
            true,
            0,
            10,
            "+",
            78.3,
            9,
            "-",
            10));

        var result = Phd2SlitLockShiftPlanner.PlanOutboundStage(
            fixture.Qualification,
            Phd2SlitGuideMode.OffSlitGuideStar,
            Measurement(new Phd2Point(100, 100), new Phd2Point(200, 200), new Phd2Point(215, 200)),
            fixture.Ledger,
            fixture.Safety,
            fixture.Topology,
            fixture.MotionLimits,
            Now);

        Assert.True(fixture.Qualification.IsQualified);
        Assert.Equal(Phd2CalibrationQualityGrade.DegradedSupervised, fixture.Qualification.CalibrationQualityGrade);
        Assert.True(fixture.Qualification.RequiresOperatorSupervision);
        Assert.False(fixture.Qualification.IsUnattendedScienceAuthority);
        Assert.True(result.IsAllowed);
        Assert.Equal(5, result.Stage!.StagePixels, 9);
        Assert.True(result.Stage.Degraded);
        Assert.False(result.Stage.IsUnattendedScienceAuthority);
        Assert.Equal(0.5, result.Stage.AppliedLockShiftScale, 9);
        Assert.Equal(0.75, result.Stage.AppliedResidualToleranceScale, 9);
    }

    private static Fixture CreateFixture(
        SlitPlacementMappingAuthority authority = SlitPlacementMappingAuthority.GradedPhd2CalibrationLockShift,
        Phd2SensorRotationAuthority rotationAuthority = Phd2SensorRotationAuthority.QualifiedPhd2Calibration,
        Phd2SensorTopology? topology = null,
        string? lockedFingerprint = null,
        Phd2Point? currentLock = null,
        Phd2Point? originLock = null,
        int attempts = 0,
        double cumulative = 0,
        string? lastFrameSha = null,
        Phd2CalibrationData? calibrationData = null)
    {
        topology ??= Topology(rotationAuthority: rotationAuthority);
        var profile = new Phd2Profile(2, "c11+ccdt67+slit+2210");
        var equipment = new Phd2Equipment(
            new Phd2EquipmentDevice(Phd2RuntimeEquipmentConventions.G3CameraName, true),
            new Phd2EquipmentDevice(Phd2RuntimeEquipmentConventions.OnStepMountName, true),
            null,
            null,
            null);
        var identity = new Phd2IdentityValidation(profile, equipment, Array.Empty<string>(), Array.Empty<string>());
        calibrationData ??= new Phd2CalibrationData(true, 0, 10, "+", 90, 9, "-", 10);
        var orthogonality = OrthogonalityError(calibrationData.RaAngleDegrees, calibrationData.DecAngleDegrees);
        var calibration = new Phd2CalibrationValidation(
            profile,
            calibrationData,
            Now - TimeSpan.FromSeconds(1),
            TimeSpan.FromHours(1),
            orthogonality,
            Array.Empty<string>(),
            Array.Empty<string>());
        var qualificationLimits = new Phd2LockShiftQualificationLimits(
            TimeSpan.FromDays(1),
            TimeSpan.FromSeconds(5),
            30,
            0.1,
            100);
        var quality = Phd2CalibrationQualityEvaluator.Evaluate(
            new Phd2CalibrationQualityCandidate(
                "fixture-calibration",
                calibration,
                Phd2CalibrationEvaluationPhase.PostSettle,
                ProfileEvidenceMatched: true,
                EquipmentIdentityMatched: true,
                CalibrationTopologyMatched: true,
                CalibrationPierSideMatched: true,
                CalibrationProcessEvidenceComplete: true,
                RaBidirectionalRateRatio: 1,
                DecBidirectionalRateRatio: 1,
                new Phd2CalibrationSettleEvidence(
                    "fixture-settle",
                    new Phd2SettleResult(true, null, 20, 0, Now - TimeSpan.FromSeconds(1)),
                    GuideCommandAccepted: true,
                    SettleBeginObserved: true,
                    SameConnectionEpoch: true,
                    SameGuideEpoch: true,
                    EvaluatedUtc: Now),
                new Phd2CalibrationResidualEvidence(
                    Hash('C'),
                    Now - TimeSpan.FromSeconds(1),
                    ResidualPixels: 0.5,
                    MaximumResidualPixels: 2,
                    TargetIdentityConfirmed: true,
                    TopologyMatched: true,
                    NoUnvalidatedCalibrationOrLockShiftAfterMeasurement: true,
                    EvaluatedUtc: Now)),
            Phd2CalibrationQualityPolicy.Default,
            Now);
        var qualification = Phd2SlitLockShiftPlanner.Qualify(new Phd2LockShiftQualificationRequest(
            authority,
            identity,
            calibration,
            topology,
            lockedFingerprint ?? topology.ComputeFingerprintSha256(),
            topology.PierSide,
            Now,
            PlateSolveRotationSeedDegrees: 12.4,
            qualificationLimits,
            quality));
        var limits = new Phd2LockShiftLimits(
            MaximumStagePixels: 10,
            MaximumCumulativePixels: 100,
            MaximumAttempts: 10,
            MaximumElapsed: TimeSpan.FromMinutes(5),
            MaximumStageDuration: TimeSpan.FromSeconds(15),
            MaximumMeasurementAge: TimeSpan.FromSeconds(5),
            MaximumSafetySnapshotAge: TimeSpan.FromSeconds(5),
            LockPreconditionTolerancePixels: 0.25,
            LockVerificationTolerancePixels: 0.25,
            TargetOnSlitTolerancePixels: 1,
            MaximumAcquisitionResidualPixels: 50,
            MinimumOffSlitGuideDistancePixels: 5,
            MinimumOffSlitGuideTargetSeparationPixels: 10,
            MaximumGuideLockResidualPixels: 1,
            MaximumDegradedDirectTargetGuideLockResidualPixels: 3,
            MaximumDirectTargetCentroidSeparationPixels: 1,
            MinimumFluxMetric: 10,
            MaximumFluxMetric: 10_000);
        var origin = originLock ?? new Phd2Point(100, 100);
        var current = currentLock ?? origin;
        var ledger = new Phd2LockShiftLedger(
            "lineage-1",
            origin,
            current,
            attempts,
            cumulative,
            Now - TimeSpan.FromSeconds(1),
            lastFrameSha);
        var safety = new Phd2LockShiftSafetySnapshot(true, 60, 55, 45, topology.PierSide, Now - TimeSpan.FromSeconds(1));
        return new Fixture(topology, qualification, limits, ledger, safety);
    }

    private static Phd2SensorTopology Topology(
        double rotation = 12.5,
        Phd2SensorRotationAuthority rotationAuthority = Phd2SensorRotationAuthority.QualifiedPhd2Calibration) => new(
        "install-20260819",
        2,
        "c11+ccdt67+slit+2210",
        Phd2RuntimeEquipmentConventions.G3CameraName,
        @"\\?\usb#vid_0547&pid_14ab#fixture-unit",
        Phd2RuntimeEquipmentConventions.OnStepMountName,
        Hash('E'),
        1920,
        1080,
        1,
        new Phd2Rectangle(0, 0, 1920, 1080),
        Phd2ImageCoordinateDomain.FullSensorCoordinates,
        rotation,
        rotationAuthority,
        "pierEast");

    private static Phd2SlitFieldMeasurement Measurement(
        Phd2Point guide,
        Phd2Point target,
        Phd2Point slit,
        double guideDistanceFromSlit = 25,
        bool minimumExposureApplied = true,
        int exposureMilliseconds = 1000,
        string fluxLabel = "FLUX_PASS",
        double fluxMetric = 100,
        Phd2TargetPositionAuthority targetPositionAuthority = Phd2TargetPositionAuthority.DetectedTargetCentroid,
        string? frameSha = null,
        Phd2SensorTopology? topology = null) => new(
        frameSha ?? Hash('F'),
        Now - TimeSpan.FromSeconds(1),
        (topology ?? Topology()).ComputeFingerprintSha256(),
        guide,
        target,
        slit,
        guideDistanceFromSlit,
        TargetIdentityConfirmed: true,
        exposureMilliseconds,
        minimumExposureApplied,
        "catalog+wcs+continuity",
        fluxLabel,
        fluxMetric,
        "FRESH_G3_TARGET_SLIT_RESIDUAL",
        targetPositionAuthority);

    private static string Hash(char value) => new(value, 64);

    private static double? OrthogonalityError(double? first, double? second)
    {
        if (!first.HasValue || !second.HasValue || !double.IsFinite(first.Value) || !double.IsFinite(second.Value))
            return null;
        var difference = Math.Abs(((second.Value - first.Value) % 360 + 360) % 360);
        if (difference > 180) difference = 360 - difference;
        return Math.Abs(90 - difference);
    }

    private sealed record Fixture(
        Phd2SensorTopology Topology,
        Phd2LockShiftQualification Qualification,
        Phd2LockShiftLimits MotionLimits,
        Phd2LockShiftLedger Ledger,
        Phd2LockShiftSafetySnapshot Safety);
}
