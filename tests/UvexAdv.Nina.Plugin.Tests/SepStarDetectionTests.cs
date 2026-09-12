using System.ComponentModel.Composition;
using NINA.Core.Interfaces;
using NINA.Image.ImageAnalysis;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class SepStarDetectionTests
{
    private static SepMeasuredSource Star(double x, double y, double radius=3, double flux=1000) =>
        new(x,y,radius,radius*2,flux,1200,1000,20,[(int)x-4,(int)y-4,9,9],[],true);
    private static SepImageMeasurements Frame(params SepMeasuredSource[] stars) =>
        new(1,SepStarDetectionClient.Algorithm,128,128,stars,false,false);

    [Fact]
    public void ExportsActualNinaSelectionContract()
    {
        Assert.IsAssignableFrom<IStarDetection>(new SepStarDetection());
        Assert.Contains(typeof(SepStarDetection).GetCustomAttributes(typeof(ExportAttribute),false)
            .Cast<ExportAttribute>(), a=>a.ContractType==typeof(IPluggableBehavior));
    }

    [Fact]
    public void UsesEnergyRadiiKeepsRejectedMeasurementsAndUpdatesNativeAnalysis()
    {
        var measured=Frame(Star(40,40,3),Star(70,70,7),Star(90,90) with { Flags=["saturated"],FocusEligible=false });
        var p=new StarDetectionParams();
        var result=SepStarDetection.ToNinaResult(measured,p);
        Assert.Equal(2,result.DetectedStars);
        Assert.Equal(5,result.AverageHFR);
        Assert.Equal(2,result.HFRStdDev);
        var detector=new SepStarDetection();
        var analysis=Assert.IsType<SepStarDetectionAnalysis>(detector.CreateAnalysis());
        detector.UpdateAnalysis(analysis,p,result);
        Assert.Same(measured,analysis.Measurements);
        Assert.Equal(3,analysis.Measurements!.Sources.Length);
        Assert.Equal(5,analysis.HFR);
    }

    [Fact]
    public void RespectsNativeCropAndBrightestCount()
    {
        var measured=Frame(Star(10,10,3,5000),Star(64,64,4,2000),Star(75,75,5,1000));
        var result=SepStarDetection.ToNinaResult(measured,new() { UseROI=true, InnerCropRatio=.5,OuterCropRatio=1,NumberOfAFStars=1 });
        Assert.Single(result.StarList);
        Assert.Equal(4,result.AverageHFR);
    }

    [Fact]
    public void MissingOrAmbiguousReferenceStarsDoNotBecomeValidFocusPoints()
    {
        var p=new StarDetectionParams { MatchStarPositions=[new(40,40),new(90,90)] };
        var result=SepStarDetection.ToNinaResult(Frame(Star(41,40)),p);
        Assert.Empty(result.StarList);
        Assert.True(double.IsNaN(result.AverageHFR));
        p.MatchStarPositions=[new(40,40)];
        result=SepStarDetection.ToNinaResult(Frame(Star(39,40),Star(41,40)),p);
        Assert.Empty(result.StarList);
        result=SepStarDetection.ToNinaResult(Frame(Star(41,40)),p);
        Assert.Single(result.StarList);
    }
}
