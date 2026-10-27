namespace UvexAdv.Observatory;

/// <summary>Skipping a redundant target-field solve requires a measured target, not a prediction alone.</summary>
public static class G3PostWcsMeasuredHandoffPolicy
{
    public const string OutsideRemainingCode = "G3_POST_WCS_MEASURED_HANDOFF_OUTSIDE";

    // An identifiable source outside the fine-motion envelope must stay on the
    // coarse route. Do not discard it after failed solve tiers and hand a stale
    // prediction to PHD2. This keeps the original bounded solve ladder, not a move or a
    // claim that a clipped component has independent short-frame identity.
    public static bool RequiresCoarseSolve(TargetIdentification identification,
        PixelPoint predictedTarget, double maximumUncertaintyPixels, PixelPoint slit,
        int width, int height, double predictionRadiusPixels, double targetSearchRadiusPixels,
        double remainingHandoffRadiusPixels) =>
        double.IsFinite(targetSearchRadiusPixels) && targetSearchRadiusPixels > 0 &&
        double.IsFinite(remainingHandoffRadiusPixels) && remainingHandoffRadiusPixels > 0 &&
        G3PostWcsPredictionPolicy.Evaluate(predictedTarget, maximumUncertaintyPixels,
            slit, width, height, predictionRadiusPixels).Authorized &&
        HasMeasuredSource(identification, width, height) &&
        Distance(identification.Target!.Centroid, predictedTarget) <= targetSearchRadiusPixels &&
        !FitsRemainingHandoff(identification, slit, remainingHandoffRadiusPixels);

    public static GateResult OutsideRemainingGate(TargetIdentification identification,
        PixelPoint slit, double remainingRadiusPixels) =>
        GateResult.TargetQuality(OutsideRemainingCode,
            "A fresh source lies outside the remaining fine-motion envelope. Try the existing frame and remaining bounded acquisition slots for the original charged coarse route; if unavailable, retain the candidate and verify return before any remaining origin recovery or target skip. A clipped candidate is not confirmed identity or authority to move.",
            new Dictionary<string, double>
            {
                ["measuredTargetResidualPixels"] = identification.Target is { } target ? Distance(target.Centroid, slit) : double.NaN,
                ["remainingHandoffRadiusPixels"] = remainingRadiusPixels,
            });

    // The recognition window and the remaining round-trip motion envelope are
    // independent. A valid centroid does not replenish the inherited ledger.
    public static bool FitsRemainingHandoff(TargetIdentification identification, PixelPoint slit,
        double remainingRadiusPixels) =>
        identification.Gate.Disposition == GateDisposition.Passed && identification.Target is { } target &&
        double.IsFinite(remainingRadiusPixels) && remainingRadiusPixels > 0 &&
        double.IsFinite(identification.CatalogPositionSpreadPixels) && identification.CatalogPositionSpreadPixels >= 0 &&
        Distance(target.Centroid, slit) + identification.CatalogPositionSpreadPixels <= remainingRadiusPixels;

    /// <summary>
    /// Admit a candidate to an image-only confirmation, not to guiding or science.
    /// The neighbour prediction keeps its original uncertainty/destination fence;
    /// the measured candidate may be farther away if the configured recognition
    /// area and the remaining return-reserved PHD2 envelope both contain it.
    /// </summary>
    public static bool CanProposeBoundedShortConfirmation(
        TargetIdentification identification, PixelPoint predictedTarget, double maximumUncertaintyPixels,
        PixelPoint slit, int width, int height, double predictionRadiusPixels,
        double targetSearchRadiusPixels, double remainingHandoffRadiusPixels) =>
        double.IsFinite(targetSearchRadiusPixels) && targetSearchRadiusPixels > 0 &&
        double.IsFinite(remainingHandoffRadiusPixels) && remainingHandoffRadiusPixels > 0 &&
        G3PostWcsPredictionPolicy.Evaluate(predictedTarget, maximumUncertaintyPixels,
            slit, width, height, predictionRadiusPixels).Authorized &&
        HasMeasuredSource(identification, width, height) &&
        Distance(identification.Target!.Centroid, predictedTarget) <= targetSearchRadiusPixels &&
        Distance(identification.Target.Centroid, slit) <= remainingHandoffRadiusPixels;

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
        return HasMeasuredSource(identification, width, height) &&
            Distance(target.Centroid, predictedTarget) <= maximumAcquisitionResidualPixels &&
            Distance(target.Centroid, slit) <= maximumAcquisitionResidualPixels;
    }

    internal static bool HasMeasuredSource(TargetIdentification identification, int width, int height)
    {
        if (identification.Gate.Disposition != GateDisposition.Passed || identification.Target is not { } target)
            return false;
        var measured = (identification.Authority == TargetIdentificationAuthority.StellarCentroid &&
                        double.IsFinite(target.SaturatedFraction) && target.SaturatedFraction == 0) ||
            (identification.Authority == TargetIdentificationAuthority.BrightWingCentroid &&
             identification.Gate.Code == "TARGET_IDENTIFIED_SATURATED_TOPOLOGY");
        return measured && double.IsFinite(target.FluxAdu) && target.FluxAdu > 0 &&
            double.IsFinite(target.Centroid.X) && double.IsFinite(target.Centroid.Y) &&
            target.Centroid.X >= 0 && target.Centroid.X < width &&
            target.Centroid.Y >= 0 && target.Centroid.Y < height;
    }

    private static double Distance(PixelPoint a, PixelPoint b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
