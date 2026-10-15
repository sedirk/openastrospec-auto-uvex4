using UvexAdv.Phd2;
using System.Text.Json;

namespace UvexAdv.Phd2.Tests;

public sealed class Phd2NativeGuidingPositionTests
{
    [Theory]
    [InlineData("null")] [InlineData("[]")] [InlineData("{\"frame\":\"1\"}")]
    [InlineData("{\"frame\":1,\"width\":2,\"height\":2,\"star_pos\":[null,1],\"pixels\":\"AQACAAMABAA=\"}")]
    [InlineData("{\"frame\":1,\"width\":2,\"height\":2,\"star_pos\":[1,1],\"pixels\":\"invalid\"}")]
    public void MalformedCropIsAnOpticalRejectionNotAParserCrash(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Null(Phd2PrimaryStarImage.Parse(document.RootElement));
    }

    [Fact]
    public void AveragedCameraOffsetIsDiagnosticNotPrimaryStarPosition()
    {
        var result = Frame();
        Assert.Equal(new Phd2Point(282.55, 857.16), result.NativeEffectiveGuidePosition);
        Assert.Null(result.ResolvePrimaryStarPosition(3, 3, new ushort[9]));
    }

    [Fact]
    public void MissingMismatchedOrErroredGuideEvidenceCannotFabricateZeroResidual()
    {
        var frame = Frame();
        Assert.Null((frame with { NativeGuideStep = null }).NativeEffectiveGuidePosition);
        Assert.Null((frame with { NativeLockPosition = null }).NativeEffectiveGuidePosition);
        Assert.Null((frame with { TriggerGuideFrame = 11 }).NativeEffectiveGuidePosition);
        Assert.Equal(frame.NativeEffectiveGuidePosition,
            (frame with { NativeGuideStep = frame.NativeGuideStep! with { ErrorCode = 1 } }).NativeEffectiveGuidePosition);
        Assert.Null((frame with { NativeGuideStep = frame.NativeGuideStep! with { ErrorCode = 2 } }).NativeEffectiveGuidePosition);
        Assert.Null((frame with { NativeGuideStep = frame.NativeGuideStep! with { DxPixels = double.NaN } }).NativeEffectiveGuidePosition);
    }

    [Fact]
    public void NativePrimaryCropRecoversPhysicalPositionDespiteDifferentMultiStarOffset()
    {
        ushort[] pixels = [0, 0, 0, 0, 0, 1, 2, 0, 0, 3, 4, 0, 0, 0, 0, 0];
        var frame = Frame() with { PrimaryStarImage = new(10, 2, 2, new(.75, .66), [1, 2, 3, 4]) };
        var measured = Assert.IsType<Phd2Point>(frame.ResolvePrimaryStarPosition(4, 4, pixels));
        Assert.Equal(1.75, measured.X, 10);
        Assert.Equal(1.66, measured.Y, 10);
        Assert.NotEqual(frame.NativeEffectiveGuidePosition, frame.ResolvePrimaryStarPosition(4, 4, pixels));
        Assert.Null((frame with { PrimaryStarImage = frame.PrimaryStarImage with { Frame = 11 } }).ResolvePrimaryStarPosition(4, 4, pixels));
        Assert.Null((frame with { NativeGuideStep = frame.NativeGuideStep! with { ErrorCode = 2 } }).ResolvePrimaryStarPosition(4, 4, pixels));
    }

    [Theory]
    [InlineData("mismatch")] [InlineData("ambiguous")] [InlineData("flat")]
    [InlineData("outside")] [InlineData("nan")] [InlineData("size")]
    public void UnprovedPrimaryCropCannotBecomeGeometry(string fault)
    {
        ushort[] pixels = [1, 2, 0, 0, 3, 4, 0, 0, 0, 0, 0, 0];
        var stamp = new Phd2PrimaryStarImage(10, 2, 2, new(.5, .5), [1, 2, 3, 4]);
        if (fault == "mismatch") pixels[0] = 9;
        if (fault == "ambiguous") pixels = [1, 2, 1, 2, 3, 4, 3, 4, 0, 0, 0, 0];
        if (fault == "flat") stamp = stamp with { Pixels = [0, 0, 0, 0] };
        if (fault == "outside") stamp = stamp with { PositionInCrop = new(2, 1) };
        if (fault == "nan") stamp = stamp with { PositionInCrop = new(double.NaN, 1) };
        if (fault == "size") stamp = stamp with { Width = 64 };
        Assert.Null(stamp.LocateIn(4, 3, pixels));
    }

    private static Phd2GuidingFrameResult Frame() => new(
        "frame.fit", new string('A', 64), 10, 193, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        false, false, false, false,
        new Phd2GuideStep(10, -3, 0.55, 140.7, 6.4, 3.05, null),
        new Phd2Point(285.55, 856.61));
}
