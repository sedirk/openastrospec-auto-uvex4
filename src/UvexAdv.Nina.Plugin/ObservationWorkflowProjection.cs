using System.Globalization;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

public enum ObservationWorkflowNodeKind { MainStage, Branch }
public enum ObservationWorkflowNodeState
{
    NotRecorded, Running, Selected, Fallback, Passed, Blocked, Skipped,
    Paused, PauseRequested, Cancelling, Cancelled, Faulted, StoppedUnconfirmed,
}
public enum ObservationWorkflowEdgeKind { Canonical, Membership, ObservedTransition }

public sealed record ObservationWorkflowEvidence(
    string Kind, string AbsolutePath, DateTimeOffset PublishedUtc, string Description);

public sealed record ObservationWorkflowTimelineItem(
    DateTimeOffset TimestampUtc, string? NodeId, string Code, string Message,
    string? EvidencePath, string Source);

public sealed record ObservationWorkflowEdge(
    string FromId, string ToId, string Label, ObservationWorkflowEdgeKind Kind)
{
    public bool IsConditional => Kind != ObservationWorkflowEdgeKind.Canonical;
}

public sealed record ObservationWorkflowNode(
    string Id, string Label, ObservationWorkflowNodeKind Kind, int Order,
    ObservationWorkflowNodeState StateKind, string StateText, bool IsCurrent,
    string Summary, string Code, DateTimeOffset? UpdatedUtc,
    ObservationStage? Stage, TargetAcquisitionBranch? Branch,
    IReadOnlyDictionary<string, double> Metrics,
    IReadOnlyList<ObservationWorkflowEvidence> Evidence,
    IReadOnlyList<ObservationWorkflowTimelineItem> Timeline,
    GateSeverity? GateSeverity = null, string CompletionSource = "",
    string GateCode = "", string GateMessage = "", string Prerequisites = "")
{
    public string StableId => Id;
    public string MetricsText => Metrics.Count == 0 ? "尚无质量数值记录" : string.Join(" · ",
        Metrics.Select(pair => $"{pair.Key} = {pair.Value.ToString("G6", CultureInfo.InvariantCulture)}"));
    public string EvidenceText => Evidence.Count == 0 ? "尚无关联证据记录" :
        string.Join(Environment.NewLine, Evidence.Select(item => $"{item.Kind} · {item.AbsolutePath}"));
    public string GateText => string.IsNullOrWhiteSpace(GateCode) ? "尚无质量门记录" : $"{GateCode} · {GateMessage}";
}

public sealed record ObservationWorkflowGraph(
    string? RunId, string TargetName, string CatalogId, ObservationRunState RunState,
    DateTimeOffset UpdatedUtc, IReadOnlyList<ObservationWorkflowNode> Nodes,
    IReadOnlyList<ObservationWorkflowEdge> Edges,
    IReadOnlyList<ObservationWorkflowTimelineItem> Timeline, string Notice,
    string RunAdapter = "unknown", string? ManifestPath = null)
{
    public string ExecutionModeText => RunAdapter switch
    {
        "real" => "真实设备运行记录",
        "simulator" => "模拟运行记录 · 非实拍",
        _ => string.IsNullOrWhiteSpace(RunId) ? "未启动" : "执行模式未记录",
    };
    public string? CurrentNodeId => Nodes.LastOrDefault(node => node.IsCurrent &&
        node.Kind == ObservationWorkflowNodeKind.Branch)?.Id ??
        Nodes.FirstOrDefault(node => node.IsCurrent)?.Id;
    public string RunSummary => string.IsNullOrWhiteSpace(RunId) ? "尚未开始观测；显示现有编排结构" :
        $"{ExecutionModeText} · 本轮 {RunId} · {(string.IsNullOrWhiteSpace(TargetName) ? "目标名称尚未记录" : TargetName)}" +
        (string.IsNullOrWhiteSpace(CatalogId) ? "" : $" / {CatalogId}");
}

/// <summary>
/// A read-only projection of the one production coordinator. It does not execute
/// a graph, open files, control equipment, or infer success from an intermediate
/// passing gate. Canonical completion counts survive the bounded event window;
/// branch outcomes require exact, run-bound branch telemetry.
/// </summary>
public static class ObservationWorkflowProjection
{
    private static readonly IReadOnlyDictionary<string, double> NoMetrics =
        new Dictionary<string, double>();

    public static ObservationWorkflowGraph Build(ObservationDashboardSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var run = snapshot.Run;
        var hasRun = !string.IsNullOrWhiteSpace(run.ObservationRunId);
        var events = hasRun ? run.RecentEvents.OrderBy(item => item.TimestampUtc).ToArray() : [];
        var boundEvidence = hasRun ? (snapshot.WorkflowEvidence ?? snapshot.Evidence).Where(item =>
            IsBound(item.Metadata, run.ObservationRunId)).OrderBy(item => item.PublishedUtc).ToArray() : [];
        var branchEvidence = boundEvidence.Where(item => item.Kind == "target-acquisition-branch" &&
            ParseBranch(item.Metadata) is not null).ToArray();
        var identity = boundEvidence.LastOrDefault(item => (item.Kind is
            "target-acquisition-strategy" or "target-acquisition-branch") &&
            !string.IsNullOrWhiteSpace(Value(item.Metadata, "targetName")));
        var lockedTarget = hasRun && string.Equals(snapshot.LockedPlan?.ObservationRunId,
            run.ObservationRunId, StringComparison.Ordinal) ? snapshot.LockedPlan?.Target : null;
        var timeline = events.Select(item => new ObservationWorkflowTimelineItem(item.TimestampUtc,
            item.Stage is { } stage ? StageId(stage) : null, item.Code, item.Message,
            item.EvidencePath, "coordinator")).Concat(branchEvidence.Select(item =>
                new ObservationWorkflowTimelineItem(item.PublishedUtc,
                    BranchId(ParseBranch(item.Metadata)!.Value), Value(item.Metadata, "code"),
                    Value(item.Metadata, "message"), EmptyToNull(item.AbsolutePath), "target-acquisition-branch")))
            .OrderBy(item => item.TimestampUtc).ToArray();
        var nodes = new List<ObservationWorkflowNode>();
        var edges = new List<ObservationWorkflowEdge>();
        var stages = ObservationRunCoordinator.Stages;
        for (var index = 0; index < stages.Count; index++)
        {
            var stage = stages[index];
            var id = StageId(stage);
            var stageEvents = events.Where(item => item.Stage == stage).ToArray();
            var started = stageEvents.LastOrDefault(item => item.Code == "STAGE_STARTED");
            var completed = stageEvents.LastOrDefault(item => item.Code is "STAGE_COMPLETED" or "STAGE_PASSED");
            var explicitCompletion = completed is not null &&
                (started is null || completed.TimestampUtc >= started.TimestampUtc);
            var countCompletion = hasRun && index < Math.Clamp(run.CompletedStageCount, 0, stages.Count);
            var passed = explicitCompletion || countCompletion;
            var isCurrent = hasRun && run.CurrentStage == stage && !passed;
            var last = stageEvents.LastOrDefault();
            var state = passed ? ObservationWorkflowNodeState.Passed :
                isCurrent ? CurrentStageState(run.State) : ObservationWorkflowNodeState.NotRecorded;
            if (!passed && !isCurrent && started is not null)
            {
                state = run.State == ObservationRunState.Cancelled &&
                    events.LastOrDefault(item => item.Code == "STAGE_STARTED")?.Stage == stage
                    ? ObservationWorkflowNodeState.Cancelled : ObservationWorkflowNodeState.StoppedUnconfirmed;
            }
            var completionSource = explicitCompletion ? "stage-completed-event" :
                countCompletion ? "coordinator-completed-stage-count" : "";
            var summary = countCompletion && !explicitCompletion
                ? "编排器已确认该阶段完成；详细完成事件不在当前事件窗口内。"
                : passed ? completed!.Message
                : isCurrent ? run.PauseReason ?? run.StatusMessage
                : last?.Message ?? "尚未记录执行；不能据此认定已跳过。";
            snapshot.Gates.TryGetValue(stage, out var gate);
            if (!hasRun) gate = null;
            var paths = stageEvents.Where(item => !string.IsNullOrWhiteSpace(item.EvidencePath))
                .Select(item => item.EvidencePath!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var associated = hasRun ? snapshot.Evidence.Where(item =>
                (IsBound(item.Metadata, run.ObservationRunId) && EvidenceStage(item, events) == stage) ||
                (paths.Contains(item.AbsolutePath) && !HasForeignRun(item.Metadata, run.ObservationRunId)))
                .Select(ToEvidence).ToList() : [];
            foreach (var item in stageEvents.Where(item => !string.IsNullOrWhiteSpace(item.EvidencePath)))
            {
                if (!associated.Any(evidence => string.Equals(evidence.AbsolutePath, item.EvidencePath, StringComparison.OrdinalIgnoreCase)))
                    associated.Add(new("coordinator-stage-evidence", item.EvidencePath!, item.TimestampUtc, item.Message));
            }
            nodes.Add(new(id, StageLabel(stage), ObservationWorkflowNodeKind.MainStage, index,
                state, state == ObservationWorkflowNodeState.Passed && gate?.Severity == GateSeverity.Warning
                    ? "已通过 · 有警告" : StateText(state), isCurrent, summary,
                passed ? completed?.Code ?? "COORDINATOR_STAGE_COMPLETED" : last?.Code ?? "",
                last?.TimestampUtc, stage, null,
                gate?.Metrics is { } metrics ? new Dictionary<string, double>(metrics) : NoMetrics,
                associated.OrderBy(item => item.PublishedUtc).ToArray(),
                timeline.Where(item => item.NodeId == id).ToArray(), gate?.Severity, completionSource,
                gate?.Code ?? "", gate?.Message ?? ""));
            if (index > 0) edges.Add(new(StageId(stages[index - 1]), id, "编排顺序", ObservationWorkflowEdgeKind.Canonical));
        }
        var latestBranchEvidence = branchEvidence.LastOrDefault();
        foreach (var branch in Enum.GetValues<TargetAcquisitionBranch>())
        {
            var description = TargetAcquisitionStrategyPolicy.Describe(branch);
            var evidence = branchEvidence.Where(item => ParseBranch(item.Metadata) == branch).ToArray();
            var latest = evidence.LastOrDefault();
            var stage = latest is null ? null : EvidenceStage(latest, events);
            var code = latest is null ? "" : Value(latest.Metadata, "code");
            var state = BranchState(code);
            var isCurrent = latest is not null && ReferenceEquals(latest, latestBranchEvidence) &&
                stage == run.CurrentStage && run.CurrentStage is not null &&
                (state is ObservationWorkflowNodeState.Selected or ObservationWorkflowNodeState.Fallback or ObservationWorkflowNodeState.Blocked) &&
                run.State is not (ObservationRunState.Completed or ObservationRunState.Cancelled or ObservationRunState.Faulted or ObservationRunState.Idle);
            if (isCurrent && (state is ObservationWorkflowNodeState.Selected or ObservationWorkflowNodeState.Fallback))
            {
                if (run.State is ObservationRunState.Paused or ObservationRunState.PausedNeedsAttention or
                    ObservationRunState.ManualTakeover or ObservationRunState.PauseRequested or ObservationRunState.Cancelling)
                    state = CurrentStageState(run.State);
            }
            else if (!isCurrent && (state is ObservationWorkflowNodeState.Selected or ObservationWorkflowNodeState.Fallback) &&
                (run.State is ObservationRunState.Completed or ObservationRunState.Cancelled or ObservationRunState.Faulted))
                state = ObservationWorkflowNodeState.StoppedUnconfirmed;
            var id = BranchId(branch);
            nodes.Add(new(id, description.Label,
                ObservationWorkflowNodeKind.Branch, (int)branch, state, StateText(state), isCurrent,
                latest is null ? $"尚未记录执行；不等于已跳过或该方法不可用。适用条件：{description.Prerequisites}" : Value(latest.Metadata, "message"),
                code, latest?.PublishedUtc, stage, branch, NoMetrics, evidence.Select(ToEvidence).ToArray(),
                timeline.Where(item => item.NodeId == id).ToArray(), Prerequisites: description.Prerequisites));
            foreach (var parent in evidence.Select(item => EvidenceStage(item, events)).OfType<ObservationStage>().Distinct())
                edges.Add(new(StageId(parent), id, "本阶段实际记录", ObservationWorkflowEdgeKind.Membership));
        }
        for (var index = 1; index < branchEvidence.Length; index++)
        {
            var previous = ParseBranch(branchEvidence[index - 1].Metadata)!.Value;
            var next = ParseBranch(branchEvidence[index].Metadata)!.Value;
            if (previous == next) continue;
            var isFallback = Value(branchEvidence[index].Metadata, "code") == "TARGET_BRANCH_FALLBACK";
            edges.Add(new(BranchId(previous), BranchId(next), isFallback ? "记录到转入后备方法" : "随后记录到此方法",
                ObservationWorkflowEdgeKind.ObservedTransition));
        }
        return new(run.ObservationRunId, lockedTarget?.Name ?? Value(identity?.Metadata, "targetName"),
            lockedTarget?.CatalogId ?? Value(identity?.Metadata, "catalogId"), run.State, run.UpdatedUtc, nodes.ToArray(),
            edges.Distinct().ToArray(), timeline,
            "只读执行视图，不改变流程。主线为编排阶段，精调内部可能先建立导星；连线区分编排顺序、实际方法归属和记录先后，不表示可任意重连。未记录不是跳过，分支通过不是精确入缝或整轮成功。" +
            (snapshot.WorkflowHistoryTruncated ? " 本轮超过 4096 条方法记录，早期记录请查完整运行归档。" : ""),
            snapshot.LockedRunAdapter, snapshot.ManifestPath);
    }

    public static string StageId(ObservationStage stage) => $"stage:{stage}";
    public static string BranchId(TargetAcquisitionBranch branch) => $"branch:{branch}";

    private static bool IsBound(IReadOnlyDictionary<string, string>? metadata, string? runId) =>
        !string.IsNullOrWhiteSpace(runId) && string.Equals(Value(metadata, "observationRunId"), runId, StringComparison.Ordinal);
    private static bool HasForeignRun(IReadOnlyDictionary<string, string>? metadata, string? runId) =>
        !string.IsNullOrWhiteSpace(Value(metadata, "observationRunId")) && !IsBound(metadata, runId);
    private static string Value(IReadOnlyDictionary<string, string>? metadata, string key) =>
        metadata is not null && metadata.TryGetValue(key, out var value) ? value : "";
    private static string? EmptyToNull(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
    private static TargetAcquisitionBranch? ParseBranch(IReadOnlyDictionary<string, string>? metadata) =>
        Enum.TryParse<TargetAcquisitionBranch>(Value(metadata, "branch"), false, out var branch) && Enum.IsDefined(branch) ? branch : null;
    private static ObservationStage? EvidenceStage(ObservationDashboardEvidence evidence, IReadOnlyList<ObservationEvent> events)
    {
        if (Enum.TryParse<ObservationStage>(Value(evidence.Metadata, "stage"), false, out var explicitStage) && Enum.IsDefined(explicitStage))
            return explicitStage;
        return events.LastOrDefault(item => item.TimestampUtc <= evidence.PublishedUtc && item.Code == "STAGE_STARTED")?.Stage;
    }
    private static ObservationWorkflowEvidence ToEvidence(ObservationDashboardEvidence evidence) => new(
        evidence.Kind, evidence.AbsolutePath, evidence.PublishedUtc, Value(evidence.Metadata, "message"));
    private static ObservationWorkflowNodeState BranchState(string code) => code switch
    {
        "TARGET_BRANCH_SELECTED" => ObservationWorkflowNodeState.Selected,
        "TARGET_BRANCH_FALLBACK" => ObservationWorkflowNodeState.Fallback,
        "TARGET_BRANCH_PASSED" => ObservationWorkflowNodeState.Passed,
        "TARGET_BRANCH_BLOCKED" => ObservationWorkflowNodeState.Blocked,
        "TARGET_BRANCH_SKIPPED" => ObservationWorkflowNodeState.Skipped,
        _ => ObservationWorkflowNodeState.NotRecorded,
    };
    private static ObservationWorkflowNodeState CurrentStageState(ObservationRunState state) => state switch
    {
        ObservationRunState.PausedNeedsAttention => ObservationWorkflowNodeState.Blocked,
        ObservationRunState.Paused or ObservationRunState.ManualTakeover => ObservationWorkflowNodeState.Paused,
        ObservationRunState.PauseRequested => ObservationWorkflowNodeState.PauseRequested,
        ObservationRunState.Cancelling => ObservationWorkflowNodeState.Cancelling,
        ObservationRunState.Cancelled => ObservationWorkflowNodeState.Cancelled,
        ObservationRunState.Faulted => ObservationWorkflowNodeState.Faulted,
        ObservationRunState.RunningAuto or ObservationRunState.Validating or ObservationRunState.Finalizing => ObservationWorkflowNodeState.Running,
        _ => ObservationWorkflowNodeState.NotRecorded,
    };
    public static string StateText(ObservationWorkflowNodeState state) => state switch
    {
        ObservationWorkflowNodeState.Running => "正在执行",
        ObservationWorkflowNodeState.Selected => "已选择，结果未记录",
        ObservationWorkflowNodeState.Fallback => "转入后备方法",
        ObservationWorkflowNodeState.Passed => "已通过",
        ObservationWorkflowNodeState.Blocked => "未通过",
        ObservationWorkflowNodeState.Skipped => "明确跳过",
        ObservationWorkflowNodeState.Paused => "已暂停",
        ObservationWorkflowNodeState.PauseRequested => "正在暂停",
        ObservationWorkflowNodeState.Cancelling => "正在取消",
        ObservationWorkflowNodeState.Cancelled => "已取消",
        ObservationWorkflowNodeState.Faulted => "故障停止",
        ObservationWorkflowNodeState.StoppedUnconfirmed => "执行记录未闭合",
        _ => "未记录执行",
    };
    private static string StageLabel(ObservationStage stage) => stage switch
    {
        ObservationStage.ValidateNightSetup => "校验本夜配置",
        ObservationStage.SlewToCatalogTarget => "转向目录目标",
        ObservationStage.AcquireQhyWideField => "测光相机广域解算",
        ObservationStage.CoarseCenter => "广域见证与交接",
        ObservationStage.AcquireG3SlitField => "导星相机取场与粗居中",
        ObservationStage.PlaceTargetOnSlit => "PHD2 入缝精调",
        ObservationStage.StartGuiding => "导星与稳定复核",
        ObservationStage.StartQhyPhotometry => "启动同步测光",
        ObservationStage.SelectAtrExposure => "光谱试拍与曝光选择",
        ObservationStage.RunScienceBlock => "科学曝光与监测",
        ObservationStage.FinalizeObservation => "收尾与归档",
        _ => stage.ToString(),
    };
}
