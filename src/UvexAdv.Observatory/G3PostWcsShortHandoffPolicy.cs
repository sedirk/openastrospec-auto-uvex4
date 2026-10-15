namespace UvexAdv.Observatory;

public sealed record G3PostWcsShortHandoffDecision(GateResult Gate, bool Accepted, bool RecheckPair);

/// <summary>One image-only recheck for a confirmed pair near the unchanged handoff boundary.</summary>
public static class G3PostWcsShortHandoffPolicy
{
    public const string OutsideCode = "G3_POST_WCS_SHORT_HANDOFF_OUTSIDE";
    public const string RecheckCode = "G3_POST_WCS_SHORT_BOUNDARY_RECHECK";

    // Routing only. A repeat-confirmed star outside the remaining fine-motion
    // envelope is not an invalid field. Try a single formal solve of the ALREADY
    // captured field before the existing charged coarse loop decides any move.
    public static bool NeedsSameFrameCoarseSolve(GateResult gate) =>
        gate.Code == OutsideCode && gate.Disposition != GateDisposition.Passed &&
        gate.Metrics?.GetValueOrDefault("confirmedPairCoarseSolveEligible") == 1;

    public static G3PostWcsShortHandoffDecision Evaluate(
        TargetIdentification longTarget, PixelPoint prediction, double uncertainty,
        PixelPoint slit, int width, int height, double radius,
        G3ShortPositionMeasurement? previous, G3ShortPositionMeasurement current,
        G3ShortPositionConfirmationDecision confirmation, int attempt, bool recheckUsed,
        double measuredHandoffRadius = 0, double candidateSearchRadius = 0)
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
        {
            // A remaining envelope can be SMALLER than the original recognition
            // window after guiding recovery. Do not bypass it via the old fast path.
            if (measuredHandoffRadius != 0 &&
                (!double.IsFinite(measuredHandoffRadius) || measuredHandoffRadius < 0 ||
                 Math.Max(priorSlit, currentSlit) + confirmation.PositionSpreadPixels > measuredHandoffRadius))
            {
                Metric("measuredHandoffRadiusPixels", measuredHandoffRadius);
                metrics["confirmedPairCoarseSolveEligible"] =
                    double.IsFinite(measuredHandoffRadius) && measuredHandoffRadius > 0 ? 1 : 0;
                return new(GateResult.Unknown(OutsideCode,
                    "The independent pair agrees inside the recognition window, but exceeds the inherited remaining fine-motion envelope. Reuse the existing field for one coarse solve; no new budget or blind search.", metrics), false, false);
            }
            return new(GateResult.Pass("G3_POST_WCS_SHORT_HANDOFF_CONFIRMED",
                "Both independent short positions fit the unchanged prediction and slit windows; fresh PHD2 fine evidence is still required.", metrics), true, false);
        }

        // Scatter authorizes only two more images, never a larger acceptance
        // window. Keep source validity, long/short identity and original prediction gates.
        bool ValidIdentity(TargetIdentification? t) => t is not null &&
            t.Authority == TargetIdentificationAuthority.StellarCentroid &&
            G3PostWcsMeasuredHandoffPolicy.HasMeasuredSource(t, width, height) &&
            longTarget.Target is { } core && Distance(core.Centroid, t.Target!.Centroid) <= radius;
        var spread = confirmation.PositionSpreadPixels;
        var worst = Math.Max(Math.Max(priorPrediction, priorSlit), Math.Max(currentPrediction, currentSlit));
        Metric("handoffBoundaryExcessPixels", Math.Max(0, worst - radius));
        Metric("handoffBoundaryMeasuredSpreadPixels", spread);
        // The narrow prediction window is not the available fine-motion budget.
        // Keep its predicted destination/uncertainty fence; an explicitly supplied
        // recognition radius may admit a farther measured long-frame candidate
        // to two independent short confirmations in the EXISTING round-trip envelope.
        // The caller deducts spent motion/time and the live planner must still
        // preflight the complete correction before issuing any lock movement.
        Metric("measuredHandoffRadiusPixels", measuredHandoffRadius);
        var recognitionRadius = candidateSearchRadius > 0 ? candidateSearchRadius : radius;
        var confirmedSearchRadius = candidateSearchRadius > 0 ? candidateSearchRadius : measuredHandoffRadius;
        Metric("candidateSearchRadiusPixels", recognitionRadius);
        var validMeasuredPair = double.IsFinite(measuredHandoffRadius) && measuredHandoffRadius > radius &&
            double.IsFinite(candidateSearchRadius) && candidateSearchRadius >= 0 &&
            confirmation.RepeatGate?.Disposition == GateDisposition.Passed &&
            previous?.Gate.Disposition == GateDisposition.Passed && current.Gate.Disposition == GateDisposition.Passed &&
            G3PostWcsMeasuredHandoffPolicy.CanProposeBoundedShortConfirmation(longTarget,
                prediction, uncertainty, slit, width, height, radius, recognitionRadius, measuredHandoffRadius) &&
            ValidIdentity(prior) && ValidIdentity(actual) &&
            double.IsFinite(spread) && spread >= 0 && spread <= G3ShortPositionMeasurementPolicy.MaximumRepeatSeparationPixels &&
            priorPrediction + spread <= confirmedSearchRadius && currentPrediction + spread <= confirmedSearchRadius &&
            double.IsFinite(worst);
        metrics["confirmedPairCoarseSolveEligible"] = validMeasuredPair &&
            worst + spread > measuredHandoffRadius ? 1 : 0;
        Metric("measuredHandoffRequiredRadiusPixels", worst + spread);
        Metric("measuredHandoffBoundaryExcessPixels", Math.Max(0, worst + spread - measuredHandoffRadius));
        if (validMeasuredPair && worst + spread <= measuredHandoffRadius)
            return new(GateResult.Warn("G3_POST_WCS_MEASURED_FINE_HANDOFF",
                "Independent measured positions exceed the prediction window but fit the remaining bounded fine-correction envelope, including repeat scatter. Continue to fresh PHD2 measurements and full correction/return preflight; slit arrival and science are not yet authorized.", metrics), true, false);
        // A pair can lie just outside on BOTH frames (WR 137: 21.19/20.13 px,
        // 20 px radius, 2.07 px repeat scatter). Requiring one inside before
        // permitting an image-only recheck discarded this valid boundary case.
        // The excess must still fit the measured scatter; the new pair must
        // satisfy the unchanged CanHandOff tests above, never radius + spread.
        // HD 18552: centroids fit the remaining 44 px envelope, but including
        // 3.355 px repeat scatter needed 44.711 px. Recheck the actual remaining
        // envelope, not just the old 20 px routing window. Never enlarge it.
        var measuredBoundary = validMeasuredPair && spread > 0 && worst <= measuredHandoffRadius &&
            worst + spread > measuredHandoffRadius;
        var originalBoundary =
            confirmation.RepeatGate?.Disposition == GateDisposition.Passed &&
            G3PostWcsMeasuredHandoffPolicy.NeedsShortExposureConfirmation(longTarget) &&
            G3PostWcsMeasuredHandoffPolicy.CanProposeHandoff(longTarget, prediction, uncertainty, slit, width, height, radius) &&
            previous?.Gate.Disposition == GateDisposition.Passed && current.Gate.Disposition == GateDisposition.Passed &&
            ValidIdentity(prior) && ValidIdentity(actual) &&
            double.IsFinite(spread) && spread > 0 && spread <= G3ShortPositionMeasurementPolicy.MaximumRepeatSeparationPixels &&
            double.IsFinite(worst) && worst > radius && worst - radius <= spread;
        var recheck = !recheckUsed && attempt >= 2 && attempt <= G3PostWcsShortExposurePolicy.MaximumFrames - 2 &&
            (measuredBoundary || originalBoundary);
        metrics["measuredHandoffBoundaryRecheck"] = recheck && measuredBoundary ? 1 : 0;
        return new(GateResult.Unknown(recheck ? RecheckCode : OutsideCode,
            recheck
                ? "The independent pair agrees and lies at the prediction or remaining measured fine-envelope boundary. Reserve exactly one new pair inside the original six-frame limit; no move, solve, tolerance or budget reset."
                : "Independent short positions agree, but the pair does not fit the unchanged prediction/slit handoff windows. No PHD2 handoff is authorized.", metrics), false, recheck);
    }
}
