using System.Windows.Media;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class AtrReductionPreviewTests
{
    [Fact]
    public void DelayedResultCannotReplaceANewerFrameOrDisposedHost()
    {
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        var first = new DrawingImage(); first.Freeze();
        var next = new DrawingImage(); next.Freeze();
        var result = new DrawingImage(); result.Freeze();
        host.PublishPreview(ObservationPreviewChannel.AtrSpectrum, first, "first");
        var captured = host.Dashboard.Previews[ObservationPreviewChannel.AtrSpectrum].UpdatedUtc;
        Assert.True(host.TryReplacePreview(ObservationPreviewChannel.AtrSpectrum, first, result, "reduced"));
        Assert.Equal(captured, host.Dashboard.Previews[ObservationPreviewChannel.AtrSpectrum].UpdatedUtc);
        host.PublishPreview(ObservationPreviewChannel.AtrSpectrum, next, "next");
        Assert.False(host.TryReplacePreview(ObservationPreviewChannel.AtrSpectrum, first, result, "stale"));
        Assert.Equal("next", host.Dashboard.Previews[ObservationPreviewChannel.AtrSpectrum].Caption);
        host.Dispose();
        Assert.False(host.TryReplacePreview(ObservationPreviewChannel.AtrSpectrum, next, result, "late"));
    }

    private sealed class SilentNotifier : IObservationAttentionNotifier
    {
        public void Notify(ObservationAttentionNotification notification) { }
        public void ClearActiveIndicator() { }
        public void Dispose() { }
    }
}
