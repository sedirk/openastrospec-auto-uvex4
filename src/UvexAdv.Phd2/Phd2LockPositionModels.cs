namespace UvexAdv.Phd2;

/// <summary>
/// One bounded exact runtime-lock-position mutation. The expected-current
/// precondition prevents a plan made from a stale G3/lock snapshot from being
/// dispatched after another actor or guide event changed the lock position.
/// </summary>
public sealed record Phd2ExactLockPositionRequest(
    Phd2Point ExpectedCurrentPosition,
    Phd2Point DesiredPosition,
    double MaximumExpectedCurrentErrorPixels,
    double MaximumStepPixels,
    double MaximumVerificationErrorPixels);

/// <summary>
/// Fresh before/after proof for an exact PHD2 runtime lock-position stage.
/// This is runtime state only; no registry/profile overlay is written.
/// </summary>
public sealed record Phd2ExactLockPositionResult(
    Phd2Point Before,
    Phd2Point Requested,
    Phd2Point Verified,
    double StepPixels,
    double VerificationErrorPixels,
    DateTimeOffset CompletedUtc,
    bool Exact,
    bool RegistryProfileMutated,
    bool AutomaticRetryAllowed,
    bool PhysicalGuideSettled,
    bool RequiresGuideAndSettle);

/// <summary>
/// Requests one immutable copy of a newly completed frame from an already
/// running PHD2 Guiding session. It deliberately contains no exposure, gain,
/// binning, loop, or stop parameter.
/// </summary>
public sealed record Phd2GuidingFrameRequest(
    string DestinationPath,
    TimeSpan FreshGuideStepTimeout);

/// <summary>
/// Binds immutable G3 evidence to the fresh GuideStep that preceded save_image.
/// The negative capability flags are part of the proof that this operation did
/// not seize PHD2's G3 acquisition state from the active guide session.
/// </summary>
public sealed record Phd2GuidingFrameResult(
    string Path,
    string Sha256,
    long TriggerGuideFrame,
    long EventSequence,
    DateTimeOffset GuideStepUtc,
    DateTimeOffset CompletedUtc,
    bool GuidingWasInterrupted,
    bool ExposureChanged,
    bool CaptureLoopStarted,
    bool AutomaticRetryAllowed);
