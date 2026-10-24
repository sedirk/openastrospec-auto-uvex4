namespace UvexAdv.Observatory;

public sealed record G3OverlapLoopAssessment(bool Consistent, double MaximumCornerDisagreementPixels,
    double MaximumAllowedDisagreementPixels);

/// <summary>Closes independently registered image edges in the same reference coordinate
/// system. A first matching neighbour is not authority when another measured path disagrees.</summary>
public static class G3OverlapGraphConsistency
{
    public static G3OverlapLoopAssessment Evaluate(G3SimilarityTransform first, G3SimilarityTransform second,
        int width, int height, double maximumDisagreementPixels = 6)
    {
        if (width <= 0 || height <= 0 || !double.IsFinite(maximumDisagreementPixels) || maximumDisagreementPixels <= 0)
            throw new ArgumentException("Invalid measured graph-loop envelope.");
        var corners = new[] { new PixelPoint(0,0),new(width,0),new(width,height),new(0,height),new(width/2d,height/2d) };
        var maximum = corners.Select(p =>
        {
            var a=first.Apply(p);var b=second.Apply(p);
            return Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2));
        }).Max();
        return new(double.IsFinite(maximum) && maximum <= maximumDisagreementPixels,
            double.IsFinite(maximum) ? maximum : double.MaxValue,maximumDisagreementPixels);
    }
}
