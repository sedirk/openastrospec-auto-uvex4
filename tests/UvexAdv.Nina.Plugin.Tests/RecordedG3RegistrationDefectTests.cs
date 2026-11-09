using System.IO;
using System.Security.Cryptography;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class RecordedG3RegistrationDefectTests
{
    [Theory]
    [InlineData("UVEX-20261030T185035Z-66935dd7c1a0438", "00269-", "02B59F28E20A1AD1B2A444551F8DD7273F9474133556319F0FF09E5BEC6B8872",
        "00287-", "506AC1D82F9804CC85DB8361F6F3C0FBB101CEFC0F4B0519316B6BA504798C53")]
    [InlineData("UVEX-20261030T193103Z-a136401f54a54f3", "00360-", "17D1940D925A0E3548C6EA50D02067FAF12D8769CEA5326514D930DFEB6B2DE6",
        "00377-", "D784C0CD9AF239A3EF336EAF15F0ECECFEC77018208708AF52DE8D90E0259358")]
    public async Task LatestFailedFieldsDoNotBuildMosaicsFromFixedDetectorDefects(string run,
        string firstPrefix,string firstSha,string secondPrefix,string secondSha)
    {
        var observations=Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(observations)) return;
        var root=Repository();
        var runtime=new SepDetectionRuntime(Path.Combine(root,"output","star-detection-venv","Scripts","python.exe"),
            Path.Combine(root,"scripts","star-detection","worker.py"));
        var directory=Path.Combine(observations,run,"evidence");
        var paths=new[]{firstPrefix,secondPrefix}.Select(p => Assert.Single(Directory.GetFiles(directory,p+"*.fit"))).ToArray();
        var hashes=paths.Select(Digest).ToArray();
        Assert.Equal(new[]{firstSha,secondSha},hashes);
        var stars=new List<IReadOnlyList<StarCandidate>>();
        foreach (var path in paths)
        {
            var raw=QhyFitsCodec.Read(path);
            var measured=await new SepStarDetectionClient().DetectAsync(runtime,raw.Width,raw.Height,raw.Pixels,65535,default);
            var sources=CatalogFieldRegistration.ReferenceStars(measured);
            Assert.All(sources,s => Assert.True(s.FwhmPixels>0));
            // Same detector location in two unrelated target fields: it is
            // not evidence of celestial continuity merely because it repeats.
            var fixedDefect=Assert.Single(sources, s => Distance(s.Centroid,new(1793,722))<2);
            Assert.False(G3OverlapRegistration.HasUsableMeasuredSourceShape(fixedDefect));
            stars.Add(sources);
        }
        var old=G3OverlapRegistration.Register(stars[0].Select(s=>s with{FwhmPixels=0}).ToArray(),
            stars[1].Select(s=>s with{FwhmPixels=0}).ToArray(),1920,1080);
        Assert.True(old.Accepted,old.Reason); // reproduces the former false-positive evidence
        var actual=G3OverlapRegistration.Register(stars[0],stars[1],1920,1080);
        Assert.False(actual.Accepted);
        Assert.Equal("G3_OVERLAP_UNCONFIRMED",actual.Code);
        Assert.Null(actual.Transform); // caller continues other bounded acquisition routes
        Assert.Equal(hashes,paths.Select(Digest).ToArray());
    }

    private static double Distance(PixelPoint a,PixelPoint b)=>Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2));
    private static string Digest(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string Repository()
    {
        var dir=new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName,"UVEX-ADV.sln"))) dir=dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
