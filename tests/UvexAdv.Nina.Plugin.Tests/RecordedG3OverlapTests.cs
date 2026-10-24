using System.IO;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class RecordedG3OverlapTests
{
    [Fact]
    public async Task PreviousSuccessfulHd219937RunRetainsIndependentFieldRegistration()
    {
        var observations = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(observations)) return;
        var root = Repository();
        var runtime = new SepDetectionRuntime(Path.Combine(root,"output","star-detection-venv","Scripts","python.exe"),
            Path.Combine(root,"scripts","star-detection","worker.py"));
        var paths = new[] { "00044-", "00049-", "00051-" }.Select(prefix => Assert.Single(Directory.GetFiles(Path.Combine(observations,
            "UVEX-20261014T112329Z-1862faa86ec84de","evidence"),prefix+"*.fit"))).ToArray();
        var hashes = paths.Select(Digest).ToArray();
        Assert.Equal(new[] { "3D24B991D36A53903F9E3E1EBE064498D7E4D2C1947DDE0DA567C826B397BF51",
            "BE99996721FC652BB20BFFAC600FCF194A26EF5F72F15D58BAE5A358C6ED51ED",
            "AFF7C24E0B29BD28C6EA0EF01CA30E6C39FD4BA795D4B9E758F3B3211C2756CA" },hashes);
        var stars = new List<IReadOnlyList<StarCandidate>>();
        var frames = paths.Select(QhyFitsCodec.Read).ToArray();
        foreach (var frame in frames)
        {
            var image = await new SepStarDetectionClient().DetectAsync(runtime,frame.Width,frame.Height,frame.Pixels,65535,CancellationToken.None);
            stars.Add(CatalogFieldRegistration.ReferenceStars(image));
        }
        var exclusions = new[] { new G3RegistrationExclusion(new(817,427),130) };
        var inputs = new List<G3MosaicInput> { new(new(1920,1080,frames[0].Pixels),G3SimilarityTransform.Identity,paths[0],hashes[0],2) };
        for (var i=1;i<stars.Count;i++)
        {
            var result = G3OverlapRegistration.Register(stars[i-1],stars[i],1920,1080,exclusions,exclusions);
            Assert.True(result.Accepted,$"{Path.GetFileName(paths[i])}: {result.Reason}; stars {stars[i-1].Count}/{stars[i].Count}");
            Assert.True(result.HoldoutPairs.Count>=2);
            inputs.Add(new(new(1920,1080,frames[i].Pixels),inputs[^1].ReferenceToFrame.Then(result.Transform!),paths[i],hashes[i],2));
        }
        var composition=G3MosaicComposer.Compose(inputs);
        WriteDerivedReplayOnly("hd219937-success",composition,inputs);
        VerifyActualPl3MosaicReturnWhenProvided(composition,inputs,frames[^1]);
        Assert.Equal(hashes,paths.Select(Digest).ToArray());
    }

    [Fact]
    public async Task ActualHd219937FieldRegistersWithoutTargetAndBuildsRealPixelMosaic()
    {
        var observations = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(observations)) return;
        var root = Repository();
        var runtime = new SepDetectionRuntime(Path.Combine(root, "output", "star-detection-venv", "Scripts", "python.exe"),
            Path.Combine(root, "scripts", "star-detection", "worker.py"));
        var paths = new[] { "00075-", "00080-", "00082-", "00084-", "00086-" }
            .Select(p => Assert.Single(Directory.GetFiles(Path.Combine(observations,
                "UVEX-20261023T123550Z-ed9c198f39b64e7", "evidence"), p + "*.fit"))).ToArray();
        var hashes = paths.Select(Digest).ToArray();
        Assert.Equal("AC523C01AA1E3BAF0E07D73C71E79E765BD61520C75A202A83BA831781BA65A0", hashes[0]);
        Assert.Equal("DC1B798D0084B89F8894E0402F88F84499B028C2F9C6DACBCAA235E5F82FE8CA", hashes[2]);
        var frames = paths.Select(QhyFitsCodec.Read).ToArray();
        var stars = new List<IReadOnlyList<StarCandidate>>();
        foreach (var frame in frames)
        {
            var measurements = await new SepStarDetectionClient().DetectAsync(runtime, frame.Width, frame.Height,
                frame.Pixels, 65535, CancellationToken.None);
            stars.Add(CatalogFieldRegistration.ReferenceStars(measurements));
        }
        var exclusion = new[] { new G3RegistrationExclusion(new(975, 425), 100) };
        var inputs = new List<G3MosaicInput> { new(new(1920,1080,frames[0].Pixels), G3SimilarityTransform.Identity, paths[0],hashes[0],2) };
        for (var i = 1; i < frames.Length; i++)
        {
            G3OverlapRegistrationResult? result = null; var registeredTo = 0;
            for (var prior = i-1; prior >= 0; prior--)
            {
                var candidate = G3OverlapRegistration.Register(stars[prior],stars[i],1920,1080,exclusion,exclusion);
                if (!candidate.Accepted) continue;
                if (result is null) { result=candidate;registeredTo=prior; }
                else
                {
                    var first=inputs[registeredTo].ReferenceToFrame.Then(result.Transform!);
                    var alternate=inputs[prior].ReferenceToFrame.Then(candidate.Transform!);
                    var closure=G3OverlapGraphConsistency.Evaluate(first,alternate,1920,1080);
                    Assert.True(closure.Consistent,$"Historical independent graph paths disagree by {closure.MaximumCornerDisagreementPixels:F3}px");
                }
            }
            Assert.NotNull(result);
            Assert.True(result.Accepted, $"{Path.GetFileName(paths[i])}: {result.Reason}; stars {stars[0].Count}/{stars[i].Count}");
            Assert.Equal(3,result.FitPairs.Count); Assert.True(result.HoldoutPairs.Count >= 2);
            Assert.InRange(result.MaximumHoldoutResidualPixels,0,2.5);
            var composite = inputs[registeredTo].ReferenceToFrame.Then(result.Transform!);
            var brightCorePrediction = composite.Apply(new(970.597222222222,420.675925925926));
            if (i == 2)
            {
                Assert.InRange(brightCorePrediction.X,974,980); Assert.InRange(brightCorePrediction.Y,427,434);
                // The old accepted noise peak is not the registered bright structure.
                Assert.True(Math.Sqrt(Math.Pow(brightCorePrediction.X-922.4573229873909,2)+
                    Math.Pow(brightCorePrediction.Y-446.868574199806,2)) > 50);
            }
            inputs.Add(new(new(1920,1080,frames[i].Pixels),composite,paths[i],hashes[i],2));
        }
        var mosaic = G3MosaicComposer.Compose(inputs);
        Assert.Equal(5,mosaic.SourceContributions.Count);
        Assert.True(mosaic.DerivedNotRaw); Assert.InRange(mosaic.CoveredFraction,.9,1);
        WriteDerivedReplayOnly("hd219937-failed",mosaic,inputs);
        VerifyActualFailedPl3WhenProvided(mosaic);
        Assert.Equal(hashes,paths.Select(Digest).ToArray());
    }

    [Fact]
    public async Task ActualHd220526NonoverlappingHistoricalFramesDoNotInventARegistration()
    {
        var observations = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(observations)) return;
        var root = Repository();
        var runtime = new SepDetectionRuntime(Path.Combine(root,"output","star-detection-venv","Scripts","python.exe"),
            Path.Combine(root,"scripts","star-detection","worker.py"));
        var paths = new[] { "00033-", "00037-" }.Select(prefix => Assert.Single(Directory.GetFiles(Path.Combine(observations,
            "UVEX-20261023T124609Z-4f199a0a5f07404","evidence"),prefix+"*.fit"))).ToArray();
        var hashes = paths.Select(Digest).ToArray(); var stars = new List<IReadOnlyList<StarCandidate>>();
        foreach (var path in paths)
        {
            var frame = QhyFitsCodec.Read(path);
            var image = await new SepStarDetectionClient().DetectAsync(runtime,frame.Width,frame.Height,frame.Pixels,65535,CancellationToken.None);
            stars.Add(CatalogFieldRegistration.ReferenceStars(image));
        }
        Assert.False(G3OverlapRegistration.Register(stars[0],stars[1],1920,1080).Accepted);
        Assert.Equal(hashes,paths.Select(Digest).ToArray());
    }

    private static string Digest(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void VerifyActualPl3MosaicReturnWhenProvided(G3MosaicComposition mosaic,List<G3MosaicInput> inputs,QhyFitsReadResult current)
    {
        var path=Environment.GetEnvironmentVariable("UVEX_MOSAIC_PL3_SUCCESS_REPORT");
        if(string.IsNullOrWhiteSpace(path)) return;
        using var report=JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(report.RootElement.GetProperty("sourcesUnchanged").GetBoolean());
        VerifyReportDerivedPixels(report,mosaic);
        var solve=report.RootElement.GetProperty("solve");
        Assert.Equal(0,solve.GetProperty("exitCode").GetInt32());Assert.False(solve.GetProperty("timedOut").GetBoolean());
        var lines=solve.GetProperty("resultText").GetString()!.Split('\n');Assert.Equal("True",lines[0].Trim());
        var center=lines[1].Split(',').Select(x=>double.Parse(x,CultureInfo.InvariantCulture)).ToArray();
        var geometry=lines[2].Split(',').Select(x=>double.Parse(x,CultureInfo.InvariantCulture)).ToArray();
        var result=new NINA.PlateSolving.PlateSolveResult{Success=true,
            Coordinates=new NINA.Astrometry.Coordinates(center[0]*180/Math.PI,center[1]*180/Math.PI,NINA.Astrometry.Epoch.J2000,NINA.Astrometry.Coordinates.RAType.Degrees),
            Pixscale=206264.806247/geometry[0]/2,PositionAngle=geometry[1]};
        var restored=G3RegisteredWcs.RestoreFromMosaic(result,mosaic,"Platesolve3Solver",inputs[^1].ReferenceToFrame,current.Width,current.Height);
        var catalogue=new NINA.Astrometry.Coordinates(349.92255278016,23.82526295019,NINA.Astrometry.Epoch.J2000,NINA.Astrometry.Coordinates.RAType.Degrees);
        var prediction=G3WcsTargetProjector.Project(catalogue,restored,current.Width,current.Height,"Platesolve3Solver");
        var frame=new MonochromeFrame(current.Width,current.Height,current.Pixels);
        var identified=SlitTargetIdentifier.Identify(frame,StarFieldDetector.Detect(frame),prediction,30,8,1.5);
        // Independent historical measurement in the same immutable 00051 frame,
        // not used in registration, composition, PL3 or WCS representation fitting.
        var recorded=new PixelPoint(857.1296829971182,459.0806916426513);
        Assert.InRange(Math.Sqrt(Math.Pow(prediction.X-recorded.X,2)+Math.Pow(prediction.Y-recorded.Y,2)),0,3);
        // This LONG guiding exposure still contains two similar peaks. Production
        // must acquire the mandatory independent short-frame pair; do not waive
        // existing identity/multi-lobe rules to make a replay claim science success.
        Assert.Equal("TARGET_AMBIGUOUS",identified.Gate.Code);
        Assert.NotEqual(GateDisposition.Passed,identified.Gate.Disposition);
    }

    private static void VerifyActualFailedPl3WhenProvided(G3MosaicComposition mosaic)
    {
        var path=Environment.GetEnvironmentVariable("UVEX_MOSAIC_PL3_FAILED_REPORT");
        if(string.IsNullOrWhiteSpace(path)) return;
        using var report=JsonDocument.Parse(File.ReadAllText(path));VerifyReportDerivedPixels(report,mosaic);
        Assert.True(report.RootElement.GetProperty("sourcesUnchanged").GetBoolean());
        var solve=report.RootElement.GetProperty("solve");
        Assert.Equal(0,solve.GetProperty("exitCode").GetInt32());Assert.False(solve.GetProperty("timedOut").GetBoolean());
        Assert.Equal("False",solve.GetProperty("resultText").GetString()!.Split('\n')[0].Trim());
        Assert.False(solve.GetProperty("success").GetBoolean());
    }

    private static void VerifyReportDerivedPixels(JsonDocument report,G3MosaicComposition composition)
    {
        var input=Assert.Single(report.RootElement.GetProperty("inputs").EnumerateObject());
        Assert.Equal(input.Value.GetString()!.ToUpperInvariant(),Digest(input.Name));
        var actual=QhyFitsCodec.Read(input.Name);
        Assert.Equal(composition.Width,actual.Width);Assert.Equal(composition.Height,actual.Height);
        Assert.Equal(composition.Pixels,actual.Pixels);
    }

    private static void WriteDerivedReplayOnly(string label,G3MosaicComposition mosaic,IReadOnlyList<G3MosaicInput> inputs)
    {
        var output=Environment.GetEnvironmentVariable("UVEX_MOSAIC_REPLAY_OUTPUT");
        if(string.IsNullOrWhiteSpace(output)) return;
        var folder=Path.GetFullPath(output);
        var observations=Path.GetFullPath(Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS")!);
        Assert.False(folder.StartsWith(observations,StringComparison.OrdinalIgnoreCase));
        Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,label+"-"+Guid.NewGuid().ToString("N")+".fits");
        string Card(string key,string value)=>(key.PadRight(8)+"= "+value.PadLeft(20)).PadRight(80);
        var header=string.Concat(new[]{Card("SIMPLE","T"),Card("BITPIX","16"),Card("NAXIS","2"),
            Card("NAXIS1",mosaic.Width.ToString(CultureInfo.InvariantCulture)),Card("NAXIS2",mosaic.Height.ToString(CultureInfo.InvariantCulture)),
            Card("BZERO","32768"),Card("BSCALE","1"),Card("DERIVED","T"),Card("IMAGETYP","'DIAGNOSTIC'"),
            Card("NPARENTS",mosaic.SourceSha256.Count.ToString(CultureInfo.InvariantCulture)),"END".PadRight(80)});
        using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write))
        {
            var bytes=Encoding.ASCII.GetBytes(header.PadRight((header.Length+2879)/2880*2880));stream.Write(bytes);
            var pixels=new byte[mosaic.Pixels.Length*2];
            for(var i=0;i<mosaic.Pixels.Length;i++) BinaryPrimitives.WriteInt16BigEndian(pixels.AsSpan(i*2,2),unchecked((short)(mosaic.Pixels[i]-32768)));
            stream.Write(pixels); stream.Write(new byte[(2880-pixels.Length%2880)%2880]);
        }
        File.WriteAllText(path+".json",JsonSerializer.Serialize(new{derivedNotRaw=true,productionComposer=typeof(G3MosaicComposer).FullName,
            path,sha256=Digest(path),mosaic.Width,mosaic.Height,mosaic.ReferencePixelOffset,mosaic.CoveredFraction,
            mosaic.SourceContributions,mosaic.SourceSha256,parents=inputs.Select(i=>new{i.SourceId,i.SourceSha256,i.ReferenceToFrame,i.ExposureSeconds})},new JsonSerializerOptions{WriteIndented=true}));
    }
    private static string Repository()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName,"AGENTS.md"))) folder = folder.Parent;
        return folder?.FullName ?? throw new InvalidOperationException("Repository not found for read-only historical replay.");
    }
}
