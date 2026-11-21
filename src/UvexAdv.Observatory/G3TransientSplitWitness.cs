namespace UvexAdv.Observatory;

/// <summary>One rejected split may be an intervening image, never a substitute position.</summary>
public static class G3TransientSplitWitness
{
    public static bool MayKeepUnsplitAnchor(G3ShortPositionMeasurement? anchor, G3ShortPositionMeasurement split,
        G3ShortPositionConfirmationDecision decision, bool sameSetting, bool ordinaryTarget)
    {
        return ordinaryTarget && sameSetting && decision.RetryAllowed && !decision.Accepted &&
            decision.Gate.Code == "G3_SHORT_REPEAT_DISAGREES" &&
            anchor?.Gate.Disposition == GateDisposition.Passed && G3SepShortPositionPolicy.IsUnsplitParent(anchor) &&
            split.Gate.Disposition == GateDisposition.Passed && G3SepShortPositionPolicy.IsSplitParent(split) &&
            split.Gate.Metrics?.TryGetValue("sepParentContinuityPixels", out var continuity) == true &&
            double.IsFinite(continuity) && continuity > G3ShortPositionMeasurementPolicy.MaximumRepeatSeparationPixels &&
            continuity <= G3ShortPositionMeasurementPolicy.MaximumTrackedRepeatSeparationPixels;
    }

    public static bool MayPairNext(G3ShortPositionMeasurement current, DateTimeOffset anchorUtc,
        DateTimeOffset currentUtc) =>
        anchorUtc != default && currentUtc > anchorUtc && currentUtc - anchorUtc <= TimeSpan.FromSeconds(8) &&
        current.Gate.Disposition == GateDisposition.Passed && G3SepShortPositionPolicy.IsUnsplitParent(current);
}
