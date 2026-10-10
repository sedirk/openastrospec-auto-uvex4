namespace UvexAdv.Observatory;

/// <summary>
/// A native terminal-gate run rejected a cooperative checkpoint. This is a
/// structured gate, not an unknown device exception or permission to retry.
/// </summary>
public sealed class ObservationCheckpointGateException(GateResult gate, ObservationStage stage)
    : InvalidOperationException($"{gate.Code}: {gate.Message}")
{
    public GateResult Gate { get; } = gate;
    public ObservationStage Stage { get; } = stage;
}
