using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

// Return arrival is a positional receipt, not native SettleDone or slit arrival.
// No motion/guide/loop command is available through this read-only operation.
internal static class Phd2MeasuredReturnWindow
{
    internal static async Task<IReadOnlyList<Phd2GuidingFrameResult>> ObserveAsync(
        IPhd2Client client, long connectionEpoch, long guideEpoch, Phd2Point verifiedLock,
        double lockTolerance, double arrivalTolerance, DateTimeOffset verifiedUtc,
        DateTimeOffset deadline, Func<string> reservePath, TimeSpan frameTimeout, CancellationToken token)
    {
        if (!double.IsFinite(arrivalTolerance) || arrivalTolerance <= 0 || deadline <= verifiedUtc)
            throw new Phd2Exception("Invalid measured return bounds.");
        var baseline = new Phd2PostLockGuidingObservation("return-" + Guid.NewGuid().ToString("N"),
            connectionEpoch, guideEpoch, client.Snapshot.EventSequence, verifiedLock, lockTolerance, verifiedUtc);
        if (!baseline.IsCurrent(client.Snapshot)) throw new Phd2Exception("Return guide identity changed.");
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) throw new Phd2Exception("Original return deadline exhausted.");
        bounded.CancelAfter(remaining);
        var accepted = new List<Phd2GuidingFrameResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sequence = baseline.AfterEventSequence;
        long lastFrame = -1;
        // A count fence also bounds work if the system wall clock changes.
        for (var i = 0; i < 16; i++)
        {
            bounded.Token.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline || !baseline.IsCurrent(client.Snapshot))
                throw new Phd2Exception("Return deadline or guide continuity changed.");
            var frame = await client.SaveCurrentGuidingFrameAsync(new(reservePath(), frameTimeout), bounded.Token).ConfigureAwait(false);
            if (!baseline.IsCurrent(client.Snapshot) || frame.EventSequence <= sequence || frame.TriggerGuideFrame <= lastFrame ||
                frame.GuideStepUtc < verifiedUtc || frame.CompletedUtc < frame.GuideStepUtc ||
                frame.CompletedUtc > DateTimeOffset.UtcNow || frame.CompletedUtc > deadline ||
                frame.Sha256 is not { Length: 64 } || !frame.Sha256.All(Uri.IsHexDigit) || !seen.Add(frame.Sha256) ||
                frame.GuidingWasInterrupted || frame.ExposureChanged || frame.CaptureLoopStarted ||
                frame.NativeLockPosition is not { } actual || Distance(actual, verifiedLock) > lockTolerance ||
                frame.NativeMeasuredGuidePosition is not { } measured)
                throw new Phd2Exception("Return has no fresh same-frame measured guide position/provenance.");
            sequence = frame.EventSequence;
            lastFrame = frame.TriggerGuideFrame;
            if (Distance(measured, verifiedLock) > arrivalTolerance) accepted.Clear();
            else accepted.Add(frame);
            if (accepted.Count == 3)
            {
                baseline.AcceptResiduals(accepted, client.Snapshot);
                return accepted;
            }
        }
        throw new Phd2Exception("Measured physical return position was not confirmed in the original bounded window.");
    }

    private static double Distance(Phd2Point a, Phd2Point b) => Math.Sqrt(Math.Pow(a.X-b.X, 2) + Math.Pow(a.Y-b.Y, 2));
}
