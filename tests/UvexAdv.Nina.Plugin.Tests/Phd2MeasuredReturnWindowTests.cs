using System.Reflection;
using UvexAdv.Phd2;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2MeasuredReturnWindowTests
{
    [Fact]
    public async Task NativeAverageDistanceDoesNotVetoFreshFrameBoundPositions()
    {
        var (client, proxy) = Create();
        proxy.Offsets = [6, 4, 2.1, 1.64, 1.3];
        var frames = await Observe(client);
        Assert.Equal(5, proxy.Count);
        Assert.Equal(3, frames.Count);
        Assert.All(frames, f => Assert.Equal(4.6, f.NativeGuideStep!.AverageDistancePixels));
        Assert.False(proxy.State.HasCurrentSuccessfulSettle);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MultiStarOffsetCannotClaimOrPreventPhysicalArrival(bool physicallyArrived)
    {
        var (client, proxy) = Create();
        proxy.Offsets = physicallyArrived ? [1, 1, 1] : [16, 16, 16];
        proxy.MultiStarOffset = physicallyArrived ? 16 : .1;
        if (physicallyArrived)
            Assert.Equal(3, (await Observe(client)).Count);
        else
        {
            var failure = await Assert.ThrowsAsync<PhysicalActionGateException>(() => Observe(client));
            Assert.Equal("PHD2_LOCK_RETURN_POSITION_UNCONFIRMED", failure.Gate.Code);
            Assert.Equal(16, proxy.Count);
            await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("skip", failure.Gate.Code,
                failure.Gate, ObservationStage.PlaceTargetOnSlit);
        }
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("epoch")]
    [InlineData("hash")]
    [InlineData("interrupted")]
    [InlineData("no-centroid")]
    [InlineData("wrong-lock")]
    [InlineData("stale")]
    [InlineData("never-arrived")]
    public async Task CoordinatesAloneAndInvalidFramesNeverConfirmReturn(string fault)
    {
        var (client, proxy) = Create(); proxy.Fault = fault;
        var failure = await Assert.ThrowsAsync<PhysicalActionGateException>(() => Observe(client));
        Assert.InRange(proxy.Count, 1, 16);
        var ordinary = fault is "never-arrived" or "lost" or "no-centroid";
        Assert.Equal(ordinary ? GateFailureScope.TargetQuality : GateFailureScope.Run, failure.Gate.FailureScope);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(ordinary ? "skip" : "unknown-gate",
            failure.Gate.Code, failure.Gate, ObservationStage.PlaceTargetOnSlit);
    }

    private static Task<IReadOnlyList<Phd2GuidingFrameResult>> Observe(IPhd2Client client) =>
        Phd2MeasuredReturnWindow.ObserveAsync(client, 1, 2, new(100, 100), .1, 3,
            DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(10),
            () => "test.fit", TimeSpan.FromSeconds(2), default,
            (frame, _) => Task.FromResult(frame.NativeGuideStep is null ? null :
                ((Proxy)(object)client).PrimaryPosition));
    private static (IPhd2Client, Proxy) Create()
    {
        var client = DispatchProxy.Create<IPhd2Client, Proxy>(); return (client, (Proxy)(object)client);
    }
    public class Proxy : DispatchProxy
    {
        public Phd2StateSnapshot State = Phd2StateSnapshot.Disconnected with { IsConnected = true,
            AppState = Phd2AppState.Guiding, ConnectionEpoch = 1, GuideEpoch = 2, LockPosition = new(100, 100) };
        public string Fault = "";
        public double[] Offsets = [1, 1, 1];
        public int Count;
        public Phd2Point? PrimaryPosition;
        public double? MultiStarOffset;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "get_Snapshot") return State;
            if (method.Name != "SaveCurrentGuidingFrameAsync") throw new InvalidOperationException(method.Name);
            var n = ++Count;
            var now = DateTimeOffset.UtcNow;
            var offset = Fault == "never-arrived" ? 5 : Offsets[Math.Min(n-1, Offsets.Length-1)];
            PrimaryPosition = new(100 + offset, 100);
            if (Fault == "epoch") State = State with { GuideEpoch = 3 };
            if (Fault == "lost") State = State with { AppState = Phd2AppState.LostLock };
            var result = new Phd2GuidingFrameResult("test.fit", n.ToString("x64"), n, n, now, now,
                Fault == "interrupted", false, false, false,
                new(n, MultiStarOffset ?? offset, 0, 20, 6, 4.6, 0), new(100, 100));
            result = Fault switch {
                "hash" => result with { Sha256 = "a" },
                "stale" => result with { GuideStepUtc = now.AddMinutes(-5) },
                "wrong-lock" => result with { NativeLockPosition = new(110, 100) },
                "no-centroid" => result with { NativeGuideStep = null }, _ => result };
            return Task.FromResult(result);
        }
    }
}
