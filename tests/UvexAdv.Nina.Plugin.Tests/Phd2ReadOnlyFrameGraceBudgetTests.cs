using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2ReadOnlyFrameGraceBudgetTests
{
    private static Phd2StateSnapshot Guiding() => Phd2StateSnapshot.Disconnected with
    {
        IsConnected = true, AppState = Phd2AppState.Guiding, ConnectionEpoch = 4,
        GuideEpoch = 7, LockPosition = new Phd2Point(1135.78, 967.12),
    };
    private static Phd2CommandTimeoutException FrameTimeout() =>
        new("fresh guiding-frame evidence", TimeSpan.FromSeconds(10));

    [Fact]
    public void OneWindowHasOnlyOneExtraWaitAndNoFreshnessRelaxation()
    {
        var budget = new Phd2ReadOnlyFrameGraceBudget();
        var snapshot = Guiding();
        Assert.True(budget.TryConsume(FrameTimeout(), snapshot, snapshot));
        Assert.False(budget.TryConsume(FrameTimeout(), snapshot, snapshot));
        Assert.Equal(TimeSpan.FromSeconds(20), Phd2ReadOnlyFrameGraceBudget.ExtraWait);
    }

    [Theory]
    [InlineData("save_image")]
    [InlineData("guide settle")]
    [InlineData("get_app_state")]
    public void AmbiguousCommandsAreNeverRetried(string operation)
    {
        var budget = new Phd2ReadOnlyFrameGraceBudget();
        Assert.False(budget.TryConsume(new(operation, TimeSpan.FromSeconds(10)), Guiding(), Guiding()));
    }

    [Fact]
    public void DisconnectLossPauseEpochOrLockChangePreventGrace()
    {
        var start = Guiding();
        var invalid = new[]
        {
            start with { IsConnected = false }, start with { AppState = Phd2AppState.LostLock },
            start with { AppState = Phd2AppState.Stopped }, start with { Phd2Paused = true },
            start with { AutomationPaused = true }, start with { PendingSettleOperationId = 3 },
            start with { ConnectionEpoch = 5 }, start with { GuideEpoch = 8 },
            start with { LockPosition = new Phd2Point(1135.8, 967.12) },
            start with { LockPosition = null }, start with { LockPosition = new Phd2Point(double.NaN, 967.12) },
        };
        foreach (var current in invalid)
            Assert.False(new Phd2ReadOnlyFrameGraceBudget().TryConsume(FrameTimeout(), start, current));
    }

    [Fact]
    public void OnlyPreExposureReadOnlyWindowOptsInAndRetryStillWaitsForFreshEvidence()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.Phd2SlitPlacement.cs"));
        Assert.Equal(1, source.Split("new Phd2ReadOnlyFrameGraceBudget()", StringSplitOptions.None).Length - 1);
        var preAtr = source.IndexOf("private async Task VerifyWindSampledGuidingBeforeAtrAsync(", StringComparison.Ordinal);
        Assert.True(source.IndexOf("new Phd2ReadOnlyFrameGraceBudget()", StringComparison.Ordinal) > preAtr);
        Assert.Contains("cancellationToken, readoutGrace)", source[preAtr..]);
        var start = source.IndexOf("catch (Phd2CommandTimeoutException ex) when (", StringComparison.Ordinal);
        var end = source.IndexOf("var residualMountReadback", start, StringComparison.Ordinal);
        var branch = source[start..end];
        Assert.Contains("!File.Exists(frameRequest.DestinationPath)", branch);
        Assert.Contains("RequireImmediatePhysicalActionGatesAsync", branch);
        Assert.Contains("SaveCurrentGuidingFrameAsync", branch);
        Assert.Contains("SameGuiding(beforeFrameWait, phd2.Snapshot)", branch);
        Assert.DoesNotContain("StopCapture", branch);
        Assert.DoesNotContain("SetLockPosition", branch);
        Assert.DoesNotContain("StartLooping", branch);
    }
}
