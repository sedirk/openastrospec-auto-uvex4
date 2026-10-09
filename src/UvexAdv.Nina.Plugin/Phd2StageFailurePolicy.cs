using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Classifies typed owner failures, never localized alert/message fragments.
/// A timeout proves missing evidence, not cloud, faintness or a defective vendor.
/// Classification itself authorizes neither capture replay nor equipment restart.
/// </summary>
internal static class Phd2StageFailurePolicy
{
    internal const string FrameTimeout = "PHD2_GUIDING_FRAME_TIMEOUT";
    internal const string StatusTimeout = "PHD2_OWNER_STATUS_TIMEOUT";
    internal const string NativeFrameTimeout = "PHD2_NATIVE_FRAME_TIMEOUT";
    internal const string PlacementStarLost = "PHD2_PLACEMENT_GUIDE_STAR_LOST";

    // Inspect the captured failure, not a later mutable owner snapshot. Native
    // guiding can recover between StarLost and the UI rendering the failure.
    internal static Phd2GuidingFrameLostException? FindLostFrame(Exception failure)
    {
        for (Exception? current = failure; current is not null; current = current.InnerException)
            if (current is Phd2GuidingFrameLostException lost) return lost;
        return null;
    }

    internal static string? CodeFor(Exception failure) => failure switch
    {
        Phd2GuidingFrameLostException => "GUIDING_LOST",
        Phd2GuideOutputException => Phd2GuideOutputStatus.FailureCode,
        Phd2CommandTimeoutException { Operation: "fresh guiding-frame evidence" } => FrameTimeout,
        Phd2CommandTimeoutException { Operation: "get_app_state" } => StatusTimeout,
        Phd2CommandTimeoutException { Operation: "native single-frame capture" } => NativeFrameTimeout,
        _ => null,
    };
}
