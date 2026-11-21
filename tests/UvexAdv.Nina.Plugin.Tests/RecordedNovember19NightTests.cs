using System.Security.Cryptography;
using System.Text.Json;
using UvexAdv.Observatory;
using UvexAdv.Phd2;
using UvexAdv.Qhy.Core;
using Xunit;
using Xunit.Abstractions;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class RecordedNovember19NightTests(ITestOutputHelper output)
{
    private sealed class ObservatoryImagesTheoryAttribute : TheoryAttribute
    {
        public ObservatoryImagesTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS")))
                Skip = "Private observatory FITS are not distributed: set UVEX_REPLAY_OBSERVATIONS for read-only pixel replay.";
        }
    }

    // Images remain at the observatory. The ordinary tests below run without
    // that archive; set UVEX_REPLAY_OBSERVATIONS to additionally exercise pixels.
    [ObservatoryImagesTheory]
    [InlineData("153213Z-0aae8a4ed5fd403", "00174", "836D51B9668D975D1894CFAD0C6FA50010E1411C01BD4B07FDCB1D2FB3B015BE",817.5104,427.9432,814.5264,426.2725)]
    [InlineData("155242Z-ddb9a6af5c82451", "00196", "CCFB65DF397B19AD8895D221C8CE23EDB30CE4A996F731ED472FBF4147FFEEDE",831.6426,436.4898,831.6426,436.4898)]
    [InlineData("163053Z-9e413bafbd29497", "00164", "880F7E300F7713EC224AB599DB3882BD006FD9AEFE8B051DEE4792637266124E",799.9388,416.7530,796.4991,411.3472)]
    [InlineData("163902Z-c2a4118262b14e8", "00153", "291EE7D1AF232EC6BD83C02E0C7FB4182D0B394250D487340083B785F3F6FAE8",808.8387,417.6311,804.1502,415.7689)]
    [InlineData("190936Z-cf4091d8ee444e8", "00084", "C608419957A235A87A7CCD58DADCF712E2DF2520BF51740AB59C90498692569D",815.6872,424.2520,815.6872,424.2520)] // successful 11 Cam, same check as a control
    public void GenuineSelectionPixelsCanCorroborateTheDistinctShortPair(string run,string prefix,string hash,
        double x,double y,double hx,double hy)
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS")!;
        var path = Assert.Single(Directory.GetFiles(Path.Combine(root, "UVEX-20261119T"+run,"evidence"),prefix+"-*.fit"));
        static string Digest(string file) { using var stream=File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
        Assert.Equal(hash,Digest(path));
        if (prefix != "00084") // The successful control used a formal WCS, not a sparse pair.
        {
            var evidence = Path.GetDirectoryName(path)!;
            var pairFile = Directory.EnumerateFiles(evidence,"*-g3-catalog-short-position-confirmation.json")
                .Where(p=>string.CompareOrdinal(Path.GetFileName(p),prefix)<0).Order(StringComparer.Ordinal).Last();
            var guideFile = Directory.EnumerateFiles(evidence,"*-g3-sparse-retained-pixels.json")
                .Where(p=>string.CompareOrdinal(Path.GetFileName(p),prefix)>0).Order(StringComparer.Ordinal).First();
            using var pairJson=JsonDocument.Parse(File.ReadAllText(pairFile));
            using var guideJson=JsonDocument.Parse(File.ReadAllText(guideFile));
            var pair=pairJson.RootElement.GetProperty("payload");
            var guide=guideJson.RootElement.GetProperty("payload");
            Assert.True(pair.GetProperty("accepted").GetBoolean());
            Assert.Contains(pair.GetProperty("astrometryAuthority").GetString(),new[]{"RegisteredNeighbourWcs","RegisteredMosaicWcs"});
            var binding = pair.GetProperty("shortBinding");
            var shortDone=binding.GetProperty("frameCompletedUtc").GetDateTimeOffset();
            var centre=pair.GetProperty("measured").GetProperty("target").GetProperty("centroid");
            Assert.InRange(Math.Abs(centre.GetProperty("x").GetDouble()-x),0,.001);
            Assert.InRange(Math.Abs(centre.GetProperty("y").GetDouble()-y),0,.001);
            string LocalFile(JsonElement record,string key) => Path.Combine(evidence,Path.GetFileName(record.GetProperty(key).GetString()!));
            Assert.Equal(pair.GetProperty("previousHash").GetString()!.ToUpperInvariant(),Digest(LocalFile(pair,"previousFramePath")));
            Assert.Equal(binding.GetProperty("frameSha256").GetString()!.ToUpperInvariant(),Digest(LocalFile(binding,"framePath")));
            Assert.Equal(hash,guide.GetProperty("sha").GetString()!.ToUpperInvariant());
            Assert.True(guide.GetProperty("captureTimingKnown").GetBoolean());
            var timing=guide.GetProperty("captureTiming");
            var nextStart=timing.GetProperty("captureRequestedUtc").GetDateTimeOffset();
            var nextEnd=timing.GetProperty("captureCompletedUtc").GetDateTimeOffset();
            Assert.True(shortDone <= nextStart && nextStart < nextEnd && nextEnd <= shortDone.AddSeconds(30));
        }
        var read=QhyFitsCodec.Read(path);
        var frame=new MonochromeFrame(read.Width,read.Height,read.Pixels,G3FrameInputPolicy.ResolveEffectiveSaturationAdu(read.Pixels,4095));
        var stars=G3StellarFocusAnalyzer.Analyze(frame).Stars;
        var measured=G3SparseGuideAncestry.Corroborate(frame,stars,new(x,y),new(hx,hy),20,100,8,1.5);
        output.WriteLine($"{run}: {measured.Gate.Code}, corroborated centre {measured.Target?.Centroid}");
        Assert.True(measured.Gate.Disposition==GateDisposition.Passed, measured.Gate.Message);
        // A perfectly good star in the guide FITS is not evidence for the wrong target.
        Assert.Null(G3SparseGuideAncestry.Corroborate(frame,stars,new(x+70,y),new(hx,hy),20,100,8,1.5).Target);
        Assert.Equal(hash,Digest(path));
    }

    [Theory]
    [InlineData("typed-skip")]
    [InlineData("typed-cleanup-failure")]
    [InlineData("late-unsafe")]
    public async Task UncorroboratedGuideFrameRunsNextNativeTargetOnlyWithCheckedStop(string mode)
    {
        var frame=new MonochromeFrame(128,128,new ushort[128*128],65535);
        var rejected=G3SparseGuideAncestry.Corroborate(frame,[],new(64,64),new(64,64),20,100,8,1.5);
        Assert.Equal(GateFailureScope.TargetQuality,rejected.Gate.FailureScope);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(mode,rejected.Gate.Code,rejected.Gate,ObservationStage.PlaceTargetOnSlit);
    }

    [Theory]
    [InlineData(19.1327,39.07,true)] // HD 218393: before guide/settle, subsequently 40–43 px
    [InlineData(19.1327,21,false)]
    [InlineData(40,42,false)] // large but not a new discrepancy
    // All twelve successful targets with their observed initial coarse and
    // real pre-guide measurements. None is sent back to coarse centring.
    [InlineData(15.5590,30.7546,false)] // HD 224559
    [InlineData(7.5316,20.5353,false)] // HD 698
    [InlineData(19.1848,35.5790,false)] // 10 Cas: still has growth headroom
    [InlineData(16.0433,23.3183,false)] // omi Cas
    [InlineData(9.1361,26.8584,false)] // HD 6226
    [InlineData(13.8785,28.1177,false)] // HD 21641
    [InlineData(13.5495,17.3438,false)] // HD 23552
    [InlineData(16.1726,7.7562,false)] // X Per
    [InlineData(6.3286,19.1246,false)] // HD 24479
    [InlineData(9.4774,27.5227,false)] // HD 29866
    [InlineData(30.1174,32.0973,false)] // AB Aur
    [InlineData(10.4109,25.9399,false)] // 11 Cam
    [InlineData(double.NaN,42,false)]
    public void OnlyMeasuredSignificantGrowthReroutesBeforeTheFirstLargeShift(double coarse,double fresh,bool expected) =>
        Assert.Equal(expected,Phd2HandoffRecoveryPolicy.GrewBeyondOneStage(coarse,fresh,25,6,44));

    [Fact]
    public void Hd25627MarginalRecognitionDoesNotOverrideTheOriginalImageSolveRoute()
    {
        var pair=GateResult.TargetQuality(G3PostWcsShortHandoffPolicy.OutsideCode,"97.65/100.31 px",
            new Dictionary<string,double>{{"shortPairConfirmed",1},{"confirmedPairCoarseSolveEligible",0}});
        var coarse=GateResult.TargetQuality(G3PostWcsMeasuredHandoffPolicy.OutsideRemainingCode,"retained original frame");
        Assert.False(G3PostWcsShortHandoffPolicy.NeedsSameFrameCoarseSolve(pair));
        Assert.True(G3PostWcsShortHandoffPolicy.NeedsSameFrameCoarseSolve(pair,coarse));
        Assert.False(G3PostWcsShortHandoffPolicy.NeedsSameFrameCoarseSolve(pair,GateResult.Unknown("G3_FIELD_MOUNT_BINDING_STALE","blocked")));
        Assert.False(G3PostWcsShortHandoffPolicy.NeedsSameFrameCoarseSolve(GateResult.Unknown("G3_SPARSE_SOURCE_CHANGED","blocked"),coarse));
    }

    [Theory]
    [InlineData("same",true)]
    [InlineData("owner",false)]
    [InlineData("calibration",false)]
    [InlineData("uncalibrated",false)]
    [InlineData("recalibrationAllowed",false)]
    [InlineData("selection",false)]
    [InlineData("unsafe",false)]
    [InlineData("cancel",false)]
    public async Task Mwc480LongProbeRequiresNewReadOnlyApprovalBeforeSameLockSettle(string scenario,bool expected)
    {
        var now=DateTimeOffset.UtcNow;
        var original=new Phd2CalibrationValidation(new(2,"slit"),new(true,79,15,"+",175,30,"+",36),now.AddSeconds(-963),null,null,[],[])
        { ConnectionEpoch=1,CalibrationChangeSequence=6,OwnerManaged=true };
        var fresh=original with { EvaluatedUtc=now };
        fresh=scenario switch
        {
            "owner"=>fresh with { ConnectionEpoch=2 },
            "calibration"=>fresh with { CalibrationChangeSequence=7 },
            "uncalibrated"=>fresh with { Calibration=fresh.Calibration with { Calibrated=false } },
            "recalibrationAllowed"=>fresh with { AllowNativeCalibration=true },
            _=>fresh
        };
        var calls=new List<string>();
        using var cancel=new CancellationTokenSource();
        Task<GateResult> Verify()=>Phd2ScienceContinuationPolicy.RevalidateSameLockAsync(original,
            _=> { calls.Add("safety"); if(scenario=="unsafe" && calls.Count>1) throw new PhysicalActionGateException(GateResult.Unknown("UNSAFE","unsafe")); return Task.CompletedTask; },
            _=> {calls.Add("read-only calibration"); if(scenario=="cancel") cancel.Cancel(); return Task.FromResult(fresh);},
            _=> {calls.Add("live lock");return Task.FromResult(scenario!="selection");},cancel.Token);
        if(scenario=="unsafe") {await Assert.ThrowsAsync<PhysicalActionGateException>(Verify);return;}
        if(scenario=="cancel") {await Assert.ThrowsAnyAsync<OperationCanceledException>(Verify);return;}
        Assert.Equal(expected,(await Verify()).Disposition==GateDisposition.Passed);
        if(expected) Assert.Equal(new[]{"safety","read-only calibration","safety","live lock"},calls);
    }
}
