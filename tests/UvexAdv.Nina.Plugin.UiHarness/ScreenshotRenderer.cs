using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UvexAdv.Nina.Plugin;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin.UiHarness;

public sealed record ScreenshotRenderResult(
    string ScenarioName,
    string CultureName,
    int Width,
    int Height,
    string AbsolutePath,
    string Sha256)
{
    public IReadOnlyList<string> VisibleTexts { get; init; } = [];
    public double PreviewViewportWidth { get; init; }
    public double PreviewViewportHeight { get; init; }
    public PlanScrollDiagnostics? PlanScroll { get; init; }
    public FocusLayoutDiagnostics? FocusLayout { get; init; }
    public TargetStrategyLayoutDiagnostics? TargetStrategyLayout { get; init; }
    public WorkflowLayoutDiagnostics? WorkflowLayout { get; init; }
    public AdvancedLayoutDiagnostics? AdvancedLayout { get; init; }
    public PreparationLayoutDiagnostics? PreparationLayout { get; init; }
}

public sealed record WorkflowLayoutDiagnostics(int NodeCount, int ExpanderCount,
    bool GraphViewportVisible, bool ToolbarVisible, bool DetailVisible, bool DetailTabsReachable,
    bool SelectionShowsReason, bool SelectionRetainedOnUpdate, bool ControlsRetainedOnUpdate,
    bool DifferentRunResetsSelection, bool LastNodeReachable, double HorizontalOverflow, double VerticalOverflow,
    bool HorizontalScrolled, bool VerticalScrolled, bool WarningsUseAmber, bool CancellationNotCompleted,
    string SelectedReason, string SelectedState, string RunState);

public sealed record AdvancedLayoutDiagnostics(int CategoryCount, int SelectedCategory,
    bool SaveVisible, bool SaveStaysFixed, bool CategoriesReachable, bool BottomInputReachable,
    double ScrollableHeight, bool WheelScrolled, bool InputWheelScrolled, int ExpanderCount);

public sealed record PreparationLayoutDiagnostics(int TabCount, int SelectedTab, int ExpanderCount,
    int NestedTabCount, int ChecklistCardCount, int AdvancedEditorCount, int NightEditorCount,
    bool NightEditorsEnabled, bool DraftActionEnabled, bool ImportActionEnabled,
    bool HeaderVisible, bool HeaderStaysFixed, bool TabsStayFixed, bool RunControlStaysFixed,
    bool HasOuterScrollViewer, bool ViewportInsideHost, double ViewportHeight, double ScrollableHeight,
    bool VerticalBarVisible, bool BottomInitiallyVisible, bool WheelScrolled, bool InputWheelScrolled,
    double FinalOffset, bool BottomFinallyVisible, IReadOnlyList<string> NavigationCommands);

public sealed record TargetStrategyLayoutDiagnostics(bool IsPlanPage, bool SelectorEnabled,
    bool AutoButtonEnabled, bool RuntimeReachable, bool PriorityReachable,
    bool AutoButtonReachable, int ChoiceCount, bool AutoChoiceLast);

public sealed record FocusLayoutDiagnostics(bool StatusVisible, bool ReasonVisible,
    bool StartVisible, bool StartEnabled, bool CancelVisible, bool CancelEnabled,
    bool NativeChartVisible, bool SettingsBottomReachable);

public sealed record PlanScrollDiagnostics(
    double ViewportHeight, double ScrollableHeight, bool VerticalBarVisible,
    bool BottomInitiallyVisible, bool WheelScrolled, bool InputWheelScrolled,
    double FinalOffset, bool BottomFinallyVisible, bool ActionFinallyVisible, bool TabsRemainVisible);

public static class ScreenshotRenderer
{
    public const string ProductionTemplateKey = "UvexAdv.Nina.Plugin.ObservationDockable_Dockable";

    public static IReadOnlyList<ScreenshotRenderResult> RenderAll(
        IReadOnlyList<ScreenshotScenario> scenarios,
        string outputDirectory)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException("WPF screenshots must be rendered on an STA thread.");
        }

        Directory.CreateDirectory(outputDirectory);
        OfflineNightTheme.ConfigureNativeChartResources();
        var templates = new Templates();
        if (templates[ProductionTemplateKey] is not DataTemplate productionTemplate)
        {
            throw new InvalidOperationException(
                $"Production template '{ProductionTemplateKey}' was not found in Templates.xaml.");
        }

        try
        {
            return scenarios.Select(scenario => Render(scenario.TemplateKey == ProductionTemplateKey ? productionTemplate :
                (DataTemplate)templates[scenario.TemplateKey], scenario, outputDirectory)).ToArray();
        }
        finally
        {
            ObservationStaticTextLocalization.SetCulture(null);
        }
    }

    private static ScreenshotRenderResult Render(
        DataTemplate template,
        ScreenshotScenario scenario,
        string outputDirectory)
    {
        ObservationStaticTextLocalization.SetCulture(scenario.Culture);
        var host = new Border
        {
            Width = scenario.Width,
            Height = scenario.Height,
            Background = new SolidColorBrush(Color.FromRgb(31, 45, 52)),
            Padding = new Thickness(8),
            Child = new ContentControl
            {
                Content = scenario.AlternateViewModel ?? scenario.ViewModel,
                ContentTemplate = template,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch
            }
        };
        host.SetValue(TextElement.ForegroundProperty, new SolidColorBrush(Color.FromRgb(226, 232, 240)));
        host.SetValue(TextElement.FontFamilyProperty, new FontFamily("Segoe UI"));
        host.Resources.MergedDictionaries.Add(OfflineNightTheme.Create());

        BitmapSource bitmap;
        IReadOnlyList<string> visibleTexts = [];
        double viewportWidth = 0, viewportHeight = 0;
        PlanScrollDiagnostics? planScroll = null;
        FocusLayoutDiagnostics? focusLayout = null;
        TargetStrategyLayoutDiagnostics? targetStrategyLayout = null;
        WorkflowLayoutDiagnostics? workflowLayout = null;
        AdvancedLayoutDiagnostics? advancedLayout = null;
        PreparationLayoutDiagnostics? preparationLayout = null;
        var window = new Window
        {
            Width = scenario.Width,
            Height = scenario.Height,
            Left = -32000,
            Top = -32000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Background = host.Background,
            Content = host
        };
        try
        {
            // A real PresentationSource is required: EmbeddedImageViewer performs
            // its initial fit from Loaded and DispatcherPriority.Loaded callbacks.
            // Keeping the window off-screen avoids flashing or activating it.
            window.Show();
            PumpLoadedAndRender(window.Dispatcher);
            if (scenario.TemplateKey == ProductionTemplateKey) SelectScenarioTabs(host, scenario);
            PumpLoadedAndRender(window.Dispatcher);
            if (scenario.Name == "atr-manual")
            {
                var inspector = Descendants<Expander>(host).First(e => e.Name == "ManualAtrInspector");
                inspector.IsExpanded = true;
            }
            if (scenario.Name == "atr-levels")
            {
                var levels = Descendants<System.Windows.Controls.Primitives.ToggleButton>(host)
                    .First(e => e.Name == "DisplayLevelsToggle");
                levels.IsChecked = true;
            }
            PumpLoadedAndRender(window.Dispatcher);
            var viewport = Descendants<ScrollViewer>(host).FirstOrDefault(e => e.Name == "ViewportScrollViewer" && e.IsVisible);
            if (viewport is not null) { viewportWidth = viewport.ActualWidth; viewportHeight = viewport.ActualHeight; }
            PumpLoadedAndRender(window.Dispatcher);

            host.Measure(new Size(scenario.Width, scenario.Height));
            host.Arrange(new Rect(0, 0, scenario.Width, scenario.Height));
            host.UpdateLayout();
            PumpLoadedAndRender(window.Dispatcher);

            planScroll = InspectPlanScrolling(host, scenario);
            focusLayout = InspectFocusLayout(host, scenario);
            targetStrategyLayout = InspectTargetStrategyLayout(host, scenario);
            workflowLayout = InspectWorkflowLayout(host, scenario);
            advancedLayout = InspectAdvancedLayout(host, scenario);
            preparationLayout = InspectPreparationLayout(host, scenario);

            var target = new RenderTargetBitmap(
                scenario.Width,
                scenario.Height,
                96,
                96,
                PixelFormats.Pbgra32);
            target.Render(host);
            target.Freeze();
            bitmap = target;
            visibleTexts = Descendants<TextBlock>(host)
                .Where(text => text.IsVisible && !string.IsNullOrWhiteSpace(text.Text))
                .Select(text => text.Text.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            window.Close();
        }

        var path = Path.Combine(outputDirectory, $"observation-dock-{scenario.Name}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        return new(scenario.Name, scenario.Culture.Name, scenario.Width, scenario.Height, Path.GetFullPath(path), sha)
        {
            VisibleTexts = visibleTexts,
            PreviewViewportWidth = viewportWidth,
            PreviewViewportHeight = viewportHeight,
            PlanScroll = planScroll,
            FocusLayout = focusLayout,
            TargetStrategyLayout = targetStrategyLayout,
            WorkflowLayout = workflowLayout,
            AdvancedLayout = advancedLayout,
            PreparationLayout = preparationLayout,
        };
    }

    private static PreparationLayoutDiagnostics? InspectPreparationLayout(FrameworkElement host, ScreenshotScenario scenario)
    {
        if (scenario.TemplateKey != ProductionTemplateKey || scenario.ViewModel.SelectedWorkspaceTabIndex != 3) return null;
        var root = Descendants<Grid>(host).Single(control => control.Name == "AutomaticPreparationRoot");
        var header = Descendants<Border>(root).Single(control => control.Name == "PreparationHeader");
        var tabs = Descendants<TabControl>(root).Single(control => control.Name == "PreparationSections");
        var night = tabs.SelectedIndex == 1;
        var scroll = Descendants<ScrollViewer>(tabs).Single(control => control.IsVisible && control.Name ==
            (night ? "PreparationNightSetupScrollViewer" : "AutomaticPreparationScrollViewer"));
        var viewport = Descendants<ScrollContentPresenter>(scroll).First();
        var bottom = Descendants<Button>(scroll).Single(control => control.Name ==
            (night ? "OpenPreparationDraftFolderButton" : "PreparationToolsEndButton"));
        var start = Descendants<Button>(host).Single(control => BindingPath(control, Button.CommandProperty) == "StartSelectedModeCommand");
        var headers = tabs.Items.Cast<TabItem>().ToArray();
        var headerPosition = header.TransformToAncestor(host).Transform(new Point());
        var startPosition = start.TransformToAncestor(host).Transform(new Point());
        var tabPositions = headers.Select(item => item.TransformToAncestor(host).Transform(new Point())).ToArray();
        var initiallyVisible = IsFullyInside(bottom, viewport);
        var wheelScrolled = false;
        var inputWheelScrolled = false;
        if (scenario.ExercisePreparationScrolling)
        {
            // Routed wheel events prove actual page movement without desktop input or equipment.
            var label = Descendants<TextBlock>(scroll).First(control => control.IsVisible);
            label.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.MouseWheelEvent });
            PumpLoadedAndRender(host.Dispatcher);
            wheelScrolled = scroll.VerticalOffset > 0;
            scroll.ScrollToTop(); PumpLoadedAndRender(host.Dispatcher);
            var input = Descendants<ComboBox>(scroll).FirstOrDefault(control => control.IsVisible);
            (input as UIElement ?? label).RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.MouseWheelEvent });
            PumpLoadedAndRender(host.Dispatcher);
            inputWheelScrolled = scroll.VerticalOffset > 0;
            scroll.ScrollToEnd(); PumpLoadedAndRender(host.Dispatcher);
        }
        string[] advancedSources = ["CommissioningProfiles", "TelescopeCandidates", "AtrCameraCandidates", "G3CameraCandidates",
            "QhyCameraCandidates", "PreparationSafetyCapabilityChoices"];
        string[] nightSources = ["PreparationSpectralRegionChoices", "PreparationCalibrationReferenceChoices", "UvexSlitChoices"];
        var nightEditors = Descendants<ComboBox>(scroll)
            .Where(control => nightSources.Contains(BindingPath(control, ItemsControl.ItemsSourceProperty)))
            .Cast<Control>().Concat(Descendants<CheckBox>(scroll).Where(control =>
                BindingPath(control, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty) == "PreparationOrderSortingFilterInstalled")).ToArray();
        var commands = Descendants<Button>(scroll).Select(control => BindingPath(control, Button.CommandProperty)).ToArray();
        var advancedEditors = Descendants<ComboBox>(root).Count(control => advancedSources.Contains(BindingPath(control, ItemsControl.ItemsSourceProperty))) +
            commands.Count(command => command == "ImportCommissioningBindingsCommand");
        var draft = Descendants<Button>(scroll).FirstOrDefault(control => BindingPath(control, Button.CommandProperty) == "CreateNightSetupDraftCommand");
        var import = Descendants<Button>(scroll).FirstOrDefault(control => BindingPath(control, Button.CommandProperty) == "SelectNightSetupSnapshotCommand");
        var outerScroll = false;
        for (DependencyObject? ancestor = VisualTreeHelper.GetParent(root); ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
            outerScroll |= ancestor is ScrollViewer;
        return new(tabs.Items.Count, tabs.SelectedIndex, Descendants<Expander>(root).Count(), Descendants<TabControl>(tabs).Count(),
            (scroll.Content as Panel)?.Children.OfType<Border>().Count() ?? 0,
            advancedEditors, nightEditors.Length, nightEditors.All(control => control.IsEnabled), draft?.IsEnabled == true, import?.IsEnabled == true,
            IsFullyInside(header, host), headerPosition == header.TransformToAncestor(host).Transform(new Point()),
            headers.Select((item, index) => IsFullyInside(item, host) && item.TransformToAncestor(host).Transform(new Point()) == tabPositions[index]).All(value => value),
            IsFullyInside(start, host) && startPosition == start.TransformToAncestor(host).Transform(new Point()), outerScroll,
            IsFullyInside(viewport, host), scroll.ViewportHeight, scroll.ScrollableHeight,
            scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible, initiallyVisible, wheelScrolled, inputWheelScrolled,
            scroll.VerticalOffset, IsFullyInside(bottom, viewport), commands.Where(command => command.StartsWith("Show", StringComparison.Ordinal)).Distinct().ToArray());
    }

    private static string BindingPath(DependencyObject element, DependencyProperty property) =>
        BindingOperations.GetBinding(element, property)?.Path?.Path ?? string.Empty;

    private static TargetStrategyLayoutDiagnostics? InspectTargetStrategyLayout(FrameworkElement host, ScreenshotScenario scenario)
    {
        if (!scenario.Name.StartsWith("target-strategy-", StringComparison.Ordinal)) return null;
        if (scenario.ViewModel.SelectedWorkspaceTabIndex != 2)
        {
            var workflow = Descendants<ObservationWorkflowView>(host).Single(c => c.IsVisible);
            var details = Descendants<Border>(workflow).Single(c => c.Name == "DetailsPanel");
            return new(false, false, false, IsFullyInside(details, host), false, false, 0, false);
        }
        var scroll = Descendants<ScrollViewer>(host).Single(c => c.Name == "ObservationTargetPlanScrollViewer" && c.IsVisible);
        var viewport = Descendants<ScrollContentPresenter>(scroll).First();
        var selector = Descendants<ComboBox>(scroll).Single(c => c.Name == "TargetObservabilityInput");
        var auto = Descendants<Button>(scroll).Single(c => c.Name == "UsePlanetariumStrategyButton");
        var runtime = Descendants<TextBlock>(scroll).Single(c => c.Name == "TargetStrategyRuntimeText");
        var priority = Descendants<TextBlock>(scroll).Single(c => c.Text == scenario.ViewModel.TargetStrategyPrioritySummary);
        var savedOffset = scroll.VerticalOffset;
        auto.BringIntoView(); PumpLoadedAndRender(host.Dispatcher);
        var autoReachable = IsFullyInside(auto, viewport);
        priority.BringIntoView(); PumpLoadedAndRender(host.Dispatcher);
        var priorityReachable = IsFullyInside(priority, viewport);
        scroll.ScrollToEnd(); PumpLoadedAndRender(host.Dispatcher);
        var runtimeReachable = IsFullyInside(runtime, viewport);
        scroll.ScrollToVerticalOffset(savedOffset); PumpLoadedAndRender(host.Dispatcher);
        var choices = selector.Items.Cast<TargetObservabilityChoice>().ToArray();
        return new(true, selector.IsEnabled, auto.IsEnabled, runtimeReachable,
            priorityReachable, autoReachable, choices.Length,
            choices.LastOrDefault()?.Value == UvexAdv.Observatory.TargetObservabilityClass.AutoFromPlanetarium);
    }

    private static WorkflowLayoutDiagnostics? InspectWorkflowLayout(FrameworkElement host, ScreenshotScenario scenario)
    {
        if (!scenario.Name.StartsWith("workflow-", StringComparison.Ordinal) && scenario.Name != "target-strategy-fallback") return null;
        var view = Descendants<ObservationWorkflowView>(host).Single(control => control.IsVisible);
        var original = view.Graph ?? throw new InvalidOperationException("Workflow graph was not bound.");
        var scroll = Descendants<ScrollViewer>(view).Single(control => control.Name == "GraphScroll");
        var viewport = Descendants<ScrollContentPresenter>(scroll).First();
        var details = Descendants<Border>(view).Single(control => control.Name == "DetailsPanel");
        var fit = Descendants<Button>(view).Single(control => control.Name == "FitButton");
        var follow = Descendants<CheckBox>(view).Single(control => control.Name == "FollowCheckBox");
        var tabs = Descendants<TabControl>(view).Single(control => control.Name == "DetailsTabs");
        var buttons = Descendants<Button>(view).Where(control => control.Content is ObservationWorkflowNode).ToArray();
        var warningsAmber = buttons.Where(button => button.Content is ObservationWorkflowNode
            { StateKind: ObservationWorkflowNodeState.Passed, GateSeverity: GateSeverity.Warning })
            .All(button => button.Background is SolidColorBrush { Color.R: 58, Color.G: 50, Color.B: 32 });
        var chosen = buttons.Single(button => ((ObservationWorkflowNode)button.Content).Id == "branch:CalibratedGhost");
        chosen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpLoadedAndRender(host.Dispatcher);
        var reason = Descendants<TextBox>(view).Single(control => control.Name == "ReasonText");
        var selectionShowsReason = view.SelectedNodeId == "branch:CalibratedGhost" &&
            reason.Text.Contains(TargetAcquisitionStrategyPolicy.Describe(TargetAcquisitionBranch.CalibratedGhost).Prerequisites, StringComparison.Ordinal);
        var tabsReachable = true;
        for (var index = 0; index < tabs.Items.Count; index++)
        {
            tabs.SelectedIndex = index; PumpLoadedAndRender(host.Dispatcher);
            var text = Descendants<TextBox>(tabs).Single(control => control.IsVisible);
            tabsReachable &= IsFullyInside(text, host) && text.ActualHeight > 15;
        }
        tabs.SelectedIndex = 0; PumpLoadedAndRender(host.Dispatcher);
        var horizontalOverflow = scroll.ScrollableWidth;
        var verticalOverflow = scroll.ScrollableHeight;
        scroll.ScrollToHorizontalOffset(scroll.ScrollableWidth);
        scroll.ScrollToVerticalOffset(scroll.ScrollableHeight);
        PumpLoadedAndRender(host.Dispatcher);
        var horizontalScrolled = horizontalOverflow <= 0 || scroll.HorizontalOffset > 0;
        var verticalScrolled = verticalOverflow <= 0 || scroll.VerticalOffset > 0;
        var bottomRight = buttons.Single(button => ((ObservationWorkflowNode)button.Content).Id == "stage:PlaceTargetOnSlit");
        var lastReachable = IsFullyInside(bottomRight, viewport);
        var offset = new Point(scroll.HorizontalOffset, scroll.VerticalOffset);
        view.Graph = original with { UpdatedUtc = original.UpdatedUtc.AddSeconds(1) };
        PumpLoadedAndRender(host.Dispatcher);
        var retained = view.SelectedNodeId == "branch:CalibratedGhost" && !view.IsFollowingExecution &&
            Math.Abs(scroll.HorizontalOffset - offset.X) < 0.5 && Math.Abs(scroll.VerticalOffset - offset.Y) < 0.5;
        var controlsRetained = ReferenceEquals(chosen, Descendants<Button>(view).Single(button => button.Tag?.ToString() == "branch:CalibratedGhost"));
        view.Graph = original with { RunId = "UI-REPLAY-DIFFERENT-RUN" };
        PumpLoadedAndRender(host.Dispatcher);
        var newRunResets = view.SelectedNodeId != "branch:CalibratedGhost";
        view.Graph = original; follow.IsChecked = true;
        PumpLoadedAndRender(host.Dispatcher);
        if (original.CurrentNodeId is null) view.SelectNode("stage:ValidateNightSetup");
        var finalReason = Descendants<TextBox>(view).Single(control => control.Name == "ReasonText").Text;
        var finalState = Descendants<TextBlock>(view).Single(control => control.Name == "SelectedStateText").Text;
        return new(view.VisibleNodeCount, Descendants<Expander>(view).Count(), IsFullyInside(scroll, host),
            IsFullyInside(fit, host) && IsFullyInside(follow, host), IsFullyInside(details, host), tabsReachable,
            selectionShowsReason, retained, controlsRetained, newRunResets, lastReachable,
            horizontalOverflow, verticalOverflow, horizontalScrolled, verticalScrolled, warningsAmber,
            original.RunState is not (ObservationRunState.Cancelling or ObservationRunState.Cancelled) ||
                original.Nodes.Count(node => node.Kind == ObservationWorkflowNodeKind.MainStage && node.StateKind == ObservationWorkflowNodeState.Passed) < 11,
            finalReason, finalState, original.RunState.ToString());
    }

    private static AdvancedLayoutDiagnostics? InspectAdvancedLayout(FrameworkElement host, ScreenshotScenario scenario)
    {
        if (scenario.TemplateKey != ProductionTemplateKey || scenario.ViewModel.SelectedWorkspaceTabIndex != 6) return null;
        var tabs = Descendants<TabControl>(host).Single(control => control.Name == "AdvancedSettingsSections");
        var scroll = Descendants<ScrollViewer>(tabs).Single(control => control.IsVisible &&
            control.Name == $"AdvancedSettingsCategory{tabs.SelectedIndex}ScrollViewer");
        var viewport = Descendants<ScrollContentPresenter>(scroll).First();
        var navigation = Descendants<ScrollViewer>(tabs).Single(control => control.Name == "AdvancedSettingsNavigationScrollViewer");
        var navViewport = Descendants<ScrollContentPresenter>(navigation).First();
        var save = Descendants<Button>(host).Single(control => AutomationProperties.GetAutomationId(control) == "OpenAstroSpec.SaveAdvancedSettings");
        var savePosition = save.TransformToAncestor(host).Transform(new Point());
        var categoriesReachable = true;
        foreach (var header in Descendants<TabItem>(navigation))
        {
            header.BringIntoView(); PumpLoadedAndRender(host.Dispatcher);
            categoriesReachable &= IsFullyInside(header, navViewport);
        }
        navigation.ScrollToTop();
        var bottom = Descendants<TextBox>(scroll).LastOrDefault(control => control.IsVisible) ??
            (FrameworkElement?)Descendants<CheckBox>(scroll).LastOrDefault(control => control.IsVisible) ??
            Descendants<TextBlock>(scroll).Last(control => control.IsVisible);
        scroll.ScrollToTop(); PumpLoadedAndRender(host.Dispatcher);
        var label = Descendants<TextBlock>(scroll).First(control => control.IsVisible);
        label.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.MouseWheelEvent });
        PumpLoadedAndRender(host.Dispatcher);
        var wheel = scroll.ScrollableHeight <= 0 || scroll.VerticalOffset > 0;
        scroll.ScrollToTop(); PumpLoadedAndRender(host.Dispatcher);
        var input = Descendants<TextBox>(scroll).FirstOrDefault(control => control.IsVisible);
        input?.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.MouseWheelEvent });
        PumpLoadedAndRender(host.Dispatcher);
        var inputWheel = input is null || scroll.ScrollableHeight <= 0 || scroll.VerticalOffset > 0;
        bottom.BringIntoView(); PumpLoadedAndRender(host.Dispatcher);
        var bottomReachable = IsFullyInside(bottom, viewport);
        var fixedSave = savePosition == save.TransformToAncestor(host).Transform(new Point());
        scroll.ScrollToTop(); PumpLoadedAndRender(host.Dispatcher);
        return new(tabs.Items.Count, tabs.SelectedIndex, IsFullyInside(save, host), fixedSave,
            categoriesReachable, bottomReachable, scroll.ScrollableHeight, wheel, inputWheel, Descendants<Expander>(tabs).Count());
    }

    private static FocusLayoutDiagnostics? InspectFocusLayout(FrameworkElement host, ScreenshotScenario scenario)
    {
        if (!scenario.Name.StartsWith("main-focus", StringComparison.Ordinal)) return null;
        var focus = Descendants<SepMainFocusView>(host).Single(c => c.IsVisible);
        var start = Descendants<Button>(focus).Single(c => c.Name == "StartFocusButton");
        var cancel = Descendants<Button>(focus).Single(c => c.Name == "CancelFocusButton");
        var status = Descendants<TextBlock>(focus).Single(c => c.Name == "FocusStatus");
        var reason = Descendants<TextBlock>(focus).Single(c => c.Name == "FocusAvailability");
        var scroll = Descendants<ScrollViewer>(focus).FirstOrDefault(c => c.Name == "FocusSettingsScroll" && c.IsVisible);
        var bottomReachable = true;
        if (scroll is not null)
        {
            scroll.ScrollToEnd(); PumpLoadedAndRender(host.Dispatcher);
            var bottom = Descendants<TextBlock>(scroll).Single(c => c.Name == "FocusSettingsEndNote");
            bottomReachable = IsFullyInside(bottom, Descendants<ScrollContentPresenter>(scroll).First());
            scroll.ScrollToTop(); PumpLoadedAndRender(host.Dispatcher);
        }
        return new(IsFullyInside(status, host), IsFullyInside(reason, host), IsFullyInside(start, host), start.IsEnabled,
            IsFullyInside(cancel, host), cancel.IsEnabled,
            Descendants<NINA.View.AutoFocusChart>(focus).Any(c => c.IsVisible), bottomReachable);
    }

    private static PlanScrollDiagnostics? InspectPlanScrolling(FrameworkElement host, ScreenshotScenario scenario)
    {
        if (scenario.TemplateKey != ProductionTemplateKey || scenario.ViewModel.SelectedWorkspaceTabIndex != 2)
            return null;
        var scroll = Descendants<ScrollViewer>(host).Single(control => control.IsVisible &&
            control.Name is "ObservationTargetPlanScrollViewer" or "ObservationAcquisitionPlanScrollViewer");
        var viewport = Descendants<ScrollContentPresenter>(scroll).First();
        var bottom = Descendants<FrameworkElement>(scroll).Single(control => control.Name ==
            (scenario.ViewModel.SelectedPlanTabIndex == 1 ? "AcquisitionPlanStateText" : "TargetStrategyRuntimeText"));
        var action = Descendants<Button>(scroll).FirstOrDefault(control => control.Name == "SaveAcquisitionPlanButton");
        var tabs = Descendants<TabControl>(host).Single(control => control.Name == "ObservationPlanTabs");
        var headers = Descendants<TabItem>(tabs).Where(control => control.IsVisible).ToArray();
        var headerPositions = headers.Select(header => header.TransformToAncestor(host).Transform(new Point())).ToArray();
        var initiallyVisible = IsFullyInside(bottom, viewport);
        var wheelScrolled = false;
        var inputWheelScrolled = false;
        if (scenario.ExercisePlanScrolling)
        {
            // Routed WPF events exercise the same scrolling handlers without desktop input or hardware.
            var label = Descendants<TextBlock>(scroll).First(control => control.IsVisible);
            label.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.MouseWheelEvent });
            PumpLoadedAndRender(host.Dispatcher);
            wheelScrolled = scroll.VerticalOffset > 0;
            scroll.ScrollToTop();
            PumpLoadedAndRender(host.Dispatcher);
            var input = Descendants<TextBox>(scroll).First(control => control.IsVisible);
            input.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.MouseWheelEvent });
            PumpLoadedAndRender(host.Dispatcher);
            inputWheelScrolled = scroll.VerticalOffset > 0;
            scroll.ScrollToEnd();
            PumpLoadedAndRender(host.Dispatcher);
        }
        return new(scroll.ViewportHeight, scroll.ScrollableHeight,
            scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible, initiallyVisible,
            wheelScrolled, inputWheelScrolled, scroll.VerticalOffset, IsFullyInside(bottom, viewport),
            action is null || IsFullyInside(action, viewport),
            headers.Length == 2 && headers.Select((header, index) => IsFullyInside(header, host) &&
                header.TransformToAncestor(host).Transform(new Point()) == headerPositions[index]).All(visible => visible));
    }

    private static bool IsFullyInside(FrameworkElement element, FrameworkElement viewport)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        var bounds = element.TransformToAncestor(viewport).TransformBounds(new Rect(element.RenderSize));
        // IsVisible alone also returns true for content clipped outside a ScrollViewer.
        return bounds.Left >= -0.5 && bounds.Top >= -0.5 &&
            bounds.Right <= viewport.ActualWidth + 0.5 && bounds.Bottom <= viewport.ActualHeight + 0.5;
    }

    private static void PumpLoadedAndRender(Dispatcher dispatcher)
    {
        dispatcher.Invoke(DispatcherPriority.Loaded, static () => { });
        dispatcher.Invoke(DispatcherPriority.Render, static () => { });
        dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
    }

    private static void SelectScenarioTabs(DependencyObject root, ScreenshotScenario scenario)
    {
        var outer = Descendants<TabControl>(root).SingleOrDefault(control => control.Name == "ObservationWorkspaceTabs");
        if (outer is null)
        {
            throw new InvalidOperationException("The production observation template does not contain its main TabControl.");
        }

        outer.SelectedIndex = Math.Clamp(scenario.ViewModel.SelectedWorkspaceTabIndex, 0, outer.Items.Count - 1);
        if (root is FrameworkElement element)
        {
            element.UpdateLayout();
        }

        if (scenario.ViewModel.SelectedWorkspaceTabIndex == 6)
        {
            var sections = Descendants<TabControl>(root).Single(control => control.Name == "AdvancedSettingsSections");
            sections.SelectedIndex = scenario.ViewModel.SelectedAdvancedCategoryIndex;
            if (root is FrameworkElement advancedRoot) advancedRoot.UpdateLayout();
        }
        if (scenario.ViewModel.SelectedWorkspaceTabIndex == 3)
        {
            var sections = Descendants<TabControl>(root).Single(control => control.Name == "PreparationSections");
            sections.SelectedIndex = scenario.ViewModel.SelectedPreparationTabIndex;
            if (root is FrameworkElement preparationRoot) preparationRoot.UpdateLayout();
        }
        if (scenario.ViewModel.SelectedWorkspaceTabIndex != 4)
        {
            return;
        }

        var preview = Descendants<TabControl>(root)
            .Where(control => !ReferenceEquals(control, outer))
            .OrderByDescending(control => control.Items.Count)
            .FirstOrDefault();
        if (preview is not null)
        {
            preview.SelectedIndex = Math.Clamp(scenario.ViewModel.SelectedPreviewTabIndex, 0, preview.Items.Count - 1);
            if (root is FrameworkElement previewRoot)
            {
                previewRoot.UpdateLayout();
            }
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}

internal static class OfflineNightTheme
{
    public static void ConfigureNativeChartResources()
    {
        // The real NINA chart expects an application-level ProfileService. Supply
        // only color data; never load a machine Profile or construct a device owner.
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (app.Resources.Contains("ProfileService")) return;
        var colors = new
        {
            PrimaryColor = Color.FromRgb(218, 233, 242), SecondaryColor = Color.FromRgb(28, 183, 166),
            BorderColor = Color.FromRgb(62, 89, 108), BackgroundColor = Color.FromRgb(16, 27, 40),
            NotificationErrorColor = Colors.Salmon, NotificationWarningColor = Colors.Goldenrod,
        };
        var mock = new { ActiveProfile = new { ColorSchemaSettings = new { ColorSchema = colors, AltColorSchema = colors } } };
        app.Resources["ProfileService"] = mock;
        app.Resources["ButtonBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(61, 209, 195));
        app.Resources["NotificationWarningBrush"] = new SolidColorBrush(Colors.Goldenrod);
        var dictionary = new ResourceDictionary { ["ProfileService"] = mock };
        app.Resources.MergedDictionaries.Add(dictionary); // Keep the weak-cache target alive for this render process.
        NINA.WPF.Base.Utility.SharedResourceDictionary.SharedDictionaries[
            new Uri("/NINA.WPF.Base;component/Resources/StaticResources/ProfileService.xaml", UriKind.Relative)] = new WeakReference(dictionary);
    }

    public static ResourceDictionary Create()
    {
        var resources = new ResourceDictionary();
        resources.Add(typeof(Control), CreateBaseControlStyle());
        resources.Add(typeof(Button), CreateButtonStyle());
        resources.Add(typeof(TextBox), CreateTextBoxStyle());
        resources.Add(typeof(ProgressBar), CreateProgressBarStyle());
        return resources;
    }

    private static Style CreateBaseControlStyle()
    {
        var style = new Style(typeof(Control));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Segoe UI")));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 13d));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(226, 232, 240))));
        return style;
    }

    private static Style CreateButtonStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(15, 118, 110))));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(45, 212, 191))));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
        style.Triggers.Add(new Trigger
        {
            Property = UIElement.IsEnabledProperty,
            Value = false,
            Setters =
            {
                new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(100, 116, 139))),
                new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(30, 41, 59))),
                new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(51, 65, 85)))
            }
        });
        return style;
    }

    private static Style CreateTextBoxStyle()
    {
        var style = new Style(typeof(TextBox));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(226, 232, 240))));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(15, 23, 42))));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(71, 85, 105))));
        style.Setters.Add(new Setter(TextBox.CaretBrushProperty, Brushes.White));
        return style;
    }

    private static Style CreateProgressBarStyle()
    {
        var style = new Style(typeof(ProgressBar));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(15, 23, 42))));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(20, 184, 166))));
        return style;
    }
}

public static class ScreenshotManifest
{
    public static void Write(string outputDirectory, IReadOnlyList<ScreenshotRenderResult> results)
    {
        var manifest = new
        {
            generatedUtc = DateTimeOffset.UtcNow,
            renderer = "offline-wpf-render-target-bitmap",
            productionTemplate = ScreenshotRenderer.ProductionTemplateKey,
            hardwareAccess = false,
            scenarios = results
        };
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(outputDirectory, "manifest.json"), json + Environment.NewLine);
    }
}
