using UvexAdv.Core;

namespace UvexAdv.Core.Tests;

public sealed class CalibrationLibraryTests
{
    [Fact]
    public void PlanSortsAndDeduplicatesDarkExposures()
    {
        var plan = CalibrationCapturePlan.Create(
            "ATR585M", "stable-id", 100, 0, 1, 1, "High Conversion Gain", -10, 0.5, 2,
            0.000276, 16, [600, 300, 600], 5);

        Assert.Collection(
            plan.Groups,
            group => Assert.Equal(CalibrationFrameKind.Bias, group.Kind),
            group => Assert.Equal(300, group.ExposureSeconds),
            group => Assert.Equal(600, group.ExposureSeconds));
    }

    [Fact]
    public void LibraryPathEncodesEveryCompatibilityDimension()
    {
        var plan = CalibrationCapturePlan.Create(
            "ATR585M", "stable-id", 100, 4, 2, 1, "High Conversion Gain", -10, 0.5, 2,
            0.001, 1, [300], 1);

        var path = CalibrationLibraryPath.GetRawDirectory(
            "C:\\Calibration", plan, plan.Groups[1], new DateOnly(2026, 8, 15));

        Assert.EndsWith(
            Path.Combine("ATR585M", "G100_O4", "B2x2", "R1_High_Conversion_Gain", "T-10C", "DARK", "300s", "raw", "2026-08-15"),
            path);
    }

    [Fact]
    public void SafeSegmentIsStableAcrossNinaAndWindowsSanitizers()
    {
        Assert.Equal("ATR585M_fixture_0_1", CalibrationLibraryPath.SafeSegment("ATR585M (fixture&0&1)"));
        Assert.Equal("High_Conversion_Gain", CalibrationLibraryPath.SafeSegment("High Conversion Gain"));
    }

    [Fact]
    public void FiveFramesRejectOneHighAndOneLowValue()
    {
        var accumulator = new RobustFrameAccumulator(2);
        accumulator.Add([100, 500]);
        accumulator.Add([101, 501]);
        accumulator.Add([102, 502]);
        accumulator.Add([103, 503]);
        accumulator.Add([65000, 504]);

        Assert.True(accumulator.UsesTrimmedMean);
        Assert.Equal(new ushort[] { 102, 502 }, accumulator.BuildMaster());
    }

    [Fact]
    public void ShortGroupIsAProvisionalMean()
    {
        var accumulator = new RobustFrameAccumulator(1);
        accumulator.Add([100]);
        accumulator.Add([104]);

        Assert.False(accumulator.UsesTrimmedMean);
        Assert.Equal((ushort)102, accumulator.BuildMaster()[0]);
    }
}
