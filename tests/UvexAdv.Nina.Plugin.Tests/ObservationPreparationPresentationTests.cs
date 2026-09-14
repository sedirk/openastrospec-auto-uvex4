using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class ObservationPreparationPresentationTests
{
    private static readonly string Source = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "Sources", "ObservationDockable.cs"));

    [Theory]
    [InlineData(0, false, false, 0)]
    [InlineData(0, true, false, 1)]
    [InlineData(0, false, true, 1)]
    [InlineData(3, true, true, 5)]
    public void ChecklistCannotIgnoreMissingTargetOrInvalidSlit(
        int staticIssues, bool targetMissing, bool slitMissing, int expected)
    {
        Assert.Equal(expected, ObservationDockable.CountPreparationChecklistIssues(
            staticIssues, targetMissing, slitMissing));
        Assert.Contains("AutomaticPreparationIssueCount => RealModeEligibilityIssues().Count", Source);
        Assert.Contains("AutomaticPreparationIssueCount, IsTargetPreparationMissing, IsSlitChoiceMissing", Source);
        Assert.Contains("FormatPreparationSummary(PreparationChecklistIssueCount, UiCulture)", Source);
    }

    [Theory]
    [InlineData("zh-CN", "静态", "尚未检查", "3")]
    [InlineData("en-US", "static", "have not been checked", "3")]
    public void SummaryDistinguishesStaticCompletenessFromLiveReadiness(
        string cultureName, string staticLabel, string notChecked, string count)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        var complete = ObservationDockable.FormatPreparationSummary(0, culture);
        Assert.Contains(staticLabel, complete);
        Assert.Contains(notChecked, complete);
        Assert.DoesNotContain("✓", complete);
        var incomplete = ObservationDockable.FormatPreparationSummary(3, culture);
        Assert.Contains(staticLabel, incomplete);
        Assert.Contains(count, incomplete);
    }

    [Theory]
    [InlineData("zh-CN", "观测目标", "设备身份", "安装标定", "本夜配置", "期望狭缝")]
    [InlineData("en-US", "Target", "Device identities", "Commissioning", "Night Setup", "Expected slit")]
    public void ChecklistGroupsFivePreparationResponsibilities(
        string cultureName, string target, string devices, string commissioning, string night, string slit)
    {
        var actions = ObservationDockable.BuildPreparationChecklistIssues(
            [], true, true, true, true, true, CultureInfo.GetCultureInfo(cultureName));
        Assert.Equal(5, actions.Count);
        foreach (var label in new[] { target, devices, commissioning, night, slit })
            Assert.Contains(actions, action => action.StartsWith(label, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("zh-CN", "设备身份", "安装标定", "其他静态条件")]
    [InlineData("en-US", "Device identities", "Commissioning", "Other static requirements")]
    public void StaticReasonsAreDeduplicatedWithoutLeakingEngineeringValues(
        string cultureName, string devices, string commissioning, string fallback)
    {
        const string privateValue = "private-camera-id-C:\\machine-local\\bindings.json-SHA256-ABCDEF";
        var actions = ObservationDockable.BuildPreparationChecklistIssues(
            ["缺少真实 ATR585M DeviceId。" + privateValue,
             "PHD2 Profile 不完整。" + privateValue,
             "commissioning preset 无法复核：" + privateValue,
             "未知静态错误：" + privateValue],
            false, true, true, false, false, CultureInfo.GetCultureInfo(cultureName));
        Assert.Equal(3, actions.Count);
        Assert.Single(actions, action => action.StartsWith(devices, StringComparison.Ordinal));
        Assert.Single(actions, action => action.StartsWith(commissioning, StringComparison.Ordinal));
        Assert.Single(actions, action => action.StartsWith(fallback, StringComparison.Ordinal));
        Assert.DoesNotContain(privateValue, string.Join("\n", actions));
        Assert.DoesNotContain("SHA256", string.Join("\n", actions));
    }

    [Theory]
    [InlineData("亮目标 QHY WCS/G3 帧新鲜度必须显式设为正数。", "亮星翼部")]
    [InlineData("G3 WCS 居中：无效参数。", "解算与居中")]
    [InlineData("G3 搜索步长超过设备标定的单次运动上限。", "邻场与导星")]
    [InlineData("N.I.N.A. 文件模板缺少目标。", "数据归档")]
    [InlineData("QHY/G3 快速配对：无效参数。", "双相机配对")]
    [InlineData("QHY H 并行曝光必须是正有限秒数。", "并行测光")]
    [InlineData("全无人监管要求安全监视器。", "安全策略")]
    public void RemainingStaticReasonsPointToTheResponsibleSettings(string issue, string category)
    {
        var action = Assert.Single(ObservationDockable.BuildPreparationChecklistIssues(
            [issue], false, false, false, false, false, CultureInfo.GetCultureInfo("zh-CN")));
        Assert.StartsWith(category, action);
    }

    [Fact]
    public void EmptyStaticChecklistDoesNotInventAnAction()
    {
        Assert.Empty(ObservationDockable.BuildPreparationChecklistIssues(
            [], false, false, false, false, false, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void CategorySelectionIsBoundedPresentationOnlyState()
    {
        // Deliberately skip the constructor: navigation properties only notify
        // the UI, and must not need settings, owner clients, sockets or devices.
        var dock = (ObservationDockable)RuntimeHelpers.GetUninitializedObject(typeof(ObservationDockable));
        dock.SelectedAdvancedCategoryIndex = 9;
        dock.SelectedPreparationTabIndex = 1;
        Assert.Equal(9, dock.SelectedAdvancedCategoryIndex);
        Assert.Equal(1, dock.SelectedPreparationTabIndex);
        dock.SelectedAdvancedCategoryIndex = -1;
        dock.SelectedAdvancedCategoryIndex = 10;
        dock.SelectedPreparationTabIndex = -1;
        dock.SelectedPreparationTabIndex = 2;
        Assert.Equal(9, dock.SelectedAdvancedCategoryIndex);
        Assert.Equal(1, dock.SelectedPreparationTabIndex);
        dock.SelectedAdvancedCategoryIndex = 0;
        dock.SelectedPreparationTabIndex = 0;
        Assert.Equal(0, dock.SelectedAdvancedCategoryIndex);
        Assert.Equal(0, dock.SelectedPreparationTabIndex);
    }

    [Theory]
    [InlineData("showDeviceBindingsSettingsCommand", "SelectedAdvancedCategoryIndex", 9, 6)]
    [InlineData("showSafetySettingsCommand", "SelectedAdvancedCategoryIndex", 2, 6)]
    [InlineData("showPreparationChecklistCommand", "SelectedPreparationTabIndex", 0, 3)]
    [InlineData("showNightSetupPreparationCommand", "SelectedPreparationTabIndex", 1, 3)]
    public void DeepLinksOnlySelectTheirDestination(
        string command, string categoryProperty, int category, int workspace)
    {
        var start = Source.IndexOf(command + " = new SimpleCommand(() =>", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = Source.IndexOf("});", start, StringComparison.Ordinal);
        var body = Source[start..end];
        Assert.Contains($"{categoryProperty} = {category};", body);
        Assert.Contains($"SelectedWorkspaceTabIndex = {workspace};", body);
        Assert.Equal(2, Regex.Matches(body, @"(?m)^\s*Selected\w+ = \d+;").Count);
        Assert.DoesNotContain("settings.", body);
        Assert.DoesNotContain("host.", body);
        Assert.DoesNotContain("Execute", body);
        Assert.Contains("showAdvancedSettingsCommand = new SimpleCommand(() => SelectedWorkspaceTabIndex = 6)", Source);
    }

    [Theory]
    [InlineData("SelectedPreparationSpectralRegion")]
    [InlineData("SelectedPreparationCalibrationReference")]
    [InlineData("SelectedPreparationSafetyCapability")]
    [InlineData("PreparationOrderSortingFilterInstalled")]
    [InlineData("SelectedTelescopeCandidate")]
    [InlineData("SelectedAtrCameraCandidate")]
    [InlineData("SelectedG3CameraCandidate")]
    [InlineData("SelectedQhyCameraCandidate")]
    public void PreparationEditsCheckTheExistingIdleBoundaryBeforeMutating(string property)
    {
        var declaration = Regex.Match(Source, @"public [^\r\n]+ " + property + @"\s*\{");
        Assert.True(declaration.Success);
        var setter = Source.IndexOf("set", declaration.Index + declaration.Length, StringComparison.Ordinal);
        var bodyStart = Source.IndexOf('{', setter);
        var firstStatement = Source[(bodyStart + 1)..].TrimStart();
        Assert.StartsWith("if (!CanEditTargetPlan()) return;", firstStatement);
    }

    [Fact]
    public void SummariesDoNotInferSafetyFromDraftIntentOrClaimSlitArrival()
    {
        Assert.Contains("PreparationSafetySummary => settings.WeakSupervisionEnabled", Source);
        Assert.Contains("SelectedPreparationSafetyCapabilityDescription => settings.WeakSupervisionEnabled", Source);
        Assert.Contains("this is a configuration requirement, not live position or arrival evidence", Source);
        var start = Source.IndexOf("public string PreparationDeviceSummary", StringComparison.Ordinal);
        var end = Source.IndexOf("public bool IsCommissioningPreparationMissing", start, StringComparison.Ordinal);
        var summary = Source[start..end];
        Assert.DoesNotContain("settings.", summary);
        Assert.Contains("does not mean devices are connected or verified", summary);
    }
}
