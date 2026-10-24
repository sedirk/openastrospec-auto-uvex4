using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public class G3MotionPredictionTimingPolicyTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-23T12:00:00Z");
    private static G3SparseExposureTiming Frame(double exposure = 2, double bracket = 3, double solve = 6) =>
        new(Start, Start.AddSeconds(bracket), exposure, Start.AddSeconds(solve));

    [Fact]
    public void UnknownDriftIsOnlyASearchPriorAndAlwaysNeedsIndependentNewPixels()
    {
        var result = G3MotionPredictionTimingPolicy.Evaluate(Frame(), Start.AddSeconds(20), 3, 100);
        Assert.True(result.SearchHintUsable);
        Assert.False(result.CurrentPositionUsable);
        Assert.True(result.RequiresIndependentTargetPair);
        Assert.False(result.MotionAuthority);
        Assert.Equal(0, result.Gate.Metrics!["driftWasMeasured"]);
    }

    [Fact]
    public void ExposureAndProcessingAgeIncreaseMeasuredUncertaintyWithoutResettingTimestamp()
    {
        var drift = new G3SparseDriftBound(.4, "independent-image-pair");
        var a = G3MotionPredictionTimingPolicy.Evaluate(Frame(), Start.AddSeconds(12), 3, 100, drift);
        var b = G3MotionPredictionTimingPolicy.Evaluate(Frame(), Start.AddSeconds(22), 3, 100, drift);
        Assert.True(a.CurrentPositionUsable);
        Assert.True(b.CurrentPositionUsable);
        Assert.Equal(4, b.UncertaintyPixels - a.UncertaintyPixels, 8);
        Assert.Equal(3, b.Gate.Metrics!["sourceSolveLatencySeconds"]);
    }

    [Fact]
    public void LongerShutterSmearIsNotCancelledByUsingItsLaterNominalMidpoint()
    {
        // Same source earliest midpoint and processing time: integration grows.
        var shortFrame = new G3SparseExposureTiming(Start.AddSeconds(4), Start.AddSeconds(7), 2, Start.AddSeconds(12));
        var longFrame = new G3SparseExposureTiming(Start, Start.AddSeconds(11), 10, Start.AddSeconds(12));
        var drift = new G3SparseDriftBound(.5, "measured");
        var a = G3MotionPredictionTimingPolicy.Evaluate(shortFrame, Start.AddSeconds(20), 2, 100, drift);
        var b = G3MotionPredictionTimingPolicy.Evaluate(longFrame, Start.AddSeconds(20), 2, 100, drift);
        Assert.Equal(2, b.UncertaintyPixels - a.UncertaintyPixels, 8);
    }

    [Fact]
    public void OldButBoundedSourceMaySuggestFreshPairButCannotClaimCurrentPosition()
    {
        var result = G3MotionPredictionTimingPolicy.Evaluate(Frame(), Start.AddSeconds(70), 3, 100,
            new G3SparseDriftBound(.1, "measured"));
        Assert.True(result.SearchHintUsable);
        Assert.False(result.CurrentPositionUsable);
    }

    [Fact]
    public void SourceExpiryCannotBeRefreshedByLateSolveCompletionOrStableMountCoordinates()
    {
        var result = G3MotionPredictionTimingPolicy.Evaluate(Frame(solve: 125), Start.AddSeconds(126), 3, 100);
        Assert.False(result.SearchHintUsable);
        Assert.Equal("G3_NEIGHBOUR_PREDICTION_STALE", result.Gate.Code);
    }

    [Theory]
    [InlineData(-1, "measurement")]
    [InlineData(double.NaN, "measurement")]
    [InlineData(.1, "")]
    public void InvalidDriftCannotBeUsed(double rate, string receipt)
    {
        var result = G3MotionPredictionTimingPolicy.Evaluate(Frame(), Start.AddSeconds(20), 3, 100,
            new G3SparseDriftBound(rate, receipt));
        Assert.False(result.SearchHintUsable);
    }

    [Fact]
    public void ExistingRecognitionEnvelopeIsNotEnlargedToFitDrift()
    {
        var result = G3MotionPredictionTimingPolicy.Evaluate(Frame(), Start.AddSeconds(20), 3, 10,
            new G3SparseDriftBound(1, "measured"));
        Assert.False(result.SearchHintUsable);
        Assert.Equal("G3_NEIGHBOUR_SEARCH_ENVELOPE_EXHAUSTED", result.Gate.Code);
    }

    [Fact]
    public void FutureSolveAndClockReversalAreNotFreshEvidence()
    {
        Assert.False(G3MotionPredictionTimingPolicy.Evaluate(Frame(), Start.AddSeconds(2), 3, 100).SearchHintUsable);
        Assert.False(G3MotionPredictionTimingPolicy.Evaluate(Frame() with { CaptureCompletedUtc = Start.AddSeconds(-1) },
            Start.AddSeconds(20), 3, 100).SearchHintUsable);
    }

    [Fact]
    public void AbsentTimingOnlyWithdrawsOptionalRouteRatherThanEndingNight()
    {
        var result = G3MotionPredictionTimingPolicy.Evaluate(null, Start.AddSeconds(20), 3, 100);
        Assert.False(result.SearchHintUsable);
        Assert.Equal(GateSeverity.Warning, result.Gate.Severity);
    }
}
