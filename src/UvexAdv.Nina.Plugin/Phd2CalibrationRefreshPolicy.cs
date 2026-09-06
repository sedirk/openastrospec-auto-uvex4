namespace UvexAdv.Nina.Plugin;

internal static class Phd2CalibrationRefreshPolicy
{
    internal static bool ShouldRefresh(TimeSpan? age, double? orthogonalityError,
        TimeSpan qualifiedMaximumAge, double qualifiedOrthogonalityLimit,
        bool alreadyAttempted, bool postCalibrationReacquisition, bool outstandingMotion) =>
        !alreadyAttempted && !postCalibrationReacquisition && !outstandingMotion &&
        age is { } knownAge && knownAge > qualifiedMaximumAge &&
        orthogonalityError is { } error && double.IsFinite(error) && error > qualifiedOrthogonalityLimit;
}
