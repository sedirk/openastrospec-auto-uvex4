using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class QhyG3SkyDriftPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);
    private static readonly QhyG3FastPairPolicy Policy = new(1, "test", true, 2, TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30), 2, TimeSpan.FromHours(1), 20);

    [Fact]
    public void TwoActualWideFieldSolutionsMeasureDriftWithoutAnyG3StarAndChargeLatencyAndError()
    {
        var first = Frame(0, 350, 30);
        var moved = G3AcquisitionMotionPlanner.ApplyTangentOffsetArcseconds(350, 30, 2.5, -1.25);
        var second = Frame(5, moved.RaDegrees, moved.DecDegrees);
        var r = QhyG3SkyDriftPolicy.Build(first, second, 1, 1, Start.AddSeconds(2), Start.AddSeconds(4),
            2000, Policy, Start.AddSeconds(9), 30);
        var w = Assert.IsType<QhyG3SkyDriftWitness>(r.Witness);
        Assert.InRange(w.EastArcsecondsPerSecond, .499, .501);
        Assert.InRange(w.NorthArcsecondsPerSecond, -.251, -.249);
        Assert.Equal(5, w.BaselineSeconds);
        Assert.InRange(w.AdditionalUncertaintyArcseconds, 8, 10); // Includes both integrated QHY shutters and the G3 half-exposure.
        Assert.Equal(1, w.G3ExposureHalfDurationSeconds);
        Assert.True(w.FirstQhyIntegrationUncertaintyArcseconds > 0);
        Assert.True(w.SecondQhyIntegrationUncertaintyArcseconds > 0);
        Assert.True(w.ValidUntilUtc > Start.AddSeconds(9));
        Assert.True(w.ValidUntilUtc <= second.ExposureEndedUtc.AddSeconds(10));
        Assert.False(w.G3StarsRequired);
        Assert.False(w.MountStableMeansPixelStable);
        var later = QhyG3SkyDriftPolicy.Build(first, second, 1, 1, Start.AddSeconds(2), Start.AddSeconds(4),
            2000, Policy, Start.AddSeconds(12), 30).Witness!;
        Assert.True(later.AdditionalUncertaintyArcseconds > w.AdditionalUncertaintyArcseconds);
    }

    [Theory]
    [InlineData("same-frame")] [InlineData("reverse-time")] [InlineData("missing-shutter")]
    [InlineData("unknown-error")] [InlineData("latency")] [InlineData("uncertainty")]
    [InlineData("parity")] [InlineData("scale")] [InlineData("rotation")]
    [InlineData("baseline-too-long")] [InlineData("actual-shutter-mismatch")]
    public void UnknownDriftAndInvalidPixelGeometryNeverBecomeZeroDriftOrUnlimitedFreshness(string fault)
    {
        var first = Frame(0, 350, 30);
        var second = Frame(5, 350.001, 30);
        var started = Start.AddSeconds(2); var ended = Start.AddSeconds(4);
        var exposure = 2000; var uncertainty = 30d; var firstError = 1d; var now = Start.AddSeconds(9);
        switch (fault)
        {
            case "same-frame": second = second with { FrameSha256 = first.FrameSha256 }; break;
            case "reverse-time": (first, second) = (second, first); break;
            case "missing-shutter": started = default; break;
            case "unknown-error": firstError = double.NaN; break;
            case "latency": now = Start.AddSeconds(24); break;
            case "uncertainty": uncertainty = .1; break;
            case "parity": second = second with { Flipped = true }; break;
            case "scale": second = second with { PixelScaleArcseconds = 2.5 }; break;
            case "rotation": second = second with { PositionAngleDegrees = 280 }; break;
            case "baseline-too-long": second = Frame(35, 350.001, 30); now = Start.AddSeconds(39); break;
            case "actual-shutter-mismatch": exposure = 3000; break;
        }
        var r = QhyG3SkyDriftPolicy.Build(first, second, firstError, 1, started, ended, exposure, Policy, now, uncertainty);
        Assert.Null(r.Witness);
        Assert.Equal(GateDisposition.Passed, r.Gate.Disposition);
        Assert.Equal(GateSeverity.Warning, r.Gate.Severity);
    }

    [Fact]
    public void EvenEqualMeasuredCentresRetainPositiveDifferentialUncertaintyRatherThanAssumingExactZero()
    {
        var w = Assert.IsType<QhyG3SkyDriftWitness>(QhyG3SkyDriftPolicy.Build(Frame(0, 350, 30), Frame(5, 350, 30),
            1, 1, Start.AddSeconds(2), Start.AddSeconds(4), 2000, Policy, Start.AddSeconds(9), 30).Witness);
        Assert.Equal(0, w.EastArcsecondsPerSecond);
        Assert.True(w.DriftRateUncertaintyArcsecondsPerSecond > 0);
        Assert.True(w.AdditionalUncertaintyArcseconds > 0);
    }

    [Fact]
    public void NativePcShutterBracketAndSmallPolarRotationIncreaseUncertaintyRatherThanInventExactTiming()
    {
        var first = Frame(0, 350, 30);
        var second = Frame(5, 350.001, 30);
        var exact = QhyG3SkyDriftPolicy.Build(first, second, 1, 1, Start.AddSeconds(2), Start.AddSeconds(4),
            2000, Policy, Start.AddSeconds(9), 30).Witness!;
        var bracket = Assert.IsType<QhyG3SkyDriftWitness>(QhyG3SkyDriftPolicy.Build(first,
            second with { PositionAngleDegrees = 270.01 }, 1, 1, Start.AddSeconds(1), Start.AddSeconds(5),
            2000, Policy, Start.AddSeconds(9), 30).Witness);
        Assert.True(bracket.AdditionalUncertaintyArcseconds > exact.AdditionalUncertaintyArcseconds);
        Assert.True(bracket.DriftRateUncertaintyArcsecondsPerSecond > exact.DriftRateUncertaintyArcsecondsPerSecond);
    }

    [Fact]
    public void WideNativeTimeBracketCannotExpandTheOriginalPairMidpointTimeWindow()
    {
        var result = QhyG3SkyDriftPolicy.Build(Frame(0, 350, 30), Frame(5, 350, 30), 1, 1,
            Start.AddSeconds(-22), Start.AddSeconds(5), 2000, Policy, Start.AddSeconds(9), 1000);
        Assert.Null(result.Witness);
        Assert.Equal("QHY_G3_SKY_DRIFT_TIME_INVALID", result.Gate.Code);
    }

    [Theory]
    [InlineData("G3")] [InlineData("QHY")]
    public void LongerShuttersAtExactlyTheSameMidpointsIncreaseUncertaintyAndCanConsumeTheOriginalEnvelope(string camera)
    {
        var first = Frame(0, 350, 30);
        var second = Frame(5, 350.001, 30);
        var shortFirst = first with { ExposureStartedUtc = first.ExposureMidpointUtc.AddSeconds(-.1),
            ExposureEndedUtc = first.ExposureMidpointUtc.AddSeconds(.1) };
        var shortSecond = second with { ExposureStartedUtc = second.ExposureMidpointUtc.AddSeconds(-.1),
            ExposureEndedUtc = second.ExposureMidpointUtc.AddSeconds(.1) };
        var g3Midpoint = Start.AddSeconds(3);
        QhyG3SkyDriftResult Evaluate(bool longer, double allowance)
        {
            var qhyLonger = camera == "QHY" && longer;
            var g3Milliseconds = camera == "G3" && longer ? 2000 : 200;
            return QhyG3SkyDriftPolicy.Build(qhyLonger ? first : shortFirst, qhyLonger ? second : shortSecond,
                1, 1, g3Midpoint.AddMilliseconds(-g3Milliseconds / 2d), g3Midpoint.AddMilliseconds(g3Milliseconds / 2d),
                g3Milliseconds, Policy, Start.AddSeconds(9), allowance);
        }
        var shortShutter = Assert.IsType<QhyG3SkyDriftWitness>(Evaluate(false, 100).Witness);
        var longShutter = Assert.IsType<QhyG3SkyDriftWitness>(Evaluate(true, 100).Witness);
        Assert.Equal(shortShutter.FirstMidpointUtc, longShutter.FirstMidpointUtc);
        Assert.Equal(shortShutter.SecondMidpointUtc, longShutter.SecondMidpointUtc);
        Assert.Equal(shortShutter.G3MidpointUtc, longShutter.G3MidpointUtc);
        Assert.Equal(shortShutter.EastArcsecondsPerSecond, longShutter.EastArcsecondsPerSecond);
        Assert.True(longShutter.AdditionalUncertaintyArcseconds > shortShutter.AdditionalUncertaintyArcseconds);
        var originalEnvelope = (shortShutter.AdditionalUncertaintyArcseconds + longShutter.AdditionalUncertaintyArcseconds) / 2;
        Assert.NotNull(Evaluate(false, originalEnvelope).Witness);
        Assert.Null(Evaluate(true, originalEnvelope).Witness);
    }

    private static QhyG3SolvedFrame Frame(int seconds, double ra, double dec) => new("QHY", "qhy", $"{seconds}.fit",
        QhyG3LocalModelPolicy.HashFields(seconds.ToString()), $"{seconds}.json", QhyG3LocalModelPolicy.HashFields("solve" + seconds),
        Start.AddSeconds(seconds), Start.AddSeconds(seconds + 1), Start.AddSeconds(seconds + 2), Start.AddSeconds(seconds + 3),
        "formal native QHY shutter timestamps", 3856, 2180, 1, 1, 0, 0, 3856, 2180, ra, dec, 2, 270, false,
        QhyG3LocalModelPolicy.HashFields("binding" + seconds));
}
