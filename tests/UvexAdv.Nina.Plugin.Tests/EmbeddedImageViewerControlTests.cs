using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class EmbeddedImageViewerControlTests
{
    [Fact]
    public void ControlLoadsAndTransitionsBetweenEmptyFitAndActualSizeStates()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var viewer = new EmbeddedImageViewer
                {
                    Width = 800,
                    Height = 500,
                    Caption = "10 s · gain 95 · R filter",
                    EmptyTitle = "尚无测试图像",
                    EmptyDetails = "等待模拟帧",
                    PopoutCommand = new RoutedCommand(),
                    PopoutLabel = "弹出测试大图",
                    ShowPopoutButton = true,
                };

                viewer.Measure(new Size(800, 500));
                viewer.Arrange(new Rect(0, 0, 800, 500));
                viewer.UpdateLayout();
                Assert.False(viewer.HasImage);
                Assert.Equal(1.0, viewer.Zoom);

                viewer.PreviewImage = CreateTestBitmap();
                viewer.Measure(new Size(800, 500));
                viewer.Arrange(new Rect(0, 0, 800, 500));
                viewer.UpdateLayout();
                Assert.True(viewer.HasImage);
                var toolbarButtons = Descendants<Button>(viewer).ToArray();
                Assert.Equal(5, toolbarButtons.Length);
                var popoutButton = Assert.Single(toolbarButtons, button => Equals(button.Content, "弹出测试大图"));
                Assert.Equal(Visibility.Visible, popoutButton.Visibility);
                Assert.All(toolbarButtons.Where(button => !ReferenceEquals(button, popoutButton)), button => Assert.True(button.IsEnabled));

                viewer.FitToViewport();
                Assert.InRange(viewer.Zoom, 0.05, 1.0);

                viewer.ShowActualSize();
                Assert.Equal(1.0, viewer.Zoom, precision: 10);

                viewer.PreviewImage = null;
                Assert.False(viewer.HasImage);
                Assert.Equal(1.0, viewer.Zoom, precision: 10);
                Assert.Equal(Visibility.Collapsed, popoutButton.Visibility);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF viewer smoke test timed out.");

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static BitmapSource CreateTestBitmap()
    {
        const int width = 1600;
        const int height = 1200;
        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = (byte)((x + y) % 256);
            }
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Gray8,
            null,
            pixels,
            width);
        bitmap.Freeze();
        return bitmap;
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
