using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2DependencyRebuildStopPolicyTests
{
    private static Phd2StateSnapshot Owned(Phd2AppState state) => Phd2StateSnapshot.Disconnected with
    {
        IsConnected = true, AppState = state, ConnectionEpoch = 7, GuideEpoch = 98,
    };

    [Theory]
    [InlineData(Phd2AppState.Guiding)]
    [InlineData(Phd2AppState.LostLock)]
    public void ChangedLossEpochDoesNotPreventCheckedStopOfOurSameConnectionAndLock(Phd2AppState state)
    {
        Assert.True(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(
            Owned(state), true, 7, new(760.5, 578.47), new(760.5, 578.47)));
    }

    [Theory]
    [InlineData(Phd2AppState.Calibrating)]
    [InlineData(Phd2AppState.Paused)]
    [InlineData(Phd2AppState.Unknown)]
    [InlineData(Phd2AppState.Looping)]
    [InlineData(Phd2AppState.Selected)]
    [InlineData(Phd2AppState.Stopped)]
    public void StopAuthorityDoesNotExpandToUnownedNonGuidingStates(Phd2AppState state)
    {
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(
            Owned(state), true, 7, new(760.5, 578.47), new(760.5, 578.47)));
    }

    [Fact]
    public void PauseReconnectMissingOwnershipAndChangedLockAllRefuseTakeover()
    {
        var s = Owned(Phd2AppState.LostLock);
        Phd2Point point = new(760.5, 578.47);
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(s, false, 7, point, point));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(s, true, null, point, point));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(s, true, 6, point, point));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(s with { IsConnected = false }, true, 7, point, point));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(s with { Phd2Paused = true }, true, 7, point, point));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(s with { AutomationPaused = true }, true, 7, point, point));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(s with { PendingSettleOperationId = 3 }, true, 7, point, point));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(s, true, 7, null, point));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(s, true, 7, point, null));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(s, true, 7, point, new(point.X + 1, point.Y)));
        Assert.False(Phd2DependencyRebuildStopPolicy.CanStopOwnedSession(s, true, 7, point, new(double.NaN, point.Y)));
    }

    [Fact]
    public void ProductionStopsBeforeInvalidatingEvidenceAndBeforeAcquisitionCanRestart()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var start = source.IndexOf("case ObservationAutomaticRecoveryAction.RebuildStageDependencies:", StringComparison.Ordinal);
        var end = source.IndexOf("case ObservationAutomaticRecoveryAction.RetrySameStage:", start, StringComparison.Ordinal);
        var branch = source[start..end];
        Assert.True(branch.IndexOf("EnsurePhdStoppedForAutomaticRebuildAsync", StringComparison.Ordinal) <
            branch.IndexOf("MarkResumeRecoveryRequired()", StringComparison.Ordinal));
        Assert.Contains("rebuildStop.Disposition != GateDisposition.Passed", branch, StringComparison.Ordinal);
        var helperStart = source.IndexOf("private async Task<GateResult> EnsurePhdStoppedForAutomaticRebuildAsync(", StringComparison.Ordinal);
        var helperEnd = source.IndexOf("public override async Task<GateResult> RevalidateAsync(", helperStart, StringComparison.Ordinal);
        var helper = source[helperStart..helperEnd];
        Assert.True(helper.IndexOf("ValidateIdentityAsync", StringComparison.Ordinal) < helper.IndexOf("StopCaptureAndConfirmAsync", StringComparison.Ordinal));
        Assert.True(helper.IndexOf("CanStopOwnedSession", StringComparison.Ordinal) < helper.IndexOf("StopCaptureAndConfirmAsync", StringComparison.Ordinal));
        Assert.Contains("ValidateConfirmedPhdStop(stopped,", helper, StringComparison.Ordinal);
        Assert.Contains("phd2-dependency-rebuild-stop-intent", helper, StringComparison.Ordinal);
        Assert.Contains("phd2-dependency-rebuild-stop-confirmed", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("CaptureFullFrameAsync", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("CaptureSingleFrame", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("SetLockPosition", helper, StringComparison.Ordinal);
    }
}
