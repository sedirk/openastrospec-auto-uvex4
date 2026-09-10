using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3RunLedSlitGeometryPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow.AddSeconds(-1);
    private static string H(char c) => new(c, 64);
    private static readonly SlitGeometry Geometry = new("this-run-led", new PixelPoint(64, 40),
        -2, 100, 2.5, 0.5, "camera", 1, 1);
    private static GateResult Passed => GateResult.Pass("MEASURED", "real LED measurement fixture");
    private static G3SlitGeometryRunCache Cache() => new(
        "run", H('B'), H('C'), 2, 15, "camera", 1, 1000, 128, 80,
        Now.AddMinutes(-10),
        new SlitIlluminationPairAnalysis(Passed, Geometry, SlitIlluminationPolarity.Dark,
            12, 0, 0, 2.5, 0.9, 2, 1, 0, 0),
        new SlitLocusDetection(Passed, Geometry, 12, 0, 0),
        new SlitWheelIdentityResult(Passed, "wheel", H('F'), 2, 15, 2.5, 0.5, null, []),
        "identity.json", "led-off.fit", H('A'), H('D'), H('E'), 7);
    private static G3RunLedSlitGeometryContext Context() => new(
        "run", H('A'), H('B'), H('C'), "camera", 1, 128, 80, 2, 15, 7,
        true, true, true, true, H('D'), H('E'), Now);

    [Fact]
    public void ValidRunLedGeometryNeedsNoDarkSlitPixelsOrStellarFlux()
    {
        // No stellar image, target flux, sky level or dark-line contrast is an
        // input. This gate proves geometry only; the caller still measures stars.
        var result = G3RunLedSlitGeometryPolicy.Evaluate(Cache(), Context());
        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal("G3_RUN_LED_SLIT_GEOMETRY_VALID", result.Gate.Code);
        Assert.Equal(Geometry, result.Geometry);
        Assert.Equal(0, result.Gate.Metrics!["stellarFrameSlitDetectionRequired"]);
        Assert.Equal(0, result.Gate.Metrics["targetOrGuidePositionReplaced"]);
        Assert.Equal(600, result.Gate.Metrics["ledReferenceAgeSeconds"]);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("action")]
    [InlineData("preset")]
    [InlineData("night")]
    [InlineData("source")]
    [InlineData("identity-file")]
    [InlineData("camera")]
    [InlineData("binning")]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("roi")]
    [InlineData("slit")]
    [InlineData("slit-width")]
    [InlineData("epoch")]
    [InlineData("uvex")]
    [InlineData("led-on-or-unknown")]
    [InlineData("focus-moving")]
    [InlineData("before-led-measurement")]
    public void InvalidBindingCannotBecomeSlitAuthority(string mutation)
    {
        var c = Context();
        c = mutation switch
        {
            "run" => c with { ObservationRunId = "other" },
            "action" => c with { ActionConfigurationSha256 = H('F') },
            "preset" => c with { CommissioningPresetSha256 = H('F') },
            "night" => c with { NightSetupSha256 = H('F') },
            "source" => c with { SourceFrameSha256 = H('F') },
            "identity-file" => c with { IdentityEvidenceSha256 = H('F') },
            "camera" => c with { CameraStableId = "other" },
            "binning" => c with { Binning = 2 },
            "width" => c with { ImageWidth = 127 },
            "height" => c with { ImageHeight = 79 },
            "roi" => c with { FullFrameOrigin = false },
            "slit" => c with { SlitPosition = 3 },
            "slit-width" => c with { SlitWidthMicrometers = 25 },
            "epoch" => c with { ConnectionEpoch = 8 },
            "uvex" => c with { UvexMatchesSetup = false },
            "led-on-or-unknown" => c with { LedConfirmedOff = false },
            "focus-moving" => c with { FocusStableAcrossFrame = false },
            _ => c with { FrameCompletedUtc = Now.AddMinutes(-11) },
        };
        Assert.NotEqual(GateDisposition.Passed, G3RunLedSlitGeometryPolicy.Evaluate(Cache(), c).Gate.Disposition);
    }

    [Theory]
    [InlineData("pair")]
    [InlineData("aperture")]
    [InlineData("wheel")]
    [InlineData("geometry-camera")]
    public void HistoricalSeedsOrFailedLedMeasurementsDoNotQualify(string failure)
    {
        var cache = Cache();
        var rejected = GateResult.Unknown("NOT_MEASURED", "no independent LED proof");
        cache = failure switch
        {
            "pair" => cache with { PairAnalysis = cache.PairAnalysis with { Gate = rejected } },
            "aperture" => cache with { SlitDetection = cache.SlitDetection with { Gate = rejected } },
            "wheel" => cache with { SlitIdentity = cache.SlitIdentity with { Gate = rejected } },
            _ => cache with { SlitDetection = cache.SlitDetection with
                { Geometry = Geometry with { CameraIdentity = "other" } } },
        };
        Assert.NotEqual(GateDisposition.Passed, G3RunLedSlitGeometryPolicy.Evaluate(cache, Context()).Gate.Disposition);
    }
}
