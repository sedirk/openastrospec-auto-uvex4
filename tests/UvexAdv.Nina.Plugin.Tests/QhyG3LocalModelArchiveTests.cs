using System.Security.Cryptography;
using System.Text.Json;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class QhyG3LocalModelArchiveTests
{
    [Fact]
    public async Task Schema2ReloadRehashesOriginalSourcesAndIndexedFocusEvidenceWithoutWritingThem()
    {
        using var fixture = new Fixture();
        var path = await fixture.SaveAsync();
        var before = fixture.SourcePaths.Select(Digest).ToArray();
        var loaded = await fixture.LoadAsync();
        Assert.False(loaded.IntegrityFailure);
        var sample = Assert.Single(loaded.Samples);
        Assert.Equal(fixture.Candidate.CandidateSha256, sample.Candidate.CandidateSha256);
        Assert.Empty(loaded.RejectedRecords);
        Assert.Equal(before, fixture.SourcePaths.Select(Digest));
        Assert.Equal(path, await fixture.SaveAsync());
    }

    [Theory]
    [InlineData("frame")] [InlineData("solve")] [InlineData("focus")]
    [InlineData("manifest-reference")] [InlineData("missing-json-field")] [InlineData("wrong-json-type")]
    public async Task CorruptedApplicableSourceInvalidatesWholeModelWithoutUnknownException(string fault)
    {
        using var f = new Fixture();
        await f.SaveAsync();
        switch (fault)
        {
            case "frame": await File.AppendAllTextAsync(f.Candidate.G3.FramePath, "changed"); break;
            case "solve": await File.AppendAllTextAsync(f.Candidate.G3.SolveEvidencePath, "changed"); break;
            case "focus": await File.AppendAllTextAsync(f.FocusPath, "changed"); break;
            case "manifest-reference": await File.WriteAllTextAsync(f.ManifestPath, "{}"); break;
            case "missing-json-field": await f.ChangeFocusAsync(new { payload = new { } }); break;
            case "wrong-json-type": await f.ChangeFocusAsync(new { observationRunId = 3 }); break;
        }
        var loaded = await f.LoadAsync();
        Assert.True(loaded.IntegrityFailure);
        Assert.Empty(loaded.Samples);
        Assert.Contains(loaded.RejectedRecords, r => r.Contains("INTEGRITY_INVALID", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OldBucketsMayAccumulateBeyondRecordLimitWithoutPermanentlyDisablingCurrentModel()
    {
        using var f = new Fixture();
        var path = await f.SaveAsync();
        var directory = Path.GetDirectoryName(path)!;
        for (var i = 0; i < 270; i++)
            await File.WriteAllTextAsync(Path.Combine(directory, $"local-candidate-20261020T010000000Z-{i:D4}.json"), "{}");
        var loaded = await f.LoadAsync();
        Assert.False(loaded.IntegrityFailure);
        Assert.Single(loaded.Samples);
    }

    [Fact]
    public async Task LegacySchema1IsNeverPromotedAndDifferentOpticalFocusIsNotSilentlyReused()
    {
        using var f = new Fixture();
        var path = await f.SaveAsync();
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(path)!, "candidate-legacy.json"), "{}");
        var loaded = await QhyG3LocalModelArchive.LoadAsync(f.Root, f.Binding with {
            OpticalState = f.Binding.OpticalState with { C11PositionSteps = 5001 } }, f.Now, TimeSpan.FromMinutes(30), default);
        Assert.False(loaded.IntegrityFailure);
        Assert.Empty(loaded.Samples);
        Assert.Contains(loaded.RejectedRecords, r => r.Contains("FOCUS", StringComparison.Ordinal));
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private readonly string directory = Path.Combine(Path.GetTempPath(), "uvex-local-model-" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(directory, "calibration");
        public string FocusPath { get; }
        public string ManifestPath { get; }
        public QhyToG3TransferCandidate Candidate { get; }
        public QhyG3LocalModelBinding Binding { get; }
        public DateTimeOffset Now { get; } = new(2026, 10, 24, 0, 0, 12, TimeSpan.Zero);
        private string? archivePath;
        public IEnumerable<string> SourcePaths => new[] { FocusPath, ManifestPath, Candidate.Qhy.FramePath,
            Candidate.Qhy.SolveEvidencePath, Candidate.G3.FramePath, Candidate.G3.SolveEvidencePath };
        public Fixture()
        {
            var t = Now.AddSeconds(-12);
            const string run = "run-test";
            var evidence = Path.Combine(directory, run, "evidence");
            Directory.CreateDirectory(evidence);
            var action = Hash("action");
            QhyG3SolvedFrame Frame(string name, double ra, double dec, double scale, double angle,
                int width, int height, int start)
            {
                var path = Path.Combine(evidence, name + ".fit");
                File.WriteAllText(path, "immutable raw " + name);
                var solvePath = Path.Combine(evidence, name + "-solve.json");
                File.WriteAllText(solvePath, JsonSerializer.Serialize(new { kind = "plate-solve-evidence", observationRunId = run,
                    actionConfigurationSha256 = action, source = new { absolutePath = path, sha256 = Digest(path) },
                    payload = new { success = true, sourceUnchanged = true,
                        solved = new { raDegrees = ra, decDegrees = dec, pixelScaleArcseconds = scale, positionAngleDegrees = angle, flipped = false } } }, Options));
                return new(name, name, path, Digest(path), solvePath, Digest(solvePath), t.AddSeconds(start), t.AddSeconds(start + 1),
                    t.AddSeconds(start + 2), t.AddSeconds(start + 3), "native", width, height, 1, 1, 0, 0, width, height,
                    ra, dec, scale, angle, false, Hash(name + "binding"));
            }
            var qhy = Frame("qhy", 350, 30, 2, 270, 3856, 2180, 3);
            var g3 = Frame("g3", 350.03, 29.98, .4, 255, 1920, 1080, 0);
            var readbacks = new[] {
                new QhyG3PairMountReadback("g3-before-exposure", 350, 30, "JNOW", "pierWest", t),
                new QhyG3PairMountReadback("g3-after-exposure", 350, 30, "JNOW", "pierWest", t.AddSeconds(2)),
                new QhyG3PairMountReadback("qhy-before-job", 350, 30, "JNOW", "pierWest", t.AddSeconds(3)),
                new QhyG3PairMountReadback("qhy-after-accepted-frame", 350, 30, "JNOW", "pierWest", t.AddSeconds(5)),
                new QhyG3PairMountReadback("pair-final-readback", 350, 30, "JNOW", "pierWest", t.AddSeconds(7)) };
            Candidate = Assert.IsType<QhyToG3TransferCandidate>(QhyG3SolvePairBuilder.Build(new(new(1, "fast", true, 2,
                TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30), 2, TimeSpan.FromHours(1), 20),
                run, action, Hash("preset"), "night", Hash("night"), "installation", "mount", "GS350/qhy", "C11/g3",
                QhyG3SolvePairSource.ImmediateSingleQhyExposure, qhy, g3, readbacks, t.AddSeconds(7))).Candidate);
            var optical = new QhyG3LocalOpticalState("C11-focus", 5000, Hash("gs-domain"), null, "ExplicitManualLock", 100);
            Binding = QhyG3LocalModelArchive.BindingFor(Candidate, optical);
            FocusPath = Path.Combine(evidence, "focus.json");
            File.WriteAllText(FocusPath, JsonSerializer.Serialize(new { observationRunId = run, actionConfigurationSha256 = action,
                payload = new { opticalState = optical, candidateSha256 = Candidate.CandidateSha256 } }, Options));
            ManifestPath = Path.Combine(directory, run, "manifest.json");
            WriteManifest();
        }
        private void WriteManifest() => File.WriteAllText(ManifestPath, JsonSerializer.Serialize(new {
            observationRunId = Candidate.ObservationRunId, lockedMetadata = new {
                commissioningPresetSha256 = Candidate.CommissioningPresetSha256, nightSetupSha256 = Candidate.NightSetupSha256 },
            evidence = new[] { FocusPath, Candidate.Qhy.SolveEvidencePath, Candidate.G3.SolveEvidencePath }
                .Select(p => new { absolutePath = p, sha256 = Digest(p) }) }, Options));
        public async Task<string> SaveAsync() => archivePath = await QhyG3LocalModelArchive.SaveAsync(Root, Candidate,
            Binding.OpticalState, FocusPath, Digest(FocusPath), default);
        public Task<QhyG3LocalArchiveLoadResult> LoadAsync() => QhyG3LocalModelArchive.LoadAsync(Root, Binding, Now,
            TimeSpan.FromMinutes(30), default);
        public async Task ChangeFocusAsync(object payload)
        {
            await File.WriteAllTextAsync(FocusPath, JsonSerializer.Serialize(payload, Options));
            var e = JsonSerializer.Deserialize<QhyG3LocalArchiveEnvelope>(await File.ReadAllTextAsync(archivePath!),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            await File.WriteAllTextAsync(archivePath!, JsonSerializer.Serialize(e with { FocusEvidenceSha256 = Digest(FocusPath) }, Options));
            WriteManifest();
        }
        public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private static string Digest(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)); }
    private static string Hash(string value) => QhyG3LocalModelPolicy.HashFields(value);
}
