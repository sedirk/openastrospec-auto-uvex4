namespace UvexAdv.Observatory;

/// <summary>Orientation-preserving image similarity. It describes measured pixels, never a mount command.</summary>
public sealed record G3SimilarityTransform(double A, double B, double X, double Y)
{
    public static G3SimilarityTransform Identity { get; } = new(1, 0, 0, 0);
    public double Scale => Math.Sqrt(A * A + B * B);
    public double RotationDegrees => Math.Atan2(B, A) * 180 / Math.PI;
    public PixelPoint Apply(PixelPoint p) => new(A * p.X - B * p.Y + X, B * p.X + A * p.Y + Y);
    public G3SimilarityTransform Inverse()
    {
        var d = A * A + B * B;
        if (!double.IsFinite(d) || d < 1e-12) throw new InvalidOperationException("Singular measured image transform.");
        return new(A / d, -B / d, (-A * X - B * Y) / d, (B * X - A * Y) / d);
    }
    /// <summary>Apply this transform, then the supplied transform.</summary>
    public G3SimilarityTransform Then(G3SimilarityTransform next) => new(
        next.A * A - next.B * B, next.B * A + next.A * B,
        next.A * X - next.B * Y + next.X, next.B * X + next.A * Y + next.Y);
}

public sealed record G3RegistrationExclusion(PixelPoint Center, double RadiusPixels);
public sealed record G3RegisteredStarPair(PixelPoint Reference, PixelPoint Current, double ResidualPixels);
public sealed record G3OverlapRegistrationResult(bool Accepted, string Code, string Reason,
    G3SimilarityTransform? Transform, IReadOnlyList<G3RegisteredStarPair> FitPairs,
    IReadOnlyList<G3RegisteredStarPair> HoldoutPairs, double MaximumHoldoutResidualPixels,
    double OverlapFraction, int EquallySupportedTransforms)
{
    public int MatchedStars => FitPairs.Count + HoldoutPairs.Count;
}

/// <summary>
/// Fits three spatially separated field stars, checks at least two stars NOT used in the fit,
/// and rejects incompatible equally supported solutions. The optional target/halo exclusions
/// prevent a target from supplying its own astrometric proof. No target identity or motion
/// authority is produced; the caller must authenticate immutable frames and live context.
/// </summary>
public static class G3OverlapRegistration
{
    private const double MatchTolerance = 4;
    private const double HoldoutTolerance = 2.5;
    private const double MinimumOverlap = 0.25;
    private const double MaximumRotation = 5;

    public static G3OverlapRegistrationResult Register(IReadOnlyList<StarCandidate> reference,
        IReadOnlyList<StarCandidate> current, int width, int height,
        IReadOnlyList<G3RegistrationExclusion>? referenceExclusions = null,
        IReadOnlyList<G3RegistrationExclusion>? currentExclusions = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (width < 32 || height < 32) throw new ArgumentOutOfRangeException(nameof(width));
        var a = Select(reference, width, height, referenceExclusions);
        var b = Select(current, width, height, currentExclusions);
        if (a.Length < 5 || b.Length < 5) return Rejected("Fewer than five independent usable field stars.");
        var ta = Triangles(a, token);
        var tb = Triangles(b, token);
        var hypotheses = new List<(double Cost, Triangle A, Triangle B)>();
        var comparisons = 0;
        // A bounded, deterministic descriptor lookup rather than brightness pairing.
        var buckets = tb.GroupBy(t => ((int)(t.R1 / .003), (int)(t.R2 / .003)))
            .ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var left in ta)
        {
            token.ThrowIfCancellationRequested();
            var key = ((int)(left.R1 / .003), (int)(left.R2 / .003));
            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
            {
                if (!buckets.TryGetValue((key.Item1 + dx, key.Item2 + dy), out var candidates)) continue;
                foreach (var right in candidates)
                {
                    if ((++comparisons & 255) == 0) token.ThrowIfCancellationRequested();
                    var cost = Math.Pow(left.R1 - right.R1, 2) + Math.Pow(left.R2 - right.R2, 2);
                    if (cost <= .0025 * .0025)
                    {
                        // A heavily repeated descriptor field is not resolved by silently
                        // dropping later aliases; decline this optional route in bounded RAM.
                        if (hypotheses.Count == 4000)
                            return Rejected("Triangle-descriptor ambiguity exceeds the bounded registration work envelope.")
                                with { Code = "G3_OVERLAP_AMBIGUOUS" };
                        hypotheses.Add((cost, left, right));
                    }
                }
            }
        }
        G3OverlapRegistrationResult? best = null;
        var alternatives = new List<G3SimilarityTransform>();
        var support = 0;
        // Evaluate every retained hypothesis. Truncating to an arbitrary first N can
        // hide an equally supported alias and turn enumeration order into authority.
        foreach (var hypothesis in hypotheses.OrderBy(t => t.Cost))
        {
            token.ThrowIfCancellationRequested();
            var initial = Fit(a, b, hypothesis.A.Indices.Zip(hypothesis.B.Indices).ToArray());
            if (!Plausible(initial)) continue;
            var pairs = Match(a, b, initial);
            if (pairs.Count < 5 || pairs.Count < support) continue;
            var train = SpreadTriangle(a, pairs, width, height);
            if (train is null) continue;
            var transform = Fit(a, b, train.Select(i => pairs[i]).ToArray());
            if (!Plausible(transform)) continue;
            var fit = train.Select(i => Pair(a, b, pairs[i], transform)).ToArray();
            var holdout = Enumerable.Range(0, pairs.Count).Except(train)
                .Select(i => Pair(a, b, pairs[i], transform)).ToArray();
            if (fit.Max(p => p.ResidualPixels) > HoldoutTolerance || holdout.Length < 2 ||
                holdout.Max(p => p.ResidualPixels) > HoldoutTolerance) continue;
            var overlap = OverlapFraction(transform, width, height);
            if (overlap < MinimumOverlap) continue;
            if (pairs.Count > support) { alternatives.Clear(); support = pairs.Count; }
            if (alternatives.All(t => Difference(t, transform, width, height) > 8)) alternatives.Add(transform);
            var result = new G3OverlapRegistrationResult(true, "G3_OVERLAP_REGISTERED",
                "Three spread-out fit stars and independent held-out field stars agree; inferred geometry, not a formal current-frame solve.",
                transform, fit, holdout, holdout.Max(p => p.ResidualPixels), overlap, 1);
            if (best is null || result.MatchedStars > best.MatchedStars ||
                result.MatchedStars == best.MatchedStars && result.MaximumHoldoutResidualPixels < best.MaximumHoldoutResidualPixels)
                best = result;
        }
        if (alternatives.Count > 1) return Rejected("Equally supported independent registrations disagree; no geometry was selected.")
            with { Code = "G3_OVERLAP_AMBIGUOUS", EquallySupportedTransforms = alternatives.Count };
        return best ?? Rejected("No overlapping, independently held-out registration; a fitting triangle alone is not validation.");
    }

    private static PixelPoint[] Select(IReadOnlyList<StarCandidate> stars, int width, int height,
        IReadOnlyList<G3RegistrationExclusion>? exclusions)
    {
        if (exclusions?.Any(e => !Finite(e.Center) || !double.IsFinite(e.RadiusPixels) || e.RadiusPixels < 0) == true)
            throw new ArgumentException("Invalid target/halo exclusion.", nameof(exclusions));
        return stars.Where(s => Finite(s.Centroid) && s.Centroid.X >= 8 && s.Centroid.Y >= 8 &&
                s.Centroid.X < width - 8 && s.Centroid.Y < height - 8 &&
                double.IsFinite(s.SignalToNoise) && s.SignalToNoise >= 5 && s.SaturatedFraction == 0 &&
                double.IsFinite(s.Ellipticity) && s.Ellipticity <= .65 &&
                (exclusions is null || exclusions.All(e => Distance(s.Centroid, e.Center) > e.RadiusPixels)))
            .OrderByDescending(s => s.SignalToNoise).Select(s => s.Centroid)
            .Distinct().Take(24).ToArray();
    }

    private sealed record Triangle(double R1, double R2, int[] Indices);
    private static List<Triangle> Triangles(PixelPoint[] points, CancellationToken token)
    {
        var result = new List<Triangle>();
        for (var i = 0; i < points.Length; i++)
        for (var j = i + 1; j < points.Length; j++)
        for (var k = j + 1; k < points.Length; k++)
        {
            if ((result.Count & 255) == 0) token.ThrowIfCancellationRequested();
            var sides = new[] { (Length: Distance(points[j], points[k]), Index: i),
                (Length: Distance(points[i], points[k]), Index: j), (Length: Distance(points[i], points[j]), Index: k) }
                .OrderBy(x => x.Length).ToArray();
            if (sides[0].Length < 20 || sides[2].Length / sides[0].Length > 12) continue;
            // Nearly isosceles triangles have unstable vertex order; other independent triangles remain.
            if (sides[1].Length - sides[0].Length < 1 || sides[2].Length - sides[1].Length < 1) continue;
            result.Add(new(sides[0].Length / sides[2].Length, sides[1].Length / sides[2].Length,
                sides.Select(s => s.Index).ToArray()));
        }
        return result;
    }

    private static G3SimilarityTransform Fit(PixelPoint[] a, PixelPoint[] b, (int First, int Second)[] pairs)
    {
        var ca = new PixelPoint(pairs.Average(p => a[p.First].X), pairs.Average(p => a[p.First].Y));
        var cb = new PixelPoint(pairs.Average(p => b[p.Second].X), pairs.Average(p => b[p.Second].Y));
        double dot = 0, cross = 0, denominator = 0;
        foreach (var (i, j) in pairs)
        {
            var x = a[i].X - ca.X; var y = a[i].Y - ca.Y;
            var u = b[j].X - cb.X; var v = b[j].Y - cb.Y;
            dot += x * u + y * v; cross += x * v - y * u; denominator += x * x + y * y;
        }
        if (denominator < 1e-9) return new(double.NaN, 0, 0, 0);
        var aa = dot / denominator; var bb = cross / denominator;
        return new(aa, bb, cb.X - aa * ca.X + bb * ca.Y, cb.Y - bb * ca.X - aa * ca.Y);
    }

    private static List<(int First, int Second)> Match(PixelPoint[] a, PixelPoint[] b, G3SimilarityTransform transform)
    {
        var projected = a.Select(transform.Apply).ToArray();
        var result = new List<(int, int)>();
        for (var i = 0; i < a.Length; i++)
        {
            var candidates = Enumerable.Range(0, b.Length).Where(j => Distance(projected[i], b[j]) <= MatchTolerance).ToArray();
            if (candidates.Length != 1) continue;
            var index = candidates[0];
            if (projected.Count(p => Distance(p, b[index]) <= MatchTolerance) == 1) result.Add((i, index));
        }
        return result;
    }

    private static int[]? SpreadTriangle(PixelPoint[] a, List<(int First, int Second)> pairs, int width, int height)
    {
        double largest = 0; int[]? selected = null;
        for (var i = 0; i < pairs.Count; i++)
        for (var j = i + 1; j < pairs.Count; j++)
        for (var k = j + 1; k < pairs.Count; k++)
        {
            var p = a[pairs[i].First]; var q = a[pairs[j].First]; var r = a[pairs[k].First];
            var area = Math.Abs((q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X));
            if (area > largest) { largest = area; selected = new[] { i, j, k }; }
        }
        return largest >= Math.Max(400, width * (double)height * .005) ? selected : null;
    }

    private static G3RegisteredStarPair Pair(PixelPoint[] a, PixelPoint[] b, (int First, int Second) p, G3SimilarityTransform transform) =>
        new(a[p.First], b[p.Second], Distance(transform.Apply(a[p.First]), b[p.Second]));
    private static bool Plausible(G3SimilarityTransform transform) => double.IsFinite(transform.Scale) &&
        transform.Scale is >= .97 and <= 1.03 && Math.Abs(transform.RotationDegrees) <= MaximumRotation;
    private static G3OverlapRegistrationResult Rejected(string reason) => new(false, "G3_OVERLAP_UNCONFIRMED", reason,
        null, Array.Empty<G3RegisteredStarPair>(), Array.Empty<G3RegisteredStarPair>(), 0, 0, 0);
    private static bool Finite(PixelPoint p) => double.IsFinite(p.X) && double.IsFinite(p.Y);
    private static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    private static double Difference(G3SimilarityTransform a, G3SimilarityTransform b, int width, int height) =>
        new[] { new PixelPoint(0, 0), new(width, 0), new(width, height), new(0, height) }
            .Max(p => Distance(a.Apply(p), b.Apply(p)));

    public static double OverlapFraction(G3SimilarityTransform transform, int width, int height)
    {
        var points = new List<PixelPoint> { transform.Apply(new(0, 0)), transform.Apply(new(width, 0)),
            transform.Apply(new(width, height)), transform.Apply(new(0, height)) };
        for (var edge = 0; edge < 4; edge++)
        {
            if (points.Count == 0) return 0;
            var source = points; points = new();
            double Signed(PixelPoint p) => edge switch { 0 => p.X, 1 => width - p.X, 2 => p.Y, _ => height - p.Y };
            var previous = source[^1]; var before = Signed(previous);
            foreach (var point in source)
            {
                var after = Signed(point);
                if ((before >= 0) != (after >= 0))
                {
                    var t = before / (before - after);
                    points.Add(new(previous.X + t * (point.X - previous.X), previous.Y + t * (point.Y - previous.Y)));
                }
                if (after >= 0) points.Add(point);
                previous = point; before = after;
            }
        }
        double area = 0;
        for (var i = 0; i < points.Count; i++)
            area += points[i].X * points[(i + 1) % points.Count].Y - points[i].Y * points[(i + 1) % points.Count].X;
        return Math.Abs(area) / 2 / (width * (double)height);
    }
}
