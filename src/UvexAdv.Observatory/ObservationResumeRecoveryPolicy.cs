namespace UvexAdv.Observatory;

/// <summary>
/// Describes which acquisition proofs become stale when a cooperative pause or
/// manual takeover interrupts a stage. The plan deliberately restarts from the
/// earliest physical evidence required by the stage that will actually resume.
/// </summary>
public sealed record ObservationResumeRecoveryPlan(
    bool InvalidateQhySolution,
    bool InvalidateG3AndGuideEpoch,
    bool ReacquireQhy,
    bool ReacquireG3,
    bool ReplaceTargetOnSlit,
    bool RestartGuiding,
    bool RestorePhotometry)
{
    public bool RequiresPreStageRecovery =>
        ReacquireQhy || ReacquireG3 || ReplaceTargetOnSlit || RestartGuiding || RestorePhotometry;
}

public static class ObservationResumeRecoveryPolicy
{
    public static ObservationResumeRecoveryPlan ForStage(ObservationStage stage) => stage switch
    {
        ObservationStage.CoarseCenter => new(
            InvalidateQhySolution: true,
            InvalidateG3AndGuideEpoch: false,
            ReacquireQhy: true,
            ReacquireG3: false,
            ReplaceTargetOnSlit: false,
            RestartGuiding: false,
            RestorePhotometry: false),
        ObservationStage.AcquireG3SlitField => new(
            InvalidateQhySolution: false,
            InvalidateG3AndGuideEpoch: true,
            ReacquireQhy: false,
            ReacquireG3: false,
            ReplaceTargetOnSlit: false,
            RestartGuiding: false,
            RestorePhotometry: false),
        ObservationStage.PlaceTargetOnSlit => new(
            InvalidateQhySolution: false,
            InvalidateG3AndGuideEpoch: true,
            ReacquireQhy: false,
            ReacquireG3: true,
            ReplaceTargetOnSlit: false,
            RestartGuiding: false,
            RestorePhotometry: false),
        ObservationStage.StartGuiding => new(
            InvalidateQhySolution: false,
            InvalidateG3AndGuideEpoch: true,
            ReacquireQhy: false,
            ReacquireG3: true,
            ReplaceTargetOnSlit: true,
            RestartGuiding: false,
            RestorePhotometry: false),
        ObservationStage.StartQhyPhotometry or
        ObservationStage.SelectAtrExposure or
        ObservationStage.RunScienceBlock => new(
            InvalidateQhySolution: false,
            InvalidateG3AndGuideEpoch: true,
            ReacquireQhy: false,
            ReacquireG3: true,
            ReplaceTargetOnSlit: true,
            RestartGuiding: true,
            RestorePhotometry: true),
        _ => new(
            InvalidateQhySolution: false,
            InvalidateG3AndGuideEpoch: false,
            ReacquireQhy: false,
            ReacquireG3: false,
            ReplaceTargetOnSlit: false,
            RestartGuiding: false,
            RestorePhotometry: false),
    };
}
