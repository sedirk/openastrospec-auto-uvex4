using System.Text.Json;
using UvexAdv.Spectroscopy;
using Xunit;

namespace UvexAdv.Spectroscopy.Tests;

public sealed class SpectrumQuickLookTests
{
    [Fact]
    public void ApertureIgnoresOffTraceDefectAndSubtractsColumnSky()
    {
        var pixels = new ushort[100 * 160];
        for (var y = 0; y < 160; y++)
        for (var x = 0; x < 100; x++) pixels[y * 100 + x] = (ushort)(100 + x + (Math.Abs(y - 80) <= 5 ? 50 : 0));
        pixels[20 * 100 + 40] = 60000;
        var copy = pixels.ToArray();
        var flux = SpectrumQuickLook.Extract(pixels, 100, 160, 80, 5, 65535);
        Assert.All(flux, v => Assert.Equal(550, v));
        Assert.Equal(copy, pixels);
        pixels[80 * 100 + 30] = 65535;
        Assert.True(double.IsNaN(SpectrumQuickLook.Extract(pixels, 100, 160, 80, 5, 65535)[30]));
    }

    [Fact]
    public void MissingTraceProducesGapsNotFullFrameMean() =>
        Assert.All(SpectrumQuickLook.Extract(new ushort[100 * 100], 100, 100, double.NaN, 5, 65535),
            value => Assert.True(double.IsNaN(value)));

    [Fact]
    public void WorkerContractPreservesNullGapsAndRejectsCalibrationClaims()
    {
        object Payload(bool calibrated, int n = 4) => new { schema = 1, algorithm = "reduction-live-preview-v1",
            previewOnly = true, calibrated, spectralSmoothing = false, backend = "aspired-horne86",
            flux = new double?[] { 1, null, 2, 3 }, rawFlux = new double?[n], uncertainty = new double?[] { 1, null, 1, 1 },
            cosmicPixels = 1, maskedColumns = 1, traceMethod = "test", warnings = Array.Empty<string>() };
        var result = ReductionPreviewClient.ParseAndValidate(JsonSerializer.Serialize(Payload(false)), 4);
        Assert.Null(result.Flux[1]);
        Assert.Throws<InvalidDataException>(() => ReductionPreviewClient.ParseAndValidate(JsonSerializer.Serialize(Payload(true)), 4));
        Assert.Throws<InvalidDataException>(() => ReductionPreviewClient.ParseAndValidate(JsonSerializer.Serialize(Payload(false, 3)), 4));
    }
}
