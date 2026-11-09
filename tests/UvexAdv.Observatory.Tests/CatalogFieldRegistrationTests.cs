using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class CatalogFieldRegistrationTests
{
    private static StarCandidate Star(double x, double y, double snr = 30) => new(new(x, y), 3000, 20000, snr, 4, .1, 0, 50);
    private static readonly StarCandidate[] Reference = [Star(40,40), Star(90,75), Star(240,100), Star(200,230), Star(60,260)];
    private static CatalogFieldRegistrationResult Register(StarCandidate[] current, double expectedX = 6) =>
        CatalogFieldRegistration.Measure(Reference, current, new(150,150), new(expectedX, -3), 10, 2,
            300,300,new("slit",new(150,155),0,100,3,.5,"camera",1,1));

    [Fact]
    public void MovesInvisibleCataloguePointWithFieldNotBrightGalaxyNucleus()
    {
        var stars = Reference.Select(s => s with { Centroid = new(s.Centroid.X+6,s.Centroid.Y-3) })
            .Append(Star(160,145,10000)).ToArray();
        var result = Register(stars);
        Assert.Equal(GateDisposition.Passed,result.Gate.Disposition);
        Assert.Equal(156,result.Target.X,8);
        Assert.Equal(147,result.Target.Y,8);
        Assert.Equal(5,result.MatchedStars);
        Assert.NotEqual(stars[^1].Centroid,result.Target);
    }

    [Fact]
    public void DoesNotUseCommandedOffsetAsOpticalProof()
    {
        var result = Register(Reference, 6);
        Assert.Equal(GateDisposition.Passed,result.Gate.Disposition);
        Assert.Equal(150,result.Target.X,8);
        Assert.Equal(150,result.Target.Y,8);
    }

    [Fact]
    public void RejectsTooFewStarsWrongFieldAndRotation()
    {
        Assert.NotEqual(GateDisposition.Passed,Register(Reference.Take(2).ToArray()).Gate.Disposition);
        Assert.NotEqual(GateDisposition.Passed,Register(Reference.Select(s=>s with {Centroid=new(s.Centroid.X+90,s.Centroid.Y)}).ToArray()).Gate.Disposition);
        Assert.NotEqual(GateDisposition.Passed,Register(Reference.Select(s=>s with {Centroid=new(300-s.Centroid.Y,s.Centroid.X)}).ToArray()).Gate.Disposition);
    }

    [Fact]
    public void RejectsTwoEquallyGoodTranslations()
    {
        var current = Reference.Concat(Reference.Select(s=>s with {Centroid=new(s.Centroid.X+10,s.Centroid.Y)})).ToArray();
        Assert.NotEqual(GateDisposition.Passed,Register(current).Gate.Disposition);
    }

    [Fact]
    public void StrongStationaryPatternCannotOutvoteStarsAfterResolvedMovement()
    {
        var fixedPattern = Enumerable.Range(0, 90).Select(i => Star(20 + i % 15 * 35, 20 + i / 15 * 35, 1000)).ToArray();
        StarCandidate[] moving = [Star(92,380), Star(182,412), Star(370,440), Star(510,365)];
        var shifted = moving.Select(s => s with { Centroid = new(s.Centroid.X - 55, s.Centroid.Y + 10) }).ToArray();
        var result = CatalogFieldRegistration.Measure(fixedPattern.Concat(moving).ToArray(),
            fixedPattern.Concat(shifted).ToArray(), new(400,300), new(-60,10), 100,2,700,600,
            new("slit",new(340,310),0,100,3,.5,"camera",1,1));
        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal(-55,result.Translation.X,8);
        Assert.Equal(345,result.Target.X,8); // measured, not commanded -60
        Assert.Equal(90,result.Gate.Metrics!["fixedReferenceExcluded"]);
    }

    [Fact]
    public void OnlyStationaryMatchesAfterLargeExpectedMoveAreNotProofOfNoMotion()
    {
        var result = CatalogFieldRegistration.Measure(Reference, Reference, new(150,150), new(-60,10), 100,2,
            300,300,new("slit",new(90,160),0,100,3,.5,"camera",1,1));
        Assert.NotEqual(GateDisposition.Passed,result.Gate.Disposition);
    }

    [Fact]
    public void SepParentMomentsArePreservedInsteadOfInventingUnknownWidth()
    {
        var parent = new SepPositionComponent(90,80,4.790056228637695,3.2826955318450928,
            834694,21984,40.819895838248414,0,65,191,[77,72,22,19],0,1);
        var image = new SepImageMeasurements(1,SepStarDetectionClient.Algorithm,300,300,[],false,false)
            { ParentComponents = [parent] };
        var star = Assert.Single(CatalogFieldRegistration.ReferenceStars(image));
        Assert.InRange(star.FwhmPixels,9.66,9.68);
        Assert.Equal(1-parent.B/parent.A,star.Ellipticity,12);
        Assert.True(G3OverlapRegistration.HasUsableMeasuredSourceShape(star));
    }

    [Theory]
    [InlineData(0,0)]
    [InlineData(1,-1)]
    [InlineData(1,2)]
    [InlineData(double.NaN,1)]
    public void InvalidNewSepMomentsCannotMasqueradeAsLegacyUnknownShape(double a,double b)
    {
        var image = new SepImageMeasurements(1,SepStarDetectionClient.Algorithm,300,300,[],false,false)
            { ParentComponents = [new(90,80,a,b,20000,3000,30,0,8,20,[87,77,7,7],0,1)] };
        Assert.Empty(CatalogFieldRegistration.ReferenceStars(image));
    }
}
