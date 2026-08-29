using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3PostWcsPredictionPolicyTests
{
    [Fact]
    public void UnsolvedTargetFieldCanHandOffWhenPredictionAndRuntimeSlitAgree()
    {
        var result = G3PostWcsPredictionPolicy.Evaluate(
            new PixelPoint(818, 426),
            maximumUncertaintyPixels: 1.5,
            new PixelPoint(817.5, 425.5),
            imageWidth: 1920,
            imageHeight: 1080,
            maximumAcquisitionResidualPixels: 5);

        Assert.True(result.Authorized);
        Assert.Equal("G3_POST_WCS_PREDICTION_AUTHORIZED", result.Code);
        Assert.InRange(result.PredictedTargetToSlitResidualPixels, 0.70, 0.71);
    }

    [Theory]
    [InlineData(1920, 426, 1, "G3_POST_WCS_PREDICTION_OUTSIDE_FRAME")]
    [InlineData(818, 426, 6, "G3_POST_WCS_PREDICTION_UNCERTAINTY_EXCEEDED")]
    [InlineData(830, 426, 1, "G3_POST_WCS_PREDICTION_RESIDUAL_EXCEEDED")]
    public void RejectedPredictionCannotAuthorizeHandoff(
        double predictedX,
        double predictedY,
        double uncertainty,
        string expectedCode)
    {
        var result = G3PostWcsPredictionPolicy.Evaluate(
            new PixelPoint(predictedX, predictedY),
            uncertainty,
            new PixelPoint(817.5, 425.5),
            imageWidth: 1920,
            imageHeight: 1080,
            maximumAcquisitionResidualPixels: 5);

        Assert.False(result.Authorized);
        Assert.Equal(expectedCode, result.Code);
    }
}
