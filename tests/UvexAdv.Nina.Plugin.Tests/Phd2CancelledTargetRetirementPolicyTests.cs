using UvexAdv.Nina.Plugin;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2CancelledTargetRetirementPolicyTests
{
    private static readonly EquatorialTarget Old = new("Deneb", "HIP 102098", 310.35798, 45.28034);
    private static readonly EquatorialTarget New = new("Mirach", "HIP 5447", 17.433016, 35.620558);

    [Fact]
    public void OnlyCancelledDisjointTargetCanBeRetired()
    {
        Assert.True(Phd2CancelledTargetRetirementPolicy.CanRetire(ObservationRunState.Cancelled, Old, New));
        Assert.False(Phd2CancelledTargetRetirementPolicy.CanRetire(null, Old, New));
        Assert.False(Phd2CancelledTargetRetirementPolicy.CanRetire(ObservationRunState.Completed, Old, New));
        Assert.False(Phd2CancelledTargetRetirementPolicy.CanRetire(ObservationRunState.PausedNeedsAttention, Old, New));
        Assert.False(Phd2CancelledTargetRetirementPolicy.CanRetire(ObservationRunState.Cancelled, Old, Old));
        Assert.False(Phd2CancelledTargetRetirementPolicy.CanRetire(ObservationRunState.Cancelled, Old, Old with { CatalogId = "Renamed" }));
        Assert.False(Phd2CancelledTargetRetirementPolicy.CanRetire(ObservationRunState.Cancelled, Old, New with { CatalogId = "" }));
    }

    [Fact]
    public void SameTargetRequiresARealNewerHomeBoundaryInTheCurrentRun()
    {
        var source = DateTimeOffset.Parse("2026-09-05T16:00:00Z");
        var now = source.AddHours(18);
        Assert.True(Phd2CancelledTargetRetirementPolicy.CanRetireAfterVerifiedHome(
            ObservationRunState.Cancelled, source, now.AddMinutes(-1), now, true));
        Assert.False(Phd2CancelledTargetRetirementPolicy.CanRetireAfterVerifiedHome(
            ObservationRunState.Cancelled, source, null, now, true));
        Assert.False(Phd2CancelledTargetRetirementPolicy.CanRetireAfterVerifiedHome(
            ObservationRunState.Cancelled, source, source.AddSeconds(-1), now, true));
        Assert.False(Phd2CancelledTargetRetirementPolicy.CanRetireAfterVerifiedHome(
            ObservationRunState.Cancelled, source, now.AddSeconds(1), now, true));
        Assert.False(Phd2CancelledTargetRetirementPolicy.CanRetireAfterVerifiedHome(
            ObservationRunState.Cancelled, source, now.AddMinutes(-1), now, false));
        Assert.False(Phd2CancelledTargetRetirementPolicy.CanRetireAfterVerifiedHome(
            ObservationRunState.RunningAuto, source, now.AddMinutes(-1), now, true));
    }
}
