using System.Text.RegularExpressions;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class XamlBindingSafetyTests
{
    [Fact]
    public void ReadOnlyTextBoxBindingsAreExplicitlyOneWay()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Templates.xaml"));
        var readOnlyTextBoxes = Regex.Matches(
            xaml,
            "<TextBox\\b(?<attributes>[^>]*)IsReadOnly=\\\"True\\\"(?<tail>[^>]*)/?>",
            RegexOptions.CultureInvariant);

        Assert.NotEmpty(readOnlyTextBoxes);
        foreach (Match textBox in readOnlyTextBoxes)
        {
            var attributes = textBox.Groups["attributes"].Value + textBox.Groups["tail"].Value;
            var bindings = Regex.Matches(attributes, "\\{Binding(?<options>[^}]*)\\}", RegexOptions.CultureInvariant);
            foreach (Match binding in bindings)
            {
                Assert.True(
                    binding.Groups["options"].Value.Contains("Mode=OneWay", StringComparison.Ordinal) ||
                    binding.Groups["options"].Value.Contains("Mode=OneTime", StringComparison.Ordinal),
                    $"Read-only TextBox bindings must not use TextBox.Text's default TwoWay mode: {textBox.Value}");
            }
        }
    }

    [Fact]
    public void ReadOnlyProgressPropertiesAreBoundOneWay()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Templates.xaml"));
        var progressBindings = Regex.Matches(
            xaml,
            "Value=\\\"\\{Binding\\s+ProgressPercent(?<options>[^}]*)\\}\\\"",
            RegexOptions.CultureInvariant);

        Assert.NotEmpty(progressBindings);
        foreach (Match binding in progressBindings)
        {
            Assert.Contains("Mode=OneWay", binding.Groups["options"].Value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PluginOptionsSeparateScopeExtractionM2AndGratingSemantics()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Templates.xaml"));
        var start = xaml.IndexOf("x:Key=\"OpenAstroSpec Auto — UVEX4_Options\"", StringComparison.Ordinal);
        var end = xaml.IndexOf(
            "x:Key=\"UvexAdv.Nina.Plugin.ObservationDockable_Dockable\"",
            start,
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var options = xaml[start..end];

        Assert.Contains("OpenAstroSpec Auto · ATR/UVEX4 光谱工具设置", options, StringComparison.Ordinal);
        Assert.Contains("Header=\"范围与连接\"", options, StringComparison.Ordinal);
        Assert.Contains("Header=\"ATR 提取\"", options, StringComparison.Ordinal);
        Assert.Contains("Header=\"M2 对焦\"", options, StringComparison.Ordinal);
        Assert.Contains("Header=\"光栅锁定\"", options, StringComparison.Ordinal);
        Assert.Contains("配置自动观测界面中的 ATR 单帧检查", options, StringComparison.Ordinal);
        Assert.Contains("不配置 C11 主镜对焦、G3 导星镜对焦", options, StringComparison.Ordinal);
        Assert.Contains("软件提取矩形（原始全帧像素）", options, StringComparison.Ordinal);
        Assert.Contains("它与 C11 主镜、G3M2210M 导星模组小镜头", options, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding SpectralAutofocusCommissioned}\"", options, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding WavelengthLockCommissioned}\"", options, StringComparison.Ordinal);
        Assert.Contains("Text=\"单独授权 UVEX M2 谱线自动对焦（会产生 M2 机械运动）\"", options, StringComparison.Ordinal);
        Assert.Contains("Text=\"单独授权 UVEX 光栅波长锁定（会产生光栅机械运动）\"", options, StringComparison.Ordinal);
        Assert.Contains("Text=\"启用 ATR585M SDK 行回绕自动修复\"", options, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding BoundCameraId, Mode=OneWay}\" IsReadOnly=\"True\"", options, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding WavelengthReferencePixelText}\"", options, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding WavelengthTargetPixelText}\"", options, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding GratingStepsPerPixelText}\"", options, StringComparison.Ordinal);
        Assert.DoesNotContain("调试完成，允许闭环电机运动", options, StringComparison.Ordinal);
        Assert.DoesNotContain("步长/最小/最大/回差", options, StringComparison.Ordinal);
        Assert.DoesNotContain("参考像素/目标像素/steps per pixel", options, StringComparison.Ordinal);
        Assert.DoesNotContain("<ScrollViewer", options, StringComparison.Ordinal);
    }

    [Fact]
    public void ObservationDockIsNarrowFriendlyAndEveryControlIsHumanLabelled()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Templates.xaml"));
        var start = xaml.IndexOf(
            "x:Key=\"UvexAdv.Nina.Plugin.ObservationDockable_Dockable\"",
            StringComparison.Ordinal);
        var end = xaml.IndexOf(
            "x:Key=\"UvexAdv.Nina.Plugin.UvexCalibrationLibraryDockable_Dockable\"",
            start,
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var dock = xaml[start..end];

        Assert.Contains("Text=\"OpenAstroSpec Auto — UVEX4\"", dock, StringComparison.Ordinal);
        Assert.DoesNotContain("UVEX-ADV 自动观测", dock, StringComparison.Ordinal);
        Assert.DoesNotContain("MinWidth=\"920\"", dock, StringComparison.Ordinal);
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", dock, StringComparison.Ordinal);
        Assert.StartsWith("x:Key=\"UvexAdv.Nina.Plugin.ObservationDockable_Dockable\">\r\n    <Grid", dock.Replace("\n", "\r\n", StringComparison.Ordinal).Replace("\r\r\n", "\r\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("Content=\"模拟演练\"", dock, StringComparison.Ordinal);
        Assert.Contains("Content=\"真实设备\"", dock, StringComparison.Ordinal);
        Assert.Contains("StartSelectedModeCommand", dock, StringComparison.Ordinal);
        Assert.Contains("Header=\"失败诊断\"", dock, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RealModeStartupSummary\"", dock, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding RealModeStatusSummary}\"", dock, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ShowObservationPlanCommand}\"", dock, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ShowStartupRequirementsCommand}\"", dock, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RealModeStartupDetails\"", dock, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(dock, "Text=\\\"\\{Binding RealModeStatus\\}\\\"", RegexOptions.CultureInvariant).Cast<Match>());
        var overviewTabStart = dock.IndexOf("<TabItem Header=\"运行概览\">", StringComparison.Ordinal);
        var planTabStart = dock.IndexOf("<TabItem Header=\"观测计划\">", overviewTabStart, StringComparison.Ordinal);
        var realtimeTabStart = dock.IndexOf("<TabItem Header=\"实时图像\">", planTabStart, StringComparison.Ordinal);
        var failureTabStart = dock.IndexOf("<TabItem Header=\"失败诊断\">", realtimeTabStart, StringComparison.Ordinal);
        var advancedTabStart = dock.IndexOf("<TabItem Header=\"高级设置\">", failureTabStart, StringComparison.Ordinal);
        Assert.True(
            overviewTabStart >= 0 &&
            planTabStart > overviewTabStart &&
            realtimeTabStart > planTabStart &&
            failureTabStart > realtimeTabStart &&
            advancedTabStart > failureTabStart);
        Assert.DoesNotContain("<ScrollViewer", dock[overviewTabStart..planTabStart], StringComparison.Ordinal);
        Assert.DoesNotContain("<RowDefinition Height=\"*\"", dock[overviewTabStart..planTabStart], StringComparison.Ordinal);
        Assert.Contains("x:Name=\"OperationalReadinessSummary\"", dock[overviewTabStart..planTabStart], StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Phd2CalibrationOverviewGradeText}\"", dock[overviewTabStart..planTabStart], StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Phd2CalibrationOverviewText}\"", dock[overviewTabStart..planTabStart], StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding GhostAssistanceModeText}\"", dock[overviewTabStart..planTabStart], StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding GhostOverviewText}\"", dock[overviewTabStart..planTabStart], StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding Phd2CalibrationGradeText}\"", dock[overviewTabStart..planTabStart], StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding GhostAssistanceMode}\"", dock[overviewTabStart..planTabStart], StringComparison.Ordinal);
        Assert.DoesNotContain("Phd2CalibrationPolicyText", dock[overviewTabStart..planTabStart], StringComparison.Ordinal);
        Assert.DoesNotContain("Phd2CommissioningRouteText", dock[overviewTabStart..planTabStart], StringComparison.Ordinal);
        Assert.DoesNotContain("Phd2CalibrationScaleText", dock[overviewTabStart..planTabStart], StringComparison.Ordinal);
        Assert.DoesNotContain("Phd2CalibrationReasonText", dock[overviewTabStart..planTabStart], StringComparison.Ordinal);
        Assert.DoesNotContain("GhostCalibrationSummaryText", dock[overviewTabStart..planTabStart], StringComparison.Ordinal);
        Assert.DoesNotContain("GhostApplicabilityText", dock[overviewTabStart..planTabStart], StringComparison.Ordinal);
        Assert.DoesNotContain("GhostDecisionText", dock[overviewTabStart..planTabStart], StringComparison.Ordinal);
        Assert.DoesNotContain("<ScrollViewer", dock[planTabStart..realtimeTabStart], StringComparison.Ordinal);
        Assert.Contains("<ScrollViewer", dock[advancedTabStart..], StringComparison.Ordinal);
        Assert.Contains("Header=\"PHD2 与目标定位策略详情\"", dock[advancedTabStart..], StringComparison.Ordinal);
        Assert.Contains("Phd2CalibrationPolicyText", dock[advancedTabStart..], StringComparison.Ordinal);
        Assert.Contains("GhostCalibrationSummaryText", dock[advancedTabStart..], StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"操作与计划\"", dock, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding RealModeStatus}", dock[overviewTabStart..planTabStart], StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding TargetName", dock[planTabStart..realtimeTabStart], StringComparison.Ordinal);
        Assert.Contains("站点、40° 围墙与模拟速度", dock[advancedTabStart..], StringComparison.Ordinal);
        Assert.Contains("HasNoFailure", dock, StringComparison.Ordinal);
        Assert.Contains("HasFailure", dock, StringComparison.Ordinal);
        Assert.Contains("当前没有失败或待处理质量门", dock, StringComparison.Ordinal);
        Assert.Contains("TargetType=\"ListBox\"", dock, StringComparison.Ordinal);
        Assert.Contains("Value=\"#0F172A\"", dock, StringComparison.Ordinal);
        Assert.Contains("QHY 广域 / 解算", dock, StringComparison.Ordinal);
        Assert.Contains("G3 对焦 / 狭缝 / 导星", dock, StringComparison.Ordinal);
        Assert.Contains("ATR 二维 / 一维光谱", dock, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(dock, "<local:EmbeddedImageViewer\\b", RegexOptions.CultureInvariant).Count);
        Assert.Contains("Text=\"ATR 单帧检查\"", dock, StringComparison.Ordinal);
        Assert.Contains("Content=\"绑定当前 ATR585M\"", dock, StringComparison.Ordinal);
        Assert.Contains("Content=\"采集一帧检查光谱\"", dock, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding CaptureManualAtrSpectrumCommand}\"", dock, StringComparison.Ordinal);
        Assert.Contains("Points=\"{Binding ManualSpectrumPoints}\"", dock, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"影子采集\"", dock, StringComparison.Ordinal);
        Assert.DoesNotContain("UvexAdv.Nina.Plugin.UvexDockable_Dockable", xaml, StringComparison.Ordinal);
        Assert.Contains("当前没有失败或待处理质量门", dock, StringComparison.Ordinal);
        Assert.DoesNotContain("没有需要检查的失败图像", dock, StringComparison.Ordinal);

        foreach (Match button in Regex.Matches(dock, "<Button\\b(?<attributes>[^>]*)>", RegexOptions.CultureInvariant))
        {
            Assert.Contains("Content=", button.Groups["attributes"].Value, StringComparison.Ordinal);
        }
        foreach (Match checkBox in Regex.Matches(dock, "<CheckBox\\b(?<attributes>[^>]*)>", RegexOptions.CultureInvariant))
        {
            Assert.Contains("Content=", checkBox.Groups["attributes"].Value, StringComparison.Ordinal);
        }
    }
}
