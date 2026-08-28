using System.Windows.Media;
using System.Windows.Media.Imaging;
using UvexAdv.Nina.Plugin;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class EmbeddedImageDisplayTransformTests
{
    [Fact]
    public void ManualLevelsApplyBlackWhiteAndGammaWithoutChangingSource()
    {
        var sourcePixels = new byte[] { 0, 64, 128, 255 };
        var source = BitmapSource.Create(4, 1, 96, 96, PixelFormats.Gray8, null, sourcePixels, 4);
        source.Freeze();

        var rendered = EmbeddedImageDisplayTransform.Apply(source, false, 64, 192, 1, out var levels);

        Assert.Equal(new EmbeddedImageDisplayLevels(64, 192, 1, false), levels);
        var output = new byte[16];
        rendered.CopyPixels(output, 16, 0);
        Assert.Equal((byte)0, output[0]);
        Assert.Equal((byte)0, output[4]);
        Assert.InRange(output[8], (byte)127, (byte)128);
        Assert.Equal((byte)255, output[12]);
        Assert.Equal(sourcePixels, new byte[] { 0, 64, 128, 255 });
    }

    [Fact]
    public void AutomaticLevelsIgnoreTransparentPixelsAndRemainFinite()
    {
        var pixels = new byte[]
        {
            0, 0, 0, 0,
            20, 20, 20, 255,
            30, 30, 30, 255,
            220, 220, 220, 255,
        };
        var source = BitmapSource.Create(4, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
        source.Freeze();

        _ = EmbeddedImageDisplayTransform.Apply(source, true, 0, 255, 1.25, out var levels);

        Assert.True(levels.Automatic);
        Assert.True(levels.WhitePoint > levels.BlackPoint);
        Assert.Equal(1.25, levels.Gamma, precision: 10);
    }
}
