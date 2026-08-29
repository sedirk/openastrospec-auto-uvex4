using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class StaticXamlLocalizationTests
{
    private static readonly Regex Cjk = new(
        "[\\u3400-\\u9fff]",
        RegexOptions.CultureInvariant);

    [Theory]
    [InlineData("运行概览", "Overview")]
    [InlineData("设备手控", "Device controls")]
    [InlineData("诊断与证据", "Diagnostics and evidence")]
    [InlineData("高级设置", "Advanced settings")]
    [InlineData("当前问题", "Current issue")]
    [InlineData("保存当前高级设置", "Save current advanced settings")]
    [InlineData("错误 / 阻断", "Error / blocked")]
    public void MainObservationLabelsFollowSelectedUiCulture(string chinese, string english)
    {
        Assert.Equal(chinese, ObservationStaticTextLocalization.Translate(chinese, CultureInfo.GetCultureInfo("zh-CN")));
        Assert.Equal(english, ObservationStaticTextLocalization.Translate(chinese, CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void EveryChineseStaticTemplateLiteralHasAnEnglishTranslation()
    {
        var xaml = string.Join(
            Environment.NewLine,
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Templates.xaml")),
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "EmbeddedImageViewer.xaml")));
        var values = Regex.Matches(
                xaml,
                "(?s)\\b(?:Text|Content|Header|ToolTip|EmptyTitle|EmptyDetails|PopoutLabel|AutomationProperties\\.Name)=\\\"(?<value>[^\\\"]*)\\\"",
                RegexOptions.CultureInvariant)
            .Select(match => WebUtility.HtmlDecode(match.Groups["value"].Value))
            .Where(value => !value.StartsWith('{') && Cjk.IsMatch(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.True(values.Length >= 300, $"Expected broad Templates.xaml coverage, found only {values.Length} literals.");
        foreach (var value in values)
        {
            Assert.True(
                ObservationStaticTextCatalog.EnglishTranslations.ContainsKey(value),
                $"Missing exact English translation for: {value}");

            var translated = ObservationStaticTextCatalog.Translate(value, CultureInfo.GetCultureInfo("en-US"));
            Assert.False(Cjk.IsMatch(translated), $"English UI leaked Chinese text: {translated}");
        }
    }

    [Fact]
    public void DiagnosticBindingFormatsAreLocalizedWithoutChangingMachineValues()
    {
        var formats = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["暂停原因：{0}"] = "Pause reason: {0}",
            ["错误代码：{0}"] = "Error code: {0}",
            ["质量数值：{0}"] = "Quality metrics: {0}",
            ["建议：{0}"] = "Recommendation: {0}",
            ["失败证据：{0}"] = "Failure evidence: {0}",
            ["最近证据：{0}"] = "Latest evidence: {0}",
            ["代码：{0}"] = "Code: {0}",
            ["数值：{0}"] = "Metrics: {0}",
            ["证据：{0}"] = "Evidence: {0}",
            ["策略：{0}"] = "Policy: {0}",
        };

        foreach (var (chinese, english) in formats)
        {
            Assert.Equal(english, ObservationStaticTextCatalog.Translate(chinese, CultureInfo.GetCultureInfo("en-US")));
            Assert.EndsWith("{0}", english, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryMajorTemplateEnablesInheritedLocalization()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Templates.xaml"));

        Assert.Contains(
            "<Grid Margin=\"8\" local:ObservationStaticTextLocalization.IsEnabled=\"True\">",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Grid Margin=\"4\" Background=\"#0B1220\" local:ObservationStaticTextLocalization.IsEnabled=\"True\">",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "local:ObservationStaticTextLocalization.IsEnabled=\"True\"",
            xaml[xaml.IndexOf("UvexCalibrationLibraryDockable_Dockable", StringComparison.Ordinal)..],
            StringComparison.Ordinal);

        Assert.True(
            Regex.Matches(
                xaml,
                "local:ObservationStaticTextLocalization\\.IsEnabled=\\\"True\\\"",
                RegexOptions.CultureInvariant).Count >= 10,
            "All major dockable and sequencer template roots must opt into localization.");

        var viewer = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "EmbeddedImageViewer.xaml"));
        Assert.Contains("local:ObservationStaticTextLocalization.IsEnabled=\"True\"", viewer, StringComparison.Ordinal);
    }
}
