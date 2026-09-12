using System.Buffers.Binary;
using System.IO;
using System.Text.RegularExpressions;
using UvexAdv.Nina.Plugin.UiHarness;

namespace UvexAdv.Nina.Plugin.UiHarness.Tests;

public sealed class ScreenshotRendererTests
{
    [Theory]
    [InlineData("plan-target-short-bottom")]
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
    [InlineData("plan-target")]
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
        var thread = new Thread(() =>
        {
            try { result = ScreenshotRenderer.RenderAll(ScenarioCatalog.Select(scenario),
                Path.Combine(Path.GetTempPath(), "uvex-photometry-roles", Guid.NewGuid().ToString("N"))).Single(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        Assert.Null(failure);
        return Assert.IsType<ScreenshotRenderResult>(result);
    }

    [Fact]
    public void WorkerTemplateRendersInEnglishWithoutChineseLeakage()
    {
        ScreenshotRenderResult? result = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = ScreenshotRenderer.RenderAll(ScenarioCatalog.Select("photometry-worker-en"),
                Path.Combine(Path.GetTempPath(), "uvex-worker-ui-tests", Guid.NewGuid().ToString("N"))).Single(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
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
        var thread = new Thread(() =>
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
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The offline WPF render did not finish within 30 seconds.");

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
        var thread = new Thread(() =>
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
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The English offline WPF render did not finish within 30 seconds.");

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
