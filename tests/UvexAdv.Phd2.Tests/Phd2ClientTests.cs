using System.Text.Json;
using UvexAdv.Phd2;

namespace UvexAdv.Phd2.Tests;

public sealed class Phd2ClientTests
{
    [Fact]
    public void DefaultLoopingFrameTimeoutAllowsCommissionedG3ReadoutJitter()
    {
        var options = new Phd2ClientOptions();

        Assert.Equal(TimeSpan.FromSeconds(20), options.MinimumLoopingFrameEventTimeout);
    }

    [Fact]
    public async Task CorrelatesOutOfOrderResponsesWhileSerializingJsonLines()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var first = await session.ReadRequestAsync(cancellationToken);
            var second = await session.ReadRequestAsync(cancellationToken);

            await ReplyForMethodAsync(session, second, cancellationToken);
            await ReplyForMethodAsync(session, first, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var profileTask = client.GetProfileAsync(CancellationToken.None);
        var equipmentTask = client.GetCurrentEquipmentAsync(CancellationToken.None);
        await Task.WhenAll(profileTask, equipmentTask);
        var profile = await profileTask;
        var equipment = await equipmentTask;

        Assert.Equal(new Phd2Profile(2, "c11+ccdt67+slit+2210"), profile);
        Assert.Equal(Phd2RuntimeEquipmentConventions.G3CameraName, equipment.Camera?.Name);
        Assert.True(equipment.Camera?.Connected);
    }

    [Fact]
    public async Task StrictIdentityValidationReportsEveryMismatch()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var first = await session.ReadRequestAsync(cancellationToken);
            var second = await session.ReadRequestAsync(cancellationToken);
            await ReplyIdentityMismatchAsync(session, first, cancellationToken);
            await ReplyIdentityMismatchAsync(session, second, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var validation = await client.ValidateIdentityAsync(
            new Phd2IdentityRequirement(
                2,
                "c11+ccdt67+slit+2210",
                Phd2RuntimeEquipmentConventions.G3CameraName,
                Phd2RuntimeEquipmentConventions.OnStepMountName,
                RequireConnected: true),
            CancellationToken.None);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Failures, failure => failure.Contains("profile id", StringComparison.Ordinal));
        Assert.Contains(validation.Failures, failure => failure.Contains("camera name", StringComparison.Ordinal));
        Assert.Contains(validation.Failures, failure => failure.Contains("not connected", StringComparison.Ordinal));
        Assert.Contains(validation.Failures, failure => failure.Contains("mount name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RuntimeIdentityDoesNotAcceptRegistryMenuNames()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var first = await session.ReadRequestAsync(cancellationToken);
            var second = await session.ReadRequestAsync(cancellationToken);
            foreach (var request in new[] { first, second })
            {
                if (request.GetProperty("method").GetString() == "get_profile")
                {
                    await session.ReplyResultAsync(
                        request,
                        new { id = 2, name = "c11+ccdt67+slit+2210" },
                        cancellationToken);
                }
                else
                {
                    await session.ReplyResultAsync(
                        request,
                        new
                        {
                            camera = new { name = "ToupTek Camera", connected = true },
                            mount = new { name = "OnStep Telescope (ASCOM)", connected = true },
                        },
                        cancellationToken);
                }
            }
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var validation = await client.ValidateIdentityAsync(
            new Phd2IdentityRequirement(
                2,
                "c11+ccdt67+slit+2210",
                Phd2RuntimeEquipmentConventions.G3CameraName,
                Phd2RuntimeEquipmentConventions.OnStepMountName),
            CancellationToken.None);

        Assert.Equal(Phd2ValidationStatus.Invalid, validation.Status);
        Assert.Contains(validation.Failures, failure => failure.Contains("camera name", StringComparison.Ordinal));
        Assert.Contains(validation.Failures, failure => failure.Contains("mount name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CaptureFullFrameUsesSupportedLoopSaveProtocolWithoutInventedEvent()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "phd-save-image.fit");
        var destination = Path.Combine(directory.Path, "g3-evidence.fit");
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var state = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_app_state", state.GetProperty("method").GetString());
            Assert.False(state.TryGetProperty("params", out _));
            await session.ReplyResultAsync(state, "Stopped", cancellationToken);

            var priorExposure = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_exposure", priorExposure.GetProperty("method").GetString());
            await session.ReplyResultAsync(priorExposure, 1500, cancellationToken);

            var exposure = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("set_exposure", exposure.GetProperty("method").GetString());
            var exposureParameters = exposure.GetProperty("params");
            Assert.Equal(JsonValueKind.Array, exposureParameters.ValueKind);
            Assert.Equal([1500], exposureParameters.EnumerateArray().Select(value => value.GetInt32()).ToArray());
            Assert.DoesNotContain("binning", exposure.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("gain", exposure.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("path", exposure.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("save", exposure.GetRawText(), StringComparison.OrdinalIgnoreCase);
            await session.ReplyResultAsync(exposure, 0, cancellationToken);

            var exposureReadback = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_exposure", exposureReadback.GetProperty("method").GetString());
            Assert.False(exposureReadback.TryGetProperty("params", out _));
            await session.ReplyResultAsync(exposureReadback, 1500, cancellationToken);

            var loop = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("loop", loop.GetProperty("method").GetString());
            Assert.False(loop.TryGetProperty("params", out _));
            await session.ReplyResultAsync(loop, 0, cancellationToken);
            await session.SendEventAsync(new { Event = "LoopingExposures", Frame = 1 }, cancellationToken);

            var stop = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("stop_capture", stop.GetProperty("method").GetString());
            Assert.False(stop.TryGetProperty("params", out _));
            await session.ReplyResultAsync(stop, 0, cancellationToken);
            await session.SendEventAsync(new { Event = "LoopingExposuresStopped" }, cancellationToken);

            var save = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("save_image", save.GetProperty("method").GetString());
            Assert.False(save.TryGetProperty("params", out _));
            await File.WriteAllBytesAsync(source, [0x53, 0x49, 0x4d, 0x50, 0x4c, 0x45], cancellationToken);
            await session.ReplyResultAsync(save, new { filename = source }, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var result = await client.CaptureFullFrameAsync(
            new Phd2SingleFrameRequest(1500, 1, 70, destination),
            CancellationToken.None);

        Assert.Equal(destination, result.Path);
        Assert.True(result.UsedLoopSaveFallback);
        Assert.False(result.RequestedParametersApplied);
        Assert.Equal(1500, result.VerifiedExposureMilliseconds);
        Assert.False(result.AutomaticRetryAllowed);
        Assert.Equal(
            new byte[] { 0x53, 0x49, 0x4d, 0x50, 0x4c, 0x45 },
            await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(source));
        Assert.Equal(result, client.Snapshot.LastSingleFrame);
        Assert.Equal(
            ["get_app_state", "get_exposure", "set_exposure", "get_exposure", "loop", "stop_capture", "save_image"],
            server.ReceivedMethods.ToArray());
        Assert.DoesNotContain("capture_single_frame", server.ReceivedMethods);
    }

    [Fact]
    public async Task NativeSingleFrameAppliesExposureBinningAndGainWithoutProfileMutation()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "native-single.fit");
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var stateBefore = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_app_state", stateBefore.GetProperty("method").GetString());
            await session.ReplyResultAsync(stateBefore, "Stopped", cancellationToken);

            var capture = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("capture_single_frame", capture.GetProperty("method").GetString());
            var parameters = capture.GetProperty("params");
            Assert.Equal(10, parameters.GetProperty("exposure").GetInt32());
            Assert.Equal(1, parameters.GetProperty("binning").GetInt32());
            Assert.Equal(0, parameters.GetProperty("gain").GetInt32());
            Assert.Equal(destination, parameters.GetProperty("path").GetString());
            Assert.True(parameters.GetProperty("save").GetBoolean());
            await session.ReplyResultAsync(capture, 0, cancellationToken);
            await File.WriteAllBytesAsync(destination, [0x47, 0x33], cancellationToken);
            await session.SendEventAsync(
                new { Event = "SingleFrameComplete", Success = true, Path = destination },
                cancellationToken);

            var stateAfter = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_app_state", stateAfter.GetProperty("method").GetString());
            await session.ReplyResultAsync(stateAfter, "Stopped", cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var result = await client.CaptureSingleFrameWithParametersAsync(
            new Phd2SingleFrameRequest(10, 1, 0, destination),
            CancellationToken.None);

        Assert.False(result.UsedLoopSaveFallback);
        Assert.True(result.RequestedParametersApplied);
        Assert.True(result.GainAndBinningApplied);
        Assert.Equal(10, result.VerifiedExposureMilliseconds);
        Assert.Equal(new byte[] { 0x47, 0x33 }, await File.ReadAllBytesAsync(destination));
        Assert.Equal(
            ["get_app_state", "capture_single_frame", "get_app_state"],
            server.ReceivedMethods.ToArray());
        Assert.DoesNotContain("set_exposure", server.ReceivedMethods);
        Assert.DoesNotContain("loop", server.ReceivedMethods);
        Assert.DoesNotContain("save_image", server.ReceivedMethods);
    }

    [Fact]
    public async Task NativeSingleFrameNeverFallsBackWhenPHD2MethodIsUnavailable()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "unsupported.fit");
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var state = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(state, "Stopped", cancellationToken);

            var capture = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("capture_single_frame", capture.GetProperty("method").GetString());
            await session.ReplyErrorAsync(capture, -32601, "method not found", cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<Phd2CaptureException>(() =>
            client.CaptureSingleFrameWithParametersAsync(
                new Phd2SingleFrameRequest(10, 1, 0, destination),
                CancellationToken.None));

        Assert.Contains("no profile-gain fallback", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["get_app_state", "capture_single_frame"], server.ReceivedMethods.ToArray());
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task CaptureFullFrameDiscardsFirstPipelineFrameAfterExposureChange()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "phd-short-exposure.fit");
        var destination = Path.Combine(directory.Path, "short-exposure-evidence.fit");
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var state = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(state, "Stopped", cancellationToken);

            var priorExposure = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_exposure", priorExposure.GetProperty("method").GetString());
            await session.ReplyResultAsync(priorExposure, 50, cancellationToken);

            var setExposure = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("set_exposure", setExposure.GetProperty("method").GetString());
            await session.ReplyResultAsync(setExposure, 0, cancellationToken);

            var readback = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(readback, 10, cancellationToken);

            var loop = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(loop, 0, cancellationToken);
            await Task.Delay(80, cancellationToken);
            await session.SendEventAsync(new { Event = "LoopingExposures", Frame = 1 }, cancellationToken);
            await Task.Delay(80, cancellationToken);
            await session.SendEventAsync(new { Event = "LoopingExposures", Frame = 2 }, cancellationToken);

            var stop = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("stop_capture", stop.GetProperty("method").GetString());
            await session.ReplyResultAsync(stop, 0, cancellationToken);
            await session.SendEventAsync(new { Event = "LoopingExposuresStopped" }, cancellationToken);

            var save = await session.ReadRequestAsync(cancellationToken);
            await File.WriteAllBytesAsync(source, [0x31, 0x30], cancellationToken);
            await session.ReplyResultAsync(save, new { filename = source }, cancellationToken);
        });
        // The old formula allowed only 2 * (10 ms + 20 ms) = 60 ms and
        // timed out before these realistic short-exposure full-frame events.
        // The per-frame readout/event floor allows both frames without adding
        // delay when PHD2 publishes them earlier.
        await using var client = CreateClient(
            server,
            eventTimeoutMargin: TimeSpan.FromMilliseconds(20),
            minimumLoopingFrameEventTimeout: TimeSpan.FromMilliseconds(120));
        await client.ConnectAsync(CancellationToken.None);

        var result = await client.CaptureFullFrameAsync(
            new Phd2SingleFrameRequest(10, 1, 100, destination),
            CancellationToken.None);

        Assert.Equal(10, result.VerifiedExposureMilliseconds);
        Assert.Equal(new byte[] { 0x31, 0x30 }, await File.ReadAllBytesAsync(destination));
        Assert.Equal(
            ["get_app_state", "get_exposure", "set_exposure", "get_exposure", "loop", "stop_capture", "save_image"],
            server.ReceivedMethods.ToArray());
    }

    [Fact]
    public async Task SaveNextLoopingFrameWaitsForFreshEventWithoutStartingOrStoppingLoop()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "phd-continuous-save.fit");
        var destination = Path.Combine(directory.Path, "continuous-evidence.fit");
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var state = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_app_state", state.GetProperty("method").GetString());
            await session.ReplyResultAsync(state, "Looping", cancellationToken);

            var exposure = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_exposure", exposure.GetProperty("method").GetString());
            await session.ReplyResultAsync(exposure, 1500, cancellationToken);

            // There is deliberately no RPC between waiter registration and
            // the fresh loop event. Delay ensures this is a post-baseline
            // frame rather than an event that was already in flight.
            await Task.Delay(50, cancellationToken);
            await session.SendEventAsync(new { Event = "LoopingExposures", Frame = 12 }, cancellationToken);

            var save = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("save_image", save.GetProperty("method").GetString());
            await File.WriteAllBytesAsync(source, [0x43, 0x4f, 0x4e, 0x54], cancellationToken);
            await session.ReplyResultAsync(save, new { filename = source }, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var result = await client.SaveNextLoopingFrameAsync(
            new Phd2SingleFrameRequest(1500, 1, 70, destination),
            CancellationToken.None);

        Assert.Equal(destination, result.Path);
        Assert.Equal(1500, result.VerifiedExposureMilliseconds);
        Assert.False(result.AutomaticRetryAllowed);
        Assert.Equal(new byte[] { 0x43, 0x4f, 0x4e, 0x54 }, await File.ReadAllBytesAsync(destination));
        Assert.Equal(
            ["get_app_state", "get_exposure", "save_image"],
            server.ReceivedMethods.ToArray());
        Assert.DoesNotContain("set_exposure", server.ReceivedMethods);
        Assert.DoesNotContain("loop", server.ReceivedMethods);
        Assert.DoesNotContain("stop_capture", server.ReceivedMethods);
    }

    [Fact]
    public async Task SaveNextLoopingFrameRejectsIdleStateWithoutMutatingCapture()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "blocked-continuous-evidence.fit");
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var state = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(state, "Stopped", cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        await Assert.ThrowsAsync<Phd2CaptureException>(() => client.SaveNextLoopingFrameAsync(
            new Phd2SingleFrameRequest(1500, 1, 70, destination),
            CancellationToken.None));

        Assert.Equal(["get_app_state"], server.ReceivedMethods.ToArray());
        Assert.DoesNotContain("save_image", server.ReceivedMethods);
        Assert.DoesNotContain("loop", server.ReceivedMethods);
        Assert.DoesNotContain("stop_capture", server.ReceivedMethods);
    }

    [Theory]
    [InlineData("Guiding", Phd2AppState.Guiding)]
    [InlineData("Calibrating", Phd2AppState.Calibrating)]
    [InlineData("Looping", Phd2AppState.Looping)]
    [InlineData("Paused", Phd2AppState.Paused)]
    [InlineData("LostLock", Phd2AppState.LostLock)]
    [InlineData("FutureState", Phd2AppState.Unknown)]
    public async Task CaptureFullFrameRejectsNonIdleStateWithoutStartingLoop(
        string rpcState,
        Phd2AppState expectedState)
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "blocked-evidence.fit");
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var state = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_app_state", state.GetProperty("method").GetString());
            Assert.False(state.TryGetProperty("params", out _));
            await session.ReplyResultAsync(state, rpcState, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<Phd2CaptureException>(() => client.CaptureFullFrameAsync(
            new Phd2SingleFrameRequest(1500, 1, 70, destination),
            CancellationToken.None));

        Assert.Contains("left untouched", exception.Message, StringComparison.Ordinal);
        Assert.Equal(expectedState, client.Snapshot.AppState);
        Assert.Equal(["get_app_state"], server.ReceivedMethods.ToArray());
        Assert.DoesNotContain("set_exposure", server.ReceivedMethods);
        Assert.DoesNotContain("loop", server.ReceivedMethods);
        Assert.DoesNotContain("stop_capture", server.ReceivedMethods);
    }

    [Fact]
    public async Task CaptureFullFrameRejectsDisconnectedClientWithoutStartingLoop()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "disconnected-evidence.fit");
        await using var server = new FakePhd2Server(
            (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        await using var client = CreateClient(server);

        await Assert.ThrowsAsync<Phd2DisconnectedException>(() => client.CaptureFullFrameAsync(
            new Phd2SingleFrameRequest(1500, 1, 70, destination),
            CancellationToken.None));

        Assert.Empty(server.ReceivedMethods);
        Assert.DoesNotContain("loop", server.ReceivedMethods);
    }

    [Fact]
    public async Task CaptureFullFrameRefusesToOverwriteImmutableEvidence()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "existing-evidence.fit");
        await File.WriteAllBytesAsync(destination, [1, 2, 3]);
        await using var server = new FakePhd2Server(
            (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => client.CaptureFullFrameAsync(
            new Phd2SingleFrameRequest(1500, 1, 70, destination),
            CancellationToken.None));

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(destination));
        Assert.Empty(server.ReceivedMethods);
    }

    [Fact]
    public async Task CalibrationDataUsesOfficialMountPositionalParameterJsonFixture()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var calibration = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal(
                "{\"jsonrpc\":\"2.0\",\"method\":\"get_calibration_data\",\"params\":[\"Mount\"],\"id\":1}",
                calibration.GetRawText());
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
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var result = await client.GetCalibrationDataAsync(CancellationToken.None);

        Assert.True(result.Calibrated);
        Assert.Equal(10.0, result.RaAngleDegrees);
        Assert.Equal(100.0, result.DecAngleDegrees);
    }

    [Fact]
    public async Task SelectGuideStarUsesNonExactModeAndVerifiesLockPosition()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var select = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("set_lock_position", select.GetProperty("method").GetString());
            var parameters = select.GetProperty("params");
            Assert.False(parameters.GetProperty("exact").GetBoolean());
            Assert.Equal(321.25, parameters.GetProperty("x").GetDouble());
            Assert.Equal(654.5, parameters.GetProperty("y").GetDouble());
            await session.SendEventAsync(new { Event = "StarSelected", X = 322.0, Y = 655.0 }, cancellationToken);
            await session.ReplyResultAsync(select, 0, cancellationToken);

            var lockPosition = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_lock_position", lockPosition.GetProperty("method").GetString());
            await session.ReplyResultAsync(lockPosition, new[] { 322.0, 655.0 }, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var selected = await client.SelectGuideStarAsync(
            new Phd2Point(321.25, 654.5),
            CancellationToken.None);

        Assert.Equal(new Phd2Point(322.0, 655.0), selected);
        Assert.Equal(selected, client.Snapshot.SelectedStar);
    }

    [Fact]
    public async Task GuideWaitsForSettleDoneAndStopUsesStopCapture()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            await ReplyValidCalibrationPreambleAsync(session, cancellationToken);

            var profileRecheck = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_profile", profileRecheck.GetProperty("method").GetString());
            await session.ReplyResultAsync(
                profileRecheck,
                new { id = 2, name = "c11+ccdt67+slit+2210" },
                cancellationToken);

            var guide = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("guide", guide.GetProperty("method").GetString());
            var parameters = guide.GetProperty("params");
            Assert.False(parameters.GetProperty("recalibrate").GetBoolean());
            var settle = parameters.GetProperty("settle");
            Assert.Equal(1.5, settle.GetProperty("pixels").GetDouble());
            Assert.Equal(10, settle.GetProperty("time").GetInt32());
            Assert.Equal(40, settle.GetProperty("timeout").GetInt32());
            await session.ReplyResultAsync(guide, 0, cancellationToken);
            await session.SendEventAsync(new { Event = "SettleBegin" }, cancellationToken);
            await session.SendEventAsync(
                new
                {
                    Event = "Settling",
                    Distance = 0.7,
                    Time = 12.0,
                    SettleTime = 10.0,
                    StarLocked = true,
                },
                cancellationToken);
            await session.SendEventAsync(
                new
                {
                    Event = "SettleDone",
                    Status = 0,
                    TotalFrames = 12,
                    DroppedFrames = 1,
                },
                cancellationToken);

            var stateBeforeStop = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_app_state", stateBeforeStop.GetProperty("method").GetString());
            await session.ReplyResultAsync(stateBeforeStop, "Guiding", cancellationToken);

            var stop = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("stop_capture", stop.GetProperty("method").GetString());
            await session.ReplyResultAsync(stop, 0, cancellationToken);

            var stateAfterStop = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_app_state", stateAfterStop.GetProperty("method").GetString());
            await session.ReplyResultAsync(stateAfterStop, "Stopped", cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var validation = await client.ValidateCalibrationAsync(
            ValidCalibrationRequirement(),
            CancellationToken.None);
        Assert.True(validation.IsValid);

        var result = await client.GuideAndSettleAsync(
            new Phd2SettleCriteria(1.5, 10, 40),
            forceRecalibration: false,
            CancellationToken.None);
        await client.StopGuidingAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(12, result.TotalFrames);
        Assert.Equal(1, result.DroppedFrames);
        Assert.Null(client.Snapshot.LastSettle);
        Assert.False(client.Snapshot.HasCurrentSuccessfulSettle);
        Assert.Null(client.Snapshot.SettleProgress);
    }

    [Fact]
    public async Task OrdinaryGuideWithoutApprovedCalibrationSendsNoGuideRpc()
    {
        await using var server = new FakePhd2Server(
            (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        await Assert.ThrowsAsync<Phd2CalibrationRejectedException>(() => client.GuideAndSettleAsync(
            new Phd2SettleCriteria(1.5, 10, 40),
            forceRecalibration: false,
            CancellationToken.None));

        Assert.Empty(server.ReceivedMethods);
        Assert.DoesNotContain("guide", server.ReceivedMethods);
    }

    [Fact]
    public async Task ForcedRecalibrationRequiresIdentityAndStarThenLeavesCalibrationUnapproved()
    {
        var selectedStar = new Phd2Point(321.0, 654.0);
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var identityFirst = await session.ReadRequestAsync(cancellationToken);
            var identitySecond = await session.ReadRequestAsync(cancellationToken);
            await ReplyForMethodAsync(session, identityFirst, cancellationToken);
            await ReplyForMethodAsync(session, identitySecond, cancellationToken);

            var select = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("set_lock_position", select.GetProperty("method").GetString());
            Assert.False(select.GetProperty("params").GetProperty("exact").GetBoolean());
            await session.ReplyResultAsync(select, 0, cancellationToken);

            var selectedLock = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_lock_position", selectedLock.GetProperty("method").GetString());
            await session.ReplyResultAsync(
                selectedLock,
                new[] { selectedStar.X, selectedStar.Y },
                cancellationToken);

            var profileRecheck = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_profile", profileRecheck.GetProperty("method").GetString());
            await ReplyForMethodAsync(session, profileRecheck, cancellationToken);

            var equipmentRecheck = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_current_equipment", equipmentRecheck.GetProperty("method").GetString());
            await ReplyForMethodAsync(session, equipmentRecheck, cancellationToken);

            var lockRecheck = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_lock_position", lockRecheck.GetProperty("method").GetString());
            await session.ReplyResultAsync(
                lockRecheck,
                new[] { selectedStar.X, selectedStar.Y },
                cancellationToken);

            var guide = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("guide", guide.GetProperty("method").GetString());
            Assert.True(guide.GetProperty("params").GetProperty("recalibrate").GetBoolean());
            await session.ReplyResultAsync(guide, 0, cancellationToken);
            await session.SendEventAsync(new { Event = "StartCalibration", Mount = "Mount" }, cancellationToken);
            await session.SendEventAsync(new { Event = "CalibrationComplete", Mount = "Mount" }, cancellationToken);
            await session.SendEventAsync(new { Event = "StartGuiding" }, cancellationToken);
            await session.SendEventAsync(new { Event = "SettleBegin" }, cancellationToken);
            // Real PHD2 persists several calibration settings after guiding
            // starts. These notifications do not begin a new guide epoch and
            // must not invalidate the pending SettleDone attestation.
            await session.SendEventAsync(new { Event = "ConfigurationChange" }, cancellationToken);
            await session.SendEventAsync(new { Event = "LoopingExposures" }, cancellationToken);
            await session.SendEventAsync(
                new
                {
                    Event = "SettleDone",
                    Status = 0,
                    TotalFrames = 8,
                    DroppedFrames = 0,
                },
                cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var identity = await client.ValidateIdentityAsync(
            new Phd2IdentityRequirement(
                2,
                "c11+ccdt67+slit+2210",
                Phd2RuntimeEquipmentConventions.G3CameraName,
                Phd2RuntimeEquipmentConventions.OnStepMountName),
            CancellationToken.None);
        Assert.True(identity.IsValid);
        _ = await client.SelectGuideStarAsync(selectedStar, CancellationToken.None);

        var recalibration = await client.GuideAndSettleAsync(
            new Phd2SettleCriteria(1.5, 10, 40),
            forceRecalibration: true,
            CancellationToken.None);

        Assert.True(recalibration.Succeeded);
        Assert.Null(client.Snapshot.CalibrationValidation);
        await Assert.ThrowsAsync<Phd2CalibrationRejectedException>(() => client.GuideAndSettleAsync(
            new Phd2SettleCriteria(1.5, 10, 40),
            forceRecalibration: false,
            CancellationToken.None));
        Assert.Equal(1, server.ReceivedMethods.Count(method => method == "guide"));
    }

    [Fact]
    public async Task CalibrationGateRejectsObservedFortyPointSixDegreeOrthogonalityError()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var profileBefore = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(
                profileBefore,
                new { id = 2, name = "c11+ccdt67+slit+2210" },
                cancellationToken);

            var calibration = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_calibration_data", calibration.GetProperty("method").GetString());
            Assert.Equal(
                ["Mount"],
                calibration.GetProperty("params").EnumerateArray().Select(value => value.GetString()).ToArray());
            await session.ReplyResultAsync(
                calibration,
                new
                {
                    calibrated = true,
                    xAngle = 46.4,
                    xRate = 28.497,
                    xParity = "+",
                    yAngle = 177.0,
                    yRate = 43.566,
                    yParity = "+",
                    declination = 45.1,
                },
                cancellationToken);

            var profileAfter = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(
                profileAfter,
                new { id = 2, name = "c11+ccdt67+slit+2210" },
                cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var validation = await client.ValidateCalibrationAsync(
            ValidCalibrationRequirement() with { MaximumOrthogonalityErrorDegrees = 10 },
            CancellationToken.None);

        Assert.Equal(Phd2ValidationStatus.Invalid, validation.Status);
        Assert.NotNull(validation.OrthogonalityErrorDegrees);
        Assert.Equal(40.6, validation.OrthogonalityErrorDegrees.Value, precision: 6);
        Assert.Contains(
            validation.Failures,
            failure => failure.Contains("orthogonality error", StringComparison.Ordinal));
        await Assert.ThrowsAsync<Phd2CalibrationRejectedException>(() => client.GuideAndSettleAsync(
            new Phd2SettleCriteria(1.5, 10, 40),
            forceRecalibration: false,
            CancellationToken.None));
        Assert.DoesNotContain("guide", server.ReceivedMethods);
    }

    [Fact]
    public async Task MissingCalibrationAgeIsIndeterminateAndBlocksGuiding()
    {
        await using var server = new FakePhd2Server(ReplyValidCalibrationPreambleAsync);
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var validation = await client.ValidateCalibrationAsync(
            ValidCalibrationRequirement() with { CalibrationTimestampUtc = null },
            CancellationToken.None);

        Assert.Equal(Phd2ValidationStatus.Indeterminate, validation.Status);
        Assert.Contains(
            validation.IndeterminateReasons,
            reason => reason.Contains("does not expose calibration age", StringComparison.Ordinal));
        await Assert.ThrowsAsync<Phd2CalibrationRejectedException>(() => client.GuideAndSettleAsync(
            new Phd2SettleCriteria(1.5, 10, 40),
            forceRecalibration: false,
            CancellationToken.None));
    }

    [Fact]
    public async Task StableCameraIdRequirementIsExplicitlyIndeterminateWhenApiCannotExposeIt()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var first = await session.ReadRequestAsync(cancellationToken);
            var second = await session.ReadRequestAsync(cancellationToken);
            await ReplyForMethodAsync(session, first, cancellationToken);
            await ReplyForMethodAsync(session, second, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var validation = await client.ValidateIdentityAsync(
            new Phd2IdentityRequirement(
                2,
                "c11+ccdt67+slit+2210",
                Phd2RuntimeEquipmentConventions.G3CameraName,
                Phd2RuntimeEquipmentConventions.OnStepMountName,
                RequireConnected: true,
                StableCameraId: @"\\?\usb#vid_0547&pid_14ab#fixture-runtime"),
            CancellationToken.None);

        Assert.Equal(Phd2ValidationStatus.Indeterminate, validation.Status);
        Assert.Contains(
            validation.IndeterminateReasons,
            reason => reason.Contains("does not expose the stable camera id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BusinessPauseIsLocalAndBlocksNewMutationsWithoutSendingSetPaused()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "paused.fit");
        await using var server = new FakePhd2Server(
            (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        client.PauseAutomation();
        await Assert.ThrowsAsync<Phd2AutomationPausedException>(() => client.CaptureFullFrameAsync(
            new Phd2SingleFrameRequest(1000, 1, 50, destination),
            CancellationToken.None));

        Assert.True(client.IsAutomationPaused);
        Assert.Empty(server.ReceivedMethods);
        Assert.DoesNotContain("set_paused", server.ReceivedMethods);

        client.ResumeAutomation();
        Assert.False(client.IsAutomationPaused);
    }

    [Fact]
    public async Task CommandTimeoutRemovesPendingRequest()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            _ = await session.ReadRequestAsync(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = CreateClient(server, commandTimeout: TimeSpan.FromMilliseconds(100));
        await client.ConnectAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<Phd2CommandTimeoutException>(
            () => client.GetProfileAsync(CancellationToken.None));

        Assert.Equal("get_profile", exception.Operation);
    }

    [Fact]
    public async Task DisconnectFaultsPendingRequest()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            _ = await session.ReadRequestAsync(cancellationToken);
            session.CloseConnection();
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        await Assert.ThrowsAsync<Phd2DisconnectedException>(
            () => client.GetProfileAsync(CancellationToken.None));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task CallerCancellationDoesNotPoisonLaterRequestsOrMisrouteLateResponse()
    {
        var assertionsCompleted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var canceled = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_profile", canceled.GetProperty("method").GetString());
            await Task.Delay(150, cancellationToken);
            await session.ReplyResultAsync(
                canceled,
                new { id = 2, name = "late response" },
                cancellationToken);

            var next = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_current_equipment", next.GetProperty("method").GetString());
            await session.ReplyResultAsync(
                next,
                new
                {
                    camera = new { name = Phd2RuntimeEquipmentConventions.G3CameraName, connected = true },
                    mount = new { name = Phd2RuntimeEquipmentConventions.OnStepMountName, connected = true },
                },
                cancellationToken);
            await assertionsCompleted.Task.WaitAsync(cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.GetProfileAsync(cancellation.Token));
            var equipment = await client.GetCurrentEquipmentAsync(CancellationToken.None);

            Assert.Equal(Phd2RuntimeEquipmentConventions.G3CameraName, equipment.Camera?.Name);
            Assert.True(client.IsConnected);
        }
        finally
        {
            assertionsCompleted.TrySetResult(true);
        }
    }

    [Fact]
    public async Task EventStreamUpdatesInspectableGuidingSnapshot()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            await session.SendEventAsync(
                new { Event = "Version", PHDVersion = "2.6.14", PHDSubver = "test" },
                cancellationToken);
            await session.SendEventAsync(new { Event = "AppState", State = "Guiding" }, cancellationToken);
            await session.SendEventAsync(
                new
                {
                    Event = "GuideStep",
                    Frame = 42,
                    dx = 0.25,
                    dy = -0.5,
                    SNR = 12.5,
                    HFD = 3.2,
                    AvgDist = 0.4,
                },
                cancellationToken);
            await session.SendEventAsync(new { Event = "Alert", Msg = "test alert", Type = "warning" }, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = CreateClient(server);
        var updated = new TaskCompletionSource<Phd2StateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SnapshotChanged += (_, state) =>
        {
            if (state.EventSequence >= 4)
            {
                updated.TrySetResult(state);
            }
        };

        await client.ConnectAsync(CancellationToken.None);
        var snapshot = await updated.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("2.6.14 test", snapshot.PhdVersion);
        Assert.Equal(Phd2AppState.Guiding, snapshot.AppState);
        Assert.Equal(42, snapshot.LastGuideStep?.Frame);
        Assert.Equal(12.5, snapshot.LastGuideStep?.Snr);
        Assert.Equal("test alert", snapshot.LastAlert);
    }

    [Fact]
    public async Task FindGuideStarInRoiConfinesPHD2SelectionAndUpdatesSnapshot()
    {
        var roi = new Phd2Rectangle(560, 770, 80, 80);
        var found = new Phd2Point(603.25, 816.5);
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var request = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("find_star", request.GetProperty("method").GetString());
            Assert.Equal(
                new[] { roi.X, roi.Y, roi.Width, roi.Height },
                request.GetProperty("params").GetProperty("roi")
                    .EnumerateArray().Select(value => value.GetInt32()).ToArray());
            await session.ReplyResultAsync(request, new[] { found.X, found.Y }, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var selected = await client.FindGuideStarInRoiAsync(roi, CancellationToken.None);

        Assert.Equal(found, selected);
        Assert.Equal(found, client.Snapshot.LockPosition);
        Assert.Equal(found, client.Snapshot.SelectedStar);
        Assert.Equal(new[] { "find_star" }, server.ReceivedMethods.ToArray());
    }

    [Fact]
    public async Task FindGuideStarDelegatesFullFrameSelectionToPHD2AndUpdatesSnapshot()
    {
        var found = new Phd2Point(1134.79, 711.6);
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var request = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("find_star", request.GetProperty("method").GetString());
            Assert.False(request.TryGetProperty("params", out _));
            await session.ReplyResultAsync(request, new[] { found.X, found.Y }, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var selected = await client.FindGuideStarAsync(CancellationToken.None);

        Assert.Equal(found, selected);
        Assert.Equal(found, client.Snapshot.LockPosition);
        Assert.Equal(found, client.Snapshot.SelectedStar);
        Assert.Equal(new[] { "find_star" }, server.ReceivedMethods.ToArray());
    }

    [Fact]
    public async Task PixelScaleUsesExactUnroundedPHD2Value()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var request = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_pixel_scale", request.GetProperty("method").GetString());
            Assert.False(request.TryGetProperty("params", out _));
            await session.ReplyResultAsync(request, 0.383749, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var result = await client.GetPixelScaleAsync(CancellationToken.None);

        Assert.Equal(0.383749, result, 9);
        Assert.Equal(new[] { "get_pixel_scale" }, server.ReceivedMethods.ToArray());
    }

    private static Phd2Client CreateClient(
        FakePhd2Server server,
        TimeSpan? commandTimeout = null,
        TimeSpan? eventTimeoutMargin = null,
        TimeSpan? minimumLoopingFrameEventTimeout = null)
    {
        return new Phd2Client(new Phd2ClientOptions
        {
            Host = "127.0.0.1",
            Port = server.Port,
            CommandTimeout = commandTimeout ?? TimeSpan.FromSeconds(2),
            EventTimeoutMargin = eventTimeoutMargin ?? TimeSpan.FromSeconds(2),
            MinimumLoopingFrameEventTimeout = minimumLoopingFrameEventTimeout ?? TimeSpan.FromSeconds(2),
            FileReadyTimeout = TimeSpan.FromSeconds(2),
        });
    }

    private static Task ReplyForMethodAsync(
        FakePhd2Session session,
        JsonElement request,
        CancellationToken cancellationToken)
    {
        return request.GetProperty("method").GetString() switch
        {
            "get_profile" => session.ReplyResultAsync(
                request,
                new { id = 2, name = "c11+ccdt67+slit+2210" },
                cancellationToken),
            "get_current_equipment" => session.ReplyResultAsync(
                request,
                new
                {
                    camera = new { name = Phd2RuntimeEquipmentConventions.G3CameraName, connected = true },
                    mount = new { name = Phd2RuntimeEquipmentConventions.OnStepMountName, connected = true },
                },
                cancellationToken),
            var method => throw new InvalidOperationException(method),
        };
    }

    private static Task ReplyIdentityMismatchAsync(
        FakePhd2Session session,
        JsonElement request,
        CancellationToken cancellationToken)
    {
        return request.GetProperty("method").GetString() switch
        {
            "get_profile" => session.ReplyResultAsync(
                request,
                new { id = 9, name = "c11+ccdt67+slit+2210" },
                cancellationToken),
            "get_current_equipment" => session.ReplyResultAsync(
                request,
                new
                {
                    camera = new { name = "Wrong Camera", connected = false },
                    mount = new { name = "Wrong Mount", connected = true },
                },
                cancellationToken),
            var method => throw new InvalidOperationException(method),
        };
    }

    private static async Task ReplyValidCalibrationPreambleAsync(
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
        Assert.Equal(
            ["Mount"],
            calibration.GetProperty("params").EnumerateArray().Select(value => value.GetString()).ToArray());
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
        Assert.Equal("get_profile", profileAfter.GetProperty("method").GetString());
        await session.ReplyResultAsync(
            profileAfter,
            new { id = 2, name = "c11+ccdt67+slit+2210" },
            cancellationToken);
    }

    private static Phd2CalibrationRequirement ValidCalibrationRequirement()
    {
        return new Phd2CalibrationRequirement(
            2,
            "c11+ccdt67+slit+2210",
            DateTimeOffset.UtcNow,
            MaximumAge: TimeSpan.FromDays(30),
            MaximumOrthogonalityErrorDegrees: 10,
            MinimumAxisRatePixelsPerSecond: 0.01,
            MaximumAxisRatePixelsPerSecond: 100);
    }
}
