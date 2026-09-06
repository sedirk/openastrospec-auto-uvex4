namespace UvexAdv.Observatory;

/// <summary>Skipping a redundant target-field solve requires a measured target, not a prediction alone.</summary>
public static class G3PostWcsMeasuredHandoffPolicy
{
    public static bool CanHandOff(
        TargetIdentification identification,
        PixelPoint predictedTarget,
        double maximumUncertaintyPixels,
        PixelPoint slit,
        int width,
        int height,
        double maximumAcquisitionResidualPixels)
    {
        if (!double.IsFinite(maximumAcquisitionResidualPixels) || maximumAcquisitionResidualPixels <= 0 ||
            !G3PostWcsPredictionPolicy.Evaluate(predictedTarget, maximumUncertaintyPixels,
                slit, width, height, maximumAcquisitionResidualPixels).Authorized ||
            identification.Gate.Disposition != GateDisposition.Passed || identification.Target is not { } target)
            return false;

        var measured = identification.Authority == TargetIdentificationAuthority.StellarCentroid ||
            (identification.Authority == TargetIdentificationAuthority.BrightWingCentroid &&
             identification.Gate.Code == "TARGET_IDENTIFIED_SATURATED_TOPOLOGY");
        return measured && double.IsFinite(target.FluxAdu) && target.FluxAdu > 0 &&
            double.IsFinite(target.Centroid.X) && double.IsFinite(target.Centroid.Y) &&
            target.Centroid.X >= 0 && target.Centroid.X < width &&
            target.Centroid.Y >= 0 && target.Centroid.Y < height &&
            Distance(target.Centroid, predictedTarget) <= maximumAcquisitionResidualPixels &&
            Distance(target.Centroid, slit) <= maximumAcquisitionResidualPixels;
    }

    private static double Distance(PixelPoint a, PixelPoint b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
