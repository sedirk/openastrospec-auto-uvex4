using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed record Phd2AdaptiveStarCaptureResult(Phd2SingleFrameResult Frame,
    Phd2StateSnapshot Before, Phd2StateSnapshot After, string? Rejection);

// Recovery needs an image, not a selection loop. PHD2's loop can legitimately
// reselect its previous star and update its lock; that is not an owner change.
// Use the already-commissioned atomic capture interface, with no loop fallback.
internal static class Phd2AdaptiveStarCapture
{
    internal static async Task<Phd2AdaptiveStarCaptureResult> CaptureAsync(
        IPhd2Client client, Phd2SingleFrameRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var before = client.Snapshot;
        if (!Stopped(before) || before.GuideOutput?.Failed == true)
            throw new InvalidOperationException("PHD2_ADAPTIVE_STOP_RECEIPT_REQUIRED: Native star detection requires the unchanged stopped owner.");
        var frame = await client.CaptureSingleFrameWithParametersAsync(request, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var after = client.Snapshot;
        var rejection = !Stopped(after) ? "not-stopped-or-paused" :
            after.ConnectionEpoch != before.ConnectionEpoch ? "connection-epoch" :
            after.GuideEpoch != before.GuideEpoch ? "guide-epoch" :
            after.Profile != before.Profile || after.Equipment != before.Equipment ? "device-identity" :
            after.LastSingleFrame != frame || frame.UsedLoopSaveFallback || !frame.RequestedParametersApplied ||
                frame.VerifiedExposureMilliseconds != request.ExposureMs ? "native-frame-receipt" :
            after.LastGuideStep != before.LastGuideStep ? "guide-step" :
            after.SelectedStar != before.SelectedStar || after.LockPosition != before.LockPosition ? "selection-or-lock" :
            after.CalibrationChangeSequence != before.CalibrationChangeSequence ? "calibration" :
            after.GuideOutput?.Failed == true ? "guide-output" : null;
        return new(frame, before, after, rejection);
    }

    private static bool Stopped(Phd2StateSnapshot snapshot) => snapshot.IsConnected &&
        snapshot.AppState == Phd2AppState.Stopped && !snapshot.AutomationPaused && !snapshot.Phd2Paused &&
        !snapshot.PendingSettleOperationId.HasValue;
}
