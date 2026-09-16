using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3CatalogReferenceHandoffTests
{
    private static G3CatalogRegistrationReference Reference() => new("original-solved.fit", "original-hash",
        "run", 7, "East", 300, 300, new(150, 150),
        new[] { Star(40, 40), Star(90, 75), Star(240, 100), Star(200, 230) });
    private static StarCandidate Star(double x, double y) => new(new(x, y), 3000, 20000, 30, 4, .1, 0, 50);
    private static G3FieldState Arrival() => G3FieldState.Failed(GateResult.Pass("G3_FIELD_ANALYZED_MOTION_PREDICTED", "coarse handoff"),
        "unsolved-arrival.fit") with { TargetIdentification = TargetIdentification.FromCatalogWcs(new(157, 145), 300, 300, "prediction only") };

    [Fact]
    public void UnsolvedArrivalRetainsOriginalReferenceAndFreshStarsMeasureItsActualTranslation()
    {
        var reference = Reference();
        var arrival = G3CatalogReferenceHandoff.Preserve(Arrival(), reference, true);
        Assert.Same(reference, arrival.CatalogRegistrationReference);
        Assert.Null(arrival.Solve); // No synthetic solve or arrival position promoted to reference.
        Assert.Equal("unsolved-arrival.fit", arrival.FramePath);
        Assert.Equal(new PixelPoint(150, 150), reference.CataloguePoint);
        var stars = reference.Stars.Select(s => s with { Centroid = new(s.Centroid.X + 6, s.Centroid.Y - 3) })
            .Append(Star(160, 145)).ToArray();
        var registered = CatalogFieldRegistration.Measure(reference.Stars, stars, reference.CataloguePoint,
            new(7, -5), 10, 2, 300, 300, new("slit", new(150, 155), 0, 100, 3, .5, "camera", 1, 1));
        Assert.Equal(GateDisposition.Passed, registered.Gate.Disposition);
        Assert.Equal(new PixelPoint(156, 147), registered.Target);
        Assert.NotEqual(arrival.TargetIdentification.PredictedPoint, registered.Target);
        Assert.NotEqual(stars[^1].Centroid, registered.Target);
    }

    [Fact]
    public void MissingReferenceStopsAtHandoffButDoesNotChangeVisibleSourceOrExistingFailure()
    {
        var arrival = Arrival();
        var missing = G3CatalogReferenceHandoff.Preserve(arrival, null, true);
        Assert.Equal("G3_CATALOG_REFERENCE_MISSING", missing.Gate.Code);
        Assert.Equal(GateDisposition.Indeterminate, missing.Gate.Disposition);
        Assert.Same(arrival, G3CatalogReferenceHandoff.Preserve(arrival, null, false));
        var failed = arrival with { Gate = GateResult.Fail("ACTUAL_FAILURE", "retain") };
        Assert.Same(failed, G3CatalogReferenceHandoff.Preserve(failed, Reference(), true));
        var withReference = arrival with { CatalogRegistrationReference = Reference() };
        Assert.Same(withReference, G3CatalogReferenceHandoff.Preserve(withReference, Reference(), true));
    }
}
