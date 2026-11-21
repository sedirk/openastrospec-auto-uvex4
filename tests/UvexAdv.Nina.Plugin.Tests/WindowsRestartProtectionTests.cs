using NINA.Astrometry.Interfaces;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using NINA.Profile.Interfaces;
using UvexAdv.Nina.Plugin.SequenceItems;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class WindowsRestartProtectionTests
{
    private sealed class LocalWuaFactAttribute : FactAttribute
    {
        public LocalWuaFactAttribute() { if (Environment.GetEnvironmentVariable("UVEX_VERIFY_LOCAL_WUA") != "1") Skip = "Local read-only WUA acceptance, opt in on the Windows workstation."; }
    }

    [LocalWuaFact]
    public async Task ActualWuaReadOnlySmoke()
    {
        Assert.NotEqual(UpdateRestartState.Unknown, await WindowsRestartProbe.System.ReadAsync(default, fresh: true));
    }
    internal static WindowsRestartProbe Probe(Func<UpdateRestartState> read) =>
        new(read, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1));

    [Theory]
    [InlineData("bound-idle")] [InlineData("other-atr")] [InlineData("other-mount")]
    [InlineData("live-view")] [InlineData("exposing")] [InlineData("slewing")] [InlineData("pulse")]
    public void MaintenanceChecksTheCurrentMasterOwnersNotJustTheOldStoppedLedger(string mode)
    {
        var camera = new CameraInfo { Connected = true, DeviceId = mode == "other-atr" ? "other" : "atr",
            LiveViewEnabled = mode == "live-view", IsExposing = mode == "exposing" };
        var mount = new TelescopeInfo { Connected = true, DeviceId = mode == "other-mount" ? "other" : "mount",
            Slewing = mode == "slewing", IsPulseGuiding = mode == "pulse" };
        Assert.Equal(mode == "bound-idle", RealObservationStageRunner.RestartMasterOwnerIssue(camera, "atr", mount, "mount") is null);
    }

    [Fact]
    public async Task OpeningUsesFreshWuaNotThePreviousClearSampleOrADownloadFlag()
    {
        var next = UpdateRestartState.NotRequired; // Downloaded/installed without reboot.
        var probe = Probe(() => next);
        Assert.Null(await probe.BeforeOpeningAsync(default));
        next = UpdateRestartState.Unknown;
        Assert.Equal("WINDOWS_UPDATE_STATUS_UNKNOWN", (await probe.BeforeOpeningAsync(default))!.Code);
        Assert.Null(probe.PollGate()); // Unknown does not trigger mechanical closeout.
        next = UpdateRestartState.Required;
        Assert.Equal(WindowsRestartProbe.Code, (await probe.BeforeOpeningAsync(default))!.Code);
        next = UpdateRestartState.Unknown;
        Assert.Equal(WindowsRestartProbe.Code, (await probe.BeforeOpeningAsync(default))!.Code);
    }

    [Fact]
    public async Task StuckWuaHasBoundedWaitAndOnlyOneOutstandingComCall()
    {
        var calls = 0;
        using var release = new ManualResetEventSlim();
        var probe = new WindowsRestartProbe(() => { Interlocked.Increment(ref calls); release.Wait(); return UpdateRestartState.NotRequired; },
            TimeSpan.Zero, TimeSpan.FromMilliseconds(80));
        try
        {
            var tasks = Enumerable.Range(0, 8).Select(_ => probe.BeforeOpeningAsync(default)).ToArray();
            var gates = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.All(gates, g => Assert.Equal("WINDOWS_UPDATE_STATUS_UNKNOWN", g!.Code));
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally { release.Set(); }
        Assert.Null(await probe.BeforeOpeningAsync(default));
    }

    [Theory]
    [InlineData("closed", false, false, 0)]
    [InlineData("open", false, false, 1)]
    [InlineData("open", true, true, 0)]
    [InlineData("invalid", false, true, 0)]
    [InlineData("disconnected", false, true, 0)]
    [InlineData("mismatch", false, true, 0)]
    [InlineData("moving", false, true, 0)]
    [InlineData("accepted-no-readback", false, true, 1)]
    [InlineData("owner-not-stopped", false, true, 1)]
    [InlineData("closed-park-failed", false, true, 1)]
    public async Task OnlyExplicitBoundTerminalReadbackCanConfirmClosure(string mode, bool weak, bool alarm, int expectedCalls)
    {
        var calls = 0;
        var roof = new DomeInfo { Connected = mode != "disconnected", DeviceId = mode == "mismatch" ? "other" : "roof",
            Slewing = mode == "moving", ShutterStatus = mode switch { "invalid" => (ShutterState)5, "closed" => ShutterState.ShutterClosed, _ => ShutterState.ShutterOpen } };
        var errors = await WindowsRestartCloseout.RunAsync(() => NinaRoofReadback.Position(roof, "roof"), weak, _ =>
        {
            calls++;
            if (mode is "open" or "closed-park-failed") roof.ShutterStatus = ShutterState.ShutterClosed;
            return Task.FromResult<IReadOnlyList<string>>(mode switch {
                "owner-not-stopped" => ["Stop not proven; no motion issued"], "closed-park-failed" => ["Park not confirmed"], _ => [] });
        }, default);
        Assert.Equal(expectedCalls, calls); Assert.Equal(alarm, errors.Count > 0);
        if (mode == "closed-park-failed") Assert.Contains(errors, e => e.Contains("屋顶已读回关闭"));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task StandaloneSkipsStartOrInterruptsOwnedLongExposureThenWaitsForItsFinally(bool pendingAtStart)
    {
        var pending = pendingAtStart ? 1 : 0;
        var events = new List<string>();
        var port = new WindowsRestartRunPort(Probe(() => Volatile.Read(ref pending) != 0 ? UpdateRestartState.Required : UpdateRestartState.NotRequired),
            (ran, token) => { Assert.Equal(!pendingAtStart, ran); Assert.False(token.IsCancellationRequested); events.Add("checked close"); return Task.FromResult<IReadOnlyList<string>>(["no Closed receipt"]); });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => port.ExecuteAsync(async ct =>
        {
            try { events.Add("expose"); Volatile.Write(ref pending, 1); await Task.Delay(Timeout.Infinite, ct); }
            finally { events.Add("joined"); }
        }, (_, alarm) => { Assert.True(alarm); events.Add("alarm"); }, default).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains(WindowsRestartProbe.Code, ex.Message);
        Assert.Equal(pendingAtStart ? new[] { "checked close", "alarm" } : ["expose", "joined", "checked close", "alarm"], events);
    }

    [Theory]
    [InlineData("opening-then-open", 1, false)]
    [InlineData("closing-then-closed", 0, false)]
    [InlineData("closing-then-open", 0, true)]
    [InlineData("error-then-open", 1, false)]
    [InlineData("error-then-disconnected", 0, true)]
    public async Task NeverReissuesAMovingRoofOrUsesAnAnonymousReadback(string mode, int count, bool alarm)
    {
        var sequence = new Queue<BoundRoofPosition>(mode switch
        {
            "opening-then-open" => [BoundRoofPosition.Opening, BoundRoofPosition.Open],
            "closing-then-closed" => [BoundRoofPosition.Closing, BoundRoofPosition.Closed],
            "closing-then-open" => [BoundRoofPosition.Closing, BoundRoofPosition.Open],
            "error-then-open" => [BoundRoofPosition.ReadbackError, BoundRoofPosition.Open],
            _ => [BoundRoofPosition.ReadbackError, BoundRoofPosition.Unconfirmed]
        });
        var calls = 0;
        var errors = await WindowsRestartCloseout.RunAsync(() => sequence.Dequeue(), false, _ =>
        { calls++; sequence.Enqueue(BoundRoofPosition.Closed); return Task.FromResult<IReadOnlyList<string>>([]); },
            default, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1));
        Assert.Equal(count, calls); Assert.Equal(alarm, errors.Count > 0);
    }
}

public sealed partial class NativeSequencePlanTests
{
    [Theory]
    [InlineData("download-only")] [InlineData("pending-at-start")] [InlineData("during-first")]
    public async Task WindowsMaintenanceActuallyControlsNativeTargetsWithoutAffectingDownloadOnly(string mode)
    {
        InitializeAstrometry();
        var settings = Settings();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? Proxy<IAstrometrySettings>() : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        var pending = mode == "pending-at-start" ? 1 : 0;
        var probe = WindowsRestartProtectionTests.Probe(() => Volatile.Read(ref pending) != 0 ? UpdateRestartState.Required : UpdateRestartState.NotRequired);
        var night = new UvexNightSequenceContainer(profiles, host, null!, settings)
        { StopAtDawn = false, HoldOpenAfterTargets = false, SimulatedRestartProbe = probe };
        var stages = new List<ObservationStage>[] { [], [] };
        for (var i = 0; i < 2; i++)
        {
            var index = i;
            night.Add(new UvexTargetObservationContainer(profiles, Proxy<INighttimeCalculator>(), host, null!, settings, () =>
                new RestartStages(async (stage, ct) =>
                {
                    stages[index].Add(stage);
                    if (index == 0 && stage == ObservationStage.RunScienceBlock && mode == "during-first")
                    { Volatile.Write(ref pending, 1); await Task.Delay(Timeout.Infinite, ct); }
                }))
            {
                TargetName = "update replay " + i, CatalogId = "SIM-" + i, NightSetupId = "SIM-NIGHT", UseRealMode = false,
                SiteLatitudeDegrees = 33, SiteLongitudeDegreesEast = 120, RightAscensionDegrees = 0, DeclinationDegrees = 89,
                DurationMinutes = 1, HorizonMinimumDegrees = 0, HorizonStartMarginDegrees = 0, HorizonContinueMarginDegrees = 0,
                ExpectedAtrCameraId = "SIM-ATR", ExpectedG3ProfileName = "SIM-G3", ExpectedQhyCameraId = "SIM-QHY",
                ScienceFrames = 1, MaximumScienceAttempts = 2,
            });
        }
        var failure = await Record.ExceptionAsync(() => night.Execute(new Progress<ApplicationStatus>(), default).WaitAsync(TimeSpan.FromSeconds(12)));
        Assert.IsNotType<TimeoutException>(failure);
        if (mode == "download-only")
        { Assert.Null(failure); Assert.All(stages, s => Assert.Contains(ObservationStage.FinalizeObservation, s)); }
        else
        {
            Assert.Equal(mode == "during-first", stages[0].Contains(ObservationStage.RunScienceBlock));
            Assert.Empty(stages[1]); Assert.False(night.NativeEndInstructionsReady);
            Assert.Contains(WindowsRestartProbe.Code, await File.ReadAllTextAsync(night.JournalPath));
        }
    }

    private sealed class RestartStages(Func<ObservationStage, CancellationToken, Task> execute) : ObservationStageRunnerBase
    {
        public override async Task<StageResult> ExecuteStageAsync(ObservationStage stage, ObservationContext context, CancellationToken token)
        { await execute(stage, token); return new(GateResult.Pass("SIM", "no devices")); }
    }
}
