using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3SepShortPositionPolicyTests
{
    private static SepPositionComponent Component(double x,double y) =>
        new(x,y,8,2,10000,3000,30,0,20,40,[(int)x-10,(int)y-10,21,21],1,1);
    private static SepImageMeasurements Image(params SepPositionComponent[] c) =>
        new(1,SepStarDetectionClient.Algorithm,1024,768,[],false,false) { Components=c };
    private static readonly PixelPoint Prediction = new(510,400);

    [Fact]
    public void ElongatedPsfUsesSepPositionButAlwaysRequiresIndependentFrame()
    {
        var m=G3SepShortPositionPolicy.Measure(Image(Component(500,390)),Prediction,100);
        Assert.Equal("G3_SEP_POSITION_MEASURED",m.Gate.Code);
        Assert.True(m.RequiresIndependentRepeat);
        Assert.Equal(0,m.Identification.Target!.FwhmPixels);
        Assert.False(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(null,m,null,new('a',64),1,100).Accepted);
        Assert.True(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(m,m,new('a',64),new('b',64),2,100).Accepted);
        Assert.False(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(m,m,new('a',64),new('a',64),2,100).Accepted);
    }

    [Theory]
    [InlineData("saturation")]
    [InlineData("snr")]
    [InlineData("support")]
    [InlineData("edge")]
    [InlineData("flag")]
    public void InvalidComponentsCannotProvidePosition(string reason)
    {
        var c=Component(500,390);
        c=reason switch {
            "saturation" => c with { SaturatedFraction=.01 },
            "snr" => c with { Snr=2 },
            "support" => c with { RawSupportPixels=1 },
            "edge" => c with { Bbox=[0,380,510,21] },
            _ => c with { SepFlags=2 }
        };
        Assert.Equal("G3_SEP_UNMEASURED",G3SepShortPositionPolicy.Measure(Image(c),Prediction,100).Gate.Code);
    }

    [Fact]
    public void IncompleteCoverageNeverTurnsIntoAUniqueTarget()
    {
        var m=G3SepShortPositionPolicy.Measure(Image(Component(500,390)) with { ComponentsTruncated=true },Prediction,100);
        Assert.Equal("G3_SEP_INVALID_COVERAGE",m.Gate.Code);
        Assert.False(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(null,m,null,new('a',64),1,100).RetryAllowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SepKnotsCannotAcquireIdentityFromAChanceSignedPair(bool swapped)
    {
        var a=Component(500,390) with { Flux=swapped ? 100 : 10000 };
        var b=Component(496,415) with { Flux=swapped ? 10000 : 100 };
        var m=G3SepShortPositionPolicy.Measure(Image(a,b,Component(470,390)),Prediction,100,requireResolvedPair:true);
        Assert.Null(m.Identification.Target);
        var resolved=G3ResolvedCompanionPositionPolicy.Resolve(m,new(-4,25),100);
        Assert.Equal("G3_SHORT_CATALOG_PRIMARY_NOT_MEASURED",resolved.Gate.Code);
        Assert.Null(resolved.Identification.Target);
        // A second plausible ordered pair is real ambiguity, not an excuse to
        // choose the brightest or the first SEP index.
        var twoPairs=G3SepShortPositionPolicy.Measure(Image(a,b,Component(470,390),Component(466,415)),Prediction,100,requireResolvedPair:true);
        Assert.Equal("G3_SHORT_CATALOG_PRIMARY_NOT_MEASURED",G3ResolvedCompanionPositionPolicy.Resolve(twoPairs,new(-4,25),100).Gate.Code);
    }

    [Fact]
    public void RepeatedSepPairsWithoutAstrometryCannotEstablishIdentity()
    {
        G3ShortPositionMeasurement Measure(params SepPositionComponent[] components) =>
            G3SepShortPositionPolicy.Measure(Image(components),Prediction,100,requireResolvedPair:true);
        var first=G3ResolvedCompanionPositionPolicy.Resolve(Measure(Component(500,390),Component(496,415)),new(-4,25),100);
        var middle=G3ResolvedCompanionPositionPolicy.Resolve(Measure(Component(496,415)),new(-4,25),100,first);
        var decision=G3ShortPositionMeasurementPolicy.EvaluateConfirmation(first,middle,new('a',64),new('b',64),2,100);
        Assert.True(decision.RetainPreviousMeasurement);
        Assert.False(decision.Accepted);
        var third=G3ResolvedCompanionPositionPolicy.Resolve(Measure(Component(501,391),Component(497,416)),new(-4,25),100,first);
        Assert.False(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(first,third,new('a',64),new('c',64),3,100).Accepted);
    }
}
