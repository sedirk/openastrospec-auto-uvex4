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
    public async Task Mid0Hd698ActualShortPairConfirmsTargetWithoutRequiringAnotherWholeFieldSolve()
    {
        var observations = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(observations)) return;
        var folder = Path.Combine(observations, "UVEX-20261025T162356Z-f5d2cbf782184c9", "evidence");
        var paths = new[] { "00136-", "00138-" }.Select(p => Assert.Single(Directory.GetFiles(folder, p + "*.fit"))).ToArray();
        var hashes = paths.Select(Digest).ToArray();
        Assert.Equal(new[] { "7BCF387F1123C4291E13864C165FDFBF721E206C05EF031C453822CC39C57431",
            "FD2C1450C9736FB4A1EFC82644047D4C31627169CADCBAC6F977FFAB22218BC8" }, hashes);
        var root = Repository();
        var runtime = new SepDetectionRuntime(Path.Combine(root, "output", "star-detection-venv", "Scripts", "python.exe"),
            Path.Combine(root, "scripts", "star-detection", "worker.py"));
        G3ShortPositionMeasurement? first = null;
        for (var i = 0; i < paths.Length; i++)
        {
            var f = QhyFitsCodec.Read(paths[i]);
            var sep = await new SepStarDetectionClient().DetectAsync(runtime, f.Width, f.Height, f.Pixels, 65520, default);
            var measured = G3SepShortPositionPolicy.Measure(sep, new(143.6689544249283, 824.6711754510916),
                100, 8, 1.5, previousSameSetting: first);
            Assert.Equal("G3_SEP_POSITION_MEASURED", measured.Gate.Code);
            var result = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(first, measured,
                i == 0 ? null : hashes[0], hashes[i], i + 1, 100, 3);
            Assert.Equal(i == 1, result.Accepted);
            if (i == 1) Assert.InRange(result.PositionSpreadPixels, 1.89, 1.90);
            first = measured;
        }
        Assert.Equal(hashes, paths.Select(Digest).ToArray());
    }

    [Fact]
    public void Mid0MosaicDriftIncludesImageHoldoutErrorWithoutAddingEncoderToleranceAgain()
    {
        var observations = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(observations)) return;
        var folder = Path.Combine(observations, "UVEX-20261025T161820Z-3eb35419525e4fa", "evidence");
        using var document = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(folder, "00126-*.json"))));
        var registration = document.RootElement.GetProperty("payload").GetProperty("registration");
        var transform = registration.GetProperty("transform");
        var motion = G3OverlapGraphConsistency.Evaluate(G3SimilarityTransform.Identity,
            new(transform.GetProperty("a").GetDouble(), transform.GetProperty("b").GetDouble(),
                transform.GetProperty("x").GetDouble(), transform.GetProperty("y").GetDouble()), 1920, 1080).MaximumCornerDisagreementPixels;
        var residual = registration.GetProperty("maximumHoldoutResidualPixels").GetDouble();
        // Use the independent native capture brackets, not the old rate to
        // reverse-engineer a time gap. Mount readback is not an optical error.
        using var first = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(folder, "00111-*.json"))));
        using var current = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(folder, "00131-*.json"))));
        var firstCapture = first.RootElement.GetProperty("payload");
        var currentTiming = current.RootElement.GetProperty("payload").GetProperty("timing");
        var gap = (currentTiming.GetProperty("earliestMidpointUtc").GetDateTimeOffset() -
            firstCapture.GetProperty("frameCompletedUtc").GetDateTimeOffset().AddSeconds(-2.5)).TotalSeconds;
        var old = 2.8779192443288903;
        var corrected = G3SparseFrameTimingPolicy.RegisteredDriftUpperBound(motion, residual, gap);
        Assert.True(corrected > motion / gap); // keep uncertainty; never call drift zero
        Assert.InRange(corrected, 0.1, old - .5);
        Assert.Throws<ArgumentOutOfRangeException>(() => G3SparseFrameTimingPolicy.RegisteredDriftUpperBound(motion, residual, 0));
    }

    [Fact]
    public void October25RawPixelsSelectShortRecheckWithoutRewritingOrClaimingRecoveredTarget()
    {
        var observations = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(observations)) return;
        var directory = Path.Combine(observations,"UVEX-20261025T110550Z-2c687c92880a466","evidence");
        var prefixes = new[] { "00034-", "00091-", "00097-", "00103-" };
        var paths = prefixes.Select(p => Assert.Single(Directory.GetFiles(directory,p+"*.fit"))).ToArray();
        var hashes = paths.Select(Digest).ToArray();
        Assert.Equal(new[] {
            "6224A712D0B48CCCD26AB5BA585A7BFF28A0610607FF6F224269289B3A5B632F",
            "EFC5687AD56F41FA299969DB720F51C359238E1BCA83D0B2265A84FE5F0FBC5B",
            "C6A891CACF92E14F0461F3DE327D3033DEE5C9D06947D174F6F2D26ECCBDE6F8",
            "C7A2DADF8D204DAB50A79AE8B127A905BFDBB2F28CDD0AF4744CB8EE9A8E8313" },hashes);
        var frames = paths.Select(QhyFitsCodec.Read).ToArray();
        var content = frames.Select(f => G3SolveProbeContentAnalyzer.Analyze(new MonochromeFrame(f.Width,f.Height,
            f.Pixels,G3FrameInputPolicy.ResolveEffectiveSaturationAdu(f.Pixels,4095)))).ToArray();
        Assert.Equal(new[] {4,9,0,0},content.Select(c => c.StellarMeasurement.DetectedStarCount));
        Assert.Equal(5000,G3SolveExposurePolicy.ShortRecheckMilliseconds(5000,10000,content[0],65520,5000,content[0]));
        Assert.Null(G3SolveExposurePolicy.ShortRecheckMilliseconds(5000,10000,content[1],65520,5000,content[1]));
        Assert.Equal(5000,G3SolveExposurePolicy.ShortRecheckMilliseconds(10000,15000,content[2],65520,5000,content[1]));
        Assert.Equal(G3SolveProbeContentAnalyzer.OverexposedGateCode,content[3].Gate.Code);
        Assert.Null(G3SolveExposurePolicy.ShortRecheckMilliseconds(15000,20000,content[3],65520,5000,content[1]));
        // The newly requested short recheck was NOT taken historically. This
        // test claims the decision, not invented later pixels or slit success.
        Assert.Equal(hashes,paths.Select(Digest).ToArray());
    }

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
