using System.Windows;
using System.Windows.Media;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Large-preview host for the same image-viewer control used by the embedded
/// real-time panels. Keeping one viewer implementation gives both surfaces the
/// same stretch, black/white point, gamma, fit, 1:1, zoom and pan behavior.
/// </summary>
internal sealed class InteractivePreviewWindow : Window
{
    public InteractivePreviewWindow(string title, ImageSource image, string caption)
    {
        Title = title;
        Width = 1280;
        Height = 860;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(4, 10, 18));

        Content = new EmbeddedImageViewer
        {
            FitOnImageChanged = true,
            ShowPopoutButton = false,
            PreviewImage = image,
            Caption = caption,
            EmptyTitle = ObservationUiPresentation.Text(
                "尚无可显示图像",
                "No image is available",
                ObservationStaticTextLocalization.EffectiveCulture),
            EmptyDetails = ObservationUiPresentation.Text(
                "关闭此窗口并等待下一张实时预览。",
                "Close this window and wait for the next live preview.",
                ObservationStaticTextLocalization.EffectiveCulture),
        };
    }
}
