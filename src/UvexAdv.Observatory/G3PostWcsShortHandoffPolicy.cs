namespace UvexAdv.Observatory;

public sealed record G3PostWcsShortHandoffDecision(GateResult Gate, bool Accepted, bool RecheckPair);

/// <summary>One image-only recheck for a confirmed pair straddling the unchanged handoff boundary.</summary>
public static class G3PostWcsShortHandoffPolicy
{
    public const string OutsideCode = "G3_POST_WCS_SHORT_HANDOFF_OUTSIDE";
    public const string RecheckCode = "G3_POST_WCS_SHORT_BOUNDARY_RECHECK";

    public static G3PostWcsShortHandoffDecision Evaluate(
        TargetIdentification longTarget, PixelPoint prediction, double uncertainty,
        PixelPoint slit, int width, int height, double radius,
        G3ShortPositionMeasurement? previous, G3ShortPositionMeasurement current,
        G3ShortPositionConfirmationDecision confirmation, int attempt, bool recheckUsed)
    {
        var metrics = new Dictionary<string, double>();
        if (confirmation.Gate.Metrics is { } input)
            foreach (var item in input) if (double.IsFinite(item.Value)) metrics[item.Key] = item.Value;
        void Metric(string key, double value) { if (double.IsFinite(value)) metrics[key] = value; }
        double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        double Residual(TargetIdentification? t, PixelPoint point) => t?.Target is { } star ? Distance(star.Centroid, point) : double.NaN;
        var prior = previous?.Identification;
        var actual = current.Identification;
        var priorPrediction = Residual(prior, prediction);
        var priorSlit = Residual(prior, slit);
        var currentPrediction = Residual(actual, prediction);
        var currentSlit = Residual(actual, slit);
        Metric("handoffPredictionResidualPixels", currentPrediction);
        Metric("handoffSlitResidualPixels", currentSlit);
        Metric("priorHandoffPredictionResidualPixels", priorPrediction);
        Metric("priorHandoffSlitResidualPixels", priorSlit);
        Metric("maximumHandoffResidualPixels", radius);
        metrics["shortPositionFrames"] = attempt;
        metrics["handoffBoundaryRecheckUsed"] = recheckUsed ? 1 : 0;
        var currentGeometry = G3PostWcsMeasuredHandoffPolicy.CanHandOff(longTarget, prediction,
            uncertainty, slit, width, height, radius, actual);
        var priorGeometry = prior is not null && G3PostWcsMeasuredHandoffPolicy.CanHandOff(longTarget,
            prediction, uncertainty, slit, width, height, radius, prior);
        metrics["shortPairConfirmed"] = confirmation.Accepted ? 1 : 0;
        metrics["currentHandoffGeometryPassed"] = currentGeometry ? 1 : 0;
        metrics["priorHandoffGeometryPassed"] = priorGeometry ? 1 : 0;
        if (!confirmation.Accepted)
            return new(GateResult.Unknown("G3_POST_WCS_SHORT_UNCONFIRMED",
                $"Independent short-frame confirmation is incomplete: {confirmation.Gate.Code}: {confirmation.Gate.Message}", metrics), false, false);
        if (currentGeometry && priorGeometry)
            return new(GateResult.Pass("G3_POST_WCS_SHORT_HANDOFF_CONFIRMED",
                "Both independent short positions fit the unchanged prediction and slit windows; fresh PHD2 fine evidence is still required.", metrics), true, false);

        // Scatter authorizes only two more images, never a larger acceptance
        // window. Keep source validity, long/short identity and original prediction gates.
        bool ValidIdentity(TargetIdentification? t) => t is not null &&
            t.Authority == TargetIdentificationAuthority.StellarCentroid &&
            G3PostWcsMeasuredHandoffPolicy.HasMeasuredSource(t, width, height) &&
            longTarget.Target is { } core && Distance(core.Centroid, t.Target!.Centroid) <= radius;
        var spread = confirmation.PositionSpreadPixels;
        var worst = Math.Max(Math.Max(priorPrediction, priorSlit), Math.Max(currentPrediction, currentSlit));
        var recheck = !recheckUsed && attempt >= 2 && attempt <= G3PostWcsShortExposurePolicy.MaximumFrames - 2 &&
            confirmation.RepeatGate?.Disposition == GateDisposition.Passed &&
            G3PostWcsMeasuredHandoffPolicy.NeedsShortExposureConfirmation(longTarget) &&
            G3PostWcsMeasuredHandoffPolicy.CanProposeHandoff(longTarget, prediction, uncertainty, slit, width, height, radius) &&
            previous?.Gate.Disposition == GateDisposition.Passed && current.Gate.Disposition == GateDisposition.Passed &&
            ValidIdentity(prior) && ValidIdentity(actual) && (priorGeometry || currentGeometry) &&
            double.IsFinite(spread) && spread > 0 && spread <= G3ShortPositionMeasurementPolicy.MaximumRepeatSeparationPixels &&
            double.IsFinite(worst) && worst > radius && worst - radius <= spread;
        return new(GateResult.Unknown(recheck ? RecheckCode : OutsideCode,
            recheck
                ? "The independent pair agrees but straddles the original handoff boundary. Reserve exactly one new pair inside the original six-frame limit; no move, solve, tolerance or budget reset."
                : "Independent short positions agree, but the pair does not fit the unchanged prediction/slit handoff windows. No PHD2 handoff is authorized.", metrics), false, recheck);
    }
}
