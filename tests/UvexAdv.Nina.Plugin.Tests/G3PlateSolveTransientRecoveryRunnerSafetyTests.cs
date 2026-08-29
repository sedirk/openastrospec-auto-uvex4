using Xunit;
using System.Security.Cryptography;
using UvexAdv.Nina.Plugin;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3PlateSolveTransientRecoveryRunnerSafetyTests
{
    private static readonly string MainSource = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "Sources",
        "RealObservationStageRunner.cs"));

    private static readonly string RecoverySource = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "Sources",
        "RealObservationStageRunner.G3PlateSolveTransientRecovery.cs"));

    [Theory]
    [InlineData((int)G3PlateSolveTransientOperation.Capture, "capture-timeout", "G3_PLATE_SOLVE_CAPTURE_TRANSIENT_TIER_SKIPPED")]
    [InlineData((int)G3PlateSolveTransientOperation.FitsRead, "fits-io", "G3_PLATE_SOLVE_FITS_READ_TRANSIENT_TIER_SKIPPED")]
    [InlineData((int)G3PlateSolveTransientOperation.Solver, "solver-timeout", "G3_PLATE_SOLVE_SOLVER_TRANSIENT_TIER_SKIPPED")]
    public void ReviewedFaultInjectionProducesStructuredTransientGate(
        int operationValue,
        string fault,
        string expectedCode)
    {
        var operation = (G3PlateSolveTransientOperation)operationValue;
        Exception exception = fault switch
        {
            "capture-timeout" => new Phd2CommandTimeoutException("native single-frame capture", TimeSpan.FromSeconds(1)),
            "fits-io" => new IOException("fresh FITS was temporarily unreadable"),
            "solver-timeout" => new TimeoutException("configured solver timed out"),
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };

        var gate = RealObservationStageRunner.ClassifyG3PlateSolveTransientFailure(operation, exception);

        Assert.NotNull(gate);
        Assert.Equal(expectedCode, gate.Code);
    }

    [Theory]
    [MemberData(nameof(HardStopFaults))]
    public void CancellationIdentityHashAccessAndConfigurationFaultsAreNotSwallowed(Exception exception)
    {
        var gate = RealObservationStageRunner.ClassifyG3PlateSolveTransientFailure(
            G3PlateSolveTransientOperation.FitsRead,
            exception);

        Assert.Null(gate);
    }

    public static IEnumerable<object[]> HardStopFaults()
    {
        yield return new object[] { new OperationCanceledException("cancel") };
        yield return new object[]
        {
            new Phd2IdentityMismatchException(new Phd2IdentityValidation(
                new Phd2Profile(2, "locked-profile"),
                new Phd2Equipment(null, null, null, null, null),
                new[] { "camera identity changed" },
                Array.Empty<string>()))
        };
        yield return new object[] { new Phd2DisconnectedException("identity-pinned owner disconnected") };
        yield return new object[] { new CryptographicException("SHA-256 computation failed") };
        yield return new object[] { new UnauthorizedAccessException("access denied") };
        yield return new object[] { new IOException("immutable evidence hash mismatch") };
        yield return new object[] { new InvalidOperationException("optical parameters are not commissioned") };
    }

    [Fact]
    public void CaptureFitsAndSolverTransientsAdvanceTheFiniteExposureLadder()
    {
        var ladder = Slice(
            MainSource,
            "private async Task<G3PlateSolveProbeState> CaptureG3PlateSolveLadderAsync(",
            "private GateResult ValidateG3SolveProbeImage(");

        Assert.Contains("for (var index = 0; index < preset.ExposureMilliseconds.Count; index++)", ladder, StringComparison.Ordinal);
        Assert.Equal(4, Count(ladder, "ClassifyG3PlateSolveTransientFailure("));
        Assert.Equal(4, Count(ladder, "RecordG3PlateSolveTransientFailureAsync("));
        Assert.Contains("G3PlateSolveTransientOperation.Capture", ladder, StringComparison.Ordinal);
        Assert.Contains("G3PlateSolveTransientOperation.FitsRead", ladder, StringComparison.Ordinal);
        Assert.Contains("G3PlateSolveTransientOperation.Solver", ladder, StringComparison.Ordinal);
        Assert.DoesNotContain("index--", ladder, StringComparison.Ordinal);
        Assert.DoesNotContain("while (true)", ladder, StringComparison.Ordinal);
        Assert.Equal(1, Count(ladder, "ReserveRunEvidencePath("));
        Assert.Contains("$\"g3-plate-solve-probe-{index + 1:D2}-{exposureMilliseconds}ms\"", ladder, StringComparison.Ordinal);
        Assert.Contains("samePathRetryAuthorized = false", RecoverySource, StringComparison.Ordinal);
        Assert.Contains("motionAuthorized = false", RecoverySource, StringComparison.Ordinal);
        Assert.Contains("durableMotionBudgetsReset = false", RecoverySource, StringComparison.Ordinal);
    }

    [Fact]
    public void CancellationIdentityAndHashFailuresRemainHardStops()
    {
        Assert.Contains("OperationCanceledException", RecoverySource, StringComparison.Ordinal);
        Assert.Contains("Phd2IdentityMismatchException", RecoverySource, StringComparison.Ordinal);
        Assert.Contains("CryptographicException", RecoverySource, StringComparison.Ordinal);
        Assert.Contains("ContainsHardEvidenceTerm", RecoverySource, StringComparison.Ordinal);
        Assert.Contains("message.Contains(\"hash\"", RecoverySource, StringComparison.Ordinal);
        Assert.Contains("message.Contains(\"sha-256\"", RecoverySource, StringComparison.Ordinal);

        var ladder = Slice(
            MainSource,
            "private async Task<G3PlateSolveProbeState> CaptureG3PlateSolveLadderAsync(",
            "private GateResult ValidateG3SolveProbeImage(");
        var hash = ladder.IndexOf("ComputeFileSha256Async(captured.Path", StringComparison.Ordinal);
        var fitsReadClassification = ladder.IndexOf(
            "G3PlateSolveTransientOperation.FitsRead",
            hash,
            StringComparison.Ordinal);
        Assert.True(hash >= 0 && fitsReadClassification > hash);
        Assert.DoesNotContain("ComputeFileSha256Async", RecoverySource, StringComparison.Ordinal);
        Assert.Contains("message.Contains(\"optical parameters\"", RecoverySource, StringComparison.Ordinal);
        Assert.Contains("if (transientGate is null) throw;", ladder, StringComparison.Ordinal);
    }

    [Fact]
    public void PureTransientExhaustionCannotBecomeStructuredOrEnvironmentalMotionAuthority()
    {
        var ladder = Slice(
            MainSource,
            "private async Task<G3PlateSolveProbeState> CaptureG3PlateSolveLadderAsync(",
            "private GateResult ValidateG3SolveProbeImage(");

        Assert.Contains("allAttemptsWereTransient", ladder, StringComparison.Ordinal);
        Assert.Contains("!IsG3PlateSolveTransientGateCode(attempt.GateCode)", ladder, StringComparison.Ordinal);
        Assert.Contains("var boundedSparseRecoveryAuthorized = !allAttemptsWereTransient", ladder, StringComparison.Ordinal);
        Assert.Contains("G3_PLATE_SOLVE_LADDER_TRANSIENT_EXHAUSTED", ladder, StringComparison.Ordinal);
        Assert.Contains("none authorizes target identity or mount motion", ladder, StringComparison.Ordinal);
        Assert.Contains("LastOrDefault(attempt => !IsG3PlateSolveTransientGateCode(attempt.GateCode))", ladder, StringComparison.Ordinal);
        Assert.Contains("summarySourcePath = null", ladder, StringComparison.Ordinal);
        Assert.Contains("summarySourcePath,", ladder, StringComparison.Ordinal);
        Assert.DoesNotContain("latest?.FramePath,", ladder, StringComparison.Ordinal);
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static string Slice(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from);
        return source[from..to];
    }
}
