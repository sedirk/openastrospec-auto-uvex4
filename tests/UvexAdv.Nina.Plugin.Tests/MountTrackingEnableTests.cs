using System.Reflection;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class MountTrackingEnableTests
{
    [Theory]
    [InlineData("already-on", "TELESCOPE_TRACKING_ENABLED", 0)]
    [InlineData("immediate", "TELESCOPE_TRACKING_ENABLED", 1)]
    [InlineData("false-then-on", "TELESCOPE_TRACKING_ENABLED", 1)]
    [InlineData("true-then-on", "TELESCOPE_TRACKING_ENABLED", 1)]
    [InlineData("false-stays-off", "TELESCOPE_TRACKING_ENABLE_TIMEOUT", 1)]
    [InlineData("true-stays-off", "TELESCOPE_TRACKING_ENABLE_TIMEOUT", 1)]
    [InlineData("exception", "TELESCOPE_TRACKING_ENABLE_REJECTED", 1)]
    [InlineData("disconnected", "TELESCOPE_TRACKING_CONNECTION_LOST", 1)]
    [InlineData("identity-changed", "TELESCOPE_TRACKING_IDENTITY_CHANGED", 1)]
    [InlineData("parked", "TELESCOPE_TRACKING_ENABLE_PARKED", 1)]
    [InlineData("slewing", "TELESCOPE_TRACKING_UNEXPECTED_MOTION", 1)]
    [InlineData("pulse", "TELESCOPE_TRACKING_UNEXPECTED_MOTION", 1)]
    [InlineData("pre-disconnected", "TELESCOPE_TRACKING_CONNECTION_LOST", 0)]
    [InlineData("pre-identity", "TELESCOPE_TRACKING_IDENTITY_CHANGED", 0)]
    [InlineData("pre-parked", "TELESCOPE_TRACKING_ENABLE_PARKED", 0)]
    public async Task OnlyVerifiedTrackingCanPermitSlew(string mode, string code, int expectedCommands)
    {
        var info = new TelescopeInfo
        {
            Connected = mode != "pre-disconnected", DeviceId = mode == "pre-identity" ? "other" : "mount-a",
            TrackingEnabled = mode == "already-on", AtPark = mode == "pre-parked",
        };
        var commands = 0;
        var readsAfterCommand = 0;
        var mediator = DispatchProxy.Create<ITelescopeMediator, TelescopeProxy>();
        ((TelescopeProxy)(object)mediator).Handler = (method, args) =>
        {
            if (method.Name == "SetTrackingEnabled")
            {
                Assert.True((bool)args![0]!);
                commands++;
                if (mode == "exception") throw new InvalidOperationException("driver refusal");
                if (mode == "immediate") info.TrackingEnabled = true;
                return mode.StartsWith("true-", StringComparison.Ordinal) || mode == "immediate";
            }
            if (method.Name == "GetInfo")
            {
                if (commands > 0 && ++readsAfterCommand >= 2)
                {
                    if (mode.EndsWith("-then-on", StringComparison.Ordinal)) info.TrackingEnabled = true;
                    if (mode == "disconnected") info.Connected = false;
                    if (mode == "identity-changed") info.DeviceId = "other";
                    if (mode == "parked") info.AtPark = true;
                    if (mode == "slewing") info.Slewing = true;
                    if (mode == "pulse") info.IsPulseGuiding = true;
                }
                return info;
            }
            throw new InvalidOperationException($"Unexpected hardware call: {method.Name}");
        };
        var result = await RealObservationStageRunner.EnableAndVerifyMountTrackingAsync(mediator,
            "mount-a", TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(5), CancellationToken.None);
        Assert.Equal(code, result.Code);
        Assert.Equal(expectedCommands, commands);
        Assert.Equal(expectedCommands, result.Metrics!["commandCount"]);
        Assert.Equal(code == "TELESCOPE_TRACKING_ENABLED", result.Disposition == GateDisposition.Passed);
        if (mode.EndsWith("-then-on", StringComparison.Ordinal) || mode.EndsWith("-stays-off", StringComparison.Ordinal))
            Assert.True(result.Metrics["pollCount"] >= 1);
        if (mode.EndsWith("-stays-off", StringComparison.Ordinal))
            Assert.True(result.Metrics["elapsedMilliseconds"] >= 100);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationNeverRetriesOrSlews(bool beforeCommand)
    {
        using var cancel = new CancellationTokenSource();
        var commands = 0;
        var mediator = DispatchProxy.Create<ITelescopeMediator, TelescopeProxy>();
        ((TelescopeProxy)(object)mediator).Handler = (method, _) =>
        {
            if (method.Name == "SetTrackingEnabled") { commands++; cancel.Cancel(); return false; }
            if (method.Name == "GetInfo") return new TelescopeInfo { Connected = true, DeviceId = "mount-a" };
            throw new InvalidOperationException($"Unexpected hardware call: {method.Name}");
        };
        if (beforeCommand) cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RealObservationStageRunner.EnableAndVerifyMountTrackingAsync(mediator, "mount-a",
                TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(5), cancel.Token));
        Assert.Equal(beforeCommand ? 0 : 1, commands);
    }

    private class TelescopeProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
