using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3PostWcsShortHandoffPolicyTests
{
    [Theory]
    // 2026-10-11 EW Lac / KY And. Original measured coordinates, not synthetic success.
    [InlineData(817.6258530793547, 421.36764123128467, 857.056768558952, 421.3467248908297,
        864.8535963793845, 421.00099087201284, 863.4796954393337, 418.8067845713103)]
    [InlineData(818.0176201486491, 421.64812084917145, 852.864312267658, 438.20631970260223,
        862.7510444697126, 437.0161240564056, 862.0911678166735, 438.0329312762361)]
    public void ConfirmedPairOutsideFineBudgetRequestsExistingImageSolveNotBlindFineMotion(
        double px, double py, double lx, double ly, double ax, double ay, double bx, double by)
    {
        var prediction = new PixelPoint(px, py);
        var a = Measured(ax, ay); var b = Measured(bx, by);
        var core = Long with { Target = Long.Target! with { Centroid = new(lx, ly) } };
        var decision = G3PostWcsShortHandoffPolicy.Evaluate(core, prediction, 5, prediction,
            1920, 1080, 20, a, b, Confirm(a, b), 2, false, 44, 100);
        Assert.False(decision.Accepted);
        Assert.False(decision.RecheckPair);
        Assert.True(G3PostWcsShortHandoffPolicy.NeedsSameFrameCoarseSolve(decision.Gate));
        Assert.Equal(44, decision.Gate.Metrics!["measuredHandoffRadiusPixels"]);
        Assert.False(G3PostWcsShortHandoffPolicy.NeedsSameFrameCoarseSolve(
            G3PostWcsShortHandoffPolicy.Evaluate(core, prediction, 5, prediction, 1920, 1080,
                20, a, b, Confirm(a, b, duplicate: true), 2, false, 44, 100).Gate));
        Assert.False(G3PostWcsShortHandoffPolicy.NeedsSameFrameCoarseSolve(
            G3PostWcsShortHandoffPolicy.Evaluate(core, prediction, 100, prediction, 1920, 1080,
                20, a, b, Confirm(a, b), 2, false, 44, 100).Gate));
    }

    [Theory]
    [InlineData(3, false, true)]
    [InlineData(4, false, true)]
    [InlineData(5, false, false)]
    [InlineData(3, true, false)]
    public void RecordedHd18552RemainingEnvelopeEdgeGetsOneNewPair(int attempt, bool used, bool recheck)
    {
        var prediction = new PixelPoint(813.6863138752175, 425.28150580976967);
        // The runtime slit, not the requested destination.
        var slit = new PixelPoint(817.5473147500325, 428.99509600458646);
        var a = Measured(805.2647986684011, 389.5057062838626);
        var b = Measured(808.6190229986954, 389.4140097965213);
        var core = Long with { Target = Long.Target! with { Centroid = new(802.6516129032258, 396.48602150537636) } };
        var confirmation = Confirm(a, b, attempt);
        var d = G3PostWcsShortHandoffPolicy.Evaluate(core, prediction, 5, slit,
            1920, 1080, 20, a, b, confirmation, attempt, used, 44, 100);
        Assert.Equal(3.3554774777561804, confirmation.PositionSpreadPixels, 9);
        Assert.False(d.Accepted);
        Assert.Equal(recheck, d.RecheckPair);
        Assert.Equal(recheck ? 1 : 0, d.Gate.Metrics!["measuredHandoffBoundaryRecheck"]);
        Assert.Equal(.710913659844065, d.Gate.Metrics["measuredHandoffBoundaryExcessPixels"], 9);
        // Synthetic FUTURE frames: no old anchor is carried to the new pair.
        var c = Measured(807.5, 390); var e = Measured(807.6, 390.1);
        Assert.False(G3PostWcsShortHandoffPolicy.Evaluate(core, prediction, 5, slit,
            1920, 1080, 20, null, c, Confirm(null, c, 4), 4, true, 44, 100).Accepted);
        Assert.True(G3PostWcsShortHandoffPolicy.Evaluate(core, prediction, 5, slit,
            1920, 1080, 20, c, e, Confirm(c, e, 5), 5, true, 44, 100).Accepted);
    }

    [Theory]
    [InlineData(40, 5, false)] // Centroids themselves exceed the remaining budget.
    [InlineData(44, 21, false)] // Prediction no longer bounded.
    [InlineData(44, 5, true)] // Same frame reused.
    [InlineData(double.NaN, 5, false)]
    public void RemainingEnvelopeRecheckDoesNotBypassBudgetPredictionOrIndependence(double radius, double uncertainty, bool duplicate)
    {
        var a = Measured(140, 100); var b = Measured(141, 102);
        var core = Long with { Target = Long.Target! with { Centroid = new(138, 101) } };
        var d = G3PostWcsShortHandoffPolicy.Evaluate(core, new(100, 100), uncertainty, new(100, 100),
            1920, 1080, 20, a, b, Confirm(a, b, 3, duplicate), 3, false, radius, 100);
        Assert.False(d.Accepted); Assert.False(d.RecheckPair);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CandidateOutsideOldWindowNeedsIndependentShortPairInsideExistingEnvelope(bool saturatedLongFrame)
    {
        var prediction = new PixelPoint(819.5, 428.3);
        var slit = new PixelPoint(817.473, 426.867);
        var core = Measured(857.49, 423.39).Identification;
        if (saturatedLongFrame) core = core with
        {
            Gate = GateResult.Pass("TARGET_IDENTIFIED_SATURATED_TOPOLOGY", "Long-frame candidate"),
            Authority = TargetIdentificationAuthority.BrightWingCentroid,
        };
        // Synthetic independent short measurements around the recorded long
        // core: this regression does not claim a real-sky identification.
        var a = Measured(858.0, 423.5); var b = Measured(858.7, 423.6);
        G3PostWcsShortHandoffDecision Check(G3ShortPositionMeasurement? prior, G3ShortPositionConfirmationDecision confirmation,
            double remaining = 44, double search = 100) => G3PostWcsShortHandoffPolicy.Evaluate(
                core, prediction, 5.3, slit, 1920, 1080, 20, prior, b, confirmation, 2, false, remaining, search);
        Assert.False(Check(null, Confirm(null, b)).Accepted);
        Assert.False(Check(a, Confirm(a, b, duplicate: true)).Accepted);
        var accepted = Check(a, Confirm(a, b));
        Assert.True(accepted.Accepted);
        Assert.Equal("G3_POST_WCS_MEASURED_FINE_HANDOFF", accepted.Gate.Code);
        Assert.Equal(GateSeverity.Warning, accepted.Gate.Severity);
        Assert.False(accepted.RecheckPair);
        Assert.False(Check(a, Confirm(a, b), remaining: 40).Accepted);
        Assert.False(Check(a, Confirm(a, b), search: 35).Accepted);
    }

    [Theory]
    [InlineData(44, true)]
    [InlineData(30.09649, false)] // 27.51217 residual + 2.58433 measured spread, not just centroid.
    [InlineData(20, false)]
    [InlineData(0, false)]
    [InlineData(double.NaN, false)]
    public void RecordedHd18552PairUsesExistingFineEnvelopeNotTwentyPixelRoutingFence(double radius, bool allowed)
    {
        var prediction = new PixelPoint(819.7242614425585, 429.93577831704033);
        var slit = new PixelPoint(817.5479697098012, 429.0138515984154);
        var core = Long with { Target = Long.Target! with { Centroid = new(833.4268104776579, 437.251155624037) } };
        var a = Measured(840.908371, 437.955628);
        var b = Measured(843.481082, 438.200420);
        var d = G3PostWcsShortHandoffPolicy.Evaluate(core, prediction, 5, slit,
            1920, 1080, 20, a, b, Confirm(a, b, 6), 6, false, radius);
        Assert.Equal(allowed, d.Accepted);
        Assert.False(d.RecheckPair); // No seventh/eighth image or budget reset.
        if (allowed) Assert.Equal(GateSeverity.Warning, d.Gate.Severity);
    }

    [Fact]
    public void MeasuredContinuationCannotWidenIdentityPredictionOrDuplicateFrameChecks()
    {
        var a = First; var b = Second;
        G3PostWcsShortHandoffDecision Check(TargetIdentification core, G3ShortPositionConfirmationDecision c, double uncertainty = 5) =>
            G3PostWcsShortHandoffPolicy.Evaluate(core, Prediction, uncertainty, Slit, 1920, 1080, 20,
                a, b, c, 6, false, 44);
        Assert.True(Check(Long, Confirm(a, b, 6)).Accepted);
        Assert.False(Check(Long, Confirm(a, b, 6, true)).Accepted);
        Assert.False(Check(Long, Confirm(a, b, 6), 21).Accepted);
        Assert.False(Check(Long with { Target = Long.Target! with { Centroid = new(850, 430) } }, Confirm(a, b, 6)).Accepted);
        Assert.False(Check(Long with { Gate = GateResult.Unknown("AMBIGUOUS", "") }, Confirm(a, b, 6)).Accepted);
    }
    [Fact]
    public void RecordedWr137PairBothJustOutsideGetsOnlyOneFreshPairNotHandoff()
    {
        var prediction = new PixelPoint(817.1791454472615, 425.19609459804724);
        // Runtime aperture centre, not the nominal commanded destination.
        var slit = new PixelPoint(817.5104725833686, 427.9400743885102);
        var a = Measured(834.1754482289446, 414.8478699285904);
        var b = Measured(834.3487016643921, 416.91039844965763);
        var core = Long with { Target = Long.Target! with { Centroid = new(830.9386973180077, 418.1570881226054) } };
        var repeat = Confirm(a, b, 3);
        var decision = G3PostWcsShortHandoffPolicy.Evaluate(core, prediction, 5, slit,
            1920, 1080, 20, a, b, repeat, 3, false);
        Assert.True(repeat.Accepted);
        Assert.Equal(2.069792417879191, repeat.PositionSpreadPixels, 9);
        Assert.False(decision.Accepted);
        Assert.True(decision.RecheckPair);
        Assert.Equal(G3PostWcsShortHandoffPolicy.RecheckCode, decision.Gate.Code);
        Assert.Equal(0, decision.Gate.Metrics!["priorHandoffGeometryPassed"]);
        Assert.Equal(0, decision.Gate.Metrics["currentHandoffGeometryPassed"]);
        Assert.Equal(1.19262208618801, decision.Gate.Metrics["handoffBoundaryExcessPixels"], 9);
        // A repeat still outside is NOT a pass and cannot request another pair.
        var exhausted = G3PostWcsShortHandoffPolicy.Evaluate(core, prediction, 5, slit,
            1920, 1080, 20, a, b, repeat, 5, true);
        Assert.False(exhausted.Accepted);
        Assert.False(exhausted.RecheckPair);
    }

    [Theory]
    [InlineData(2, false, true)]
    [InlineData(4, false, true)]
    [InlineData(5, false, false)]
    [InlineData(2, true, false)]
    public void BothOutsideRecheckStillHonorsOriginalFrameCapAndOneUse(int frames, bool used, bool expected)
    {
        // Synthetic geometry with both centres outside, but within repeat scatter.
        var a = Measured(120.1, 100); var b = Measured(121, 101);
        a = a with { Identification = a.Identification with { PredictedPoint = new(100, 100), PredictionResidualPixels = 20.1 } };
        b = b with { Identification = b.Identification with { PredictedPoint = new(100, 100), PredictionResidualPixels = Math.Sqrt(442) } };
        var core = Long with { Target = Long.Target! with { Centroid = new(118, 100) } };
        var decision = G3PostWcsShortHandoffPolicy.Evaluate(core, new(100, 100), 1,
            new(100, 100), 1920, 1080, 20, a, b, Confirm(a, b, frames), frames, used);
        Assert.False(decision.Accepted);
        Assert.Equal(expected, decision.RecheckPair);
    }

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
