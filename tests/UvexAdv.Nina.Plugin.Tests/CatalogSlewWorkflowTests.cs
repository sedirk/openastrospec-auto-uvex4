using System.IO;
using System.Reflection;
using NINA.Core.Enum;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class CatalogSlewWorkflowTests
{
    [Fact]
    public async Task RecordedTrueButOffEnableRecoversWithOneGotoNotThreeEnableRequests()
    {
        using var f = new Fixture();
        var enable = await RealObservationStageRunner.EnableAndVerifyMountTrackingAsync(
            f.CreateMediator(), "mount", TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(5), CancellationToken.None);
        Assert.Equal("TELESCOPE_TRACKING_ENABLE_TIMEOUT", enable.Code);
        Assert.Equal(1, f.EnableCalls);
        f.OnSlew = _ => { f.Info.TrackingEnabled = true; return Task.FromResult(true); };
        Assert.Equal(GateDisposition.Passed, (await f.Run(code: enable.Code)).Disposition);
        Assert.Equal(1, f.Commands);
        Assert.Equal(1, f.EnableCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalGotoRunsOnceAndOnlyFreshTrackingReachesAcquisition(bool recovery)
    {
        using var f = new Fixture();
        f.Info.TrackingEnabled = !recovery;
        f.OnSlew = _ =>
        {
            Assert.True(File.Exists(f.Path));
            Assert.Contains("HD 18552", File.ReadAllText(f.Path), StringComparison.Ordinal);
            Assert.Equal(2, f.Boundaries);
            f.Info.TrackingEnabled = true;
            f.Info.SideOfPier = PierSide.pierEast;
            return Task.FromResult(true);
        };
        var result = await f.Run(recovery: recovery);
        var freshAcquisitions = 0;
        if (result.Disposition == GateDisposition.Passed) freshAcquisitions++;
        Assert.Equal(1, freshAcquisitions);
        Assert.Equal(1, f.Commands);
        Assert.Equal(1, f.WaitCalls);
        Assert.True(f.Boundaries > 3);
        Assert.Equal("CATALOG_SLEW_TRACKING_VERIFIED", result.Code);
        Assert.Equal("CATALOG_SLEW_ALREADY_ATTEMPTED", (await f.Run(recovery: recovery)).Code);
        Assert.Equal(1, f.Commands);
    }

    [Theory]
    [InlineData("TELESCOPE_TRACKING_ENABLE_REJECTED")]
    [InlineData("TELESCOPE_TRACKING_CONNECTION_LOST")]
    [InlineData("TELESCOPE_TRACKING_IDENTITY_CHANGED")]
    [InlineData("TELESCOPE_TRACKING_ENABLE_PARKED")]
    [InlineData("TELESCOPE_TRACKING_UNEXPECTED_MOTION")]
    public async Task OnlyTimeoutAdmitsRecovery(string code)
    {
        using var f = new Fixture();
        Assert.Equal(code, (await f.Run(code: code)).Code);
        Assert.Equal(0, f.Commands);
        Assert.Equal(0, f.Boundaries);
        Assert.False(File.Exists(f.Path));
    }

    [Theory]
    [InlineData("weather", 1)]
    [InlineData("owner", 1)]
    [InlineData("pending-return", 1)]
    [InlineData("cancelled-night", 2)]
    [InlineData("roof", 2)]
    [InlineData("deadline", 2)]
    [InlineData("busy-camera", 2)]
    [InlineData("identity", 3)]
    public async Task BoundaryFailureGrantsNoExposureOrAnotherGoto(string reason, int onCheck)
    {
        using var f = new Fixture();
        f.OnBoundary = n => n == onCheck ? GateResult.Unknown(reason, "injected boundary") : Fixture.Clear;
        f.OnSlew = _ => { f.Info.TrackingEnabled = true; return Task.FromResult(true); };
        var result = await f.Run();
        Assert.Equal(reason, result.Code);
        Assert.NotEqual(GateDisposition.Passed, result.Disposition);
        Assert.Equal(onCheck > 2 ? 1 : 0, f.Commands);
        if (onCheck > 1)
        {
            f.OnBoundary = _ => Fixture.Clear;
            Assert.Equal("CATALOG_SLEW_ALREADY_ATTEMPTED", (await f.Run()).Code);
            Assert.Equal(onCheck > 2 ? 1 : 0, f.Commands);
        }
    }

    [Theory]
    [InlineData("disconnected")]
    [InlineData("wrong-id")]
    [InlineData("parked")]
    [InlineData("slewing")]
    [InlineData("pulse")]
    public async Task PreCommandMountStateIsNotBypassed(string state)
    {
        using var f = new Fixture();
        if (state == "disconnected") f.Info.Connected = false;
        if (state == "wrong-id") f.Info.DeviceId = "other";
        if (state == "parked") f.Info.AtPark = true;
        if (state == "slewing") f.Info.Slewing = true;
        if (state == "pulse") f.Info.IsPulseGuiding = true;
        Assert.Equal("CATALOG_SLEW_PRE_STATE_INVALID", (await f.Run()).Code);
        Assert.Equal(0, f.Commands);
    }

    [Theory]
    [InlineData("reject", "CATALOG_SLEW_REJECTED")]
    [InlineData("exception", "CATALOG_SLEW_EXCEPTION")]
    [InlineData("timeout", "CATALOG_SLEW_TIMEOUT")]
    [InlineData("tracking-off", "CATALOG_SLEW_TRACKING_NOT_RESTORED")]
    [InlineData("post-motion", "CATALOG_SLEW_POST_STATE_INVALID")]
    [InlineData("post-id", "CATALOG_SLEW_POST_STATE_INVALID")]
    public async Task FailedGotoOrPostcheckCannotRepeatOrExpose(string mode, string expected)
    {
        using var f = new Fixture();
        f.OnSlew = async token =>
        {
            if (mode == "reject") return false;
            if (mode == "exception") throw new InvalidOperationException("driver limit");
            if (mode == "timeout") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            if (mode == "post-motion") f.Info.Slewing = true;
            if (mode == "post-id") f.Info.DeviceId = "other";
            return true;
        };
        var result = await f.Run();
        Assert.Equal(expected, result.Code);
        Assert.NotEqual(GateDisposition.Passed, result.Disposition);
        Assert.Equal("CATALOG_SLEW_ALREADY_ATTEMPTED", (await f.Run()).Code);
        Assert.Equal(1, f.Commands);
        Assert.False(ObservationAutomaticRecoveryPolicy.For(ObservationStage.SlewToCatalogTarget, result).IsRecoverable);
    }

    [Fact]
    public async Task IntentWriteFailureStartsNoMovement()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(f.Path);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Run());
        Assert.Equal(0, f.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationIsNotConvertedToSuccessOrRetried(bool afterCommand)
    {
        using var f = new Fixture();
        using var cancel = new CancellationTokenSource();
        if (!afterCommand) cancel.Cancel();
        f.OnSlew = async token => { cancel.Cancel(); await Task.Delay(100, token); return true; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Run(token: cancel.Token));
        Assert.Equal(afterCommand ? 1 : 0, f.Commands);
        if (afterCommand) Assert.Equal("CATALOG_SLEW_ALREADY_ATTEMPTED", (await f.Run()).Code);
    }

    private sealed class Fixture : IDisposable
    {
        internal static readonly GateResult Clear = GateResult.Pass("clear", "clear");
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "catalog-test-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(root, "intent.json");
        internal readonly TelescopeInfo Info = new() { Connected = true, DeviceId = "mount", SideOfPier = PierSide.pierWest };
        internal Func<CancellationToken, Task<bool>> OnSlew = _ => Task.FromResult(true);
        internal Func<int, GateResult> OnBoundary = _ => Clear;
        internal int Commands, Boundaries, WaitCalls, EnableCalls;
        internal ITelescopeMediator CreateMediator()
        {
            var mediator = DispatchProxy.Create<ITelescopeMediator, Proxy>();
            ((Proxy)(object)mediator).Handler = name => name switch
            {
                "GetInfo" => Info,
                "WaitForSlew" => Wait(),
                "SetTrackingEnabled" => Enable(),
                _ => throw new InvalidOperationException("Unapproved hardware call: " + name),
            };
            Task Wait() { WaitCalls++; return Task.CompletedTask; }
            bool Enable() { EnableCalls++; return true; } // Actual polled state stays off.
            return mediator;
        }
        internal async Task<GateResult> Run(bool recovery = true, string? code = null, CancellationToken token = default)
        {
            var mediator = CreateMediator();
            var tracking = !recovery ? Clear : GateResult.Unknown(code ?? "TELESCOPE_TRACKING_ENABLE_TIMEOUT", "recorded timeout");
            return await CatalogSlewWorkflow.ExecuteAsync(tracking, Path, new { target = "HD 18552", raDegrees = 45.049525 },
                _ => Task.FromResult(OnBoundary(++Boundaries)),
                ct => { Commands++; return OnSlew(ct); }, mediator, "mount", token,
                TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(5));
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private class Proxy : DispatchProxy
    {
        internal Func<string, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!.Name);
    }
}
