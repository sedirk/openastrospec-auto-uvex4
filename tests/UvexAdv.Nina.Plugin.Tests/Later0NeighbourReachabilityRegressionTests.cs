using System.Security.Cryptography;
using System.Text.Json;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Later0NeighbourReachabilityRegressionTests
{
    [Fact]
    public async Task ActualFirstTargetHasNoRegistrationAndDoesNotJustifyThe117ArcsecondSearchCap()
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var folder = Path.Combine(root, "UVEX-20261025T191054Z-db9ce157515f4a8", "evidence");
        var file = Assert.Single(Directory.GetFiles(folder, "00035-*.fit"));
        var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        Assert.Equal("E07FD7FE63FFFF4617BE476C535D6134ABB31EF97E66E78C21CD38E8925155F1", digest);
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "AGENTS.md"))) repo = repo.Parent;
        Assert.NotNull(repo);
        var raw = QhyFitsCodec.Read(file);
        var sep = await new SepStarDetectionClient().DetectAsync(new(
            Path.Combine(repo.FullName, "output", "star-detection-venv", "Scripts", "python.exe"),
            Path.Combine(repo.FullName, "scripts", "star-detection", "worker.py")), raw.Width, raw.Height, raw.Pixels, 65520, default);
        var stars = CatalogFieldRegistration.ReferenceStars(sep);
        Assert.Empty(stars); // do not weaken extraction to manufacture a graph
        foreach (var receipt in Directory.GetFiles(folder, "*g3-overlap-registration.json"))
        {
            using var evidence = JsonDocument.Parse(File.ReadAllText(receipt));
            Assert.False(evidence.RootElement.GetProperty("payload").GetProperty("registration").GetProperty("accepted").GetBoolean());
        }
        var limits = new G3LocalSearchLimits(G3LocalSearchPattern.SquareSpiral, 300, 900, 3600, 12, TimeSpan.FromSeconds(1200));
        var footprint = new G3OverlapFootprint(1920, 1080, .3837484767386058, null, 40.03047514115362);
        var old = G3FieldOverlapPolicy.PlanSearch(limits, footprint);
        Assert.InRange(old.EffectiveLimits.StepArcseconds, 116, 118);
        var visit = G3NeighbourSearchPolicy.PlanVisit(limits, footprint, false,
            G3LocalSearchPlanner.Build(limits)[0], 0, 0, 1);
        Assert.Equal(300, visit.Waypoint.MoveFromPreviousArcseconds);
        Assert.False(visit.PreservesOverlap);
        Assert.Equal(digest, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        // No frame exists at the newly reachable destination. This regression
        // proves reachability only, never optical success or target identity.
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("cleanup-failure")]
    [InlineData("late-unsafe")]
    [InlineData("late-cancel")]
    [InlineData("late-deadline")]
    [InlineData("late-dawn")]
    public async Task ExhaustedSearchActuallyExecutesTheNextNativeTargetOnlyAtCheckedBoundary(string boundary)
    {
        var failed = G3OverlapRegistration.Register([], [], 1920, 1080);
        Assert.False(failed.Accepted);
        var gate = G3WcsRecoveryPolicy.SearchTerminalGate(
            GateResult.Unknown("G3_BOUNDED_SEARCH_EXHAUSTED_RETURNED", "Checked return fixture."),
            G3WcsRecoveryPolicy.SearchStopGate("G3_SEARCH_ATTEMPTS_EXHAUSTED", "Original action allowance ended.", null),
            returned: true, returnFailure: null);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(boundary, gate.Code,
            gate, ObservationStage.AcquireG3SlitField);
    }

    [Fact]
    public void ActualRunnerReevaluatesSupportBeforeOriginalReservationAndNeverRecreatesLedger()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var start = source.IndexOf("private async Task<StageResult> RunBoundedG3LocalSearchAsync(", StringComparison.Ordinal);
        var loop = source.IndexOf("for (var actionNumber = 1;", start, StringComparison.Ordinal);
        var support = source.IndexOf("CurrentSearchOverlapSupportAsync", loop, StringComparison.Ordinal);
        var reserve = source.IndexOf("ValidateG3SearchMoveAndReturnReserve", loop, StringComparison.Ordinal);
        Assert.True(start < loop && loop < support && support < reserve);
        Assert.DoesNotContain("BeginG3AcquisitionMotionAsync", source[loop..source.IndexOf("private GateResult ValidateG3SearchMountState", loop, StringComparison.Ordinal)]);
    }
}
