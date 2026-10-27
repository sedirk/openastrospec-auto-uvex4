using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3MeasuredCoarseRecoveryPolicyTests
{
    private static readonly PixelPoint Prediction = new(818.2895, 430.0253);
    private static readonly PixelPoint Slit = new(817.6039, 430.6153);
    private static G3ShortPositionMeasurement Position(double x, double y) => new(
        GateResult.Pass("G3_SEP_POSITION_MEASURED", "Synthetic independent compact parent, not historical sky."),
        new(GateResult.Pass("G3_SEP_POSITION_MEASURED", ""),
            new(new(x, y), 10000, 300000, 100, 4, .1, 0, 30), Prediction, 89, 1), true, .5);
    private static TargetIdentification Long => Position(843.313, 344.988).Identification with
    {
        Gate = GateResult.Pass("TARGET_IDENTIFIED_SATURATED_TOPOLOGY", "Recorded long-core geometry"),
        Authority = TargetIdentificationAuthority.BrightWingCentroid,
    };
    private static G3ShortPositionConfirmationDecision Confirm(G3ShortPositionMeasurement? a,
        G3ShortPositionMeasurement b, bool duplicate = false) => G3ShortPositionMeasurementPolicy.EvaluateConfirmation(
            a, b, a is null ? null : new('a', 64), new(duplicate ? 'a' : 'b', 64), 2, 100, 6);

    [Fact]
    public void RecordedLongCandidateAdmitsIndependentImagesButNotFineMotion()
    {
        Assert.True(G3MeasuredCoarseRecoveryPolicy.CanPropose(Long, Prediction, 6, Slit, 1920, 1080, 20, 100));
        Assert.False(G3PostWcsMeasuredHandoffPolicy.CanProposeBoundedShortConfirmation(Long, Prediction, 6,
            Slit, 1920, 1080, 20, 100, 44));
        var a = Position(843, 345); var b = Position(843.6, 345.4);
        Assert.True(Eligible(a, b, Confirm(a, b)));
        Assert.False(G3PostWcsShortHandoffPolicy.Evaluate(Long, Prediction, 6, Slit, 1920, 1080, 20,
            a, b, Confirm(a, b), 2, false, 44, 100).Accepted);
    }

    [Theory]
    [InlineData("missing")] [InlineData("duplicate")] [InlineData("disagree")]
    [InlineData("multilobe")] [InlineData("ambiguous")] [InlineData("saturated")]
    [InlineData("other-component")] [InlineData("outside")] [InlineData("nan")]
    public void BadPairNeverObtainsLocalCorrection(string mode)
    {
        G3ShortPositionMeasurement? a = Position(843, 345); var b = Position(843.6, 345.4);
        if (mode == "missing") a = null;
        if (mode == "disagree") b = Position(850, 345);
        if (mode == "other-component") { a = Position(844, 370); b = Position(844.2, 370.1); }
        if (mode == "outside") { a = Position(900, 345); b = Position(900.2, 345.1); }
        if (mode == "nan") b = Position(double.NaN, 345);
        if (mode is "multilobe" or "ambiguous") b = b with { Gate = GateResult.Unknown(mode, "Not an accepted identity") };
        if (mode == "saturated") b = b with { Identification = b.Identification with
            { Target = b.Identification.Target! with { SaturatedFraction = .1 } } };
        Assert.False(Eligible(a, b, Confirm(a, b, mode == "duplicate")));
    }

    [Fact]
    public void LocalModelCannotBypassFineHandoffByAcceptingInPlace()
    {
        var a = Position(843, 345); var b = Position(843.6, 345.4);
        Assert.False(G3MeasuredCoarseRecoveryPolicy.PairUsable(Long, Prediction, 6, Slit,
            1920, 1080, 20, 100, .3821204176623478, 100, a, b, Confirm(a, b)));
    }

    [Theory]
    [InlineData(true, 0, 40, 960, .382, true)]
    [InlineData(false, 0, 40, 960, .382, false)]
    [InlineData(true, 1, 40, 960, .382, false)]
    [InlineData(true, 0, 121, 960, .382, false)]
    [InlineData(true, 0, -1, 960, .382, false)]
    [InlineData(true, 0, 40, 3601, .382, false)]
    [InlineData(true, 0, 40, 960, double.NaN, false)]
    public void AnchorCannotBeInferredRepeatedStaleFutureOrFarAway(bool formal, int used,
        double age, double separation, double scale, bool expected) =>
        Assert.Equal(expected, G3MeasuredCoarseRecoveryPolicy.AnchorUsable(formal, used, age, separation, scale));

    private static bool Eligible(G3ShortPositionMeasurement? a, G3ShortPositionMeasurement b,
        G3ShortPositionConfirmationDecision c) => G3MeasuredCoarseRecoveryPolicy.PairUsable(Long,
            Prediction, 6, Slit, 1920, 1080, 20, 100, .3821204176623478, 44, a, b, c);
}
