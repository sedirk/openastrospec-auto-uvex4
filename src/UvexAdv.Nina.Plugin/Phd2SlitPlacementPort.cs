using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed record Phd2SlitConvergenceRequest(
    Phd2SlitPlacementSession Session, Phd2LockShiftLedger Ledger,
    IReadOnlyList<Phd2GuidingResidualState> InitialWindow,
    Phd2SlitPlacementCommissioning Preset, Phd2IdentityValidation Identity,
    string PendingPath, IReadOnlyDictionary<string, string> CommissioningMetadata,
    int PostCalibrationReacquisitionDepth, int LostLockReacquisitionDepth,
    bool AllowCoarseReacquisition = true);

// Fine placement has no ATR, roof, raw camera or mount slew surface. Recovery
// remains with the run supervisor; returning a denied plan never creates a new
// acquisition budget. Published session/field snapshots are immutable witnesses
// for the next stage, not permission to skip its fresh pre-exposure checks.
internal interface IPhd2SlitPlacementPort
{
    void ThrowIfGuideOutputFailed();
    Phd2StateSnapshot GuideSnapshot { get; }
    Phd2LockShiftPendingState? PendingMotion { get; }
    bool GuidePrecisionWarningActive { get; }
    bool TryConsumePrimaryStarSettle(string lineage, int chargedStage);
    Phd2LockShiftSafetySnapshot BuildPhd2LockShiftSafetySnapshot(ObservationContext context,
        Phd2SlitPlacementCommissioning preset, string pierSide);
    Task<StageResult> ReturnPhd2LockToOriginAsync(ObservationContext context, Phd2SlitPlacementSession session,
        Phd2LockShiftPendingState state, string reason, CancellationToken token,
        IReadOnlyDictionary<string, double>? completionFailureMetrics = null);
    Task<IReadOnlyList<Phd2GuidingResidualState>> CapturePhd2PlacementGuideWindowAsync(ObservationContext context,
        Phd2SlitPlacementCommissioning preset, Phd2SensorTopology topology, Phd2Point currentLock,
        Phd2Point expectedTarget, SlitGeometry runtimeSlitLocal, Phd2SlitGuideMode guideMode,
        int count, DateTimeOffset deadlineUtc, CancellationToken token,
        IReadOnlyList<Phd2GuidingResidualState>? previousWindow = null, Phd2Point? referenceGuide = null);
    void RecordSession(Phd2SlitPlacementSession? session);
    void RecordPendingMotion(Phd2LockShiftPendingState? pending);
    void PromoteMeasurement(Phd2GuidingResidualState measurement, Phd2SlitPlacementCommissioning preset);
    void ClearField();
    Task<StageResult> HandleDeniedPhd2AcquisitionBudgetAsync(ObservationContext context, Phd2SlitPlacementSession session,
        Phd2SlitPlacementCommissioning preset, Phd2LockShiftAcquisitionBudget budget,
        int postCalibrationReacquisitionDepth, int lostLockReacquisitionDepth, CancellationToken token);
    Phd2LockShiftPendingState CreatePhd2PendingState(ObservationContext context, Phd2SlitPlacementCommissioning preset,
        Phd2SlitPlacementSession session, Phd2LockShiftLedger ledger, Phd2Point requested,
        Phd2LockShiftPendingPhase phase, string? intentEvidencePath, string reason);
    Task PersistPendingAsync(string path, Phd2LockShiftPendingState pending, CancellationToken token);
    Task<GateResult> ValidateCurrentFieldBindingAsync(ObservationContext context, CancellationToken token);
    Task<GateResult> EnsurePhdStoppedForAutomaticRebuildAsync(ObservationStage stage, string reason, CancellationToken token);
    Task RequireImmediatePhysicalActionGatesAsync(ObservationContext context, CancellationToken token);
    Task<Phd2ExactLockPositionResult> SetExactLockPositionAsync(Phd2ExactLockPositionRequest request, CancellationToken token);
    Task<Phd2SettleResult> ResettleAtCurrentLockAsync(Phd2SameLockSettleRequest request, CancellationToken token);
    Task<Phd2CalibrationValidation> ValidateCalibrationAsync(Phd2SlitPlacementCommissioning preset, CancellationToken token);
    Phd2PostLockGuidingObservation BeginFreshResidualObservation(Phd2ExactLockPositionResult exact,
        Phd2SlitPlacementSession session, double tolerance, bool supervised, CancellationToken token);
    Phd2CalibrationCandidateSelection SelectPhd2CalibrationQuality(Phd2CalibrationValidation calibration,
        Phd2SlitPlacementCommissioning preset, Phd2CalibrationEvaluationPhase phase,
        Phd2CalibrationSettleEvidence? settle, Phd2CalibrationResidualEvidence? residual,
        Phd2CalibrationSelectionPurpose purpose);
    Phd2CalibrationSettleEvidence CreateCalibrationSettleEvidence(Phd2SettleResult settle,
        Phd2StateSnapshot snapshot, bool freshGuidingWindowAccepted, int freshGuidingSampleCount);
    Phd2LockShiftQualification BuildPhd2LockShiftQualification(Phd2IdentityValidation identity,
        Phd2CalibrationValidation calibration, Phd2SensorTopology topology,
        Phd2SlitPlacementCommissioning preset, Phd2CalibrationQualityAssessment quality, string pierSide);
    Dictionary<string, double> Phd2EffectiveQualityMetrics(Phd2CalibrationQualityAssessment quality,
        Phd2SlitGuideMode guideMode, Phd2Point selectedGuide, Phd2SettleResult settle, double residual);
    Task<string> PublishRunJsonEvidenceAsync(string kind, string caption, object payload,
        string? frame, CancellationToken token);
    void Report(string message, double value = -1);
}
