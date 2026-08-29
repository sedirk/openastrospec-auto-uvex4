namespace UvexAdv.Observatory;

/// <summary>
/// Pure acceptance policy for a fresh but unsolved target-field frame reached
/// from a formally solved overlapping G3 field. It evaluates the same detector
/// geometry whether the slit was measured now or reused from the unchanged
/// run-scoped cache.
/// </summary>
public sealed record G3PostWcsPredictionAssessment(
    bool Authorized,
    bool PredictionInsideFrame,
    double PredictedTargetToSlitResidualPixels,
    string Code,
    string Message);

public static class G3PostWcsPredictionPolicy
{
    public static G3PostWcsPredictionAssessment Evaluate(
        PixelPoint predictedTarget,
        double maximumUncertaintyPixels,
        PixelPoint runtimeSlit,
        int imageWidth,
        int imageHeight,
        double maximumAcquisitionResidualPixels)
    {
        if (imageWidth <= 0 || imageHeight <= 0 ||
            !double.IsFinite(maximumAcquisitionResidualPixels) ||
            maximumAcquisitionResidualPixels <= 0)
        {
            return Rejected(false, double.NaN, "G3_POST_WCS_PREDICTION_POLICY_INVALID",
                "Post-WCS prediction dimensions or the commissioned acquisition window are invalid.");
        }

        var inside = double.IsFinite(predictedTarget.X) &&
            double.IsFinite(predictedTarget.Y) &&
            predictedTarget.X >= 0 && predictedTarget.X < imageWidth &&
            predictedTarget.Y >= 0 && predictedTarget.Y < imageHeight;
        var residual = inside && double.IsFinite(runtimeSlit.X) && double.IsFinite(runtimeSlit.Y)
            ? Math.Sqrt(
                Math.Pow(predictedTarget.X - runtimeSlit.X, 2) +
                Math.Pow(predictedTarget.Y - runtimeSlit.Y, 2))
            : double.NaN;

        if (!inside)
        {
            return Rejected(false, residual, "G3_POST_WCS_PREDICTION_OUTSIDE_FRAME",
                "The preceding PL3/mount prediction lies outside the fresh target frame.");
        }
        if (!double.IsFinite(maximumUncertaintyPixels) || maximumUncertaintyPixels < 0 ||
            maximumUncertaintyPixels > maximumAcquisitionResidualPixels)
        {
            return Rejected(true, residual, "G3_POST_WCS_PREDICTION_UNCERTAINTY_EXCEEDED",
                "The preceding PL3/mount prediction uncertainty exceeds the commissioned acquisition window.");
        }
        if (!double.IsFinite(residual) || residual > maximumAcquisitionResidualPixels)
        {
            return Rejected(true, residual, "G3_POST_WCS_PREDICTION_RESIDUAL_EXCEEDED",
                "The predicted target is outside the commissioned PHD2 acquisition window around the runtime slit.");
        }

        return new G3PostWcsPredictionAssessment(
            true,
            true,
            residual,
            "G3_POST_WCS_PREDICTION_AUTHORIZED",
            "The fresh target frame, preceding formal PL3/mount prediction and runtime slit geometry authorize PHD2 hand-off without another target-field solve.");
    }

    private static G3PostWcsPredictionAssessment Rejected(
        bool inside,
        double residual,
        string code,
        string message) => new(false, inside, residual, code, message);
}
