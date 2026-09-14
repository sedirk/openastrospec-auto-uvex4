using System.Windows;
using System.Runtime.Versioning;
using System.Windows.Media;
using AvalonDock;
using AvalonDock.Layout;
using NINA.Equipment.Interfaces.ViewModel;

namespace UvexAdv.Nina.Plugin;

/// <summary>Activates only an already-created N.I.N.A. dock. Never creates a layout or window.</summary>
[SupportedOSPlatform("windows")]
internal static class NativeDockNavigation
{
    internal static LayoutAnchorable? ActivateExisting(IDockableVM panel)
    {
        var application = Application.Current;
        if (application is null || !application.Dispatcher.CheckAccess()) return null;
        foreach (Window window in application.Windows)
        foreach (var manager in Descendants(window).OfType<DockingManager>())
        {
            var root = manager.Layout;
            if (root is null) continue;
            var candidates = root.Descendents().OfType<LayoutAnchorable>().Concat(root.Hidden).Distinct().ToArray();
            var existing = FindExisting(candidates, panel, panel.ContentId);
            if (existing is null) continue;
            if (existing.IsHidden) existing.Show();
            existing.IsSelected = true;
            existing.IsActive = true;
            return existing.IsSelected && existing.IsVisible ? existing : null;
        }
        return null;
    }

    internal static LayoutAnchorable? FindExisting(IEnumerable<LayoutAnchorable> candidates, object panel, string contentId)
    {
        var panels = candidates.ToArray();
        var shared = panels.Where(item => ReferenceEquals(item.Content, panel)).ToArray();
        if (shared.Length == 1) return shared[0];
        if (shared.Length > 1) return null;
        var matchingIds = panels.Where(item => string.Equals(item.ContentId, contentId, StringComparison.Ordinal)).ToArray();
        return matchingIds.Length == 1 ? matchingIds[0] : null;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        foreach (var descendant in Descendants(VisualTreeHelper.GetChild(parent, index)))
            yield return descendant;
    }
}
