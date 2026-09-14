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
    public void AdvancedCategoryNavigationDoesNotMutateBrightTargetAuthorization()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Templates.xaml"));

        var document = System.Xml.Linq.XDocument.Parse(xaml);
        var advanced = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "TabItem" && element.Attribute("Header")?.Value == "高级设置");
        Assert.DoesNotContain(advanced.Descendants(), element => element.Name.LocalName == "Expander");
        var sections = Assert.Single(advanced.Descendants(), element =>
            element.Name.LocalName == "TabControl" && element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name" && attribute.Value == "AdvancedSettingsSections"));
        Assert.Equal("Left", sections.Attribute("TabStripPlacement")?.Value);
        Assert.Equal("{Binding SelectedAdvancedCategoryIndex, Mode=TwoWay}", sections.Attribute("SelectedIndex")?.Value);
        Assert.Null(sections.Attribute("SelectedItem"));
        var categories = sections.Elements().Where(element => element.Name.LocalName == "TabItem").ToArray();
        Assert.Equal(new[] { "运行与归档", "后台接口", "站点与模拟", "解算与居中", "邻场与导星",
            "双相机配对", "并行测光", "鬼影辅助", "亮星翼部", "设备绑定" },
            categories.Select(element => element.Attribute("Header")?.Value));
        foreach (var category in categories)
        {
            var scroll = Assert.Single(category.Elements());
            Assert.Equal("ScrollViewer", scroll.Name.LocalName);
            Assert.Equal("Auto", scroll.Attribute("VerticalScrollBarVisibility")?.Value);
            Assert.Equal("Disabled", scroll.Attribute("HorizontalScrollBarVisibility")?.Value);
            Assert.Equal("False", scroll.Attribute("CanContentScroll")?.Value);
            Assert.Equal("VerticalOnly", scroll.Attribute("PanningMode")?.Value);
        }
        var bright = categories[8].ToString();
        Assert.Contains("超亮目标：饱和核的未饱和翼部入缝（例外分支，默认关闭）", bright, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IsExpanded=\"{Binding BrightTargetWingCentroidEnabled",
            bright,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsChecked=\"{Binding BrightTargetWingCentroidEnabled}\"",
            bright,
            StringComparison.Ordinal);
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

        Assert.Contains("OpenAstroSpec Auto · 光谱相机/UVEX4 光谱工具设置", options, StringComparison.Ordinal);
        Assert.Contains("Header=\"范围与连接\"", options, StringComparison.Ordinal);
        Assert.Contains("Header=\"光谱相机提取\"", options, StringComparison.Ordinal);
        Assert.Contains("Header=\"M2 对焦\"", options, StringComparison.Ordinal);
        Assert.Contains("Header=\"光栅锁定\"", options, StringComparison.Ordinal);
        Assert.Contains("配置自动观测界面中的光谱相机单帧检查", options, StringComparison.Ordinal);
        Assert.Contains("不配置主镜对焦、光谱仪导星相机对焦", options, StringComparison.Ordinal);
        Assert.Contains("软件提取矩形（原始全帧像素）", options, StringComparison.Ordinal);
        Assert.Contains("它与主镜、光谱仪导星相机模组小镜头", options, StringComparison.Ordinal);
        Assert.Contains("SelectedValue=\"{Binding M2FocusMode, Mode=TwoWay}\"", options, StringComparison.Ordinal);
        Assert.Contains("保持当前位置（默认，不移动 M2）", options, StringComparison.Ordinal);
        Assert.Contains("CommissionedSpectralAutofocus", options, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding WavelengthLockCommissioned}\"", options, StringComparison.Ordinal);
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
    public void AutomaticPreparationUsesNeutralSurfacesAndNarrowSemanticAccents()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Templates.xaml"));
        var dockStart = xaml.IndexOf(
            "x:Key=\"UvexAdv.Nina.Plugin.ObservationDockable_Dockable\"",
            StringComparison.Ordinal);
        var preparationStart = xaml.IndexOf("<TabItem Header=\"自动准备\">", dockStart, StringComparison.Ordinal);
        var preparationEnd = xaml.IndexOf("<TabItem Header=\"实时图像\">", preparationStart, StringComparison.Ordinal);
        var fieldStyleStart = xaml.IndexOf("x:Key=\"PreparationFieldBorder\"", dockStart, StringComparison.Ordinal);
        var fieldStyleEnd = xaml.IndexOf("<Style TargetType=\"CheckBox\">", fieldStyleStart, StringComparison.Ordinal);
        var comboStyleStart = xaml.IndexOf("<Style TargetType=\"ComboBox\">", dockStart, StringComparison.Ordinal);
        var comboStyleEnd = xaml.IndexOf("x:Key=\"PreparationPanelBorder\"", comboStyleStart, StringComparison.Ordinal);

        Assert.True(
            dockStart >= 0 &&
            preparationStart > dockStart &&
            preparationEnd > preparationStart &&
            fieldStyleStart > dockStart &&
            fieldStyleEnd > fieldStyleStart &&
            comboStyleStart > dockStart &&
            comboStyleEnd > comboStyleStart);

        var preparation = xaml[preparationStart..preparationEnd];
        var fieldStyle = xaml[fieldStyleStart..fieldStyleEnd];
        var comboStyle = xaml[comboStyleStart..comboStyleEnd];

        Assert.Contains("Style=\"{StaticResource PreparationIntroBorder}\"", preparation, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource PreparationPanelBorder}\"", preparation, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource PreparationSecondaryButton}\"", preparation, StringComparison.Ordinal);
        Assert.DoesNotContain("Background=\"#10243B\"", preparation, StringComparison.Ordinal);
        Assert.DoesNotContain("BorderBrush=\"#38BDF8\"", preparation, StringComparison.Ordinal);

        Assert.Contains("Value=\"{StaticResource PreparationSurfaceBrush}\"", fieldStyle, StringComparison.Ordinal);
        Assert.Contains("Value=\"3,1,1,1\"", fieldStyle, StringComparison.Ordinal);
        Assert.Contains("Value=\"{StaticResource PreparationAttentionBrush}\"", fieldStyle, StringComparison.Ordinal);
        Assert.DoesNotContain("Value=\"#0E2A1B\"", fieldStyle, StringComparison.Ordinal);
        Assert.DoesNotContain("Value=\"#3F1515\"", fieldStyle, StringComparison.Ordinal);

        Assert.Contains("x:Name=\"ObservationComboChrome\"", comboStyle, StringComparison.Ordinal);
        Assert.Contains("Background=\"#0F172A\"", comboStyle, StringComparison.Ordinal);
        Assert.Contains("Property=\"IsEnabled\" Value=\"False\"", comboStyle, StringComparison.Ordinal);
        Assert.DoesNotContain("Value=\"#F8FAFC\"", comboStyle, StringComparison.Ordinal);
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
        Assert.Contains("Content=\"模拟\"", dock, StringComparison.Ordinal);
        Assert.Contains("Content=\"真实\"", dock, StringComparison.Ordinal);
        Assert.Contains("Content=\"设备手控\"", dock, StringComparison.Ordinal);
        Assert.Contains("StartSelectedModeCommand", dock, StringComparison.Ordinal);
        Assert.Contains("Content=\"新开一轮\"", dock, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding RestartWithCurrentConfigurationCommand}\"", dock, StringComparison.Ordinal);
        Assert.Contains("不会把新设置热替换进旧运行", dock, StringComparison.Ordinal);
        Assert.Contains("Header=\"诊断与证据\"", dock, StringComparison.Ordinal);
        Assert.Contains("Header=\"当前问题\"", dock, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RealModeStartupSummary\"", dock, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding RealModeStatusSummary}\"", dock, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ShowObservationPlanCommand}\"", dock, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ShowStartupRequirementsCommand}\"", dock, StringComparison.Ordinal);
        Assert.Contains("Content=\"保存当前高级设置\"", dock, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding SaveAdvancedSettingsCommand}\"", dock, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding AdvancedSettingsSaveStatus}\"", dock, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RealModeStartupDetails\"", dock, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(dock, "Text=\\\"\\{Binding RealModeStatus\\}\\\"", RegexOptions.CultureInvariant).Cast<Match>());
        var overviewTabStart = dock.IndexOf("<TabItem Header=\"运行概览\">", StringComparison.Ordinal);
        var manualTabStart = dock.IndexOf("<TabItem Header=\"设备手控\">", overviewTabStart, StringComparison.Ordinal);
        var planTabStart = dock.IndexOf("<TabItem Header=\"观测计划\">", manualTabStart, StringComparison.Ordinal);
        var preparationTabStart = dock.IndexOf("<TabItem Header=\"自动准备\">", planTabStart, StringComparison.Ordinal);
        var realtimeTabStart = dock.IndexOf("<TabItem Header=\"实时图像\">", preparationTabStart, StringComparison.Ordinal);
        var failureTabStart = dock.IndexOf("<TabItem Header=\"诊断与证据\">", realtimeTabStart, StringComparison.Ordinal);
        var advancedTabStart = dock.IndexOf("<TabItem Header=\"高级设置\">", failureTabStart, StringComparison.Ordinal);
        Assert.True(
            overviewTabStart >= 0 &&
            manualTabStart > overviewTabStart &&
            planTabStart > manualTabStart &&
            preparationTabStart > planTabStart &&
            realtimeTabStart > planTabStart &&
            failureTabStart > realtimeTabStart &&
            advancedTabStart > failureTabStart);
        Assert.DoesNotContain("<ScrollViewer", dock[overviewTabStart..manualTabStart], StringComparison.Ordinal);
        Assert.Contains("<local:ObservationWorkflowView", dock[overviewTabStart..manualTabStart], StringComparison.Ordinal);
        Assert.Contains("Graph=\"{Binding Workflow, Mode=OneWay}\"", dock[overviewTabStart..manualTabStart], StringComparison.Ordinal);
        Assert.DoesNotContain("<Expander", dock[overviewTabStart..manualTabStart], StringComparison.Ordinal);
        Assert.DoesNotContain("自动流程（真实成功路线）", dock[overviewTabStart..manualTabStart], StringComparison.Ordinal);
        Assert.Contains("G3WcsFreshSolveAuthorizationResidualArcseconds", dock, StringComparison.Ordinal);
        Assert.Contains("大步后允许拍 fresh 验证帧的最大实报终点残差", dock, StringComparison.Ordinal);
        Assert.Contains("目标与定位策略", dock[overviewTabStart..manualTabStart], StringComparison.Ordinal);
        Assert.Contains("观测准备", dock[overviewTabStart..manualTabStart], StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding Phd2CalibrationGradeText}\"", dock[overviewTabStart..manualTabStart], StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding GhostAssistanceMode}\"", dock[overviewTabStart..manualTabStart], StringComparison.Ordinal);
        Assert.DoesNotContain("Phd2CalibrationPolicyText", dock[overviewTabStart..manualTabStart], StringComparison.Ordinal);
        Assert.DoesNotContain("Phd2CommissioningRouteText", dock[overviewTabStart..manualTabStart], StringComparison.Ordinal);
        Assert.DoesNotContain("Phd2CalibrationScaleText", dock[overviewTabStart..manualTabStart], StringComparison.Ordinal);
        Assert.DoesNotContain("Phd2CalibrationReasonText", dock[overviewTabStart..manualTabStart], StringComparison.Ordinal);
        Assert.DoesNotContain("GhostCalibrationSummaryText", dock[overviewTabStart..manualTabStart], StringComparison.Ordinal);
        Assert.DoesNotContain("GhostApplicabilityText", dock[overviewTabStart..manualTabStart], StringComparison.Ordinal);
        Assert.DoesNotContain("GhostDecisionText", dock[overviewTabStart..manualTabStart], StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ManualUvexControlScrollViewer\"", dock[manualTabStart..planTabStart], StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ObservationPlanTabs\"", dock[planTabStart..preparationTabStart], StringComparison.Ordinal);
        var planTabs = System.Xml.Linq.XDocument.Parse(xaml).Descendants()
            .Single(element => element.Name.LocalName == "TabControl" &&
                element.Attributes().Any(attribute => attribute.Name.LocalName == "Name" && attribute.Value == "ObservationPlanTabs"));
        Assert.DoesNotContain(planTabs.Ancestors(), element => element.Name.LocalName == "ScrollViewer");
        var planPages = planTabs.Elements().Where(element => element.Name.LocalName == "TabItem").ToArray();
        Assert.Equal(2, planPages.Length);
        foreach (var page in planPages)
        {
            var scroll = Assert.Single(page.Elements());
            Assert.Equal("ScrollViewer", scroll.Name.LocalName);
            Assert.Equal("Auto", scroll.Attribute("VerticalScrollBarVisibility")?.Value);
            Assert.Equal("Disabled", scroll.Attribute("HorizontalScrollBarVisibility")?.Value);
            Assert.Equal("False", scroll.Attribute("CanContentScroll")?.Value);
            Assert.Equal("VerticalOnly", scroll.Attribute("PanningMode")?.Value);
        }
        Assert.Contains("Header=\"采集计划（曝光预算）\"", dock[planTabStart..preparationTabStart], StringComparison.Ordinal);
        Assert.Contains("DataContext=\"{Binding AcquisitionPlan}\"", dock[planTabStart..preparationTabStart], StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding SaveCommand}\"", dock[planTabStart..preparationTabStart], StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AutomaticPreparationScrollViewer\"", dock[preparationTabStart..realtimeTabStart], StringComparison.Ordinal);
        Assert.Contains("<ScrollViewer", dock[advancedTabStart..], StringComparison.Ordinal);
        Assert.Contains("Text=\"PHD2 与目标定位策略详情\"", dock[advancedTabStart..], StringComparison.Ordinal);
        Assert.Contains("旧版测光相机广域运动门（仅保留兼容/取证，不是当前生产路线）", dock[advancedTabStart..], StringComparison.Ordinal);
        Assert.Contains("2″只约束静态 frame/mount 绑定", dock[advancedTabStart..], StringComparison.Ordinal);
        Assert.Contains("Phd2CalibrationPolicyText", dock[advancedTabStart..], StringComparison.Ordinal);
        Assert.Contains("GhostCalibrationSummaryText", dock[advancedTabStart..], StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"操作与计划\"", dock, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding RealModeStatus}", dock[overviewTabStart..manualTabStart], StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding TargetName", dock[planTabStart..preparationTabStart], StringComparison.Ordinal);
        Assert.Contains("并行观测与初始地平线规划时长（分钟）", dock[planTabStart..preparationTabStart], StringComparison.Ordinal);
        Assert.Contains("不是曝光倒计时", dock[planTabStart..preparationTabStart], StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource PreparationFieldBorder}\"", dock[preparationTabStart..realtimeTabStart], StringComparison.Ordinal);
        Assert.Contains("Tag=\"{Binding IsTargetPreparationMissing}\"", dock[preparationTabStart..realtimeTabStart], StringComparison.Ordinal);
        Assert.Contains("Content=\"导入完整标定包…\"", dock[advancedTabStart..], StringComparison.Ordinal);
        Assert.Contains("Content=\"生成准备草稿\"", dock[preparationTabStart..realtimeTabStart], StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding PreparationSpectralRegionChoices}\"", dock[preparationTabStart..realtimeTabStart], StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding PreparationCalibrationReferenceChoices}\"", dock[preparationTabStart..realtimeTabStart], StringComparison.Ordinal);
        foreach (var setting in new[] { "PreparationSafetyCapabilityChoices", "CommissioningProfiles",
                     "TelescopeCandidates", "AtrCameraCandidates", "G3CameraCandidates", "QhyCameraCandidates" })
        {
            Assert.DoesNotContain($"ItemsSource=\"{{Binding {setting}}}\"", dock[preparationTabStart..realtimeTabStart], StringComparison.Ordinal);
            Assert.Contains($"ItemsSource=\"{{Binding {setting}}}\"", dock[advancedTabStart..], StringComparison.Ordinal);
        }
        Assert.Contains("SelectedValue=\"{Binding ExpectedUvexSlitPosition", dock[preparationTabStart..realtimeTabStart], StringComparison.Ordinal);
        Assert.Contains("站点、地平线与模拟速度", dock[advancedTabStart..], StringComparison.Ordinal);
        Assert.Contains("HasNoFailure", dock, StringComparison.Ordinal);
        Assert.Contains("HasFailure", dock, StringComparison.Ordinal);
        Assert.Contains("当前没有错误或待处理证据门", dock, StringComparison.Ordinal);
        Assert.Contains("TargetType=\"ListBox\"", dock, StringComparison.Ordinal);
        Assert.Contains("Value=\"#0F172A\"", dock, StringComparison.Ordinal);
        Assert.Contains("测光相机 · 定位", dock, StringComparison.Ordinal);
        Assert.Contains("光谱仪导星相机", dock, StringComparison.Ordinal);
        Assert.Contains("光谱相机 · 光谱", dock, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(dock, "<local:EmbeddedImageViewer\\b", RegexOptions.CultureInvariant).Count);
        Assert.Equal(3, Regex.Matches(dock, "PopoutCommand=", RegexOptions.CultureInvariant).Count);
        Assert.DoesNotContain("<Button Grid.Row=\"1\" Content=\"弹出", dock, StringComparison.Ordinal);
        Assert.Contains("Text=\"光谱相机单帧检查\"", dock, StringComparison.Ordinal);
        Assert.Contains("Content=\"绑定当前光谱相机\"", dock, StringComparison.Ordinal);
        Assert.Contains("Content=\"采集一帧检查光谱\"", dock, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding SelectManualSlit1Command}\"", dock[manualTabStart..planTabStart], StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding MoveManualM2PositiveCommand}\"", dock[manualTabStart..planTabStart], StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ManualSlitLightOffCommand}\"", dock[manualTabStart..planTabStart], StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding ManualUvexDeviceChoices}\"", dock[manualTabStart..planTabStart], StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedManualUvexDevice, Mode=TwoWay}\"", dock[manualTabStart..planTabStart], StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ConnectManualUvexCommand}\"", dock[manualTabStart..planTabStart], StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding DisconnectManualUvexCommand}\"", dock[manualTabStart..planTabStart], StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ReleaseManualUvexComPortCommand}\"", dock[manualTabStart..planTabStart], StringComparison.Ordinal);
        Assert.Contains("打开本页不会连接", dock[manualTabStart..planTabStart], StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding CaptureManualAtrSpectrumCommand}\"", dock, StringComparison.Ordinal);
        Assert.Contains("Points=\"{Binding ManualSpectrumPoints}\"", dock, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"影子采集\"", dock, StringComparison.Ordinal);
        Assert.DoesNotContain("UvexAdv.Nina.Plugin.UvexDockable_Dockable", xaml, StringComparison.Ordinal);
        Assert.Contains("当前没有错误或待处理证据门", dock, StringComparison.Ordinal);
        Assert.DoesNotContain("没有需要检查的失败图像", dock, StringComparison.Ordinal);

        Assert.Contains("Text=\"LLM 后台：\"", dock, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding ModelAutomationBridgeEnabled, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"", dock, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"OpenAstroSpec.ModelAutomationBridgeEnabled\"", dock, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding ModelAutomationRealControlArmed, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"", dock, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"OpenAstroSpec.ModelAutomationRealControlArmed\"", dock, StringComparison.Ordinal);
        Assert.Contains("把后台请求派发到本页按钮绑定的同一个 ICommand", dock, StringComparison.Ordinal);
        Assert.Contains("证明不会写入日志", dock, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding EnableModelAutomationBridgeCommand}\"", dock, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ArmModelAutomationRealControlCommand}\"", dock, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding DisarmModelAutomationRealControlCommand}\"", dock, StringComparison.Ordinal);

        // A property element such as CheckBox.ContentTemplate is not a control.
        foreach (Match button in Regex.Matches(dock, "<Button(?=\\s|>)(?<attributes>[^>]*)>", RegexOptions.CultureInvariant))
        {
            Assert.Contains("Content=", button.Groups["attributes"].Value, StringComparison.Ordinal);
        }
        foreach (Match checkBox in Regex.Matches(dock, "<CheckBox(?=\\s|>)(?<attributes>[^>]*)>", RegexOptions.CultureInvariant))
        {
            Assert.Contains("Content=", checkBox.Groups["attributes"].Value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ModelAutomationBridgeUsesCurrentUserPipeAndOnlyDispatchesWhitelistedUiCommands()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "ObservationAutomationBridge.cs"));
        var dockable = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "ObservationDockable.cs"));
        var bindingStart = dockable.IndexOf("private IReadOnlyList<ObservationAutomationCommandBinding> AutomationCommandBindings()", StringComparison.Ordinal);
        var bindingEnd = dockable.IndexOf("private ObservationAutomationSnapshot CreateAutomationSnapshot", bindingStart, StringComparison.Ordinal);

        Assert.True(bindingStart >= 0 && bindingEnd > bindingStart);
        var bindings = dockable[bindingStart..bindingEnd];

        Assert.Contains("PipeOptions.CurrentUserOnly", source, StringComparison.Ordinal);
        Assert.Contains("Application.Current?.Dispatcher", source, StringComparison.Ordinal);
        Assert.Contains("ICommand Command", source, StringComparison.Ordinal);
        Assert.Contains("expectedRevision", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("QualityGates", source, StringComparison.Ordinal);
        Assert.Contains("RecentTimeline", source, StringComparison.Ordinal);
        Assert.Contains("EvidenceFiles", source, StringComparison.Ordinal);
        Assert.Contains("BridgeInstanceId", source, StringComparison.Ordinal);
        Assert.Contains("PluginBuildSha256", source, StringComparison.Ordinal);
        Assert.Contains("ObservationRunId", source, StringComparison.Ordinal);
        Assert.Contains("RunUpdatedUtc", source, StringComparison.Ordinal);
        Assert.Contains("CurrentStageCode", source, StringComparison.Ordinal);
        Assert.Contains("CompletedStageCount", source, StringComparison.Ordinal);
        Assert.Contains("LatestRunEvent", source, StringComparison.Ordinal);
        Assert.Contains("UiError", source, StringComparison.Ordinal);
        Assert.Contains("RealControlOperatorAttestation", source, StringComparison.Ordinal);
        Assert.Contains("enable-bridge", bindings, StringComparison.Ordinal);
        Assert.Contains("arm-real-control", bindings, StringComparison.Ordinal);
        Assert.Contains("disarm-real-control", bindings, StringComparison.Ordinal);
        Assert.Contains("start-selected", bindings, StringComparison.Ordinal);
        Assert.DoesNotContain("new(\"start-simulation\",", bindings, StringComparison.Ordinal);
        Assert.DoesNotContain("new(\"start-real\",", bindings, StringComparison.Ordinal);
        Assert.Contains("restart-real-run", bindings, StringComparison.Ordinal);
        Assert.Contains("restartWithCurrentConfigurationCommand", bindings, StringComparison.Ordinal);
        Assert.Contains("pause", bindings, StringComparison.Ordinal);
        Assert.Contains("resume", bindings, StringComparison.Ordinal);
        Assert.Contains("takeover", bindings, StringComparison.Ordinal);
        Assert.Contains("cancel", bindings, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearG3RecoveryStateCommand", bindings, StringComparison.Ordinal);
        Assert.DoesNotContain("MoveManual", bindings, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveAdvancedSettingsCommand", bindings, StringComparison.Ordinal);
        Assert.DoesNotContain("Delete", bindings, StringComparison.OrdinalIgnoreCase);

        var dispatcherStart = source.IndexOf("dispatcher.InvokeAsync(() =>", StringComparison.Ordinal);
        var revisionCheck = source.IndexOf("request.ExpectedRevision.Value != Revision", dispatcherStart, StringComparison.Ordinal);
        var commandDispatch = source.IndexOf("return commandInvoker(command, request.OperatorAttestation, request.TargetDraft, request.FocusSampling);", revisionCheck, StringComparison.Ordinal);
        Assert.True(dispatcherStart >= 0 && revisionCheck > dispatcherStart && commandDispatch > revisionCheck);
        Assert.DoesNotContain("capturedRevision", source, StringComparison.Ordinal);
        Assert.Contains("dispatcher.InvokeAsync(() => snapshotFactory(Revision))", source, StringComparison.Ordinal);
        Assert.Contains("new(\"apply-target-draft\", applyTargetDraftCommand", bindings, StringComparison.Ordinal);
        Assert.Contains("ApplyDashboard(dashboard, notifyBridge: false)", dockable, StringComparison.Ordinal);
        Assert.Contains("binding.Command.Execute(commandParameter)", dockable, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomationCalibrationNavigationUsesOnlyTheExistingNativeDock()
    {
        var dockable = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "ObservationDockable.cs"));
        var navigation = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "NativeDockNavigation.cs"));
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Templates.xaml"));
        Assert.Contains("public ICommand ShowCalibrationLibraryCommand => showCalibrationLibraryCommand", dockable, StringComparison.Ordinal);
        Assert.Contains("new(\"show-calibration-library\", showCalibrationLibraryCommand, false, false, false, false,", dockable, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ShowCalibrationLibraryCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("library.IsVisible = true", dockable, StringComparison.Ordinal);
        Assert.Contains("NativeDockNavigation.ActivateExisting(library)", dockable, StringComparison.Ordinal);
        Assert.Contains("PanelNotAvailable", dockable, StringComparison.Ordinal);
        Assert.Contains("application.Dispatcher.CheckAccess()", navigation, StringComparison.Ordinal);
        Assert.Contains("OfType<DockingManager>()", navigation, StringComparison.Ordinal);
        Assert.Contains("existing.IsSelected = true", navigation, StringComparison.Ordinal);
        Assert.DoesNotContain("new Window", navigation, StringComparison.Ordinal);
        Assert.DoesNotContain("Reflection", navigation, StringComparison.Ordinal);
        Assert.DoesNotContain("Capture", navigation, StringComparison.Ordinal);
        Assert.DoesNotContain("Connect", navigation, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomationAtrInspectionNavigationReusesVisibleCommandWithoutAcquiring()
    {
        var dockable = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "ObservationDockable.cs"));
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Templates.xaml"));
        var bridge = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "ObservationAutomationBridge.cs"));
        var commandStart = dockable.IndexOf("showAtrInspectorCommand = new SimpleCommand(() =>", StringComparison.Ordinal);
        var commandEnd = dockable.IndexOf("});", commandStart, StringComparison.Ordinal);
        Assert.True(commandStart >= 0 && commandEnd > commandStart);
        var command = dockable[commandStart..commandEnd];
        Assert.Contains("SelectedWorkspaceTabIndex = 4", command, StringComparison.Ordinal);
        Assert.Contains("SelectedPreviewTabIndex = 2", command, StringComparison.Ordinal);
        Assert.Contains("ManualAtrInspectionExpanded = true", command, StringComparison.Ordinal);
        Assert.Contains("ActivateObservationPanel();", command, StringComparison.Ordinal);
        Assert.DoesNotContain("Capture", command, StringComparison.Ordinal);
        Assert.DoesNotContain("Connect", command, StringComparison.Ordinal);
        Assert.Contains("public ICommand ShowAtrInspectorCommand => showAtrInspectorCommand", dockable, StringComparison.Ordinal);
        Assert.Contains("new(\"show-atr-inspector\", showAtrInspectorCommand, false, false, false, false,", dockable, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ShowAtrInspectorCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedIndex=\"{Binding SelectedPreviewTabIndex, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding ManualAtrInspectionExpanded, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        var liveStart = xaml.IndexOf("<TabItem Header=\"实时图像\">", StringComparison.Ordinal);
        var navigationHeaderStart = xaml.IndexOf("<TabItem.Header>", liveStart, StringComparison.Ordinal);
        var navigationHeaderEnd = xaml.IndexOf("</TabItem.Header>", navigationHeaderStart, StringComparison.Ordinal);
        Assert.True(navigationHeaderStart > liveStart && navigationHeaderEnd > navigationHeaderStart);
        var navigationHeader = xaml[navigationHeaderStart..navigationHeaderEnd];
        Assert.Contains("ShowAtrInspectorCommand", navigationHeader, StringComparison.Ordinal);
        Assert.Contains("ShowCalibrationLibraryCommand", navigationHeader, StringComparison.Ordinal);
        foreach (var field in new[] { "SelectedWorkspaceTabIndex", "SelectedPlanTabIndex", "SelectedPreviewTabIndex", "SelectedManualTabIndex", "ManualAtrInspectionExpanded" })
            Assert.Contains(field, bridge, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetStrategyNavigationAlsoActivatesItsExistingObservationDock()
    {
        var dockable = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "ObservationDockable.cs"));
        var bridge = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "ObservationAutomationBridge.cs"));
        var start = dockable.IndexOf("showObservationPlanCommand = new SimpleCommand(() =>", StringComparison.Ordinal);
        var end = dockable.IndexOf("});", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        Assert.Contains("ActivateObservationPanel();", dockable[start..end], StringComparison.Ordinal);
        var activationStart = dockable.IndexOf("private void ActivateObservationPanel()", StringComparison.Ordinal);
        var activationEnd = dockable.IndexOf("private async Task ShowCalibrationLibraryAsync()", activationStart, StringComparison.Ordinal);
        Assert.True(activationStart >= 0 && activationEnd > activationStart);
        var activation = dockable[activationStart..activationEnd];
        Assert.Contains("NativeDockNavigation.ActivateExisting(this)", activation, StringComparison.Ordinal);
        Assert.Contains("PanelNotAvailable", activation, StringComparison.Ordinal);
        Assert.DoesNotContain("Capture", activation, StringComparison.Ordinal);
        Assert.DoesNotContain("Connect", activation, StringComparison.Ordinal);
        Assert.Contains("ObservationPanelNavigationOutcome", bridge, StringComparison.Ordinal);
        Assert.Contains("ObservationPanelSelected", bridge, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomationSnapshotRefreshDoesNotInvalidateItsOwnRevision()
    {
        var dockable = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "ObservationDockable.cs"));
        var dashboardStart = dockable.IndexOf("private void ApplyDashboard(", StringComparison.Ordinal);
        var dashboardEnd = dockable.IndexOf("private IReadOnlyList<ObservationAutomationCommandBinding> AutomationCommandBindings()", dashboardStart, StringComparison.Ordinal);
        Assert.True(dashboardStart >= 0 && dashboardEnd > dashboardStart);
        var dashboard = dockable[dashboardStart..dashboardEnd];

        // A read deliberately renders the latest dashboard on the dispatcher. Its
        // non-notifying flag must reach the shared command-state refresh as well.
        Assert.Contains("ApplyDashboard(dashboard, notifyBridge: false)", dockable, StringComparison.Ordinal);
        Assert.Contains("RaiseCommandStates(notifyBridge: notifyBridge)", dashboard, StringComparison.Ordinal);
        Assert.DoesNotContain("RaiseCommandStates();", dashboard, StringComparison.Ordinal);

        // Real setters and focus/run callbacks retain the notifying default;
        // only the explicit snapshot-render path suppresses a revision increment.
        Assert.Contains("private void RaiseCommandStates(bool notifyBridge = true)", dockable, StringComparison.Ordinal);
        var refreshStart = dockable.IndexOf("private void RaiseCommandStates(bool notifyBridge = true)", StringComparison.Ordinal);
        var refreshNotification = dockable.IndexOf("automationBridge?.NotifyStateChanged();", refreshStart, StringComparison.Ordinal);
        Assert.True(refreshNotification > refreshStart);
        Assert.Contains("if (notifyBridge) automationBridge?.NotifyStateChanged();", dockable[refreshStart..(refreshNotification + "automationBridge?.NotifyStateChanged();".Length)], StringComparison.Ordinal);
    }

    [Fact]
    public void ModelAutomationControllerUsesOnlyVisibleStartAndRetriesStaleRevisions()
    {
        var controller = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "invoke-model-frontend-closed-loop.ps1"));
        var client = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "invoke-observation-automation-bridge.ps1"));

        Assert.Contains("$maximumStaleRevisionRetries = 3", controller, StringComparison.Ordinal);
        Assert.Contains("FRONTEND_STALE_REVISION_RETRY", controller, StringComparison.Ordinal);
        Assert.Contains("(Get-AvailableCommands $snapshot) -notcontains $Name", controller, StringComparison.Ordinal);
        Assert.Contains("$currentRevision = [long]$nextRevision", controller, StringComparison.Ordinal);
        Assert.Contains("$startCommand = 'start-selected'", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("$startCommand = 'start-real'", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("$startCommand = 'start-simulation'", controller, StringComparison.Ordinal);
        Assert.Contains("'start-selected'", client, StringComparison.Ordinal);
        Assert.DoesNotContain("'start-real'", client, StringComparison.Ordinal);
        Assert.DoesNotContain("'start-simulation'", client, StringComparison.Ordinal);
    }
}
