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
        var result=await f.Workflow.ConvergeAsync(f.Context,request,default);
        Assert.Equal("PHD2_SLIT_PRECISION_WARNING_PROBE_AUTHORIZED",result.Gate.Code);
        Assert.Equal(0,result.Gate.Metrics!["preciseSlitPlacementProven"]);
        Assert.DoesNotContain("SetExactLockPositionAsync",f.Trace.Calls);
        Assert.DoesNotContain("ResettleAtCurrentLockAsync",f.Trace.Calls);
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
