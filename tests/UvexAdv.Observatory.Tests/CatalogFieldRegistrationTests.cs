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
}
