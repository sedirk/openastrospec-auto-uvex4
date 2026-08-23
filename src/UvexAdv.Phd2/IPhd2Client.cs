namespace UvexAdv.Phd2;

public interface IPhd2Client : IAsyncDisposable
{
    event EventHandler<Phd2EventMessage>? EventReceived;

    event EventHandler<Phd2StateSnapshot>? SnapshotChanged;

    bool IsConnected { get; }

    bool IsAutomationPaused { get; }

    Phd2StateSnapshot Snapshot { get; }

    Task ConnectAsync(CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);

    void PauseAutomation();

    void ResumeAutomation();

    Task<Phd2Profile> GetProfileAsync(CancellationToken cancellationToken);

    Task<Phd2Equipment> GetCurrentEquipmentAsync(CancellationToken cancellationToken);

    Task<Phd2IdentityValidation> ValidateIdentityAsync(
        Phd2IdentityRequirement requirement,
        CancellationToken cancellationToken);

    Task EnsureIdentityAsync(Phd2IdentityRequirement requirement, CancellationToken cancellationToken);

    Task<Phd2CalibrationData> GetCalibrationDataAsync(CancellationToken cancellationToken);

    Task<Phd2CalibrationValidation> ValidateCalibrationAsync(
        Phd2CalibrationRequirement requirement,
        CancellationToken cancellationToken);

    Task EnsureCalibrationSaneAsync(
        Phd2CalibrationRequirement requirement,
        CancellationToken cancellationToken);

    Task<Phd2SingleFrameResult> CaptureFullFrameAsync(
        Phd2SingleFrameRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// While an existing PHD2 full-frame loop is already running, waits for
    /// the next fresh LoopingExposures event and saves that completed frame.
    /// It never changes exposure, starts a loop, stops capture, or retries.
    /// </summary>
    Task<Phd2SingleFrameResult> SaveNextLoopingFrameAsync(
        Phd2SingleFrameRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// From a fresh Stopped/Selected state, sends one normal PHD2 loop command,
    /// waits for a fresh LoopingExposures frame, and deliberately leaves the
    /// loop running for non-exact guide-star selection and guide takeover.
    /// It never sends stop_capture or changes exposure settings.
    /// </summary>
    Task<Phd2LoopingStartResult> StartLoopingAndWaitForFreshFrameAsync(
        Phd2LoopingStartRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// In Stopped/Selected state, sends one set_exposure for an explicitly
    /// commissioned duration and verifies it through a fresh get_exposure.
    /// It does not start or stop capture and never retries an ambiguous set.
    /// </summary>
    Task<Phd2ExposureSelectionResult> SetExposureAndVerifyAsync(
        int exposureMilliseconds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Saves the first PHD2 image associated with a fresh GuideStep while the
    /// existing Guiding session remains in control. Exposure, looping, capture,
    /// and guiding state are not changed.
    /// </summary>
    Task<Phd2GuidingFrameResult> SaveCurrentGuidingFrameAsync(
        Phd2GuidingFrameRequest request,
        CancellationToken cancellationToken);

    Task<Phd2Point> SelectGuideStarAsync(
        Phd2Point approximatePosition,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads PHD2's current runtime lock position. This is a read-only JSON-RPC
    /// operation and does not select a star or mutate the PHD2 profile.
    /// </summary>
    Task<Phd2Point?> GetLockPositionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Applies one explicitly bounded runtime lock-position stage with
    /// <c>exact=true</c>, then verifies the value with a fresh
    /// <c>get_lock_position</c>. The method never retries an ambiguous set.
    /// </summary>
    Task<Phd2ExactLockPositionResult> SetExactLockPositionAsync(
        Phd2ExactLockPositionRequest request,
        CancellationToken cancellationToken);

    Task<Phd2SettleResult> GuideAndSettleAsync(
        Phd2SettleCriteria criteria,
        bool forceRecalibration,
        CancellationToken cancellationToken);

    /// <summary>
    /// Starts or re-settles guiding while restricting PHD2's fallback star
    /// search to an already morphology-qualified detector region. Callers
    /// starting a new guide epoch in a bright-target field must provide the
    /// same-frame candidate ROI instead of allowing full-frame auto-selection.
    /// </summary>
    Task<Phd2SettleResult> GuideAndSettleAsync(
        Phd2SettleCriteria criteria,
        bool forceRecalibration,
        Phd2Rectangle? selectionRoi,
        CancellationToken cancellationToken);

    Task<Phd2StopCaptureResult> StopCaptureAndConfirmAsync(CancellationToken cancellationToken);

    Task<Phd2StopCaptureResult> PauseAutomationAndStopCaptureAsync(CancellationToken cancellationToken);

    Task StopGuidingAsync(CancellationToken cancellationToken);
}
