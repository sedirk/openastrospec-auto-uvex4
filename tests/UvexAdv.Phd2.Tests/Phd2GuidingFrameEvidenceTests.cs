using System.Security.Cryptography;
using UvexAdv.Phd2;

namespace UvexAdv.Phd2.Tests;

public sealed class Phd2GuidingFrameEvidenceTests
{
    [Fact]
    public async Task SavesOnlyAfterFreshGuideStepWithoutChangingCaptureOrGuiding()
    {
        var directory = CreateTemporaryDirectory();
        var sourcePath = Path.Combine(directory, "phd2-current.fit");
        var destinationPath = Path.Combine(directory, "immutable-guiding-frame.fit");
        var bytes = Enumerable.Range(0, 128).Select(value => (byte)value).ToArray();
        await File.WriteAllBytesAsync(sourcePath, bytes);
        try
        {
            await using var server = new FakePhd2Server(async (session, cancellationToken) =>
            {
                var state = await session.ReadRequestAsync(cancellationToken);
                Assert.Equal("get_app_state", state.GetProperty("method").GetString());
                await session.ReplyResultAsync(state, "Guiding", cancellationToken);

                await session.SendEventAsync(new
                {
                    Event = "GuideStep",
                    Frame = 41,
                    dx = 0.05,
                    dy = -0.02,
                    SNR = 25.0,
                    HFD = 2.5,
                    AvgDist = 0.06,
                    ErrorCode = 0,
                }, cancellationToken);
                // The first event is intentionally allowed to race the fresh
                // waiter. The next completed frame must still be accepted.
                await Task.Delay(50, cancellationToken);
                await session.SendEventAsync(new
                {
                    Event = "GuideStep",
                    Frame = 42,
                    dx = 0.04,
                    dy = -0.01,
                    SNR = 26.0,
                    HFD = 2.4,
                    AvgDist = 0.05,
                    ErrorCode = 0,
                }, cancellationToken);

                var save = await session.ReadRequestAsync(cancellationToken);
                Assert.Equal("save_image", save.GetProperty("method").GetString());
                await session.ReplyResultAsync(save, new { filename = sourcePath }, cancellationToken);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
            await using var client = CreateClient(server);
            await client.ConnectAsync(CancellationToken.None);

            var result = await client.SaveCurrentGuidingFrameAsync(
                new Phd2GuidingFrameRequest(destinationPath, TimeSpan.FromSeconds(2)),
                CancellationToken.None);

            Assert.Contains(result.TriggerGuideFrame, new long[] { 41, 42 });
            Assert.True(result.EventSequence > 0);
            Assert.Equal(destinationPath, result.Path);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), result.Sha256);
            Assert.False(result.GuidingWasInterrupted);
            Assert.False(result.ExposureChanged);
            Assert.False(result.CaptureLoopStarted);
            Assert.False(result.AutomaticRetryAllowed);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(destinationPath));
            Assert.Equal(new[] { "get_app_state", "save_image" }, server.ReceivedMethods.ToArray());
            Assert.DoesNotContain("set_exposure", server.ReceivedMethods);
            Assert.DoesNotContain("loop", server.ReceivedMethods);
            Assert.DoesNotContain("stop_capture", server.ReceivedMethods);
            Assert.DoesNotContain("guide", server.ReceivedMethods);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NonGuidingStateFailsWithoutSavingOrChangingCapture()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            await using var server = new FakePhd2Server(async (session, cancellationToken) =>
            {
                var state = await session.ReadRequestAsync(cancellationToken);
                await session.ReplyResultAsync(state, "Selected", cancellationToken);
            });
            await using var client = CreateClient(server);
            await client.ConnectAsync(CancellationToken.None);

            var exception = await Assert.ThrowsAsync<Phd2CaptureException>(() =>
                client.SaveCurrentGuidingFrameAsync(
                    new Phd2GuidingFrameRequest(
                        Path.Combine(directory, "must-not-exist.fit"),
                        TimeSpan.FromSeconds(1)),
                    CancellationToken.None));

            Assert.Contains("Guiding", exception.Message, StringComparison.Ordinal);
            Assert.Equal(new[] { "get_app_state" }, server.ReceivedMethods.ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"uvex-phd2-guiding-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
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
