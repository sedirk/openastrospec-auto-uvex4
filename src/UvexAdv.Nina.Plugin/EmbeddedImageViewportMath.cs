namespace UvexAdv.Nina.Plugin;

internal readonly record struct ViewportOffsets(double Horizontal, double Vertical);

/// <summary>
/// Pure viewport calculations shared by the embedded image viewer and its tests.
/// Keeping these calculations independent from WPF input events makes zoom behavior
/// deterministic and testable without a running N.I.N.A. window.
/// </summary>
internal static class EmbeddedImageViewportMath
{
    internal const double MinimumZoom = 0.05;
    internal const double MaximumZoom = 16.0;
    internal const double WheelZoomFactor = 1.15;

    internal static double ClampZoom(double zoom) =>
        Math.Clamp(zoom, MinimumZoom, MaximumZoom);

    internal static double CalculateFitZoom(
        double viewportWidth,
        double viewportHeight,
        double imageWidth,
        double imageHeight)
    {
        if (!IsPositiveFinite(viewportWidth) ||
            !IsPositiveFinite(viewportHeight) ||
            !IsPositiveFinite(imageWidth) ||
            !IsPositiveFinite(imageHeight))
        {
            return 1.0;
        }

        return ClampZoom(Math.Min(viewportWidth / imageWidth, viewportHeight / imageHeight));
    }

    internal static double CalculateWheelZoom(double currentZoom, int wheelDelta)
    {
        if (wheelDelta == 0)
        {
            return ClampZoom(currentZoom);
        }

        var factor = wheelDelta > 0 ? WheelZoomFactor : 1.0 / WheelZoomFactor;
        return ClampZoom(currentZoom * factor);
    }

    internal static ViewportOffsets CalculateAnchoredOffsets(
        double oldZoom,
        double newZoom,
        double anchorX,
        double anchorY,
        double oldHorizontalOffset,
        double oldVerticalOffset)
    {
        if (!IsPositiveFinite(oldZoom) || !IsPositiveFinite(newZoom))
        {
            return new ViewportOffsets(
                Math.Max(0, oldHorizontalOffset),
                Math.Max(0, oldVerticalOffset));
        }

        var imageX = (Math.Max(0, oldHorizontalOffset) + Math.Max(0, anchorX)) / oldZoom;
        var imageY = (Math.Max(0, oldVerticalOffset) + Math.Max(0, anchorY)) / oldZoom;

        return new ViewportOffsets(
            Math.Max(0, (imageX * newZoom) - Math.Max(0, anchorX)),
            Math.Max(0, (imageY * newZoom) - Math.Max(0, anchorY)));
    }

    internal static ViewportOffsets CalculateCenteredAnchoredOffsets(
        double oldZoom,
        double newZoom,
        double anchorX,
        double anchorY,
        double oldHorizontalOffset,
        double oldVerticalOffset,
        double imageWidth,
        double imageHeight,
        double viewportWidth,
        double viewportHeight)
    {
        if (!IsPositiveFinite(oldZoom) ||
            !IsPositiveFinite(newZoom) ||
            !IsPositiveFinite(imageWidth) ||
            !IsPositiveFinite(imageHeight) ||
            !IsPositiveFinite(viewportWidth) ||
            !IsPositiveFinite(viewportHeight))
        {
            return CalculateAnchoredOffsets(
                oldZoom,
                newZoom,
                anchorX,
                anchorY,
                oldHorizontalOffset,
                oldVerticalOffset);
        }

        var oldPaddingX = Math.Max(0, (viewportWidth - (imageWidth * oldZoom)) / 2.0);
        var oldPaddingY = Math.Max(0, (viewportHeight - (imageHeight * oldZoom)) / 2.0);
        var imageX = Math.Clamp(
            (Math.Max(0, oldHorizontalOffset) + anchorX - oldPaddingX) / oldZoom,
            0,
            imageWidth);
        var imageY = Math.Clamp(
            (Math.Max(0, oldVerticalOffset) + anchorY - oldPaddingY) / oldZoom,
            0,
            imageHeight);
        var newPaddingX = Math.Max(0, (viewportWidth - (imageWidth * newZoom)) / 2.0);
        var newPaddingY = Math.Max(0, (viewportHeight - (imageHeight * newZoom)) / 2.0);

        return new ViewportOffsets(
            Math.Max(0, (imageX * newZoom) + newPaddingX - anchorX),
            Math.Max(0, (imageY * newZoom) + newPaddingY - anchorY));
    }

    /// <summary>
    /// An image viewer consumes the wheel whenever it has an image, including at
    /// the zoom limits, so an outer ScrollViewer cannot unexpectedly move the whole
    /// operator panel. With no image, the wheel bubbles to the outer panel normally.
    /// </summary>
    internal static bool ShouldHandleMouseWheel(bool hasImage) => hasImage;

    private static bool IsPositiveFinite(double value) =>
        double.IsFinite(value) && value > 0;
}
