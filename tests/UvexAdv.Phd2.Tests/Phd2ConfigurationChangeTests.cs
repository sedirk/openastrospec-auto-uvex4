using UvexAdv.Phd2;

namespace UvexAdv.Phd2.Tests;

public sealed class Phd2ConfigurationChangeTests
{
    [Theory]
    [InlineData("profile/2/Gamma", true)]
    [InlineData("profile/2/camera/gain", false)]
    [InlineData("profile/2/scope/MaxRaDuration", false)]
    [InlineData("profile/2/scope/calibration/xRate", false)]
    [InlineData("profile/2/unknown", false)]
    [InlineData("profile/3/Gamma", false)]
    [InlineData("currentProfile", false)]
    public void OnlyActiveProfilesScreenGammaIsExcluded(string path, bool unchanged)
    {
        var settings = new Dictionary<string, string> { [path] = "81", ["profile/2/camera/binning"] = "1" };
        var before = WindowsPhd2ConfigurationFingerprint.Compute(settings, 2);
        settings[path] = "52";
        Assert.Equal(unchanged, before == WindowsPhd2ConfigurationFingerprint.Compute(settings, 2));
    }

    [Theory]
    [InlineData("gamma", true)]
    [InlineData("same-value", true)]
    [InlineData("camera", false)]
    [InlineData("unreadable", false)]
    [InlineData("racing", false)]
    [InlineData("no-provider", false)]
    [InlineData("lost-lock", false)]
    public async Task RealClientClassifiesNotificationWithoutAnyDeviceCommand(string scenario, bool preserved)
    {
        var phase = 0;
        var read = 0;
        string? Fingerprint()
        {
            if (Volatile.Read(ref phase) != 0)
            {
                if (scenario == "unreadable") throw new IOException("read failed");
                if (scenario == "racing") return new string(Interlocked.Increment(ref read) % 2 == 0 ? 'A' : 'B', 64);
                if (scenario == "camera") return new string('B', 64);
            }
            return new string('A', 64); // Gamma excluded by the separately tested registry reader.
        }
        await using var server = new FakePhd2Server(async (session, token) =>
        {
            await session.SendEventAsync(new { Event = "StartGuiding" }, token);
            var first = await session.ReadRequestAsync(token);
            await session.ReplyResultAsync(first, "Guiding", token);
            var second = await session.ReadRequestAsync(token);
            Volatile.Write(ref phase, 1);
            await session.SendEventAsync(new { Event = "ConfigurationChange" }, token);
            if (scenario == "lost-lock")
            {
                await session.SendEventAsync(new { Event = "StarLost", Frame = 190, ErrorCode = 2 }, token);
                await session.SendEventAsync(new { Event = "GuideStep", Frame = 191, ErrorCode = 0, dx = 1, dy = 1 }, token);
            }
            await session.ReplyResultAsync(second, "Guiding", token);
            await Task.Delay(Timeout.Infinite, token);
        });
        await using var client = new Phd2Client(new Phd2ClientOptions
        {
            Host = "127.0.0.1", Port = server.Port,
            ReadConfigurationFingerprint = scenario == "no-provider" ? null : Fingerprint,
        });
        await client.ConnectAsync(CancellationToken.None);
        await client.GetAppStateAsync(CancellationToken.None);
        var before = client.Snapshot;
        await client.GetAppStateAsync(CancellationToken.None);
        var after = client.Snapshot;
        Assert.Equal(preserved, before.GuideEpoch == after.GuideEpoch);
        Assert.Equal(before.ConnectionEpoch, after.ConnectionEpoch);
        Assert.NotNull(after.LastConfigurationChange);
        Assert.False(after.HasCurrentSuccessfulSettle); // Never synthesizes settle success.
        Assert.All(server.ReceivedMethods, method => Assert.Equal("get_app_state", method));
    }
}
