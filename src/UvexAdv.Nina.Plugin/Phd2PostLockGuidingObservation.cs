using System.Threading.Channels;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Read-only continuation of a verified exact-lock operation. This is never a
/// synthetic SettleDone and cannot grant unattended or exact-placement status.
/// </summary>
internal sealed record Phd2PostLockGuidingObservation(
    string EvidenceId,
    long ConnectionEpoch,
    long GuideEpoch,
    long AfterEventSequence,
    Phd2Point LockPosition,
    double LockTolerancePixels,
    DateTimeOffset LockVerifiedUtc,
    bool TrackingWithinTolerance = false,
    int ObservedGuideFrames = 0,
    int AcceptedResidualFrames = 0,
    DateTimeOffset? ResidualsCompletedUtc = null,
    bool YieldedToFreshResiduals = false)
{
    internal bool IsCurrent(Phd2StateSnapshot state) =>
        state.IsConnected && !state.AutomationPaused && !state.Phd2Paused &&
        state.AppState == Phd2AppState.Guiding &&
        state.ConnectionEpoch == ConnectionEpoch && state.GuideEpoch == GuideEpoch &&
        state.PendingSettleOperationId is null &&
        state.LockPosition is { } actual &&
        double.IsFinite(LockTolerancePixels) && LockTolerancePixels >= 0 &&
        Distance(actual, LockPosition) <= LockTolerancePixels;

    internal bool HasAcceptedWindow(Phd2StateSnapshot state) =>
        AcceptedResidualFrames >= 3 && ResidualsCompletedUtc >= LockVerifiedUtc && IsCurrent(state);

    internal Phd2PostLockGuidingObservation AcceptResiduals(
        IReadOnlyList<Phd2GuidingFrameResult> frames, Phd2StateSnapshot state)
    {
        if (!IsCurrent(state) || frames.Count < 3 ||
            frames.Select(frame => frame.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() != frames.Count)
            throw new Phd2Exception("Post-lock guiding requires three distinct fresh same-epoch residual frames.");
        var sequence = AfterEventSequence;
        var guideFrame = -1L;
        foreach (var frame in frames)
        {
            if (frame.EventSequence <= sequence || frame.TriggerGuideFrame <= guideFrame ||
                frame.GuideStepUtc < LockVerifiedUtc || frame.CompletedUtc < frame.GuideStepUtc ||
                frame.CompletedUtc > DateTimeOffset.UtcNow ||
                frame.Sha256 is not { Length: 64 } || !frame.Sha256.All(Uri.IsHexDigit) || frame.GuidingWasInterrupted ||
                frame.ExposureChanged || frame.CaptureLoopStarted)
                throw new Phd2Exception("Post-lock residual provenance is stale, reused or interrupted.");
            sequence = frame.EventSequence;
            guideFrame = frame.TriggerGuideFrame;
        }
        return this with { AcceptedResidualFrames = frames.Count, ResidualsCompletedUtc = frames[^1].CompletedUtc };
    }

    internal Phd2CalibrationSettleEvidence ToCalibrationEvidence(
        Phd2SettleResult originalNativeSettle, Phd2StateSnapshot state) => new(
        EvidenceId, originalNativeSettle,
        GuideCommandAccepted: false, SettleBeginObserved: false,
        SameConnectionEpoch: state.ConnectionEpoch == ConnectionEpoch,
        SameGuideEpoch: state.GuideEpoch == GuideEpoch,
        EvaluatedUtc: DateTimeOffset.UtcNow,
        FreshGuidingWindowAccepted: HasAcceptedWindow(state),
        FreshGuidingSampleCount: AcceptedResidualFrames,
        ReadOnlyPostLockWindow: true,
        ExactLockReadbackVerified: IsCurrent(state),
        FreshGuidingWindowCompletedUtc: ResidualsCompletedUtc);

    internal static Phd2PostLockGuidingObservation BeginFreshResidualObservation(
        IPhd2Client client, Phd2ExactLockPositionResult exact,
        long connectionEpoch, long guideEpoch, double lockTolerancePixels,
        bool supervised, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!supervised || !exact.Exact || exact.RegistryProfileMutated ||
            !double.IsFinite(lockTolerancePixels) || lockTolerancePixels < 0 ||
            !double.IsFinite(exact.VerificationErrorPixels) || exact.VerificationErrorPixels < 0 ||
            exact.VerificationErrorPixels > lockTolerancePixels ||
            !double.IsFinite(Distance(exact.Requested, exact.Verified)) ||
            Distance(exact.Requested, exact.Verified) > lockTolerancePixels ||
            exact.CompletedUtc > DateTimeOffset.UtcNow)
            throw new Phd2Exception("A supervised, verified exact-lock operation is required for read-only tracking observation.");

        var initial = client.Snapshot;
        var proof = new Phd2PostLockGuidingObservation(
            "post-lock-window-" + Guid.NewGuid().ToString("N"), connectionEpoch, guideEpoch,
            initial.EventSequence, exact.Verified, lockTolerancePixels, exact.CompletedUtc);
        if (!proof.IsCurrent(initial)) throw new Phd2Exception("The verified lock/guide epoch changed before read-only observation.");
        // This is only an unaccepted continuity baseline. SaveCurrentGuidingFrameAsync
        // already waits for a newer GuideStep and binds its immutable FITS to the
        // same epoch/lock. Waiting for an extra unsaved frame here consumes a full
        // exposure without adding evidence to the mandatory three-frame window.
        return proof with { YieldedToFreshResiduals = true };
    }

    internal static async Task<Phd2PostLockGuidingObservation> ObserveAsync(
        IPhd2Client client, Phd2ExactLockPositionResult exact,
        long connectionEpoch, long guideEpoch, double lockTolerancePixels,
        Phd2SettleCriteria criteria, bool supervised, CancellationToken cancellationToken,
        bool yieldToFreshResidualsAfterFirstGuideStep = false)
    {
        if (!double.IsFinite(criteria.Pixels) || criteria.Pixels <= 0 ||
            criteria.StableTimeSeconds < 0 || criteria.TimeoutSeconds <= 0)
            throw new Phd2Exception("Valid tracking criteria are required for read-only observation.");
        var proof = BeginFreshResidualObservation(
            client, exact, connectionEpoch, guideEpoch, lockTolerancePixels, supervised, cancellationToken)
            with { YieldedToFreshResiduals = false };
        var initial = client.Snapshot;

        // Preserve every state transition during this bounded wait; a LostLock
        // followed by Guiding must not be coalesced into an apparently good state.
        var updates = Channel.CreateUnbounded<Phd2StateSnapshot>();
        void Changed(object? sender, Phd2StateSnapshot state) => updates.Writer.TryWrite(state);
        client.SnapshotChanged += Changed;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = exact.CompletedUtc.AddSeconds(criteria.TimeoutSeconds) - DateTimeOffset.UtcNow;
        try
        {
            if (!proof.IsCurrent(client.Snapshot)) throw new Phd2Exception("Guide continuity changed while subscribing to tracking updates.");
            if (remaining <= TimeSpan.Zero) return proof;
            bounded.CancelAfter(remaining);
            long lastFrame = initial.LastGuideStep?.Frame ?? -1;
            DateTimeOffset? inRangeSince = null;
            while (true)
            {
                var state = await updates.Reader.ReadAsync(bounded.Token).ConfigureAwait(false);
                if (!proof.IsCurrent(state)) throw new Phd2Exception("Guide/lock continuity was lost during post-lock observation.");
                if (state.EventSequence <= proof.AfterEventSequence ||
                    state.LastGuideStep is not { Frame: { } frame } step || frame <= lastFrame) continue;
                lastFrame = frame;
                proof = proof with { ObservedGuideFrames = proof.ObservedGuideFrames + 1 };
                var measuredOffset =
                    step.DxPixels is { } dx && step.DyPixels is { } dy &&
                    double.IsFinite(dx) && double.IsFinite(dy)
                    ? Math.Sqrt(dx * dx + dy * dy) : double.NaN;
                var validSample = step.ErrorCode is null or 0 or 1 && double.IsFinite(measuredOffset);
                var inRange = validSample && measuredOffset <= criteria.Pixels;
                if (yieldToFreshResidualsAfterFirstGuideStep && validSample)
                {
                    // A supervised stage needs real optical residuals, not a
                    // second settle-sized wait before starting those exposures.
                    // At a 4 s cadence that redundant wait can consume half of
                    // the unchanged 30 s stage deadline. This confirms only
                    // continuity; three newer immutable frames are still required.
                    while (updates.Reader.TryRead(out var queued))
                        if (!proof.IsCurrent(queued))
                            throw new Phd2Exception("Guide/lock continuity was lost before the fresh residual window.");
                    var current = client.Snapshot;
                    if (!proof.IsCurrent(current))
                        throw new Phd2Exception("Guide/lock continuity changed before the fresh residual window.");
                    return proof with
                    {
                        AfterEventSequence = Math.Max(state.EventSequence, current.EventSequence),
                        YieldedToFreshResiduals = true,
                        TrackingWithinTolerance = false,
                    };
                }
                if (!inRange) { inRangeSince = null; continue; }
                var now = DateTimeOffset.UtcNow;
                inRangeSince ??= now;
                if ((now - inRangeSince.Value).TotalSeconds >= criteria.StableTimeSeconds)
                    return proof with { TrackingWithinTolerance = true };
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!proof.IsCurrent(client.Snapshot)) throw new Phd2Exception("Guide continuity failed at the tracking-window deadline.");
            return proof; // Quality timeout only. Fresh optical frames are still mandatory.
        }
        finally { client.SnapshotChanged -= Changed; }
    }

    private static double Distance(Phd2Point a, Phd2Point b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
