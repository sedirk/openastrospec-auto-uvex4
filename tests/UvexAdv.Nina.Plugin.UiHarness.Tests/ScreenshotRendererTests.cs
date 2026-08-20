using System.Buffers.Binary;
using System.IO;
using UvexAdv.Nina.Plugin.UiHarness;

namespace UvexAdv.Nina.Plugin.UiHarness.Tests;

public sealed class ScreenshotRendererTests
{
    [Fact]
    public void Renderer_UsesProductionTemplateAndWritesRequestedPngDimensions()
    {
        var output = Path.Combine(Path.GetTempPath(), "uvex-adv-ui-harness-tests", Guid.NewGuid().ToString("N"));
        ScreenshotRenderResult? result = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = ScreenshotRenderer.RenderAll(ScenarioCatalog.Select("idle"), output).Single();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The offline WPF render did not finish within 30 seconds.");

        Assert.Null(failure);
        Assert.NotNull(result);
        Assert.True(File.Exists(result.AbsolutePath));
        Assert.Equal(64, result.Sha256.Length);

        var header = File.ReadAllBytes(result.AbsolutePath).AsSpan(0, 24);
        Assert.True(header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        Assert.Equal(result.Width, BinaryPrimitives.ReadInt32BigEndian(header[16..20]));
        Assert.Equal(result.Height, BinaryPrimitives.ReadInt32BigEndian(header[20..24]));
    }
}
