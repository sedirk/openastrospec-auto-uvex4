using System.IO;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

// Immutable catalogue ancestry for the guide-camera handoff. It deliberately
// has no WCS, current centroid, expiry setter, or motion authority.
internal sealed record G3SparseGuideAncestry(G3FieldMountBinding Selection, long ConnectionEpoch,
    IReadOnlyDictionary<string, string> Parents)
{
    internal static bool IsRegisteredPair(G3FieldState seed) =>
        seed.Solve is { Authority: "RegisteredNeighbourWcs" or "RegisteredMosaicWcs" } &&
        seed.SparseMeasuredContinuity is not null;

    internal static GateResult SelectionFrameGate(G3MeasuredTargetSource shortFrame, G3FieldMountBinding selection,
        DateTimeOffset? selectionRequestedUtc, long epoch, DateTimeOffset pairExpiry, DateTimeOffset now,
        double maximumMountSeparationArcseconds)
    {
        var bound = shortFrame.Binding;
        if (shortFrame.ConnectionEpoch != epoch)
            return GateResult.Unknown("G3_SPARSE_GUIDE_CONTEXT_CHANGED", "The short pair and guide frame do not share the same observing context.");
        var pointing = G3FieldMountBindingPolicy.ValidateForMotion(bound, selection.ObservationRunId,
            selection.ActionConfigurationSha256, selection.CommissioningPresetSha256, shortFrame.Path, shortFrame.Sha,
            selection.RightAscensionDegrees, selection.DeclinationDegrees, selection.CoordinateEpoch, selection.PierSide,
            maximumMountSeparationArcseconds);
        if (pointing.Disposition != GateDisposition.Passed) return pointing;
        if (now > pairExpiry || selection.FrameCompletedUtc > now || selectionRequestedUtc is not { } start ||
            start < shortFrame.Timing.CaptureCompletedUtc || start >= selection.FrameCompletedUtc ||
            string.Equals(selection.FramePath, shortFrame.Path, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(selection.FrameSha256, shortFrame.Sha, StringComparison.OrdinalIgnoreCase))
            return GateResult.TargetQuality("G3_SPARSE_GUIDE_SOURCE_UNCONFIRMED", "No independent, timely successor to the measured short pair.");
        return GateResult.Pass("G3_SPARSE_GUIDE_SUCCESSOR", "Distinct, later image inside the original short-pair interval.");
    }

    internal static TargetIdentification Corroborate(MonochromeFrame frame, IReadOnlyList<StarCandidate> stars,
        PixelPoint shortCentroid, PixelPoint searchHint, double maximumSeparation, double searchRadius,
        double minimumSnr, double minimumUniqueness)
    {
        // Search the full commissioned region so a neighbouring core/ring is
        // not made artificially unique by cropping. Then bound the new source
        // against BOTH the independently measured short pair and the search hint.
        var measured = SlitTargetIdentifier.Identify(frame, stars, searchHint, searchRadius, minimumSnr, minimumUniqueness);
        static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(Math.Pow(a.X-b.X,2) + Math.Pow(a.Y-b.Y,2));
        if (double.IsFinite(maximumSeparation) && maximumSeparation > 0 && maximumSeparation <= searchRadius &&
            measured.Gate.Disposition == GateDisposition.Passed && measured.Target is { FluxAdu: > 0 } source &&
            Distance(source.Centroid, shortCentroid) <= maximumSeparation &&
            Distance(source.Centroid, searchHint) <= maximumSeparation)
            return measured;
        return measured with { Target = null, Gate = GateResult.TargetQuality("G3_SPARSE_GUIDE_SOURCE_UNCONFIRMED",
            $"New guide pixels did not corroborate the measured short pair ({measured.Gate.Code}); no guide position or motion authorized.") };
    }

    // Only called after both original and new physical bindings and the complete
    // measured pair passed on the live clock. Never manufacture a longer TTL.
    internal static G3FieldState Transfer(G3FieldState seed, G3FieldState guide, long connectionEpoch,
        string receipt, string receiptHash)
    {
        var pair = seed.SparseMeasuredContinuity!;
        var selection = guide.MountBinding!;
        var parents = new Dictionary<string, string>(seed.Solve!.InferenceSources, StringComparer.OrdinalIgnoreCase)
        {
            [pair.ConfirmationPath] = pair.ConfirmationSha256, [pair.FirstFramePath] = pair.FirstFrameSha256,
            [pair.CurrentFramePath] = pair.CurrentFrameSha256, [selection.FramePath] = selection.FrameSha256,
            [receipt] = receiptHash
        };
        return guide with { Solve = null, SparseMeasuredContinuity = null, SparseInferenceConfirmed = false,
            TargetTrackSource = null, NeighbourCatalogIdentityEvidencePath = receipt,
            SparseGuideAncestry = new(selection, connectionEpoch, parents) };
    }

    internal async Task<GateResult> VerifyAsync(G3FieldMountBinding? current, long epoch,
        Func<string, CancellationToken, Task<string>> hash, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (current is null || epoch != ConnectionEpoch || current.ObservationRunId != Selection.ObservationRunId ||
            current.PierSide != Selection.PierSide || current.CoordinateEpoch != Selection.CoordinateEpoch ||
            current.ActionConfigurationSha256 != Selection.ActionConfigurationSha256 ||
            current.CommissioningPresetSha256 != Selection.CommissioningPresetSha256)
            return GateResult.Unknown("G3_SPARSE_GUIDE_CONTEXT_CHANGED", "Guide ancestry belongs to another owner, configuration, side or run.");
        foreach (var (path, expected) in Parents)
        {
            token.ThrowIfCancellationRequested();
            try { if (string.Equals(await hash(path, token).ConfigureAwait(false), expected, StringComparison.OrdinalIgnoreCase)) continue; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return GateResult.Unknown("G3_SPARSE_SOURCE_CHANGED", "An immutable handoff parent changed or is unavailable.");
        }
        token.ThrowIfCancellationRequested();
        return GateResult.Pass("G3_SPARSE_GUIDE_ANCESTRY_VERIFIED", "Historical association only; current frame, mount and guide gates still apply.");
    }
}
