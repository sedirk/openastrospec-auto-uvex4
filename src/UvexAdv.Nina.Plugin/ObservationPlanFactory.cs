using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal static class ObservationPlanFactory
{
    public static ObservationPlan FromSettings(
        UvexPluginSettings settings,
        RealRunConfiguration? lockedConfiguration = null)
    {
        var binding = lockedConfiguration?.Commissioning;
        var motion = binding is null
            ? new MotionLimits(
                settings.MaximumSingleCorrectionArcseconds / 3600d,
                settings.MaximumCumulativeCorrectionArcseconds / 3600d,
                settings.MaximumCorrectionAttempts,
                TimeSpan.FromMinutes(settings.MaximumAcquisitionMinutes))
            : new MotionLimits(
                binding.MaximumSingleCorrectionArcseconds / 3600d,
                binding.MaximumCumulativeCorrectionArcseconds / 3600d,
                binding.MaximumCorrectionAttempts,
                TimeSpan.FromMinutes(binding.MaximumAcquisitionMinutes));
        return Create(
            settings.ObservationTargetName,
            settings.ObservationCatalogId,
            settings.ObservationRightAscensionDegrees,
            settings.ObservationDeclinationDegrees,
            settings.ObservationDurationMinutes,
            settings.ObservationNightSetupId,
            settings.ObservatoryLatitudeDegrees,
            settings.ObservatoryLongitudeDegreesEast,
            settings.ObservatoryElevationMeters,
            settings.HorizonMinimumDegrees,
            settings.HorizonStartMarginDegrees,
            settings.HorizonContinueMarginDegrees,
            settings.ObservationExpectedAtrCameraId,
            settings.ObservationExpectedG3ProfileName,
            settings.ObservationExpectedQhyCameraId,
            motion,
            lockedConfiguration?.Environment.RequireSafetyMonitor ?? settings.RequireSafetyMonitor,
            settings.ObservationTargetObservability);
    }

    public static ObservationPlan Create(
        string targetName,
        string catalogId,
        double rightAscensionDegrees,
        double declinationDegrees,
        double durationMinutes,
        string nightSetupId,
        double siteLatitudeDegrees,
        double siteLongitudeDegreesEast,
        double siteElevationMeters,
        double horizonMinimumDegrees,
        double horizonStartMarginDegrees,
        double horizonContinueMarginDegrees,
        string expectedAtrCameraId,
        string expectedG3ProfileName,
        string expectedQhyCameraId,
        MotionLimits motion,
        bool requireSafetyMonitor,
        TargetObservabilityClass targetObservability = TargetObservabilityClass.DirectStellar)
    {
        var now = DateTimeOffset.UtcNow;
        return new ObservationPlan(
            $"UVEX-{now:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}"[..37],
            nightSetupId,
            new EquatorialTarget(targetName, catalogId, rightAscensionDegrees, declinationDegrees),
            new ObservatorySite(siteLatitudeDegrees, siteLongitudeDegreesEast, siteElevationMeters),
            now,
            TimeSpan.FromMinutes(durationMinutes),
            new HorizonPolicy(
                horizonMinimumDegrees,
                horizonStartMarginDegrees,
                horizonContinueMarginDegrees),
            motion,
            expectedAtrCameraId,
            expectedG3ProfileName,
            expectedQhyCameraId,
            requireSafetyMonitor,
            targetObservability);
    }
}
