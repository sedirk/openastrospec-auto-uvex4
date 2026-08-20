using UvexAdv.Phd2;

namespace UvexAdv.Phd2.Tests;

public sealed class Phd2LoopSelectionGuideTakeoverTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdleLoopFreshFrameSelectionThenGuideTakeoverProducesCurrentSettle(
        bool loopStoppedBeforeStartGuiding)
    {
        var requestedGuide = new Phd2Point(321.25, 654.5);
        var selectedGuide = new Phd2Point(322.0, 655.0);
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            await ReplyValidCalibrationAsync(session, cancellationToken);

            var appState = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_app_state", appState.GetProperty("method").GetString());
            await session.ReplyResultAsync(appState, "Stopped", cancellationToken);

            var loop = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("loop", loop.GetProperty("method").GetString());
            Assert.False(loop.TryGetProperty("params", out _));
            // Exercise the event-before-RPC-reply race. The waiter must already
            // be registered and the loop must remain running.
            await session.SendEventAsync(new { Event = "LoopingExposures", Frame = 1 }, cancellationToken);
            await session.ReplyResultAsync(loop, 0, cancellationToken);

            var select = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("set_lock_position", select.GetProperty("method").GetString());
            Assert.False(select.GetProperty("params").GetProperty("exact").GetBoolean());
            Assert.Equal(requestedGuide.X, select.GetProperty("params").GetProperty("x").GetDouble());
            Assert.Equal(requestedGuide.Y, select.GetProperty("params").GetProperty("y").GetDouble());
            await session.SendEventAsync(
                new { Event = "StarSelected", X = selectedGuide.X, Y = selectedGuide.Y },
                cancellationToken);
            await session.ReplyResultAsync(select, 0, cancellationToken);

            var lockPosition = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_lock_position", lockPosition.GetProperty("method").GetString());
            await session.ReplyResultAsync(
                lockPosition,
                new[] { selectedGuide.X, selectedGuide.Y },
                cancellationToken);

            var profileRecheck = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_profile", profileRecheck.GetProperty("method").GetString());
            await session.ReplyResultAsync(
                profileRecheck,
                new { id = 2, name = "c11+ccdt67+slit+2210" },
                cancellationToken);

            var guide = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("guide", guide.GetProperty("method").GetString());
            Assert.False(guide.GetProperty("params").GetProperty("recalibrate").GetBoolean());
            await session.ReplyResultAsync(guide, 0, cancellationToken);
            // This is the normal interactive takeover: guide consumes the
            // existing loop. The stop event must not erase this RPC's pending
            // settle epoch in either ordering relative to StartGuiding.
            if (loopStoppedBeforeStartGuiding)
            {
                await session.SendEventAsync(new { Event = "LoopingExposuresStopped" }, cancellationToken);
            }
            await session.SendEventAsync(new { Event = "StartGuiding" }, cancellationToken);
            if (!loopStoppedBeforeStartGuiding)
            {
                await session.SendEventAsync(new { Event = "LoopingExposuresStopped" }, cancellationToken);
            }
            await session.SendEventAsync(new { Event = "SettleBegin" }, cancellationToken);
            await session.SendEventAsync(
                new
                {
                    Event = "GuideStep",
                    Frame = 18,
                    dx = 0.2,
                    dy = -0.1,
                    SNR = 20.0,
                    HFD = 3.0,
                    AvgDist = 0.22,
                    ErrorCode = 0,
                },
                cancellationToken);
            await session.SendEventAsync(
                new { Event = "SettleDone", Status = 0, TotalFrames = 8, DroppedFrames = 0 },
                cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);
        var calibration = await client.ValidateCalibrationAsync(
            ValidCalibrationRequirement(),
            CancellationToken.None);
        Assert.True(calibration.IsValid);

        var looping = await client.StartLoopingAndWaitForFreshFrameAsync(
            new Phd2LoopingStartRequest(TimeSpan.FromSeconds(2)),
            CancellationToken.None);
        var selected = await client.SelectGuideStarAsync(requestedGuide, CancellationToken.None);
        var settled = await client.GuideAndSettleAsync(
            new Phd2SettleCriteria(1.5, 1, 10),
            forceRecalibration: false,
            CancellationToken.None);

        Assert.Equal(Phd2AppState.Stopped, looping.InitialState);
        Assert.Equal(1, looping.Frame);
        Assert.True(looping.LoopCommandSent);
        Assert.False(looping.StopCommandSent);
        Assert.False(looping.ExposureChanged);
        Assert.True(looping.LeavesLoopingForGuideTakeover);
        Assert.False(looping.AutomaticRetryAllowed);
        Assert.Equal(selectedGuide, selected);
        Assert.True(settled.Succeeded);
        Assert.Equal(Phd2AppState.Guiding, client.Snapshot.AppState);
        Assert.True(client.Snapshot.HasCurrentSuccessfulSettle);
        Assert.Equal(client.Snapshot.ConnectionEpoch, client.Snapshot.LastSettleConnectionEpoch);
        Assert.Equal(client.Snapshot.GuideEpoch, client.Snapshot.LastSettleGuideEpoch);

        var methods = server.ReceivedMethods.ToArray();
        Assert.Equal(1, methods.Count(method => method == "loop"));
        Assert.Equal(1, methods.Count(method => method == "guide"));
        Assert.Equal(1, methods.Count(method => method == "set_lock_position"));
        Assert.True(Array.IndexOf(methods, "loop") < Array.IndexOf(methods, "set_lock_position"));
        Assert.True(Array.IndexOf(methods, "set_lock_position") < Array.IndexOf(methods, "guide"));
        Assert.DoesNotContain("stop_capture", methods);
        Assert.DoesNotContain("set_exposure", methods);
    }

    [Fact]
    public async Task MissingFreshLoopFrameTimesOutWithoutImplicitStopOrRetry()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var appState = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(appState, "Selected", cancellationToken);
            var loop = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(loop, 0, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        await Assert.ThrowsAsync<Phd2CommandTimeoutException>(() =>
            client.StartLoopingAndWaitForFreshFrameAsync(
                new Phd2LoopingStartRequest(TimeSpan.FromMilliseconds(100)),
                CancellationToken.None));

        Assert.Equal(new[] { "get_app_state", "loop" }, server.ReceivedMethods.ToArray());
        Assert.DoesNotContain("stop_capture", server.ReceivedMethods);
        Assert.Equal(1, server.ReceivedMethods.Count(method => method == "loop"));
    }

    [Fact]
    public async Task NonIdleStateRejectsBeforeLoop()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var appState = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(appState, "Guiding", cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<Phd2CaptureException>(() =>
            client.StartLoopingAndWaitForFreshFrameAsync(
                new Phd2LoopingStartRequest(TimeSpan.FromSeconds(1)),
                CancellationToken.None));

        Assert.Contains("Stopped or Selected", exception.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "get_app_state" }, server.ReceivedMethods.ToArray());
        Assert.DoesNotContain("loop", server.ReceivedMethods);
        Assert.DoesNotContain("stop_capture", server.ReceivedMethods);
    }

    [Fact]
    public async Task DefiniteGuideRpcFailureClearsOperationAndRejectsLateSettleEvents()
    {
        var lateEventsSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            await ReplyValidCalibrationAsync(session, cancellationToken);
            var profile = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_profile", profile.GetProperty("method").GetString());
            await session.ReplyResultAsync(
                profile,
                new { id = 2, name = "c11+ccdt67+slit+2210" },
                cancellationToken);
            var guide = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("guide", guide.GetProperty("method").GetString());
            await session.ReplyErrorAsync(guide, -32600, "could not start guiding", cancellationToken);
            await session.SendEventAsync(new { Event = "SettleBegin" }, cancellationToken);
            await session.SendEventAsync(
                new { Event = "SettleDone", Status = 0, TotalFrames = 8, DroppedFrames = 0 },
                cancellationToken);
            lateEventsSent.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);
        _ = await client.ValidateCalibrationAsync(ValidCalibrationRequirement(), CancellationToken.None);

        await Assert.ThrowsAsync<Phd2RpcException>(() => client.GuideAndSettleAsync(
            new Phd2SettleCriteria(1.5, 1, 10),
            forceRecalibration: false,
            CancellationToken.None));
        await lateEventsSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (client.Snapshot.EventSequence < 2 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Null(client.Snapshot.PendingSettleOperationId);
        Assert.Null(client.Snapshot.LastSettle);
        Assert.False(client.Snapshot.HasCurrentSuccessfulSettle);
        Assert.Equal(1, server.ReceivedMethods.Count(method => method == "guide"));
    }

    [Fact]
    public async Task SuccessfulSettleDoneWithoutThisOperationsSettleBeginIsRejected()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            await ReplyValidCalibrationAsync(session, cancellationToken);
            var profile = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(
                profile,
                new { id = 2, name = "c11+ccdt67+slit+2210" },
                cancellationToken);
            var guide = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(guide, 0, cancellationToken);
            await session.SendEventAsync(
                new { Event = "SettleDone", Status = 0, TotalFrames = 8, DroppedFrames = 0 },
                cancellationToken);
            var stop = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("stop_capture", stop.GetProperty("method").GetString());
            await session.ReplyResultAsync(stop, 0, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);
        _ = await client.ValidateCalibrationAsync(ValidCalibrationRequirement(), CancellationToken.None);

        await Assert.ThrowsAsync<Phd2CommandTimeoutException>(() => client.GuideAndSettleAsync(
            new Phd2SettleCriteria(1.5, 1, 1),
            forceRecalibration: false,
            CancellationToken.None));

        Assert.Null(client.Snapshot.PendingSettleOperationId);
        Assert.Null(client.Snapshot.LastSettle);
        Assert.False(client.Snapshot.HasCurrentSuccessfulSettle);
    }

    private static async Task ReplyValidCalibrationAsync(
        FakePhd2Session session,
        CancellationToken cancellationToken)
    {
        var profileBefore = await session.ReadRequestAsync(cancellationToken);
        Assert.Equal("get_profile", profileBefore.GetProperty("method").GetString());
        await session.ReplyResultAsync(
            profileBefore,
            new { id = 2, name = "c11+ccdt67+slit+2210" },
            cancellationToken);
        var calibration = await session.ReadRequestAsync(cancellationToken);
        Assert.Equal("get_calibration_data", calibration.GetProperty("method").GetString());
        await session.ReplyResultAsync(
            calibration,
            new
            {
                calibrated = true,
                xAngle = 10.0,
                xRate = 20.0,
                xParity = "+",
                yAngle = 100.0,
                yRate = 25.0,
                yParity = "+",
                declination = 15.0,
            },
            cancellationToken);
        var profileAfter = await session.ReadRequestAsync(cancellationToken);
        Assert.Equal("get_profile", profileAfter.GetProperty("method").GetString());
        await session.ReplyResultAsync(
            profileAfter,
            new { id = 2, name = "c11+ccdt67+slit+2210" },
            cancellationToken);
    }

    private static Phd2CalibrationRequirement ValidCalibrationRequirement() => new(
        2,
        "c11+ccdt67+slit+2210",
        DateTimeOffset.UtcNow,
        TimeSpan.FromMinutes(60),
        MaximumOrthogonalityErrorDegrees: 12,
        MinimumAxisRatePixelsPerSecond: 1,
        MaximumAxisRatePixelsPerSecond: 100,
        RequireKnownAge: true);

    private static Phd2Client CreateClient(FakePhd2Server server) => new(new Phd2ClientOptions
    {
        Host = "127.0.0.1",
        Port = server.Port,
        CommandTimeout = TimeSpan.FromSeconds(2),
        EventTimeoutMargin = TimeSpan.FromSeconds(2),
        FileReadyTimeout = TimeSpan.FromSeconds(2),
    });
}
