using UvexAdv.Phd2;

namespace UvexAdv.Phd2.Tests;

public sealed class Phd2GuideOutputLifecycleTests
{
    [Theory]
    [InlineData("event", "Paused", false)]
    [InlineData("event", "Stopped", false)]
    [InlineData("event", "LostLock", false)]
    [InlineData("rpc", "Paused", false)]
    [InlineData("rpc", "Stopped", false)]
    [InlineData("rpc", "LostLock", false)]
    [InlineData("rpc", "Selected", false)]
    [InlineData("rpc", "Looping", false)]
    [InlineData("event", "Paused", true)]
    [InlineData("rpc", "Stopped", true)]
    public async Task StateEvidenceBreaksOnlyUnconfirmedOutputSeries(string source, string state, bool latched)
    {
        await using var server = new FakePhd2Server(async (session, token) =>
        {
            await BindEquipmentAsync(session, token);
            var initial = await session.ReadRequestAsync(token);
            await SeedOutputSeriesAsync(session, latched, token);
            await session.ReplyResultAsync(initial, "Guiding", token);

            var boundary = await session.ReadRequestAsync(token);
            if (source == "event")
                await session.SendEventAsync(new { Event = "AppState", State = state }, token);
            await session.ReplyResultAsync(boundary, source == "event" ? "Guiding" : state, token);

            var next = await session.ReadRequestAsync(token);
            await session.SendEventAsync(new { Event = "AppState", State = "Guiding" }, token);
            await SendMissingOutputAsync(session, 4, token);
            await session.ReplyResultAsync(next, "Guiding", token);
            await Task.Delay(Timeout.Infinite, token);
        });
        await using var client = new Phd2Client(new Phd2ClientOptions { Host = "127.0.0.1", Port = server.Port });
        await client.ConnectAsync(CancellationToken.None);
        await client.GetCurrentEquipmentAsync(CancellationToken.None);
        await client.GetAppStateAsync(CancellationToken.None);
        var before = Assert.IsType<Phd2GuideOutputStatus>(client.Snapshot.GuideOutput);
        Assert.Equal(latched, before.Failed);
        Assert.Equal(latched ? 3 : 2, before.ConsecutiveMissingOutputs);

        await client.GetAppStateAsync(CancellationToken.None);
        if (latched) Assert.Equal(before, client.Snapshot.GuideOutput);
        else Assert.Null(client.Snapshot.GuideOutput);

        await client.GetAppStateAsync(CancellationToken.None);
        var after = Assert.IsType<Phd2GuideOutputStatus>(client.Snapshot.GuideOutput);
        Assert.Equal(latched, after.Failed);
        if (latched) Assert.Equal(before, after);
        else Assert.Equal(1, after.ConsecutiveMissingOutputs);
        Assert.False(client.Snapshot.HasCurrentSuccessfulSettle);
        Assert.All(server.ReceivedMethods, method => Assert.Contains(method, new[] { "get_app_state", "get_current_equipment" }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportLossAndReconnectBreakOnlyUnconfirmedOutputSeries(bool latched)
    {
        var connections = 0;
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakePhd2Server(async (session, token) =>
        {
            await BindEquipmentAsync(session, token);
            if (++connections == 1)
            {
                var initial = await session.ReadRequestAsync(token);
                await SeedOutputSeriesAsync(session, latched, token);
                await session.ReplyResultAsync(initial, "Guiding", token);
                await session.ReadRequestAsync(token);
                session.CloseConnection(); // Lose TCP without a GuidingStopped event.
                return;
            }
            await session.SendEventAsync(new { Event = "AppState", State = "Guiding" }, token);
            var next = await session.ReadRequestAsync(token);
            await SendMissingOutputAsync(session, 4, token);
            await session.ReplyResultAsync(next, "Guiding", token);
            await Task.Delay(Timeout.Infinite, token);
        });
        await using var client = new Phd2Client(new Phd2ClientOptions { Host = "127.0.0.1", Port = server.Port });
        client.SnapshotChanged += (_, snapshot) =>
        {
            if (!snapshot.IsConnected) disconnected.TrySetResult();
        };
        await client.ConnectAsync(CancellationToken.None);
        await client.GetCurrentEquipmentAsync(CancellationToken.None);
        await client.GetAppStateAsync(CancellationToken.None);
        var before = Assert.IsType<Phd2GuideOutputStatus>(client.Snapshot.GuideOutput);
        Assert.Equal(latched, before.Failed);

        await Assert.ThrowsAsync<Phd2DisconnectedException>(() => client.GetAppStateAsync(CancellationToken.None));
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (latched) Assert.Equal(before, client.Snapshot.GuideOutput);
        else Assert.Null(client.Snapshot.GuideOutput);

        await client.ConnectAsync(CancellationToken.None);
        await client.GetCurrentEquipmentAsync(CancellationToken.None);
        await client.GetAppStateAsync(CancellationToken.None);
        var after = Assert.IsType<Phd2GuideOutputStatus>(client.Snapshot.GuideOutput);
        Assert.Equal(latched, after.Failed);
        if (latched) Assert.Equal(before, after);
        else Assert.Equal(1, after.ConsecutiveMissingOutputs);
        Assert.False(client.Snapshot.HasCurrentSuccessfulSettle);
        Assert.All(server.ReceivedMethods, method => Assert.Contains(method, new[] { "get_app_state", "get_current_equipment" }));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EquipmentReadbackChangeBreaksOnlyUnconfirmedSeries(bool latched, bool changed)
    {
        var name = changed ? "Another mount" : "On-Step (ASCOM)";
        await using var server = new FakePhd2Server(async (session, token) =>
        {
            await BindEquipmentAsync(session, token);
            var initial = await session.ReadRequestAsync(token);
            await SeedOutputSeriesAsync(session, latched, token);
            await session.ReplyResultAsync(initial, "Guiding", token);
            var equipment = await session.ReadRequestAsync(token);
            Assert.Equal("get_current_equipment", equipment.GetProperty("method").GetString());
            await session.ReplyResultAsync(equipment, new { mount = new { name, connected = true } }, token);
            var next = await session.ReadRequestAsync(token);
            await session.SendEventAsync(new
            {
                Event = "GuideStep", Frame = 4, Mount = name, ErrorCode = 1,
                RADistanceGuide = 1.5, DECDistanceGuide = 2.0,
            }, token);
            await session.ReplyResultAsync(next, "Guiding", token);
            await Task.Delay(Timeout.Infinite, token);
        });
        await using var client = new Phd2Client(new Phd2ClientOptions { Host = "127.0.0.1", Port = server.Port });
        await client.ConnectAsync(CancellationToken.None);
        await client.GetCurrentEquipmentAsync(CancellationToken.None);
        await client.GetAppStateAsync(CancellationToken.None);
        var before = Assert.IsType<Phd2GuideOutputStatus>(client.Snapshot.GuideOutput);
        await client.GetCurrentEquipmentAsync(CancellationToken.None);
        if (changed && !latched) Assert.Null(client.Snapshot.GuideOutput);
        else Assert.Equal(before, client.Snapshot.GuideOutput);
        await client.GetAppStateAsync(CancellationToken.None);
        var after = Assert.IsType<Phd2GuideOutputStatus>(client.Snapshot.GuideOutput);
        if (latched) Assert.Equal(before, after);
        else Assert.Equal(changed ? 1 : 3, after.ConsecutiveMissingOutputs);
        Assert.Equal(latched || !changed, after.Failed);
    }

    private static async Task BindEquipmentAsync(FakePhd2Session session, CancellationToken token)
    {
        var request = await session.ReadRequestAsync(token);
        Assert.Equal("get_current_equipment", request.GetProperty("method").GetString());
        await session.ReplyResultAsync(request, new
        {
            mount = new { name = "On-Step (ASCOM)", connected = true },
        }, token);
    }

    private static async Task SeedOutputSeriesAsync(FakePhd2Session session, bool latched, CancellationToken token)
    {
        await session.SendEventAsync(new { Event = "StartGuiding" }, token);
        await SendMissingOutputAsync(session, 1, token);
        await Task.Delay(2100, token); // Cross the production minimum window, not a test-only clock.
        await SendMissingOutputAsync(session, 2, token);
        if (latched)
        {
            await Task.Delay(20, token);
            await SendMissingOutputAsync(session, 3, token);
        }
    }

    private static Task SendMissingOutputAsync(FakePhd2Session session, long frame, CancellationToken token) =>
        session.SendEventAsync(new
        {
            Event = "GuideStep", Frame = frame, Mount = "On-Step (ASCOM)", ErrorCode = 0,
            RADistanceGuide = 1.5, DECDistanceGuide = 2.0,
        }, token);
}
