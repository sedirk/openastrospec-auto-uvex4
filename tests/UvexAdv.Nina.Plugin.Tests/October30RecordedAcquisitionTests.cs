using System.Security.Cryptography;
using System.Text.Json;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;
using Xunit;
using Xunit.Abstractions;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class October30RecordedAcquisitionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("00144-", "00187-")]
    [InlineData("00264-", "00306-")]
    public async Task Hd18552AmbiguousRimAndOutsideCandidateDoNotProveArrivalAndNextTargetActuallyRuns(string prefix, string predictionPrefix)
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var directory = Path.Combine(root, "UVEX-20261029T161634Z-78e6524c4524465", "evidence");
        var path = Assert.Single(Directory.GetFiles(directory, prefix + "*.fit"));
        var hash = SHA256.HashData(File.ReadAllBytes(path));
        using var record = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(directory, predictionPrefix + "*.json"))));
        var payload = record.RootElement.GetProperty("payload");
        PixelPoint Point(string name)
        {
            var value = payload.GetProperty(name);
            return new(value.GetProperty("x").GetDouble(), value.GetProperty("y").GetDouble());
        }
        var predicted = Point("predictedTarget");
        var slit = Point("cachedRuntimeSlit");
        var raw = QhyFitsCodec.Read(path);
        var frame = new MonochromeFrame(raw.Width, raw.Height, raw.Pixels,
            G3FrameInputPolicy.ResolveEffectiveSaturationAdu(raw.Pixels, 4095));
        var content = G3SolveProbeContentAnalyzer.Analyze(frame);
        var identification = SlitTargetIdentifier.Identify(frame, content.StellarMeasurement.Stars, predicted, 100, 8, 1.5);
        var propose = G3PostWcsMeasuredHandoffPolicy.CanProposeBoundedShortConfirmation(identification,
            predicted, payload.GetProperty("maximumUncertaintyPixels").GetDouble(), slit, raw.Width, raw.Height, 20, 100, 44);
        output.WriteLine($"{prefix} predicted={predicted} slit={slit} sources={content.CoherentSourceCount}; {identification.Gate.Code} {identification.Gate.Message}; target={identification.Target}; propose={propose}");
        Assert.Equal(prefix == "00144-" ? "TARGET_AMBIGUOUS" : "TARGET_NOT_FOUND", identification.Gate.Code);
        Assert.False(propose);
        Assert.True(content.HasCoherentSource); // Not a blank/zero-star or autofocus diagnosis.
        if (prefix == "00144-")
        {
            Assert.InRange(identification.PredictionResidualPixels, 91, 93);
            Assert.InRange(identification.UniquenessRatio, 1, 1.1);
        }
        Assert.InRange(Math.Sqrt(Math.Pow(predicted.X - slit.X, 2) + Math.Pow(predicted.Y - slit.Y, 2)), 1, 4);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
        var gate = GateResult.TargetQuality("G3_BOUNDED_SEARCH_EXHAUSTED_RETURNED", "Recorded failed candidate, verified original return");
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("skip", gate.Code, gate, ObservationStage.AcquireG3SlitField);
    }
}
