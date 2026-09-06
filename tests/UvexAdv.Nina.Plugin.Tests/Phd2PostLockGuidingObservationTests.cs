using System.Reflection;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2PostLockGuidingObservationTests
{
    [Fact]
    public async Task VerifiedShiftObservesExistingStreamWithoutAnyRpcOrSyntheticSettle()
    {
        var (client, proxy) = Client();
        var native = proxy.State.LastSettle!;
        var observation = Observe(client);
        proxy.Publish(proxy.State with { EventSequence = 11, LastGuideStep = Step(11, 0.5) });
        var result = await observation;
        Assert.True(result.TrackingWithinTolerance);
        Assert.Equal(1, result.ObservedGuideFrames);
        Assert.False(result.HasAcceptedWindow(proxy.State));
        Assert.Same(native, proxy.State.LastSettle);
        Assert.False(proxy.State.HasCurrentSuccessfulSettle);
        Assert.Equal(0, proxy.SubscriberCount);

        var accepted = result.AcceptResiduals(Frames(result), proxy.State);
        Assert.True(accepted.HasAcceptedWindow(proxy.State));
        var evidence = accepted.ToCalibrationEvidence(native, proxy.State);
        Assert.Same(native, evidence.Result);
        Assert.False(evidence.Result.Succeeded);
        Assert.False(evidence.GuideCommandAccepted);
        Assert.False(evidence.SettleBeginObserved);
        Assert.True(evidence.ReadOnlyPostLockWindow);
        Assert.True(evidence.ExactLockReadbackVerified);
        Assert.True(evidence.FreshGuidingWindowAccepted);
    }

    [Fact]
    public async Task WindTimeoutIsOnlyObservationAndStillRequiresFreshOpticalWindow()
    {
        var (client, proxy) = Client();
        var observation = Observe(client);
        proxy.Publish(proxy.State with { EventSequence = 11, LastGuideStep = Step(11, 5) });
        var result = await observation;
        Assert.False(result.TrackingWithinTolerance);
        Assert.Equal(1, result.ObservedGuideFrames);
        Assert.False(result.HasAcceptedWindow(proxy.State));
        Assert.Null(proxy.State.LastAlert);
        Assert.Equal("original native timeout", proxy.State.LastSettle!.Error);
        Assert.Equal(0, proxy.SubscriberCount);
        Assert.True(result.AcceptResiduals(Frames(result), proxy.State).HasAcceptedWindow(proxy.State));
    }

    [Fact]
    public async Task TransientLostLockIsNotCoalescedIntoLaterGuiding()
    {
        var (client, proxy) = Client();
        var observation = Observe(client);
        var initial = proxy.State;
        proxy.Publish(initial with { AppState = Phd2AppState.LostLock, EventSequence = 11 });
        proxy.Publish(initial with { EventSequence = 12, LastGuideStep = Step(12, 0) });
        await Assert.ThrowsAsync<Phd2Exception>(() => observation);
        Assert.Equal(0, proxy.SubscriberCount);
    }

    [Theory]
    [InlineData("disconnect")]
    [InlineData("connection-epoch")]
    [InlineData("guide-epoch")]
    [InlineData("lock")]
    [InlineData("pause")]
    [InlineData("automation-pause")]
    [InlineData("pending-settle")]
    public async Task InvalidatedContinuityCannotBecomeWarningOnly(string mutation)
    {
        var (client, proxy) = Client();
        var observation = Observe(client);
        var changed = mutation switch
        {
            "disconnect" => proxy.State with { IsConnected = false },
            "connection-epoch" => proxy.State with { ConnectionEpoch = 2 },
            "guide-epoch" => proxy.State with { GuideEpoch = 3 },
            "lock" => proxy.State with { LockPosition = new Phd2Point(200, 200) },
            "pause" => proxy.State with { Phd2Paused = true },
            "automation-pause" => proxy.State with { AutomationPaused = true },
            _ => proxy.State with { PendingSettleOperationId = 42 },
        };
        proxy.Publish(changed);
        await Assert.ThrowsAsync<Phd2Exception>(() => observation);
        Assert.Equal(0, proxy.SubscriberCount);
    }

    [Fact]
    public async Task CancellationDoesNotBecomeQualityTimeout()
    {
        var (client, proxy) = Client();
        using var cancellation = new CancellationTokenSource();
        var observation = Observe(client, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observation);
        Assert.Equal(0, proxy.SubscriberCount);
    }

    [Theory]
    [InlineData("strict")]
    [InlineData("unverified")]
    [InlineData("profile-mutated")]
    [InlineData("mismatched-readback")]
    [InlineData("nan-position")]
    [InlineData("future")]
    public async Task UnverifiedOrUnsupervisedShiftCannotStartReadOnlyContinuation(string mutation)
    {
        var (client, proxy) = Client();
        var exact = Exact();
        exact = mutation switch
        {
            "unverified" => exact with { Exact = false },
            "profile-mutated" => exact with { RegistryProfileMutated = true },
            "mismatched-readback" => exact with { VerificationErrorPixels = 10 },
            "nan-position" => exact with { Requested = new Phd2Point(double.NaN, 100) },
            "future" => exact with { CompletedUtc = DateTimeOffset.UtcNow.AddMinutes(1) },
            _ => exact,
        };
        await Assert.ThrowsAsync<Phd2Exception>(() => Phd2PostLockGuidingObservation.ObserveAsync(
            client, exact, 1, 2, 0.1, new Phd2SettleCriteria(2, 0, 1), mutation != "strict", default));
        Assert.Equal(0, proxy.SubscriberCount);
    }

    [Theory]
    [InlineData("duplicate-hash")]
    [InlineData("invalid-hash")]
    [InlineData("old-sequence")]
    [InlineData("old-time")]
    [InlineData("future-time")]
    [InlineData("reused-frame")]
    [InlineData("interrupted")]
    [InlineData("exposure-changed")]
    [InlineData("loop-started")]
    [InlineData("two-frames")]
    public async Task ReusedOrInterruptedOpticalEvidenceCannotAuthorizeContinuation(string mutation)
    {
        var (client, proxy) = Client();
        var observation = Observe(client);
        proxy.Publish(proxy.State with { EventSequence = 11, LastGuideStep = Step(11, 0) });
        var result = await observation;
        var frames = Frames(result);
        frames[2] = mutation switch
        {
            "duplicate-hash" => frames[2] with { Sha256 = frames[0].Sha256 },
            "invalid-hash" => frames[2] with { Sha256 = "not-a-hash" },
            "old-sequence" => frames[2] with { EventSequence = result.AfterEventSequence },
            "old-time" => frames[2] with { GuideStepUtc = result.LockVerifiedUtc.AddSeconds(-1) },
            "future-time" => frames[2] with { CompletedUtc = DateTimeOffset.UtcNow.AddMinutes(1) },
            "reused-frame" => frames[2] with { TriggerGuideFrame = frames[1].TriggerGuideFrame },
            "interrupted" => frames[2] with { GuidingWasInterrupted = true },
            "exposure-changed" => frames[2] with { ExposureChanged = true },
            "loop-started" => frames[2] with { CaptureLoopStarted = true },
            _ => frames[2],
        };
        Assert.Throws<Phd2Exception>(() => result.AcceptResiduals(
            mutation == "two-frames" ? frames[..2] : frames, proxy.State));
        var accepted = result.AcceptResiduals(Frames(result), proxy.State);
        Assert.False(accepted.HasAcceptedWindow(proxy.State with { GuideEpoch = 3 }));
    }

    [Fact]
    public void OutboundSupervisedBranchDoesNotRequestNativeGuideAndStrictBranchStillDoes()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.Phd2SlitPlacement.cs"));
        var start = source.IndexOf("if (HasSupervisedScienceOptIn())\n", source.IndexOf("Phd2SettleResult stageSettle;", StringComparison.Ordinal), StringComparison.Ordinal);
        // Source files can be checked out with either LF or CRLF.
        if (start < 0) start = source.IndexOf("if (HasSupervisedScienceOptIn())\r\n", source.IndexOf("Phd2SettleResult stageSettle;", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("else", start, StringComparison.Ordinal);
        var supervised = source[start..end];
        Assert.Contains("Phd2PostLockGuidingObservation.ObserveAsync", supervised);
        Assert.Contains("stageSettle = session.Settle", supervised);
        Assert.DoesNotContain("GuideAndSettleAsync", supervised);
        Assert.Contains("GuideAndSettleAsync", source[end..(end + 850)]);
        Assert.Contains("stageReadOnlyObservation.AcceptResiduals", source);
        Assert.Contains("stageReadOnlyObservation?.ToCalibrationEvidence", source);
    }

    private static Task<Phd2PostLockGuidingObservation> Observe(IPhd2Client client, CancellationToken token = default) =>
        Phd2PostLockGuidingObservation.ObserveAsync(client, Exact(), 1, 2, 0.1,
            new Phd2SettleCriteria(2, 0, 1), supervised: true, token);

    private static Phd2ExactLockPositionResult Exact() => new(
        new Phd2Point(99, 100), new Phd2Point(100, 100), new Phd2Point(100, 100),
        1, 0, DateTimeOffset.UtcNow, true, false, false, false, true);

    private static Phd2GuideStep Step(long frame, double offset) => new(frame, offset, 0, 50, 3, offset, 0);

    private static Phd2GuidingFrameResult[] Frames(Phd2PostLockGuidingObservation observation) =>
        Enumerable.Range(1, 3).Select(index => new Phd2GuidingFrameResult(
            $"frame-{index}.fit", new string((char)('a' + index), 64), index + 20,
            observation.AfterEventSequence + index + 10, observation.LockVerifiedUtc,
            DateTimeOffset.UtcNow, false, false, false, false)).ToArray();

    private static (IPhd2Client Client, ReadOnlyClientProxy Proxy) Client()
    {
        var client = DispatchProxy.Create<IPhd2Client, ReadOnlyClientProxy>();
        var proxy = (ReadOnlyClientProxy)(object)client;
        proxy.State = Phd2StateSnapshot.Disconnected with
        {
            IsConnected = true, AppState = Phd2AppState.Guiding, ConnectionEpoch = 1, GuideEpoch = 2,
            LockPosition = new Phd2Point(100, 100), EventSequence = 10, LastGuideStep = Step(10, 4),
            LastSettle = new Phd2SettleResult(false, "original native timeout", 5, 0, DateTimeOffset.UtcNow.AddSeconds(-10)),
        };
        return (client, proxy);
    }

    // Any call other than snapshots/subscription is a test failure, including
    // guide, stop, set_lock_position, capture, or modifications to PHD2 settings.
    public class ReadOnlyClientProxy : DispatchProxy
    {
        public Phd2StateSnapshot State { get; set; } = Phd2StateSnapshot.Disconnected;
        private EventHandler<Phd2StateSnapshot>? changed;
        public int SubscriberCount => changed?.GetInvocationList().Length ?? 0;
        public void Publish(Phd2StateSnapshot state) { State = state; changed?.Invoke(this, state); }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "get_Snapshot": return State;
                case "add_SnapshotChanged": changed += (EventHandler<Phd2StateSnapshot>)args![0]!; return null;
                case "remove_SnapshotChanged": changed -= (EventHandler<Phd2StateSnapshot>)args![0]!; return null;
                default: throw new InvalidOperationException($"Forbidden client operation: {targetMethod?.Name}");
            }
        }
    }
}
