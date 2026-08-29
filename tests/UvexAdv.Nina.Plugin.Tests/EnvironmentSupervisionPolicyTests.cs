using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class EnvironmentSupervisionPolicyTests
{
    private static readonly string RunnerSource = File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "src",
        "UvexAdv.Nina.Plugin",
        "RealObservationStageRunner.cs"));

    [Fact]
    public void FullUnattendedRequiresEveryLockedEnvironmentSelection()
    {
        var complete = new NinaEnvironmentDeviceSelection(
            "AIWeatherSafetyMonitor",
            "RRCIAdvanced.Dome",
            "NINA.OpenMeteo.Client",
            "ASCOM.GeminiAutoCover.CoverCalibrator");
        var incomplete = complete with { WeatherDataId = NinaEnvironmentDeviceSelection.NoDeviceId };

        Assert.Equal(GateDisposition.Passed, complete.ValidateForMode(weakSupervisionEnabled: false).Disposition);
        var gate = incomplete.ValidateForMode(weakSupervisionEnabled: false);
        Assert.Equal(GateDisposition.Indeterminate, gate.Disposition);
        Assert.Equal("UNATTENDED_ENVIRONMENT_ADAPTER_SELECTION_MISSING", gate.Code);
    }

    [Fact]
    public void WeakSupervisionDegradesOnlyMissingSelectionsToWarningPass()
    {
        var missing = NinaEnvironmentDeviceSelection.None with
        {
            SafetyMonitorId = "AIWeatherSafetyMonitor",
        };

        var gate = missing.ValidateForMode(weakSupervisionEnabled: true);

        Assert.Equal(GateDisposition.Passed, gate.Disposition);
        Assert.Equal(GateSeverity.Warning, gate.Severity);
        Assert.Equal("WEAK_SUPERVISION_ENVIRONMENT_ADAPTERS_DEGRADED", gate.Code);
        Assert.Contains("dome/roll-off-roof adapter", gate.Message, StringComparison.Ordinal);
        Assert.Contains("weather adapter", gate.Message, StringComparison.Ordinal);
        Assert.Contains("optical-cover adapter", gate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectedAdaptersAreAutoConnectedBeforeAcquisitionOwners()
    {
        var connection = MethodBody(
            "private async Task<GateResult> EnsureNinaEnvironmentAdaptersConnectedAsync(",
            "private async Task<GateResult> EnsureDomeOrRoofOpenForUnattendedAsync(");

        Assert.Contains("safetyMonitorMediator.Connect()", connection, StringComparison.Ordinal);
        Assert.Contains("weatherDataMediator.Connect()", connection, StringComparison.Ordinal);
        Assert.Contains("domeMediator.Connect()", connection, StringComparison.Ordinal);
        Assert.Contains("flatDeviceMediator.Connect()", connection, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenShutter", connection, StringComparison.Ordinal);
        Assert.DoesNotContain("CloseShutter", connection, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenCover", connection, StringComparison.Ordinal);
        Assert.DoesNotContain("CloseCover", connection, StringComparison.Ordinal);
        Assert.Contains("connectedRoof.ShutterStatus == ShutterState.ShutterOpen", connection, StringComparison.Ordinal);
        Assert.Contains("domeOrRoofOpenEstablished, 1", connection, StringComparison.Ordinal);
        Assert.Contains("domeOrRoofLifecycleCommitted, 1", connection, StringComparison.Ordinal);
    }

    [Fact]
    public void FullUnattendedRoofOpenIsSafeParkedBoundedAndWeakModeNeverOpens()
    {
        var open = MethodBody(
            "private async Task<GateResult> EnsureDomeOrRoofOpenForUnattendedAsync(",
            "private GateResult ValidateRoofOpeningPrerequisites(");
        var weak = open.IndexOf("configuration.Environment.WeakSupervisionEnabled", StringComparison.Ordinal);
        var park = open.IndexOf("telescopeMediator.ParkTelescope", StringComparison.Ordinal);
        var openCommand = open.IndexOf("domeMediator.OpenShutter", StringComparison.Ordinal);
        var wait = open.IndexOf("WaitForDomeOrRoofStateAsync", StringComparison.Ordinal);

        Assert.True(weak >= 0 && park > weak && openCommand > park && wait > openCommand);
        Assert.Contains("ValidateRoofOpeningPrerequisites", open, StringComparison.Ordinal);
        Assert.Contains("ROOF_OPEN_COMMAND_REJECTED", open, StringComparison.Ordinal);
        Assert.Contains("replica commands plus replica opening", open, StringComparison.Ordinal);
        Assert.Contains("domeOrRoofLifecycleCommitted", open, StringComparison.Ordinal);
        Assert.Contains("ROOF_CLOSED_AFTER_OPEN", open, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Exchange(ref domeOrRoofLifecycleCommitted, 0)",
            RunnerSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NormalAndFaultCleanupCloseCoverBeforeParkingAndClosingRoof()
    {
        var finalize = MethodBody(
            "private async Task<StageResult> FinalizeObservationAsync(",
            "private IReadOnlyList<string> ValidateStaticConfiguration(");
        var finalizeCover = finalize.IndexOf("CloseOpticalCoverAsync(", StringComparison.Ordinal);
        var finalizeRoof = finalize.IndexOf("ParkMountAndCloseDomeOrRoofAsync(", StringComparison.Ordinal);
        Assert.True(finalizeCover >= 0 && finalizeRoof > finalizeCover);

        var close = MethodBody(
            "private async Task<string?> ParkMountAndCloseDomeOrRoofAsync(",
            "private async Task<QhyCameraStatus> ConnectQhyAtCheckpointAsync(");
        var park = close.IndexOf("telescopeMediator.ParkTelescope", StringComparison.Ordinal);
        var closeCommand = close.IndexOf("domeMediator.CloseShutter", StringComparison.Ordinal);
        Assert.True(park >= 0 && closeCommand > park);
        Assert.Contains("No mount motion was attempted under a closed roof", close, StringComparison.Ordinal);

        var cleanup = MethodBody(
            "private async Task<IReadOnlyList<string>> CleanupAfterFailureCoreAsync(",
            "private async Task StopPhdAndWaitAsync(");
        var cleanupCover = cleanup.IndexOf("CloseOpticalCoverAsync(reason", StringComparison.Ordinal);
        var cleanupRoof = cleanup.IndexOf("ParkMountAndCloseDomeOrRoofAsync(reason", StringComparison.Ordinal);
        Assert.True(cleanupCover >= 0 && cleanupRoof > cleanupCover);
    }

    [Fact]
    public void OptionalQhyReadbackCannotBypassRequiredFinalizationCleanup()
    {
        var finalize = MethodBody(
            "private async Task<StageResult> FinalizeObservationAsync(",
            "private async Task<(QhyJobSnapshot? Snapshot, string? Issue)> ReadQhyJobForFinalizationAsync(");
        var read = finalize.IndexOf("ReadQhyJobForFinalizationAsync(", StringComparison.Ordinal);
        var slitOff = finalize.IndexOf("EnsureSlitIlluminationOffAsync(", StringComparison.Ordinal);
        var phdStop = finalize.IndexOf("StopPhdAndWaitAsync(", StringComparison.Ordinal);
        var cover = finalize.IndexOf("CloseOpticalCoverAsync(", StringComparison.Ordinal);
        var roof = finalize.IndexOf("ParkMountAndCloseDomeOrRoofAsync(", StringComparison.Ordinal);

        Assert.True(read >= 0 && slitOff > read && phdStop > slitOff && cover > phdStop && roof > cover);
        Assert.Contains("if (readbackIssue is not null)", finalize, StringComparison.Ordinal);
        Assert.Contains("cleanupWarnings.Add(readbackIssue)", finalize, StringComparison.Ordinal);
        var qhyStopBoundary = finalize.IndexOf(
            "if (snapshot is not null && snapshot.State is not",
            StringComparison.Ordinal);
        var qhyCheckpoint = finalize.IndexOf(
            "CheckpointAndRejectStaleStageStackAsync(context, cancellationToken)",
            qhyStopBoundary,
            StringComparison.Ordinal);
        var cancellationCatch = finalize.IndexOf(
            "catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }",
            qhyCheckpoint,
            StringComparison.Ordinal);
        var pauseCatch = finalize.IndexOf(
            "catch (ResumeStageRestartException) { throw; }",
            cancellationCatch,
            StringComparison.Ordinal);
        var optionalWarning = finalize.IndexOf(
            "cleanupWarnings.Add($\"QHY photometry stop failed:",
            pauseCatch,
            StringComparison.Ordinal);
        Assert.True(qhyStopBoundary >= 0 && qhyCheckpoint > qhyStopBoundary && cancellationCatch > qhyCheckpoint && pauseCatch > cancellationCatch && optionalWarning > pauseCatch && slitOff > optionalWarning);

        var retry = MethodBody(
            "private async Task<(QhyJobSnapshot? Snapshot, string? Issue)> ReadQhyJobForFinalizationAsync(",
            "private IReadOnlyList<string> ValidateStaticConfiguration(");
        Assert.Contains("attempt <= 2", retry, StringComparison.Ordinal);
        Assert.Contains("qhy.GetJobAsync(jobId", retry, StringComparison.Ordinal);
        Assert.Contains("catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)", retry, StringComparison.Ordinal);
        Assert.Contains("requiredCleanupContinues = true", retry, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureCameraConnectedAsync", retry, StringComparison.Ordinal);
        Assert.DoesNotContain("qhy.ResumeAsync", retry, StringComparison.Ordinal);
        Assert.DoesNotContain("qhy.Start", retry, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalizeReconnectsOnlyTheLockedCoverAdapterBeforeAnyCloseCommand()
    {
        var close = MethodBody(
            "private async Task<string?> CloseOpticalCoverAsync(",
            "private async Task<string?> ParkMountAndCloseDomeOrRoofAsync(");
        var selection = close.IndexOf("ValidateCurrentEnvironmentDeviceSelections()", StringComparison.Ordinal);
        var reconnect = close.IndexOf("flatDeviceMediator.Connect()", StringComparison.Ordinal);
        var reread = close.IndexOf("info = flatDeviceMediator.GetInfo()", reconnect, StringComparison.Ordinal);
        var identity = close.IndexOf("EnvironmentAdapterIdentityMatches(info.DeviceId", reread, StringComparison.Ordinal);
        var closeCommand = close.IndexOf("flatDeviceMediator.CloseCover", identity, StringComparison.Ordinal);

        Assert.True(selection >= 0 && reconnect > selection && reread > reconnect && identity > reread && closeCommand > identity);
        Assert.Contains("readOnlyReconnect = true", close, StringComparison.Ordinal);
        Assert.Contains("coverCommandSent = false", close, StringComparison.Ordinal);
        Assert.Contains("does not match locked selection", close, StringComparison.Ordinal);
        Assert.DoesNotContain("拒绝向未知设备发命令并记录 warning", close, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(close, "flatDeviceMediator.Connect()"));
    }

    [Fact]
    public void UnsafeSafetyMonitorTripAbortsAtrAndStartsTerminalCleanupOnlyInFullMode()
    {
        var handler = MethodBody(
            "private void OnSafetyMonitorSafeChanged(",
            "private async Task<StageResult> ValidateNightSetupAsync(");

        Assert.Contains("configuration.Environment.WeakSupervisionEnabled", handler, StringComparison.Ordinal);
        Assert.Contains("cameraMediator.AbortExposure()", handler, StringComparison.Ordinal);
        Assert.Contains("allowMechanicalActions: true", handler, StringComparison.Ordinal);
        Assert.Contains("environmentSafetyShutdownStarted", handler, StringComparison.Ordinal);
        Assert.Contains("environment-safety-trip", handler, StringComparison.Ordinal);
    }

    private static string MethodBody(string startMarker, string endMarker)
    {
        var start = RunnerSource.IndexOf(startMarker, StringComparison.Ordinal);
        var end = RunnerSource.IndexOf(endMarker, start + 1, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"Could not isolate {startMarker}.");
        return RunnerSource[start..end];
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "UVEX-ADV.sln"))) return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root containing UVEX-ADV.sln was not found.");
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
