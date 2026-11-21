namespace UvexAdv.Observatory;

/// <summary>A mount arrival is not a catalogue identity measurement.</summary>
public static class G3PostWcsIdentityPolicy
{
    // Even the full detector diagonal is shorter than a field-to-field jump
    // like the 10 Cas arrival. There can be no old sky pixels to transport.
    // A repeated new peak near the encoder prediction can be another star.
    // Retain the fast path for potential overlap; its existing evidence and
    // budget checks still apply. This predicate never grants identity or motion.
    public static bool RequiresIndependentArrivalAstrometry(
        double traveledArcseconds, int width, int height, double sourceArcsecondsPerPixel) =>
        !double.IsFinite(traveledArcseconds) || traveledArcseconds < 0 ||
        width <= 0 || height <= 0 ||
        !double.IsFinite(sourceArcsecondsPerPixel) || sourceArcsecondsPerPixel <= 0 ||
        traveledArcseconds >= Math.Sqrt((double)width * width + (double)height * height) * sourceArcsecondsPerPixel;
}
