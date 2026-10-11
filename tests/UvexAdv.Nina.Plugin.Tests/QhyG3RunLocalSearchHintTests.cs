using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class QhyG3RunLocalSearchHintTests
{
    [Fact]
    public void SameRunPairPredictsCorrectSignedG3CenterAcrossRaWrapButGrantsNoAuthority()
    {
        var candidate = Candidate();
        var hint = QhyG3RunLocalSearchHint.TryProject(candidate, candidate.Qhy,
            Binding(candidate), 1, candidate.CreatedUtc, out var reason);
        Assert.NotNull(hint);
        Assert.Equal("QHY_G3_RUN_LOCAL_SEARCH_HINT", reason);
        Assert.InRange(hint.RightAscensionDegrees, 0.000999, 0.001001);
        Assert.InRange(hint.DeclinationDegrees, 30.000999, 30.001001);
        Assert.False(hint.MotionAuthority);
        Assert.False(hint.MeasuredTarget);
        Assert.False(candidate.MotionAuthority);
        Assert.Equal(QhyToG3TransferLifecycle.Candidate, candidate.Lifecycle);
    }

    [Fact]
    public void FreshNearbyQhyFieldUsesSameRunOffsetNotOldQhyCenterOrReversePreposition()
    {
        var candidate = Candidate();
        var current = candidate.Qhy with { CenterRightAscensionDegrees = 0.02 };
        var hint = QhyG3RunLocalSearchHint.TryProject(candidate, current,
            Binding(candidate), 1, candidate.CreatedUtc, out _);
        Assert.NotNull(hint);
        Assert.InRange(hint.RightAscensionDegrees, 0.021999, 0.022001);
        Assert.InRange(hint.DistanceFromPairArcseconds, 65, 66);
        Assert.Equal(candidate.Model.PredictionUncertaintyArcseconds, hint.SourcePairUncertaintyArcseconds);
    }

    [Theory]
    [InlineData("other-run")] [InlineData("action")] [InlineData("preset")]
    [InlineData("night")] [InlineData("installation")] [InlineData("pier")]
    [InlineData("camera")] [InlineData("g3-binning")] [InlineData("geometry")]
    [InlineData("parity")] [InlineData("rotation")] [InlineData("scale")]
    [InlineData("stale")] [InlineData("future")] [InlineData("far-sky")]
    [InlineData("invalid-radius")] [InlineData("hash")] [InlineData("motion-authority")]
    public void InapplicablePairReturnsFallbackWithoutBlockingOrInventingIdentity(string fault)
    {
        var candidate = Candidate();
        var binding = Binding(candidate);
        var current = candidate.Qhy;
        var now = candidate.CreatedUtc;
        var radius = 1d;
        switch (fault)
        {
            case "other-run": binding = binding with { RunId = "other" }; break;
            case "action": binding = binding with { ActionSha256 = Hash('F') }; break;
            case "preset": binding = binding with { PresetSha256 = Hash('F') }; break;
            case "night": binding = binding with { NightSetupId = "other" }; break;
            case "installation": binding = binding with { InstallationEpochId = "other" }; break;
            case "pier": binding = binding with { PierSide = "pierWest" }; break;
            case "camera": binding = binding with { QhyCameraId = "other" }; break;
            case "g3-binning": binding = binding with { G3Binning = 2 }; break;
            case "geometry": current = current with { RoiOriginX = 1 }; break;
            case "parity": current = current with { Flipped = true }; break;
            case "rotation": current = current with { PositionAngleDegrees = 180 }; break;
            case "scale": current = current with { PixelScaleArcseconds = 4.2 }; break;
            case "stale": now += TimeSpan.FromMinutes(1); break;
            case "future": current = current with { ExposureMidpointUtc = now.AddSeconds(1) }; break;
            case "far-sky": current = current with { CenterRightAscensionDegrees = 2 }; break;
            case "invalid-radius": radius = double.NaN; break;
            case "hash": candidate = candidate with { CandidateSha256 = Hash('F') }; break;
            case "motion-authority": candidate = candidate with { MotionAuthority = true }; break;
        }
        Assert.Null(QhyG3RunLocalSearchHint.TryProject(candidate, current, binding, radius, now, out var reason));
        Assert.StartsWith("QHY_G3_SEARCH_HINT_", reason);
    }

    [Fact]
    public void ProductionHintComesAfterFreshQhyCheckAndCannotStartNewExposureOrMove()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.QhyG3SolvePair.cs"));
        var selection = source[..source.IndexOf("private async Task<QhyG3RunLocalSearchHint?> TryApplyRunLocalPairSearchHintAsync", StringComparison.Ordinal)];
        Assert.True(selection.IndexOf("ValidateQhyAcceptedFrameMountBindingForMotionAsync", StringComparison.Ordinal) <
            selection.IndexOf("TryApplyRunLocalPairSearchHintAsync(context", StringComparison.Ordinal));
        Assert.Contains("!coordinateRecovery.SyncCommanded", selection);
        Assert.Contains("FreshQhyWcsWithRunLocalPairedG3SearchHintOnly", selection);
        var start = source.IndexOf("private async Task<QhyG3RunLocalSearchHint?> TryApplyRunLocalPairSearchHintAsync", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task<QhyMountCoordinateRecoveryResult> RecoverMountCoordinatesFromQhyWcsIfRequiredAsync", start, StringComparison.Ordinal);
        var method = source[start..end];
        Assert.DoesNotContain("SlewToCoordinates", method);
        Assert.DoesNotContain("telescopeMediator.Sync", method);
        Assert.DoesNotContain("StartOrAdoptAcquisition", method);
        Assert.DoesNotContain("File.ReadAllText", method);
        Assert.Contains("source.SolveEvidenceSha256", method);
        Assert.Contains("fallbackToOriginalHint = true", method);
        Assert.Contains("OperationCanceledException", method);
    }

    private static QhyG3SearchHintBinding Binding(QhyToG3TransferCandidate candidate) => new(
        candidate.ObservationRunId, candidate.ActionConfigurationSha256, candidate.CommissioningPresetSha256,
        candidate.NightSetupId, candidate.NightSetupSha256, candidate.InstallationEpochId,
        candidate.TelescopeDeviceId, candidate.QhyCameraStableId, candidate.G3CameraStableId, candidate.PierSide, 1);

    private static QhyToG3TransferCandidate Candidate()
    {
        var t0 = new DateTimeOffset(2026, 10, 10, 17, 0, 0, TimeSpan.Zero);
        var readbacks = new[]
        {
            new QhyG3PairMountReadback("g3-before-exposure", 359.5, 30, "JNOW", "pierEast", t0),
            new QhyG3PairMountReadback("g3-after-exposure", 359.5, 30, "JNOW", "pierEast", t0.AddSeconds(2)),
            new QhyG3PairMountReadback("qhy-before-job", 359.5, 30, "JNOW", "pierEast", t0.AddSeconds(3)),
            new QhyG3PairMountReadback("qhy-after-accepted-frame", 359.5, 30, "JNOW", "pierEast", t0.AddSeconds(5)),
            new QhyG3PairMountReadback("pair-final-readback", 359.5, 30, "JNOW", "pierEast", t0.AddSeconds(6)),
        };
        var request = new QhyG3SolvePairBuildRequest(
            new(1, "fast-pair", true, 2, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20),
                TimeSpan.FromSeconds(30), 2, TimeSpan.FromHours(1), 10),
            "run", Hash('A'), Hash('B'), "night", Hash('C'), "installation", "mount", "GS350/QHY", "C11/G3",
            QhyG3SolvePairSource.ReusedFreshQhySolve,
            new("QHY/GS350", "qhy", "qhy.fit", Hash('D'), "qhy-solve.json", Hash('E'),
                t0.AddSeconds(3), t0.AddSeconds(4), t0.AddSeconds(5), t0.AddSeconds(5.5), "QHY manifest",
                3056, 2048, 1, 1, 0, 0, 3056, 2048, 359.999, 30, 2.1, 5, false, Hash('F')),
            new("PHD2/G3/C11", "g3", "g3.fit", Hash('1'), "g3-solve.json", Hash('2'),
                t0, t0.AddSeconds(1), t0.AddSeconds(2), t0.AddSeconds(2.5), "PHD2 frame",
                1920, 1080, 1, 1, 0, 0, 1920, 1080, 0.001, 30.001, 0.38, -10, false, Hash('3')),
            readbacks, t0.AddSeconds(6));
        return Assert.IsType<QhyToG3TransferCandidate>(QhyG3SolvePairBuilder.Build(request).Candidate);
    }

    private static string Hash(char value) => new(value, 64);
}
