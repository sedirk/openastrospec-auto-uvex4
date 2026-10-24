using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Equipment.MyFlatDevice;
using NINA.Equipment.Interfaces;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class StartupClockRecoveryBoundaryTests
{
    private static readonly NinaEnvironmentDeviceSelection Expected = new("safety", "roof", "weather", "cover");
    private static GateResult Pass() => GateResult.Pass("IDLE", "Test read-only port.");
    private static Task<GateResult> Idle(CancellationToken _) => Task.FromResult(Pass());
    private static DomeInfo Roof(ShutterState state) => new() { Connected = true, DeviceId = "roof", ShutterStatus = state };
    private static FlatDeviceInfo Cover(CoverState state) => new()
    { Connected = true, DeviceId = "cover", SupportsOpenClose = true, CoverState = state };

    [Fact]
    public void OnlyFirstNightSetupCanUseKnownClosedOpticalPath()
    {
        foreach (var stage in Enum.GetValues<ObservationStage>())
            Assert.Equal(stage == ObservationStage.ValidateNightSetup,
                StartupClockRecoveryBoundary.CanEnter(stage, false, false, false));
        Assert.False(StartupClockRecoveryBoundary.CanEnter(ObservationStage.ValidateNightSetup, true, false, false));
        Assert.False(StartupClockRecoveryBoundary.CanEnter(ObservationStage.ValidateNightSetup, false, true, false));
        Assert.False(StartupClockRecoveryBoundary.CanEnter(ObservationStage.ValidateNightSetup, false, false, true));
        // Production native sessions attach before running and record only at the
        // terminal boundary. An ordinary first segment must not look inherited.
        var session = new NativeTargetSession(new(2, 4, 0, DeadlineUtc: DateTimeOffset.UtcNow.AddMinutes(35)));
        bool Earlier() => session.ClockBudget is not null || session.HasBudgetedContinuation || session.SegmentRunIds.Count > 0;
        Assert.False(Earlier());
        Assert.True(StartupClockRecoveryBoundary.CanEnter(ObservationStage.ValidateNightSetup, false, false, Earlier()));
        session.RecordSegment("first-completed", 0, 0, 0, 0);
        Assert.True(Earlier());
        Assert.False(StartupClockRecoveryBoundary.CanEnter(ObservationStage.ValidateNightSetup, false, false, Earlier()));
    }

    [Theory]
    [InlineData(ShutterState.ShutterClosed, CoverState.Closed, false)]
    [InlineData(ShutterState.ShutterClosed, CoverState.Open, false)]
    [InlineData(ShutterState.ShutterOpen, CoverState.Closed, true)]
    [InlineData(ShutterState.ShutterOpen, CoverState.Open, true)]
    public void InitialKnownEndpointPairsPassWithoutOpeningAnything(ShutterState roof, CoverState cover, bool committed)
    {
        Assert.Equal(GateDisposition.Passed,
            StartupClockRecoveryBoundary.OpticalPath(Roof(roof), Cover(cover), Expected, committed, roof, cover).Disposition);
    }

    [Fact]
    public void MovingUnknownChangedOrPreviouslyOpenedThenClosedPathCannotUseStartupException()
    {
        var roof = Roof(ShutterState.ShutterClosed);
        var cover = Cover(CoverState.Closed);
        GateResult Check(bool committed = false, ShutterState? oldRoof = null, CoverState? oldCover = null) =>
            StartupClockRecoveryBoundary.OpticalPath(roof, cover, Expected, committed, oldRoof, oldCover);
        foreach (var state in Enum.GetValues<ShutterState>().Where(x => x is not (ShutterState.ShutterOpen or ShutterState.ShutterClosed)))
        { roof.ShutterStatus = state; Assert.NotEqual(GateDisposition.Passed, Check().Disposition); }
        roof.ShutterStatus = ShutterState.ShutterClosed;
        foreach (var state in Enum.GetValues<CoverState>().Where(x => x is not (CoverState.Open or CoverState.Closed)))
        { cover.CoverState = state; Assert.NotEqual(GateDisposition.Passed, Check().Disposition); }
        cover.CoverState = CoverState.Closed;
        roof.Slewing = true;
        Assert.NotEqual(GateDisposition.Passed, Check().Disposition);
        roof.Slewing = false;
        Assert.Equal("ROOF_CLOSED_AFTER_OPEN", Check(committed: true).Code);
        Assert.Equal("MOUNT_CLOCK_STARTUP_OPTICAL_PATH_CHANGED", Check(oldRoof: ShutterState.ShutterOpen).Code);
        Assert.Equal("MOUNT_CLOCK_STARTUP_OPTICAL_PATH_CHANGED", Check(oldCover: CoverState.Open).Code);
        roof.DeviceId = "other-roof";
        Assert.NotEqual(GateDisposition.Passed, Check().Disposition);
        roof.DeviceId = "roof";
        cover.Connected = false;
        Assert.NotEqual(GateDisposition.Passed, Check().Disposition);
        cover.Connected = true;
        cover.SupportsOpenClose = false;
        Assert.NotEqual(GateDisposition.Passed, Check().Disposition);
    }

    [Fact]
    public async Task FreshReadSequenceRechecksGuideAfterGlobalPhotometryAndLocalSafetyLast()
    {
        var calls = new List<string>();
        var boundary = new StartupClockRecoveryBoundary(_ => { calls.Add("local"); return Pass(); },
            _ => { calls.Add("guide"); return Idle(default); }, _ => { calls.Add("photometry-global"); return Idle(default); });
        Assert.Equal(GateDisposition.Passed, (await boundary.CheckAsync(true, default)).Disposition);
        Assert.Equal(new[] { "local", "guide", "photometry-global", "guide", "local" }, calls);
    }

    [Theory]
    [InlineData("late-unsafe")]
    [InlineData("late-capture")]
    [InlineData("guide-busy")]
    [InlineData("guide-owner-changed")]
    [InlineData("photometry-global-busy")]
    public async Task LateUnsafeAndActualOwnersRejectBeforeReconnectCanBeDispatched(string failure)
    {
        var remoteRead = false;
        var guideReads = 0;
        var dispatched = false;
        var boundary = new StartupClockRecoveryBoundary(_ =>
            remoteRead && failure.StartsWith("late-", StringComparison.Ordinal) ? GateResult.Unknown(failure, "") : Pass(),
            _ => Task.FromResult(failure == "guide-busy" || failure == "guide-owner-changed" && ++guideReads == 2
                ? GateResult.Unknown(failure, "") : Pass()),
            _ => { remoteRead = true; return Task.FromResult(failure == "photometry-global-busy" ? GateResult.Unknown(failure, "") : Pass()); });
        var result = await boundary.CheckAsync(true, default);
        if (result.Disposition == GateDisposition.Passed) dispatched = true;
        Assert.Equal(failure, result.Code);
        Assert.False(dispatched);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeOrDuringReadNeverDispatchesOwner(bool duringRead)
    {
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        var boundary = new StartupClockRecoveryBoundary(_ => Pass(), _ =>
        { reads++; cancellation.Cancel(); return Idle(default); }, Idle);
        if (!duringRead) cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => boundary.CheckAsync(true, cancellation.Token));
        Assert.Equal(duringRead ? 1 : 0, reads);
    }

    [Theory]
    [InlineData(ShutterState.ShutterClosed, CoverState.Closed, false)]
    [InlineData(ShutterState.ShutterOpen, CoverState.Closed, true)]
    public async Task JoinedLocalDisconnectDoesNotMasqueradeAsExternalLossOrOpenOptics(
        ShutterState initialRoof, CoverState initialCover, bool committed)
    {
        var roof = Roof(initialRoof);
        var cover = Cover(initialCover);
        var connected = true;
        var commands = new List<string>();
        var startup = new StartupClockRecoveryBoundary(requireConnected =>
            requireConnected && !connected ? GateResult.Unknown("OWNER_LOST", "") :
                StartupClockRecoveryBoundary.OpticalPath(roof, cover, Expected, committed, initialRoof, initialCover), Idle, Idle);
        var owner = new MountClockReconnectBoundary();
        Assert.Equal(GateDisposition.Passed, (await startup.CheckAsync(true, default)).Disposition);
        await owner.RunAsync("disconnect", () => { commands.Add("disconnect"); connected = false; return Task.CompletedTask; },
            TimeSpan.FromSeconds(1), default);
        Assert.Null(owner.BoundaryGate);
        Assert.Equal("OWNER_LOST", (await startup.CheckAsync(true, default)).Code);
        Assert.Equal(GateDisposition.Passed, (await startup.CheckAsync(false, default)).Disposition);
        await owner.RunAsync("connect", () => { commands.Add("connect"); connected = true; return Task.CompletedTask; },
            TimeSpan.FromSeconds(1), default);
        Assert.Equal(GateDisposition.Passed, (await startup.CheckAsync(true, default)).Disposition);
        Assert.Equal(new[] { "disconnect", "connect" }, commands);
        Assert.Equal(initialRoof, roof.ShutterStatus);
        Assert.Equal(initialCover, cover.CoverState);
    }

    [Fact]
    public void ProductionWiringPreservesStrictRuntimeGatesAndSeparateOpeningSequence()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var start = source.IndexOf("private async Task<GateResult> EnsureMountClockSynchronizedAsync", StringComparison.Ordinal);
        var end = source.IndexOf("private C11MainFocusOwnerSnapshot ReadC11MainFocusOwner", start, StringComparison.Ordinal);
        var clock = source[start..end];
        Assert.Contains("allowClosedRoofForUnattendedOpening: startupRepair", clock);
        Assert.Contains("allowInvalidClockForIdleRecovery: true", clock);
        Assert.Contains(": ValidateOpticalCoverOpen()", clock);
        Assert.Contains("ValidateRoofOpeningPrerequisites(context, context.Plan)", clock);
        Assert.Contains("!NightMountClockRecovery.IsClockFailure(prerequisite)", clock);
        Assert.Contains("cameraMediator.IsFreeToCapture(captureOwner ?? this)", clock);
        Assert.Contains("focuser.IsMoving || focuser.IsSettling", clock);
        Assert.Contains("phd2.Snapshot.ConnectionEpoch == guideEpoch", clock);
        Assert.Contains("await qhy.ReadGlobalIdleAsync(token)", clock);
        Assert.Contains("requireConnectedMount: false", clock);
        Assert.Contains("NativeTarget?.ClockBudget is not null", clock);
        Assert.Contains("NativeTarget?.SegmentRunIds.Count > 0", clock);
        Assert.Contains("File.Exists(G3AcquisitionMotionPath", clock);
        Assert.DoesNotContain("OpenShutter", clock);
        Assert.DoesNotContain("OpenCover", clock);
        var setup = source[..start];
        var synchronize = setup.IndexOf("await EnsureMountClockSynchronizedAsync(context", StringComparison.Ordinal);
        var openRoof = setup.IndexOf("await EnsureDomeOrRoofOpenForUnattendedAsync(", synchronize, StringComparison.Ordinal);
        var openCover = setup.IndexOf("await EnsureOpticalCoverOpenAsync(", openRoof, StringComparison.Ordinal);
        Assert.True(synchronize >= 0 && openRoof > synchronize && openCover > openRoof);
    }
}
