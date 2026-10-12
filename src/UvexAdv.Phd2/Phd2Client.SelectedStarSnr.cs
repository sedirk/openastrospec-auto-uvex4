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
            using var waiter = RegisterEventWaiter(message =>
            {
                if (message.Sequence <= baseline.EventSequence) return false;
                // During preview, PHD2 clears the selected lock immediately
                // before StarLost. Surface one candidate loss, not an unrelated
                // owner failure that would restart the complete WCS chain.
                if (message.Name == "StarLost" || message.Name == "LockPositionLost" &&
                    Snapshot.AppState == Phd2AppState.Looping)
                    throw new Phd2GuidingFrameLostException(Snapshot, ParseGuideStep(message.Payload))
                        { SelectedStarSnrBaseline = baseline };
                RequireSameSelection();
                if (message.Name != "LoopingExposures") return false;
                var frame = GetOptionalInt64(message.Payload, "Frame");
                if (frame is null or < 1 || frame <= lastFrame) return false;
                lastFrame = frame.Value;
                if (!pipelineDiscarded) { pipelineDiscarded = true; return false; }
                var snr = GetOptionalDouble(message.Payload, "SNR");
                samples.Add(new(frame.Value, snr is { } s && double.IsFinite(s) && s > 0 ? s : null,
                    (int?)GetOptionalInt64(message.Payload, "ErrorCode")));
                return samples.Count == 3;
            });
            RequireSameSelection();
            await RequireExposureAsync().ConfigureAwait(false);
            var timedOut = false;
            try { await WaitForEventAsync(waiter, "selected-star SNR", timeout, token).ConfigureAwait(false); }
            catch (Phd2CommandTimeoutException) { timedOut = true; }
            // Remove before reading the sample list, including the timeout path.
            waiter.Dispose();
            RequireSameSelection();
            await RequireExposureAsync().ConfigureAwait(false);
            RequireSameSelection();
            return new(baseline.ConnectionEpoch, Snapshot.GuideEpoch, selected,
                exposureMilliseconds, samples.ToArray(), timedOut, DateTimeOffset.UtcNow);
        }
        finally { operationGate.Release(); }
    }
}
