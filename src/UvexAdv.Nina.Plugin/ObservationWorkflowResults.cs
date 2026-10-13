using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal static class ObservationWorkflowResults
{
    // Context must not replace the original code, scope, metrics or evidence.
    // This is equally important for target quality and for real safety failures.
    internal static StageResult WithFailureContext(StageResult result, string context) =>
        result with { Gate = result.Gate with { Message = $"{context} {result.Gate.Message}" } };

    internal static StageResult Passed(string code, string message,
        IReadOnlyDictionary<string, double>? metrics = null, IReadOnlyDictionary<string, string>? metadata = null) =>
        new(GateResult.Pass(code, message, metrics), Metadata: metadata);

    internal static StageResult Warning(string code, string message,
        IReadOnlyDictionary<string, double>? metrics = null, IReadOnlyDictionary<string, string>? metadata = null) =>
        new(GateResult.Warn(code, message, metrics), Metadata: metadata);

    internal static StageResult Attention(ObservationStage stage, string code, string message,
        IReadOnlyDictionary<string, double>? metrics = null) =>
        new(GateResult.Unknown(code, $"{SimulatedObservationStageRunner.StageDisplayName(stage)}: {message}", metrics));
}
