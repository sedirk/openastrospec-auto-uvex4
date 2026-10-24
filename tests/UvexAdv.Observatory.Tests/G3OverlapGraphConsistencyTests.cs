using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3OverlapGraphConsistencyTests
{
    [Fact]
    public void IndependentMeasuredPathsMustCloseAtCornersNotOnlyAtImageCenter()
    {
        var first=new G3SimilarityTransform(1,0,10,20);
        Assert.True(G3OverlapGraphConsistency.Evaluate(first,new(1,0,12,21),1920,1080).Consistent);
        // A rotation about the center can hide in a center-only comparison.
        var angle=.01;var a=Math.Cos(angle);var b=Math.Sin(angle);
        var aroundCenter=new G3SimilarityTransform(a,b,10+960-a*960+b*540,20+540-b*960-a*540);
        var result=G3OverlapGraphConsistency.Evaluate(first,aroundCenter,1920,1080);
        Assert.False(result.Consistent);Assert.True(result.MaximumCornerDisagreementPixels>6);
    }
    [Fact]
    public void AliasTranslationCannotBeSelectedByTheFirstMatchingPrior()
    {
        Assert.False(G3OverlapGraphConsistency.Evaluate(new(1,0,0,0),new(1,0,30,0),1920,1080).Consistent);
    }
    [Fact]
    public void InvalidNumericTransformsCannotCloseARealPixelLoop()
    {
        Assert.False(G3OverlapGraphConsistency.Evaluate(G3SimilarityTransform.Identity,new(double.NaN,0,0,0),1920,1080).Consistent);
    }
}
