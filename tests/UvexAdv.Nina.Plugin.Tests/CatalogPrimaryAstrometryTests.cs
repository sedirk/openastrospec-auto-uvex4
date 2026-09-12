using System.Text.Json.Nodes;
using NINA.Astrometry;
using NINA.PlateSolving;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class CatalogPrimaryAstrometryTests
{
    private const string Response="""
    {"metadata":[{"name":"id"},{"name":"main_id"},
    {"name":"ra","unit":"deg","utype":"CS.spaceSys=ICRS CT.epoch=J2000"},
    {"name":"dec","unit":"deg","utype":"CS.spaceSys=ICRS CT.epoch=J2000"},
    {"name":"pmra","unit":"mas.yr-1"},{"name":"pmdec","unit":"mas.yr-1"},{"name":"coo_bibcode"}],
    "data":[["HIP 9640","* gam01 And",30.97480120653972,42.32972842352701,42.32,-49.3,"2007A&A...474..653V"]]}
    """;
    private static CatalogPrimaryAstrometry? Parse(string json)=>CatalogPrimaryAstrometryReader.Parse(json,"HIP 9640",
        30.980509610010348,42.328688904778076,new(2026,9,12,17,57,0,TimeSpan.Zero),"https://simbad.cds.unistra.fr/simbad/sim-tap/sync");

    [Fact]
    public void ExactIdEpochAndProperMotionProjectThroughTheActualNinaWcs()
    {
        var reference=Parse(Response)!;
        Assert.NotNull(reference);
        Assert.Equal(64,reference.ResponseSha256.Length);
        Assert.Equal(Response,reference.RawResponse);
        var solve=new PlateSolveResult { Success=true,Coordinates=new(31.000463075321672,42.318693147424064,Epoch.J2000,Coordinates.RAType.Degrees),
            Pixscale=.382428384173075,PositionAngle=252.585008722976 };
        var p=G3WcsTargetProjector.Project(new(reference.RightAscensionDegrees,reference.DeclinationDegrees,Epoch.J2000,Coordinates.RAType.Degrees),solve,1920,1080,"Platesolve3Solver");
        Assert.InRange(p.X,811.56,811.60);
        Assert.InRange(p.Y,402.47,402.51);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("duplicate")]
    [InlineData("frame")]
    [InlineData("unit")]
    [InlineData("pm")]
    [InlineData("distant")]
    public void InvalidCatalogueEvidenceCannotCorrectAReference(string problem)
    {
        var json=JsonNode.Parse(Response)!;
        switch(problem) {
            case "id": json["data"]![0]![0]="HIP 5447"; break;
            case "duplicate": json["data"]!.AsArray().Add(json["data"]![0]!.DeepClone()); break;
            case "frame": json["metadata"]![2]!["utype"]="FK5"; break;
            case "unit": json["metadata"]![4]!["unit"]="arcsec/yr"; break;
            case "pm": json["data"]![0]![4]=null; break;
            default: json["data"]![0]![2]=31.1; break;
        }
        Assert.Null(Parse(json.ToJsonString()));
    }
}
