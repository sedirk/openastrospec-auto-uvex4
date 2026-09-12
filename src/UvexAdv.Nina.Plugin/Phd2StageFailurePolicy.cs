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

    internal static string? CodeFor(Exception failure) => failure switch
    {
        Phd2GuideOutputException => Phd2GuideOutputStatus.FailureCode,
        Phd2CommandTimeoutException { Operation: "fresh guiding-frame evidence" } => FrameTimeout,
        Phd2CommandTimeoutException { Operation: "get_app_state" } => StatusTimeout,
        _ => null,
    };
}
