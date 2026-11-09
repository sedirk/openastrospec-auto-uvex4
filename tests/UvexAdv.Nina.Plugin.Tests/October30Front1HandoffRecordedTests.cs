using System.Security.Cryptography;
using System.Text.Json;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class October30Front1HandoffRecordedTests
{
    [Theory]
    [InlineData("7c11c7995973446889c37bbb4d8a794b", "EF382BEE8979FBCCFEDDED1A39CA394DDEC2EB4EA43FD22019EEAE43450C16BC")]
    [InlineData("UVEX-20261030T124551Z-161727cb8ff5471", "2434B22A93F0E8C7E82F367477B848B1A261B0DEE039F34D780755587E42DB5C")]
    public async Task SuccessfulDualCameraSolvesWithUnverifiedMountSyncAreNotBlankFieldOrNightFailure(string runId, string expectedHash)
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var evidence = Path.Combine(root, runId, "evidence");
        using var ladder = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(evidence, "*g3-plate-solve-ladder-attempt.json"))));
        using var sync = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(evidence, "*qhy-mount-coordinate-sync-degraded-to-hint.json"))));
        var p = ladder.RootElement.GetProperty("payload");
        var s = sync.RootElement.GetProperty("payload");
        Assert.True(p.GetProperty("solveSucceeded").GetBoolean());
        Assert.True(p.GetProperty("usableSourceCount").GetInt32() >= 12);
        Assert.Equal("FormalSolver", p.GetProperty("astrometryAuthority").GetString());
        Assert.True(s.GetProperty("syncCommanded").GetBoolean());
        Assert.False(s.GetProperty("syncVerified").GetBoolean());
        Assert.False(s.GetProperty("catalogueSlewAuthorized").GetBoolean());
        Assert.False(s.GetProperty("additionalSyncAuthorized").GetBoolean());
        var disagreement = G3AcquisitionMotionPlanner.AngularSeparationArcseconds(
            p.GetProperty("solvedRaDegrees").GetDouble(), p.GetProperty("solvedDecDegrees").GetDouble(),
            p.GetProperty("solveHintRaDegrees").GetDouble(), p.GetProperty("solveHintDecDegrees").GetDouble());
        Assert.InRange(disagreement, 100, 180); // Two optical witnesses agree; mount report does not.
        Assert.InRange(s.GetProperty("residualAfterArcseconds").GetDouble(), 11800, 12000);
        var binding = p.GetProperty("mountBinding");
        var framePath = binding.GetProperty("framePath").GetString()!;
        var before = SHA256.HashData(File.ReadAllBytes(framePath));
        Assert.Equal(expectedHash, Convert.ToHexString(before));
        var raw = QhyFitsCodec.Read(framePath);
        var image = new MonochromeFrame(raw.Width, raw.Height, raw.Pixels,
            G3FrameInputPolicy.ResolveEffectiveSaturationAdu(raw.Pixels, 4095));
        Assert.True(G3SolveProbeContentAnalyzer.Analyze(image).HasCoherentSource);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(framePath)));

        var reserve = Assert.IsType<GateResult>(G3WcsRecoveryPolicy.UnreachableRoundTripAtOrigin(
            0, 0, 0, 11738.9, 40, 2, 21600));
        Assert.Null(G3WcsRecoveryPolicy.UnreachableRoundTripAtOrigin(
            0, 0, 0, 11738.9, 40, 2, 21600, formalWaypointApproach: true));
        var failure = G3WcsRecoveryPolicy.ReturnedBudgetGate(reserve, true,
            new Dictionary<string, double> { ["chargedActions"] = 0, ["returnedToOrigin"] = 1 });
        Assert.Equal(GateFailureScope.TargetQuality, failure.FailureScope);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("skip", failure.Code, failure, ObservationStage.AcquireG3SlitField);
    }

    [Fact]
    public async Task RestoredCandidateLossAfterCheckedStopActuallyExecutesNextNativeTarget()
    {
        var result = new Phd2NativeGuideSelectionExhaustedException(3,
            ["Recorded 2s -> 4s -> 2s preview loss; no guide or exact-lock command issued"]).AfterCheckedStop();
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("skip", result.Gate.Code,
            result.Gate, ObservationStage.PlaceTargetOnSlit);
    }
}
