namespace UvexAdv.Phd2;

/// <summary>
/// Starts PHD2's normal idle full-frame exposure loop and waits for a newly
/// completed LoopingExposures event. This request deliberately has no exposure,
/// gain, binning, save, or stop parameter.
/// </summary>
public sealed record Phd2LoopingStartRequest(TimeSpan FreshFrameTimeout);

/// <summary>
/// Proof that one loop command reached a fresh frame and intentionally left
/// PHD2 in Looping so set_lock_position(exact=false) can perform the same
/// full-frame star selection as the normal interactive workflow.
/// </summary>
public sealed record Phd2LoopingStartResult(
    Phd2AppState InitialState,
    long Frame,
    long EventSequence,
    DateTimeOffset FrameUtc,
    DateTimeOffset CompletedUtc,
    long ConnectionEpoch,
    long GuideEpoch,
    bool LoopCommandSent,
    bool StopCommandSent,
    bool ExposureChanged,
    bool LeavesLoopingForGuideTakeover,
    bool AutomaticRetryAllowed);

/// <summary>
/// One explicitly commissioned idle-state PHD2 exposure selection.  The
/// mutation is sent once, read back through get_exposure, and never retried on
/// an ambiguous transport outcome.
/// </summary>
public sealed record Phd2ExposureSelectionResult(
    int RequestedExposureMilliseconds,
    int VerifiedExposureMilliseconds,
    Phd2AppState AppState,
    DateTimeOffset CompletedUtc,
    bool AutomaticRetryAllowed);
