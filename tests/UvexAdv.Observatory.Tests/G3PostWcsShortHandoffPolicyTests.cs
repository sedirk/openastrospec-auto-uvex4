using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3PostWcsShortHandoffPolicyTests
{
    // Immutable 2026-10-06 Mirach measurements, not generated observing products.
    private static readonly PixelPoint Prediction = new(819.9692644269775, 433.11545792884345);
    private static readonly PixelPoint Slit = new(817.6416459762652, 431.69638889147524);
    private static G3ShortPositionMeasurement Measured(double x, double y) => new(
        GateResult.Pass("G3_SEP_POSITION_MEASURED", "Whole unsaturated parent"),
        new(GateResult.Pass("G3_SEP_POSITION_MEASURED", ""),
            new(new(x, y), 23840, 1036314, 1270, 0, .28, 0, 415), Prediction,
            Math.Sqrt(Math.Pow(x - Prediction.X, 2) + Math.Pow(y - Prediction.Y, 2)), 2), true, .5);
    private static TargetIdentification Long => Measured(809.1049818259568, 419.2820183878555).Identification with
    {
        Gate = GateResult.Pass("TARGET_IDENTIFIED_SATURATED_TOPOLOGY", "Unique filled core"),
        Authority = TargetIdentificationAuthority.BrightWingCentroid,
    };
    private static G3ShortPositionMeasurement First => Measured(811.5641643367921, 417.90948313495494);
    private static G3ShortPositionMeasurement Second => Measured(809.9960819265875, 415.5564593357438);
    private static G3ShortPositionConfirmationDecision Confirm(G3ShortPositionMeasurement? a, G3ShortPositionMeasurement b,
        int attempt = 2, bool duplicate = false) => G3ShortPositionMeasurementPolicy.EvaluateConfirmation(
        a, b, a is null ? null : new('a', 64), new(duplicate ? 'a' : 'b', 64), attempt, 100, 6);
    private static G3PostWcsShortHandoffDecision Decide(G3ShortPositionMeasurement? a, G3ShortPositionMeasurement b,
        int attempt = 2, bool used = false, G3ShortPositionConfirmationDecision? confirmation = null,
        double uncertainty = 5) => G3PostWcsShortHandoffPolicy.Evaluate(Long, Prediction, uncertainty, Slit,
        1920, 1080, 20, a, b, confirmation ?? Confirm(a, b, attempt), attempt, used);

    [Fact]
    public void RecordedPairPassedRepeatButStraddledPredictionBoundaryCanOnlyRequestNewPair()
    {
        var repeat = Confirm(First, Second);
        Assert.True(repeat.Accepted);
        Assert.Equal(2.827649809443735, repeat.PositionSpreadPixels, 9);
        Assert.False(G3PostWcsMeasuredHandoffPolicy.CanHandOff(Long, Prediction, 5, Slit, 1920, 1080, 20, Second.Identification));
        var decision = Decide(First, Second);
        Assert.False(decision.Accepted);
        Assert.True(decision.RecheckPair);
        Assert.Equal(G3PostWcsShortHandoffPolicy.RecheckCode, decision.Gate.Code);
        Assert.Equal(20.193632679103583, decision.Gate.Metrics!["handoffPredictionResidualPixels"], 9);
        Assert.True(decision.Gate.Metrics["handoffSlitResidualPixels"] < 20);
        Assert.Equal(20, decision.Gate.Metrics["maximumHandoffResidualPixels"]);
    }

    [Fact]
    public void FreshPairMustStartWithoutOldAnchorAndBothFitOriginalWindows()
    {
        // Synthetic future frames; cannot prove unseen real-sky success.
        var a = Measured(812, 419); var b = Measured(812.3, 418.8);
        Assert.False(Decide(null, a, 3, true).Accepted);
        var decision = Decide(a, b, 4, true);
        Assert.True(decision.Accepted);
        Assert.False(decision.RecheckPair);
        Assert.Equal(20, decision.Gate.Metrics!["maximumHandoffResidualPixels"]);
    }

    [Fact]
    public void LatestInsideCannotMaskOutsideMemberOfNewPair()
    {
        var decision = Decide(Second, First, 4, true);
        Assert.False(decision.Accepted);
        Assert.False(decision.RecheckPair);
        Assert.Equal(G3PostWcsShortHandoffPolicy.OutsideCode, decision.Gate.Code);
        Assert.Equal(1, decision.Gate.Metrics!["shortPairConfirmed"]);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    public void NeverExtendsSixFramesOrReissuesBoundaryRecheck(int frames, bool used)
    {
        var d = Decide(First, Second, frames, used);
        Assert.False(d.Accepted);
        Assert.False(d.RecheckPair);
    }

    [Fact]
    public void LastAvailablePairCanStillBeReservedAtFrameFour() => Assert.True(Decide(First, Second, 4).RecheckPair);

    [Fact]
    public void ReusedFrameNeverAuthorizesRecheckOrHandoff()
    {
        var d = Decide(First, Second, confirmation: Confirm(First, Second, duplicate: true));
        Assert.False(d.Accepted); Assert.False(d.RecheckPair);
        Assert.Contains("G3_SHORT_FRAME_REUSED", d.Gate.Message);
    }

    [Theory]
    [InlineData(805, 411)] // Well outside; pair disagrees.
    [InlineData(809.5, 414.5)] // Both centres outside original window.
    [InlineData(double.NaN, 417)]
    public void DisagreementOrBothOutsideOrInvalidPositionDoesNotGetBoundaryRetry(double x, double y)
    {
        var d = Decide(Second, Measured(x, y));
        Assert.False(d.Accepted); Assert.False(d.RecheckPair);
        Assert.All(d.Gate.Metrics!.Values, v => Assert.True(double.IsFinite(v)));
    }

    [Theory]
    [InlineData(21)]
    [InlineData(double.NaN)]
    public void StaleOrInvalidPredictionCannotGainRetry(double uncertainty) =>
        Assert.False(Decide(First, Second, uncertainty: uncertainty).RecheckPair);

    [Fact]
    public void BadShortIdentityOrSaturationCannotGainRetry()
    {
        var star = Second.Identification.Target!;
        foreach (var id in new[] {
            Second.Identification with { Authority = TargetIdentificationAuthority.BrightWingCentroid },
            Second.Identification with { Gate = GateResult.Unknown("AMBIGUOUS", "") },
            Second.Identification with { Target = star with { SaturatedFraction = .01 } },
            Second.Identification with { Target = star with { FluxAdu = 0 } },
        })
        {
            var d = Decide(First, Second with { Identification = id });
            Assert.False(d.Accepted); Assert.False(d.RecheckPair);
        }
    }
}
