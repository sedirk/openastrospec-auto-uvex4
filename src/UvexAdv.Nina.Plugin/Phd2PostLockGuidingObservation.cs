using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Read-only continuation of a verified exact-lock operation. This is never a
/// synthetic SettleDone or proof of exact target placement. ADR-0029 permits
/// the measured continuation in either mode, subject to independent slit gates.
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
    bool YieldedToFreshResiduals = false,
    bool MeasuredContinuation = false)
{
    internal bool IsCurrent(Phd2StateSnapshot state) =>
        state.IsConnected && !state.AutomationPaused && !state.Phd2Paused &&
        state.AppState == Phd2AppState.Guiding &&
        state.ConnectionEpoch == ConnectionEpoch && state.GuideEpoch == GuideEpoch &&
        state.PendingSettleOperationId is null &&
        state.GuideOutput?.Failed != true &&
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
        FreshGuidingWindowCompletedUtc: ResidualsCompletedUtc,
        MeasuredPostLockContinuation: MeasuredContinuation);

    internal static Phd2PostLockGuidingObservation BeginFreshResidualObservation(
        IPhd2Client client, Phd2ExactLockPositionResult exact,
        long connectionEpoch, long guideEpoch, double lockTolerancePixels,
        bool supervised, CancellationToken cancellationToken, bool measuredContinuation = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((!supervised && !measuredContinuation) || !exact.Exact || exact.RegistryProfileMutated ||
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
        return proof with { YieldedToFreshResiduals = true, MeasuredContinuation = measuredContinuation };
    }


    private static double Distance(Phd2Point a, Phd2Point b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
