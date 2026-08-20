using System.Text.Json;
using Xunit;

namespace UvexAdv.Phd2.Tests;

public sealed class Phd2LifecycleSafetyTests
{
    [Theory]
    [InlineData("StartGuiding")]
    [InlineData("GuidingStopped")]
    [InlineData("Paused")]
    [InlineData("Resumed")]
    public async Task GuideLifecycleEventInvalidatesPriorSettleEpoch(string lifecycleEvent)
    {
        var releaseEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
            await session.SendEventAsync(new { Event = "SettleBegin" }, cancellationToken);
            await session.SendEventAsync(
                new { Event = "SettleDone", Status = 0, TotalFrames = 8, DroppedFrames = 0 },
                cancellationToken);

            await releaseEvent.Task.WaitAsync(cancellationToken);
            await session.SendEventAsync(new { Event = lifecycleEvent }, cancellationToken);
            eventSent.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);
        _ = await client.ValidateCalibrationAsync(ValidCalibrationRequirement(), CancellationToken.None);
        _ = await client.GuideAndSettleAsync(
            new Phd2SettleCriteria(1.5, 1, 10),
            forceRecalibration: false,
            CancellationToken.None);

        var settled = client.Snapshot;
        Assert.True(settled.HasCurrentSuccessfulSettle);
        Assert.Equal(settled.ConnectionEpoch, settled.LastSettleConnectionEpoch);
        Assert.Equal(settled.GuideEpoch, settled.LastSettleGuideEpoch);

        releaseEvent.TrySetResult();
        await eventSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => client.Snapshot.EventSequence > settled.EventSequence);

        var invalidated = client.Snapshot;
        Assert.True(invalidated.GuideEpoch > settled.GuideEpoch);
        Assert.Null(invalidated.LastSettle);
        Assert.Null(invalidated.LastSettleConnectionEpoch);
        Assert.Null(invalidated.LastSettleGuideEpoch);
        Assert.False(invalidated.HasCurrentSuccessfulSettle);
    }

    [Fact]
    public async Task LocalPauseResumeAndDisconnectCannotReusePriorSettle()
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
            await session.SendEventAsync(new { Event = "SettleBegin" }, cancellationToken);
            await session.SendEventAsync(
                new { Event = "SettleDone", Status = 0, TotalFrames = 8, DroppedFrames = 0 },
                cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);
        _ = await client.ValidateCalibrationAsync(ValidCalibrationRequirement(), CancellationToken.None);
        _ = await client.GuideAndSettleAsync(
            new Phd2SettleCriteria(1.5, 1, 10),
            forceRecalibration: false,
            CancellationToken.None);
        var settledEpoch = client.Snapshot.GuideEpoch;
        Assert.True(client.Snapshot.HasCurrentSuccessfulSettle);

        client.PauseAutomation();
        Assert.Null(client.Snapshot.LastSettle);
        Assert.True(client.Snapshot.GuideEpoch > settledEpoch);
        var pauseEpoch = client.Snapshot.GuideEpoch;

        client.ResumeAutomation();
        Assert.Null(client.Snapshot.LastSettle);
        Assert.True(client.Snapshot.GuideEpoch > pauseEpoch);

        var connectionEpoch = client.Snapshot.ConnectionEpoch;
        await client.DisconnectAsync(CancellationToken.None);
        Assert.False(client.IsConnected);
        Assert.Equal(connectionEpoch, client.Snapshot.ConnectionEpoch);
        Assert.Null(client.Snapshot.LastSettle);
        Assert.False(client.Snapshot.HasCurrentSuccessfulSettle);
    }

    [Fact]
    public async Task PauseAndStopCaptureConfirmsSelectedIdleState()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var before = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_app_state", before.GetProperty("method").GetString());
            await session.ReplyResultAsync(before, "Guiding", cancellationToken);
            var stop = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("stop_capture", stop.GetProperty("method").GetString());
            await session.ReplyResultAsync(stop, 0, cancellationToken);
            var after = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_app_state", after.GetProperty("method").GetString());
            await session.ReplyResultAsync(after, "Selected", cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var result = await client.PauseAutomationAndStopCaptureAsync(CancellationToken.None);

        Assert.True(client.IsAutomationPaused);
        Assert.True(result.StopCommandSent);
        Assert.True(result.ConfirmedIdle);
        Assert.Equal(Phd2AppState.Guiding, result.InitialState);
        Assert.Equal(Phd2AppState.Selected, result.FinalState);
        Assert.Null(client.Snapshot.LastSettle);
    }

    [Fact]
    public async Task ReconnectCreatesNewConnectionAndGuideEpoch()
    {
        var acceptedConnections = 0;
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            Interlocked.Increment(ref acceptedConnections);
            try
            {
                while (true)
                {
                    _ = await session.ReadRequestAsync(cancellationToken);
                }
            }
            catch (EndOfStreamException)
            {
            }
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);
        var firstConnectionEpoch = client.Snapshot.ConnectionEpoch;
        var firstGuideEpoch = client.Snapshot.GuideEpoch;

        await client.DisconnectAsync(CancellationToken.None);
        await client.ConnectAsync(CancellationToken.None);

        Assert.True(client.Snapshot.ConnectionEpoch > firstConnectionEpoch);
        Assert.True(client.Snapshot.GuideEpoch > firstGuideEpoch);
        Assert.Null(client.Snapshot.LastSettle);
        Assert.False(client.Snapshot.HasCurrentSuccessfulSettle);
        await WaitUntilAsync(() => Volatile.Read(ref acceptedConnections) >= 2);
    }

    [Fact]
    public async Task StopConfirmationTimeoutNeverClaimsIdle()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                JsonElement request;
                try
                {
                    request = await session.ReadRequestAsync(cancellationToken);
                }
                catch (EndOfStreamException)
                {
                    break;
                }
                if (request.GetProperty("method").GetString() == "get_app_state")
                {
                    await session.ReplyResultAsync(request, "Guiding", cancellationToken);
                }
                else
                {
                    await session.ReplyResultAsync(request, 0, cancellationToken);
                }
            }
        });
        await using var client = new Phd2Client(new Phd2ClientOptions
        {
            Host = "127.0.0.1",
            Port = server.Port,
            CommandTimeout = TimeSpan.FromSeconds(1),
            StopConfirmationTimeout = TimeSpan.FromMilliseconds(150),
            StatePollInterval = TimeSpan.FromMilliseconds(20),
        });
        await client.ConnectAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<Phd2CommandTimeoutException>(
            () => client.StopCaptureAndConfirmAsync(CancellationToken.None));

        Assert.Contains("idle confirmation", error.Operation, StringComparison.Ordinal);
        Assert.Null(client.Snapshot.LastSettle);
        Assert.False(client.Snapshot.HasCurrentSuccessfulSettle);
    }

    private static Phd2Client CreateClient(FakePhd2Server server) => new(new Phd2ClientOptions
    {
        Host = "127.0.0.1",
        Port = server.Port,
        CommandTimeout = TimeSpan.FromSeconds(2),
        EventTimeoutMargin = TimeSpan.FromSeconds(2),
        FileReadyTimeout = TimeSpan.FromSeconds(2),
    });

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
                declination = 30.0,
            },
            cancellationToken);
        var profileAfter = await session.ReadRequestAsync(cancellationToken);
        await session.ReplyResultAsync(
            profileAfter,
            new { id = 2, name = "c11+ccdt67+slit+2210" },
            cancellationToken);
    }

    private static Phd2CalibrationRequirement ValidCalibrationRequirement() => new(
        2,
        "c11+ccdt67+slit+2210",
        DateTimeOffset.UtcNow,
        TimeSpan.FromDays(30),
        MaximumOrthogonalityErrorDegrees: 10,
        MinimumAxisRatePixelsPerSecond: 0.01,
        MaximumAxisRatePixelsPerSecond: 100);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!predicate() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.True(predicate());
    }
}
