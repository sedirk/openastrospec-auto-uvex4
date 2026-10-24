using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>Identity-preserving image continuity. Predictions never become target measurements.</summary>
internal static class G3TargetTrackPolicy
{
    internal const double MaximumReferenceAgeSeconds = 120;

    internal static bool MatchesImmutablePixels(MonochromeFrame frame, ReadOnlySpan<ushort> pixels)
    {
        if(pixels.Length != frame.Width * frame.Height) return false;
        for(var y=0;y<frame.Height;y++) for(var x=0;x<frame.Width;x++)
            if(frame[x,y] != pixels[y*frame.Width+x]) return false;
        return true;
    }

    internal static TargetIdentification? MeasureSearchHint(MonochromeFrame frame,IReadOnlyList<StarCandidate> candidates,
        PixelPoint hint,PixelPoint original,double radius,double minimumSnr,double uniqueness)
    {
        if(!double.IsFinite(hint.X)||!double.IsFinite(hint.Y)||Distance(hint,original)>radius) return null;
        var measured=SlitTargetIdentifier.Identify(frame,candidates,hint,radius,minimumSnr,uniqueness);
        return Phd2FreshTargetFluxPolicy.HasMeasuredHandoffPosition(measured) &&
            Distance(measured.Target!.Centroid,original)<=radius ? measured : null;
    }

    internal static G3OverlapRegistrationResult Register(IReadOnlyList<StarCandidate> reference,
        IReadOnlyList<StarCandidate> current, PixelPoint referenceTarget, PixelPoint expectedTarget,
        SlitGeometry slit, int width, int height, double searchRadius, CancellationToken token)
    {
        // Exclude the complete recognition region: neither the target nor its
        // bright halo is allowed to certify its own displacement. Also retain
        // the detector-fixed-pattern veto used by the older fine registrar.
        var radius = Math.Max(30, searchRadius);
        var shifted = Distance(referenceTarget, expectedTarget) > 8;
        bool Independent(StarCandidate s) =>
            GuideStarSelector.DistanceToGuideExclusion(s.Centroid, slit) > slit.WidthPixels / 2 + 5;
        var a = reference.Where(Independent).ToArray();
        var b = current.Where(Independent).ToArray();
        if (shifted)
        {
            var originalA = a; var originalB = b;
            a = originalA.Where(s => !originalB.Any(t => Distance(s.Centroid, t.Centroid) <= 2)).ToArray();
            b = originalB.Where(s => !originalA.Any(t => Distance(s.Centroid, t.Centroid) <= 2)).ToArray();
        }
        return G3OverlapRegistration.Register(a, b, width, height,
            new[] { new G3RegistrationExclusion(referenceTarget, radius) },
            new[] { new G3RegistrationExclusion(expectedTarget, radius) }, token);
    }

    internal static bool IsFresh(DateTimeOffset referenceCompletedUtc, DateTimeOffset currentCompletedUtc,
        DateTimeOffset evaluatedUtc, string referenceSha, string currentSha) =>
        referenceCompletedUtc != default && currentCompletedUtc > referenceCompletedUtc &&
        currentCompletedUtc <= evaluatedUtc &&
        (evaluatedUtc - referenceCompletedUtc).TotalSeconds <= MaximumReferenceAgeSeconds &&
        !string.Equals(referenceSha, currentSha, StringComparison.OrdinalIgnoreCase);

    internal static bool AcceptMeasuredCandidate(MonochromeFrame reference, PixelPoint referencePoint,
        MonochromeFrame current, PixelPoint measuredPoint, PixelPoint registeredPrediction,
        double allowance, bool comparableExposure, out double correlation)
    {
        correlation = double.NaN;
        if (!Phd2TargetContinuityPolicy.IsContinuous(measuredPoint, registeredPrediction, allowance)) return false;
        // A template is an additional component check, never a centroid or flux
        // authority. Exposure/saturation transitions are not comparable PSFs;
        // the fresh detector and independent geometry retain authority then.
        if (!comparableExposure || ClippedCore(reference,referencePoint) != ClippedCore(current,measuredPoint)) return true;
        correlation = PatchCorrelation(reference, referencePoint, current, measuredPoint);
        return double.IsFinite(correlation) && correlation >= .6;
    }

    internal static double PatchCorrelation(MonochromeFrame reference, PixelPoint a,
        MonochromeFrame current, PixelPoint b)
    {
        const int radius = 16;
        double sx = 0, sy = 0, xx = 0, yy = 0, xy = 0;
        var count = 0;
        for (var dy = -radius; dy <= radius; dy++)
        for (var dx = -radius; dx <= radius; dx++)
        {
            var ax = (int)Math.Round(a.X) + dx; var ay = (int)Math.Round(a.Y) + dy;
            var bx = (int)Math.Round(b.X) + dx; var by = (int)Math.Round(b.Y) + dy;
            if (ax < 0 || ay < 0 || ax >= reference.Width || ay >= reference.Height ||
                bx < 0 || by < 0 || bx >= current.Width || by >= current.Height) return double.NaN;
            double x = reference[ax, ay]; double y = current[bx, by];
            sx += x; sy += y; xx += x * x; yy += y * y; xy += x * y; count++;
        }
        var variance = (xx - sx * sx / count) * (yy - sy * sy / count);
        return variance <= 0 ? double.NaN : (xy - sx * sy / count) / Math.Sqrt(variance);
    }

    private static double Distance(PixelPoint a, PixelPoint b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private static bool ClippedCore(MonochromeFrame frame,PixelPoint p)
    {
        var x=(int)Math.Round(p.X); var y=(int)Math.Round(p.Y);
        for(var dy=-3;dy<=3;dy++) for(var dx=-3;dx<=3;dx++)
            if(x+dx>=0 && x+dx<frame.Width && y+dy>=0 && y+dy<frame.Height &&
                frame[x+dx,y+dy] >= frame.SaturationLevel*.98) return true;
        return false;
    }
}
