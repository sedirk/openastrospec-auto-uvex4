using System.Reflection;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2AdaptiveStarCaptureTests
{
    private static readonly Phd2StateSnapshot Stopped = Phd2StateSnapshot.Disconnected with {
        IsConnected = true, AppState = Phd2AppState.Stopped, ConnectionEpoch = 1, GuideEpoch = 89,
        SelectedStar = new(654.44, 99.12), LockPosition = new(654.44, 99.12),
    };

    [Fact]
    public async Task RecoveryUsesAtomicNativeCaptureNotPreviewLoopAndPreservesOriginalStopEpoch()
    {
        var (client, mock) = Create("none");
        var result = await Phd2AdaptiveStarCapture.CaptureAsync(client, Request, default);
        Assert.Null(result.Rejection);
        Assert.Equal(89, result.After.GuideEpoch);
        Assert.Equal(result.Before.SelectedStar, result.After.SelectedStar);
        Assert.Equal(new[] { "get_Snapshot", "CaptureSingleFrameWithParametersAsync", "get_Snapshot" }, mock.Calls);
    }

    [Theory]
    [InlineData("preview-reselection", "selection-or-lock")]
    [InlineData("connection", "connection-epoch")]
    [InlineData("epoch", "guide-epoch")]
    [InlineData("calibration", "calibration")]
    [InlineData("guiding", "not-stopped-or-paused")]
    [InlineData("pause", "not-stopped-or-paused")]
    [InlineData("disconnect", "not-stopped-or-paused")]
    [InlineData("fallback", "native-frame-receipt")]
    [InlineData("wrong-frame", "native-frame-receipt")]
    [InlineData("profile", "device-identity")]
    public async Task UnexpectedOwnerChangesRemainFailuresAndAreExplained(string fault, string expected)
    {
        var (client, _) = Create(fault);
        var result = await Phd2AdaptiveStarCapture.CaptureAsync(client, Request, default);
        Assert.Equal(expected, result.Rejection);
    }

    [Fact]
    public async Task UnconfirmedStopOrCancellationDoesNotTakeAFrame()
    {
        var (client, mock) = Create("none");
        mock.State = Stopped with { AppState = Phd2AppState.Guiding };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Phd2AdaptiveStarCapture.CaptureAsync(client, Request, default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Phd2AdaptiveStarCapture.CaptureAsync(client, Request, new(true)));
        Assert.DoesNotContain("CaptureSingleFrameWithParametersAsync", mock.Calls);
    }

    [Fact]
    public async Task UnsupportedAtomicCaptureDoesNotFallbackOrRetry()
    {
        var (client, mock) = Create("unsupported");
        await Assert.ThrowsAsync<Phd2CaptureException>(() => Phd2AdaptiveStarCapture.CaptureAsync(client, Request, default));
        Assert.Equal(1, mock.Calls.Count(x => x == "CaptureSingleFrameWithParametersAsync"));
        Assert.DoesNotContain("CaptureFullFrameAsync", mock.Calls);
    }

    private static readonly Phd2SingleFrameRequest Request = new(2000, 1, 0, "recovery.fit");
    private static (IPhd2Client, CaptureProxy) Create(string fault)
    {
        var client = DispatchProxy.Create<IPhd2Client, CaptureProxy>();
        var mock = (CaptureProxy)(object)client; mock.Fault = fault;
        return (client, mock);
    }
    private class CaptureProxy : DispatchProxy
    {
        public string Fault { get; set; } = "none";
        public Phd2StateSnapshot State { get; set; } = Stopped;
        public List<string> Calls { get; } = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Calls.Add(method!.Name);
            if (method.Name == "get_Snapshot") return State;
            if (method.Name != "CaptureSingleFrameWithParametersAsync") throw new InvalidOperationException(method.Name);
            if (Fault == "unsupported") throw new Phd2CaptureException("atomic capture unavailable");
            var frame = new Phd2SingleFrameResult(Request.DestinationPath, Fault == "fallback", true, DateTimeOffset.UtcNow, 2000);
            State = State with { LastSingleFrame = frame };
            State = Fault switch {
                // Recorded 6 Cep preview-loop relocation must never be excused by
                // removing identity checks; prevent the loop operation instead.
                "preview-reselection" => State with { SelectedStar = new(653, 90.61), LockPosition = new(653, 90.61) },
                "connection" => State with { ConnectionEpoch = 2 },
                "epoch" => State with { GuideEpoch = 90 },
                "calibration" => State with { CalibrationChangeSequence = 20 },
                "guiding" => State with { AppState = Phd2AppState.Guiding },
                "pause" => State with { AutomationPaused = true },
                "disconnect" => State with { IsConnected = false },
                "wrong-frame" => State with { LastSingleFrame = null },
                "profile" => State with { Profile = new(3, "other") },
                _ => State,
            };
            return Task.FromResult(frame);
        }
    }
}
