using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Localizes static operator-facing XAML literals without duplicating the
/// templates. The original Chinese value or Binding is retained in a weak
/// snapshot so a N.I.N.A. locale change can always restore Chinese exactly.
/// </summary>
public static class ObservationStaticTextLocalization
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(ObservationStaticTextLocalization),
        new FrameworkPropertyMetadata(
            false,
            FrameworkPropertyMetadataOptions.Inherits,
            OnIsEnabledChanged));

    private static readonly ConditionalWeakTable<DependencyObject, ElementSnapshot> Snapshots = new();
    private static readonly object RootsSync = new();
    private static readonly List<WeakReference<FrameworkElement>> Roots = [];
    private static CultureInfo? selectedCulture;

    public static event EventHandler? CultureChanged;

    static ObservationStaticTextLocalization()
    {
        // DataTemplate roots may already carry the attached value when WPF
        // materializes them, in which case an instance property-changed
        // callback is not guaranteed on every framework version. A guarded
        // class handler makes Loaded deterministic while touching only the
        // subtree that explicitly inherited this behavior.
        EventManager.RegisterClassHandler(
            typeof(FrameworkElement),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnAnyFrameworkElementLoaded),
            handledEventsToo: true);
    }

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    /// <summary>
    /// Selects the authoritative N.I.N.A. UI culture and refreshes every live
    /// localized template. Pass <see langword="null"/> to resume following
    /// <see cref="CultureInfo.CurrentUICulture"/>.
    /// </summary>
    public static void SetCulture(CultureInfo? culture)
    {
        var previous = EffectiveCulture.Name;
        Volatile.Write(ref selectedCulture, culture);
        RefreshAll();
        if (!string.Equals(previous, EffectiveCulture.Name, StringComparison.OrdinalIgnoreCase))
        {
            CultureChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>Reapplies localization to all live template roots.</summary>
    public static void RefreshAll()
    {
        List<FrameworkElement> liveRoots = [];
        lock (RootsSync)
        {
            for (var index = Roots.Count - 1; index >= 0; index--)
            {
                if (!Roots[index].TryGetTarget(out var root))
                {
                    Roots.RemoveAt(index);
                    continue;
                }

                if (!liveRoots.Contains(root, ReferenceEqualityComparer.Instance))
                {
                    liveRoots.Add(root);
                }
            }
        }

        var culture = EffectiveCulture;
        foreach (var root in liveRoots)
        {
            if (root.Dispatcher.CheckAccess())
            {
                ApplyTree(root, culture);
            }
            else
            {
                root.Dispatcher.BeginInvoke(
                    DispatcherPriority.DataBind,
                    () => ApplyTree(root, EffectiveCulture));
            }
        }
    }

    internal static CultureInfo EffectiveCulture =>
        Volatile.Read(ref selectedCulture) ?? CultureInfo.CurrentUICulture;

    internal static string Translate(string source, CultureInfo culture) =>
        ObservationStaticTextCatalog.Translate(source, culture);

    /// <summary>
    /// Applies the selected language to an already materialized host subtree.
    /// This is useful for offline rendering hosts and for shell integrations
    /// that create a ContentPresenter after its DataTemplate root loaded.
    /// </summary>
    public static void LocalizeSubtree(FrameworkElement root, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ApplyTree(root, culture ?? EffectiveCulture);
    }

    internal static void ApplyTree(FrameworkElement root, CultureInfo culture)
    {
        var pending = new Stack<DependencyObject>();
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            ApplyElement(current, culture);

            if (current is FrameworkElement or FrameworkContentElement)
            {
                foreach (var logicalChild in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>())
                {
                    pending.Push(logicalChild);
                }
            }

            if (current is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                {
                    pending.Push(VisualTreeHelper.GetChild(current, index));
                }
            }
        }
    }

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not FrameworkElement element)
        {
            return;
        }

        element.Loaded -= OnElementLoaded;
        if (args.NewValue is not true)
        {
            return;
        }

        element.Loaded += OnElementLoaded;

        // Only values explicitly placed on template roots enter the refresh
        // registry. Descendants inherit IsEnabled and localize on Loaded.
        if (element.ReadLocalValue(IsEnabledProperty) is true)
        {
            lock (RootsSync)
            {
                Roots.Add(new WeakReference<FrameworkElement>(element));
            }
        }

        if (element.IsLoaded)
        {
            ApplyElement(element, EffectiveCulture);
        }
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement element || !GetIsEnabled(element))
        {
            return;
        }

        // A root scan catches elements created by an already-materialized
        // ControlTemplate. Inherited descendants also receive this callback,
        // which covers later item-container generation.
        if (element.ReadLocalValue(IsEnabledProperty) is true)
        {
            ApplyTree(element, EffectiveCulture);
        }
        else
        {
            ApplyElement(element, EffectiveCulture);
        }
    }

    private static void OnAnyFrameworkElementLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement element || !GetIsEnabled(element))
        {
            return;
        }

        if (element.ReadLocalValue(IsEnabledProperty) is bool localValue && localValue)
        {
            lock (RootsSync)
            {
                Roots.Add(new WeakReference<FrameworkElement>(element));
            }
            ApplyTree(element, EffectiveCulture);
        }
        else
        {
            ApplyElement(element, EffectiveCulture);
        }
    }

    private static void ApplyElement(DependencyObject element, CultureInfo culture)
    {
        if (element is TextBlock)
        {
            ApplyProperty(element, TextBlock.TextProperty, culture);
        }

        if (element is ContentControl)
        {
            ApplyProperty(element, ContentControl.ContentProperty, culture);
        }

        if (element is HeaderedContentControl)
        {
            ApplyProperty(element, HeaderedContentControl.HeaderProperty, culture);
        }

        if (element is HeaderedItemsControl)
        {
            ApplyProperty(element, HeaderedItemsControl.HeaderProperty, culture);
        }

        if (element is FrameworkElement)
        {
            ApplyProperty(element, FrameworkElement.ToolTipProperty, culture);
            ApplyProperty(element, AutomationProperties.NameProperty, culture);
        }

        if (element is EmbeddedImageViewer viewer)
        {
            ApplyProperty(viewer, EmbeddedImageViewer.EmptyTitleProperty, culture);
            ApplyProperty(viewer, EmbeddedImageViewer.EmptyDetailsProperty, culture);
            ApplyProperty(viewer, EmbeddedImageViewer.PopoutLabelProperty, culture);
        }
    }

    private static void ApplyProperty(DependencyObject element, DependencyProperty property, CultureInfo culture)
    {
        var snapshot = Snapshots.GetOrCreateValue(element);
        if (!snapshot.Values.TryGetValue(property, out var original))
        {
            var binding = BindingOperations.GetBindingBase(element, property);
            if (binding is not null)
            {
                if (binding is not Binding textBinding || string.IsNullOrWhiteSpace(textBinding.StringFormat))
                {
                    // Dynamic ViewModel text is localized by the presentation
                    // layer. Never replace a live binding with its current
                    // evaluated string.
                    return;
                }
                original = new OriginalValue(textBinding, null);
            }
            else
            {
                var localValue = element.ReadLocalValue(property);
                // Values authored inside a DataTemplate often have
                // ParentTemplate value source rather than Local. GetValue is
                // therefore required even though the XAML itself is static.
                var effectiveValue = localValue is string ? localValue : element.GetValue(property);
                if (effectiveValue is not string staticText)
                {
                    return;
                }

                original = new OriginalValue(null, staticText);
            }

            snapshot.Values.Add(property, original);
        }

        if (original.Binding is not null)
        {
            var localizedFormat = ObservationStaticTextCatalog.Translate(original.Binding.StringFormat, culture);
            BindingOperations.SetBinding(element, property, CloneBinding(original.Binding, localizedFormat));
            return;
        }

        element.SetCurrentValue(
            property,
            ObservationStaticTextCatalog.Translate(original.StaticText, culture));
    }

    private static Binding CloneBinding(Binding source, string stringFormat)
    {
        var clone = new Binding
        {
            AsyncState = source.AsyncState,
            BindingGroupName = source.BindingGroupName,
            BindsDirectlyToSource = source.BindsDirectlyToSource,
            Converter = source.Converter,
            ConverterCulture = source.ConverterCulture,
            ConverterParameter = source.ConverterParameter,
            Delay = source.Delay,
            FallbackValue = source.FallbackValue,
            IsAsync = source.IsAsync,
            Mode = source.Mode,
            NotifyOnSourceUpdated = source.NotifyOnSourceUpdated,
            NotifyOnTargetUpdated = source.NotifyOnTargetUpdated,
            NotifyOnValidationError = source.NotifyOnValidationError,
            StringFormat = stringFormat,
            TargetNullValue = source.TargetNullValue,
            UpdateSourceExceptionFilter = source.UpdateSourceExceptionFilter,
            UpdateSourceTrigger = source.UpdateSourceTrigger,
            ValidatesOnDataErrors = source.ValidatesOnDataErrors,
            ValidatesOnExceptions = source.ValidatesOnExceptions,
            ValidatesOnNotifyDataErrors = source.ValidatesOnNotifyDataErrors,
            XPath = source.XPath,
        };

        if (source.Path is not null)
        {
            clone.Path = source.Path;
        }

        if (!string.IsNullOrWhiteSpace(source.ElementName))
        {
            clone.ElementName = source.ElementName;
        }
        else if (source.RelativeSource is not null)
        {
            clone.RelativeSource = source.RelativeSource;
        }
        else if (source.Source is not null)
        {
            clone.Source = source.Source;
        }

        foreach (var validationRule in source.ValidationRules)
        {
            clone.ValidationRules.Add(validationRule);
        }

        return clone;
    }

    private sealed class ElementSnapshot
    {
        public Dictionary<DependencyProperty, OriginalValue> Values { get; } = [];
    }

    private sealed record OriginalValue(Binding? Binding, string? StaticText);
}
