using System.Buffers.Binary;
using System.IO;
using System.Text.RegularExpressions;
using UvexAdv.Nina.Plugin.UiHarness;

namespace UvexAdv.Nina.Plugin.UiHarness.Tests;

public sealed class ScreenshotRendererTests
{
    // Native NINA chart resources are application-scoped. Render every scenario on
    // one long-lived STA, matching a real WPF host rather than switching UI threads.
    private static readonly System.Collections.Concurrent.BlockingCollection<Action> UiActions = new();
    static ScreenshotRendererTests()
    {
        var thread = new Thread(() => { foreach (var action in UiActions.GetConsumingEnumerable()) action(); }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }
    private static void RunOnUiThread(Action action)
    {
        using var done = new ManualResetEventSlim();
        Exception? failure = null;
        UiActions.Add(() => { try { action(); } catch (Exception e) { failure = e; } finally { done.Set(); } });
        Assert.True(done.Wait(TimeSpan.FromSeconds(30)), "Offline WPF render timed out.");
        Assert.Null(failure);
    }

    [Theory]
    [InlineData("main-focus")]
    [InlineData("main-focus-narrow")]
    [InlineData("main-focus-invalid")]
    [InlineData("main-focus-busy")]
    [InlineData("main-focus-curve")]
    [InlineData("main-focus-cancelling")]
    [InlineData("main-focus-native")]
    [InlineData("main-focus-embedded")]
    public void FocusStatusAndActionsStayVisibleAndNativeChartIsActuallyLoaded(string scenario)
    {
        var layout = Assert.IsType<FocusLayoutDiagnostics>(RenderPhotometry(scenario).FocusLayout);
        Assert.True(layout.StatusVisible);
        Assert.True(layout.ReasonVisible);
        Assert.True(layout.CancelVisible);
        Assert.True(layout.SettingsBottomReachable);
        Assert.Equal(scenario != "main-focus-native", layout.StartVisible);
        Assert.Equal(scenario is not ("main-focus-invalid" or "main-focus-busy" or "main-focus-cancelling"), layout.StartEnabled);
        Assert.Equal(scenario == "main-focus-cancelling", layout.CancelEnabled);
        Assert.Equal(scenario is "main-focus-curve" or "main-focus-cancelling" or "main-focus-native", layout.NativeChartVisible);
    }

    [Theory]
    [InlineData("target-strategy-auto")]
    [InlineData("target-strategy-unknown")]
    [InlineData("target-strategy-catalogue")]
    [InlineData("target-strategy-busy")]
    [InlineData("target-strategy-narrow-bottom")]
    public void TargetStrategySelectionPriorityAndActualRuntimeAreReachable(string scenario)
    {
        var result = RenderPhotometry(scenario);
        var layout = Assert.IsType<TargetStrategyLayoutDiagnostics>(result.TargetStrategyLayout);
        Assert.True(layout.IsPlanPage);
        Assert.Equal(6, layout.ChoiceCount);
        Assert.True(layout.AutoChoiceLast);
        Assert.True(layout.RuntimeReachable);
        Assert.True(layout.PriorityReachable);
        Assert.True(layout.AutoButtonReachable);
        Assert.Equal(scenario != "target-strategy-busy", layout.SelectorEnabled);
        Assert.Equal(scenario != "target-strategy-busy", layout.AutoButtonEnabled);
        Assert.Contains("本目标的尝试顺序", result.VisibleTexts);
        Assert.Contains("本轮实际执行", result.VisibleTexts);
        Assert.Contains(result.VisibleTexts, text => text.Contains("共用本轮预算", StringComparison.Ordinal));
    }

    [Fact]
    public void OverviewShowsActualFallbackAndReasonWithoutClaimingPrecisePlacement()
    {
        var result = RenderPhotometry("target-strategy-fallback");
        var layout = Assert.IsType<TargetStrategyLayoutDiagnostics>(result.TargetStrategyLayout);
        Assert.False(layout.IsPlanPage);
        Assert.True(layout.RuntimeReachable);
        var workflow = Assert.IsType<WorkflowLayoutDiagnostics>(result.WorkflowLayout);
        Assert.Contains("本轮已切换", workflow.SelectedReason);
        Assert.Contains("原因", workflow.SelectedReason);
        Assert.Contains("未宣称精确入缝", workflow.SelectedReason);
    }

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
    public void WorkflowShowsAllNodesAndRealDetailsWithoutExpansion(string scenario)
    {
        var layout = Assert.IsType<WorkflowLayoutDiagnostics>(RenderPhotometry(scenario).WorkflowLayout);
        Assert.Equal(17, layout.NodeCount);
        Assert.Equal(0, layout.ExpanderCount);
        Assert.True(layout.GraphViewportVisible, "Graph viewport must be inside the actual host bounds.");
        Assert.True(layout.ToolbarVisible, "Fit and follow controls must remain accessible.");
        Assert.True(layout.DetailVisible);
        Assert.True(layout.DetailTabsReachable, "Every detail tab's actual text viewport must be reachable.");
        Assert.True(layout.SelectionShowsReason);
        Assert.True(layout.SelectionRetainedOnUpdate, "Background graph refresh must not steal manual selection or scroll position.");
        Assert.True(layout.ControlsRetainedOnUpdate, "Live snapshots should retain existing node controls.");
        Assert.True(layout.DifferentRunResetsSelection);
        Assert.True(layout.LastNodeReachable, "The lower-right node must be reachable inside the clipped graph viewport.");
        Assert.True(layout.HorizontalScrolled);
        Assert.True(layout.VerticalScrolled);
        Assert.True(layout.WarningsUseAmber);
        Assert.True(layout.CancellationNotCompleted);
    }

    [Theory]
    [InlineData("workflow-narrow")]
    [InlineData("workflow-short")]
    public void SmallWorkflowWindowsKeepReadableNodesAndAllowActualPanning(string scenario)
    {
        var layout = Assert.IsType<WorkflowLayoutDiagnostics>(RenderPhotometry(scenario).WorkflowLayout);
        Assert.True(layout.HorizontalOverflow > 0);
        Assert.True(layout.VerticalOverflow > 0);
        Assert.True(layout.HorizontalScrolled);
        Assert.True(layout.VerticalScrolled);
        Assert.True(layout.LastNodeReachable);
    }

    [Theory]
    [InlineData("workflow-cancelling", "Cancelling")]
    [InlineData("workflow-cancelled", "Cancelled")]
    public void WorkflowCancellationNeverPaintsTheWholeRunAsCompleted(string scenario, string state)
    {
        var layout = Assert.IsType<WorkflowLayoutDiagnostics>(RenderPhotometry(scenario).WorkflowLayout);
        Assert.Equal(state, layout.RunState);
        Assert.True(layout.CancellationNotCompleted);
    }

    [Theory]
    [InlineData("advanced-category-0", 0)]
    [InlineData("advanced-category-1", 1)]
    [InlineData("advanced-category-2", 2)]
    [InlineData("advanced-category-3", 3)]
    [InlineData("advanced-category-4", 4)]
    [InlineData("advanced-category-5", 5)]
    [InlineData("advanced-category-6", 6)]
    [InlineData("advanced-category-7", 7)]
    [InlineData("advanced-category-8", 8)]
    [InlineData("advanced-category-9", 9)]
    [InlineData("advanced-narrow", 8)]
    [InlineData("qhy-g3-fast-pair", 5)]
    public void AdvancedCategoriesKeepNavigationSaveAndLastInputAccessible(string scenario, int index)
    {
        var layout = Assert.IsType<AdvancedLayoutDiagnostics>(RenderPhotometry(scenario).AdvancedLayout);
        Assert.Equal(10, layout.CategoryCount);
        Assert.Equal(index, layout.SelectedCategory);
        Assert.Equal(0, layout.ExpanderCount);
        Assert.True(layout.SaveVisible);
        Assert.True(layout.SaveStaysFixed);
        Assert.True(layout.CategoriesReachable);
        Assert.True(layout.BottomInputReachable);
        Assert.True(layout.WheelScrolled);
        Assert.True(layout.InputWheelScrolled);
    }

    [Theory]
    [InlineData("preparation-default", 0)]
    [InlineData("preparation-missing", 0)]
    [InlineData("preparation-ready", 0)]
    [InlineData("preparation-night", 1)]
    [InlineData("preparation-busy", 1)]
    [InlineData("preparation-en", 0)]
    [InlineData("preparation-night-en", 1)]
    [InlineData("preparation-narrow", 0)]
    [InlineData("preparation-night-narrow", 1)]
    public void PreparationUsesTwoFlatTabsAndKeepsEngineeringEditorsOutOfRoutinePreparation(string scenario, int tab)
    {
        var result = RenderPhotometry(scenario);
        var layout = Assert.IsType<PreparationLayoutDiagnostics>(result.PreparationLayout);
        Assert.Equal(2, layout.TabCount);
        Assert.Equal(tab, layout.SelectedTab);
        Assert.Equal(0, layout.ExpanderCount);
        Assert.Equal(0, layout.NestedTabCount);
        Assert.Equal(0, layout.AdvancedEditorCount);
        Assert.False(layout.HasOuterScrollViewer);
        Assert.True(layout.HeaderVisible);
        Assert.True(layout.HeaderStaysFixed);
        Assert.True(layout.TabsStayFixed);
        Assert.True(layout.RunControlStaysFixed);
        Assert.True(layout.ViewportInsideHost);
        if (tab == 0)
        {
            Assert.Equal(7, layout.ChecklistCardCount);
            Assert.Equal(0, layout.NightEditorCount);
            Assert.Contains("ShowObservationPlanCommand", layout.NavigationCommands);
            Assert.Contains("ShowDeviceBindingsSettingsCommand", layout.NavigationCommands);
            Assert.Contains("ShowNightSetupPreparationCommand", layout.NavigationCommands);
            Assert.Contains("ShowSafetySettingsCommand", layout.NavigationCommands);
        }
        else
        {
            Assert.Equal(4, layout.NightEditorCount);
            Assert.Equal(scenario != "preparation-busy", layout.NightEditorsEnabled);
            Assert.Equal(scenario != "preparation-busy", layout.DraftActionEnabled);
            Assert.Equal(scenario != "preparation-busy", layout.ImportActionEnabled);
        }
        if (scenario.EndsWith("-en", StringComparison.Ordinal))
            Assert.DoesNotContain(result.VisibleTexts, text => Regex.IsMatch(text, "[\\u3400-\\u9fff]"));
    }

    [Theory]
    [InlineData("preparation-narrow-bottom")]
    [InlineData("preparation-night-narrow-bottom")]
    public void PreparationPagesActuallyScrollTheirLastActionIntoTheViewportWithoutMovingFixedControls(string scenario)
    {
        var layout = Assert.IsType<PreparationLayoutDiagnostics>(RenderPhotometry(scenario).PreparationLayout);
        Assert.True(double.IsFinite(layout.ViewportHeight) && layout.ViewportHeight > 0);
        Assert.True(layout.ViewportInsideHost);
        Assert.True(layout.ScrollableHeight > 0);
        Assert.True(layout.VerticalBarVisible);
        Assert.False(layout.BottomInitiallyVisible);
        Assert.True(layout.WheelScrolled, "Wheel input must move the content, not only the scroll thumb.");
        Assert.True(layout.InputWheelScrolled, "Wheel input over nightly selectors must still scroll the page.");
        Assert.Equal(layout.ScrollableHeight, layout.FinalOffset, 1);
        Assert.True(layout.BottomFinallyVisible, "The last actual action must fit inside the clipped content viewport.");
        Assert.True(layout.HeaderVisible);
        Assert.True(layout.HeaderStaysFixed);
        Assert.True(layout.TabsStayFixed);
        Assert.True(layout.RunControlStaysFixed);
    }

    [Theory]
    [InlineData("plan-target-short-bottom")]
    [InlineData("target-strategy-narrow-bottom")]
    [InlineData("plan-budget-short-bottom")]
    [InlineData("plan-budget-small-bottom")]
    [InlineData("plan-budget-short-bottom-en")]
    public void ShortPlanPagesScrollToTheirLastControlsWithoutMovingTabs(string scenario)
    {
        var result = RenderPhotometry(scenario);
        var scroll = Assert.IsType<PlanScrollDiagnostics>(result.PlanScroll);
        Assert.True(scroll.ViewportHeight > 0 && double.IsFinite(scroll.ViewportHeight));
        Assert.True(scroll.ScrollableHeight > 0);
        Assert.True(scroll.VerticalBarVisible);
        Assert.False(scroll.BottomInitiallyVisible);
        Assert.True(scroll.WheelScrolled, "Wheel events on content must scroll the page.");
        Assert.True(scroll.InputWheelScrolled, "Single-line text inputs must not trap the page's wheel events.");
        Assert.Equal(scroll.ScrollableHeight, scroll.FinalOffset, 1);
        Assert.True(scroll.BottomFinallyVisible, "Last control must be inside the actual clipped viewport.");
        Assert.True(scroll.ActionFinallyVisible, "Save action must be reachable, not just IsVisible.");
        Assert.True(scroll.TabsRemainVisible);
        if (scenario.EndsWith("-en", StringComparison.Ordinal))
            Assert.DoesNotContain(result.VisibleTexts, text => Regex.IsMatch(text, "[\\u3400-\\u9fff]"));
    }

    [Theory]
    [InlineData("target-strategy-auto")]
    [InlineData("plan-budget")]
    public void TallPlanPagesDoNotShowAnUnnecessaryScrollBar(string scenario)
    {
        var scroll = Assert.IsType<PlanScrollDiagnostics>(RenderPhotometry(scenario).PlanScroll);
        Assert.Equal(0, scroll.ScrollableHeight);
        Assert.False(scroll.VerticalBarVisible);
        Assert.True(scroll.BottomFinallyVisible);
        Assert.True(scroll.ActionFinallyVisible);
    }

    [Theory]
    [InlineData("plan-budget", "保存采集计划")]
    [InlineData("plan-budget-narrow", "保存采集计划")]
    [InlineData("plan-budget-en", "Save acquisition plan")]
    public void ExposureBudgetMaterializesFromTheProductionTemplate(string scenario, string action)
    {
        var result = RenderPhotometry(scenario);
        Assert.Contains(action, result.VisibleTexts);
        if (scenario.EndsWith("-en", StringComparison.Ordinal))
        {
            Assert.Contains(result.VisibleTexts, text => text.Contains("Accepted science integration", StringComparison.Ordinal));
            Assert.DoesNotContain(result.VisibleTexts, text => Regex.IsMatch(text, "[\\u3400-\\u9fff]"));
        }
        else
            Assert.Contains(result.VisibleTexts, text => text.Contains("合格科学累计", StringComparison.Ordinal));
    }

    [Fact]
    public void LiveSpectrumGetsFullWidthAndManualInspectorIsCollapsed()
    {
        var result = RenderPhotometry("atr-live");
        Assert.True(result.PreviewViewportWidth > 900, $"Preview width: {result.PreviewViewportWidth}");
        Assert.True(result.PreviewViewportHeight > 150, $"Preview height: {result.PreviewViewportHeight}");
        Assert.Contains(result.VisibleTexts, s => s.Contains("手动检查暂不可用", StringComparison.Ordinal));
        Assert.DoesNotContain("采集一帧检查光谱", result.VisibleTexts);
        Assert.DoesNotContain("N.I.N.A. 当前没有连接相机。", result.VisibleTexts);
        Assert.DoesNotContain("γ", result.VisibleTexts);
    }

    [Fact]
    public void DisplayLevelsExpandWithoutReintroducingSidePanel()
    {
        var result = RenderPhotometry("atr-levels");
        Assert.True(result.PreviewViewportWidth > 900);
        Assert.True(result.PreviewViewportHeight > 110);
        Assert.Contains("γ", result.VisibleTexts);
        Assert.DoesNotContain("采集一帧检查光谱", result.VisibleTexts);
    }

    [Theory]
    [InlineData("photometry-master", "复制主控编号", "记录所选设备（不连接）")]
    [InlineData("photometry-worker", "记录所选设备（不连接）", "保存测光端地址")]
    [InlineData("photometry-master-en", "Copy master ID", "Record selected devices (no connection)")]
    public void PairingShowsOnlyTheSelectedRolesActions(string scenario, string expected, string hidden)
    {
        var result = RenderPhotometry(scenario);
        Assert.Contains(expected, result.VisibleTexts);
        Assert.DoesNotContain(hidden, result.VisibleTexts);
        Assert.DoesNotContain(result.VisibleTexts, text => text.Contains("fixture-manifest", StringComparison.Ordinal));
        if (scenario.EndsWith("-en", StringComparison.Ordinal))
            Assert.DoesNotContain(result.VisibleTexts, text => Regex.IsMatch(text, "[\\u3400-\\u9fff]"));
    }

    [Fact]
    public void PausingUiDoesNotClaimStoppedAndKeepsRawStatusCollapsed()
    {
        var result = RenderPhotometry("photometry-worker-pausing");
        Assert.Contains(result.VisibleTexts, text => text.Contains("等待当前帧", StringComparison.Ordinal));
        Assert.DoesNotContain(result.VisibleTexts, text => text.Contains("fixture-manifest", StringComparison.Ordinal));
        Assert.DoesNotContain(result.VisibleTexts, text => text.Contains("Photometry · Running", StringComparison.Ordinal));
    }

    private static ScreenshotRenderResult RenderPhotometry(string scenario)
    {
        ScreenshotRenderResult? result = null;
        Exception? failure = null;
        RunOnUiThread(() =>
        {
            try { result = ScreenshotRenderer.RenderAll(ScenarioCatalog.Select(scenario),
                Path.Combine(Path.GetTempPath(), "uvex-photometry-roles", Guid.NewGuid().ToString("N"))).Single(); }
            catch (Exception ex) { failure = ex; }
        });
        Assert.Null(failure);
        return Assert.IsType<ScreenshotRenderResult>(result);
    }

    [Fact]
    public void WorkerTemplateRendersInEnglishWithoutChineseLeakage()
    {
        ScreenshotRenderResult? result = null;
        Exception? failure = null;
        RunOnUiThread(() =>
        {
            try { result = ScreenshotRenderer.RenderAll(ScenarioCatalog.Select("photometry-worker-en"),
                Path.Combine(Path.GetTempPath(), "uvex-worker-ui-tests", Guid.NewGuid().ToString("N"))).Single(); }
            catch (Exception ex) { failure = ex; }
        });
        Assert.Null(failure); Assert.NotNull(result);
        Assert.Contains("Photometry instance", result.VisibleTexts);
        Assert.DoesNotContain(result.VisibleTexts, text => Regex.IsMatch(text, "[\\u3400-\\u9fff]"));
    }

    [Fact]
    public void Renderer_UsesProductionTemplateAndWritesRequestedPngDimensions()
    {
        var output = Path.Combine(Path.GetTempPath(), "uvex-adv-ui-harness-tests", Guid.NewGuid().ToString("N"));
        ScreenshotRenderResult? result = null;
        Exception? failure = null;
        RunOnUiThread(() =>
        {
            try
            {
                result = ScreenshotRenderer.RenderAll(ScenarioCatalog.Select("idle"), output).Single();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        Assert.Null(failure);
        Assert.NotNull(result);
        Assert.True(File.Exists(result.AbsolutePath));
        Assert.Equal(64, result.Sha256.Length);

        var header = File.ReadAllBytes(result.AbsolutePath).AsSpan(0, 24);
        Assert.True(header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        Assert.Equal(result.Width, BinaryPrimitives.ReadInt32BigEndian(header[16..20]));
        Assert.Equal(result.Height, BinaryPrimitives.ReadInt32BigEndian(header[20..24]));
    }

    [Fact]
    public void EnglishFailureScenario_LocalizesMaterializedTemplateWithoutChineseLeakage()
    {
        var output = Path.Combine(Path.GetTempPath(), "uvex-adv-ui-harness-tests", Guid.NewGuid().ToString("N"));
        ScreenshotRenderResult? result = null;
        Exception? failure = null;
        RunOnUiThread(() =>
        {
            try
            {
                result = ScreenshotRenderer.RenderAll(ScenarioCatalog.Select("failure-en"), output).Single();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        Assert.Null(failure);
        Assert.NotNull(result);
        Assert.Equal("en-US", result.CultureName);
        Assert.Contains("Diagnostics and evidence", result.VisibleTexts);
        Assert.Contains("Current impact", result.VisibleTexts);
        Assert.Contains("Automatic handling", result.VisibleTexts);
        Assert.Contains("Recommended action", result.VisibleTexts);
        Assert.DoesNotContain(result.VisibleTexts, text => Regex.IsMatch(text, "[\\u3400-\\u9fff]"));
    }
}
