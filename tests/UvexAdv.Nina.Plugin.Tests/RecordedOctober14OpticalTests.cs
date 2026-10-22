using System.Text.Json;
using System.Security.Cryptography;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;
using UvexAdv.Phd2;
using Xunit;
using Xunit.Abstractions;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class RecordedOctober14OpticalTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("00075-", false)]
    [InlineData("00077-", true)]
    [InlineData("00079-", true)]
    public void Hd21455NeighbourHandoffKeepsCoreFluxAuthorityInsteadOfTheRecordedHaloPeak(string prefix, bool saturated)
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var dir = Path.Combine(root, "UVEX-20261014T173028Z-8bf9bed7c77d404", "evidence");
        var path = Assert.Single(Directory.GetFiles(dir, prefix + "*fresh-residual.json"));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var source = doc.RootElement.GetProperty("source");
        var raw = source.GetProperty("absolutePath").GetString()!;
        var before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(raw)));
        Assert.Equal(source.GetProperty("sha256").GetString()!.ToUpperInvariant(), before);
        var p = doc.RootElement.GetProperty("payload");
        var expected = p.GetProperty("expectedTarget");
        var point = new PixelPoint(expected.GetProperty("x").GetDouble(), expected.GetProperty("y").GetDouble());
        var fits = QhyFitsCodec.Read(raw);
        var frame = new MonochromeFrame(fits.Width, fits.Height, fits.Pixels,
            G3FrameInputPolicy.ResolveEffectiveSaturationAdu(fits.Pixels, 4095));
        var id = SlitTargetIdentifier.Identify(frame, StarFieldDetector.Detect(frame), point, 100, 8, 1.5);
        output.WriteLine($"{prefix}: {id.Gate.Code}, {id.Authority}; flux={id.Target?.FluxAdu}; raw={before}");
        Assert.Equal("TARGET_IDENTIFIED_SATURATED_TOPOLOGY", id.Gate.Code);
        Assert.True(Phd2FreshTargetFluxPolicy.UsesSaturatedTopologyFluxNotApplicable(id, true));
        var m = p.GetProperty("measurement").Deserialize<Phd2SlitFieldMeasurement>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        if (saturated) Assert.Equal(m.FluxMetric, id.Target!.FluxAdu);
        else
        {
            // The old first-frame assertion preserved a noise maximum at
            // (831.83,450.96), not an independent source. Its raw local core
            // median is 9664 ADU, below its surrounding halo (Q75=14936 ADU).
            // The same immutable image contains this filled stellar core.
            Assert.InRange(id.Target!.Centroid.X, 849.5, 849.8);
            Assert.InRange(id.Target.Centroid.Y, 466.1, 466.5);
            Assert.False(Phd2TargetContinuityPolicy.IsContinuous(id.Target.Centroid,
                new(m.TargetCentroid.X, m.TargetCentroid.Y), 6));
        }
        Assert.True(m.GuidePositionMeasuredInFrame && m.TargetIdentityConfirmed);
        var f = new Phd2ConvergenceFixture();
        var request = f.Request([1, 1, 1]);
        using var preflight = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(dir, "00081-*.json"))));
        var actual = preflight.RootElement.GetProperty("payload");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var limits = actual.GetProperty("fineMotionLimits").Deserialize<Phd2LockShiftLimits>(options)!;
        var ledger = actual.GetProperty("ledger").Deserialize<Phd2LockShiftLedger>(options)!;
        Assert.True(m.FluxMetric > limits.MaximumFluxMetric);
        Assert.True(Phd2SlitLockShiftPlanner.IsMeasuredTargetFluxWarning(m, limits));
        m = m with { TargetCentroid = new(id.Target!.Centroid.X, id.Target.Centroid.Y), FluxMetric = 0,
            FluxEvidenceLabel = "SATURATED_TARGET_TOPOLOGY_FLUX_NOT_APPLICABLE",
            TargetPositionAuthority = Phd2TargetPositionAuthority.CatalogWcsIdentityWithSaturatedTopologyCentroid };
        // Exercise the planner with the core remeasured above. Original files
        // and the prior bad measurement remain unchanged. The
        // original >50px displacement may still need bounded coarse recovery;
        // fixing the flux interpretation is not arrival or extra movement credit.
        var now = m.CapturedUtc.AddSeconds(1);
        var budget = Phd2SlitLockShiftPlanner.EvaluateAcquisitionBudget(request.Session.Qualification with {
                MaximumLockShiftScale = actual.GetProperty("maximumLockShiftScale").GetDouble() },
            request.Session.GuideMode, m with { TopologyFingerprintSha256 = request.Session.Topology.ComputeFingerprintSha256() },
            ledger with { StartedUtc = m.CapturedUtc },
            new Phd2LockShiftSafetySnapshot(true, 60, 55, 40, request.Session.Topology.PierSide, now),
            request.Session.Topology, limits, now);
        Assert.True(budget.IsAllowed || budget.Code is "SLIT_LOCK_ACQUISITION_CUMULATIVE_RESERVE" or
            "SLIT_LOCK_ACQUISITION_ATTEMPT_RESERVE" or "SLIT_LOCK_ACQUISITION_TIME_RESERVE", budget.Code);
        Assert.Equal(100, limits.MaximumCumulativePixels);
        output.WriteLine($"Corrected real preflight: {budget.Code}; total reserved {budget.TotalCumulativePixels:F3}px");
        Assert.Equal(before, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(raw))));
    }

    [Theory]
    [InlineData("UVEX-20261014T164404Z-df4d95d35392407", "00075-", 7.73, 7.75, 37.6, 37.8)]
    [InlineData("UVEX-20261014T163412Z-e712171d99c6467", "00324-", 13.6, 13.7, 18.8, 19.0)]
    public void Later0RealGuideResidualWarnsButDoesNotProveSlitArrival(
        string run, string prefix, double guideMin, double guideMax, double targetMin, double targetMax)
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var path = Assert.Single(Directory.GetFiles(Path.Combine(root, run, "evidence"), prefix + "*fresh-residual.json"));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var source = doc.RootElement.GetProperty("source");
        var raw = source.GetProperty("absolutePath").GetString()!;
        var before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(raw)));
        Assert.Equal(source.GetProperty("sha256").GetString()!.ToUpperInvariant(), before);
        var p = doc.RootElement.GetProperty("payload");
        var measurement = p.GetProperty("measurement").Deserialize<Phd2SlitFieldMeasurement>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var guideResidual = p.GetProperty("guideLockResidualPixels").GetDouble();
        var targetResidual = p.GetProperty("targetSlitResidualPixels").GetDouble();
        Assert.InRange(guideResidual, guideMin, guideMax);
        Assert.InRange(targetResidual, targetMin, targetMax);
        Assert.True(measurement.TargetIdentityConfirmed && measurement.GuidePositionMeasuredInFrame);
        var f = new Phd2ConvergenceFixture();
        var request = f.Request([1, 1, 1]);
        // Rebind only the offline fixture topology/time; all recorded coordinates,
        // residuals, measured authority and raw file hash remain unchanged.
        var sample = request.InitialWindow[^1] with { Measurement = measurement with {
            TopologyFingerprintSha256 = request.Session.Topology.ComputeFingerprintSha256() },
            Frame = request.InitialWindow[^1].Frame with { Path = raw, Sha256 = before,
                GuideStepUtc = measurement.CapturedUtc } };
        var evidence = Phd2PlacementGuideWindowPolicy.CalibrationResidualEvidence(
            sample, guideResidual, request.Session.Topology, measurement.CapturedUtc.AddSeconds(1));
        Assert.True(evidence.IsAdvisoryMeasuredGuideLockResidual);
        Assert.Equal(3, evidence.MaximumResidualPixels);
        Assert.Equal(guideResidual, evidence.ResidualPixels);
        Assert.True(Phd2PlacementGuideWindowPolicy.HasPrecisionWarning([guideResidual]));
        Assert.False(Phd2PlacementGuideWindowPolicy.AllWithinTolerance([targetResidual], 2));
        Assert.Equal(before, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(raw))));
        output.WriteLine($"{run}: guide warning {guideResidual:F4}px, target still {targetResidual:F4}px; raw unchanged {before}");
    }

    [Fact]
    public void Hd223554RecoveryUsesTheCoreForBoundedShortConfirmationNotTheRecordedHaloPeak()
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var dir = Path.Combine(root, "UVEX-20261014T151836Z-e773b21b4e2348b", "evidence");
        var path = Assert.Single(Directory.GetFiles(dir, "00188-*.fit"));
        var before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var fits = QhyFitsCodec.Read(path);
        var frame = new MonochromeFrame(fits.Width, fits.Height, fits.Pixels,
            G3FrameInputPolicy.ResolveEffectiveSaturationAdu(fits.Pixels, 4095));
        var prediction = new PixelPoint(816.445, 422.2);
        var slit = new PixelPoint(817.567, 429.571);
        var content = G3SolveProbeContentAnalyzer.Analyze(frame);
        var id = SlitTargetIdentifier.Identify(frame, content.StellarMeasurement.Stars, prediction, 100, 8, 1.5);
        Assert.Equal("TARGET_IDENTIFIED_SATURATED_TOPOLOGY", id.Gate.Code);
        // The old (812.55,420.25) noise aperture lies below its surrounding
        // halo, not on a resolved companion. Do not fossilize that wrong match.
        Assert.InRange(id.Target!.Centroid.X, 797.8, 798.1);
        Assert.InRange(id.Target.Centroid.Y, 404.1, 404.5);
        Assert.False(Phd2TargetContinuityPolicy.IsContinuous(id.Target.Centroid, new(812.55, 420.25), 6));
        var retained = Phd2FreshTargetFluxPolicy.PreserveMeasuredHandoff(
            TargetIdentification.FromCatalogWcs(prediction, fits.Width, fits.Height, "prediction"), id, false);
        Assert.True(Phd2FreshTargetFluxPolicy.HasMeasuredHandoffPosition(retained));
        Assert.True(G3PostWcsMeasuredHandoffPolicy.CanProposeBoundedShortConfirmation(
            id, prediction, 5.219, slit, fits.Width, fits.Height, 20, 100, 44));
        Assert.True(G3PostWcsMeasuredHandoffPolicy.NeedsShortExposureConfirmation(id));
        Assert.False(G3PostWcsMeasuredHandoffPolicy.CanHandOff(id, prediction, 5.219, slit, fits.Width, fits.Height, 44));
        Assert.False(G3PostWcsMeasuredHandoffPolicy.FitsRemainingHandoff(retained, slit, 2));
        Assert.True(G3PostWcsMeasuredHandoffPolicy.FitsRemainingHandoff(retained, slit, 44));
        Assert.Equal(before, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        output.WriteLine($"HD 223554 actual centroid {id.Target.Centroid}; raw SHA-256 unchanged: {before}");
    }

    [Fact]
    public void Front1DirectFallbackDoesNotTurnSplitPeaksOrAnOldShortPositionIntoAGuideStar()
    {
        var root=Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if(string.IsNullOrWhiteSpace(root)) return;
        var dir=Path.Combine(root,"UVEX-20261014T121309Z-ed973acef1d6462","evidence");
        foreach(var prefix in new[]{"00052-","00054-","00066-"})
        {
            var path=Assert.Single(Directory.GetFiles(dir,prefix+"*.fit"));
            var before=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            var fits=QhyFitsCodec.Read(path);
            var frame=new MonochromeFrame(fits.Width,fits.Height,fits.Pixels,
                G3FrameInputPolicy.ResolveEffectiveSaturationAdu(fits.Pixels,4095));
            var stars=G3StellarFocusAnalyzer.Analyze(frame).Stars;
            var id=SlitTargetIdentifier.Identify(frame,stars,new PixelPoint(857.06,424.38),100,8,1.5);
            if(prefix=="00066-")
            {
                Assert.Equal("TARGET_AMBIGUOUS",id.Gate.Code);
                Assert.False(Phd2FreshTargetFluxPolicy.HasMeasuredHandoffPosition(id));
            }
            else
            {
                Assert.Equal("TARGET_IDENTIFIED",id.Gate.Code);
                Assert.True(Phd2FreshTargetFluxPolicy.HasMeasuredHandoffPosition(id));
            }
            Assert.Equal(before,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            output.WriteLine($"{prefix} {id.Gate.Code}; raw SHA-256 unchanged: {before}");
        }
    }

    [Theory]
    [InlineData("HD-18552")]
    [InlineData("HD-21641")]
    [InlineData("X-Per-selection")]
    [InlineData("X-Per-guiding")]
    public void RecordedRawFrameUsesSignalAwareCoreOrRetainsRealAmbiguity(string name)
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_FRAME_DIR");
        if (string.IsNullOrWhiteSpace(root)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, name + ".json")));
        var r = doc.RootElement;
        var bytes = File.ReadAllBytes(Path.Combine(root, name + ".u16"));
        Assert.Equal(r.GetProperty("pixelsSha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        Assert.Equal(r.GetProperty("sourceSha256").GetString(), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(r.GetProperty("source").GetString()!))).ToLowerInvariant());
        var pixels = new ushort[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, pixels, 0, bytes.Length);
        var f = new MonochromeFrame(r.GetProperty("width").GetInt32(), r.GetProperty("height").GetInt32(),
            pixels, 65520);
        var prediction = new PixelPoint(r.GetProperty("x").GetDouble(), r.GetProperty("y").GetDouble());
        var candidates = StarFieldDetector.Detect(f);
        var id = SlitTargetIdentifier.Identify(f, candidates, prediction, 100, 8, 1.5);
        var topology = SaturatedTargetGhostTopologyAnalyzer.Analyze(f, prediction, 100);
        output.WriteLine($"{name}: {id.Gate.Code}; topology={topology.Gate.Code}; source SHA={r.GetProperty("sourceSha256")}");
        if (name.StartsWith("HD-", StringComparison.Ordinal))
        {
            Assert.Equal(GateDisposition.Passed, topology.Gate.Disposition);
            Assert.NotNull(topology.Target);
            if (name == "HD-21641")
            {
                Assert.Equal(GateSeverity.Warning, topology.Gate.Severity);
                Assert.InRange(topology.Target.DistanceToPredictionPixels, 1, 2);
            }
            else Assert.InRange(topology.Target.DistanceToPredictionPixels, 34, 37);
        }
        else
        {
            Assert.Equal("TARGET_AMBIGUOUS", id.Gate.Code);
            Assert.False(Phd2FreshTargetFluxPolicy.HasMeasuredHandoffPosition(id));
        }
    }
}
