using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using NINA.Astrometry;
using NINA.Equipment.Interfaces;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class StellariumNamelessDeepSkyImportTests
{
    // Observed IC 2149 response, 2026-10-08; no location or device data.
    private const double Ra = 89.10167360200106;
    private const double Dec = 46.102760262604605;
    private const string Aliases = "IC 2149 - PGC 2902418 - PK 166+10.1 - PN G166.1+10.4";
    private const string Status = """{"time":{"jday":2461321.2,"deltaT":0.00078943},"location":{"planet":"Earth"}}""";
    private const string Properties = """{"StelCore.flagUseAberration":{"value":true},"StelCore.aberrationFactor":{"value":1},"StelCore.flagUseParallax":{"value":true},"StelCore.parallaxFactor":{"value":1}}""";

    private static string Info(string? designations = Aliases, string? designation = null,
        string? name = "", string? localizedName = "", string type = "行星状星云",
        string subtype = "planetary nebula", double ra = Ra, bool found = true) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["name"] = name, ["localized-name"] = localizedName, ["designation"] = designation,
            ["designations"] = designations, ["found"] = found, ["type"] = type,
            ["object-type"] = subtype, ["raJ2000"] = ra, ["decJ2000"] = Dec, ["vmag"] = 10.78,
        });

    private static NinaPlanetariumTargetSource Source(HttpClient http)
    {
        var planetarium = DispatchProxy.Create<IPlanetarium, ObservationTargetImportServiceTests.DynamicInterfaceProxy>();
        var proxy = (ObservationTargetImportServiceTests.DynamicInterfaceProxy)(object)planetarium;
        proxy.Values["get_Name"] = "Stellarium";
        proxy.Values["get_CanGetRotationAngle"] = false;
        // Native import may expose a blank or previous display name; neither
        // may replace the catalogue identity in the verified selected response.
        proxy.Values["GetTarget"] = Task.FromResult(new DeepSkyObject("stale native alias",
            new Coordinates(Ra, Dec, Epoch.J2000, Coordinates.RAType.Degrees), null));
        var factory = DispatchProxy.Create<IPlanetariumFactory, ObservationTargetImportServiceTests.DynamicInterfaceProxy>();
        ((ObservationTargetImportServiceTests.DynamicInterfaceProxy)(object)factory).Values["GetPlanetarium"] = planetarium;
        return new(factory, () => new Uri("http://localhost:8090/"), http, _ => new(.00003, .00008, .000035));
    }

    [Fact]
    public async Task ObservedIc2149ImportsWithCatalogIdentityTypeAndNormalizedCoordinates()
    {
        using var handler = new Responses(Info());
        using var http = new HttpClient(handler);
        var result = await Source(http).CaptureAsync(CancellationToken.None);
        Assert.Equal("IC 2149", result.TargetName);
        Assert.Equal("IC 2149", result.CatalogId);
        Assert.Equal("planetary nebula", result.CatalogMetadata!.ObjectSubtype);
        Assert.Equal(10.78, result.CatalogMetadata.VisualMagnitude);
        Assert.Equal("AstrometricJ2000", result.CatalogMetadata.CoordinateProvenance!.Convention);
        Assert.NotEqual(Ra, result.TargetBodyCoordinates!.RightAscensionDegrees);
        Assert.Equal(result.TargetBodyCoordinates.RightAscensionDegrees, result.CatalogMetadata.RightAscensionDegrees);
        Assert.Equal(6, handler.Calls);
    }

    [Theory]
    [InlineData("NGC 6543 - PK 096+29.1", null, "NGC 6543")]
    [InlineData("PK 166-10.1 - PN G166.1-10.4", null, "PK 166-10.1")]
    [InlineData("PN G166.1-10.4", null, "PN G166.1-10.4")]
    [InlineData("M 31 - NGC 224", null, "M 31")]
    [InlineData(null, "IC 2149", "IC 2149")]
    [InlineData("PGC 2902418", "IC 2149", "IC 2149")]
    public async Task CatalogFallbackSupportsDeepSkyIdentifiersWithoutSplittingTheirSigns(
        string? aliases, string? designation, string expected)
    {
        using var handler = new Responses(Info(aliases, designation, name: null));
        using var http = new HttpClient(handler);
        var result = await Source(http).CaptureAsync(CancellationToken.None);
        Assert.Equal(expected, result.TargetName);
        Assert.Equal(expected, result.CatalogId);
    }

    [Fact]
    public async Task NamedNebulaKeepsCommonNameAndSeparateCatalogId()
    {
        using var handler = new Responses(Info("NGC 6543 - PK 096+29.1", name: "Cat's Eye Nebula", localizedName: "猫眼星云"));
        using var http = new HttpClient(handler);
        var result = await Source(http).CaptureAsync(CancellationToken.None);
        Assert.Equal("Cat's Eye Nebula", result.TargetName);
        Assert.Equal("NGC 6543", result.CatalogId);
    }

    [Theory]
    [InlineData("designation")]
    [InlineData("alias")]
    [InlineData("type")]
    [InlineData("coordinates")]
    [InlineData("deselected")]
    public async Task ChangingSelectionCannotAttachIdentityToPreviousCoordinates(string change)
    {
        var after = change switch
        {
            "designation" => Info("IC 2150"),
            "alias" => Info(Aliases.Replace("PGC 2902418", "PGC 9999999")),
            "type" => Info(subtype: "galaxy"),
            "coordinates" => Info(ra: Ra + 0.01),
            _ => Info(found: false),
        };
        using var handler = new Responses(Info(), after);
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ObservationTargetImportException>(
            () => Source(http).CaptureAsync(CancellationToken.None));
        Assert.Equal("STELLARIUM_COORDINATE_CONVERSION_FAILED", error.Code);
    }

    [Fact]
    public async Task NoNameOrDesignationHasAnIdentityErrorAndDoesNotBorrowNativeAlias()
    {
        using var handler = new Responses(Info(designations: null));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ObservationTargetImportException>(
            () => Source(http).CaptureAsync(CancellationToken.None));
        Assert.Equal("STELLARIUM_TARGET_IDENTITY_MISSING", error.Code);
    }

    [Fact]
    public async Task CoordinateMismatchWithNativeTargetIsStillRejected()
    {
        using var handler = new Responses(Info(ra: Ra + 0.1));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ObservationTargetImportException>(
            () => Source(http).CaptureAsync(CancellationToken.None));
        Assert.Equal("STELLARIUM_COORDINATE_CONVERSION_FAILED", error.Code);
    }

    private sealed class Responses(string info, string? after = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            var path = request.RequestUri!.AbsolutePath;
            var body = path.EndsWith("status") ? Status : path.EndsWith("list") ? Properties :
                Calls == 6 ? after ?? info : info;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
