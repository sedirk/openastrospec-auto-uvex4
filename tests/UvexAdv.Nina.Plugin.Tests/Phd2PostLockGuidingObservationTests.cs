using System.Reflection;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2PostLockGuidingObservationTests
{
    [Fact]
    public void DirectOpticalWindowDoesNotDiscardAnExtraExposureOrGrantAuthorityEarly()
    {
        var (client, proxy) = Client();
        var native = proxy.State.LastSettle!;
        var result = Phd2PostLockGuidingObservation.BeginFreshResidualObservation(
            client, Exact(), 1, 2, 0.1, supervised: true, default);
        Assert.True(result.YieldedToFreshResiduals);
        Assert.False(result.TrackingWithinTolerance);
        Assert.Equal(10, result.AfterEventSequence);
        Assert.Equal(0, result.ObservedGuideFrames);
        Assert.False(result.HasAcceptedWindow(proxy.State));
        Assert.Throws<Phd2Exception>(() => result.AcceptResiduals(Frames(result)[..2], proxy.State));
        var accepted = result.AcceptResiduals(Frames(result), proxy.State);
        Assert.True(accepted.HasAcceptedWindow(proxy.State));
        Assert.False(accepted.HasAcceptedWindow(proxy.State with { GuideEpoch = 3 }));
        Assert.Same(native, accepted.ToCalibrationEvidence(native, proxy.State).Result);
        Assert.False(native.Succeeded);
    }

    [Fact]
    public void DirectOpticalWindowRejectsOldFramesAndChangedEpoch()
    {
        var (client, proxy) = Client();
        var result = Phd2PostLockGuidingObservation.BeginFreshResidualObservation(
            client, Exact(), 1, 2, 0.1, supervised: true, default);
        var frames = Frames(result);
        frames[0] = frames[0] with { EventSequence = result.AfterEventSequence };
        Assert.Throws<Phd2Exception>(() => result.AcceptResiduals(frames, proxy.State));
        Assert.Throws<Phd2Exception>(() => result.AcceptResiduals(
            Frames(result), proxy.State with { GuideEpoch = 3 }));
        Assert.Throws<Phd2Exception>(() => Phd2PostLockGuidingObservation.BeginFreshResidualObservation(
            client, Exact(), 1, 3, 0.1, supervised: true, default));
        Assert.Throws<Phd2Exception>(() => Phd2PostLockGuidingObservation.BeginFreshResidualObservation(
            client, Exact(), 1, 2, 0.1, supervised: false, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Phd2PostLockGuidingObservation.BeginFreshResidualObservation(
            client, Exact(), 1, 2, 0.1, supervised: true, cancellation.Token));
    }

    [Fact]
    public void LiveFreshWindowPreservesFailedNativeSettleWithoutSendingAnyClientCommands()
    {
        var (client, proxy) = Client();
        var native = proxy.State.LastSettle!;
        var proof = Begin(client);
        Assert.False(proof.HasAcceptedWindow(proxy.State));
        var accepted = proof.AcceptResiduals(Frames(proof), proxy.State);
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

    [Theory]
    [InlineData("disconnect")]
    [InlineData("connection-epoch")]
    [InlineData("guide-epoch")]
    [InlineData("lock")]
    [InlineData("missing-lock")]
    [InlineData("lost")]
    [InlineData("stopped")]
    [InlineData("pause")]
    [InlineData("automation-pause")]
    [InlineData("pending-settle")]
    [InlineData("output-failed")]
    public void InvalidatedContinuityCannotBecomeWarningOnly(string mutation)
    {
        var (client, proxy) = Client();
        var proof = Begin(client);
        proxy.State = mutation switch
        {
            "disconnect" => proxy.State with { IsConnected = false },
            "connection-epoch" => proxy.State with { ConnectionEpoch = 2 },
            "guide-epoch" => proxy.State with { GuideEpoch = 3 },
            "lock" => proxy.State with { LockPosition = new Phd2Point(200, 200) },
            "missing-lock" => proxy.State with { LockPosition = null },
            "lost" => proxy.State with { AppState = Phd2AppState.LostLock },
            "stopped" => proxy.State with { AppState = Phd2AppState.Stopped },
            "pause" => proxy.State with { Phd2Paused = true },
            "automation-pause" => proxy.State with { AutomationPaused = true },
            "output-failed" => proxy.State with
            {
                GuideOutput = new(1, 3, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true, Step(1, 4)),
            },
            _ => proxy.State with { PendingSettleOperationId = 42 },
        };
        Assert.False(proof.IsCurrent(proxy.State));
        Assert.Throws<Phd2Exception>(() => proof.AcceptResiduals(Frames(proof), proxy.State));
        Assert.Throws<Phd2Exception>(() => Begin(client));
    }

    [Theory]
    [InlineData("strict")]
    [InlineData("unverified")]
    [InlineData("profile-mutated")]
    [InlineData("mismatched-readback")]
    [InlineData("nan-position")]
    [InlineData("future")]
    public void UnverifiedOrUnsupervisedShiftCannotStartReadOnlyContinuation(string mutation)
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
        Assert.Throws<Phd2Exception>(() => Phd2PostLockGuidingObservation.BeginFreshResidualObservation(
            client, exact, 1, 2, 0.1, mutation != "strict", default));
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
    public void ReusedOrInterruptedOpticalEvidenceCannotAuthorizeContinuation(string mutation)
    {
        var (client, proxy) = Client();
        var result = Begin(client);
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
    public void BothModesUseMeasuredPostLockWindowWithoutRedundantNativeGuide()
    {
        var source = WorkflowSource.Convergence;
        var start = source.IndexOf("Phd2SettleResult stageSettle;", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("var expectedTargetAfter", start, StringComparison.Ordinal);
        var supervised = source[start..end];
        Assert.Contains("port.BeginFreshResidualObservation", supervised);
        Assert.DoesNotContain("ObserveAsync", supervised);
        Assert.Contains("stageSettle = session.Settle", supervised);
        Assert.DoesNotContain("GuideAndSettleAsync", supervised);
        Assert.Contains("measuredContinuation: true", WorkflowSource.Ports);
        Assert.Contains("stageReadOnlyObservation.AcceptResiduals", source);
        Assert.Contains("stageReadOnlyObservation?.ToCalibrationEvidence", source);
        Assert.Contains("PHD2_FRESH_GUIDE_WINDOW_DEADLINE", WorkflowSource.Read("RealObservationStageRunner.Phd2SlitPlacement"));
    }

    [Fact]
    public void ExplicitMeasuredContinuationWorksWithoutSupervisionButNeverWithTwoFrames()
    {
        var (client, proxy) = Client();
        var proof = Phd2PostLockGuidingObservation.BeginFreshResidualObservation(client,
            Exact(), 1, 2, .1, supervised: false, default, measuredContinuation: true);
        Assert.True(proof.MeasuredContinuation);
        Assert.False(proof.HasAcceptedWindow(proxy.State));
        Assert.Throws<Phd2Exception>(() => proof.AcceptResiduals(Frames(proof)[..2], proxy.State));
        var accepted = proof.AcceptResiduals(Frames(proof), proxy.State);
        Assert.True(accepted.ToCalibrationEvidence(proxy.State.LastSettle!, proxy.State).MeasuredPostLockContinuation);
        Assert.False(accepted.HasAcceptedWindow(proxy.State with { AppState = Phd2AppState.LostLock }));
    }

    private static Phd2PostLockGuidingObservation Begin(IPhd2Client client) =>
        Phd2PostLockGuidingObservation.BeginFreshResidualObservation(
            client, Exact(), 1, 2, 0.1, supervised: true, default);

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

    // Any call other than reading the snapshot is a test failure, including
    // guide, stop, set_lock_position, capture, or modifications to PHD2 settings.
    public class ReadOnlyClientProxy : DispatchProxy
    {
        public Phd2StateSnapshot State { get; set; } = Phd2StateSnapshot.Disconnected;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "get_Snapshot": return State;
                default: throw new InvalidOperationException($"Forbidden client operation: {targetMethod?.Name}");
            }
        }
    }
}
