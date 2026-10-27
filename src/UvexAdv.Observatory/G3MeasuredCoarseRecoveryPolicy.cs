namespace UvexAdv.Observatory;

/// <summary>Image-only eligibility. Movement still belongs to the original coarse ledger.</summary>
public static class G3MeasuredCoarseRecoveryPolicy
{
    public const string Authority = "MeasuredLocalWcsJacobian";
    public const string ReadyCode = "G3_MEASURED_LOCAL_COARSE_READY";
    public const double MaximumRadiusPixels = 100;
    public const double MaximumCorrectionArcseconds = 40;

    public static bool AnchorUsable(bool formal, int attemptsUsed, double ageSeconds,
        double separationArcseconds, double pixelScale) => formal && attemptsUsed == 0 &&
        double.IsFinite(ageSeconds) && ageSeconds >= 0 && ageSeconds <= 120 &&
        double.IsFinite(separationArcseconds) && separationArcseconds >= 0 && separationArcseconds <= 3600 &&
        double.IsFinite(pixelScale) && pixelScale > 0;

    public static bool CanPropose(TargetIdentification candidate, PixelPoint prediction, double uncertainty,
        PixelPoint slit, int width, int height, double predictionRadius, double recognitionRadius) =>
        G3PostWcsMeasuredHandoffPolicy.CanProposeBoundedShortConfirmation(candidate, prediction,
            uncertainty, slit, width, height, predictionRadius,
            Math.Min(recognitionRadius, MaximumRadiusPixels), Math.Min(recognitionRadius, MaximumRadiusPixels));

    public static bool PairUsable(TargetIdentification longTarget, PixelPoint prediction, double uncertainty,
        PixelPoint slit, int width, int height, double predictionRadius, double recognitionRadius,
        double pixelScale, double remainingFineRadiusPixels, G3ShortPositionMeasurement? first, G3ShortPositionMeasurement second,
        G3ShortPositionConfirmationDecision confirmation)
    {
        if (!CanPropose(longTarget, prediction, uncertainty, slit, width, height, predictionRadius, recognitionRadius) ||
            !confirmation.Accepted || confirmation.RepeatGate?.Disposition != GateDisposition.Passed ||
            first?.Gate.Disposition != GateDisposition.Passed || second.Gate.Disposition != GateDisposition.Passed ||
            !double.IsFinite(pixelScale) || pixelScale <= 0 ||
            !double.IsFinite(remainingFineRadiusPixels) || remainingFineRadiusPixels <= 0 ||
            !double.IsFinite(confirmation.PositionSpreadPixels) ||
            confirmation.PositionSpreadPixels < 0 || confirmation.PositionSpreadPixels > G3ShortPositionMeasurementPolicy.MaximumRepeatSeparationPixels)
            return false;
        var radius = Math.Min(recognitionRadius, MaximumRadiusPixels);
        return Valid(first.Identification) && Valid(second.Identification);

        bool Valid(TargetIdentification id) => id.Authority == TargetIdentificationAuthority.StellarCentroid &&
            G3PostWcsMeasuredHandoffPolicy.HasMeasuredSource(id, width, height) &&
            Distance(id.Target!.Centroid, longTarget.Target!.Centroid) <= predictionRadius &&
            Distance(id.Target.Centroid, prediction) + confirmation.PositionSpreadPixels <= radius &&
            // This authority requires a real coarse move and a fresh destination image.
            // Never use it to bypass a failed fine-handoff identity/geometry check in place.
            Distance(id.Target.Centroid, slit) + confirmation.PositionSpreadPixels > remainingFineRadiusPixels &&
            Distance(id.Target.Centroid, slit) + confirmation.PositionSpreadPixels <= radius &&
            (Distance(id.Target.Centroid, slit) + confirmation.PositionSpreadPixels) * pixelScale <= MaximumCorrectionArcseconds;
    }

    private static double Distance(PixelPoint a, PixelPoint b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
