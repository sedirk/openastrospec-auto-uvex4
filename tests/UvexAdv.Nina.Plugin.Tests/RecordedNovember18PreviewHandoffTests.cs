using System.Security.Cryptography;
using System.Text.Json;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;
using Xunit;
using Xunit.Abstractions;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class RecordedNovember18PreviewHandoffTests(ITestOutputHelper output)
{
    // Opt-in raw replay; do not redistribute science FITS. The source hash
    // is pinned here because the premature exception preceded its journal entry.
    [Theory]
    [InlineData("UVEX-20261118T111033Z-584bb1594eb0459", "6D7344CD1F16E398A8A12048975660428728B3040DEAD48CCA4F36C24AB33F84", 55.1, true)]
    [InlineData("UVEX-20261118T111632Z-4774c40977c2426", "5CF8CB3D68D807A4E771CBD7516CFB6D0F15FF3E517112EB4B43432E5016A8F4", 33.5, false)]
    [InlineData("UVEX-20261118T111943Z-0c99ac57184f4dd", "FA3D7B9603E228D6ABF4F60DB660385CE0669ADF6472ADA74E12495BDA5D4D72", 45.0, true)]
    [InlineData("UVEX-20261118T112347Z-25b38002dd394c1", "2EDF0776A24E4B8E82B6E6C89406C1F6371127A4E0B0A6E4E2FEFB5A09393B78", 30.0, false)]
    [InlineData("UVEX-20261118T112753Z-4d41525b44ba461", "98AA7B6FAF875254B5C66416CBE629DD6F633EDAE34C2B0662E76BB311EE116F", 18.5, false)]
    public void RealRefreshPixelsDetermineWhetherFineAndReturnStillFit(string run, string sha,
        double expectedResidual, bool requiresCoarse)
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var evidence = Path.Combine(root, run, "evidence");
        using var handoff = JsonDocument.Parse(File.ReadAllBytes(Assert.Single(
            Directory.GetFiles(evidence, "*-g3-post-wcs-motion-prediction-cached-slit.json"))));
        var payload = handoff.RootElement.GetProperty("payload");
        var center = Point(payload.GetProperty("cachedRuntimeSlit"));
        var limit = payload.GetProperty("remainingMeasuredHandoffRadiusPixels").GetDouble();
        using var selection = JsonDocument.Parse(File.ReadAllBytes(
            Directory.GetFiles(evidence, "*-phd2-native-guide-candidate.json").Order().Last()));
        var selected = selection.RootElement.GetProperty("payload");
        Assert.True(selected.GetProperty("geometryAccepted").GetBoolean());
        var translation = Point(selected.GetProperty("plannedGuideTranslation"));
        var previous = new PixelPoint(center.X - translation.X, center.Y - translation.Y);
        var path = Assert.Single(Directory.GetFiles(evidence, "*-g3-phd2-pre-guide-target-refresh.fit"));
        Assert.Equal(sha, Hash(path));
        var fits = QhyFitsCodec.Read(path);
        var frame = new MonochromeFrame(fits.Width, fits.Height, fits.Pixels,
            G3FrameInputPolicy.ResolveEffectiveSaturationAdu(fits.Pixels, 4095));
        var measured = SlitTargetIdentifier.Identify(frame, G3StellarFocusAnalyzer.Analyze(frame).Stars,
            previous, 100, 8, 1.5);
        Assert.True(Phd2FreshTargetFluxPolicy.HasMeasuredHandoffPosition(measured), measured.Gate.Code);
        var point = measured.Target!.Centroid;
        var residual = Math.Sqrt(Math.Pow(point.X - center.X, 2) + Math.Pow(point.Y - center.Y, 2));
        output.WriteLine($"{run}: {measured.Gate.Code}, fresh ({point.X:F2}, {point.Y:F2}), residual {residual:F2}px, original radius {limit:F2}px.");
        Assert.InRange(residual, expectedResidual - 1, expectedResidual + 1);
        Assert.InRange(limit, 43.99, 44.01);
        Assert.Equal(requiresCoarse, Phd2HandoffRecoveryPolicy.FreshTargetExceedsHandoff(measured,
            new("run-led", center, 0, 410, 3.25, .5, "G3", 1, 1), limit));
        Assert.Equal(sha, Hash(path));
    }

    private static PixelPoint Point(JsonElement p) => new(p.GetProperty("x").GetDouble(), p.GetProperty("y").GetDouble());
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
