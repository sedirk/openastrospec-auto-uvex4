using System.Security.Cryptography;
using System.Text.Json;
using UvexAdv.Observatory;
using UvexAdv.Phd2;
using UvexAdv.Qhy.Core;
using Xunit;
using Xunit.Abstractions;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class November21LastNightTests(ITestOutputHelper output)
{
    [Fact]
    public void Hd26546LargeStageCanStartGentlyButNeverSpendTheReturnReserve()
    {
        var step = new Phd2GuideStep(15,-2.128,-2.657,33.11,5.37,3.26,1);
        var primary = new Phd2Point(1719.68,984.34);
        var endpoint = new Phd2Point(1721.81,987);
        var limits = new Phd2LockShiftLimits(25,100,8,TimeSpan.FromSeconds(300),TimeSpan.FromSeconds(40),
            TimeSpan.FromSeconds(30),TimeSpan.FromSeconds(10),.5,.5,3,44,10,10,5,5,5,1,100000);
        var budget = new Phd2LockShiftAcquisitionBudget(true,"ready","",39,2,42,2,81,4,165);
        double? Size(Phd2GuideStep s, Phd2LockShiftAcquisitionBudget b) =>
            Phd2SlitConvergenceWorkflow.DistanceGuardApproachPixels(s,primary,endpoint,25,b,limits,1);
        Assert.InRange(Size(step,budget)!.Value,3,3.2);
        Assert.Null(Size(step with { Snr=0, ErrorCode=2 },budget));
        Assert.Null(Size(step with { ErrorCode=8 },budget)); // loss is never motion evidence
        Assert.Null(Size(step,budget with { IsAllowed=false }));
        Assert.Null(Size(step,budget with { TotalAttempts=8 }));
        Assert.Null(Size(step,budget with { TotalElapsedSeconds=290 }));
    }

    [Fact]
    public async Task HistoryPreflightAwaitsEveryPathPreservesRejectionsAndHonorsCancellation()
    {
        var indices = Enumerable.Range(0, 97).ToArray();
        var seen = new int[indices.Length];
        var results = await RealObservationStageRunner.ReadLockManifestsInOrderAsync(indices, async (i, token) =>
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref seen[i]);
            return (i, trusted: i != 73);
        }, default);
        Assert.Equal(indices, results.Select(r => r.i));
        Assert.Equal(73, Assert.Single(results, r => !r.trusted).i);
        Assert.All(seen, n => Assert.Equal(1, n));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RealObservationStageRunner.ReadLockManifestsInOrderAsync(indices, (i, _) => Task.FromResult(i), new(true)));
    }

    [Theory]
    [InlineData(true, 0, "ATR", 6, -10, true)]
    [InlineData(false, 0, "ATR", 6, -10, false)]
    [InlineData(true, 0, "other", 6, -10, false)]
    [InlineData(true, .1, "ATR", 6, -10, false)]
    [InlineData(true, 0, "ATR", 5, -10, false)]
    [InlineData(true, 0, "ATR", 6, 0, false)]
    public void OnlyPreviouslyStableExactOwnerWithSixFrozenZerosGetsEarlierIdleRecovery(
        bool stable, double temperature, string owner, int count, double target, bool expedite)
    {
        var p = new AtrCoolingRecoveryPolicy();
        var t = DateTimeOffset.UnixEpoch;
        var sample = new AtrCoolingTelemetry(true, owner, true, true, temperature, target, 29.5);
        var confirmation = new AtrTemperatureConfirmation(false, 0, null, null,
            "OWNER_TEMPERATURE_READBACK_UNCONFIRMED", Enumerable.Repeat(sample, count).ToArray());
        Assert.Equal(expedite, p.NotePostSaveZero(confirmation, stable, "ATR", target, t));
        var bad = AtrCoolingReadinessPolicy.Evaluate(sample, "ATR", target);
        Assert.Equal(AtrCoolingRecoveryAction.Wait, p.Observe(bad, t));
        Assert.Equal(expedite ? AtrCoolingRecoveryAction.ReapplyTarget : AtrCoolingRecoveryAction.Wait, p.Observe(bad, t.AddSeconds(15)));
        Assert.Equal(expedite ? AtrCoolingRecoveryAction.ReconnectOwner : AtrCoolingRecoveryAction.Wait, p.Observe(bad, t.AddSeconds(30)));
        Assert.Equal(AtrCoolingRecoveryAction.Wait, p.Observe(bad, t.AddSeconds(45)));
        if (expedite)
            Assert.Equal(AtrCoolingReadinessPolicy.SavedFrameInvalidCode,
                AtrCoolingReadinessPolicy.EvaluateSavedFrame(new Dictionary<string,string> { ["CCD-TEMP"]="0", ["SET-TEMP"]="-10" }, target, bad).Code);
    }

    private sealed class RecordedAttribute : TheoryAttribute
    {
        public RecordedAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS")))
                Skip = "Private historical originals require UVEX_REPLAY_OBSERVATIONS.";
        }
    }

    [Recorded]
    [InlineData("UVEX-20261120T171605Z-80dd3a0007a04e8", ObservationStage.PlaceTargetOnSlit, "G3_POST_WCS_SHORT_UNCONFIRMED")]
    [InlineData("UVEX-20261120T175505Z-970ea5f556594fa", ObservationStage.SelectAtrExposure, "G3_WCS_CENTERING_RESPONSE_NOT_CONVERGED_RETURNED")]
    [InlineData("UVEX-20261120T180429Z-f51eb843a3124a9", ObservationStage.PlaceTargetOnSlit, "PHD2_PLACEMENT_GUIDE_STAR_LOST")]
    [InlineData("UVEX-20261120T193556Z-7e68c99d9a6f442", ObservationStage.SelectAtrExposure, "PHD2_SCIENCE_RECOVERY_BUDGET_UNAVAILABLE")]
    public async Task RecordedGateExecutesNextNativeTargetOnlyWithCheckedStop(string run, ObservationStage stage, string code)
    {
        using var m=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS")!,run,"manifest.json")));
        var options=new JsonSerializerOptions{PropertyNameCaseInsensitive=true};
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var gate=m.RootElement.GetProperty("gates").GetProperty(stage.ToString()).Deserialize<GateResult>(options)!;
        Assert.Equal(code,gate.Code);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("skip",code,gate,stage);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("cleanup-failure",code,gate,stage);
    }

    [Recorded]
    [InlineData("phd2-lock-shift-pending.json")]
    public async Task HistoricalLedgerInventoryChecksIdenticalFilesAndVerdictsInBothOrders(string ledgerName)
    {
        var root=Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS")!;
        var files=await Phd2LockShiftPendingStore.DiscoverAsync(root, default);
        Assert.NotEmpty(files);
        Assert.All(files,f=>{ Assert.Null(f.Error); Assert.Equal(ledgerName,Path.GetFileName(f.Path)); });
        // Warm only the projected decode cache. The full bytes are rehashed in both measurements.
        await RealObservationStageRunner.ReadLockManifestsInOrderAsync(files,RealObservationStageRunner.ValidatePhd2LockManifestAsync,default);
        var timer=System.Diagnostics.Stopwatch.StartNew();
        var sequential=new List<(bool RunIsTerminal,GateResult? Error)>();
        foreach(var f in files) sequential.Add(await RealObservationStageRunner.ValidatePhd2LockManifestAsync(f,default));
        var serialMs=timer.ElapsedMilliseconds;
        timer.Restart();
        var bounded=await RealObservationStageRunner.ReadLockManifestsInOrderAsync(files,RealObservationStageRunner.ValidatePhd2LockManifestAsync,default);
        output.WriteLine($"{files.Count} manifests; serial {serialMs} ms, bounded {timer.ElapsedMilliseconds} ms (warm local files).");
        Assert.Equal(sequential.Select(v=>(v.RunIsTerminal,v.Error?.Code)), bounded.Select(v=>(v.RunIsTerminal,v.Error?.Code)));
    }

    [Recorded]
    [InlineData("UVEX-20261120T171605Z-80dd3a0007a04e8", "00300")]
    public async Task Hd25627DoesNotLetASeparatedBrightCoreMoveItsOwnWindow(string run, string prefix)
    {
        var dir=Path.Combine(Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS")!,run,"evidence");
        var json=new JsonSerializerOptions{PropertyNameCaseInsensitive=true};
        using var receipt=JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(dir,prefix+"*.json"))));
        using var geometry=JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(dir,"*slit-wheel-optical-identity.json").Take(1))));
        var p=receipt.RootElement.GetProperty("payload");
        var slit=geometry.RootElement.GetProperty("payload").GetProperty("measurement").GetProperty("physicalApertureGeometry").Deserialize<SlitGeometry>(json)!;
        var anchor=p.GetProperty("longTargetAnchor").Deserialize<PixelPoint>(json)!;
        var longPath=Path.Combine(dir,Path.GetFileName(p.GetProperty("longFramePath").GetString()!));
        var shortPath=Path.Combine(dir,Path.GetFileName(p.GetProperty("shortFramePath").GetString()!));
        Assert.Equal("EC4481A890F7DC5CD996DDCAF2E7D2B04079A457930D9A377FE74569112DCB47",Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(longPath))));
        Assert.Equal(p.GetProperty("shortFrameSha256").GetString()!.ToUpperInvariant(),Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(shortPath))));
        var raw=QhyFitsCodec.Read(longPath);
        var runtime=SepStarDetectionClient.ReadRuntime(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"UVEX-ADV","star-detection","runtime.json"));
        var l=CatalogFieldRegistration.ReferenceStars(await new SepStarDetectionClient().DetectAsync(runtime,raw.Width,raw.Height,raw.Pixels,G3FrameInputPolicy.ResolveEffectiveSaturationAdu(raw.Pixels,4095),default));
        var s=CatalogFieldRegistration.ReferenceStars(p.GetProperty("sep").Deserialize<SepImageMeasurements>(json)!);
        var fit=G3TargetTrackPolicy.Register(l,s,anchor,anchor,slit,raw.Width,raw.Height,100,default);
        output.WriteLine($"Original long/short stars {l.Count}/{s.Count}, fit {fit.Code}, holdouts {fit.HoldoutPairs.Count}.");
        Assert.False(fit.Accepted); // no position is inferred from the bright candidate alone
    }

    [Recorded]
    [InlineData("UVEX-20261120T175505Z-970ea5f556594fa", "00245", "00263", "00249",
        "B3AA09911329B0BE274A698E7B5F174D7AC1495AE3C91CACBBDF16A7935F64D5", "20F1623DE627E12F5F9265AD70DEC759D03588ACAEFDE2A2CFCEAC8CAC7CF281")]
    [InlineData("UVEX-20261120T193556Z-7e68c99d9a6f442", "00314", "00328", "00318",
        "3DC17AAF565D9BB4414117C564D9BABBD67E904766BC6B91076C7ADDA8592FEF", "88BEC41F5D561165CF1A44A3E08F04705E3D026A4B91D2AEE024C90EF3A37651")]
    public async Task StoppedFieldOriginalsDistinguishSearchPriorsFromPhysicalSlit(
        string run, string before, string after, string prior, string oldHash, string newHash)
    {
        var e = Path.Combine(Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS")!, run, "evidence");
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        JsonDocument Read(string n) => JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(e, n+"*.json"))));
        using var m = Read(prior);
        using var slitRecord = Read("00033");
        var point = m.RootElement.GetProperty("payload").GetProperty("measurement").GetProperty("targetCentroid").Deserialize<PixelPoint>(json)!;
        var slit = slitRecord.RootElement.GetProperty("payload").GetProperty("measurement").GetProperty("physicalApertureGeometry").Deserialize<SlitGeometry>(json)!;
        var runtime = SepStarDetectionClient.ReadRuntime(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UVEX-ADV", "star-detection", "runtime.json"));
        async Task<(MonochromeFrame frame, IReadOnlyList<StarCandidate> stars)> Extract(string n, string sha)
        {
            var path = Assert.Single(Directory.GetFiles(e, n+"*.fit"));
            Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            var raw = QhyFitsCodec.Read(path);
            var saturation = G3FrameInputPolicy.ResolveEffectiveSaturationAdu(raw.Pixels, 4095);
            var sep = await new SepStarDetectionClient().DetectAsync(runtime, raw.Width, raw.Height, raw.Pixels, saturation, default);
            return (new(raw.Width, raw.Height, raw.Pixels, saturation), CatalogFieldRegistration.ReferenceStars(sep));
        }
        var a = await Extract(before, oldHash); var b = await Extract(after, newHash);
        var fit = G3TargetTrackPolicy.Register(a.stars, b.stars, point, point, slit, a.frame.Width, a.frame.Height, 100, default);
        output.WriteLine($"{run}: stars {a.stars.Count}/{b.stars.Count}; {fit.Code}; matches {fit.MatchedStars}, holdouts {fit.HoldoutPairs.Count}; predicted {fit.Transform?.Apply(point)}, original {point}");
        var seed = G3FieldState.Failed(GateResult.Pass("field","recorded"),before) with {
            Frame=a.frame, Candidates=a.stars, SlitDetection=new(GateResult.Pass("slit","recorded"),slit,20,0,0),
            TargetIdentification=new(GateResult.Pass("target","recorded"),new(point,8000,100000,50,5,.1,0,20),point,0,2) };
        var recovered = RealObservationStageRunner.TryRegisterSameFieldRecoverySeed(seed,b.stars,b.frame.Width,b.frame.Height,100,default);
        output.WriteLine(recovered is null ? "No search prior authorized." : "Search prior only; no slit or shutter authority.");
        Assert.True(fit.Accepted);
        Assert.Equal(before == "00245" ? 5 : 9, fit.MatchedStars);
        Assert.Equal(before == "00245" ? 2 : 6, fit.HoldoutPairs.Count);
        var transported = fit.Transform!.Apply(point);
        var shift = double.Hypot(transported.X - point.X, transported.Y - point.Y);
        if (before == "00245") Assert.InRange(shift, 103, 105);
        else Assert.InRange(shift, 150, 153);
        Assert.Null(recovered); // a real transform is not permission for an out-of-range takeover
    }
}
