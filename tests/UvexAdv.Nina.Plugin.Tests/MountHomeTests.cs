using System.Reflection;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class MountHomeTests
{
    [Theory]
    [InlineData("already-home", "NIGHT_HOME_VERIFIED", 0)]
    [InlineData("success", "NIGHT_HOME_VERIFIED", 1)]
    [InlineData("tracking", "NIGHT_HOME_VERIFIED", 1)]
    [InlineData("false", "NIGHT_HOME_REJECTED", 1)]
    [InlineData("ack-only", "NIGHT_HOME_TIMEOUT", 1)]
    [InlineData("never-completes", "NIGHT_HOME_TIMEOUT", 1)]
    [InlineData("identity-changed", "NIGHT_HOME_STATE_CHANGED", 1)]
    [InlineData("disconnected", "NIGHT_HOME_STATE_CHANGED", 1)]
    [InlineData("exception", "NIGHT_HOME_FAILED", 1)]
    [InlineData("pre-parked", "NIGHT_HOME_PRECONDITION", 0)]
    [InlineData("pre-motion", "NIGHT_HOME_PRECONDITION", 0)]
    [InlineData("pre-tracking", "NIGHT_HOME_PRECONDITION", 0)]
    [InlineData("pre-pulse", "NIGHT_HOME_PRECONDITION", 0)]
    [InlineData("pre-identity", "NIGHT_HOME_PRECONDITION", 0)]
    [InlineData("unsupported", "NIGHT_HOME_PRECONDITION", 0)]
    public async Task HomeRequiresNativeCompletionAndStoppedReadback(string mode, string code, int count)
    {
        var info = new TelescopeInfo
        {
            Connected = true, DeviceId = mode == "pre-identity" ? "wrong" : "mount-a",
            CanFindHome = mode != "unsupported", AtHome = mode == "already-home",
            AtPark = mode == "pre-parked", Slewing = mode == "pre-motion", IsPulseGuiding = mode == "pre-pulse",
            TrackingEnabled = mode == "pre-tracking",
        };
        var commands = 0; var stops = 0; var trackingOff = 0;
        var mediator = DispatchProxy.Create<ITelescopeMediator, TelescopeProxy>();
        ((TelescopeProxy)(object)mediator).Handler = (method, args) =>
        {
            if (method.Name == "GetInfo") return info;
            if (method.Name == "FindHome")
            {
                commands++;
                if (mode == "exception") throw new InvalidOperationException("driver failed");
                if (mode == "never-completes") return new TaskCompletionSource<bool>().Task;
                if (mode == "success" || mode == "tracking") info.AtHome = true;
                if (mode == "tracking") info.TrackingEnabled = true;
                if (mode == "identity-changed") info.DeviceId = "wrong";
                if (mode == "disconnected") info.Connected = false;
                return Task.FromResult(mode != "false");
            }
            if (method.Name == "StopSlew") { stops++; return null; }
            if (method.Name == "SetTrackingEnabled")
            {
                Assert.False((bool)args![0]!); info.TrackingEnabled = false; trackingOff++; return false;
            }
            throw new InvalidOperationException($"Unrequested hardware action: {method.Name}");
        };
        var result = await RealObservationStageRunner.HomeAndVerifyMountAsync(mediator, "mount-a",
            new Progress<ApplicationStatus>(), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(5), CancellationToken.None);
        Assert.Equal(code, result.Code);
        Assert.Equal(count, commands);
        Assert.Equal(count, result.Metrics!["commandCount"]);
        Assert.Equal(code == "NIGHT_HOME_VERIFIED", result.Disposition == GateDisposition.Passed);
        Assert.Equal(mode == "tracking" ? 1 : 0, trackingOff);
        Assert.Equal(mode is "false" or "ack-only" or "never-completes" or "exception" ? 1 : 0, stops);
    }

    [Fact]
    public async Task CancellationStopsSingleHomeAndNeverParks()
    {
        using var cancel = new CancellationTokenSource();
        var calls = new List<string>();
        var mediator = DispatchProxy.Create<ITelescopeMediator, TelescopeProxy>();
        ((TelescopeProxy)(object)mediator).Handler = (method, _) =>
        {
            if (method.Name == "GetInfo") return new TelescopeInfo { Connected = true, DeviceId = "a", CanFindHome = true };
            calls.Add(method.Name);
            if (method.Name == "FindHome") { cancel.Cancel(); return Task.FromResult(true); }
            if (method.Name == "StopSlew") return null;
            throw new InvalidOperationException(method.Name);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RealObservationStageRunner.HomeAndVerifyMountAsync(
            mediator, "a", new Progress<ApplicationStatus>(), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5), cancel.Token));
        Assert.Equal(new[] { "FindHome", "StopSlew" }, calls);
    }

    private class TelescopeProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }
}
