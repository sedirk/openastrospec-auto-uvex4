namespace UvexAdv.Observatory;

public sealed record G3SparseGraphWindowDecision(bool Usable, bool StartIndependentComponent,
    G3SparseTimingAssessment Timing);

/// <summary>
/// A recent real pixel pair may start a new graph when an unrelated old branch
/// exceeds the unchanged realtime window. This does not carry an old inferred
/// anchor across the boundary or declare registration; the caller must measure
/// and validate that direct pair before rebasing its coordinate origin.
/// </summary>
public static class G3SparseGraphWindowPolicy
{
    public static G3SparseGraphWindowDecision Evaluate(G3SparseExposureTiming current,
        G3SparseExposureTiming candidate, IReadOnlyList<G3SparseExposureTiming> component,
        DateTimeOffset now, double recognitionRadiusPixels)
    {
        var whole = G3SparseFrameTimingPolicy.Evaluate(current, now,
            G3SparseTimingPurpose.RegisteredCurrentFrame, 0, recognitionRadiusPixels, component);
        if (whole.Usable) return new(true, false, whole);
        if (whole.Gate.Code is not ("G3_SPARSE_TIMING_SPAN_EXCEEDED" or "G3_SPARSE_TIMING_GAP_EXCEEDED"))
            return new(false, false, whole);
        var pair = G3SparseFrameTimingPolicy.Evaluate(current, now,
            G3SparseTimingPurpose.RegisteredCurrentFrame, 0, recognitionRadiusPixels, [candidate]);
        return new(pair.Usable, pair.Usable, pair);
    }
}
