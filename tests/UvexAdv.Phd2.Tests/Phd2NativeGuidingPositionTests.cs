using UvexAdv.Phd2;

namespace UvexAdv.Phd2.Tests;

public sealed class Phd2NativeGuidingPositionTests
{
    [Fact]
    public void UsesMeasuredCameraOffsetNotRequestedLockAsStarPosition()
    {
        var result = Frame();
        Assert.Equal(new Phd2Point(282.55, 857.16), result.NativeMeasuredGuidePosition);
        Assert.NotEqual(result.NativeLockPosition, result.NativeMeasuredGuidePosition);
    }

    [Fact]
    public void MissingMismatchedOrErroredGuideEvidenceCannotFabricateZeroResidual()
    {
        var frame = Frame();
        Assert.Null((frame with { NativeGuideStep = null }).NativeMeasuredGuidePosition);
        Assert.Null((frame with { NativeLockPosition = null }).NativeMeasuredGuidePosition);
        Assert.Null((frame with { TriggerGuideFrame = 11 }).NativeMeasuredGuidePosition);
        Assert.Equal(frame.NativeMeasuredGuidePosition,
            (frame with { NativeGuideStep = frame.NativeGuideStep! with { ErrorCode = 1 } }).NativeMeasuredGuidePosition);
        Assert.Null((frame with { NativeGuideStep = frame.NativeGuideStep! with { ErrorCode = 2 } }).NativeMeasuredGuidePosition);
        Assert.Null((frame with { NativeGuideStep = frame.NativeGuideStep! with { DxPixels = double.NaN } }).NativeMeasuredGuidePosition);
    }

    private static Phd2GuidingFrameResult Frame() => new(
        "frame.fit", new string('A', 64), 10, 193, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        false, false, false, false,
        new Phd2GuideStep(10, -3, 0.55, 140.7, 6.4, 3.05, null),
        new Phd2Point(285.55, 856.61));
}
