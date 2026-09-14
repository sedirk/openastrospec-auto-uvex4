using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class WorkflowTelemetryRetentionTests
{
    [Fact]
    public void ScienceEvidenceWindowDoesNotEvictAcquisitionPath()
    {
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        host.PublishEvidence("target-acquisition-branch", "not-opened-branch.json", metadata: new Dictionary<string, string>
        {
            ["observationRunId"] = "run-a", ["branch"] = "ShortExposureSep", ["code"] = "TARGET_BRANCH_PASSED",
            ["stage"] = "AcquireG3SlitField", ["message"] = "SEP position confirmed",
        });
        for (var i = 0; i < 250; i++) host.PublishEvidence("science-frame", $"not-opened-{i}.fits");
        var snapshot = host.Dashboard;
        Assert.Equal(200, snapshot.Evidence.Count);
        Assert.Single(snapshot.WorkflowEvidence!);
        var run = ObservationSnapshot.Idle with
        {
            ObservationRunId = "run-a", State = ObservationRunState.Completed, CompletedStageCount = 11,
        };
        var graph = ObservationWorkflowProjection.Build(snapshot with { Run = run, LockedRunAdapter = "simulator" });
        Assert.Equal(ObservationWorkflowNodeState.Passed, Assert.Single(graph.Nodes,
            node => node.Branch == TargetAcquisitionBranch.ShortExposureSep).StateKind);
        Assert.Contains("非实拍", graph.RunSummary);
        Assert.Contains("非实拍", graph.ExecutionModeText);
        Assert.DoesNotContain(ObservationWorkflowProjection.Build(snapshot with
        {
            Run = run with { ObservationRunId = "run-b" },
        }).Nodes, node => node.Kind == ObservationWorkflowNodeKind.Branch && node.StateKind == ObservationWorkflowNodeState.Passed);
    }

    [Fact]
    public void OversizedMethodHistoryIsBoundedAndDisclosed()
    {
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        for (var i = 0; i < 4100; i++) host.PublishEvidence("target-acquisition-branch", $"not-opened-{i}.json");
        var snapshot = host.Dashboard;
        Assert.Equal(4096, snapshot.WorkflowEvidence!.Count);
        Assert.True(snapshot.WorkflowHistoryTruncated);
        Assert.Contains("4096", ObservationWorkflowProjection.Build(snapshot).Notice);
    }

    [Fact]
    public void NewManifestCannotBeShownOnPreviousRunDuringStartup()
    {
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(ObservationCoordinatorHost).GetField("manifestPath", flags)!.SetValue(host, "new-run-manifest.json");
        typeof(ObservationCoordinatorHost).GetField("manifestRunId", flags)!.SetValue(host, "new-run");
        var create = typeof(ObservationCoordinatorHost).GetMethod("CreateDashboardLocked", flags)!;
        var previous = Assert.IsType<ObservationDashboardSnapshot>(create.Invoke(host,
            [ObservationSnapshot.Idle with { ObservationRunId = "previous-run", State = ObservationRunState.Completed }]));
        Assert.Null(previous.ManifestPath);
        Assert.Null(ObservationWorkflowProjection.Build(previous).ManifestPath);
        var current = Assert.IsType<ObservationDashboardSnapshot>(create.Invoke(host,
            [ObservationSnapshot.Idle with { ObservationRunId = "new-run", State = ObservationRunState.RunningAuto }]));
        Assert.Equal("new-run-manifest.json", current.ManifestPath);
    }

    [Fact]
    public void QueuedUiRefreshReReadsHostAndBridgeShowsSameProjection()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "ObservationDockable.cs"));
        Assert.Contains("BeginInvoke(() => ApplyDashboard(host.Dashboard))", source);
        Assert.DoesNotContain("BeginInvoke(() => ApplyDashboard(dashboard))", source);
        Assert.Contains("Workflow = ObservationWorkflowProjection.Build(dashboard)", source);
        Assert.Contains("new(\"show-workflow\", ShowWorkflowCommand, false, false, false, false", source);
        Assert.Contains("ApplyDashboard(dashboard, notifyBridge: false)", source);
    }

    private sealed class SilentNotifier : IObservationAttentionNotifier
    {
        public void Notify(ObservationAttentionNotification notification) { }
        public void ClearActiveIndicator() { }
        public void Dispose() { }
    }
}
