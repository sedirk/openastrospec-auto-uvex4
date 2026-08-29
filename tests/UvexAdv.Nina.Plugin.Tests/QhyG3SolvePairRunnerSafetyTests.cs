using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class QhyG3SolvePairRunnerSafetyTests
{
    private static readonly string MainSource = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "Sources",
        "RealObservationStageRunner.cs"));

    private static readonly string PairSource = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "Sources",
        "RealObservationStageRunner.QhyG3SolvePair.cs"));

    [Fact]
    public void FeatureIsExplicitlyDisabledByDefaultAndActionConfigCapturesPolicy()
    {
        var parameter = typeof(G3RunConfiguration)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Single(item => item.Name == "FastSolvePair");

        Assert.True(parameter.HasDefaultValue);
        Assert.Null(parameter.DefaultValue);
        Assert.NotNull(typeof(UvexPluginSettings).GetProperty(nameof(UvexPluginSettings.QhyG3FastPairEnabled)));
        Assert.NotNull(typeof(G3RunConfiguration).GetProperty(nameof(G3RunConfiguration.FastSolvePair)));
        Assert.False(QhyG3FastPairPolicy.Disabled.Enabled);
    }

    [Fact]
    public void SuccessfulG3SolveTriggersPairBeforeAnyWcsCenteringDecision()
    {
        var wrapper = Slice(
            MainSource,
            "private async Task<G3FieldState> CaptureAndAnalyzeG3WithSolveLadderAsync(",
            "private async Task<G3PlateSolveProbeState> CaptureG3PlateSolveLadderAsync(");

        var validate = wrapper.IndexOf("ValidateG3ProbeMountBindingForMotionAsync", StringComparison.Ordinal);
        var pair = wrapper.IndexOf("TryCollectQhyG3FastSolvePairAsync", StringComparison.Ordinal);
        var targetOutside = wrapper.IndexOf("probe.Gate.Disposition", StringComparison.Ordinal);
        Assert.True(validate >= 0 && pair > validate && targetOutside > pair);
    }

    [Fact]
    public void FastPathReusesFreshQhyFirstAndFallbackIsExactlyOneExposureTier()
    {
        var cached = PairSource.IndexOf("TryPrepareCachedQhyPairSourceAsync", StringComparison.Ordinal);
        var immediate = PairSource.IndexOf("CaptureImmediateQhyPairSourceAsync", StringComparison.Ordinal);
        Assert.True(cached >= 0 && cached < immediate);
        Assert.Contains("new[] { policy.QuickQhyExposureSeconds }", PairSource, StringComparison.Ordinal);
        Assert.Contains("MaximumAttempts: 1", PairSource, StringComparison.Ordinal);
        Assert.Contains("did not plate-solve; no additional exposure tier is attempted", PairSource, StringComparison.Ordinal);
        Assert.Contains("QHY_G3_PAIR_DEADLINE_ALREADY_EXCEEDED", PairSource, StringComparison.Ordinal);
        Assert.Contains("no QHY exposure was started", PairSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PairingNeverCommandsMountAndCandidateCannotAuthorizeMotion()
    {
        var collector = Slice(
            PairSource,
            "private async Task TryCollectQhyG3FastSolvePairAsync(",
            "private static async Task<string> PersistQhyG3CandidateCalibrationAsync(");

        Assert.DoesNotContain("SlewToCoordinatesAsync", collector, StringComparison.Ordinal);
        Assert.DoesNotContain("telescopeMediator.Sync", collector, StringComparison.Ordinal);
        Assert.DoesNotContain("SetExactLockPositionAsync", collector, StringComparison.Ordinal);
        Assert.DoesNotContain("Pulse", collector, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mountMotionCommandCount = 0", collector, StringComparison.Ordinal);
        Assert.Contains("motionAuthority = false", collector, StringComparison.Ordinal);
        Assert.Contains("cannot authorize", collector, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GrossNoHomeCoordinateRecoveryIsOneSyncThenOneCatalogSlewWithFreshG3Required()
    {
        var recovery = Slice(
            PairSource,
            "private async Task<QhyMountCoordinateRecoveryResult> RecoverMountCoordinatesFromQhyWcsIfRequiredAsync(",
            "private async Task TryCollectQhyG3FastSolvePairAsync(");

        var intent = recovery.IndexOf("qhy-mount-coordinate-sync-intent", StringComparison.Ordinal);
        var oneShot = recovery.IndexOf("Interlocked.CompareExchange(ref qhyMountCoordinateSyncPerformed, 1, 0)", StringComparison.Ordinal);
        var epochConversion = recovery.IndexOf("qhyTruthJ2000.Transform(syncCommandReadback.Epoch)", StringComparison.Ordinal);
        var sync = recovery.IndexOf("telescopeMediator.Sync(qhySyncCommandCoordinates)", StringComparison.Ordinal);
        var catalogSlew = recovery.IndexOf("SlewToCoordinatesAsync(target", StringComparison.Ordinal);
        Assert.True(epochConversion >= 0 && intent > epochConversion && oneShot > intent && sync > oneShot && catalogSlew > sync);
        Assert.Contains("Interlocked.CompareExchange(ref qhyMountCoordinateSyncPerformed, 1, 0)", recovery, StringComparison.Ordinal);
        Assert.Contains("telescopeMediator.Sync(qhySyncCommandCoordinates)", recovery, StringComparison.Ordinal);
        Assert.DoesNotContain("telescopeMediator.Sync(qhyCoordinates)", recovery, StringComparison.Ordinal);
        Assert.Equal(sync, recovery.LastIndexOf("telescopeMediator.Sync(", StringComparison.Ordinal));
        Assert.Contains("qhyTruthJ2000 = new", recovery, StringComparison.Ordinal);
        Assert.Contains("qhySyncCommand = new", recovery, StringComparison.Ordinal);
        Assert.Contains("residualAfter > 5d", recovery, StringComparison.Ordinal);
        Assert.Contains("QHY_MOUNT_COORDINATE_SYNC_REPEAT_BLOCKED", recovery, StringComparison.Ordinal);
        Assert.Contains("SlewToCoordinatesAsync(target", recovery, StringComparison.Ordinal);
        Assert.Contains("opticalArrivalAuthority = \"next fresh G3 WCS\"", recovery, StringComparison.Ordinal);
    }

    [Fact]
    public void UnverifiedStationarySyncDegradesToFreshQhyHintWithoutRetryOrCatalogSlew()
    {
        var selection = Slice(
            PairSource,
            "private async Task<G3PlateSolveHintSelection> SelectG3PlateSolveHintAsync(",
            "private async Task<QhyMountCoordinateRecoveryResult> RecoverMountCoordinatesFromQhyWcsIfRequiredAsync(");
        var recovery = Slice(
            PairSource,
            "private async Task<QhyMountCoordinateRecoveryResult> RecoverMountCoordinatesFromQhyWcsIfRequiredAsync(",
            "private async Task TryCollectQhyG3FastSolvePairAsync(");
        var degraded = Slice(
            PairSource,
            "private async Task<QhyMountCoordinateRecoveryResult> ContinueWithFreshQhyHintAfterUnverifiedSyncAsync(",
            "private async Task<QhyPostSyncCatalogSlewResult> SlewToCatalogTargetAfterQhyCoordinateSyncAsync(");

        Assert.Contains("coordinateRecovery.SyncVerified", selection, StringComparison.Ordinal);
        Assert.Contains("FreshQhyPl3WcsAfterUnverifiedMountSync", selection, StringComparison.Ordinal);
        Assert.Contains("QHY_MOUNT_COORDINATE_SYNC_EXCEPTION", recovery, StringComparison.Ordinal);
        Assert.Contains("QHY_MOUNT_COORDINATE_SYNC_REJECTED", recovery, StringComparison.Ordinal);
        Assert.Contains("QHY_MOUNT_COORDINATE_SYNC_READBACK_FAILED", recovery, StringComparison.Ordinal);
        Assert.Contains("QHY_MOUNT_COORDINATE_SYNC_REPEAT_BLOCKED", recovery, StringComparison.Ordinal);
        Assert.Contains("ContinueWithFreshQhyHintAfterUnverifiedSyncAsync", recovery, StringComparison.Ordinal);
        Assert.Contains("additionalSyncAuthorized = false", degraded, StringComparison.Ordinal);
        Assert.Contains("catalogueSlewAuthorized = false", degraded, StringComparison.Ordinal);
        Assert.Contains("mountMotionAuthority = false", degraded, StringComparison.Ordinal);
        Assert.DoesNotContain("telescopeMediator.Sync", degraded, StringComparison.Ordinal);
        Assert.DoesNotContain("SlewToCoordinatesAsync", degraded, StringComparison.Ordinal);
        Assert.Contains("syncVerified = false", degraded, StringComparison.Ordinal);
    }

    [Fact]
    public void MachineLocalCandidateIndexFailureIsOnlyAWarning()
    {
        var collector = Slice(
            PairSource,
            "private async Task TryCollectQhyG3FastSolvePairAsync(",
            "private static async Task<string> PersistQhyG3CandidateCalibrationAsync(");

        Assert.Contains("qhy-g3-automatic-calibration-index-warning", collector, StringComparison.Ordinal);
        Assert.Contains("qhyG3AutomaticCalibrationWarning", collector, StringComparison.Ordinal);
        Assert.Contains("CandidateCreated", collector, StringComparison.Ordinal);
    }

    [Fact]
    public void PairRehashesBothFitsAndBothSolveRecordsAndBracketsBothExposures()
    {
        Assert.Contains("g3FrameSha", PairSource, StringComparison.Ordinal);
        Assert.Contains("g3SolveSha", PairSource, StringComparison.Ordinal);
        Assert.Contains("qhyFrameSha", PairSource, StringComparison.Ordinal);
        Assert.Contains("qhySolveSha", PairSource, StringComparison.Ordinal);
        Assert.Contains("g3-before-exposure", PairSource, StringComparison.Ordinal);
        Assert.Contains("g3-after-exposure", PairSource, StringComparison.Ordinal);
        Assert.Contains("qhy-before-job", PairSource, StringComparison.Ordinal);
        Assert.Contains("qhy-after-accepted-frame", PairSource, StringComparison.Ordinal);
        Assert.Contains("pair-final-readback", PairSource, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionalFailureKeepsDirectG3FallbackButUserCancellationEscapes()
    {
        Assert.Contains("catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)", PairSource, StringComparison.Ordinal);
        Assert.Contains("directG3FallbackContinues = true", PairSource, StringComparison.Ordinal);
        Assert.Contains("QHY_G3_PAIR_OPTIONAL_FAILURE", PairSource, StringComparison.Ordinal);
        Assert.Contains("CancelQhyFastPairJobBestEffortAsync", PairSource, StringComparison.Ordinal);
    }

    private static string Slice(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from);
        return source[from..to];
    }
}
