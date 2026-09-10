using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed record Phd2SaturatedGuideExclusion(
    Phd2Rectangle Region, PixelPoint Center, string Topology, double MeasuredRadiusPixels);

/// <summary>
/// Candidate-level geometry only. A native candidate on the measured extent of
/// a clipped extended feature is reselected by PHD2, not replaced by our star
/// detector. An indeterminate clipped feature is not asserted to be a ghost.
/// </summary>
internal static class Phd2NativeGuideSaturatedRegions
{
    internal static IReadOnlyList<Phd2SaturatedGuideExclusion> Measure(
        MonochromeFrame frame, GuideStarSelectionPolicy policy)
    {
        // Target recognition uses a small catalogue-bound search radius. Guide
        // exclusions must cover the whole detector: reflected rings can be
        // hundreds of pixels away, outside that target-recognition radius.
        var topology = SaturatedTargetGhostTopologyAnalyzer.Analyze(
            frame, new PixelPoint(frame.Width / 2d, frame.Height / 2d),
            Math.Sqrt((double)frame.Width * frame.Width + (double)frame.Height * frame.Height));
        return topology.Candidates
            .Where(c => c.Topology == SaturatedSourceTopology.AnnularGhost ||
                Math.Max(c.BoundingWidthPixels, c.BoundingHeightPixels) >= 2 * policy.MaximumFwhmPixels)
            .Select(c =>
            {
                var radius = c.ExclusionRadiusPixels + policy.MinimumEdgeDistancePixels;
                var left = Math.Max(0, (int)Math.Floor(c.Centroid.X - radius));
                var top = Math.Max(0, (int)Math.Floor(c.Centroid.Y - radius));
                var right = Math.Min(frame.Width, (int)Math.Ceiling(c.Centroid.X + radius) + 1);
                var bottom = Math.Min(frame.Height, (int)Math.Ceiling(c.Centroid.Y + radius) + 1);
                return new Phd2SaturatedGuideExclusion(new(left, top, right - left, bottom - top),
                    c.Centroid, c.Topology.ToString(), c.ExclusionRadiusPixels);
            }).ToArray();
    }

    internal static bool Contains(Phd2Rectangle region, PixelPoint point) =>
        point.X >= region.X && point.X < region.X + region.Width &&
        point.Y >= region.Y && point.Y < region.Y + region.Height;
}
