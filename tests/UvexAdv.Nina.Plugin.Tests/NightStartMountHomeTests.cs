using System.IO;
using System.Reflection;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class NightStartMountHomeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "start-home-" + Guid.NewGuid().ToString("N"));
    private string Journal => Path.Combine(root, "night-test.json");
    private static GateResult Clear => GateResult.Pass("clear", "test");
    private static Task<GateResult> Boundary(CancellationToken _) => Task.FromResult(Clear);

    [Theory]
    [InlineData(true, false, 1, 1)]
    [InlineData(false, false, 1, 0)] // recorded .361: stopped/unparked, tracking off at the limit
    [InlineData(false, true, 0, 0)]
    [InlineData(true, true, 0, 1)]
    public async Task ExplicitNightNativeHomeHandlesResidualTrackingOrAlreadyHome(bool tracking, bool atHome, int homes, int off)
    {
        var f = new Mount { Info = { TrackingEnabled = tracking, AtHome = atHome } };
        var night = new NightStartMountHome(Journal);
        Assert.Equal("NIGHT_START_HOME_VERIFIED", (await night.EnsureAsync("first", "mount", "hash", Boundary, f.Home, default)).Code);
        Assert.True(f.Info.AtHome && !f.Info.TrackingEnabled);
        Assert.Equal(homes, f.Homes); Assert.Equal(off, f.TrackingOff);
        f.Info.AtHome = false; f.Info.TrackingEnabled = true; // next target or a resumed segment is already tracking elsewhere
        Assert.Equal("NIGHT_START_HOME_ALREADY_VERIFIED", (await night.EnsureAsync("next", "mount", "hash",
            _ => throw new Exception("No pre-home checks during a continuation"), f.Home, default)).Code);
        Assert.Equal(homes, f.Homes); Assert.Equal(off, f.TrackingOff);
        Assert.Single(Directory.GetFiles(Path.Combine(root, "start-home"), "*.intent.json"));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "start-home"), "*.verified.json"));
        var rebuilt = new NightStartMountHome(Journal);
        Assert.Equal("NIGHT_START_HOME_UNCONFIRMED", (await rebuilt.EnsureAsync("retry", "mount", "hash", Boundary, f.Home, default)).Code);
        Assert.Equal(homes, f.Homes);
    }

    [Theory]
    [InlineData("roof-closed", 1)] [InlineData("weather-unsafe", 2)] [InlineData("owner-busy", 2)]
    [InlineData("return-pending", 1)] [InlineData("deadline", 2)] [InlineData("lost-after-home", 3)]
    public async Task PrechargeAndRechecksDoNotTurnAnUnsafeGateIntoSuccess(string issue, int check)
    {
        var f = new Mount(); var night = new NightStartMountHome(Journal); int n = 0;
        var result = await night.EnsureAsync("first", "mount", "hash",
            _ => Task.FromResult(++n == check ? GateResult.Unknown(issue, issue) : Clear), f.Home, default);
        Assert.Equal(issue, result.Code); Assert.False(night.Verified);
        Assert.Equal(check == 3 ? 1 : 0, f.Homes);
        if (check > 1)
            Assert.Equal("NIGHT_START_HOME_UNCONFIRMED", (await night.EnsureAsync("again", "mount", "hash", Boundary, f.Home, default)).Code);
        else Assert.False(Directory.Exists(Path.Combine(root, "start-home")));
    }

    [Theory]
    [InlineData("slewing")] [InlineData("pulsing")] [InlineData("wrong-mount")] [InlineData("parked")]
    [InlineData("unsupported")] [InlineData("tracking-stuck")] [InlineData("ack-only")] [InlineData("rejected")]
    public async Task FailedOwnerOrMissingPhysicalArrivalNeverPermitsNextGoto(string mode)
    {
        var f = new Mount { Mode = mode }; var i = f.Info;
        i.Slewing = mode == "slewing"; i.IsPulseGuiding = mode == "pulsing"; i.AtPark = mode == "parked";
        i.DeviceId = mode == "wrong-mount" ? "other" : "mount"; i.CanFindHome = mode != "unsupported";
        i.TrackingEnabled = mode == "tracking-stuck";
        var night = new NightStartMountHome(Journal);
        var result = await night.EnsureAsync("run", "mount", "hash", Boundary, f.Home, default);
        Assert.False(night.Verified); Assert.Equal("NIGHT_START_HOME_UNCONFIRMED", result.Code);
        Assert.Equal(mode is "ack-only" or "rejected" ? 1 : 0, f.Homes);
        Assert.Equal(mode == "tracking-stuck" ? 1 : 0, f.TrackingOff);
        Assert.False(NativeSequencePlan.IsSkippableQualityFailure(result));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "start-home"), "*.verified.json"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task UnsafeOrCancelledDuringIgnoredNativeTaskStopsButDoesNotLoseItsOwner(bool operatorCancels)
    {
        var f = new Mount { Mode = "held" }; var night = new NightStartMountHome(Journal);
        using var cancel = new CancellationTokenSource();
        f.Guard = () =>
        {
            if (f.Homes == 0) return null;
            if (operatorCancels) cancel.Cancel();
            return operatorCancels ? null : GateResult.Unknown("NIGHT_UNSAFE", "weather changed");
        };
        var run = night.EnsureAsync("run", "mount", "hash", Boundary, f.Home, cancel.Token);
        if (operatorCancels) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        else Assert.NotEqual(GateDisposition.Passed, (await run).Disposition);
        Assert.Equal(1, f.Homes); Assert.Equal(1, f.Stops); Assert.False(night.Verified);
        Assert.NotNull(NightStartHomeDriverBoundary.For(f.Mediator).Unjoined);
        f.Held.SetResult(true);
        Assert.Null(NightStartHomeDriverBoundary.For(f.Mediator).Unjoined);
        Assert.Equal("NIGHT_START_HOME_UNCONFIRMED", (await night.EnsureAsync("retry", "mount", "hash", Boundary, f.Home, default)).Code);
    }

    [Fact]
    public async Task FailedJournalWriteCannotCommandAndUnverifiedHomeBlocksActualNativeFollowingTarget()
    {
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "start-home"), "not a directory");
        var f = new Mount();
        var gate = await new NightStartMountHome(Journal).EnsureAsync("run", "mount", "hash", Boundary, f.Home, default);
        Assert.Equal("NIGHT_START_HOME_RECORD_UNCONFIRMED", gate.Code); Assert.Equal(0, f.Homes);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("unknown", gate.Code, gate, ObservationStage.SlewToCatalogTarget);
    }

    private sealed class Mount
    {
        internal readonly TelescopeInfo Info = new() { Connected = true, DeviceId = "mount", CanFindHome = true, CanSetTrackingEnabled = true };
        internal readonly ITelescopeMediator Mediator = DispatchProxy.Create<ITelescopeMediator, Proxy>();
        internal readonly TaskCompletionSource<bool> Held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string Mode = "";
        internal Func<GateResult?> Guard = () => null;
        internal int Homes, TrackingOff, Stops;
        internal Task<GateResult> Home(CancellationToken ct)
        {
            ((Proxy)(object)Mediator).InvokeMember = (name, args) => name switch
            {
                "GetInfo" => Info,
                "FindHome" => Find(),
                "SetTrackingEnabled" when args![0] is false => Off(),
                "StopSlew" => Stop(),
                _ => throw new InvalidOperationException("Unexpected hardware command: " + name)
            };
            Task<bool> Find() { Homes++; if (Mode == "held") return Held.Task;
                Info.AtHome = Mode is not ("ack-only" or "rejected"); return Task.FromResult(Mode != "rejected"); }
            bool Off() { TrackingOff++; if (Mode != "tracking-stuck") Info.TrackingEnabled = false; return false; }
            object? Stop() { Stops++; return null; }
            return RealObservationStageRunner.HomeAndVerifyMountAsync(Mediator, "mount", new Progress<ApplicationStatus>(),
                TimeSpan.FromMilliseconds(Mode is "ack-only" or "tracking-stuck" ? 150 : 2000), TimeSpan.FromMilliseconds(5),
                ct, Guard, NightStartHomeDriverBoundary.For(Mediator).Retain);
        }
    }
    public class Proxy : DispatchProxy
    {
        internal Func<string, object?[]?, object?> InvokeMember = null!;
        protected override object? Invoke(MethodInfo? m, object?[]? args) => InvokeMember(m!.Name, args);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
