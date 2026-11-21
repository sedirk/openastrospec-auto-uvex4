using System.Security.Cryptography;
using System.Text.Json;
using NINA.Astrometry;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;
using Xunit;
using Xunit.Abstractions;

namespace UvexAdv.Nina.Plugin.Tests;

[Collection("Read-only NINA ephemeris boundary")]
public sealed class RecordedNovember20FieldTests(ITestOutputHelper output)
{
    private sealed class LocalOriginalsAttribute : TheoryAttribute
    {
        public LocalOriginalsAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS")))
                Skip = "Private historical originals require UVEX_REPLAY_OBSERVATIONS.";
        }
    }

    [LocalOriginals]
    [InlineData("UVEX-20261120T151948Z-25e9beacfd1f4c9", "00351", "22C08C156334DAE0F5AEB2B3EC1002DC40FF61152EF2A9FB71A7F3FD0625A5F6", 6)]
    [InlineData("UVEX-20261120T153550Z-2b1909efac4d4e3", "00086", "30FC1C6C62E916275153BCF23408D94913FFAFBDB51629484A660DB6642D090A", 6)]
    public async Task BroadProposalUsesOriginalFailedHintAndUnfilteredNativeCentroids(string run, string prefix, string hash, int count)
    {
        var dir=Path.Combine(Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS")!,run,"evidence");
        using var e=JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(dir,prefix+"*.json"))));
        var p=e.RootElement.GetProperty("payload");
        Assert.False(p.GetProperty("solveSucceeded").GetBoolean());
        var path=Path.Combine(dir,Path.GetFileName(e.RootElement.GetProperty("source").GetProperty("absolutePath").GetString())!);
        Assert.Equal(hash,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        ReadOnlyNinaAstrometry.Initialize();
        var hint=new Coordinates(p.GetProperty("solveHintRaDegrees").GetDouble(),p.GetProperty("solveHintDecDegrees").GetDouble(),Epoch.J2000,Coordinates.RAType.Degrees);
        var repo=new DirectoryInfo(AppContext.BaseDirectory);
        while(repo!=null && !File.Exists(Path.Combine(repo.FullName,"AGENTS.md"))) repo=repo.Parent;
        var worker=new MeasuredAstapRecovery(configuredWorker:Path.Combine(repo!.FullName,"scripts","star-detection","astap_recovery.py"));
        var result=await worker.SolveAsync(path,hash,1920,1080,hint,.38375,2,Path.Combine(repo.FullName,"tmp","november21-adapter-replay"),default);
        Assert.NotNull(result.Result);
        using var receipt=JsonDocument.Parse(File.ReadAllText(result.EvidencePath!));
        var root=receipt.RootElement;
        Assert.Equal("median3-bin4-broad-proposal",root.GetProperty("solverSelection").GetProperty("mode").GetString());
        Assert.Equal(count,root.GetProperty("matches").GetArrayLength());
        Assert.False(root.GetProperty("targetIdentityConfirmed").GetBoolean());
        Assert.False(root.GetProperty("motionAuthorized").GetBoolean());
        Assert.Equal(hash,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    [LocalOriginals]
    [InlineData("UVEX-20261120T155400Z-f8c2eb5584834b0")]
    public void Hd26380OneRejectedSplitDoesNotReplaceUnsplitShortPixelWitness(string run)
    {
        var dir=Path.Combine(Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS")!,run,"evidence");
        var opts=new JsonSerializerOptions{PropertyNameCaseInsensitive=true};
        (G3ShortPositionMeasurement measurement,string hash,DateTimeOffset utc) Read(string prefix,G3ShortPositionMeasurement? anchor)
        {
            using var e=JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(dir,prefix+"*.json"))));
            var p=e.RootElement.GetProperty("payload");
            var path=Path.Combine(dir,Path.GetFileName(p.GetProperty("shortFramePath").GetString())!);
            var hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            Assert.Equal(hash,p.GetProperty("shortFrameSha256").GetString()!.ToUpperInvariant());
            Assert.Equal(100,p.GetProperty("exposureMilliseconds").GetInt32());
            var point=p.GetProperty("originalPlanPrediction").Deserialize<PixelPoint>(opts)!;
            var result=G3SepShortPositionPolicy.Measure(p.GetProperty("sepMeasurements").Deserialize<SepImageMeasurements>(opts)!,
                point,100,previousSameSetting:anchor);
            return (result,hash,p.GetProperty("shortBinding").GetProperty("frameCompletedUtc").GetDateTimeOffset());
        }
        var a=Read("00086",null);
        var b=Read("00088",a.measurement);
        var d=G3ShortPositionMeasurementPolicy.EvaluateConfirmation(a.measurement,b.measurement,a.hash,b.hash,3,100,4);
        Assert.True(G3TransientSplitWitness.MayKeepUnsplitAnchor(a.measurement,b.measurement,d,true,true));
        Assert.False(G3TransientSplitWitness.MayKeepUnsplitAnchor(a.measurement,b.measurement,d,true,false));
        Assert.False(G3TransientSplitWitness.MayKeepUnsplitAnchor(a.measurement,b.measurement,d,false,true));
        Assert.False(G3TransientSplitWitness.MayPairNext(b.measurement,a.utc,b.utc));
        var c=Read("00091",a.measurement);
        Assert.True(G3TransientSplitWitness.MayPairNext(c.measurement,a.utc,c.utc));
        Assert.False(G3TransientSplitWitness.MayPairNext(c.measurement,a.utc,a.utc.AddSeconds(9)));
        var fresh=G3ShortPositionMeasurementPolicy.EvaluateConfirmation(a.measurement,c.measurement,a.hash,c.hash,4,100,4);
        Assert.True(fresh.Accepted);
        Assert.InRange(fresh.PositionSpreadPixels,3.90,3.95);
        Assert.False(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(b.measurement,c.measurement,b.hash,c.hash,4,100,4).Accepted);
        using var wcs=JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(dir,"00077*.json"))));
        Assert.True(c.utc<wcs.RootElement.GetProperty("payload").GetProperty("validUntilUtc").GetDateTimeOffset());
    }

    [LocalOriginals]
    [InlineData("00373")]
    [InlineData("00376")]
    public void FinalHd224060ShortFramesCannotSelfCertifyAgainstTheLongCore(string prefix)
    {
        var dir = Path.Combine(Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS")!,
            "UVEX-20261120T121854Z-834d9e6bbea74b0", "evidence");
        JsonDocument Record(string p) => JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(dir, p+"*.json"))));
        using var shortReceipt=Record(prefix);
        using var slitReceipt=Record("00035");
        var p=shortReceipt.RootElement.GetProperty("payload");
        var opts=new JsonSerializerOptions { PropertyNameCaseInsensitive=true };
        var slit=slitReceipt.RootElement.GetProperty("payload").GetProperty("measurement").GetProperty("physicalApertureGeometry").Deserialize<SlitGeometry>(opts)!;
        var longPath=Path.Combine(dir,Path.GetFileName(p.GetProperty("longFramePath").GetString()!));
        Assert.Equal("41D1B9D8C6E8D2578F6A38EEF6B70D44D4F7CA4E165BA77C0613EDDBE6C5C009",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(longPath))));
        var shortPath=Path.Combine(dir,Path.GetFileName(p.GetProperty("shortFramePath").GetString()!));
        Assert.Equal(p.GetProperty("shortFrameSha256").GetString()!.ToUpperInvariant(),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(shortPath))));
        var raw=QhyFitsCodec.Read(longPath);
        var frame=new MonochromeFrame(raw.Width,raw.Height,raw.Pixels,G3FrameInputPolicy.ResolveEffectiveSaturationAdu(raw.Pixels,4095));
        var reference=G3SolveProbeContentAnalyzer.Analyze(frame).StellarMeasurement.Stars;
        var sep=p.GetProperty("sep").Deserialize<SepImageMeasurements>(opts)!;
        var anchor=p.GetProperty("longTargetAnchor").Deserialize<PixelPoint>(opts)!;
        var current=CatalogFieldRegistration.ReferenceStars(sep);
        var fit=G3TargetTrackPolicy.Register(reference,current,anchor,anchor,slit,raw.Width,raw.Height,100,default);
        output.WriteLine($"reference {reference.Count}, current {current.Count}, fit {fit.FitPairs.Count}, holdouts {fit.HoldoutPairs.Count}: {fit.Code}");
        // No independent geometry: the bright parent near the old aim point
        // is NOT allowed to move its own recognition window.
        Assert.False(fit.Accepted);
    }

    [LocalOriginals]
    [InlineData("UVEX-20261120T112832Z-f5e432457721429", "00058", "CF065C84EBDA303FAF946E5091873407F837520F4E62793E65412EB5959BF2B3", 9)]
    [InlineData("4bd9d0186e414b64ae1bb116d1b9fcb4", "00370", "B038AE9F1273730ED22466331D060B720CD2DDEA087780D87080B53A36579FA1", 5)]
    public async Task OriginalPixelsProduceIndependentDetectorWcsNotMosaicMotionAuthority(string run, string prefix, string expectedHash, int matches)
    {
        var folder = Path.Combine(Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS")!, run, "evidence");
        using var record = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(folder, prefix+"*.json"))));
        var p = record.RootElement.GetProperty("payload");
        var path = Path.Combine(folder, Path.GetFileName(p.GetProperty("currentFrame").GetString()!));
        Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        ReadOnlyNinaAstrometry.Initialize();
        var hint = new Coordinates(p.GetProperty("inferredCentreRaDegrees").GetDouble(), p.GetProperty("inferredCentreDecDegrees").GetDouble(),
            Epoch.J2000, Coordinates.RAType.Degrees);
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !File.Exists(Path.Combine(repo.FullName,"AGENTS.md"))) repo=repo.Parent;
        Assert.NotNull(repo);
        var worker = new MeasuredAstapRecovery(configuredWorker:Path.Combine(repo.FullName,"scripts","star-detection","astap_recovery.py"));
        var result = await worker.SolveAsync(path, expectedHash, 1920, 1080, hint, p.GetProperty("pixscale").GetDouble(), 2,
            Path.Combine(repo.FullName,"tmp","november20-adapter-replay"), default);
        Assert.NotNull(result.Result);
        using var receipt = JsonDocument.Parse(File.ReadAllText(result.EvidencePath!));
        var r = receipt.RootElement;
        Assert.Equal("sep-multiscale-astap-raw-verified-v4", r.GetProperty("algorithm").GetString());
        Assert.Equal(matches, r.GetProperty("matches").GetArrayLength());
        Assert.False(r.GetProperty("motionAuthorized").GetBoolean());
        Assert.False(r.GetProperty("targetIdentityConfirmed").GetBoolean());
        Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }
}
