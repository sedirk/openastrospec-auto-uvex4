namespace UvexAdv.Phd2;

public sealed record Phd2SelectedStarSnrSample(long Frame, double? Snr, int? ErrorCode);

public sealed record Phd2SelectedStarSnrObservation(
    long ConnectionEpoch, long GuideEpoch, Phd2Point SelectedStar,
    int ExposureMilliseconds, IReadOnlyList<Phd2SelectedStarSnrSample> Samples,
    bool TimedOut, DateTimeOffset CompletedUtc);

public sealed partial class Phd2Client
{
    /// <summary>Read-only native durations; zero (Auto) is deliberately excluded.</summary>
    public async Task<IReadOnlyList<int>> GetExposureDurationsAsync(CancellationToken token)
    {
        var result = await InvokeAsync("get_exposure_durations", null, token).ConfigureAwait(false);
        if (result.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw new Phd2Exception("PHD2 exposure duration list is not an array.");
        return result.EnumerateArray().Select(x => x.ValueKind == System.Text.Json.JsonValueKind.Number && x.TryGetInt32(out var value) ? value : 0)
            .Where(x => x > 0).Distinct().Order().ToArray();
    }

    /// <summary>
    /// Observes the star PHD2 already selected, without capture/selection/guide
    /// commands. Discards one pipeline frame, then measures three distinct fresh
    /// LoopingExposures events. Missing SNR is missing evidence, never zero.
    /// </summary>
    public async Task<Phd2SelectedStarSnrObservation> ObserveSelectedStarSnrAsync(
        Phd2Point selected, int exposureMilliseconds, TimeSpan timeout, CancellationToken token)
    {
        if (exposureMilliseconds <= 0 || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(exposureMilliseconds));
        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfAutomationPaused();
            _ = await GetAppStateAsync(token).ConfigureAwait(false);
            var baseline = Snapshot;
            void RequireSameSelection()
            {
                var now = Snapshot;
                if (now.AppState == Phd2AppState.LostLock && now.ConnectionEpoch == baseline.ConnectionEpoch)
                    throw new Phd2GuidingFrameLostException(now) { SelectedStarSnrBaseline = baseline };
                // Native get_app_state reports Selected while the camera is
                // looping on an already selected star. The next loop event
                // changes our telemetry state to Looping (one epoch advance),
                // without a new selection or any physical operation. This
                // read-only pre-guide sample is not settled-guide authority.
                var nativeSelectedLoopTransition = baseline.AppState == Phd2AppState.Selected &&
                    now.AppState == Phd2AppState.Looping && now.GuideEpoch == baseline.GuideEpoch + 1;
                if (!now.IsConnected || now.AutomationPaused || now.Phd2Paused ||
                    now.AppState is not (Phd2AppState.Looping or Phd2AppState.Selected) ||
                    now.ConnectionEpoch != baseline.ConnectionEpoch ||
                    (now.GuideEpoch != baseline.GuideEpoch && !nativeSelectedLoopTransition) ||
                    now.SelectedStar != selected || now.LockPosition != baseline.LockPosition ||
                    now.CalibrationChangeSequence != baseline.CalibrationChangeSequence)
                    throw new Phd2CaptureException("PHD2_GUIDE_SNR_CONTEXT_CHANGED: selection/owner changed while reading native SNR; no guide or exposure command sent.");
            }
            async Task RequireExposureAsync()
            {
                var value = await InvokeAsync("get_exposure", null, token).ConfigureAwait(false);
                if (value.ValueKind != System.Text.Json.JsonValueKind.Number ||
                    !value.TryGetInt32(out var actual) || actual != exposureMilliseconds)
                    throw new Phd2CaptureException("PHD2_GUIDE_EXPOSURE_MISMATCH: selected-star SNR exposure changed; no setting override.");
            }
            RequireSameSelection();
            var samples = new List<Phd2SelectedStarSnrSample>(3);
            long lastFrame = -1;
            bool pipelineDiscarded = false;
            var sampled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = baseline;
            var candidateLost = false;
            Phd2GuideStep? lostFrame = null;
            void RequireOwner(Phd2StateSnapshot now)
            {
                if (!now.IsConnected || now.AutomationPaused || now.Phd2Paused ||
                    now.ConnectionEpoch != baseline.ConnectionEpoch || now.SelectedStar != selected ||
                    now.Profile != baseline.Profile || now.Equipment != baseline.Equipment ||
                    now.CalibrationChangeSequence != baseline.CalibrationChangeSequence ||
                    now.LastConfigurationChange?.EventSequence != baseline.LastConfigurationChange?.EventSequence ||
                    now.PendingSettleOperationId.HasValue || now.GuideOutput?.Failed == true)
                    throw new Phd2CaptureException("PHD2_GUIDE_SNR_CONTEXT_CHANGED: preview owner changed while reading native SNR.");
            }
            // Keep the context guard alive THROUGH the final exposure readback.
            // Completing a three-sample waiter used to remove it just before
            // native LoopingExposures(ErrorCode=2), LockPositionLost, StarLost
            // finished their same-frame notification sequence (HD 24479).
            using var guard = RegisterEventWaiter(message =>
            {
                if (message.Sequence <= baseline.EventSequence) return false;
                var now = Snapshot;
                RequireOwner(now);
                var lossEvent = message.Name is "StarLost" or "LockPositionLost";
                var loopTransition = message.Name == "LoopingExposures" && observed.AppState != Phd2AppState.Looping;
                var expectedState = loopTransition ? Phd2AppState.Looping :
                    lossEvent && observed.AppState != Phd2AppState.Looping ? Phd2AppState.LostLock : observed.AppState;
                var expectedLock = message.Name == "LockPositionLost" ||
                    message.Name == "StarLost" && observed.AppState == Phd2AppState.Looping ? null : observed.LockPosition;
                if (now.GuideEpoch != observed.GuideEpoch + (lossEvent || loopTransition ? 1 : 0) ||
                    now.AppState != expectedState || now.LockPosition != expectedLock ||
                    now.AppState is not (Phd2AppState.Selected or Phd2AppState.Looping or Phd2AppState.LostLock))
                    throw new Phd2CaptureException("PHD2_GUIDE_SNR_CONTEXT_CHANGED: unexpected selection/lifecycle event during preview SNR.");
                observed = now;
                if (lossEvent || message.Name == "LoopingExposures" && GetOptionalInt64(message.Payload, "ErrorCode") is >= 2)
                {
                    candidateLost = true;
                    if (message.Name != "LockPositionLost") lostFrame = ParseGuideStep(message.Payload);
                    sampled.TrySetResult();
                    return false;
                }
                if (message.Name != "LoopingExposures" || candidateLost || samples.Count == 3) return false;
                var frame = GetOptionalInt64(message.Payload, "Frame");
                if (frame is null or < 1 || frame <= lastFrame) return false;
                lastFrame = frame.Value;
                if (!pipelineDiscarded) { pipelineDiscarded = true; return false; }
                var snr = GetOptionalDouble(message.Payload, "SNR");
                samples.Add(new(frame.Value, snr is { } s && double.IsFinite(s) && s > 0 ? s : null,
                    (int?)GetOptionalInt64(message.Payload, "ErrorCode")));
                if (samples.Count == 3) sampled.TrySetResult();
                return false;
            });
            await RequireExposureAsync().ConfigureAwait(false);
            var timedOut = false;
            try
            {
                var completed = await Task.WhenAny(sampled.Task, guard.Task).WaitAsync(timeout, token).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
            }
            catch (TimeoutException) { timedOut = true; }
            await RequireExposureAsync().ConfigureAwait(false);
            // Dispose synchronizes with event dispatch before reading the list
            // and loss witness. An external reselect/stop still wins over loss.
            guard.Dispose();
            if (guard.Task.IsCompleted) await guard.Task.ConfigureAwait(false);
            var final = Snapshot;
            RequireOwner(final);
            if (final.GuideEpoch != observed.GuideEpoch || final.AppState != observed.AppState || final.LockPosition != observed.LockPosition)
                throw new Phd2CaptureException("PHD2_GUIDE_SNR_CONTEXT_CHANGED: preview changed after final SNR readback.");
            if (candidateLost)
                throw new Phd2GuidingFrameLostException(final, lostFrame) { SelectedStarSnrBaseline = baseline };
            return new(baseline.ConnectionEpoch, Snapshot.GuideEpoch, selected,
                exposureMilliseconds, samples.ToArray(), timedOut, DateTimeOffset.UtcNow);
        }
        finally { operationGate.Release(); }
    }
}
