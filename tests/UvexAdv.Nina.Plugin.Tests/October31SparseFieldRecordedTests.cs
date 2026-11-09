using System.Security.Cryptography;
using System.Text.Json;
using NINA.Astrometry;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

[Collection("Read-only NINA ephemeris boundary")]
public sealed class October31SparseFieldRecordedTests
{
    [Theory]
    [InlineData("UVEX-20261030T162509Z-d7635be8dcd1490", "00364-", true)]
    [InlineData("UVEX-20261030T162509Z-d7635be8dcd1490", "00442-", true)]
    [InlineData("UVEX-20261030T164356Z-a34deece615a419", "00094-", true)]
    [InlineData("UVEX-20261030T164356Z-a34deece615a419", "00106-", true)]
    [InlineData("UVEX-20261030T154422Z-5b7c2279e68f4da", "00035-", false)]
    [InlineData("UVEX-20261030T160652Z-a066fa9212a74ce", "00035-", false)]
    [InlineData("UVEX-20261030T170221Z-62207a80f16841e", "00034-", false)]
    public async Task ActualWorkerAndAdapterKeepRawSupportAndReachCoarseGeometryOrNextTarget(
        string run, string prefix, bool expected)
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var dir = Path.Combine(root, run);
        var path = Assert.Single(Directory.GetFiles(Path.Combine(dir, "evidence"), prefix + "*.fit"));
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")));
        var plan = manifest.RootElement.GetProperty("plan");
        var target = plan.GetProperty("target");
        var coordinate = new Coordinates(target.GetProperty("rightAscensionDegrees").GetDouble(),
            target.GetProperty("declinationDegrees").GetDouble(), Epoch.J2000, Coordinates.RAType.Degrees);
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "AGENTS.md"))) repo = repo.Parent;
        Assert.NotNull(repo);
        ReadOnlyNinaAstrometry.Initialize();
        var worker = new MeasuredAstapRecovery(configuredWorker:
            Path.Combine(repo.FullName, "scripts", "star-detection", "astap_recovery.py"));
        var actual = await worker.SolveAsync(path, hash, 1920, 1080, coordinate, .3837484767386058, 2,
            Path.Combine(repo.FullName, "tmp", "october31-adapter-replay"), default);
        Assert.Equal(expected, actual.Result is not null);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        using var receipt = JsonDocument.Parse(File.ReadAllText(actual.EvidencePath!));
        var r = receipt.RootElement;
        Assert.False(r.GetProperty("targetIdentityConfirmed").GetBoolean());
        Assert.False(r.GetProperty("motionAuthorized").GetBoolean());
        if (actual.Result is { } solved)
        {
            Assert.Equal("noise-tail-5sigma", r.GetProperty("solverSelection").GetProperty("mode").GetString());
            Assert.InRange(r.GetProperty("matches").GetArrayLength(), 5, 300);
            var position = G3WcsTargetProjector.Project(coordinate, solved, 1920, 1080, MeasuredAstapRecovery.Identity);
            Assert.True(double.IsFinite(position.X) && double.IsFinite(position.Y));
            var inverse = G3WcsTargetProjector.SolveCenterForTargetAtPixel(coordinate, solved,
                1920, 1080, MeasuredAstapRecovery.Identity, new(818, 430));
            var distance = G3AcquisitionMotionPlanner.AngularSeparationArcseconds(solved.Coordinates.RADegrees,
                solved.Coordinates.Dec, inverse.DesiredG3Center.RADegrees, inverse.DesiredG3Center.Dec);
            Assert.InRange(distance, 0, 5400); // Original single coarse-step ceiling, not motion authority.
            var projectedAfter = solved.WithCenter(inverse.DesiredG3Center, 1920, 1080).Project(coordinate);
            Assert.InRange(Math.Abs(projectedAfter.X - 818), 0, .1);
            Assert.InRange(Math.Abs(projectedAfter.Y - 430), 0, .1);
        }
        else
        {
            var exhausted = G3WcsRecoveryPolicy.SearchTerminalGate(
                GateResult.Unknown("G3_BOUNDED_SEARCH_EXHAUSTED_RETURNED", "Checked return fixture after actual raw failure."),
                G3WcsRecoveryPolicy.SearchStopGate("G3_SEARCH_TIME_EXHAUSTED", "Original allowance ended.", null), true, null);
            await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("skip", exhausted.Code,
                exhausted, ObservationStage.AcquireG3SlitField);
        }
    }
}
