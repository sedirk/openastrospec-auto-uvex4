using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class NativeTargetBoundaryOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JoinedSegmentRetainsExactNightLeaseAndAlwaysReleasesHandoff(bool fail)
    {
        var root = Path.Combine(Path.GetTempPath(), "uvex-native-boundary-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "owner.lock");
        using var host = new ObservationCoordinatorHost(new SilentNotifier(), path);
        var owner = new object();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using (await host.ReserveNightAsync(owner, true, timeout.Token))
        {
            var first = Plan("first");
            var runner = new Fixture();
            await host.RunAsync(first, runner, timeout.Token, owner);
            Assert.Equal(11, runner.Stages.Count);
            Assert.NotEqual(GateDisposition.Passed, host.RealRunOwnershipGate().Disposition);
            var calls = 0;
            async Task Handoff(CancellationToken token)
            {
                calls++;
                Assert.Equal(GateDisposition.Passed, host.RealRunOwnershipGate().Disposition);
                Assert.False(RealObservationRunOwnershipLease.TryAcquire(path).Acquired);
                // A new run cannot steal this terminal segment's boundary.
                await Assert.ThrowsAsync<InvalidOperationException>(() => host.RunAsync(Plan("race"), new Fixture(), token, owner));
                await Assert.ThrowsAsync<InvalidOperationException>(() => host.RunNativeTargetBoundaryAsync(owner,
                    first.ObservationRunId, _ => Task.CompletedTask, token));
                if (fail) throw new InvalidOperationException("synthetic failed readback");
            }
            if (fail) await Assert.ThrowsAsync<InvalidOperationException>(() => host.RunNativeTargetBoundaryAsync(owner,
                first.ObservationRunId, Handoff, timeout.Token));
            else await host.RunNativeTargetBoundaryAsync(owner, first.ObservationRunId, Handoff, timeout.Token);
            Assert.Equal(1, calls);
            Assert.NotEqual(GateDisposition.Passed, host.RealRunOwnershipGate().Disposition);
            // Original lease persists, but no leaked local reservation prevents
            // the next independently authorized target's actual stage execution.
            var next = new Fixture();
            await host.RunAsync(Plan("next"), next, timeout.Token, owner);
            Assert.Equal(11, next.Stages.Count);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.RunNativeTargetBoundaryAsync(owner,
                first.ObservationRunId, _ => throw new Exception("stale runner gained authority"), timeout.Token));
        }
        using (var available = RealObservationRunOwnershipLease.TryAcquire(path).Lease) Assert.NotNull(available);
        var standalone = new Fixture();
        await host.RunAsync(Plan("standalone-after-night"), standalone, timeout.Token);
        Assert.Equal(11, standalone.Stages.Count);
    }

    [Fact]
    public async Task ForeignOwnerAndSimulationCannotClaimBoundaryAndCancelReachesJoinedSegment()
    {
        using var host = new ObservationCoordinatorHost(new SilentNotifier(), Path.Combine(Path.GetTempPath(),
            "uvex-native-cancel-" + Guid.NewGuid().ToString("N"), "owner.lock"));
        var owner = new object();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using (await host.ReserveNightAsync(owner, false, timeout.Token))
        {
            var simulated = Plan("simulation");
            await host.RunAsync(simulated, new Fixture(), timeout.Token, owner);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.RunNativeTargetBoundaryAsync(owner,
                simulated.ObservationRunId, _ => Task.CompletedTask, timeout.Token));
        }
        using (await host.ReserveNightAsync(owner, true, timeout.Token))
        {
            var plan = Plan("cancel");
            await host.RunAsync(plan, new Fixture(), timeout.Token, owner);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.RunNativeTargetBoundaryAsync(new object(),
                plan.ObservationRunId, _ => Task.CompletedTask, timeout.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.RunNativeTargetBoundaryAsync(owner,
                plan.ObservationRunId, token => { host.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }, timeout.Token));
            Assert.NotEqual(GateDisposition.Passed, host.RealRunOwnershipGate().Disposition);
            await host.RunAsync(Plan("after-cancel"), new Fixture(), timeout.Token, owner);
        }
    }

    private static ObservationPlan Plan(string name) => new("boundary-" + name + "-" + Guid.NewGuid().ToString("N"), "test",
        new(name, name, 0, 89), new(31, 121, 20), DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), new(0, 0, 0), new(), "ATR", "G3", "QHY");
    private sealed class Fixture : ObservationStageRunnerBase
    {
        internal List<ObservationStage> Stages { get; } = [];
        public override Task<StageResult> ExecuteStageAsync(ObservationStage stage, ObservationContext context, CancellationToken token)
        { Stages.Add(stage); return Task.FromResult(new StageResult(GateResult.Pass("TEST", "synthetic, no hardware"))); }
    }
    private sealed class SilentNotifier : IObservationAttentionNotifier
    {
        public void Notify(ObservationAttentionNotification notification) { }
        public void ClearActiveIndicator() { }
        public void Dispose() { }
    }
}
