using System.Security.Cryptography;
using System.Text.Json;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;
using Xunit;
using Xunit.Abstractions;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Later0October26RegressionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("UVEX-20261021T182813Z-73b715bb6c9343b", "00042-", "00044-", "00045-", 817.473, 426.867, 1.2152679600138834)]
    [InlineData("UVEX-20261021T185545Z-8111b0fe665b4a9", "00038-", "00040-", "00041-", 817.473, 426.867, 3.7075337557772956)]
    [InlineData("UVEX-20261025T171824Z-2d23e25e9e414a2", "00074-", "00077-", "00078-", 817.6038918066614, 430.61525092821745, .8780329667164096)]
    public async Task SuccessfulHistoricalAndCurrentShortPairsStillPassActualSepAndFineHandoff(
        string run, string firstPrefix, string secondPrefix, string receiptPrefix, double sx, double sy, double expectedSpread)
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var folder = Path.Combine(root, run, "evidence");
        using var doc = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(folder, receiptPrefix + "*.json"))));
        var p = doc.RootElement.GetProperty("payload");
        var prediction = p.GetProperty("longTarget").GetProperty("predictedPoint")
            .Deserialize<PixelPoint>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var longPath = p.GetProperty("longFramePath").GetString()!;
        var longHash = Hash(longPath);
        var longRaw = QhyFitsCodec.Read(longPath);
        var longFrame = new MonochromeFrame(longRaw.Width, longRaw.Height, longRaw.Pixels, 65520);
        var longTarget = SlitTargetIdentifier.Identify(longFrame, StarFieldDetector.Detect(longFrame), prediction, 100, 8, 1.5);
        Assert.Equal("TARGET_IDENTIFIED_SATURATED_TOPOLOGY", longTarget.Gate.Code);
        var paths = new[] { firstPrefix, secondPrefix }.Select(prefix => Assert.Single(Directory.GetFiles(folder, prefix + "*.fit"))).ToArray();
        var hashes = paths.Select(Hash).ToArray();
        Assert.Equal(p.GetProperty("previousHash").GetString(), hashes[0], ignoreCase: true);
        Assert.Equal(p.GetProperty("shortFrameSha256").GetString(), hashes[1], ignoreCase: true);
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "AGENTS.md"))) repo = repo.Parent;
        Assert.NotNull(repo);
        var runtime = new SepDetectionRuntime(Path.Combine(repo.FullName, "output", "star-detection-venv", "Scripts", "python.exe"),
            Path.Combine(repo.FullName, "scripts", "star-detection", "worker.py"));
        G3ShortPositionMeasurement? previous = null;
        for (var i = 0; i < 2; i++)
        {
            var raw = QhyFitsCodec.Read(paths[i]);
            var sep = await new SepStarDetectionClient().DetectAsync(runtime, raw.Width, raw.Height, raw.Pixels, 65520, default);
            var measured = G3SepShortPositionPolicy.Measure(sep, longTarget.PredictedPoint, 100, 8, 1.5, previousSameSetting: previous);
            Assert.Equal("G3_SEP_POSITION_MEASURED", measured.Gate.Code);
            var confirmation = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(previous, measured,
                i == 0 ? null : hashes[0], hashes[i], i + 1, 100, 6);
            Assert.Equal(i == 1, confirmation.Accepted);
            if (i == 1)
            {
                Assert.InRange(Math.Abs(confirmation.PositionSpreadPixels - expectedSpread), 0, .001);
                Assert.True(G3PostWcsShortHandoffPolicy.Evaluate(longTarget, longTarget.PredictedPoint, 6,
                    new(sx, sy), raw.Width, raw.Height, 20, previous, measured, confirmation, 2, false, 44, 100).Accepted);
            }
            previous = measured;
        }
        Assert.Equal(hashes, paths.Select(Hash).ToArray());
        Assert.Equal(longHash, Hash(longPath));
        output.WriteLine($"{run}: actual immutable pair, SEP spread={expectedSpread:F4}px; original fine envelope retained.");
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("cleanup-failure")]
    [InlineData("late-unsafe")]
    [InlineData("late-cancel")]
    [InlineData("late-deadline")]
    [InlineData("late-dawn")]
    public async Task ActualHd18552PixelsRetainRecoveryAndNextNativeTargetRequiresCheckedStop(string boundary)
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var path = Assert.Single(Directory.GetFiles(Path.Combine(root,
            "UVEX-20261025T171448Z-7166783820b44fe", "evidence"), "00046-*.fit"));
        const string sha = "DF87BDF2BCBC61BF92F30A14A0ACA0E4233FBCE59E36F52C6A2743EA313670F3";
        Assert.Equal(sha, Hash(path));
        var raw = QhyFitsCodec.Read(path);
        var frame = new MonochromeFrame(raw.Width, raw.Height, raw.Pixels,
            G3FrameInputPolicy.ResolveEffectiveSaturationAdu(raw.Pixels, 4095));
        var prediction = new PixelPoint(818.2894964665661, 430.0252725262022);
        var slit = new PixelPoint(817.6038918066614, 430.61525092821745);
        var measured = SlitTargetIdentifier.Identify(frame, StarFieldDetector.Detect(frame), prediction, 100, 8, 1.5);
        Assert.Equal("TARGET_IDENTIFIED_SATURATED_TOPOLOGY", measured.Gate.Code);
        Assert.InRange(measured.Target!.Centroid.X, 842, 845);
        Assert.InRange(measured.Target.Centroid.Y, 343, 347);
        Assert.True(G3PostWcsMeasuredHandoffPolicy.RequiresCoarseSolve(measured, prediction, 6, slit, 1920, 1080, 20, 100, 44));
        Assert.False(G3PostWcsMeasuredHandoffPolicy.FitsRemainingHandoff(measured, slit, 44));
        var content = G3SolveProbeContentAnalyzer.Analyze(frame);
        Assert.InRange(content.BackgroundMedianAdu, 24000, 26000);
        Assert.Equal(5000, G3SolveExposurePolicy.ShortRecheckMilliseconds(5000, 10000, content, 65520, 5000, content));
        var gate = G3PostWcsMeasuredHandoffPolicy.OutsideRemainingGate(measured, slit, 44);
        Assert.InRange(gate.Metrics!["measuredTargetResidualPixels"], 88, 91);
        Assert.True(TargetAcquisitionStrategyPolicy.MayTryBoundedNeighbor(gate));

        // Feed the actual produced optical gate into the acquisition dispatcher.
        // Strict ports allow only its existing bounded recovery; they cannot
        // silently accept a candidate, replenish a ledger or invoke PHD2.
        var port = StrictWorkflowPort.Create<IG3AcquisitionPort>(out var trace);
        var pass = GateResult.Pass("checked", "Finite fixture, not hardware attestation.");
        var field = G3FieldState.Failed(gate, path);
        trace.Handlers["EvaluateInterlocksAsync"] = _ => Task.FromResult(pass);
        trace.Handlers["EnsureFineAcquisitionClockStarted"] = _ => null;
        trace.Handlers["CompletePendingG3SearchReturnAsync"] = _ => Task.FromResult<StageResult?>(null);
        trace.Handlers["ReturnAfterOwnedGuidingBeforeReacquisitionAsync"] = _ => Task.FromResult(pass);
        trace.Handlers["EnsureWideToSlitTransferSkippedEvidenceAsync"] = _ => Task.FromResult("original-transfer.json");
        trace.Handlers["UsesCatalogPositionOnly"] = _ => false;
        trace.Handlers["PublishTargetBranchAsync"] = _ => Task.CompletedTask;
        trace.Handlers["CaptureAndAnalyzeG3WithSolveLadderAsync"] = _ => Task.FromResult(field);
        trace.Handlers["RememberField"] = _ => null;
        trace.Handlers["RunBoundedG3LocalSearchAsync"] = a =>
        {
            Assert.Same(field, a[1]);
            Assert.True((bool)a[4]!);
            return Task.FromResult(new StageResult(gate, path)); // Finite exhausted alternative, not fake new pixels.
        };
        var config = new G3RunConfiguration(1000, 50, 1, 65535, 1000, 2.4, false, 5,
            null!, null!, 60, 120, 3, WideToSlitTransferMode.Skip, null!);
        var result = await new G3AcquisitionWorkflow(config, port).AcquireAsync(AtrWorkflowFixture.CreateContext(), default, true);
        Assert.Single(trace.Calls, c => c == "RunBoundedG3LocalSearchAsync");
        Assert.DoesNotContain("AcceptField", trace.Calls);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(boundary,
            result.Gate.Code, result.Gate, ObservationStage.AcquireG3SlitField);
        Assert.Equal(sha, Hash(path));
        output.WriteLine($"HD 18552 actual 89px candidate retained; original optical fallback dispatched, boundary={boundary}. No new sky success claimed.");
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
