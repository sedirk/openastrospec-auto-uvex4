using UvexAdv.Phd2;

namespace UvexAdv.Phd2.Tests;

public sealed class Phd2SelectedStarSnrTests
{
    private static readonly Phd2Point Star = new(1125.29, 503.75);

    private static Phd2Client Client(FakePhd2Server server) => new(new()
    {
        Port = server.Port, CommandTimeout = TimeSpan.FromSeconds(3),
    });

    [Theory]
    [InlineData("samples")]
    [InlineData("missing")]
    [InlineData("timeout")]
    [InlineData("reselected")]
    [InlineData("guiding")]
    [InlineData("exposure-changed")]
    [InlineData("selected-loop")]
    [InlineData("selected-reselected-same")]
    [InlineData("star-lost")]
    [InlineData("preview-lock-lost")]
    [InlineData("stopped")]
    [InlineData("calibration")]
    [InlineData("lock-changed")]
    [InlineData("selected-last-sample-lost")]
    [InlineData("selected-final-read-lost")]
    [InlineData("selected-final-read-lost-reselected")]
    [InlineData("selected-final-read-lost-stopped")]
    [InlineData("selected-final-read-lost-calibration")]
    [InlineData("selected-final-read-lost-paused")]
    public async Task SnrObservationIsReadOnlyFreshBoundedAndSelectionBound(string mode)
    {
        await using var server = new FakePhd2Server(async (session, token) =>
        {
            // Seed a native selection as the production caller does, not by
            // setting private state in the client.
            var request = await session.ReadRequestAsync(token);
            Assert.Equal("find_star", request.GetProperty("method").GetString());
            await session.ReplyResultAsync(request, new[] { Star.X, Star.Y }, token);
            request = await session.ReadRequestAsync(token);
            Assert.Equal("get_app_state", request.GetProperty("method").GetString());
            await session.ReplyResultAsync(request, mode.StartsWith("selected-") ? "Selected" : "Looping", token);
            request = await session.ReadRequestAsync(token);
            Assert.Equal("get_exposure", request.GetProperty("method").GetString());
            await session.ReplyResultAsync(request, 2000, token);
            if (mode == "reselected")
                await session.SendEventAsync(new { Event = "StarSelected", X = 900, Y = 400 }, token);
            else if (mode == "selected-reselected-same")
                await session.SendEventAsync(new { Event = "StarSelected", X = Star.X, Y = Star.Y }, token);
            else if (mode == "star-lost")
                await session.SendEventAsync(new { Event = "StarLost", Frame = 2, SNR = 2.9, ErrorCode = 3 }, token);
            else if (mode == "preview-lock-lost")
            {
                await session.SendEventAsync(new { Event = "LoopingExposures", Frame = 2, SNR = 0, ErrorCode = 2 }, token);
                await session.SendEventAsync(new { Event = "LockPositionLost" }, token);
                await session.SendEventAsync(new { Event = "StarLost", Frame = 2, SNR = 0, ErrorCode = 2 }, token);
            }
            else if (mode == "stopped")
                await session.SendEventAsync(new { Event = "LoopingExposuresStopped" }, token);
            else if (mode == "calibration")
                await session.SendEventAsync(new { Event = "StartCalibration", Mount = "Mount" }, token);
            else if (mode == "lock-changed")
                await session.SendEventAsync(new { Event = "LockPositionSet", X = 999, Y = 555 }, token);
            else if (mode == "guiding")
                await session.SendEventAsync(new { Event = "StartGuiding" }, token);
            else if (mode != "timeout")
            {
                await session.SendEventAsync(new { Event = "LoopingExposures", Frame = 1, SNR = 999 }, token);
                await session.SendEventAsync(new { Event = "LoopingExposures", Frame = 1, SNR = 999 }, token);
                for (int frame = 2; frame <= 4; frame++)
                {
                    if (mode == "selected-last-sample-lost" && frame == 4)
                    {
                        await session.SendEventAsync(new { Event = "LoopingExposures", Frame = frame, ErrorCode = 2 }, token);
                        await session.SendEventAsync(new { Event = "LockPositionLost" }, token);
                        await session.SendEventAsync(new { Event = "StarLost", Frame = frame, SNR = 2.9, ErrorCode = 2 }, token);
                    }
                    else if (mode == "missing")
                        await session.SendEventAsync(new { Event = "LoopingExposures", Frame = frame }, token);
                    else await session.SendEventAsync(new { Event = "LoopingExposures", Frame = frame, SNR = 20 + frame, ErrorCode = 0 }, token);
                }
            }
            if (mode is not ("reselected" or "guiding" or "selected-reselected-same" or "stopped" or "calibration" or "lock-changed"))
            {
                request = await session.ReadRequestAsync(token);
                Assert.Equal("get_exposure", request.GetProperty("method").GetString());
                if (mode.StartsWith("selected-final-read-lost"))
                {
                    // Reproduce the completed-window/final-RPC race, including
                    // the native loss notification order from HD 24479.
                    await session.SendEventAsync(new { Event = "LoopingExposures", Frame = 5, ErrorCode = 2 }, token);
                    await session.SendEventAsync(new { Event = "LockPositionLost" }, token);
                    await session.SendEventAsync(new { Event = "StarLost", Frame = 5, SNR = 2.9, ErrorCode = 2 }, token);
                    if (mode.EndsWith("reselected"))
                        await session.SendEventAsync(new { Event = "StarSelected", X = Star.X, Y = Star.Y }, token);
                    if (mode.EndsWith("stopped"))
                        await session.SendEventAsync(new { Event = "LoopingExposuresStopped" }, token);
                    if (mode.EndsWith("calibration"))
                        await session.SendEventAsync(new { Event = "StartCalibration", Mount = "Mount" }, token);
                    if (mode.EndsWith("paused"))
                        await session.SendEventAsync(new { Event = "Paused" }, token);
                }
                await session.ReplyResultAsync(request, mode == "exposure-changed" ? 4000 : 2000, token);
            }
            await Task.Delay(Timeout.Infinite, token);
        });
        await using var client = Client(server);
        await client.ConnectAsync(CancellationToken.None);
        var selected = await client.FindGuideStarAsync(CancellationToken.None);
        var task = client.ObserveSelectedStarSnrAsync(selected, 2000,
            TimeSpan.FromMilliseconds(mode == "timeout" ? 120 : 2000), CancellationToken.None);
        if (mode is "star-lost" or "preview-lock-lost" or "selected-last-sample-lost" or "selected-final-read-lost")
        {
            var lost = await Assert.ThrowsAsync<Phd2GuidingFrameLostException>(() => task);
            Assert.NotNull(lost.SelectedStarSnrBaseline);
            Assert.Equal(Star, lost.SelectedStarSnrBaseline.SelectedStar);
            Assert.Equal(mode.StartsWith("selected-") ? Phd2AppState.Selected : Phd2AppState.Looping, lost.SelectedStarSnrBaseline.AppState);
            Assert.True(lost.Snapshot.EventSequence > lost.SelectedStarSnrBaseline.EventSequence);
            Assert.Equal(client.Snapshot.GuideEpoch, lost.Snapshot.GuideEpoch);
            if (mode.StartsWith("selected-"))
            {
                Assert.Equal(3, lost.Snapshot.GuideEpoch - lost.SelectedStarSnrBaseline.GuideEpoch);
                Assert.Equal(2, lost.LostFrame?.ErrorCode);
            }
        }
        else if (mode is "reselected" or "guiding" or "exposure-changed" or "selected-reselected-same" or "stopped" or "calibration" or "lock-changed" || mode.StartsWith("selected-final-read-lost-"))
            await Assert.ThrowsAsync<Phd2CaptureException>(() => task);
        else
        {
            var observation = await task;
            Assert.Equal(mode == "timeout", observation.TimedOut);
            Assert.Equal(mode == "timeout" ? 0 : 3, observation.Samples.Count);
            if (mode is "samples" or "selected-loop")
            {
                Assert.Equal(new long[] { 2, 3, 4 }, observation.Samples.Select(x => x.Frame));
                Assert.Equal(new double?[] { 22, 23, 24 }, observation.Samples.Select(x => x.Snr));
            }
            if (mode == "missing") Assert.All(observation.Samples, x => Assert.Null(x.Snr));
        }
        Assert.All(server.ReceivedMethods.Skip(1), method => Assert.Contains(method, new[] { "get_app_state", "get_exposure" }));
    }

    [Fact]
    public async Task NativeDurationsExcludeAutoAndInvalidValuesWithoutChangingExposure()
    {
        await using var server = new FakePhd2Server(async (session, token) =>
        {
            var request = await session.ReadRequestAsync(token);
            Assert.Equal("get_exposure_durations", request.GetProperty("method").GetString());
            await session.ReplyResultAsync(request, new object[] { 6000, 0, 2000, 4000, 4000, -1, "bad" }, token);
            await Task.Delay(Timeout.Infinite, token);
        });
        await using var client = Client(server);
        await client.ConnectAsync(CancellationToken.None);
        Assert.Equal(new[] { 2000, 4000, 6000 }, await client.GetExposureDurationsAsync(CancellationToken.None));
        Assert.Single(server.ReceivedMethods);
    }
}
