using System.Security.Cryptography;
using System.Text.Json;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;
using Xunit;
using Xunit.Abstractions;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class RecordedNovember15HandoffTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("00182-")]
    [InlineData("00187-")]
    [InlineData("00192-")]
    public void Hd18552HollowMultilobePixelsAreNotReclassifiedAsAUsableUniqueCore(string prefix)
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var evidence = Path.Combine(root, "UVEX-20261114T172852Z-5eed60506a8942c", "evidence");
        using var doc = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(evidence, prefix + "*.json"))));
        var p = doc.RootElement.GetProperty("payload");
        var (frame, path, before) = ReadOriginal(doc.RootElement.GetProperty("source"));
        var predicted = Point(p.GetProperty("expectedTargetLocal"));
        var result = SlitTargetIdentifier.Identify(frame, StarFieldDetector.Detect(frame), predicted, 100, 8, 1.5);
        Assert.Equal("TARGET_AMBIGUOUS", result.Gate.Code);
        Assert.False(Phd2FreshTargetFluxPolicy.HasMeasuredHandoffPosition(result));
        Assert.Equal(before, Hash(path));
        output.WriteLine($"HD18552 {prefix}: {result.Gate.Code}; unchanged source {before}; not a focus or weather diagnosis.");
    }

    [Fact]
    public void Hd21455AlreadyMovedOutsideFineCapacityDuringNativeSelectionNotOnlyAfterTheLongSettle()
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var evidence = Path.Combine(root, "UVEX-20261114T174031Z-79196419b3084ba", "evidence");
        using var shortDoc = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(evidence, "00061-*.json"))));
        var shortResult = shortDoc.RootElement.GetProperty("payload");
        Assert.True(shortResult.GetProperty("accepted").GetBoolean());
        var previous = Point(shortResult.GetProperty("shortTarget").GetProperty("target").GetProperty("centroid"));
        using var nativeDoc = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(evidence, "00102-*.json"))));
        var (frame, path, before) = ReadOriginal(nativeDoc.RootElement.GetProperty("source"));
        var measured = SlitTargetIdentifier.Identify(frame, StarFieldDetector.Detect(frame), previous, 100, 8, 1.5);
        Assert.Equal("TARGET_IDENTIFIED_SATURATED_TOPOLOGY", measured.Gate.Code);
        Assert.InRange(measured.Target!.Centroid.X, 787.3, 787.6);
        Assert.InRange(measured.Target.Centroid.Y, 393.5, 393.8);
        var slit = new SlitGeometry("run-led", new(817.6892243131091, 433.0588541961166),
            -1, 410, 3.25, .58, "G3", 1, 1);
        Assert.InRange(Distance(previous, slit.AcquisitionPoint), 15, 16);
        Assert.InRange(Distance(measured.Target.Centroid, slit.AcquisitionPoint), 49, 51);
        Assert.True(Phd2HandoffRecoveryPolicy.FreshTargetExceedsHandoff(measured, slit, 44));
        // This replaces neither the raw short centroid nor a later frame. It
        // proves why a NEW measurement is needed before the 180-second settle;
        // it does not authorize a coarse command or claim a successful spectrum.
        Assert.Equal(before, Hash(path));
        output.WriteLine($"HD21455: short {previous}, selection {measured.Target.Centroid}; original 44 px fine/return envelope no longer fits before guiding. Source unchanged: {before}.");
    }

    private static (MonochromeFrame Frame, string Path, string Hash) ReadOriginal(JsonElement source)
    {
        var path = source.GetProperty("absolutePath").GetString()!;
        var before = Hash(path);
        Assert.Equal(source.GetProperty("sha256").GetString()!.ToUpperInvariant(), before);
        var fits = QhyFitsCodec.Read(path);
        return (new(fits.Width, fits.Height, fits.Pixels,
            G3FrameInputPolicy.ResolveEffectiveSaturationAdu(fits.Pixels, 4095)), path, before);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static PixelPoint Point(JsonElement p) => new(p.GetProperty("x").GetDouble(), p.GetProperty("y").GetDouble());
    private static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
