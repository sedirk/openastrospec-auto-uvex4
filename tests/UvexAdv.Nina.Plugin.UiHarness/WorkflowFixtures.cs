using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin.UiHarness;

// Explicit replay inputs for UI checks. These never represent equipment evidence.
internal static class WorkflowFixtures
{
    public static ObservationWorkflowGraph Create(string scenario)
    {
        var now = new DateTimeOffset(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);
        if (scenario == "idle") return ObservationWorkflowProjection.Build(new(
            ObservationSnapshot.Idle, new Dictionary<ObservationStage, GateResult>(),
            new Dictionary<ObservationPreviewChannel, ObservationPreview>(), [], null));
        const string runId = "UI-REPLAY-20260914";
        var completed = scenario == "completed" ? 11 : scenario == "running" ? 8 : scenario == "placement" ? 5 : 4;
        var current = scenario == "completed" ? (ObservationStage?)null : ObservationRunCoordinator.Stages[completed];
        var state = scenario switch
        {
            "blocked" => ObservationRunState.PausedNeedsAttention,
            "cancelled" => ObservationRunState.Cancelled,
            "cancelling" => ObservationRunState.Cancelling,
            "completed" => ObservationRunState.Completed,
            _ => ObservationRunState.RunningAuto,
        };
        var events = new List<ObservationEvent>();
        var gates = new Dictionary<ObservationStage, GateResult>();
        for (var i = 0; i < completed; i++)
        {
            var stage = ObservationRunCoordinator.Stages[i];
            events.Add(new(now.AddSeconds(-40 + i * 2), ObservationRunState.RunningAuto, stage, "STAGE_STARTED", "开始本阶段"));
            events.Add(new(now.AddSeconds(-39 + i * 2), ObservationRunState.RunningAuto, stage, "STAGE_COMPLETED", "本阶段已通过；详见保留的运行记录。"));
            gates[stage] = GateResult.Pass("UI_REPLAY_STAGE_PASS", "本阶段已通过。");
        }
        if (current is { } active)
            events.Add(new(now.AddSeconds(-10), state, active, "STAGE_STARTED", "正在执行本阶段"));
        var reason = scenario switch
        {
            "blocked" => "短曝光仍有两颗独立星像，无法唯一确认目录目标；暂停等待处理，未继续运动。",
            "cancelled" => "取消与软件清理已结束；设备终态以实际回读记录为准。",
            "cancelling" => "已收到取消请求；等待当前原子操作结束，尚未确认取消完成。",
            "running" => "保留 2.3 px 入缝精度警告，正在按实际光谱信号选择曝光。",
            "placement" => "导星相机取场已确认，正在执行 PHD2 有界锁点微调并等待新鲜残差。",
            "recovering" => "导星相机新帧不足，保持原位并按本轮原有次数上限补拍；没有重置运动预算。",
            "completed" => "模拟编排已完成；此截图不是实机验收证据。",
            _ => "本轮已切换目录定位。原因：本轮同帧解算有效，星像无法稳定分离；未宣称精确入缝。",
        };
        var evidence = new List<ObservationDashboardEvidence>();
        void Branch(TargetAcquisitionBranch branch, string code, string message, int seconds)
        {
            evidence.Add(new("target-acquisition-branch", $@"C:\UI-REPLAY\{branch}-{seconds}.json", now.AddSeconds(seconds),
                new Dictionary<string, string>
                {
                    ["observationRunId"] = runId, ["targetName"] = "Almach（离线回放）", ["catalogId"] = "HIP 9640",
                    ["branch"] = branch.ToString(), ["stage"] = ObservationStage.AcquireG3SlitField.ToString(),
                    ["code"] = code, ["message"] = message,
                }));
        }
        if (scenario is "fallback" or "blocked" or "running" or "completed" or "cancelling" or "cancelled")
        {
            Branch(TargetAcquisitionBranch.DirectStellarPosition, "TARGET_BRANCH_BLOCKED", "实测星像不可唯一分离；这次方法未通过，不代表所有定位方法失效。", -9);
            if (scenario == "blocked")
            {
                Branch(TargetAcquisitionBranch.ShortExposureSep, "TARGET_BRANCH_SELECTED", "过曝条件要求短曝光复核；使用 SEP 提取独立星像。", -8);
                Branch(TargetAcquisitionBranch.ShortExposureSep, "TARGET_BRANCH_BLOCKED", reason, -1);
            }
            else
            {
                Branch(TargetAcquisitionBranch.CatalogWcsGeometry, "TARGET_BRANCH_FALLBACK", reason, -8);
                if (scenario is "running" or "completed")
                    Branch(TargetAcquisitionBranch.CatalogWcsGeometry, "TARGET_BRANCH_PASSED", "正式 WCS 目录几何已确认取场；不等于精确入缝。", -7);
            }
        }
        if (scenario == "ghost")
            Branch(TargetAcquisitionBranch.CalibratedGhost, "TARGET_BRANCH_SELECTED", "正在核对同安装标定、外部目标身份及两张新鲜帧；尚未授予后续运动。", -1);
        if (scenario == "recovering")
            Branch(TargetAcquisitionBranch.DirectStellarPosition, "TARGET_BRANCH_SELECTED", reason, -1);
        if (current is { } currentStage)
            gates[currentStage] = scenario == "blocked"
                ? GateResult.Unknown("G3_CATALOG_SHORT_POSITION_UNCONFIRMED", reason)
                : GateResult.Warn("UI_REPLAY_QUALITY_WARNING", reason, new Dictionary<string, double> { ["residualPixels"] = 2.3 });
        if (scenario is "running" or "completed")
            gates[ObservationStage.PlaceTargetOnSlit] = GateResult.Warn("SLIT_PRECISION_WARNING", "保留实测入缝精度警告。", new Dictionary<string, double> { ["residualPixels"] = 2.3 });
        if (scenario == "cancelled")
            events.Add(new(now, state, null, "RUN_CANCELLED", reason));
        else if (scenario == "cancelling")
            events.Add(new(now, state, current, "CANCEL_REQUESTED", reason));
        else if (scenario == "completed")
            events.Add(new(now, state, null, "RUN_COMPLETED", reason));
        var run = new ObservationSnapshot(runId, state, scenario == "cancelled" ? null : current, null, reason,
            scenario == "blocked" ? reason : null, completed, 11, now, events);
        var plan = new ObservationPlan(runId, "UI-REPLAY-NIGHT", new("Almach（离线回放）", "HIP 9640", 30.98, 42.32),
            new(33, 120, 0), now, TimeSpan.FromMinutes(1), new(), new(), "replay-spectral", "replay-guide", "replay-photometry", false);
        return ObservationWorkflowProjection.Build(new(run, gates,
            new Dictionary<ObservationPreviewChannel, ObservationPreview>(), evidence, @"C:\UI-REPLAY\manifest.json", plan,
            LockedRunAdapter: "simulator"));
    }
}
