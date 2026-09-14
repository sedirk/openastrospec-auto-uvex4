using System.Net;
using System.Net.Http;
using System.Reflection;
using NINA.Astrometry;
using NINA.Equipment.Interfaces;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class StellariumImportContextTests
{
    private const string Info = """{"name":"Gaia DR3 2006109684151966720","type":"CustomObject","object-type":"SIMBAD; WR*","raJ2000":-25.8910877773934,"decJ2000":55.629439466874345}""";
    private const string Status = """{"time":{"jday":2461298.2,"deltaT":0.00078943},"location":{"planet":"Earth"}}""";
    private const string Properties = """{"StelCore.flagUseAberration":{"value":true},"StelCore.aberrationFactor":{"value":1},"StelCore.flagUseParallax":{"value":true},"StelCore.parallaxFactor":{"value":1}}""";

    private static NinaPlanetariumTargetSource Source(HttpClient http,double ra=334.1089122226066)
    {
        var planet=DispatchProxy.Create<IPlanetarium,ObservationTargetImportServiceTests.DynamicInterfaceProxy>();
        var p=(ObservationTargetImportServiceTests.DynamicInterfaceProxy)(object)planet;
        p.Values["get_Name"]="Stellarium";
        p.Values["get_CanGetRotationAngle"]=false;
        var dso=new DeepSkyObject("Gaia DR3 2006109684151966720",new Coordinates(ra,55.629439466874345,Epoch.J2000,Coordinates.RAType.Degrees),null);
        p.Values["GetTarget"]=Task.FromResult(dso);
        var factory=DispatchProxy.Create<IPlanetariumFactory,ObservationTargetImportServiceTests.DynamicInterfaceProxy>();
        ((ObservationTargetImportServiceTests.DynamicInterfaceProxy)(object)factory).Values["GetPlanetarium"]=planet;
        return new(factory,()=>new Uri("http://localhost:8090/"),http,_=>new(.00003,.00008,.000035));
    }

    [Fact]
    public async Task BindsNormalizedCoordinateAndMetadataFromReadOnlyContext()
    {
        using var handler=new Responses(); using var http=new HttpClient(handler);
        var result=await Source(http).CaptureAsync(CancellationToken.None);
        Assert.Equal(result.TargetBodyCoordinates!.RightAscensionDegrees,result.CatalogMetadata!.RightAscensionDegrees);
        Assert.NotEqual(334.1089122226066,result.TargetBodyCoordinates.RightAscensionDegrees);
        Assert.Equal("AstrometricJ2000",result.CatalogMetadata.CoordinateProvenance!.Convention);
        Assert.Equal(6,handler.Calls);
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("selection")]
    [InlineData("unavailable")]
    [InlineData("view-center")]
    public async Task DoesNotImportUnknownOrChangingApparentSemantics(string failure)
    {
        using var handler=new Responses(failure); using var http=new HttpClient(handler);
        var error=await Assert.ThrowsAsync<ObservationTargetImportException>(()=>Source(http,failure=="view-center"?334.2:334.1089122226066).CaptureAsync(CancellationToken.None));
        Assert.Equal("STELLARIUM_COORDINATE_CONVERSION_FAILED",error.Code);
    }

    private sealed class Responses(string? failure=null):HttpMessageHandler
    {
        public int Calls {get;private set;}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Calls++; Assert.Equal(HttpMethod.Get,request.Method);
            var path=request.RequestUri!.AbsolutePath;
            var body=path.EndsWith("status")?Status:path.EndsWith("list")?Properties:Info;
            if(failure=="settings"&&Calls==4)body=Properties.Replace("true","false");
            if(failure=="selection"&&Calls==6)body=Info.Replace("2006109684151966720","another object");
            return Task.FromResult(new HttpResponseMessage(failure=="unavailable"?HttpStatusCode.NotFound:HttpStatusCode.OK){Content=new StringContent(body)});
        }
    }
}
