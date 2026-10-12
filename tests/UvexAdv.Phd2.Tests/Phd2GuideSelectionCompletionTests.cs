namespace UvexAdv.Phd2.Tests;

public sealed class Phd2GuideSelectionCompletionTests
{
    // HD 21641, 2026-10-12 03:44:13: the RPC's (812.91,402.44)
    // becomes (811.03,402.05) on PHD2's first SELECTING -> SELECTED frame.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task LoopingSelectionReturnsCompletedNativeCentroidNotProvisionalRpcLock(bool beforeAck, bool unchangedLock)
    {
        await using var server = new FakePhd2Server(async (session, token) =>
        {
            if (unchangedLock)
            {
                await session.SendEventAsync(new { Event = "LockPositionSet", X = 811.03, Y = 402.05 }, token);
                await session.SendEventAsync(new { Event = "LoopingExposures", Frame = 1 }, token);
            }
            await Reply(session, "get_app_state", "Looping", token);
            var request = await session.ReadRequestAsync(token);
            Assert.Equal("set_lock_position", request.GetProperty("method").GetString());
            Assert.False(request.GetProperty("params").GetProperty("exact").GetBoolean());
            if (!unchangedLock)
                await session.SendEventAsync(new { Event = "LockPositionSet", X = 812.91, Y = 402.44 }, token);
            if (!beforeAck)
            {
                await session.ReplyResultAsync(request, 0, token);
                await Task.Delay(100, token);
            }
            if (unchangedLock)
                await session.SendEventAsync(new { Event = "StarSelected", X = 811.03, Y = 402.05 }, token);
            else await CompleteSelection(session, token);
            if (beforeAck) await session.ReplyResultAsync(request, 0, token);
            await Reply(session, "get_app_state", "Selected", token);
            await Reply(session, "get_lock_position", new[] { 811.03, 402.05 }, token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await using var client = Client(server);
        await client.ConnectAsync(CancellationToken.None);
        if (unchangedLock)
        {
            for (var i = 0; i < 100 && client.Snapshot.AppState != Phd2AppState.Looping; i++)
                await Task.Delay(10);
            Assert.Equal(Phd2AppState.Looping, client.Snapshot.AppState);
        }
        var selected = await client.SelectGuideStarAsync(new(815.42636, 413.45571), CancellationToken.None);
        Assert.Equal(new Phd2Point(811.03, 402.05), selected);
        Assert.Equal(selected, client.Snapshot.SelectedStar);
        Assert.Equal(selected, client.Snapshot.LockPosition);
        Assert.Equal(new[] { "get_app_state", "set_lock_position", "get_app_state", "get_lock_position" }, server.ReceivedMethods);
    }

    [Theory]
    [InlineData("StartGuiding")]
    [InlineData("StartCalibration")]
    [InlineData("Paused")]
    [InlineData("GuidingDithered")]
    [InlineData("ConfigurationChange")]
    [InlineData("LoopingExposuresStopped")]
    [InlineData("reselect")]
    [InlineData("far-lock")]
    [InlineData("unpaired")]
    [InlineData("readback-changed")]
    [InlineData("disconnect")]
    public async Task CompetingContextCannotBecomeSelectionSuccessOrQualitySkip(string change)
    {
        await using var server = new FakePhd2Server(async (session, token) =>
        {
            await Reply(session, "get_app_state", "Looping", token);
            await Reply(session, "set_lock_position", 0, token);
            if (change == "disconnect") { session.CloseConnection(); return; }
            if (change == "reselect")
            {
                await CompleteSelection(session, token);
                await session.SendEventAsync(new { Event = "StarSelected", X = 811.04, Y = 402.05 }, token);
            }
            else if (change == "far-lock")
                await session.SendEventAsync(new { Event = "LockPositionSet", X = 1000, Y = 700 }, token);
            else if (change == "unpaired")
                await session.SendEventAsync(new { Event = "StarSelected", X = 811.03, Y = 402.05 }, token);
            else if (change == "readback-changed")
            {
                await CompleteSelection(session, token);
                await Reply(session, "get_app_state", "Selected", token);
                await Reply(session, "get_lock_position", new[] { 812.03, 402.05 }, token);
            }
            else await session.SendEventAsync(new { Event = change }, token);
            // A completion could have won the race before an external event;
            // service its read-only confirmation, never accept a guide command.
            try
            {
                while (true)
                {
                    var request = await session.ReadRequestAsync(token);
                    var method = request.GetProperty("method").GetString();
                    Assert.Contains(method, new[] { "get_app_state", "get_lock_position" });
                    await session.ReplyResultAsync(request, method == "get_app_state" ? "Selected" : new[] { 811.03, 402.05 }, token);
                }
            }
            catch (EndOfStreamException) { } // client disposed after expected rejection
        });
        await using var client = Client(server);
        await client.ConnectAsync(CancellationToken.None);
        var error = await Record.ExceptionAsync(() => client.SelectGuideStarAsync(new(815.42636, 413.45571), CancellationToken.None));
        if (change == "disconnect") Assert.IsType<Phd2DisconnectedException>(error);
        else Assert.IsType<Phd2CaptureException>(error);
        Assert.DoesNotContain("guide", server.ReceivedMethods);
    }

    [Theory]
    [InlineData("StarLost")]
    [InlineData("LockPositionLost")]
    [InlineData("timeout")]
    [InlineData("cancel")]
    public async Task FailedPreviewIsBoundedAndNeverStartsGuiding(string failure)
    {
        using var cancel = new CancellationTokenSource();
        await using var server = new FakePhd2Server(async (session, token) =>
        {
            await Reply(session, "get_app_state", "Looping", token);
            await Reply(session, "set_lock_position", 0, token);
            if (failure == "cancel") cancel.Cancel();
            else if (failure == "timeout") await Reply(session, "get_app_state", "Looping", token);
            else await session.SendEventAsync(new { Event = failure }, token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await using var client = Client(server);
        await client.ConnectAsync(CancellationToken.None);
        if (failure == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SelectGuideStarAsync(new(815, 413), cancel.Token));
        else
            await Assert.ThrowsAsync<Phd2GuideSelectionUnavailableException>(() => client.SelectGuideStarAsync(new(815, 413), cancel.Token));
        Assert.DoesNotContain("guide", server.ReceivedMethods);
        Assert.DoesNotContain("stop_capture", server.ReceivedMethods); // caller owns checked cleanup
    }

    private static Phd2Client Client(FakePhd2Server server) => new(new Phd2ClientOptions
    {
        Port = server.Port, CommandTimeout = TimeSpan.FromSeconds(1),
        MinimumLoopingFrameEventTimeout = TimeSpan.FromMilliseconds(250),
    });

    private static async Task CompleteSelection(FakePhd2Session session, CancellationToken token)
    {
        await session.SendEventAsync(new { Event = "LoopingExposures", Frame = 2, SNR = 44.7 }, token);
        await session.SendEventAsync(new { Event = "LockPositionSet", X = 811.03, Y = 402.05 }, token);
        await session.SendEventAsync(new { Event = "StarSelected", X = 811.03, Y = 402.05 }, token);
    }

    private static async Task Reply(FakePhd2Session session, string method, object result, CancellationToken token)
    {
        var request = await session.ReadRequestAsync(token);
        Assert.Equal(method, request.GetProperty("method").GetString());
        await session.ReplyResultAsync(request, result, token);
    }
}
