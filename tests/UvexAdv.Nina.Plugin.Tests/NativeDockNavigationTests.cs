using AvalonDock.Layout;
using UvexAdv.Nina.Plugin;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class NativeDockNavigationTests
{
    [Fact]
    public void ExistingSharedPanelWinsOverAnotherPanelsMatchingId()
    {
        var shared = new object();
        var actual = new LayoutAnchorable { Content = shared, ContentId = "Calibration" };
        var other = new LayoutAnchorable { Content = new object(), ContentId = "Calibration" };
        Assert.Same(actual, NativeDockNavigation.FindExisting([other, actual], shared, "Calibration"));
    }

    [Fact]
    public void UniqueNativeContentIdMayIdentifyTheExistingPanel()
    {
        var actual = new LayoutAnchorable { ContentId = "Calibration" };
        Assert.Same(actual, NativeDockNavigation.FindExisting([actual], new object(), "Calibration"));
        Assert.Null(NativeDockNavigation.FindExisting([actual], new object(), "Unknown"));
    }

    [Fact]
    public void AmbiguousNativeLayoutIsNotArbitrarilySelected()
    {
        var first = new LayoutAnchorable { ContentId = "Calibration" };
        var second = new LayoutAnchorable { ContentId = "Calibration" };
        Assert.Null(NativeDockNavigation.FindExisting([first, second], new object(), "Calibration"));
    }
}
