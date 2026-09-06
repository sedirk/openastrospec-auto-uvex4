using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class UvexAutomaticConnectionRunnerSafetyTests
{
    private static readonly string Source = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "Sources",
        "RealObservationStageRunner.cs"));

    [Fact]
    public void InitialNightSetupAutoConnectsUvexButRuntimeRevalidationDoesNot()
    {
        var nightSetup = MethodBody(
            "private async Task<StageResult> ValidateNightSetupAsync(",
            "private async Task<GateResult> EvaluateInterlocksAsync(");
        Assert.Contains("connectUvex: true", nightSetup, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(nightSetup, "connectUvex: true"));

        var signature = Source.IndexOf(
            "private async Task<GateResult> EvaluateInterlocksAsync(",
            StringComparison.Ordinal);
        Assert.True(signature >= 0);
        var signatureEnd = Source.IndexOf('{', signature);
        Assert.True(signatureEnd > signature);
        Assert.Contains(
            "bool connectUvex = false",
            Source[signature..signatureEnd],
            StringComparison.Ordinal);
    }

    [Fact]
    public void DurableReturnRecoveryReconnectsOnlyAfterIdentityAndBeforeCheckedReturn()
    {
        var recovery = MethodBody(
            "private async Task<StageResult?> RecoverDurableG3AcquisitionBeforeStageAsync(",
            "private async Task<StageResult> RunG3WcsCenteringAsync(");

        AssertInOrder(
            recovery,
            "var identity = ValidateG3AcquisitionMotionIdentity(context, state);",
            "if (identity.Disposition != GateDisposition.Passed) return",
            "var recoveryInterlocks = await EvaluateInterlocksAsync(",
            "connectUvex: true",
            "if (recoveryInterlocks.Disposition != GateDisposition.Passed)",
            "return new StageResult(recoveryInterlocks, selected.Path);",
            "ReturnDurableG3AcquisitionToOriginAsync(");
        Assert.Equal(1, CountOccurrences(recovery, "connectUvex: true"));
        // Night Setup and an identity-bound outstanding return are the only
        // opt-in callers; ordinary runtime revalidation remains read-only.
        Assert.Equal(2, CountOccurrences(Source, "connectUvex: true"));
        Assert.Contains("StartedUtc = lineageCopies.Min(copy => copy.StartedUtc)", recovery, StringComparison.Ordinal);
        Assert.Contains("CorrectionAttempts = lineageCopies.Max(copy => copy.CorrectionAttempts)", recovery, StringComparison.Ordinal);
    }

    [Fact]
    public void UvexAutoConnectIsCheckpointedLeasedVerifiedAndAudited()
    {
        var body = MethodBody(
            "private async Task<UvexDeviceStatus> ConnectUvexAtCheckpointAsync(",
            "private GateResult ValidateUvexStatus(");

        AssertInOrder(
            body,
            "uvex.GetStatusAsync(",
            "current.PortName",
            "CheckpointAndRejectStaleStageStackAsync(",
            "uvex.AcquireLeaseAsync(",
            "lease.ConnectAndVerifyAsync(",
            "uvex-night-setup-auto-connected");
        Assert.Contains("DeviceConnectionState.Ready && current.PositionKnown", body, StringComparison.Ordinal);
        Assert.DoesNotContain("DisconnectAndVerifyAsync(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void UvexConnectionFailureHasAStableDiagnosticCode()
    {
        var body = MethodBody(
            "private async Task<GateResult> EvaluateInterlocksAsync(",
            "private async Task<GateResult> ValidateQhyServiceConfigurationAsync(");

        Assert.Contains("ConnectUvexAtCheckpointAsync(context, uvex, cancellationToken)", body, StringComparison.Ordinal);
        Assert.Contains("UVEX_AUTO_CONNECT_FAILED", body, StringComparison.Ordinal);
        Assert.Contains("ValidateUvexStatus(uvexStatus)", body, StringComparison.Ordinal);
    }

    private static string MethodBody(string startMarker, string endMarker)
    {
        var start = Source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        var end = Source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing source marker: {endMarker}");
        return Source[start..end];
    }

    private static void AssertInOrder(string source, params string[] markers)
    {
        var prior = -1;
        foreach (var marker in markers)
        {
            var current = source.IndexOf(marker, prior + 1, StringComparison.Ordinal);
            Assert.True(current > prior, $"Missing or out-of-order source marker: {marker}");
            prior = current;
        }
    }

    private static int CountOccurrences(string source, string value)
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
}
