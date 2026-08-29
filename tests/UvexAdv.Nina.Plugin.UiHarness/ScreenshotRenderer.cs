using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UvexAdv.Nina.Plugin;

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
}

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
        var templates = new Templates();
        if (templates[ProductionTemplateKey] is not DataTemplate productionTemplate)
        {
            throw new InvalidOperationException(
                $"Production template '{ProductionTemplateKey}' was not found in Templates.xaml.");
        }

        try
        {
            return scenarios.Select(scenario => Render(productionTemplate, scenario, outputDirectory)).ToArray();
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
                Content = scenario.ViewModel,
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
            SelectScenarioTabs(host, scenario);
            PumpLoadedAndRender(window.Dispatcher);
            if (string.Equals(scenario.Name, "advanced", StringComparison.OrdinalIgnoreCase))
            {
                var algorithms = Descendants<Expander>(host).FirstOrDefault(expander =>
                    expander.Header?.ToString()?.StartsWith("目标获取算法", StringComparison.Ordinal) == true);
                if (algorithms is not null)
                {
                    algorithms.IsExpanded = true;
                    algorithms.UpdateLayout();
                    PumpLoadedAndRender(window.Dispatcher);
                }
                var brightTarget = Descendants<Expander>(host).FirstOrDefault(expander =>
                    expander.Header?.ToString()?.StartsWith("超亮目标", StringComparison.Ordinal) == true);
                if (brightTarget is not null)
                {
                    brightTarget.IsExpanded = true;
                    brightTarget.UpdateLayout();
                    brightTarget.BringIntoView();
                }
            }
            else if (string.Equals(scenario.Name, "qhy-g3-fast-pair", StringComparison.OrdinalIgnoreCase))
            {
                var transfer = Descendants<TextBlock>(host).FirstOrDefault(textBlock =>
                    string.Equals(textBlock.Text, "广域→狭缝场转换", StringComparison.Ordinal));
                transfer?.BringIntoView();
            }
            PumpLoadedAndRender(window.Dispatcher);

            host.Measure(new Size(scenario.Width, scenario.Height));
            host.Arrange(new Rect(0, 0, scenario.Width, scenario.Height));
            host.UpdateLayout();
            PumpLoadedAndRender(window.Dispatcher);

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
        };
    }

    private static void PumpLoadedAndRender(Dispatcher dispatcher)
    {
        dispatcher.Invoke(DispatcherPriority.Loaded, static () => { });
        dispatcher.Invoke(DispatcherPriority.Render, static () => { });
        dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
    }

    private static void SelectScenarioTabs(DependencyObject root, ScreenshotScenario scenario)
    {
        var outer = Descendants<TabControl>(root)
            .OrderByDescending(control => control.Items.Count)
            .FirstOrDefault();
        if (outer is null)
        {
            throw new InvalidOperationException("The production observation template does not contain its main TabControl.");
        }

        outer.SelectedIndex = Math.Clamp(scenario.ViewModel.SelectedWorkspaceTabIndex, 0, outer.Items.Count - 1);
        if (root is FrameworkElement element)
        {
            element.UpdateLayout();
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
