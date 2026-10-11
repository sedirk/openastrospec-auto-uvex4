using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal static class ObservationWorkflowResults
{
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
