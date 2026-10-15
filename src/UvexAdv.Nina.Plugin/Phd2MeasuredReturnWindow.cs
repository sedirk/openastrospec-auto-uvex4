using UvexAdv.Phd2;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

// Return arrival is a positional receipt, not native SettleDone or slit arrival.
// No motion/guide/loop command is available through this read-only operation.
internal static class Phd2MeasuredReturnWindow
{
    internal static async Task<IReadOnlyList<Phd2GuidingFrameResult>> ObserveAsync(
        IPhd2Client client, long connectionEpoch, long guideEpoch, Phd2Point verifiedLock,
        double lockTolerance, double arrivalTolerance, DateTimeOffset verifiedUtc,
        DateTimeOffset deadline, Func<string> reservePath, TimeSpan frameTimeout, CancellationToken token,
        Func<Phd2GuidingFrameResult, CancellationToken, Task<Phd2Point?>> readPrimaryPosition)
    {
        if (!double.IsFinite(arrivalTolerance) || arrivalTolerance <= 0 ||
            !double.IsFinite(lockTolerance) || lockTolerance <= 0)
            throw new PhysicalActionGateException(GateResult.Unknown("PHD2_RETURN_WINDOW_BOUNDS_INVALID", "Invalid measured return bounds."));
        if (deadline <= verifiedUtc) throw NotArrived();
        var baseline = new Phd2PostLockGuidingObservation("return-" + Guid.NewGuid().ToString("N"),
            connectionEpoch, guideEpoch, client.Snapshot.EventSequence, verifiedLock, lockTolerance, verifiedUtc);
        if (!baseline.IsCurrent(client.Snapshot)) throw ContinuityFailure(client.Snapshot, connectionEpoch, guideEpoch);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) throw NotArrived();
        bounded.CancelAfter(remaining);
        var accepted = new List<Phd2GuidingFrameResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sequence = baseline.AfterEventSequence;
        long lastFrame = -1;
        // A count fence also bounds work if the system wall clock changes.
        for (var i = 0; i < 16; i++)
        {
            token.ThrowIfCancellationRequested();
            if (!baseline.IsCurrent(client.Snapshot)) throw ContinuityFailure(client.Snapshot, connectionEpoch, guideEpoch);
            if (DateTimeOffset.UtcNow >= deadline || bounded.IsCancellationRequested) throw NotArrived();
            Phd2GuidingFrameResult frame;
            try { frame = await client.SaveCurrentGuidingFrameAsync(new(reservePath(), frameTimeout), bounded.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && bounded.IsCancellationRequested) { throw NotArrived(); }
            if (!baseline.IsCurrent(client.Snapshot)) throw ContinuityFailure(client.Snapshot, connectionEpoch, guideEpoch);
            if (frame.EventSequence <= sequence || frame.TriggerGuideFrame <= lastFrame ||
                frame.GuideStepUtc < verifiedUtc || frame.CompletedUtc < frame.GuideStepUtc ||
                frame.CompletedUtc > DateTimeOffset.UtcNow || frame.CompletedUtc > deadline ||
                frame.Sha256 is not { Length: 64 } || !frame.Sha256.All(Uri.IsHexDigit) || !seen.Add(frame.Sha256) ||
                frame.GuidingWasInterrupted || frame.ExposureChanged || frame.CaptureLoopStarted ||
                frame.NativeLockPosition is not { } actual || Distance(actual, verifiedLock) > lockTolerance)
                throw new PhysicalActionGateException(GateResult.Unknown("PHD2_RETURN_FRAME_PROVENANCE_INVALID", "Return has no fresh same-frame measured guide position/provenance."));
            sequence = frame.EventSequence;
            lastFrame = frame.TriggerGuideFrame;
            Phd2Point? measured;
            try { measured = await readPrimaryPosition(frame, bounded.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && bounded.IsCancellationRequested) { throw NotArrived(); }
            if (!baseline.IsCurrent(client.Snapshot)) throw ContinuityFailure(client.Snapshot, connectionEpoch, guideEpoch);
            if (DateTimeOffset.UtcNow >= deadline || bounded.IsCancellationRequested) throw NotArrived();
            if (measured is null)
            {
                // A frame/crop race or ambiguous crop is an optical rejection,
                // not an owner failure and never proof from averaged dx/dy.
                accepted.Clear();
                continue;
            }
            if (Distance(measured, verifiedLock) > arrivalTolerance) accepted.Clear();
            else accepted.Add(frame);
            if (accepted.Count == 3)
            {
                baseline.AcceptResiduals(accepted, client.Snapshot);
                return accepted;
            }
        }
        throw NotArrived();
    }

    private static PhysicalActionGateException ContinuityFailure(Phd2StateSnapshot state, long connectionEpoch, long guideEpoch) => new(
        state.IsConnected && state.ConnectionEpoch == connectionEpoch && state.GuideEpoch == guideEpoch &&
        !state.AutomationPaused && !state.Phd2Paused && state.GuideOutput?.Failed != true &&
        state.PendingSettleOperationId is null && state.AppState == Phd2AppState.LostLock
            ? GateResult.TargetQuality("PHD2_PLACEMENT_GUIDE_STAR_LOST", "Guide star lost within the original return session; no arrival proven. Retain debt until checked-stop retirement.")
            : GateResult.Unknown("PHD2_RETURN_OWNER_CHANGED", "Return guide owner/continuity changed; not a normal positional timeout."));
    private static PhysicalActionGateException NotArrived() => new(GateResult.TargetQuality(
        "PHD2_LOCK_RETURN_POSITION_UNCONFIRMED", "Measured physical return was not confirmed in the original bounded window; retain debt until checked-stop retirement."));

    private static double Distance(Phd2Point a, Phd2Point b) => Math.Sqrt(Math.Pow(a.X-b.X, 2) + Math.Pow(a.Y-b.Y, 2));
}
