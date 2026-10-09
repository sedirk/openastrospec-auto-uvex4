using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>Reserve one new image pair before a marginal coarse correction.
/// This does not accept a position or change any motion/identity tolerance.</summary>
internal static class G3HandoffBoundaryRecheckPolicy
{
    public static bool ShouldRecheck(bool confirmed, bool slitMeasured, bool alreadyUsed,
        double residualPixels, double spreadPixels, double handoffRadiusPixels, int framesUsed) =>
        confirmed && slitMeasured && !alreadyUsed &&
        framesUsed >= 2 && framesUsed <= G3ShortExposurePolicy.AbsoluteMaximumFrames - 2 &&
        double.IsFinite(residualPixels) && residualPixels >= 0 &&
        double.IsFinite(spreadPixels) && spreadPixels > 0 &&
        double.IsFinite(handoffRadiusPixels) && handoffRadiusPixels > 0 &&
        residualPixels <= handoffRadiusPixels && residualPixels + spreadPixels > handoffRadiusPixels;
}
