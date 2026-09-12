using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3ShortPositionMeasurementPolicyTests
{
    [Fact]
    public void CompactClippedCoreUsesUnsaturatedContoursAndRequiresIndependentRepeat()
    {
        var frame = StarFrame(160000, 3, 2.7);
        var result = Measure(frame);
        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.True(result.RequiresIndependentRepeat);
        Assert.InRange(result.Identification.Target!.Centroid.X, 79.5, 80.5);
        Assert.InRange(result.Identification.Target.Centroid.Y, 79.5, 80.5);
        Assert.True(result.Identification.Target.SaturatedFraction > 0);
        Assert.False(result.Identification.HasCatalogPositionRefinement);
        Assert.False(G3CatalogTargetPositionPolicy.CanUseShortMeasurement(frame, result.Identification));
        Assert.Equal(TargetIdentificationAuthority.BrightWingCentroid, result.Identification.Authority);
    }

    [Fact]
    public void OrdinaryUnsaturatedTargetKeepsSingleFrameRoute()
    {
        var result = Measure(StarFrame(30000, 2, 2));
        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.False(result.RequiresIndependentRepeat);
    }

    [Fact]
    public void ShrinkingOffsetPlateauDoesNotRedefinePsfSizeOrContourPosition()
    {
        var pixels = Enumerable.Repeat((ushort)1000, 160 * 160).ToArray();
        for (var y = 60; y <= 100; y++) for (var x = 60; x <= 100; x++)
        {
            if (Math.Pow((x - 78) / 6d, 2) + Math.Pow((y - 80) / 7d, 2) <= 1) pixels[y * 160 + x] = 25000;
            if (Math.Pow((x - 79) / 5d, 2) + Math.Pow((y - 80) / 5d, 2) <= 1) pixels[y * 160 + x] = 35000;
            if (Math.Pow((x - 80) / 4d, 2) + Math.Pow((y - 80) / 4d, 2) <= 1) pixels[y * 160 + x] = 50000;
            if (Math.Pow((x - 82) / 1.5, 2) + Math.Pow((y - 80) / 2d, 2) <= 1) pixels[y * 160 + x] = 65520;
        }
        var measured = Measure(new(160, 160, pixels, 65520));
        Assert.Equal(GateDisposition.Passed, measured.Gate.Disposition);
        var metrics = measured.Gate.Metrics!;
        // Reproduces BOTH former hard failures: outer extent / clipped plateau,
        // and clipped plateau centre / otherwise consistent contour positions.
        Assert.True(metrics["shortOuterContourExtentPixels"] > 2.5 * Math.Max(metrics["shortCoreWidthPixels"], metrics["shortCoreHeightPixels"]));
        Assert.True(metrics["shortCoreContourOffsetPixels"] > G3ShortPositionMeasurementPolicy.MaximumContourSpreadPixels);
        Assert.True(metrics["shortContourSpreadPixels"] <= G3ShortPositionMeasurementPolicy.MaximumContourSpreadPixels);
        Assert.InRange(measured.Identification.Target!.Centroid.X, 78.5, 79.5);
        Assert.True(measured.RequiresIndependentRepeat);
    }

    [Fact]
    public void GoodBadGoodConfirmsUsingOnlyTwoValidPositionsWithinThreeFrames()
    {
        var first = Measure(StarFrame(160000, 3, 3));
        var bad = first with { Gate = GateResult.Unknown("G3_SHORT_CONTOUR_BLENDED", "transient shape"), PositionSpreadPixels = 0 };
        var a = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(null, first, null, new('a', 64), 1, 60);
        Assert.False(a.Accepted);
        Assert.True(a.RetryAllowed);
        Assert.False(a.RetainPreviousMeasurement);
        var b = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(first, bad, new('a', 64), new('b', 64), 2, 60);
        Assert.False(b.Accepted);
        Assert.True(b.RetryAllowed);
        Assert.True(b.RetainPreviousMeasurement);
        Assert.Equal("G3_SHORT_CONTOUR_BLENDED", b.Gate.Code);
        var third = Measure(StarFrame(160000, 3, 3, shift: 1));
        var c = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(first, third, new('a', 64), new('c', 64), 3, 60);
        Assert.True(c.Accepted);
        Assert.False(c.RetryAllowed);
        Assert.Equal(3, c.Gate.Metrics!["shortPositionFrames"]);
        Assert.Equal("G3_SHORT_REPEAT_CONFIRMED", c.RepeatGate!.Code);
    }

    [Fact]
    public void NoValidPairCannotPassOrExtendThreeFrameCap()
    {
        var good = Measure(StarFrame(160000, 3, 3));
        var bad = good with { Gate = GateResult.Unknown("G3_SHORT_WINGS_INCOMPLETE", "missing wings") };
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var result = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(null, bad, null, new((char)('a' + attempt), 64), attempt, 60);
            Assert.False(result.Accepted);
            Assert.Equal(attempt < 3, result.RetryAllowed);
            Assert.True(result.RetainPreviousMeasurement);
            Assert.Equal(attempt, result.Gate.Metrics!["shortPositionFrames"]);
        }
        var lastOnly = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(null, good, null, new('c', 64), 3, 60);
        Assert.False(lastOnly.Accepted);
        Assert.False(lastOnly.RetryAllowed);
        var fourth = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(good, good, new('a', 64), new('d', 64), 4, 60);
        Assert.False(fourth.Accepted);
        Assert.False(fourth.RetryAllowed);
    }

    [Theory]
    [InlineData("G3_SHORT_CORE_NOT_UNIQUE")]
    [InlineData("G3_SHORT_CORE_HOLLOW")]
    [InlineData("G3_SHORT_POSITION_OUTSIDE")]
    [InlineData("G3_FIELD_MOUNT_BINDING_STALE")]
    [InlineData("PHD2_IDENTITY_MISMATCH")]
    [InlineData("UNCLASSIFIED")]
    public void OnlyExplicitImageQualityConditionsCanRequestAnotherFrame(string code)
    {
        var bad = Measure(StarFrame(160000, 3, 3)) with { Gate = GateResult.Unknown(code, "failure") };
        var result = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(null, bad, null, new('a', 64), 1, 60);
        Assert.False(result.Accepted);
        Assert.False(result.RetryAllowed);
        Assert.Equal(code, result.Gate.Code);
        Assert.NotEmpty(result.Gate.Metrics!);
    }

    [Fact]
    public void ReusedFrameOrOutOfWindowSpreadNeverEntersRetryPath()
    {
        var good = Measure(StarFrame(160000, 3, 3));
        var reused = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(good, good, new('a', 64), new('A', 64), 2, 60);
        Assert.False(reused.Accepted);
        Assert.False(reused.RetryAllowed);
        Assert.Equal("G3_SHORT_FRAME_REUSED", reused.Gate.Code);
        var outside = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(good, good, new('a', 64), new('b', 64), 2, 1);
        Assert.False(outside.Accepted);
        Assert.False(outside.RetryAllowed);
        Assert.Equal("G3_SHORT_POSITION_OUTSIDE", outside.Gate.Code);
    }

    [Fact]
    public void CompactCoreWithoutUnsaturatedSupportCannotBecomePosition()
    {
        var pixels = Enumerable.Repeat((ushort)1000, 160 * 160).ToArray();
        for (var y = 77; y <= 83; y++) for (var x = 77; x <= 83; x++) pixels[y * 160 + x] = 65520;
        var result = Measure(new(160, 160, pixels, 65520));
        Assert.NotEqual(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal("G3_SHORT_WINGS_INCOMPLETE", result.Gate.Code);
    }

    [Theory]
    [InlineData(300000, 12, 12)]
    [InlineData(160000, 10, 2)]
    public void LargeOrElongatedSaturatedSourcesAreRejected(double amplitude, double sx, double sy)
    {
        var result = Measure(StarFrame(amplitude, sx, sy));
        Assert.NotEqual(GateDisposition.Passed, result.Gate.Disposition);
    }

    [Fact]
    public void HollowRingNeverBecomesTarget()
    {
        var pixels = Enumerable.Repeat((ushort)1000, 160 * 160).ToArray();
        for (var y = 60; y <= 100; y++) for (var x = 60; x <= 100; x++)
        {
            var r = Math.Sqrt((x - 80) * (x - 80) + (y - 80) * (y - 80));
            pixels[y * 160 + x] = (ushort)Math.Min(65520, 1000 + 130000 * Math.Exp(-Math.Pow(r - 10, 2) / 4));
        }
        Assert.NotEqual(GateDisposition.Passed, Measure(new(160, 160, pixels, 65520)).Gate.Disposition);
    }

    [Fact]
    public void TwoSolidCoresInRecognitionWindowCannotBePromotedByNearestRanking()
    {
        var frame = StarFrame(160000, 3, 3, second: true);
        Assert.Equal("G3_SHORT_CORE_NOT_UNIQUE", Measure(frame).Gate.Code);
    }

    [Fact]
    public void RepeatedFrameHashCannotPassEvenWhenCoordinatesAgree()
    {
        var a = Measure(StarFrame(160000, 3, 3));
        Assert.Equal("G3_SHORT_FRAME_REUSED", G3ShortPositionMeasurementPolicy.ConfirmRepeat(a, a, new('a', 64), new('A', 64)).Code);
        Assert.Equal("G3_SHORT_FRAME_REUSED", G3ShortPositionMeasurementPolicy.ConfirmRepeat(a, a, "", "").Code);
        Assert.Equal(GateDisposition.Passed, G3ShortPositionMeasurementPolicy.ConfirmRepeat(a, a, new('a', 64), new('b', 64)).Disposition);
    }

    [Fact]
    public void NewFramesMustAgreeAndBothBeMeasured()
    {
        var a = Measure(StarFrame(160000, 3, 3));
        var b = Measure(StarFrame(160000, 3, 3, shift: 6));
        Assert.Equal("G3_SHORT_REPEAT_DISAGREES", G3ShortPositionMeasurementPolicy.ConfirmRepeat(a, b, new('a', 64), new('b', 64)).Code);
        Assert.Equal("G3_SHORT_REPEAT_UNMEASURED", G3ShortPositionMeasurementPolicy.ConfirmRepeat(a,
            b with { Gate = GateResult.Unknown("UNKNOWN", "missing") }, new('a', 64), new('b', 64)).Code);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1)]
    [InlineData(70)]
    public void CoarsePositionSpreadCannotEscapeRecognitionEnvelope(double spread)
    {
        var measured = Measure(StarFrame(160000, 3, 3));
        var target = measured.Identification with { Authority = TargetIdentificationAuthority.CatalogWcsProjection,
            BoundShortPositionEvidencePath = "receipt.json", CatalogPositionSpreadPixels = spread };
        Assert.Throws<InvalidOperationException>(() => G3CatalogTargetPositionPolicy.ProjectionDestination(target, new(85, 85), 60));
    }

    private static G3ShortPositionMeasurement Measure(MonochromeFrame frame) =>
        G3ShortPositionMeasurementPolicy.Measure(frame, new(85, 85), 60);

    private static MonochromeFrame StarFrame(double amplitude, double sx, double sy, bool second = false, double shift = 0)
    {
        var pixels = new ushort[160 * 160];
        for (var y = 0; y < 160; y++) for (var x = 0; x < 160; x++)
        {
            var signal = amplitude * Math.Exp(-.5 * (Math.Pow((x - 80 - shift) / sx, 2) + Math.Pow((y - 80) / sy, 2)));
            if (second) signal += amplitude * Math.Exp(-.5 * (Math.Pow((x - 110) / 3d, 2) + Math.Pow((y - 80) / 3d, 2)));
            pixels[y * 160 + x] = (ushort)Math.Clamp(1000 + signal, 0, 65520);
        }
        return new(160, 160, pixels, 65520);
    }
}
