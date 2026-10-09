using System.Globalization;
using UvexAdv.Nina.Plugin;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3PostWcsShortHandoffPresentationTests
{
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
