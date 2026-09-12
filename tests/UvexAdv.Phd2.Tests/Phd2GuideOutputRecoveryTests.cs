using System.Collections.Concurrent;
using UvexAdv.Phd2;

namespace UvexAdv.Phd2.Tests;

public sealed class Phd2GuideOutputRecoveryTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("output-disabled")]
    [InlineData("extra-device")]
    [InlineData("identity-changed")]
    [InlineData("disconnect-unconfirmed")]
    [InlineData("active-capture")]
    public async Task NativeFaultAndBoundedReconnectUseSameProductionClient(string scenario)
    {
        var connectionCommands = new ConcurrentQueue<bool>();
        var faultSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakePhd2Server(async (session, token) =>
        {
            await session.SendEventAsync(new { Event = "StartGuiding" }, token);
            for (var frame = 4; frame <= 6; frame++)
            {
                await session.SendEventAsync(new { Event = "GuideStep", Frame = frame, Mount = "Mount",
                    dx = -1.548, dy = -1.798, RADistanceGuide = -1.786, DECDistanceGuide = 2.326,
                    SNR = 143.67, HFD = 5.36, AvgDist = 1.39, ErrorCode = 1 }, token);
                if (frame < 6) await Task.Delay(1050, token);
            }
            var connected = true;
            var state = "Guiding";
            while (!token.IsCancellationRequested)
            {
                System.Text.Json.JsonElement request;
                try { request = await session.ReadRequestAsync(token); }
                catch (EndOfStreamException) { break; }
                var method = request.GetProperty("method").GetString();
                object? result = method switch
                {
                    "get_app_state" => state,
                    "get_profile" => new { id = 2, name = "guide-only" },
                    "get_current_equipment" => new
                    {
                        camera = new { name = scenario == "identity-changed" ? "unapproved" : "bound-camera", connected },
                        mount = new { name = "bound-mount", connected },
                        aux_mount = scenario == "extra-device" ? new { name = "unapproved-aux", connected = true } : null,
                    },
                    "get_guide_output_enabled" => scenario != "output-disabled",
                    _ => 0,
                };
                if (method == "stop_capture")
                {
                    state = "Stopped";
                    await session.SendEventAsync(new { Event = "GuidingStopped" }, token);
                }
                if (method == "set_connected")
                {
                    var value = request.GetProperty("params").GetProperty("connected").GetBoolean();
                    connectionCommands.Enqueue(value);
                    connected = scenario == "disconnect-unconfirmed" || value;
                }
                await session.ReplyResultAsync(request, result, token);
            }
        });
        await using var client = new Phd2Client(new Phd2ClientOptions { Host = "127.0.0.1", Port = server.Port });
        client.SnapshotChanged += (_, snapshot) => { if (snapshot.GuideOutput?.Failed == true) faultSeen.TrySetResult(); };
        await client.ConnectAsync(CancellationToken.None);
        await faultSeen.Task.WaitAsync(TimeSpan.FromSeconds(8));
        var fault = Assert.IsType<Phd2GuideOutputStatus>(client.Snapshot.GuideOutput);
        Assert.True(fault.Failed);
        Assert.Equal(-1.786, fault.LastStep.RaGuideDistancePixels);
        Assert.Null(fault.LastStep.RaDurationMilliseconds); // Native protocol omits zero, not missing parse.
        await Assert.ThrowsAsync<Phd2GuideOutputException>(() => client.SetExactLockPositionAsync(
            new(new(10, 10), new(11, 11), 1, 2, 1), CancellationToken.None));
        Assert.DoesNotContain("set_lock_position", server.ReceivedMethods);

        if (scenario != "active-capture")
            Assert.True((await client.StopCaptureAndConfirmAsync(CancellationToken.None)).ConfirmedIdle);
        Assert.True(client.Snapshot.GuideOutput!.Failed); // Stopping does not clear the fault.
        var epoch = client.Snapshot.ConnectionEpoch;
        var reconnect = () => client.ReconnectEquipmentAfterOutputFailureAsync(
            new(2, "guide-only", "bound-camera", "bound-mount"), CancellationToken.None);
        if (scenario == "success")
        {
            var receipt = await reconnect();
            Assert.Equal(new[] { false, true }, connectionCommands.ToArray());
            Assert.True(receipt.ConfirmedStopped);
            Assert.False(receipt.CaptureStarted);
            Assert.False(receipt.MotionCommandIssued);
            Assert.Equal(epoch + 1, receipt.ConnectionEpoch);
            Assert.Null(client.Snapshot.GuideOutput);
            Assert.False(client.Snapshot.HasCurrentSuccessfulSettle);
        }
        else
        {
            await Assert.ThrowsAnyAsync<Phd2Exception>(reconnect);
            Assert.True(client.Snapshot.GuideOutput!.Failed);
            Assert.Equal(scenario == "disconnect-unconfirmed" ? new[] { false } : [], connectionCommands.ToArray());
        }
        Assert.DoesNotContain("guide", server.ReceivedMethods);
        Assert.DoesNotContain("guide_pulse", server.ReceivedMethods);
        Assert.DoesNotContain("set_profile", server.ReceivedMethods);
        Assert.DoesNotContain("capture_single_frame", server.ReceivedMethods);
        await client.DisconnectAsync(CancellationToken.None);
    }
}
