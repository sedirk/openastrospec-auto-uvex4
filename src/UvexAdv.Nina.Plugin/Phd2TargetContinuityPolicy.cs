using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

// Search continuity, not a measured position or permission to move. The primary
// must come from a pixel-matched native crop in the same immutable fresh FITS.
internal static class Phd2TargetContinuityPolicy
{
    // A catalogue forecast may guide the first bounded search, but is not a
    // preceding measurement. Only after a real centroid exists does the tight
    // inter-frame continuity allowance apply; identity/search gates remain.
    internal static bool RejectMeasuredDiscontinuity(bool referenceTargetMeasured,
        PixelPoint measured, PixelPoint predicted, double allowance) =>
        referenceTargetMeasured && !IsContinuous(measured, predicted, allowance);

    internal static Phd2Point Predict(Phd2Point target, Phd2Point referenceGuide, Phd2Point freshGuide) =>
        new(target.X + freshGuide.X - referenceGuide.X, target.Y + freshGuide.Y - referenceGuide.Y);

    internal static bool IsContinuous(PixelPoint measured, PixelPoint predicted, double allowance) =>
        double.IsFinite(allowance) && allowance > 0 &&
        double.IsFinite(measured.X) && double.IsFinite(measured.Y) &&
        double.IsFinite(predicted.X) && double.IsFinite(predicted.Y) &&
        Math.Sqrt(Math.Pow(measured.X - predicted.X, 2) + Math.Pow(measured.Y - predicted.Y, 2)) <= allowance;
}
