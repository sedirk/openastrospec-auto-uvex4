namespace UvexAdv.Observatory;

/// <summary>
/// Evaluates the physical plausibility of a plate solver's formal solution.
/// Source and catalogue-match counts are deliberately telemetry rather than a
/// second project-level acceptance threshold.  The optical-axis envelope is
/// independent of the much smaller post-solve mount-correction budget.
/// </summary>
public static class G3PlateSolveTrustPolicy
{
    public static GateResult Evaluate(
        bool formalSuccess,
        bool hasCoordinates,
        double measuredPixelScaleArcseconds,
        double expectedPixelScaleArcseconds,
        double positionAngleDegrees,
        double hintResidualArcseconds,
        int imageWidth,
        int imageHeight,
        double maximumOpticalAxisOffsetDegrees,
        double minimumScaleRatio = 0.70,
        double maximumScaleRatio = 1.30)
    {
        if (!formalSuccess || !hasCoordinates)
        {
            return GateResult.Unknown(
                "G3_PLATE_SOLVE_FORMAL_SOLUTION_UNAVAILABLE",
                "A formal plate-solver success with coordinates is required.");
        }

        var scaleRatio = measuredPixelScaleArcseconds / expectedPixelScaleArcseconds;
        var halfDiagonalArcseconds = 0.5d * Math.Sqrt(
            imageWidth * (double)imageWidth + imageHeight * (double)imageHeight) * measuredPixelScaleArcseconds;
        var maximumHintResidualArcseconds = maximumOpticalAxisOffsetDegrees * 3600d + halfDiagonalArcseconds;
        var metrics = new Dictionary<string, double>
        {
            ["g3SolveHintResidualArcseconds"] = hintResidualArcseconds,
            ["g3SolveMaximumHintResidualArcseconds"] = maximumHintResidualArcseconds,
            ["g3SolveMaximumOpticalAxisOffsetDegrees"] = maximumOpticalAxisOffsetDegrees,
            ["g3SolveExpectedPixelScaleArcseconds"] = expectedPixelScaleArcseconds,
            ["g3SolveMeasuredPixelScaleArcseconds"] = measuredPixelScaleArcseconds,
            ["g3SolvePixelScaleRatio"] = scaleRatio,
        };

        if (!double.IsFinite(measuredPixelScaleArcseconds) ||
            measuredPixelScaleArcseconds <= 0 ||
            !double.IsFinite(expectedPixelScaleArcseconds) ||
            expectedPixelScaleArcseconds <= 0 ||
            !double.IsFinite(positionAngleDegrees) ||
            !double.IsFinite(hintResidualArcseconds) ||
            hintResidualArcseconds < 0 ||
            imageWidth <= 0 ||
            imageHeight <= 0 ||
            !double.IsFinite(maximumOpticalAxisOffsetDegrees) ||
            maximumOpticalAxisOffsetDegrees <= 0 ||
            !double.IsFinite(scaleRatio) ||
            scaleRatio < minimumScaleRatio ||
            scaleRatio > maximumScaleRatio ||
            hintResidualArcseconds > maximumHintResidualArcseconds)
        {
            return GateResult.Fail(
                "G3_PLATE_SOLVE_PLAUSIBILITY_REJECTED",
                $"The formal solution is outside the physical camera/optical-axis envelope: hint residual {hintResidualArcseconds:F1} arcsec (maximum {maximumHintResidualArcseconds:F1}), scale ratio {scaleRatio:F3}.",
                metrics);
        }

        return GateResult.Pass(
            "G3_PLATE_SOLVE_FORMAL_SUCCESS_TRUSTED",
            $"The solver's formal solution is trusted inside the independent {maximumOpticalAxisOffsetDegrees:F2} degree optical-axis envelope; source and match counts remain telemetry (hint residual {hintResidualArcseconds:F1} arcsec, scale ratio {scaleRatio:F3}).",
            metrics);
    }
}
