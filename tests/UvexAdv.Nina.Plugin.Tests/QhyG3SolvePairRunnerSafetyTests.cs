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
        Assert.DoesNotContain("SlewToCoordinatesAsync", PairSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SetExactLockPositionAsync", PairSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Pulse", PairSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mountMotionCommandCount = 0", PairSource, StringComparison.Ordinal);
        Assert.Contains("motionAuthority = false", PairSource, StringComparison.Ordinal);
        Assert.Contains("cannot authorize", PairSource, StringComparison.OrdinalIgnoreCase);
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
