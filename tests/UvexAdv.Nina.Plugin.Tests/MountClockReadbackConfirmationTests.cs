using UvexAdv.Nina.Plugin;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class MountClockReadbackConfirmationTests
{
    [Fact] public void TwoDayOutlierThenTwoCurrentOwnerReadbacksNeedsNoReconnect()
    {
        var now = DateTimeOffset.Parse("2026-09-15T16:07:53Z");
        var samples = new MountClockReadbackConfirmation();
        Assert.False(samples.Observe(MountClockGate.Evaluate(now.AddDays(-2).UtcDateTime, now, TimeSpan.FromSeconds(60))));
        var good = MountClockGate.Evaluate(now.UtcDateTime, now, TimeSpan.FromSeconds(60));
        Assert.False(samples.Observe(good)); Assert.True(samples.Observe(good));
    }
    [Fact] public void InterveningInvalidSampleResetsConsecutiveProof()
    {
        var samples = new MountClockReadbackConfirmation();
        Assert.False(samples.Observe(GateResult.Pass("ok", "")));
        Assert.False(samples.Observe(GateResult.Unknown("unavailable", "")));
        Assert.False(samples.Observe(GateResult.Pass("ok", "")));
        Assert.True(samples.Observe(GateResult.Pass("ok", "")));
    }
    [Theory]
    [InlineData(ObservationStage.SelectAtrExposure)] [InlineData(ObservationStage.RunScienceBlock)]
    [InlineData(ObservationStage.PlaceTargetOnSlit)] [InlineData(ObservationStage.StartGuiding)]
    public void ClockRepairCannotDestroyAnAcquiredGuide(ObservationStage stage) =>
        Assert.False(MountClockReadbackConfirmation.MayReconnect(stage, false));
    [Fact] public void ReconnectOnlyInNightSetupBeforeAcquiredGuiding()
    {
        Assert.True(MountClockReadbackConfirmation.MayReconnect(ObservationStage.ValidateNightSetup, false));
        Assert.False(MountClockReadbackConfirmation.MayReconnect(ObservationStage.ValidateNightSetup, true));
    }
}
