using System.Security.Cryptography;
using System.Text.Json;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;
using UvexAdv.Phd2;
using Xunit;
using Xunit.Abstractions;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class RecordedPlacementContinuityTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("skip")]
    [InlineData("cleanup-failure")]
    public async Task Hd220526MeasuredCandidateCannotBeReplacedByAStaleFineHandoff(string boundary)
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var dir = Path.Combine(root,"UVEX-20261021T104802Z-7f19f79b05d64fd","evidence");
        var path = Assert.Single(Directory.GetFiles(dir,"00033-*.fit"));
        const string sha = "0A419FA85D7FDB946F19CD1652E498DAFCF3DE2F5BC71412A5D68FD4F6072B1E";
        Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        var raw = QhyFitsCodec.Read(path);
        var frame = new MonochromeFrame(raw.Width,raw.Height,raw.Pixels,
            G3FrameInputPolicy.ResolveEffectiveSaturationAdu(raw.Pixels,4095));
        var predicted = new PixelPoint(816.3728,415.6175);
        var slit = new PixelPoint(817.640,431.660);
        var measured = SlitTargetIdentifier.Identify(frame,StarFieldDetector.Detect(frame),predicted,100,8,1.5);
        Assert.Equal("TARGET_IDENTIFIED_SATURATED_TOPOLOGY", measured.Gate.Code);
        Assert.InRange(measured.Target!.Centroid.X,865,867);
        Assert.InRange(measured.Target.Centroid.Y,434,436);
        Assert.True(G3PostWcsMeasuredHandoffPolicy.RequiresCoarseSolve(measured,predicted,5.2331,slit,1920,1080,20,100,44));
        Assert.False(G3PostWcsMeasuredHandoffPolicy.CanProposeBoundedShortConfirmation(measured,predicted,5.2331,slit,1920,1080,20,100,44));
        Assert.False(G3PostWcsMeasuredHandoffPolicy.CanHandOff(measured,predicted,5.2331,slit,1920,1080,20));
        var gate = G3PostWcsMeasuredHandoffPolicy.OutsideRemainingGate(measured,slit,44);
        Assert.InRange(gate.Metrics!["measuredTargetResidualPixels"],48,49);
        // The existing-frame formal solve may still recover coarse placement.
        // If it fails, this production gate must run the NEXT native target only
        // after checked stop; it must not become an all-night quality blocker.
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(boundary,gate.Code,gate,ObservationStage.AcquireG3SlitField);
        Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        output.WriteLine($"HD 220526 {measured.Target.Centroid}: coarse existing-frame solve retained, no stale prediction/PHD2 promotion, boundary={boundary}.");
    }

    [Fact]
    public void Hd219937ShortToGuideHandoffKeepsTheFilledCoreAcrossFreshPrimaryMeasurements()
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var dir = Path.Combine(root, "UVEX-20261021T103936Z-4b73b3e23af4412", "evidence");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var hashes = new Dictionary<string, string>();
        using var shortDoc = ReadRecord("00067-");
        var shortTarget = shortDoc.RootElement.GetProperty("payload").GetProperty("measurement")
            .GetProperty("identification").GetProperty("target").GetProperty("centroid")
            .Deserialize<PixelPoint>(options)!;
        var selection = ReadFrame("00070-");
        var selected = SlitTargetIdentifier.Identify(selection, G3StellarFocusAnalyzer.Analyze(selection).Stars,
            shortTarget, 100, 8, 1.5);
        Assert.Equal("TARGET_IDENTIFIED_SATURATED_TOPOLOGY", selected.Gate.Code);
        Assert.InRange(selected.Target!.Centroid.X, 831, 834);
        Assert.InRange(selected.Target.Centroid.Y, 457, 460);
        using var handoffDoc = ReadRecord("00073-");
        var guide = handoffDoc.RootElement.GetProperty("payload").GetProperty("selectedGuidePosition")
            .Deserialize<Phd2Point>(options)!;
        var target = new Phd2Point(selected.Target.Centroid.X, selected.Target.Centroid.Y);
        // These five immutable frames have recorded pixel-matched native primary
        // measurements. Replay the actual shared identifier and continuity policy,
        // not commanded lock deltas or multi-star dx/dy. No new motion is simulated.
        foreach (var prefix in new[] { "00076-", "00080-", "00084-", "00086-", "00088-" })
        {
            using var doc = ReadRecord(prefix);
            var measurement = doc.RootElement.GetProperty("payload").GetProperty("measurement")
                .Deserialize<Phd2SlitFieldMeasurement>(options)!;
            Assert.True(measurement.GuidePositionMeasuredInFrame);
            var predicted = Phd2TargetContinuityPolicy.Predict(target, guide, measurement.GuideStar);
            var point = new PixelPoint(predicted.X, predicted.Y);
            var raw = doc.RootElement.GetProperty("source").GetProperty("absolutePath").GetString()!;
            var frame = ReadPath(raw);
            var id = SlitTargetIdentifier.Identify(frame, StarFieldDetector.Detect(frame), point, 100, 8, 1.5);
            var topology = SaturatedTargetGhostTopologyAnalyzer.Analyze(frame, point, 100);
            Assert.Equal("TARGET_IDENTIFIED_SATURATED_TOPOLOGY", id.Gate.Code);
            Assert.Equal(topology.Target!.Centroid, id.Target!.Centroid);
            Assert.True(Phd2TargetContinuityPolicy.IsContinuous(id.Target.Centroid, point, 6));
            Assert.False(Phd2TargetContinuityPolicy.IsContinuous(
                new(measurement.TargetCentroid.X, measurement.TargetCentroid.Y), point, 6));
            output.WriteLine($"{prefix}: measured core {id.Target.Centroid}, primary-shift prediction {point}; original 6px continuity bound retained.");
            target = new(id.Target.Centroid.X, id.Target.Centroid.Y);
            guide = measurement.GuideStar;
        }
        foreach (var (path, sha) in hashes)
            Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

        JsonDocument ReadRecord(string prefix) => JsonDocument.Parse(File.ReadAllText(
            Assert.Single(Directory.GetFiles(dir, prefix + "*.json"))));
        MonochromeFrame ReadFrame(string prefix) => ReadPath(Assert.Single(Directory.GetFiles(dir, prefix + "*.fit")));
        MonochromeFrame ReadPath(string path)
        {
            hashes[path] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            var fits = QhyFitsCodec.Read(path);
            return new(fits.Width, fits.Height, fits.Pixels,
                G3FrameInputPolicy.ResolveEffectiveSaturationAdu(fits.Pixels, 4095));
        }
    }

    [Fact]
    public async Task Hd21641ArchivedLedgerRetainsItsActualBudgetAcrossMeasuredHandoff()
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var run = Path.Combine(root, "UVEX-20261014T185823Z-4f3592c3a03d4af");
        var path = Assert.Single(Directory.GetFiles(Path.Combine(run, "control"), "g3-acquisition-motion.json.terminal-*.archive"));
        var before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var loaded = await G3AcquisitionMotionStore.LoadAsync(path, default);
        Assert.Null(loaded.Error);
        var state = loaded.State!;
        using var doc = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(run, "evidence"), "00112-*g3-wcs-centering-declared.json"))));
        var current = doc.RootElement.GetProperty("payload").GetProperty("origin");
        var ra = current.GetProperty("raDegrees").GetDouble();
        var dec = current.GetProperty("decDegrees").GetDouble();
        var offset = G3AcquisitionMotionPlanner.SignedTangentOffsetArcseconds(state.OriginRaDegrees, state.OriginDeclinationDegrees, ra, dec);
        var spherical = G3AcquisitionMotionPlanner.AngularSeparationArcseconds(state.PriorReportedRaDegrees, state.PriorReportedDeclinationDegrees, ra, dec);
        var tangent = Math.Sqrt(Math.Pow(offset.RaArcseconds - state.CurrentRaTangentOffsetArcseconds, 2) +
            Math.Pow(offset.DecArcseconds - state.CurrentDeclinationOffsetArcseconds, 2));
        var delta = Math.Max(spherical, tangent);
        Assert.InRange(delta, 9, 9.1);
        var now = DateTimeOffset.Parse("2026-10-14T19:06:24.450Z");
        var continued = G3AcquisitionMotionPlanner.ContinueSettledLedger(state, state.ObservationRunId,
            G3AcquisitionMotionKind.WcsCentering, "recorded-fresh-handoff.json", now) with
        {
            PriorReportedRaDegrees = ra, PriorReportedDeclinationDegrees = dec,
            CommandedRaDegrees = ra, CommandedDeclinationDegrees = dec,
            CurrentRaTangentOffsetArcseconds = offset.RaArcseconds,
            CurrentDeclinationOffsetArcseconds = offset.DecArcseconds,
        };
        var result = G3MeasuredHandoffPolicy.Charge(continued, delta, continued.CurrentRadiusArcseconds, now);
        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal(state.StartedUtc, result.Charged!.StartedUtc);
        Assert.Equal(state.MaximumCorrectionAttempts, result.Charged.MaximumCorrectionAttempts);
        Assert.Equal(state.MaximumCumulativeMotionArcseconds, result.Charged.MaximumCumulativeMotionArcseconds);
        Assert.Equal(state.MaximumElapsedSeconds, result.Charged.MaximumElapsedSeconds);
        Assert.Equal(state.CorrectionAttempts + 1, result.Charged.CorrectionAttempts);
        Assert.Equal(state.CumulativeMotionArcseconds + delta, result.Charged.CumulativeMotionArcseconds);
        Assert.Equal(before, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        output.WriteLine($"HD 21641 archived ledger: {delta:F6} arcsec charged; full return retained; no hardware/ownership attestation is synthesized by this accounting replay.");
    }

    [Fact]
    public async Task Hd21455FailedWindowReachesActualSharedProbeRouteFromMeasuredCoreOverlap()
    {
        var root=Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if(string.IsNullOrWhiteSpace(root)) return;
        var dir=Path.Combine(root,"UVEX-20261014T184845Z-be4bfb9f20ef45f","evidence");
        var options=new JsonSerializerOptions {PropertyNameCaseInsensitive=true};
        using var geometryDoc=JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(dir,"*slit-wheel-optical-identity.json"))));
        var slit=geometryDoc.RootElement.GetProperty("payload").GetProperty("measurement")
            .GetProperty("physicalApertureGeometry").Deserialize<SlitGeometry>(options)!;
        var f=new Phd2ConvergenceFixture(warning:true);
        var request=f.Request([12,12,12]);
        var samples=new List<Phd2GuidingResidualState>();
        var rawHashes=new List<(string Path,string Sha)>();
        Phd2Point? actualLock=null;
        foreach(var file in Directory.GetFiles(dir,"*fresh-residual.json").Order().TakeLast(3))
        {
            using var doc=JsonDocument.Parse(File.ReadAllText(file));
            var p=doc.RootElement.GetProperty("payload");
            var source=doc.RootElement.GetProperty("source");
            var raw=source.GetProperty("absolutePath").GetString()!;
            var sha=source.GetProperty("sha256").GetString()!.ToUpperInvariant();
            Assert.Equal(sha,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(raw))));
            rawHashes.Add((raw,sha));
            var fits=QhyFitsCodec.Read(raw);
            var m=p.GetProperty("measurement").Deserialize<Phd2SlitFieldMeasurement>(options)!;
            actualLock=p.GetProperty("nativeLockPosition").Deserialize<Phd2Point>(options)!;
            var frame=new MonochromeFrame(fits.Width,fits.Height,fits.Pixels,
                G3FrameInputPolicy.ResolveEffectiveSaturationAdu(fits.Pixels,4095));
            var sample=request.InitialWindow[samples.Count] with { MonochromeFrame=frame,RuntimeSlitLocal=slit,
                Measurement=m with { CapturedUtc=f.Now.AddSeconds(samples.Count-2),
                    TopologyFingerprintSha256=request.Session.Topology.ComputeFingerprintSha256() },
                Frame=request.InitialWindow[samples.Count].Frame with {Path=raw,Sha256=sha} };
            var aperture=Phd2PlacementGuideWindowPolicy.ProjectMeasuredTarget(sample,new(m.TargetCentroid.X,m.TargetCentroid.Y))!;
            Assert.True(aperture.TargetCoreRadiusPixels>0);
            output.WriteLine($"HD 21455 recorded residual {aperture.MidpointPixels:F3}px, cross={aperture.CrossSlitPixels:F3}px, measured core radius={aperture.TargetCoreRadiusPixels:F2}px; SHA={sha}");
            samples.Add(sample);
        }
        var window=samples.ToArray();
        request=request with { InitialWindow=window,Session=request.Session with {LastMeasurement=window[^1]},
            Ledger=request.Ledger with {OriginLockPosition=actualLock!,CurrentLockPosition=actualLock!},
            Preset=request.Preset with {TargetOnSlitTolerancePixels=2,MaximumResidualGrowthPixels=2,
                MaximumAcquisitionResidualPixels=100,MaximumStagePixels=25,MaximumCumulativePixels=100,MaximumAttempts=8} };
        f.Trace.Handlers["get_GuideSnapshot"] = _ => Phd2StateSnapshot.Disconnected with {
            IsConnected=true, AppState=Phd2AppState.Guiding, ConnectionEpoch=1, GuideEpoch=1, LockPosition=actualLock };
        // .321 may make one optional read-only check before a biased core-overlap
        // probe. Controlled unchanged response: do not fabricate new real FITS or
        // pretend the archived guide caught up. It must still reach the probe.
        f.Trace.Handlers["CapturePhd2PlacementGuideWindowAsync"] = _ => {
            f.ClockNow=f.Now.AddSeconds(3);
            return Task.FromResult<IReadOnlyList<Phd2GuidingResidualState>>(window.Select((s,i) => s with {
                Measurement=s.Measurement with {CapturedUtc=f.Now.AddSeconds(i+1),FrameSha256=new((char)('D'+i),64)},
                Frame=s.Frame with {Sha256=new((char)('D'+i),64),CompletedUtc=f.Now.AddSeconds(i+1),GuideStepUtc=f.Now.AddSeconds(i+1)}
            }).ToArray());
        };
        var result=await f.Workflow.ConvergeAsync(f.Context,request,default);
        Assert.Equal("PHD2_SLIT_PRECISION_WARNING_PROBE_AUTHORIZED",result.Gate.Code);
        Assert.Equal(0,result.Gate.Metrics!["preciseSlitPlacementProven"]);
        Assert.DoesNotContain("SetExactLockPositionAsync",f.Trace.Calls);
        Assert.DoesNotContain("ResettleAtCurrentLockAsync",f.Trace.Calls);
        Assert.InRange(f.Trace.Calls.Count(c => c == "CapturePhd2PlacementGuideWindowAsync"),0,1);
        Assert.Single(f.Persisted);
        Assert.Equal(request.Ledger.StartedUtc,f.Persisted[0].StartedUtc);
        Assert.Equal(request.Ledger.CumulativeCommandedPixels,f.Persisted[0].CumulativeCommandedPixels);
        // Execute the real ATR workflow using a non-device port. This is NOT a
        // counterfactual spectrum: the failed on-sky run had no ATR exposure.
        var atr=new AtrWorkflowFixture(1);
        Assert.True((await atr.Science.SelectExposureAsync(atr.Context,default)).CanAdvance);
        Assert.True((await atr.Science.RunAsync(atr.Context,default)).CanAdvance);
        Assert.Equal(1,atr.Science.AcceptedFrames);
        foreach(var raw in rawHashes) Assert.Equal(raw.Sha,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(raw.Path))));
    }

    [Theory]
    [InlineData("00088-")]
    [InlineData("00090-")]
    [InlineData("00092-")]
    public void Hd21641FreshPositionRetainsImmutablePixels(string prefix)
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var dir = Path.Combine(root, "UVEX-20261014T185823Z-4f3592c3a03d4af", "evidence");
        using var doc = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(dir, prefix + "*fresh-residual.json"))));
        var source = doc.RootElement.GetProperty("source");
        var path = source.GetProperty("absolutePath").GetString()!;
        var before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Equal(source.GetProperty("sha256").GetString()!.ToUpperInvariant(), before);
        var p = doc.RootElement.GetProperty("payload");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var measurement = p.GetProperty("measurement").Deserialize<Phd2SlitFieldMeasurement>(options)!;
        // Actual last accepted pre-guide target/primary pair. The subsequent
        // native guide operation moved the stars without an exact-lock command.
        var predicted = Phd2TargetContinuityPolicy.Predict(new(790.4858757062146, 411.40112994350284),
            new(1698.97, 798.43), measurement.GuideStar);
        var point = new PixelPoint(predicted.X, predicted.Y);
        var fits = QhyFitsCodec.Read(path);
        var frame = new MonochromeFrame(fits.Width, fits.Height, fits.Pixels,
            G3FrameInputPolicy.ResolveEffectiveSaturationAdu(fits.Pixels, 4095));
        var stars = StarFieldDetector.Detect(frame);
        var id = SlitTargetIdentifier.Identify(frame, stars, point, 100, 8, 1.5);
        var topology = SaturatedTargetGhostTopologyAnalyzer.Analyze(frame, point, 100);
        Assert.Equal("TARGET_IDENTIFIED_SATURATED_TOPOLOGY", id.Gate.Code);
        Assert.Equal(topology.Target!.Centroid, id.Target!.Centroid);
        Assert.True(Phd2TargetContinuityPolicy.IsContinuous(id.Target.Centroid, point, 6));
        if (prefix == "00088-") Assert.False(Phd2TargetContinuityPolicy.IsContinuous(
            new(measurement.TargetCentroid.X, measurement.TargetCentroid.Y), point, 6));
        output.WriteLine($"{prefix}: core={id.Target.Centroid}; shifted search={point}; original pixels unchanged {before}");
        Assert.Equal(before, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }
}
