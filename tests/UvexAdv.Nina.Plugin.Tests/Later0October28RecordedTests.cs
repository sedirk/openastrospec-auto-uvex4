using System.Security.Cryptography;
using System.Text.Json;
using NINA.Astrometry;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;
using Xunit;
using Xunit.Abstractions;

namespace UvexAdv.Nina.Plugin.Tests;

[Collection("Read-only NINA ephemeris boundary")]
public sealed class Later0October28RecordedTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("skip")]
    [InlineData("cleanup-failure")]
    public async Task Hd18552StableRimCandidatesDoNotBecomeTargetIdentityAndNextTargetRequiresStop(string boundary)
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var dir = Path.Combine(root, "UVEX-20261027T192855Z-d9b4d7b5f9744ff", "evidence");
        foreach (var prefix in new[] { "00218-", "00224-", "00240-" })
        {
            using var record = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(dir, prefix + "*.json"))));
            var path = record.RootElement.GetProperty("source").GetProperty("absolutePath").GetString()!;
            var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            Assert.Equal(record.RootElement.GetProperty("source").GetProperty("sha256").GetString()!.ToUpperInvariant(), sha);
            var raw = QhyFitsCodec.Read(path);
            var frame = new MonochromeFrame(raw.Width, raw.Height, raw.Pixels,
                G3FrameInputPolicy.ResolveEffectiveSaturationAdu(raw.Pixels, 4095));
            var p = record.RootElement.GetProperty("payload").GetProperty("expectedTargetLocal");
            var predicted = new PixelPoint(p.GetProperty("x").GetDouble(), p.GetProperty("y").GetDouble());
            var stars = StarFieldDetector.Detect(frame);
            var id = SlitTargetIdentifier.Identify(frame, stars, predicted, 100, 8, 1.5);
            Assert.Equal("TARGET_AMBIGUOUS", id.Gate.Code);
            Assert.InRange(id.PredictionResidualPixels, 0.8, 1.8);
            Assert.InRange(id.UniquenessRatio, 1.2, 1.5);
            Assert.NotNull(id.Target);
            Assert.True(id.Target.SignalToNoise > 50);
            var window = new Phd2FreshTargetWindow(3);
            window.Observe(false); window.Observe(false); window.Observe(false);
            Assert.True(window.Exhausted);
            Assert.False(window.Complete);
            output.WriteLine($"{prefix}: high SNR={id.Target.SignalToNoise:F1}, small residual={id.PredictionResidualPixels:F2}px still does not prove a unique core.");
            Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }
        var failure = new Phd2FreshTargetNotMeasuredException(GateResult.TargetQuality(
            Phd2FreshTargetWindow.FailureCode, "Actual recorded ambiguous rim, no centroid promotion"), "recorded.json");
        var gate = failure.AfterStop(true).Gate;
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(boundary, gate.Code, gate, ObservationStage.PlaceTargetOnSlit);
    }

    [Fact]
    public async Task Hd21455FailedOptionalShortFramesRetainExistingFrameSolveWithoutFakeHandoff()
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var dir = Path.Combine(root, "UVEX-20261027T193755Z-228acd7fb8144ab", "evidence");
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "AGENTS.md"))) repo = repo.Parent;
        Assert.NotNull(repo);
        var runtime = new SepDetectionRuntime(Path.Combine(repo.FullName, "output", "star-detection-venv", "Scripts", "python.exe"),
            Path.Combine(repo.FullName, "scripts", "star-detection", "worker.py"));
        foreach (var prefix in new[] { "00052-", "00054-", "00057-", "00059-", "00062-", "00064-" })
        {
            using var record = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(dir, prefix + "*.json"))));
            var p = record.RootElement.GetProperty("payload");
            var path = p.GetProperty("shortFramePath").GetString()!;
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            Assert.Equal(p.GetProperty("shortFrameSha256").GetString(), hash, ignoreCase: true);
            var raw = QhyFitsCodec.Read(path);
            var sep = await new SepStarDetectionClient().DetectAsync(runtime, raw.Width, raw.Height, raw.Pixels, 65520, default);
            var predicted = new PixelPoint(815.2206996772265, 423.2715531303814);
            var measured = G3SepShortPositionPolicy.Measure(sep, predicted, 100, 8, 1.5);
            Assert.Equal("G3_SEP_UNMEASURED", measured.Gate.Code);
            var confirmation = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(null, measured, null, hash, 6, 100, 6);
            var longTarget = new TargetIdentification(GateResult.Pass("long-candidate", "not confirmed"), null, predicted, 0, 2);
            var decision = G3PostWcsShortHandoffPolicy.Evaluate(longTarget, predicted, 6, new(817,431),
                raw.Width, raw.Height, 20, null, measured, confirmation, 6, false, 44, 100);
            Assert.False(decision.Accepted);
            Assert.True(G3PostWcsShortHandoffPolicy.NeedsSameFrameCoarseSolve(decision.Gate));
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }
        output.WriteLine("All six actual short frames remain unmeasured; only the existing long-frame solver is restored. No new sky success inferred.");
    }

    [Fact]
    public async Task Hd21455ExistingLongFrameReallySolvesAndRestoresBoundedCoarseGeometry()
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        ReadOnlyNinaAstrometry.Initialize();
        var path = Assert.Single(Directory.GetFiles(Path.Combine(root,
            "UVEX-20261027T193755Z-228acd7fb8144ab", "evidence"), "00047-*.fit"));
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "AGENTS.md"))) repo = repo.Parent;
        Assert.NotNull(repo);
        var recovery = new MeasuredAstapRecovery(configuredWorker: Path.Combine(repo.FullName, "scripts", "star-detection", "astap_recovery.py"));
        Assert.True(recovery.Claim("PHD2/G3 existing frame", "Platesolve3Solver", false, hash));
        var target = new Coordinates(52.35952694549, 46.93786616322, Epoch.J2000, Coordinates.RAType.Degrees);
        var attempt = await recovery.SolveAsync(path, hash, 1920, 1080, target, .3815683798332645, 2,
            Path.Combine(repo.FullName, "tmp", "oct28-adapter-replay"), default);
        var solve = Assert.IsType<DetectorWcsResult>(attempt.Result);
        var pixel = solve.Project(target);
        Assert.InRange(pixel.X, 737, 741);
        Assert.InRange(pixel.Y, 338, 342);
        Assert.True(Math.Sqrt(Math.Pow(pixel.X-815.2207,2)+Math.Pow(pixel.Y-423.2716,2)) > 100);
        var slit = new PixelPoint(817.473, 426.867);
        var inverse = G3WcsTargetProjector.SolveCenterForTargetAtPixel(target, solve, 1920, 1080,
            MeasuredAstapRecovery.Identity, slit);
        var correction = ObservationMotionGeometry.AngularSeparationArcseconds(solve.Coordinates, inverse.DesiredG3Center);
        Assert.InRange(correction, 40, 50);
        var result = solve.WithCenter(inverse.DesiredG3Center, 1920, 1080).Project(target);
        Assert.InRange(Math.Abs(result.X-slit.X), 0, .03);
        Assert.InRange(Math.Abs(result.Y-slit.Y), 0, .03);
        using var evidence = JsonDocument.Parse(File.ReadAllText(attempt.EvidencePath!));
        Assert.True(evidence.RootElement.GetProperty("matches").GetArrayLength() >= 5);
        Assert.False(evidence.RootElement.GetProperty("motionAuthorized").GetBoolean());
        Assert.False(recovery.Claim("PHD2/G3 same frame again", "Platesolve3Solver", false, hash));
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        output.WriteLine($"Raw ASTAP target=({pixel.X:F3},{pixel.Y:F3}); inverse correction={correction:F3} arcsec. This verifies the command calculation, not a future exposure or physical arrival.");
    }
}
