using System.Security.Cryptography;
using System.Text.Json;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3OverexposureContinuationTests
{
    public static IEnumerable<object[]> Boundaries()
    {
        foreach (var duringSearch in new[] { false, true })
        foreach (var boundary in new[] { "skip", "cleanup-failure", "end-policy", "late-unsafe",
                     "late-cancel", "late-deadline", "late-dawn" })
            yield return [duringSearch, boundary];
    }

    [Theory]
    [MemberData(nameof(Boundaries))]
    public async Task ValidatedClippingActuallyRunsNextNativeTargetOnlyAfterCheckedStop(
        bool duringSearch, string boundary)
    {
        var content = ClippedContent();
        var source = RealObservationStageRunner.ClassifyG3OverexposedLadder(Attempts(), content);
        Assert.Equal(GateFailureScope.TargetQuality, source.FailureScope);
        Assert.Same(content.Metrics, source.Metrics);
        Assert.Equal(GateDisposition.Indeterminate, source.Disposition);
        var gate = duringSearch
            ? G3WcsRecoveryPolicy.SearchTerminalGate(
                GateResult.Unknown("G3_BOUNDED_SEARCH_EXHAUSTED_RETURNED", "Checked return; no replenished budget."),
                source, true, null)
            : source;
        // Exercises the production classifier + search terminal policy, then
        // TWO native containers/coordinator stacks, not a skip-code allowlist.
        // The common harness verifies journal outcomes and second Finalize.
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(boundary, gate.Code, gate);
    }

    [Theory]
    [InlineData("capture-transient")]
    [InlineData("solver-transient")]
    [InlineData("missing-binding")]
    [InlineData("mismatched-frame")]
    [InlineData("missing-evidence")]
    [InlineData("missing-tier")]
    [InlineData("solve-succeeded")]
    [InlineData("content-missing")]
    [InlineData("unknown-gate")]
    [InlineData("clipping-not-terminal")]
    public async Task ClippedPixelsCannotHideOtherEvidenceFailures(string failure)
    {
        var attempts = Attempts();
        attempts[0] = failure switch
        {
            "capture-transient" => attempts[0] with { GateCode = "G3_PLATE_SOLVE_CAPTURE_TRANSIENT_TIER_SKIPPED" },
            "solver-transient" => attempts[0] with { GateCode = "G3_PLATE_SOLVE_SOLVER_TRANSIENT_TIER_SKIPPED" },
            "missing-binding" => attempts[0] with { MountBinding = null },
            "mismatched-frame" => attempts[0] with { FramePath = "different.fits" },
            "missing-evidence" => attempts[0] with { AttemptEvidencePath = "" },
            "missing-tier" => attempts[0] with { LadderIndex = 0 },
            "solve-succeeded" => attempts[0] with { SolveSucceeded = true },
            "unknown-gate" => attempts[0] with { GateCode = "NEW_UNREVIEWED_GATE" },
            _ => attempts[0],
        };
        if (failure == "clipping-not-terminal") Array.Reverse(attempts);
        var gate = RealObservationStageRunner.ClassifyG3OverexposedLadder(attempts,
            failure == "content-missing" ? null : ClippedContent());
        Assert.Equal(GateFailureScope.Run, gate.FailureScope);
        Assert.False(NativeSequencePlan.IsSkippableQualityFailure(gate));
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("unknown-gate", gate.Code, gate);
    }

    [Theory]
    [InlineData("G3_SEARCH_PIER_SIDE_CHANGED")]
    [InlineData("G3_SEARCH_RETURN_STOP_UNCONFIRMED")]
    [InlineData("PHD2_IDENTITY_STALE")]
    public async Task OverexposedTargetDoesNotOverrideUnconfirmedReturnOrOwnerFailure(string code)
    {
        var source = RealObservationStageRunner.ClassifyG3OverexposedLadder(Attempts(), ClippedContent());
        var returnFailure = GateResult.Unknown(code, "The return/stop was not verified.");
        var gate = G3WcsRecoveryPolicy.SearchTerminalGate(
            GateResult.Unknown("G3_SEARCH_RETURN_BLOCKED", "Origin not confirmed."), source, false, returnFailure);
        Assert.Equal(code, gate.Code);
        Assert.Equal(GateFailureScope.Run, gate.FailureScope);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("unknown-gate", code, gate);
    }

    [Theory]
    [InlineData("PHD2_IDENTITY_STALE", false)]
    [InlineData("NIGHT_UNSAFE", false)]
    [InlineData("UNKNOWN_FALLBACK_FAILURE", false)]
    [InlineData("G3_POST_WCS_SHORT_UNCONFIRMED", true)]
    public async Task DeterministicFallbackRetainsOriginalFailureIdentity(string code, bool quality)
    {
        var metrics = new Dictionary<string, double> { ["original"] = 1 };
        var original = quality ? GateResult.TargetQuality(code, "Original failure.", metrics)
            : GateResult.Unknown(code, "Original failure.", metrics);
        var gate = RealObservationStageRunner.PreserveG3OverexposureFallbackFailure(original);
        Assert.Equal(code, gate.Code);
        Assert.Equal(original.FailureScope, gate.FailureScope);
        Assert.Same(original.Metrics, gate.Metrics);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(
            quality ? "skip" : "unknown-gate", code, gate);
    }

    [Fact]
    public async Task RecordedHd220526ClippingAndReturnedSearchProduceCheckedNativeSkip()
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var dir = Path.Combine(root, "UVEX-20261023T105930Z-c80ec9898496432", "evidence");
        var summaryPath = Assert.Single(Directory.GetFiles(dir, "00115-*-g3-plate-solve-ladder-summary.json"));
        var attemptPath = Assert.Single(Directory.GetFiles(dir, "00114-*-g3-plate-solve-ladder-attempt.json"));
        var returnPath = Assert.Single(Directory.GetFiles(dir, "00117-*-g3-bounded-search-summary.json"));
        var rawPath = Assert.Single(Directory.GetFiles(dir, "00112-*-g3-plate-solve-probe-03-15000ms.fit"));
        var paths = new[] { summaryPath, attemptPath, returnPath, rawPath };
        var hashes = paths.ToDictionary(path => path, Hash);
        using var summary = JsonDocument.Parse(File.ReadAllText(summaryPath));
        using var attempt = JsonDocument.Parse(File.ReadAllText(attemptPath));
        using var returned = JsonDocument.Parse(File.ReadAllText(returnPath));
        var attempts = summary.RootElement.GetProperty("payload").GetProperty("attempts")
            .Deserialize<G3PlateSolveAttemptEvidence[]>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(attempt.RootElement.GetProperty("source").GetProperty("sha256").GetString()!.ToUpperInvariant(), hashes[rawPath]);
        var fits = QhyFitsCodec.Read(rawPath);
        var content = G3SolveProbeContentAnalyzer.Analyze(new MonochromeFrame(fits.Width, fits.Height, fits.Pixels,
            G3FrameInputPolicy.ResolveEffectiveSaturationAdu(fits.Pixels, 4095)));
        Assert.Equal(0.5183024691358025, content.SaturatedPixelFraction, 12);
        Assert.Equal(65520, content.BackgroundMedianAdu);
        var source = RealObservationStageRunner.ClassifyG3OverexposedLadder(attempts, content.Gate);
        Assert.Equal(GateFailureScope.TargetQuality, source.FailureScope);
        var physical = returned.RootElement.GetProperty("payload");
        Assert.True(physical.GetProperty("returnedToOrigin").GetBoolean());
        Assert.Equal(1845.9072101621628, physical.GetProperty("cumulativeSearchMotionArcseconds").GetDouble(), 9);
        var terminal = G3WcsRecoveryPolicy.SearchTerminalGate(
            GateResult.Unknown("G3_BOUNDED_SEARCH_EXHAUSTED_RETURNED", physical.GetProperty("finalReason").GetString()!),
            source, true, null);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("skip", terminal.Code, terminal);
        foreach (var path in paths) Assert.Equal(hashes[path], Hash(path));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static GateResult ClippedContent() => G3SolveProbeContentAnalyzer.Analyze(
        new MonochromeFrame(96, 96, Enumerable.Repeat((ushort)65520, 96 * 96).ToArray(), 65520)).Gate;

    private static G3PlateSolveAttemptEvidence[] Attempts() => Enumerable.Range(1, 3).Select(i =>
    {
        var path = $"synthetic-{i}.fits";
        var binding = new G3FieldMountBinding(1, "test-run", "test-action", "test-preset", path,
            "test-sha", DateTimeOffset.UtcNow, 45, 40, "J2000", "East", DateTimeOffset.UtcNow, "test-binding");
        return new G3PlateSolveAttemptEvidence(i, i * 5000,
            i == 3 ? G3SolveProbeContentAnalyzer.OverexposedGateCode : "G3_PLATE_SOLVE_TIER_FAILED",
            GateDisposition.Indeterminate, false, path, $"attempt-{i}.json",
            i == 3 ? G3SolveProbeContentAnalyzer.OverexposedGateCode : "G3_SOLVE_PROBE_STRUCTURED_FIELD",
            i == 3 ? 0 : 3, binding);
    }).ToArray();
}
