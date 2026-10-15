using System.Text.Json;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class NightSafetyStandbyTests
{
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
            RecheckIdle = _ => { checks++; return Task.FromResult(idle); }
        });
        Assert.Equal(0, checks); // Guardian itself never starts recovery.
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reserve());
        Assert.Equal(1, checks);
        Assert.Equal("Waiting", guard.Snapshot.Phase);
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
            { checks++; if (reason == "read-error") throw new IOException("offline"); return Task.FromResult(true); }
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host.ReserveNightAsync(new object(),
            reason == "real", default, () => f.Watch(() => { }), reason == "config" ? "changed" : "config"));
        Assert.Equal(reason == "read-error" ? 1 : 0, checks);
        Assert.Equal("Waiting", guard.Snapshot.Phase);
        Assert.Equal(0, f.Closes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SafetyOrCancellationDuringRecheckCannotCommitTransfer(bool cancel)
    {
        using var f = new Fixture();
        using var old = await f.Reserve();
        using var cts = new CancellationTokenSource();
        var guard = await f.Host.BeginSafetyStandbyAsync(f.Owner, f.Request with
        {
            CanHandOver = false,
            RecheckIdle = _ =>
            {
                if (cancel) cts.Cancel();
                else f.Stop = GateResult.Unknown("NIGHT_UNSAFE", "Changed during idle check");
                return Task.FromResult(true);
            }
        });
        var watches = 0;
        await Assert.ThrowsAnyAsync<Exception>(() => f.Host.ReserveNightAsync(new object(), false, cts.Token,
            () => { watches++; return f.Watch(() => { }); }, "config"));
        Assert.Equal(0, watches);
        Assert.NotEqual("Transferred", guard.Snapshot.Phase);
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
