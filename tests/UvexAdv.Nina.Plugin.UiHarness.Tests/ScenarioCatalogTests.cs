using UvexAdv.Nina.Plugin;
using UvexAdv.Nina.Plugin.UiHarness;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin.UiHarness.Tests;

public sealed class ScenarioCatalogTests
{
    [Theory]
    [InlineData("workflow-idle")]
    [InlineData("workflow-running")]
    [InlineData("workflow-fallback")]
    [InlineData("workflow-blocked")]
    [InlineData("workflow-cancelling")]
    [InlineData("workflow-cancelled")]
    [InlineData("workflow-completed")]
    [InlineData("workflow-narrow")]
    [InlineData("workflow-short")]
    public void WorkflowHeaderComesFromTheSameReplayAsItsNodes(string scenario)
    {
        var vm = ScenarioCatalog.Select(scenario).Single().ViewModel;
        var graph = vm.Workflow;
        var stages = graph.Nodes.Where(node => node.Kind == ObservationWorkflowNodeKind.MainStage).ToArray();
        var current = stages.FirstOrDefault(node => node.IsCurrent);
        var completed = stages.Count(node => node.StateKind == ObservationWorkflowNodeState.Passed);
        Assert.True(vm.IsSimulationMode);
        Assert.Contains("模拟", vm.ModeText);
        Assert.Contains("模拟", vm.RealModeStatusSummary);
        Assert.Equal(completed * 100d / 11, vm.ProgressPercent);
        Assert.Contains($"{completed}/11", vm.ProgressSummary);
        if (current is not null) Assert.Equal(current.Label, vm.CurrentStageText);
        if (graph.RunId is not null) Assert.Equal("simulator", graph.RunAdapter);
        if (graph.RunState is ObservationRunState.Cancelled or ObservationRunState.Completed)
        {
            Assert.False(vm.IsRunActive);
            Assert.Equal("不再自动执行", vm.NextStageText);
        }
    }

    [Fact]
    public void TargetStrategiesDistinguishCatalogueRecommendationRuntimeAndUnavailableMetadata()
    {
        var automatic = ScenarioCatalog.Select("target-strategy-auto").Single().ViewModel;
        Assert.Equal(TargetObservabilityClass.AutoFromPlanetarium, automatic.TargetObservability);
        Assert.Equal(6, automatic.AvailableTargetObservabilityClasses.Count);
        Assert.Equal(TargetObservabilityClass.AutoFromPlanetarium, automatic.AvailableTargetObservabilityClasses.Last().Value);
        Assert.Equal("根据星图自动", automatic.AvailableTargetObservabilityClasses.Last().Label);
        Assert.Contains("double star", automatic.TargetStrategyMetadataSummary);
        Assert.Contains("尚未执行", automatic.TargetStrategyRuntimeSummary);
        Assert.True(automatic.UsePlanetariumStrategyCommand.CanExecute(null));

        var unknown = ScenarioCatalog.Select("target-strategy-unknown").Single().ViewModel;
        Assert.Contains("不推断为暗目标", unknown.TargetObservabilitySummary);
        Assert.Contains("旧资料不会沿用", unknown.TargetStrategyMetadataSummary);
        var catalogue = ScenarioCatalog.Select("target-strategy-catalogue").Single().ViewModel;
        Assert.Contains("行星状星云", catalogue.TargetObservabilitySummary);
        Assert.StartsWith("1. 正式 WCS", catalogue.TargetStrategyPrioritySummary);

        var fallback = ScenarioCatalog.Select("target-strategy-fallback").Single().ViewModel;
        Assert.Contains("原因", fallback.TargetStrategyRuntimeSummary);
        Assert.Contains("未宣称精确入缝", fallback.TargetStrategyRuntimeSummary);
        Assert.Contains("本轮同帧解算有效", fallback.TargetStrategyAttemptHistory);
        Assert.Equal("#86EFAC", fallback.TargetStrategyRuntimeColor);
        Assert.Equal(0, fallback.SelectedWorkspaceTabIndex);
        var busy = ScenarioCatalog.Select("target-strategy-busy").Single().ViewModel;
        Assert.False(busy.IsTargetPlanEditable);
        Assert.False(busy.UsePlanetariumStrategyCommand.CanExecute(null));
        Assert.False(busy.ImportFromPlanetariumCommand.CanExecute(null));
        Assert.Contains("第 2/3 张", busy.TargetStrategyRuntimeSummary);
        Assert.Contains("尚未通过", busy.TargetStrategyAttemptHistory);
        Assert.Equal("#7DD3FC", busy.TargetStrategyRuntimeColor);
        Assert.True(ScenarioCatalog.Select("target-strategy-narrow-bottom").Single().ExercisePlanScrolling);
    }

    [Fact]
    public void FocusScenariosExposeInvalidBusyCancelledAndNativeStatesWithoutDevices()
    {
        var invalid = Assert.IsType<MainFocusMock>(ScenarioCatalog.Select("main-focus-invalid").Single().AlternateViewModel);
        Assert.False(invalid.StartCommand.CanExecute(null));
        Assert.Contains("最小、最大", invalid.AvailabilityMessage);
        var cancelling = Assert.IsType<MainFocusMock>(ScenarioCatalog.Select("main-focus-cancelling").Single().AlternateViewModel);
        Assert.False(cancelling.StartCommand.CanExecute(null));
        Assert.True(cancelling.CancelCommand.CanExecute(null));
        var native = ScenarioCatalog.Select("main-focus-native").Single();
        Assert.Equal("UvexAdv.Nina.Plugin.SepMainFocusViewModel_Dockable", native.TemplateKey);
        Assert.Equal(1, ScenarioCatalog.Select("main-focus-embedded").Single().ViewModel.SelectedManualTabIndex);
    }

    [Fact]
    public void PreparationFixturesIncludeTargetAndSlitIssuesAndLockBusyEdits()
    {
        var missing = ScenarioCatalog.Select("preparation-missing").Single().ViewModel;
        Assert.Equal(3, missing.SelectedWorkspaceTabIndex);
        Assert.Equal(0, missing.SelectedPreparationTabIndex);
        Assert.Equal(8, missing.AutomaticPreparationIssueCount);
        Assert.Equal(10, missing.PreparationChecklistIssueCount);
        Assert.Contains(missing.PreparationChecklistIssues, issue => issue.Contains("期望狭缝", StringComparison.Ordinal));
        Assert.Contains(missing.PreparationChecklistIssues, issue => issue.Contains("设备身份", StringComparison.Ordinal));
        var ready = ScenarioCatalog.Select("preparation-ready").Single().ViewModel;
        Assert.Equal(0, ready.PreparationChecklistIssueCount);
        Assert.Empty(ready.PreparationChecklistIssues);
        Assert.Contains("尚未检查设备连接", ready.AutomaticPreparationSummary);
        Assert.Contains("不代表实时设备已经就绪", ready.AutomationPolicyPreparationStatus);
        Assert.Contains("不是实时位置或到位证明", ready.PreparationSlitStatus);
        Assert.Contains("已选择本夜配置：", ready.NightSetupPreparationStatus);
        Assert.Contains("3 项待处理", ScenarioCatalog.Select("preparation-default").Single().ViewModel.AutomaticPreparationSummary);
        Assert.Contains("准备草稿不能代替锁定配置", missing.NightSetupPreparationStatus);
        var night = ScenarioCatalog.Select("preparation-night").Single().ViewModel;
        Assert.Equal(1, night.SelectedPreparationTabIndex);
        Assert.True(night.IsTargetPlanEditable);
        Assert.True(night.CreateNightSetupDraftCommand.CanExecute(null));
        var busy = ScenarioCatalog.Select("preparation-busy").Single().ViewModel;
        Assert.False(busy.IsTargetPlanEditable);
        Assert.False(busy.CreateNightSetupDraftCommand.CanExecute(null));
        Assert.False(busy.SelectNightSetupSnapshotCommand.CanExecute(null));
        Assert.True(busy.ShowPreparationChecklistCommand.CanExecute(null));
        Assert.True(busy.ShowDeviceBindingsSettingsCommand.CanExecute(null));
        Assert.True(busy.ShowSafetySettingsCommand.CanExecute(null));
        Assert.True(ScenarioCatalog.Select("preparation-narrow-bottom").Single().ExercisePreparationScrolling);
        Assert.True(ScenarioCatalog.Select("preparation-night-narrow-bottom").Single().ExercisePreparationScrolling);
        Assert.Equal("en-US", ScenarioCatalog.Select("preparation-en").Single().Culture.Name);
        Assert.Equal("en-US", ScenarioCatalog.Select("preparation-night-en").Single().Culture.Name);
        foreach (var scenario in ScenarioCatalog.Select(null).Where(item => item.ViewModel.SelectedWorkspaceTabIndex == 6))
            Assert.Equal(scenario.ViewModel.AdvancedCategoryIndex, scenario.ViewModel.SelectedAdvancedCategoryIndex);
    }

    [Fact]
    public void Catalog_CoversRequiredOperatorAndAdvancedSettingsStates()
    {
        var scenarios = ScenarioCatalog.Select(null);

        string[] existing = ["idle", "main-focus", "main-focus-narrow", "main-focus-invalid", "main-focus-busy", "main-focus-curve", "main-focus-cancelling", "main-focus-native", "main-focus-embedded", "plan-target", "target-strategy-auto", "target-strategy-unknown", "target-strategy-catalogue", "target-strategy-fallback", "target-strategy-busy", "target-strategy-narrow-bottom", "plan-budget", "plan-budget-narrow", "plan-budget-invalid", "plan-budget-locked", "plan-budget-en", "plan-target-short", "plan-target-short-bottom", "plan-budget-short", "plan-budget-short-bottom", "plan-budget-small-bottom", "plan-budget-short-bottom-en", "uvex-manual", "startup-requirements", "running", "recovering", "atr-manual", "atr-live", "atr-levels", "atr-narrow", "failure", "failure-en", "phd2-degraded", "phd2-direct-target", "ghost-assistance", "qhy-g3-fast-pair", "narrow", "advanced", "photometry-off", "photometry-worker", "photometry-worker-en", "photometry-master", "photometry-master-en", "photometry-worker-running", "photometry-worker-pausing", "photometry-worker-narrow", "photometry-help"];
        string[] added = ["workflow-idle", "workflow-running", "workflow-fallback", "workflow-blocked", "workflow-cancelling", "workflow-cancelled", "workflow-completed", "workflow-narrow", "workflow-short", "advanced-narrow"];
        string[] preparation = ["preparation-default", "preparation-missing", "preparation-ready", "preparation-night", "preparation-busy",
            "preparation-en", "preparation-night-en", "preparation-narrow", "preparation-narrow-bottom", "preparation-night-narrow", "preparation-night-narrow-bottom"];
        var expected = existing.Concat(added).Concat(preparation).Concat(Enumerable.Range(0, 10).Select(index => $"advanced-category-{index}")).ToArray();
        Assert.Equal(expected.Length, scenarios.Count);
        Assert.Equal(expected.OrderBy(name => name), scenarios.Select(item => item.Name).OrderBy(name => name));
        Assert.Equal(scenarios.Count, scenarios.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("zh-CN", scenarios.Single(item => item.Name == "failure").Culture.Name);
        Assert.Equal("en-US", scenarios.Single(item => item.Name == "failure-en").Culture.Name);
        Assert.True(scenarios.Single(item => item.Name == "narrow").Width <= 540);
        Assert.False(scenarios.Single(item => item.Name == "idle").ViewModel.HasFailure);
        Assert.True(scenarios.Single(item => item.Name == "running").ViewModel.IsRunActive);
        Assert.Equal(ObservationUiTone.Recovering, scenarios.Single(item => item.Name == "recovering").ViewModel.RunTone);
        var atrManual = scenarios.Single(item => item.Name == "atr-manual").ViewModel;
        Assert.Equal(4, atrManual.SelectedWorkspaceTabIndex);
        Assert.Equal(2, atrManual.SelectedPreviewTabIndex);
        Assert.NotEmpty(atrManual.ManualSpectrumPoints);
        Assert.True(atrManual.CaptureManualAtrSpectrumCommand.CanExecute(null));
        Assert.True(scenarios.Single(item => item.Name == "failure").ViewModel.HasFailure);
        Assert.Equal(0, scenarios.Single(item => item.Name == "idle").ViewModel.SelectedWorkspaceTabIndex);
        var startup = scenarios.Single(item => item.Name == "startup-requirements").ViewModel;
        Assert.Equal(3, startup.SelectedWorkspaceTabIndex);
        Assert.Contains("准备尚未完成", startup.RealModeStatusSummary, StringComparison.Ordinal);
        Assert.Contains("设备标定证据", startup.RealModeStatus, StringComparison.Ordinal);
        Assert.False(startup.IsDevicePreparationMissing);
        Assert.True(startup.IsCommissioningPreparationMissing);
        Assert.True(startup.IsNightSetupPreparationMissing);
        var manual = scenarios.Single(item => item.Name == "uvex-manual").ViewModel;
        Assert.Equal(1, manual.SelectedWorkspaceTabIndex);
        Assert.Contains("未连接", manual.ManualUvexConnectionStatus, StringComparison.Ordinal);
        Assert.Equal("UVEX4 / 已绑定串口", manual.SelectedManualUvexDevice);
        Assert.True(manual.ConnectManualUvexCommand.CanExecute(null));
        Assert.False(manual.DisconnectManualUvexCommand.CanExecute(null));
        Assert.False(manual.SelectManualSlit1Command.CanExecute(null));
        Assert.True(manual.ReleaseManualUvexComPortCommand.CanExecute(null));
        var advanced = scenarios.Single(item => item.Name == "advanced").ViewModel;
        Assert.Equal(6, advanced.SelectedWorkspaceTabIndex);
        Assert.True(advanced.BrightTargetWingCentroidEnabled);
        Assert.True(advanced.BrightTargetMinimumG3ExposureMilliseconds > 0);
        Assert.Equal(8, advanced.AdvancedCategoryIndex);
        var degraded = scenarios.Single(item => item.Name == "phd2-degraded").ViewModel;
        Assert.Equal("DegradedSupervised", degraded.Phd2CalibrationGradeText);
        Assert.Contains("exact-lock：是", degraded.Phd2CalibrationPermissionText, StringComparison.Ordinal);
        Assert.Contains("无人值守科学：否", degraded.Phd2CalibrationPermissionText, StringComparison.Ordinal);
        Assert.Contains("0.5", degraded.Phd2CalibrationScaleText, StringComparison.Ordinal);
        Assert.Contains("0.75", degraded.Phd2CalibrationScaleText, StringComparison.Ordinal);
        Assert.Contains("OffSlitGuideStar", degraded.Phd2CommissioningRouteText, StringComparison.Ordinal);
        var directTarget = scenarios.Single(item => item.Name == "phd2-direct-target").ViewModel;
        Assert.Equal("Qualified", directTarget.Phd2CalibrationGradeText);
        Assert.Contains("DegradedDirectTargetGuiding", directTarget.Phd2CommissioningRouteText, StringComparison.Ordinal);
        Assert.Contains("10 ms", directTarget.Phd2CommissioningRouteText, StringComparison.Ordinal);
        Assert.Contains("无人值守科学：否", directTarget.Phd2CalibrationPermissionText, StringComparison.Ordinal);
        Assert.Contains("RequiresOperatorSupervision", directTarget.Phd2CalibrationReasonText, StringComparison.Ordinal);
        var ghost = scenarios.Single(item => item.Name == "ghost-assistance").ViewModel;
        Assert.Equal("AutoIfValidElseSkip", ghost.GhostAssistanceMode);
        Assert.Contains("GHOST_TEMPLATE_APPLICABLE", ghost.GhostApplicabilityText, StringComparison.Ordinal);
        Assert.Contains("UseCalibratedAuxiliaryEstimate", ghost.GhostDecisionText, StringComparison.Ordinal);
        Assert.Contains("不能建立身份或授权运动", ghost.GhostDecisionText, StringComparison.Ordinal);
        var fastPair = scenarios.Single(item => item.Name == "qhy-g3-fast-pair").ViewModel;
        Assert.True(fastPair.QhyG3FastPairEnabled);
        Assert.Equal(6, fastPair.SelectedWorkspaceTabIndex);
        Assert.Equal(5, fastPair.AdvancedCategoryIndex);
        Assert.Contains("0 次赤道仪命令", fastPair.QhyG3FastPairStatus, StringComparison.Ordinal);
        Assert.Contains("Candidate", fastPair.WideToSlitTransferStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void MockStates_UseDeterministicImagesAndNoProductionDockable()
    {
        var running = ScenarioCatalog.Select("running").Single().ViewModel;
        var failure = ScenarioCatalog.Select("failure").Single().ViewModel;

        Assert.NotNull(running.QhyPreviewImage);
        Assert.NotNull(running.G3PreviewImage);
        Assert.NotNull(running.AtrPreviewImage);
        Assert.Contains("G3_FOCUS_STARS_TOO_BROAD", failure.LastFailureCode, StringComparison.Ordinal);
        Assert.Equal("UvexAdv.Nina.Plugin.UiHarness", running.GetType().Assembly.GetName().Name);
        Assert.NotEqual("UvexAdv.Nina.Plugin.ObservationDockable", running.GetType().FullName);
    }

    [Fact]
    public void TargetImport_IsVisibleWhenSuccessfulAndDisabledDuringRun()
    {
        var idle = ScenarioCatalog.Select("idle").Single().ViewModel;
        var running = ScenarioCatalog.Select("running").Single().ViewModel;
        var narrow = ScenarioCatalog.Select("narrow").Single().ViewModel;

        Assert.True(idle.HasTargetImport);
        Assert.True(idle.IsTargetPlanEditable);
        Assert.Contains("构图助手", idle.TargetImportSummary, StringComparison.Ordinal);
        Assert.False(idle.ImportFramingCenter);
        Assert.True(idle.ImportFromFramingAssistantCommand.CanExecute(null));
        Assert.True(idle.ImportFromPlanetariumCommand.CanExecute(null));
        Assert.False(running.ImportFromFramingAssistantCommand.CanExecute(null));
        Assert.False(narrow.ImportFromPlanetariumCommand.CanExecute(null));
        Assert.Contains("运行期间禁止", narrow.TargetImportDetails, StringComparison.Ordinal);
        Assert.False(narrow.IsTargetPlanEditable);
    }
}
