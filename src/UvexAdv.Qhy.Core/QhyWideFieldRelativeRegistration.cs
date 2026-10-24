namespace UvexAdv.Qhy.Core;

/// <summary>Relative QHY detector measurement only, not a sky solve, G3 position or mount authority.</summary>
public sealed record QhyRelativeImageTransform(double A, double B, double X, double Y)
{
    public (double X, double Y) Apply(double x, double y) => (A * x - B * y + X, B * x + A * y + Y);
    public double Scale => Math.Sqrt(A * A + B * B);
    public double RotationDegrees => Math.Atan2(B, A) * 180 / Math.PI;
}

public sealed record QhyRelativeRegistration(bool Accepted, string Code, QhyRelativeImageTransform? Transform,
    int TrainingMatches, int HoldoutMatches, int ReferenceHoldoutCount, double HoldoutP95Pixels,
    double HoldoutRecoveryFraction, double MaximumHoldoutResidualPixels)
{
    public bool FormalSolve => false;
    public bool TargetOrSlitMeasurement => false;
    public double CornerUncertaintyPixels { get; init; }
}

/// <summary>Fast current-field registration using independently withheld reference-star IDs.
/// Missing/changed geometry and wrong fields retire this optional route, never physical safety.</summary>
public static class QhyWideFieldRelativeRegistration
{
    public static QhyRelativeRegistration Register(QhyFrameRecord reference, QhyFrameRecord current, CancellationToken token = default)
    {
        QhyRelativeRegistration Reject(string reason) => new(false, reason, null, 0, 0, 0, double.NaN, 0, double.NaN);
        if (reference.FrameId == current.FrameId || reference.Sha256.Equals(current.Sha256, StringComparison.OrdinalIgnoreCase))
            return Reject("QHY_RELATIVE_DUPLICATE_FRAME");
        var width = reference.Metrics.SourceWidthPixels; var height = reference.Metrics.SourceHeightPixels;
        if (width <= 0 || height <= 0 || current.Metrics.SourceWidthPixels != width || current.Metrics.SourceHeightPixels != height ||
            reference.Settings with { ExposureSeconds = current.Settings.ExposureSeconds } != current.Settings ||
            current.ExposureMidpointUtc <= reference.ExposureMidpointUtc ||
            current.ExposureStartedUtc < reference.ExposureEndedUtc ||
            current.ExposureMidpointUtc - reference.ExposureMidpointUtc > TimeSpan.FromSeconds(120))
            return Reject("QHY_RELATIVE_CONTEXT_OR_TIME_CHANGED");
        var a = reference.Metrics.AstrometricStars?.Take(500).ToArray();
        var b = current.Metrics.AstrometricStars?.Take(500).ToArray();
        if (a is null || b is null || a.Length < 12 || b.Length < 12 ||
            a.Concat(b).Any(s => !double.IsFinite(s.X) || !double.IsFinite(s.Y) || s.X < 0 || s.X >= width || s.Y < 0 || s.Y >= height))
            return Reject("QHY_RELATIVE_CATALOG_UNAVAILABLE");
        var forward = Nearest(a, b, token); var backward = Nearest(b, a, token);
        var pairs = Enumerable.Range(0, a.Length).Where(i => forward[i] >= 0 && backward[forward[i]] == i)
            .Select(i => (Left: i, Right: forward[i])).ToArray();
        var train = pairs.Where(p => p.Left % 3 != 0).ToArray();
        var holdout = pairs.Where(p => p.Left % 3 == 0).ToArray();
        var expectedHoldout = Enumerable.Range(0, a.Length).Count(i => i % 3 == 0);
        if (train.Length < 8 || holdout.Length < 4) return Reject("QHY_RELATIVE_INSUFFICIENT_INDEPENDENT_MATCHES");
        bool Distributed((int Left, int Right)[] points) =>
            points.Max(p => a[p.Left].X) - points.Min(p => a[p.Left].X) >= width / 3d &&
            points.Max(p => a[p.Left].Y) - points.Min(p => a[p.Left].Y) >= height / 3d;
        if (!Distributed(train) || !Distributed(holdout)) return Reject("QHY_RELATIVE_LOCAL_CLUSTER_NOT_FIELD_PROOF");
        var random = new Random(20261024); var best = Array.Empty<(int Left, int Right)>();
        for (var iteration = 0; iteration < 256; iteration++)
        {
            token.ThrowIfCancellationRequested();
            var sampled = new HashSet<int>();
            while (sampled.Count < 3) sampled.Add(random.Next(train.Length));
            var model = Fit(a, b, sampled.Select(i => train[i]).ToArray());
            if (model is null) continue;
            var inliers = train.Where(p => Residual(a[p.Left], b[p.Right], model) <= 1).ToArray();
            if (inliers.Length > best.Length) best = inliers;
        }
        if (best.Length < 8 || Fit(a, b, best) is not { } fitted)
            return Reject("QHY_RELATIVE_NO_CONSISTENT_TRANSFORM");
        // Holdouts never entered fitting or its outlier selection. Keep every
        // matched holdout, and count missing reference stars in recovery.
        var errors = holdout.Select(p => Residual(a[p.Left], b[p.Right], fitted)).Order().ToArray();
        var p95 = errors[(int)Math.Ceiling(.95 * (errors.Length - 1))];
        var recovery = holdout.Length / (double)expectedHoldout;
        var accepted = best.Length >= .8 * train.Length && recovery >= .8 && p95 <= 1 &&
            errors.Count(e => e <= 1) >= .8 * errors.Length && Math.Abs(fitted.Scale - 1) <= .002;
        var lever = Math.Max(width / (train.Max(p => a[p.Left].X) - train.Min(p => a[p.Left].X)),
            height / (train.Max(p => a[p.Left].Y) - train.Min(p => a[p.Left].Y)));
        return new(accepted, accepted ? "QHY_RELATIVE_HELDOUT_VALIDATED" : "QHY_RELATIVE_INDEPENDENT_VALIDATION_FAILED",
            accepted ? fitted : null, train.Length, holdout.Length, expectedHoldout, p95, recovery, errors[^1])
        { CornerUncertaintyPixels = Math.Max(errors[^1], .05) * Math.Max(1, lever) };
    }

    private static int[] Nearest(QhyAstrometricStar[] a, QhyAstrometricStar[] b, CancellationToken token)
    {
        var result = Enumerable.Repeat(-1, a.Length).ToArray();
        for (var i = 0; i < a.Length; i++)
        {
            token.ThrowIfCancellationRequested(); var best = 15d * 15;
            for (var j = 0; j < b.Length; j++)
            {
                var d = Math.Pow(a[i].X - b[j].X, 2) + Math.Pow(a[i].Y - b[j].Y, 2);
                if (d < best) { best = d; result[i] = j; }
            }
        }
        return result;
    }

    private static QhyRelativeImageTransform? Fit(QhyAstrometricStar[] a, QhyAstrometricStar[] b, (int Left, int Right)[] pairs)
    {
        var x = pairs.Average(p => a[p.Left].X); var y = pairs.Average(p => a[p.Left].Y);
        var u = pairs.Average(p => b[p.Right].X); var v = pairs.Average(p => b[p.Right].Y);
        double denominator = 0, dot = 0, cross = 0;
        foreach (var (left, right) in pairs)
        {
            var dx = a[left].X - x; var dy = a[left].Y - y;
            var du = b[right].X - u; var dv = b[right].Y - v;
            denominator += dx * dx + dy * dy; dot += dx * du + dy * dv; cross += dx * dv - dy * du;
        }
        if (!double.IsFinite(denominator) || denominator < 1) return null;
        var aa = dot / denominator; var bb = cross / denominator;
        return new(aa, bb, u - aa * x + bb * y, v - bb * x - aa * y);
    }

    private static double Residual(QhyAstrometricStar a, QhyAstrometricStar b, QhyRelativeImageTransform model)
    {
        var mapped = model.Apply(a.X, a.Y);
        return Math.Sqrt(Math.Pow(mapped.X - b.X, 2) + Math.Pow(mapped.Y - b.Y, 2));
    }
}
