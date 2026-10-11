using System.Globalization;
using UvexAdv.Nina.Plugin;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3PostWcsShortHandoffPresentationTests
{
    [Fact]
    public void RemainingEnvelopeAndScatterHaveDistinctVisibleLabels()
    {
        var text = ObservationUiPresentation.FormatMetrics(new Dictionary<string, double> {
            ["measuredHandoffRadiusPixels"] = 44, ["measuredHandoffRequiredRadiusPixels"] = 44.7109,
            ["measuredHandoffBoundaryExcessPixels"] = .7109 }, CultureInfo.GetCultureInfo("zh-CN"));
        Assert.Contains("剩余预算", text);
        Assert.Contains("含两帧分散", text);
        Assert.Contains("超出剩余精调", text);
    }

    [Fact]
    public void ProductionRecheckRequiresWhollyNewPairWithoutNewMotionOrBudget()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.ShortHandoff.cs"));
        var start = source.IndexOf("if (decision.RecheckPair)", StringComparison.Ordinal);
        var end = source.IndexOf("if (!confirmation.RetryAllowed", start, StringComparison.Ordinal);
        var branch = source[start..end];
        Assert.Contains("boundaryRecheckUsed = true", branch);
        Assert.Contains("maximumFrames = attempt + 2", branch);
        Assert.Contains("previous = null", branch);
        Assert.Contains("previousHash = previousPath = null", branch);
        Assert.Contains("continue;", branch);
        Assert.Contains("boundaryRecheckUsed ? policy", source);
        Assert.Contains("ValidateG3ProbeMountBindingForMotionAsync(context, longProbe", source);
        Assert.Contains("ValidateG3ProbeMountBindingForMotionAsync(context, probe", source);
        Assert.Contains("RequireImmediatePhysicalActionGatesAsync", source);
        Assert.Contains("if (accepted) return latest", source);
        Assert.DoesNotContain("SlewTo", source);
        Assert.DoesNotContain("SetExactLock", source);
        Assert.DoesNotContain("SolveImageAsync", source);
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public void OutsideGeometryIsNotMisreportedAsUnconfirmedPairOrRetryAuthority(string language)
    {
        var culture = CultureInfo.GetCultureInfo(language);
        var gate = GateResult.Unknown(G3PostWcsShortHandoffPolicy.OutsideCode,
            "Independent short positions agree, but do not fit the original handoff window.",
            new Dictionary<string, double> { ["handoffPredictionResidualPixels"] = 20.1936, ["maximumHandoffResidualPixels"] = 20 });
        var p = ObservationUiPresentation.Present(ObservationStage.AcquireG3SlitField, gate, culture);
        Assert.False(ObservationAutomaticRecoveryPolicy.For(ObservationStage.AcquireG3SlitField, gate).IsRecoverable);
        if (language == "zh-CN") { Assert.Contains("一致性已通过", p.Summary); Assert.Contains("原交接半径", p.AutomaticRecovery); }
        else Assert.False(ObservationUiPresentation.ContainsCjk(p.Summary + p.Recommendation));
        Assert.Contains("20.1936", ObservationUiPresentation.FormatMetrics(gate.Metrics, culture));
    }
}
