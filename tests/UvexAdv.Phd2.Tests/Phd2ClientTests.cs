using System.Text.Json;
using UvexAdv.Phd2;

namespace UvexAdv.Phd2.Tests;

public sealed class Phd2ClientTests
{
    [Fact]
    public async Task EpochDiagnosticsRetainTheInvalidatingEventBeforeGuidingResumesWithoutReauthorizingIt()
    {
        await using var server = new FakePhd2Server(async (session, token) =>
        {
            await session.SendEventAsync(new { Event = "AppState", State = "Guiding" }, token);
            await session.SendEventAsync(new { Event = "LoopingExposures", Frame = 58 }, token);
            await session.SendEventAsync(new { Event = "GuideStep", Frame = 59, dx = -5.37, dy = -3.1,
                SNR = 228.5, ErrorCode = 1 }, token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await using var client = CreateClient(server);
        var updated = new TaskCompletionSource<Phd2StateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SnapshotChanged += (_, s) => { if (s.LastGuideStep?.Frame == 59) updated.TrySetResult(s); };
        await client.ConnectAsync(default);
        var snapshot = await updated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Phd2AppState.Guiding, snapshot.AppState);
        Assert.False(snapshot.HasCurrentSuccessfulSettle);
        Assert.Equal("event:GuideStep", snapshot.LastGuideEpochChange!.Cause);
        Assert.Contains(snapshot.RecentGuideEpochChanges, c => c.Cause == "event:LoopingExposures" &&
            c.PreviousState == Phd2AppState.Guiding && c.State == Phd2AppState.Looping);
        var before = snapshot.GuideEpoch;
        for (var i = 0; i < 12; i++) client.PauseAutomation();
        Assert.Equal(8, client.Snapshot.RecentGuideEpochChanges.Count);
        Assert.True(client.Snapshot.GuideEpoch > before);
        Assert.Equal("PauseAutomation", client.Snapshot.LastGuideEpochChange!.Cause);
        Assert.False(client.Snapshot.HasCurrentSuccessfulSettle);
    }

    [Theory]
    [InlineData("success")] [InlineData("native-timeout")] [InlineData("lost")]
    [InlineData("lock-changed")] [InlineData("external-epoch")]
    public async Task PrimaryRecoverySettlesOnlyTheOriginalGuidingLockWithoutSelectionOrCalibration(string scenario)
    {
        await using var server = new FakePhd2Server(async (session, token) =>
        {
            await ReplyValidCalibrationPreambleAsync(session, token);
            var prepare = await session.ReadRequestAsync(token);
            await session.SendEventAsync(new { Event = "LockPositionSet", X = 100.0, Y = 110.0 }, token);
            await session.SendEventAsync(new { Event = "StartGuiding" }, token);
            await session.ReplyResultAsync(prepare, "Guiding", token);
            var state = await session.ReadRequestAsync(token);
            Assert.Equal("get_app_state", state.GetProperty("method").GetString());
            await session.ReplyResultAsync(state, scenario == "lost" ? "LostLock" : "Guiding", token);
            var position = await session.ReadRequestAsync(token);
            Assert.Equal("get_lock_position", position.GetProperty("method").GetString());
            await session.ReplyResultAsync(position, new[] { scenario == "lock-changed" ? 120.0 : 100.0, 110.0 }, token);
            if (scenario is "lost" or "lock-changed") { await Task.Delay(Timeout.InfiniteTimeSpan, token); return; }
            var profile = await session.ReadRequestAsync(token);
            Assert.Equal("get_profile", profile.GetProperty("method").GetString());
            if (scenario == "external-epoch") await session.SendEventAsync(new { Event = "StarSelected", X = 100.0, Y = 110.0 }, token);
            await session.ReplyResultAsync(profile, new { id = 2, name = "c11+ccdt67+slit+2210" }, token);
            if (scenario == "external-epoch") { await Task.Delay(Timeout.InfiniteTimeSpan, token); return; }
            var guide = await session.ReadRequestAsync(token);
            Assert.Equal("guide", guide.GetProperty("method").GetString());
            Assert.False(guide.GetProperty("params").GetProperty("recalibrate").GetBoolean());
            Assert.False(guide.GetProperty("params").TryGetProperty("roi", out _));
            await session.ReplyResultAsync(guide, 0, token);
            await session.SendEventAsync(new { Event = "SettleBegin" }, token);
            await session.SendEventAsync(new { Event = "SettleDone", Status = scenario == "native-timeout" ? 1 : 0,
                Error = scenario == "native-timeout" ? "timed out" : null, TotalFrames = 3, DroppedFrames = 0 }, token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(default);
        Assert.True((await client.ValidateCalibrationAsync(ValidCalibrationRequirement(), default)).IsValid);
        await client.GetAppStateAsync(default);
        var before = client.Snapshot;
        var request = new Phd2SameLockSettleRequest(before.ConnectionEpoch, before.GuideEpoch,
            new(100, 110), .1, new(3, 1, 10));
        if (scenario is "success" or "native-timeout")
        {
            var result = await client.ResettleAtCurrentLockAsync(request, default);
            Assert.Equal(scenario == "success", result.Succeeded);
            Assert.False(result.NativeCalibrationOccurred);
            Assert.Equal(before.GuideEpoch + 1, client.Snapshot.GuideEpoch);
            Assert.Equal(before.LockPosition, client.Snapshot.LockPosition);
            Assert.Single(server.ReceivedMethods, method => method == "guide");
        }
        else
        {
            await Assert.ThrowsAsync<Phd2Exception>(() => client.ResettleAtCurrentLockAsync(request, default));
            Assert.DoesNotContain("guide", server.ReceivedMethods);
        }
        Assert.DoesNotContain("set_lock_position", server.ReceivedMethods);
        Assert.DoesNotContain("find_star", server.ReceivedMethods);
        Assert.DoesNotContain("set_exposure", server.ReceivedMethods);
        Assert.DoesNotContain("stop_capture", server.ReceivedMethods);
    }

    [Theory]
    [InlineData("Stopped", false)]
    [InlineData("Looping", true)]
    [InlineData("Selected", true)]
    public async Task PreviewRecoveryProducesRealStopReceiptWithoutCaptureOrGuiding(string initial, bool sendsStop)
    {
        await using var server = new FakePhd2Server(async (session, token) =>
        {
            var request = await session.ReadRequestAsync(token);
            Assert.Equal("get_app_state", request.GetProperty("method").GetString());
            await session.ReplyResultAsync(request, initial, token);
            if (sendsStop)
            {
                request = await session.ReadRequestAsync(token);
                Assert.Equal("stop_capture", request.GetProperty("method").GetString());
                await session.ReplyResultAsync(request, 0, token);
                request = await session.ReadRequestAsync(token);
                Assert.Equal("get_app_state", request.GetProperty("method").GetString());
                await session.ReplyResultAsync(request, "Stopped", token);
            }
            // Keep the fake owner connected until assertions/fixture disposal.
            // Returning here closes the socket and races the legitimate live
            // connection guard; that is not a successful-preview fixture.
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);
        var result = await client.StopPreviewAndConfirmAsync(client.Snapshot.ConnectionEpoch, CancellationToken.None);
        Assert.True(result.ConfirmedIdle);
        Assert.Equal(Phd2AppState.Stopped, result.FinalState);
        Assert.Equal(sendsStop, result.StopCommandSent);
        Assert.DoesNotContain("guide", server.ReceivedMethods);
        Assert.DoesNotContain("loop", server.ReceivedMethods);
        Assert.DoesNotContain("capture_single_frame", server.ReceivedMethods);
    }

    [Theory]
    [InlineData("Guiding", false, false)]
    [InlineData("Calibrating", false, false)]
    [InlineData("LostLock", false, false)]
    [InlineData("Paused", false, false)]
    [InlineData("Stopped", true, false)]
    [InlineData("Looping", false, true)]
    public async Task PreviewRecoveryCannotStopActiveChangedOrPausedOwner(string state, bool wrongEpoch, bool paused)
    {
        await using var server = new FakePhd2Server(async (session, token) =>
        {
            await session.ReplyResultAsync(await session.ReadRequestAsync(token), state, token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);
        if (paused) client.PauseAutomation();
        var epoch = client.Snapshot.ConnectionEpoch + (wrongEpoch ? 1 : 0);
        var ex = await Assert.ThrowsAsync<Phd2Exception>(() => client.StopPreviewAndConfirmAsync(epoch, CancellationToken.None));
        Assert.Contains("PHD2_PREVIEW_STOP_CONTEXT_CHANGED", ex.Message);
        Assert.Equal(new[] { "get_app_state" }, server.ReceivedMethods.ToArray());
    }
    [Fact]
    public void DefaultLoopingFrameTimeoutAllowsCommissionedG3ReadoutJitter()
    {
        var options = new Phd2ClientOptions();

        Assert.Equal(TimeSpan.FromSeconds(60), options.MinimumLoopingFrameEventTimeout);
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

    [Theory]
    [InlineData(null, true)]
    [InlineData("GuidingStopped", true)]
    [InlineData("LoopingExposuresStopped", true)]
    [InlineData("double-stop", false)]
    [InlineData("StarSelected", false)]
    [InlineData("ConfigurationChange", false)]
    [InlineData("StartGuiding", false)]
    public async Task NativeSingleFrameAppliesExposureBinningAndGainWithoutProfileMutation(string? completionStop, bool preserveEpoch)
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
            if (completionStop is not null)
            {
                await session.SendEventAsync(new { Event = completionStop == "double-stop" ? "GuidingStopped" : completionStop }, cancellationToken);
                if (!preserveEpoch)
                    await session.SendEventAsync(new { Event = "GuidingStopped" }, cancellationToken);
            }
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

        long captureEpoch = -1;
        client.SnapshotChanged += (_, state) =>
        {
            if (captureEpoch < 0 && state.AppState == Phd2AppState.Stopped) captureEpoch = state.GuideEpoch;
        };

        var result = await client.CaptureSingleFrameWithParametersAsync(
            new Phd2SingleFrameRequest(10, 1, 0, destination),
            CancellationToken.None);

        Assert.False(result.UsedLoopSaveFallback);
        Assert.Equal(preserveEpoch, captureEpoch == client.Snapshot.GuideEpoch);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeSingleFrameOwnerStallTimesOutWithoutRetryOrAcceptingLateCompletion(bool lateSuccess)
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "late-native-single.fit");
        var timeoutObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var stateBefore = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_app_state", stateBefore.GetProperty("method").GetString());
            await session.ReplyResultAsync(stateBefore, "Stopped", cancellationToken);

            var capture = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("capture_single_frame", capture.GetProperty("method").GetString());
            await session.ReplyResultAsync(capture, 0, cancellationToken);
            // Reproduce an owner that accepts the command but supplies neither
            // a frame nor a completion event until after the client deadline.
            var stop = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("stop_capture", stop.GetProperty("method").GetString());
            await session.ReplyResultAsync(stop, 0, cancellationToken);
            await timeoutObserved.Task.WaitAsync(cancellationToken);

            if (lateSuccess)
            {
                await File.WriteAllBytesAsync(destination, [0x47, 0x33], cancellationToken);
            }
            await session.SendEventAsync(
                new { Event = "SingleFrameComplete", Success = lateSuccess, Path = destination, Error = "Capture failed" },
                cancellationToken);

            // A fresh read is a receive-order barrier, not an automatic retry
            // or proof that the timed-out exposure can become accepted evidence.
            var stateAfter = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_app_state", stateAfter.GetProperty("method").GetString());
            await session.ReplyResultAsync(stateAfter, "Stopped", cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        var eventTimeout = TimeSpan.FromMilliseconds(150);
        await using var client = CreateClient(
            server,
            eventTimeoutMargin: TimeSpan.FromMilliseconds(50),
            minimumLoopingFrameEventTimeout: eventTimeout);
        await client.ConnectAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<Phd2CommandTimeoutException>(() =>
            client.CaptureSingleFrameWithParametersAsync(
                new Phd2SingleFrameRequest(10, 1, 0, destination),
                CancellationToken.None));
        timeoutObserved.SetResult();
        Assert.Equal(Phd2AppState.Stopped, await client.GetAppStateAsync(CancellationToken.None));

        Assert.Equal("native single-frame capture", error.Operation);
        Assert.Equal(eventTimeout, error.Timeout);
        Assert.Null(client.Snapshot.LastSingleFrame);
        Assert.Equal(
            ["get_app_state", "capture_single_frame", "stop_capture", "get_app_state"],
            server.ReceivedMethods.ToArray());
        Assert.Equal(lateSuccess, File.Exists(destination));
        if (lateSuccess)
        {
            Assert.Equal(new byte[] { 0x47, 0x33 }, await File.ReadAllBytesAsync(destination));
        }
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
        // delay when PHD2 publishes them earlier. Leave scheduler headroom in
        // the test floor: 120 ms/frame races the two 80 ms timers under the full
        // parallel suite; the rejected old formula is still only 60 ms.
        await using var client = CreateClient(
            server,
            eventTimeoutMargin: TimeSpan.FromMilliseconds(20),
            minimumLoopingFrameEventTimeout: TimeSpan.FromSeconds(1));
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

    [Theory]
    [InlineData("Looping")]
    [InlineData("Selected")]
    public async Task SaveNextLoopingFrameWaitsForFreshEventWithoutStartingOrStoppingLoop(string reportedState)
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "phd-continuous-save.fit");
        var destination = Path.Combine(directory.Path, "continuous-evidence.fit");
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var state = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_app_state", state.GetProperty("method").GetString());
            await session.ReplyResultAsync(state, reportedState, cancellationToken);

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

    [Theory]
    [InlineData("candidate-lost")]
    [InlineData("StartGuiding")]
    [InlineData("StartCalibration")]
    [InlineData("LoopingExposuresStopped")]
    [InlineData("Paused")]
    public async Task LoopFrameCandidateLossDoesNotPretendGuidingStartedOrChangeLoopOwner(string transition)
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "preview.fit");
        var destination = Path.Combine(directory.Path, "evidence.fit");
        await using var server = new FakePhd2Server(async (session, token) =>
        {
            var request = await session.ReadRequestAsync(token);
            await session.ReplyResultAsync(request, "Looping", token);
            request = await session.ReadRequestAsync(token);
            await session.ReplyResultAsync(request, 2000, token);
            await Task.Delay(50, token);
            await session.SendEventAsync(new { Event = "LoopingExposures", Frame = 4 }, token);
            request = await session.ReadRequestAsync(token);
            Assert.Equal("save_image", request.GetProperty("method").GetString());
            // Real PHD2 STATE_SELECTED failure: still capturing full frames,
            // no guide or calibration has been requested.
            await session.SendEventAsync(new { Event = "LockPositionLost" }, token);
            await session.SendEventAsync(new { Event = "StarLost", Frame = 4, SNR = 0, ErrorCode = 2 }, token);
            if (transition != "candidate-lost")
                await session.SendEventAsync(new { Event = transition }, token);
            await File.WriteAllBytesAsync(source, [1, 2, 3], token);
            await session.ReplyResultAsync(request, new { filename = source }, token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);
        var capture = client.SaveNextLoopingFrameAsync(new(2000, 1, 70, destination), CancellationToken.None);
        if (transition == "candidate-lost")
        {
            var saved = await capture;
            Assert.Equal(destination, saved.Path);
            Assert.Equal(Phd2AppState.Looping, client.Snapshot.AppState);
            Assert.Null(client.Snapshot.LockPosition);
            Assert.False(client.Snapshot.HasCurrentSuccessfulSettle);
            Assert.Equal(2, client.Snapshot.LastGuideStep?.ErrorCode);
        }
        else await Assert.ThrowsAsync<Phd2CaptureException>(() => capture);
        Assert.Equal(["get_app_state", "get_exposure", "save_image"], server.ReceivedMethods.ToArray());
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

    [Fact]
    public async Task SelectedWithoutANewLoopFrameCannotSaveOrStartCapture()
    {
        using var directory = new TemporaryDirectory();
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var state = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(state, "Selected", cancellationToken);
            var exposure = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(exposure, 1500, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SaveNextLoopingFrameAsync(
            new Phd2SingleFrameRequest(1500, 1, 70, Path.Combine(directory.Path, "must-not-exist.fit")), cancellation.Token));
        Assert.Equal(["get_app_state", "get_exposure"], server.ReceivedMethods.ToArray());
        Assert.False(File.Exists(Path.Combine(directory.Path, "must-not-exist.fit")));
    }

    [Theory]
    [InlineData("Guiding", Phd2AppState.Guiding)]
    [InlineData("Calibrating", Phd2AppState.Calibrating)]
    [InlineData("Looping", Phd2AppState.Looping)]
    [InlineData("Selected", Phd2AppState.Selected)]
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
        Assert.Equal(expectedState, exception.RejectedBeforeMutationState);
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
            var state = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_app_state", state.GetProperty("method").GetString());
            await session.ReplyResultAsync(state, "Selected", cancellationToken);
            var select = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("set_lock_position", select.GetProperty("method").GetString());
            var parameters = select.GetProperty("params");
            Assert.False(parameters.GetProperty("exact").GetBoolean());
            Assert.Equal(321.25, parameters.GetProperty("x").GetDouble());
            Assert.Equal(654.5, parameters.GetProperty("y").GetDouble());
            await session.SendEventAsync(new { Event = "LockPositionSet", X = 322.0, Y = 655.0 }, cancellationToken);
            await session.ReplyResultAsync(select, 0, cancellationToken);

            var finalState = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_app_state", finalState.GetProperty("method").GetString());
            await session.ReplyResultAsync(finalState, "Selected", cancellationToken);
            var lockPosition = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_lock_position", lockPosition.GetProperty("method").GetString());
            await session.ReplyResultAsync(lockPosition, new[] { 322.0, 655.0 }, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
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
            // This is a stop-confirmation scenario, not a disconnect scenario.
            // Keep the owner connected until the client has consumed the reply.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SupervisedGuidingRequiresRemoteTerminalSettleInsteadOfLocalTimeout(bool remoteTerminal)
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
            await session.ReplyResultAsync(guide, 0, cancellationToken);
            await session.SendEventAsync(new { Event = "StartGuiding" }, cancellationToken);
            await session.SendEventAsync(new { Event = "SettleBegin" }, cancellationToken);
            await session.SendEventAsync(new
            {
                Event = "Settling",
                Distance = 2.5,
                Time = 1.0,
                SettleTime = 0.0,
                StarLocked = true,
            }, cancellationToken);
            if (remoteTerminal)
            {
                // Later than the old timeout+one-margin (1.3s), but still
                // inside the startup/readout-aware terminal-event allowance.
                await Task.Delay(1450, cancellationToken);
                await session.SendEventAsync(new { Event = "SettleDone", Status = 1,
                    Error = "wind", TotalFrames = 2, DroppedFrames = 0 }, cancellationToken);
            }
            else
            {
                var stop = await session.ReadRequestAsync(cancellationToken);
                Assert.Equal("stop_capture", stop.GetProperty("method").GetString());
                await session.ReplyResultAsync(stop, 0, cancellationToken);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = CreateClient(
            server,
            commandTimeout: TimeSpan.FromSeconds(2),
            eventTimeoutMargin: TimeSpan.FromMilliseconds(300));
        await client.ConnectAsync(CancellationToken.None);
        var validation = await client.ValidateCalibrationAsync(
            ValidCalibrationRequirement(),
            CancellationToken.None);
        Assert.True(validation.IsValid);

        var operation = client.GuideAndSettleAsync(
            new Phd2SettleCriteria(1.5, 1, 1),
            forceRecalibration: false,
            selectionRoi: null,
            preserveSameEpochGuidingOnSettleTimeout: true,
            CancellationToken.None);

        if (remoteTerminal)
        {
            var result = await operation;
            Assert.False(result.Succeeded);
            Assert.Equal(2, result.TotalFrames);
            Assert.Equal(Phd2AppState.Guiding, client.Snapshot.AppState);
            Assert.Same(result, client.Snapshot.LastSettle);
            Assert.Equal(client.Snapshot.ConnectionEpoch, client.Snapshot.LastSettleConnectionEpoch);
            Assert.Equal(client.Snapshot.GuideEpoch, client.Snapshot.LastSettleGuideEpoch);
            Assert.DoesNotContain("stop_capture", server.ReceivedMethods);
        }
        else
        {
            await Assert.ThrowsAsync<Phd2CommandTimeoutException>(() => operation);
            Assert.Null(client.Snapshot.LastSettle);
            Assert.Null(client.Snapshot.PendingSettleOperationId);
            Assert.Single(server.ReceivedMethods.Where(method => method == "stop_capture"));
        }
        Assert.Equal(1, server.ReceivedMethods.Count(method => method == "guide"));
        Assert.DoesNotContain("set_lock_position", server.ReceivedMethods);
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

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(true, false, false, true)]
    public async Task OwnerManagedGuideNeverForcesRecalibrationAndKeepsNativeFailure(
        bool calibrated, bool nativeCalibration, bool nativeFailure, bool settleFailure)
    {
        await using var server = new FakePhd2Server(async (session, token) =>
        {
            while (!token.IsCancellationRequested)
            {
                var request = await session.ReadRequestAsync(token);
                switch (request.GetProperty("method").GetString())
                {
                    case "get_profile":
                        await session.ReplyResultAsync(request, new { id = 2, name = "c11+ccdt67+slit+2210" }, token);
                        break;
                    case "get_calibration_data":
                        await session.ReplyResultAsync(request, new { calibrated, raAngle = 0, decAngle = 42.5,
                            raRate = 0.00001, decRate = 10000, raParity = "?", decParity = "?" }, token);
                        break;
                    case "export_config_settings":
                        await session.ReplyErrorAsync(request, -32601, "unavailable diagnostic export", token);
                        break;
                    case "guide":
                        Assert.False(request.GetProperty("params").GetProperty("recalibrate").GetBoolean());
                        await session.ReplyResultAsync(request, 0, token);
                        if (nativeCalibration)
                        {
                            await session.SendEventAsync(new { Event = "StartCalibration" }, token);
                            await session.SendEventAsync(new { Event = nativeFailure ? "CalibrationFailed" : "CalibrationComplete", Reason = "native failure" }, token);
                        }
                        if (!nativeFailure)
                        {
                            await session.SendEventAsync(new { Event = "StartGuiding" }, token);
                            await session.SendEventAsync(new { Event = "SettleBegin" }, token);
                            await session.SendEventAsync(new { Event = "SettleDone", Status = settleFailure ? 1 : 0,
                                Error = settleFailure ? "timed-out waiting for guider to settle" : null,
                                TotalFrames = 41, DroppedFrames = settleFailure ? 3 : 0 }, token);
                        }
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        break;
                    default: throw new InvalidOperationException(request.ToString());
                }
            }
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);
        var validation = await client.ValidateCalibrationAsync(ValidCalibrationRequirement() with
        {
            OwnerManaged = true, AllowNativeCalibration = true,
            CalibrationTimestampUtc = DateTimeOffset.UtcNow.AddDays(-31.21),
            RequireGuideRateMatch = true,
        }, CancellationToken.None);
        // Unsupported native export and missing guide-rate metadata must warn,
        // not prevent PHD2 from running its own ordinary guide operation.
        Assert.Equal(calibrated, validation.IsValid);
        Assert.True(validation.CanStartNativeGuide);
        Assert.NotEmpty(validation.Warnings);
        var operation = client.GuideAndSettleAsync(new(1.5, 1, 1), false, CancellationToken.None);
        if (nativeFailure)
            await Assert.ThrowsAsync<Phd2CalibrationRejectedException>(() => operation);
        else
        {
            var result = await operation;
            Assert.Equal(!settleFailure, result.Succeeded);
            Assert.Equal(nativeCalibration, result.NativeCalibrationOccurred);
            Assert.Equal(!settleFailure, client.Snapshot.HasCurrentSuccessfulSettle);
            Assert.Equal(result with { NativeCalibrationOccurred = false }, client.Snapshot.LastSettle);
            Assert.Null(client.Snapshot.PendingSettleOperationId);
        }
        Assert.Single(server.ReceivedMethods.Where(method => method == "guide"));
        Assert.DoesNotContain("set_lock_position", server.ReceivedMethods);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OwnerManagedStillRejectsAbsentCalibrationWithoutMotionFreePermissionOrWrongProfile(bool calibrated, bool correctProfile)
    {
        await using var server = new FakePhd2Server(async (session, token) =>
        {
            for (var i = 0; i < 3; i++)
            {
                var request = await session.ReadRequestAsync(token);
                if (request.GetProperty("method").GetString() == "get_profile")
                    await session.ReplyResultAsync(request, new { id = correctProfile ? 2 : 99, name = "c11+ccdt67+slit+2210" }, token);
                else
                    await session.ReplyResultAsync(request, new { calibrated }, token);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);
        var validation = await client.ValidateCalibrationAsync(ValidCalibrationRequirement() with
        { OwnerManaged = true, CalibrationTimestampUtc = null }, CancellationToken.None);
        Assert.False(validation.IsValid);
        Assert.False(validation.CanStartNativeGuide);
        await Assert.ThrowsAsync<Phd2CalibrationRejectedException>(() =>
            client.GuideAndSettleAsync(new(1.5, 1, 1), false, CancellationToken.None));
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

            var selectionState = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_app_state", selectionState.GetProperty("method").GetString());
            await session.ReplyResultAsync(selectionState, "Selected", cancellationToken);
            var select = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("set_lock_position", select.GetProperty("method").GetString());
            Assert.False(select.GetProperty("params").GetProperty("exact").GetBoolean());
            await session.ReplyResultAsync(select, 0, cancellationToken);

            var finalState = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("get_app_state", finalState.GetProperty("method").GetString());
            await session.ReplyResultAsync(finalState, "Selected", cancellationToken);
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
        await using var server = new FakePhd2Server(async (session, token) =>
        {
            await ReplyValidCalibrationPreambleAsync(session, token);
            // This case tests missing age, not a disconnected owner. Keep the
            // peer alive while the client validates its final connection epoch.
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
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
    public async Task RoiCentroidEscapingTheSeedRegionIsATypedRejectedCandidateWithoutGuiding()
    {
        var roi = new Phd2Rectangle(21, 557, 1878, 502);
        var found = new Phd2Point(747.41, 513.96);
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var request = await session.ReadRequestAsync(cancellationToken);
            await session.ReplyResultAsync(request, new[] { found.X, found.Y }, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);
        var error = await Assert.ThrowsAsync<Phd2GuideStarOutsideRoiException>(
            () => client.FindGuideStarInRoiAsync(roi, CancellationToken.None));
        Assert.Equal(found, error.Selected);
        Assert.Equal(roi, error.RequestedRoi);
        Assert.Null(client.Snapshot.SelectedStar);
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
    public async Task FindGuideStarClassifiesSuccessfulNullResultAsNoCandidateOnly()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var request = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("find_star", request.GetProperty("method").GetString());
            await session.ReplyResultAsync(request, null, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<Phd2NoGuideStarException>(
            () => client.FindGuideStarAsync(CancellationToken.None));

        Assert.Contains(Phd2NoGuideStarException.FailureCode, exception.Message, StringComparison.Ordinal);
        Assert.Null(client.Snapshot.LockPosition);
        Assert.Null(client.Snapshot.SelectedStar);
        Assert.Equal(new[] { "find_star" }, server.ReceivedMethods.ToArray());
    }

    [Fact]
    public async Task FindGuideStarDoesNotClassifyRpcFailureAsNoCandidate()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var request = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("find_star", request.GetProperty("method").GetString());
            await session.ReplyErrorAsync(request, -32001, "camera unavailable", cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<Phd2RpcException>(
            () => client.FindGuideStarAsync(CancellationToken.None));

        Assert.Equal(-32001, exception.Code);
        Assert.IsNotType<Phd2NoGuideStarException>(exception);
        Assert.Equal(new[] { "find_star" }, server.ReceivedMethods.ToArray());
    }

    [Fact]
    public async Task FindGuideStarDoesNotClassifyConnectionLossAsNoCandidate()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var request = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("find_star", request.GetProperty("method").GetString());
            session.CloseConnection();
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<Phd2DisconnectedException>(
            () => client.FindGuideStarAsync(CancellationToken.None));

        Assert.IsNotType<Phd2NoGuideStarException>(exception);
        Assert.False(client.IsConnected);
        Assert.Equal(new[] { "find_star" }, server.ReceivedMethods.ToArray());
    }

    [Fact]
    public async Task FindGuideStarDoesNotClassifyMalformedPointAsNoCandidate()
    {
        await using var server = new FakePhd2Server(async (session, cancellationToken) =>
        {
            var request = await session.ReadRequestAsync(cancellationToken);
            Assert.Equal("find_star", request.GetProperty("method").GetString());
            await session.ReplyResultAsync(request, new[] { 42.0 }, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<Phd2Exception>(
            () => client.FindGuideStarAsync(CancellationToken.None));

        Assert.IsNotType<Phd2NoGuideStarException>(exception);
        Assert.Contains("numeric two-element array", exception.Message, StringComparison.Ordinal);
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
