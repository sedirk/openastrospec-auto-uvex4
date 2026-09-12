using System.Text.Json.Nodes;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class SepStarDetectionClientTests
{
    private const string Valid = """
        {"schema":1,"algorithm":"sep-parent-energy-v3","width":128,"height":128,
         "components":[],"components_truncated":false,
         "parent_components":[],"parent_components_truncated":false,
         "sources":[{"x":64.2,"y":63.5,"r50":3,"r80":6,"flux":500,"peak":1200,
         "background":1000,"snr":20,"bbox":[54,53,20,20],"flags":[],"focus_eligible":true,
         "children":[{"x":64.2,"y":63.5}],"axis_ratio":0.3}],
         "target_identity_confirmed":false,"motion_authorized":false,"truncated":false}
        """;

    [Fact]
    public void KeepsShapeAndDeblendDiagnosticsWithoutConferringAuthority()
    {
        var result = SepStarDetectionClient.ParseAndValidate(Valid,128,128);
        Assert.Single(result.Sources);
        Assert.Equal(.3,result.Sources[0].Details!["axis_ratio"].GetDouble());
        Assert.False(result.Details!["truncated"].GetBoolean());
        Assert.False(result.MotionAuthorized);
    }

    [Theory]
    [InlineData("authority")]
    [InlineData("missing-authority")]
    [InlineData("identity")]
    [InlineData("dimensions")]
    [InlineData("coordinates")]
    [InlineData("bbox")]
    [InlineData("energy")]
    [InlineData("flags")]
    [InlineData("null-source")]
    [InlineData("missing-components")]
    [InlineData("missing-coverage")]
    [InlineData("missing-parents")]
    [InlineData("missing-parent-coverage")]
    public void RejectsInvalidWorkerContracts(string problem)
    {
        var root = JsonNode.Parse(Valid)!;
        var star = root["sources"]![0]!;
        switch(problem)
        {
            case "authority": root["motion_authorized"]=true; break;
            case "missing-authority": root.AsObject().Remove("motion_authorized"); break;
            case "identity": root["target_identity_confirmed"]=true; break;
            case "dimensions": root["width"]=256; break;
            case "coordinates": star["x"]=-1; break;
            case "bbox": star["bbox"]![2]=1000; break;
            case "energy": star["r80"]=1; break;
            case "flags": star["flags"]=new JsonArray("saturated"); break;
            case "null-source": root["sources"]![0]=null; break;
            case "missing-components": root.AsObject().Remove("components"); break;
            case "missing-coverage": root.AsObject().Remove("components_truncated"); break;
            case "missing-parents": root.AsObject().Remove("parent_components"); break;
            case "missing-parent-coverage": root.AsObject().Remove("parent_components_truncated"); break;
        }
        Assert.Throws<InvalidDataException>(()=>SepStarDetectionClient.ParseAndValidate(root.ToJsonString(),128,128));
    }

    [Fact]
    public async Task CancelledWorkAndInvalidInputCannotStartAProcess()
    {
        var client = new SepStarDetectionClient();
        var runtime = new SepDetectionRuntime("not-a-real-executable","not-a-worker");
        await Assert.ThrowsAsync<ArgumentException>(()=>client.DetectAsync(runtime,32,32,[],65520,CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>client.DetectAsync(runtime,32,32,new ushort[1024],65520,new(true)));
    }
}
