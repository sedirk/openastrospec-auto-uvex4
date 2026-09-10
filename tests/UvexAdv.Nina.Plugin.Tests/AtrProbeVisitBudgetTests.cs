using UvexAdv.Spectroscopy;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class AtrProbeVisitBudgetTests
{
    [Fact]
    public void AlmachSixtyToOneTwentyBackoffGetsOneFreshSixtySecondValidation()
    {
        var tiers = new List<double> { 0.1, 0.3, 60, 120 };
        var budget = new AtrProbeVisitBudget(tiers);
        Assert.True(budget.TryBeginProbe(0.1, false));
        Assert.True(budget.TryBeginProbe(0.3, false));
        Assert.True(budget.TryBeginProbe(60, false));
        var first = Probe(60, 16016, "original-sixty");
        var up = ExposureTierSelector.Select(first, new ExposureTierOptions(tiers));
        Assert.True(up.Accepted);
        Assert.Equal(120, up.SelectedExposureSeconds);
        Assert.True(budget.TryBeginProbe(up.SelectedExposureSeconds, false));
        var bright = Probe(120, 56448, "bright-one-twenty");
        var down = ExposureTierSelector.Select(bright, new ExposureTierOptions(tiers));
        Assert.Equal("SATURATION_BACKOFF", down.Code);
        Assert.Equal(60, down.SelectedExposureSeconds);
        tiers.RemoveAll(value => value >= bright.ExposureSeconds);
        Assert.True(budget.TryBeginProbe(down.SelectedExposureSeconds, true));
        var fresh = Probe(60, 20000, "fresh-sixty");
        var verified = ExposureTierSelector.Select(fresh, new ExposureTierOptions(tiers));
        Assert.True(verified.Accepted);
        Assert.Equal(fresh.ExposureSeconds, verified.SelectedExposureSeconds);
        Assert.NotEqual(first.SourceFrameId, fresh.SourceFrameId);
        Assert.Equal(5, budget.AttemptCount);
        Assert.Equal(budget.MaximumAttempts, budget.AttemptCount);
        Assert.Equal(1, budget.BackoffReprobes);
        Assert.False(budget.TryBeginProbe(0.3, true));
    }

    [Fact]
    public void OrdinaryRevisitOrAnUpwardRevisitIsNotGranted()
    {
        var budget = new AtrProbeVisitBudget([30, 60, 120]);
        Assert.True(budget.TryBeginProbe(60, false));
        Assert.True(budget.TryBeginProbe(120, false));
        Assert.False(budget.TryBeginProbe(60, false));
        Assert.True(budget.TryBeginProbe(30, true));
        Assert.False(budget.TryBeginProbe(60, true));
        Assert.Equal(0, budget.BackoffReprobes);
    }

    [Fact]
    public void SecondRepeatedBackoffIsRejectedEvenWithRemainingConfiguredTiers()
    {
        var budget = new AtrProbeVisitBudget([0.1, 0.3, 1, 30, 60, 120]);
        Assert.True(budget.TryBeginProbe(30, false));
        Assert.True(budget.TryBeginProbe(60, false));
        Assert.True(budget.TryBeginProbe(120, false));
        Assert.True(budget.TryBeginProbe(60, true));
        Assert.False(budget.TryBeginProbe(30, true));
        Assert.False(budget.TryBeginProbe(90, true));
        Assert.Equal(4, budget.AttemptCount);
    }

    [Fact]
    public void ReprobePermissionDoesNotTurnClippedOrInvalidFreshMetricsIntoSuccess()
    {
        var options = new ExposureTierOptions([0.1, 0.3, 60]);
        var clipped = ExposureTierSelector.Select(Probe(60, 64000, "fresh-clipped"), options);
        Assert.Equal("SATURATION_BACKOFF", clipped.Code);
        Assert.NotEqual(60, clipped.SelectedExposureSeconds);
        var invalid = ExposureTierSelector.Select(Probe(60, double.NaN, "fresh-invalid"), options);
        Assert.False(invalid.Accepted);
    }

    [Fact]
    public void RunnerStillPrunesBrightTiersAndRequiresFreshCaptureBeforeAcceptance()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var start = source.IndexOf("private async Task<StageResult> SelectAtrExposureAsync(", StringComparison.Ordinal);
        var end = source.IndexOf("private static bool IsSafeSignalLimitedAtrProbe(", start, StringComparison.Ordinal);
        var method = source[start..end];
        Assert.Contains("var visitBudget = new AtrProbeVisitBudget(availableTiers)", method);
        Assert.Contains("visitBudget.AttemptCount < visitBudget.MaximumAttempts", method);
        Assert.Contains("availableTiers.RemoveAll(value => value >= probeExposure);\n                followsSaturationBackoff = true;", method.Replace("\r\n", "\n"));
        Assert.True(method.IndexOf("TryBeginProbe", StringComparison.Ordinal) < method.IndexOf("await CaptureAtrImageAsync", StringComparison.Ordinal));
        Assert.True(method.IndexOf("await CaptureAtrImageAsync", StringComparison.Ordinal) < method.IndexOf("selectedTierValidatedByThisFrame = decision.Accepted", StringComparison.Ordinal));
        Assert.Contains("Math.Abs(decision.SelectedExposureSeconds - probeExposure) < 1e-9", method);
    }

    private static SpectralProbeMetrics Probe(double exposure, double peak, string frame) =>
        new(exposure, 272, peak, 65535, 0, 417, 660, 37, true, frame);
}
