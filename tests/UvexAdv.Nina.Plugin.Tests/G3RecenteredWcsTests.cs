using System.Security.Cryptography;
using NINA.Astrometry;
using NINA.PlateSolving;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3RecenteredWcsTests
{
    [Theory]
    [InlineData(0, false)] [InlineData(37, false)] [InlineData(77, false)]
    [InlineData(145, false)] [InlineData(271, false)]
    [InlineData(0, true)] [InlineData(37, true)] [InlineData(77, true)]
    [InlineData(145, true)] [InlineData(271, true)]
    public void InverseAndArrivalRetainRotatedReflectedAsymmetricDetectorGeometry(double angle, bool reflected)
    {
        var radians = angle * Math.PI / 180;
        var sign = reflected ? -1 : 1;
        // Unequal scales, skew and an off-centre reference pixel deliberately
        // cannot be represented by a scalar angle/scale PlateSolveResult.
        double[][] cd = [
            [sign * .000107 * Math.Cos(radians), .000098 * (-Math.Sin(radians) + .12 * Math.Cos(radians))],
            [sign * .000107 * Math.Sin(radians), .000098 * (Math.Cos(radians) + .12 * Math.Sin(radians))],
        ];
        var source = new DetectorWcsResult(120, 30, [812.5, 600.25], cd, 1920, 1080);
        var target = Sky(120.01, 30.01);
        var desired = new PixelPoint(817.473, 426.867);
        var inverse = G3WcsTargetProjector.SolveCenterForTargetAtPixel(target, source,
            1920, 1080, MeasuredAstapRecovery.Identity, desired);

        // The same helper is used by production post-slew prediction and by
        // measured coarse correction, not just the inverse's private evaluator.
        var arrival = Assert.IsType<DetectorWcsResult>(G3WcsTargetProjector.Recenter(
            source, inverse.DesiredG3Center, 1920, 1080));
        Assert.Equal(reflected, arrival.Flipped);
        Assert.Equal(source.ReferencePixel, arrival.ReferencePixel);
        Assert.Equal(source.Cd[0], arrival.Cd[0]);
        Assert.Equal(source.Cd[1], arrival.Cd[1]);
        Assert.Equal(120, source.Coordinates.RADegrees, 10);
        Assert.Equal(30, source.Coordinates.Dec, 10);
        AssertPoint(desired, G3WcsTargetProjector.Project(target, arrival, 1920, 1080,
            MeasuredAstapRecovery.Identity), .1);

        var measured = new PixelPoint(734.2, 486.8);
        var local = Assert.IsType<DetectorWcsResult>(RealObservationStageRunner.MeasuredCoarseLocalModel(
            Evidence(source), target, measured, 1920, 1080));
        AssertPoint(measured, G3WcsTargetProjector.Project(target, local, 1920, 1080,
            MeasuredAstapRecovery.Identity), .1);
        Assert.Equal(source.Cd[0], local.Cd[0]);
        Assert.Equal(source.Cd[1], local.Cd[1]);
    }

    [Fact]
    public void Hd25627RecordedArrivalSearchesNearSlitInsteadOf358PixelsAway()
    {
        // Immutable run UVEX-20261030T185035Z-66935dd7c1a0438:
        // 00402 formal raw-verified WCS, 00409 inverse, 00410 first arrival raw,
        // 00414 old wrong prediction. These are geometry/coverage assertions,
        // not a claim that the bright candidate is the catalogue target.
        var source = RecordedWcs();
        var target = Sky(61.16494117570999, 17.51759233269);
        var slit = new PixelPoint(817.473, 426.867);
        var inverse = G3WcsTargetProjector.SolveCenterForTargetAtPixel(target, source,
            1920, 1080, MeasuredAstapRecovery.Identity, slit);
        Assert.Equal(61.149161208799164, inverse.DesiredG3Center.RADegrees, 7);
        Assert.Equal(17.52957193371269, inverse.DesiredG3Center.Dec, 7);
        var arrivalCenter = Sky(61.14875048849488, 17.529438649054466);
        var arrival = G3WcsTargetProjector.Recenter(source, arrivalCenter, 1920, 1080);
        var prediction = G3WcsTargetProjector.Project(target, arrival, 1920, 1080, MeasuredAstapRecovery.Identity);
        AssertPoint(new(817.87773097, 422.99310473), prediction, .00001);
        var lostGeometry = new PlateSolveResult
        {
            Success = true, Coordinates = arrivalCenter, Pixscale = source.Pixscale,
            PositionAngle = source.PositionAngle, Flipped = source.Flipped,
        };
        var oldPrediction = G3WcsTargetProjector.Project(target, lostGeometry, 1920, 1080, MeasuredAstapRecovery.Identity);
        AssertPoint(new(1036.1009205064, 707.0162443811), oldPrediction, .001);
        Assert.InRange(Distance(prediction, oldPrediction), 358.17, 358.19);
        // The existing 100 px candidate search now covers the actual detected
        // bright source. Independent identity/morphology/freshness gates remain.
        var firstBrightCandidate = new PixelPoint(801.97, 411.13);
        Assert.InRange(Distance(prediction, firstBrightCandidate), 19.84, 19.85);
        Assert.True(Distance(oldPrediction, firstBrightCandidate) > 100);
        Assert.InRange(Distance(prediction, new(790.18, 388.75)), 0, 100);
    }

    [Fact]
    public void RecordedHd25627RawReachesShortConfirmationButDoesNotInventShortFrameSuccess()
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var directory = Path.Combine(root, "UVEX-20261030T185035Z-66935dd7c1a0438", "evidence");
        var prediction = new PixelPoint(817.87773097, 422.99310473);
        var oldPrediction = new PixelPoint(1036.1009205064, 707.0162443811);
        var slit = new PixelPoint(817.6152, 430.9390);
        var files = new[]
        {
            ("00410-", "2219A60CBAF0E397D2D2169A98ADB8573A92BEE91BACFA08DC81EA8EC831F1D8", new PixelPoint(803.4783216783, 410.5972027972), true),
            ("00427-", "DF775AEDBA542ED60BB07EA6E4B6E1270C37020966E6837A4508B306C2F691B2", new PixelPoint(790.9404990403, 388.2811900192), false),
        };
        foreach (var (prefix, hash, centroid, canPropose) in files)
        {
            var path = Assert.Single(Directory.GetFiles(directory, prefix + "*.fit"));
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            var raw = QhyFitsCodec.Read(path);
            var frame = new MonochromeFrame(raw.Width, raw.Height, raw.Pixels,
                G3FrameInputPolicy.ResolveEffectiveSaturationAdu(raw.Pixels, 4095));
            Assert.Equal(65520, frame.SaturationLevel);
            var content = G3SolveProbeContentAnalyzer.Analyze(frame);
            Assert.True(content.HasCoherentSource);
            // Both historical SNR 5 and the default SNR 8 produce the same
            // topology candidate. No threshold/multi-lobed rejection is relaxed.
            foreach (var minimumSnr in new[] { 5d, 8d })
            {
                var actual = SlitTargetIdentifier.Identify(frame, content.StellarMeasurement.Stars,
                    prediction, 100, minimumSnr, 1.5);
                var old = SlitTargetIdentifier.Identify(frame, content.StellarMeasurement.Stars,
                    oldPrediction, 100, minimumSnr, 1.5);
                Assert.Equal("TARGET_NOT_FOUND", old.Gate.Code);
                Assert.Equal("TARGET_IDENTIFIED_SATURATED_TOPOLOGY", actual.Gate.Code);
                Assert.Equal(TargetIdentificationAuthority.BrightWingCentroid, actual.Authority);
                AssertPoint(centroid, actual.Target!.Centroid, .00001);
                Assert.Equal(canPropose, G3PostWcsMeasuredHandoffPolicy.CanProposeBoundedShortConfirmation(actual,
                    prediction, 5.239603230730207, slit, raw.Width, raw.Height, 20, 100, 44));
                Assert.False(G3PostWcsMeasuredHandoffPolicy.CanHandOff(actual, prediction,
                    5.239603230730207, slit, raw.Width, raw.Height, 20));
                // These are 5/10 s long frames, NOT the independent short pair
                // the corrected production route would request. Their clipped
                // cores are too large to pass short-position confirmation.
                var shortMeasurement = G3ShortPositionMeasurementPolicy.Measure(frame, prediction, 100, minimumSnr, 1.5);
                Assert.Equal("G3_SHORT_CORE_TOO_LARGE", shortMeasurement.Gate.Code);
                Assert.False(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(null, shortMeasurement,
                    null, hash, 1, 100).Accepted);
            }
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }
    }

    [Fact]
    public void RegisteredReferenceSkyAndDetectorCenterRemainDistinctAfterRecenter()
    {
        // G3RegisteredWcs keeps original CRVAL but assigns the new detector
        // centre to Coordinates after a measured image registration.
        var source = RecordedWcs();
        source.Coordinates = Sky(60.97, 17.58);
        var center = Sky(61.15, 17.53);
        var recentered = Assert.IsType<DetectorWcsResult>(G3WcsTargetProjector.Recenter(source, center, 1920, 1080));
        Assert.Equal(center.RADegrees, recentered.Coordinates.RADegrees, 10);
        Assert.Equal(center.Dec, recentered.Coordinates.Dec, 10);
        Assert.Equal(source.ReferenceSky.RADegrees + center.RADegrees - source.Coordinates.RADegrees,
            recentered.ReferenceSky.RADegrees, 10);
        Assert.Equal(source.ReferenceSky.Dec + center.Dec - source.Coordinates.Dec,
            recentered.ReferenceSky.Dec, 10);
        var target = Sky(61.16494117570999, 17.51759233269);
        var inverse = G3WcsTargetProjector.SolveCenterForTargetAtPixel(target, source, 1920, 1080,
            MeasuredAstapRecovery.Identity, new(817, 427));
        var moved = G3WcsTargetProjector.Recenter(source, inverse.DesiredG3Center, 1920, 1080);
        AssertPoint(new(817, 427), G3WcsTargetProjector.Project(target, moved, 1920, 1080,
            MeasuredAstapRecovery.Identity), .1);
    }

    [Fact]
    public void LegacyScalarSolveKeepsItsExistingProjectionAndDoesNotMutateSource()
    {
        var source = new PlateSolveResult { Success = true, Coordinates = Sky(120, 30),
            Pixscale = .38, PositionAngle = 256.4, Flipped = true, Radius = .12 };
        var center = Sky(120.02, 30.03);
        var recentered = G3WcsTargetProjector.Recenter(source, center, 1920, 1080);
        Assert.IsType<PlateSolveResult>(recentered);
        Assert.Equal(source.Pixscale, recentered.Pixscale);
        Assert.Equal(source.PositionAngle, recentered.PositionAngle);
        Assert.Equal(source.Flipped, recentered.Flipped);
        Assert.Equal(source.Radius, recentered.Radius);
        Assert.Equal(center.RADegrees, recentered.Coordinates.RADegrees);
        Assert.Equal(center.Dec, recentered.Coordinates.Dec);
        Assert.Equal(120, source.Coordinates.RADegrees);
        Assert.Equal(30, source.Coordinates.Dec);
    }

    [Fact]
    public void ProductionPostSlewPredictionUsesTheGeometryPreservingPath()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var start = source.IndexOf("var estimatedArrivalSolve =", StringComparison.Ordinal);
        var end = source.IndexOf("var predictedTarget =", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var block = source[start..end];
        Assert.Contains("G3WcsTargetProjector.Recenter(", block);
        Assert.Contains("currentField.Solve.Result, estimatedCenter", block);
        Assert.DoesNotContain("new PlateSolveResult", block);
    }

    private static Coordinates Sky(double ra, double dec) => new(ra, dec, Epoch.J2000, Coordinates.RAType.Degrees);
    private static DetectorWcsResult RecordedWcs() => new(60.96574897485, 17.57632159661,
        [959.5, 539.5], [
            [-2.380009299524e-05, -.0001035922784799],
            [.0001030932052874, -2.364352550323e-05],
        ], 1920, 1080);
    private static PlateSolveEvidence Evidence(PlateSolveResult solve) => new(solve, solve.Coordinates,
        0, MeasuredAstapRecovery.Identity, "anchor.fit", "anchor.json", new('A', 64), 1920, 1080,
        1, DateTimeOffset.UtcNow);
    private static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    private static void AssertPoint(PixelPoint expected, PixelPoint actual, double tolerance) =>
        Assert.InRange(Distance(expected, actual), 0, tolerance);
}
