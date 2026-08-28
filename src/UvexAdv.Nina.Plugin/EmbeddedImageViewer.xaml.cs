using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Dark, embedded astronomical-image viewer with cursor-anchored zoom and pan.
/// When an image is present it deliberately consumes mouse-wheel input so a
/// surrounding operator-panel ScrollViewer cannot scroll instead of zooming.
/// When no image is present, wheel input is allowed to bubble to the parent.
/// </summary>
public partial class EmbeddedImageViewer : UserControl
{
    private enum ViewMode
    {
        Fit,
        ActualSize,
        Custom,
    }

    private const double ButtonZoomFactor = 1.25;
    private Point dragStart;
    private double horizontalOffsetAtDragStart;
    private double verticalOffsetAtDragStart;
    private bool isDragging;
    private bool fitUpdateScheduled;
    private bool displayUpdateScheduled;
    private bool displayControlsReady;
    private bool syncingDisplayControls;
    private bool automaticStretch;
    private double displayBlackPoint;
    private double displayWhitePoint = 255;
    private double displayGamma = 1;
    private EmbeddedImageDisplayLevels displayedLevels = new(0, 255, 1, false);
    private double zoom = 1.0;
    private ViewMode viewMode = ViewMode.Fit;

    public static readonly RoutedUICommand FitCommand = new(
        "适配图像",
        nameof(FitCommand),
        typeof(EmbeddedImageViewer));

    public static readonly RoutedUICommand ActualSizeCommand = new(
        "一比一显示",
        nameof(ActualSizeCommand),
        typeof(EmbeddedImageViewer));

    public static readonly RoutedUICommand ZoomInCommand = new(
        "放大图像",
        nameof(ZoomInCommand),
        typeof(EmbeddedImageViewer));

    public static readonly RoutedUICommand ZoomOutCommand = new(
        "缩小图像",
        nameof(ZoomOutCommand),
        typeof(EmbeddedImageViewer));

    public static readonly DependencyProperty PreviewImageProperty = DependencyProperty.Register(
        nameof(PreviewImage),
        typeof(ImageSource),
        typeof(EmbeddedImageViewer),
        new FrameworkPropertyMetadata(null, OnPreviewImageChanged));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption),
        typeof(string),
        typeof(EmbeddedImageViewer),
        new FrameworkPropertyMetadata(string.Empty, OnCaptionChanged));

    public static readonly DependencyProperty EmptyTitleProperty = DependencyProperty.Register(
        nameof(EmptyTitle),
        typeof(string),
        typeof(EmbeddedImageViewer),
        new FrameworkPropertyMetadata("尚无预览图像"));

    public static readonly DependencyProperty EmptyDetailsProperty = DependencyProperty.Register(
        nameof(EmptyDetails),
        typeof(string),
        typeof(EmbeddedImageViewer),
        new FrameworkPropertyMetadata("自动观测尚未提供这一通道的图像。请检查当前阶段、服务连接和最近质量门。"));

    public static readonly DependencyProperty FitOnImageChangedProperty = DependencyProperty.Register(
        nameof(FitOnImageChanged),
        typeof(bool),
        typeof(EmbeddedImageViewer),
        new FrameworkPropertyMetadata(true));

    public static readonly DependencyProperty PopoutCommandProperty = DependencyProperty.Register(
        nameof(PopoutCommand),
        typeof(ICommand),
        typeof(EmbeddedImageViewer),
        new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty PopoutLabelProperty = DependencyProperty.Register(
        nameof(PopoutLabel),
        typeof(string),
        typeof(EmbeddedImageViewer),
        new FrameworkPropertyMetadata("弹出大图"));

    public static readonly DependencyProperty ShowPopoutButtonProperty = DependencyProperty.Register(
        nameof(ShowPopoutButton),
        typeof(bool),
        typeof(EmbeddedImageViewer),
        new FrameworkPropertyMetadata(false));

    private static readonly DependencyPropertyKey ZoomPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Zoom),
        typeof(double),
        typeof(EmbeddedImageViewer),
        new FrameworkPropertyMetadata(1.0));

    public static readonly DependencyProperty ZoomProperty = ZoomPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey HasImagePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(HasImage),
        typeof(bool),
        typeof(EmbeddedImageViewer),
        new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty HasImageProperty = HasImagePropertyKey.DependencyProperty;

    public EmbeddedImageViewer()
    {
        InitializeComponent();
        displayControlsReady = true;

        CommandBindings.Add(new CommandBinding(FitCommand, (_, _) => FitToViewport(), CanExecuteImageCommand));
        CommandBindings.Add(new CommandBinding(ActualSizeCommand, (_, _) => ShowActualSize(), CanExecuteImageCommand));
        CommandBindings.Add(new CommandBinding(ZoomInCommand, (_, _) => ZoomBy(ButtonZoomFactor), CanExecuteImageCommand));
        CommandBindings.Add(new CommandBinding(ZoomOutCommand, (_, _) => ZoomBy(1.0 / ButtonZoomFactor), CanExecuteImageCommand));

        Loaded += (_, _) =>
        {
            RefreshImageState();
            if (HasImage && viewMode == ViewMode.Fit)
            {
                ScheduleFitToViewport();
            }
        };
    }

    public ImageSource? PreviewImage
    {
        get => (ImageSource?)GetValue(PreviewImageProperty);
        set => SetValue(PreviewImageProperty, value);
    }

    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public string EmptyTitle
    {
        get => (string)GetValue(EmptyTitleProperty);
        set => SetValue(EmptyTitleProperty, value);
    }

    public string EmptyDetails
    {
        get => (string)GetValue(EmptyDetailsProperty);
        set => SetValue(EmptyDetailsProperty, value);
    }

    public bool FitOnImageChanged
    {
        get => (bool)GetValue(FitOnImageChangedProperty);
        set => SetValue(FitOnImageChangedProperty, value);
    }

    public ICommand? PopoutCommand
    {
        get => (ICommand?)GetValue(PopoutCommandProperty);
        set => SetValue(PopoutCommandProperty, value);
    }

    public string PopoutLabel
    {
        get => (string)GetValue(PopoutLabelProperty);
        set => SetValue(PopoutLabelProperty, value);
    }

    public bool ShowPopoutButton
    {
        get => (bool)GetValue(ShowPopoutButtonProperty);
        set => SetValue(ShowPopoutButtonProperty, value);
    }

    public double Zoom => (double)GetValue(ZoomProperty);

    public bool HasImage => (bool)GetValue(HasImageProperty);

    public bool AutomaticStretchEnabled => automaticStretch;

    internal EmbeddedImageDisplayLevels DisplayedLevels => displayedLevels;

    internal ImageSource? DisplayedImage => PreviewImageElement.Source;

    public void SetDisplayStretch(bool automatic, double blackPoint, double whitePoint, double gamma)
    {
        automaticStretch = automatic;
        displayBlackPoint = Math.Clamp(blackPoint, 0, 254);
        displayWhitePoint = Math.Clamp(whitePoint, displayBlackPoint + 1, 255);
        displayGamma = double.IsFinite(gamma) ? Math.Clamp(gamma, 0.2, 5) : 1;
        SyncDisplayControls();
        RefreshDisplayedImage();
    }

    /// <summary>Shows the complete image within the current embedded viewport.</summary>
    public void FitToViewport()
    {
        if (!TryGetImageDimensions(out var imageWidth, out var imageHeight))
        {
            return;
        }

        var viewportWidth = ViewportScrollViewer.ViewportWidth;
        var viewportHeight = ViewportScrollViewer.ViewportHeight;
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0)
        {
            viewportWidth = ViewportScrollViewer.ActualWidth;
        }
        if (!double.IsFinite(viewportHeight) || viewportHeight <= 0)
        {
            viewportHeight = ViewportScrollViewer.ActualHeight;
        }

        viewMode = ViewMode.Fit;
        SetZoom(EmbeddedImageViewportMath.CalculateFitZoom(
            viewportWidth,
            viewportHeight,
            imageWidth,
            imageHeight));
        ScrollToOffsetsAfterLayout(new ViewportOffsets(0, 0));
    }

    /// <summary>Displays source pixels at the viewer's conventional 1:1 scale.</summary>
    public void ShowActualSize()
    {
        if (!HasImage)
        {
            return;
        }

        viewMode = ViewMode.ActualSize;
        SetZoom(1.0);
        ScrollToOffsetsAfterLayout(new ViewportOffsets(0, 0));
    }

    private static void OnPreviewImageChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var viewer = (EmbeddedImageViewer)dependencyObject;
        viewer.RefreshDisplayedImage();
        viewer.RefreshImageState();

        if (viewer.HasImage && viewer.FitOnImageChanged)
        {
            viewer.viewMode = ViewMode.Fit;
            viewer.ScheduleFitToViewport();
        }
    }

    private static void OnCaptionChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        ((EmbeddedImageViewer)dependencyObject).RefreshCaptionVisibility();
    }

    private void RefreshImageState()
    {
        var hasImage = TryGetImageDimensions(out var imageWidth, out var imageHeight);
        SetValue(HasImagePropertyKey, hasImage);
        EmptyStatePanel.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;
        PreviewImageElement.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;

        if (hasImage)
        {
            // Width/height are set from bitmap pixels so 1:1 has a stable meaning
            // even when a FITS-derived BitmapSource contains unusual DPI metadata.
            PreviewImageElement.Width = imageWidth;
            PreviewImageElement.Height = imageHeight;
        }
        else
        {
            isDragging = false;
            PreviewImageElement.ClearValue(WidthProperty);
            PreviewImageElement.ClearValue(HeightProperty);
            SetZoom(1.0);
        }

        RefreshCaptionVisibility();
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshDisplayedImage()
    {
        if (!displayControlsReady) return;
        if (PreviewImage is not BitmapSource bitmapSource)
        {
            PreviewImageElement.Source = PreviewImage;
            displayedLevels = new EmbeddedImageDisplayLevels(0, 255, 1, false);
            DisplayLevelsText.Text = PreviewImage is null ? "显示：无图像" : "显示：此图像不支持拉伸";
            return;
        }

        try
        {
            PreviewImageElement.Source = EmbeddedImageDisplayTransform.Apply(
                bitmapSource,
                automaticStretch,
                displayBlackPoint,
                displayWhitePoint,
                displayGamma,
                out displayedLevels);
            DisplayLevelsText.Text = displayedLevels.Automatic
                ? $"显示：自动 黑 {displayedLevels.BlackPoint} / 白 {displayedLevels.WhitePoint} / γ {displayedLevels.Gamma:0.00}"
                : displayedLevels.BlackPoint == 0 && displayedLevels.WhitePoint == 255 && Math.Abs(displayedLevels.Gamma - 1) < 1e-9
                    ? "显示：原图"
                    : $"显示：手动 黑 {displayedLevels.BlackPoint} / 白 {displayedLevels.WhitePoint} / γ {displayedLevels.Gamma:0.00}";
        }
        catch
        {
            // A display transform must never block acquisition or hide a valid
            // source preview. Fall back to the immutable source bitmap.
            PreviewImageElement.Source = bitmapSource;
            displayedLevels = new EmbeddedImageDisplayLevels(0, 255, 1, false);
            DisplayLevelsText.Text = "显示：原图（拉伸不可用）";
        }
    }

    private void ScheduleDisplayRefresh()
    {
        if (!displayControlsReady || displayUpdateScheduled) return;
        displayUpdateScheduled = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () =>
            {
                displayUpdateScheduled = false;
                RefreshDisplayedImage();
            });
    }

    private void SyncDisplayControls()
    {
        if (!displayControlsReady) return;
        syncingDisplayControls = true;
        try
        {
            BlackPointSlider.Value = displayBlackPoint;
            WhitePointSlider.Value = displayWhitePoint;
            GammaSlider.Value = displayGamma;
            BlackPointText.Text = $"{displayBlackPoint:0}";
            WhitePointText.Text = $"{displayWhitePoint:0}";
            GammaText.Text = $"{displayGamma:0.00}";
            AutoStretchButton.Content = automaticStretch ? "自动拉伸：开" : "自动拉伸：关";
            AutoStretchButton.BorderBrush = automaticStretch ? Brushes.DeepSkyBlue : new SolidColorBrush(Color.FromRgb(82, 100, 122));
        }
        finally
        {
            syncingDisplayControls = false;
        }
    }

    private bool TryGetImageDimensions(out double width, out double height)
    {
        if (PreviewImage is BitmapSource bitmapSource && bitmapSource.PixelWidth > 0 && bitmapSource.PixelHeight > 0)
        {
            width = bitmapSource.PixelWidth;
            height = bitmapSource.PixelHeight;
            return true;
        }

        width = PreviewImage?.Width ?? 0;
        height = PreviewImage?.Height ?? 0;
        return double.IsFinite(width) && width > 0 && double.IsFinite(height) && height > 0;
    }

    private void RefreshCaptionVisibility()
    {
        if (!IsInitialized)
        {
            return;
        }

        CaptionPanel.Visibility = string.IsNullOrWhiteSpace(Caption)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void CanExecuteImageCommand(object sender, CanExecuteRoutedEventArgs args)
    {
        args.CanExecute = HasImage;
        args.Handled = true;
    }

    private void ZoomBy(double factor)
    {
        if (!HasImage)
        {
            return;
        }

        var viewport = GetViewportSize();
        var anchor = new Point(viewport.Width / 2.0, viewport.Height / 2.0);
        SetCustomZoom(EmbeddedImageViewportMath.ClampZoom(zoom * factor), anchor);
    }

    private void SetCustomZoom(double requestedZoom, Point anchor)
    {
        var newZoom = EmbeddedImageViewportMath.ClampZoom(requestedZoom);
        var viewport = GetViewportSize();
        var offsets = TryGetImageDimensions(out var imageWidth, out var imageHeight)
            ? EmbeddedImageViewportMath.CalculateCenteredAnchoredOffsets(
                zoom,
                newZoom,
                anchor.X,
                anchor.Y,
                ViewportScrollViewer.HorizontalOffset,
                ViewportScrollViewer.VerticalOffset,
                imageWidth,
                imageHeight,
                viewport.Width,
                viewport.Height)
            : new ViewportOffsets(0, 0);

        viewMode = ViewMode.Custom;
        SetZoom(newZoom);
        ScrollToOffsetsAfterLayout(offsets);
    }

    private void SetZoom(double requestedZoom)
    {
        zoom = EmbeddedImageViewportMath.ClampZoom(requestedZoom);
        ImageScaleTransform.ScaleX = zoom;
        ImageScaleTransform.ScaleY = zoom;
        SetValue(ZoomPropertyKey, zoom);
        ZoomTextBlock.Text = $"{zoom * 100:0}%";
    }

    private void ScrollToOffsetsAfterLayout(ViewportOffsets offsets)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                ViewportScrollViewer.ScrollToHorizontalOffset(offsets.Horizontal);
                ViewportScrollViewer.ScrollToVerticalOffset(offsets.Vertical);
            });
    }

    private void ScheduleFitToViewport()
    {
        if (fitUpdateScheduled)
        {
            return;
        }

        fitUpdateScheduled = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                fitUpdateScheduled = false;
                if (HasImage && viewMode == ViewMode.Fit)
                {
                    FitToViewport();
                }
            });
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (IsFromScrollBar(args.OriginalSource))
        {
            return;
        }

        if (!EmbeddedImageViewportMath.ShouldHandleMouseWheel(HasImage))
        {
            return;
        }

        // Mark first: even at 5%/1600%, an outer ScrollViewer must not suddenly
        // move the whole automation panel while the pointer is over an image.
        args.Handled = true;
        var anchor = args.GetPosition(ViewportScrollViewer);
        SetCustomZoom(EmbeddedImageViewportMath.CalculateWheelZoom(zoom, args.Delta), anchor);
    }

    private void OnZoomOutClick(object sender, RoutedEventArgs args) => ZoomBy(1.0 / ButtonZoomFactor);

    private void OnZoomInClick(object sender, RoutedEventArgs args) => ZoomBy(ButtonZoomFactor);

    private void OnFitClick(object sender, RoutedEventArgs args) => FitToViewport();

    private void OnActualSizeClick(object sender, RoutedEventArgs args) => ShowActualSize();

    private void OnAutoStretchClick(object sender, RoutedEventArgs args)
    {
        automaticStretch = !automaticStretch;
        SyncDisplayControls();
        RefreshDisplayedImage();
    }

    private void OnResetDisplayClick(object sender, RoutedEventArgs args) =>
        SetDisplayStretch(false, 0, 255, 1);

    private void OnManualDisplayValueChanged(object sender, RoutedPropertyChangedEventArgs<double> args)
    {
        if (!displayControlsReady || syncingDisplayControls) return;
        automaticStretch = false;
        displayBlackPoint = Math.Min(BlackPointSlider.Value, WhitePointSlider.Value - 1);
        displayWhitePoint = Math.Max(WhitePointSlider.Value, displayBlackPoint + 1);
        displayGamma = GammaSlider.Value;
        BlackPointText.Text = $"{displayBlackPoint:0}";
        WhitePointText.Text = $"{displayWhitePoint:0}";
        GammaText.Text = $"{displayGamma:0.00}";
        AutoStretchButton.Content = "自动拉伸：关";
        ScheduleDisplayRefresh();
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (!HasImage || IsFromScrollBar(args.OriginalSource))
        {
            return;
        }

        if (args.ClickCount >= 2)
        {
            FitToViewport();
            args.Handled = true;
            return;
        }

        isDragging = true;
        dragStart = args.GetPosition(ViewportScrollViewer);
        horizontalOffsetAtDragStart = ViewportScrollViewer.HorizontalOffset;
        verticalOffsetAtDragStart = ViewportScrollViewer.VerticalOffset;
        ViewportScrollViewer.Cursor = Cursors.SizeAll;
        ViewportScrollViewer.CaptureMouse();
        args.Handled = true;
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs args)
    {
        if (!isDragging)
        {
            return;
        }

        EndDrag();
        args.Handled = true;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs args)
    {
        if (!isDragging || args.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = args.GetPosition(ViewportScrollViewer);
        ViewportScrollViewer.ScrollToHorizontalOffset(horizontalOffsetAtDragStart - (current.X - dragStart.X));
        ViewportScrollViewer.ScrollToVerticalOffset(verticalOffsetAtDragStart - (current.Y - dragStart.Y));
        args.Handled = true;
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs args) => EndDrag();

    private void EndDrag()
    {
        isDragging = false;
        ViewportScrollViewer.Cursor = Cursors.Arrow;
        if (ViewportScrollViewer.IsMouseCaptured)
        {
            ViewportScrollViewer.ReleaseMouseCapture();
        }
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (HasImage && viewMode == ViewMode.Fit)
        {
            // SizeChanged already runs after WPF has assigned a usable viewport.
            // Fit synchronously so the first painted frame (and offline screenshot)
            // never flashes or captures a clipped 100% image.
            FitToViewport();
        }
    }

    private Size GetViewportSize()
    {
        var width = ViewportScrollViewer.ViewportWidth;
        var height = ViewportScrollViewer.ViewportHeight;
        if (!double.IsFinite(width) || width <= 0)
        {
            width = ViewportScrollViewer.ActualWidth;
            if (!double.IsFinite(width) || width <= 0)
            {
                width = 1;
            }
        }
        if (!double.IsFinite(height) || height <= 0)
        {
            height = ViewportScrollViewer.ActualHeight;
            if (!double.IsFinite(height) || height <= 0)
            {
                height = 1;
            }
        }

        return new Size(width, height);
    }

    private static bool IsFromScrollBar(object originalSource)
    {
        var current = originalSource as DependencyObject;
        while (current is not null)
        {
            if (current is ScrollBar)
            {
                return true;
            }

            current = current switch
            {
                Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(current),
                FrameworkContentElement contentElement => contentElement.Parent,
                _ => null,
            };
        }

        return false;
    }
}
