namespace UvexAdv.Observatory;

/// <summary>Skipping a redundant target-field solve requires a measured target, not a prediction alone.</summary>
public static class G3PostWcsMeasuredHandoffPolicy
{
    public static bool NeedsShortExposureConfirmation(TargetIdentification identification) =>
        identification.Authority == TargetIdentificationAuthority.BrightWingCentroid &&
        identification.Gate.Code == "TARGET_IDENTIFIED_SATURATED_TOPOLOGY";

    public static bool CanHandOff(
        TargetIdentification identification, PixelPoint predictedTarget, double maximumUncertaintyPixels,
        PixelPoint slit, int width, int height, double maximumAcquisitionResidualPixels,
        TargetIdentification? independentShortExposure = null)
    {
        if (!CanProposeHandoff(identification, predictedTarget, maximumUncertaintyPixels,
            slit, width, height, maximumAcquisitionResidualPixels)) return false;
        if (!NeedsShortExposureConfirmation(identification)) return true;
        // A filled clipped component can merge a star and its optical ghost.
        // Its apparent centre alone is insufficient even when it is very close
        // to the prediction. The caller must bind an independent, shorter
        // exposure to the same unchanged mount/owner before passing it here.
        return independentShortExposure is not null &&
            independentShortExposure.Authority == TargetIdentificationAuthority.StellarCentroid &&
            CanProposeHandoff(independentShortExposure, predictedTarget, maximumUncertaintyPixels,
                slit, width, height, maximumAcquisitionResidualPixels) &&
            Distance(identification.Target!.Centroid, independentShortExposure.Target!.Centroid) <= maximumAcquisitionResidualPixels;
    }

    public static bool CanProposeHandoff(
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

        // An ordinary detector can segment a clipped halo/diffraction feature
        // close to the prediction while the real saturated core is farther
        // away. Only explicit filled-core topology may authorize a clipped
        // source; otherwise keep the normal formal-solve route.
        var measured = (identification.Authority == TargetIdentificationAuthority.StellarCentroid &&
                        double.IsFinite(target.SaturatedFraction) && target.SaturatedFraction == 0) ||
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
