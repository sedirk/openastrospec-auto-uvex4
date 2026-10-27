using System.Security.Cryptography;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;
using Xunit;
using Xunit.Abstractions;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Later0BrightBackgroundRegressionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("UVEX-20261025T180350Z-4dcf62581f764dd", "00029-")]
    [InlineData("UVEX-20261025T180711Z-f103f559def94e6", "00035-")]
    [InlineData("UVEX-20261025T182100Z-10109c111b85442", "00036-")]
    public void MoonlitSparseRawFramesContainLocallyCoherentSources(string run, string prefix)
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var path = Assert.Single(Directory.GetFiles(Path.Combine(root, run, "evidence"), prefix + "*.fit"));
        var hash = Hash(path);
        var raw = QhyFitsCodec.Read(path);
        var content = G3SolveProbeContentAnalyzer.Analyze(new MonochromeFrame(raw.Width, raw.Height, raw.Pixels, 65520));
        Assert.Equal(0, content.StellarMeasurement.DetectedStarCount);
        Assert.True(content.LocalBackgroundSourceCount > 0);
        Assert.True(content.HasCoherentSource);
        Assert.Equal("G3_SOLVE_PROBE_STRUCTURED_FIELD", content.Gate.Code);
        Assert.Equal(hash, Hash(path));
        output.WriteLine($"{run}: global={content.StellarMeasurement.DetectedStarCount}, local={content.LocalBackgroundSourceCount}, unchanged={hash}; no WCS, target identity or sky success inferred.");
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("cleanup-failure")]
    [InlineData("late-unsafe")]
    [InlineData("late-cancel")]
    [InlineData("late-deadline")]
    [InlineData("late-dawn")]
    public async Task NativeNextTargetReallyExecutesOnlyAfterTheExistingStopBoundary(string boundary)
    {
        // These are the actual terminal codes of the first four targets. A
        // failure remains a failed target, never fabricated science success.
        foreach (var code in new[] { "G3_SOLVE_PROBE_OVEREXPOSED", "G3_CLOUD_OR_TRANSPARENCY_INVALID", "G3_BOUNDED_SEARCH_EXHAUSTED_RETURNED" })
            await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(boundary, code,
                GateResult.TargetQuality(code, "Recorded October 26 optical failure with the production TargetQuality scope"), ObservationStage.AcquireG3SlitField);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
