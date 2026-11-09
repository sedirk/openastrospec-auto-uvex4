using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class ClockProbeOwnerReadbackTests
{
    [Theory]
    [InlineData(Phd2AppState.LostLock)]
    [InlineData(Phd2AppState.Looping)]
    [InlineData(Phd2AppState.Selected)]
    public void OnlyOwnSingleFreshStoppedReconciliationCanAdvanceEpoch(Phd2AppState cached)
    {
        var before = Phd2StateSnapshot.Disconnected with { IsConnected = true, ConnectionEpoch = 7, GuideEpoch = 10, AppState = cached };
        var change = new Phd2GuideEpochChange(10, 11, nameof(Phd2Client.GetAppStateAsync), 9, DateTimeOffset.UtcNow, cached, Phd2AppState.Stopped);
        var after = before with { AppState = Phd2AppState.Stopped, GuideEpoch = 11, LastGuideEpochChange = change };
        Assert.True(RealObservationStageRunner.ClockProbeReadbackIsSameStoppedOwner(before, after));
        Assert.False(RealObservationStageRunner.ClockProbeReadbackIsSameStoppedOwner(before, after with { ConnectionEpoch = 8 }));
        Assert.False(RealObservationStageRunner.ClockProbeReadbackIsSameStoppedOwner(before, after with { GuideEpoch = 12 }));
        Assert.False(RealObservationStageRunner.ClockProbeReadbackIsSameStoppedOwner(before, after with { IsConnected = false }));
        Assert.False(RealObservationStageRunner.ClockProbeReadbackIsSameStoppedOwner(before, after with { LastGuideEpochChange = null }));
        Assert.False(RealObservationStageRunner.ClockProbeReadbackIsSameStoppedOwner(before,
            after with { LastGuideEpochChange = change with { Cause = "HandleEvent:GuidingStopped" } }));
        Assert.False(RealObservationStageRunner.ClockProbeReadbackIsSameStoppedOwner(before,
            after with { LastGuideEpochChange = change with { PreviousState = Phd2AppState.Guiding } }));
        foreach (var busy in new[] { Phd2AppState.Looping, Phd2AppState.Selected, Phd2AppState.Guiding, Phd2AppState.Calibrating, Phd2AppState.Unknown })
            Assert.False(RealObservationStageRunner.ClockProbeReadbackIsSameStoppedOwner(before, after with { AppState = busy }));
        Assert.True(RealObservationStageRunner.ClockProbeReadbackIsSameStoppedOwner(after, after));
        Assert.False(RealObservationStageRunner.ClockProbeReadbackIsSameStoppedOwner(before with { AppState = Phd2AppState.Guiding },
            after with { LastGuideEpochChange = change with { PreviousState = Phd2AppState.Guiding } }));
    }
}
