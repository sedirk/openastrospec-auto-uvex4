// Offline image-only adapter to the ALREADY BUILT production registration core.
// No ProjectReference: running this audit does not rebuild production or own hardware.
using System.Text.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using UvexAdv.Observatory;

if (args.Length != 2 || !args[1].EndsWith(".json",StringComparison.OrdinalIgnoreCase) || File.Exists(args[1]))
    throw new ArgumentException("Supply an existing offline input report and a NEW output .json path.");
var options=new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true};
using var input=JsonDocument.Parse(File.ReadAllText(args[0]));
var destination=Path.GetFullPath(args[1]);
if(input.RootElement.GetProperty("actorScope").GetString()!="offline-history-replay")
    throw new ArgumentException("Only an explicitly offline historical audit report is supported.");
foreach(var item in input.RootElement.GetProperty("cases").EnumerateArray())
foreach(var source in item.GetProperty("sourceVerification").EnumerateArray())
{
    var folder=Directory.GetParent(source.GetProperty("path").GetString()!)?.Parent?.Parent;
    if(folder is not null && destination.StartsWith(folder.FullName+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("Audit output must not be inside the immutable observations tree.");
}
var output=new Dictionary<string,object>();
foreach(var item in input.RootElement.GetProperty("cases").EnumerateArray())
{
    var frames=item.GetProperty("frames").EnumerateArray().ToArray();
    var allStars=frames.Select(f=>f.GetProperty("registrationStars").EnumerateArray().Select(s=>
        new StarCandidate(new(s.GetProperty("x").GetDouble(),s.GetProperty("y").GetDouble()),100,
            s.GetProperty("flux").GetDouble(),s.GetProperty("snr").GetDouble(),3,0,0,20)).ToArray()).ToArray();
    var transforms=new List<G3SimilarityTransform?>{G3SimilarityTransform.Identity};var results=new List<object>();
    for(var i=0;i<frames.Length;i++)
    {
        var clock=Stopwatch.StartNew();
        G3OverlapRegistrationResult? selected=null;G3SimilarityTransform? transform=i==0?G3SimilarityTransform.Identity:null;
        int? reference=null;var closureConsistent=true;
        var width=frames[i].GetProperty("width").GetInt32();var height=frames[i].GetProperty("height").GetInt32();
        if(i>0)
        {
            for(var prior=i-1;prior>=0;prior--)
            {
                if(transforms[prior] is null)continue;
                var candidate=G3OverlapRegistration.Register(allStars[prior],allStars[i],width,height);
                if(!candidate.Accepted)continue;
                var proposed=transforms[prior]!.Then(candidate.Transform!);
                if(selected is null){selected=candidate;transform=proposed;reference=prior;}
                else if(!G3OverlapGraphConsistency.Evaluate(transform!,proposed,width,height).Consistent){closureConsistent=false;break;}
            }
            if(!closureConsistent){transform=null;selected=null;reference=null;}
            transforms.Add(transform);
        }
        double[][]? matrix=transform is null?null:new[]{new[]{transform.A,-transform.B,transform.X},new[]{transform.B,transform.A,transform.Y},new[]{0d,0d,1d}};
        results.Add(new{file=frames[i].GetProperty("file").GetString(),registration=selected,referenceIndex=reference,
            sourceSha256=frames[i].GetProperty("sha256").GetString(),registrationMilliseconds=clock.Elapsed.TotalMilliseconds,
            cumulativeMatrix=matrix,closureConsistent,spatialOnly=true,temporalAuthorization=false});
    }
    output.Add(item.GetProperty("name").GetString()!,results);
}
output.Add("_context",new{actorScope="offline-history-replay",noHardwareIO=true,
    inputReportSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))),
    productionAssemblySha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(G3OverlapRegistration).Assembly.Location)))});
Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
using(var stream=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None))
    JsonSerializer.Serialize(stream,output,options);
