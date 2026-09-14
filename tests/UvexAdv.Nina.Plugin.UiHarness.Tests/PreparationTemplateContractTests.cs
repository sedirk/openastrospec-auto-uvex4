using System.IO;
using System.Xml.Linq;

namespace UvexAdv.Nina.Plugin.UiHarness.Tests;

public sealed class PreparationTemplateContractTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void PreparationHasExactlyTwoIndependentContentScrollersWithoutHiddenExpansion()
    {
        var document = LoadTemplate();
        var root = Named(document, "AutomaticPreparationRoot");
        var tabs = Named(document, "PreparationSections");
        Assert.Equal("Grid", root.Name.LocalName);
        Assert.Contains(tabs, root.Descendants());
        Assert.DoesNotContain(root.Ancestors(), node => node.Name.LocalName == "ScrollViewer");
        Assert.DoesNotContain(root.Descendants(), node => node.Name.LocalName == "Expander");
        Assert.Equal("{Binding SelectedPreparationTabIndex, Mode=TwoWay}", tabs.Attribute("SelectedIndex")?.Value);
        var pages = tabs.Elements().Where(node => node.Name.LocalName == "TabItem").ToArray();
        Assert.Equal(new[] { "核对与入口", "本夜配置" }, pages.Select(page => page.Attribute("Header")?.Value));
        Assert.DoesNotContain(tabs.Descendants(), node => node.Name.LocalName == "TabControl");
        Assert.Contains(Named(document, "AutomaticPreparationScrollViewer"), pages[0].Descendants());
        Assert.Contains(Named(document, "PreparationNightSetupScrollViewer"), pages[1].Descendants());
        Assert.DoesNotContain(Named(document, "PreparationHeader").Ancestors(), node => node.Name.LocalName == "ScrollViewer");
        foreach (var page in pages)
        {
            var scroll = Assert.Single(page.Descendants(), node => node.Name.LocalName == "ScrollViewer");
            Assert.Equal("Auto", scroll.Attribute("VerticalScrollBarVisibility")?.Value);
            Assert.Equal("Disabled", scroll.Attribute("HorizontalScrollBarVisibility")?.Value);
        }
    }

    [Fact]
    public void EngineeringSelectorsAndImportRemainInTheirExistingAdvancedCategories()
    {
        var document = LoadTemplate();
        var preparation = Named(document, "AutomaticPreparationRoot");
        var advanced = Named(document, "AdvancedSettingsSections");
        var categories = advanced.Elements().Where(node => node.Name.LocalName == "TabItem").ToArray();
        Assert.Equal(10, categories.Length);
        Assert.Equal("{Binding SelectedAdvancedCategoryIndex, Mode=TwoWay}", advanced.Attribute("SelectedIndex")?.Value);
        string[] bindings = ["CommissioningProfiles", "TelescopeCandidates", "AtrCameraCandidates", "G3CameraCandidates", "QhyCameraCandidates",
            "ImportCommissioningBindingsCommand", "PreparationSafetyCapabilityChoices"];
        foreach (var binding in bindings)
        {
            Assert.DoesNotContain(preparation.DescendantsAndSelf().Attributes(), attribute => Binds(attribute, binding));
            var category = categories[binding == "PreparationSafetyCapabilityChoices" ? 2 : 9];
            Assert.Contains(category.DescendantsAndSelf().Attributes(), attribute => Binds(attribute, binding));
            Assert.Single(advanced.DescendantsAndSelf().Attributes(), attribute => Binds(attribute, binding));
        }
    }

    [Fact]
    public void ChecklistContainsStatusAndNavigationOnlyWhileNightEditorsInheritTheRunLock()
    {
        var document = LoadTemplate();
        var checklist = Named(document, "AutomaticPreparationScrollViewer");
        var night = Named(document, "PreparationNightSetupScrollViewer");
        string[] statuses = ["TargetPreparationStatus", "PreparationDeviceSummary", "CommissioningPreparationStatus",
            "NightSetupPreparationStatus", "PreparationSlitStatus", "AutomationPolicyPreparationStatus"];
        foreach (var status in statuses)
            Assert.Contains(checklist.DescendantsAndSelf().Attributes(), attribute => Binds(attribute, status));
        Assert.DoesNotContain(checklist.Descendants(), node => node.Name.LocalName is "ComboBox" or "TextBox" or "CheckBox");
        string[] selections = ["ExpectedUvexSlitPosition", "SelectedPreparationSpectralRegion", "SelectedPreparationCalibrationReference",
            "PreparationOrderSortingFilterInstalled"];
        foreach (var selection in selections)
        {
            var attribute = Assert.Single(night.DescendantsAndSelf().Attributes(), attribute => Binds(attribute, selection));
            Assert.Contains(attribute.Parent!.AncestorsAndSelf(), node =>
                node.Attributes().Any(value => value.Name.LocalName == "IsEnabled" && Binds(value, "IsTargetPlanEditable")));
        }
        Assert.Contains(Named(document, "PreparationToolsEndButton"), checklist.Descendants());
        Assert.Contains(Named(document, "OpenPreparationDraftFolderButton"), night.Descendants());
        Assert.Equal("PreparationToolsEndButton", checklist.Descendants().Last(node => node.Name.LocalName == "Button").Attribute(Xaml + "Name")?.Value);
        Assert.Equal("OpenPreparationDraftFolderButton", night.Descendants().Last(node => node.Name.LocalName == "Button").Attribute(Xaml + "Name")?.Value);
    }

    private static bool Binds(XAttribute attribute, string property) =>
        attribute.Value.Contains($"{{Binding {property}}}", StringComparison.Ordinal) ||
        attribute.Value.Contains($"{{Binding {property},", StringComparison.Ordinal);

    private static XElement Named(XDocument document, string name) =>
        document.Descendants().Single(node => node.Attribute(Xaml + "Name")?.Value == name);

    private static XDocument LoadTemplate()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "src", "UvexAdv.Nina.Plugin", "Templates.xaml");
            if (File.Exists(path)) return XDocument.Load(path);
        }
        throw new FileNotFoundException("Could not locate the production Templates.xaml from the test output directory.");
    }
}
