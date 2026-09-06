using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Immutable display-only layers associated with the original camera bitmap.
/// The shared embedded/popout viewer stretches only the bitmap, never these
/// annotations or the separately laid-out diagnostic spectrum.
/// </summary>
internal sealed record ObservationPreviewLayers(ImageSource? Overlay, ImageSource? Spectrum)
{
    private static readonly ConditionalWeakTable<ImageSource, ObservationPreviewLayers> Layers = new();

    internal static BitmapSource Attach(BitmapSource image, ImageSource? overlay = null, ImageSource? spectrum = null)
    {
        if (image.CanFreeze && !image.IsFrozen) image.Freeze();
        if (overlay is { CanFreeze: true, IsFrozen: false }) overlay.Freeze();
        if (spectrum is { CanFreeze: true, IsFrozen: false }) spectrum.Freeze();
        Layers.Add(image, new ObservationPreviewLayers(overlay, spectrum));
        return image;
    }

    internal static ObservationPreviewLayers? For(ImageSource? image) =>
        image is not null && Layers.TryGetValue(image, out var layers) ? layers : null;
}
