using System.Globalization;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// A fixed-topology, read-only view of coordinator records. Selection, zoom and
/// panning are local presentation only; this control has no equipment commands.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class ObservationWorkflowView : UserControl
{
    public static readonly DependencyProperty GraphProperty = DependencyProperty.Register(
        nameof(Graph), typeof(ObservationWorkflowGraph), typeof(ObservationWorkflowView),
        new FrameworkPropertyMetadata(null, OnGraphChanged));

    public ObservationWorkflowGraph? Graph
    {
        get => (ObservationWorkflowGraph?)GetValue(GraphProperty);
        set => SetValue(GraphProperty, value);
    }

    public string? SelectedNodeId { get; private set; }
    public bool IsFollowingExecution => FollowCheckBox.IsChecked == true;
    public double ZoomLevel => GraphScale.ScaleX;
    public int VisibleNodeCount => nodeButtons.Count;
    public int VisibleEdgeCount { get; private set; }

    private const double MinimumZoom = 0.85;
    private const double MaximumZoom = 1.8;
    private readonly Dictionary<string, Rect> nodeBounds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> nodeButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> currentMarkers = new(StringComparer.Ordinal);
    private readonly List<UIElement> edgeVisuals = [];
    private string? nodeSignature;
    private string? edgeSignature;
    private string? lastCurrentNodeId;
    private string? renderedRunId;
    private bool fitOnResize = true;
    private bool initialized;
    private Point? panOrigin;
    private double panHorizontal;
    private double panVertical;

    public ObservationWorkflowView()
    {
        InitializeComponent();
        initialized = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        BodyGrid.SizeChanged += (_, _) => ArrangePanels();
        ApplyText();
    }

    private static void OnGraphChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((ObservationWorkflowView)sender).RenderGraph();

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        ObservationStaticTextLocalization.CultureChanged += OnCultureChanged;
        ApplyText();
        ArrangePanels();
        RenderGraph();
        if (fitOnResize) Dispatcher.BeginInvoke(FitGraph, DispatcherPriority.Loaded);
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        ObservationStaticTextLocalization.CultureChanged -= OnCultureChanged;
        EndPan();
    }

    private void OnCultureChanged(object? sender, EventArgs args)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnCultureChanged(sender, args)); return; }
        ApplyText();
        nodeSignature = null; // A locale switch, unlike a dashboard tick, changes static canvas labels.
        RenderGraph();
    }

    private static string Text(string chinese, string english) =>
        ObservationUiPresentation.Text(chinese, english, ObservationStaticTextLocalization.EffectiveCulture);

    private void ApplyText()
    {
        FitButton.Content = Text("适配全图", "Fit graph");
        FitButton.ToolTip = Text("保留可读字号；小窗口可滚动或拖动画布", "Keep labels readable; scroll or drag in small windows");
        FollowCheckBox.Content = Text("跟随执行", "Follow execution");
        GraphScroll.ToolTip = Text("滚轮缩放 · 拖动画布平移 · 点击节点查看记录，不执行设备动作", "Wheel to zoom · drag to pan · click nodes to inspect records, never execute equipment actions");
        ReasonTab.Header = Text("原因", "Reason");
        MetricsTab.Header = Text("指标", "Metrics");
        TimelineTab.Header = Text("时间与日志", "Time and log");
        EvidenceTab.Header = Text("证据", "Evidence");
        LegendText.Text = Text("只读流程 · 灰色=未记录，不是已跳过 · 主链实线=编排顺序 · 虚线=阶段关联 · 橙色实线=本轮实际切换", "Read-only · gray means unrecorded, not skipped · solid main line: canonical order · dashed: stage association · orange: recorded transition");
        EmptyGraphText.Text = Text("等待编排状态。没有运行记录时，不会把节点显示为成功。", "Waiting for coordinator state. Nodes are never shown as successful without run records.");
    }

    private void RenderGraph()
    {
        if (!initialized) return;
        var graph = Graph;
        var newRun = !string.Equals(renderedRunId, graph?.RunId, StringComparison.Ordinal);
        if (newRun)
        {
            renderedRunId = graph?.RunId;
            SelectedNodeId = null;
            fitOnResize = true;
        }
        EmptyGraphText.Visibility = graph is null || graph.Nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RunHeader.Text = graph is null || string.IsNullOrWhiteSpace(graph.RunId)
            ? Text("观测流程 · 未启动", "Observation workflow · not started")
            : $"{graph.ExecutionModeText} · {(string.IsNullOrWhiteSpace(graph.TargetName) ? Text("观测流程", "Observation workflow") : graph.TargetName)}";
        RunHeader.ToolTip = RunHeader.Text;
        RunSubtitle.Text = graph?.RunSummary ?? Text("尚无本轮记录", "No run recorded");
        RunSubtitle.ToolTip = RunSubtitle.Text;
        if (graph is null)
        {
            GraphCanvas.Children.Clear(); nodeBounds.Clear(); nodeButtons.Clear(); currentMarkers.Clear(); edgeVisuals.Clear();
            nodeSignature = edgeSignature = lastCurrentNodeId = null;
            VisibleEdgeCount = 0;
            ShowDetails(null);
            return;
        }

        // Dashboard previews can publish frequently. Keep the same controls,
        // focus, zoom and scroll offsets unless the topology actually changed.
        var nextNodeSignature = string.Join("|", graph.Nodes.Select(node => $"{node.Id}:{node.Kind}:{node.Order}"));
        var topologyChanged = nodeSignature != nextNodeSignature;
        if (topologyChanged)
        {
            GraphCanvas.Children.Clear(); nodeBounds.Clear(); nodeButtons.Clear(); currentMarkers.Clear(); edgeVisuals.Clear();
            AddBranchBand();
            foreach (var node in graph.Nodes) { nodeBounds[node.Id] = BoundsFor(node); AddNode(node); }
            nodeSignature = nextNodeSignature;
            edgeSignature = null;
        }
        else
        {
            foreach (var node in graph.Nodes)
            {
                nodeButtons[node.Id].Content = node;
                nodeButtons[node.Id].Background = Brush(Colors(node).Background);
                nodeButtons[node.Id].ToolTip = $"{node.Label} · {node.StateText}\n{node.Summary}";
                AutomationProperties.SetName(nodeButtons[node.Id], $"{node.Label} · {node.StateText}");
                currentMarkers[node.Id].Visibility = node.IsCurrent ? Visibility.Visible : Visibility.Collapsed;
                currentMarkers[node.Id].Text = Text("当前", "NOW");
            }
        }
        var nextEdgeSignature = string.Join("|", graph.Edges.Select(edge => $"{edge.FromId}>{edge.ToId}:{edge.Kind}:{edge.Label}"));
        if (edgeSignature != nextEdgeSignature)
        {
            foreach (var visual in edgeVisuals) GraphCanvas.Children.Remove(visual);
            edgeVisuals.Clear(); VisibleEdgeCount = 0;
            foreach (var edge in graph.Edges) DrawEdge(edge);
            edgeSignature = nextEdgeSignature;
        }

        var selected = IsFollowingExecution && graph.CurrentNodeId is { } current ? current : SelectedNodeId;
        if (selected is null || !nodeBounds.ContainsKey(selected)) selected = graph.Nodes.FirstOrDefault()?.Id;
        SelectNodeCore(selected, userSelection: false);
        if (topologyChanged || newRun) Dispatcher.BeginInvoke(FitGraph, DispatcherPriority.Loaded);
        else if (IsFollowingExecution && graph.CurrentNodeId is not null && graph.CurrentNodeId != lastCurrentNodeId)
            Dispatcher.BeginInvoke(CenterSelection, DispatcherPriority.Loaded);
        lastCurrentNodeId = graph.CurrentNodeId;
    }

    private static Rect BoundsFor(ObservationWorkflowNode node)
    {
        if (node.Kind == ObservationWorkflowNodeKind.Branch)
            return new Rect(10 + Math.Clamp(node.Order, 0, 5) * 158, 130, 144, 66);
        if (node.Order < 5) return new Rect(10 + Math.Clamp(node.Order, 0, 4) * 190, 18, 176, 66);
        return new Rect(10 + Math.Clamp(10 - node.Order, 0, 5) * 158, 246, 144, 66);
    }

    private void AddBranchBand()
    {
        var band = new Border
        {
            Width = 948, Height = 122, CornerRadius = new CornerRadius(7),
            Background = Brush("#112335"), BorderBrush = Brush("#2E526C"), BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
        };
        Place(band, 2, 96);
        var caption = new TextBlock
        {
            Text = Text("定位方法 · 六种方法全部列出，不表示本轮都可用或会依次执行", "Acquisition methods · all six are shown; not all are available or executed in sequence"),
            Foreground = Brush("#8DB9D3"), FontSize = 11, IsHitTestVisible = false,
        };
        Place(caption, 12, 103);
    }

    private void AddNode(ObservationWorkflowNode node)
    {
        var bounds = nodeBounds[node.Id];
        var colors = Colors(node);
        var button = new Button
        {
            Width = bounds.Width, Height = bounds.Height, Content = node, Tag = node.Id,
            Style = (Style)Resources["WorkflowNodeButton"], ContentTemplate = (DataTemplate)Resources["WorkflowNodeTemplate"],
            Background = Brush(colors.Background), BorderBrush = Brush(colors.Border),
            ToolTip = $"{node.Label} · {node.StateText}\n{node.Summary}",
        };
        AutomationProperties.SetName(button, $"{node.Label} · {node.StateText}");
        button.Click += (_, _) => SelectNodeCore(node.Id, userSelection: true);
        nodeButtons[node.Id] = button;
        Place(button, bounds.X, bounds.Y);
        var marker = new TextBlock { Text = Text("当前", "NOW"), FontSize = 10, Foreground = Brush("#7DD3FC"), IsHitTestVisible = false,
            Visibility = node.IsCurrent ? Visibility.Visible : Visibility.Collapsed };
        currentMarkers[node.Id] = marker;
        Place(marker, bounds.Right - 29, bounds.Top - 13);
    }

    private void DrawEdge(ObservationWorkflowEdge edge)
    {
        if (!nodeBounds.TryGetValue(edge.FromId, out var from) || !nodeBounds.TryGetValue(edge.ToId, out var to)) return;
        var points = EdgePoints(from, to, edge.Kind);
        var color = edge.Kind == ObservationWorkflowEdgeKind.ObservedTransition ? "#F4AA58" :
            edge.Kind == ObservationWorkflowEdgeKind.Membership ? "#5F9EBB" : "#657D98";
        var line = new Polyline
        {
            Points = new PointCollection(points), Stroke = Brush(color), StrokeThickness = edge.Kind == ObservationWorkflowEdgeKind.ObservedTransition ? 2.4 : 1.5,
            ToolTip = edge.Label,
        };
        if (edge.Kind == ObservationWorkflowEdgeKind.Membership) line.StrokeDashArray = new DoubleCollection([4, 3]);
        AddEdgeVisual(line);
        var tip = points[^1];
        var previous = points[^2];
        var direction = tip - previous;
        if (direction.Length > 0)
        {
            direction.Normalize();
            var normal = new Vector(-direction.Y, direction.X);
            AddEdgeVisual(new Polygon
            {
                Points = new PointCollection([tip, tip - direction * 6 + normal * 3, tip - direction * 6 - normal * 3]),
                Fill = Brush(color), IsHitTestVisible = false,
            });
        }
        VisibleEdgeCount++;
    }

    private void AddEdgeVisual(UIElement element)
    {
        // The band and its caption are the first two visuals; lines stay behind
        // the persistent interactive nodes, so they cannot steal node clicks.
        GraphCanvas.Children.Insert(Math.Min(2, GraphCanvas.Children.Count), element);
        edgeVisuals.Add(element);
    }

    private static Point[] EdgePoints(Rect from, Rect to, ObservationWorkflowEdgeKind kind)
    {
        if (kind == ObservationWorkflowEdgeKind.Canonical && Math.Abs(from.Top - to.Top) < 1)
        {
            var right = to.Left > from.Left;
            return [new(right ? from.Right : from.Left, from.Top + from.Height / 2), new(right ? to.Left : to.Right, to.Top + to.Height / 2)];
        }
        if (kind == ObservationWorkflowEdgeKind.Canonical)
            return [new(from.Right, from.Top + from.Height / 2), new(959, from.Top + from.Height / 2), new(959, to.Top + to.Height / 2), new(to.Right, to.Top + to.Height / 2)];
        if (kind == ObservationWorkflowEdgeKind.ObservedTransition)
        {
            var lane = 229d;
            return [new(from.Left + from.Width / 2, from.Bottom), new(from.Left + from.Width / 2, lane), new(to.Left + to.Width / 2, lane), new(to.Left + to.Width / 2, to.Bottom)];
        }
        var fromAbove = from.Top < to.Top;
        var start = new Point(from.Left + from.Width / 2, fromAbove ? from.Bottom : from.Top);
        var end = new Point(to.Left + to.Width / 2, fromAbove ? to.Top : to.Bottom);
        var midY = fromAbove ? 122d : 237d;
        return [start, new(start.X, midY), new(end.X, midY), end];
    }

    private void Place(UIElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        GraphCanvas.Children.Add(element);
    }

    public void SelectNode(string id) => SelectNodeCore(id, userSelection: true);

    private void SelectNodeCore(string? id, bool userSelection)
    {
        if (userSelection) FollowCheckBox.IsChecked = false;
        SelectedNodeId = id;
        foreach (var pair in nodeButtons)
        {
            var node = (ObservationWorkflowNode)pair.Value.Content;
            pair.Value.BorderThickness = new Thickness(pair.Key == id ? 2.8 : node.IsCurrent ? 2.2 : 1.5);
            pair.Value.BorderBrush = Brush(pair.Key == id ? "#DDEEFF" : Colors(node).Border);
        }
        ShowDetails(Graph?.Nodes.FirstOrDefault(node => node.Id == id));
    }

    private void ShowDetails(ObservationWorkflowNode? node)
    {
        SelectedTitle.Text = node?.Label ?? Text("点击一个节点查看记录", "Select a node to inspect its records");
        SelectedStateText.Text = node?.StateText ?? Text("未记录", "Unrecorded");
        SelectedStateText.Foreground = Brush(node is null ? "#93A7BF" : Colors(node).Border);
        SelectedTimestampText.Text = node?.UpdatedUtc is { } updated
            ? $"{updated.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz} · {node.Code}"
            : Text("没有该节点的执行时间记录", "No execution timestamp recorded for this node");
        var reason = node is null ? Text("此视图只展示状态，不会执行节点。", "This view inspects state and never executes a node.") :
            node.Summary + (string.IsNullOrWhiteSpace(node.Code) ? "" : $"\n\n{Text("原始代码", "Original code")}: {node.Code}") +
            (string.IsNullOrWhiteSpace(node.Prerequisites) ? "" : $"\n\n{Text("使用条件", "Prerequisites")}: {node.Prerequisites}") +
            (string.IsNullOrWhiteSpace(node.GateText) ? "" : $"\n\n{Text("质量门记录（不等于阶段完成）", "Quality gate record (not stage completion)")}:\n{node.GateText}") +
            (string.IsNullOrWhiteSpace(node.CompletionSource) ? "" : $"\n{Text("完成来源", "Completion source")}: {node.CompletionSource}") +
            (string.IsNullOrWhiteSpace(Graph?.Notice) ? "" : $"\n\n{Graph!.Notice}");
        var metrics = node is null || node.Metrics.Count == 0 ? Text("尚无质量数值记录；不显示默认零值。", "No quality metrics recorded; default zeros are not fabricated.") :
            string.Join(Environment.NewLine, node.Metrics.Select(pair => $"{pair.Key} = {pair.Value.ToString("G6", CultureInfo.InvariantCulture)}"));
        var timeline = node is null || node.Timeline.Count == 0 ? Text("尚无此节点关联日志。", "No log entries associated with this node.") :
            string.Join("\n\n", node.Timeline.OrderBy(item => item.TimestampUtc).Select(item => $"{item.TimestampUtc.ToLocalTime():HH:mm:ss.fff} · {item.Code}\n{item.Message}"));
        var evidence = node is null || node.Evidence.Count == 0 ? Text("尚无此节点关联证据。不会把其他目标或旧运行的文件当作本轮证明。", "No evidence associated with this node. Other targets or old runs are not substituted as proof.") :
            string.Join("\n\n", node.Evidence.Select(item => $"{item.PublishedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {item.Kind}\n{item.Description}\n{item.AbsolutePath}"));
        if (!string.IsNullOrWhiteSpace(Graph?.ManifestPath))
            evidence += $"\n\n{Text("完整本轮归档（不是单个节点的完成证明；可复制路径）", "Complete run archive (not proof of an individual node's completion; copyable path)")}\n{Graph!.ManifestPath}";
        SetTextIfChanged(ReasonText, reason); SetTextIfChanged(MetricsText, metrics);
        SetTextIfChanged(TimelineText, timeline); SetTextIfChanged(EvidenceText, evidence);
    }

    private static void SetTextIfChanged(TextBox box, string value)
    {
        if (box.Text != value) box.Text = value;
    }

    private static (string Background, string Border) Colors(ObservationWorkflowNode node) =>
        node.StateKind == ObservationWorkflowNodeState.Passed && node.GateSeverity == GateSeverity.Warning
            ? ("#3A3220", "#E8C779") : Colors(node.StateKind);

    private static (string Background, string Border) Colors(ObservationWorkflowNodeState state) => state switch
    {
        ObservationWorkflowNodeState.Passed => ("#15382F", "#65C6A2"),
        ObservationWorkflowNodeState.Running or ObservationWorkflowNodeState.Selected => ("#14384B", "#66C9F0"),
        ObservationWorkflowNodeState.Fallback => ("#3A2C19", "#F4AA58"),
        ObservationWorkflowNodeState.Blocked or ObservationWorkflowNodeState.Faulted => ("#422328", "#EF8C98"),
        ObservationWorkflowNodeState.Paused or ObservationWorkflowNodeState.PauseRequested or ObservationWorkflowNodeState.Cancelling => ("#3A3220", "#E8C779"),
        ObservationWorkflowNodeState.Cancelled or ObservationWorkflowNodeState.Skipped or ObservationWorkflowNodeState.StoppedUnconfirmed => ("#282D38", "#AFB8C8"),
        _ => ("#192739", "#52667E"),
    };

    private static Brush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        ArrangePanels();
        if (fitOnResize) Dispatcher.BeginInvoke(FitGraph, DispatcherPriority.Loaded);
    }

    private void ArrangePanels()
    {
        if (!initialized) return;
        // The host may leave only ~230 px after its fixed controls. Stacking a
        // 150 px detail panel there would leave the graph only a scrollbar.
        // In short docks, keep both panes at full available height instead;
        // even a 540 px window still fits a readable node in the graph pane.
        var side = ActualWidth >= 1080 || (ActualWidth >= 480 && BodyGrid.ActualHeight < 290);
        var detailsWidth = Math.Clamp(ActualWidth * 0.45, 220, 340);
        BodyGrid.ColumnDefinitions[1].Width = new GridLength(side ? detailsWidth : 0);
        // Give the medium-width overview a full readable canvas before growing
        // its details; percentages otherwise crop both ends of the main path.
        BodyGrid.RowDefinitions[1].Height = new GridLength(side ? 0 : Math.Clamp(BodyGrid.ActualHeight - 300, 145, 230));
        Grid.SetColumn(DetailsPanel, side ? 1 : 0);
        Grid.SetRow(DetailsPanel, side ? 0 : 1);
        DetailsPanel.Margin = side ? new Thickness(7, 0, 0, 0) : new Thickness(0, 6, 0, 0);
    }

    private void FitGraph()
    {
        if (!initialized || GraphScroll.ViewportWidth <= 0 || GraphScroll.ViewportHeight <= 0) return;
        var scale = Math.Min((GraphScroll.ViewportWidth - 6) / GraphCanvas.Width, (GraphScroll.ViewportHeight - 6) / GraphCanvas.Height);
        ApplyZoom(Math.Clamp(scale, MinimumZoom, 1.15));
        GraphScroll.ScrollToHorizontalOffset(0);
        GraphScroll.ScrollToVerticalOffset(0);
        if (IsFollowingExecution && Graph?.CurrentNodeId is not null) CenterSelection();
    }

    private void ApplyZoom(double scale)
    {
        GraphScale.ScaleX = GraphScale.ScaleY = Math.Clamp(scale, MinimumZoom, MaximumZoom);
        ZoomText.Text = $"{GraphScale.ScaleX:P0}";
    }

    private void CenterSelection()
    {
        if (SelectedNodeId is null || !nodeBounds.TryGetValue(SelectedNodeId, out var bounds)) return;
        var scale = ZoomLevel;
        GraphScroll.ScrollToHorizontalOffset(Math.Max(0, (bounds.Left + bounds.Width / 2) * scale - GraphScroll.ViewportWidth / 2));
        GraphScroll.ScrollToVerticalOffset(Math.Max(0, (bounds.Top + bounds.Height / 2) * scale - GraphScroll.ViewportHeight / 2));
    }

    private void OnFitClick(object sender, RoutedEventArgs args) { fitOnResize = true; FitGraph(); }
    private void OnZoomOutClick(object sender, RoutedEventArgs args) { fitOnResize = false; ApplyZoom(ZoomLevel / 1.12); }
    private void OnZoomInClick(object sender, RoutedEventArgs args) { fitOnResize = false; ApplyZoom(ZoomLevel * 1.12); }

    private void OnFollowChanged(object sender, RoutedEventArgs args)
    {
        if (!initialized || !IsFollowingExecution || Graph?.CurrentNodeId is not { } current) return;
        SelectNodeCore(current, userSelection: false);
        Dispatcher.BeginInvoke(CenterSelection, DispatcherPriority.Loaded);
    }

    private void OnCanvasMouseWheel(object sender, MouseWheelEventArgs args)
    {
        fitOnResize = false;
        var anchor = args.GetPosition(GraphScroll);
        var old = ZoomLevel;
        var logicalX = (GraphScroll.HorizontalOffset + anchor.X) / old;
        var logicalY = (GraphScroll.VerticalOffset + anchor.Y) / old;
        ApplyZoom(old * (args.Delta > 0 ? 1.12 : 1 / 1.12));
        GraphScroll.UpdateLayout();
        GraphScroll.ScrollToHorizontalOffset(logicalX * ZoomLevel - anchor.X);
        GraphScroll.ScrollToVerticalOffset(logicalY * ZoomLevel - anchor.Y);
        args.Handled = true;
    }

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs args)
    {
        var source = args.OriginalSource as DependencyObject;
        while (source is not null && source != GraphScroll)
        {
            if (source is Button || source is System.Windows.Controls.Primitives.ScrollBar) return;
            source = VisualTreeHelper.GetParent(source);
        }
        FollowCheckBox.IsChecked = false;
        fitOnResize = false;
        panOrigin = args.GetPosition(GraphScroll);
        panHorizontal = GraphScroll.HorizontalOffset;
        panVertical = GraphScroll.VerticalOffset;
        GraphScroll.CaptureMouse();
        GraphScroll.Cursor = Cursors.Hand;
        args.Handled = true;
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs args)
    {
        if (panOrigin is not { } origin || args.LeftButton != MouseButtonState.Pressed) return;
        var delta = args.GetPosition(GraphScroll) - origin;
        GraphScroll.ScrollToHorizontalOffset(panHorizontal - delta.X);
        GraphScroll.ScrollToVerticalOffset(panVertical - delta.Y);
        args.Handled = true;
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs args) => EndPan();
    private void OnCanvasLostCapture(object sender, MouseEventArgs args) { panOrigin = null; GraphScroll.Cursor = Cursors.Arrow; }
    private void EndPan()
    {
        panOrigin = null;
        if (GraphScroll.IsMouseCaptured) GraphScroll.ReleaseMouseCapture();
        GraphScroll.Cursor = Cursors.Arrow;
    }
}
