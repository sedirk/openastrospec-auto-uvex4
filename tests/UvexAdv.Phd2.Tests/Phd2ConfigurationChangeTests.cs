using UvexAdv.Phd2;

namespace UvexAdv.Phd2.Tests;

public sealed class Phd2ConfigurationChangeTests
{
    [Theory]
    [InlineData("profile/2/camera/gain", "4:0", true)]
    [InlineData("profile/2/camera/gain", "4:50", false)]
    [InlineData("profile/2/camera/binning", "4:2", false)]
    [InlineData("profile/2/scope/MaxRaDuration", "4:900", false)]
    [InlineData("profile/2/unknown", "4:1", false)]
    public void NativeGainWitnessDoesNotIgnoreUnrequestedSettings(string key, string value, bool allowed)
    {
        var before = new Dictionary<string, string> { ["profile/2/camera/gain"] = "4:100", ["profile/2/camera/binning"] = "4:1" };
        var current = new Dictionary<string, string>(before) { [key] = value };
        Assert.Equal(allowed, WindowsPhd2ConfigurationFingerprint.IsOnlyNativeGainChange(before, current, 2, 0));
        Assert.False(WindowsPhd2ConfigurationFingerprint.IsOnlyNativeGainChange(before, current, 3, 0));
    }

    [Theory]
    [InlineData("owned", true)]
    [InlineData("other-setting", false)]
    [InlineData("no-witness", false)]
    [InlineData("not-restored", false)]
    [InlineData("failed-not-restored", false)]
    [InlineData("paused", false)]
    public async Task October31StoppedHandoffSurvivesOnlyExpectedNativeGainTransaction(string scenario, bool preserve)
    {
        var path = Path.Combine(Path.GetTempPath(), "uvex-native-gain-" + Guid.NewGuid() + ".fit");
        var original = new Dictionary<string, string> { ["profile/2/camera/gain"] = "4:100", ["profile/2/camera/binning"] = "4:1" };
        var modified = new Dictionary<string, string>(original) { ["profile/2/camera/gain"] = "4:0" };
        if (scenario == "other-setting") modified["profile/2/scope/MaxRaDuration"] = "4:900";
        IReadOnlyDictionary<string, string> values = original;
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakePhd2Server(async (session, token) =>
        {
            var before = await session.ReadRequestAsync(token);
            await session.ReplyResultAsync(before, "Stopped", token);
            var capture = await session.ReadRequestAsync(token);
            Assert.Equal("capture_single_frame", capture.GetProperty("method").GetString());
            values = modified;
            await session.SendEventAsync(new { Event = "ConfigurationChange" }, token);
            await changed.Task.WaitAsync(token); // Must inspect the temporary value, not only its later restoration.
            if (scenario == "paused") await session.SendEventAsync(new { Event = "Paused" }, token);
            await session.ReplyResultAsync(capture, 0, token);
            await File.WriteAllBytesAsync(path, [1, 2], token);
            // Native FinishStop completes the FITS, restores gain, then reports loop stopped.
            await session.SendEventAsync(new { Event = "SingleFrameComplete", Success = scenario != "failed-not-restored", Path = path }, token);
            if (scenario == "failed-not-restored")
            {
                await Task.Delay(Timeout.Infinite, token);
                return;
            }
            if (scenario != "not-restored") values = original;
            await session.SendEventAsync(new { Event = "ConfigurationChange" }, token);
            await session.SendEventAsync(new { Event = "LoopingExposuresStopped" }, token);
            var after = await session.ReadRequestAsync(token);
            await session.ReplyResultAsync(after, "Stopped", token);
            await Task.Delay(Timeout.Infinite, token);
        });
        await using var client = new Phd2Client(new Phd2ClientOptions
        {
            Host = "127.0.0.1", Port = server.Port,
            ReadConfigurationFingerprint = () => WindowsPhd2ConfigurationFingerprint.Compute(values, 2),
            CreateNativeCaptureConfigurationWitness = scenario == "no-witness" ? null :
                request => () => WindowsPhd2ConfigurationFingerprint.IsOnlyNativeGainChange(original, values, 2, request.GainPercent),
        });
        client.SnapshotChanged += (_, s) => { if (s.LastConfigurationChange is not null) changed.TrySetResult(); };
        await client.ConnectAsync(default);
        long epoch = -1;
        client.SnapshotChanged += (_, s) => { if (epoch < 0 && s.AppState == Phd2AppState.Stopped) epoch = s.GuideEpoch; };
        try
        {
            if (scenario is "not-restored" or "failed-not-restored")
                await Assert.ThrowsAsync<Phd2CaptureException>(() => client.CaptureSingleFrameWithParametersAsync(new(10, 1, 0, path), default));
            else
                await client.CaptureSingleFrameWithParametersAsync(new(10, 1, 0, path), default);
            Assert.Equal(preserve, epoch == client.Snapshot.GuideEpoch);
            Assert.False(client.Snapshot.HasCurrentSuccessfulSettle);
            Assert.Equal(scenario == "failed-not-restored" ? new[] { "get_app_state", "capture_single_frame" } :
                new[] { "get_app_state", "capture_single_frame", "get_app_state" }, server.ReceivedMethods);
        }
        finally { File.Delete(path); }
    }

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
