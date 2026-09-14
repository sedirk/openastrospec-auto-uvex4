using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3SepShortPositionPolicyTests
{
    private static SepPositionComponent Component(double x,double y) =>
        new(x,y,8,2,10000,3000,30,0,20,40,[(int)x-10,(int)y-10,21,21],1,1);
    private static SepImageMeasurements Image(params SepPositionComponent[] c) =>
        new(1,SepStarDetectionClient.Algorithm,1024,768,[],false,false)
        {
            Components=c.Select((p,i)=>p with { ParentId=i+1 }).ToArray(),
            ParentComponents=c.Select((p,i)=>p with { ParentId=i+1, SepFlags=p.SepFlags & ~1 }).ToArray()
        };
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
        Assert.Equal(reason == "saturation" ? G3ShortExposurePolicy.SaturatedCode : "G3_SEP_UNMEASURED",
            G3SepShortPositionPolicy.Measure(Image(c),Prediction,100).Gate.Code);
    }

    [Fact]
    public void SaturatedStrongRegionCannotDisappearLeavingAWeakUniqueCandidate()
    {
        var strong = Component(500,390) with { SaturatedFraction=.012, Snr=5000, Npix=2287 };
        var weak = Component(480,360) with { Flux=543, Snr=6.3, Npix=10, RawSupportPixels=3 };
        var m = G3SepShortPositionPolicy.Measure(Image(strong,weak),Prediction,100,minimumSnr:5);
        Assert.Equal(G3ShortExposurePolicy.SaturatedCode,m.Gate.Code);
        Assert.Null(m.Identification.Target);
        Assert.Equal(1,m.Gate.Metrics!["sepValidRoiComponents"]);
        Assert.Equal(.012,m.Gate.Metrics["sepMaximumRoiSaturationFraction"]);
    }

    [Fact]
    public void ClippedParentVetoesUnsaturatedDeblendKnotButOutsideRegionDoesNot()
    {
        var parent = Component(500,390) with { SaturatedFraction=.01 };
        Assert.Equal(G3ShortExposurePolicy.SaturatedCode,G3SepShortPositionPolicy.Measure(
            Image(Component(480,360)) with { ParentComponents=[parent] },Prediction,100).Gate.Code);
        Assert.Equal("G3_SEP_POSITION_MEASURED",G3SepShortPositionPolicy.Measure(
            Image(Component(500,390),parent with { X=100,Y=100 }),Prediction,100).Gate.Code);
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
    public void ParentCoverageMustBeCompleteWithoutChildFallback(bool truncated)
    {
        var image=Image(Component(500,390));
        image=truncated ? image with { ParentComponentsTruncated=true } : image with { ParentComponents=null };
        Assert.Equal("G3_SEP_INVALID_COVERAGE",G3SepShortPositionPolicy.Measure(image,Prediction,100).Gate.Code);
    }

    private static SepImageMeasurements SplitImage(double x,double y)
    {
        var parent=Component(x,y) with { SepFlags=0,ParentId=88 };
        return Image() with { ParentComponents=[parent], Components=[
            Component(x+.8,y-2.7) with { ParentId=88 },
            Component(x+2.6,y+3.6) with { ParentId=88 }] };
    }

    [Fact]
    public void RecordedSplitVegaParentConfirmsWithIndependentUnsplitFrameWithoutRelaxingFourPixels()
    {
        var prediction=new PixelPoint(853.5627314820472,396.84492770850295);
        var first=G3SepShortPositionPolicy.Measure(SplitImage(810.9094389867533,413.4011818819125),prediction,100);
        var second=G3SepShortPositionPolicy.Measure(Image(Component(812.0915243047176,414.08427404546944)),prediction,100);
        Assert.Equal("G3_SEP_POSITION_MEASURED",first.Gate.Code);
        Assert.Equal(new PixelPoint(810.9094389867533,413.4011818819125),first.Identification.Target!.Centroid);
        Assert.Single(first.ResolvedCandidates!);
        Assert.Equal(2,first.Gate.Metrics!["sepSelectedParentChildCount"]);
        Assert.False(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(null,first,null,new('a',64),3,100,5).Accepted);
        var result=G3ShortPositionMeasurementPolicy.EvaluateConfirmation(first,second,new('a',64),new('b',64),4,100,5);
        Assert.True(result.Accepted);
        Assert.InRange(result.PositionSpreadPixels,1.365,1.366);
        Assert.Equal(4,result.Gate.Metrics!["maximumShortRepeatSeparationPixels"]);
        Assert.True(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(second,first,new('b',64),new('c',64),4,100,5).Accepted);
        var fifth=G3SepShortPositionPolicy.Measure(Image(Component(811.3777751057378,409.6643695593816)),prediction,100);
        Assert.False(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(second,fifth,new('b',64),new('c',64),5,100,5).Accepted);
    }

    [Fact]
    public void RepeatedSplitParentsCannotSilentlyMergeAPersistentBinary()
    {
        var first=G3SepShortPositionPolicy.Measure(SplitImage(500,390),Prediction,100);
        // IDs are frame-local segmentation labels, not persistent identity.
        var next=SplitImage(501,391);
        next=next with { ParentComponents=next.ParentComponents!.Select(c=>c with { ParentId=642 }).ToArray(),
            Components=next.Components!.Select(c=>c with { ParentId=642 }).ToArray() };
        var second=G3SepShortPositionPolicy.Measure(next,Prediction,100);
        var result=G3ShortPositionMeasurementPolicy.EvaluateConfirmation(first,second,new('a',64),new('b',64),2,100);
        Assert.False(result.Accepted);
        Assert.True(result.RetryAllowed);
        Assert.Equal("G3_SEP_PARENT_BLEND_UNRESOLVED",result.Gate.Code);
        Assert.False(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(first,second,new('a',64),new('b',64),3,100).RetryAllowed);
        Assert.Null(G3SepShortPositionPolicy.Measure(SplitImage(500,390),Prediction,100,requireResolvedPair:true).Identification.Target);
    }

    [Fact]
    public void DistinctParentsRemainAmbiguousAndAreNotMergedByTheShortPolicy()
    {
        var result=G3SepShortPositionPolicy.Measure(Image(Component(507,395),Component(513,405)),Prediction,100);
        Assert.Equal("G3_SEP_AMBIGUOUS",result.Gate.Code);
        Assert.Null(result.Identification.Target);
        Assert.Equal(2,result.Gate.Metrics!["sepValidRoiParents"]);
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
