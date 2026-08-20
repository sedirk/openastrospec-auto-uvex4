using UvexAdv.Phd2;

namespace UvexAdv.Phd2.Tests;

public sealed class Phd2LockEpochInvalidationTests
{
    [Theory]
    [InlineData("LockPositionSet")]
    [InlineData("LockPositionLost")]
    [InlineData("GuidingDithered")]
    [InlineData("LockPositionShiftLimitReached")]
    [InlineData("CalibrationDataFlipped")]
    public async Task LockOrCalibrationChangeInvalidatesPreviouslySuccessfulSettle(string eventName)
    {
        var releaseInvalidation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
            await session.ReplyResultAsync(guide, 0, cancellationToken);
            await session.SendEventAsync(new { Event = "SettleBegin" }, cancellationToken);
            await session.SendEventAsync(new
            {
                Event = "SettleDone",
                Status = 0,
                Error = (string?)null,
                TotalFrames = 4,
                DroppedFrames = 0,
            }, cancellationToken);
            await releaseInvalidation.Task.WaitAsync(cancellationToken);
            await session.SendEventAsync(new
            {
                Event = eventName,
                X = 101.0,
                Y = 201.0,
            }, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);
        _ = await client.ValidateCalibrationAsync(ValidCalibrationRequirement(), CancellationToken.None);
        _ = await client.GuideAndSettleAsync(
            new Phd2SettleCriteria(1.5, 1, 10),
            forceRecalibration: false,
            CancellationToken.None);
        Assert.True(client.Snapshot.HasCurrentSuccessfulSettle);
        var settledGuideEpoch = client.Snapshot.GuideEpoch;

        releaseInvalidation.TrySetResult(true);
        await WaitUntilAsync(() => !client.Snapshot.HasCurrentSuccessfulSettle);

        Assert.True(client.IsConnected);
        Assert.False(client.Snapshot.HasCurrentSuccessfulSettle);
        Assert.Null(client.Snapshot.LastSettle);
        Assert.True(client.Snapshot.GuideEpoch > settledGuideEpoch);
        if (eventName == "LockPositionLost")
        {
            Assert.Null(client.Snapshot.LockPosition);
            Assert.Equal(Phd2AppState.LostLock, client.Snapshot.AppState);
        }
    }

    [Fact]
    public async Task UnsolicitedStartGuidingAndSettleEventsNeverCreateAnAttestation()
    {
        var eventsSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            await session.SendEventAsync(new { Event = "StartGuiding" }, cancellationToken);
            await session.SendEventAsync(new { Event = "SettleBegin" }, cancellationToken);
            await session.SendEventAsync(
                new { Event = "SettleDone", Status = 0, TotalFrames = 4, DroppedFrames = 0 },
                cancellationToken);
            eventsSent.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);
        await eventsSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => client.Snapshot.EventSequence >= 3);

        Assert.Equal(Phd2AppState.Guiding, client.Snapshot.AppState);
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

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Fake PHD2 state did not reach the expected condition.");
            }

            await Task.Delay(10);
        }
    }

    private static Phd2Client CreateClient(FakePhd2Server server) => new(new Phd2ClientOptions
    {
        Host = "127.0.0.1",
        Port = server.Port,
        CommandTimeout = TimeSpan.FromSeconds(2),
        EventTimeoutMargin = TimeSpan.FromSeconds(2),
        FileReadyTimeout = TimeSpan.FromSeconds(2),
    });
}
