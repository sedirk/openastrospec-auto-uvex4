using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using NINA.Astrometry;
using UvexAdv.Nina.Plugin;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

[Collection("Read-only NINA ephemeris boundary")]
public class MeasuredAstapRecoveryTests
{
    private static DetectorWcsResult Solve() => new(52.42059244071, 47.35515341038, [959.5, 539.5],
        [[-2.350037948239e-5, -.0001032166242124], [.0001017172777465, -2.221416828151e-5]], 1920, 1080);

    [Fact]
    public void FullMatrixReproducesAstropyRawPixelsAndPreservesInverseAndRegistration()
    {
        ReadOnlyNinaAstrometry.Initialize();
        var solve = Solve();
        Assert.False(solve.Flipped);
        var target = new Coordinates(52.4688608925856, 47.3930618039443, Epoch.J2000, Coordinates.RAType.Degrees);
        var projected = G3WcsTargetProjector.Project(target, solve, 1920, 1080, MeasuredAstapRecovery.Identity);
        Assert.InRange(Math.Abs(projected.X-1248.76266922747), 0, 1e-6);
        Assert.InRange(Math.Abs(projected.Y-157.063073326107), 0, 1e-6);
        var shift = new G3SimilarityTransform(1.001, .003, 10, -5);
        var registered = G3RegisteredWcs.RestoreCurrentResult(solve, 1920, 1080,
            MeasuredAstapRecovery.Identity, shift, 1920, 1080);
        var actual = G3WcsTargetProjector.Project(target, registered, 1920, 1080, MeasuredAstapRecovery.Identity);
        var expected = shift.Apply(projected);
        Assert.InRange(Math.Abs(actual.X-expected.X), 0, 1e-6);
        Assert.InRange(Math.Abs(actual.Y-expected.Y), 0, 1e-6);
        var inverse = G3WcsTargetProjector.SolveCenterForTargetAtPixel(target, solve, 1920, 1080,
            MeasuredAstapRecovery.Identity, new(818, 430));
        var moved = solve.WithCenter(inverse.DesiredG3Center, 1920, 1080).Project(target);
        Assert.InRange(Math.Abs(moved.X-818), 0, .03);
        Assert.InRange(Math.Abs(moved.Y-430), 0, .03);
    }

    [Fact]
    public void FallbackClaimDoesNotReplacePrimarySuccessOrWideFieldAndNeverRepeatsHash()
    {
        var recovery = new MeasuredAstapRecovery();
        var hash = new string('a', 64);
        Assert.False(recovery.Claim("PHD2/G3", "Platesolve3Solver", true, hash));
        Assert.False(recovery.Claim("QHY/GS350", "Platesolve3Solver", false, hash));
        Assert.False(recovery.Claim("PHD2/G3", "OtherSolver", false, hash));
        Assert.True(recovery.Claim("PHD2/G3 full frame", "NINA.Platesolve3Solver", false, hash));
        Assert.False(recovery.Claim("PHD2/G3 retry", "NINA.Platesolve3Solver", false, hash.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("latest21455_b", true)]
    [InlineData("moon18552_core", true)]
    [InlineData("latest18552_a", false)]
    [InlineData("moon18552_15s", false)]
    public async Task ActualProductionAdapterReplaysImmutableHistoricalFrame(string label, bool expected)
    {
        var root = Environment.GetEnvironmentVariable("UVEX_ASTAP_REPLAY");
        if (string.IsNullOrWhiteSpace(root)) return;
        using var samples = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "samples.json")));
        var s = samples.RootElement.EnumerateArray().Single(s => s.GetProperty("label").GetString() == label);
        var path = s.GetProperty("path").GetString()!;
        var hash = s.GetProperty("sha256").GetString()!;
        var shape = s.GetProperty("shape").Deserialize<int[]>()!;
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), ignoreCase: true);
        var recovery = new MeasuredAstapRecovery(Path.Combine(root, "production-runtime.json"),
            Path.GetFullPath("scripts/star-detection/astap_recovery.py", FindRepo()));
        var outcome = await recovery.SolveAsync(path, hash, shape[1], shape[0], new Coordinates(
                s.GetProperty("ra").GetDouble(), s.GetProperty("dec").GetDouble(), Epoch.J2000, Coordinates.RAType.Degrees),
            s.GetProperty("scale").GetDouble(), 2, Path.Combine(root, "adapter-replay"), default);
        Assert.Equal(expected, outcome.Result is not null);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), ignoreCase: true);
        if (outcome.Result is not null)
        {
            using var receipt = JsonDocument.Parse(File.ReadAllText(outcome.EvidencePath!));
            Assert.False(receipt.RootElement.GetProperty("targetIdentityConfirmed").GetBoolean());
            Assert.False(receipt.RootElement.GetProperty("motionAuthorized").GetBoolean());
            Assert.Throws<InvalidDataException>(() => MeasuredAstapRecovery.ParseVerified(receipt.RootElement, new string('f',64), shape[1], shape[0]));
        }
        else
        {
            // Optional recovery exhaustion retains the original acquisition
            // route. Model its subsequent checked search return, then execute
            // actual native targets rather than testing a code whitelist.
            var gate = G3WcsRecoveryPolicy.SearchTerminalGate(
                GateResult.Unknown("G3_BOUNDED_SEARCH_EXHAUSTED_RETURNED", "Checked return fixture after real raw solve failure."),
                G3WcsRecoveryPolicy.SearchStopGate("G3_SEARCH_ATTEMPTS_EXHAUSTED", "Original allowance ended.", null), true, null);
            await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("skip", gate.Code, gate, ObservationStage.AcquireG3SlitField);
        }
    }

    private static string FindRepo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "AGENTS.md"))) d = d.Parent;
        return d!.FullName;
    }
}
