using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3SignalExposureReprobeTests
{
    private static G3ShortPositionMeasurement Missing(string code = "G3_SEP_UNMEASURED") =>
        new(GateResult.Unknown(code, "No supported target", new Dictionary<string, double>
            { ["sepValidRoiParents"] = 0, ["sepSaturatedRoiComponents"] = 0 }),
            new(GateResult.Unknown(code, "No target"), null, new(770.4841, 390.6491), double.PositiveInfinity, 0), true, 0);

    [Fact]
    public void RecordedWr152TenMsEmptyFrameSelectsLongerExposureNotAnotherIdenticalEmptyProbe()
    {
        // 2026-10-02 16:07Z, source WCS 5s/gain100; short frames gain0.
        // ROI raw peaks 4256/4288/4224 ADU; no qualified parent in any frame.
        var p = new G3ShortExposurePolicy(10);
        var m = Missing();
        var next = p.AfterUnmeasured(m, 1, 4256, 65520, 5000);
        Assert.Equal(100, next.ExposureMilliseconds);
        Assert.Equal(4, next.MaximumFrames);
        Assert.Equal(1, next.SignalExposureIncreases);
        Assert.Equal(0, next.Reductions);
        var decision = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(null, m, null, new('a', 64), 1, 100, next.MaximumFrames);
        Assert.True(decision.RetryAllowed);
        Assert.False(decision.Accepted);
        Assert.Null(m.Identification.Target);
    }

    [Fact]
    public void SignalSearchIsCappedAtSourceExposureThreeIncreasesAndSixTotalFrames()
    {
        var m = Missing();
        var a = new G3ShortExposurePolicy(10).AfterUnmeasured(m, 1, 4256, 65520, 5000);
        var b = a.AfterUnmeasured(m, 2, 4256, 65520, 5000);
        var c = b.AfterUnmeasured(m, 3, 4256, 65520, 5000);
        Assert.Equal(1000, b.ExposureMilliseconds);
        Assert.Equal(5000, c.ExposureMilliseconds);
        Assert.Equal(6, c.MaximumFrames);
        Assert.Equal(3, c.SignalExposureIncreases);
        Assert.Equal(c, c.AfterUnmeasured(m, 4, 4256, 65520, 10000));
        var small = new G3ShortExposurePolicy(10).AfterUnmeasured(m, 1, 4256, 65520, 50);
        Assert.Equal(50, small.ExposureMilliseconds);
        Assert.Equal(small, small.AfterUnmeasured(m, 2, 4256, 65520, 50));
    }

    [Fact]
    public void RisingSignalStopsAtConservativeRawHeadroomAndNeverOscillatesAfterSaturation()
    {
        var m = Missing();
        var p = new G3ShortExposurePolicy(100).AfterUnmeasured(m, 1, 10000, 65520, 5000);
        Assert.Equal(524, p.ExposureMilliseconds);
        Assert.True(10000d * p.ExposureMilliseconds / 100 <= .8 * 65520);
        var reduced = p.AfterMeasurement(G3ShortExposurePolicy.SaturatedCode, 2);
        Assert.Equal(262, reduced.ExposureMilliseconds);
        Assert.Equal(reduced, reduced.AfterUnmeasured(m, 3, 4256, 65520, 5000));
        Assert.Equal(1, reduced.SignalExposureIncreases);
    }

    [Theory]
    [InlineData("G3_SEP_AMBIGUOUS")]
    [InlineData("G3_SEP_CATALOG_PRIMARY_UNMEASURED")]
    [InlineData("G3_SEP_INVALID_COVERAGE")]
    [InlineData("G3_SEP_SATURATED")]
    [InlineData("G3_SHORT_REPEAT_DISAGREES")]
    [InlineData("G3_SEP_PARENT_BLEND_UNRESOLVED")]
    public void IdentityCoverageAndBlendFailuresCannotRequestSignalExposure(string code)
    {
        var p = new G3ShortExposurePolicy(10);
        Assert.Equal(p, p.AfterUnmeasured(Missing(code), 1, 4256, 65520, 5000));
    }

    [Theory]
    [InlineData(double.NaN, 65520, 5000)]
    [InlineData(0, 65520, 5000)]
    [InlineData(40000, 65520, 5000)]
    [InlineData(65520, 65520, 5000)]
    [InlineData(4256, double.NaN, 5000)]
    [InlineData(4256, 65520, double.NaN)]
    [InlineData(4256, 65520, 0)]
    public void MissingHeadroomOrAttestedExposureCannotExtend(double peak, double saturation, double exposure)
    {
        var p = new G3ShortExposurePolicy(10);
        Assert.Equal(p, p.AfterUnmeasured(Missing(), 1, peak, saturation, exposure));
    }

    [Fact]
    public void LastTwoFramesAreReservedForIndependentConfirmation()
    {
        var p = new G3ShortExposurePolicy(10, 6);
        Assert.Equal(p, p.AfterUnmeasured(Missing(), 5, 4256, 65520, 5000));
        var star = new StarCandidate(new(770, 391), 5000, 10000, 30, 0, .2, 0, 100);
        var gate = GateResult.Pass("G3_SEP_POSITION_MEASURED", "Measured");
        var good = new G3ShortPositionMeasurement(gate, new(gate, star, new(770, 391), 0, 2), true, .5);
        Assert.False(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(null, good, null, new('a', 64), 5, 100, 6).Accepted);
        Assert.True(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(good, good, new('a', 64), new('b', 64), 6, 100, 6).Accepted);
        Assert.False(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(good, good, new('a', 64), new('a', 64), 6, 100, 6).Accepted);
        Assert.Equal(p, p.AfterUnmeasured(Missing() with { Gate = GateResult.Unknown("G3_SEP_UNMEASURED", "Missing metrics") }, 1, 4256, 65520, 5000));
    }

    [Fact]
    public void RawHeadroomIncludesUndetectedHotPixelsAndRejectsPartialCoverage()
    {
        var pixels = Enumerable.Repeat((ushort)4096, 100 * 100).ToArray();
        pixels[50 * 100 + 59] = 65520;
        pixels[50 * 100 + 70] = 65535;
        var frame = new MonochromeFrame(100, 100, pixels, 65520);
        Assert.Equal(65520, G3ShortExposurePolicy.RecognitionPeak(frame, new(50, 50), 10));
        Assert.True(double.IsNaN(G3ShortExposurePolicy.RecognitionPeak(frame, new(2, 2), 10)));
        Assert.True(double.IsNaN(G3ShortExposurePolicy.RecognitionPeak(frame, new(double.NaN, 50), 10)));
        Assert.Equal(4096, G3ShortExposurePolicy.RecognitionPeak(frame, new(50, 50), 5));
    }
}
