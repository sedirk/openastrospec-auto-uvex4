using NINA.Image.Interfaces;
using NINA.Astrometry;
using UvexAdv.Phd2;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private sealed class Phd2SlitPlacementPort(RealObservationStageRunner run) : IPhd2SlitPlacementPort
    {
        public void ThrowIfGuideOutputFailed() => run.phd2.ThrowIfGuideOutputFailed();
        public Phd2StateSnapshot GuideSnapshot => run.phd2.Snapshot;
        public Phd2LockShiftPendingState? PendingMotion => run.pendingPhd2LockShift;
        public bool GuidePrecisionWarningActive => run.placementGuidePrecisionWarningActive;
        public bool TryConsumePrimaryStarSettle(string lineage, int chargedStage) =>
            run.GuideRecovery.TryConsumePrimaryStarSettle(lineage, chargedStage);
        public Phd2LockShiftSafetySnapshot BuildPhd2LockShiftSafetySnapshot(ObservationContext c,
            Phd2SlitPlacementCommissioning preset, string side) => run.BuildPhd2LockShiftSafetySnapshot(c, preset, side);
        public Task<StageResult> ReturnPhd2LockToOriginAsync(ObservationContext c, Phd2SlitPlacementSession session,
            Phd2LockShiftPendingState state, string reason, CancellationToken t,
            IReadOnlyDictionary<string, double>? completionFailureMetrics = null) =>
            run.ReturnPhd2LockToOriginAsync(c, session, state, reason, t, completionFailureMetrics);
        public Task<IReadOnlyList<Phd2GuidingResidualState>> CapturePhd2PlacementGuideWindowAsync(ObservationContext c,
            Phd2SlitPlacementCommissioning preset, Phd2SensorTopology topology, Phd2Point currentLock,
            Phd2Point expectedTarget, SlitGeometry slit, Phd2SlitGuideMode mode, int count, DateTimeOffset deadline,
            CancellationToken t, IReadOnlyList<Phd2GuidingResidualState>? previousWindow = null, Phd2Point? referenceGuide = null) =>
            run.CapturePhd2PlacementGuideWindowAsync(c, preset, topology, currentLock, expectedTarget, slit, mode, count, deadline, t, previousWindow, referenceGuide);
        public void RecordSession(Phd2SlitPlacementSession? session) => run.phd2SlitPlacementSession = session;
        public void RecordPendingMotion(Phd2LockShiftPendingState? state) => run.pendingPhd2LockShift = state;
        public void PromoteMeasurement(Phd2GuidingResidualState measurement, Phd2SlitPlacementCommissioning preset) =>
            run.lastG3Field = UpdateG3FieldFromGuidingResidual(run.lastG3Field!, measurement, preset);
        public void ClearField() => run.lastG3Field = null;
        public Task<StageResult> HandleDeniedPhd2AcquisitionBudgetAsync(ObservationContext c, Phd2SlitPlacementSession session,
            Phd2SlitPlacementCommissioning preset, Phd2LockShiftAcquisitionBudget budget,
            int calibrationDepth, int lostDepth, CancellationToken t) =>
            run.HandleDeniedPhd2AcquisitionBudgetAsync(c, session, preset, budget, calibrationDepth, lostDepth, t);
        public Phd2LockShiftPendingState CreatePhd2PendingState(ObservationContext c, Phd2SlitPlacementCommissioning preset,
            Phd2SlitPlacementSession session, Phd2LockShiftLedger ledger, Phd2Point requested,
            Phd2LockShiftPendingPhase phase, string? intentEvidencePath, string reason) =>
            run.CreatePhd2PendingState(c, preset, session, ledger, requested, phase, intentEvidencePath, reason);
        public Task PersistPendingAsync(string path, Phd2LockShiftPendingState pending, CancellationToken t) =>
            Phd2LockShiftPendingStore.WriteAtomicAsync(path, pending, t);
        public Task<GateResult> ValidateCurrentFieldBindingAsync(ObservationContext c, CancellationToken t) =>
            run.ValidateG3FieldMountBindingForMotionAsync(c, run.lastG3Field!, t);
        public Task<GateResult> EnsurePhdStoppedForAutomaticRebuildAsync(ObservationStage stage, string reason, CancellationToken t) =>
            run.EnsurePhdStoppedForAutomaticRebuildAsync(stage, reason, t);
        public Task RequireImmediatePhysicalActionGatesAsync(ObservationContext c, CancellationToken t) => run.RequireImmediatePhysicalActionGatesAsync(c, t);
        public Task<Phd2ExactLockPositionResult> SetExactLockPositionAsync(Phd2ExactLockPositionRequest request, CancellationToken t) =>
            run.phd2.SetExactLockPositionAsync(request, t);
        public Task<Phd2SettleResult> ResettleAtCurrentLockAsync(Phd2SameLockSettleRequest request, CancellationToken t) =>
            run.phd2.ResettleAtCurrentLockAsync(request, t);
        public Task<Phd2CalibrationValidation> ValidateCalibrationAsync(Phd2SlitPlacementCommissioning preset, CancellationToken t) =>
            run.phd2.ValidateCalibrationAsync(preset.CalibrationQualityPolicy.ApplyHardRejectionCeilings(run.PhdCalibrationRequirement()), t);
        public Phd2PostLockGuidingObservation BeginFreshResidualObservation(Phd2ExactLockPositionResult exact,
            Phd2SlitPlacementSession session, double tolerance, bool supervised, CancellationToken t) =>
            Phd2PostLockGuidingObservation.BeginFreshResidualObservation(run.phd2, exact, session.ConnectionEpoch,
                session.GuideEpoch, tolerance, supervised, t, measuredContinuation: true);
        public Phd2CalibrationCandidateSelection SelectPhd2CalibrationQuality(Phd2CalibrationValidation calibration,
            Phd2SlitPlacementCommissioning preset, Phd2CalibrationEvaluationPhase phase, Phd2CalibrationSettleEvidence? settle,
            Phd2CalibrationResidualEvidence? residual, Phd2CalibrationSelectionPurpose purpose) =>
            run.SelectPhd2CalibrationQuality(calibration, preset, phase, settle, residual, purpose);
        public Phd2CalibrationSettleEvidence CreateCalibrationSettleEvidence(Phd2SettleResult settle,
            Phd2StateSnapshot snapshot, bool fresh, int count) => run.CreateCalibrationSettleEvidence(settle, snapshot, fresh, count);
        public Phd2LockShiftQualification BuildPhd2LockShiftQualification(Phd2IdentityValidation identity,
            Phd2CalibrationValidation calibration, Phd2SensorTopology topology, Phd2SlitPlacementCommissioning preset,
            Phd2CalibrationQualityAssessment quality, string side) => run.BuildPhd2LockShiftQualification(identity, calibration, topology, preset, quality, side);
        public Dictionary<string, double> Phd2EffectiveQualityMetrics(Phd2CalibrationQualityAssessment quality,
            Phd2SlitGuideMode mode, Phd2Point selected, Phd2SettleResult settle, double residual) =>
            RealObservationStageRunner.Phd2EffectiveQualityMetrics(quality, mode, selected, settle, residual);
        public Task<string> PublishRunJsonEvidenceAsync(string kind, string caption, object payload, string? frame, CancellationToken t) =>
            run.PublishRunJsonEvidenceAsync(kind, caption, payload, frame, t);
        public void Report(string message, double value = -1) => run.Report(message, value);
    }

    private sealed class G3AcquisitionPort(RealObservationStageRunner run) : IG3AcquisitionPort
    {
        public Task<GateResult> EvaluateInterlocksAsync(ObservationContext c, CancellationToken t) => run.EvaluateInterlocksAsync(c, false, t);
        public void EnsureFineAcquisitionClockStarted() => run.fineAcquisitionStartedUtc ??= DateTimeOffset.UtcNow;
        public Task<StageResult?> CompletePendingG3SearchReturnAsync(ObservationContext c, CancellationToken t) => run.CompletePendingG3SearchReturnAsync(c, t);
        public Task<GateResult> ReturnAfterOwnedGuidingBeforeReacquisitionAsync(ObservationContext c, CancellationToken t) => run.ReturnAfterOwnedGuidingBeforeReacquisitionAsync(c, t);
        public Task<string> EnsureWideToSlitTransferSkippedEvidenceAsync(ObservationContext c, CancellationToken t) => run.EnsureWideToSlitTransferSkippedEvidenceAsync(c, t);
        public bool UsesCatalogPositionOnly(ObservationContext c) => ResolveTargetStrategy(c).UsesCatalogPositionOnly;
        public TargetAcquisitionBranch? ActiveTargetBranch => run.activeTargetBranch;
        public Phd2SlitPlacementCommissioning? PlacementPreset => run.commissioning?.Value.Phd2SlitPlacement;
        public Task PublishTargetBranchAsync(ObservationContext c, TargetAcquisitionBranch branch, string code,
            string message, string? frame, CancellationToken t) => run.PublishTargetBranchAsync(c, branch, code, message, frame, t);
        public Task<G3FieldState> CaptureAndAnalyzeG3WithSolveLadderAsync(ObservationContext c, CancellationToken t) => run.CaptureAndAnalyzeG3WithSolveLadderAsync(c, t);
        public void RememberField(G3FieldState field) => run.lastG3Field = field;
        public double HandoffResidualPixels(G3FieldState field, Phd2SlitPlacementCommissioning preset) => run.Phd2HandoffResidualPixels(field, preset);
        public StageResult AcceptField(G3FieldState field, string transfer) => run.G3FieldPassed(field, transfer, 0, null);
        public Task<StageResult> RunG3WcsCenteringAsync(ObservationContext c, G3FieldState field, string transfer,
            CancellationToken t, bool charged) => run.RunG3WcsCenteringAsync(c, field, transfer, t, charged);
        public Task<StageResult> RunBoundedG3LocalSearchAsync(ObservationContext c, G3FieldState field, string transfer,
            CancellationToken t, bool charged) => run.RunBoundedG3LocalSearchAsync(c, field, transfer, t, charged);
    }

    private sealed class G3AcquisitionReturnPort(RealObservationStageRunner run) : IG3AcquisitionReturnPort
    {
        public Phd2StateSnapshot GuideSnapshot => run.phd2.Snapshot;
        public Coordinates GetCurrentPosition() => run.telescopeMediator.GetCurrentPosition();
        public GateResult ValidateG3SearchMountState(string side) => run.ValidateG3SearchMountState(side);
        public GateResult ValidateG3DestinationPierSide(Coordinates c, string side) => run.ValidateG3DestinationPierSide(c, side);
        public GateResult ValidateCommandCoordinateHorizon(ObservationContext c, Coordinates commanded, string action) =>
            RealObservationStageRunner.ValidateCommandCoordinateHorizon(c, commanded, action);
        public Task RequireImmediatePhysicalActionGatesAsync(ObservationContext c, CancellationToken t) => run.RequireImmediatePhysicalActionGatesAsync(c, t);
        public Task PersistG3AcquisitionMotionAsync(G3AcquisitionMotionState state, CancellationToken t) => run.PersistG3AcquisitionMotionAsync(state, t);
        public Task<bool> SlewToCoordinatesAsync(Coordinates c, CancellationToken t) => run.telescopeMediator.SlewToCoordinatesAsync(c, t);
        public Task WaitForSlew(CancellationToken t) => run.telescopeMediator.WaitForSlew(t);
        public Task<G3PostSlewStabilityResult> WaitForG3PostSlewStabilityAsync(ObservationContext c, Coordinates command,
            Coordinates reported, string side, string epoch, string action, double tolerance, CancellationToken t) =>
            run.WaitForG3PostSlewStabilityAsync(c, command, reported, side, epoch, action, tolerance, t);
        public Task<string> PublishRunJsonEvidenceAsync(string kind, string caption, object payload, string? frame, CancellationToken t) =>
            run.PublishRunJsonEvidenceAsync(kind, caption, payload, frame, t);
        public void Report(string message, double value = -1) => run.Report(message, value);
    }

    // Adapters expose bounded operations, never the runner or a generic device
    // command surface, to independently testable workflow decision loops.
    private sealed class AtrObservationPort(RealObservationStageRunner run) : IAtrObservationPort
    {
        public GateResult ValidateAtrCameraIdentity(string id) => run.ValidateAtrCameraIdentity(id);
        public void ThrowIfGuideOutputFailed() => run.phd2.ThrowIfGuideOutputFailed();
        public bool IsGuidingStable() => run.IsGuidingStable();
        public Task<GateResult> WaitForAtrScienceTemperatureAsync(ObservationContext c, CancellationToken t) => run.WaitForAtrScienceTemperatureAsync(c, t);
        public Task<GateResult> EnsureOpticalCoverOpenAsync(ObservationContext c, CancellationToken t) => run.EnsureOpticalCoverOpenAsync(c, t);
        public GateResult? SequenceStopGate() => run.SequenceStopGate();
        public bool MeridianRequested => run.NativeMeridian?.Requested == true;
        public bool RequestNativeMeridianBoundary(ObservationContext c, double exposure) => run.RequestNativeMeridianBoundary(c, exposure);
        public StageResult MeridianSegmentBoundary() => run.MeridianSegmentBoundary();
        public GateResult ValidateEnvironment(ObservationPlan plan) => run.ValidateEnvironment(plan);
        public Task<GateResult> CheckPhotometryHealthAsync(ObservationContext c, CancellationToken t) => run.CheckPhotometryHealthAsync(c, t);
        public bool PhotometryUnavailable => run.configuration.Qhy.SynchronizedPhotometryEnabled && !run.qhyAvailableForRun;
        public bool PhotometryAvailable => run.qhyAvailableForRun;
        public Task RequireImmediatePhysicalActionGatesAsync(ObservationContext c, CancellationToken t) => run.RequireImmediatePhysicalActionGatesAsync(c, t);
        public Task<AtrCapture> CaptureAtrImageAsync(ObservationContext c, double exposure, string imageType, string title, CancellationToken t) =>
            run.CaptureAtrImageAsync(c, exposure, imageType, title, t);
        public Task<AtrSavedImage> SaveAtrImageAsync(AtrCapture capture, int attempt, bool accepted, GateDisposition disposition,
            string code, string message, CancellationToken t) => run.SaveAtrImageAsync(capture, attempt, accepted, disposition, code, message, t);
        public Task<string> ComputeFileSha256Async(string path, CancellationToken t) => RealObservationStageRunner.ComputeFileSha256Async(path, t);
        public void PublishScienceEvidence(string path, string sha, IReadOnlyDictionary<string, string> metadata) =>
            run.host.PublishEvidence("atr-science-fits", path, sha, metadata);
        public void PublishAtrPreview(IImageData image, string caption, double center, double halfWidth) => run.PublishAtrPreview(image, caption, center, halfWidth);
        public void PublishFrameCounters() => run.PublishFrameCounters();
        public void PublishAcquisitionProgress(ObservationContext c, string phase = "Ready") => run.PublishAcquisitionProgress(c, phase);
        public Task WriteAuditBestEffortAsync(string kind, object payload) => RealObservationStageRunner.WriteAuditBestEffortAsync(kind, payload);
        public void Report(string message, double value = -1) => run.Report(message, value);
    }
}
