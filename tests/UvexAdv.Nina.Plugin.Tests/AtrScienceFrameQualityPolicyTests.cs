using UvexAdv.Observatory;
using UvexAdv.Spectroscopy;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class AtrScienceFrameQualityPolicyTests
{
    [Theory]
    [InlineData(0, 3, false)]
    [InlineData(0, 4, true)]
    [InlineData(1, 4, true)]
    [InlineData(1, 5, true)]
    [InlineData(2, 4, false)]
    [InlineData(2, 5, false)]
    public void AttemptExhaustionOnlyStopsAnUnfinishedScienceBlock(int accepted, int attempted, bool exhausted)
    {
        var gate = AtrScienceFrameQualityPolicy.RemainingAttemptGate(accepted, attempted, Configuration());

        if (!exhausted)
        {
            Assert.Null(gate);
            return;
        }
        Assert.NotNull(gate);
        Assert.Equal("ATR_ATTEMPT_LIMIT", gate.Code);
        Assert.False(new StageResult(gate).CanAdvance);
        Assert.False(ObservationAutomaticRecoveryPolicy.For(ObservationStage.RunScienceBlock, gate).IsRecoverable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalAcceptedFrameCompletesAtTheAttemptLimitIncludingAnApprovedWarning(bool lowSignalWarning)
    {
        var metrics = GoodFrame() with { TargetToSkyContrast = lowSignalWarning ? 0.5 : 10 };
        var quality = AtrScienceFrameQualityPolicy.Evaluate(metrics, Configuration());
        var acceptedFrames = 1;
        if (quality.Disposition == GateDisposition.Passed) acceptedFrames++;

        Assert.Equal(2, acceptedFrames);
        Assert.Equal(lowSignalWarning ? GateSeverity.Warning : GateSeverity.Info, quality.Severity);
        Assert.Null(AtrScienceFrameQualityPolicy.RemainingAttemptGate(acceptedFrames, 4, Configuration()));
    }

    [Fact]
    public void LastAttemptRejectedForGuideContinuityStopsWithoutProbeOrRecoveryAllowance()
    {
        var quality = AtrScienceFrameQualityPolicy.Evaluate(GoodFrame() with { GuidingStable = false }, Configuration());
        var acceptedFrames = 1;
        if (quality.Disposition == GateDisposition.Passed) acceptedFrames++;
        var terminal = AtrScienceFrameQualityPolicy.RemainingAttemptGate(acceptedFrames, 4, Configuration());

        Assert.Equal("GUIDING_UNSTABLE", quality.Code);
        Assert.Equal(1, acceptedFrames);
        Assert.NotNull(terminal);
        Assert.Equal("ATR_ATTEMPT_LIMIT", terminal.Code);
        Assert.False(ObservationAutomaticRecoveryPolicy.For(ObservationStage.RunScienceBlock, terminal).IsRecoverable);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void LostExposureContinuityCannotBeAcceptedEvenWithSignalOrClippingWarnings(bool lowSignal, bool clipped)
    {
        var metrics = GoodFrame() with
        {
            GuidingStable = false,
            TargetToSkyContrast = lowSignal ? 0.5 : 10,
            SaturatedFraction = clipped ? 0.5 : 0,
        };

        var gate = AtrScienceFrameQualityPolicy.Evaluate(metrics, Configuration());

        Assert.Equal("GUIDING_UNSTABLE", gate.Code);
        Assert.Equal(GateDisposition.Indeterminate, gate.Disposition);
        Assert.Equal(GateSeverity.Error, gate.Severity);
        Assert.False(new StageResult(gate).CanAdvance);
        Assert.Contains("retained but not accepted", gate.Message, StringComparison.Ordinal);
        Assert.Equal(ObservationAutomaticRecoveryAction.RebuildStageDependencies,
            ObservationAutomaticRecoveryPolicy.For(ObservationStage.RunScienceBlock, gate).Action);
    }

    [Fact]
    public void CurrentGuidingAuthorityRemainsAcceptedWithoutANewSlitPrecisionThreshold()
    {
        // The runner has already applied the owner-approved supervised warning
        // policy when producing GuidingStable. No residual is reinterpreted here.
        var gate = AtrScienceFrameQualityPolicy.Evaluate(GoodFrame(), Configuration());

        Assert.Equal("ATR_FRAME_QUALITY_VALID", gate.Code);
        Assert.True(new StageResult(gate).CanAdvance);
    }

    [Fact]
    public void StableLowSignalFramesKeepTheirAcceptedStackingWarnings()
    {
        var contrast = AtrScienceFrameQualityPolicy.Evaluate(
            GoodFrame() with { TargetToSkyContrast = 0.5 }, Configuration());
        var snr = AtrScienceFrameQualityPolicy.Evaluate(
            GoodFrame() with { LineSnrPerResolutionElement = 1, ContinuumSnrPerResolutionElement = 1 }, Configuration());

        Assert.Equal("ATR_TARGET_CONTRAST_LOW", contrast.Code);
        Assert.Equal("ATR_SNR_LOW", snr.Code);
        Assert.All(new[] { contrast, snr }, gate =>
        {
            Assert.Equal(GateSeverity.Warning, gate.Severity);
            Assert.True(new StageResult(gate).CanAdvance);
        });
    }

    [Theory]
    [InlineData(0.01, 0, 0, 0, "ATR_SATURATION_LIMIT")]
    [InlineData(0, 0.01, 0, 0, "ATR_TRACE_SATURATION_LIMIT")]
    [InlineData(0, 0, 0.01, 0, "ATR_CLIPPED_COLUMNS_LIMIT")]
    [InlineData(0, 0, 0, 4, "ATR_CONSECUTIVE_CLIP_LIMIT")]
    public void ExistingTraceClippingLimitsStillRejectStableFrames(
        double saturation, double traceSaturation, double clippedColumns, int consecutiveColumns, string code)
    {
        var gate = AtrScienceFrameQualityPolicy.Evaluate(GoodFrame() with
        {
            SaturatedFraction = saturation,
            TraceSaturatedFraction = traceSaturation,
            ClippedDispersionColumnFraction = clippedColumns,
            LongestClippedDispersionColumnRun = consecutiveColumns,
        }, Configuration());

        Assert.Equal(code, gate.Code);
        Assert.False(new StageResult(gate).CanAdvance);
    }

    private static SpectralProbeMetrics GoodFrame() =>
        new(60, 256, 30000, 65535, 0, 100, 100, 10, true, "immutable-science-frame");

    private static AtrRunConfiguration Configuration() => new(
        Gain: 100, Offset: 256, Binning: 1, TargetTemperatureC: -10,
        ReadoutModeIndex: 0, Roi: new ImageRoi(0, 0, 100, 100),
        DispersionAxis: DispersionAxis.Horizontal, ApertureStart: 20, ApertureLength: 60,
        ExposureLadderSeconds: [30, 60, 120], ProbeExposureSeconds: 30,
        ScienceFrameCount: 2, MaximumScienceAttempts: 4,
        MaximumSaturatedFraction: 0.001, MaximumTraceSaturatedFraction: 0.001,
        MaximumClippedDispersionColumnFraction: 0.002, MaximumConsecutiveClippedDispersionColumns: 3,
        MinimumTargetToSkyContrast: 1.15, MinimumLineSnr: 8, MinimumContinuumSnr: 5);
}
