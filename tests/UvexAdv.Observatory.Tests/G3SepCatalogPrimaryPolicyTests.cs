using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3SepCatalogPrimaryPolicyTests
{
    private static readonly PixelPoint Prediction=new(811.5747,402.4852), Vector=new(-4.1746,24.753);
    private static SepPositionComponent Parent(double x=808.72768,double y=407.27273)=>
        new(x,y,8,2,1126773,30336,1088,0,60,100,[(int)x-15,(int)y-15,31,31],0,1);
    private static SepImageMeasurements Image(params SepPositionComponent[] parents)=>
        new(1,SepStarDetectionClient.Algorithm,1920,1080,[],false,false) { Components=[],ParentComponents=parents };
    private static G3ShortPositionMeasurement Measure(SepImageMeasurements image)=>
        G3SepCatalogPrimaryPolicy.Measure(image,Prediction,Vector,100);

    [Fact]
    public void RecordedUnsaturatedWholeParentsConfirmWithoutSeeingCompanion()
    {
        var first=Measure(Image(Parent()));
        var second=Measure(Image(Parent(807.79031,408.40297)));
        Assert.Equal("G3_SEP_CATALOG_PRIMARY_REGION_MEASURED",first.Gate.Code);
        Assert.InRange(first.PositionSpreadPixels,5,6);
        Assert.Equal(0,first.Identification.Target!.FwhmPixels);
        Assert.False(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(null,first,null,new('a',64),1,100).Accepted);
        Assert.True(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(first,second,new('a',64),new('b',64),2,100).Accepted);
        Assert.False(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(first,second,new('a',64),new('a',64),2,100).Accepted);
    }

    [Fact]
    public void FalseWingNoisePairCannotDisplaceTheWholeParent()
    {
        var image=Image(Parent(809.709,408.8)) with { Components=[Parent(799.9445,418.9364),Parent(794.7631,443.912)] };
        var result=Measure(image);
        Assert.Equal(new PixelPoint(809.709,408.8),result.Identification.Target!.Centroid);
        Assert.Null(Measure(Image() with { Components=image.Components }).Identification.Target);
    }

    [Theory]
    [InlineData("companion")]
    [InlineData("ambiguous")]
    [InlineData("saturated")]
    [InlineData("edge")]
    [InlineData("truncated")]
    [InlineData("missing")]
    [InlineData("flags")]
    [InlineData("support")]
    public void InvalidOrAmbiguousParentNeverBecomesPrimary(string problem)
    {
        var image=problem switch {
            "companion"=>Image(Parent(Prediction.X+Vector.X,Prediction.Y+Vector.Y)),
            "ambiguous"=>Image(Parent(),Parent(816,404)),
            "saturated"=>Image(Parent() with { SaturatedFraction=.001 }),
            "edge"=>Image(Parent() with { Bbox=[0,390,830,35] }),
            "truncated"=>Image(Parent()) with { ParentComponentsTruncated=true },
            "missing"=>Image(Parent()) with { ParentComponents=null },
            "flags"=>Image(Parent() with { SepFlags=1 }),
            _=>Image(Parent() with { RawSupportPixels=1 }) };
        var m=Measure(image);
        Assert.Null(m.Identification.Target);
        Assert.NotEqual(GateDisposition.Passed,m.Gate.Disposition);
    }
}
