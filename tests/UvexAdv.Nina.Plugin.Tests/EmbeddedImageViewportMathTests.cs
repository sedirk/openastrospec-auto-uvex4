using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class EmbeddedImageViewportMathTests
{
    [Theory]
    [InlineData(800, 600, 1600, 1200, 0.5)]
    [InlineData(800, 600, 400, 1200, 0.5)]
    [InlineData(800, 600, 1600, 300, 0.5)]
    [InlineData(800, 600, 400, 300, 2.0)]
    public void FitZoomKeepsWholeImageVisible(
        double viewportWidth,
        double viewportHeight,
        double imageWidth,
        double imageHeight,
        double expected)
    {
        var actual = EmbeddedImageViewportMath.CalculateFitZoom(
            viewportWidth,
            viewportHeight,
            imageWidth,
            imageHeight);

        Assert.Equal(expected, actual, precision: 10);
        Assert.True((imageWidth * actual) <= viewportWidth + 0.001);
        Assert.True((imageHeight * actual) <= viewportHeight + 0.001);
    }

    [Theory]
    [InlineData(0, 600, 1600, 1200)]
    [InlineData(800, double.NaN, 1600, 1200)]
    [InlineData(800, 600, 0, 1200)]
    [InlineData(800, 600, 1600, double.PositiveInfinity)]
    public void FitZoomFallsBackSafelyForInvalidGeometry(
        double viewportWidth,
        double viewportHeight,
        double imageWidth,
        double imageHeight)
    {
        Assert.Equal(
            1.0,
            EmbeddedImageViewportMath.CalculateFitZoom(
                viewportWidth,
                viewportHeight,
                imageWidth,
                imageHeight));
    }

    [Fact]
    public void WheelZoomIsBoundedAndReversibleAwayFromLimits()
    {
        var zoomedIn = EmbeddedImageViewportMath.CalculateWheelZoom(1.0, 120);
        var zoomedBack = EmbeddedImageViewportMath.CalculateWheelZoom(zoomedIn, -120);

        Assert.Equal(EmbeddedImageViewportMath.WheelZoomFactor, zoomedIn, precision: 10);
        Assert.Equal(1.0, zoomedBack, precision: 10);
        Assert.Equal(
            EmbeddedImageViewportMath.MaximumZoom,
            EmbeddedImageViewportMath.CalculateWheelZoom(EmbeddedImageViewportMath.MaximumZoom, 120));
        Assert.Equal(
            EmbeddedImageViewportMath.MinimumZoom,
            EmbeddedImageViewportMath.CalculateWheelZoom(EmbeddedImageViewportMath.MinimumZoom, -120));
    }

    [Fact]
    public void AnchoredZoomKeepsImagePointUnderCursor()
    {
        const double oldZoom = 1.0;
        const double newZoom = 2.0;
        const double cursorX = 250;
        const double cursorY = 120;
        const double oldHorizontalOffset = 400;
        const double oldVerticalOffset = 80;

        var imageXBefore = (oldHorizontalOffset + cursorX) / oldZoom;
        var imageYBefore = (oldVerticalOffset + cursorY) / oldZoom;
        var offsets = EmbeddedImageViewportMath.CalculateAnchoredOffsets(
            oldZoom,
            newZoom,
            cursorX,
            cursorY,
            oldHorizontalOffset,
            oldVerticalOffset);
        var imageXAfter = (offsets.Horizontal + cursorX) / newZoom;
        var imageYAfter = (offsets.Vertical + cursorY) / newZoom;

        Assert.Equal(imageXBefore, imageXAfter, precision: 10);
        Assert.Equal(imageYBefore, imageYAfter, precision: 10);
    }

    [Fact]
    public void CenteredAnchorRemainsStableWhenFitImageHasLetterboxing()
    {
        const double imageWidth = 1000;
        const double imageHeight = 500;
        const double viewportWidth = 600;
        const double viewportHeight = 600;
        const double oldZoom = 0.6;
        const double newZoom = 1.2;
        const double cursorX = 300;
        const double cursorY = 300;

        var offsets = EmbeddedImageViewportMath.CalculateCenteredAnchoredOffsets(
            oldZoom,
            newZoom,
            cursorX,
            cursorY,
            oldHorizontalOffset: 0,
            oldVerticalOffset: 0,
            imageWidth,
            imageHeight,
            viewportWidth,
            viewportHeight);

        // At fit, the viewport center is image coordinate (500, 250). After zoom,
        // those same image coordinates remain directly under the cursor.
        var newPaddingY = Math.Max(0, (viewportHeight - (imageHeight * newZoom)) / 2.0);
        var imageXAfter = (offsets.Horizontal + cursorX) / newZoom;
        var imageYAfter = (offsets.Vertical + cursorY - newPaddingY) / newZoom;
        Assert.Equal(500, imageXAfter, precision: 10);
        Assert.Equal(250, imageYAfter, precision: 10);
    }

    [Fact]
    public void WheelIsConsumedOnlyWhenAnImageExists()
    {
        Assert.True(EmbeddedImageViewportMath.ShouldHandleMouseWheel(hasImage: true));
        Assert.False(EmbeddedImageViewportMath.ShouldHandleMouseWheel(hasImage: false));
    }
}
