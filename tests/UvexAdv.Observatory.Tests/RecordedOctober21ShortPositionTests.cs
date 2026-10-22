using System.Security.Cryptography;
using System.Text.Json;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class RecordedOctober21ShortPositionTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void Hd218393GoodThenTwoMissingTenMsFramesAdmitBoundedReprobeNotFalseConfirmation()
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var evidence = Path.Combine(root, "UVEX-20261021T140225Z-e9a326e30a0c404", "evidence");
        var receipts = Directory.GetFiles(evidence, "*g3-catalog-short-position-confirmation.json").Order().ToArray();
        Assert.Equal(3, receipts.Length);
        G3ShortPositionMeasurement? previous = null;
        string? previousHash = null;
        var misses = 0;
        var policy = new G3ShortExposurePolicy(10);
        for (var i = 0; i < receipts.Length; i++)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(receipts[i]));
            var payload = doc.RootElement.GetProperty("payload");
            var hash = VerifyRaw(doc.RootElement);
            var sep = payload.GetProperty("sepMeasurements").Deserialize<SepImageMeasurements>(Json)!;
            var prediction = payload.GetProperty("originalPlanPrediction").Deserialize<PixelPoint>(Json)!;
            var measurement = G3SepShortPositionPolicy.Measure(sep, prediction, 100, previousSameSetting: previous);
            misses = measurement.Gate.Code == "G3_SEP_UNMEASURED" ? misses + 1 : 0;
            var peak = payload.GetProperty("measurement").GetProperty("gate").GetProperty("metrics")
                .GetProperty("shortRecognitionPeakAdu").GetDouble();
            var next = policy.AfterMeasurement(measurement.Gate.Code, i + 1)
                .AfterUnmeasured(measurement, i + 1, peak, 65520, 5000,
                    previous?.Gate.Disposition == GateDisposition.Passed, misses);
            var confirmation = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(previous, measurement,
                previousHash, hash, i + 1, 100, next.MaximumFrames);
            Assert.False(confirmation.Accepted);
            Assert.True(confirmation.RetryAllowed);
            if (i == 0)
            {
                Assert.Equal("G3_SEP_POSITION_MEASURED", measurement.Gate.Code);
                Assert.InRange(measurement.Identification.Target!.SignalToNoise, 16, 18);
                previous = measurement;
                previousHash = hash;
            }
            else Assert.Equal("G3_SEP_UNMEASURED", measurement.Gate.Code);
            Assert.Equal(i < 2 ? 10 : 100, next.ExposureMilliseconds);
            policy = next;
            VerifyRaw(doc.RootElement);
        }
        Assert.Equal(6, policy.MaximumFrames);
        Assert.Equal(1, policy.SignalExposureIncreases);
        // No 100 ms images were captured in this old run. This test proves the
        // recovery decision, not a fabricated successful measurement/observation.
    }

    [Fact]
    public void Hd224559FinalPairContinuesRealParentInsteadOfNineteenPixelPatchJump()
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var evidence = Path.Combine(root, "UVEX-20261021T140550Z-203e6c44012c406", "evidence");
        using var first = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(evidence,
            "00051-*g3-post-wcs-short-target-confirmation.json"))));
        using var second = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(evidence,
            "00053-*g3-post-wcs-short-target-confirmation.json"))));
        var a = first.RootElement.GetProperty("payload");
        var b = second.RootElement.GetProperty("payload");
        var firstHash = VerifyRaw(first.RootElement);
        var secondHash = VerifyRaw(second.RootElement);
        Assert.Equal(90, a.GetProperty("exposureMilliseconds").GetInt32());
        Assert.Equal(90, b.GetProperty("exposureMilliseconds").GetInt32());
        var prediction = a.GetProperty("measurement").GetProperty("identification")
            .GetProperty("predictedPoint").Deserialize<PixelPoint>(Json)!;
        var initial = G3SepShortPositionPolicy.Measure(a.GetProperty("sep").Deserialize<SepImageMeasurements>(Json)!, prediction, 100);
        var final = G3SepShortPositionPolicy.Measure(b.GetProperty("sep").Deserialize<SepImageMeasurements>(Json)!, prediction, 100,
            previousSameSetting: initial);
        Assert.InRange(final.Identification.Target!.Centroid.X, 827.99, 828.01);
        Assert.InRange(final.Identification.Target.Centroid.Y, 444.42, 444.44);
        Assert.InRange(final.Gate.Metrics!["sepParentContinuityPixels"], .64, .65);
        var confirmation = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(initial, final,
            firstHash, secondHash, 6, 100, 6);
        Assert.Equal("G3_SEP_PARENT_BLEND_UNRESOLVED", confirmation.Gate.Code);
        Assert.False(confirmation.Accepted);
        Assert.False(confirmation.RetryAllowed);
        Assert.InRange(confirmation.PositionSpreadPixels, .64, .65);
        VerifyRaw(first.RootElement);
        VerifyRaw(second.RootElement);
    }

    private static string VerifyRaw(JsonElement receipt)
    {
        var source = receipt.GetProperty("source");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source.GetProperty("absolutePath").GetString()!)));
        Assert.Equal(source.GetProperty("sha256").GetString()!.ToUpperInvariant(), hash);
        return hash;
    }
}
