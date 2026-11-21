using System.Text.Json;
using System.Reflection;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class NightSafetyStandbyTests
{
    [Theory]
    [InlineData(ShutterState.ShutterOpen)]
    [InlineData(ShutterState.ShutterClosed)]
    public async Task FirstConnectedRoofSnapshotMustActuallyReachStoppedTerminalState(ShutterState terminal)
    {
        var roof = new DomeInfo { Connected = true, DeviceId = "RRCI.Dome", ShutterStatus = (ShutterState)5 };
        var reads = 0;
        var gate = await RealObservationStageRunnerFactory.WaitForStandbyReadbackAsync(() =>
        {
            if (++reads == 3) roof.ShutterStatus = terminal;
            return RealObservationStageRunnerFactory.CheckStandbyRoofReadback(roof, "RRCI.Dome");
        }, default, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1));
        Assert.Equal(GateDisposition.Passed, gate.Disposition);
        Assert.Equal(3, reads);
    }

    [Theory]
    [InlineData("invalid-state")]
    [InlineData("error")]
    [InlineData("moving")]
    [InlineData("wrong-owner")]
    [InlineData("disconnected")]
    public async Task UnknownOrChangedRoofCannotBecomeOpenJustByWaiting(string mode)
    {
        var roof = new DomeInfo { Connected = mode != "disconnected", DeviceId = mode == "wrong-owner" ? "other-roof" : "RRCI.Dome",
            ShutterStatus = mode == "invalid-state" ? (ShutterState)5 : mode == "error" ? ShutterState.ShutterError : ShutterState.ShutterOpen,
            Slewing = mode == "moving" };
        var gate = await RealObservationStageRunnerFactory.WaitForStandbyReadbackAsync(
            () => RealObservationStageRunnerFactory.CheckStandbyRoofReadback(roof, "RRCI.Dome"), default,
            TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(1));
        Assert.NotEqual(GateDisposition.Passed, gate.Disposition);
        Assert.Equal(mode == "wrong-owner" ? "NIGHT_STANDBY_ROOF_IDENTITY_CHANGED" :
            mode == "disconnected" ? "NIGHT_STANDBY_ROOF_DISCONNECTED" : "NIGHT_STANDBY_ROOF_STATE_UNCONFIRMED", gate.Code);
    }

    [Fact]
    public async Task SafetyOrIdentityFailureIsNeverRetriedAsInitialConnectionReadback()
    {
        var reads = 0;
        var gate = await RealObservationStageRunnerFactory.WaitForStandbyReadbackAsync(() =>
        { reads++; return GateResult.Unknown("NIGHT_SAFETY_UNAVAILABLE", "unsafe"); }, default);
        Assert.Equal(1, reads); Assert.Equal("NIGHT_SAFETY_UNAVAILABLE", gate.Code);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RealObservationStageRunnerFactory.WaitForStandbyReadbackAsync(
            () => GateResult.Unknown("NIGHT_STANDBY_ROOF_STATE_UNCONFIRMED", "unknown"), new CancellationToken(true)));
    }

    [Theory]
    [InlineData("dawn")]
    [InlineData("deadline")]
    [InlineData("unsafe")]
    [InlineData("unavailable")]
    public async Task EveningWaitDoesNotCloseButMorningDeadlineAndUnsafeStillCloseExactlyOnce(string reason)
    {
        using var f = new Fixture();
        using var reservation = await f.Reserve();
        var evening = DateTimeOffset.Parse("2026-10-21T18:00:31+08:00");
        var morning = evening.AddHours(11.5);
        var plan = new NativeSequencePlan(1, 1, 0, StopAtDawn: true,
            DeadlineUtc: reason == "deadline" ? evening.AddHours(6.5) : null);
        var phase = 0;
        var firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = f.Request with { StopGate = () =>
        {
            var later = Volatile.Read(ref phase) != 0;
            var now = later ? morning : evening;
            var gate = plan.StopGate(now, t => later && reason == "unavailable" ? double.NaN :
                t == now ? -10 : later ? -10.2 : -9.8);
            if (!later) firstRead.TrySetResult();
            return gate;
        } };
        var guard = await f.Host.BeginSafetyStandbyAsync(f.Owner, request);
        await firstRead.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("Waiting", guard.Snapshot.Phase);
        Assert.Equal(0, f.Closes);
        if (reason == "unsafe") f.Unsafe!();
        else Volatile.Write(ref phase, 1);
        await guard.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("Closed", guard.Snapshot.Phase);
        Assert.Equal(reason switch { "dawn" => "NIGHT_DAWN_REACHED", "deadline" => "NIGHT_DEADLINE_REACHED",
            "unsafe" => "NIGHT_UNSAFE", _ => "NIGHT_SAFETY_READ_FAILED" }, guard.Snapshot.Reason);
        Assert.Equal(1, f.Closes);
        guard.RequestCloseout();
        Assert.Equal(1, f.Closes);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)]
    public async Task WindowsPendingDuringStandbyClosesOnceOrRetainsTheAlarm(bool monitoringOnly, bool fails)
    {
        using var f = new Fixture();
        using var reservation = await f.Reserve();
        var pending = 0; var closes = 0;
        var probe = WindowsRestartProtectionTests.Probe(() => Volatile.Read(ref pending) == 0 ? UpdateRestartState.NotRequired : UpdateRestartState.Required);
        var guard = await f.Host.BeginSafetyStandbyAsync(f.Owner, f.Request with
        {
            MonitoringOnly = monitoringOnly, StopGate = probe.PollGate, RestartCloseout = _ =>
            { Interlocked.Increment(ref closes); return Task.FromResult<IReadOnlyList<string>>(fails ? ["missing readback"] : []); }
        });
        await probe.ReadAsync(default, true); Assert.Null(probe.PollGate());
        Volatile.Write(ref pending, 1); await probe.ReadAsync(default, true);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (guard.Snapshot.Reason != WindowsRestartProbe.Code && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(WindowsRestartProbe.Code, guard.Snapshot.Reason);
        await guard.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(monitoringOnly ? "MonitoringOnly" : fails ? "CloseoutFailed" : "Closed", guard.Snapshot.Phase);
        Assert.Equal(monitoringOnly ? 0 : 1, closes); Assert.Equal(0, f.Closes);
        if (guard.Snapshot.IsActive)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reserve());
            guard.RequestCloseout(); Assert.Equal(monitoringOnly ? 0 : 1, closes);
        }
        f.Host.Dispose(); await guard.LifetimeCompletion.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task FailedConnectionBoundaryCanBeRecheckedOnlyOnExplicitNewNight()
    {
        using var f = new Fixture();
        using var old = await f.Reserve();
        var idle = false;
        var checks = 0;
        var guard = await f.Host.BeginSafetyStandbyAsync(f.Owner, f.Request with
        {
            CanHandOver = false,
            MonitoringOnly = true,
            RecheckIdle = _ => { checks++; return Task.FromResult(idle
                ? GateResult.Pass("IDLE", "Synthetic stopped owners")
                : GateResult.Unknown("NIGHT_STANDBY_ATR_DISCONNECTED", "ATR 未连接；请连接原设备后重试，不需要重启。")); }
        });
        Assert.Equal(0, checks); // Guardian itself never starts recovery.
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reserve());
        Assert.Contains("NIGHT_STANDBY_ATR_DISCONNECTED", refusal.Message);
        Assert.Contains("不需要重启", refusal.Message);
        Assert.Equal("NIGHT_STANDBY_ATR_DISCONNECTED", guard.Snapshot.LastHandoverGate?.Code);
        using (var failedCheck = JsonDocument.Parse(await File.ReadAllTextAsync(guard.Snapshot.JournalPath)))
            Assert.Equal("NIGHT_STANDBY_ATR_DISCONNECTED", failedCheck.RootElement.GetProperty("current")
                .GetProperty("LastHandoverGate").GetProperty("Code").GetString());
        Assert.Equal(1, checks);
        Assert.Equal("MonitoringOnly", guard.Snapshot.Phase);
        idle = true;
        using var next = await f.Host.ReserveNightAsync(new object(), false, default,
            () => f.Watch(() => { }), "config");
        await guard.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, checks);
        Assert.Equal("Transferred", guard.Snapshot.Phase);
        Assert.Contains("历史失败记录保留", guard.Snapshot.Message);
        Assert.Equal(0, f.Closes);
        using var journal = JsonDocument.Parse(await File.ReadAllTextAsync(guard.Snapshot.JournalPath));
        Assert.False(journal.RootElement.GetProperty("CanHandOver").GetBoolean());
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reserve()); // New owner really holds the lease.
    }

    [Theory]
    [InlineData("config")]
    [InlineData("real")]
    [InlineData("missing-proof")]
    [InlineData("read-error")]
    public async Task RecheckCannotEraseUnsettledResponsibilitiesOrChangeBinding(string reason)
    {
        using var f = new Fixture();
        using var old = await f.Reserve();
        var checks = 0;
        var guard = await f.Host.BeginSafetyStandbyAsync(f.Owner, f.Request with
        {
            CanHandOver = false,
            RecheckIdle = reason == "missing-proof" ? null : _ =>
            { checks++; if (reason == "read-error") throw new IOException("offline"); return Task.FromResult(GateResult.Pass("IDLE", "Synthetic")); }
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host.ReserveNightAsync(new object(),
            reason == "real", default, () => f.Watch(() => { }), reason == "config" ? "changed" : "config"));
        Assert.Equal(reason == "read-error" ? 1 : 0, checks);
        Assert.Equal("Waiting", guard.Snapshot.Phase);
        Assert.Equal(0, f.Closes);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("NIGHT_UNSAFE")]
    [InlineData("NIGHT_DEADLINE_REACHED")]
    [InlineData("NIGHT_DAWN_REACHED")]
    public async Task SafetyOrCancellationDuringRecheckCannotCommitTransfer(string reason)
    {
        using var f = new Fixture();
        using var old = await f.Reserve();
        using var cts = new CancellationTokenSource();
        var guard = await f.Host.BeginSafetyStandbyAsync(f.Owner, f.Request with
        {
            CanHandOver = false,
            RecheckIdle = _ =>
            {
                if (reason == "cancel") cts.Cancel();
                else f.Stop = GateResult.Unknown(reason, "Changed during idle check");
                return Task.FromResult(GateResult.Pass("IDLE", "Synthetic"));
            }
        });
        var watches = 0;
        await Assert.ThrowsAnyAsync<Exception>(() => f.Host.ReserveNightAsync(new object(), false, cts.Token,
            () => { watches++; return f.Watch(() => { }); }, "config"));
        Assert.Equal(0, watches);
        Assert.NotEqual("Transferred", guard.Snapshot.Phase);
    }

    [Fact]
    public async Task ConcurrentRejectedRechecksKeepOneValidJournalAndOriginalProtection()
    {
        using var f = new Fixture();
        using var old = await f.Reserve();
        var guard = await f.Host.BeginSafetyStandbyAsync(f.Owner, f.Request with
        {
            CanHandOver = false, MonitoringOnly = true,
            RecheckIdle = async _ =>
            {
                await Task.Yield();
                return GateResult.Unknown("NIGHT_STANDBY_ATR_DISCONNECTED", "Recorded disconnected ATR; no hardware");
            },
        });
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => guard.RecheckIdleAsync(false, "config", default)));
        Assert.Equal("MonitoringOnly", guard.Snapshot.Phase);
        Assert.Null(guard.Snapshot.Errors);
        Assert.Equal(1, f.Watches); Assert.Equal(0, f.Closes);
        using var journal = JsonDocument.Parse(await File.ReadAllTextAsync(guard.Snapshot.JournalPath));
        Assert.Equal("NIGHT_STANDBY_ATR_DISCONNECTED", journal.RootElement.GetProperty("current")
            .GetProperty("LastHandoverGate").GetProperty("Code").GetString());
    }

    [Theory]
    [InlineData("NIGHT_SAFETY_UNAVAILABLE")]
    [InlineData("NIGHT_UNSAFE")]
    [InlineData("NIGHT_DAWN_REACHED")]
    [InlineData("NIGHT_DEADLINE_REACHED")]
    public async Task PolledSafetyStopInMonitorOnlyRemainsLatchedAfterReadbackBecomesSafe(string reason)
    {
        using var f = new Fixture();
        using var old = await f.Reserve();
        var guard = await f.Host.BeginSafetyStandbyAsync(f.Owner, f.Request with
        {
            CanHandOver = false, MonitoringOnly = true,
            RecheckIdle = _ => Task.FromResult(GateResult.Pass("IDLE", "Synthetic same-owner stopped readback")),
        });
        await guard.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        var deadline = guard.Snapshot.DeadlineUtc;
        f.Stop = GateResult.Unknown(reason, "Synthetic polling-only stop: no unsafe event");
        Assert.False(guard.RequestCloseout()); // Only wakes diagnostics; it grants no action.
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (guard.Snapshot.LatestSafetyReason != reason) await Task.Delay(5, limit.Token);
        f.Stop = null;
        guard.RequestCloseout();
        while (guard.Snapshot.LatestSafetyReason is not null) await Task.Delay(5, limit.Token);
        Assert.Equal(reason, guard.Snapshot.Reason);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reserve());
        Assert.Contains("NIGHT_STANDBY_CLOSEOUT_LATCHED", refused.Message);
        Assert.Contains(reason, refused.Message);
        Assert.Equal(deadline, guard.Snapshot.DeadlineUtc);
        Assert.Equal("MonitoringOnly", guard.Snapshot.Phase);
        Assert.Equal(0, f.Closes); Assert.Equal(1, f.Watches);
    }

    [Fact]
    public async Task UnavailableOperatorCloseoutDoesNotPoisonExplicitFreshIdleRetry()
    {
        using var f = new Fixture();
        using var old = await f.Reserve();
        var guard = await f.Host.BeginSafetyStandbyAsync(f.Owner, f.Request with
        {
            CanHandOver = false, MonitoringOnly = true,
            RecheckIdle = _ => Task.FromResult(GateResult.Pass("IDLE", "Synthetic same-owner stopped readback")),
        });
        await guard.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(f.Host.RequestSafetyStandbyCloseout());
        Assert.Null(guard.Snapshot.Reason);
        using var next = await f.Host.ReserveNightAsync(new object(), false, default,
            () => f.Watch(() => { }), "config");
        await guard.LifetimeCompletion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("Transferred", guard.Snapshot.Phase);
        Assert.Equal(0, f.Closes); Assert.Equal(1, f.Watches);
    }

    [Theory]
    [InlineData("NIGHT_UNSAFE")]
    [InlineData("NIGHT_SAFETY_UNAVAILABLE")]
    [InlineData("NIGHT_DEADLINE_REACHED")]
    [InlineData("NIGHT_DAWN_REACHED")]
    [InlineData("OPERATOR_CLOSEOUT")]
    public async Task IndependentLifetimeKeepsProtectionAndClosesExactlyOnce(string reason)
    {
        using var f = new Fixture();
        using var reservation = await f.Reserve();
        var guard = await f.Host.BeginSafetyStandbyAsync(f.Owner, f.Request);
        reservation.Dispose();
        f.Host.Cancel(); // No active observation. Does not cancel the guardian.
        Assert.Equal("Waiting", guard.Snapshot.Phase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host.RunFocusPreparationAsync(() => Task.FromResult(1), default));
        if (reason == "NIGHT_UNSAFE") f.Unsafe!();
        else if (reason == "OPERATOR_CLOSEOUT") Assert.True(f.Host.RequestSafetyStandbyCloseout());
        else f.Stop = GateResult.Unknown(reason, "Synthetic read-only stop gate.");
        await guard.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, f.Closes);
        Assert.Equal("Closed", guard.Snapshot.Phase);
        Assert.Equal(reason, guard.Snapshot.Reason);
        Assert.False(guard.Snapshot.PhysicalHardware);
        f.Unsafe!(); f.Host.RequestSafetyStandbyCloseout();
        Assert.Equal(1, f.Closes);
        using var journal = JsonDocument.Parse(await File.ReadAllTextAsync(guard.Snapshot.JournalPath));
        Assert.Equal(reason, journal.RootElement.GetProperty("current").GetProperty("Reason").GetString());
        using var next = await f.Reserve();
    }

    [Fact]
    public async Task NewExplicitNightAtomicallyTakesLeaseWithoutRoofCycleOrMonitorGap()
    {
        using var f = new Fixture();
        using var old = await f.Reserve();
        var guard = await f.Host.BeginSafetyStandbyAsync(f.Owner, f.Request);
        old.Dispose();
        Assert.Equal(1, f.Watches);
        using var next = await f.Host.ReserveNightAsync(new object(), false, default,
            () => { Assert.Equal(1, f.Watches); return f.Watch(() => { }); }, "config");
        await guard.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("Transferred", guard.Snapshot.Phase);
        Assert.Equal(0, f.Closes);
        Assert.Equal(1, f.Watches);
        Assert.Null(f.Host.Dashboard.SafetyStandby); // No old 'waiting' status in the new run.
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reserve()); // Old disposal didn't free new lease.
        next.Dispose(); Assert.Equal(0, f.Watches);
        using var after = await f.Reserve();
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("config")]
    [InlineData("unsafe")]
    [InlineData("deadline")]
    [InlineData("real")]
    public async Task IncompatibleOrClosingGuardCannotBeOvertaken(string reason)
    {
        using var f = new Fixture();
        using var old = await f.Reserve();
        var closeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = f.Request with { CanHandOver = reason != "busy", Closeout = async _ =>
        { closeEntered.SetResult(); await finish.Task; return Array.Empty<string>(); } };
        var guard = await f.Host.BeginSafetyStandbyAsync(f.Owner, request);
        if (reason is "unsafe" or "deadline")
        {
            f.Stop = GateResult.Unknown(reason, "Synthetic");
            guard.RequestCloseout(reason);
            await closeEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host.ReserveNightAsync(new object(),
            reason == "real", default, configurationKey: reason == "config" ? "changed" : "config"));
        guard.RequestCloseout(); finish.TrySetResult();
        await guard.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task FailedNewWatchLeavesExistingGuardianArmed()
    {
        using var f = new Fixture();
        using var old = await f.Reserve();
        var guard = await f.Host.BeginSafetyStandbyAsync(f.Owner, f.Request);
        await Assert.ThrowsAsync<IOException>(() => f.Host.ReserveNightAsync(new object(), false, default,
            () => throw new IOException("Synthetic failed handoff"), "config"));
        Assert.Equal("Waiting", guard.Snapshot.Phase); Assert.Equal(1, f.Watches);
        f.Unsafe!(); await guard.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, f.Closes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseoutErrorsRemainVisibleAndAreNeverRetried(bool throws)
    {
        using var f = new Fixture();
        using var old = await f.Reserve();
        var guard = await f.Host.BeginSafetyStandbyAsync(f.Owner, f.Request with { Closeout = _ =>
        { f.Closes++; if (throws) throw new IOException("offline"); return Task.FromResult<IReadOnlyList<string>>(new[] { "idle not confirmed" }); } });
        guard.RequestCloseout(); await guard.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("CloseoutFailed", guard.Snapshot.Phase);
        Assert.NotEmpty(guard.Snapshot.Errors!); Assert.Equal(1, f.Closes);
        Assert.Equal(guard.Snapshot, f.Host.Dashboard.SafetyStandby);
    }

    [Fact]
    public async Task ApplicationExitStopsSoftwareWatchWithoutMovingOrAutomaticallyRestoringIt()
    {
        using var f = new Fixture();
        using var old = await f.Reserve();
        var guard = await f.Host.BeginSafetyStandbyAsync(f.Owner, f.Request);
        f.Host.Dispose(); await guard.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("Shutdown", guard.Snapshot.Phase);
        Assert.Equal(0, f.Closes); Assert.Equal(0, f.Watches);
        using var restarted = new ObservationCoordinatorHost(new SilentNotifier());
        Assert.Null(restarted.SafetyStandby); // Historical journal isn't execution authority.
    }

    [Fact]
    public async Task ReadFailureRequestsCheckedCloseoutInsteadOfAbandoningProtection()
    {
        using var f = new Fixture();
        using var old = await f.Reserve();
        var guard = await f.Host.BeginSafetyStandbyAsync(f.Owner, f.Request with { StopGate = () => throw new IOException("Disconnected") });
        await guard.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("NIGHT_SAFETY_READ_FAILED", guard.Snapshot.Reason); Assert.Equal(1, f.Closes);
    }

    [Fact]
    public async Task JournalArmFailureRetainsOriginalLeaseAndDoesNotLeakWatch()
    {
        using var f = new Fixture();
        using var old = await f.Reserve();
        var file = Path.GetTempFileName();
        await Assert.ThrowsAnyAsync<IOException>(() => f.Host.BeginSafetyStandbyAsync(f.Owner,
            f.Request with { ParentJournal = Path.Combine(file, "night.json") }));
        Assert.Equal(0, f.Watches); Assert.Equal(0, f.Closes); Assert.Null(f.Host.SafetyStandby);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reserve());
        old.Dispose(); using var next = await f.Reserve();
    }

    [Fact]
    public async Task LostJournalWriteDuringCloseoutDoesNotSkipMechanicalSafetyOrClaimSuccess()
    {
        using var f = new Fixture();
        using var old = await f.Reserve();
        var guard = await f.Host.BeginSafetyStandbyAsync(f.Owner, f.Request);
        Directory.CreateDirectory(guard.Snapshot.JournalPath + ".tmp"); // Synthetic write failure, no raw inputs.
        guard.RequestCloseout(); await guard.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, f.Closes); Assert.Equal("CloseoutFailed", guard.Snapshot.Phase);
        Assert.Contains("日志写入失败", guard.Snapshot.Message);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task UnknownStopKeepsMonitorAndOriginalDeadlineWithoutAnyMechanicalAttempt(bool journalUnavailable)
    {
        using var f = new Fixture();
        using var old = await f.Reserve();
        var request = f.Request with { MonitoringOnly = true, CanHandOver = false, InitialReason = "NIGHT_UNSAFE" };
        if (journalUnavailable) request = request with { ParentJournal = Path.Combine(Path.GetTempFileName(), "night.json") };
        var guard = await f.Host.BeginSafetyStandbyAsync(f.Owner, request);
        old.Dispose();
        await guard.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(guard.Snapshot.IsActive);
        Assert.Equal("MonitoringOnly", guard.Snapshot.Phase);
        Assert.Equal(request.DeadlineUtc, guard.Snapshot.DeadlineUtc);
        Assert.Equal("NIGHT_UNSAFE", guard.Snapshot.Reason);
        Assert.Contains("不能保证关顶", guard.Snapshot.Message);
        if (journalUnavailable) Assert.Contains("日志写入失败", guard.Snapshot.Message);
        f.Stop = GateResult.Unknown("NIGHT_DAWN_REACHED", "Synthetic morning, no hardware");
        f.Unsafe!();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (guard.Snapshot.LatestSafetyReason != "NIGHT_DAWN_REACHED") await Task.Delay(10, deadline.Token);
        Assert.Equal(1, f.Watches); Assert.Equal(0, f.Closes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reserve());
        f.Host.RequestSafetyStandbyCloseout(); Assert.Equal(0, f.Closes);
        f.Host.Dispose(); await guard.LifetimeCompletion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("Shutdown", guard.Snapshot.Phase); Assert.Equal(0, f.Watches);
    }

    [Fact]
    public async Task FailedCloseoutFinishesNativeWaitButRetainsWatchAndLeaseUntilApplicationExit()
    {
        using var f = new Fixture();
        using var old = await f.Reserve();
        var guard = await f.Host.BeginSafetyStandbyAsync(f.Owner, f.Request with
        {
            InitialReason = "NIGHT_UNSAFE",
            Closeout = _ => { f.Closes++; return Task.FromResult<IReadOnlyList<string>>(new[] { "Roof terminal readback missing" }); },
        });
        old.Dispose();
        await guard.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("CloseoutFailed", guard.Snapshot.Phase);
        Assert.True(guard.Snapshot.IsActive); Assert.False(guard.LifetimeCompletion.IsCompleted);
        Assert.Equal(1, f.Watches); Assert.Equal(1, f.Closes);
        f.Unsafe!(); guard.RequestCloseout();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reserve());
        Assert.Equal(1, f.Closes);
        f.Host.Dispose(); await guard.LifetimeCompletion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, f.Watches);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void OnlySafetyCloseoutOutlivesCancelledObservingToken(bool safety)
    {
        using var observing = new CancellationTokenSource(); observing.Cancel();
        using var closeout = SequenceItems.UvexNightSequenceContainer.CreateCloseoutLifetime(safety, observing.Token);
        Assert.Equal(!safety, closeout.IsCancellationRequested);
        closeout.Cancel(); Assert.True(closeout.IsCancellationRequested); // Its own bounded timeout remains effective.
    }

    [Theory]
    [InlineData("delayed", false)] [InlineData("stuck", false)]
    [InlineData("identity", false)] [InlineData("delayed", true)]
    public async Task RecordedCancellationBoundaryReachesCheckedRoofHomeParkOrSendsNothing(string mode, bool roofRejects)
    {
        using var f = new Fixture();
        using var old = await f.Reserve();
        using var cancelledObservation = new CancellationTokenSource(); cancelledObservation.Cancel();
        var calls = new List<string>();
        var mountInfo = new TelescopeInfo { Connected = true, DeviceId = "mount", CanFindHome = true };
        var mount = DispatchProxy.Create<ITelescopeMediator, CommandProxy>();
        ((CommandProxy)(object)mount).InvokeCommand = (method, args) => method.Name switch
        {
            "GetInfo" => mountInfo,
            "FindHome" => Home(),
            "ParkTelescope" => Park(),
            _ => throw new InvalidOperationException("Unexpected mechanical action: " + method.Name),
        };
        Task<bool> Home() { calls.Add("home"); mountInfo.AtHome = true; return Task.FromResult(true); }
        Task<bool> Park() { Assert.True(mountInfo.AtHome); calls.Add("park"); mountInfo.AtPark = true; return Task.FromResult(true); }
        var guard = await f.Host.BeginSafetyStandbyAsync(f.Owner, f.Request with
        {
            InitialReason = "NIGHT_UNSAFE",
            Closeout = async _ =>
            {
                using var close = SequenceItems.UvexNightSequenceContainer.CreateCloseoutLifetime(true, cancelledObservation.Token);
                close.CancelAfter(TimeSpan.FromSeconds(3));
                var stopped = await Phd2MountIdleWaitTests.ReplayCancelledSlew(mode, close.Token);
                if (stopped.Disposition != GateDisposition.Passed) return new[] { stopped.Code };
                calls.Add("cover"); // Finite cover port; no camera or COM connection.
                var issue = await RoofClearancePolicy.CloseThenParkAsync(
                    _ => Task.FromResult<string?>(null),
                    _ => { calls.Add("roof"); return Task.FromResult<string?>(roofRejects ? "Roof refused" : null); },
                    async ct =>
                    {
                        var home = await RealObservationStageRunner.HomeAndVerifyMountAsync(mount, "mount",
                            new Progress<ApplicationStatus>(), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1), ct);
                        if (home.Disposition != GateDisposition.Passed) return home.Code;
                        return await mount.ParkTelescope(new Progress<ApplicationStatus>(), ct) && mount.GetInfo().AtPark ? null : "Park not verified";
                    }, close.Token);
                return issue is null ? Array.Empty<string>() : new[] { issue };
            },
        });
        old.Dispose(); await guard.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(mode == "delayed" && !roofRejects ? "Closed" : "CloseoutFailed", guard.Snapshot.Phase);
        Assert.Equal(mode != "delayed" ? [] : roofRejects ? new[] { "cover", "roof" } : new[] { "cover", "roof", "home", "park" }, calls);
        var count = calls.Count;
        guard.RequestCloseout(); f.Unsafe!(); Assert.Equal(count, calls.Count);
        f.Host.Dispose(); await guard.LifetimeCompletion.WaitAsync(TimeSpan.FromSeconds(3));
    }

    public class CommandProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> InvokeCommand = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => InvokeCommand(method!, args);
    }

    private sealed class Fixture : IDisposable
    {
        public object Owner { get; } = new();
        public ObservationCoordinatorHost Host { get; } = new(new SilentNotifier());
        public Action? Unsafe;
        public GateResult? Stop;
        public int Closes, Watches;
        public IDisposable Watch(Action callback) { Unsafe = callback; Interlocked.Increment(ref Watches); return new Subscription(() => Interlocked.Decrement(ref Watches)); }
        public NightSafetyStandbyRequest Request => new(false, "config", DateTimeOffset.UtcNow.AddHours(1),
            Path.Combine(Path.GetTempPath(), "UvexAdv-standby-tests", Guid.NewGuid().ToString("N"), "night.json"),
            true, Watch, () => Stop, _ => { Interlocked.Increment(ref Closes); return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()); });
        public Task<IDisposable> Reserve() => Host.ReserveNightAsync(Owner, false, default, configurationKey: "config");
        public void Dispose() => Host.Dispose();
    }

    private sealed class Subscription(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    private sealed class SilentNotifier : IObservationAttentionNotifier
    {
        public void Notify(ObservationAttentionNotification notification) { }
        public void ClearActiveIndicator() { }
        public void Dispose() { }
    }
}
