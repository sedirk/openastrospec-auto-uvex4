using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3UnsaturatedShortPositionTests
{
    [Fact]
    public void KnownCompanionDisablesTheNearestWcsShortcutAndMeasuresBothPsfs()
    {
        var frame=Create((x,y)=>Gaussian(x,y,80,50,35000,1.7)+Gaussian(x,y,76,75,6000,1.7));
        var prediction=new PixelPoint(76,86);
        var ordinary=G3ShortPositionMeasurementPolicy.Measure(frame,prediction,70);
        Assert.Equal("TARGET_IDENTIFIED",ordinary.Gate.Code);
        Assert.InRange(ordinary.Identification.Target!.Centroid.Y,74,76);
        var resolved=G3ShortPositionMeasurementPolicy.Measure(frame,prediction,70,requireResolvedPsfCandidates:true);
        Assert.Equal("G3_SHORT_UNSATURATED_AMBIGUOUS",resolved.Gate.Code);
        Assert.Equal(2,resolved.ResolvedCandidates!.Count);
        var primary=G3ResolvedCompanionPositionPolicy.Resolve(resolved,new(-4,25),70);
        Assert.Equal("G3_SHORT_CATALOG_COMPANION_PRIMARY_MEASURED",primary.Gate.Code);
        Assert.InRange(primary.Identification.Target!.Centroid.Y,49,51);
    }

    [Theory]
    [InlineData(1.0, 1.1, 0.0)]
    [InlineData(1.15, 1.15, 0.2)]
    [InlineData(1.25, 1.25, 0.5)]
    public void PixelPhaseMayLeaveOneToFourInnerPixelsWithoutErasingThePsf(double sx, double sy, double phase)
    {
        var random = new Random(37);
        var frame = Create((x, y) => 40000 * Math.Exp(-.5 *
            (Math.Pow((x - 80 - phase) / sx, 2) + Math.Pow((y - 80 - phase) / sy, 2))) +
            Gaussian(x, y, 78, 80, 1800, 12) + random.Next(-150, 151));
        var result = G3ShortPositionMeasurementPolicy.Measure(frame, new(80, 105), 60, 5);
        Assert.True(result.Gate.Code == "G3_SHORT_UNSATURATED_POSITION_MEASURED", System.Text.Json.JsonSerializer.Serialize(result.Gate));
        Assert.True(result.RequiresIndependentRepeat);
        Assert.InRange(result.Gate.Metrics!["shortInnerContourPixels"], 1, 4);
        Assert.True(result.Gate.Metrics["shortMiddleContourPixels"] >= 5);
        Assert.True(result.Gate.Metrics["shortPsfPixels"] >= 9);
        Assert.Equal(1, result.Gate.Metrics["shortInnerSamplingAllowancePixels"]);
        Assert.InRange(result.Identification.Target!.Centroid.X, 79.5 + phase, 80.5 + phase);
        Assert.InRange(result.Identification.Target.Centroid.Y, 79.5 + phase, 80.5 + phase);
        Assert.False(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(null, result, null, new('a', 64), 1, 60).Accepted);
    }

    [Fact]
    public void OnePixelPeakOnAHaloWithNoMiddleAndOuterSupportIsNotPromoted()
    {
        var random = new Random(37);
        var frame = Create((x, y) => (x == 80 && y == 80 ? 40000 : 0) +
            Gaussian(x, y, 78, 80, 1800, 12) + random.Next(-150, 151));
        var result = G3ShortPositionMeasurementPolicy.Measure(frame, new(80, 105), 60, 5);
        Assert.Equal("G3_SHORT_UNSATURATED_UNMEASURED", result.Gate.Code);
        Assert.True(result.Gate.Metrics!["shortContourPixelRejectedCount"] > 0);
    }

    [Fact]
    public void UndersampledInnerContourDoesNotWaiveElongatedOuterShape()
    {
        var random = new Random(37);
        var frame = Create((x, y) => 40000 * Math.Exp(-.5 *
            (Math.Pow((x - 80.5) / .9, 2) + Math.Pow((y - 80.5) / 1.7, 2))) +
            Gaussian(x, y, 78, 80, 1800, 12) + random.Next(-150, 151));
        var result = G3ShortPositionMeasurementPolicy.Measure(frame, new(80, 105), 60, 5);
        Assert.Equal("G3_SHORT_UNSATURATED_UNMEASURED", result.Gate.Code);
        Assert.True(result.Gate.Metrics!["shortContourAspectRejectedCount"] > 0);
    }

    [Theory]
    [InlineData(45000)]
    [InlineData(4500)]
    public void TwoPixelSampledPsfsAreStillAmbiguous(double secondAmplitude)
    {
        var frame = Create((x, y) => Gaussian(x, y, 60.5, 80.5, 45000, 1.25) +
            Gaussian(x, y, 100.5, 80.5, secondAmplitude, 1.25));
        var result = G3ShortPositionMeasurementPolicy.Measure(frame, new(80, 105), 60, 5);
        Assert.Equal("G3_SHORT_UNSATURATED_AMBIGUOUS", result.Gate.Code);
        Assert.Equal(2, result.Gate.Metrics!["shortValidPsfCount"]);
        Assert.False(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(null, result, null, new('a', 64), 1, 60).Accepted);
    }

    [Fact]
    public void HaloLocalMaximaDoNotRequireASaturatedCoreOrBecomeSeparateStars()
    {
        var frame = HaloFrame();
        var prediction = new PixelPoint(80, 105);
        var original = SlitTargetIdentifier.Identify(frame, StarFieldDetector.Detect(frame), prediction, 60);
        Assert.Equal("TARGET_AMBIGUOUS", original.Gate.Code);
        var result = G3ShortPositionMeasurementPolicy.Measure(frame, prediction, 60);
        Assert.True(result.Gate.Code == "G3_SHORT_UNSATURATED_POSITION_MEASURED", System.Text.Json.JsonSerializer.Serialize(result.ResolvedCandidates));
        Assert.True(result.RequiresIndependentRepeat);
        Assert.InRange(result.Identification.Target!.Centroid.X, 79, 81);
        Assert.InRange(result.Identification.Target.Centroid.Y, 79, 81);
        Assert.Equal(0, result.Identification.Target.SaturatedFraction);
        Assert.Equal(1, result.Gate.Metrics!["shortValidPsfCount"]);
        Assert.False(result.Identification.HasCatalogPositionRefinement);
        var first = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(null, result, null, new('a', 64), 1, 60);
        Assert.False(first.Accepted);
        Assert.True(first.RetryAllowed);
        var fresh = G3ShortPositionMeasurementPolicy.Measure(HaloFrame(shift: 1), prediction, 60);
        Assert.Equal("G3_SHORT_UNSATURATED_POSITION_MEASURED", fresh.Gate.Code);
        var pair = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(result, fresh, new('a', 64), new('b', 64), 2, 60);
        Assert.True(pair.Accepted);
        Assert.Equal("G3_SHORT_REPEAT_CONFIRMED", pair.Gate.Code);
    }

    [Theory]
    [InlineData(45000)]
    [InlineData(4000)]
    public void TwoIndependentPsfsStayAmbiguousEvenWithDifferentBrightness(double secondAmplitude)
    {
        var frame = Create((x, y) => Gaussian(x, y, 60, 80, 45000, 1.6) + Gaussian(x, y, 100, 80, secondAmplitude, 1.6));
        var result = G3ShortPositionMeasurementPolicy.Measure(frame, new(80, 105), 60);
        Assert.Equal("G3_SHORT_UNSATURATED_AMBIGUOUS", result.Gate.Code);
        Assert.Equal(2, result.Gate.Metrics!["shortValidPsfCount"]);
        var decision = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(null, result, null, new('a', 64), 1, 60);
        Assert.False(decision.Accepted);
        Assert.False(decision.RetryAllowed);
    }

    [Fact]
    public void MissingUnsaturatedSourceUsesBoundedImageRetryNotFalseCoreAmbiguity()
    {
        var result = G3ShortPositionMeasurementPolicy.Measure(Create((_, _) => 0), new(80, 80), 60);
        Assert.Equal("G3_SHORT_UNSATURATED_UNMEASURED", result.Gate.Code);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var decision = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(null, result, null, new('a', 64), attempt, 60);
            Assert.False(decision.Accepted);
            Assert.Equal(attempt < 3, decision.RetryAllowed);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RingOrBroadBlobCannotPassTheNewFallback(bool ring)
    {
        var frame = Create((x, y) => ring
            ? 40000 * Math.Exp(-Math.Pow(Math.Sqrt(Math.Pow(x - 80, 2) + Math.Pow(y - 80, 2)) - 9, 2) / 2)
            : Gaussian(x, y, 80, 80, 35000, 15));
        var result = G3ShortPositionMeasurementPolicy.Measure(frame, new(80, 105), 60);
        Assert.NotEqual("G3_SHORT_UNSATURATED_POSITION_MEASURED", result.Gate.Code);
    }

    [Fact]
    public void CloseResolvedDoubleDoesNotDisappearWhenSeedsAreCanonicalized()
    {
        var frame = Create((x, y) => Gaussian(x, y, 77, 80, 35000, 1.3) + Gaussian(x, y, 83, 80, 35000, 1.3));
        var result = G3ShortPositionMeasurementPolicy.Measure(frame, new(80, 105), 60);
        Assert.NotEqual(GateDisposition.Passed, result.Gate.Disposition);
    }

    private static MonochromeFrame HaloFrame(int shift = 0)
    {
        var random = new Random(37);
        return Create((x, y) => Gaussian(x, y, 80 + shift, 80, 48000, 1.25) +
            Gaussian(x, y, 78 + shift, 80, 1800, 12) + random.Next(-150, 151));
    }

    private static MonochromeFrame Create(Func<int, int, double> signal)
    {
        var pixels = new ushort[160 * 160];
        for (var y = 0; y < 160; y++) for (var x = 0; x < 160; x++)
            pixels[y * 160 + x] = (ushort)Math.Clamp(1000 + signal(x, y), 0, 65520);
        return new(160, 160, pixels, 65520);
    }

    private static double Gaussian(int x, int y, double cx, double cy, double amplitude, double sigma) =>
        amplitude * Math.Exp(-.5 * (Math.Pow(x - cx, 2) + Math.Pow(y - cy, 2)) / (sigma * sigma));
}
