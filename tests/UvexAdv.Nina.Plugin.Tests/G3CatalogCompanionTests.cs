using System.Text.Json;
using NINA.Astrometry;
using NINA.PlateSolving;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3CatalogCompanionTests
{
    [Fact]
    public void PrimaryCompanionPrimarySequenceRetainsOriginalComponentAndConfirmsThirdFrame()
    {
        var pair=RecordedPair(35920,6320);
        var vector=new PixelPoint(-4.146779905192034,24.76000749970234);
        // Recorded .184 sequence translated to the same origin: the middle
        // frame's generic nearest-WCS shortcut chose B, not the measured A.
        var first=G3ResolvedCompanionPositionPolicy.Resolve(pair,vector,100);
        G3ShortPositionMeasurement Single(StarCandidate star,string code="G3_SHORT_UNSATURATED_POSITION_MEASURED") =>
            new(GateResult.Pass(code,"measurement"),first.Identification with { Target=star },true,1,[star]);
        var secondary=Single(pair.ResolvedCandidates![1]);
        var rejected=G3ResolvedCompanionPositionPolicy.Resolve(secondary,vector,100,first);
        Assert.Equal("G3_SHORT_CATALOG_PRIMARY_NOT_MEASURED",rejected.Gate.Code);
        var decision=G3ShortPositionMeasurementPolicy.EvaluateConfirmation(first,rejected,new('a',64),new('b',64),2,100);
        Assert.False(decision.Accepted);
        Assert.True(decision.RetryAllowed);
        Assert.True(decision.RetainPreviousMeasurement);
        var primary=pair.ResolvedCandidates[0] with { Centroid=new(pair.ResolvedCandidates[0].Centroid.X+1.91857031857,
            pair.ResolvedCandidates[0].Centroid.Y-1.36266511266) };
        var third=G3ResolvedCompanionPositionPolicy.Resolve(Single(primary),vector,100,first);
        Assert.Equal("G3_SHORT_CATALOG_COMPANION_PRIMARY_TRACKED",third.Gate.Code);
        Assert.True(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(first,third,new('a',64),new('c',64),3,100).Accepted);
        Assert.False(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(first,third,new('a',64),new('a',64),3,100).Accepted);
        Assert.Equal("G3_SHORT_CATALOG_PRIMARY_NOT_MEASURED",G3ResolvedCompanionPositionPolicy.Resolve(Single(primary),vector,100).Gate.Code);
        Assert.Equal("G3_SHORT_CATALOG_PRIMARY_NOT_MEASURED",G3ResolvedCompanionPositionPolicy.Resolve(Single(primary,"TARGET_IDENTIFIED"),vector,100,first).Gate.Code);
        var distant=Single(primary with { Centroid=new(primary.Centroid.X+10,primary.Centroid.Y) });
        Assert.Equal("G3_SHORT_CATALOG_PRIMARY_NOT_MEASURED",G3ResolvedCompanionPositionPolicy.Resolve(distant,vector,100,first).Gate.Code);
    }

    [Fact]
    public void ProductionRequestsResolvedPsfsBeforeAnyNearestStarShortcut()
    {
        var source=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Sources","RealObservationStageRunner.CatalogShortPosition.cs"));
        Assert.Contains("G3SepCatalogPrimaryPolicy.Measure",source);
        Assert.Contains("G3SepShortPositionPolicy.Measure",source);
        Assert.DoesNotContain("G3ResolvedCompanionPositionPolicy.Resolve",source);
    }

    [Theory]
    [InlineData(32128, 7104)]
    [InlineData(7104, 32128)]
    public void RecordedAlmachPairUsesSignedCatalogueGeometryNotBrightness(double firstPeak, double secondPeak)
    {
        var primary = new Coordinates(30.980509610010348, 42.328688904778076, Epoch.J2000, Coordinates.RAType.Degrees);
        var pa = 63 * Math.PI / 180;
        var companion = new Coordinates(primary.RADegrees + 9.6 * Math.Sin(pa) / (3600 * Math.Cos(primary.Dec*Math.PI/180)),
            primary.Dec + 9.6 * Math.Cos(pa) / 3600, Epoch.J2000, Coordinates.RAType.Degrees);
        var solve = new PlateSolveResult { Success=true, Coordinates=new(31.002185961309493,42.319379666903025,Epoch.J2000,Coordinates.RAType.Degrees),
            Pixscale=.38295873602498115, PositionAngle=252.454376510512 };
        var p = G3WcsTargetProjector.Project(primary,solve,1920,1080,"Platesolve3Solver");
        var c = G3WcsTargetProjector.Project(companion,solve,1920,1080,"Platesolve3Solver");
        var original = RecordedPair(firstPeak, secondPeak);
        var result = G3ResolvedCompanionPositionPolicy.Resolve(original,new(c.X-p.X,c.Y-p.Y),100);
        Assert.Equal("G3_SHORT_CATALOG_COMPANION_PRIMARY_MEASURED", result.Gate.Code);
        Assert.Equal(original.ResolvedCandidates![0],result.Identification.Target);
        Assert.InRange(result.Gate.Metrics!["catalogCompanionVectorResidualPixels"],0,2);
        Assert.False(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(null,result,null,new('a',64),1,100).Accepted);
        Assert.True(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(result,result,new('a',64),new('b',64),2,100).Accepted);
        Assert.False(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(result,result,new('a',64),new('a',64),2,100).Accepted);
        Assert.Equal(original,G3ResolvedCompanionPositionPolicy.Resolve(original,new(25,0),100));
        Assert.Equal(original,G3ResolvedCompanionPositionPolicy.Resolve(original,new(double.NaN,0),100));
        Assert.Equal(original,G3ResolvedCompanionPositionPolicy.Resolve(original,new(1,1),100));
    }

    [Theory]
    [InlineData("HIP 9640",30.98051,42.328689,2019,true)]
    [InlineData("HIP 9640 B",30.98051,42.328689,2019,false)]
    [InlineData("HIP 5447",30.98051,42.328689,2019,false)]
    [InlineData("HIP 9640",30.99,42.328689,2019,false)]
    [InlineData("HIP 9640",30.98051,42.328689,1990,false)]
    [InlineData("HIP 9640",30.98051,42.328689,2030,false)]
    public void CatalogueReferenceMustMatchLockedSelectionAndHaveRecentBoundedGeometry(string id,double ra,double dec,int year,bool accepted)
    {
        using var document=JsonDocument.Parse($$"""
            {"name":"HIP 9640","type":"Star","star-type":"double-star","raJ2000":30.98051,"decJ2000":42.328689,
             "wds-separation":9.6,"wds-position-angle":63,"wds-year":{{year}}}
            """);
        Assert.Equal(accepted,StellariumCompanionReferenceReader.Parse(document.RootElement,id,ra,dec,new('a',64),new(2026,9,13,0,0,0,TimeSpan.Zero)) is not null);
    }

    private static G3ShortPositionMeasurement RecordedPair(double firstPeak,double secondPeak)
    {
        StarCandidate Star(double x,double y,double peak)=>new(new(x,y),peak,10000,40,4.5,.15,0,390);
        var stars=new[]{Star(814.8952922077923,390.3682359307359,firstPeak),Star(810.4619047619047,413.8666666666666,secondPeak)};
        return new(GateResult.Unknown("G3_SHORT_UNSATURATED_AMBIGUOUS","recorded two candidates"),
            TargetIdentification.FromCatalogWcs(new(831.1274497434641,422.7511223124043),1920,1080,"formal WCS"),true,0,stars);
    }
}
