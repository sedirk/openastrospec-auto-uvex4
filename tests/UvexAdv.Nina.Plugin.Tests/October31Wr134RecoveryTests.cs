using System.Security.Cryptography;
using System.Text.Json;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class October31Wr134RecoveryTests
{
    [Fact]
    public async Task RecordedFailuresAreAfterSuccessfulAcquisitionNotNewSolveFailures()
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return; // Explicit opt-in local immutable evidence.
        foreach (var (run, code, probes) in new[]
        {
            ("UVEX-20261031T100422Z-cd46bc3dd584486", "G3_GUIDING_RECOVERY_STOP_CHANGED", 6),
            ("UVEX-20261031T121222Z-298a8c2b1b3c4d6", "PHD2_GUIDE_OUTPUT_STOP_UNCONFIRMED", 0),
        })
        {
            var path = Path.Combine(root, run, "manifest.json");
            var before = SHA256.HashData(await File.ReadAllBytesAsync(path));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var journal = document.RootElement.GetProperty("journal").EnumerateArray().ToArray();
            Assert.Contains(journal, e => e.GetProperty("code").GetString() == "G3_WCS_CENTERED_FIELD_IDENTIFIED");
            Assert.Contains(journal, e => e.GetProperty("code").GetString() == code);
            var counts = document.RootElement.GetProperty("counters");
            Assert.Equal(0, counts.GetProperty("atrAcceptedFrames").GetInt32());
            Assert.Equal(probes, counts.GetProperty("additional").GetProperty("atrProbeAttemptedFrames").GetInt32());
            Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(path)));
        }
        // These repaired paths must preserve the original distinction: a new
        // optical failure after recovery can skip only after all owners stop.
        // A failed/unconfirmed output recovery is still equipment uncertainty.
        var quality = RealObservationStageRunner.Phd2OutputRecoveryFailure(
            new PhysicalActionGateException(GateResult.TargetQuality("G3_BOUNDED_SEARCH_EXHAUSTED_RETURNED", "original budget exhausted")), null);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("skip", quality.Gate.Code, quality.Gate,
            ObservationStage.PlaceTargetOnSlit);
        var uncertain = RealObservationStageRunner.Phd2OutputRecoveryFailure(new IOException("RPC server unavailable"), null);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("unknown-gate", uncertain.Gate.Code, uncertain.Gate,
            ObservationStage.PlaceTargetOnSlit);
    }
}
