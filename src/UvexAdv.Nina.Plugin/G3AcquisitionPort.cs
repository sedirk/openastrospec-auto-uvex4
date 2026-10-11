using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

// Existing commissioned acquisition mechanisms; no roof/ATR or raw-device route.
// The workflow chooses the branch, while these operations retain the original
// shared durable motion ledger, immutable images and measured-handoff checks.
internal interface IG3AcquisitionPort
{
    Task<GateResult> EvaluateInterlocksAsync(ObservationContext context, CancellationToken token);
    void EnsureFineAcquisitionClockStarted();
    Task<StageResult?> CompletePendingG3SearchReturnAsync(ObservationContext context, CancellationToken token);
    Task<GateResult> ReturnAfterOwnedGuidingBeforeReacquisitionAsync(ObservationContext context, CancellationToken token);
    Task<string> EnsureWideToSlitTransferSkippedEvidenceAsync(ObservationContext context, CancellationToken token);
    bool UsesCatalogPositionOnly(ObservationContext context);
    TargetAcquisitionBranch? ActiveTargetBranch { get; }
    Phd2SlitPlacementCommissioning? PlacementPreset { get; }
    Task PublishTargetBranchAsync(ObservationContext context, TargetAcquisitionBranch branch, string code,
        string message, string? frame, CancellationToken token);
    Task<G3FieldState> CaptureAndAnalyzeG3WithSolveLadderAsync(ObservationContext context, CancellationToken token);
    void RememberField(G3FieldState field);
    double HandoffResidualPixels(G3FieldState field, Phd2SlitPlacementCommissioning preset);
    StageResult AcceptField(G3FieldState field, string transferEvidencePath);
    Task<StageResult> RunG3WcsCenteringAsync(ObservationContext context, G3FieldState field, string transferEvidencePath,
        CancellationToken token, bool allowChargedCurrentPositionHandoff);
    Task<StageResult> RunBoundedG3LocalSearchAsync(ObservationContext context, G3FieldState field, string transferEvidencePath,
        CancellationToken token, bool allowChargedCurrentPositionHandoff);
}
