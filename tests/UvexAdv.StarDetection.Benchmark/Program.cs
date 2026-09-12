using System.Text;
using System.Text.Json;
using UvexAdv.Observatory;

// Offline image arrays only. No FITS writer, camera, mount or SDK references.
using var input = Console.OpenStandardInput();
var header = new List<byte>();
int value;
while ((value = input.ReadByte()) != '\n' && value != -1)
{
    if (header.Count >= 4096) throw new InvalidDataException("Header too large.");
    header.Add((byte)value);
}
var request = JsonDocument.Parse(Encoding.UTF8.GetString(header.ToArray())).RootElement;
var width = request.GetProperty("width").GetInt32();
var height = request.GetProperty("height").GetInt32();
if (width < 16 || height < 16 || (long)width * height > 32_000_000) throw new InvalidDataException("Dimensions.");
var pixels = new ushort[checked(width * height)];
using var reader = new BinaryReader(input);
for (var i = 0; i < pixels.Length; i++) pixels[i] = reader.ReadUInt16();
if (args is ["catalog-short-replay"] or ["sep-catalog-short-replay", _, _] or ["sep-primary-replay", _, _])
{
    var count=request.GetProperty("frameCount").GetInt32();
    if (count is < 1 or > 3) throw new InvalidDataException("Bounded short frame count.");
    var prediction=new PixelPoint(request.GetProperty("predictionX").GetDouble(),request.GetProperty("predictionY").GetDouble());
    var vector=new PixelPoint(request.GetProperty("companionX").GetDouble(),request.GetProperty("companionY").GetDouble());
    var radius=request.GetProperty("radius").GetDouble();
    G3ShortPositionMeasurement? previous=null;
    string? previousHash=null;
    var results=new List<object>();
    for(var attempt=1;attempt<=count;attempt++)
    {
        if(attempt>1) for(var i=0;i<pixels.Length;i++) pixels[i]=reader.ReadUInt16();
        var hash=request.GetProperty("hashes")[attempt-1].GetString()!;
        var sep = args.Length == 3 ? await new SepStarDetectionClient().DetectAsync(
                new(args[1],args[2]),width,height,pixels,65520,CancellationToken.None) : null;
        var measured = args[0] == "sep-primary-replay"
            ? G3SepCatalogPrimaryPolicy.Measure(sep!,prediction,vector,radius)
            : sep is not null ? G3SepShortPositionPolicy.Measure(sep,prediction,radius,requireResolvedPair:true)
            : G3ShortPositionMeasurementPolicy.Measure(new(width,height,pixels,65520),prediction,radius,requireResolvedPsfCandidates:true);
        var resolved=args[0] == "sep-primary-replay" ? measured : G3ResolvedCompanionPositionPolicy.Resolve(measured,vector,radius,previous);
        var decision=G3ShortPositionMeasurementPolicy.EvaluateConfirmation(previous,resolved,previousHash,hash,attempt,radius);
        results.Add(new { measured,resolved,decision });
        if(!decision.RetainPreviousMeasurement) { previous=resolved; previousHash=hash; }
    }
    Console.WriteLine(JsonSerializer.Serialize(results,new JsonSerializerOptions {
        NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
}
else if (args is ["focus", _, _])
{
    var reference = request.TryGetProperty("reference", out var refs)
        ? refs.Deserialize<SepFocusReference[]>(new JsonSerializerOptions { PropertyNameCaseInsensitive=true }) : null;
    var measured = await new SepStarDetectionClient().MeasureFocusAsync(new(args[1],args[2]),width,height,pixels,65520,reference,CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(measured));
}
else if (args.Length == 2)
{
    var measurements = await new SepStarDetectionClient().DetectAsync(new(args[0], args[1]), width, height, pixels, 65520, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(measurements));
}
else
{
    var candidates = StarFieldDetector.Detect(new(width, height, pixels, 65520));
    Console.WriteLine(JsonSerializer.Serialize(candidates.Select(s => new { x = s.Centroid.X, y = s.Centroid.Y, flux = s.FluxAdu })));
}
