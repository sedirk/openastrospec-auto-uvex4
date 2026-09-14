namespace UvexAdv.Observatory;

public sealed record CatalogFieldRegistrationResult(GateResult Gate, PixelPoint Target,
    PixelPoint Translation, int MatchedStars, double ScatterPixels);

/// <summary>
/// Transfer a fixed catalogue sample point using independently measured field
/// stars. The target itself is excluded: neither a galaxy nucleus nor the nearest
/// bright knot may replace it. Only bounded translation, not a fitted rotation,
/// scale, or a commanded lock offset, is admitted during fine guiding.
/// </summary>
public static class CatalogFieldRegistration
{
    public static IReadOnlyList<StarCandidate> ReferenceStars(SepImageMeasurements image)
    {
        if (image.Algorithm != SepStarDetectionClient.Algorithm || image.ParentComponents is null ||
            image.ParentComponentsTruncated || image.MotionAuthorized || image.TargetIdentityConfirmed)
            throw new InvalidOperationException("G3_CATALOG_REGISTRATION_SEP_COVERAGE: Complete measurement-only SEP parents are required.");
        // Use connected SEP parents, not multiple local peaks within one
        // irregular PSF. Hot pixels and saturated/clipped regions cannot vote.
        return image.ParentComponents.Where(c => c.SepFlags == 0 && c.Flux > 0 && c.Snr >= 5 &&
                c.SaturatedFraction == 0 && c.Npix >= 9 && c.RawSupportPixels >= 3 &&
                c.Bbox[0] >= 8 && c.Bbox[1] >= 8 && c.Bbox[0]+c.Bbox[2] <= image.Width-8 &&
                c.Bbox[1]+c.Bbox[3] <= image.Height-8)
            .Select(c => new StarCandidate(new(c.X,c.Y),c.Peak,c.Flux,c.Snr,0,1-c.B/c.A,0,
                Math.Min(Math.Min(c.X,c.Y),Math.Min(image.Width-1-c.X,image.Height-1-c.Y))))
            .ToArray();
    }

    public static CatalogFieldRegistrationResult Measure(IReadOnlyList<StarCandidate> reference,
        IReadOnlyList<StarCandidate> current, PixelPoint cataloguePoint, PixelPoint expectedTranslation,
        double searchRadius, double matchTolerance, int width, int height, SlitGeometry slit)
    {
        CatalogFieldRegistrationResult Failed(string reason) => new(
            GateResult.Unknown("G3_CATALOG_FIELD_REGISTRATION_UNCONFIRMED", reason),
            cataloguePoint, new(0, 0), 0, double.NaN);
        if (!Finite(cataloguePoint) || !Finite(expectedTranslation) || !double.IsFinite(searchRadius) ||
            searchRadius <= 0 || !double.IsFinite(matchTolerance) || matchTolerance <= 0 || width <= 0 || height <= 0)
            return Failed("Invalid catalogue field-registration geometry.");
        var predicted = Add(cataloguePoint, expectedTranslation);
        bool Usable(StarCandidate star, PixelPoint excluded) => Finite(star.Centroid) &&
            double.IsFinite(star.SignalToNoise) && star.SignalToNoise >= 5 && star.SaturatedFraction == 0 &&
            star.EdgeDistancePixels >= 8 && Distance(star.Centroid, excluded) > 20 &&
            GuideStarSelector.DistanceToSlit(star.Centroid, slit) > slit.WidthPixels / 2 + 5;
        var a = reference.Where(s => Usable(s, cataloguePoint)).OrderByDescending(s => s.SignalToNoise).Take(80).ToArray();
        var b = current.Where(s => Usable(s, predicted)).OrderByDescending(s => s.SignalToNoise).Take(80).ToArray();
        if (a.Length < 3 || b.Length < 3) return Failed("Fewer than three independent unsaturated reference stars; no target/nucleus snap was attempted.");
        var hypotheses = new List<(PixelPoint Shift, int Count, double Scatter)>();
        foreach (var left in a)
        foreach (var right in b)
        {
            var shift = Subtract(right.Centroid, left.Centroid);
            if (Distance(shift, expectedTranslation) > searchRadius) continue;
            var pairs = new List<PixelPoint>();
            // Mutual unique associations, not independent nearest-neighbour
            // picks that can reuse one bright star several times.
            foreach (var source in a)
            {
                var matches = b.Where(s => Distance(s.Centroid, Add(source.Centroid, shift)) <= matchTolerance).ToArray();
                if (matches.Length != 1 || a.Count(s => Distance(Add(s.Centroid, shift), matches[0].Centroid) <= matchTolerance) != 1) continue;
                pairs.Add(Subtract(matches[0].Centroid, source.Centroid));
            }
            if (pairs.Count < 3) continue;
            var mean = new PixelPoint(pairs.Average(p => p.X), pairs.Average(p => p.Y));
            var scatter = Math.Sqrt(pairs.Average(p => Math.Pow(Distance(p, mean), 2)));
            if (scatter <= matchTolerance && Distance(mean, expectedTranslation) <= searchRadius)
                hypotheses.Add((mean, pairs.Count, scatter));
        }
        if (hypotheses.Count == 0) return Failed("No coherent three-star translation; fresh WCS/identity is required.");
        var best = hypotheses.OrderByDescending(h => h.Count).ThenBy(h => h.Scatter).First();
        if (hypotheses.Any(h => h.Count >= Math.Max(3, best.Count - 1) && Distance(h.Shift, best.Shift) > 2 * matchTolerance))
            return Failed("Multiple independent field-registration solutions; retained catalogue identity without selecting a peak.");
        var target = Add(cataloguePoint, best.Shift);
        if (target.X < 0 || target.X >= width || target.Y < 0 || target.Y >= height)
            return Failed("Registered catalogue point is outside the detector.");
        return new(GateResult.Pass("G3_CATALOG_FIELD_REGISTERED",
            "Catalogue sample point transferred by fresh independent field stars; not a detected target centroid.",
            new Dictionary<string, double> { ["registrationStars"] = best.Count, ["registrationScatterPixels"] = best.Scatter,
                ["translationX"] = best.Shift.X, ["translationY"] = best.Shift.Y }),
            target, best.Shift, best.Count, best.Scatter);
    }

    private static bool Finite(PixelPoint p) => double.IsFinite(p.X) && double.IsFinite(p.Y);
    private static PixelPoint Add(PixelPoint a, PixelPoint b) => new(a.X + b.X, a.Y + b.Y);
    private static PixelPoint Subtract(PixelPoint a, PixelPoint b) => new(a.X - b.X, a.Y - b.Y);
    private static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
