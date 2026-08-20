using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace UvexAdv.Nina.Plugin;

internal sealed class InteractivePreviewWindow : Window
{
    private readonly ScrollViewer scroller;
    private readonly ScaleTransform scaleTransform = new(1, 1);
    private readonly Slider zoomSlider;
    private Point dragStart;
    private double horizontalStart;
    private double verticalStart;
    private bool dragging;

    public InteractivePreviewWindow(string title, ImageSource image, string caption)
    {
        Title = title;
        Width = 1280;
        Height = 860;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(4, 10, 18));

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var toolbar = new DockPanel { Margin = new Thickness(8) };
        toolbar.Children.Add(new TextBlock
        {
            Text = "滚轮缩放 · 左键拖动 · 双击复位",
            Foreground = Brushes.LightGray,
            VerticalAlignment = VerticalAlignment.Center,
        });
        zoomSlider = new Slider
        {
            Minimum = 0.1,
            Maximum = 8,
            Value = 1,
            TickFrequency = 0.1,
            Width = 230,
            Margin = new Thickness(16, 0, 0, 0),
        };
        DockPanel.SetDock(zoomSlider, Dock.Right);
        toolbar.Children.Add(zoomSlider);
        root.Children.Add(toolbar);

        var preview = new Image
        {
            Source = image,
            Stretch = Stretch.None,
            LayoutTransform = scaleTransform,
            SnapsToDevicePixels = true,
        };
        scroller = new ScrollViewer
        {
            Content = preview,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Black,
            CanContentScroll = false,
        };
        Grid.SetRow(scroller, 1);
        root.Children.Add(scroller);

        var captionBlock = new TextBlock
        {
            Text = caption,
            Foreground = Brushes.WhiteSmoke,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 7, 10, 9),
        };
        Grid.SetRow(captionBlock, 2);
        root.Children.Add(captionBlock);
        Content = root;

        zoomSlider.ValueChanged += (_, _) => SetZoom(zoomSlider.Value);
        scroller.PreviewMouseWheel += OnMouseWheel;
        scroller.PreviewMouseLeftButtonDown += OnMouseDown;
        scroller.PreviewMouseLeftButtonUp += OnMouseUp;
        scroller.PreviewMouseMove += OnMouseMove;
        scroller.MouseDoubleClick += (_, _) => zoomSlider.Value = 1;
    }

    private void SetZoom(double value)
    {
        scaleTransform.ScaleX = value;
        scaleTransform.ScaleY = value;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs args)
    {
        zoomSlider.Value = Math.Clamp(
            zoomSlider.Value * (args.Delta > 0 ? 1.15 : 1 / 1.15),
            zoomSlider.Minimum,
            zoomSlider.Maximum);
        args.Handled = true;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs args)
    {
        dragging = true;
        dragStart = args.GetPosition(scroller);
        horizontalStart = scroller.HorizontalOffset;
        verticalStart = scroller.VerticalOffset;
        scroller.Cursor = Cursors.SizeAll;
        scroller.CaptureMouse();
        args.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs args)
    {
        dragging = false;
        scroller.Cursor = Cursors.Arrow;
        scroller.ReleaseMouseCapture();
        args.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs args)
    {
        if (!dragging || args.LeftButton != MouseButtonState.Pressed) return;
        var current = args.GetPosition(scroller);
        scroller.ScrollToHorizontalOffset(horizontalStart - (current.X - dragStart.X));
        scroller.ScrollToVerticalOffset(verticalStart - (current.Y - dragStart.Y));
        args.Handled = true;
    }
}
