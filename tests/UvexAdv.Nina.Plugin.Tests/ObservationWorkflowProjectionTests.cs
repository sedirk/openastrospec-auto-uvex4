using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class ObservationWorkflowProjectionTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-09-14T00:00:00Z");

    [Fact]
    public void IdleShowsEveryNodeWithoutClaimingExecutionOrSkipping()
    {
        var graph = ObservationWorkflowProjection.Build(Dashboard(ObservationSnapshot.Idle));
        Assert.Equal(17, graph.Nodes.Count);
        Assert.Equal(11, graph.Nodes.Count(node => node.Kind == ObservationWorkflowNodeKind.MainStage));
        Assert.Equal(6, graph.Nodes.Count(node => node.Kind == ObservationWorkflowNodeKind.Branch));
        Assert.All(graph.Nodes, node => Assert.Equal(ObservationWorkflowNodeState.NotRecorded, node.StateKind));
        Assert.Equal(10, graph.Edges.Count);
        Assert.All(graph.Edges, edge => Assert.Equal(ObservationWorkflowEdgeKind.Canonical, edge.Kind));
        Assert.Null(graph.CurrentNodeId);
        Assert.Empty(graph.Timeline);
    }

    [Fact]
    public void IntermediatePassedGateCannotCompleteRunningStage()
    {
        var snapshot = Dashboard(Run(ObservationRunState.RunningAuto, ObservationStage.AcquireG3SlitField,
            Event(0, ObservationStage.AcquireG3SlitField, "STAGE_STARTED")), gates:
            new() { [ObservationStage.AcquireG3SlitField] = GateResult.Pass("WCS_VALID", "Only WCS passed.") });
        var node = Stage(ObservationWorkflowProjection.Build(snapshot), ObservationStage.AcquireG3SlitField);
        Assert.Equal(ObservationWorkflowNodeState.Running, node.StateKind);
        Assert.Empty(node.CompletionSource);
    }

    [Fact]
    public void ExplicitStageCompletionWinsUntilAnotherAttemptStarts()
    {
        var run = Run(ObservationRunState.RunningAuto, ObservationStage.AcquireG3SlitField,
            Event(0, ObservationStage.AcquireG3SlitField, "STAGE_STARTED"),
            Event(1, ObservationStage.AcquireG3SlitField, "STAGE_COMPLETED", "正式阶段完成"));
        var node = Stage(ObservationWorkflowProjection.Build(Dashboard(run)), ObservationStage.AcquireG3SlitField);
        Assert.Equal(ObservationWorkflowNodeState.Passed, node.StateKind);
        Assert.Equal("stage-completed-event", node.CompletionSource);
        Assert.False(node.IsCurrent);
        var retried = run with { RecentEvents = run.RecentEvents.Append(Event(2, ObservationStage.AcquireG3SlitField, "STAGE_STARTED")).ToArray() };
        Assert.Equal(ObservationWorkflowNodeState.Running,
            Stage(ObservationWorkflowProjection.Build(Dashboard(retried)), ObservationStage.AcquireG3SlitField).StateKind);
    }

    [Fact]
    public void CanonicalCompletionCountSurvivesTruncatedEventWindow()
    {
        var run = Run(ObservationRunState.RunningAuto, ObservationStage.RunScienceBlock,
            Event(9, ObservationStage.RunScienceBlock, "STAGE_STARTED")) with { CompletedStageCount = 9 };
        var graph = ObservationWorkflowProjection.Build(Dashboard(run));
        Assert.All(graph.Nodes.Where(node => node.Kind == ObservationWorkflowNodeKind.MainStage && node.Order < 9), node =>
        {
            Assert.Equal(ObservationWorkflowNodeState.Passed, node.StateKind);
            Assert.Equal("coordinator-completed-stage-count", node.CompletionSource);
            Assert.Contains("事件窗口", node.Summary);
        });
        Assert.Equal(ObservationWorkflowNodeState.Running, Stage(graph, ObservationStage.RunScienceBlock).StateKind);
        Assert.Equal(ObservationWorkflowNodeState.NotRecorded, Stage(graph, ObservationStage.FinalizeObservation).StateKind);
    }

    [Theory]
    [InlineData(ObservationRunState.PauseRequested, ObservationWorkflowNodeState.PauseRequested)]
    [InlineData(ObservationRunState.Paused, ObservationWorkflowNodeState.Paused)]
    [InlineData(ObservationRunState.PausedNeedsAttention, ObservationWorkflowNodeState.Blocked)]
    [InlineData(ObservationRunState.ManualTakeover, ObservationWorkflowNodeState.Paused)]
    [InlineData(ObservationRunState.Cancelling, ObservationWorkflowNodeState.Cancelling)]
    [InlineData(ObservationRunState.Faulted, ObservationWorkflowNodeState.Faulted)]
    public void CurrentStagePreservesActualControlState(ObservationRunState state, ObservationWorkflowNodeState expected)
    {
        var run = Run(state, ObservationStage.PlaceTargetOnSlit,
            Event(0, ObservationStage.PlaceTargetOnSlit, "STAGE_STARTED")) with { PauseReason = "真实暂停原因" };
        var node = Stage(ObservationWorkflowProjection.Build(Dashboard(run)), ObservationStage.PlaceTargetOnSlit);
        Assert.Equal(expected, node.StateKind);
        Assert.Equal("真实暂停原因", node.Summary);
    }

    [Fact]
    public void ResumeShowsLatestAttemptInsteadOfOldBlock()
    {
        var run = Run(ObservationRunState.RunningAuto, ObservationStage.AcquireG3SlitField,
            Event(0, ObservationStage.AcquireG3SlitField, "STAGE_STARTED"),
            Event(1, ObservationStage.AcquireG3SlitField, "TARGET_NOT_FOUND", state: ObservationRunState.PausedNeedsAttention),
            Event(2, ObservationStage.AcquireG3SlitField, "RUN_RESUMED"),
            Event(3, ObservationStage.AcquireG3SlitField, "STAGE_STARTED"));
        var graph = ObservationWorkflowProjection.Build(Dashboard(run, evidence:
        [ BranchEvidence(1, TargetAcquisitionBranch.DirectStellarPosition, "TARGET_BRANCH_BLOCKED"),
          BranchEvidence(3, TargetAcquisitionBranch.DirectStellarPosition, "TARGET_BRANCH_SELECTED") ]));
        Assert.Equal(ObservationWorkflowNodeState.Running, Stage(graph, ObservationStage.AcquireG3SlitField).StateKind);
        var branch = Branch(graph, TargetAcquisitionBranch.DirectStellarPosition);
        Assert.Equal(ObservationWorkflowNodeState.Selected, branch.StateKind);
        Assert.True(branch.IsCurrent);
        Assert.Equal(2, branch.Timeline.Count);
    }

    [Fact]
    public void CancelledWithoutCurrentStageMarksOnlyUnfinishedLastStageCancelled()
    {
        var run = Run(ObservationRunState.Cancelled, null,
            Event(0, ObservationStage.AcquireG3SlitField, "STAGE_STARTED"),
            Event(1, null, "RUN_CANCELLED", state: ObservationRunState.Cancelled)) with { CompletedStageCount = 4 };
        var graph = ObservationWorkflowProjection.Build(Dashboard(run));
        Assert.Equal(ObservationWorkflowNodeState.Cancelled, Stage(graph, ObservationStage.AcquireG3SlitField).StateKind);
        Assert.Equal(ObservationWorkflowNodeState.NotRecorded, Stage(graph, ObservationStage.PlaceTargetOnSlit).StateKind);
        Assert.DoesNotContain(graph.Nodes, node => node.StateKind == ObservationWorkflowNodeState.Skipped);
    }

    [Theory]
    [InlineData("TARGET_BRANCH_SELECTED", ObservationWorkflowNodeState.Selected)]
    [InlineData("TARGET_BRANCH_FALLBACK", ObservationWorkflowNodeState.Fallback)]
    [InlineData("TARGET_BRANCH_PASSED", ObservationWorkflowNodeState.Passed)]
    [InlineData("TARGET_BRANCH_BLOCKED", ObservationWorkflowNodeState.Blocked)]
    [InlineData("TARGET_BRANCH_SKIPPED", ObservationWorkflowNodeState.Skipped)]
    [InlineData("UNRECOGNIZED_SUCCESS_TEXT", ObservationWorkflowNodeState.NotRecorded)]
    public void BranchOutcomeRequiresExactStructuredCode(string code, ObservationWorkflowNodeState expected)
    {
        var graph = ObservationWorkflowProjection.Build(Dashboard(
            Run(ObservationRunState.RunningAuto, ObservationStage.AcquireG3SlitField,
                Event(0, ObservationStage.AcquireG3SlitField, "STAGE_STARTED")),
            [BranchEvidence(1, TargetAcquisitionBranch.ShortExposureSep, code)]));
        Assert.Equal(expected, Branch(graph, TargetAcquisitionBranch.ShortExposureSep).StateKind);
        Assert.Equal(ObservationWorkflowNodeState.Running, Stage(graph, ObservationStage.AcquireG3SlitField).StateKind);
        Assert.All(graph.Nodes.Where(node => node.Kind == ObservationWorkflowNodeKind.Branch &&
            node.Branch != TargetAcquisitionBranch.ShortExposureSep), node =>
            Assert.Equal(ObservationWorkflowNodeState.NotRecorded, node.StateKind));
    }

    [Fact]
    public void ForeignOrUnboundBranchEvidenceCannotLeakIntoNewRun()
    {
        var stale = BranchEvidence(1, TargetAcquisitionBranch.ShortExposureSep, "TARGET_BRANCH_PASSED", runId: "old-run");
        var missing = BranchEvidence(2, TargetAcquisitionBranch.CatalogWcsGeometry, "TARGET_BRANCH_PASSED", runId: "");
        var graph = ObservationWorkflowProjection.Build(Dashboard(
            Run(ObservationRunState.RunningAuto, ObservationStage.AcquireG3SlitField), [stale, missing]));
        Assert.All(graph.Nodes.Where(node => node.Kind == ObservationWorkflowNodeKind.Branch), node =>
        {
            Assert.Equal(ObservationWorkflowNodeState.NotRecorded, node.StateKind);
            Assert.Empty(node.Evidence);
        });
        Assert.Empty(graph.TargetName);
        Assert.Empty(graph.Timeline);
        Assert.DoesNotContain(graph.Edges, edge => edge.Kind != ObservationWorkflowEdgeKind.Canonical);
    }

    [Fact]
    public void BranchCanBeRecordedDuringFinePlacementAndProbeRecovery()
    {
        var graph = ObservationWorkflowProjection.Build(Dashboard(
            Run(ObservationRunState.RunningAuto, ObservationStage.SelectAtrExposure,
                Event(0, ObservationStage.PlaceTargetOnSlit, "STAGE_STARTED"),
                Event(2, ObservationStage.SelectAtrExposure, "STAGE_STARTED")),
            [BranchEvidence(1, TargetAcquisitionBranch.ShortExposureSep, "TARGET_BRANCH_PASSED"),
             BranchEvidence(3, TargetAcquisitionBranch.ShortExposureSep, "TARGET_BRANCH_FALLBACK")]));
        var branch = Branch(graph, TargetAcquisitionBranch.ShortExposureSep);
        Assert.Equal(ObservationStage.SelectAtrExposure, branch.Stage);
        Assert.Contains(graph.Edges, edge => edge.Kind == ObservationWorkflowEdgeKind.Membership &&
            edge.FromId == "stage:PlaceTargetOnSlit" && edge.ToId == branch.Id);
        Assert.Contains(graph.Edges, edge => edge.Kind == ObservationWorkflowEdgeKind.Membership &&
            edge.FromId == "stage:SelectAtrExposure" && edge.ToId == branch.Id);
        Assert.DoesNotContain(graph.Edges, edge => edge.Kind == ObservationWorkflowEdgeKind.Membership &&
            edge.FromId == "stage:AcquireG3SlitField");
    }

    [Fact]
    public void ExplicitBranchStageSurvivesTruncatedStageStartHistory()
    {
        var evidence = BranchEvidence(1, TargetAcquisitionBranch.ShortExposureSep, "TARGET_BRANCH_SELECTED");
        evidence = evidence with { Metadata = new Dictionary<string, string>(evidence.Metadata!) { ["stage"] = "PlaceTargetOnSlit" } };
        var graph = ObservationWorkflowProjection.Build(Dashboard(
            Run(ObservationRunState.RunningAuto, ObservationStage.PlaceTargetOnSlit), [evidence]));
        Assert.Equal(ObservationStage.PlaceTargetOnSlit, Branch(graph, TargetAcquisitionBranch.ShortExposureSep).Stage);
        Assert.True(Branch(graph, TargetAcquisitionBranch.ShortExposureSep).IsCurrent);
    }

    [Fact]
    public void ObservedTransitionsAreNotTheConfiguredPriorityList()
    {
        var graph = ObservationWorkflowProjection.Build(Dashboard(
            Run(ObservationRunState.RunningAuto, ObservationStage.AcquireG3SlitField,
                Event(0, ObservationStage.AcquireG3SlitField, "STAGE_STARTED")),
            [BranchEvidence(1, TargetAcquisitionBranch.DirectStellarPosition, "TARGET_BRANCH_BLOCKED"),
             BranchEvidence(2, TargetAcquisitionBranch.BoundedNeighborWcs, "TARGET_BRANCH_FALLBACK"),
             BranchEvidence(3, TargetAcquisitionBranch.CatalogWcsGeometry, "TARGET_BRANCH_PASSED")]));
        var transitions = graph.Edges.Where(edge => edge.Kind == ObservationWorkflowEdgeKind.ObservedTransition).ToArray();
        Assert.Equal(2, transitions.Length);
        Assert.Equal("branch:DirectStellarPosition", transitions[0].FromId);
        Assert.Equal("branch:BoundedNeighborWcs", transitions[0].ToId);
        Assert.Equal("branch:CatalogWcsGeometry", transitions[1].ToId);
        Assert.DoesNotContain(transitions, edge => edge.FromId == "branch:ShortExposureSep" || edge.ToId == "branch:ShortExposureSep");
    }

    [Fact]
    public void RunCompletionDoesNotInventBranchSuccess()
    {
        var graph = ObservationWorkflowProjection.Build(Dashboard(
            Run(ObservationRunState.Completed, null,
                Event(0, ObservationStage.AcquireG3SlitField, "STAGE_STARTED"),
                Event(3, null, "RUN_COMPLETED", state: ObservationRunState.Completed)) with { CompletedStageCount = 11 },
            [BranchEvidence(1, TargetAcquisitionBranch.DirectStellarPosition, "TARGET_BRANCH_SELECTED")]));
        Assert.Equal(ObservationWorkflowNodeState.StoppedUnconfirmed, Branch(graph, TargetAcquisitionBranch.DirectStellarPosition).StateKind);
        Assert.Equal(ObservationWorkflowNodeState.NotRecorded, Branch(graph, TargetAcquisitionBranch.ShortExposureSep).StateKind);
        Assert.Null(graph.CurrentNodeId);
    }

    [Fact]
    public void MetricsAndEvidenceRemainInspectableWithoutChangingTheirInputs()
    {
        var metrics = new Dictionary<string, double> { ["residualPixels"] = 2.35, ["snr"] = 13 };
        var eventPath = "C:/immutable/frame.fits";
        var run = Run(ObservationRunState.PausedNeedsAttention, ObservationStage.PlaceTargetOnSlit,
            Event(0, ObservationStage.PlaceTargetOnSlit, "STAGE_STARTED"),
            Event(1, ObservationStage.PlaceTargetOnSlit, "RESIDUAL_TOO_LARGE") with { EvidencePath = eventPath });
        var graph = ObservationWorkflowProjection.Build(Dashboard(run,
            gates: new() { [ObservationStage.PlaceTargetOnSlit] = GateResult.Warn("QUALITY_WARNING", "仍保留警告", metrics) }));
        var node = Stage(graph, ObservationStage.PlaceTargetOnSlit);
        Assert.Equal(2.35, node.Metrics["residualPixels"]);
        Assert.Contains("residualPixels = 2.35", node.MetricsText);
        Assert.Equal(GateSeverity.Warning, node.GateSeverity);
        Assert.Contains(node.Evidence, item => item.AbsolutePath == eventPath);
        Assert.Contains(node.Timeline, item => item.EvidencePath == eventPath);
        metrics["residualPixels"] = 99;
        Assert.Equal(2.35, node.Metrics["residualPixels"]);
        Assert.Equal(eventPath, run.RecentEvents[1].EvidencePath);
    }

    [Fact]
    public void EmptyPathRemainsTelemetryNotAClaimThatAFileExists()
    {
        var evidence = BranchEvidence(1, TargetAcquisitionBranch.ShortExposureSep, "TARGET_BRANCH_FALLBACK") with { AbsolutePath = "" };
        var graph = ObservationWorkflowProjection.Build(Dashboard(
            Run(ObservationRunState.RunningAuto, ObservationStage.AcquireG3SlitField), [evidence]));
        Assert.Null(Assert.Single(graph.Timeline).EvidencePath);
        Assert.Equal("Almach", graph.TargetName);
        Assert.Equal("HIP 9640", graph.CatalogId);
        Assert.Contains("run-current", graph.RunSummary);
    }

    [Fact]
    public void MatchingLockedPlanNamesTheNewRunBeforeStrategyEvidenceExists()
    {
        var run = Run(ObservationRunState.Validating, ObservationStage.ValidateNightSetup);
        var snapshot = Dashboard(run) with { LockedPlan = Plan("run-current", "M31", "NGC 224") };
        var graph = ObservationWorkflowProjection.Build(snapshot);
        Assert.Equal("M31", graph.TargetName);
        Assert.Equal("NGC 224", graph.CatalogId);
        Assert.Empty(graph.Timeline);
    }

    [Fact]
    public void LockedPlanOverridesMetadataButForeignPlanDoesNotNameThisRun()
    {
        var snapshot = Dashboard(Run(ObservationRunState.RunningAuto, ObservationStage.AcquireG3SlitField),
            [BranchEvidence(1, TargetAcquisitionBranch.ShortExposureSep, "TARGET_BRANCH_SELECTED")]);
        var graph = ObservationWorkflowProjection.Build(snapshot with { LockedPlan = Plan("run-current", "M76", "NGC 650") });
        Assert.Equal("M76", graph.TargetName);
        Assert.Equal("NGC 650", graph.CatalogId);
        graph = ObservationWorkflowProjection.Build(snapshot with { LockedPlan = Plan("other-run", "M31", "NGC 224") });
        Assert.Equal("Almach", graph.TargetName);
    }

    [Fact]
    public void CompletedBranchIsNotPresentedAsCurrentlyExecuting()
    {
        var graph = ObservationWorkflowProjection.Build(Dashboard(
            Run(ObservationRunState.RunningAuto, ObservationStage.AcquireG3SlitField,
                Event(0, ObservationStage.AcquireG3SlitField, "STAGE_STARTED")),
            [BranchEvidence(1, TargetAcquisitionBranch.ShortExposureSep, "TARGET_BRANCH_PASSED")]));
        Assert.False(Branch(graph, TargetAcquisitionBranch.ShortExposureSep).IsCurrent);
        Assert.Equal("stage:AcquireG3SlitField", graph.CurrentNodeId);
    }

    [Fact]
    public void PassedStageKeepsItsQualityWarningExplicit()
    {
        var run = Run(ObservationRunState.RunningAuto, ObservationStage.PlaceTargetOnSlit,
            Event(0, ObservationStage.PlaceTargetOnSlit, "STAGE_STARTED"),
            Event(1, ObservationStage.PlaceTargetOnSlit, "STAGE_COMPLETED"));
        var graph = ObservationWorkflowProjection.Build(Dashboard(run,
            gates: new() { [ObservationStage.PlaceTargetOnSlit] = GateResult.Warn("PRECISION_WARNING", "允许试拍；非精确入缝") }));
        var node = Stage(graph, ObservationStage.PlaceTargetOnSlit);
        Assert.Equal(ObservationWorkflowNodeState.Passed, node.StateKind);
        Assert.Equal("已通过 · 有警告", node.StateText);
        Assert.Equal("PRECISION_WARNING", node.GateCode);
        Assert.Contains("非精确入缝", node.GateText);
    }

    [Fact]
    public void EveryBranchExposesPrerequisitesWithoutNeedingExecution()
    {
        var graph = ObservationWorkflowProjection.Build(Dashboard(ObservationSnapshot.Idle));
        foreach (var node in graph.Nodes.Where(node => node.Branch is not null))
        {
            Assert.Equal(TargetAcquisitionStrategyPolicy.Describe(node.Branch!.Value).Prerequisites, node.Prerequisites);
            Assert.Contains(node.Prerequisites, node.Summary);
            Assert.Equal(ObservationWorkflowNodeState.NotRecorded, node.StateKind);
        }
    }

    [Fact]
    public void GenericUnboundEvidenceNeedsExactCoordinatorPathCorrelation()
    {
        var run = Run(ObservationRunState.RunningAuto, ObservationStage.AcquireG3SlitField,
            Event(0, ObservationStage.AcquireG3SlitField, "STAGE_STARTED"),
            Event(1, ObservationStage.AcquireG3SlitField, "SOLVE_RESULT") with { EvidencePath = "C:/verified.fits" });
        var graph = ObservationWorkflowProjection.Build(Dashboard(run,
            [new("fits", "C:/verified.fits", Epoch.AddSeconds(1)),
             new("fits", "C:/unrelated.fits", Epoch.AddSeconds(1))]));
        var node = Stage(graph, ObservationStage.AcquireG3SlitField);
        Assert.Contains(node.Evidence, item => item.AbsolutePath == "C:/verified.fits");
        Assert.DoesNotContain(node.Evidence, item => item.AbsolutePath == "C:/unrelated.fits");
    }

    private static ObservationWorkflowNode Stage(ObservationWorkflowGraph graph, ObservationStage stage) =>
        Assert.Single(graph.Nodes, node => node.Id == ObservationWorkflowProjection.StageId(stage));
    private static ObservationWorkflowNode Branch(ObservationWorkflowGraph graph, TargetAcquisitionBranch branch) =>
        Assert.Single(graph.Nodes, node => node.Id == ObservationWorkflowProjection.BranchId(branch));
    private static ObservationSnapshot Run(ObservationRunState state, ObservationStage? stage, params ObservationEvent[] events) =>
        new("run-current", state, stage, null, "本轮状态", null, 0, 11, Epoch.AddSeconds(20), events);
    private static ObservationEvent Event(int seconds, ObservationStage? stage, string code, string message = "事件原因",
        ObservationRunState state = ObservationRunState.RunningAuto) => new(Epoch.AddSeconds(seconds), state, stage, code, message);
    private static ObservationDashboardSnapshot Dashboard(ObservationSnapshot run,
        IReadOnlyList<ObservationDashboardEvidence>? evidence = null, Dictionary<ObservationStage, GateResult>? gates = null) =>
        new(run, gates ?? new(), new Dictionary<ObservationPreviewChannel, ObservationPreview>(), evidence ?? [], null);
    private static ObservationDashboardEvidence BranchEvidence(int seconds, TargetAcquisitionBranch branch, string code,
        string runId = "run-current") => new("target-acquisition-branch", $"C:/evidence/{seconds}.json", Epoch.AddSeconds(seconds),
            new Dictionary<string, string>
            {
                ["observationRunId"] = runId, ["branch"] = branch.ToString(), ["code"] = code,
                ["message"] = $"{branch}: 原始原因 {seconds}", ["targetName"] = "Almach", ["catalogId"] = "HIP 9640",
            });
    private static ObservationPlan Plan(string runId, string name, string catalogId) => new(
        runId, "night", new(name, catalogId, 10, 40), new(30, 120, 0), Epoch,
        TimeSpan.FromMinutes(5), new(), new(), "spectral-camera", "guide-profile", "photometry-camera", false);
}
