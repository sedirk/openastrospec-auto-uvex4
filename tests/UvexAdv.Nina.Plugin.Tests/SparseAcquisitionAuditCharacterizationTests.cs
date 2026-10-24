using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

/// <summary>Regressions for the .311 audit findings. No hardware owner is opened.</summary>
public sealed class SparseAcquisitionAuditCharacterizationTests
{
    [Theory]
    [InlineData("skip")]
    [InlineData("cleanup-failure")]
    [InlineData("late-unsafe")]
    [InlineData("late-cancel")]
    [InlineData("late-deadline")]
    [InlineData("end-policy")]
    public async Task ReturnedPredictionWindowMissActuallyExecutesNextNativeTargetOnlyAtSafeBoundary(string boundary)
    {
        var gate = G3PostWcsPredictionPolicy.RejectedTargetGate(
            "The optical prediction misses the acquisition window; saved origin is independently verified by the terminal boundary.");
        Assert.Equal(GateFailureScope.TargetQuality, gate.FailureScope);
        Assert.True(NativeSequencePlan.IsSkippableQualityFailure(gate));
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(
            boundary, gate.Code, producedGate: gate);
    }

    [Theory]
    [InlineData("SLIT_DARK_APERTURE_LONG_EXPOSURE_CLIPPED", "skip")]
    [InlineData("SLIT_DARK_APERTURE_SECOND_EDGE_NOT_FOUND", "skip")]
    [InlineData("SLIT_DARK_APERTURE_LONG_EXPOSURE_CLIPPED", "cleanup-failure")]
    [InlineData("SLIT_DARK_APERTURE_SECOND_EDGE_NOT_FOUND", "late-unsafe")]
    public async Task ValidatedHdrImageMissKeepsQualityScopeAfterOneRetryAndActuallyExecutesNextNativeTargetOnlyAfterStop(
        string opticalCode, string boundary)
    {
        var measurement = Measurement(opticalCode);
        var gate = SlitWheelIdentityMatcher.Match(Calibration(), measurement, 2, 15,
            "audit-g3", 1, 1, 1920, 1080).Gate;
        Assert.Equal("SLIT_LED_IDENTITY_GEOMETRY_UNAVAILABLE", gate.Code);
        Assert.Contains(opticalCode, gate.Message);
        Assert.Same(measurement.Gate.Metrics, gate.Metrics);
        Assert.Equal(GateFailureScope.TargetQuality, gate.FailureScope);
        var retries = new ObservationAutomaticRecoverySession();
        Assert.True(retries.Evaluate(ObservationStage.AcquireG3SlitField, gate).ShouldRetry);
        Assert.True(retries.Evaluate(ObservationStage.AcquireG3SlitField, gate).Exhausted);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(boundary, gate.Code, producedGate: gate);
    }

    [Theory]
    [InlineData("SLIT_DARK_APERTURE_EDGE_DIRECTION_MISMATCH")]
    [InlineData("SLIT_DARK_APERTURE_MODEL_NOT_COMMISSIONED")]
    [InlineData("UNKNOWN_HDR_OWNER_OR_PROCESS_FAILURE")]
    public async Task PhysicalConfigurationAndUnknownHdrFailureStillSuppressSecondNativeTarget(string code)
    {
        var gate = SlitWheelIdentityMatcher.Match(Calibration(), Measurement(code), 2, 15,
            "audit-g3", 1, 1, 1920, 1080).Gate;
        Assert.Equal(GateFailureScope.Run, gate.FailureScope);
        Assert.False(NativeSequencePlan.IsSkippableQualityFailure(gate));
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("hard-stop", gate.Code, producedGate: gate);
    }

    [Theory]
    [InlineData("calibration", "SLIT_LED_IDENTITY_CALIBRATION_INVALID")]
    [InlineData("detector", "SLIT_LED_IDENTITY_DETECTOR_MISMATCH")]
    [InlineData("declaration", "SLIT_LED_IDENTITY_DECLARATION_INVALID")]
    public void PureImageMissCannotHideAnIndependentConfigurationOrIdentityFailure(string defect, string expected)
    {
        var calibration = Calibration();
        if (defect == "calibration") calibration = calibration with { CalibrationSha256 = "corrupt" };
        var gate = SlitWheelIdentityMatcher.Match(calibration, Measurement("SLIT_DARK_APERTURE_SECOND_EDGE_NOT_FOUND"),
            defect == "declaration" ? 3 : 2, 15, defect == "detector" ? "wrong-camera" : "audit-g3",
            1, 1, 1920, 1080).Gate;
        Assert.Equal(expected, gate.Code);
        Assert.Equal(GateFailureScope.Run, gate.FailureScope);
        Assert.False(NativeSequencePlan.IsSkippableQualityFailure(gate));
    }

    [Fact]
    public void OldConnectedBranchNoLongerPoisonsADirectFreshRawPair()
    {
        var start = DateTimeOffset.Parse("2026-10-24T00:00:00Z");
        G3SparseExposureTiming Timing(int seconds) => new(start.AddSeconds(seconds),
            start.AddSeconds(seconds + 2), 2, start.AddSeconds(seconds + 3));
        var current = Timing(150);
        var recent = Timing(119);
        var window = G3SparseGraphWindowPolicy.Evaluate(current, recent,
            [Timing(0), Timing(40), Timing(80), recent], start.AddSeconds(154), 100);
        Assert.True(window.Usable);
        Assert.True(window.StartIndependentComponent);
        Assert.Equal(31, window.Timing.MaximumAdjacentGapUpperSeconds);
        Assert.Equal(3, window.Timing.CurrentMidpointAgeUpperSeconds);
        Assert.False(window.Timing.MotionAuthority);
    }

    [Fact]
    public void RebaseCannotRescueStaleCurrentPixelsOrAnOverGapCandidate()
    {
        var now = DateTimeOffset.Parse("2026-10-24T00:10:00Z");
        G3SparseExposureTiming Timing(int ago) => new(now.AddSeconds(-ago - 2), now.AddSeconds(-ago), 2, now);
        Assert.False(G3SparseGraphWindowPolicy.Evaluate(Timing(50), Timing(60), [Timing(150), Timing(60)], now, 100).Usable);
        Assert.False(G3SparseGraphWindowPolicy.Evaluate(Timing(0), Timing(65), [Timing(150), Timing(65)], now, 100).Usable);
    }

    private static SlitDarkApertureHdrAnalysis Measurement(string code)
    {
        var seed = new SlitGeometry("audit", new PixelPoint(800, 400), 0, 400, 9, .4, "audit-g3", 1, 1);
        return new(GateResult.Unknown(code, "Immutable optical image evidence only.",
                new Dictionary<string, double> { ["fitSignalToNoise"] = 1.5 }),
            seed, seed, SlitDarkApertureResolution.Unresolved,
            double.NaN, double.NaN, double.NaN, 0, 0, 0, 1, 0, 0);
    }

    private static SlitWheelIdentityCalibration Calibration()
    {
        var widths = new[] { 70d, 9d, 15d, 22d };
        var nominal = new[] { 300d, 15d, 25d, 35d };
        var entries = Enumerable.Range(0, 4).Select(i => new SlitWidthFingerprint(i + 1, $"{nominal[i]}um",
            nominal[i], widths[i], .4, DateTimeOffset.Parse("2026-10-24T00:00:00Z"), new((char)('A' + i), 64),
            SlitDarkApertureResolution.DirectTwoEdge, widths[i] / 2, .1, new((char)('1' + i), 64), new((char)('6' + i), 64))).ToArray();
        var calibration = new SlitWheelIdentityCalibration(2, "audit", "audit-install", "audit-g3",
            1, 1, 1920, 1080, 3, 2, entries, "").WithComputedSha256();
        Assert.Empty(calibration.Validate());
        return calibration;
    }
}
