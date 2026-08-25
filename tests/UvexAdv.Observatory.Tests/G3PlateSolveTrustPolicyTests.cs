using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3PlateSolveTrustPolicyTests
{
    [Fact]
    public void TrustsFormalPlateSolve3ResultAcrossIndependentOpticalAxes()
    {
        var gate = G3PlateSolveTrustPolicy.Evaluate(
            formalSuccess: true,
            hasCoordinates: true,
            measuredPixelScaleArcseconds: 0.38254,
            expectedPixelScaleArcseconds: 0.383,
            positionAngleDegrees: 252.1,
            hintResidualArcseconds: 3677.39,
            imageWidth: 1920,
            imageHeight: 1080,
            maximumOpticalAxisOffsetDegrees: 5);

        Assert.Equal(GateDisposition.Passed, gate.Disposition);
        Assert.Equal("G3_PLATE_SOLVE_FORMAL_SUCCESS_TRUSTED", gate.Code);
        Assert.Contains("source and match counts remain telemetry", gate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DoesNotConfuseSmallCorrectionBudgetWithOpticalAxisEnvelope()
    {
        var gate = G3PlateSolveTrustPolicy.Evaluate(
            formalSuccess: true,
            hasCoordinates: true,
            measuredPixelScaleArcseconds: 0.382,
            expectedPixelScaleArcseconds: 0.383,
            positionAngleDegrees: 252,
            hintResidualArcseconds: 4_000,
            imageWidth: 1920,
            imageHeight: 1080,
            maximumOpticalAxisOffsetDegrees: 5);

        Assert.Equal(GateDisposition.Passed, gate.Disposition);
        Assert.True(gate.Metrics!["g3SolveMaximumHintResidualArcseconds"] > 18_000);
    }

    [Fact]
    public void RejectsOnlyAFormalResultOutsidePhysicalEnvelope()
    {
        var gate = G3PlateSolveTrustPolicy.Evaluate(
            formalSuccess: true,
            hasCoordinates: true,
            measuredPixelScaleArcseconds: 0.382,
            expectedPixelScaleArcseconds: 0.383,
            positionAngleDegrees: 252,
            hintResidualArcseconds: 25_000,
            imageWidth: 1920,
            imageHeight: 1080,
            maximumOpticalAxisOffsetDegrees: 5);

        Assert.Equal(GateDisposition.Failed, gate.Disposition);
        Assert.Equal("G3_PLATE_SOLVE_PLAUSIBILITY_REJECTED", gate.Code);
    }

    [Theory]
    [InlineData(double.NaN, 0.383)]
    [InlineData(0.382, 0)]
    [InlineData(0.382, double.PositiveInfinity)]
    public void RejectsNonPhysicalScaleInputs(double measured, double expected)
    {
        var gate = G3PlateSolveTrustPolicy.Evaluate(
            formalSuccess: true,
            hasCoordinates: true,
            measuredPixelScaleArcseconds: measured,
            expectedPixelScaleArcseconds: expected,
            positionAngleDegrees: 252,
            hintResidualArcseconds: 100,
            imageWidth: 1920,
            imageHeight: 1080,
            maximumOpticalAxisOffsetDegrees: 5);

        Assert.Equal(GateDisposition.Failed, gate.Disposition);
    }
}
