namespace UvexAdv.Observatory;

/// <summary>SEP owns extraction. This policy only filters measurement validity
/// and associates it with an existing WCS/catalogue reference. No hardware APIs.</summary>
public static class G3SepShortPositionPolicy
{
    public const string Version = "sep-short-position-v1";

    public static G3ShortPositionMeasurement Measure(SepImageMeasurements image, PixelPoint prediction,
        double radius, double minimumSnr = 8, double minimumUniqueness = 1.5,
        bool requireResolvedPair = false)
    {
        if (!double.IsFinite(radius) || radius <= 0 || !double.IsFinite(prediction.X)
            || !double.IsFinite(prediction.Y) || !double.IsFinite(minimumSnr) || minimumSnr <= 0
            || !double.IsFinite(minimumUniqueness) || minimumUniqueness < 1)
            throw new ArgumentException("Invalid SEP association parameters.");
        var metrics = new Dictionary<string,double> { ["sepBackend"] = 1,
            ["sepComponentCount"] = image.Components?.Length ?? 0,
            ["sepComponentsTruncated"] = image.ComponentsTruncated ? 1 : 0 };
        G3ShortPositionMeasurement Reject(string code, string message) => new(
            GateResult.Unknown(code,message,metrics),
            new(GateResult.Unknown(code,message),null,prediction,double.PositiveInfinity,0),true,0,[]);
        if (image.Algorithm != SepStarDetectionClient.Algorithm || image.Components is null
            || image.ComponentsTruncated || image.MotionAuthorized || image.TargetIdentityConfirmed)
            return Reject("G3_SEP_INVALID_COVERAGE", "SEP components are missing, truncated or violate the measurement-only contract.");

        var candidates = image.Components.Where(c =>
            c.SaturatedFraction == 0 && c.Flux > 0 && c.Snr >= minimumSnr
            && c.Npix >= 9 && c.RawSupportPixels >= 3 && (c.SepFlags & ~1) == 0
            && c.Bbox[0] > 0 && c.Bbox[1] > 0
            && c.Bbox[0] + c.Bbox[2] < image.Width && c.Bbox[1] + c.Bbox[3] < image.Height
            && Distance(new(c.X,c.Y),prediction) + .5 <= radius)
            .Select(c => new StarCandidate(new(c.X,c.Y),c.Peak,c.Flux,c.Snr,
                // No Gaussian FWHM was fitted. Preserve SEP moments in evidence,
                // not in a falsely labelled FWHM field.
                0,1-c.B/c.A,c.SaturatedFraction,
                Math.Min(Math.Min(c.X,c.Y),Math.Min(image.Width-1-c.X,image.Height-1-c.Y))))
            .ToArray();
        metrics["sepValidRoiComponents"] = candidates.Length;
        if (candidates.Length == 0)
            return Reject("G3_SEP_UNMEASURED", "SEP found no sufficiently supported unsaturated component inside the existing WCS window.");
        var identified = SlitTargetIdentifier.Identify(candidates,prediction,radius,minimumSnr,minimumUniqueness);
        // A known binary must reach signed-vector association before any
        // nearest-WCS shortcut. Brightness never decides primary identity.
        var ambiguous = requireResolvedPair && candidates.Length > 1
            || identified.Gate.Disposition != GateDisposition.Passed;
        var gate = ambiguous
            ? GateResult.Unknown("G3_SEP_AMBIGUOUS", "SEP components need an unambiguous catalogue association.",metrics)
            : GateResult.Pass("G3_SEP_POSITION_MEASURED", "SEP measured a raw-image component. Independent fresh-frame confirmation remains mandatory.",metrics);
        return new(gate,identified with { Gate=gate, Target=ambiguous ? null : identified.Target },true,.5,candidates);
    }

    private static double Distance(PixelPoint a,PixelPoint b) => Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2));
}
