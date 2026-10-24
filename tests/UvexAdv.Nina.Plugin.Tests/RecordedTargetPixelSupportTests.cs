using System.Security.Cryptography;
using System.Text.Json;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class RecordedTargetPixelSupportTests
{
    [Fact]
    public void RecordedHd219937NoiseCannotAnchorTheTargetWhenTheBrightCoreIsOutsideTheOriginalWindow()
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var path = Assert.Single(Directory.GetFiles(Path.Combine(root,
            "UVEX-20261023T123550Z-ed9c198f39b64e7", "evidence"), "00082-*.fit"));
        const string hash = "DC1B798D0084B89F8894E0402F88F84499B028C2F9C6DACBCAA235E5F82FE8CA";
        Assert.Equal(hash, Digest(path));
        var frame = ReadFrame(path);
        var candidates = StarFieldDetector.Detect(frame);
        var prediction = new PixelPoint(824.2511739828378, 437.1614114650993);
        // Reproduce the locked historical preset, not today's/default SNR=8.
        // Its SHA-256 was verified against the retained commissioning preset:
        // night-focus-recovery-20261021T145909Z-d0dd7d8d/commissioning-preset.json.
        const string presetSha256 = "EA0A4D33580C2BCCA88B89A9320898E1904F353B48CB6290A8207398803F271E";
        const double historicalMinimumSnr = 5;
        const double historicalSearchRadius = 100;
        const double historicalUniquenessRatio = 1.5;
        var residualPath = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "00083-*-phd2-lock-shift-fresh-residual.json"));
        using var recordedResidual = JsonDocument.Parse(File.ReadAllText(residualPath));
        var payload = recordedResidual.RootElement.GetProperty("payload");
        Assert.Equal(presetSha256, payload.GetProperty("slitReferenceContext").GetProperty("commissioningPresetSha256").GetString());
        var recordedTarget = payload.GetProperty("measurement").GetProperty("targetCentroid");
        // The old unbound-to-pixels distance/SNR choice exactly reproduces the
        // recorded false centroid. It is not a genuine companion or arrival.
        var old = SlitTargetIdentifier.Identify(candidates, prediction,
            historicalSearchRadius, historicalMinimumSnr, historicalUniquenessRatio);
        Assert.Equal(GateDisposition.Passed, old.Gate.Disposition);
        Assert.InRange(old.Target!.Centroid.X, 922.45, 922.47);
        Assert.InRange(old.Target.Centroid.Y, 446.86, 446.88);
        Assert.Equal(recordedTarget.GetProperty("x").GetDouble(), old.Target.Centroid.X, 9);
        Assert.Equal(recordedTarget.GetProperty("y").GetDouble(), old.Target.Centroid.Y, 9);
        Assert.InRange(old.Target.SignalToNoise, historicalMinimumSnr, 8);
        var result = SlitTargetIdentifier.Identify(frame, candidates, prediction,
            historicalSearchRadius, historicalMinimumSnr, historicalUniquenessRatio);
        Assert.Equal("TARGET_NOT_FOUND", result.Gate.Code);
        Assert.Null(result.Target);
        Assert.True(result.Gate.Metrics!["unsupportedOrdinaryCandidateCount"] >= 1);
        Assert.Equal(6416, result.Gate.Metrics["unsupportedCoreMedianAdu"]);
        Assert.True(result.Gate.Metrics["unsupportedCoreMedianAdu"] <= result.Gate.Metrics["unsupportedBackgroundAdu"]);
        Assert.True(result.Gate.Metrics["unsupportedCoreMedianAdu"] <= result.Gate.Metrics["unsupportedBoundaryUpperQuartileAdu"]);
        // The real bright structure around x=977 lies outside this 100px
        // recognition window. Reject noise, do not invent its catalogue ID.
        Assert.Equal(prediction, result.PredictedPoint);
        Assert.Equal(hash, Digest(path));
    }

    [Theory]
    [InlineData("UVEX-20261023T125614Z-cdae286312eb469", "00044-",
        "BDA7D34BF21E03665F3C05A97960F18139485DCE7026FAA7B85133D8035E5F74",
        853.8051130937795, 422.91968736472177, 860, 862, 435, 437)]
    [InlineData("UVEX-20261021T103936Z-4b73b3e23af4412", "00070-",
        "F10B9CEBAABE17539148472A5043294AA77543F37F3ABD671961064D393FAC14",
        828.3383133934492, 440.91244993792105, 831, 834, 457, 460)]
    public void RecordedLegitimateShortToGuideCoreIsNotLostByTheNarrowUnsupportedPixelFilter(
        string run, string prefix, string hash, double predictedX, double predictedY,
        double minimumX, double maximumX, double minimumY, double maximumY)
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var path = Assert.Single(Directory.GetFiles(Path.Combine(root, run, "evidence"), prefix + "*.fit"));
        Assert.Equal(hash, Digest(path));
        var frame = ReadFrame(path);
        var result = SlitTargetIdentifier.Identify(frame, StarFieldDetector.Detect(frame),
            new(predictedX, predictedY), 100, 8, 1.5);
        Assert.Equal("TARGET_IDENTIFIED_SATURATED_TOPOLOGY", result.Gate.Code);
        Assert.Equal(TargetIdentificationAuthority.BrightWingCentroid, result.Authority);
        Assert.InRange(result.Target!.Centroid.X, minimumX, maximumX);
        Assert.InRange(result.Target.Centroid.Y, minimumY, maximumY);
        Assert.Equal(hash, Digest(path));
    }

    private static string Digest(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static MonochromeFrame ReadFrame(string path)
    {
        var raw = QhyFitsCodec.Read(path);
        return new(raw.Width, raw.Height, raw.Pixels,
            G3FrameInputPolicy.ResolveEffectiveSaturationAdu(raw.Pixels, 4095));
    }
}
